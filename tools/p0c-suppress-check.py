#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-C 真机实测（钩子链判定 + QUNS 文件钩子）：抑制闸门
    P0-C-7  抑制期「不装键盘钩子、不吞按键」
    P0-C-8  抑制解除（QUNS 4→0）后「立刻补弹」，且不重复计时
    P0-C-9  抑制持续到上限后「强制放行」（遮罩真的弹出来）

## P0-C-7：怎么判定"没装钩子"
抑制期（QUNS=4）EyeLite 根本不进休息，也就不会装 KeyboardGuard。但"没装钩子"是
**关于隐藏机制的反向断言**，低级键盘钩子不可枚举，没法直接数。所以拆成两条可观测事实：

  (1) 抑制期全程没有横幅、没有遮罩（产品没进入"会装钩子"的状态）；
      代码上钩子只在 BeginRest 里装，与 (1) 互为印证。
  (2) 抑制期注入的按键**全部放行**（down/up 成对穿过）。

为了让 (2) 不是"仪器瞎了"的假通过，脚本内跑一个**对照组 TC**：同样的
StrictMode=1 + AllowSkip=1，只把 QUNS 换成 0（不抑制），等遮罩出来再注入同一个
F24 —— 此时必须被吞（observer 0 条）。先证明仪器**看得见吞键**，再证明抑制期
**看不见吞键**，结论才成立（P0-E T3 的同一套方法论）。
TC 同时充当 C-8/C-9 的前置自检：它证明"这台机器 + 这个二进制 + 这套判据"
确实能枚举到遮罩、且到点时刻稳定（≈61s）。TC 不通过 → 不产出 C-8/C-9 结论。

## P0-C-8：为什么必须是"改文件→遮罩"的毫秒级延迟
旧 build 里 `EYE_LITE_FORCE_QUNS` 在类型静态构造时读一次就冻结（进程起来后
改不了环境变量），所以 4→0 的跃迁构造不出来。新 build 加了文件钩子
`EYE_LITE_FORCE_QUNS_FILE`：**每次 Query() 重读文件内容**（≈1 次/秒），
于是可以在同一个进程内把 4 改成 0。

判据不是"遮罩最终出现过"，而是**从改文件到遮罩出现的延迟**：
  · 延迟 ≤5s          → 抑制解除后立刻补弹（累加器一直在走，到点判定马上成立）
  · 延迟接近 60s      → 说明是"重新计时"，本轮账丢了 → FAIL
所以阈值取 5s / 10s 两个刻度，中间的空白区域本身就否证了"重新计时"。

## P0-C-9：上限放行
`EYE_LITE_SUPPRESS_MAX_MS` 可以把"抑制最多持续多久"从 30 分钟压到 60 秒。
判定窗口取 55~95s：到点 60s 与上限 60s 大致重合，两者谁先都不会太偏。
只验**第一次放行**：休息结束后 QUNS 仍是 4，会重新开始一轮抑制并重新给足上限
（这是预期行为），所以不把"之后又没提醒了"算失败。
断言里额外加"遮罩不抖动"：连续可见 ≥6s，且消失后 6s 内不再重开。

## 仪器细节（沿用 p0e-input-check.py，坑已踩过）
  · 低级键盘钩子按「最后安装的最先调用」执行，任一返回非 0 则链终止。
    本脚本在启动 EyeLite **之前**装 observer，于是 EyeLite 休息期装的钩子排在
    observer 之前（先被调用）：observer 收到 = 放行，收不到 = 被吞。
  · `GetModuleHandleW` 必须显式 `restype = HMODULE`，否则 64 位句柄被 32 位截断，
    `SetWindowsHookEx` 报 err=126 ERROR_MOD_NOT_FOUND。
  · 钩子回调里只做记录：超过 LowLevelHooksTimeout 会被系统静默摘钩。
  · 判定按键必须匹配 `LLKHF_INJECTED`，把用户真手按的键排除掉。
  · 注入只用 VK_F24（不产生字符）与裸 Ctrl（不产生字符、但能验"卡键"）。
  · 遮罩/横幅判定必须按 PID 过滤：Windows 桌面窗口（Program Manager，3840×2160）
    本身就是可见顶层窗口，不按 PID 过滤会被当成遮罩。PID 用 Popen.proc.pid 缓存，
    轮询里绝不调 tasklist（每次 0.3~1s，会把观测节奏拖垮）。
  · 对照组的"假半吞"：遮罩可见的时刻早于 KeyboardGuard.Install() 完成。
    若在遮罩出现后 ~80ms 就注入，F24 的 down 会抢在钩子安装之前穿过去，
    观测到"1 条"这种假半吞。修法：先泵 1.2s，打一次**不计分的预热探针**，再计分。
  · **压暗层（DimForm）不是遮罩**：新 build 加了 A2「休息前渐暗」，它铺满整块屏幕、
    尺寸同样 w>800 h>500，只按尺寸判会把启动后 1.7s 出现的压暗层当成遮罩
    （首跑就把 C-8/C-9 判成 FAIL，其实是仪器瞎判）。区分依据：
    压暗层有 WS_EX_TRANSPARENT（鼠标穿透）且**无子窗口**；遮罩里有 Label/环形/按钮。
    现在判据是「大尺寸 + 非 TRANSPARENT + 子窗口 ≥1」，压暗层单独做信息性观测。

## 临时文件
QUNS 文件放在 `tools/_probe/quns.txt`（不碰 %TEMP%、不碰用户目录），结束删除。

## 用法
    python tools/p0c-suppress-check.py           # 全部（约 6 分钟）
    python tools/p0c-suppress-check.py c8        # 只跑 C-8（含前置自检 + TC）
    python tools/p0c-suppress-check.py c8 c9     # 只跑 C-8 / C-9
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')
PROBE_DIR = os.path.join(ROOT, 'tools', '_probe')
QUNS_FILE = os.path.join(PROBE_DIR, 'quns.txt')

SUPPRESS_MAX_ENV = 'EYE_LITE_SUPPRESS_MAX_MS'
QUNS_FILE_ENV = 'EYE_LITE_FORCE_QUNS_FILE'

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020

# ---------------------------------------------------------------- 常量
WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
LLKHF_INJECTED = 0x10
PM_REMOVE = 0x0001
KEYEVENTF_KEYUP = 0x0002

VK_CONTROL = 0x11
VK_LCONTROL = 0xA2          # keybd_event(0x11) 实际产出 0xA2，只比 0x11 会漏掉全部消息
VK_SPACE = 0x20
VK_ESCAPE = 0x1B
VK_F24 = 0x87               # 不产生字符的注入键

CTRL_VKS = (VK_CONTROL, VK_LCONTROL)

LRESULT = ctypes.c_ssize_t
WPARAM = ctypes.c_size_t
LPARAM = ctypes.c_ssize_t


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ('vkCode', wt.DWORD),
        ('scanCode', wt.DWORD),
        ('flags', wt.DWORD),
        ('time', wt.DWORD),
        ('dwExtraInfo', ctypes.c_size_t),
    ]


HOOKPROC = ctypes.WINFUNCTYPE(LRESULT, ctypes.c_int, WPARAM, LPARAM)

_seen = []          # [(wParam, vkCode, injected)]
_hhook = [None]


def _hook_cb(nCode, wParam, lParam):
    """回调里只记录，绝不做耗时操作（超时会被系统摘钩）。"""
    if nCode == 0:
        try:
            kb = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
            _seen.append((int(wParam), int(kb.vkCode),
                          bool(kb.flags & LLKHF_INJECTED)))
        except Exception:
            pass
    return user32.CallNextHookEx(_hhook[0], nCode, wParam, lParam)


_HOOK_REF = HOOKPROC(_hook_cb)


def install_observer():
    # 所有返回句柄的 API 都必须显式声明 restype，否则 64 位句柄被 32 位截断，
    # SetWindowsHookEx 收到残句柄 → err=126 ERROR_MOD_NOT_FOUND。本机踩过两次。
    kernel32.GetModuleHandleW.restype = wt.HMODULE
    kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]
    user32.SetWindowsHookExW.restype = wt.HANDLE
    user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wt.HMODULE, wt.DWORD]
    user32.CallNextHookEx.restype = LRESULT
    user32.CallNextHookEx.argtypes = [wt.HANDLE, ctypes.c_int, WPARAM, LPARAM]

    hinst = kernel32.GetModuleHandleW(None)
    if not hinst:
        print('  GetModuleHandleW failed, err=%d' % ctypes.get_last_error())
        return False
    _hhook[0] = user32.SetWindowsHookExW(WH_KEYBOARD_LL, _HOOK_REF, hinst, 0)
    if not _hhook[0]:
        print('  SetWindowsHookEx err=%d (hinst=%s)'
              % (ctypes.get_last_error(), hinst))
    return bool(_hhook[0])


def uninstall_observer():
    if _hhook[0]:
        try:
            user32.UnhookWindowsHookEx(_hhook[0])
        except Exception:
            pass
        _hhook[0] = None


def pump(seconds):
    """泵消息，让装钩子的线程真正收到低级钩子回调。不泵就永远收不到。"""
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


def is_down(vk):
    return (user32.GetAsyncKeyState(vk) & 0x8000) != 0


def release_all():
    """兜底：把可能被吞掉 keyup 的键全部补一次 up，别给用户留"卡住的键"。"""
    keys = [VK_CONTROL, VK_LCONTROL, 0x10, 0xA0, 0x12, 0xA4, 0x5B, 0x14, VK_SPACE, VK_ESCAPE]
    for _ in range(10):
        if not any(is_down(k) for k in keys):
            return True
        for k in keys:
            user32.keybd_event(k, 0, KEYEVENTF_KEYUP, 0)
        time.sleep(0.05)
    return not any(is_down(k) for k in keys)


def probe(seq, wait=0.55):
    """注入键序列，返回 observer 期间看到的**注入**消息。"""
    pump(0.08)
    start = len(_seen)
    for vk in seq:
        key_tap(vk)
    pump(wait)
    return [m for m in _seen[start:] if m[2]]


def count_passed(msgs, vk):
    return len([m for m in msgs if m[1] == vk
                and m[0] in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP)])


def _msg_desc(msgs):
    """把 observer 收到的消息翻译成 down/up 序列，半吞时一眼能看出是哪一条漏了。"""
    name = {WM_KEYDOWN: 'down', WM_KEYUP: 'up',
            WM_SYSKEYDOWN: 'sdown', WM_SYSKEYUP: 'sup'}
    return '[%s]' % ' '.join(name.get(m[0], hex(m[0])) for m in msgs) if msgs else '[]'


def pairing(msgs, vks):
    """返回 (成对?, 描述)。合法结果只有两种：0 条全吞，或 2 条且 down/up 各 1。

    只放行 down 不放行 up（或反之）会让应用侧认为键一直按着 —— 这正是用户报过的
    「Ctrl 卡住」的形态，所以半吞一律算 FAIL。
    """
    sel = [m for m in msgs if m[1] in vks]
    downs = len([m for m in sel if m[0] in (WM_KEYDOWN, WM_SYSKEYDOWN)])
    ups = len([m for m in sel if m[0] in (WM_KEYUP, WM_SYSKEYUP)])
    n = len(sel)
    ok = (n == 0) or (n == 2 and downs == 1 and ups == 1)
    return ok, 'down=%d up=%d 共%d条' % (downs, ups, n)


# ---------------------------------------------------------------- 窗口


def list_windows():
    out = []
    Enum = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

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

    user32.EnumWindows(Enum(cb), 0)
    return out


_pid_cache = set()

# ---- 提速开关（2026-11-15）----
# p0c 的 C-7 / C-8 判据都是**布尔**的（抑制期"有没有"提醒、键"有没有"被吞；
# 解除后"多久"补弹是 C-8 的数值判据，仍然保留真实测量）。
# 外部注入 EYE_LITE_FAKE_WORK_MS=59000 → 第一个 tick 工作累计就到 59s，
# 到点只差 1s，于是只需十几秒就"越过到点"，等价于不注入时看 95s。
# **C-9（抑制上限 60s 强制放行）必须真实等 60s，不参与压缩。**
_FAKE_WORK = os.environ.get('EYE_LITE_FAKE_WORK_MS', '').strip()
try:
    _FAKE_WORK_OK = int(_FAKE_WORK) >= 0
except Exception:
    _FAKE_WORK_OK = False

C7_CHECKPOINTS = (5.0, 9.0, 13.0) if _FAKE_WORK_OK else (35.0, 68.0, 92.0)
C7_WATCH_S = 15.0 if _FAKE_WORK_OK else 95.0
C8_PRE_FLIP_S = 10.0 if _FAKE_WORK_OK else 95.0


def own_windows():
    if not _pid_cache:
        return []
    return [t for t in list_windows() if t[3] in _pid_cache]


def preview_present():
    return bool([t for t in own_windows() if 200 < t[1] <= 900 and t[2] <= 300])


def child_count(hwnd):
    n = [0]
    Enum = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, Enum(cb), 0)
    return n[0]


def _is_transparent(hwnd):
    return bool(user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT)


def overlay_windows():
    """休息遮罩：可见 + 大尺寸 + **不是压暗层**，且**必须是 EyeLite 自己的窗口**。

    新 build 加了 DimForm（A2 休息前渐暗）：它是铺满整块屏幕的黑色窗口，
    尺寸同样 w>800 h>500，只看尺寸会把它当成遮罩 —— 表现为"启动 1.7s 就弹遮罩"，
    是典型的"仪器缺陷伪装成产品缺陷"（本机第 3 次）。
    两者的可分辨特征（已核对源码 Program.cs:2146-2196 vs 1264-1320）：
      · DimForm 有 WS_EX_TRANSPARENT（鼠标穿透）且**没有任何子窗口**；
      · RestForm 会 Add 一排 Label / RingMeter / 按钮（子窗口 ≥1），
        因为要显示"休息一下"、环形倒计时和出口按钮。
    所以判据是：大尺寸 + 不是 TRANSPARENT 层 + 至少 1 个子窗口。
    """
    out = []
    for hwnd, w, h, pid in own_windows():
        if not (w > 800 and h > 500):
            continue
        if _is_transparent(hwnd):
            continue
        if child_count(hwnd) < 1:
            continue
        out.append((hwnd, w, h, pid))
    return out


def overlay_present():
    return bool(overlay_windows())


def dim_windows():
    """压暗层（DimForm）：大尺寸 + TRANSPARENT + 无子窗口。只做信息性观测。

    它不属于本次用例的判定对象，但抑制期/休息期出现压暗层同样是打扰，
    所以记下来供报告参考（不参与 PASS/FAIL）。
    """
    return [(hwnd, w, h) for hwnd, w, h, _pid in own_windows()
            if w > 800 and h > 500 and _is_transparent(hwnd)
            and child_count(hwnd) == 0]


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
        pump(0.3)
    return False


def launch(quns=None, env_extra=None):
    """启动 EyeLite 并缓存 PID。

    所有测试钩子的环境变量先全部清掉再按需设置，避免从父 shell 继承到
    上一次手工测试留下的值（那会让"文件钩子没生效"这类问题极难定位）。
    """
    env = os.environ.copy()
    for k in ('EYE_LITE_FORCE_QUNS', QUNS_FILE_ENV, SUPPRESS_MAX_ENV):
        env.pop(k, None)
    if quns is not None:
        env['EYE_LITE_FORCE_QUNS'] = str(quns)
    if env_extra:
        env.update(env_extra)
    proc = subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _pid_cache.add(proc.pid)        # PID 缓存：轮询里绝不调 tasklist
    return proc


# ---------------------------------------------------------------- 配置


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
    order = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen']
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in order:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:                      # 未知键也写回，不能弄丢用户配置
            if k not in order:
                f.write('%s=%s\n' % (k, d[k]))


# ---------------------------------------------------------------- QUNS 文件钩子


def ensure_probe_dir():
    if not os.path.isdir(PROBE_DIR):
        try:
            os.makedirs(PROBE_DIR)
        except Exception as e:
            print('  创建 _probe 目录失败:', e)


def write_quns(v):
    """把 QUNS 文件改成 v，返回**回读**到的内容（写进去但读不回来等于没写）。

    不写 BOM（用 encoding='utf-8'）；产品侧也剥了 BOM，两边都稳。
    """
    ensure_probe_dir()
    with open(QUNS_FILE, 'w', encoding='utf-8') as f:
        f.write('%d\n' % v)
    return read_quns()


def read_quns():
    try:
        with open(QUNS_FILE, 'r', encoding='utf-8-sig') as f:
            return f.read().strip()
    except Exception as e:
        return 'ERR:%s' % e


def cleanup_probe():
    try:
        os.remove(QUNS_FILE)
    except Exception:
        pass
    try:
        os.rmdir(PROBE_DIR)          # 只在空目录时成功，别人的文件不会被删
    except Exception:
        pass


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


def user_idle_ms():
    li = LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(li)
    if not user32.GetLastInputInfo(ctypes.byref(li)):
        return 0
    return int(kernel32.GetTickCount() - li.dwTime)


def wait_user_idle(threshold_ms=1000, tries=3):
    for _ in range(tries):
        idle = user_idle_ms()
        if idle >= threshold_ms:
            return True
        print('  用户刚有输入（idle=%dms），等 3 秒再开始' % idle)
        pump(3.0)
    return False


# ---------------------------------------------------------------- 观测


class Timeline(object):
    """从 t0 起持续轮询横幅/遮罩，记录首次出现时刻与**状态跃迁**。

    跃迁表是给失败场景用的：C-9 一旦"到上限仍不弹"，这张表就是直接证据
    （第几秒有过什么、之后一直是什么）。
    """

    def __init__(self, tag=''):
        self.t0 = time.time()
        self.tag = tag
        self.first_preview = None
        self.first_overlay = None
        self.first_dim = None
        self.events = []
        self._state = (False, False)
        self._hb = 0.0

    def tick(self, seconds=0.15):
        pump(seconds)
        now = time.time() - self.t0
        pv = preview_present()
        ov = overlay_present()
        dim = bool(dim_windows())
        if pv and self.first_preview is None:
            self.first_preview = now
            self.events.append((now, 'preview ON', ''))
        if ov and self.first_overlay is None:
            self.first_overlay = now
            self.events.append((now, 'overlay ON', ''))
        if dim and self.first_dim is None:
            self.first_dim = now
            self.events.append((now, 'dim ON', ''))
        if (pv, ov) != self._state:
            if self._state[0] and not pv:
                self.events.append((now, 'preview off', ''))
            if self._state[1] and not ov:
                self.events.append((now, 'overlay off', ''))
            self._state = (pv, ov)
        return now

    def until(self, t, hb=15.0):
        while True:
            now = self.tick()
            if now >= t:
                return now
            if now - self._hb >= hb:
                self._hb = now
                print('    %s%.0fs  preview=%s overlay=%s dim=%s'
                      % (self.tag, now,
                         'YES' if self._state[0] else 'no',
                         'YES' if self._state[1] else 'no',
                         'YES' if dim_windows() else 'no'))

    def earlier_than(self, t):
        """跑到 t 秒或遮罩出现为止（谁先到）。"""
        while True:
            now = self.tick()
            if now >= t or self.first_overlay is not None:
                return now
            if now - self._hb >= 15.0:
                self._hb = now
                print('    等遮罩... %.0fs' % now)

    def dump(self):
        if not self.events:
            print('      （无任何状态跃迁：横幅与遮罩全程都不可见）')
            return
        for t, what, _ in self.events:
            print('      %7.2fs  %s' % (t, what))


def wait_overlay(timeout_s):
    """等遮罩并保持泵消息（不泵就收不到钩子回调）。返回秒数或 None。"""
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if overlay_present():
            return time.time() - t0
        pump(0.08)
    return None


def wait_overlay_gone(timeout_s):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if not overlay_present():
            return time.time() - t0
        pump(0.08)
    return None


# ---------------------------------------------------------------- 用例


def case_c7(results, base):
    """P0-C-7 抑制期不装钩子、不吞按键（QUNS=4）。

    配置故意用 StrictMode=1 + AllowSkip=1 —— 这是"一旦进休息就会全吞键盘"的组合。
    如果抑制期还能看见钩子在吞键，说明 gate 没拦住休息；现在要求全程放行。
    """
    print('\n--- P0-C-7 抑制期不吞按键（QUNS=4）---')
    if not stop_eyelite():
        results.append(('C7 抑制期不吞键', False, 'EyeLite 实例停不下来'))
        return False
    cfg = dict(base)
    cfg.update({'PreviewSeconds': '30', 'StrictMode': '1',
                # E6 之后严格模式会先给 N 秒自由时间；本用例要验的是
                # '装了钩子之后确实吞键'，所以必须把倒计时设为 0，
                # 否则注入落在倒计时窗口内会被放行，看起来像产品没吞键。
                'LockCountdownSeconds': '0'})
    write_cfg(cfg)
    launch(quns=4)
    tl = Timeline()

    # 时间点选择：预告窗口本该在 ~30s 开，遮罩本该在 ~60s 出。
    # 35s / 68s / 92s 三个点分别覆盖"抑制掉预告"、"抑制掉遮罩"、"越过到点之后"。
    #
    # **提速**：外部注入 `EYE_LITE_FAKE_WORK_MS=59000` 时，第一个 tick 就把工作累计
    # 推到 59s → 到点只差 1s，于是 5/9/13s 三个点等价于原来的 35/68/92s
    # （C-7 的判据是布尔：抑制期"有没有"横幅/遮罩 + 键"有没有"被吞，与"第几秒"无关）。
    # 未注入时保持原检查点，行为与旧版一致。
    probes = []
    for cp in (C7_CHECKPOINTS if _FAKE_WORK_OK else (35.0, 68.0, 92.0)):
        tl.until(cp)
        print('  %.0fs：preview=%s overlay=%s  注入 F24/Ctrl'
              % (cp, 'YES' if tl.first_preview is not None else 'no',
                 'YES' if tl.first_overlay is not None else 'no'))
        m_f24 = probe([VK_F24])
        n_f24 = count_passed(m_f24, VK_F24)
        m_ctrl = probe([VK_CONTROL])
        ok_ctrl, desc_ctrl = pairing(m_ctrl, CTRL_VKS)
        probes.append((cp, n_f24, ok_ctrl, desc_ctrl))
        print('        F24 放行 %d 条（期望 2）、Ctrl %s（期望成对）'
              % (n_f24, desc_ctrl))
        release_all()

    tl.until(C7_WATCH_S)

    no_banner = tl.first_preview is None
    no_overlay = tl.first_overlay is None
    all_passed = all(p[1] == 2 for p in probes)
    ctrl_ok = all(p[2] for p in probes)
    detail = ('横幅=%s 遮罩=%s（%.0fs 内）；F24 放行 %s（每次期望 2）；Ctrl %s；压暗层=%s'
              % ('无' if no_banner else '有@%.1fs' % tl.first_preview,
                 '无' if no_overlay else '有@%.1fs' % tl.first_overlay,
                 C7_WATCH_S,
                 '/'.join(str(p[1]) for p in probes),
                 '全部成对' if ctrl_ok else '存在半吞',
                 '无' if tl.first_dim is None else '有@%.1fs（信息项，不参与判定）'
                 % tl.first_dim))
    ok = no_banner and no_overlay and all_passed and ctrl_ok
    results.append(('C7 抑制期不吞键', ok, detail))
    return ok


def case_control(results, base):
    """对照组 TC：QUNS=0（不抑制）→ 必须看得见吞键，且必须枚举到遮罩。

    它同时是 C-8/C-9 的**仪器前置自检**：
      · 能枚举到遮罩（遮罩判据在这台机器/这个二进制上有效）
      · 到点时刻稳定（≈61s，说明 config 与计时链路正常）
    不计入 PASS 统计，但失败会让 C-7/C-8/C-9 都没有立论基础。
    """
    print('\n--- TC 对照组：QUNS=0 休息期应吞键（仪器前置自检）---')
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    # 必须自己写配置：TC 跑在所有用例之前，此时 config.ini 还是用户原值
    # （WorkMinutes=20），不写就会等 20 分钟才出遮罩。
    cfg = dict(base)
    cfg.update({'PreviewSeconds': '30', 'StrictMode': '1',
                # E6 之后严格模式会先给 N 秒自由时间；本用例要验的是
                # '装了钩子之后确实吞键'，所以必须把倒计时设为 0，
                # 否则注入落在倒计时窗口内会被放行，看起来像产品没吞键。
                'LockCountdownSeconds': '0'})
    write_cfg(cfg)
    launch(quns=0)
    t = wait_overlay(110.0)
    if t is None:
        print('  110s 内遮罩未出现：遮罩判据/计时链路有问题，仪器不可信')
        return None
    print('  遮罩 %.1fs 出现（StrictMode=1 + AllowSkip=1 → 应全吞）' % t)
    # **必须等钩子装稳再判**：遮罩可见的时刻早于 KeyboardGuard.Install() 完成
    # （BeginRest 里先建遮罩再装钩子）。实测第一次在遮罩出现后 ~80ms 就注入，
    # F24 的 down 抢在钩子安装之前穿了过去，只有 up 被吞 → 观测到"1 条"这种
    # 假半吞。先泵 1.2s 让钩子就位，再打一次不计分的预热探针，然后才计分。
    pump(1.2)
    m_warm = probe([VK_F24])
    print('  预热探针（不计分）：%d 条 %s'
          % (count_passed(m_warm, VK_F24), _msg_desc(m_warm)))
    m = probe([VK_F24])
    n = count_passed(m, VK_F24)
    print('  休息期 F24 放行 %d 条（期望 0 = 被吞）%s' % (n, _msg_desc(m)))
    probe([VK_ESCAPE])          # 严格档下 Esc 是出口
    pump(1.0)
    release_all()
    return n


def case_c8(results, base):
    """P0-C-8 抑制解除后立刻补弹、不重复计时（4→0，靠 QUNS 文件钩子驱动）。"""
    print('\n--- P0-C-8 抑制解除（QUNS 4→0）后立刻补弹、且不重复计时 ---')
    if not stop_eyelite():
        results.append(('C8 解除后补弹', False, 'EyeLite 实例停不下来'))
        return
    got = write_quns(4)
    if got != '4':
        results.append(('C8 解除后补弹', False,
                        '仪器自检失败：QUNS 文件写入后回读为 %r' % got))
        return
    print('  QUNS 文件 %s ← 写入 %r（回读 %r）' % (QUNS_FILE, '4\n', got))

    cfg = dict(base)
    cfg.update({'PreviewSeconds': '0', 'RestSeconds': '8', 'StrictMode': '0'})
    write_cfg(cfg)
    launch(env_extra={QUNS_FILE_ENV: QUNS_FILE})
    print('  已启动：%s=%s（WorkMinutes=1 PreviewSeconds=0 RestSeconds=8）'
          % (QUNS_FILE_ENV, QUNS_FILE))

    # ---- 阶段一：抑制一段时间，到点已过，必须毫无提醒 ----
    # 提速：注入 FAKE_WORK_MS 时只需 ~10s 就"越过到点"（见文件头说明）。
    tl = Timeline('抑制期')
    tl.until(C8_PRE_FLIP_S)
    if tl.first_overlay is not None:
        results.append(('C8 解除后补弹', False,
                        '仪器自检失败：抑制期 %.1fs 就出现遮罩，'
                        'QUNS 文件=4 未被生效' % tl.first_overlay))
        return
    if tl.first_preview is not None:
        print('  NOTE: PreviewSeconds=0 却出现了横幅 @%.1fs（另一处异常）'
              % tl.first_preview)
    print('  抑制 %.0fs：遮罩=%s 横幅=%s 压暗层=%s（到点 60s 已过）'
          % (C8_PRE_FLIP_S,
             '无' if tl.first_overlay is None else '有',
             '无' if tl.first_preview is None else '有',
             '无' if tl.first_dim is None else '有@%.1fs（信息项）' % tl.first_dim))

    # ---- 阶段二：改文件 4 → 0，看"改文件到遮罩"的延迟 ----
    t_flip = time.time()
    got = write_quns(0)
    print('  %.0fs：QUNS 文件改写为 0（回读 %r）' % (time.time() - tl.t0, got))
    d = wait_overlay(30.0)
    if d is None:
        results.append(('C8 解除后补弹', False,
                        '抑制 %.0fs 无遮罩（前提成立）；但改文件为 0 后 30s 内仍无遮罩'
                        '（文件钩子没被重读？）' % C8_PRE_FLIP_S))
        return
    total = time.time() - t_flip
    print('  遮罩在改文件后 %.2fs 出现（等待函数口径 %.2fs）' % (total, d))

    # 休息应正常结束（RestSeconds=8），且不要"消失又重开"
    rest = wait_overlay_gone(20.0)
    flap = False
    if rest is not None:
        print('  遮罩持续 %.1fs 后消失（期望 ≈8s）' % rest)
        flap = wait_overlay(6.0) is not None
        if flap:
            print('  NOTE: 遮罩消失后又立刻重开（抖动）')
    else:
        print('  NOTE: 遮罩 20s 内没有消失')

    ok = (total <= 5.0) and not flap
    expect_note = '' if total <= 5.0 else (
        '  <-- 期望 ≤5s；若接近 60s 说明是"重新计时"而不是补弹')
    results.append(('C8 解除后补弹', ok,
                    '抑制 %.0fs 无遮罩/无横幅；改文件→遮罩 %.2fs（期望 ≤5s，'
                    '重新计时会是 ≈60s）；休息 %.1fs%s'
                    % (C8_PRE_FLIP_S, total, rest if rest is not None else -1,
                       expect_note)))


def case_c9(results, base):
    """P0-C-9 抑制到上限强制放行（EYE_LITE_SUPPRESS_MAX_MS=60000）。"""
    print('\n--- P0-C-9 抑制到上限（60s）强制放行 ---')
    if not stop_eyelite():
        results.append(('C9 上限强制放行', False, 'EyeLite 实例停不下来'))
        return
    got = write_quns(4)
    if got != '4':
        results.append(('C9 上限强制放行', False,
                        '仪器自检失败：QUNS 文件写入后回读为 %r' % got))
        return
    cfg = dict(base)
    cfg.update({'PreviewSeconds': '0', 'RestSeconds': '8', 'StrictMode': '0'})
    write_cfg(cfg)
    launch(env_extra={QUNS_FILE_ENV: QUNS_FILE, SUPPRESS_MAX_ENV: '60000'})
    print('  已启动：%s=%s、%s=60000（QUNS 文件全程保持 4，不改）'
          % (QUNS_FILE_ENV, QUNS_FILE, SUPPRESS_MAX_ENV))

    tl = Timeline('等放行')
    tl.earlier_than(120.0)
    ov = tl.first_overlay
    if ov is None:
        print('  120s 内没有出现遮罩。时间线：')
        tl.dump()
        results.append(('C9 上限强制放行', False,
                        '120s 内无遮罩（到点 60s + 上限 60s 都已过）——'
                        '强制放行没有落地'))
        return
    print('  遮罩 %.1fs 出现（期望 55~95s）' % ov)

    # 不抖动：连续可见 ≥6s，消失后 6s 内不再重开
    rest = wait_overlay_gone(25.0)
    flap = False
    if rest is None:
        print('  NOTE: 遮罩 25s 内没有消失')
    else:
        print('  遮罩持续 %.1fs 后消失（期望 ≈8s）' % rest)
        flap = wait_overlay(6.0) is not None
        if flap:
            print('  NOTE: 遮罩消失后又立刻重开（抖动）')

    ok = (55.0 <= ov <= 95.0) and not flap
    extra = '' if 55.0 <= ov <= 95.0 else (
        '  <-- 期望 55~95s' + ('（太早：上限没生效，可能是别的原因弹的）'
                              if ov < 55.0 else '（太晚/未弹：上限没落地）'))
    results.append(('C9 上限强制放行', ok,
                    '遮罩 %.1fs（期望 55~95s）；持续 %s；抖动=%s%s'
                    % (ov, ('%.1fs' % rest) if rest is not None else '未消失',
                       '有' if flap else '无', extra)))


# ---------------------------------------------------------------- main


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at %s' % EXE)
        return 2

    want = [a.lower() for a in sys.argv[1:]] or ['all']
    if 'all' in want:
        plan = ['c7', 'c8', 'c9']
    else:
        plan = [c for c in ('c7', 'c8', 'c9') if c in want]
    if not plan:
        print('usage: p0c-suppress-check.py [all|c7|c8|c9]')
        return 2

    had_instance = bool(eyelite_pids())
    release_all()

    if not install_observer():
        print('FAIL: SetWindowsHookEx 失败，err=%d' % ctypes.get_last_error())
        return 2
    print('observer hook installed (WH_KEYBOARD_LL)，前置于 EyeLite 的钩子')

    results = []
    ctrl = None
    backup_raw = b''
    backup = {}
    try:
        # ---------- T0 仪器自检：钩子链能收到注入键 ----------
        stop_eyelite()
        got = probe([VK_F24])
        n = count_passed(got, VK_F24)
        if n < 2:
            print('instrument selfcheck: FAIL 注入 F24 只看到 %d 条消息' % n)
            print('仪器不可信，不产出任何结论')
            return 2
        print('instrument selfcheck: PASS（F24 down/up 均被 observer 看到，%d 条）'
              % n)

        try:
            with open(CFG, 'rb') as f:
                backup_raw = f.read()
        except Exception:
            pass
        backup = read_cfg()
        print('orig config: StrictMode=%s WorkMinutes=%s RestSeconds=%s '
              'SuppressOnFullscreen=%s'
              % (backup.get('StrictMode'), backup.get('WorkMinutes'),
                 backup.get('RestSeconds'), backup.get('SuppressOnFullscreen')))

        if not wait_user_idle():
            print('  NOTE: 用户持续有输入，仍继续（只注入 F24/裸 Ctrl，不产生字符）')

        base = dict(backup)
        base.update({
            'WorkMinutes': '1',            # 到点 ≈60s
            'RestSeconds': '8',            # 对照组的探针窗口 + 抖动观察都要时间
            'PomodoroEnabled': '0',
            'IdleMinutes': '0',            # 空闲暂停会冻结计时，观测不到"到点"
            'SedentaryEnabled': '0',
            'SoundEnabled': '0',
            'AllowSkip': '1',
            'StrictMode': '1',             # 一旦进休息就会全吞键盘 —— 让 C-7 更严格
            'SuppressOnFullscreen': '1',
        })

        # ---------- 仪器前置自检（同时是 C-7 的对照组）----------
        ctrl = case_control(results, base)
        if ctrl is None:
            print('\n仪器前置自检失败（枚举不到遮罩）→ 不产出任何用例结论')
            return 2
        if ctrl != 0:
            print('  WARNING: 休息期 F24 仍被放行 %d 条 —— '
                  '"没吞键"的结论会失去判别力' % ctrl)

        if 'c7' in plan:
            c7_ok = case_c7(results, base)
            if ctrl != 0 and c7_ok:
                results[-1] = ('C7 抑制期不吞键', False,
                               results[-1][2] + '；但对照组失效，结论不可信')
        if 'c8' in plan:
            case_c8(results, base)
        if 'c9' in plan:
            case_c9(results, base)
    finally:
        stop_eyelite()
        uninstall_observer()
        release_all()
        cleanup_probe()          # 临时 QUNS 文件只放 tools/_probe/，结束删掉
        if backup_raw:
            with open(CFG, 'wb') as f:
                f.write(backup_raw)
            same = os.path.getsize(CFG) == len(backup_raw)
            print('config restored: %d bytes (backup %d bytes, %s)'
                  % (os.path.getsize(CFG), len(backup_raw),
                     'OK' if same else 'SIZE MISMATCH!'))
        elif backup:
            write_cfg(backup)
            print('config restored (from parsed values): %d bytes'
                  % os.path.getsize(CFG))
        else:
            try:
                os.remove(CFG)
                print('config removed (no original)')
            except Exception:
                pass
        print('probe dir cleaned: %s' % (not os.path.exists(PROBE_DIR)))
        if had_instance:
            launch(quns=None)
            print('EyeLite restarted (was running before the test)')

    print()
    for name, ok, detail in results:
        verdict = 'SKIP' if ok is None else ('PASS' if ok else 'FAIL')
        print('%-18s %s  %s' % (name, verdict, detail))
    if ctrl is not None:
        print('%-18s %s  %s' % ('TC 对照组(不计分)',
                                'PASS' if ctrl == 0 else 'FAIL',
                                '休息期 F24 放行 %d 条（期望 0）' % ctrl))

    judged = [r for r in results if r[1] is not None]
    skipped = [r for r in results if r[1] is None]
    npass = sum(1 for r in judged if r[1])
    print('\n=== P0-C summary: %d/%d PASS%s ==='
          % (npass, len(judged),
             ('  + %d SKIP（未测，不计 PASS）' % len(skipped)) if skipped else ''))
    for name, ok, detail in judged:
        if not ok:
            print('  FAILED: %s (%s)' % (name, detail))
    for name, ok, detail in skipped:
        print('  SKIPPED: %s (%s)' % (name, detail))
    if any('仪器自检失败' in r[2] for r in results):
        return 2
    if skipped:
        return 2        # 有未测项：不能报 0，否则等于默认"通过"
    return 0 if (len(judged) == len(plan) and npass == len(plan)) else 1


if __name__ == '__main__':
    sys.exit(main())
