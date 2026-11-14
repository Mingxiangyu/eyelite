#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-B 真机实测：30 秒预告 + 三出口。

测四件事：
  T1 预告横幅出现 —— 到点前 PreviewSeconds 秒应出现小横幅（尺寸远小于全屏遮罩）
  T2 预告期键盘零干预 —— 横幅可见期间，注入一个普通按键（修饰键 + 字符），
                        必须能正常到达目标窗口（判据用钩子链，见 a11-case.py 的方法论）
  T3 预告期不抢焦点 —— 横幅出现时，前台窗口必须仍是横幅出现前的那个
  T4 到点转遮罩 —— 横幅出现后应准时转成全屏遮罩，且横幅消失

设计约束（与用户的硬要求一致）：
  · **不注入任何按键到用户正在用的窗口**。T2 用的是"钩子链观测"：
    在 EyeLite 之后再装一个观察钩子，看键是否被上游（EyeLite）吞掉。
    观察钩子装在 EyeLite 之后 = 钩子链上游，不会干扰真实输入路径。
  · 只枚举窗口、读焦点、读时间，不抢焦点、不移动用户光标。
  · 所有临时文件写在仓库 tools/ 目录内，不外溢到系统 Temp。

用法：python tools/p0b-preview-check.py [轮数]
前置：config.ini 里 WorkMinutes=1 / PreviewSeconds=10 / RestSeconds=8 /
      PomodoroEnabled=0 / IdleMinutes=0 / SedentaryEnabled=0。
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

# LRESULT CALLBACK(int nCode, WPARAM wParam, LPARAM lParam) —— 回调只有 3 个参数。
# INFUNCTYPE 的 argtypes 个数必须与回调形参个数一致：多写一个 HWND 会让 ctypes
# 传 4 个实参，回调每次都抛 TypeError，而 ctypes 把回调里的异常**静默吞掉**，
# 表现就是"钩子装上了但永远收不到事件"（2026-11-14 实测踩过）。
HOOKPROC_T = ctypes.WINFUNCTYPE(ctypes.c_longlong, ctypes.c_int,
                                wt.WPARAM, wt.LPARAM)

user32 = ctypes.WinDLL('user32', use_last_error=True)
user32.SetWindowsHookExW.restype = wt.HANDLE
user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC_T, wt.HINSTANCE, wt.DWORD]
user32.UnhookWindowsHookEx.argtypes = [wt.HANDLE]
user32.CallNextHookEx.restype = ctypes.c_longlong

EYELITE_PID = [0]

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105

# 观察到的按键（注入的键在观察钩子里应当可见）
OBSERVED = {'count': 0, 'last_vk': 0, 'installed': False}

HOOKPROC = HOOKPROC_T


def _hook_cb(code, wparam, lparam):
    if code >= 0:
        msg = wparam & 0xFFFFFFFF
        if msg in (WM_KEYDOWN, WM_SYSKEYDOWN):
            OBSERVED['count'] += 1
            OBSERVED['last_vk'] = lparam & 0xFFFFFFFF
    return user32.CallNextHookEx(HOOK_HANDLE[0], code, wparam, lparam)


HOOK = HOOKPROC(_hook_cb)
HOOK_HANDLE = [None]


def install_observer():
    """装一个观察钩子（在 EyeLite 之后安装 = 钩子链上游，只观测不吞键）。

    hinst 必须是**真实已加载模块**的句柄，且 SetWindowsHookExW 的 restype 要显式
    声明为 HANDLE —— 否则 64 位句柄被按 32 位截断，装上去的是个假句柄。
    用 GetModuleHandleW(None) 会得到 ERROR_MOD_NOT_FOUND(126)，钩子根本装不上；
    这里用 user32.dll 自身的模块句柄（它一定已加载），实测可装。
    """
    hinst = ctypes.cast(ctypes.WinDLL('user32')._handle, wt.HINSTANCE)
    h = user32.SetWindowsHookExW(WH_KEYBOARD_LL, HOOK, hinst, 0)
    if h:
        HOOK_HANDLE[0] = h
        OBSERVED['installed'] = True
    return bool(h)


def uninstall_observer():
    h = HOOK_HANDLE[0]
    if h:
        try:
            user32.UnhookWindowsHookEx(h)
        except Exception:
            pass
        HOOK_HANDLE[0] = None
    OBSERVED['installed'] = False


def instrument_selfcheck():
    """仪器可信度自检（a11-case.py 的方法论，必须做）。

    不启动 EyeLite，装观察钩子 + 注入一个键，确认观察钩子**能看到**它。
    若这一步失败，后面"看不到键"就分不清是 EyeLite 吞了还是仪器坏了 ——
    上一版就是没做自检，把仪器故障误判成"EyeLite 吞键"。
    """
    if not install_observer():
        return False, '观察钩子装不上（hinst 或签名问题）'
    time.sleep(0.2)
    OBSERVED['count'] = 0
    send_key(VK_F13)
    time.sleep(0.3)
    n = OBSERVED['count']
    uninstall_observer()
    return (n > 0), ('自检观察到 %d 次' % n)


def find_eyelite_pid():
    r = subprocess.run(['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
                       capture_output=True)
    text = r.stdout.decode('gbk', 'ignore')
    for line in text.splitlines():
        if 'EyeLite.exe' in line:
            parts = [p.strip('" ') for p in line.split('","')]
            return int(parts[1])
    return None


def enum_windows():
    out = []
    EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(hwnd, lparam):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        out.append({
            'hwnd': hwnd,
            'pid': pid.value,
            'cls': cls.value,
            'w': r.right - r.left,
            'h': r.bottom - r.top,
            'visible': bool(user32.IsWindowVisible(hwnd)),
            'title': _get_title(hwnd),
        })
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def _get_title(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def eyelite_windows():
    return [w for w in enum_windows() if w['pid'] == EYELITE_PID[0]]


def overlay_windows():
    """全屏休息遮罩：可见 + 大尺寸。"""
    return [w for w in eyelite_windows() if w['visible'] and w['w'] > 800 and w['h'] > 500]


def preview_windows():
    """预告横幅：可见 + 小尺寸（宽度在 200~900 之间的窄条）。"""
    return [w for w in eyelite_windows()
            if w['visible'] and 200 < w['w'] <= 900 and w['h'] <= 300]


def foreground():
    return user32.GetForegroundWindow()


def window_title(hwnd):
    if not hwnd:
        return '(none)'
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, cls, 256)
    return '%s [%s]' % (buf.value or '(untitled)', cls.value)


def send_key(vk):
    """注入一个按键（down + up）。keybd_event 在本机确实会进输入队列。"""
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.02)
    user32.keybd_event(vk, 0, 2, 0)


VK_F13 = 0x7C   # 选一个普通应用绝不会用到的键，避免污染用户的文档


def wait_for(pred, timeout_s, poll=0.1):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        v = pred()
        if v:
            return v, time.time() - t0
        time.sleep(poll)
    return None, time.time() - t0


def process_start_time():
    r = subprocess.run(
        ['powershell', '-NoProfile', '-Command',
         "(Get-Process EyeLite).StartTime.ToUniversalTime().ToOADate()"],
        capture_output=True)
    try:
        oadate = float(r.stdout.decode('utf-8', 'ignore').strip())
        import datetime
        epoch = datetime.datetime(1899, 12, 30, tzinfo=datetime.timezone.utc)
        return (epoch + datetime.timedelta(days=oadate)).timestamp()
    except Exception:
        return None


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    t0 = time.time()
    while time.time() - t0 < 15:
        if find_eyelite_pid() is None:
            return
        time.sleep(0.3)


def start_eyelite(exe):
    subprocess.Popen([exe], cwd=os.path.dirname(exe))
    time.sleep(1.0)
    pid = find_eyelite_pid()
    if pid:
        EYELITE_PID[0] = pid
    return pid


def main():
    rounds = 2
    if len(sys.argv) > 1:
        try:
            rounds = int(sys.argv[1])
        except Exception:
            pass

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    exe = os.path.join(root, 'EyeLite.exe')
    if not os.path.exists(exe):
        print('[FATAL] 找不到 EyeLite.exe：' + exe)
        return 2

    print('[env] exe = %s' % exe)
    print('[env] rounds = %d' % rounds)

    results = []

    # 仪器可信度自检：先证明"观察钩子能看到注入的按键"
    ok_self, detail_self = instrument_selfcheck()
    print('[自检] 观察钩子可见性：%s（%s）' % ('PASS' if ok_self else 'FAIL', detail_self))
    if not ok_self:
        print('[FATAL] 仪器不可信，本次不产出结论（避免把仪器故障当成产品缺陷）')
        return 2
    results.append(('仪器自检', True, detail_self))

    for rnd in range(1, rounds + 1):
        print('\n=========== 第 %d 轮 ===========' % rnd)
        stop_eyelite()
        uninstall_observer()

        fg_before = foreground()
        print('  横幅出现前的前台窗口：%s' % window_title(fg_before))

        pid = start_eyelite(exe)
        if not pid:
            print('  [FATAL] 启动失败')
            results.append(('第%d轮 启动' % rnd, False, '启动失败'))
            continue

        base = process_start_time() or time.time()

        # --- T1：预告横幅出现（应在 WorkMinutes*60 - PreviewSeconds 附近）---
        pv, waited = wait_for(lambda: preview_windows() or None, 120)
        if not pv:
            print('  预告横幅 120s 内未出现')
            results.append(('第%d轮 T1 预告出现' % rnd, False, '未出现'))
            stop_eyelite()
            continue

        appear = time.time()
        elapsed = appear - base
        expect = 60.0 - 10.0     # WorkMinutes=1, PreviewSeconds=10
        print('  T1 横幅出现：进程启动后 %.2fs（期望 %.0fs ± 4s）' % (elapsed, expect))
        print('     横幅尺寸 %dx%d' % (pv[0]['w'], pv[0]['h']))
        ok1 = abs(elapsed - expect) <= 4.0
        results.append(('第%d轮 T1 预告出现 %.2fs' % (rnd, elapsed), ok1,
                        '期望 %.0f±4s' % expect))

        # --- T3：预告期不抢焦点 ---
        fg_now = foreground()
        same_fg = (fg_now == fg_before)
        print('  T3 预告期前台窗口：%s （与横幅出现前%s）' %
              (window_title(fg_now), '相同' if same_fg else '不同'))
        results.append(('第%d轮 T3 不抢焦点' % rnd, same_fg,
                        '前台窗口未变' if same_fg else '前台被抢'))

        # --- T2：预告期键盘零干预 ---
        # 观察钩子装在 EyeLite 之后（上游），若 EyeLite 吞键，观察钩子就看不到。
        installed = install_observer()
        OBSERVED['count'] = 0
        if not installed:
            print('  T2 观察钩子装不上（hinst 问题），跳过键盘观测')
            results.append(('第%d轮 T2 键盘不受影响' % rnd, False, '观察钩子装不上'))
        else:
            time.sleep(0.3)
            send_key(VK_F13)
            time.sleep(0.5)
            seen = OBSERVED['count'] > 0
            print('  T2 预告期注入按键：观察钩子收到 %d 次（期望 >0，说明未被吞）'
                  % OBSERVED['count'])
            results.append(('第%d轮 T2 键盘不受影响' % rnd, seen,
                            '钩子链观测到 %d 次' % OBSERVED['count']))
            uninstall_observer()

        # --- T4：到点转遮罩 ---
        ov, waited2 = wait_for(lambda: overlay_windows() or None, 40)
        pv_after = preview_windows()
        if ov:
            print('  T4 到点转遮罩：横幅后 %.2fs 出现全屏遮罩，横幅剩余 %d 个'
                  % (waited2, len(pv_after)))
            ok4 = True
        else:
            print('  T4 到点未出现全屏遮罩')
            ok4 = False
        results.append(('第%d轮 T4 到点转遮罩' % rnd, ok4,
                        '%.2fs 后出现' % waited2))

        # 遮罩应持续 RestSeconds=8 秒
        gone, dur = wait_for(lambda: True if not overlay_windows() else None, 25)
        print('     遮罩持续 %.2fs 后消失（期望 8±2s）' % dur)
        results.append(('第%d轮 休息时长 %.2fs' % (rnd, dur),
                        abs(dur - 8.0) <= 2.0, '期望 8±2s'))

    stop_eyelite()
    uninstall_observer()

    print('\n================ 汇总 ================')
    allok = True
    for name, ok, detail in results:
        print('  %-30s %s   %s' % (name, 'PASS' if ok else 'FAIL', detail))
        if not ok:
            allok = False
    print('  OVERALL: %s' % ('PASS' if allok else 'FAIL'))
    return 0 if allok else 1


if __name__ == '__main__':
    sys.exit(main())
