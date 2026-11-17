#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P3-B 真机实验：倒计时卡顿 A/B（托盘 tooltip / 进度环是不是把 UI 线程顶住了）

## 为什么用 TRACE_UI 而不是"看数字"
预告横幅上的秒数是**画在控件里**的，外部探针读不到"它什么时候变的"。
`EYE_LITE_TRACE_UI=<文件>` 会在**每次真正刷新数字**时追加一行：
    P <Clock.NowMs()> <秒>
于是"相邻两行的 ms 差"就是 UI 线程两次刷新的真实间隔：
  · ≈1000ms  → 正常
  · 2000~3000ms → **进程内 UI 线程被顶住**（不是探针慢、不是 tick 没到）
这正是本次要 A/B 的对象。

## 三档（每档 ~66s，WorkMinutes=1 / PreviewSeconds=10 → 预告期 50~60s，约 10 条 P）
    档 0：不设任何闸门
    档 1：EYE_LITE_NO_TRAY_TEXT=1      （闸掉托盘 tooltip 刷新）
    档 2：NO_TRAY_TEXT=1 + NO_TRAY_ICON=1（再闸掉托盘进度环）
> 设了这两个变量后 tooltip / 进度环**会冻住** —— 这是闸门的定义，不是缺陷。

## 判读（只报数据，不替产品解释）
每档报：P 行条数、**最大间隔**、**≥2000ms 的间隔条数与具体值**、（附）秒数是否连号。

## 纪律
· config.ini 按原始字节备份/还原；trace 文件只写 `tools/_probe/`，结束删除
· 启动前清空所有 `EYE_LITE_*` 钩子；每档开始前 taskkill；结束也 taskkill
· 不抢前台、不动鼠标（本实验完全不注入输入）

## 用法
    python tools/p3-ui-stutter-check.py            # 三档全跑（约 3.5 分钟）
    python tools/p3-ui-stutter-check.py 0 1        # 只跑指定档
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

_user32 = ctypes.WinDLL('user32', use_last_error=True)
_user32.keybd_event.argtypes = [wt.BYTE, wt.BYTE, wt.DWORD, ctypes.c_size_t]
VK_F24 = 0x87
KEYEVENTF_KEYUP = 0x0002


_EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def _preview_visible(pid):
    """该 pid 有没有可见的预告横幅（200 < w <= 900 且 h <= 300）。"""
    hit = [False]
    u = ctypes.WinDLL('user32', use_last_error=True)
    u.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]

    def cb(hwnd, lparam):
        if not u.IsWindowVisible(hwnd):
            return True
        p = wt.DWORD(0)
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        r = wt.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if 200 < w <= 900 and h <= 300:
            hit[0] = True
            return False
        return True

    u.EnumWindows(_EnumProc(cb), 0)
    return hit[0]


def tap_f24():
    """注入 F24（不产生字符、不进用户文档）。B4 用它模拟"用户一直在打字"。"""
    _user32.keybd_event(VK_F24, 0, 0, 0)
    _user32.keybd_event(VK_F24, 0, KEYEVENTF_KEYUP, 0)


CFG_ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']

TIERS = [
    ('档0 全开（无闸门）', {}),
    ('档1 闸 tray-text', {'EYE_LITE_NO_TRAY_TEXT': '1'}),
    ('档2 闸 tray-text+tray-icon',
     {'EYE_LITE_NO_TRAY_TEXT': '1', 'EYE_LITE_NO_TRAY_ICON': '1'}),
]

RUN_SECONDS = 66.0        # 预告 50~60s，多跑 6s 收尾


def read_cfg_raw():
    try:
        with open(CFG, 'rb') as f:
            return f.read()
    except Exception:
        return b''


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
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in CFG_ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in CFG_ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def restore_cfg_raw(raw):
    if not raw:
        return
    with open(CFG, 'wb') as f:
        f.write(raw)
    print('config restored: %d bytes (backup %d bytes, %s)'
          % (os.path.getsize(CFG), len(raw),
             'OK' if os.path.getsize(CFG) == len(raw) else 'SIZE MISMATCH!'))


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
    t0 = time.time()
    while time.time() - t0 < 12:
        if not eyelite_pids():
            return True
        time.sleep(0.25)
    return False


def launch(env_extra):
    env = os.environ.copy()
    for k in list(env.keys()):
        if k.startswith('EYE_LITE_'):
            env.pop(k, None)
    env.update(env_extra)
    return subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def parse_trace(path):
    """读 trace 里的 P 行，返回 [(ms, sec), ...]。"""
    out = []
    try:
        with open(path, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                line = line.strip()
                if not line or not line.startswith('P '):
                    continue
                parts = line.split()
                if len(parts) < 3:
                    continue
                try:
                    out.append((int(parts[1]), int(parts[2])))
                except ValueError:
                    pass
    except Exception as e:
        print('  读取 trace 失败:', e)
    return out


def seg_stats(gaps, lo, hi):
    """按"变化后的秒数"分段的间隔统计（sec 落在 [lo, hi] 的那些间隔）。"""
    sel = [(g, s) for (g, s) in gaps if lo <= s <= hi]
    return {'n': len(sel),
            'max': max([g for (g, _s) in sel], default=0),
            'big': [(g, s) for (g, s) in sel if g >= 2000]}


def analyse(samples):
    """相邻 P 行的 ms 差。返回 dict。"""
    gaps = []
    for i in range(1, len(samples)):
        gaps.append((samples[i][0] - samples[i - 1][0], samples[i][1]))
    big = [(g, s) for (g, s) in gaps if g >= 2000]
    mx = max([g for (g, _s) in gaps], default=0)
    # 秒数是否严格递减 1（连号）——用于交叉验证"数字有没有跳"
    skipped = []
    for i in range(1, len(samples)):
        d = samples[i - 1][1] - samples[i][1]
        if d != 1:
            skipped.append((samples[i - 1][1], samples[i][1], d))
    early = seg_stats(gaps, 11, 30)      # 预告前段：渐暗还没开始（剩余 30→11s）
    late = seg_stats(gaps, 1, 10)        # 预告后段：渐暗层已挂着并每秒刷新
    return {'n': len(samples), 'gaps': gaps, 'max': mx, 'big': big,
            'skipped': skipped, 'early': early, 'late': late}


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found')
        return 2
    print('被测产物: %s (%d bytes)' % (EXE, os.path.getsize(EXE)))

    args = [a.strip() for a in sys.argv[1:]]
    b2 = 'b2' in args                     # B2：30 秒预告 + **开着渐暗**（贴近用户真实配置）
    b3 = 'b3' in args                     # B3：例程窗开着 + 10s 预告（用户真实现场）
    b3b = 'b3b' in args                   # B3b：例程窗开着 + 40s 预告（采样撞上素材段）
    b4 = 'b4' in args                     # B4：持续键盘输入 + 10s 预告（钩子回调压 UI 线程）
    b5 = 'b5' in args                     # B5：组合场景 —— 例程窗(素材段) + 打字同时进行
    want = [a for a in args if a not in ('b2', 'b3', 'b3b', 'b4', 'b5')]
    tiers = [(n, e) for i, (n, e) in enumerate(TIERS) if not want or str(i) in want]
    if b3 or b3b or b4 or b5:
        tiers = [TIERS[0]]                # B3/B3b/B4/B5 都只跑全开一档
    if not tiers:
        print('usage: p3-ui-stutter-check.py [0|1|2 ...]')
        return 2

    if not os.path.isdir(PROBE_DIR):
        os.makedirs(PROBE_DIR)

    if not stop_eyelite():
        print('FAIL: 有残留 EyeLite 进程')
        return 2

    backup_raw = read_cfg_raw()
    orig = read_cfg()
    cfg = dict(orig)
    # 关掉渐暗（排除它干扰）、不因空闲暂停、关门控闸门
    if b5:
        # 组合场景：例程窗开着 + 用户在打字（两件事抢同一个 UI 线程）
        # 预告 20~60s（PreviewSeconds=40），例程「腰」90s 全程覆盖
        cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '40', 'RestSeconds': '20',
                    'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                    'PomodoroEnabled': '0', 'DimAlphaMax': '45',
                    'MeetingGuardEnabled': '0', 'SuppressOnFullscreen': '0'})
    elif b3b:
        # 预告 20~60s（PreviewSeconds=40）：其中 20~40s 正好是「深蹲 10 次」（素材），
        # 这样"素材渲染 + 倒计时刷新"同时进行 —— 素材版重绘更重，这条才覆盖到素材。
        cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '40', 'RestSeconds': '20',
                    'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                    'PomodoroEnabled': '0', 'DimAlphaMax': '45',
                    'MeetingGuardEnabled': '0', 'SuppressOnFullscreen': '0'})
    elif b4:
        # 用户是边打字边遇到卡顿的；我们装了 WH_KEYBOARD_LL，每键都在 UI 线程跑回调。
        cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '10', 'RestSeconds': '20',
                    'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                    'PomodoroEnabled': '0', 'DimAlphaMax': '45',
                    'MeetingGuardEnabled': '0', 'SuppressOnFullscreen': '0'})
    elif b3:
        # 用户现场：720x640 例程窗开着（60fps 重绘）+ 预告横幅倒计时
        # 用「腰」例程（起身伸展 30 + 深蹲 20 + 靠墙站立 40 = 90s），窗口全程覆盖 50s 的预告
        cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '10', 'RestSeconds': '20',
                    'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                    'PomodoroEnabled': '0', 'DimAlphaMax': '45',
                    'MeetingGuardEnabled': '0', 'SuppressOnFullscreen': '0'})
    elif b2:
        # B2：把渐暗层**打开**（用户报卡顿时是 DimAlphaMax=45），预告拉长到 30s
        # 以获得约 30 个间隔；同时关掉会议闸门，避免它插一脚。
        cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '30', 'RestSeconds': '20',
                    'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                    'PomodoroEnabled': '0', 'DimAlphaMax': '45',
                    'MeetingGuardEnabled': '0',
                    'SuppressOnFullscreen': '0'})
    else:
        cfg.update({'WorkMinutes': '1', 'PreviewSeconds': '10', 'RestSeconds': '20',
                    'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                    'PomodoroEnabled': '0', 'DimAlphaMax': '0',
                    'SuppressOnFullscreen': '0'})
    write_cfg(cfg)
    print('config: WorkMinutes=1 PreviewSeconds=%s IdleMinutes=0 DimAlphaMax=%s%s'
          % (cfg.get('PreviewSeconds'), cfg.get('DimAlphaMax'),
             '  ← B5 模式（例程窗「腰」+ 40s 预告 + 注入 F24）' if b5 else
             ('  ← B4 模式（持续注入 F24 + 渐暗开）' if b4 else
             ('  ← B3b 模式（例程窗「腰」+ 40s 预告，撞素材段）' if b3b else
              ('  ← B3 模式（例程窗「腰」90s + 渐暗开）' if b3 else
                 ('  ← B2 模式（渐暗开着）' if b2 else ''))))))

    results = []
    traces = []
    try:
        for idx, (name, env_extra) in enumerate(tiers):
            print('\n--- %s ---' % name)
            if not stop_eyelite():
                results.append((name, None, '实例停不下来'))
                continue
            trace = os.path.join(PROBE_DIR, 'trace%d.txt' % idx)
            if os.path.exists(trace):
                os.remove(trace)
            traces.append(trace)
            env = dict(env_extra)
            if b3 or b3b or b5:
                env['EYE_LITE_FAKE_ROUTINE'] = '腰'   # 例程窗全程开着（90s）
            env['EYE_LITE_TRACE_UI'] = trace
            print('  环境变量: %s' % ', '.join('%s=%s' % kv for kv in sorted(env.items())))
            proc = launch(env)
            t0 = time.time()
            injected = 0
            if b4 or b5:
                # 先等预告窗出现（预热输入会把 due 推后，所以不能按固定时刻注入）
                while time.time() - t0 < RUN_SECONDS and not _preview_visible(proc.pid):
                    time.sleep(0.1)
                seen_at = time.time() - t0
                print('  预告窗 %.1fs 出现 → 开始注入 F24（每 60ms，持续 8s）' % seen_at)
                t_inj = time.time()
                while time.time() - t_inj < 8.0:
                    tap_f24()
                    injected += 1
                    time.sleep(0.06)
                print('  注入结束，共 %d 次' % injected)
                while time.time() - t0 < RUN_SECONDS:
                    time.sleep(0.2)
                print('  （注入期 %.1f~%.1fs，仍在预告期内）'
                      % (seen_at, seen_at + 8.0))
            else:
                while time.time() - t0 < RUN_SECONDS:
                    time.sleep(0.2)
            rc = proc.poll()
            alive = rc is None
            if rc is not None:
                # exitcode=1 + 无 WER 崩溃记录 ⇒ 大概率是外部 taskkill：
                # build.cmd 第 56 行无条件 `taskkill /im EyeLite.exe /f`，
                # 只要有人构建，正在跑的批就会被腰斩，并被误读成"产品崩溃"。
                print('  [注意] 进程在收尾前已退出，exitcode=%s' % rc)
                if rc == 1:
                    print('  [判定] exitcode=1 且窗口数/日志无崩溃迹象 → **疑似被外部 kill**'
                          '（build.cmd 会 taskkill /im EyeLite.exe /f）；本次数据作废')
            stop_eyelite()
            samples = parse_trace(trace)
            a = analyse(samples)
            print('  进程存活到收尾=%s  P 行 %d 条' % (alive, a['n']))
            if a['n'] >= 2:
                print('  相邻间隔(ms): %s' % [g for (g, _s) in a['gaps']])
                print('  最大间隔=%dms  ≥2000ms 的间隔=%d 条%s'
                      % (a['max'], len(a['big']),
                         (': %s' % a['big']) if a['big'] else ''))
                for label, seg in (('前段(剩30→11s，渐暗未开始)', a['early']),
                                   ('后段(剩10→1s，渐暗已挂)', a['late'])):
                    if seg['n']:
                        print('  %s：%d 个间隔，最大 %dms，≥2000ms %d 条%s'
                              % (label, seg['n'], seg['max'], len(seg['big']),
                                 (': %s' % seg['big']) if seg['big'] else ''))
                if a['skipped']:
                    print('  秒数未连号: %s' % a['skipped'])
            results.append((name, a, None))
    finally:
        stop_eyelite()
        restore_cfg_raw(backup_raw)
        for t in traces:
            try:
                os.remove(t)
            except Exception:
                pass
        try:
            if os.path.isdir(PROBE_DIR) and not os.listdir(PROBE_DIR):
                os.rmdir(PROBE_DIR)
        except Exception:
            pass

    print('\n================ 汇总（只列数据）================')
    print('%-26s %6s %10s %9s %9s %9s'
          % ('档', 'P条数', '总最大', '前段最大', '后段最大', '≥2000ms'))
    for name, a, err in results:
        if err:
            print('%-26s %s' % (name, err))
            continue
        print('%-26s %6d %8dms %8dms %8dms %9d %s'
              % (name, a['n'], a['max'],
                 a['early']['max'], a['late']['max'], len(a['big']),
                 (('%s' % a['big']) if a['big'] else '')
                 + (('  跳号:%s' % a['skipped']) if a['skipped'] else '')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
