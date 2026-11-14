"""决定性实验：R2 下「首键 keydown 丢失、keyup 仍在」的归因。

观测到的现象：遮罩期间注入 xyzw，第一轮只有 X 的 keydown 没到下游观察钩子，
但 X 的 keyup 到了。

需要区分三种可能：
  (a) EyeLite 吞了 X 的 keydown（真实行为，源码里却没有"只吞 down 不吞 up"的路径）
  (b) 输入链路/钩子链重建导致该事件丢失（测试假象）
  (c) Python 回调里抛异常导致 ctypes 返回 0（等价于吞键，典型假象）

判别手段：同时装**上游**与**下游**两个观察钩子。
  上游（EyeLite 之后装）能看到 EyeLite 吞掉的每一个键 —— 已实测 F4。
  下游（EyeLite 之前装）只能看到 EyeLite 放行的键。
  * 上游看到 X keydown、下游看不到 -> EyeLite 吞的
  * 上游也看不到            -> 事件在系统层就没了（链路/注入问题）
  另外给回调加 try/except + 异常计数，排除 (c)。
"""
import ctypes
import os
import subprocess
import sys
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
u.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD,
                           ctypes.c_ulonglong]
u.IsWindowVisible.argtypes = [wintypes.HWND]
u.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u.GetWindowThreadProcessId.restype = wintypes.DWORD

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101

INST = ctypes.cast(ctypes.WinDLL('user32')._handle, wintypes.HINSTANCE)
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CFG = os.environ.get('EYELITE_CONFIG',
                     os.path.join(os.environ['APPDATA'], 'EyeLite', 'config.ini'))
EXE = os.environ.get('EYELITE_EXE', os.path.join(REPO, 'EyeLite.exe'))

down_ev = []
up_ev = []
errors = []
h_down = [None]
h_up = [None]


def _mk(tag, store):
    def proc(ncode, wparam, lparam):
        try:
            if ncode >= 0:
                msg = wparam & 0xFFFFFFFF
                vk = ctypes.cast(lparam, ctypes.POINTER(ctypes.c_int)).contents.value
                if msg in (WM_KEYDOWN, WM_KEYUP):
                    store.append((time.time(), msg, vk, tag))
        except Exception as e:
            errors.append((tag, repr(e)))
        return u.CallNextHookEx(h_down[0] if tag == 'down' else h_up[0],
                                ncode, wparam, lparam)
    return HOOKPROC(proc)


cb_down = _mk('down', down_ev)
cb_up = _mk('up', up_ev)


def eye_pid():
    out = subprocess.run(
        ['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
        capture_output=True).stdout.decode('gbk', 'ignore').strip()
    if not out or 'No tasks' in out:
        return 0
    try:
        return int(out.split(',')[1].strip('" '))
    except (ValueError, IndexError):
        return 0


def overlays(pid):
    hits = []
    PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(hh, l):
        p = wintypes.DWORD(0)
        u.GetWindowThreadProcessId(hh, ctypes.byref(p))
        if p.value == pid and u.IsWindowVisible(hh):
            r = wintypes.RECT()
            u.GetWindowRect(hh, ctypes.byref(r))
            if (r.right - r.left) > 1200 and (r.bottom - r.top) > 700:
                hits.append(hh)
        return True

    u.EnumWindows(PROC(cb), 0)
    return hits


def send(s, gap=0.10):
    for ch in s:
        vk = ord(ch.upper())
        u.keybd_event(vk, 0, 0, 0)
        time.sleep(0.04)
        u.keybd_event(vk, 0, 2, 0)
        time.sleep(gap)


def summarize(tag):
    seq = [(m, vk) for (_, m, vk, t) in
           (down_ev + up_ev) if t == tag]
    return seq


def main():
    strict = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    subprocess.run(['taskkill', '/IM', 'EyeLite.exe', '/F'], capture_output=True)
    time.sleep(1.0)

    # 下游观察钩子（先装 -> 在链条下游）
    h_down[0] = u.SetWindowsHookExW(WH_KEYBOARD_LL, cb_down, INST, 0)
    print('下游观察钩子 handle =', h_down[0], 'err =', k32.GetLastError())
    time.sleep(0.5)

    body = ("# EyeLite config\n[General]\n"
            "WorkMinutes=1\nRestSeconds=120\nPomodoroEnabled=0\n"
            "PomodoroWork=25\nPomodoroRest=5\nAllowSkip=1\nPostponeMinutes=5\n"
            "IdleMinutes=0\nSoundEnabled=0\nStrictMode=%d\nStartWithWindows=0\n"
            "SedentaryEnabled=0\nSedentaryMinutes=45\nSedentarySeconds=120\n"
            "CameraCountEnabled=0\n" % strict)
    with open(CFG, 'w', encoding='utf-8', newline='\n') as f:
        f.write(body)

    subprocess.Popen([EXE], cwd=os.path.dirname(EXE))
    time.sleep(3.0)
    pid = eye_pid()
    print('EyeLite pid =', pid, ' StrictMode =', strict)

    t0 = time.time()
    while time.time() - t0 < 150:
        if overlays(pid):
            break
        time.sleep(0.25)
    print('overlay appeared after %.1fs' % (time.time() - t0))

    # 遮罩已弹出 -> EyeLite 的钩子已装 -> 现在装上游观察钩子
    time.sleep(0.5)
    h_up[0] = u.SetWindowsHookExW(WH_KEYBOARD_LL, cb_up, INST, 0)
    print('上游观察钩子 handle =', h_up[0], 'err =', k32.GetLastError())
    time.sleep(1.0)

    for rnd in range(1, 4):
        del down_ev[:]
        del up_ev[:]
        send('xyzw')
        time.sleep(1.0)
        d = [(m, vk) for (_, m, vk, t) in down_ev if t == 'down']
        p = [(m, vk) for (_, m, vk, t) in up_ev if t == 'up']
        print()
        print('  round %d' % rnd)
        print('    下游(能看到EyeLite放行的): %s' % d)
        print('    上游(能看到EyeLite吞掉的)  : %s' % p)
        print('    回调异常数: %d %s' % (len(errors), errors[:3]))

    print()
    print('--- 结论辅助：上游出现但下游没出现的 vk = EyeLite 吞掉的键 ---')
    if down_ev:
        pass


if __name__ == '__main__':
    main()
