#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-B 真机实测（v1）：预告横幅三出口 —— P0-B-6 / B-7 / B-8

## 判定对象
判的是「点了按钮之后计时状态机怎么走」，不是「窗口有没有出现」。
三个出口的语义（Program.cs:2867-2911）：

    延后      → workLimitMs += PostponeMinutes*60s，累加器不清零 → 本轮不结束，阈值后移
    跳过      → ResetWork()：累加器清零、阈值回整周期        → 满周期重新计时
    立即休息  → BeginRest() 立刻建遮罩

三者唯一可区分的外部现象就是**下一次遮罩出现的时刻**，所以判据全部落在时刻上：

    B-8 立即休息   → 点击后 ≤3s 遮罩出现
    B-7 跳过       → 点击后 45s 内**没有**遮罩（不是"立刻结束本轮"），下一次遮罩 ≈60s（整周期）
    B-6 延后 5 分  → 点击后 120s 内**没有**遮罩，下一次遮罩 ≈330s

B-6 的 330s 不是笔误：「延后」加的是**阈值**（workLimitMs += 5min），而点击发生在
预告刚开始时，累计已经吃掉 PreviewSeconds 秒，所以点击后到遮罩的时长是
`PostponeMinutes*60 + PreviewSeconds` = 300 + 30 = 330s。第一版按 300s 判会误报 FAIL。

B-6/B-7 都是「先证明没有立刻弹，再证明推迟到该弹的时刻才弹」——只测前者会让
"按钮根本没生效、计时器还在原处跑"也混过去，只测后者会让"按钮 = 立即结束"混过去。

## 点击方式
预告横幅的三个按钮是**真实 Win32 子窗口**（RoundButton : Button，Program.cs:306），
按钮的窗口文本就是控件的 Text。两个必须注意的点：
  · 「延后」的文本是 **「延后 5 分」**（Program.cs:2937 SetPostponeText），
    不是「延后」——按**前缀**匹配，否则永远找不到那个按钮。
  · 直接向子窗口 PostMessage WM_LBUTTONDOWN/WM_LBUTTONUP（WinForms Button
    自己在 WndProc 里处理鼠标消息并触发 Click），兜底再试 BM_CLICK(0x00F5)。
    全程 **不抢前台、不移动鼠标、不碰用户界面**。

## 仪器踩坑记录
1. 遮罩判定必须按 PID 过滤：Windows 桌面窗口（Program Manager，3840×2160）本身
   就是可见顶层窗口，不按 PID 过滤会被当成遮罩 → "遮罩立刻出现"，全部用例误报。
   （p0c-gate-check.py 已踩过，这里沿用同一套判据。）
2. PID 用 Popen 的 proc.pid 直接缓存，**不在轮询里调 tasklist**（每次 0.3~1s，
   在 0.1s 的轮询间隔里会把观测节奏拖垮，表现为"遮罩明明在，脚本说没有"）。
3. 自检必须前置：先证明「能枚举到横幅 + 点一下横幅会消失」。点不中按钮时，
   B-6/B-7 的"没弹遮罩"会**伪装成 PASS**，这正是最危险的假通过。
   自检不过 → return 2，不产出任何结论。
4. 进程可能是单实例的：启动前必须确认没有残留实例，否则 Popen 的 pid 立刻退出，
   窗口属于旧进程，PID 过滤会把所有窗口都过滤掉。
5. **压暗层不是遮罩**：新 build 加了 A2「休息前渐暗」（DimForm），它铺满整块屏幕，
   尺寸同样 w>800 h>500，且在 WorkMinutes=1 时启动后 1.7s 就出现 ——
   只按尺寸+PID 判会让 B-6/B-7 的"没有立刻弹遮罩"直接误报 FAIL。
   区分：压暗层带 WS_EX_TRANSPARENT 且**没有子窗口**；遮罩里有 Label/环形/按钮。
   判据已改为「大尺寸 + 非 TRANSPARENT + 子窗口 ≥1」。

## 用法
    python tools/p0b-exits-check.py            # 三轮全跑（约 8 分钟，B-6 要等 5 分钟）
    python tools/p0b-exits-check.py b8         # 只跑某一条：b8 / b7 / b6
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

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
# GetTickCount 在 kernel32 里（放 user32 会 AttributeError: function not found）。
# 不声明 restype 的话 32 位返回值会被当成 c_int，49.7 天回绕时会算出负数间隔。
kernel32.GetTickCount.restype = wt.DWORD
user32.PostMessageW.restype = wt.BOOL
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
BM_CLICK = 0x00F5
MK_LBUTTON = 0x0001

# 三个出口的匹配前缀（「延后」真实文本是「延后 5 分」）
EXIT_TEXTS = [('postpone', '延后'), ('skip', '跳过'), ('now', '立即休息')]

_pid_cache = set()

# ---------------------------------------------------------------- 窗口观测


def list_windows():
    """返回 [(hwnd, w, h, pid), ...]，只列可见顶层窗口。"""
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


def preview_windows():
    """预告横幅：可见 + 小尺寸（150% 缩放下 600×204）。"""
    return [t for t in own_windows() if 200 < t[1] <= 900 and t[2] <= 300]


def overlay_windows():
    """全屏遮罩：可见 + 大尺寸 + **不是压暗层** + **必须是 EyeLite 自己的窗口**。

    PID 过滤见踩坑 1；排除压暗层见踩坑 7（新 build 的 A2 渐暗层铺满屏幕，
    只看尺寸会把启动后 1.7s 出现的 DimForm 当成"遮罩立刻出现"）。
    """
    out = []
    for hwnd, w, h, pid in own_windows():
        if not (w > 800 and h > 500):
            continue
        if user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT:
            continue                        # DimForm：鼠标穿透层，不是遮罩
        if _child_count(hwnd) < 1:
            continue                        # 遮罩里有 Label/环形/按钮，压暗层没有
        out.append((hwnd, w, h, pid))
    return out


def _child_count(hwnd):
    n = [0]

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return n[0]


def dim_present():
    """压暗层是否在场（信息性观测，不参与判定）。"""
    for hwnd, w, h, pid in own_windows():
        if w > 800 and h > 500 \
                and (user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) \
                and _child_count(hwnd) == 0:
            return True
    return False


def window_text(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def enum_children(hwnd):
    """枚举子孙窗口（EnumChildWindows 会递归）。返回 [(hwnd, class, text, visible)]。"""
    out = []

    def cb(ch, lparam):
        out.append((ch, class_name(ch), window_text(ch),
                    bool(user32.IsWindowVisible(ch))))
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return out


def find_buttons(preview_hwnd):
    """在横幅里按文本前缀找三个出口按钮。返回 {'postpone': hwnd, ...}。"""
    found = {}
    for ch, cls, text, vis in enum_children(preview_hwnd):
        for key, prefix in EXIT_TEXTS:
            if key in found:
                continue
            if text and text.startswith(prefix):
                found[key] = (ch, cls, text, vis)
    return found


def click_child(hwnd, method='mouse'):
    """向子窗口投递一次点击，不抢前台、不动鼠标。

    method='mouse' → WM_LBUTTONDOWN + WM_LBUTTONUP（WinForms Button 走 WndProc 处理，
                     落在按钮中心，不需要真的把光标移过去）
    method='bm'    → BM_CLICK，标准按钮语义的兜底路径
    """
    if method == 'bm':
        user32.PostMessageW(hwnd, BM_CLICK, 0, 0)
        return
    cr = wt.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(cr)):
        cr.right, cr.bottom = 10, 10
    x = max(1, (cr.right - cr.left) // 2)
    y = max(1, (cr.bottom - cr.top) // 2)
    lparam = (y << 16) | (x & 0xFFFF)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lparam)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lparam)


# ---------------------------------------------------------------- 进程/配置


def eyelite_pids():
    pids = []
    try:
        out = subprocess.run(
            ['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
            capture_output=True)
        for line in out.stdout.decode('utf-8', 'ignore').splitlines():
            line = line.strip()
            if 'EyeLite.exe' not in line:
                continue
            parts = [p.strip('" ') for p in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                pids.append(int(parts[1]))
    except Exception:
        pass
    return pids


def stop_eyelite():
    """停掉所有 EyeLite 并确认真的没了（单实例进程会让 Popen 的 pid 立刻失效）。"""
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    _pid_cache.clear()
    t0 = time.time()
    while time.time() - t0 < 12:
        if not eyelite_pids():
            return True
        time.sleep(0.3)
    return False


def launch(quns=0):
    """启动 EyeLite 并缓存 PID。quns=None 走真实 API，否则强制 QUNS。"""
    env = os.environ.copy()
    env.pop('EYE_LITE_FORCE_QUNS', None)
    if quns is not None:
        env['EYE_LITE_FORCE_QUNS'] = str(quns)
    proc = subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _pid_cache.add(proc.pid)
    return proc


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
        for k in d:                      # 保留未知键，不能把用户配置弄丢
            if k not in order:
                f.write('%s=%s\n' % (k, d[k]))


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


def user_idle_ms():
    li = LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(li)
    if not user32.GetLastInputInfo(ctypes.byref(li)):
        return 0
    return int(kernel32.GetTickCount() - li.dwTime)


def wait_user_idle(threshold_ms=1000, tries=4):
    """用户正在打字就先等一等（本机有真人，别抢他的输入节奏）。"""
    for _ in range(tries):
        idle = user_idle_ms()
        if idle >= threshold_ms:
            return True
        print('  用户刚有输入（idle=%dms），等 3 秒再开始' % idle)
        time.sleep(3.0)
    return False


# ---------------------------------------------------------------- 观测循环


class Watch(object):
    """从 t0 起记录横幅/遮罩的首次出现时刻（秒）。"""

    def __init__(self):
        self.t0 = time.time()
        self.first_preview = None
        self.first_overlay = None
        self.first_dim = None           # 信息性：压暗层（不是遮罩）

    def poll(self):
        now = time.time() - self.t0
        if self.first_preview is None and preview_windows():
            self.first_preview = now
        if self.first_overlay is None and overlay_windows():
            self.first_overlay = now
        if self.first_dim is None and dim_present():
            self.first_dim = now
        return now


def watch(seconds, stop_on_overlay=False, progress_every=30.0, tag=''):
    w = Watch()
    end = w.t0 + seconds
    last = time.time()
    while time.time() < end:
        w.poll()
        if stop_on_overlay and w.first_overlay is not None:
            break
        if time.time() - last >= progress_every:
            last = time.time()
            print('    %s... %.0fs  preview=%s overlay=%s dim=%s' % (
                tag, time.time() - w.t0,
                ('%.1fs' % w.first_preview) if w.first_preview is not None else 'no',
                ('%.1fs' % w.first_overlay) if w.first_overlay is not None else 'no',
                ('%.1fs' % w.first_dim) if w.first_dim is not None else 'no'))
        time.sleep(0.1)
    return w


def wait_preview(timeout_s=110.0):
    """等预告横幅出现，返回出现时刻（相对进程启动）。超时返回 None。"""
    w = Watch()
    end = w.t0 + timeout_s
    while time.time() < end:
        if preview_windows():
            return time.time() - w.t0
        time.sleep(0.08)
    return None


def wait_preview_gone(timeout_s=4.0):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if not preview_windows():
            return time.time() - t0
        time.sleep(0.05)
    return None


def click_exit(key):
    """点某个出口。返回 (landed, method, detail)。

    landed 的判据是**横幅消失**：点不中按钮时横幅会一直挂着，
    这样"没弹遮罩"就不会被误判成 PASS（踩坑 3）。

    **两种方式都发一遍**（2026-11-14 实测：只发鼠标消息偶发不生效，脚本要等
    3 秒超时才退到 BM_CLICK，导致点击时刻漂移 ~3 秒、下游期望时刻跟着偏）。
    两个处理器都有 `if (!isPreviewing) return;` 守卫，重复点击是幂等的，
    不会重复进入休息 / 重复重置周期。先鼠标消息（走 WinForms 的 WndProc），
    再补一发 BM_CLICK（标准按钮语义），任何一条生效都算成功。
    """
    pv = preview_windows()
    if not pv:
        return False, '-', '点击前横幅已不在'
    pvw = pv[0][0]
    btns = find_buttons(pvw)
    if key not in btns:
        return False, '-', '横幅里找不到 %s 按钮（枚举到 %d 个子窗口）' % (
            key, len(enum_children(pvw)))
    hwnd, cls, text, vis = btns[key]

    click_child(hwnd, 'mouse')
    time.sleep(0.05)
    click_child(hwnd, 'bm')
    gone = wait_preview_gone(3.0)
    if gone is not None:
        return True, 'mouse+bm', '%s「%s」%s 横幅在 %.2fs 内消失' % (
            cls, text, '(不可见!)' if not vis else '', gone)

    # 兜底：再来一轮（极端情况下消息可能在窗体还没就绪时被丢弃）
    for method in ('mouse', 'bm'):
        click_child(hwnd, method)
        gone = wait_preview_gone(3.0)
        if gone is not None:
            return True, 'retry-' + method, '%s「%s」横幅在 %.2fs 内消失' % (
                cls, text, gone)
        print('    重试 %s 未生效（method=%s）' % (text, method))
    return False, '-', '两种点击方式都没能让横幅消失'


def buttons_selfcheck():
    """仪器自检 S1：能不能枚举到横幅里的三个按钮、文本对不对得上。"""
    pv = preview_windows()
    if not pv:
        return False, '没有可见的预告横幅'
    pvw = pv[0]
    btns = find_buttons(pvw[0])
    desc = ', '.join('%s=%s' % (k, v[2]) for k, v in btns.items()) or '(无)'
    ok = all(k in btns for k, _ in EXIT_TEXTS)
    return ok, '横幅 %dx%d，按钮：%s' % (pvw[1], pvw[2], desc)


# ---------------------------------------------------------------- 用例


def case_b8(results):
    """P0-B-8 立即休息 → 遮罩立刻出现（≤3s）。同时充当点击通路自检 S2。"""
    print('\n--- P0-B-8 立即休息 → 遮罩立刻出现 ---')
    if not stop_eyelite():
        results.append(('B8 立即休息', False, '无法停止残留 EyeLite 实例'))
        return
    launch(quns=0)
    t_pv = wait_preview(110.0)
    if t_pv is None:
        results.append(('B8 立即休息', False, '110s 内预告横幅未出现（无法施测）'))
        return
    print('  预告横幅 %.1fs 出现' % t_pv)

    ok_s1, detail_s1 = buttons_selfcheck()
    if not ok_s1:
        print('  [仪器自检 S1 失败] %s' % detail_s1)
        results.append(('B8 立即休息', False, '仪器自检 S1 失败：' + detail_s1))
        return
    print('  仪器自检 S1 PASS：%s' % detail_s1)

    landed, method, detail = click_exit('now')
    print('  点击：%s' % detail)
    if not landed:
        print('  [仪器自检 S2 失败] 点击通路不成立，后续"没遮罩"没有意义')
        results.append(('B8 立即休息', False, '仪器自检 S2 失败：' + detail))
        return

    w = watch(5.0, stop_on_overlay=True, progress_every=99, tag='等遮罩')
    if w.first_overlay is None:
        results.append(('B8 立即休息', False,
                        '%s；点击后 5s 内无遮罩' % detail))
        return
    ok = w.first_overlay <= 3.0
    results.append(('B8 立即休息', ok,
                    '%s；遮罩在点击后 %.2fs 出现（期望 ≤3s，method=%s）'
                    % (detail, w.first_overlay, method)))


def case_b7(results):
    """P0-B-7 跳过 → 满周期重新计时：既不立刻弹，也不拖延。"""
    print('\n--- P0-B-7 跳过 → 满周期重新计时 ---')
    if not stop_eyelite():
        results.append(('B7 跳过', False, '无法停止残留 EyeLite 实例'))
        return
    launch(quns=0)
    t_pv = wait_preview(110.0)
    if t_pv is None:
        results.append(('B7 跳过', False, '110s 内预告横幅未出现（无法施测）'))
        return
    print('  预告横幅 %.1fs 出现' % t_pv)
    landed, method, detail = click_exit('skip')
    print('  点击：%s' % detail)
    if not landed:
        results.append(('B7 跳过', False, '点击未生效：' + detail))
        return

    # 点击后 95s 内观察：应 30s 后再预告、60s 后才弹遮罩
    w = watch(95.0, stop_on_overlay=True, progress_every=20, tag='等下一轮遮罩')
    ov = w.first_overlay
    pv2 = w.first_preview
    if ov is None:
        results.append(('B7 跳过', False,
                        '%s；95s 内没有出现下一次遮罩（满周期 60s 未重新计时）' % detail))
        return
    no_instant = ov >= 45.0
    on_cycle = 50.0 <= ov <= 78.0
    pv_ok = pv2 is not None and 20.0 <= pv2 <= 45.0
    ok = no_instant and on_cycle and pv_ok
    results.append(('B7 跳过', ok,
                    '%s；下一次遮罩 %.1fs（期望 ≈60s，且 ≥45s）、'
                    '下一次预告 %s（期望 ≈30s）%s'
                    % (detail, ov,
                       ('%.1fs' % pv2) if pv2 is not None else 'never',
                       '' if ok else '  <-- 不符合"满周期重新计时"')))


def case_b6(results):
    """P0-B-6 延后 5 分 → 本轮不结束，阈值后移 5 分钟。"""
    print('\n--- P0-B-6 延后 5 分 → 本轮不再弹遮罩 ---')
    if not stop_eyelite():
        results.append(('B6 延后', False, '无法停止残留 EyeLite 实例'))
        return
    launch(quns=0)
    t_pv = wait_preview(110.0)
    if t_pv is None:
        results.append(('B6 延后', False, '110s 内预告横幅未出现（无法施测）'))
        return
    print('  预告横幅 %.1fs 出现' % t_pv)
    landed, method, detail = click_exit('postpone')
    print('  点击：%s' % detail)
    if not landed:
        results.append(('B6 延后', False, '点击未生效：' + detail))
        return

    # 期望值推导（第一版这里算错过，记下来）：
    #   「延后」加的是**阈值**（workLimitMs += 5min），不是"从现在起再等 5 分钟"。
    #   点击发生在预告刚开始时，此时 accum ≈ limit_old - PreviewSeconds。
    #   于是点击后到遮罩的时长 = limit_new - accum = PostponeMinutes*60 + PreviewSeconds
    #                          = 300 + 30 = 330s
    #   （不是 300s —— 那 30 秒是"已经累计过的"工作，已经记在账上了。）
    expect_ov = 300.0 + 30.0
    w = watch(370.0, stop_on_overlay=True, progress_every=30, tag='等延后后的遮罩')
    ov = w.first_overlay
    pv2 = w.first_preview
    if ov is None:
        results.append(('B6 延后', False,
                        '%s；370s 内没有出现下一次遮罩' % detail))
        return
    no_instant = ov >= 120.0
    shifted = abs(ov - expect_ov) <= 10.0
    pv_ok = pv2 is not None and abs(pv2 - 300.0) <= 10.0   # 遮罩前 30s 预告
    ok = no_instant and shifted and pv_ok
    results.append(('B6 延后', ok,
                    '%s；下一次遮罩 %.1fs（期望 %.0fs = 延后 300s + 预告 30s，'
                    '且 ≥120s）、下一次预告 %s（期望 ≈300s）%s'
                    % (detail, ov, expect_ov,
                       ('%.1fs' % pv2) if pv2 is not None else 'never',
                       '' if ok else '  <-- 不符合"阈值后移"')))


# ---------------------------------------------------------------- main


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at %s' % EXE)
        return 2

    which = sys.argv[1].lower() if len(sys.argv) > 1 else 'all'
    plan = {'all': ['b8', 'b7', 'b6']}.get(which, [which])
    if any(c not in ('b8', 'b7', 'b6') for c in plan):
        print('usage: p0b-exits-check.py [all|b8|b7|b6]')
        return 2

    had_instance = bool(eyelite_pids())
    if not stop_eyelite():
        print('FAIL: 有 EyeLite 实例停不下来，无法施测')
        return 2

    backup_raw = b''
    backup = {}
    try:
        with open(CFG, 'rb') as f:
            backup_raw = f.read()
    except Exception:
        pass
    backup = read_cfg()
    print('orig config: WorkMinutes=%s PreviewSeconds=%s RestSeconds=%s '
          'PostponeMinutes=%s' % (backup.get('WorkMinutes'),
                                  backup.get('PreviewSeconds'),
                                  backup.get('RestSeconds'),
                                  backup.get('PostponeMinutes')))

    results = []
    try:
        base = dict(backup)
        base.update({
            'WorkMinutes': '1',            # 到点 ≈60s
            'PreviewSeconds': '30',        # 横幅在 30s 出现，留 30s 给点击
            'RestSeconds': '5',            # 用户就在这台机器上，缩短停留
            'PostponeMinutes': '5',        # 用例默认值，必须按 5 分钟验证
            'PomodoroEnabled': '0',
            'IdleMinutes': '0',            # 不因空闲暂停（否则计时被冻结，观测不到点）
            'SedentaryEnabled': '0',
            'SoundEnabled': '0',
            'AllowSkip': '1',
            'StrictMode': '0',             # 不装键盘钩子，与本组用例无关
            'SuppressOnFullscreen': '0',   # 隔离 P0-C 变量：本组不测抑制
        })
        write_cfg(base)
        print('test config written: %d bytes' % os.path.getsize(CFG))

        if not wait_user_idle():
            print('  NOTE: 用户持续有输入，仍继续（只发消息、不抢焦点/不动鼠标）')

        for c in plan:
            if c == 'b8':
                case_b8(results)
            elif c == 'b7':
                case_b7(results)
            elif c == 'b6':
                case_b6(results)
    finally:
        stop_eyelite()
        # 按**原始字节**还原：比重新序列化更可靠，能保证未识别的自定义键与格式完全一致
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

        if had_instance:
            launch(quns=None)
            print('EyeLite restarted (was running before the test)')

    print()
    for name, ok, detail in results:
        print('%-16s %s  %s' % (name, 'PASS' if ok else 'FAIL', detail))

    npass = sum(1 for r in results if r[1])
    print('\n=== P0-B summary: %d/%d PASS ===' % (npass, len(results)))
    for name, ok, detail in results:
        if not ok:
            print('  FAILED: %s (%s)' % (name, detail))
    if any('仪器自检' in r[2] for r in results):
        print('  NOTE: 有仪器自检失败项 —— 该条结论不成立，先修仪器再复测')
        return 2
    return 0 if (len(results) == len(plan) and npass == len(plan)) else 1


if __name__ == '__main__':
    sys.exit(main())
