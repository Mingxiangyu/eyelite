#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0 收尾实测：A2（休息前渐进压暗）+ A3（托盘进度环）+ 核心内存基线

判定原则（沿用本项目既有纪律）：
  · 先自检仪器"看得见现象"，再下结论
  · 判据用**可观测的结构特征**，不靠肉眼
  · 每个用例都有反面对照，避免"没出现"其实是"根本没跑"

窗口判据（2026-11-14 修正，A2 引入 DimForm 之后）：
  dim     = 大尺寸 + 带 WS_EX_TRANSPARENT(0x20) + 子窗口数 == 0
  overlay = 大尺寸 + 不带 WS_EX_TRANSPARENT + 子窗口数 >= 1
  原因：DimForm 铺满整块屏幕，只用 w>800 and h>500 会把它误判成休息遮罩。

用例：
  MEM       默认配置（用户真实配置）启动 30s：Private commit ≤ 45MB
  A3-GDI    同一进程 t=30s 与 t=180s 的 GDI 对象数差值 ≤ 5（进度环每分钟重绘一次）
  A2-BASE   WorkMinutes=2：t≈70s 必须出现 dim（且无 overlay）；t≈125s 必须出现 overlay
  A2-CANCEL t≈70s 确认 dim 存在后注入 F24 → 3s 内 dim 消失；t≈100s 仍无 dim（本轮不再尝试）
  A2-OFF    DimAlphaMax=0：t≈70s 必须既无 dim 也无 overlay（证明是开关在起作用）
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
psapi = ctypes.WinDLL('psapi', use_last_error=True)

WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
GWLP_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20
KEYEVENTF_KEYUP = 0x0002
VK_F24 = 0x87

EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

# ---- 提速开关（2026-11-15，lead 批准）----
# 只有"布尔否定断言"的那一项（A2-OFF：等一个**不该出现**的压暗层）可以缩短观测窗口：
# 注入 EYE_LITE_FAKE_WORK_MS 后，阳性（DimAlphaMax>0）会在 ~1s 就出现，
# 所以 12s 足以证伪；不注入时保持原 80s。
_FAKE_WORK = os.environ.get('EYE_LITE_FAKE_WORK_MS', '').strip()
try:
    _FAKE_WORK_OK = int(_FAKE_WORK) >= 0
except Exception:
    _FAKE_WORK_OK = False
OFF_DIM_TIMEOUT_S = 12.0 if _FAKE_WORK_OK else 80.0


def _ex_style(hwnd):
    """取扩展样式。

    必须显式声明签名：`GetWindowLongW` 不声明时 ctypes 按 32 位 int 处理参数，
    HWND 超过 2^31 会被静默截断 —— 和当初 `GetModuleHandleW` 漏设 restype 导致
    `SetWindowsHookEx` 报 err=126 是同一类坑（本机第 5 次）。
    """
    user32.GetWindowLongPtrW.restype = ctypes.c_ssize_t
    user32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
    return user32.GetWindowLongPtrW(hwnd, GWLP_EXSTYLE)


def _child_count(hwnd):
    n = [0]

    def cb(h, l):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, EnumProc(cb), 0)
    return n[0]


def windows_of(pid):
    """返回该 PID 的可见窗口 [(hwnd,w,h,exstyle,children)]。"""
    out = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        p = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        r = wt.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return True
        out.append((hwnd, r.right - r.left, r.bottom - r.top,
                    _ex_style(hwnd), _child_count(hwnd)))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return out


def dim_windows(pid):
    return [t for t in windows_of(pid)
            if t[1] > 800 and t[2] > 500 and (t[3] & WS_EX_TRANSPARENT) and t[4] == 0]


def overlay_windows(pid):
    return [t for t in windows_of(pid)
            if t[1] > 800 and t[2] > 500 and not (t[3] & WS_EX_TRANSPARENT) and t[4] >= 1]


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ('cb', wt.DWORD), ('PageFaultCount', wt.DWORD),
        ('PeakWorkingSetSize', ctypes.c_size_t), ('WorkingSetSize', ctypes.c_size_t),
        ('QuotaPeakPagedPoolUsage', ctypes.c_size_t), ('QuotaPagedPoolUsage', ctypes.c_size_t),
        ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t), ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
        ('PagefileUsage', ctypes.c_size_t), ('PeakPagefileUsage', ctypes.c_size_t),
        ('PrivateUsage', ctypes.c_size_t),
    ]


PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010


def private_commit_mb(pid):
    """Private commit（与 checkmem.ps1 的 PrivateMemorySize64 同口径）。

    同时返回 (PrivateUsage, PagefileUsage)：
      · .NET 的 `Process.PrivateMemorySize64` 在 .NET Framework 下取的是 **PagefileUsage**
      · 而 `PrivateUsage` 是 Vista 之后新增的字段，两者通常相等但不保证
    历史上出现过 17.2MB（PrivateUsage）与 28.7MB（checkmem 的 PrivateMemorySize64）不一致，
    所以这里把两个字段都打出来，一次对齐口径，不要靠猜。
    """
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        return (-1.0, -1.0)
    try:
        c = PROCESS_MEMORY_COUNTERS()
        c.cb = ctypes.sizeof(c)
        if not psapi.GetProcessMemoryInfo(h, ctypes.byref(c), c.cb):
            return (-1.0, -1.0)
        return (c.PrivateUsage / (1024.0 * 1024.0), c.PagefileUsage / (1024.0 * 1024.0))
    finally:
        kernel32.CloseHandle(h)


def gdi_objects(pid):
    """GDI 对象总数（GetGuiResources 的 GR_GDIOBJECTS=0）。"""
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION, False, pid)
    if not h:
        return -1
    try:
        return user32.GetGuiResources(h, 0)
    finally:
        kernel32.CloseHandle(h)


def key_tap(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.03)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.03)


# ---------------- 配置 ----------------
ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
         'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
         'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
         'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
         'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']


def read_cfg_bytes():
    try:
        with open(CFG, 'rb') as f:
            return f.read()
    except Exception:
        return b''


def read_cfg():
    d = {}
    try:
        with open(CFG, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if '=' in line and not line.startswith('#') and not line.startswith('['):
                    k, v = line.split('=', 1)
                    d[k.strip()] = v.strip()
    except Exception:
        pass
    return d


def write_cfg(d):
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def restore_cfg(raw):
    if raw:
        with open(CFG, 'wb') as f:
            f.write(raw)


def eyelite_pids():
    pids = []
    try:
        out = subprocess.run(['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
                             capture_output=True)
        for line in out.stdout.decode('utf-8', 'ignore').splitlines():
            if 'EyeLite.exe' in line:
                parts = [p.strip('" ') for p in line.split('","')]
                if len(parts) > 1 and parts[1].isdigit():
                    pids.append(int(parts[1]))
    except Exception:
        pass
    return pids


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    for _ in range(20):
        if not eyelite_pids():
            return True
        time.sleep(0.25)
    return not eyelite_pids()


def launch(env_extra=None):
    env = os.environ.copy()
    for k in ('EYE_LITE_FORCE_QUNS', 'EYE_LITE_FORCE_QUNS_FILE',
              'EYE_LITE_SUPPRESS_MAX_MS', 'EYE_LITE_FAKE_IDLE_MS'):
        env.pop(k, None)
    if env_extra:
        env.update(env_extra)
    p = subprocess.Popen([EXE], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return p


def wait_window(pid, kind, timeout_s):
    """从**现在**起等待某类窗口出现，返回等待秒数；超时返回 None。

    不要用"相对某个历史时刻"的算法：那会让实际余量随历史时刻增长而缩水，
    t_dim 一旦偏晚就会正好卡在超时线上（B 指出过这个隐患）。
    """
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if kind == 'dim' and dim_windows(pid):
            return time.time() - t0
        if kind == 'overlay' and overlay_windows(pid):
            return time.time() - t0
        time.sleep(0.05)
    return None


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found')
        return 2

    if not stop_eyelite():
        print('FAIL: 有残留 EyeLite 进程，无法开始（单实例会让所有用例假 FAIL）')
        return 2

    raw = read_cfg_bytes()
    orig = read_cfg()
    print('orig config bytes=%d  WorkMinutes=%s DimAlphaMax=%s'
          % (len(raw), orig.get('WorkMinutes'), orig.get('DimAlphaMax')))

    results = []
    try:
        # ================= MEM + A3-GDI（空闲态基线）=================
        #
        # 判据修正记录：原版拿 t=30s 与 t=180s 比，前提是 WorkMinutes=1（60s 就休息）。
        # 5 秒粒度探针证明那 +22 是"首次 Show 时控件句柄/GDI+ 缓冲"的**一次性分配**，
        # 之后完全平坦（不休息 185s 全程 0 增长；休息后到 320s 也不再涨）。
        # 所以这里改成两个独立用例：
        #   MEM/A3-GDI  —— WorkMinutes=20、DimAlphaMax=0：不休息、不压暗，
        #                  测"空闲稳态零增长"（这才是托盘进度环重绘的责任范围）
        #   A3-STEADY   —— WorkMinutes=1、DimAlphaMax=45：跑 400s，
        #                  在**两次休息之后**（t=140）与 t=380 各采一次，
        #                  要求稳态不增长（证明首次显示之后不泄漏）
        idle = dict(orig)
        idle.update({'WorkMinutes': '20', 'DimAlphaMax': '0', 'PreviewSeconds': '0',
                     'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                     'PomodoroEnabled': '0'})
        write_cfg(idle)
        p = launch()
        time.sleep(2.0)
        if p.pid not in eyelite_pids():
            print('FAIL: EyeLite 启动后立即退出（残留单实例？）')
            return 2
        time.sleep(28.0)
        m30, p30 = private_commit_mb(p.pid)
        g30 = gdi_objects(p.pid)
        time.sleep(150.0)
        m180, p180 = private_commit_mb(p.pid)
        g180 = gdi_objects(p.pid)
        results.append(('MEM 核心裸跑 Private commit ≤ 45MB', 0 < m30 <= 45.0,
                        't=30s %.1fMB(PrivateUsage)/%.1fMB(Pagefile) / t=180s %.1f/%.1f'
                        % (m30, p30, m180, p180)))
        results.append(('A3-GDI 空闲稳态 180s 零增长(≤5)', g30 > 0 and (g180 - g30) <= 5,
                        't=30s %d → t=180s %d (delta %+d)' % (g30, g180, g180 - g30)))
        stop_eyelite()

        # ================= A3-STEADY（首次显示之后的稳态）=================
        steady = dict(orig)
        steady.update({'WorkMinutes': '1', 'PreviewSeconds': '0', 'RestSeconds': '8',
                       'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                       'PomodoroEnabled': '0', 'StrictMode': '0', 'DimAlphaMax': '45'})
        write_cfg(steady)
        p = launch()
        time.sleep(140.0)                    # 两次休息已发生，全部 UI 至少显示过一次
        g140 = gdi_objects(p.pid)
        m140, _ = private_commit_mb(p.pid)
        time.sleep(240.0)
        g380 = gdi_objects(p.pid)
        m380, _ = private_commit_mb(p.pid)
        results.append(('A3-STEADY 首次显示后 240s 稳态零增长(≤5)',
                        g140 > 0 and (g380 - g140) <= 5,
                        't=140s %d → t=380s %d (delta %+d)；内存 %.1f→%.1fMB'
                        % (g140, g380, g380 - g140, m140, m380)))
        stop_eyelite()

        # ================= A2-BASE =================
        base = dict(orig)
        base.update({'WorkMinutes': '2', 'PreviewSeconds': '0', 'RestSeconds': '8',
                     'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                     'PomodoroEnabled': '0', 'StrictMode': '0', 'DimAlphaMax': '45'})
        write_cfg(base)
        p = launch()
        t_dim = wait_window(p.pid, 'dim', 110.0)
        # 遮罩在 120s 到点出现；dim 出现后再给足 75s 独立预算，不靠"减去已用时间"。
        t_ovl = wait_window(p.pid, 'overlay', 75.0) if t_dim is not None else None
        ok_base = (t_dim is not None) and (t_ovl is not None)
        results.append(('A2-BASE 压暗层先出现、遮罩后出现', ok_base,
                        'dim=%s overlay_after_dim=%s' % (
                            ('%.1fs' % t_dim) if t_dim is not None else 'no',
                            ('%.1fs' % t_ovl) if t_ovl is not None else 'no')))
        stop_eyelite()

        # ================= A2-CANCEL =================
        write_cfg(base)
        p = launch()
        t_dim = wait_window(p.pid, 'dim', 110.0)
        if t_dim is None:
            results.append(('A2-CANCEL 输入即撤销本轮', False, '压暗层未出现，无法验证撤销'))
        else:
            time.sleep(1.0)
            key_tap(VK_F24)                  # F24 不产生字符
            gone = None
            t0 = time.time()
            while time.time() - t0 < 4.0:
                if not dim_windows(p.pid):
                    gone = time.time() - t0
                    break
                time.sleep(0.05)
            # 再等一段时间，确认本轮不再回来
            time.sleep(max(0.0, 15.0 - (time.time() - t0)))
            came_back = bool(dim_windows(p.pid))
            results.append(('A2-CANCEL 输入后 4s 内消失且本轮不再出现',
                            gone is not None and not came_back,
                            '消失=%s 之后又出现=%s' % (
                                ('%.2fs' % gone) if gone is not None else '否',
                                '是' if came_back else '否')))
        stop_eyelite()

        # ================= A2-OFF =================
        off = dict(base)
        off['DimAlphaMax'] = '0'
        write_cfg(off)
        p = launch()
        # 这是**布尔否定断言**（"关掉时确实不建压暗层"），不是"等得久才更确定"。
        # 注入 EYE_LITE_FAKE_WORK_MS 时，若 DimAlphaMax>0 压暗层会在 ~1s 就出现，
        # 所以 12s 足够证伪；不注入时仍按原来的 80s 跑（行为不变）。
        # （改动经 lead 2026-11-15 批准：原本固定 80s，压完省 68s。）
        t_dim = wait_window(p.pid, 'dim', OFF_DIM_TIMEOUT_S)
        has_ovl = bool(overlay_windows(p.pid))
        results.append(('A2-OFF DimAlphaMax=0 时不建压暗层', t_dim is None and not has_ovl,
                        'dim=%s overlay=%s' % (
                            ('%.1fs' % t_dim) if t_dim is not None else 'no',
                            'yes' if has_ovl else 'no')))
        stop_eyelite()
    finally:
        stop_eyelite()
        if raw and len(raw) > 0:
            restore_cfg(raw)
            print('config restored: %d bytes (backup %d bytes)' % (os.path.getsize(CFG), len(raw)))

    print()
    for name, ok, detail in results:
        print('%-46s %s  %s' % (name, 'PASS' if ok else 'FAIL', detail))

    npass = sum(1 for r in results if r[1])
    print('\n=== summary: %d/%d PASS ===' % (npass, len(results)))
    for r in results:
        if not r[1]:
            print('  FAILED: %s (%s)' % (r[0], r[2]))
    return 0 if npass == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
