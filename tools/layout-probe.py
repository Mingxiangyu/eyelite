# -*- coding: utf-8 -*-
"""设置页底部版式只读探针（E1~E4）。

为什么单独写一个：`tools/settings-ui-check.py` 的 C1/C2/E5 会**真实注入鼠标点击**，
在用户正用电脑时跑会抢焦点、动光标。这里只做只读检查：
不移动鼠标、不点击、不发键，只读窗口几何 + 抓一张 PrintWindow 位图数留白。

判据：
  E1  WS_VSCROLL 样式位 / 滚动条子窗口数 / 窗口宽-客户区宽
  E2  「保存」按钮底边 vs 客户区底边
  E3  位图里按钮下方纯页面底色的行数（视觉留白）
  E4  按钮中心 WindowFromPoint 是否命中它自己

用法：python tools/layout-probe.py <exe>
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys
import time
import zlib

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

u = ctypes.WinDLL('user32', use_last_error=True)
g = ctypes.WinDLL('gdi32', use_last_error=True)

u.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u.WindowFromPoint.argtypes = [wt.POINT]
u.WindowFromPoint.restype = wt.HWND
u.GetParent.argtypes = [wt.HWND]
u.GetParent.restype = wt.HWND
u.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
u.GetWindowLongW.restype = ctypes.c_long
u.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.ClientToScreen.argtypes = [wt.HWND, ctypes.POINTER(wt.POINT)]

EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
GWL_STYLE = -16
WS_VSCROLL = 0x00200000

PAGE_BG = (0xF5, 0xF7, 0xFA)      # Ui.PageBg


def text_of(h):
    buf = ctypes.create_unicode_buffer(256)
    u.GetWindowTextW(h, buf, 256)
    return buf.value


def class_of(h):
    buf = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, buf, 256)
    return buf.value


def kids(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            out.append(h)
        return True

    u.EnumWindows(EnumProc(cb), 0)
    # 再递归子窗口
    allw = []
    for h in out:
        allw.append(h)

        def cb2(c, l):
            allw.append(c)
            return True

        u.EnumChildWindows(h, EnumProc(cb2), 0)
    return allw


def grab(hwnd, w, hgt):
    hdc = u.GetDC(0)
    mem = g.CreateCompatibleDC(hdc)

    class BIH(ctypes.Structure):
        _fields_ = [('biSize', wt.DWORD), ('biWidth', ctypes.c_long),
                    ('biHeight', ctypes.c_long), ('biPlanes', wt.WORD),
                    ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                    ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                    ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', wt.DWORD),
                    ('biClrImportant', wt.DWORD)]

    bi = BIH()
    bi.biSize = ctypes.sizeof(bi)
    bi.biWidth = w
    bi.biHeight = -hgt
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    bits = ctypes.c_void_p()
    hbmp = g.CreateDIBSection(mem, ctypes.byref(bi), 0, ctypes.byref(bits), None, 0)
    g.SelectObject(mem, hbmp)
    ok = u.PrintWindow(hwnd, mem, 2)
    buf = ctypes.string_at(bits, w * hgt * 4)
    g.DeleteObject(hbmp)
    g.DeleteDC(mem)
    u.ReleaseDC(0, hdc)
    return ok, buf


def is_bg(px):
    return (abs(px[0] - PAGE_BG[0]) <= 3 and abs(px[1] - PAGE_BG[1]) <= 3
            and abs(px[2] - PAGE_BG[2]) <= 3)


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else 'EyeLite.exe'
    exe = os.path.abspath(exe)
    env = os.environ.copy()
    env['EYE_LITE_OPEN_SETTINGS'] = '1'

    # 单实例互斥是写死的字符串，与文件名无关 → 有别的实例就起不来。
    # **绝不 /IM 强杀**（用户可能正用着）；发现占用就退出，由调用方决定怎么办。
    ex = subprocess.run(['tasklist', '/FI', 'imagename eq EyeLite.exe', '/FO', 'CSV', '/NH'],
                        capture_output=True).stdout.decode('utf-8', 'ignore')
    pids = [ln.split('","')[1] for ln in ex.splitlines() if 'EyeLite.exe' in ln]
    if pids:
        print('[ABORT] 已有 EyeLite 实例在跑（pid=%s）。本探针要独占互斥，请先自己关掉。'
              % ','.join(pids))
        return 3

    p = subprocess.Popen([exe], env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    t0 = time.time()
    win = None
    while time.time() - t0 < 25:
        for h in kids(p.pid):
            r = wt.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            w, hh = r.right - r.left, r.bottom - r.top
            if w > 700 and hh > 800 and not u.GetParent(h):
                win = (h, w, hh, r)
                break
        if win:
            break
        time.sleep(0.2)

    if not win:
        print('[FAIL] 25s 内没等到设置窗')
        subprocess.run(['taskkill', '/PID', str(p.pid), '/F'], capture_output=True)
        return 1

    h, w, hh, wr = win
    time.sleep(1.5)

    cr = wt.RECT()
    u.GetClientRect(h, ctypes.byref(cr))
    cw, chh = cr.right - cr.left, cr.bottom - cr.top
    style = u.GetWindowLongW(h, GWL_STYLE)
    has_sb = bool(style & WS_VSCROLL)

    print('窗口 %dx%d  客户区 %dx%d' % (w, hh, cw, chh))
    print('[E1] WS_VSCROLL=%d  宽差=%d  预期滚动条宽≈%d'
          % (1 if has_sb else 0, w - cw, int(17 * 1.5)))

    # 找「保存」按钮
    save = None
    for k in kids(p.pid):
        if text_of(k).strip() == '保存':
            save = k
            break
    if save is None:
        print('[FAIL] 找不到「保存」按钮')
        subprocess.run(['taskkill', '/PID', str(p.pid), '/F'], capture_output=True)
        return 1

    br = wt.RECT()
    u.GetWindowRect(save, ctypes.byref(br))
    # 客户区**在屏幕上的**底边：必须用 ClientToScreen 换算。
    # 不能写 `wr.bottom - chh` —— 客户区起点在标题栏下面，不是窗口顶边，
    # 那样算出来的数跟客户区没有关系（我第一版就是这么错的，量出 -2010px）。
    pt = wt.POINT(0, 0)
    u.ClientToScreen(h, ctypes.byref(pt))
    client_bottom = pt.y + chh
    print('「保存」底边 %d  vs 客户区底边 %d  → 余量 %d px'
          % (br.bottom, client_bottom, client_bottom - br.bottom))
    print('[E2] %s' % ('PASS' if br.bottom <= client_bottom else 'FAIL'))

    cx = (br.left + br.right) // 2
    cy = (br.top + br.bottom) // 2
    hit = u.WindowFromPoint(wt.POINT(cx, cy))
    print('[E4] 按钮中心(%d,%d) 命中 %s「%s」→ %s'
          % (cx, cy, class_of(hit), text_of(hit),
             'PASS' if hit == save else 'FAIL'))

    # E3：位图里按钮下方还有多少行纯页面底色
    ok, buf = grab(h, w, hh)
    if not ok:
        print('[SKIP] PrintWindow 失败')
    else:
        by0 = br.bottom - wr.top + 4        # 避开圆角描边/抗锯齿
        rows = 0
        for y in range(by0, hh):
            row_bg = True
            for x in range(max(0, br.left - wr.left), min(w, br.right - wr.left), 3):
                i = (y * w + x) * 4
                if not is_bg((buf[i + 2], buf[i + 1], buf[i])):
                    row_bg = False
                    break
            if row_bg:
                rows += 1
            elif rows > 0:
                break
        print('[E3] 按钮下方纯底色行数 = %d（阈值 ≥10）→ %s'
              % (rows, 'PASS' if rows >= 10 else 'FAIL'))

    subprocess.run(['taskkill', '/PID', str(p.pid), '/F'], capture_output=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
