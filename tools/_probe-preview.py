#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
预告横幅（右下角那个小窗）的倒计时验证：
  1. 数字是否每秒只减 1（不跳）
  2. 数字 Label 的高度是否容得下字号（150% 下 Sc(38) 行高约 89px）

为什么单独验一遍：休息遮罩上的数字是画在环里的（没有控件文本），
而预告横幅的数字是真正的 Label，可以读文本 —— 两处代码不同，得分开验。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import time

EXE = r'D:\WorkbuddyWorkSpace\2026-11-11-10-05-35\EyeLite\EyeLite.exe'
CFG = os.path.join(os.environ['APPDATA'], 'EyeLite', 'config.ini')

u = ctypes.WinDLL('user32', use_last_error=True)
CB = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u.EnumWindows.argtypes = [CB, wt.LPARAM]
u.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
u.GetWindowLongW.restype = ctypes.c_long


def preview(pid):
    res = []

    def cb(h, l):
        if not u.IsWindowVisible(h):
            return True
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != pid:
            return True
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        w, hh = r.right - r.left, r.bottom - r.top
        if not (300 <= w <= 500 and 100 <= hh <= 220):
            return True
        kids = []

        def cb2(ch, lp):
            m = u.GetWindowTextLengthW(ch)
            b = ctypes.create_unicode_buffer(m + 2)
            u.GetWindowTextW(ch, b, m + 1)
            rr = wt.RECT()
            u.GetWindowRect(ch, ctypes.byref(rr))
            kids.append((b.value, rr.bottom - rr.top))
            return True

        u.EnumChildWindows(h, CB(cb2), 0)
        res.append(kids)
        return True

    u.EnumWindows(CB(cb), 0)
    return res[0] if res else None


def main():
    raw = open(CFG, 'rb').read()
    try:
        subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
        time.sleep(1)
        open(CFG, 'w', encoding='utf-8').write(
            "# EyeLite config\r\n[General]\r\nWorkMinutes=1\r\nRestSeconds=8\r\n"
            "PreviewSeconds=10\r\nIdleMinutes=0\r\nSoundEnabled=0\r\nStrictMode=0\r\n"
            "DimAlphaMax=0\r\nEyeGameEnabled=1\r\nMeetingGuardEnabled=0\r\n"
            "SuppressOnFullscreen=0\r\n")
        p = subprocess.Popen([EXE], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        seq, hgt = [], None
        t0 = time.time()
        while time.time() - t0 < 75:
            k = preview(p.pid)
            if k:
                for txt, h in k:
                    if txt.isdigit():
                        v = int(txt)
                        if not seq or v != seq[-1]:
                            seq.append(v)
                        hgt = h
            time.sleep(0.15)
            if seq and seq[-1] == 0:
                break

        bad = [(a, b) for a, b in zip(seq, seq[1:]) if a - b not in (0, 1)]
        print('预告倒计时序列:', seq)
        print('跳变:', bad if bad else '无')
        print('数字 Label 高度: %s px（38pt 字形行高约 59px，需要 >=60 才不裁）' % hgt)
        ok = bool(seq) and not bad and (hgt is not None and hgt >= 60)
        print('=> %s' % ('PASS 每秒减 1 且高度足够' if ok else 'FAIL'))
        return 0 if ok else 1
    finally:
        subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
        open(CFG, 'wb').write(raw)
        print('restored', len(raw))


if __name__ == '__main__':
    raise SystemExit(main())
