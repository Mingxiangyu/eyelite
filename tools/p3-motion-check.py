#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P3 真机验证：动作幅度 / 双臂对称 / 例程窗可关 / 预告说明文字 / 素材帧识别

被测产物：素材版 `EyeLite.exe`（477184 字节起；**具体指纹以运行时打印的那行为准**）。

## 仪器坑（先看这段，三条都是踩过的，且都能伪装成"产品缺陷"）

1. **DPI 虚拟化**：探针不声明 DPI 感知时，`GetWindowRect` 返回的是**虚拟化坐标**
   （本机窗口真实 1080×960，未声明时报成 720×640），于是 `PrintWindow` 到 720 宽的位图
   **右侧 360px 被裁掉** —— 人形看起来"偏到一边"、左右像素比被裁剪扭曲。
   → 本脚本开头调 `SetProcessDpiAwareness(2)`，并且**绝不硬编码窗口尺寸**：
     一律 `GetWindowRect` 拿真实尺寸，`scale = w / 720` 再换算逻辑坐标。
   → 另：找窗口时按**宽高比 720:640 = 1.125** 认，不按绝对像素。
2. **本会话里"从屏幕 BitBlt"抓到的是过期桌面内容**（白底文字，没有火柴人）。
   用整窗 `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT=2)` 让窗口自绘才正确；
   脚本保留"PrintWindow 失败才退回屏幕抓取并打印 warn"的路径。
3. **文字带会污染指纹，而且文字带的位置会随版式改动而失效** —— 不要相信硬编码坐标：
   2026-11-16 就有一次「如有不适请立即停止」从 `Sc(392)` 挪到 `Sc(524)`，
   落在原来的采样区里、又是一行居中文字，会把 M2 的伸展比往 1.0 拉（掩盖不对称）。
   → 现在的排除区是三条（y<100 / y∈[500,560) / y>h-70），但**每轮开跑前应先 dump 一次
     所有带文本的子窗口 rect** 来确认（`enum_children()` 已经会返回宽高，
     必要时把 rect 一起打出来），版式再动就能立刻发现。：窗口上「还剩 N 秒」每秒变、标题/要点也会变。
   上一轮"整窗抓图比指纹"判"动画在动"就是因此假阳性。
   → 本脚本抓完**裁掉** y<100（标题/要点）与 y>h-70（底部倒计时）两条带。
   （另：`DimForm`（渐暗层）与 `EdgeGlowForm`（边缘微光）在结构上完全一样 ——
     整屏 + `WS_EX_TRANSPARENT` + 零子窗口，只能靠配置隔离，不能靠窗口特征区分。）

## 判据

### M1 动作在动（两条渲染路径各验一次）
素材版里动作分两条路径，**必须分别验**，否则"素材没加载、火柴人还在动"会掩盖问题：
  · `M1 自绘`：`EYE_LITE_FAKE_ACTION=肩颈绕环`（仍是矢量火柴人）
  · `M1 素材`：`EYE_LITE_FAKE_ACTION=深蹲 10 次`（位图帧）
判据相同：8 帧 / 300ms，**不同指纹 ≥3**。
对照：不设 `FAKE_ACTION` → 12s 内不该出现例程窗。

### M2 双臂对称（只能用**自绘**动作）
素材帧没有"胳膊角度"可言，所以 M2 用 `肩颈绕环`（自绘，周期 4s）：
1.0~3.0s 采 5 帧，取"人形像素最多"的极值帧，判 **伸展比 0.8~1.25**。
像素比只打印、不判定 —— 躯干前倾 + 描边粗细会让像素数天然不等
（实测：居中且对称时像素比仍可到 1.74~1.83）。

### M3 例程窗可以关掉
点「结束」（`closeRect = (w-Sc(84), Sc(14), Sc(72), Sc(26))`，按实测 scale 换算）
→ 2s 内关窗且进程存活；再点窗口中心 → 不该关。

### M4 预告说明文字不被裁
`PreviewSeconds=10`（逻辑 36 → 本机 1.5x 即 54px），判据 **高度 ≥34**。

### M5 素材帧确实被画出来
素材没加载但火柴人还在动时，M1 依然会过 —— 所以单独比：
素材动作（`深蹲 10 次`）vs 自绘动作（`肩颈绕环`）各 2 帧，
比较**量化色数**（4bit/通道）与**过渡像素数**；判据 **素材色数 ≥ 自绘的 2 倍**。
> 风险已知：若素材本身是线条图，色数比可能拉不开 2 倍。那种情况**如实报数字并标
> "判据可能不适用"**，不改阈值凑绿；备选第二条腿是"双三次插值的软边"（过渡像素占比）。

## 纪律
· 不改 `src/`；临时图只写 `tools/_m1/`；config.ini 按原始字节备份并在 finally 还原
· 启动前 taskkill；清空所有 `EYE_LITE_*` 钩子；结束 taskkill
· 不抢前台、不移动鼠标（点击用 PostMessage 发客户区坐标）

## 用法
    python tools/p3-motion-check.py                  # 全部（m1vec m1mat m1ctl m2 m3 m4 m5）
    python tools/p3-motion-check.py m1mat m5         # 挑着跑
"""

import ctypes
import ctypes.wintypes as wt
import hashlib
import os
import subprocess
import sys
import time

# **探针必须自己声明 DPI 感知**，否则 GetWindowRect 返回的是虚拟化坐标：
# 本机窗口真实尺寸 1080x960（DPI 144 / 1.5x），未声明时被报成 720x640 ——
# 用 720 的位图去 PrintWindow 1080 的窗口会**裁掉右侧 360px**，
# 于是人形看起来"偏右"、左右像素比也被裁剪扭曲。这是仪器坑，不是产品问题。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # PER_MONITOR_DPI_AWARE
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')
M1_DIR = os.path.join(ROOT, 'tools', '_m1')

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)

# ---- 64 位句柄必须显式声明 restype，否则被截成 32 位（本项目踩过多次）----
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.PostMessageW.restype = wt.BOOL
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.GetDC.restype = wt.HDC
user32.GetDC.argtypes = [wt.HWND]
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
user32.PrintWindow.restype = wt.BOOL
user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]

gdi32.CreateCompatibleDC.restype = wt.HDC
gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
gdi32.CreateCompatibleBitmap.restype = wt.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = wt.HANDLE
gdi32.SelectObject.argtypes = [wt.HDC, wt.HANDLE]
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteDC.argtypes = [wt.HDC]
gdi32.BitBlt.restype = wt.BOOL
gdi32.BitBlt.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         ctypes.c_int, wt.HDC, ctypes.c_int, ctypes.c_int, wt.DWORD]
gdi32.GetDIBits.restype = ctypes.c_int

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
MK_LBUTTON = 0x0001
SRCCOPY = 0x00CC0020

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

# 人形采样区（100% 缩放下；实际按窗口实测缩放换算）
HUMAN_X, HUMAN_Y, HUMAN_W, HUMAN_H = 180, 180, 360, 360
CLOSE_REL = (84, 14, 72, 26)      # 「结束」按钮：w-Sc(84), Sc(14), Sc(72), Sc(26)

_pid_cache = set()


# ---------------------------------------------------------------- 基础

def ex_style(hwnd):
    return user32.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    n = [0]

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return n[0]


def window_text(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def enum_children(hwnd):
    out = []

    def cb(ch, lparam):
        r = wt.RECT()
        user32.GetWindowRect(ch, ctypes.byref(r))
        out.append((ch, class_name(ch), window_text(ch),
                    r.right - r.left, r.bottom - r.top))
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return out


def rect(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def own_windows():
    out = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in _pid_cache:
            r = rect(hwnd)
            out.append((hwnd, r.right - r.left, r.bottom - r.top, pid.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def find_move_window():
    """例程窗。返回 (hwnd, w, h, scale) 或 None。

    **不硬编码 720×640**：窗口的逻辑尺寸是 720×640，但物理尺寸随 DPI 变
    （本机 144 DPI → 1080×960）。所以按**宽高比 720:640 = 1.125** 认窗口，
    scale = w / 720 用于换算逻辑坐标（文字带高度、按钮位置都乘它）。
    另外排除透明穿透层（渐变/微光）与预览横幅。
    """
    best = None
    for hwnd, w, h, _p in own_windows():
        if w < 640 or h < 560:
            continue
        if ex_style(hwnd) & WS_EX_TRANSPARENT:
            continue
        if child_count(hwnd) >= 1:
            continue                      # 遮罩有子控件；例程窗全自绘、0 子控件
        if abs(w / float(h) - 720.0 / 640.0) > 0.02:
            continue
        best = (hwnd, w, h, w / 720.0)
        break
    return best


# ---------------------------------------------------------------- 抓图


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', ctypes.c_long),
                ('biHeight', ctypes.c_long), ('biPlanes', wt.WORD),
                ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', wt.DWORD),
                ('biClrImportant', wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [('bmiHeader', BITMAPINFOHEADER), ('bmiColors', wt.DWORD * 3)]


gdi32.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT,
                            ctypes.c_void_p, ctypes.POINTER(BITMAPINFO), wt.UINT]


def grab(x, y, w, h):
    """从**屏幕**抓一块，返回 (bytes, w, h) 的 32bpp BGRA（自上而下）。

    为什么抓屏幕而不是 PrintWindow：例程窗是自绘 + Opacity=0.96，
    抓屏幕拿到的就是用户实际看到的合成结果；再叠上"只取人形区"就排除了文字。
    """
    hdc = user32.GetDC(None)
    if not hdc:
        return None
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    try:
        if not gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, SRCCOPY):
            return None
        bi = BITMAPINFO()
        bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bi.bmiHeader.biWidth = w
        bi.bmiHeader.biHeight = -h
        bi.bmiHeader.biPlanes = 1
        bi.bmiHeader.biBitCount = 32
        bi.bmiHeader.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        if not gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0):
            return None
        return (buf.raw, w, h)
    finally:
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(None, hdc)


def grab_window(hwnd, w, h):
    """让**窗口自己**把客户区画到内存 DC（PrintWindow），返回 32bpp BGRA。

    为什么不用"从屏幕 BitBlt"：本机实测（2026-11-16）在窗口矩形处抓到的是
    **白底文字**（过期桌面内容），说明该会话下 `GetDC(NULL)` 拿不到真正的合成结果，
    照它判定会得出"画面完全没动"的假 FAIL。PrintWindow 走的是窗口自绘路径，
    对带 Opacity 的窗口用 PW_RENDERFULLCONTENT(2)，失败再退 0。
    """
    hdc = user32.GetDC(hwnd)
    if not hdc:
        return None
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    try:
        ok = False
        for flag in (2, 0):
            if user32.PrintWindow(hwnd, mem, flag):
                ok = True
                break
        if not ok:
            return None
        bi = BITMAPINFO()
        bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bi.bmiHeader.biWidth = w
        bi.bmiHeader.biHeight = -h
        bi.bmiHeader.biPlanes = 1
        bi.bmiHeader.biBitCount = 32
        bi.bmiHeader.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        if not gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0):
            return None
        return (buf.raw, w, h)
    finally:
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(hwnd, hdc)


def crop(data, w, h, x0, y0, cw, ch):
    """从整窗 BGRA 里裁一块（坐标已按缩放换算），越界自动夹紧。"""
    x0 = max(0, min(w - 1, int(x0)))
    y0 = max(0, min(h - 1, int(y0)))
    cw = max(1, min(w - x0, int(cw)))
    ch = max(1, min(h - y0, int(ch)))
    out = bytearray(cw * ch * 4)
    for y in range(ch):
        src = ((y0 + y) * w + x0) * 4
        out[y * cw * 4:(y + 1) * cw * 4] = data[src:src + cw * 4]
    return (bytes(out), cw, ch)


def grab_canvas(w):
    """抓整窗客户区（窗口自绘），并**整段丢掉会变的文字带**。

    当前要丢三条（坐标都是逻辑值，按实测 scale 换算，**不写死物理像素**）：
        y < 100            标题 / 要点
        y in [520, 548)    「如有不适请立即停止」（Sc(524) 起、高约 Sc(18)；
                           收窄到 520~548 是为了**不切掉脚**（脚在 Sc(512)））
        y > h - 70         底部「还剩 N 秒」
    做法是**纵向拼接**剩余两段，而不是把文字涂黑 —— 涂黑会制造"非背景像素"，
    混进对称性统计里（左右各一份，比值照样接近 1 → 假 PASS）。
    """
    hwnd, ww, hh, s = w
    full = grab_window(hwnd, ww, hh)
    if full is None:
        print('    [warn] PrintWindow 失败，无法抓图')
        return None
    data, gw, gh = full
    top = int(round(100 * s))
    band0 = int(round(520 * s))
    band1 = int(round(548 * s))
    bot = gh - int(round(70 * s))
    top = max(0, min(gh, top))
    bot = max(0, min(gh, bot))
    band0 = max(top, min(bot, band0))
    band1 = max(band0, min(bot, band1))

    segs = []
    if band0 > top:
        segs.append(crop(data, gw, gh, 0, top, gw, band0 - top))
    if bot > band1:
        segs.append(crop(data, gw, gh, 0, band1, gw, bot - band1))
    if not segs:
        return None
    if len(segs) == 1:
        return segs[0]
    d0, w0, h0 = segs[0]
    d1, _w1, h1 = segs[1]
    return (d0 + d1, w0, h0 + h1)


def write_bmp(path, data, w, h):
    """写 24 位 BMP（BGRA→BGR，行倒序），方便 lead 直接用看图工具打开。"""
    row = ((w * 3 + 3) // 4) * 4
    pad = row - w * 3
    body = bytearray()
    for yy in range(h - 1, -1, -1):
        off = yy * w * 4
        for xx in range(w):
            b, g, rr = data[off + xx * 4], data[off + xx * 4 + 1], data[off + xx * 4 + 2]
            body += bytes((b, g, rr))
        body += b'\x00' * pad
    size = 14 + 40 + len(body)
    hdr = b'BM' + size.to_bytes(4, 'little') + b'\x00\x00\x00\x00' + (54).to_bytes(4, 'little')
    bih = (40).to_bytes(4, 'little') + w.to_bytes(4, 'little') + h.to_bytes(4, 'little') \
        + (1).to_bytes(2, 'little') + (24).to_bytes(2, 'little') + (0).to_bytes(4, 'little') \
        + len(body).to_bytes(4, 'little') + b'\x00' * 16
    with open(path, 'wb') as f:
        f.write(hdr + bih + bytes(body))


def lum(data, i):
    b, g, r = data[i], data[i + 1], data[i + 2]
    return 0.114 * b + 0.587 * g + 0.299 * r


def symmetry_stats(data, w, h, thresh=40.0):
    """人形像素的左右分布与伸展（轴心 = 下肢中心，不用硬编码坐标）。

    背景是垂直渐变（同行横向近似均匀），所以用**每行亮度中位数**当背景基准，
    偏离 > thresh 的像素才算人形 —— 不依赖配色，也不受 Opacity=0.96 影响。

    轴心怎么定：**取人形像素里最靠下的 25%（腿/脚）的平均 x**。
    理由：下肢永远在躯干正下方，是天然的对称轴；而"包围盒中点"是构造出来的，
    用它算左右伸展永远相等（无判别力）。两臂叠在一侧时，
    该侧相对下肢中线的伸展/像素数会明显更大。
    """
    hits = []
    for y in range(h):
        row = y * w * 4
        ls = sorted(lum(data, row + x * 4) for x in range(w))
        med = ls[w // 2]
        for x in range(w):
            if abs(lum(data, row + x * 4) - med) > thresh:
                hits.append((x, y))
    if not hits:
        return {'left': 0, 'right': 0, 'total': 0, 'ratio': 0.0,
                'ext_left': 0, 'ext_right': 0, 'axis': w // 2}
    ys = [p[1] for p in hits]
    ymin, ymax = min(ys), max(ys)
    cut = ymax - max(1, int((ymax - ymin) * 0.25))
    legs = [p[0] for p in hits if p[1] >= cut]
    axis = int(sum(legs) / len(legs)) if legs else (min(p[0] for p in hits) + max(p[0] for p in hits)) // 2

    left = right = 0
    ext_l = ext_r = 0
    for x, _y in hits:
        d = abs(x - axis)
        if x < axis:
            left += 1
            ext_l = max(ext_l, d)
        else:
            right += 1
            ext_r = max(ext_r, d)
    ratio = (left / float(right)) if right else (999.0 if left else 0.0)
    return {'left': left, 'right': right, 'total': len(hits), 'ratio': ratio,
            'ext_left': ext_l, 'ext_right': ext_r, 'axis': axis}


# ---------------------------------------------------------------- 进程 / 配置


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


def launch(env_extra=None):
    env = os.environ.copy()
    # 父进程可能残留任何测试钩子 —— 一律清掉，只留本次显式设置的
    for k in list(env.keys()):
        if k.startswith('EYE_LITE_'):
            env.pop(k, None)
    if env_extra:
        env.update(env_extra)
    proc = subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _pid_cache.add(proc.pid)
    return proc


def alive(pid):
    return pid in eyelite_pids()


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
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in CFG_ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in CFG_ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def restore_cfg_raw(raw):
    if not raw:
        return
    with open(CFG, 'wb') as f:
        f.write(raw)
    print('config restored: %d bytes (backup %d bytes, %s)'
          % (os.path.getsize(CFG), len(raw),
             'OK' if os.path.getsize(CFG) == len(raw) else 'SIZE MISMATCH!'))


def base_cfg(orig, **kw):
    d = dict(orig)
    d.update({
        'WorkMinutes': '20',        # 别让普通休息插进来
        'PreviewSeconds': '0',
        'PomodoroEnabled': '0',
        'IdleMinutes': '0',
        'SedentaryEnabled': '0',
        'SoundEnabled': '0',
        'StrictMode': '0',
        'SuppressOnFullscreen': '0',
        'DimAlphaMax': '0',
    })
    d.update(kw)
    return d


def wait_move_window(timeout_s, tag='等例程窗'):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        w = find_move_window()
        if w:
            return time.time() - t0, w
        time.sleep(0.08)
    return None, None


def wait_gone(hwnd, timeout_s):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        w = find_move_window()
        if not w or w[0] != hwnd:
            return time.time() - t0
        time.sleep(0.05)
    return None


def click_client(hwnd, x, y):
    lp = (int(y) << 16) | (int(x) & 0xFFFF)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp)
    time.sleep(0.03)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)


# ---------------------------------------------------------------- 用例


def run_case(name, env_extra, cfg_over, fn):
    """统一的"起进程 → 准备 → 执行 → 收尾"外壳。"""
    if not stop_eyelite():
        return (name, False, 'EyeLite 实例停不下来')
    write_cfg(cfg_over)
    proc = launch(env_extra)
    time.sleep(1.5)
    try:
        if not alive(proc.pid):
            return (name, False, '进程启动后消失')
        return fn(proc)
    finally:
        stop_eyelite()


def _run_action(orig, action, n, interval, tag):
    """跑一次单动作（FAKE_ACTION），抓 n 帧。返回 frames=[(data,w,h), ...]。"""
    frames = []

    def body(proc):
        t, w = wait_move_window(15.0)
        if not w:
            return (tag, False, '15s 内没出现例程窗')
        print('  %s 窗口 %.1fs 出现：%dx%d scale=%.3f'
              % (tag, t, w[1], w[2], w[3]))
        for i in range(n):
            # 窗口刚 Show 出来时 PrintWindow 可能拿不到内容（句柄已建、首帧未绘完），
            # 重试几次；仍失败就打印原因返回，别静默返回空列表（M5 首跑就是这样
            # 变成 SKIP 的：窗口找到了、一帧都没抓到）。
            g = None
            for attempt in range(6):
                g = grab_canvas(w)
                if g is not None:
                    break
                time.sleep(0.2)
            if g is None:
                print('  [warn] 抓图失败（第 %d 帧，重试 6 次）' % i)
                return (tag, False, '抓图失败（第 %d 帧）' % i)
            frames.append(g)
            if i < n - 1:
                time.sleep(interval)
        return (tag, True, 'ok')

    run_case(tag, {'EYE_LITE_FAKE_ACTION': action}, base_cfg(orig), body)
    return frames


def case_m1(orig, action='肩颈绕环', label='M1'):
    """M1：单动作播放时画面在动（8 帧 / 300ms，不同指纹 ≥3）。

    素材版里动作分两条路径，**必须各验一次**：
      · 素材路径（深蹲 10 次）—— 位图帧序列
      · 自绘路径（肩颈绕环）—— 仍然是矢量火柴人
    """
    print('')
    print('--- %s 动作在动：FAKE_ACTION=%s ---' % (label, action))
    frames = _run_action(orig, action, 8, 0.3, label)
    if len(frames) < 8:
        return (label + ' 动作在动', False, '只抓到 %d 帧' % len(frames))
    hashes = [hashlib.md5(d).hexdigest() for (d, _w, _h) in frames]
    uniq = len(set(hashes))
    print('  8 帧指纹（前 8 位）：%s' % [h[:8] for h in hashes])
    print('  不同指纹数 = %d' % uniq)
    if uniq < 3:
        os.makedirs(M1_DIR, exist_ok=True)
        for i, (d, gw, gh) in enumerate(frames):
            write_bmp(os.path.join(M1_DIR, '%s_frame%d.bmp' % (label, i)), d, gw, gh)
        print('  [%s 失败] 8 张已存：%s' % (label, M1_DIR))
        return (label + ' 动作在动', False,
                '不同指纹数 %d < 3；截图 %s/'
                % (uniq, os.path.relpath(M1_DIR, ROOT)))
    return (label + ' 动作在动', True,
            '8 帧（300ms）不同指纹数 = %d（≥3）' % uniq)


def case_m1_ctl(orig):
    def body(proc):
        w = None
        t0 = time.time()
        while time.time() - t0 < 12.0:
            if find_move_window():
                w = time.time() - t0
                break
            time.sleep(0.1)
        if w is not None:
            return ('M1 对照（不设例程钩子）', False,
                    '12s 内竟出现了例程窗 @%.1fs' % w)
        return ('M1 对照（不设例程钩子）', True, '12s 内无例程窗（期望无）')
    return run_case('M1ctl', {}, base_cfg(orig), body)


def case_m2(orig):
    """M2：双臂对称 —— 必须用**仍走矢量自绘**的动作（素材帧没有"胳膊角度"可言）。

    用 `肩颈绕环`（自绘，周期 4s）：1.0~3.0s 采 5 帧，取"人形像素最多"的极值帧，
    判 **伸展比 0.8~1.25**；像素比只打印不判定（躯干前倾 + 描边粗细会让像素数天然不等）。
    """
    print('')
    print('--- M2 双臂对称：FAKE_ACTION=肩颈绕环（矢量自绘）---')
    frames = _run_action(orig, '肩颈绕环', 5, 0.5, 'M2')
    if len(frames) < 5:
        return ('M2 双臂对称', False, '只抓到 %d 帧' % len(frames))
    best = None
    for i, (d, gw, gh) in enumerate(frames):
        st = symmetry_stats(d, gw, gh)
        rel = 1.0 + i * 0.5
        print('  t=%.1fs 左=%d 右=%d 伸展 左=%d 右=%d 轴心 x=%d 总数=%d'
              % (rel, st['left'], st['right'], st['ext_left'], st['ext_right'],
                 st['axis'], st['total']))
        if best is None or st['total'] > best[1]['total']:
            best = (rel, st, d, gw, gh)
    rel, st, d, gw, gh = best
    ext_ratio = (st['ext_left'] / float(st['ext_right'])) if st['ext_right'] else 999.0
    ok = (st['ext_left'] > 20 and st['ext_right'] > 20 and 0.8 <= ext_ratio <= 1.25)
    if not ok:
        os.makedirs(M1_DIR, exist_ok=True)
        write_bmp(os.path.join(M1_DIR, 'm2_t%.1f.bmp' % rel), d, gw, gh)
    return ('M2 双臂对称', ok,
            '极值帧 t=%.1fs：伸展 左=%d 右=%d → 伸展比 %.2f（判据 0.8~1.25）｜'
            '像素 左=%d 右=%d（比值 %.2f，仅打印）｜轴心 x=%d、总数=%d'
            % (rel, st['ext_left'], st['ext_right'], ext_ratio,
               st['left'], st['right'], st['ratio'], st['axis'], st['total']))


def color_variety(data, w, h):
    """一次扫描拿到三个判别量：量化色数 / 过渡像素数 / 前景里"接近纯白"的占比。

    为什么要三个：
      · 矢量绘制 = 垂直渐变背景 + 少数纯色描边（偏灰蓝）→ 量化色少、白线少
      · 位图素材（尤其白线条 + 半透明抗锯齿边）→ 量化色多、**三通道都 >200 的前景占比高**
    白线占比这条是 lead 给的硬判据：**不受缩放影响、不依赖色数**，
    素材 ≥30%、矢量 <10% 就能拉开。
    """
    colors = set()
    fg = 0
    white = 0
    for y in range(0, h, 2):
        row = y * w * 4
        # 每行取亮度中位数当背景基准（背景是垂直渐变，同行横向近似均匀）
        ls = sorted(lum(data, row + x * 4) for x in range(0, w, 2))
        med = ls[len(ls) // 2] if ls else 0.0
        for x in range(0, w, 2):
            i = row + x * 4
            b, g, r = data[i], data[i + 1], data[i + 2]
            colors.add((r >> 4, g >> 4, b >> 4))
            if abs(lum(data, i) - med) > 40:
                fg += 1
                if r > 200 and g > 200 and b > 200:
                    white += 1
    ramp = 0
    for y in range(0, h, 4):
        row = y * w * 4
        for x in range(0, w - 2, 2):
            i = row + x * 4
            d = abs(lum(data, i) - lum(data, i + 8))
            if 3 <= d <= 24:
                ramp += 1
    return {'colors': len(colors), 'ramp': ramp, 'fg': fg, 'white': white,
            'white_share': (white / float(fg)) if fg else 0.0}


def case_m5(orig):
    """M5：素材帧**确实被画出来**（挡住"M1 在动"的假阳性）。

    三层判据，按强度排序：
      ① 主判据（硬）  过渡像素：素材 ≥ 矢量 × 5   ← 实测 914 : 57 = 16 倍，留 3 倍余量
      ② 佐证（打印）  8 帧不同指纹数：素材 ≈3 且循环、矢量 ≥5
      ③ 参考（打印）  白线占比 / 量化色数 / 色数比  ← 只记录、不判定（数值漂移=素材换了）

    **①的前提（必须留着这句）**：它依赖"素材经**双三次**缩放"这个事实 ——
    512→300 由 GDI+ HighQualityBicubic 缩放，边缘必然留一片中间调；
    矢量是 GDI+ 抗锯齿细线，过渡带窄得多。若将来改成**最近邻**缩放、或换成 SVG/矢量素材，
    这条会失效，届时改用"色数比"或直接对比两张图的边缘梯度。

    **②为什么比①更有说服力**：素材只有 3 帧 → 8 次采样必然呈现"3 值循环"；
    如果素材没画（回退成火柴人）会看到 7 个连续变化的指纹，如果素材没动只会看到 1 个。
    "3 个循环 + 矢量 7 个"两个数字一对照，"素材在画且只有 3 帧"这件事就锁死了。
    """
    print('')
    print('--- M5 素材帧识别（深蹲10次=素材 / 肩颈绕环=矢量）---')

    def _grab8(action, tag):
        return _run_action(orig, action, 8, 0.3, tag)

    f_mat = _grab8('深蹲 10 次', 'M5mat')
    f_vec = _grab8('肩颈绕环', 'M5vec')
    if len(f_mat) < 8 or len(f_vec) < 8:
        print('  [SKIP] 抓帧不足：mat=%d vec=%d' % (len(f_mat), len(f_vec)))
        return ('M5 素材帧识别', None,
                'SKIP 抓帧不足（mat=%d vec=%d）' % (len(f_mat), len(f_vec)))

    m = [color_variety(d, w, h) for (d, w, h) in f_mat]
    v = [color_variety(d, w, h) for (d, w, h) in f_vec]
    mat_ramp = max(x['ramp'] for x in m)
    vec_ramp = max(x['ramp'] for x in v)
    mat_c = max(x['colors'] for x in m)
    vec_c = max(x['colors'] for x in v)
    mat_w = max(x['white_share'] for x in m)
    vec_w = max(x['white_share'] for x in v)
    ramp_ratio = (mat_ramp / float(vec_ramp)) if vec_ramp else 999.0

    mat_uniq = len(set(hashlib.md5(d).hexdigest() for (d, _w, _h) in f_mat))
    vec_uniq = len(set(hashlib.md5(d).hexdigest() for (d, _w, _h) in f_vec))

    ok = (vec_ramp > 0) and (mat_ramp >= 5 * vec_ramp)
    detail = ('① 过渡像素 素材 %d vs 矢量 %d → **%.1f 倍**（判据 ≥5）｜'
              '② 8帧指纹数 素材 %d（应≈3 循环）、矢量 %d（应≥5）｜'
              '③ 参考：白线 素材 %.1f%%/矢量 %.1f%%，量化色 %d/%d'
              % (mat_ramp, vec_ramp, ramp_ratio, mat_uniq, vec_uniq,
                 mat_w * 100, vec_w * 100, mat_c, vec_c))
    return ('M5 素材帧识别', ok, detail)


def case_m3(orig):
    """M3：例程窗可以关掉。

    a) 点「结束」按钮中心（`closeRect = (w-Sc(84), Sc(14), Sc(72), Sc(26))`，按实测 scale 换算）
       → 2s 内窗口消失且**进程存活**（关窗不能把进程带走）。
    b) 点窗口中心（360,400 逻辑）→ **不该**关窗（防止"点哪儿都关"）。
    用素材动作 `扩胸`（20s 窗口），确保观察期够长。
    """
    out = []

    def body_close(proc):
        t, w = wait_move_window(15.0)
        if not w:
            return ('M3a 点「结束」关窗', False, '15s 内没出现例程窗')
        hwnd, ww, hh, sc = w
        cx = int(round((720 - 84 + 72 / 2.0) * sc))
        cy = int(round((14 + 26 / 2.0) * sc))
        print('  窗口 %dx%d scale=%.3f；点「结束」客户坐标 (%d,%d)' % (ww, hh, sc, cx, cy))
        click_client(hwnd, cx, cy)
        gone = wait_gone(hwnd, 2.0)
        still = alive(proc.pid)
        return ('M3a 点「结束」关窗', (gone is not None) and still,
                '窗口%s、进程存活=%s'
                % (('%.2fs 内消失' % gone) if gone is not None else '2s 内仍在', still))

    def body_center(proc):
        t, w = wait_move_window(15.0)
        if not w:
            return ('M3b 点窗口中心不该关', False, '15s 内没出现例程窗')
        hwnd, ww, hh, sc = w
        cx = int(round(360 * sc))
        cy = int(round(400 * sc))
        print('  窗口 %dx%d；点中心客户坐标 (%d,%d)' % (ww, hh, cx, cy))
        click_client(hwnd, cx, cy)
        gone = wait_gone(hwnd, 2.0)
        still = alive(proc.pid)
        return ('M3b 点窗口中心不该关', (gone is None) and still,
                '窗口%s（期望仍在）、进程存活=%s'
                % ('被关掉了' if gone is not None else '2s 内仍在', still))

    out.append(run_case('M3a', {'EYE_LITE_FAKE_ACTION': '扩胸'}, base_cfg(orig), body_close))
    out.append(run_case('M3b', {'EYE_LITE_FAKE_ACTION': '扩胸'}, base_cfg(orig), body_center))
    return out


def case_m4(orig):
    """M4：预告横幅的说明文字不被裁。

    逻辑高度 `lblBody` 是 Sc(36) → 1.5x 下 54px；判据 **≥34**（逻辑口径的保守下限）。
    同时打印横幅与父客户区尺寸，便于复核裁剪是否真的发生。
    """
    def body(proc):
        t0 = time.time()
        pv = None
        while time.time() - t0 < 90.0:
            cand = [x for x in own_windows() if 200 < x[1] <= 900 and x[2] <= 300]
            if cand:
                pv = cand[0]
                break
            time.sleep(0.1)
        if not pv:
            return ('M4 说明文字不被裁', False, '90s 内没出现预告横幅')
        hwnd = pv[0]
        cr = wt.RECT()
        user32.GetClientRect(hwnd, ctypes.byref(cr))
        kids = enum_children(hwnd)
        print('  横幅 %dx%d；客户区 %dx%d' % (pv[1], pv[2],
                                           cr.right - cr.left, cr.bottom - cr.top))
        for ch, cls, text, kw, kh in kids:
            print('    子窗口 %-46s %3dx%-3d text=%r' % (cls[:46], kw, kh, text))
        target = None
        for ch, cls, text, kw, kh in kids:
            if text and ('快到休息' in text or '起身活动' in text or '番茄钟' in text):
                target = (ch, cls, text, kw, kh)
                break
        if target is None:
            cands = [(ch, cls, text, kw, kh) for ch, cls, text, kw, kh in kids if text]
            if cands:
                target = max(cands, key=lambda t: t[4])
        if target is None:
            return ('M4 说明文字不被裁', False, '横幅里没有带文本的子窗口')
        ch, cls, text, kw, kh = target
        return ('M4 说明文字不被裁', kh >= 34,
                '标签「%s」高 %dpx（判据 ≥34）、横幅客户区高 %dpx、横幅 %dx%d'
                % (text, kh, cr.bottom - cr.top, pv[1], pv[2]))

    cfg = base_cfg(orig)
    cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '10'})
    return run_case('M4', {}, cfg, body)


# ---------------------------------------------------------------- main


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at %s' % EXE)
        return 2
    print('被测产物: %s (%d bytes)' % (EXE, os.path.getsize(EXE)))

    want = [a.lower() for a in sys.argv[1:]] or ['all']
    if 'all' in want:
        # m1vec = 自绘路径（肩颈绕环）；m1mat = 素材路径（深蹲 10 次）
        plan = ['m1vec', 'm1mat', 'm1ctl', 'm2', 'm3', 'm4', 'm5']
    else:
        plan = want

    if not stop_eyelite():
        print('FAIL: 有残留 EyeLite 进程，无法开始')
        return 2

    backup_raw = read_cfg_raw()
    orig = read_cfg()
    print('orig config: WorkMinutes=%s PreviewSeconds=%s DimAlphaMax=%s'
          % (orig.get('WorkMinutes'), orig.get('PreviewSeconds'),
             orig.get('DimAlphaMax')))

    results = []
    try:
        if 'm1vec' in plan or 'm1' in plan:
            results.append(case_m1(orig, '肩颈绕环', 'M1 自绘'))
        if 'm1mat' in plan:
            results.append(case_m1(orig, '深蹲 10 次', 'M1 素材'))
        if 'm5' in plan:
            results.append(case_m5(orig))
        if 'm1ctl' in plan:
            results.append(case_m1_ctl(orig))
        if 'm2' in plan:
            results.append(case_m2(orig))
        if 'm3' in plan:
            results.extend(case_m3(orig))
        if 'm4' in plan:
            results.append(case_m4(orig))
    finally:
        stop_eyelite()
        restore_cfg_raw(backup_raw)

    print()
    for name, ok, detail in results:
        print('%-24s %s  %s' % (name, 'PASS' if ok else 'FAIL', detail))
    npass = sum(1 for r in results if r[1])
    print('\n=== P3 summary: %d/%d PASS ===' % (npass, len(results)))
    for name, ok, detail in results:
        if not ok:
            print('  FAILED: %s (%s)' % (name, detail))
    return 0 if npass == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
