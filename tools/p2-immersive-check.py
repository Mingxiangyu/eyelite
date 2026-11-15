#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
沉浸模式验收：**照常计时，但一个窗口都不弹**。

这条用例天然"安静"——断言就是"没有任何窗口出现"，所以可以在用户正常用电脑时跑。

判据：
  1. 开沉浸 + WorkMinutes=1（60 秒就该到点）+ PreviewSeconds=10 + DimAlphaMax=45，
     连续观察 75 秒，**不得出现**任何遮罩 / 预告横幅 / 压暗层。
  2. 进程存活（不能因为跳过了到点逻辑而崩）。
  3. 对照组必须能看到遮罩 —— 但那条会弹 8 秒全屏，默认**不跑**，
     用 `--with-control` 显式开启（用户不在时再跑）。

"记账"这一半无法从外部观测（累加器在进程内，且换配置要重启进程），
只能由代码审查保证：SetImmersive 不调用 ResetWork，tick 里的早退发生在累加之后。
这一点在报告里如实标注为"源码审查"，不冒充实测。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite', 'config.ini')

user32 = ctypes.WinDLL('user32', use_last_error=True)
CB = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [CB, wt.LPARAM]
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20


def read_raw():
    try:
        return open(CFG, 'rb').read()
    except Exception:
        return b''


def write_cfg(d):
    if not os.path.isdir(os.path.dirname(CFG)):
        os.makedirs(os.path.dirname(CFG))
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\r\n[General]\r\n')
        for k, v in d.items():
            f.write('%s=%s\r\n' % (k, v))


def stop():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    time.sleep(1.0)


def pids():
    out = subprocess.run(['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
                         capture_output=True).stdout.decode('utf-8', 'ignore')
    r = []
    for line in out.splitlines():
        if 'EyeLite.exe' in line:
            parts = [p.strip('" ') for p in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                r.append(int(parts[1]))
    return r


def scan(pid):
    """返回 (overlay 数, preview 数, dim 数)。"""
    ov = pv = dm = 0
    seen = []
    def cb(hwnd, lparam):
        nonlocal ov, pv, dm
        if not user32.IsWindowVisible(hwnd):
            return True
        p = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        ex = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        if w >= 1000 and h >= 600:
            if ex & WS_EX_TRANSPARENT:
                dm += 1
            else:
                ov += 1
        elif 300 <= w <= 500 and 100 <= h <= 200:
            pv += 1
        seen.append((w, h))
        return True
    user32.EnumWindows(CB(cb), 0)
    return ov, pv, dm, seen


def run(immersive, seconds, label):
    stop()
    write_cfg({
        'WorkMinutes': '1', 'RestSeconds': '8', 'PreviewSeconds': '10',
        'IdleMinutes': '0', 'SoundEnabled': '0', 'StrictMode': '0',
        'PomodoroEnabled': '0', 'SedentaryEnabled': '0',
        'SuppressOnFullscreen': '0', 'DimAlphaMax': '45',
        'Immersive': '1' if immersive else '0',
    })
    p = subprocess.Popen([EXE], cwd=ROOT,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(2.0)
    seen_ov = seen_pv = seen_dm = 0
    t0 = time.time()
    while time.time() - t0 < seconds:
        ov, pv, dm, _ = scan(p.pid)
        seen_ov = max(seen_ov, ov)
        seen_pv = max(seen_pv, pv)
        seen_dm = max(seen_dm, dm)
        time.sleep(2.0)
    alive = p.poll() is None
    print('%-10s 观察 %ds：遮罩峰值=%d 预告峰值=%d 压暗峰值=%d 进程存活=%s'
          % (label, seconds, seen_ov, seen_pv, seen_dm, alive))
    stop()
    return seen_ov, seen_pv, seen_dm, alive


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe 不存在')
        return 2
    raw = read_raw()
    try:
        ov, pv, dm, alive = run(True, 75, '沉浸开')
        ok = (ov == 0 and pv == 0 and dm == 0 and alive)
        print('  → %s' % ('PASS 75 秒内没有任何窗口' if ok else 'FAIL 出现了不该出现的窗口'))

        if '--with-control' in sys.argv:
            ov2, pv2, dm2, alive2 = run(False, 75, '对照(关)')
            ctl = (ov2 >= 1 and alive2)
            print('  → 对照 %s（应能看到遮罩）' % ('PASS' if ctl else 'FAIL'))
            ok = ok and ctl
        else:
            print('  · 对照组未跑（会弹 8 秒全屏）。要跑加 --with-control')

        print('\n注：关于「照常计时」——累加器在进程内且换配置要重启，无法从外部观测。')
        print('    实现上 SetImmersive 不调用 ResetWork，且 tick 里的早退发生在累加之后，')
        print('    因此时间不会丢。这一条是**源码审查**结论，不是实测，特此标明。')
        return 0 if ok else 1
    finally:
        stop()
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('config restored: %d bytes' % len(raw))


if __name__ == '__main__':
    sys.exit(main())
