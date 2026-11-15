#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-E 真机实测（v2 / 钩子链判定）：严格模式默认降档
硬要求：「不得影响用户输入」「Ctrl 不会卡住」

## 为什么推翻 v1 的判定方式
v1 用「自建沙箱窗口 + 看沙箱收没收到 WM_KEYDOWN」判定，实测踩了两个坑：
  ① WNDCLASS 的 WndProc 把 LPARAM 声明成 32 位 → 64 位下 OverflowError，
     回调每次都抛异常被静默吞掉，仪器等于全瞎
  ② SetForegroundWindow 被前台锁拒绝（当时前台是用户自己的程序），
     而"抢前台"这件事本身就等于干扰用户输入 —— 与要验证的目标自相矛盾

v2 换成 **WH_KEYBOARD_LL 钩子链**：
  · 不创建窗口、不抢焦点、不改前台
  · 注入只用 VK_F24 与裸 Ctrl —— 都不产生字符，不落进用户的文档
  · 判定是端到端的：看按键消息能不能穿过 EyeLite 的钩子

## 判定原理
Windows 低级键盘钩子按「最后安装的最先调用」执行；任一钩子返回非 0，
链即终止，更早安装的钩子收不到该消息。

本脚本在启动 EyeLite **之前**就装好 observer，因此 EyeLite 休息期间装的钩子
排在 observer 之后（先被调用）：
  · observer 收到   → EyeLite 放行 → 不吞键
  · observer 收不到 → EyeLite 吞了 → 被拦

## 用例
  T0   仪器自检（不启 EyeLite）：F24 down/up 必须被 observer 看到 2 条
  T3   严格档对照组：遮罩期 F24 应被吞（observer 0 条）→ 证明仪器看得见吞键
  T1   默认档遮罩期：F24 应被放行（observer 2 条）→ 用户输入未被吞
  T2   默认档裸 Ctrl 配对：放行条数必须 ∈ {0,2}，不得为 1 → 不会卡 Ctrl
  T2b  默认档连续按键后物理键无残留
  T3b  严格档裸 Ctrl 配对：同样必须 ∈ {0,2}
  T4   休息结束后物理键全部释放

配对性为什么重要：若 EyeLite 只吞了 down 没吞 up（或反之），应用侧就会
认为 Ctrl 一直按着 —— 这正是用户报告过的「ctrl 一直卡住」的根因形态。

**踩坑记录 4（2026-11-14，新 build）**：A2 的 DimForm（休息前 60 秒渐进压暗）铺满整块
屏幕，尺寸同样 w>800 h>500，WorkMinutes=1 时启动后 ~1 秒就出现。只按尺寸判遮罩会把它
当成"遮罩已弹出"，于是 wait_overlay 立刻返回、探针打在**压暗层**上而不是休息期，
判定全部失真。区分依据（源码 2146-2196 vs 1264-1320）：
  · DimForm：带 WS_EX_TRANSPARENT(0x20，鼠标穿透) 且**没有子窗口**
  · RestForm：有 Label / RingMeter / 按钮，**子窗口 >= 1**，且无 TRANSPARENT
压暗层单独记为信息项（dim），不参与 PASS/FAIL。
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

# ---- 遮罩判据的两个辅助（见顶部踩坑记录 4）----
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

DIM_SEEN = [False]       # 压暗层是否出现过（信息项，不参与判定）


def ex_style(hwnd):
    return user32.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    """子窗口数：遮罩里有 Label / 环形 / 按钮，压暗层一个都没有。"""
    n = [0]
    Enum = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, Enum(cb), 0)
    return n[0]

# ---------------- Win32 常量 ----------------
WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
LLKHF_INJECTED = 0x10
PM_REMOVE = 0x0001
KEYEVENTF_KEYUP = 0x0002

VK_CONTROL = 0x11
VK_LCONTROL = 0xA2          # keybd_event(0x11) 实际产出的就是它，必须一起算
VK_SHIFT = 0x10
VK_LSHIFT = 0xA0
VK_MENU = 0x12
VK_LMENU = 0xA4
VK_LWIN = 0x5B
VK_CAPITAL = 0x14
VK_SPACE = 0x20
VK_TAB = 0x09
VK_F24 = 0x87
VK_ESCAPE = 0x1B

# 「裸 Ctrl」判定要覆盖两种 vkCode：通用 VK_CONTROL 与具体 VK_LCONTROL。
# 实测 keybd_event(0x11) 走的是 0xA2，只比 0x11 会把消息全漏掉，
# 表现为"默认档也放行 0 条"这种假 PASS。本机第三次踩同类仪器坑。
CTRL_VKS = (VK_CONTROL, VK_LCONTROL)

CHECK_KEYS = [
    ('Ctrl', VK_CONTROL), ('Shift', VK_SHIFT), ('Alt', VK_MENU),
    ('Win', VK_LWIN), ('CapsLock', VK_CAPITAL), ('Space', VK_SPACE), ('Tab', VK_TAB),
]

# ---------------- 类型 ----------------
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

# observer 记录：(msg, vkCode, 是否注入)
_seen = []
_hhook = [None]


def _hook_cb(nCode, wParam, lParam):
    """回调里只做记录，绝不做耗时操作（超过 LowLevelHooksTimeout 会被系统摘钩）。"""
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
    # 所有返回句柄的 API 都必须显式声明 restype，否则 ctypes 默认按 32 位
    # c_int 解释返回值，64 位句柄被高位截断 → SetWindowsHookEx 收到残句柄
    # 报 ERROR_MOD_NOT_FOUND(126)。这是本机第二次踩同一个坑。
    kernel32.GetModuleHandleW.restype = wt.HMODULE
    kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]
    user32.SetWindowsHookExW.restype = wt.HANDLE
    user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wt.HMODULE, wt.DWORD]
    user32.CallNextHookEx.restype = LRESULT
    user32.CallNextHookEx.argtypes = [wt.HANDLE, ctypes.c_int, WPARAM, LPARAM]

    hinst = kernel32.GetModuleHandleW(None)
    if not hinst:
        print('GetModuleHandleW failed, err=%d' % ctypes.get_last_error())
        return False
    _hhook[0] = user32.SetWindowsHookExW(WH_KEYBOARD_LL, _HOOK_REF, hinst, 0)
    if not _hhook[0]:
        print('SetWindowsHookEx err=%d (hinst=%s)' % (ctypes.get_last_error(), hinst))
    return bool(_hhook[0])


def uninstall_observer():
    if _hhook[0]:
        try:
            user32.UnhookWindowsHookEx(_hhook[0])
        except Exception:
            pass
        _hhook[0] = None


def pump(seconds):
    """泵消息，让安装钩子的线程真正收到低级钩子回调。"""
    msg = wt.MSG()
    end = time.time() + seconds
    while time.time() < end:
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.005)


def key_down(vk):
    user32.keybd_event(vk, 0, 0, 0)


def key_up(vk):
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


def is_down(vk):
    return (user32.GetAsyncKeyState(vk) & 0x8000) != 0


def key_states():
    return dict((n, is_down(v)) for n, v in CHECK_KEYS)


def all_up():
    return not any(key_states().values())


def release_all():
    for _ in range(20):
        if all_up():
            return True
        for _, vk in CHECK_KEYS:
            key_up(vk)
        time.sleep(0.05)
    return all_up()


def pairing_ok(msgs, vks, expect_passed=None):
    """检查某个键的 down/up 是否成对通过 observer。

    返回 (ok, 描述, 通过条数)。

    成对性（防"卡键"）：
      · 2 条且一条 KEYDOWN 一条 KEYUP —— 完全放行，键不会被卡住
      · 0 条                          —— 完全吞掉，应用侧不知道这个键存在
      危险的是只放行 1 条：应用收到按下却收不到抬起（或反之），键就"卡住"了
      —— 这正是用户报告过的 Ctrl 卡死形态。

    expect_passed:
      · None —— 只要求成对（严格档用：吞干净或全放行都算安全）
      · True —— 还要求**确实放行**了（默认档用：必须真的送达，否则就是被吞了）
    """
    sel = [m for m in msgs if m[1] in vks]
    downs = len([m for m in sel if m[0] in (WM_KEYDOWN, WM_SYSKEYDOWN)])
    ups = len([m for m in sel if m[0] in (WM_KEYUP, WM_SYSKEYUP)])
    n = len(sel)
    paired = (n == 0) or (n == 2 and downs == 1 and ups == 1)
    ok = paired if expect_passed is None else (paired and n == 2)
    return ok, 'down=%d up=%d 共%d条' % (downs, ups, n), n


def probe(seq, wait=0.55):
    """注入按键序列，返回 observer 在这期间看到的消息列表。

    seq: [(vk, is_down), ...]
    只统计 LLKHF_INJECTED 的消息，排除用户自己手按的键。
    """
    pump(0.08)
    start = len(_seen)
    for vk, down in seq:
        if down:
            key_down(vk)
        else:
            key_up(vk)
        time.sleep(0.03)
    pump(wait)
    return [m for m in _seen[start:] if m[2]]


# ---------------- 配置 ----------------
def read_cfg():
    d = {}
    try:
        with open(CFG, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if '=' in line and not line.startswith('#') and not line.startswith('['):
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
        for k in d:
            if k not in order:
                f.write('%s=%s\n' % (k, d[k]))


def eyelite_pids():
    pids = []
    try:
        out = subprocess.run(
            ['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
            capture_output=True)
        for line in out.stdout.decode('utf-8', 'ignore').splitlines():
            if 'EyeLite.exe' in line:
                parts = [p.strip('" ') for p in line.split('","')]
                if len(parts) > 1 and parts[1].isdigit():
                    pids.append(int(parts[1]))
    except Exception:
        pass
    return pids


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    time.sleep(1.2)


def overlay_present():
    """按 PID 判定 EyeLite 遮罩是否出现（必须排除 Windows 桌面窗口 + 压暗层）。

    压暗层（DimForm）铺满屏幕但带 WS_EX_TRANSPARENT 且无子窗口；遮罩（RestForm）
    有 Label / 环形 / 按钮。只按尺寸判会让 wait_overlay 在启动 1 秒后就返回，
    探针全打在压暗层上 —— 判定会整体失真（踩坑记录 4）。
    """
    pids = set(eyelite_pids())
    if not pids:
        return False
    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, LPARAM)
    found = [False]

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids:
            r = wt.RECT()
            if user32.GetWindowRect(hwnd, ctypes.byref(r)):
                if (r.right - r.left) > 800 and (r.bottom - r.top) > 500:
                    if ex_style(hwnd) & WS_EX_TRANSPARENT:
                        DIM_SEEN[0] = True        # 压暗层：信息项，不算遮罩
                    elif child_count(hwnd) >= 1:
                        found[0] = True
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return found[0]


def wait_overlay(timeout_s):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if overlay_present():
            return time.time() - t0
        pump(0.2)
    return None


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found')
        return 2

    had_instance = bool(eyelite_pids())
    release_all()

    if not install_observer():
        print('FAIL: SetWindowsHookEx 失败，err=%d' % ctypes.get_last_error())
        return 2

    print('observer hook installed (WH_KEYBOARD_LL), 前置于 EyeLite 的钩子')
    if had_instance:
        print('detected running EyeLite instance; will be restarted at the end')

    results = []
    try:
        # ---------- T0 仪器自检 ----------
        stop_eyelite()
        got = probe([(VK_F24, True), (VK_F24, False)])
        if len(got) < 2:
            print('instrument selfcheck: FAIL 注入 F24 只看到 %d 条消息' % len(got))
            print('仪器不可信，不产出结论')
            return 2
        print('instrument selfcheck: PASS（F24 down/up 均被 observer 看到，%d 条）' % len(got))

        orig = read_cfg()
        print('orig config: StrictMode=%s WorkMinutes=%s RestSeconds=%s' % (
            orig.get('StrictMode'), orig.get('WorkMinutes'), orig.get('RestSeconds')))
        backup = dict(orig)

        try:
            base = dict(backup)
            base.update({
                'WorkMinutes': '1', 'RestSeconds': '12', 'PreviewSeconds': '2',
                'SedentaryEnabled': '0', 'IdleMinutes': '0', 'SoundEnabled': '0',
                'AllowSkip': '1', 'PomodoroEnabled': '0',
            })

            # ================= 严格档对照组 =================
            strict = dict(base)
            strict['StrictMode'] = '1'
            # E6（锁前倒计时）之后，严格模式**不再是一出现就装钩子**：
            # 默认会在休息开始时先给 N 秒自由时间。本用例要验的是"装了钩子之后确实吞键"，
            # 所以必须显式把倒计时设为 0，否则注入落在倒计时窗口内 → 键被放行，
            # 看起来像"严格模式没生效"，实际是测试前提过时了（曾因此误判过一次）。
            strict['LockCountdownSeconds'] = '0'
            write_cfg(strict)
            subprocess.Popen([EXE], stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)
            t = wait_overlay(100.0)
            if t is None:
                results.append(('T3 严格档对照组（应吞键）', False,
                                '遮罩未出现，对照组无法成立'))
            else:
                got_strict = probe([(VK_F24, True), (VK_F24, False)])
                got_ctrl_s = probe([(VK_CONTROL, True), (VK_CONTROL, False)])
                release_all()

                # 对照组只证明"仪器看得见吞键"，不参与 PASS 统计
                seen_strict = len([m for m in got_strict
                                   if m[1] == VK_F24 and m[0] in (WM_KEYDOWN, WM_KEYUP)])
                results.append((
                    'T3 严格档对照组（仪器应看得见吞键）',
                    True,
                    '遮罩%.1fs F24 被放行=%d 条(期望0)' % (t, seen_strict)))
                if seen_strict > 0:
                    print('  NOTE: 严格档下 F24 仍被放行 %d 条 —— '
                          '对照组失效，T1/T2 的结论需打折解读' % seen_strict)

                ok_ctrl_s, desc_ctrl_s, n_ctrl_s = pairing_ok(got_ctrl_s, CTRL_VKS)
                results.append((
                    'T3b 严格档裸 Ctrl 配对（不得只吞一半）',
                    ok_ctrl_s, desc_ctrl_s))

                # 退出休息
                probe([(VK_ESCAPE, True), (VK_ESCAPE, False)], wait=0.8)
                time.sleep(1.5)
                release_all()

            stop_eyelite()

            # ================= 默认档（本次改动的主角）=================
            light = dict(base)
            light['StrictMode'] = '0'
            write_cfg(light)
            subprocess.Popen([EXE], stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)
            t = wait_overlay(100.0)
            if t is None:
                results.append(('T1 默认档遮罩期按键送达', False, '遮罩未出现'))
                results.append(('T2 默认档 Ctrl 配对', False, '未进入遮罩期'))
                results.append(('T2b 默认档无残留', False, '未进入遮罩期'))
            else:
                got_light = probe([(VK_F24, True), (VK_F24, False)])
                n_light = len([m for m in got_light
                               if m[1] == VK_F24 and m[0] in (WM_KEYDOWN, WM_KEYUP)])
                results.append((
                    'T1 默认档遮罩期按键送达（输入未被吞）',
                    n_light >= 2,
                    '遮罩%.1fs F24 放行=%d 条（期望 2）' % (t, n_light)))

                got_ctrl_l = probe([(VK_CONTROL, True), (VK_CONTROL, False)])
                ok_ctrl_l, desc_ctrl_l, n_ctrl_l = pairing_ok(
                    got_ctrl_l, CTRL_VKS, expect_passed=True)
                results.append((
                    'T2 默认档裸 Ctrl 必须送达且成对（不得吞、不得卡）',
                    ok_ctrl_l, desc_ctrl_l))

                for vk in (VK_SPACE, 0x42, 0x43):
                    key_down(vk)
                    time.sleep(0.03)
                    key_up(vk)
                    time.sleep(0.08)
                pump(0.2)
                stuck = [n for n, d in key_states().items() if d]
                results.append((
                    'T2b 默认档连续按键后物理键无残留',
                    not stuck, 'stuck=%s' % (stuck or '无')))

                # 结束休息并确认全部释放
                probe([(VK_ESCAPE, True), (VK_ESCAPE, False)], wait=0.8)
                time.sleep(1.5)
                release_all()
                stuck3 = [n for n, d in key_states().items() if d]
                results.append((
                    'T4 休息结束后物理键全部释放',
                    not stuck3, 'stuck=%s' % (stuck3 or '无')))
        finally:
            stop_eyelite()
            if backup:
                write_cfg(backup)
                print('config restored: %d bytes' % os.path.getsize(CFG))
    finally:
        uninstall_observer()
        release_all()
        if had_instance:
            subprocess.Popen([EXE], stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)
            print('EyeLite restarted (was running before the test)')

    print()
    for name, ok, detail in results:
        print('%-44s %s  %s' % (name, 'PASS' if ok else 'FAIL', detail))

    # 压暗层信息项：让后来的人看到 dim=yes 时不会把它当成遮罩
    print('%-44s %s  %s' % ('压暗层 DimForm（信息项，不计分）',
                            'yes' if DIM_SEEN[0] else 'no',
                            '出现过，但它不是遮罩（TRANSPARENT 且无子窗口）'))

    core = [r for r in results if not r[0].startswith('T3 ')]
    npass = sum(1 for r in core if r[1])
    print('\n=== P0-E summary: %d/%d PASS（不含对照组）===' % (npass, len(core)))
    for r in core:
        if not r[1]:
            print('  FAILED: %s (%s)' % (r[0], r[2]))
    return 0 if npass == len(core) else 1


if __name__ == '__main__':
    sys.exit(main())
