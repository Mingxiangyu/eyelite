#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""观察钩子自检：不启动 EyeLite，只验证「观察钩子能否看到注入的按键」。

如果自检都收不到，那就是仪器坏了（注入方式 / 钩子安装 / 回调签名），
而不是 EyeLite 吞了键。a11-case.py 的方法论：每次观测前必须先自检仪器。
"""
import ctypes
import ctypes.wintypes as wt
import time

# LRESULT CALLBACK(int nCode, WPARAM wParam, LPARAM lParam) —— 回调只有 3 个参数。
# INFUNCTYPE 的 argtypes 个数必须与回调形参个数一致：多写一个 HWND 会让 ctypes
# 传 4 个实参，回调每次都抛 TypeError，而 ctypes 会把回调里的异常静默吞掉，
# 表现就是"观察钩子装上了但永远收不到事件"（2026-11-14 实测踩过）。
HOOKPROC_T = ctypes.WINFUNCTYPE(ctypes.c_longlong, ctypes.c_int,
                                wt.WPARAM, wt.LPARAM)
u = ctypes.WinDLL('user32', use_last_error=True)
u.SetWindowsHookExW.restype = wt.HANDLE
u.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC_T, wt.HINSTANCE, wt.DWORD]
u.UnhookWindowsHookEx.argtypes = [wt.HANDLE]
u.CallNextHookEx.restype = ctypes.c_longlong
u.CallNextHookEx.argtypes = [wt.HANDLE, ctypes.c_int, wt.WPARAM, wt.LPARAM]
u.keybd_event.argtypes = [wt.BYTE, wt.BYTE, wt.DWORD, ctypes.c_void_p]

events = []
_handle = [None]


def _cb(code, wparam, lparam):
    if code >= 0:
        msg = wparam & 0xFFFFFFFF
        if msg in (0x0100, 0x0101, 0x0104, 0x0105):
            vk = ctypes.cast(lparam, ctypes.POINTER(ctypes.c_int)).contents.value
            events.append((time.time(), msg, vk))
    return u.CallNextHookEx(_handle[0], code, wparam, lparam)

cb = HOOKPROC_T(_cb)

hinst = ctypes.cast(ctypes.WinDLL('user32')._handle, wt.HINSTANCE)
_handle[0] = u.SetWindowsHookExW(13, cb, hinst, 0)
print('hook handle = %s  lasterror=%d' % (_handle[0], ctypes.get_last_error()))

if not _handle[0]:
    raise SystemExit('钩子装不上，无法自检')

time.sleep(0.3)

for name, vk in (('F13', 0x7C), ('A', 0x41)):
    n0 = len(events)
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.03)
    u.keybd_event(vk, 0, 2, 0)
    time.sleep(0.3)
    print('  注入 %s (vk=0x%02X) -> 观察钩子新增 %d 条事件'
          % (name, vk, len(events) - n0))

for e in events:
    print('    msg=0x%04X vk=0x%02X' % (e[1], e[2]))

u.UnhookWindowsHookEx(_handle[0])
print('自检结论：%s' % ('PASS 观察钩子能看到注入的按键'
                        if events else 'FAIL 观察钩子看不到注入的按键（仪器问题）'))
