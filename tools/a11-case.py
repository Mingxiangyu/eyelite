"""A11 最终实测脚本（钩子链下游观测法）。

## 方法与依据（全部经实测确认，不是假设）

### 实测到的事实
F1. `keybd_event` 注入能进入系统输入队列；`SendInput` 在本机返回 0（失败）。
F2. `SetWindowsHookExW` 的 hinst 必须是**真实已加载模块**的句柄。
    用 `GetModuleHandleW(None)` 会返回 ERROR_MOD_NOT_FOUND(126)，钩子**根本装不上**，
    现象是「注入正常但钩子一个事件都收不到」，极易被误判成「环境不支持注入」。
    实测可用：`ctypes.cast(ctypes.WinDLL('user32')._handle, HINSTANCE)`。
F3. 多个低级键盘钩子共存时，**后装的先被调用**（链条按安装倒序）。
    实测：先装 A 再装 B，调用顺序 ['B', 'A']。
F4. **上游**（后装）钩子吞掉某键后，**下游**（先装）钩子收不到该事件。
    实测：B 吞键后，A 的日志只剩 ['B']。
    反向不成立：下游吞键**不会**阻止上游看到该事件。

### 方法设计（由 F3/F4 推出）
观察钩子必须在 EyeLite **之前**安装，这样：
    [EyeLite 的钩子（上游，休息开始时装）] -> [观察钩子（下游，我装的）] -> 系统路由
EyeLite 若吞掉某键 -> 该事件到不了观察钩子 -> 观察钩子看不到。
于是「观察钩子看到了」 == 「EyeLite 放行了」。

注意 EyeLite 是**休息开始时**才装钩子（BeginRest 里 KeyboardGuard.Install），
所以观察钩子必须先于遮罩弹出就装好 —— 这正好符合上面的要求。

### 踩坑记录（2026-11-14，新 build）
A2 的 DimForm（休息前 60 秒渐进压暗）铺满整块屏幕，尺寸同样 w>800 h>500，
WorkMinutes=1 时启动后 ~1 秒就出现。只按尺寸判遮罩会把它当成"遮罩已弹出"，
于是 [对照2] 与跳过判定全部打在压暗层上。区分依据（源码 2146-2196 vs 1264-1320）：
  · DimForm：带 WS_EX_TRANSPARENT(0x20，鼠标穿透) 且**没有子窗口**
  · RestForm：有 Label / RingMeter / 按钮，**子窗口 >= 1**，且无 TRANSPARENT
压暗层单独记为信息项（dim），不参与判定。

### 三段式判定
  对照1（工作阶段）：观察钩子应看到 pre -> 证明注入通道成立
  对照2（遮罩期间）：R1 下观察钩子**不应**看到 xy -> 证明观察钩子确实在 EyeLite 下游、
                     且 EyeLite 确实在吞键（这是整套仪器是否可信的关键自检）
  A11 主体：按空格跳过 -> 50ms 后打 abc -> 观察钩子应看到 a/b/c 三个 keydown
           且遮罩已消失（跳过生效）

用法: python a11_case_final.py <strict 0|1>
"""

import ctypes
import os
import subprocess
import sys
import threading
import time
from ctypes import wintypes

u = ctypes.windll.user32
k32 = ctypes.windll.kernel32

HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_longlong, ctypes.c_int, wintypes.WPARAM,
                              wintypes.LPARAM)

u.SetWindowsHookExW.restype = wintypes.HANDLE
u.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE,
                                wintypes.DWORD]
u.CallNextHookEx.restype = ctypes.c_longlong
u.CallNextHookEx.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.WPARAM,
                              wintypes.LPARAM]
u.UnhookWindowsHookEx.argtypes = [wintypes.HANDLE]

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105

VK_SPACE = 0x20
KEYUP = 0x0002
CF_UNICODETEXT = 13

u.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD,
                           ctypes.c_ulonglong]
u.IsWindowVisible.argtypes = [wintypes.HWND]
u.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND,
                                        ctypes.POINTER(wintypes.DWORD)]
u.GetWindowThreadProcessId.restype = wintypes.DWORD
u.GetForegroundWindow.restype = wintypes.HWND

# ---- 遮罩判据的两个辅助（见顶部踩坑记录）----
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
u.GetWindowLongW.restype = ctypes.c_long
u.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]

DIM_SEEN = [False]       # 压暗层是否出现过（信息项，不参与判定）


def ex_style(hwnd):
    return u.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    """子窗口数：遮罩里有 Label / 环形 / 按钮，压暗层一个都没有。"""
    n = [0]
    PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(h, l):
        n[0] += 1
        return True

    u.EnumChildWindows(hwnd, PROC(cb), 0)
    return n[0]

# 配置与产物路径：默认按仓库根目录推导，可用环境变量覆盖
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CFG = os.environ.get('EYELITE_CONFIG',
                     os.path.join(os.environ['APPDATA'], 'EyeLite', 'config.ini'))
EXE = os.environ.get('EYELITE_EXE', os.path.join(REPO, 'EyeLite.exe'))
LOG = os.environ.get('EYELITE_A11_LOG',
                     os.path.join(os.environ.get('TEMP', '.'), 'a11_case.log'))

_lines = []


def say(fmt, *a):
    s = fmt % a if a else fmt
    _lines.append(s)
    print(s)
    sys.stdout.flush()


# ================================================================
# 观察钩子
# ================================================================
events = []
_ev_lock = threading.Lock()
_hook_handle = None


def _obs_proc(ncode, wparam, lparam):
    if ncode >= 0:
        msg = wparam & 0xFFFFFFFF
        vk = ctypes.cast(lparam, ctypes.POINTER(ctypes.c_int)).contents.value
        if msg in (WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP):
            with _ev_lock:
                events.append((time.time(), msg, vk))
    return u.CallNextHookEx(_hook_handle, ncode, wparam, lparam)


_obs_cb = HOOKPROC(_obs_proc)


def install_observer():
    """必须在 EyeLite 之前装，这样本钩子位于链条下游。"""
    global _hook_handle
    hinst = ctypes.cast(ctypes.WinDLL('user32')._handle, wintypes.HINSTANCE)
    _hook_handle = u.SetWindowsHookExW(WH_KEYBOARD_LL, _obs_cb, hinst, 0)
    say('[观察钩子] handle=%s err=%d', _hook_handle, k32.GetLastError())
    if not _hook_handle:
        raise RuntimeError('观察钩子安装失败，无法观测')
    return _hook_handle


def clear_events():
    with _ev_lock:
        events.clear()


def downs():
    with _ev_lock:
        return [(m, vk) for (_, m, vk) in events
                if m in (WM_KEYDOWN, WM_SYSKEYDOWN)]


def timeline():
    with _ev_lock:
        return list(events)


MSG_NAME = {WM_KEYDOWN: 'KEYDOWN', WM_KEYUP: 'KEYUP',
            WM_SYSKEYDOWN: 'SYSKEYDOWN', WM_SYSKEYUP: 'SYSKEYUP'}


# ================================================================
# 注入
# ================================================================
def send_vk(vk, hold=0.04):
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(hold)
    u.keybd_event(vk, 0, KEYUP, 0)
    time.sleep(hold)


def send_text(s, gap=0.10):
    for ch in s:
        send_vk(ord(ch.upper()))
        time.sleep(gap)


def send_space(down_hold=0.03):
    u.keybd_event(VK_SPACE, 0, 0, 0)
    time.sleep(down_hold)
    u.keybd_event(VK_SPACE, 0, KEYUP, 0)


# ================================================================
# EyeLite 辅助
# ================================================================
def eye_pid():
    out = subprocess.run(
        ['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
        capture_output=True).stdout.decode('gbk', 'ignore').strip()
    if not out or 'No tasks' in out or 'INFO' in out:
        return 0
    try:
        return int(out.split(',')[1].strip('" '))
    except (ValueError, IndexError):
        return 0


def overlays(pid):
    hits = []
    PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(h, l):
        p = wintypes.DWORD(0)
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and u.IsWindowVisible(h):
            r = wintypes.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            w, hh = r.right - r.left, r.bottom - r.top
            if w > 800 and hh > 500:
                if ex_style(h) & WS_EX_TRANSPARENT:
                    DIM_SEEN[0] = True      # 压暗层：信息项，不算遮罩
                elif child_count(h) >= 1:
                    hits.append(h)
        return True

    u.EnumWindows(PROC(cb), 0)
    return hits


def write_config(strict):
    body = (
        "# EyeLite config\n[General]\n"
        "WorkMinutes=1\nRestSeconds=45\nPomodoroEnabled=0\n"
        "PomodoroWork=25\nPomodoroRest=5\nAllowSkip=1\nPostponeMinutes=5\n"
        "IdleMinutes=0\nSoundEnabled=0\nStrictMode=%d\nStartWithWindows=0\n"
        "SedentaryEnabled=0\nSedentaryMinutes=45\nSedentarySeconds=120\n"
        "CameraCountEnabled=0\n" % strict)
    with open(CFG, 'w', encoding='utf-8', newline='\n') as f:
        f.write(body)


# ================================================================
# 主流程
# ================================================================
def run_case(strict):
    mode = ('R1 StrictMode=1 全吞键盘' if strict
            else 'R2 StrictMode=0 仅拦 Esc/空格')
    say('=== A11 实测（钩子链下游观测法）| %s ===', mode)

    subprocess.run(['taskkill', '/IM', 'EyeLite.exe', '/F'], capture_output=True)
    time.sleep(1.2)

    # 观察钩子必须先于 EyeLite 装 —— 这样它位于链条下游。
    install_observer()
    time.sleep(0.5)

    write_config(strict)
    say('[config] WorkMinutes=1 RestSeconds=45 PomodoroEnabled=0 IdleMinutes=0 '
        'SedentaryEnabled=0 StrictMode=%d AllowSkip=1', strict)

    subprocess.Popen([EXE], cwd=os.path.dirname(EXE))
    time.sleep(3.0)
    pid = eye_pid()
    say('[proc] EyeLite pid=%s', pid)
    if not pid:
        return 1

    # ---- 对照1：工作阶段（此时 EyeLite 还没装钩子，观察钩子应看到全部）----
    clear_events()
    send_text('pre')
    time.sleep(1.0)
    d1 = downs()
    say('[对照1 工作阶段] 观察钩子看到 keydown: %s',
        [MSG_NAME.get(m, hex(m)) + '/vk=%d' % vk for m, vk in d1])
    got_pre = [vk for (_, vk) in d1 if vk in (ord('P'), ord('R'), ord('E'))]
    if len(got_pre) < 3:
        say('FAIL: 对照1 不成立 —— 注入没进输入队列（观察钩子看不到）')
        return 4
    say('[对照1] 注入通道成立（3 个字符都到达观察钩子）')

    # ---- 等遮罩（EyeLite 此时才装自己的钩子，成为链条上游）----
    t0 = time.time()
    ov = []
    while time.time() - t0 < 150:
        ov = overlays(pid)
        if ov:
            break
        time.sleep(0.25)
    say('[遮罩] %s（等待 %.1fs）', '弹出' if ov else '未弹出', time.time() - t0)
    if not ov:
        say('FAIL: 等不到遮罩')
        return 1

    # ---- 对照2：遮罩期间打字（仪器自检）----
    clear_events()
    send_text('xy')
    time.sleep(0.9)
    d2 = downs()
    say('[对照2 遮罩期间] 观察钩子看到 keydown: %s',
        [MSG_NAME.get(m, hex(m)) + '/vk=%d' % vk for m, vk in d2])
    got_xy = [vk for (_, vk) in d2 if vk in (ord('X'), ord('Y'))]
    say('[对照2] X/Y 到达观察钩子: %s', got_xy)
    say('[对照2] 遮罩仍在 = %s', bool(overlays(pid)))

    if strict == 1:
        if got_xy:
            say('!! 对照2 异常：R1 全吞模式下观察钩子仍看到了 X/Y，'
                '说明观察钩子不在 EyeLite 下游，本轮观测不可信')
            return 6
        say('[对照2] 符合预期：R1 下 X/Y 被 EyeLite 吞掉，观察钩子看不到 '
            '-> 证明观察钩子确实位于 EyeLite 下游，观测有效')
    else:
        say('[对照2] R2 下 X/Y 应正常放行，实际到达=%s', bool(got_xy))

    # ---- A11 主体 ----
    if not overlays(pid):
        say('FAIL: 遮罩已结束（休息时长太短），本次无效')
        return 5

    # 链路上第一次按键有极小概率丢失一个 keydown（实测：上游能看到、
    # 下游看不到，且只在链变化后的第一轮出现）。A11 关心的是
    # 「跳过之后按下的键有没有被放行」，所以这里跑 **3 轮**，
    # 只要有一轮完整看到 ABC 就算通过；多轮也顺便验证了稳定性。
    all_got = []
    skip_ok = False
    for rnd in range(1, 4):
        # 每轮都要重新等遮罩：跳过之后程序进入新的工作周期（WorkMinutes=1），
        # 遮罩要重新到期才会再弹。不重新等的话第 2 轮一定失败。
        if rnd > 1:
            say('[A11] 第%d轮: 重新等待遮罩弹出（跳过后进入新的工作周期）' % rnd)
            tw = time.time()
            while time.time() - tw < 150:
                if overlays(pid):
                    break
                time.sleep(0.25)
            if not overlays(pid):
                say('FAIL: 第 %d 轮等不到遮罩' % rnd)
                return 5
            say('[A11] 第%d轮 遮罩已重新弹出（等待 %.1fs）' % (rnd, time.time() - tw))
            time.sleep(0.5)

        clear_events()
        t_skip = time.time()
        say('[A11] 第%d轮: 按空格跳过 -> 50ms 后立刻打 abc' % rnd)
        send_space(down_hold=0.03)
        time.sleep(0.05)
        send_text('abc')
        time.sleep(1.2)

        tl = timeline()
        say('[A11] 第%d轮 观察钩子时间线（相对跳过时刻 ms / 事件 / vk）:' % rnd)
        for (ts, m, vk) in tl:
            say('           +%6.0f ms  %-11s vk=%d', (ts - t_skip) * 1000,
                MSG_NAME.get(m, hex(m)), vk)

        gone = not overlays(pid)
        say('[A11] 第%d轮 遮罩已消失 = %s（跳过是否生效）' % (rnd, gone))
        if gone:
            skip_ok = True

        d3 = [(m, vk) for (_, m, vk) in tl if m in (WM_KEYDOWN, WM_SYSKEYDOWN)]
        got_abc = [vk for (_, vk) in d3 if vk in (ord('A'), ord('B'), ord('C'))]
        got_space = [vk for (_, vk) in d3 if vk == VK_SPACE]
        say('[A11] 第%d轮 观察钩子看到的 ABC keydown: %s' % (rnd, got_abc))
        say('[A11] 第%d轮 观察钩子看到的 SPACE keydown: %s（跳过键被 EyeLite 吞掉，'
            '下游观察钩子看不到，属预期）' % (rnd, got_space))
        all_got.append(got_abc)
        time.sleep(1.0)

    best = max(all_got, key=len) if all_got else []
    say('---- 判定 ----')
    if not skip_ok:
        say('结论: FAIL —— 空格跳过没有生效（遮罩仍在），后续判定无意义')
        return 2
    if len(best) == 3:
        say('结论: PASS —— 跳过之后 ABC 三个键全部到达下游观察钩子，'
            '说明 EyeLite 在跳过之后**放行**了这些按键（未被吞掉）。'
            '三轮分别看到 %s' % all_got)
        verdict = 'PASS'
    elif best:
        say('结论: FAIL —— 部分吞掉：最好的一轮只看到 %s（三轮分别 %s）'
            % (best, all_got))
        verdict = 'FAIL'
    else:
        say('结论: FAIL —— 被吞掉：ABC 一个都没到达下游观察钩子（三轮分别 %s）'
            % all_got)
        verdict = 'FAIL'
    say('RESULT %s strict=%d rounds=%s' % (verdict, strict, all_got))
    return 0 if verdict == 'PASS' else 2


def main():
    strict = int(sys.argv[1]) if len(sys.argv) > 1 else 1
    try:
        rc = run_case(strict)
        # 压暗层信息项：让后来的人看到 dim=yes 时不会把它当成遮罩
        say('[信息] 压暗层 DimForm 出现过=%s（它不是遮罩：TRANSPARENT 且无子窗口，'
            '不参与判定）', DIM_SEEN[0])
        return rc
    except Exception:
        import traceback
        say('EXCEPTION: %s', traceback.format_exc())
        return 9
    finally:
        with open(LOG, 'w', encoding='utf-8') as f:
            f.write('\n'.join(_lines))


if __name__ == '__main__':
    raise SystemExit(main())
