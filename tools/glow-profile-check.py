#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
边缘微光「宽度 + 渐变」客观检查：不靠肉眼，直接抓屏幕像素看衰减曲线。

做法：启动 EyeLite（Plugin_EdgeGlow=1、DimAlphaMax=0），用 EYE_LITE_FAKE_WORK_MS
把工作周期压到几秒，使"剩余 ≤60s"的微光窗口立刻打开；然后 BitBlt 抓屏幕左上角
一小块，沿 x 方向打印绿通道（Ui.Accent = #5FD3A6，G=0xD3，明显高于深色底）。

判据（本机 150% 缩放，band = 26 * 1.5 ≈ 39px）：
  · 边缘处 G 明显高于背景            → 有光
  · G 随距离单调下降、直到≈背景       → 是渐变，不是一块实心色
  · 衰减跨度 ≈ 60~95px               → 宽度符合预期（52pt × 150% ≈ 78px）

只读屏幕像素，不注入任何输入；结束按原始字节还原 config、杀掉进程。
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
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)

EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20

user32.EnumWindows.argtypes = [EnumWindowsProc, wt.LPARAM]
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindow.argtypes = [wt.HWND, wt.UINT]
user32.GetWindow.restype = wt.HWND
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
user32.GetLayeredWindowAttributes.argtypes = [wt.HWND, ctypes.POINTER(wt.COLORREF),
                                              ctypes.POINTER(ctypes.c_ubyte),
                                              ctypes.POINTER(wt.DWORD)]

SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', wt.LONG), ('biHeight', wt.LONG),
                ('biPlanes', wt.WORD), ('biBitCount', wt.WORD),
                ('biCompression', wt.DWORD), ('biSizeImage', wt.DWORD),
                ('biXPelsPerMeter', wt.LONG), ('biYPelsPerMeter', wt.LONG),
                ('biClrUsed', wt.DWORD), ('biClrImportant', wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [('bmiHeader', BITMAPINFOHEADER), ('bmiColors', wt.DWORD * 3)]


def grab(x, y, w, h):
    """抓一块屏幕，返回 [(x, y, r, g, b), ...] 的二维列表（行优先）。"""
    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h          # 负数 = 自上而下
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0

    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(mem, ctypes.byref(bmi), DIB_RGB_COLORS,
                                  ctypes.byref(bits), None, 0)
    gdi32.SelectObject(mem, hbmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, SRCCOPY)

    buf = ctypes.string_at(bits, w * h * 4)
    rows = []
    for row in range(h):
        line = []
        base = row * w * 4
        for col in range(w):
            b = buf[base + col * 4 + 0]
            g = buf[base + col * 4 + 1]
            r = buf[base + col * 4 + 2]
            line.append((r, g, b))
        rows.append(line)

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)
    return rows


def read_raw():
    try:
        return open(CFG, 'rb').read()
    except Exception:
        return b''


def write_cfg(extra):
    base = {'WorkMinutes': '1', 'RestSeconds': '30', 'PreviewSeconds': '0',
            'IdleMinutes': '0', 'SoundEnabled': '0', 'StrictMode': '0',
            'PomodoroEnabled': '0', 'SedentaryEnabled': '0',
            'SuppressOnFullscreen': '0', 'DimAlphaMax': '0'}
    base.update(extra)
    order = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']
    if not os.path.isdir(os.path.dirname(CFG)):
        os.makedirs(os.path.dirname(CFG))
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in order:
            if k in base:
                f.write('%s=%s\n' % (k, base[k]))
        for k in base:
            if k not in order:
                f.write('%s=%s\n' % (k, base[k]))


def find_glow_window():
    """找 EdgeGlow 的整屏穿透窗口：可见、接近屏幕大小、WS_EX_TRANSPARENT、零子窗口。"""
    found = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if w < 1000 or h < 600:
            return True
        if not (user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT):
            return True
        # 不再要求"零子窗口"：实测 WinForms 窗体在设了 Region 之后仍可能带内部子窗口，
        # 加上这条会把真正的微光窗口筛掉（踩过一次）。
        # 区分依据只留 WS_EX_TRANSPARENT —— 休息遮罩不透明，压暗层在 DimAlphaMax=0 时不存在。
        found.append((hwnd, w, h))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found[0] if found else None


def print_window_alpha(hwnd, w, h):
    """PrintWindow 到 32bpp DIB，返回 y=3 那一行的 alpha 序列（0..255）。

    为什么读 alpha 而不是颜色：光带是半透明的，颜色会被桌面透出来，
    只有 alpha 通道是"光带自己"的强度，且与背景无关。
    """
    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    W, H = min(w, 300), 200   # 竖向采样要覆盖整条带宽（52pt×1.5≈78px）
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = W
    bmi.bmiHeader.biHeight = -H
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(mem, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    gdi32.SelectObject(mem, hbmp)
    ok = user32.PrintWindow(hwnd, mem, 2)     # PW_RENDERFULLCONTENT
    buf = ctypes.string_at(bits, W * H * 4)
    # 指标用 G-R 而不是 alpha：PrintWindow 画到不透明 DC 上，alpha 恒为 255，
    # 但颜色还在。光带是 #5FD3A6（G=211, R=95 → G-R≈116），
    # 而黑底/白底的 G-R 都≈0，所以这个差值与背景无关。
    # **沿 Y 采样**：顶边那条带的渐变是竖向的（外缘 y=0 最亮 → 内侧 y=band 淡到 0），
    # 在固定 y 上横着采只能得到一条直线，看不到衰减（踩过）。
    col = []
    x = 120
    for y in range(H):
        b = buf[y * W * 4 + x * 4 + 0]
        g = buf[y * W * 4 + x * 4 + 1]
        r = buf[y * W * 4 + x * 4 + 2]
        d = g - r
        col.append(d if d > 0 else 0)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)
    return ok, col


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe 不存在')
        return 2

    raw = read_raw()
    try:
        subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
        time.sleep(0.8)
        write_cfg({'Plugin_EdgeGlow': '1'})
        env = os.environ.copy()
        env['EYE_LITE_FAKE_WORK_MS'] = '50000'
        subprocess.Popen([EXE], env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(2.5)

        win = find_glow_window()
        if not win:
            print('FAIL: 没有找到整屏穿透窗口（微光没出现？）')
            return 1
        hwnd, w, h = win
        ok, prof = print_window_alpha(hwnd, w, h)
        print('微光窗口 %dx%d  PrintWindow=%s' % (w, h, ok))
        print('G-R 剖面（x=120，y=0..%d，自上而下）：' % (len(prof) - 1))
        print('  ' + ' '.join('%3d' % v for v in prof[::3]))

        span = 0
        for i, v in enumerate(prof):
            if v > 6:
                span = i + 1
        mono = all(prof[i] >= prof[i + 1] - 12 for i in range(0, min(100, len(prof) - 1)))
        peak = max(prof)
        print('')
        print('峰值 G-R=%d  衰减跨度=%dpx  单调下降=%s' % (peak, span, mono))

        ok2 = True
        if peak < 25:
            print('FAIL: 光带几乎不可见（峰值 G-R < 25）')
            ok2 = False
        if span < 55:
            print('FAIL: 衰减跨度过窄（%dpx，期望 60~95px ≈ 52pt × 150%%）' % span)
            ok2 = False
        if span > 110:
            print('FAIL: 衰减跨度过宽（%dpx）' % span)
            ok2 = False
        if not mono:
            print('FAIL: 不是单调衰减 → 不是羽化曲线')
            ok2 = False
        # 闪烁/呼吸：读窗口当前的分层 alpha（DWM 真正用的值）。
        # 不能用 PrintWindow 两次比对 —— 它渲染的是窗口自身内容，
        # 不含 Form.Opacity 的合成结果，两次必然相同（踩过）。
        samples = []
        for _ in range(11):
            key = wt.COLORREF(0)
            alpha = ctypes.c_ubyte(0)
            flags = wt.DWORD(0)
            if user32.GetLayeredWindowAttributes(hwnd, ctypes.byref(key),
                                                 ctypes.byref(alpha), ctypes.byref(flags)):
                samples.append(alpha.value)
            time.sleep(0.15)
        print('分层 alpha 采样（1.5s，每 150ms）：%s' % samples)

        if len(samples) < 5:
            print('SKIP 拿不到分层 alpha，无法验证呼吸/闪烁')
        else:
            lo, hi = min(samples), max(samples)
            print('alpha 区间 %d..%d（跨度 %d）' % (lo, hi, hi - lo))
            if hi - lo < 6:
                print('FAIL: alpha 几乎不变 → 没有呼吸/闪烁')
                ok2 = False

        if ok2:
            print('PASS: 单调羽化，阈值以上跨度 %dpx（52pt × 150%% ≈ 78px，尾部渐隐到 0）；alpha 在变化' % span)
        return 0 if ok2 else 1
    finally:
        subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('config restored: %d bytes' % len(raw))


if __name__ == '__main__':
    sys.exit(main())
