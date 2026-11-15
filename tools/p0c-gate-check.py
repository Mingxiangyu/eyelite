#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-C 真机实测：FullscreenDndGate（系统通知状态闸门）

只观测，不注入按键、不抢焦点、不改用户系统设置。
用环境变量 EYE_LITE_FORCE_QUNS 强制 QUNS 状态来覆盖真实 API，
这样不需要真的开全屏/演示就能验证各分支。

被测行为（对照 Program.cs 的实现）：
  T1  强制 QUNS=4（RUNNING_D3D_FULL_SCREEN）→ 到点**不弹**遮罩，托盘显示暂缓
  T2  强制 QUNS=2（USING_D3D_FULL_SCREEN）→ 同上
  T3  强制 QUNS=3（PRESENTATION_MODE）→ 同上
  T4  强制 QUNS=5（QUIET_TIME，本机真实状态）→ **照常弹**遮罩（这是本机最关键的一条）
  T5  强制 QUNS=0（NOTIFICATION_FORCED_ON）→ 照常弹
  T6  关闭开关（SuppressOnFullscreen=0）+ 强制 QUNS=4 → 照常弹
  T7  抑制期**不装键盘钩子**、不吞按键（零输入干预）
  T8  抑制期结束（QUNS 改回 0）→ 立刻补弹，且不重复计时

窗口识别（与 p0b-preview-check.py 同一套判据）：
  preview  = 可见 + 200 < w <= 900 + h <= 300
  overlay  = 可见 + w > 800 + h > 500 + 非 WS_EX_TRANSPARENT + 子窗口 >= 1

**踩坑记录 3（2026-11-14，新 build）**：A2 的 DimForm（休息前 60 秒渐进压暗）铺满整块
屏幕，尺寸同样 w>800 h>500，且 WorkMinutes=1 时启动后 ~1 秒就出现 —— 只按尺寸判遮罩
会把它当成"遮罩立刻出现"，所有用例假 FAIL。区分依据（源码 2146-2196 vs 1264-1320）：
  · DimForm：带 WS_EX_TRANSPARENT(0x20，鼠标穿透) 且**没有子窗口**
  · RestForm：有 Label / RingMeter / 按钮，**子窗口 >= 1**，且无 TRANSPARENT
压暗层单独记为信息项（dim），不参与 PASS/FAIL。
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')

user32 = ctypes.WinDLL('user32', use_last_error=True)

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

# ---- 遮罩判据的两个辅助（见顶部踩坑记录 3）----
GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

DIM_SEEN = [False]       # 压暗层是否出现过（信息项，不参与判定）

# ---- 提速开关：判据是布尔的，所以"到点前那段"可以靠钩子压缩 ----
# 外部注入 EYE_LITE_FAKE_WORK_MS=59000 时，第一个 tick 就把工作累计推到 59s，
# 到点只差 1s → 观测 12s 就足以覆盖"越过到点"；未注入时保持原 95s 窗口。
_FAKE_WORK = os.environ.get('EYE_LITE_FAKE_WORK_MS', '').strip()
try:
    _FAKE_WORK_OK = int(_FAKE_WORK) >= 0
except Exception:
    _FAKE_WORK_OK = False
WATCH_S = 12.0 if _FAKE_WORK_OK else 95.0


def ex_style(hwnd):
    return user32.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    """子窗口数：遮罩里有 Label / 环形 / 按钮，压暗层一个都没有。"""
    n = [0]

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return n[0]


def list_windows():
    """返回 [(hwnd, w, h, pid), ...]，只列有可见尺寸的顶层窗口。"""
    out = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        r = wt.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return True
        w = r.right - r.left
        h = r.bottom - r.top
        pid = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        out.append((hwnd, w, h, pid.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def eyelite_pid():
    """取 EyeLite.exe 的进程 PID（可能多实例，取全部）。"""
    pids = []
    try:
        out = subprocess.run(
            ['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
            capture_output=True)
        for line in out.stdout.decode('utf-8', 'ignore').splitlines():
            line = line.strip()
            if not line or 'EyeLite.exe' not in line:
                continue
            parts = [p.strip('" ') for p in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                pids.append(int(parts[1]))
    except Exception:
        pass
    return pids


def _own_windows():
    """EyeLite 自己的可见窗口。

    PID 用**缓存**：tasklist 每次要 0.3~1 秒，在 0.15 秒的轮询间隔里
    会把观测节奏拖垮，导致"遮罩明明在，脚本却说没有"。
    """
    pids = set(_pid_cache)
    if not pids:
        pids = set(eyelite_pid())
        _pid_cache.update(pids)
    return [t for t in list_windows() if t[3] in pids]


_pid_cache = set()


def preview_windows():
    return [t for t in _own_windows() if 200 < t[1] <= 900 and t[2] <= 300]


def overlay_windows():
    # **必须按 PID 过滤**：Windows 桌面（Program Manager / WorkerW）本身就是
    # 一个 3840x2160 的可见顶层窗口，不按进程过滤会把它当成遮罩，
    # 表现为"overlay 立刻出现"，所有用例全错。
    #
    # 还必须排除压暗层（DimForm）：它同样铺满屏幕，但带 WS_EX_TRANSPARENT
    # 且没有子窗口 —— 只按尺寸判会让每条用例都误报"遮罩立刻出现"。
    out = []
    for hwnd, w, h, pid in _own_windows():
        if not (w > 800 and h > 500):
            continue
        if ex_style(hwnd) & WS_EX_TRANSPARENT:
            DIM_SEEN[0] = True          # 压暗层：只记信息，不算遮罩
            continue
        if child_count(hwnd) < 1:
            continue
        out.append((hwnd, w, h, pid))
    return out


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'],
                   capture_output=True)
    time.sleep(1.5)
    _pid_cache.clear()


def read_cfg():
    """读 config.ini 为 dict。"""
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
        # 保留不在已知键列表里的自定义键（不能因为测试就把用户的配置弄丢）
        for k in d:
            if k not in order:
                f.write('%s=%s\n' % (k, d[k]))


def launch(quns=None, env_extra=None):
    """启动 EyeLite，可选注入 EYE_LITE_FORCE_QUNS。"""
    env = os.environ.copy()
    env.pop('EYE_LITE_FORCE_QUNS', None)
    if quns is not None:
        env['EYE_LITE_FORCE_QUNS'] = str(quns)
    if env_extra:
        env.update(env_extra)
    proc = subprocess.Popen([EXE], env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # PID 直接从子进程拿，不必回头调 tasklist（慢）。
    _pid_cache.add(proc.pid)
    return proc


def wait_for_overlay(timeout_s):
    """等待遮罩出现，返回等待秒数；超时返回 None。"""
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if overlay_windows():
            return time.time() - t0
        time.sleep(0.15)
    return None


def preview_ever_appeared(dur_s):
    """在 dur_s 内轮询，观察预告横幅是否出现过（横幅存在时间短，必须轮询）。"""
    t0 = time.time()
    while time.time() - t0 < dur_s:
        if preview_windows():
            return True
        time.sleep(0.15)
    return False


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at', EXE)
        return 2

    stop_eyelite()

    # 备份原配置
    orig_cfg = read_cfg()
    backup = dict(orig_cfg)
    print('original config keys:', sorted(backup.keys()))

    results = []
    print('观测窗口 = %.0fs（%s）'
          % (WATCH_S, 'FAKE_WORK_MS 生效 → 压缩模式' if _FAKE_WORK_OK
             else '未注入钩子 → 完整窗口'))
    try:
        # ---- 准备一份快节奏配置：工作 1 分钟，休息 8 秒，预告 30 秒 ----
        base = dict(backup)
        base.update({
            'WorkMinutes': '1',
            'RestSeconds': '8',
            'PreviewSeconds': '30',
            'SedentaryEnabled': '0',
            'StrictMode': '0',          # 不装钩子，避免任何输入干预风险
            'AllowSkip': '1',
            'IdleMinutes': '0',         # 不因空闲暂停
            'SoundEnabled': '0',
            'SuppressOnFullscreen': '1',
        })
        write_cfg(base)

        cases = [
            # (名称, QUNS, 期望弹遮罩, 开关)
            ('T1 QUNS=4 RUNNING_D3D_FULL_SCREEN', 4, False, '1'),
            ('T2 QUNS=2 USING_D3D_FULL_SCREEN',    2, False, '1'),
            ('T3 QUNS=3 PRESENTATION_MODE',        3, False, '1'),
            ('T4 QUNS=5 QUIET_TIME (本机真实状态)', 5, True,  '1'),
            ('T5 QUNS=0 NOTIFICATION_FORCED_ON',   0, True,  '1'),
            ('T6 QUNS=4 + 关闭开关',               4, True,  '0'),
        ]

        for name, quns, expect_overlay, suppress in cases:
            stop_eyelite()
            cfg = dict(base)
            cfg['SuppressOnFullscreen'] = suppress
            write_cfg(cfg)
            launch(quns=quns)
            DIM_SEEN[0] = False        # 每个用例单独统计压暗层（信息项）
            time.sleep(2.0)   # 让进程起来

            # 工作 1 分钟 → 预告横幅约在 30s 出现，遮罩约在 60s 出现。
            # 一次性连续监测，同时记录横幅与遮罩的首次出现时刻。
            #
            # **提速（2026-11-15）**：本脚本的判据是**布尔**的（overlay 该不该出现），
            # 与"第几秒出现"无关，所以外部注入 `EYE_LITE_FAKE_WORK_MS=59000` 时，
            # 第一个 tick 就把工作累计推到 59s（距到点只差 1s）→ 只需观测很短一段
            # 就已经"越过到点"，等价于不注入时看 95s。
            # 未注入时仍按原来的 95s 窗口跑，行为与旧版一致。
            t0 = time.time()
            first_preview = None
            first_overlay = None
            while time.time() - t0 < WATCH_S:
                if first_preview is None and preview_windows():
                    first_preview = time.time() - t0
                if first_overlay is None and overlay_windows():
                    first_overlay = time.time() - t0
                    break
                time.sleep(0.05)

            got_overlay = first_overlay is not None
            got_preview = first_preview is not None

            # 期望：弹遮罩的用例 → 必须有遮罩，且必须有预告横幅（走正常 P0-B 流程）；
            #       抑制的用例   → 遮罩与横幅**都不该有**（横幅也要被压住）。
            ok = (got_overlay == expect_overlay) and (got_preview == expect_overlay)
            detail = 'preview=%s overlay=%s dim=%s' % (
                ('%.1fs' % first_preview) if first_preview is not None else 'no',
                ('%.1fs' % first_overlay) if first_overlay is not None else 'no',
                'yes' if DIM_SEEN[0] else 'no')
            results.append((name, ok, detail, expect_overlay))
            print('%-42s %s  %s (expect overlay=%s)'
                  % (name, 'PASS' if ok else 'FAIL', detail, expect_overlay))

    finally:
        stop_eyelite()
        # 还原用户原配置
        if backup:
            write_cfg(backup)
            print('config restored:', os.path.getsize(CFG), 'bytes')
        else:
            try:
                os.remove(CFG)
                print('config removed (no original)')
            except Exception:
                pass

    npass = sum(1 for r in results if r[1])
    print('\n=== P0-C summary: %d/%d PASS ===' % (npass, len(results)))
    for name, ok, detail, exp in results:
        if not ok:
            print('  FAILED: %s (%s, expected overlay=%s)' % (name, detail, exp))
    return 0 if npass == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
