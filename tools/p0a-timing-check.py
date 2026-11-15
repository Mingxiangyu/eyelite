#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-A / P0-D 真机实测：验证时钟源改造后的计时精度。

测三件事：
  T1 工作时钟精度 —— 「进程启动 → 遮罩出现」的间隔应 ≈ WorkMinutes × 60 秒
                     （这直接验证 workAccumMs 的累加速率；速率错则间隔错）
  T2 遮罩按时弹出 —— 同上，但看它落在容差窗口内
  T3 休息倒计时准确 —— RestSeconds=8 时遮罩应约 8 秒后自动消失

全部为**只读观测**：只枚举窗口、读时间，不注入任何按键、不抢焦点。
这保证不会影响用户的键盘输入。

用法：python tools/p0a-timing-check.py [轮数]
前置：config.ini 里 WorkMinutes=1 / RestSeconds=8 / PomodoroEnabled=0 /
      IdleMinutes=0 / SedentaryEnabled=0，且 EyeLite.exe 已启动。

**踩坑记录（2026-11-14，新 build）**：A2 的 DimForm（休息前 60 秒渐进压暗）铺满整块
屏幕，尺寸同样 w>800 h>500，WorkMinutes=1 时启动后 ~1 秒就出现。只按尺寸判遮罩会让
T1 的"遮罩出现时刻"直接变成 1 秒（计时精度结论全废）。区分依据
（源码 2146-2196 vs 1264-1320）：
  · DimForm：带 WS_EX_TRANSPARENT(0x20，鼠标穿透) 且**没有子窗口**
  · RestForm：有 Label / RingMeter / 按钮，**子窗口 >= 1**，且无 TRANSPARENT
压暗层单独记为信息项（dim），不参与 PASS/FAIL。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.WinDLL('user32', use_last_error=True)

EYELITE_PID = [0]

# ---- 遮罩判据的两个辅助（见顶部踩坑记录）----
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

DIM_SEEN = [False]       # 压暗层是否出现过（信息项，不参与判定）


def ex_style(hwnd):
    return user32.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    """子窗口数：遮罩里有 Label / 环形 / 按钮，压暗层一个都没有。"""
    n = [0]
    Enum = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, Enum(cb), 0)
    return n[0]


def find_eyelite_pid():
    """tasklist 找 EyeLite.exe 的 pid（输出是 GBK）。"""
    r = subprocess.run(['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
                       capture_output=True)
    text = r.stdout.decode('gbk', 'ignore')
    for line in text.splitlines():
        if 'EyeLite.exe' in line:
            parts = [p.strip('" ') for p in line.split('","')]
            return int(parts[1])
    return None


def enum_eyelite_windows():
    """列出 EyeLite 进程的所有顶层窗口。"""
    out = []
    EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(hwnd, lparam):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == EYELITE_PID[0]:
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, cls, 256)
            r = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            out.append({
                'hwnd': hwnd,
                'cls': cls.value,
                'w': r.right - r.left,
                'h': r.bottom - r.top,
                'visible': bool(user32.IsWindowVisible(hwnd)),
            })
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def overlay_windows():
    """EyeLite 进程里可见的、大尺寸的窗口 = 休息遮罩。

    必须排除压暗层（DimForm）：它铺满屏幕、尺寸一样，但带 WS_EX_TRANSPARENT
    且没有子窗口（见顶部踩坑记录）。
    """
    out = []
    for w in enum_eyelite_windows():
        if not (w['visible'] and w['w'] > 800 and w['h'] > 500):
            continue
        if ex_style(w['hwnd']) & WS_EX_TRANSPARENT:
            DIM_SEEN[0] = True          # 压暗层：信息项，不算遮罩
            continue
        if child_count(w['hwnd']) < 1:
            continue
        out.append(w)
    return out


def process_start_time():
    """取 EyeLite 进程的启动时刻（perf_counter 无关的系统时间，转成 epoch 秒）。"""
    r = subprocess.run(
        ['powershell', '-NoProfile', '-Command',
         "(Get-Process EyeLite).StartTime.ToUniversalTime().ToOADate()"],
        capture_output=True)
    try:
        oadate = float(r.stdout.decode('utf-8', 'ignore').strip())
        # OLE Automation Date 基准：1899-12-30
        import datetime
        epoch = datetime.datetime(1899, 12, 30, tzinfo=datetime.timezone.utc)
        return (epoch + datetime.timedelta(days=oadate)).timestamp()
    except Exception:
        return None


def wait_for_overlay(timeout_s, poll=0.15):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        ov = overlay_windows()
        if ov:
            return ov, time.time() - t0
        time.sleep(poll)
    return [], timeout_s


def wait_for_no_overlay(timeout_s, poll=0.1):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if not overlay_windows():
            return True, time.time() - t0
        time.sleep(poll)
    return False, timeout_s


def wait_process_exit(timeout_s=15):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if find_eyelite_pid() is None:
            return True
        time.sleep(0.3)
    return False


def start_eyelite(exe):
    subprocess.Popen([exe], cwd=os.path.dirname(exe))
    time.sleep(1.0)
    pid = find_eyelite_pid()
    if not pid:
        return None
    EYELITE_PID[0] = pid
    return pid


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'],
                   capture_output=True)
    wait_process_exit()


# ---------------------------------------------------------------- 配置
#
# 前置配置由脚本**自己写**、跑完按**原始字节**还原。
# 原来只在 docstring 里写"前置"，直接跑会用用户的真实配置（WorkMinutes=20），
# 120s 内不可能出遮罩 → 三轮回全都 FAIL，而且看起来像产品缺陷（2026-11-14 实测踩过）。

CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')

CFG_ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']


def read_cfg_raw():
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
                if not line or line.startswith('#') or line.startswith('['):
                    continue
                if '=' in line:
                    k, v = line.split('=', 1)
                    d[k.strip()] = v.strip()
    except Exception as e:
        print('  read_cfg failed:', e)
    return d


def write_cfg(d):
    # 关掉 P1 的两个实验性守卫。
    #
    # 为什么：C4（打字段）的判据是 idle<300ms → 到点延后 5s、累计 60s；
    # C3（前台白名单）命中即延后 60s。两者都会让"遮罩出现时刻"偏离 60s，
    # 而本脚本量的是**计时精度**，不是守卫行为。守卫由 tools/p1-batch1-check.py 单独验证。
    # 踩过一次：p0d 的对照组用 EYE_LITE_FAKE_IDLE_MS=0 伪造"人就在"，
    # 结果 idle 恒为 0 → C4 一直判"正在打字" → 遮罩被推到 60s 之后，对照组直接失效。
    d = dict(d)
    d['ForegroundGuardEnabled'] = '0'
    d['TypingGuardEnabled'] = '0'
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in CFG_ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:                      # 未知键也写回，不能弄丢用户配置
            if k not in CFG_ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def restore_cfg_raw(raw):
    if not raw:
        return
    with open(CFG, 'wb') as f:
        f.write(raw)
    print('[env] config restored: %d bytes (backup %d bytes, %s)'
          % (os.path.getsize(CFG), len(raw),
             'OK' if os.path.getsize(CFG) == len(raw) else 'SIZE MISMATCH!'))


def main():
    rounds = 3
    if len(sys.argv) > 1:
        try:
            rounds = int(sys.argv[1])
        except ValueError:
            pass

    exe = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       'EyeLite.exe')
    if not os.path.exists(exe):
        print('[FATAL] 找不到 EyeLite.exe：' + exe)
        return 2

    print('[env] exe = %s' % exe)
    print('[env] rounds = %d' % rounds)

    raw = read_cfg_raw()
    orig = read_cfg()
    cfg = dict(orig)
    cfg.update({'WorkMinutes': '1', 'RestSeconds': '8', 'PreviewSeconds': '0',
                'PomodoroEnabled': '0', 'IdleMinutes': '0',
                'SedentaryEnabled': '0', 'SoundEnabled': '0'})
    write_cfg(cfg)
    print('[env] config: WorkMinutes=1 RestSeconds=8 PreviewSeconds=0 '
          'IdleMinutes=0（脚本自备前置，结束按原始字节还原）')

    results = []
    try:
        for rnd in range(1, rounds + 1):
            print('\n=========== 第 %d 轮 ===========' % rnd)
            stop_eyelite()

            t_launch = time.time()
            pid = start_eyelite(exe)
            if not pid:
                print('  [FATAL] 启动失败')
                results.append(('第%d轮 启动' % rnd, False, '启动失败'))
                continue

            # 用进程启动时刻做基准（比脚本自己的计时更准，不含启动开销）
            st = process_start_time()
            base = st if st else t_launch
            print('  pid=%d  进程启动时刻基准已取' % pid)

            # --- T1/T2：遮罩应在 WorkMinutes(1 分钟) 后弹出 ---
            ov, waited = wait_for_overlay(120)
            appear = time.time()
            if not ov:
                print('  遮罩 120s 内未出现')
                results.append(('第%d轮 工作计时' % rnd, False, '遮罩未出现'))
                stop_eyelite()
                continue

            elapsed = appear - base
            print('  T1/T2 遮罩出现：进程启动后 %.2fs（期望 60s ± 3s）' % elapsed)
            print('       遮罩窗口 %d 个：%s' %
                  (len(ov), [(w['w'], w['h']) for w in ov]))
            ok = abs(elapsed - 60.0) <= 3.0
            results.append(('第%d轮 工作计时 %.2fs' % (rnd, elapsed), ok,
                            '期望 60±3s'))

            # --- T3：休息倒计时 ---
            gone, dur = wait_for_no_overlay(25)
            print('  T3 遮罩持续 %.2fs 后消失（期望 8s ± 2s）' % dur)
            ok3 = gone and abs(dur - 8.0) <= 2.0
            results.append(('第%d轮 休息时长 %.2fs' % (rnd, dur), ok3, '期望 8±2s'))
    finally:
        stop_eyelite()
        restore_cfg_raw(raw)

    stop_eyelite()

    print('\n================ 汇总 ================')
    allok = True
    for name, ok, detail in results:
        print('  %-28s %s   %s' % (name, 'PASS' if ok else 'FAIL', detail))
        if not ok:
            allok = False
    # 压暗层信息项：让后来的人看到 dim=yes 时不会把它当成遮罩
    print('  %-28s %s   %s' % ('压暗层 DimForm（信息项）',
                               'yes' if DIM_SEEN[0] else 'no',
                               '出现过，但它不是遮罩（TRANSPARENT 且无子窗口）'))
    print('  OVERALL: %s' % ('PASS' if allok else 'FAIL'))
    return 0 if allok else 1


if __name__ == '__main__':
    sys.exit(main())
