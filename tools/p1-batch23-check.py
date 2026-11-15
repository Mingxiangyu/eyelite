#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P1 批 2/3 验收脚本：Movement(G1/G2/G11) / E1 跳过配额 / E6 锁前倒计时 / Stats / EdgeGlow / Ritual / 设置页

## 先说三条"读源码后才知道"的硬约束（不遵守就会得到一堆假 FAIL）

1. **演示窗只挂在 bodyBreak 上**（Program.cs:6352 `if (bodyBreak) { ShowMovementForRest(...); ... }`）。
   普通护眼休息（到点弹的那种）**永远没有** 480×420 / 320×360 小窗 —— 这是刻意的设计
   （注释原文：护眼休息时在屏幕正中弹一个会动的窗，等于把视线钉回屏幕上）。
2. **bodyBreak 只能由久坐时钟触发**，`sedLimitMs = SedentaryMinutes * 60s`（Program.cs:5323），
   而 `SedentaryMinutes` 的钳制范围是 **15~180**（Program.cs:3109）→ 自然等待最短也要 15 分钟。
   注意 `SedentarySeconds` 是**活动休息的时长**，不是触发阈值 —— 把 `SedentaryEnabled=1`
   配成 `SedentarySeconds=30` 不会产生 bodyBreak（这是个很容易搞错的点）。
   为测试加了钩子 `EYE_LITE_FAKE_SED_MS`（静态、启动读一次、"久坐累计毫秒"钉成给定值，
   在 sedDue 计算之前生效）：本脚本一律设 `=900000`（= 15 分钟）→ **bodyBreak 启动后一两个
   tick 就到点**，每个 bodyBreak 用例从 15.5 分钟压到几秒。
   **注意该钩子是"一次性消耗"的**（2026-11-15 语义变更）：只在第一次 tick 把久坐累计推上去，
   之后立刻失效、回到真实累加。这正好合用 —— 休息结束时 `ResetWork()` 会把久坐累计归零，
   所以**不会出现"每轮休息后又立刻到点"的无限循环**；每个用例都是新进程，钩子各自生效一次。
   仍有两条硬要求：`WorkMinutes=20`（否则 60s 会先弹普通休息、ResetWork 把久坐累积清零）、
   `SedentaryMinutes=15`（钳制下限，保持真实）。
   钩子没编进构建时的自保：bodyBreak 若 150s 内没出现，判 **SKIP**（"钩子未生效"），
   而不是傻等 15 分钟，也不是判产品 FAIL。
3. **`EdgeGlowForm` 和 `DimForm` 在结构上完全一样**：整屏、`WS_EX_TRANSPARENT(0x20)`、
   零子窗口（EdgeGlowForm 见 Program.cs:1456-1504）。所以**不能**靠窗口特征区分，
   只能靠配置隔离：EdgeGlow 用例里把 `DimAlphaMax=0`（关掉渐暗），此时屏幕上唯一的
   整屏穿透层就是 EdgeGlow。G2 的像素抓取同理。

## 用例与判据（每条都有对照组）

### Movement（插件 Plugin_Movement）
配置：`SedentaryEnabled=1, SedentaryMinutes=15, SedentarySeconds=20, WorkMinutes=20`
      + 环境变量 `EYE_LITE_FAKE_SED_MS=900000`（久坐累积钉到 15 分钟 → bodyBreak 立即到点）
  · **G1** `MovementContraindications=""` → 遮罩出现后应出现 **480×420**（允许 ±6px DPI 误差）
    的窗口；子窗口/标题为空是正常的（MoveForm 全部自绘，见 2606 行），所以判据只认尺寸+属性。
  · **G2 动画**：对 480×420 窗口客户区**连抓 3 帧、间隔 0.8s**，判据分三档：
      - 任意两帧指纹不同 → 画面在动 → PASS
      - 三帧全同 → 才判 FAIL（先排除"两次抓的是同一帧"：MoveForm 是 16ms 帧，
        间隔 0.8s 抓 3 次仍全同，说明定时器真的没在跑）
      - 三种抓图方式全失败 → SKIP（判"仪器不成立"），**不**用"文本不同"冒充动画
        （MoveForm 没有子控件，文本法本来就不成立）
  · **G11**（本批最重要）：
      - `=""` → 有 480×420（6 个动作全可用）
      - `="腰椎"` → **仍有** 480×420（还剩 3 个动作）
      - `="腰椎;脊柱;膝;髋;颈椎;肩袖;肩;腕;腱鞘;腕管"` → **没有** 480×420，但**遮罩必须正常**
        （全部动作被排除 = 安静休息，且不能退回去弹 Ritual）
      - 对照：`Plugin_Movement=0` → 没有 480×420（**必跑**，见下）
    > **为什么 `movementctl` 不能省**：`=""` 与"全部排除"是同一二进制、同一配置、
    > 只差一行禁忌词的组内对照 —— 这能证明"禁忌过滤生效"，但证明不了"没有窗口"
    > 是因为过滤而**不是插件根本没工作**。所以 `Plugin_Movement=0` 这条对照必跑
    > （加了 `EYE_LITE_FAKE_SED_MS` 之后它只要 ~1 分钟）。

### E1 跳过配额（插件 Guard）
配置：`Plugin_Guard=1, SkipQuotaPerDay=2, AllowSkip=1, SkipQuotaUsed=0, SkipQuotaDate=<今天>`
同理 `Plugin_Guard=0` 做对照。
  · 第 1、2 次点「跳过」→ 遮罩消失
  · 第 3 次：按钮文本变「跳过（今日已用完）」/「今日已用完」（程序置 `Enabled=false` 并换文案，
    Program.cs:3610-3627）；**点击后遮罩不消失**
  · **额外（最关键）**：配额用完时按 **Esc**，在非严格模式下**必须不被吞** ——
    用 WH_KEYBOARD_LL 观察钩子验证 down/up 都到达下游。
    实现正确性要求是"不挂 OnSkipKey 回调"（Program.cs:6382 `else KeyboardGuard.OnSkipKey = null`），
    而不是"回调里 return"（后者会变成"吞了但没跳过"，比不响应更糟）。

### E6 锁前倒计时
配置：`StrictMode=1, AllowSkip=1, LockCountdownSeconds=5, RestSeconds=20`
  · 遮罩出现后 ≤5s：注入 F24 → observer 应看到 2 条（还没锁）；且子窗口文本应含「秒后锁定键盘」
  · 5s 之后：注入 F24 → observer 应看到 **0** 条（已锁）
  · 对照：`LockCountdownSeconds=0` → 遮罩一出现就该吞（0 条）

### Stats（插件 Plugin_Stats）
  · `Plugin_Stats=1`：完整坐满一轮休息 → `config.ini` 的 `StatDone` +1（计数即刻写盘，
    Program.cs:2211-2216）；跳过一轮 → `StatSkip` +1
  · 对照：`Plugin_Stats=0` → 两个计数都不变

### EdgeGlow / Ritual
  · EdgeGlow：`Plugin_EdgeGlow=1, DimAlphaMax=0` → 剩余 ≤60s 时应有整屏穿透层（0 子窗口），
    进入遮罩后应消失
  · Ritual：`Plugin_Ritual=1` 时，**普通护眼休息不应有 320×360 窗**；**久坐休息应有 320×360 窗**
  · 两者互斥：Movement 开启优先于 Ritual（Program.cs:6349-6359），用例里分别只开一个

### 设置页「扩展」卡（settings）
设置窗需要点托盘菜单才能打开 —— 那要真实点击/抢前台，违反本批纪律。
所以本项**只观察"已经打开的"设置窗**：若检测到 EyeLite 的可设置窗（见下）就读文本；
没有就 SKIP 并提示"请手动打开设置页后重跑 settings"，**不自己开窗**。

## 预判的假 FAIL 风险
1. **bodyBreak 等不到**：`SedentaryMinutes` 被钳到 ≥15，且没有钩子。若中途有人打字/空闲，
   累积会暂停（`IdleMinutes=0` 时不会因空闲暂停，但 `paused` 会造成）。脚本每 60s 打印一次
   进度，超时 16 分钟即 FAIL 并打印"最后一次看到的遮罩/小窗状态"。
2. **G2 抓图失败不等于产品没动**：`PrintWindow` 对某些 DWM 合成窗口返回黑图。脚本连试三种
   抓图法；全失败判 SKIP，**不判 FAIL**（宁可没结论，也不给假结论）。
3. **E1 第 3 次点击**：按钮 `Enabled=false` 后 `PostMessage` 仍能发进去，但 WinForms 不会派发
   Click —— 这正是我们要的"点了没反应"。判据用"遮罩是否消失"，不用"按钮是否 Enabled"（跨进程读不到）。
4. **E6 倒计时文本**：文案是「5 秒后锁定键盘」，秒数会变；用**包含**匹配「秒后锁定键盘」。
5. **EdgeGlow 与 DimForm 结构相同**：所以 EdgeGlow 用例必须 `DimAlphaMax=0`，否则两个整屏
   穿透层无法区分（见头部第 3 条）。
6. **Stats 计数写盘会重写 config.ini**（`cfg.Save()`），会把测试配置写成文件内容 —— 所以
   脚本按**原始字节**备份、最后还原，并在统计前后都重新读取同一份配置做对比。

## 耗时预估（单次全跑 ≈ 8~10 分钟，且不依赖人不动鼠标之外的任何前提）
    settings   ~0s      （窗口没开就 SKIP）
    edge       ~2 min   （亮/灭 两组）
    e6         ~1.5 min （倒计时 5s + 对照）
    stats      ~3 min   （坐满 1 轮 + 跳过 1 轮 + 对照）
    e1         ~2 min   （3 轮跳过 ×2 组）
    ritual     ~1.5 min （普通休息无小窗 + 久坐休息有小窗）
    movement   ~2 min   （3 个禁忌配置）
    movementctl ~0.5 min（Plugin_Movement=0 对照）
  合并成一次跑完全可行（≈10 分钟）；若 `EYE_LITE_FAKE_SED_MS` 没编进构建，
  bodyBreak 相关的 4 项会各自在 150s 处判 SKIP 并明确提示"钩子未生效"。

## 用法
    python tools/p1-batch23-check.py                 # 全部（含 movementctl）
    python tools/p1-batch23-check.py edge e6 stats   # 挑着跑
    python tools/p1-batch23-check.py settings        # 只跑设置页（需你先手动把设置页打开）
"""

import ctypes
import ctypes.wintypes as wt
import hashlib
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')
PROBE_DIR = os.path.join(ROOT, 'tools', '_probe')

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)

# ---------------------------------------------------------------- Win32 声明

user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.PostMessageW.restype = wt.BOOL
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetForegroundWindow.restype = wt.HWND
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.CloseHandle.argtypes = [wt.HANDLE]

# 观察钩子：WPARAM/LPARAM 必须 64 位（keyup 的 lParam 带高位，用 32 位会 OverflowError
# → 回调抛异常 → ctypes 返回 0 → 观察钩子自己把键吞掉。批 1 已经踩过这个坑）
LRESULT_T = ctypes.c_ssize_t
WPARAM_T = ctypes.c_size_t
LPARAM_T = ctypes.c_ssize_t
WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
LLKHF_INJECTED = 0x10
PM_REMOVE = 0x0001
KEYEVENTF_KEYUP = 0x0002
VK_ESCAPE = 0x1B
VK_F24 = 0x87

HOOKPROC_T = ctypes.WINFUNCTYPE(LRESULT_T, ctypes.c_int, WPARAM_T, LPARAM_T)
user32.SetWindowsHookExW.restype = wt.HANDLE
user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC_T, wt.HMODULE, wt.DWORD]
user32.CallNextHookEx.restype = LRESULT_T
user32.CallNextHookEx.argtypes = [wt.HANDLE, ctypes.c_int, WPARAM_T, LPARAM_T]
user32.UnhookWindowsHookEx.argtypes = [wt.HANDLE]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
BM_CLICK = 0x00F5
MK_LBUTTON = 0x0001

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

# P1 批 2/3 的配置键（用户 config 里没有，写配置时追加；还原按原始字节）
P1_KEYS = ['Plugin_Sound', 'ForegroundGuardEnabled', 'ForegroundWhitelist',
           'TypingGuardEnabled', 'SittingOptionEnabled',
           'Plugin_Stats', 'Plugin_EdgeGlow', 'Plugin_Ritual', 'Plugin_Movement',
           'Plugin_Guard', 'MovementContraindications', 'SkipQuotaPerDay',
           'SkipQuotaDate', 'SkipQuotaUsed', 'LockCountdownSeconds',
           'StatDone', 'StatSkip', 'StatIgnore']

_pid_cache = set()
DIM_SEEN = [False]

# ---------------------------------------------------------------- 观察钩子


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [('vkCode', wt.DWORD), ('scanCode', wt.DWORD), ('flags', wt.DWORD),
                ('time', wt.DWORD), ('dwExtraInfo', ctypes.c_size_t)]


_seen = []
_hhook = [None]


def _hook_cb(nCode, wParam, lParam):
    if nCode == 0:
        try:
            kb = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
            _seen.append((int(wParam), int(kb.vkCode),
                          bool(kb.flags & LLKHF_INJECTED)))
        except Exception:
            pass
    return user32.CallNextHookEx(_hhook[0], nCode, wParam, lParam)


_HOOK_REF = HOOKPROC_T(_hook_cb)


def install_observer():
    kernel32.GetModuleHandleW.restype = wt.HMODULE
    kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]
    hinst = kernel32.GetModuleHandleW(None)      # 必须显式 restype，否则 64 位句柄被截断
    if not hinst:
        print('  GetModuleHandleW failed, err=%d' % ctypes.get_last_error())
        return False
    _hhook[0] = user32.SetWindowsHookExW(WH_KEYBOARD_LL, _HOOK_REF, hinst, 0)
    if not _hhook[0]:
        print('  SetWindowsHookEx err=%d' % ctypes.get_last_error())
    return bool(_hhook[0])


def uninstall_observer():
    if _hhook[0]:
        try:
            user32.UnhookWindowsHookEx(_hhook[0])
        except Exception:
            pass
        _hhook[0] = None


def pump(seconds):
    """泵消息 + 小睡。**不泵就收不到低级钩子回调**（观察钩子形同不存在）。"""
    msg = wt.MSG()
    end = time.time() + seconds
    while time.time() < end:
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.005)


def key_tap(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.03)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


def probe(vk, wait=0.5):
    """注入一个键，返回 observer 在此期间看到的**注入**消息。"""
    pump(0.08)
    start = len(_seen)
    key_tap(vk)
    pump(wait)
    return [m for m in _seen[start:] if m[2]]


def count_msgs(msgs, vk):
    return len([m for m in msgs if m[1] == vk
                and m[0] in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP)])


# ---------------------------------------------------------------- 窗口


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
        out.append((ch, class_name(ch), window_text(ch),
                    bool(user32.IsWindowVisible(ch))))
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return out


def child_texts(hwnd):
    return [t for (_c, _k, t, _v) in enum_children(hwnd) if t]


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
    """休息遮罩：大尺寸 + 非 TRANSPARENT + 子窗口≥1（沿用 P0 判据，排除渐暗层）。"""
    out = []
    for hwnd, w, h, pid in own_windows():
        if not (w > 800 and h > 500):
            continue
        if ex_style(hwnd) & WS_EX_TRANSPARENT:
            DIM_SEEN[0] = True
            continue
        if child_count(hwnd) < 1:
            continue
        out.append((hwnd, w, h, pid))
    return out


def preview_windows():
    return [t for t in own_windows() if 200 < t[1] <= 900 and t[2] <= 300]


def transparent_fullscreen():
    """整屏 + TRANSPARENT + 无子窗口：DimForm 或 EdgeGlowForm（靠配置区分，见头部第 3 条）。"""
    return [(hwnd, w, h) for hwnd, w, h, _p in own_windows()
            if w > 800 and h > 500 and (ex_style(hwnd) & WS_EX_TRANSPARENT)
            and child_count(hwnd) == 0]


def find_by_size(w, h, tol=6):
    """按尺寸找窗口（小窗没有标题、也常常没有子控件，只能用尺寸认）。"""
    return [(hw, a, b) for hw, a, b, _p in own_windows()
            if abs(a - w) <= tol and abs(b - h) <= tol]


def describe(hwnd):
    return '%s [%s] %dx%d children=%d' % (
        (window_text(hwnd) or '(untitled)'), class_name(hwnd),
        (lambda r: r.right - r.left)(_rect(hwnd)),
        (lambda r: r.bottom - r.top)(_rect(hwnd)), child_count(hwnd))


def _rect(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


# ---------------------------------------------------------------- 抓图（G2）


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', ctypes.c_long),
                ('biHeight', ctypes.c_long), ('biPlanes', wt.WORD),
                ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', wt.DWORD),
                ('biClrImportant', wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [('bmiHeader', BITMAPINFOHEADER),
                ('bmiColors', wt.DWORD * 3)]


# GDI 句柄全是 64 位指针：不声明 restype 会被截成 32 位，抓图必然失败
# （和 GetModuleHandleW 那次同一个坑，本机踩过多次）。argtypes 也要显式声明，
# 否则 HWND/HDC 按 32 位 int 传，遇到大句柄会静默抓错窗口。
gdi32.CreateCompatibleDC.restype = wt.HDC
gdi32.CreateCompatibleDC.argtypes = [wt.HDC]
gdi32.CreateCompatibleBitmap.restype = wt.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = wt.HANDLE
gdi32.SelectObject.argtypes = [wt.HDC, wt.HANDLE]
gdi32.DeleteObject.argtypes = [wt.HANDLE]
gdi32.DeleteDC.argtypes = [wt.HDC]
gdi32.GetDIBits.restype = ctypes.c_int
gdi32.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT,
                            ctypes.c_void_p, ctypes.POINTER(BITMAPINFO), wt.UINT]
gdi32.BitBlt.restype = wt.BOOL
gdi32.BitBlt.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         ctypes.c_int, wt.HDC, ctypes.c_int, ctypes.c_int, wt.DWORD]
user32.GetDC.restype = wt.HDC
user32.GetDC.argtypes = [wt.HWND]
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
user32.PrintWindow.restype = wt.BOOL
user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]


def capture_hash(hwnd):
    """抓窗口客户区像素，返回 (方法名, md5) 或 (None, 原因)。

    三层退路：PrintWindow(PW_RENDERFULLCONTENT=2) → PrintWindow(0) → BitBlt。
    全失败返回 None，调用方据此判 SKIP（绝不拿"文本变了"来假装验过动画）。
    """
    r = wt.RECT()
    w = h = 0
    if user32.GetClientRect(hwnd, ctypes.byref(r)):
        w, h = r.right - r.left, r.bottom - r.top
    if w <= 0 or h <= 0:
        return None, '客户区尺寸为 0'

    hdc = user32.GetDC(hwnd)
    if not hdc:
        return None, 'GetDC 失败'
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(memdc, bmp)

    def _to_hash(tag):
        bi = BITMAPINFO()
        bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bi.bmiHeader.biWidth = w
        bi.bmiHeader.biHeight = -h        # 负数 = 自上而下
        bi.bmiHeader.biPlanes = 1
        bi.bmiHeader.biBitCount = 32
        bi.bmiHeader.biCompression = 0
        buf = ctypes.create_string_buffer(w * h * 4)
        n = gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
        if not n:
            return None
        return hashlib.md5(buf.raw).hexdigest()

    try:
        for tag, flag in (('PrintWindow2', 2), ('PrintWindow0', 0)):
            ok = user32.PrintWindow(hwnd, memdc, flag)
            if ok:
                d = _to_hash(tag)
                if d:
                    return tag, d
        if gdi32.BitBlt(memdc, 0, 0, w, h, hdc, 0, 0, 0x00CC0020):
            d = _to_hash('BitBlt')
            if d:
                return 'BitBlt', d
        return None, '三种抓图方式都失败'
    finally:
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(memdc)
        user32.ReleaseDC(hwnd, hdc)


# ---------------------------------------------------------------- 点击


def click_child(hwnd):
    cr = wt.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(cr)):
        cr.right, cr.bottom = 10, 10
    x = max(1, (cr.right - cr.left) // 2)
    y = max(1, (cr.bottom - cr.top) // 2)
    lp = (y << 16) | (x & 0xFFFF)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)
    time.sleep(0.05)
    user32.PostMessageW(hwnd, BM_CLICK, 0, 0)


def find_child_contains(hwnd, needle):
    for ch, cls, text, _v in enum_children(hwnd):
        if text and needle in text:
            return (ch, cls, text)
    return None


# ---------------------------------------------------------------- 配置


CFG_ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax'] + P1_KEYS


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


def today_str():
    return time.strftime('%Y-%m-%d')


# ---------------------------------------------------------------- 进程


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


def launch(sed_ms=None):
    """启动 EyeLite，清掉所有测试钩子（免得继承到旧状态）。

    sed_ms：把"久坐累计毫秒"钉成给定值（`EYE_LITE_FAKE_SED_MS`），
    让 bodyBreak 不用真等 15 分钟。钩子没编进构建时它会被忽略（无害）。
    """
    env = os.environ.copy()
    for k in ('EYE_LITE_FORCE_QUNS', 'EYE_LITE_FORCE_QUNS_FILE',
              'EYE_LITE_SUPPRESS_MAX_MS', 'EYE_LITE_FAKE_IDLE_MS',
              'EYE_LITE_FAKE_IDLE_FILE', 'EYE_LITE_TRACE_FILE',
              'EYE_LITE_FAKE_SED_MS'):
        env.pop(k, None)
    if sed_ms is not None:
        env['EYE_LITE_FAKE_SED_MS'] = str(sed_ms)
    proc = subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _pid_cache.add(proc.pid)
    return proc


def alive(pid):
    return pid in eyelite_pids()


def wait_pred(pred, timeout_s, tag='', hb=20.0):
    """轮询（带泵消息），返回 (命中时的秒数 或 None, pred 的返回值)。"""
    t0 = time.time()
    last = t0
    while time.time() - t0 < timeout_s:
        v = pred()
        if v:
            return time.time() - t0, v
        pump(0.1)
        if time.time() - last >= hb:
            last = time.time()
            print('    %s... %.0fs' % (tag, time.time() - t0))
    return None, None


def wait_overlay(timeout_s, tag='等遮罩'):
    return wait_pred(lambda: overlay_windows() or None, timeout_s, tag)


def wait_overlay_gone(timeout_s):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if not overlay_windows():
            return time.time() - t0
        pump(0.1)
    return None


# ---------------------------------------------------------------- 公共底配置


def base_cfg(orig, **kw):
    """P1 批次统一底配置：把与本次用例无关的机制全隔离掉。

    隔离理由：
      · SuppressOnFullscreen=0 —— 否则用户一旦切全屏，15 分钟的等待直接白费
      · DimAlphaMax=0          —— 渐暗层和 EdgeGlow 结构相同，必须关掉才能区分
      · IdleMinutes=0          —— 空闲暂停会让久坐累积停住
      · PreviewSeconds=0       —— 不要横幅来干扰"遮罩出现时刻"
    """
    d = dict(orig)
    d.update({
        'WorkMinutes': '20',            # bodyBreak 用例里避免 60s 先弹一次普通休息
        'PreviewSeconds': '0',
        'PomodoroEnabled': '0',
        'IdleMinutes': '0',
        'SoundEnabled': '0',
        'AllowSkip': '1',
        'StrictMode': '0',
        'SuppressOnFullscreen': '0',
        'DimAlphaMax': '0',
        'SedentaryEnabled': '0',
        'Plugin_Sound': '0',
        'Plugin_Stats': '0',
        'Plugin_EdgeGlow': '0',
        'Plugin_Ritual': '0',
        'Plugin_Movement': '0',
        'Plugin_Guard': '0',
        'MovementContraindications': '',
        'SkipQuotaPerDay': '3',
        'SkipQuotaDate': today_str(),
        'SkipQuotaUsed': '0',
        'LockCountdownCountdown': '0',
        'LockCountdownSeconds': '0',
        'StatDone': '0',
        'StatSkip': '0',
    })
    d.update(kw)
    return d


def body_break_cfg(orig, **kw):
    """构造一次「久坐/活动休息」。

    久坐累积由 `EYE_LITE_FAKE_SED_MS=900000` 钉住 → 启动后一两个 tick 就到点。
    `SedentarySeconds=20` 只是"这次活动休息多长"，调短一点减少遮罩停留。
    """
    d = base_cfg(orig, **{
        'WorkMinutes': '20',            # 别让 60s 的普通休息先弹（会 ResetWork 清掉久坐累积）
        'SedentaryEnabled': '1',
        'SedentaryMinutes': '15',       # 钳制下限，保持真实
        'SedentarySeconds': '20',       # 活动休息时长
        'RestSeconds': '20',
    })
    d.update(kw)
    return d


FAKE_SED_MS = 900000              # = 15 分钟，等于 SedentaryMinutes 的下限
BODY_BREAK_TIMEOUT = 150          # 有钩子时几秒就到；150s 没来 = 钩子未生效，判 SKIP


# ---------------------------------------------------------------- 用例


def case_movement(base_orig, results, plugin_on=True, contra='', label='G1/G2',
                  expect_window=True):
    """跑一次 bodyBreak，判定 480×420 演示窗是否出现（并在出现时验动画）。"""
    print('\n--- Movement %s：contraindications=%r 期望 %s480×420 ---'
          % (label, contra, '' if expect_window else '**没有**'))
    if not stop_eyelite():
        results.append(('Movement %s' % label, False, 'EyeLite 实例停不下来'))
        return
    cfg = body_break_cfg(base_orig, **{
        'Plugin_Movement': '1' if plugin_on else '0',
        'MovementContraindications': contra,
    })
    write_cfg(cfg)
    proc = launch(sed_ms=FAKE_SED_MS)
    print('  cfg: SedMinutes=15 SedSeconds=20 WorkMinutes=20 Plugin_Movement=%d '
          'contra=%r  EYE_LITE_FAKE_SED_MS=%d'
          % (1 if plugin_on else 0, contra, FAKE_SED_MS))
    time.sleep(1.5)
    if not alive(proc.pid):
        results.append(('Movement %s' % label, False, '进程启动后消失'))
        return

    # 先等遮罩（bodyBreak 到点后会同时弹遮罩 + 小窗）
    t_ov, _ = wait_overlay(BODY_BREAK_TIMEOUT, tag='等久坐休息遮罩')
    if t_ov is None:
        results.append(('Movement %s' % label, None,
                        'SKIP %ds 内没等到久坐休息遮罩：EYE_LITE_FAKE_SED_MS 很可能'
                        '没编进构建（不是产品问题，也不是硬等 15 分钟）'
                        % BODY_BREAK_TIMEOUT))
        return
    print('  遮罩 %.0fs 出现（有钩子时应在 1~3s）' % t_ov)

    hit, wins = wait_pred(lambda: find_by_size(480, 420) or None, 6.0)
    kids_txt = []
    anim = 'n/a'
    hashes = []
    if wins:
        hwnd = wins[0][0]
        kids_txt = child_texts(hwnd)
        print('  480×420 窗口：%s  子窗口文本=%s' % (describe(hwnd), kids_txt))
        # 连抓 3 帧、间隔 0.8s：两帧相同不足以判"没动"（可能恰好在同一帧边界），
        # 三帧全同才判 FAIL。MoveForm 是 16ms 帧，0.8s 间隔抓 3 次仍全同 = 定时器没跑。
        cap_fail = None
        for i in range(3):
            method, val = capture_hash(hwnd)
            if method is None:
                cap_fail = val
                break
            hashes.append((method, val))
            if i < 2:
                pump(0.8)
        if cap_fail is not None:
            anim = 'skip(%s)' % cap_fail
        else:
            uniq = len(set(h for _m, h in hashes))
            anim = 'diff' if uniq >= 2 else 'same'
            print('  抓图 3 帧：%s → 不同指纹 %d 个 → %s'
                  % ([h[:8] for _m, h in hashes], uniq, anim))
    else:
        print('  6s 内没有 480×420 窗口')

    if expect_window:
        ok = bool(wins)
        detail = ('遮罩 %.0fs；480×420=%s；动画=%s；动作名文本=%s'
                  % (t_ov, '有' if wins else '无', anim, kids_txt))
        results.append(('Movement %s' % label, ok, detail))
        # G2 单独一条（仅当窗口在时才判定，抓图失败判 SKIP）
        if wins:
            if anim == 'diff':
                results.append(('G2 演示窗内容在动', True,
                                '3 帧中不同指纹 %d 个（%s）'
                                % (len(set(h for _m, h in hashes)),
                                   hashes[0][0])))
            elif anim.startswith('skip'):
                results.append(('G2 演示窗内容在动', None,
                                'SKIP 抓图不可用（%s），不拿文本变化冒充动画' % anim))
            else:
                results.append(('G2 演示窗内容在动', False,
                                '连抓 3 帧（间隔 0.8s）像素指纹全同 → 画面没动'))
    else:
        ok = (not wins) and bool(overlay_windows())
        results.append(('Movement %s' % label, ok,
                        '遮罩 %.0fs 正常；480×420=%s（期望无）'
                        % (t_ov, '有' if wins else '无')))


def case_ritual(base_orig, results):
    """Ritual：普通护眼休息不该有 320×360；久坐休息应该有。"""
    print('\n--- Ritual：320×360 仪式窗只挂在 bodyBreak 上 ---')
    # ---- (a) 普通护眼休息：不该有 ----
    if not stop_eyelite():
        results.append(('Ritual 普通休息无小窗', False, '实例停不下来'))
    else:
        cfg = base_cfg(base_orig, **{'WorkMinutes': '1', 'RestSeconds': '40',
                                     'Plugin_Ritual': '1'})
        write_cfg(cfg)
        proc = launch()
        print('  (a) 普通护眼休息 WorkMinutes=1 RestSeconds=40 Plugin_Ritual=1')
        time.sleep(1.5)
        t_ov, _ = wait_overlay(110.0, tag='等普通休息遮罩')
        if t_ov is None:
            results.append(('Ritual 普通休息无小窗', False, '遮罩未出现'))
        else:
            _, wins = wait_pred(lambda: find_by_size(320, 360) or None, 4.0)
            print('  遮罩 %.0fs；320×360=%s' % (t_ov, '有' if wins else '无'))
            results.append(('Ritual 普通休息无小窗', not wins,
                            '遮罩 %.0fs，320×360=%s（期望无）'
                            % (t_ov, '有' if wins else '无')))

    # ---- (b) 久坐休息：应该有 ----
    if not stop_eyelite():
        results.append(('Ritual 久坐休息有小窗', False, '实例停不下来'))
        return
    cfg = body_break_cfg(base_orig, **{'Plugin_Ritual': '1'})
    write_cfg(cfg)
    proc = launch(sed_ms=FAKE_SED_MS)
    print('  (b) 久坐休息 SedMinutes=15 SedSeconds=20 Plugin_Ritual=1 '
          'EYE_LITE_FAKE_SED_MS=%d' % FAKE_SED_MS)
    time.sleep(1.5)
    t_ov, _ = wait_overlay(BODY_BREAK_TIMEOUT, tag='等久坐休息遮罩')
    if t_ov is None:
        results.append(('Ritual 久坐休息有小窗', None,
                        'SKIP %ds 内没等到久坐休息遮罩：EYE_LITE_FAKE_SED_MS '
                        '很可能没编进构建' % BODY_BREAK_TIMEOUT))
        return
    _, wins = wait_pred(lambda: find_by_size(320, 360) or None, 6.0)
    if wins:
        print('  320×360 窗口：%s' % describe(wins[0][0]))
    results.append(('Ritual 久坐休息有小窗', bool(wins),
                    '遮罩 %.0fs，320×360=%s（期望有）'
                    % (t_ov, '有' if wins else '无')))


def case_edge(base_orig, results):
    """EdgeGlow：剩 ≤60s 时应有整屏穿透层，进遮罩后消失；对照=关插件则没有。"""
    print('\n--- EdgeGlow：剩余 ≤60s 出现整屏穿透层，进遮罩后消失 ---')
    for on in (1, 0):
        if not stop_eyelite():
            results.append(('EdgeGlow', False, '实例停不下来'))
            return
        cfg = base_cfg(base_orig, **{'WorkMinutes': '1', 'RestSeconds': '8',
                                     'PreviewSeconds': '0',
                                     'Plugin_EdgeGlow': str(on)})
        write_cfg(cfg)
        proc = launch()
        print('  组=%s（Plugin_EdgeGlow=%d，DimAlphaMax=0 以便区分渐暗）'
              % ('插件开' if on else '对照/关', on))
        time.sleep(1.5)

        seen_glow = None
        t0 = time.time()
        while time.time() - t0 < 62.0:
            if transparent_fullscreen():
                seen_glow = time.time() - t0
                break
            if overlay_windows():
                break
            pump(0.15)
        print('    整屏穿透层首现=%s' % (('%.1fs' % seen_glow) if seen_glow else '无'))

        t_ov, _ = wait_overlay(70.0, tag='等遮罩')
        gone_after = None
        if t_ov is not None:
            pump(1.2)
            gone_after = not bool(transparent_fullscreen())
        print('    遮罩=%s，遮罩后穿透层已消失=%s'
              % (('%.1fs' % t_ov) if t_ov is not None else '未出现', gone_after))

        if on:
            ok = (seen_glow is not None) and gone_after is True
            results.append(('EdgeGlow 亮→灭', ok,
                            '穿透层 %.1fs 出现，遮罩 %.1fs，遮罩后消失=%s'
                            % (seen_glow or -1, t_ov or -1, gone_after)))
        else:
            ok = (seen_glow is None)
            results.append(('EdgeGlow 对照（关）', ok,
                            '整屏穿透层=%s（期望无）'
                            % (('%.1fs' % seen_glow) if seen_glow else '无')))


def case_e6(base_orig, results):
    """E6：StrictMode + LockCountdownSeconds=5 → 前 5s 不吞键、之后吞；对照=0 立刻吞。"""
    print('\n--- E6 锁前倒计时（StrictMode=1 + AllowSkip=1）---')
    for lcs, label in ((5, '倒计时5s'), (0, '对照0s')):
        if not stop_eyelite():
            results.append(('E6 %s' % label, False, '实例停不下来'))
            return
        cfg = base_cfg(base_orig, **{'WorkMinutes': '1', 'RestSeconds': '25',
                                     'StrictMode': '1', 'AllowSkip': '1',
                                     'LockCountdownSeconds': str(lcs)})
        write_cfg(cfg)
        proc = launch()
        print('  组=%s（LockCountdownSeconds=%d）' % (label, lcs))
        time.sleep(1.5)
        t_ov, _ = wait_overlay(110.0, tag='等遮罩')
        if t_ov is None:
            results.append(('E6 %s' % label, False, '遮罩未出现'))
            continue
        hwnd = overlay_windows()[0][0]
        # 子控件是 WinForms 里"显示时才建句柄"的，刚判定遮罩可见的那一刻去枚举
        # 可能一个都读不到（实测 E6 首跑就是 texts=[]）。这里给 3 秒重试窗口。
        texts = []
        hint = []
        t_txt = time.time()
        while time.time() - t_txt < 3.0:
            texts = child_texts(hwnd)
            hint = [t for t in texts if '秒后锁定' in t]
            if texts and (hint or lcs == 0):
                break
            pump(0.2)
        print('  遮罩 %.0fs；子窗口文本=%s%s'
              % (t_ov, texts, ('  倒计时文案=%s' % hint) if hint else ''))

        # 遮罩刚出现时立刻探（必须趁倒计时还没走完）
        early = probe(VK_F24)
        n_early = count_msgs(early, VK_F24)
        print('    倒计时内注入 F24 → 放行 %d 条' % n_early)

        if lcs > 0:
            pump(max(0.0, lcs + 1.5))
            late = probe(VK_F24)
            n_late = count_msgs(late, VK_F24)
            print('    倒计时结束后注入 F24 → 放行 %d 条' % n_late)
            ok = (n_early == 2) and (n_late == 0) and bool(hint)
            results.append(('E6 %s' % label, ok,
                            '倒计时内放行 %d 条（期望 2）、倒计时后 %d 条（期望 0）、'
                            '倒计时文案=%s' % (n_early, n_late, hint)))
        else:
            ok = (n_early == 0)
            results.append(('E6 %s' % label, ok,
                            '遮罩一出现就吞键：放行 %d 条（期望 0）' % n_early))


def case_stats(base_orig, results):
    """Stats：坐满一轮 StatDone+1；跳过一轮 StatSkip+1；对照=插件关则都不变。"""
    print('\n--- Stats 三态计数（写盘即刻，可读 config.ini 核对）---')

    def run_one(plugin_on, do_skip):
        if not stop_eyelite():
            return None
        cfg = base_cfg(base_orig, **{'WorkMinutes': '1', 'RestSeconds': '8',
                                     'Plugin_Stats': str(plugin_on),
                                     'StatDone': '0', 'StatSkip': '0'})
        write_cfg(cfg)
        proc = launch()
        time.sleep(1.5)
        t_ov, _ = wait_overlay(110.0, tag='等遮罩')
        if t_ov is None:
            return None
        if do_skip:
            hwnd = overlay_windows()[0][0]
            btn = find_child_contains(hwnd, '跳过')
            if btn is None:
                return None
            click_child(btn[0])
            wait_overlay_gone(5.0)
        else:
            # 「坐满一轮」必须**等休息自然结束**（RestSeconds 到期 → EndRest(true)
            # → CountStatDone）。首跑这里漏了等待、遮罩出现 1s 就读 config，
            # StatDone 当然还是 0 —— 那是我的 bug，不是产品不计数。
            wait_overlay_gone(30.0)
        pump(1.2)
        d = read_cfg()
        return (int(d.get('StatDone', '-1') or -1),
                int(d.get('StatSkip', '-1') or -1))

    done_after = run_one(1, False)
    print('  坐满一轮后：StatDone=%s StatSkip=%s'
          % (done_after if done_after else 'n/a'))
    skip_after = run_one(1, True)
    print('  跳过一轮后：StatDone=%s StatSkip=%s'
          % (skip_after if skip_after else 'n/a'))
    ctl = run_one(0, False)
    print('  对照（插件关）坐满一轮后：StatDone=%s StatSkip=%s'
          % (ctl if ctl else 'n/a'))

    if done_after is None or skip_after is None or ctl is None:
        results.append(('Stats 计数', False, '有子用例没跑起遮罩，无法判定'))
        return
    ok = (done_after[0] == 1 and done_after[1] == 0
          and skip_after[0] == 0 and skip_after[1] == 1
          and ctl == (0, 0))
    results.append(('Stats 计数', ok,
                    '坐满→(done=%d,skip=%d)、跳过→(done=%d,skip=%d)、对照→(done=%d,skip=%d)'
                    % (done_after[0], done_after[1], skip_after[0], skip_after[1],
                       ctl[0], ctl[1])))


def case_e1(base_orig, results):
    """E1：配额 2 次；第 3 次置灰且点击无效；且配额用完时 Esc 不被吞（非严格档）。"""
    print('\n--- E1 跳过配额（SkipQuotaPerDay=2）---')
    for guard in (1, 0):
        if not stop_eyelite():
            results.append(('E1 配额', False, '实例停不下来'))
            return
        cfg = base_cfg(base_orig, **{'WorkMinutes': '1', 'RestSeconds': '10',
                                     'AllowSkip': '1', 'StrictMode': '0',
                                     'Plugin_Guard': str(guard),
                                     'SkipQuotaPerDay': '2',
                                     'SkipQuotaDate': today_str(),
                                     'SkipQuotaUsed': '0'})
        write_cfg(cfg)
        proc = launch()
        print('  组=%s（Plugin_Guard=%d，配额 2）' % ('插件开' if guard else '对照/关', guard))
        time.sleep(1.5)

        skips = 0
        third_text = None
        third_still = None
        esc_msgs = None
        for rnd in (1, 2, 3):
            t_ov, _ = wait_overlay(110.0, tag='第%d轮等遮罩' % rnd)
            if t_ov is None:
                print('    第%d轮遮罩未出现，停止' % rnd)
                break
            hwnd = overlay_windows()[0][0]
            btn = find_child_contains(hwnd, '跳过')
            if rnd == 3:
                third_text = btn[2] if btn else '(找不到跳过按钮)'
                print('    第3轮「跳过」按钮文本=%r' % third_text)
                if btn is not None:
                    click_child(btn[0])
                    still = wait_overlay_gone(2.5)
                    third_still = (still is None)
                    print('      点击后遮罩仍在=%s' % third_still)
                # 配额用完 → Esc 应不被吞（用的是观察钩子）
                esc_msgs = probe(VK_ESCAPE, wait=0.8)
                print('      Esc 放行 %d 条（期望 2 = down+up 都到达）'
                      % count_msgs(esc_msgs, VK_ESCAPE))
                # 收尾：用推迟离开（避免影响下一组）
                pp = find_child_contains(hwnd, '推迟')
                if pp is not None:
                    click_child(pp[0])
                wait_overlay_gone(5.0)
                break
            if btn is None:
                print('    第%d轮找不到跳过按钮' % rnd)
                break
            click_child(btn[0])
            gone = wait_overlay_gone(5.0)
            print('    第%d轮点击跳过 → 遮罩%s' % (rnd, '消失' if gone else '仍在'))
            if gone is None:
                break
            skips += 1

        if guard:
            esc_n = count_msgs(esc_msgs or [], VK_ESCAPE)
            ok = (skips == 2) and (third_text is not None)
            if third_text:
                ok = ok and ('已用完' in third_text)
            ok = ok and (third_still is True) and (esc_n == 2)
            detail = ('前两次跳过成功=%s；第3次按钮=%r、点击后遮罩仍在=%s；'
                      '配额用完时 Esc 放行 %d 条（期望 2）'
                      % (skips == 2, third_text, third_still, esc_n))
        else:
            # 对照：配额关掉后第 3 次仍应能跳过（插件关 → SkipAllowed 恒 true）
            ok = (skips == 2) and (third_still is False)
            detail = ('对照：前两次成功=%s、第3次点击后遮罩仍在=%s（期望 False）'
                      % (skips == 2, third_still))
        results.append(('E1 配额（%s）' % ('插件开' if guard else '对照/关'),
                        ok, detail))


def case_settings(results):
    """设置页「扩展」卡：只观察已打开的设置窗（不自己开窗，避免抢前台）。"""
    print('\n--- 设置页「扩展」卡（只观察已打开的窗口）---')
    cand = []
    for hwnd, w, h, pid in own_windows():
        if pid in _pid_cache and w > 400 and h > 300 and not overlay_windows():
            texts = child_texts(hwnd)
            if any('扩展' in t for t in texts):
                cand.append((hwnd, w, h, texts))
    if not cand:
        print('  没有找到已打开的设置窗 → 不自己开窗（会抢前台，违反纪律）')
        results.append(('设置页扩展卡', None,
                        'SKIP 需要先手动打开设置页再重跑本项'
                        '（枚举不到含「扩展」的设置窗）'))
        return
    hwnd, w, h, texts = cand[0]
    plugins = [t for t in texts if t in ('Stats', 'EdgeGlow', 'Ritual', 'Movement',
                                         'Guard', 'Sound')]
    pending = [t for t in texts if '待测' in t]
    ok = bool(plugins) and bool(pending)
    results.append(('设置页扩展卡', ok,
                    '窗口 %dx%d；插件名=%s；「待测」标注 %d 处' % (w, h, plugins, len(pending))))


# ---------------------------------------------------------------- main


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at %s' % EXE)
        return 2
    print('被测产物: %s (%d bytes)' % (EXE, os.path.getsize(EXE)))

    want = [a.lower() for a in sys.argv[1:]] or ['all']
    plan = (['edge', 'e6', 'stats', 'e1', 'ritual', 'movement', 'movementctl',
             'settings']
            if 'all' in want else want)

    if not install_observer():
        print('FAIL: 观察钩子装不上 → 无法验证 E1 的 Esc 与 E6 的吞键，不产出结论')
        return 2
    print('observer hook installed (WH_KEYBOARD_LL)，前置于 EyeLite 的钩子')
    got = probe(VK_F24)
    if count_msgs(got, VK_F24) < 2:
        print('instrument selfcheck: FAIL 注入 F24 只看到 %d 条' % count_msgs(got, VK_F24))
        uninstall_observer()
        return 2
    print('instrument selfcheck: PASS（F24 down/up 均被 observer 看到）')

    if not stop_eyelite():
        print('FAIL: 有残留 EyeLite 进程，无法开始')
        uninstall_observer()
        return 2

    backup_raw = read_cfg_raw()
    orig = read_cfg()
    print('orig config: WorkMinutes=%s SedentaryMinutes=%s RestSeconds=%s'
          % (orig.get('WorkMinutes'), orig.get('SedentaryMinutes'),
             orig.get('RestSeconds')))

    results = []
    try:
        if 'edge' in plan:
            case_edge(orig, results)
        if 'e6' in plan:
            case_e6(orig, results)
        if 'stats' in plan:
            case_stats(orig, results)
        if 'e1' in plan:
            case_e1(orig, results)
        if 'ritual' in plan:
            case_ritual(orig, results)
        if 'movement' in plan:
            case_movement(orig, results, True, '', 'G1 无禁忌', True)
            case_movement(orig, results, True, '腰椎', 'G11 腰椎', True)
            case_movement(orig, results, True, '腰椎;脊柱;膝;髋;颈椎;肩袖;肩;腕;腱鞘;腕管',
                          'G11 全部排除', False)
        if 'movementctl' in plan:
            case_movement(orig, results, False, '', '对照 Plugin=0', False)
        if 'settings' in plan:
            case_settings(results)
    finally:
        stop_eyelite()
        uninstall_observer()
        cleanup_probe()
        restore_cfg_raw(backup_raw)

    print()
    for name, ok, detail in results:
        verdict = 'SKIP' if ok is None else ('PASS' if ok else 'FAIL')
        print('%-30s %s  %s' % (name, verdict, detail))
    print('%-30s %s  %s' % ('压暗层/穿透层 DimForm（信息项）',
                            'yes' if DIM_SEEN[0] else 'no',
                            '出现过；DimForm 与 EdgeGlow 结构相同，靠配置区分'))

    judged = [r for r in results if r[1] is not None]
    skipped = [r for r in results if r[1] is None]
    npass = sum(1 for r in judged if r[1])
    print('\n=== P1-批2/3 summary: %d/%d PASS%s ==='
          % (npass, len(judged),
             ('  + %d SKIP（不计 PASS）' % len(skipped)) if skipped else ''))
    for name, ok, detail in judged:
        if not ok:
            print('  FAILED: %s (%s)' % (name, detail))
    for name, ok, detail in skipped:
        print('  SKIPPED: %s (%s)' % (name, detail))
    if skipped:
        return 2
    return 0 if npass == len(judged) else 1


def cleanup_probe():
    """本脚本不写临时文件；只清掉别的脚本可能留下的空目录。"""
    try:
        if os.path.isdir(PROBE_DIR) and not os.listdir(PROBE_DIR):
            os.rmdir(PROBE_DIR)
    except Exception:
        pass


if __name__ == '__main__':
    sys.exit(main())
