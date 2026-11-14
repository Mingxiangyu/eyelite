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
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

user32 = ctypes.WinDLL('user32', use_last_error=True)

EYELITE_PID = [0]


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
    """EyeLite 进程里可见的、大尺寸的窗口 = 休息遮罩。"""
    return [w for w in enum_eyelite_windows()
            if w['visible'] and w['w'] > 800 and w['h'] > 500]


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

    results = []

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

    stop_eyelite()

    print('\n================ 汇总 ================')
    allok = True
    for name, ok, detail in results:
        print('  %-28s %s   %s' % (name, 'PASS' if ok else 'FAIL', detail))
        if not ok:
            allok = False
    print('  OVERALL: %s' % ('PASS' if allok else 'FAIL'))
    return 0 if allok else 1


if __name__ == '__main__':
    sys.exit(main())
