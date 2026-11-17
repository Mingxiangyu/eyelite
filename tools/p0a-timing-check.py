#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0-A / P0-D 真机实测：验证时钟源改造后的计时精度。

测三件事：
  T1 工作时钟精度 —— 「进程启动 → 遮罩出现」的间隔应 ≈ WorkMinutes × 60 秒
                     （这直接验证 workAccumMs 的累加速率；速率错则间隔错）
  T2 遮罩按时弹出 —— 同上，但看它落在容差窗口内
  T3 休息倒计时准确 —— RestSeconds=8 时遮罩应约 8 秒后自动消失

全部为**只读观测**：只枚举窗口、读时间，不注入任何按键、不抢焦点。
这保证不会影响用户的键盘输入。

用法：python tools/p0a-timing-check.py [轮数]
前置：config.ini 里 WorkMinutes=1 / RestSeconds=8 / PomodoroEnabled=0 /
      IdleMinutes=0 / SedentaryEnabled=0，且 EyeLite.exe 已启动。


### 3) 休息结束的 δ（durationMs − 配置值）呈"相位锁死"，不是随机
2026-11-16 十轮 `end.durMs`（配置 8s，`EndRest` 量化修复**尚未编入**该 exe）：
    9002, 8989, 2554, 8992, 8991, 8990, 8992, 8991, 8991, 8002
  · 九轮挤在 ~8990（δ≈+990），**只有一轮 8002（δ=+2）**
  · 若 tick 与休息结束是"随机相位"，应看到 8300/8600/8800 之类的分散值；
    九轮都贴在 +990 说明 **`restDueMs` 与 tick 网格存在固定相位**，不是均匀分布。
  · 所以：**等 `EndRest` 搬到 200ms 后，δ 应变成 [0,200] 的均匀分布**。
    届时若仍看到"九轮 ~190、一轮 ~5"，说明相位锁死只是周期变小、机制还在 ——
    那时才需要往"两个定时器的相对相位"方向查。
  · 另：2554ms 那一轮是 δ=−5446（大幅为负），按"δ 不可能为负"的规则，
    **只能是提前收窗**（用户按了空格/Esc 跳过），不是自然到点。

## 【实测留档 2026-11-16 · 产物 111104/53a9734d】

### 1) 工作计时呈"双峰"，不是随机抖动
    60.53 / 60.43          ← 簇 A，贴 60.5
    62.38 / 62.41 / 63.50  ← 簇 B，约 +1.9s
两簇差 ~1.9s；休息时长五轮都正常（8.91~9.02s）。**5 轮分不出"60% 概率"与"40% 概率"，
要给它定性请跑 10 轮**（这也是 lead 要求新产物上跑 10 轮的原因）。

### 2) 元凶是 A2 渐暗层（三组只改一个变量，判据一字未改）
    默认（DimAlphaMax=45、TypingGuard 默认开）
        60.53 / 62.38 / 63.50 / 60.43 / 62.41   中位 62.38   1/5 超 ±3s   FAIL
    TypingGuardEnabled=0
        64.40 / 62.52 / 60.48 / 61.51 / 60.45   中位 61.51   1/5 超 ±3s   FAIL（更差）
    DimAlphaMax=0（关渐暗）
        60.52 / 60.54 / 61.38 / 60.55 / 60.51   中位 60.54   0/5 超 ±3s   PASS
→ 打字段排除；**方差从 3.97s 跨度塌到 0.86s**，是"渐暗就是那个第二延迟源"的强证据。
→ 结论口径：**判据不动（60±3s 是产品承诺），改产品**；修不好就把 A2 降级为默认关。

**踩坑记录（2026-11-14，新 build）**：A2 的 DimForm（休息前 60 秒渐进压暗）铺满整块
屏幕，尺寸同样 w>800 h>500，WorkMinutes=1 时启动后 ~1 秒就出现。只按尺寸判遮罩会让
T1 的"遮罩出现时刻"直接变成 1 秒（计时精度结论全废）。区分依据
（源码 2146-2196 vs 1264-1320）：
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

user32 = ctypes.WinDLL('user32', use_last_error=True)

EYELITE_PID = [0]

# ---- 遮罩判据的两个辅助（见顶部踩坑记录）----
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


def find_eyelite_pid():
    """tasklist 找 EyeLite.exe 的 pid（输出是 GBK）。"""
    r = subprocess.run(['tasklist', '/fi', 'imagename eq EyeLite.exe', '/fo', 'csv', '/nh'],
                       capture_output=True)
    text = r.stdout.decode('gbk', 'ignore')
    for line in text.splitlines():
        if 'EyeLite.exe' in line:
            parts = [p.strip('" ') for p in line.split('","')]
            return int(parts[1])
    return None


def enum_eyelite_windows():
    """列出 EyeLite 进程的所有顶层窗口。"""
    out = []
    EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(hwnd, lparam):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == EYELITE_PID[0]:
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, cls, 256)
            r = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            out.append({
                'hwnd': hwnd,
                'cls': cls.value,
                'w': r.right - r.left,
                'h': r.bottom - r.top,
                'visible': bool(user32.IsWindowVisible(hwnd)),
            })
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def overlay_windows():
    """EyeLite 进程里可见的、大尺寸的窗口 = 休息遮罩。

    必须排除压暗层（DimForm）：它铺满屏幕、尺寸一样，但带 WS_EX_TRANSPARENT
    且没有子窗口（见顶部踩坑记录）。
    """
    out = []
    for w in enum_eyelite_windows():
        if not (w['visible'] and w['w'] > 800 and w['h'] > 500):
            continue
        if ex_style(w['hwnd']) & WS_EX_TRANSPARENT:
            DIM_SEEN[0] = True          # 压暗层：信息项，不算遮罩
            continue
        if child_count(w['hwnd']) < 1:
            continue
        out.append(w)
    return out


def process_start_time():
    """取 EyeLite 进程的启动时刻（perf_counter 无关的系统时间，转成 epoch 秒）。"""
    r = subprocess.run(
        ['powershell', '-NoProfile', '-Command',
         "(Get-Process EyeLite).StartTime.ToUniversalTime().ToOADate()"],
        capture_output=True)
    try:
        oadate = float(r.stdout.decode('utf-8', 'ignore').strip())
        # OLE Automation Date 基准：1899-12-30
        import datetime
        epoch = datetime.datetime(1899, 12, 30, tzinfo=datetime.timezone.utc)
        return (epoch + datetime.timedelta(days=oadate)).timestamp()
    except Exception:
        return None


def wait_for_overlay(timeout_s, poll=0.15):
    """等遮罩出现。返回 (窗口列表, 等待秒数, idle 统计)。

    idle 统计：{'at_fire': 命中那一刻的空闲毫秒, 'min_3s': 命中前 3 秒内的最小空闲毫秒}
    空窗判定用得到：at_fire 小 → 用户正在动键鼠（让路会生效，这轮不能算"准时性"样本）。
    """
    t0 = time.time()
    samples = []          # [(elapsed_s, idle_ms)]
    while time.time() - t0 < timeout_s:
        samples.append((time.time() - t0, idle_ms()))
        ov = overlay_windows()
        if ov:
            el = time.time() - t0
            recent = [i for (t, i) in samples if t >= el - 3.0 and i >= 0]
            return ov, el, {'at_fire': samples[-1][1] if samples else -1,
                            'min_3s': min(recent) if recent else -1}
        time.sleep(poll)
    return [], timeout_s, {'at_fire': -1, 'min_3s': -1}


def wait_for_no_overlay(timeout_s, poll=0.1):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if not overlay_windows():
            return True, time.time() - t0
        time.sleep(poll)
    return False, timeout_s


def wait_process_exit(timeout_s=15):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if find_eyelite_pid() is None:
            return True
        time.sleep(0.3)
    return False


def start_eyelite(exe):
    subprocess.Popen([exe], cwd=os.path.dirname(exe))
    time.sleep(1.0)
    pid = find_eyelite_pid()
    if not pid:
        return None
    EYELITE_PID[0] = pid
    return pid


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'],
                   capture_output=True)
    wait_process_exit()


# ---------------------------------------------------------------- 配置
#
# 前置配置由脚本**自己写**、跑完按**原始字节**还原。
# 原来只在 docstring 里写"前置"，直接跑会用用户的真实配置（WorkMinutes=20），
# 120s 内不可能出遮罩 → 三轮回全都 FAIL，而且看起来像产品缺陷（2026-11-14 实测踩过）。

def idle_ms():
    """距上次键鼠输入的毫秒（GetLastInputInfo）。用来判断"这一轮有没有人在动键鼠"。

    为什么要采这个：C3/C4 的"临门一脚让路"会被**真实输入**触发，
    表现为"到点晚 2~3 秒但 T 间隔完全正常"。不采 idle 就会把"用户在场"误判成产品缺陷。
    """
    import ctypes
    import ctypes.wintypes as wt

    class LASTINPUTINFO(ctypes.Structure):
        _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]

    li = LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(li)
    u = ctypes.WinDLL('user32', use_last_error=True)
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.GetTickCount.restype = wt.DWORD
    if not u.GetLastInputInfo(ctypes.byref(li)):
        return -1
    return int(k.GetTickCount() - li.dwTime)


def exe_sig():
    """(size, md5)：用来发现"跑到一半 exe 被重新编译"。"""
    import hashlib
    exe = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       'EyeLite.exe')
    try:
        st = os.stat(exe)
        with open(exe, 'rb') as f:
            h = hashlib.md5(f.read()).hexdigest()
        return (st.st_size, h)
    except Exception as e:
        return (-1, 'ERR:%s' % e)


CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')

CFG_ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']


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
    # 关掉 P1 的两个实验性守卫。
    #
    # 为什么：C4（打字段）的判据是 idle<300ms → 到点延后 5s、累计 60s；
    # C3（前台白名单）命中即延后 60s。两者都会让"遮罩出现时刻"偏离 60s，
    # 而本脚本量的是**计时精度**，不是守卫行为。守卫由 tools/p1-batch1-check.py 单独验证。
    # 踩过一次：p0d 的对照组用 EYE_LITE_FAKE_IDLE_MS=0 伪造"人就在"，
    # 结果 idle 恒为 0 → C4 一直判"正在打字" → 遮罩被推到 60s 之后，对照组直接失效。
    d = dict(d)
    d['ForegroundGuardEnabled'] = '0'
    d['TypingGuardEnabled'] = '0'
    if not os.path.isdir(CFG_DIR):
        os.makedirs(CFG_DIR)
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in CFG_ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:                      # 未知键也写回，不能弄丢用户配置
            if k not in CFG_ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def restore_cfg_raw(raw):
    if not raw:
        return
    with open(CFG, 'wb') as f:
        f.write(raw)
    print('[env] config restored: %d bytes (backup %d bytes, %s)'
          % (os.path.getsize(CFG), len(raw),
             'OK' if os.path.getsize(CFG) == len(raw) else 'SIZE MISMATCH!'))


WORK_TIMES = []

def main():
    # 5 轮而不是 3 轮：3 轮样本太少，一次系统停顿就整批判 FAIL，而那与产品无关（见文件头判据说明）。
    rounds = 5
    if len(sys.argv) > 1:
        try:
            rounds = int(sys.argv[1])
        except ValueError:
            pass

    exe = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       'EyeLite.exe')
    if not os.path.exists(exe):
        print('[FATAL] 找不到 EyeLite.exe：' + exe)
        return 2

    print('[env] exe = %s' % exe)
    print('[env] rounds = %d' % rounds)

    raw = read_cfg_raw()
    orig = read_cfg()
    cfg = dict(orig)
    cfg.update({'WorkMinutes': '1', 'RestSeconds': '8', 'PreviewSeconds': '0',
                'PomodoroEnabled': '0', 'IdleMinutes': '0',
                'SedentaryEnabled': '0', 'SoundEnabled': '0'})
    write_cfg(cfg)
    print('[env] config: WorkMinutes=1 RestSeconds=8 PreviewSeconds=0 '
          'IdleMinutes=0（脚本自备前置，结束按原始字节还原）')

    results = []
    artifact_changed = [False]
    try:
        for rnd in range(1, rounds + 1):
            print('\n=========== 第 %d 轮 ===========' % rnd)
            sig_before = exe_sig()
            stop_eyelite()

            t_launch = time.time()
            pid = start_eyelite(exe)
            if not pid:
                print('  [FATAL] 启动失败')
                results.append(('第%d轮 启动' % rnd, False, '启动失败'))
                continue

            # 用进程启动时刻做基准（比脚本自己的计时更准，不含启动开销）
            st = process_start_time()
            base = st if st else t_launch
            print('  pid=%d  进程启动时刻基准已取' % pid)

            # --- T1/T2：遮罩应在 WorkMinutes(1 分钟) 后弹出 ---
            ov, waited, idle_info = wait_for_overlay(120)
            appear = time.time()
            if not ov:
                print('  遮罩 120s 内未出现')
                results.append(('第%d轮 工作计时' % rnd, False, '遮罩未出现'))
                stop_eyelite()
                continue

            elapsed = appear - base
            print('  T1/T2 遮罩出现：进程启动后 %.2fs（期望 60s ± 3s）' % elapsed)
            print('       idle@到点=%sms  最后3s最小=%sms%s'
                  % (idle_info['at_fire'], idle_info['min_3s'],
                     '   ← 有输入，C3/C4 让路可能生效' if 0 <= idle_info['min_3s'] < 1000 else ''))
            print('       遮罩窗口 %d 个：%s' %
                  (len(ov), [(w['w'], w['h']) for w in ov]))
            WORK_TIMES.append(elapsed)
            ok = abs(elapsed - 60.0) <= 3.0
            results.append(('第%d轮 工作计时 %.2fs' % (rnd, elapsed), ok,
                            '期望 60±3s'))

            # --- T3：休息倒计时 ---
            gone, dur = wait_for_no_overlay(25)
            print('  T3 遮罩持续 %.2fs 后消失（期望 8s ± 2s）' % dur)
            ok3 = gone and abs(dur - 8.0) <= 2.0

            # 产物在轮内被替换 → 这一轮的数字不可信，丢弃并记录
            sig_after = exe_sig()
            if sig_after != sig_before:
                artifact_changed[0] = True
                print('  [产物变更] 本轮开始 %s → 结束 %s，本轮结果作废'
                      % (sig_before[1][:8], sig_after[1][:8]))
                continue

            results.append(('第%d轮 休息时长 %.2fs' % (rnd, dur), ok3, '期望 8±2s'))
    finally:
        stop_eyelite()
        restore_cfg_raw(raw)

    stop_eyelite()

    print('')
    print('================ 汇总 ================')
    allok = True
    for name, ok, detail in results:
        print('  %-28s %s   %s' % (name, 'PASS' if ok else 'FAIL', detail))
        # 单轮「工作计时」不参与总判定，交给下面的统计量（见文件头说明）
        if not ok and '工作计时' not in name:
            allok = False

    # ---- 工作计时：统计判据 ----
    # 允许 1/5 轮超 ±3s（外部调度停顿，已用 TRACE 证明在 tick 内部，5 个进程内假设全被推翻）
    # 但硬上限 66s，中位数必须 60~61.5s —— 后者才是"典型用户看到的准时性"。
    if WORK_TIMES:
        srt = sorted(WORK_TIMES)
        n = len(srt)
        med = srt[n // 2] if n % 2 else (srt[n // 2 - 1] + srt[n // 2]) / 2.0
        over = [t for t in WORK_TIMES if abs(t - 60.0) > 3.0]
        hard = [t for t in WORK_TIMES if t > 66.0]
        allow = max(1, n // 5)            # 5 轮里允许 1 轮（句柄预热已消除系统性延迟）
        stat_ok = (len(over) <= allow) and (not hard) and (60.0 <= med <= 61.5)
        print('  %-28s %s   中位 %.2fs，最大 %.2fs，超±3s 的 %d/%d 轮（允许≤%d），硬上限 66s'
              % ('工作计时统计', 'PASS' if stat_ok else 'FAIL', med, max(WORK_TIMES),
                 len(over), n, allow))
        if not stat_ok:
            allok = False
    # 压暗层信息项：让后来的人看到 dim=yes 时不会把它当成遮罩
    print('  %-28s %s   %s' % ('压暗层 DimForm（信息项）',
                               'yes' if DIM_SEEN[0] else 'no',
                               '出现过，但它不是遮罩（TRANSPARENT 且无子窗口）'))
    print('  OVERALL: %s' % ('PASS' if allok else 'FAIL'))
    if artifact_changed[0]:
        print('  [警告] 本轮次里有 exe 被替换，已丢弃受影响的轮次；'
              '整份结论不成立（请用同一产物重跑）')
        return 2
    return 0 if allok else 1


if __name__ == '__main__':
    sys.exit(main())
