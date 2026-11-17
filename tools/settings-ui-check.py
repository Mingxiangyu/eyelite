#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
EyeLite 设置页「数值输入框」可交互性取证仪器（Phase 1：采集修复前基线）

## 这个仪器要证明的一件事

设置页右侧有 10 个 NumericUpDown（工作 / 休息 / 预告 / 渐暗 / 番茄工作 / 番茄休息 /
久坐间隔 / 活动时长 / 推迟时长 / 离开多少分钟）。它们被包在一个圆角 RoundPanel 里，
而 `AddRow()` 用**卡片绝对坐标**去定位一个**子控件**（子控件坐标是相对父容器的），
于是 10 个输入框整体落在容器边界之外，被 RoundPanel 的 Region 裁掉 ——
表现为"一个浅浅的空白圆角矩形：看不到数字、点不进去、也无法聚焦"。

## 四条判据，以及为什么这样判

### A 数量：`EDIT` 子孙窗口个数 == 10
WinForms 的 NumericUpDown（UpDownBase）内部有一个原生 `EDIT` 子窗口，类名形如
`WindowsForms10.EDIT.app.0.xxx`。所以"数 EDIT"就是数数量框。
**注意：判据 A 在缺陷态下也是 PASS** —— 窗口对象确实存在，只是被裁掉了。
这正是下面 B/C/D 存在的理由。

### A2 可见性标记不可信（缺陷态实证，INFO）
`IsWindowVisible` 读的是 WS_VISIBLE 样式位。被父窗口 Region 裁掉的子窗口
**WS_VISIBLE 依然是 1**（实测：10 个 EDIT 全部 visible=1，同时全部越界）。
这条输出成一个独立的 INFO 行：`N 个 EDIT 中 visible=1 的有 X 个，越界的有 Y 个，交集 Z 个`。
存在交集 → "看 IsWindowVisible" 这条判据会产生假 PASS。
**为什么必须单列**：本项目已经被"探针自己骗自己"坑过四类 ——
探针比产品快、抓图抓到静态背景、抓屏抓到别人桌面，以及 **可见性标志 ≠ 真的画出来了**。

### B 越界：EDIT 是否完全落在**每一层祖先**的客户区内
"存在"和"可见"是两件事。`IsWindowVisible` 读的是 WS_VISIBLE 样式位，
被父窗口 Region 裁掉的子窗口 **WS_VISIBLE 依然是 1**（实测见输出里的 visible=1）。
所以判据 B 不看样式位，而是把 EDIT 的矩形逐层映射到每个祖先的客户区坐标，
检查是否整体落在 `[0,0,w,h]` 里；只要有任何一层越界，这块区域就画不出来、
也收不到鼠标消息（Region 同时裁剪绘制与命中测试）。

### C 可交互：点击 → 按 ↑ → 值必须 +1
这是本仪器最硬的一条，也是唯一"不看外观、只看行为"的一条：
  · 用 SendInput 在**圆角容器左侧留白处**点一下（用户会去点的地方）
  · 再用 SendInput 按一次 VK_UP
  · 重读 WM_GETTEXT，值应 +1
为什么不能只看窗口是否存在、也不能只看它的矩形：这两者都只能证明"控件对象在那儿"，
证明不了"用户的手指能落上去、键盘能进去"。点一下＋按一下是**端到端**的：
点击若落在别的控件上 → 焦点不进来 → 上下键没人处理 → 值不变。
注意：改完值**不点保存**，最后直接按 PID 杀进程，配置不会被写回。

> 关于"EDIT 左边缘往左 40px"：实测容器左侧留白只有 `Sc(6)`≈9px（150%），
> 再往左 40px 已经出了容器、落在卡片上 —— 那测的是卡片不是输入框。
> 所以本仪器取**容器左缘 + 4px**（留白区内），并把"按字面取 -40px 会落在谁身上"
> 作为诊断信息打印出来（WindowFromPoint），让这个偏差可审计、不靠嘴说。

### D 聚焦态可见：PrintWindow 前后比对容器描边
输入框自己 `BorderStyle.None`，焦点反馈只能靠容器描边换色（v1.6.1 新增
`RoundPanel.SetBorderColor`）。所以抓容器矩形那一块像素，比较聚焦前后差异。
- 只用 **PrintWindow(PW_RENDERFULLCONTENT)**，**绝不用屏幕截屏**——
  本机踩过：BitBlt 抓屏把用户桌面上的其它窗口一起拍进来，既污染证据也泄露隐私。
- 抓图走 CreateDIBSection，连 `GetDC(0)` 都不碰，物理上不可能读到别的窗口。
- 比较前先连抓两帧确认基线稳定：不稳就 SKIP，不硬给结论。

### E 「预览渐暗」按钮 禁用/可用 的像素方向
自绘按钮（RoundButton）没有系统主题兜底，Enabled 改了不等于画对了：
v1.6.1 之前用 `ControlPaint.Dark(fore, 0.45f)`，在"浅底深字"上把禁用态压得**更黑**，
方向整个反了（lead 像素取样：禁用 (28,32,38) < 可用 (74,85,104)）。
所以这条只认像素：同一张图里取该按钮文字带的最深亮度，断言 **禁用更淡**（差 > +20）。
顺带验行为：把强度值连按 ↓ 改到 0 → 按钮**立刻**变灰（ValueChanged 直连，不用保存）。
这条判法很通用，以后所有自绘按钮都能复用。

## 纪律
- 只 kill **自己启动的 PID**（`taskkill /PID`），绝不用 `/IM` —— 用户可能正在用。
- 启动前若发现已有 EyeLite 实例：直接退出并说明（单实例互斥是写死字符串
  `EyeLite_SingleInstance_v1`，与之并存必然抢不到窗口）。
- `config.ini` 按**原始字节**备份，结束原字节还原，并打印还原后的字节数。
- 零第三方依赖：ctypes + 标准库（PNG 用 zlib 手写，见 write_png）。

## 用法
    python tools/settings-ui-check.py
    python tools/settings-ui-check.py --shot C:\\Temp\\settings-before.png --crop C:\\Temp\\input-before.png
    python tools/settings-ui-check.py --settle 2.5
    # 仓库根目录的 EyeLite.exe 会被下一次构建原地覆盖；要跑"修复前"就指向留存的旧副本：
    python tools/settings-ui-check.py --exe "%LOCALAPPDATA%\\Temp\\EyeLite-prefix-c2e5e1b8.exe"
    # 旧的 v1.6.0 发行包也能解出来（两者 md5 同为 c2e5e1b890c305db744420e8b240dd28）：
    #   python -c "import zipfile;zipfile.ZipFile('dist/EyeLite-v1.6.0.zip').extract('EyeLite.exe', r'%TEMP%\\eyelite-v160')"

退出码：全部 PASS → 0；有 FAIL 或 SKIP → 非 0（SKIP 意味着"没有证据"，不能当通过）。
INFO 行（A2）不参与统计：它是方法论结论，在修复态无从证伪。
"""

import ctypes
import ctypes.wintypes as wt
import hashlib
import os
import struct
import subprocess
import sys
import time
import zlib

# ------------------------------------------------------------------ DPI 感知
# 必须在**取任何坐标之前**声明 PER_MONITOR_DPI_AWARE，否则 user32 返回的是
# 按系统 DPI 虚拟化过的坐标，和真实像素差一个缩放因子 —— 本项目的脚本已经被这个
# 坑过三次（抓图偏移、点击落空、矩形比对全错位）。只在进程启动时能设一次。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
SRC = os.path.join(ROOT, 'src', 'Program.cs')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')
DEFAULT_SHOT = os.path.join(os.environ.get('TEMP', '.'), 'settings-before.png')

EXPECT_EDITS = 10          # 10 个 NumericUpDown，见 src/Program.cs 的 10 处 AddRow
FORM = [None]              # 设置窗 hwnd，清理阶段要用（恢复 NOTOPMOST）
FOCUS_DBG = []             # clear_focus 的前几次诊断（attach/prevFocus/err）

user32 = ctypes.WinDLL('user32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', ctypes.c_long),
                ('biHeight', ctypes.c_long), ('biPlanes', wt.WORD),
                ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', wt.DWORD),
                ('biClrImportant', wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [('bmiHeader', BITMAPINFOHEADER), ('bmiColors', wt.DWORD * 3)]


# ------------------------------------------------------------------ Win32 声明
# 句柄一律 64 位：不写 restype 会被截成 32 位（本机踩过，GetModuleHandleW 那次）。
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
LRESULT_T = ctypes.c_ssize_t
WPARAM_T = ctypes.c_size_t
LPARAM_T = ctypes.c_ssize_t

user32.EnumWindows.argtypes = [WNDENUMPROC, wt.LPARAM]
user32.EnumChildWindows.argtypes = [wt.HWND, WNDENUMPROC, wt.LPARAM]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.IsWindowEnabled.argtypes = [wt.HWND]
user32.IsWindowEnabled.restype = wt.BOOL
user32.IsWindow.argtypes = [wt.HWND]
user32.IsWindow.restype = wt.BOOL
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindowVisible.restype = wt.BOOL
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetParent.argtypes = [wt.HWND]
user32.GetParent.restype = wt.HWND
user32.MapWindowPoints.argtypes = [wt.HWND, wt.HWND, ctypes.POINTER(wt.POINT), wt.UINT]
user32.MapWindowPoints.restype = ctypes.c_int
user32.WindowFromPoint.argtypes = [wt.POINT]
user32.WindowFromPoint.restype = wt.HWND
user32.ClientToScreen.argtypes = [wt.HWND, ctypes.POINTER(wt.POINT)]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.GetWindowThreadProcessId.restype = wt.DWORD
user32.GetForegroundWindow.restype = wt.HWND
user32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
user32.SetForegroundWindow.argtypes = [wt.HWND]
user32.SetFocus.argtypes = [wt.HWND]
user32.SetFocus.restype = wt.HWND
user32.PeekMessageW.argtypes = [ctypes.POINTER(wt.MSG), wt.HWND, wt.UINT, wt.UINT, wt.UINT]
user32.SendMessageW.argtypes = [wt.HWND, wt.UINT, WPARAM_T, LPARAM_T]
user32.SendMessageW.restype = LRESULT_T
user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
user32.PrintWindow.restype = wt.BOOL
user32.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
                                ctypes.c_int, ctypes.c_int, wt.UINT]
user32.AttachThreadInput.argtypes = [wt.DWORD, wt.DWORD, wt.BOOL]
user32.BringWindowToTop.argtypes = [wt.HWND]
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
kernel32.GetCurrentThreadId.restype = wt.DWORD

gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
gdi32.CreateCompatibleDC.restype = wt.HDC
gdi32.CreateDIBSection.argtypes = [wt.HDC, ctypes.POINTER(BITMAPINFO),
                                   wt.UINT, ctypes.POINTER(ctypes.c_void_p),
                                   wt.HANDLE, wt.DWORD]
gdi32.CreateDIBSection.restype = wt.HBITMAP
gdi32.SelectObject.argtypes = [wt.HDC, wt.HANDLE]
gdi32.SelectObject.restype = wt.HANDLE
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteDC.argtypes = [wt.HDC]


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.DWORD), ('flags', wt.DWORD),
                ('hwndActive', wt.HWND), ('hwndFocus', wt.HWND),
                ('hwndCapture', wt.HWND), ('hwndMenuOwner', wt.HWND),
                ('hwndMoveSize', wt.HWND), ('hwndCaret', wt.HWND),
                ('rcCaret', wt.RECT)]


class SCROLLINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('fMask', wt.UINT),
                ('nMin', ctypes.c_int), ('nMax', ctypes.c_int),
                ('nPage', wt.UINT), ('nPos', ctypes.c_int),
                ('nTrackPos', ctypes.c_int)]


user32.GetScrollInfo.argtypes = [wt.HWND, ctypes.c_int, ctypes.POINTER(SCROLLINFO)]
user32.GetScrollInfo.restype = wt.BOOL
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowLongW.restype = ctypes.c_long
GWL_STYLE = -16
WS_VSCROLL = 0x00200000
SB_VERT = 1
SIF_ALL = 0x07 | 0x10 | 0x01 | 0x02   # RANGE|PAGE|POS|TRACKPOS


user32.GetGUIThreadInfo.argtypes = [wt.DWORD, ctypes.POINTER(GUITHREADINFO)]
user32.GetGUIThreadInfo.restype = wt.BOOL

# ---- SendInput（鼠标/键盘注入）----
ULONG_PTR = ctypes.c_size_t


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [('dx', wt.LONG), ('dy', wt.LONG), ('mouseData', wt.DWORD),
                ('dwFlags', wt.DWORD), ('time', wt.DWORD), ('dwExtraInfo', ULONG_PTR)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [('wVk', wt.WORD), ('wScan', wt.WORD), ('dwFlags', wt.DWORD),
                ('time', wt.DWORD), ('dwExtraInfo', ULONG_PTR)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [('uMsg', wt.DWORD), ('wParamL', wt.WORD), ('wParamH', wt.WORD)]


class _INPUTUNION(ctypes.Union):
    _fields_ = [('mi', MOUSEINPUT), ('ki', KEYBDINPUT), ('hi', HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ('u',)
    _fields_ = [('type', wt.DWORD), ('u', _INPUTUNION)]


user32.SendInput.argtypes = [wt.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = wt.UINT

INPUT_MOUSE = 0
INPUT_KEYBOARD = 1
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
KEYEVENTF_KEYUP = 0x0002

WM_GETTEXT = 0x000D
VK_UP = 0x26
VK_DOWN = 0x28
SW_RESTORE = 9


# ------------------------------------------------------------------ 结果记录
class Report(object):
    def __init__(self):
        self.rows = []

    def add(self, status, key, msg):
        """status: PASS / FAIL / SKIP / INFO
        INFO 是"方法论观察"，不是产品属性：只打印、不计入 PASS/FAIL/SKIP 统计
        （例：A2 的"可见性标记不可信"在缺陷态能实证、在修复态根本无从证伪，
        把它算成 FAIL 会让修复态永远非零退出，那等于自己给自己挖坑）。"""
        self.rows.append((status, key, msg))
        print('  [%s] %-6s %s' % (status, key, msg))
        return status == 'PASS'

    def summary(self):
        cnt = [r for r in self.rows if r[0] in ('PASS', 'FAIL', 'SKIP')]
        n = len(cnt)
        p = len([r for r in cnt if r[0] == 'PASS'])
        f = len([r for r in cnt if r[0] == 'FAIL'])
        s = len([r for r in cnt if r[0] == 'SKIP'])
        info = len(self.rows) - n
        print('\n  PASS %d / FAIL %d / SKIP %d（共 %d 项，另有 %d 条 INFO）'
              % (p, f, s, n, info))
        print('=== summary: %d/%d PASS ===' % (p, n))
        return 0 if p == n else 1


REP = Report()


# ------------------------------------------------------------------ 窗口工具
def class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def window_text(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def short_class(cls):
    """WindowsForms10.EDIT.app.0.xxx -> EDIT；其它原样返回。"""
    if cls.startswith('WindowsForms10.'):
        rest = cls[len('WindowsForms10.'):]
        return rest.split('.')[0]
    return cls


def window_rect(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def client_size(hwnd):
    r = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(r))
    return (r.right - r.left, r.bottom - r.top)


def descendants(hwnd):
    """EnumChildWindows 本身是**递归**的（含孙子、曾孙），不需要自己写递归。
    返回顺序为系统枚举顺序（Z 序），不保证等于代码里的创建顺序。"""
    out = []

    def cb(ch, lparam):
        out.append(ch)
        return True

    user32.EnumChildWindows(hwnd, WNDENUMPROC(cb), 0)
    return out


def ancestors(hwnd):
    """父链：[直接父, 祖父, ..., 顶层]。"""
    out = []
    h = user32.GetParent(hwnd)
    while h:
        out.append(h)
        h = user32.GetParent(h)
        if len(out) > 32:
            break
    return out


def rect_in_client(hwnd, ancestor):
    """把 hwnd 的窗口矩形（屏幕坐标）映射成 ancestor 的**客户区**坐标。"""
    l, t, r, b = window_rect(hwnd)
    pts = (wt.POINT * 2)()
    pts[0].x, pts[0].y = l, t
    pts[1].x, pts[1].y = r, b
    user32.MapWindowPoints(None, ancestor, pts, 2)   # hwndFrom=NULL → 入参是屏幕坐标
    return (pts[0].x, pts[0].y, pts[1].x, pts[1].y)


def client_origin_in_window(hwnd):
    """客户区原点在窗口矩形里的偏移（PrintWindow 画的是整个窗口，含非客户区）。"""
    p = wt.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(p))
    l, t, _r, _b = window_rect(hwnd)
    return (p.x - l, p.y - t)


def hwnd_in_subtree(hwnd, root):
    h = hwnd
    for _ in range(32):
        if not h:
            return False
        if h == root:
            return True
        h = user32.GetParent(h)
    return False


def focus_of(hwnd):
    """读 hwnd 所属 GUI 线程的焦点窗口（跨进程可用 GetGUIThreadInfo）。"""
    tid = user32.GetWindowThreadProcessId(hwnd, None)
    gti = GUITHREADINFO()
    gti.cbSize = ctypes.sizeof(GUITHREADINFO)
    if not user32.GetGUIThreadInfo(tid, ctypes.byref(gti)):
        return None
    return gti.hwndFocus or None


def edit_text(hwnd):
    """WM_GETTEXT 是系统会跨进程代劳封送的消息之一，可以直接发给别的进程的窗口。"""
    buf = ctypes.create_unicode_buffer(64)
    user32.SendMessageW(hwnd, WM_GETTEXT, 64, ctypes.cast(buf, ctypes.c_void_p).value)
    return buf.value


def clear_focus(form, panel=None):
    """把焦点从任何输入框上摘掉，钉到一个**Panel** 上（默认退回窗体）。

    **为什么必须有这一步**（真机踩到的假阳性）：设置页打开时，第一个 NumericUpDown
    会拿到初始焦点。于是"点一下容器留白 + 按 ↑"这条判据在缺陷态下也可能误判成 PASS
    —— 点击什么都没做，但 ↑ 打到了那个**本来就有焦点**的框上，值真的 +1 了。
    第一次跑就是这样：B-01..B-10 全 FAIL，C1-01 却 PASS（20→21），其余 9 行 FAIL。

    **为什么不能直接 SetFocus(窗体)**（第二次真机跑踩到的）：SetFocus 是成功了，
    但 WinForms 的 Form 收到 WM_SETFOCUS 后会立刻把焦点还给 ActiveControl
    （也就是第一个 NumericUpDown）—— 实测日志里就是"清焦点失败，仍在 EDIT 上"。
    所以这里钉到一个普通 Panel（RoundPanel 容器）：它不是可选控件、不参与 Tab、
    收到 WM_SETFOCUS 也不会转发，能把焦点稳稳停住。

    跨进程 SetFocus 要求输入队列已附加（AttachThreadInput），且调用线程必须已有
    消息队列（用 PeekMessage 强制创建），否则一律失败。
    """
    msg = wt.MSG()
    user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 0)
    tid = user32.GetWindowThreadProcessId(form, None)
    tid_me = kernel32.GetCurrentThreadId()
    attached = False
    prev = None
    err = 0
    try:
        attached = bool(user32.AttachThreadInput(tid_me, tid, True))
        prev = user32.SetFocus(panel if panel else form)
        err = ctypes.get_last_error()
    except Exception as e:
        err = -1
        FOCUS_DBG.append('exception %s' % e)
    finally:
        if attached:
            user32.AttachThreadInput(tid_me, tid, False)
    time.sleep(0.15)
    if len(FOCUS_DBG) < 4:
        FOCUS_DBG.append('attach=%s prevFocus=0x%X err=%d'
                         % (attached, prev or 0, err))
    return focus_of(form)


# ------------------------------------------------------------------ 抓图 / PNG
def capture_window(hwnd):
    """只用 PrintWindow(PW_RENDERFULLCONTENT=2) 抓目标窗口自己画的像素。

    **绝不截屏**：BitBlt/GetDC(0) 读到的是屏幕合成结果，会把用户桌面上别的窗口
    一起拍进来（本项目踩过）。这里连 GetDC(0) 都不用 —— CreateDIBSection 直接给
    一块自己的位图内存，物理上不可能读到别人的像素。
    返回 (ok, w, h, buf)，buf 是自上而下的 BGRA。
    """
    l, t, r, b = window_rect(hwnd)
    w, h = r - l, b - t
    if w <= 0 or h <= 0:
        return False, 0, 0, None
    memdc = gdi32.CreateCompatibleDC(None)
    if not memdc:
        return False, 0, 0, None
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h          # 负数 = 自上而下，省一次翻转
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0
    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(memdc, ctypes.byref(bi), 0, ctypes.byref(bits), None, 0)
    if not hbmp:
        gdi32.DeleteDC(memdc)
        return False, 0, 0, None
    gdi32.SelectObject(memdc, hbmp)
    ok = bool(user32.PrintWindow(hwnd, memdc, 2))
    buf = ctypes.string_at(bits, w * h * 4) if bits else None
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(memdc)
    return ok, w, h, buf


def crop(buf, w, x0, y0, cw, ch):
    out = bytearray()
    for y in range(y0, y0 + ch):
        base = (y * w + x0) * 4
        out += buf[base:base + cw * 4]
    return bytes(out)


def rect_in_image(hwnd, form, img_w, img_h, expand=0):
    """窗口矩形 → 整窗抓图的图像坐标（可外扩，用于"带上下文"的裁切）。"""
    l, t, r, b = window_rect(hwnd)
    fl, ft, _fr, _fb = window_rect(form)
    x0 = max(0, l - fl - expand)
    y0 = max(0, t - ft - expand)
    x1 = min(img_w, r - fl + expand)
    y1 = min(img_h, b - ft + expand)
    return x0, y0, max(x0 + 1, x1), max(y0 + 1, y1)


def darkest(buf, w, x0, y0, x1, y1):
    """区域内最深的像素亮度（0~255）。自绘按钮的文字色只能这样量：
    文字比底色/描边都深，所以"最深像素"就是文字带的代表值。"""
    mn = 999
    for y in range(y0, y1):
        base = (y * w + x0) * 4
        for x in range(x1 - x0):
            i = base + x * 4
            v = (buf[i] + buf[i + 1] + buf[i + 2]) // 3
            if v < mn:
                mn = v
    return mn


def diff_count(a, b, thr=6):
    """返回 (差异像素数, 最大单通道差)。thr 用来滤掉抗锯齿噪声。"""
    n = 0
    mx = 0
    for i in range(0, min(len(a), len(b)), 4):
        d = abs(a[i] - b[i])
        d2 = abs(a[i + 1] - b[i + 1])
        if d2 > d:
            d = d2
        d3 = abs(a[i + 2] - b[i + 2])
        if d3 > d:
            d = d3
        if d > mx:
            mx = d
        if d > thr:
            n += 1
    return n, mx


def write_png(path, w, h, bgra):
    """BGRA(自上而下) → RGB PNG。不依赖 PIL：本项目一直零第三方依赖。
    设置窗是不透明窗口，alpha 实测恒为 255，所以不走合成，直接取 RGB 三个通道。"""
    raw = bytearray()
    for y in range(h):
        row = bgra[y * w * 4:(y + 1) * w * 4]
        dst = bytearray(w * 3)
        dst[0::3] = row[2::4]
        dst[1::3] = row[1::4]
        dst[2::3] = row[0::4]
        raw.append(0)                    # 每行前缀 filter type 0
        raw += dst

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    out = b'\x89PNG\r\n\x1a\n'
    out += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    out += chunk(b'IDAT', zlib.compress(bytes(raw), 6))
    out += chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(out)


# ------------------------------------------------------------------ 注入输入
def send_input(inp):
    n = user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
    return n == 1


def click_at(x, y):
    """SetCursorPos 定位 + SendInput 注入左键按下/抬起。
    为什么不用 MOUSEEVENTF_ABSOLUTE 的 0..65535 归一化坐标：在 150% 缩放的多屏
    桌面上归一化取整会差 1px，而圆角容器左侧留白只有 9px —— 宁可少一层换算。"""
    user32.SetCursorPos(x, y)
    time.sleep(0.06)
    i = INPUT()
    i.type = INPUT_MOUSE
    i.mi.dwFlags = MOUSEEVENTF_LEFTDOWN
    ok1 = send_input(i)
    time.sleep(0.04)
    i = INPUT()
    i.type = INPUT_MOUSE
    i.mi.dwFlags = MOUSEEVENTF_LEFTUP
    ok2 = send_input(i)
    return ok1 and ok2


def key_tap(vk):
    i = INPUT()
    i.type = INPUT_KEYBOARD
    i.ki.wVk = vk
    ok1 = send_input(i)
    time.sleep(0.04)
    i = INPUT()
    i.type = INPUT_KEYBOARD
    i.ki.wVk = vk
    i.ki.dwFlags = KEYEVENTF_KEYUP
    ok2 = send_input(i)
    return ok1 and ok2


def point_owner_class(x, y):
    """这个屏幕坐标上**实际**是哪个窗口 —— 用来证明"用户的手指落在谁身上"。"""
    h = user32.WindowFromPoint(wt.POINT(int(x), int(y)))
    if not h:
        return None, '(无窗口)'
    return h, '%s/%s' % (short_class(class_name(h)), (window_text(h) or '')[:14])


# ---------------------------------------------------------------- 置前
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOACTIVATE = 0x0010
SWP_SHOWWINDOW = 0x0040
HWND_TOPMOST = -1
HWND_NOTOPMOST = -2


def bring_to_front(hwnd):
    """把设置窗弄到最上层，否则注入的点击会打到别人窗口上。

    为什么不能只靠 SetForegroundWindow：Windows 有前台锁 —— 只有当调用进程
    已经是前台、或刚收到过用户输入时才允许抢前台。本脚本是后台进程，第一次实测
    就被拒了（设置窗开在微信下面）。两条一起用：
      1. SetWindowPos(HWND_TOPMOST)：不改变前台，只改变 Z 序。**这一步就够点击用了**
         —— WindowFromPoint 只看 Z 序，真实点击也会顺带把窗口激活。
      2. AttachThreadInput 到当前前台线程再 SetForegroundWindow：让键盘输入
         （↑ 键）有确定的前台归属，而不是赌"点击会激活"。
    用完在清理阶段恢复 NOTOPMOST（进程随后被杀，纯粹为了不做多余改变）。
    """
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
    fg = user32.GetForegroundWindow()
    if fg and fg != hwnd:
        tid_fg = user32.GetWindowThreadProcessId(fg, None)
        tid_me = kernel32.GetCurrentThreadId()
        attached = False
        try:
            attached = bool(user32.AttachThreadInput(tid_me, tid_fg, True))
            user32.SetForegroundWindow(hwnd)
            user32.BringWindowToTop(hwnd)
        finally:
            if attached:
                user32.AttachThreadInput(tid_me, tid_fg, False)
    else:
        user32.SetForegroundWindow(hwnd)
    time.sleep(0.35)


# ------------------------------------------------------------------ 进程 / 配置
def pids_of(name):
    out = []
    try:
        r = subprocess.run(['tasklist', '/fi', 'imagename eq ' + name, '/fo', 'csv', '/nh'],
                           capture_output=True)
        for line in r.stdout.decode('utf-8', 'ignore').splitlines():
            if name not in line:
                continue
            parts = [p.strip('" ') for p in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                out.append(int(parts[1]))
    except Exception:
        pass
    return out


def read_cfg_raw():
    try:
        with open(CFG, 'rb') as f:
            return f.read()
    except Exception:
        return b''


def write_cfg_raw(raw):
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    with open(CFG, 'wb') as f:
        f.write(raw)


def kill_pid(pid):
    """**只按 PID 杀**。用 /IM 会连带杀掉用户自己开的实例（用户可能正在用）。"""
    subprocess.run(['taskkill', '/PID', str(pid), '/F'], capture_output=True)
    t0 = time.time()
    while time.time() - t0 < 8:
        if pid not in pids_of('EyeLite.exe'):
            return True
        time.sleep(0.2)
    return False


def launch():
    env = os.environ.copy()
    for k in list(env.keys()):
        if k.startswith('EYE_LITE_'):        # 别把上一轮的一次性钩子继承进来
            env.pop(k, None)
    env['EYE_LITE_OPEN_SETTINGS'] = '1'     # 唯一的自动化入口：启动即开设置页
    p = subprocess.Popen([EXE], cwd=ROOT, env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return p


def wait_settings_window(pid, timeout=40.0):
    """按 PID + 标题认设置窗。标题在 src/Program.cs 里是「护眼小助手 - 设置」。
    返回 (hwnd 或 None, 最后一次看到的窗口快照) —— 快照用于没认到窗口时诊断。"""
    t0 = time.time()
    seen = []
    while time.time() - t0 < timeout:
        seen = []
        for hwnd in top_windows():
            l, t, r, b = window_rect(hwnd)
            p = wt.DWORD(0)
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
            seen.append(('pid%d' % p.value, short_class(class_name(hwnd)),
                         window_text(hwnd), r - l, b - t))
            if p.value != pid:
                continue
            if '设置' in window_text(hwnd) and (r - l) > 600:
                return hwnd, seen
        time.sleep(0.2)
    return None, seen


def top_windows():
    out = []

    def cb(hwnd, lparam):
        if user32.IsWindowVisible(hwnd):
            out.append(hwnd)
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return out


# ------------------------------------------------------------------ 行标签
def row_label(edit):
    """给一个 EDIT 找它的行文字标签。

    结构是 form > card(RoundPanel) > box(RoundPanel) > NumericUpDown > EDIT，
    标签是 card 的另一个子窗口（Label）。只能按"同一行的纵向位置最接近"来配 ——
    控件树里标签和输入框没有父子关系，没有更硬的关联可用。
    """
    anc = ancestors(edit)
    if len(anc) < 3:
        return '?'
    box, card = anc[1], anc[2]
    bx0, by0, bx1, by1 = rect_in_client(box, card)
    best = None
    for ch in descendants(card):
        if 'EDIT' in class_name(ch).upper():
            continue
        txt = window_text(ch)
        if not txt:
            continue
        l, t, r, b = rect_in_client(ch, card)
        dy = abs((t + b) / 2.0 - (by0 + by1) / 2.0)
        if dy > 40:                      # 150% 下 40px 约等于一行内的容差
            continue
        if best is None or dy < best[0]:
            best = (dy, txt)
    return best[1] if best else '?'


# ------------------------------------------------------------------ 主体
def main():
    global EXE
    shot_path = DEFAULT_SHOT
    crop_path = None
    settle = 2.0
    argv = sys.argv[1:]
    i = 0
    while i < len(argv):
        if argv[i] == '--shot' and i + 1 < len(argv):
            shot_path = argv[i + 1]
            i += 2
        elif argv[i] == '--crop' and i + 1 < len(argv):
            # 整页缩略图看不出 9px 的间隙和 1px 的描边，所以另外存一张
            # "第一个输入框 + 四周各 20px 上下文"的裁切图，供像素级对照。
            crop_path = argv[i + 1]
            i += 2
        elif argv[i] == '--settle' and i + 1 < len(argv):
            settle = float(argv[i + 1])
            i += 2
        elif argv[i] == '--exe' and i + 1 < len(argv):
            # 为什么需要这个开关：仓库根目录的 EyeLite.exe 会被下一次构建**原地覆盖**，
            # 想复现"修复前"只能拿 dist/ 里的旧发行包解出来跑。单实例互斥是写死的
            # 字符串（EyeLite_SingleInstance_v1），与文件名/路径无关，所以跑旧副本
            # 与跑根目录那份在行为上等价 —— 但报告里必须写清楚测的是哪一个文件。
            EXE = os.path.abspath(argv[i + 1])
            i += 2
        else:
            print('unknown arg: %s' % argv[i])
            return 2

    print('=== EyeLite 设置页数值输入框 取证仪器 ===')
    if not os.path.isfile(EXE):
        print('找不到被测程序: %s' % EXE)
        return 2
    exe_st = os.stat(EXE)
    # 产物指纹只认 md5 / size，不认 mtime：mtime 精度到秒，两次编译可能同秒，
    # 而"同一份报告对不上产物"是最难查的那类扯皮。SE 也会拿这两个数交叉核对。
    exe_md5 = hashlib.md5(open(EXE, 'rb').read()).hexdigest()
    print('被测程序: %s' % EXE)
    print('          size=%d  md5=%s  mtime=%s'
          % (exe_st.st_size, exe_md5,
             time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(exe_st.st_mtime))))
    if os.path.abspath(EXE) != os.path.abspath(os.path.join(ROOT, 'EyeLite.exe')):
        print('          （--exe 指定：不是仓库根目录那份，注意核对 md5 是否等于你要测的版本）')
    if os.path.isfile(SRC):
        src_st = os.stat(SRC)
        src_md5 = hashlib.md5(open(SRC, 'rb').read()).hexdigest()
        print('源码    : src/Program.cs  size=%d  md5=%s  mtime=%s'
              % (src_st.st_size, src_md5,
                 time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(src_st.st_mtime))))
        if src_st.st_mtime > exe_st.st_mtime:
            # 分情况写清楚，否则采基线的人会被自己的警告骗一次：
            # 基线的定义就是"源码已修、产物未修"，这时产物早于源码是**预期**。
            print('          ⚠ 产物早于源码 —— 若目标是**采基线**，这正是预期状态'
                  '（源码已修、产物未修），请以 exe md5 为准；'
                  '若要**验证修复**，则说明这个产物不含最新源码，结论不成立。')

    # ---- 前置：不许有别人的实例 ----
    running = pids_of('EyeLite.exe')
    if running:
        print('检测到已运行的 EyeLite 实例 %s：单实例互斥（EyeLite_SingleInstance_v1）'
              '会让我启动的进程立刻退出。' % running)
        print('请先自行退出该实例后重跑。本脚本不会替你杀它（用户可能正在用）。')
        return 3

    # ---- 配置原字节备份 ----
    raw_cfg = read_cfg_raw()
    raw_sha = hashlib.sha1(raw_cfg).hexdigest()[:12] if raw_cfg else '(空/不存在)'
    print('配置    : %s  备份 %d 字节  sha1:%s' % (CFG, len(raw_cfg), raw_sha))

    cursor0 = wt.POINT()
    user32.GetCursorPos(ctypes.byref(cursor0))

    proc = None
    try:
        proc = launch()
        print('启动    : PID %d（EYE_LITE_OPEN_SETTINGS=1）' % proc.pid)
        # 认窗要能容忍"句柄被重建"：WinForms 在构造/Show 期间会重建原生窗口句柄
        # （改 ExStyle/ShowInTaskbar 等都会），我这一跑就抓到过一个 0x0 的失效句柄
        # —— 标题空、尺寸 0，后面所有判据都会变成假失败。所以：认 → 校验 → 失效就重认。
        form = None
        for attempt in range(4):
            form, seen = wait_settings_window(proc.pid)
            if not form:
                print('没等到设置窗（标题含「设置」且宽 > 600）。')
                print('最后一次看到的可见顶层窗口：')
                for s in seen[:12]:
                    print('   %s [%s] "%s" %dx%d' % s)
                print('可能原因：EYE_LITE_OPEN_SETTINGS 钩子没编进这份产物 / 启动被单实例挡掉。')
                REP.add('FAIL', 'W0', '设置窗未出现 → 后续判据全部无法执行')
                return REP.summary()
            user32.ShowWindow(form, SW_RESTORE)
            FORM[0] = form
            bring_to_front(form)
            time.sleep(settle)            # 等控件首帧绘制完（设置页需要）
            fl, ft, fr, fb = window_rect(form)
            if (user32.IsWindow(form) and '设置' in window_text(form)
                    and (fr - fl) > 600 and (fb - ft) > 600):
                break
            print('          ⚠ 句柄 0x%X 已失效或被重建（%dx%d），重新认一次'
                  % (form, fr - fl, fb - ft))
            form = None
            time.sleep(0.8)
        if not form:
            REP.add('FAIL', 'W0', '设置窗句柄反复失效，无法定位窗口')
            return REP.summary()

        fl, ft, fr, fb = window_rect(form)
        cw, ch = client_size(form)
        print('窗口    : "%s" hwnd=0x%X  %dx%d  客户区 %dx%d  dpi感知=PER_MONITOR'
              % (window_text(form), form, fr - fl, fb - ft, cw, ch))

        # ---------- 前置：确认设置窗真的在屏幕最上层 ----------
        # 为什么必须先查：SendInput 的点击不看"我以为在哪儿"，只看光标底下**实际**是谁。
        # 若设置窗被别的窗口盖住，点击会落到别人身上，C 会变成一堆"点不到"的假 FAIL，
        # 或者更糟 —— 点进用户其它程序里。所以先探一次，不是我们的就再抢一次前台，
        # 还不行就明确中止（宁可没结论，也不制造假证据）。
        # 第一次真机跑就在这里被拒过：设置窗开在微信窗口下面，SetForegroundWindow 被
        # 前台锁挡掉 —— 所以 bring_to_front 里补了 TOPMOST + AttachThreadInput。
        mx = fl + (fr - fl) // 2
        my = ft + (fb - ft) // 2
        top_h, top_cls = point_owner_class(mx, my)
        for attempt in range(3):
            if hwnd_in_subtree(top_h, form):
                break
            print('          ⚠ 设置窗不在最上层（该点上是 %s），第 %d 次强制置前'
                  % (top_cls, attempt + 1))
            bring_to_front(form)
            time.sleep(0.6)
            top_h, top_cls = point_owner_class(mx, my)
        if not hwnd_in_subtree(top_h, form):
            print('          ✗ 仍被 %s 盖住：注入点击会落到别人的窗口里，中止' % top_cls)
            print('          （这个窗口多半是"总在最前"的，脚本不会去动别人的窗口）')
            REP.add('FAIL', 'W1', '设置窗不在最上层（%s），拒绝注入点击' % top_cls)
            return REP.summary()
        print('前台确认: 窗口中心点命中 %s ∈ 设置窗' % top_cls)

        # ---------- 内容底边 / 是否要滚动（INFO，可量化）----------
        # 为什么量这个：设置窗是 AutoScroll 的，内容比客户区高的时候「保存 / 取消 / 关于」
        # 会被滚出可视区 —— 这是"用户能不能点到保存"，且随 DPI 变化（4K@150% 只缺几十像素，
        # 1080p@150% 缺上千像素），不能靠"看起来放得下"判断。
        # 不用 GetScrollInfo：没有滚动条时它直接失败，量不到内容高。改成取**所有子控件
        # 最下沿**（客户区坐标）—— 两种状态都能量，也不依赖滚动条是否存在。
        style = user32.GetWindowLongW(form, GWL_STYLE)
        has_vs = bool(style & WS_VSCROLL)
        max_b = -10 ** 9
        which = '?'
        for k in descendants(form):
            rr = rect_in_client(k, form)
            if rr[3] > max_b:
                max_b = rr[3]
                which = (window_text(k) or short_class(class_name(k)))[:14]
        gapv = max_b - ch
        REP.add('INFO', 'S',
                'WS_VSCROLL=%d；最底部子控件底边 %d（%s）vs 可视客户区 %d → %s'
                % (1 if has_vs else 0, max_b, which, ch,
                   ('需滚动 %d px（「保存」会被滚出可视区）' % gapv) if gapv > 0
                   else ('放得下，余量 %d px' % (-gapv))))

        # ---------- 盘点子孙窗口 ----------
        kids = descendants(form)
        by_class = {}
        for k in kids:
            c = short_class(class_name(k))
            by_class[c] = by_class.get(c, 0) + 1
        edits = [k for k in kids if 'EDIT' in class_name(k).upper()]
        top = sorted(by_class.items(), key=lambda kv: -kv[1])[:6]
        print('子孙窗口: %d 个（%s）' % (len(kids), '，'.join('%s x%d' % kv for kv in top)))

        # ================= 判据 A：输入框个数 =================
        # 这一条在缺陷态下**也会 PASS**：窗口对象一直存在，被裁掉的是它的可见区域。
        # 记下来是为了排除"控件根本没被创建"这个更严重的可能。
        if len(edits) == EXPECT_EDITS:
            REP.add('PASS', 'A', 'EDIT 子孙窗口 %d 个 / 期望 %d（10 个 NumericUpDown 都在）'
                    % (len(edits), EXPECT_EDITS))
        else:
            REP.add('FAIL', 'A', 'EDIT 子孙窗口 %d 个 / 期望 %d → 控件数量已不对，'
                                '下面按实际 %d 个继续' % (len(edits), EXPECT_EDITS, len(edits)))

        # 每行信息：EDIT 自身 + 逐层祖先的越界情况
        rows = []
        for idx, ed in enumerate(edits, 1):
            anc = ancestors(ed)
            chain = []
            clipped = None
            for a in anc:
                rr = rect_in_client(ed, a)
                aw, ah = client_size(a)
                inside = (rr[0] >= 0 and rr[1] >= 0 and rr[2] <= aw and rr[3] <= ah)
                chain.append((a, short_class(class_name(a)), rr, (aw, ah), inside))
                if not inside and clipped is None:
                    clipped = chain[-1]
            rows.append({'idx': idx, 'hwnd': ed, 'chain': chain, 'clipped': clipped,
                         'label': row_label(ed),
                         'visible': bool(user32.IsWindowVisible(ed))})
        # EnumChildWindows 是按 Z 序深度优先枚举的，顺序既不等于创建顺序、也不等于
        # 屏幕上的行序。按屏幕左上坐标重排一遍，让 "B-01" 稳定对应页面第一行
        # （否则每次跑 idx 和标签的对应关系都可能变，报告没法对照）。
        rows.sort(key=lambda r: (window_rect(r['hwnd'])[1], window_rect(r['hwnd'])[0]))
        for i2, r in enumerate(rows, 1):
            r['idx'] = i2

        edit_set = set(r['hwnd'] for r in rows)

        def foc_desc(foc, ed):
            """把焦点窗口翻译成人话：无/本行EDIT/别的EDIT/窗体/类名。"""
            if not foc:
                return '无'
            if foc == ed:
                return '本行EDIT'
            if foc in edit_set:
                return '别的EDIT(!)'
            if foc == form:
                return '窗体'
            return short_class(class_name(foc)) or '?'

        # ================= 判据 A2：可见性标记不可信（缺陷态实证） =================
        # 这条不是给产品打分的，是给"后来复现的人"看的。
        # 本项目已经被四种"探针自己骗自己"坑过：探针比产品快 / 抓图抓到静态背景 /
        # 抓屏抓到别人的桌面 / **可见性标志 ≠ 真的画出来了**（就是这一条）。
        # 判定规则：只要存在"IsWindowVisible=1 且 逐层祖先客户区越界"的 EDIT，
        # 就说明"看 IsWindowVisible"那条判据会产生假 PASS。
        vis = [r for r in rows if r['visible']]
        clipped = [r for r in rows if r['clipped'] is not None]
        both = [r for r in rows if r['visible'] and r['clipped'] is not None]
        msg_a2 = ('%d 个 EDIT 中 IsWindowVisible=1 的有 %d 个，而祖先客户区越界的有 %d 个'
                  % (len(rows), len(vis), len(clipped)))
        if both:
            REP.add('INFO', 'A2',
                    msg_a2 + ' → **交集 %d 个**：可见性标记不可信，'
                    '只能靠 B(几何越界)+C(行为) 定案' % len(both))
        else:
            REP.add('INFO', 'A2',
                    msg_a2 + ' → 交集 %d 个：这一态下无从证伪（越界集为空），'
                    '该结论只在缺陷态可实证' % len(both))

        # ---------- 相邻输入框的垂直间隙（INFO）----------
        # 为什么量这个：容器高度曾经等于行距（32/32），相邻两行的圆角边框**完全贴合**，
        # 一组四个框看起来像"连成一根柱子"—— 这是外观缺陷，B/C/D 都抓不到（框本身是可见可点的）。
        # 只在同一张卡内比较（跨卡有大跳距，不能算间隙）。
        gaps = []
        by_card = {}
        for r in rows:
            bx = ancestors(r['hwnd'])[1]
            card = ancestors(bx)[1] if len(ancestors(bx)) > 1 else form
            by_card.setdefault(card, []).append(bx)
        for _card, boxes in by_card.items():
            boxes = sorted(boxes, key=lambda b: window_rect(b)[1])
            for i2 in range(1, len(boxes)):
                t0, b0 = window_rect(boxes[i2 - 1])[1], window_rect(boxes[i2 - 1])[3]
                t1 = window_rect(boxes[i2])[1]
                gaps.append((t1 - t0 - (b0 - t0), t1 - t0, b0 - t0))
        if gaps:
            mn_gap = min(g[0] for g in gaps)
            pitch, bh0 = gaps[0][1], gaps[0][2]
            REP.add('INFO', 'G',
                    '同一张卡内相邻输入框：行距 %d，容器高 %d → 最小垂直间隙 %d px（物理）%s'
                    % (pitch, bh0, mn_gap,
                       '；**0 = 边框贴死、四个框连成柱**' if mn_gap <= 0
                       else '（%d px 逻辑）' % round(mn_gap / 1.5)))
        else:
            REP.add('INFO', 'G', '同一张卡内不足两个输入框，量不到相邻间隙')

        # ================= 判据 B：是否被父容器裁掉 =================
        # 关键：越界数字必须报**在裁剪它的那一层祖先的坐标系里**。
        # 如果只报"在直接父窗口(NumericUpDown)坐标系里的矩形"，永远是 [0,0]..[w,h]，
        # 看起来像"没问题" —— 而真正的越界是被上一层的圆角容器 Region 裁掉的。
        print('\n  -- 判据 B：EDIT 是否完全落在每一层祖先的客户区内 --')
        LAYER = {1: 'NumericUpDown 自身', 2: '圆角容器 box(RoundPanel)',
                 3: '卡片(RoundPanel)', 4: '设置窗体'}
        for r in rows:
            ed = r['hwnd']
            if r['clipped'] is None:
                rr = rect_in_client(ed, user32.GetParent(ed)) if user32.GetParent(ed) else (0, 0, 0, 0)
                REP.add('PASS', 'B-%02d' % r['idx'],
                        '%s: 未被裁剪（父坐标系 [%d,%d]..[%d,%d] visible=%d）'
                        % (r['label'], rr[0], rr[1], rr[2], rr[3], r['visible']))
            else:
                _a, cls, rr, (aw, ah), _in = r['clipped']
                depth = [c[0] for c in r['chain']].index(_a) + 1
                REP.add('FAIL', 'B-%02d' % r['idx'],
                        '%s: 被第 %d 层 %s 的客户区(%dx%d)裁掉 —— EDIT 在该层坐标系里是 '
                        '[%d,%d]..[%d,%d]（宽 %d 全部落在容器外），visible=%d '
                        '（样式位是 1，所以"可见性标记"不能当判据）'
                        % (r['label'], depth, LAYER.get(depth, cls), aw, ah,
                           rr[0], rr[1], rr[2], rr[3], rr[2] - rr[0], r['visible']))

        # ---------- 判据 D 的基线：先把焦点清掉，抓两帧确认稳定 ----------
        # 必须用 clear_focus（把焦点钉到窗体）而不是"点一下页头空白"：
        # 点窗体背景**不会**把焦点从子控件上摘走，第一次真机跑就是这么被污染的 ——
        # 基线那一帧其实"第一个框正被聚焦"，如果描边逻辑存在，基线就已经是深色了。
        print('\n  -- 判据 D：聚焦前后容器描边像素比对（只用 PrintWindow）--')
        foc_base = clear_focus(form, ancestors(rows[0]['hwnd'])[1] if rows else form)
        print('      基线焦点: %s（不能是任何 EDIT，否则基线不是真正的未聚焦态）'
              % foc_desc(foc_base, 0))
        if FOCUS_DBG:
            print('      clear_focus 诊断: %s' % ' | '.join(FOCUS_DBG))

        okA, wA, hA, bufA1 = capture_window(form)
        time.sleep(0.25)
        okB, wB, hB, bufA2 = capture_window(form)
        if not (okA and okB):
            REP.add('SKIP', 'D', 'PrintWindow 抓图失败，无法比对聚焦态（不接受截图替代）')
        elif bufA1 is None or bufA2 is None or (wA, hA) != (wB, hB):
            REP.add('SKIP', 'D', '两次抓图尺寸不一致（%dx%d / %dx%d），仪器不成立'
                    % (wA, hA, wB, hB))
        else:
            # 容器矩形（客户区坐标）→ 抓图坐标（加客户区在窗口内的偏移）
            if rows:
                box = ancestors(rows[0]['hwnd'])[1]
                bx0, by0, bx1, by1 = rect_in_client(box, form)
                ox, oy = client_origin_in_window(form)
                cx0 = max(0, min(wA - 1, bx0 + ox))
                cy0 = max(0, min(hA - 1, by0 + oy))
                cw_c = max(1, min(wA - cx0, (bx1 - bx0)))
                ch_c = max(1, min(hA - cy0, (by1 - by0)))
                base1 = crop(bufA1, wA, cx0, cy0, cw_c, ch_c)
                base2 = crop(bufA2, wA, cx0, cy0, cw_c, ch_c)
                n_base, mx_base = diff_count(base1, base2)
                print('      容器矩形(客户区) [%d,%d]..[%d,%d] → 抓图裁切 %dx%d'
                      % (bx0, by0, bx1, by1, cw_c, ch_c))
                print('      基线稳定性: 两帧差异 %d px（最大通道差 %d）' % (n_base, mx_base))
                if n_base > 0:
                    REP.add('SKIP', 'D', '基线两帧就不一致（%d px 在动），无法判定聚焦态'
                            % n_base)
                else:
                    rows[0]['_dim'] = (cx0, cy0, cw_c, ch_c, base2, wA, hA)
                    rows[0]['_shot'] = (wA, hA, bufA2)

        # ================= 判据 C：点击 + ↑ 值必须 +1 =================
        print('\n  -- 判据 C：SendInput 点一下 + 按 ↑，值必须 +1 --')

        for r in rows:
            ed = r['hwnd']
            box = ancestors(ed)[1] if len(ancestors(ed)) > 1 else ed

            bl, bt, br, bb = window_rect(box)
            bw, bh = br - bl, bb - bt
            pad_x = bl + max(2, min(4, bw // 8))       # 容器左侧留白（Sc(6)≈9px 之内）
            pad_y = bt + bh // 2
            # 字面意义上的"EDIT 左边缘往左 40px"：只报它落在谁身上，不去点它 ——
            # 因为 150% 下整个左侧留白只有 9px，-40px 已经在容器外、落到卡片上了。
            el, et, er, eb = window_rect(ed)
            lit_h, lit_cls = point_owner_class(el - 40, (et + eb) // 2)

            owner, owner_cls = point_owner_class(pad_x, pad_y)
            # 落点必须命中"这一行的圆角容器"或它的子孙（输入框本体）。
            # 命中卡片/页头/别的控件 = 点歪了，必须和"产品坏了"分开报：
            # 点歪了要判 FAIL 并打印命中的类名（否则用户会以为输入框还是坏的），
            # 但它不是产品的缺陷 —— 所以消息里写清"落点错误"。
            hit_ok = (owner == box) or hwnd_in_subtree(owner, box)
            if not hit_ok:
                REP.add('FAIL', 'C1-%02d' % r['idx'],
                        '%s: 落点(%d,%d) 命中 %s，**不是**该行容器或其子孙 → 判据不成立'
                        '（点歪了，不是产品结论）'
                        % (r['label'], pad_x, pad_y, owner_cls))
                REP.add('SKIP', 'C2-%02d' % r['idx'],
                        '%s: 容器落点已经不对，框心不再盲点' % r['label'])
                continue
            if not hwnd_in_subtree(owner, form):
                REP.add('SKIP', 'C1-%02d' % r['idx'],
                        '%s: 点击点被 %s 占住（不是设置窗的窗口），拒绝盲点'
                        % (r['label'], owner_cls))
                REP.add('SKIP', 'C2-%02d' % r['idx'], '%s: 同上，跳过' % r['label'])
                continue

            # 先清焦点：否则"值变了"可能来自开窗时的初始焦点，而不是这次点击
            foc0 = clear_focus(form, box)
            if foc0 in edit_set:
                print('      ⚠ %s: 清焦点失败（仍在 %s 上），本行证据偏弱，请注意'
                      % (r['label'], foc_desc(foc0, ed)))

            v0 = edit_text(ed)
            click_at(pad_x, pad_y)
            time.sleep(0.25)
            foc = focus_of(form)
            v1 = edit_text(ed)          # 只点击、还没按 ↑

            # 判据 D 用第一行：点完留白（焦点应已进入 EDIT）立刻抓一帧
            if r['idx'] == 1 and '_dim' in r:
                okC, wC, hC, bufC = capture_window(form)
                if okC and bufC and (wC, hC) == (r['_dim'][5], r['_dim'][6]):
                    cx0, cy0, cw_c, ch_c, base, _w, _h = r['_dim']
                    after = crop(bufC, wC, cx0, cy0, cw_c, ch_c)
                    n_diff, mx_diff = diff_count(base, after)
                    foc_ok = (foc == ed)
                    if n_diff > 0 and foc_ok:
                        REP.add('PASS', 'D',
                                '聚焦后容器描边变化 %d px（最大通道差 %d），焦点=%s '
                                '（裁切区 %dx%d）' % (n_diff, mx_diff, foc_desc(foc, ed),
                                                     cw_c, ch_c))
                    else:
                        REP.add('FAIL', 'D',
                                '聚焦后容器描边变化 %d px（最大通道差 %d），焦点=%s'
                                ' → 用户看不出这个框被选中'
                                % (n_diff, mx_diff, foc_desc(foc, ed)))
                else:
                    REP.add('SKIP', 'D', '聚焦后抓图失败，无法比对')

            key_tap(VK_UP)
            time.sleep(0.3)
            v2 = edit_text(ed)
            try:
                up = (int(v2) - int(v1)) == 1
                detail = '%s→%s' % (v1, v2)
            except Exception:
                up = False
                detail = '%r→%r（非数字，读不到值）' % (v1, v2)

            # 判据 = 值 +1 **且** 焦点确实进了这一行的 EDIT。
            # 两条都要：只有值变了、焦点却在别的框上是"假阳性"（见 clear_focus 注释）；
            # 只有焦点进来了、值不变成，说明能点、能聚焦但改不了值，同样是缺陷。
            got_focus = (foc == ed)
            extra = '；字面 -40px 处是 %s' % lit_cls if lit_cls else ''
            if up and got_focus:
                REP.add('PASS', 'C1-%02d' % r['idx'],
                        '%s: 点容器留白(%d,%d)，命中 %s → 焦点进本行EDIT → %s（点击前 %s）%s'
                        % (r['label'], pad_x, pad_y,
                           '容器本身' if owner == box else owner_cls,
                           detail, v0, extra))
            else:
                why = []
                if not got_focus:
                    why.append('点击没把焦点送进输入框（焦点=%s）' % foc_desc(foc, ed))
                if not up:
                    why.append('按 ↑ 值不变')
                REP.add('FAIL', 'C1-%02d' % r['idx'],
                        '%s: 点容器留白(%d,%d) 后 %s（点击前 %s）—— %s%s'
                        % (r['label'], pad_x, pad_y, detail, v0, '；'.join(why), extra))

            # C2 诊断：直接点输入框正中。用来区分"留白没转发焦点"和"整个框都不可交互"。
            cxp, cyp = (el + er) // 2, (et + eb) // 2
            owner2, owner2_cls = point_owner_class(cxp, cyp)
            if not ((owner2 == box) or hwnd_in_subtree(owner2, box)):
                REP.add('SKIP', 'C2-%02d' % r['idx'],
                        '%s: 框心(%d,%d) 不在该行容器内（该点是 %s），拒绝盲点 —— '
                        '这本身就是"输入框跑到容器外"的直接证据'
                        % (r['label'], cxp, cyp, owner2_cls))
                continue
            clear_focus(form, box)
            w0 = edit_text(ed)
            click_at(cxp, cyp)
            time.sleep(0.2)
            foc2 = focus_of(form)
            key_tap(VK_UP)
            time.sleep(0.3)
            w1 = edit_text(ed)
            try:
                up2 = (int(w1) - int(w0)) == 1
                d2 = '%s→%s' % (w0, w1)
            except Exception:
                up2 = False
                d2 = '%r→%r' % (w0, w1)
            if up2 and foc2 == ed:
                REP.add('PASS', 'C2-%02d' % r['idx'],
                        '%s: 点框心(%d,%d) → 焦点进本行EDIT → %s'
                        % (r['label'], cxp, cyp, d2))
            else:
                why2 = []
                if foc2 != ed:
                    why2.append('焦点没进本行EDIT（%s）' % foc_desc(foc2, ed))
                if not up2:
                    why2.append('值不变')
                REP.add('FAIL', 'C2-%02d' % r['idx'],
                        '%s: 点框心(%d,%d) 后 %s —— %s（该点上实际是 %s）'
                        % (r['label'], cxp, cyp, d2, '；'.join(why2), owner2_cls))

        # ================= 判据 E：「预览渐暗」按钮 禁用/可用 的视觉方向 =================
        # 这条只看像素，不看属性 —— 因为自绘按钮没有系统主题兜底，Enabled 改了但画出来
        # 可能"禁用比可用更醒目"（v1.6.1 之前就是这样：ControlPaint.Dark 把字压得更黑，
        # 在浅底深字上方向反了；lead 取样为 禁用(28,32,38) < 可用(74,85,104)）。
        # 同时顺带验一条行为：把强度值改到 0 → 按钮**立刻**变灰（ValueChanged 直连，
        # 不用点保存），这也是 v1.6.1 新增的联动。
        print('\n  -- 判据 E：「预览渐暗」按钮 禁用/可用 的像素方向 --')
        btn = None
        for k in kids:
            if short_class(class_name(k)) == 'BUTTON' and '预览渐暗' in (window_text(k) or ''):
                btn = k
                break
        dim_row = None
        for r in rows:
            if '渐暗' in r['label']:
                dim_row = r
                break
        if btn is None or dim_row is None:
            REP.add('SKIP', 'E', '没找到「预览渐暗」按钮或渐暗强度行（文案变了？）')
        else:
            dim_edit = dim_row['hwnd']
            dim_box = ancestors(dim_edit)[1]
            bl, bt, br, bb = window_rect(dim_box)
            dpx, dpy = bl + max(2, min(4, (br - bl) // 8)), bt + (bb - bt) // 2

            def btn_brightness():
                ok, w2, h2, buf2 = capture_window(form)
                if not ok or not buf2:
                    return None, None
                x0, y0, x1, y1 = rect_in_image(btn, form, w2, h2, expand=-6)
                return darkest(buf2, w2, x0, y0, x1, y1), (x0, y0, x1, y1)

            v_start = edit_text(dim_edit)
            # 先确保"可用态"：强度为 0 时按钮本来就是灰的
            if edit_text(dim_edit) == '0':
                clear_focus(form, dim_box)
                click_at(dpx, dpy)
                key_tap(VK_UP)
                time.sleep(0.3)
            en0 = bool(user32.IsWindowEnabled(btn))
            brt_en, rect_e = btn_brightness()

            # 降到 0：连按 ↓（不用打字，避免输入法/全角干扰）
            clear_focus(form, dim_box)
            click_at(dpx, dpy)
            time.sleep(0.15)
            n_down = 0
            while edit_text(dim_edit) != '0' and n_down < 120:
                key_tap(VK_DOWN)
                n_down += 1
                time.sleep(0.015)
            time.sleep(0.35)
            v_zero = edit_text(dim_edit)
            en1 = bool(user32.IsWindowEnabled(btn))
            brt_dis, _r2 = btn_brightness()

            if brt_en is None or brt_dis is None:
                REP.add('SKIP', 'E', '抓图失败，无法量按钮文字色')
            else:
                e1 = (en0 and not en1)
                e2 = (brt_dis is not None and brt_en is not None and brt_dis > brt_en + 20)
                if e1:
                    REP.add('PASS', 'E1',
                            '按钮可用性随强度联动：强度>0 时 Enabled=%s，降到 0 后 Enabled=%s'
                            '（按 ↓ %d 次，实测值=%s）' % (en0, en1, n_down, v_zero))
                else:
                    REP.add('FAIL', 'E1',
                            '按钮可用性没跟着强度走：强度>0 Enabled=%s，降到 0 后 Enabled=%s'
                            % (en0, en1))
                if e2:
                    REP.add('PASS', 'E2',
                            '禁用态文字更"淡"（方向正确）：可用亮度 %d → 禁用亮度 %d'
                            '（差 +%d）' % (brt_en, brt_dis, brt_dis - brt_en))
                else:
                    REP.add('FAIL', 'E2',
                            '禁用态文字比可用态更"浓"或几乎无差：可用亮度 %d → 禁用亮度 %d'
                            '（差 %d，要求 > +20）' % (brt_en, brt_dis, brt_dis - brt_en))

            # 还原强度值（不点保存，进程随后被杀，配置不会被写回；这里只是别留个脏状态）
            if v_start != edit_text(dim_edit):
                clear_focus(form, dim_box)
                click_at(dpx, dpy)
                n_up = 0
                while edit_text(dim_edit) != v_start and n_up < 130:
                    key_tap(VK_UP)
                    n_up += 1
                    time.sleep(0.015)
                print('      强度已还原: %s → %s' % (v_zero, edit_text(dim_edit)))

        # ---------- 存全景图 ----------
        # 取最后一次成功的整窗抓图；一次都不成功就不写文件（不拿截图凑数）。
        if not [x for x in REP.rows if x[1] == 'D']:
            REP.add('SKIP', 'D', '未执行（第一行的点击点没通过落点校验，或抓图基线不稳定）')
        shot = None
        for r in rows:
            if '_shot' in r:
                shot = r['_shot']
        if shot:
            try:
                write_png(shot_path, shot[0], shot[1], shot[2])
                print('\n  全景图（PrintWindow，未截屏）: %s  %dx%d  %.1f KB'
                      % (shot_path, shot[0], shot[1],
                         os.path.getsize(shot_path) / 1024.0))
            except Exception as e:
                print('\n  全景图写入失败: %s' % e)
        else:
            print('\n  全景图: 没有成功的 PrintWindow 抓图，未写文件')

        # 另外存一张"第一个输入框 + 四周 20px 上下文"的裁切图：
        # 整页缩略图看不出 9px 的间隙和 1px 的描边，像素级对照要用这张。
        if crop_path and shot and rows:
            try:
                box0 = ancestors(rows[0]['hwnd'])[1]
                x0, y0, x1, y1 = rect_in_image(box0, form, shot[0], shot[1], expand=20)
                write_png(crop_path, x1 - x0, y1 - y0,
                          crop(shot[2], shot[0], x0, y0, x1 - x0, y1 - y0))
                print('  裁切图（%s + 四周 20px）: %s  %dx%d  %.1f KB'
                      % (rows[0]['label'], crop_path, x1 - x0, y1 - y0,
                         os.path.getsize(crop_path) / 1024.0))
            except Exception as e:
                print('  裁切图写入失败: %s' % e)

        return REP.summary()
    finally:
        # ---- 清理：只杀自己启动的进程，配置按原始字节还原 ----
        try:
            user32.SetCursorPos(cursor0.x, cursor0.y)   # 别把用户的鼠标留在别处
        except Exception:
            pass
        if proc is not None:
            try:
                user32.SetWindowPos(FORM[0], HWND_NOTOPMOST, 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
            except Exception:
                pass
            ok = kill_pid(proc.pid)
            print('\n清理    : taskkill /PID %d = %s' % (proc.pid, 'ok' if ok else '未退出'))
        write_cfg_raw(raw_cfg)
        now = read_cfg_raw()
        print('          config.ini 已按原始字节还原: %d 字节  sha1:%s  与原备份一致=%s'
              % (len(now),
                 hashlib.sha1(now).hexdigest()[:12] if now else '-',
                 'YES' if now == raw_cfg else 'NO'))


if __name__ == '__main__':
    sys.exit(main())
