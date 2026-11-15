#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""探针：验证 p0a 第三轮偏晚是不是"启动期被上一个实例的 mutex 卡住"。

假设：EyeLite 用命名 Mutex 做单实例，`!createdNew` 时弹**模态 MessageBox** 并 return。
如果上一轮进程还没完全退出就启动下一轮，新进程会卡在 MessageBox 上——
表现为"进程已启动，但计时根本没开始"，直到某个时刻对话框被关掉才开始跑 60 秒。
历史数据全是**最后一轮**偏晚（63.53 / 63.59 / 73.67），符合"上一轮残留"的特征。

判据：
  A) 每轮之间**等 8 秒**再启动，看偏晚是否消失
  B) 运行期检测**属于 EyeLite 的对话框窗口**（类名 #32770），有 → 坐实
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
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
         'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
         'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
         'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
         'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']
GUARD_OFF = {'ForegroundGuardEnabled': '0', 'TypingGuardEnabled': '0',
             'SittingOptionEnabled': '0'}
CFG_TEST = {'WorkMinutes': '1', 'RestSeconds': '8', 'PreviewSeconds': '0',
            'PomodoroEnabled': '0', 'IdleMinutes': '0', 'SedentaryEnabled': '0',
            'SoundEnabled': '0', 'StrictMode': '0', 'SuppressOnFullscreen': '1',
            'DimAlphaMax': '45'}


def write_cfg(d):
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def raw_cfg():
    try:
        with open(CFG, 'rb') as f:
            return f.read()
    except Exception:
        return b''


def pids():
    out = subprocess.run(['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
                         capture_output=True)
    r = []
    for line in out.stdout.decode('utf-8', 'ignore').splitlines():
        if 'EyeLite.exe' in line:
            p = [x.strip('" ') for x in line.split('","')]
            if len(p) > 1 and p[1].isdigit():
                r.append(int(p[1]))
    return r


def stop(settle=0.0):
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    for _ in range(40):
        if not pids():
            break
        time.sleep(0.25)
    if settle:
        time.sleep(settle)


def windows_of(pid):
    out = []

    def cb(h, l):
        p = wt.DWORD(0)
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            cls = ctypes.create_unicode_buffer(128)
            user32.GetClassNameW(h, cls, 128)
            out.append((h, cls.value, r.right - r.left, r.bottom - r.top,
                        bool(user32.IsWindowVisible(h))))
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return out


def _ex_style(h):
    user32.GetWindowLongPtrW.restype = ctypes.c_ssize_t
    user32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
    return user32.GetWindowLongPtrW(h, -20)


def _child_count(h):
    n = [0]

    def cb(ch, l):
        n[0] += 1
        return True

    user32.EnumChildWindows(h, EnumProc(cb), 0)
    return n[0]


def is_overlay(pid):
    """遮罩判据：大尺寸 + **不带 WS_EX_TRANSPARENT** + 子窗口≥1。

    只看尺寸会把 A2 的 DimForm（铺满屏幕、1.6 秒就出现）当成遮罩 —— 我第一次
    写这个探针就栽在这，测出来"遮罩 1.67s 出现"。压暗层是 TRANSPARENT 且没有子控件。
    """
    for h, cls, w, hgt, vis in windows_of(pid):
        if not vis or w <= 800 or hgt <= 500:
            continue
        if _ex_style(h) & 0x20:
            continue
        if _child_count(h) >= 1:
            return True
    return False


def has_dialog(pid):
    for h, cls, w, hgt, vis in windows_of(pid):
        if vis and cls == '#32770' and w > 100:
            return True
    return False


def one_round(idx, settle):
    stop(settle)
    cfg = dict(CFG_TEST)
    cfg.update(GUARD_OFF)
    write_cfg(cfg)
    t_launch = time.time()
    p = subprocess.Popen([EXE], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    t_ov = None
    saw_dialog = False
    while time.time() - t_launch < 110:
        if has_dialog(p.pid):
            saw_dialog = True
        if is_overlay(p.pid):
            t_ov = time.time() - t_launch
            break
        time.sleep(0.1)
    print('  第 %d 轮（前摇 %.0fs）：遮罩=%s  中途出现对话框=%s'
          % (idx, settle, ('%.2fs' % t_ov) if t_ov is not None else '未出现',
             '是' if saw_dialog else '否'))
    stop(0)
    return t_ov, saw_dialog


def main():
    raw = raw_cfg()
    print('=== 假设验证：p0a 末轮偏晚是否来自"上一轮残留 + 单实例对话框" ===')
    try:
        print('\n[组 1] 轮间不等待（复现原脚本行为）')
        r1 = [one_round(i + 1, 0.0) for i in range(3)]

        print('\n[组 2] 轮间等 8 秒')
        r2 = [one_round(i + 1, 8.0) for i in range(3)]
    finally:
        stop(0)
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('\nconfig restored: %d bytes' % len(raw))

    print('\n=== 汇总 ===')
    for name, rs in (('组1 无等待', r1), ('组2 等 8s', r2)):
        vals = [('%.1f' % v) if v is not None else 'NA' for v, _d in rs]
        dlgs = sum(1 for _v, d in rs if d)
        print('  %-10s 遮罩时刻=%s  出现对话框=%d 次' % (name, vals, dlgs))


if __name__ == '__main__':
    sys.exit(main())
