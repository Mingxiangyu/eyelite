#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
会议检测（C2）验收。

判据三条，其中前两条是"安静"的，可以在用户正常用电脑时跑：

  A. 在会中 → **不打扰**：MeetingGuardEnabled=1 + 入会信号为真，
     75 秒内不得出现遮罩 / 预告横幅 / 压暗层。（安静，默认跑）
  B. 不在会中 → **照常提醒**：MeetingGuardEnabled=1 + 入会信号为假，
     应在 ~60s 出现遮罩。**会弹 8 秒全屏，默认不跑**
     （加 --with-control 才跑；跑之前请确认没人正在用这块屏幕）。
  C. 关掉开关 → 即使在会中也照常提醒（防止"开关失效"）。

入会信号用 `EYE_LITE_FAKE_MEETING`：
  =1 恒真 / =0 恒假 / 未设走真实检测（进程白名单 + 可见窗口 + 标题关键词）。
用钩子而不是真去开一个 Zoom 会议，是因为后者无法在自动化里稳定复现，
而且真的会占麦克风、真的会弹窗。

「30 分钟强制放行」这条不在这里验 —— 它复用 P0-C-9 那套机制（`EYE_LITE_SUPPRESS_MAX_MS`
可以把它压到几秒），已在 p0c-suppress 里验过同一段代码。
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


def scan(pid):
    ov = pv = dm = 0
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
        return True
    user32.EnumWindows(CB(cb), 0)
    return ov, pv, dm


def run(meeting, guard, seconds, label):
    stop()
    write_cfg({
        'WorkMinutes': '1', 'RestSeconds': '8', 'PreviewSeconds': '10',
        'IdleMinutes': '0', 'SoundEnabled': '0', 'StrictMode': '0',
        'PomodoroEnabled': '0', 'SedentaryEnabled': '0',
        'SuppressOnFullscreen': '0', 'DimAlphaMax': '45',
        'MeetingGuardEnabled': '1' if guard else '0',
        'Immersive': '0',
    })
    env = os.environ.copy()
    for k in ('EYE_LITE_FORCE_QUNS', 'EYE_LITE_FORCE_QUNS_FILE',
              'EYE_LITE_SUPPRESS_MAX_MS', 'EYE_LITE_FAKE_IDLE_MS',
              'EYE_LITE_FAKE_SED_MS', 'EYE_LITE_FAKE_WORK_MS',
              'EYE_LITE_OPEN_SETTINGS', 'EYE_LITE_NO_TRAY_TEXT'):
        env.pop(k, None)
    env['EYE_LITE_FAKE_MEETING'] = '1' if meeting else '0'

    p = subprocess.Popen([EXE], cwd=ROOT, env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(2.0)
    ov_max = pv_max = dm_max = 0
    t0 = time.time()
    while time.time() - t0 < seconds:
        ov, pv, dm = scan(p.pid)
        ov_max = max(ov_max, ov)
        pv_max = max(pv_max, pv)
        dm_max = max(dm_max, dm)
        time.sleep(2.0)
    alive = p.poll() is None
    print('%-22s 观察 %ds：遮罩峰值=%d 预告峰值=%d 压暗峰值=%d 存活=%s'
          % (label, seconds, ov_max, pv_max, dm_max, alive))
    stop()
    return ov_max, pv_max, dm_max, alive


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe 不存在')
        return 2
    raw = read_raw()
    try:
        # A：在会中 → 不打扰（安静，默认跑）
        ov, pv, dm, alive = run(True, True, 75, 'A 在会中（应安静）')
        a_ok = (ov == 0 and pv == 0 and dm == 0 and alive)
        print('   → %s' % ('PASS 75 秒内没有任何窗口' if a_ok else 'FAIL 出现了不该出现的窗口'))

        ok = a_ok
        if '--with-control' in sys.argv:
            # B：不在会中 → 照常提醒（会弹 8 秒全屏）
            ov2, pv2, dm2, alive2 = run(False, True, 75, 'B 不在会中（应提醒）')
            b_ok = (ov2 >= 1 and alive2)
            print('   → %s（应看到遮罩）' % ('PASS' if b_ok else 'FAIL'))

            # C：关掉开关 → 即使在会中也照常提醒
            ov3, pv3, dm3, alive3 = run(True, False, 75, 'C 开关关（应提醒）')
            c_ok = (ov3 >= 1 and alive3)
            print('   → %s（应看到遮罩）' % ('PASS' if c_ok else 'FAIL'))
            ok = ok and b_ok and c_ok
        else:
            print('   · B/C 未跑（会弹 8 秒全屏）。要跑加 --with-control')

        return 0 if ok else 1
    finally:
        stop()
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('config restored: %d bytes' % len(raw))


if __name__ == '__main__':
    sys.exit(main())
