#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
预告横幅"刚出现那两秒"的 A/B 对照：`PreviewSeconds=3` vs `10`，各配 dim=0 / 45。

## 为什么单独写这个
用户观察：`PreviewSeconds=10` 时倒数到 2、1 不卡；`PreviewSeconds=3` 时 2、1 卡两三秒。
→ 卡的不是"某几个数字"，可能是**横幅刚出现后头 1~3 秒**的一次性开销
  （`PreviewForm` 懒创建 + 首次 Show 的 DWM 表面 + `Shown` 里的 `AlignUnitToCountBaseline()`
   那次 `DrawToBitmap` 逐像素扫描）。
之前的 B2/B3/B3b/B4 全用 PreviewSeconds=10/30/40，**从没覆盖"横幅刚弹出的头两秒"**。

## 判据（这轮不看"最大值"，看"间隙落在第几个间隔"）
对每次运行打印：
  · 横幅出现时刻（从进程启动算，窗口轮询得到，不依赖 trace）
  · **P 行的完整间隔序列**：每个间隔标出 `前秒→后秒` 与毫秒数
  · 最大间隔、≥2000ms 条数
假设成立的签名：**间隙只出现在紧挨第一条 P 行之后的 1~2 个间隔**；
若间隙分散在全程或出现在最后，则假设不成立。

## 纪律
· 只 kill **自己的子进程**（`taskkill /PID`），不用 `/im`（今天已被别人的 `/im` 腰斩过两次）
· config.ini 按原始字节备份/还原；trace 只写 tools/_probe/
· 不改 src/

用法：
    python tools/p3-preview-onset-check.py          # 跑 4 组
    python tools/p3-preview-onset-check.py 1 3      # 只跑第 1、3 组
"""

import ctypes
import ctypes.wintypes as wt
import datetime
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE_NAME = 'EyeLite.exe'
for _i, _a in enumerate(sys.argv):
    if _a == '--exe' and _i + 1 < len(sys.argv):
        EXE_NAME = sys.argv[_i + 1]
EXE = os.path.join(ROOT, EXE_NAME)
CFG = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite', 'config.ini')
PROBE = os.path.join(ROOT, 'tools', '_probe')

u = ctypes.WinDLL('user32', use_last_error=True)
u.keybd_event.argtypes = [wt.BYTE, wt.BYTE, wt.DWORD, ctypes.c_size_t]
VK_F24 = 0x87


def tap_f24():
    """注入 F24（不产生字符、不进文档）。用来模拟"用户此刻在打字"。"""
    u.keybd_event(VK_F24, 0, 0, 0)
    u.keybd_event(VK_F24, 0, 0x0002, 0)
u.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
PROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

CASES = [
    ('#1', 3, 0, '复现（排除渐暗）'),
    ('#2', 3, 45, '渐暗开着是否更糟'),
    ('#3', 10, 45, '对照（用户说不卡）'),
    ('#4', 10, 0, '对照（排除渐暗）'),
]
RUN_SECONDS = 66.0


def mask_visible(pid):
    """休息遮罩：大尺寸 + 非 TRANSPARENT + 子窗口≥1（沿用既有判据）。"""
    hit = [False]
    u.GetWindowLongW.restype = ctypes.c_long
    u.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]

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
        if not (w > 800 and h > 500):
            return True
        if u.GetWindowLongW(hwnd, -20) & 0x20:
            return True
        # 光"有子窗口"不够：实测会在启动 1.5~1.7s 误报（大尺寸+非透明+有子窗口的别的窗口）。
        # RestForm 一定有文本子控件，其中包含「休息」二字，用它来认更稳。
        texts = []

        def cb2(ch, lp):
            buf = ctypes.create_unicode_buffer(256)
            u.GetWindowTextW(ch, buf, 256)
            if buf.value:
                texts.append(buf.value)
            return True

        u.EnumChildWindows(hwnd, ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)(cb2), 0)
        if not any('休息' in t for t in texts):
            return True
        hit[0] = True
        return False

    u.EnumWindows(PROC(cb), 0)
    return hit[0]


def banner_visible(pid):
    hit = [False]

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
        if not (200 < w <= 900 and h <= 300):
            return True
        # 必须含「休息」文本：预览横幅有「休息提醒」「快到休息时间了」；
        # 「护眼小助手已经在运行了…」那个 MessageBox 尺寸也落在这个区间，会被误当横幅。
        texts = []

        def cb2(ch, lp):
            buf = ctypes.create_unicode_buffer(256)
            u.GetWindowTextW(ch, buf, 256)
            if buf.value:
                texts.append(buf.value)
            return True

        u.EnumChildWindows(hwnd, ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)(cb2), 0)
        if any('休息' in t for t in texts):
            hit[0] = True
            return False
        return True

    u.EnumWindows(PROC(cb), 0)
    return hit[0]


def banner_child_texts(pid):
    """读出横幅窗口里所有子控件文本（用来观察「正在输入，稍后提醒」这类文案）。"""
    out = []

    def cb(hwnd, lparam):
        if not u.IsWindowVisible(hwnd):
            return True
        q = wt.DWORD(0)
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(q))
        if q.value != pid:
            return True
        r = wt.RECT()
        u.GetWindowRect(hwnd, ctypes.byref(r))
        w, h = r.right - r.left, r.bottom - r.top
        if not (200 < w <= 900 and h <= 300):
            return True
        texts = []

        def cb2(ch, lp):
            buf = ctypes.create_unicode_buffer(256)
            u.GetWindowTextW(ch, buf, 256)
            if buf.value:
                texts.append(buf.value)
            return True

        u.EnumChildWindows(hwnd, ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)(cb2), 0)
        out.extend(texts)
        return False

    u.EnumWindows(PROC(cb), 0)
    return out


def batch_header():
    """本批自证：起跑时刻 / exe 指纹 / 产物里有没有本轮新文案 / 关键配置。

    为什么要有这行：事后发现污染时，能立刻定位"哪几轮在哪个时间窗"，
    不用再翻 trace 反算时间戳。
    """
    import hashlib
    t = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    try:
        size = os.path.getsize(EXE)
        md5 = hashlib.md5(open(EXE, 'rb').read()).hexdigest()
    except Exception as e:
        size, md5 = -1, 'ERR:%s' % e
    # 比 md5 更硬的判据：这行文案只可能由本轮新增的 DueHeldText() 产生
    needle = '正在输入，稍后提醒'.encode('utf-16-le')
    try:
        has_new = needle in open(EXE, 'rb').read()
    except Exception:
        has_new = False
    cfg = read_cfg()
    print('=== 本批起跑 %s ===' % t)
    print('    exe=%s  size=%d  md5=%s' % (EXE_NAME, size, md5))
    print('    产物含新文案「正在输入，稍后提醒」= %s %s'
          % (has_new, '' if has_new else '  ← 警告：可能不是修后版！'))
    print('    cfg: WorkMinutes=%s PreviewSeconds=%s IdleMinutes=%s DimAlphaMax=%s'
          % (cfg.get('WorkMinutes'), cfg.get('PreviewSeconds'),
             cfg.get('IdleMinutes'), cfg.get('DimAlphaMax')))


def read_cfg_raw():
    try:
        with open(CFG, 'rb') as f:
            return f.read()
    except Exception:
        return b''


def read_cfg():
    d = {}
    try:
        for line in open(CFG, encoding='utf-8-sig'):
            line = line.strip()
            if '=' in line and not line.startswith('#') and not line.startswith('['):
                k, v = line.split('=', 1)
                d[k.strip()] = v.strip()
    except Exception:
        pass
    return d


ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
         'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
         'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
         'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
         'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax',
         'MeetingGuardEnabled']


def write_cfg(d):
    os.makedirs(os.path.dirname(CFG), exist_ok=True)
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def kill_pid(pid):
    """只杀自己的子进程 —— 不用 /im（别人也有实例时会把别人的一起杀掉）。"""
    subprocess.run(['taskkill', '/PID', str(pid), '/f'], capture_output=True)
    for _ in range(20):
        if not any(x == pid for x in eyelite_pids()):
            return True
        time.sleep(0.25)
    return False


def _image_running(name):
    try:
        r = subprocess.run(['tasklist', '/fi', 'imagename eq %s' % name,
                            '/fo', 'csv', '/nh'], capture_output=True)
        out = r.stdout.decode('utf-8', 'ignore')
        return (name in out) and ('"PID"' not in out)
    except Exception:
        return False


def eyelite_pids():
    out = []
    try:
        r = subprocess.run(['tasklist', '/fi', 'imagename eq %s' % EXE_NAME,
                            '/fo', 'csv', '/nh'], capture_output=True)
        for line in r.stdout.decode('utf-8', 'ignore').splitlines():
            if EXE_NAME not in line:
                continue
            parts = [p.strip('" ') for p in line.split('","')]
            if len(parts) > 1 and parts[1].isdigit():
                out.append(int(parts[1]))
    except Exception:
        pass
    return out


def parse_p(path):
    out = []
    try:
        for line in open(path, encoding='utf-8', errors='ignore'):
            line = line.strip()
            if line.startswith('P '):
                c = line.split()
                if len(c) >= 3:
                    out.append((int(c[1]), int(c[2])))
    except Exception as e:
        print('  读取 trace 失败:', e)
    return out


def run_one(tag, preview_s, dim, why):
    trace = os.path.join(PROBE, 'onset%s.log' % tag.lstrip('#'))
    cfg = read_cfg()
    cfg.update({'WorkMinutes': '1', 'RestSeconds': '10',
                'PreviewSeconds': str(preview_s), 'DimAlphaMax': str(dim),
                'IdleMinutes': '0', 'SedentaryEnabled': '0', 'SoundEnabled': '0',
                'PomodoroEnabled': '0', 'MeetingGuardEnabled': '0',
                'SuppressOnFullscreen': '0'})
    write_cfg(cfg)
    if os.path.exists(trace):
        os.remove(trace)

    env = os.environ.copy()
    for k in list(env.keys()):
        if k.startswith('EYE_LITE_'):
            env.pop(k, None)
    env['EYE_LITE_TRACE_UI'] = trace

    print('')
    print('--- %s PreviewSeconds=%d DimAlphaMax=%d（%s）---' % (tag, preview_s, dim, why))
    p = subprocess.Popen([EXE], cwd=ROOT, env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    t0 = time.time()
    t_banner = None
    t_mask = [None]
    rc = None
    try:
        last_dbg = 0
        while time.time() - t0 < RUN_SECONDS:
            if t_banner is None and banner_visible(p.pid):
                t_banner = time.time() - t0
                print('    横幅 %.1fs 出现' % t_banner)
            if not t_mask[0] and mask_visible(p.pid):
                t_mask[0] = time.time() - t0
                print('    遮罩 %.1fs 出现（此时横幅=%s）'
                      % (t_mask[0], 'YES' if t_banner else '**从未出现**'))
            if p.poll() is not None:
                rc = p.returncode
                print('    [退出] %.1fs exitcode=%s' % (time.time() - t0, rc))
                break
            if time.time() - last_dbg >= 10.0:
                last_dbg = time.time()
                print('    t=%2.0fs 进程存活=%s 横幅=%s'
                      % (time.time() - t0, p.poll() is None,
                         'YES' if t_banner else 'no'))
            time.sleep(0.1)
    finally:
        kill_pid(p.pid)

    samples = parse_p(trace)
    if t_banner is None:
        print('  横幅窗口：**全程未出现**；遮罩=%s'
              % (('%.1fs' % t_mask[0]) if t_mask[0] else '也未出现'))
    else:
        print('  横幅出现：%.1fs（进程启动后）' % t_banner)
    if rc is not None:
        print('  [注意] 进程提前退出，exitcode=%s（1 = 疑似外部 taskkill）' % rc)
    print('  P 行 %d 条' % len(samples))

    if len(samples) >= 2:
        gaps = [(samples[i][0] - samples[i - 1][0], samples[i - 1][1], samples[i][1])
                for i in range(1, len(samples))]
        print('  完整间隔序列（第 i 个间隔：前秒→后秒 = 毫秒）:')
        for i, (g, a, b) in enumerate(gaps, 1):
            flag = '   ← ≥2000ms' if g >= 2000 else ''
            print('    #%d  %2d→%-2d = %5dms%s' % (i, a, b, g, flag))
        mx = max(g for (g, _a, _b) in gaps)
        big = [g for (g, _a, _b) in gaps if g >= 2000]
        print('  最大 %dms，≥2000ms %d 条%s' % (mx, len(big), (': %s' % big) if big else ''))
        # 间隙相对位置：只在前两个间隔出现？
        first_two = [g for (g, _a, _b) in gaps[:2]]
        later = [g for (g, _a, _b) in gaps[2:]]
        print('  前 2 个间隔：%s ｜ 其余：最大 %s'
              % (first_two, max(later) if later else '-'))
    return {'banner': t_banner, 'n': len(samples),
            'gaps': [(samples[i][0] - samples[i - 1][0], samples[i - 1][1], samples[i][1])
                     for i in range(1, len(samples))]}


def run_repeat(n, inj_from=55.0, inj_to=63.0, preview=3, tag='', run_s=75.0):
    """Preview=3 + Dim=45 + 55~63s 持续注入 F24（模拟用户在打字），跑 n 次。

    每次 75 秒：预告窗本该在 57~60s。三种可能：
      · 横幅 57~60s 出现      → 正常
      · 遮罩 ~60s 出现但**无横幅** → **预告窗被整段跨过**（真缺陷）
      · 遮罩明显晚于 60s      → 被 C3/C4 让路推迟（设计行为，不是缺陷）
    """
    print('')
    print('=== repeat 模式%s：Preview=%d / Dim=45 / 注入=%s / 每次 75s / 共 %d 次 ==='
          % (tag, preview,
             ('%.0f~%.0fs' % (inj_from, inj_to)) if inj_from is not None else '**不注入**',
             n))
    print('    每次运行 %.0fs（IdleMinutes=0，避免"人走开"导致累加器不涨）' % run_s)
    rows = []
    for i in range(1, n + 1):
        trace = os.path.join(PROBE, 'rep%d.log' % i)
        cfg = read_cfg()
        cfg.update({'WorkMinutes': '1', 'RestSeconds': '10', 'PreviewSeconds': str(preview),
                    'DimAlphaMax': '45', 'IdleMinutes': '0', 'SedentaryEnabled': '0',
                    'SoundEnabled': '0', 'PomodoroEnabled': '0',
                    'MeetingGuardEnabled': '0', 'SuppressOnFullscreen': '0'})
        write_cfg(cfg)
        if os.path.exists(trace):
            os.remove(trace)
        env = os.environ.copy()
        for k in list(env.keys()):
            if k.startswith('EYE_LITE_'):
                env.pop(k, None)
        env['EYE_LITE_TRACE_UI'] = trace

        # 互斥名是固定的 "EyeLite_SingleInstance_v1"（Program.cs:8834），**dev 与 release 共享**，
        # 不是按文件名判的。只要有一个别的实例在跑，本实例会弹「已经在运行了」并 exit(-1)。
        others = [n for n in ('EyeLite.exe', 'EyeLite-dev.exe')
                  if n != EXE_NAME and _image_running(n)]
        if others:
            print('  #%d [SKIP] 外部实例 %s 正在运行（共享同一互斥名），本实例会拒绝启动'
                  % (i, others))
            rows.append({'i': i, 'banner': None, 'banner_last': None, 'texts': [],
                         'mask': None, 'gaps': [], 'verdict': 'SKIP（外部实例占用互斥）',
                         'inj': 0, 'skip': True})
            continue

        p = subprocess.Popen([EXE], cwd=ROOT, env=env,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        t0 = time.time()
        t_banner = None
        t_mask = None
        t_banner_last = None
        banner_texts = []
        last_text_at = [0.0]
        injected = 0
        rc = None
        try:
            while time.time() - t0 < run_s:
                el = time.time() - t0
                if banner_visible(p.pid):
                    if t_banner is None:
                        t_banner = el
                    t_banner_last = el
                    # 文案会在"到点被让路"时被换掉（→「正在输入，稍后提醒」），
                    # 只在首次出现时采一次会漏掉新文案。所以每 2 秒采一次并去重。
                    if el - last_text_at[0] >= 2.0:
                        last_text_at[0] = el
                        t_now = banner_child_texts(p.pid)
                        if t_now and t_now not in banner_texts:
                            banner_texts.append(t_now)
                if t_mask is None and mask_visible(p.pid):
                    t_mask = el
                if inj_from is not None and inj_from <= el <= inj_to:
                    tap_f24()
                    injected += 1
                    time.sleep(0.06)
                    continue
                if p.poll() is not None:
                    rc = p.returncode
                    break
                time.sleep(0.1)
        finally:
            kill_pid(p.pid)

        samples = parse_p(trace)
        gaps = [(samples[j][0] - samples[j - 1][0], samples[j - 1][1], samples[j][1])
                for j in range(1, len(samples))]
        verdict = '正常'
        if t_banner is None:
            if t_mask is not None and t_mask <= 62.5:
                verdict = '**预告窗被跨过**（无横幅、遮罩仍按时）'
            elif t_mask is None:
                verdict = '无横幅且 75s 内无遮罩（疑被让路推迟）'
            else:
                verdict = '无横幅、遮罩推迟到 %.1fs（疑让路）' % t_mask
        print('  #%d 横幅=%s 最后可见=%s 遮罩=%s 注入=%d P=%d 间隔=%s%s'
              % (i, ('%.1fs' % t_banner) if t_banner else '**无**',
                 ('%.1fs' % t_banner_last) if t_banner_last else '-',
                 ('%.1fs' % t_mask) if t_mask else '无', injected, len(samples),
                 [g for (g, _a, _b) in gaps],
                 ('  exitcode=%s' % rc) if rc is not None else ''))
        if banner_texts:
            print('     横幅文案（按时间顺序去重）：')
            for t in banner_texts:
                print('       %s' % t)
        if samples:
            print('     P 完整序列 (ms, 秒)：%s' % samples)
        if gaps and any(g >= 2000 for (g, _a, _b) in gaps):
            print('      ↑ 出现 ≥2000ms 间隔：%s'
                  % [(a, b, g) for (g, a, b) in gaps if g >= 2000])
        rows.append({'i': i, 'banner': t_banner, 'banner_last': t_banner_last,
                     'texts': banner_texts, 'mask': t_mask,
                     'gaps': gaps, 'verdict': verdict, 'inj': injected})
    return rows


def main():
    if not os.path.exists(EXE):
        print('FAIL: 找不到 EyeLite.exe')
        return 2
    print('被测产物: %s (%d bytes)' % (EXE, os.path.getsize(EXE)))
    os.makedirs(PROBE, exist_ok=True)

    if len(sys.argv) > 1 and sys.argv[1] in ('repeat', 'repeat_early', 'repeat_quiet', 'repeat_b'):
        n = int(sys.argv[2]) if len(sys.argv) > 2 else 8
        batch_header()
        raw = read_cfg_raw()
        try:
            if sys.argv[1] == 'repeat_b':
                # B 组：注入必须从 50s 起（早于 57s 的预告窗）—— 这是本次修复的关键路径，
                # 晚于 57s 就测不到"让路期间保留横幅"。跑到 95s 是为了看"停手后立刻进休息"。
                rows = run_repeat(n, inj_from=50.0, inj_to=68.0, preview=3,
                                  tag='（B 组：50~68s 注入）', run_s=95.0)
            elif sys.argv[1] == 'repeat_quiet':
                # A 组：不打字，只验横幅是否出现（修前 33% 未出现 → 修后应 0%）
                rows = run_repeat(n, inj_from=None, inj_to=None, preview=3,
                                  tag='（不注入，A 组）')
            elif sys.argv[1] == 'repeat_early':
                # 注入提前到 50~56s：到点前 4 秒停手 → idle 到点时会 >300ms，
                # 不触发 C3/C4 让路。用来分离"让路导致跳过"与"3 秒窗本身太窄"。
                rows = run_repeat(n, inj_from=50.0, inj_to=56.0, preview=3,
                                  tag='（提前注入，不让你路）')
            else:
                rows = run_repeat(n)
        finally:
            open(CFG, 'wb').write(raw)
            print('config restored: %d bytes' % os.path.getsize(CFG))
        print('')
        print('================ 汇总 ================')
        valid = [r for r in rows if not r.get('skip')]
        skipped = [r for r in rows if r.get('skip')]
        nb = sum(1 for r in valid if r['banner'] is None)
        big = sum(1 for r in valid if any(g >= 2000 for (g, _a, _b) in r['gaps']))
        for r in rows:
            print('  #%d  横幅=%s  遮罩=%s  结论=%s'
                  % (r['i'], ('%.1fs' % r['banner']) if r['banner'] else '无',
                     ('%.1fs' % r['mask']) if r['mask'] else '无', r['verdict']))
        print('')
        if skipped:
            print('  （另有 %d 次因外部实例占用互斥被 SKIP，不计入分母）' % len(skipped))
        print('  **横幅未出现 %d/%d 次（%.0f%%）**；出现 ≥2000ms 间隔的 %d/%d 次'
              % (nb, len(valid), 100.0 * nb / max(1, len(valid)), big, len(valid)))
        return 0

    want = [a.lstrip('#') for a in sys.argv[1:]]
    cases = [c for c in CASES if not want or c[0].lstrip('#') in want]

    batch_header()
    raw = read_cfg_raw()
    results = []
    try:
        for tag, pv, dim, why in cases:
            results.append((tag, pv, dim, run_one(tag, pv, dim, why)))
    finally:
        open(CFG, 'wb').write(raw)
        print('')
        print('config restored: %d bytes' % os.path.getsize(CFG))

    print('')
    print('================ 汇总 ================')
    print('%-4s %-8s %-6s %-9s %-8s %s' % ('组', 'Preview', 'Dim', '横幅出现', 'P条数', '间隔序列(ms)'))
    for tag, pv, dim, r in results:
        gaps = [g for (g, _a, _b) in r['gaps']]
        print('%-4s %-8d %-6d %-9s %-8d %s'
              % (tag, pv, dim,
                 ('%.1fs' % r['banner']) if r['banner'] else '未捕获',
                 r['n'], gaps))
    return 0


if __name__ == '__main__':
    sys.exit(main())
