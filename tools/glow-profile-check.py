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
  · 衰减跨度 ≈ 30~45px               → 宽度符合预期（旧版 6*1.5=9px 一看就窄）

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
        env['EYE_LITE_FAKE_WORK_MS'] = '50000'    # 剩余 10 秒，留足抓图时间
        subprocess.Popen([EXE], env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(2.5)

        rows = grab(0, 0, 64, 64)
        prof = [rows[3][x][1] for x in range(0, 64)]
        print('沿上边缘的绿通道（y=3，x=0..63）：')
        print('  ' + ' '.join('%3d' % v for v in prof))

        edge = prof[0]
        bg = prof[-1]
        span = 0
        for i, v in enumerate(prof):
            if v > bg + 3:
                span = i + 1
        mono = all(prof[i] >= prof[i + 1] - 5 for i in range(0, min(48, len(prof) - 1)))

        print('')
        print('边缘G=%d  背景G=%d  对比=%d  衰减跨度=%dpx  单调下降=%s'
              % (edge, bg, abs(edge - bg), span, mono))
        print('提示：对比度取决于该处桌面的明暗，仅供参考；判据看"跨度 + 单调性"。')

        ok = True
        if span < 20:
            print('FAIL: 衰减跨度过窄（%dpx，期望 30~45px ≈ 26pt × 150%%）' % span)
            ok = False
        if span > 55:
            print('FAIL: 衰减跨度过宽（%dpx），已经溢到屏幕中部' % span)
            ok = False
        if not mono:
            print('FAIL: 不是单调衰减 → 看起来不像渐变，而像一块实心色')
            ok = False
        if ok:
            print('PASS: 单调衰减，跨度 %dpx（旧版 6pt 时约 9px）' % span)
        return 0 if ok else 1
    finally:
        subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('config restored: %d bytes' % len(raw))


if __name__ == '__main__':
    sys.exit(main())
