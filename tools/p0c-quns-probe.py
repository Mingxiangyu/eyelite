#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
探测 SHQueryUserNotificationState 的真实行为（只读调用，不改任何系统状态）。

目的：确认 QUNS_* 常量的实际取值与 API 的可用性，
避免在 C# 里凭记忆写死常量导致判断错。

SHQueryUserNotificationState 位于 shell32.dll，签名：
    HRESULT SHQueryUserNotificationState(DWORD *pQUNS);
"""

import ctypes
import ctypes.wintypes as wt

shell32 = ctypes.WinDLL('shell32', use_last_error=True)

# 候选常量名与它们的已知取值（Windows SDK shellapi.h）
NAMES = {
    0: 'QUNS_NOTIFICATION_FORCED_ON',
    1: 'QUNS_BUSY',
    2: 'QUNS_USING_D3D_FULL_SCREEN',
    3: 'QUNS_PRESENTATION_MODE',
    4: 'QUNS_RUNNING_D3D_FULL_SCREEN',
    5: 'QUNS_QUIET_TIME',
    6: 'QUNS_BUSY_DND',
}

shell32.SHQueryUserNotificationState.argtypes = [ctypes.POINTER(wt.DWORD)]
shell32.SHQueryUserNotificationState.restype = ctypes.c_long

val = wt.DWORD(0xFFFFFFFF)
hr = shell32.SHQueryUserNotificationState(ctypes.byref(val))
print('HRESULT = 0x%08X' % (hr & 0xFFFFFFFF))
print('QUNS value = %d -> %s' % (val.value, NAMES.get(val.value, '(unknown)')))

# Ex 版本（Win10+，额外输出 flags）
try:
    flags = wt.DWORD(0)
    hr2 = shell32.SHQueryUserNotificationStateEx(ctypes.byref(val),
                                                 ctypes.byref(flags))
    print('Ex HRESULT = 0x%08X  value=%d flags=0x%08X'
          % (hr2 & 0xFFFFFFFF, val.value, flags.value))
except AttributeError:
    print('Ex 版本不存在（Win10 之前）')

# 顺带查当前是否有 DND 开关（供对照，不改设置）
try:
    shell32.SHQueryUserNotificationState.restype = ctypes.c_long
    print('shell32 导出里与通知状态相关的符号：')
    import subprocess
    out = subprocess.run(['powershell', '-NoProfile', '-Command',
                          "[System.Environment]::OSVersion.VersionString"],
                         capture_output=True)
    print('  OS = %s' % out.stdout.decode('utf-8', 'ignore').strip())
except Exception as e:
    print('err', e)
