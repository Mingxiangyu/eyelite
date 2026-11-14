#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-D 真机实测：空闲（idle）与休眠对计时的影响
    P0-D-1  空闲超过 IdleMinutes          → 计时暂停（不该弹）
    P0-D-2  空闲超过整个休息时长           → 周期直接重置（回来后**不立刻**弹）
    P0-D-3  休眠后回来不计睡眠时段          → 真机未实测，只做源码审查

## 判定原理
两个测试钩子（都是"设成非负整数毫秒即生效"）：
  · `EYE_LITE_FAKE_IDLE_MS`   进程启动时读一次（Program.cs:2786），只能全程冻结一个值
  · `EYE_LITE_FAKE_IDLE_FILE` 指向文本文件，**每次取空闲都重读**（Program.cs:3735），
    优先于环境变量，可以在运行中改 —— D-2 的"离开 → 回来"必须靠它
两条路径的分歧（Program.cs:3023 附近）：
    IsIdleTooLong() = IdleSeconds() >= IdleMinutes*60      → 只是**暂停**累加
    idle && IdleSeconds() >= RestTotalSeconds              → 直接 **ResetWork()**
两者都"不弹遮罩"，所以只观测"弹不弹"**无法区分**暂停与重置。

## D-2 的归因测法（本脚本的核心）
先让活跃累计吃掉大半个周期，再伪造"离开超过整个休息时长"，然后模拟"人回来了"：
    写 0 → 等 52s（剩余 ≈8s）→ 写 130000 保持 2s（命中重置）→ 写回 0 → 计时到遮罩
判据：
  · 写回 0 后 **≤10s**  就弹 → 只是暂停，剩余 8s 还在 → **FAIL**
  · 写回 0 后 **40~90s** 才弹 → 周期真的被重置（要重新走完 60s）→ **PASS**
  · 90s 内一直不弹 → 卡死 → **FAIL**（打印时间线）
这一步只靠"延迟"就能把两条路径分开，不需要读内部状态。

## 为什么还保留 FAKE_IDLE_MS=999999 那条
它是"长空闲不打扰"的独立证据（对应 P0-D-1 的暂停语义），与 D-2 的归因互不替代。

## D-1 为什么用 RestSeconds=120
D2 的重置阈值是 RestTotalSeconds。若 RestSeconds=8，FAKE=65000（65s）会**同时**
命中暂停（65>=60）与重置（65>=8），结论无法归因。取 120 后：65>=60 命中暂停、
65<120 不命中重置，D-1 只测得到"暂停"。

## 仪器纪律
· 遮罩判据必须**排除压暗层**：DimForm 铺满屏幕、尺寸同样 w>800 h>500，
  靠 `WS_EX_TRANSPARENT(0x20)` + 子窗口数区分（遮罩有 Label/环形/按钮）。
  本脚本另外把 DimAlphaMax 设成 0，从配置上关掉渐暗，双重隔离。
· 遮罩必须按 PID 过滤（Windows 桌面窗口是 3840×2160 的可见顶层窗口）。
· PID 用 `Popen.pid` 缓存，轮询里不调 tasklist。
· 只观测窗口、不动键鼠、不抢前台（伪造空闲已足够，无需注入按键）。
· 临时文件只放 `tools/_probe/`，结束删除。

用法：python tools/p0d-idle-check.py [all|d1|d2]
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
PROBE_DIR = os.path.join(ROOT, 'tools', '_probe')
IDLE_FILE = os.path.join(PROBE_DIR, 'idle.txt')

FAKE_IDLE_ENV = 'EYE_LITE_FAKE_IDLE_MS'
FAKE_IDLE_FILE_ENV = 'EYE_LITE_FAKE_IDLE_FILE'

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

DIM_SEEN = [False]      # 压暗层是否出现过（信息项，不参与判定）
_pid_cache = set()


def ex_style(hwnd):
    return user32.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    n = [0]

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return n[0]


# ---------------------------------------------------------------- 窗口


def list_windows():
    out = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        r = wt.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return True
        pid = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        out.append((hwnd, r.right - r.left, r.bottom - r.top, pid.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def own_windows():
    if not _pid_cache:
        return []
    return [t for t in list_windows() if t[3] in _pid_cache]


def overlay_windows():
    out = []
    for hwnd, w, h, pid in own_windows():
        if not (w > 800 and h > 500):
            continue
        if ex_style(hwnd) & WS_EX_TRANSPARENT:
            DIM_SEEN[0] = True          # 压暗层：信息项，不算遮罩
            continue
        if child_count(hwnd) < 1:
            continue
        out.append((hwnd, w, h, pid))
    return out


def preview_windows():
    return [t for t in own_windows() if 200 < t[1] <= 900 and t[2] <= 300]


# ---------------------------------------------------------------- 进程/配置


def eyelite_pids():
    pids = []
    try:
        out = subprocess.run(
            ['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
            capture_output=True)
        for line in out.stdout.decode('utf-8', 'ignore').splitlines():
            if 'EyeLite.exe' not in line:
                continue
            parts = [p.strip('" ') for p in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                pids.append(int(parts[1]))
    except Exception:
        pass
    return pids


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    _pid_cache.clear()
    t0 = time.time()
    while time.time() - t0 < 12:
        if not eyelite_pids():
            return True
        time.sleep(0.25)
    return False


def launch(fake_idle_ms=None, idle_file=None):
    env = os.environ.copy()
    for k in ('EYE_LITE_FORCE_QUNS', 'EYE_LITE_FORCE_QUNS_FILE',
              'EYE_LITE_SUPPRESS_MAX_MS', FAKE_IDLE_ENV, FAKE_IDLE_FILE_ENV):
        env.pop(k, None)
    if fake_idle_ms is not None:
        env[FAKE_IDLE_ENV] = str(fake_idle_ms)
    if idle_file is not None:
        env[FAKE_IDLE_FILE_ENV] = idle_file
    proc = subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _pid_cache.add(proc.pid)
    return proc


# ---------------------------------------------------------------- 伪造空闲文件


def ensure_probe_dir():
    if not os.path.isdir(PROBE_DIR):
        try:
            os.makedirs(PROBE_DIR)
        except Exception as e:
            print('  创建 _probe 目录失败:', e)


def write_idle(ms):
    """把伪造空闲时长写进文件，返回回读值（写进去读不回来等于没写）。"""
    ensure_probe_dir()
    with open(IDLE_FILE, 'w', encoding='utf-8') as f:
        f.write('%d\n' % ms)
    return read_idle()


def read_idle():
    try:
        with open(IDLE_FILE, 'r', encoding='utf-8-sig') as f:
            return f.read().strip()
    except Exception as e:
        return 'ERR:%s' % e


def cleanup_probe():
    try:
        os.remove(IDLE_FILE)
    except Exception:
        pass
    try:
        os.rmdir(PROBE_DIR)          # 只在空目录时成功
    except Exception:
        pass


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
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    order = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in order:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in order:
                f.write('%s=%s\n' % (k, d[k]))


# ---------------------------------------------------------------- 观测


def watch(seconds, tag='', hb=15.0):
    """在 seconds 内轮询遮罩/横幅/压暗层，返回 (first_overlay, first_preview)。"""
    t0 = time.time()
    first_overlay = None
    first_preview = None
    last = t0
    while time.time() - t0 < seconds:
        if first_overlay is None and overlay_windows():
            first_overlay = time.time() - t0
            print('    %s遮罩出现 @%.1fs' % (tag, first_overlay))
            return first_overlay, first_preview
        if first_preview is None and preview_windows():
            first_preview = time.time() - t0
        if time.time() - last >= hb:
            last = time.time()
            print('    %s... %.0fs  无遮罩' % (tag, time.time() - t0))
        time.sleep(0.1)
    return first_overlay, first_preview


def alive(pid):
    return pid in eyelite_pids()


# ---------------------------------------------------------------- 用例


def case_d1a_control(base):
    """D-1 对照组：FAKE_IDLE_MS=0（人就在）→ 必须弹遮罩（证明计时在跑）。"""
    print('\n--- D1a 对照组：FAKE_IDLE_MS=0，应在 ≈60s 弹 ---')
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    cfg = dict(base)
    cfg.update({'IdleMinutes': '1', 'RestSeconds': '120'})
    write_cfg(cfg)
    proc = launch(0)
    print('  config: WorkMinutes=1 RestSeconds=120 IdleMinutes=1 %s=0' % FAKE_IDLE_ENV)
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失（单实例被顶掉？）')
        return None
    ov, _ = watch(95.0, tag='等遮罩')
    ok = ov is not None and 45.0 <= ov <= 75.0
    return (ok, ov, alive(proc.pid))


def case_d1b_experiment(base):
    """D-1 实验组：FAKE_IDLE_MS=65000（65s 前有过输入，超过 IdleMinutes=1 分钟）。"""
    print('\n--- D1b 实验组：FAKE_IDLE_MS=65000，95s 内不得弹 ---')
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    cfg = dict(base)
    cfg.update({'IdleMinutes': '1', 'RestSeconds': '120'})
    write_cfg(cfg)
    proc = launch(65000)
    print('  config: WorkMinutes=1 RestSeconds=120 IdleMinutes=1 %s=65000'
          '（65s >= 60s → 暂停；65s < 120s → 不触发 D2 重置）' % FAKE_IDLE_ENV)
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失（单实例被顶掉？）')
        return None
    ov, pv = watch(95.0, tag='抑制期')
    still = alive(proc.pid)
    return (ov is None, ov, pv, still)


def case_d2b_attribution(base):
    """D-2 归因：靠"离开 → 回来"之后的延迟区分"重置"与"暂停"。

    流程（全部靠 EYE_LITE_FAKE_IDLE_FILE 驱动，运行中可改）：
      写 0       → 人就在，正常累计
      等 52s     → 累计吃掉大半个周期，剩余 ≈8s
      写 130000 保持 2s → 130s >= RestSeconds(8s) → 命中 ResetWork 分支
      写回 0     → 人回来了
    判据：写回后 ≤10s 弹 = 只是暂停（剩余 8s 还在）→ FAIL；
          40~90s 才弹 = 周期被重置（要重走 60s）→ PASS；
          90s 不弹 = 卡死 → FAIL（打印时间线）。
    """
    print('\n--- D2b 归因：离开超过整个休息时长后，回来不该立刻弹 ---')
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    got = write_idle(0)
    if got != '0':
        print('  仪器自检失败：空闲文件写入后回读为 %r' % got)
        return None
    print('  空闲文件 %s ← 0（回读 %r）' % (IDLE_FILE, got))

    cfg = dict(base)
    cfg.update({'IdleMinutes': '1', 'RestSeconds': '8'})
    write_cfg(cfg)
    proc = launch(idle_file=IDLE_FILE)
    print('  config: WorkMinutes=1 RestSeconds=8 IdleMinutes=1 %s=%s'
          % (FAKE_IDLE_FILE_ENV, IDLE_FILE))
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失（单实例被顶掉？）')
        return None

    # ---- 阶段一：活跃累计 52s ----
    t0 = time.time()
    ov_early = None
    while time.time() - t0 < 52.0:
        if overlay_windows():
            ov_early = time.time() - t0
            break
        time.sleep(0.1)
    if ov_early is not None:
        print('  异常：累计还没到 52s（%.1fs）就弹了遮罩，配置或计时不对' % ov_early)
        return ('early', ov_early, None, None)
    print('  活跃累计 52s：无遮罩（剩余应约 8s）')

    # ---- 阶段二：伪造"离开 130s"，保持 2s（命中重置分支）----
    got = write_idle(130000)
    print('  写 130000（回读 %r），保持 2s —— 130s >= RestSeconds 8s → 重置分支' % got)
    t_hold = time.time()
    ov_hold = None
    while time.time() - t_hold < 2.0:
        if overlay_windows():
            ov_hold = time.time() - t_hold
            break
        time.sleep(0.1)
    if ov_hold is not None:
        print('  异常：伪造空闲期间竟然弹了遮罩（%.2fs）' % ov_hold)

    # ---- 阶段三：写回 0（人回来了），计时到遮罩 ----
    t_back = time.time()
    got = write_idle(0)
    print('  写回 0（回读 %r），开始计时到遮罩' % got)
    ov_after = None
    last_hb = 0.0
    while time.time() - t_back < 90.0:
        if overlay_windows():
            ov_after = time.time() - t_back
            break
        el = time.time() - t_back
        if el - last_hb >= 15.0:
            last_hb = el
            print('    写回后 %.0fs：仍无遮罩' % el)
        time.sleep(0.1)
    still = alive(proc.pid)
    return ('ok', ov_after, still, ov_hold)


def case_d2a_long_idle(base):
    """D-2 补充证据：长时间离开（FAKE_IDLE_MS=999999）→ 120s 内不打扰。

    这条只证明"长空闲不打扰"（与 D-1 的暂停语义一致），
    不能替代 D2b 的"重置"归因。
    """
    print('\n--- D2a 补充：FAKE_IDLE_MS=999999 + RestSeconds=8，120s 内不得弹 ---')
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    cfg = dict(base)
    cfg.update({'IdleMinutes': '1', 'RestSeconds': '8'})
    write_cfg(cfg)
    proc = launch(fake_idle_ms=999999)
    print('  config: WorkMinutes=1 RestSeconds=8 IdleMinutes=1 %s=999999'
          % FAKE_IDLE_ENV)
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失（单实例被顶掉？）')
        return None
    ov, pv = watch(120.0, tag='长空闲')
    still = alive(proc.pid)
    return (ov is None, ov, pv, still)


# ---------------------------------------------------------------- main


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at %s' % EXE)
        return 2
    if not stop_eyelite():
        print('FAIL: 有残留 EyeLite 进程，无法开始（单实例会让所有用例假 PASS）')
        return 2

    try:
        with open(CFG, 'rb') as f:
            backup_raw = f.read()
    except Exception:
        backup_raw = b''
    orig = read_cfg()
    print('orig config: WorkMinutes=%s IdleMinutes=%s RestSeconds=%s DimAlphaMax=%s'
          % (orig.get('WorkMinutes'), orig.get('IdleMinutes'),
             orig.get('RestSeconds'), orig.get('DimAlphaMax')))

    # D 组只看计时，与 P0-C 的闸门、A2 的渐暗无关，一律隔离掉，避免交叉污染
    base = dict(orig)
    base.update({
        'WorkMinutes': '1',
        'PreviewSeconds': '0',
        'PomodoroEnabled': '0',
        'SedentaryEnabled': '0',
        'SoundEnabled': '0',
        'AllowSkip': '1',
        'StrictMode': '0',
        'SuppressOnFullscreen': '0',
        'DimAlphaMax': '0',
    })

    results = []
    ctl_ok = False
    ctl_ov = None
    try:
        ctl = case_d1a_control(base)
        if ctl is None:
            print('\n对照组无法成立（进程起不来）→ 不产出任何结论')
            return 2
        ctl_ok, ctl_ov, ctl_alive = ctl
        print('  对照组结果：遮罩=%s 进程存活=%s'
              % (('%.1fs' % ctl_ov) if ctl_ov is not None else '未出现', ctl_alive))
        if not ctl_ok:
            print('\n对照组未按时弹出 → 计时链路或配置有问题，"不弹"没有判别力')
            print('不产出 D-1/D-2 结论（不把仪器故障当成产品缺陷）')
            return 2

        exp = case_d1b_experiment(base)
        if exp is None:
            results.append(('D1 空闲超 IdleMinutes → 暂停', False, '实验组无法启动'))
        else:
            no_ov, ov, pv, still = exp
            ok = no_ov and still
            results.append(('D1 空闲超 IdleMinutes → 暂停', ok,
                            '对照组 %.1fs 弹（计时在跑）｜实验组 95s 内遮罩=%s、横幅=%s、'
                            '进程存活=%s'
                            % (ctl_ov or -1,
                               ('有@%.1fs' % ov) if ov is not None else '无',
                               ('有@%.1fs' % pv) if pv is not None else '无',
                               still)))

        d2a = case_d2a_long_idle(base)
        if d2a is None:
            results.append(('D2a 长空闲不打扰', False, '实验组无法启动'))
        else:
            no_ov, ov, pv, still = d2a
            results.append(('D2a 长空闲不打扰', no_ov and still,
                            '120s 内遮罩=%s、横幅=%s、进程存活=%s'
                            '（补充证据，不替代 D2b 的归因）'
                            % (('有@%.1fs' % ov) if ov is not None else '无',
                               ('有@%.1fs' % pv) if pv is not None else '无',
                               still)))

        d2b = case_d2b_attribution(base)
        if d2b is None:
            results.append(('D2b 离开超时后回来不立刻弹（重置归因）', False,
                            '实验组无法启动'))
        elif d2b[0] == 'early':
            results.append(('D2b 离开超时后回来不立刻弹（重置归因）', False,
                            '异常：活跃累计阶段 %.1fs 就弹了遮罩' % d2b[1]))
        else:
            _, ov_after, still, ov_hold = d2b
            if not still:
                results.append(('D2b 离开超时后回来不立刻弹（重置归因）', False,
                                '进程已退出，"不弹"无法采信'))
            elif ov_after is None:
                results.append(('D2b 离开超时后回来不立刻弹（重置归因）', False,
                                '写回 0 后 90s 内始终不弹（卡死）'))
            else:
                if ov_after <= 10.0:
                    verdict = False
                    note = ('  <-- ≤10s：只是暂停，剩余约 8s 还在 → 周期没有被重置')
                elif 40.0 <= ov_after <= 90.0:
                    verdict = True
                    note = ('  <-- 40~90s：周期确实被重置（要重走完整 60s）')
                else:
                    verdict = False
                    note = '  <-- 落在 10~40s 的空白区，判据无法归因'
                results.append(('D2b 离开超时后回来不立刻弹（重置归因）', verdict,
                                '写回 0 后 %.1fs 弹（伪造空闲期间遮罩=%s）%s'
                                % (ov_after,
                                   '有' if ov_hold is not None else '无', note)))
    finally:
        stop_eyelite()
        cleanup_probe()
        if backup_raw:
            with open(CFG, 'wb') as f:
                f.write(backup_raw)
            print('config restored: %d bytes (backup %d bytes, %s)'
                  % (os.path.getsize(CFG), len(backup_raw),
                     'OK' if os.path.getsize(CFG) == len(backup_raw) else 'MISMATCH'))
        else:
            print('WARN: 没有备份到 config.ini（测试配置可能残留在用户配置里！）')
        print('probe dir cleaned: %s' % (not os.path.exists(PROBE_DIR)))

    print()
    for name, ok, detail in results:
        print('%-40s %s  %s' % (name, 'PASS' if ok else 'FAIL', detail))
    print('%-40s %s  %s' % ('D1 对照组(不计分)',
                            'PASS' if ctl_ok else 'FAIL',
                            'FAKE_IDLE_MS=0 时遮罩 %s'
                            % (('%.1fs' % ctl_ov) if ctl_ov is not None else '未出现')))

    npass = sum(1 for r in results if r[1])
    print('\n=== P0-D summary: %d/%d PASS（D-3 未实测，见报告）==='
          % (npass, len(results)))
    for name, ok, detail in results:
        if not ok:
            print('  FAILED: %s (%s)' % (name, detail))
    return 0 if npass == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
