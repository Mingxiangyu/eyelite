#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P1 批 1 验收脚本：A5 仪式音 / C3 前台白名单 / C4 打字段 / G3 坐一分钟

## 总原则
每条用例都必须有**对照组**，没有对照不算验证；仪器先自检，自检不过 `return 2`。
判据一律落在"遮罩出现的时刻"或"窗口里有没有那个按钮"这种**可观测的结构特征**上，
不靠肉眼、不靠听声音。

## 用例与判据

### C3 前台白名单（ForegroundWhitelist）
行为：到点若前台进程在白名单内 → 延后 60s 再判，最多累计 10 分钟。

**白名单口径（2026-11-14 与产品侧确认，只此一种）**：
  · **basename，含扩展名**：`vlc.exe`、`potplayermini64.exe`
  · **分号分隔**多项：`vlc.exe;obs64.exe;wmplayer.exe`
  · **大小写不敏感**、逐项 `Trim()`
  · 实现侧用 `Process.GetProcessById(pid).ProcessName + ".exe"` 取名，所以填的名字
    **必须带 `.exe`**（本脚本会把取到的名字补成 `.exe`，并打印实际写入的字符串）
  · 取不到进程名时按**不在白名单**处理（fail-open：宁可提醒）
本脚本只用**一项**（当前前台进程），不测多值匹配 —— 那属于产品侧解析逻辑。

  · 实验组：白名单 = **当前前台进程名**（脚本自己取，不写死）→ 95s 内**不得**弹
  · 对照组：白名单 = 空字符串 → 同样配置必须 **≈60s 弹**
  · 可选 `c3cap`：白名单一直生效，等最多 700s，验证"超 10 分钟必须出现"
    （默认不跑；lead 要求先让主判据跑绿再单独安排）

### C4 打字段（TypingGuardEnabled）
行为：到点若 idle < 300ms → 延后 5s，最多累计 60s。
  · 实验组：45s~75s 每 150ms 注入一次 F24（不产生字符）→ 遮罩出现时刻应 **> 75s**
    且注入停止后应在 60s 内出现（出现时刻 ≤ 75+60=135s）
  · 对照组：不注入 → ≈60s 弹

### G3 坐一分钟（SittingOptionEnabled）
行为：开关打开时遮罩上多一个「坐一分钟」按钮，点击关闭遮罩。
  · 实验组：枚举遮罩子窗口文本，必须能找到「坐一分钟」；点击后 ≤3s 遮罩消失
  · 对照组：开关关掉 → **找不到**「坐一分钟」（证明是版式差异，不是我们瞎找）

### A5 仪式音（Plugin_Sound）—— **间接验证**
声音无法用窗口/钩子观测，本脚本只断言"打开后会响的那条路径仍然正常"：
预告横幅按时出现、遮罩按时出现、进程存活、没有多出来的异常窗口。
**报告里必须写明"声音本身未直接验证"**，不能因为这条 PASS 就宣称"声音正常"。

## 预判的"假 FAIL"风险（写在这里提醒以后看日志的人）
1. **C3 实验组**：整段 95s 里前台必须一直是白名单进程。用户在测试期间切窗口/我的注入
   让前台变化，都会让 gate 立刻放行 → 看起来像"白名单没生效"。所以脚本**每次轮询都采样
   前台进程名**，只要中途离开过白名单，就判"环境不成立"（`return 2`）而不是 FAIL。
2. **C3 对照组**：若白名单语法不是"进程名"（比如要全路径 / 逗号分隔 / 大小写敏感），
   配对会失败。脚本会打印实际取到的全路径与进程名，供排查。
3. **C4 实验组**：注入必须真的把 idle 压到 <300ms。脚本在注入窗口内采样
   `GetLastInputInfo`，若 idle 大量 ≥300ms（注入没被系统算作输入），判"仪器不成立"。
4. **G3**：`RestSeconds` 太短（8s）时，找到按钮之前遮罩就自己关了。所以 G3 用
   `RestSeconds=30`，给枚举 + 点击留时间；用例本身仍只断言"点击后 ≤3s 消失"。
5. **A5**：只要出现"预告没按时到"就会被记 FAIL —— 但 A5 的实际变量是**声音**，
   若发现 FAIL 要先排除配置/计时问题（对照组在 C3/C4 里已经覆盖计时链路）。

## 纪律
· 遮罩判据 = 大尺寸 + 非 WS_EX_TRANSPARENT(0x20) + 子窗口 ≥1（A2 的 DimForm 会伪装成遮罩）
· 遮罩按 PID 过滤；PID 用 Popen.pid 缓存，轮询里不调 tasklist
· 不抢前台、不移动鼠标（点击用 PostMessage 发给子窗口）
· 临时文件只放 tools/_probe/，结束删除；config.ini 按原始字节备份/还原；结束 taskkill

## 用法
    python tools/p1-batch1-check.py                # 全部（约 12 分钟）
    python tools/p1-batch1-check.py c3 c4          # 只跑指定项
    python tools/p1-batch1-check.py c3cap          # 单独跑"超 10 分钟必须出现"（约 11 分钟）
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

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.PostMessageW.restype = wt.BOOL
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.GetClientRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.GetForegroundWindow.restype = wt.HWND
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.QueryFullProcessImageNameW.restype = wt.BOOL
kernel32.QueryFullProcessImageNameW.argtypes = [
    wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
kernel32.GetTickCount.restype = wt.DWORD
kernel32.CloseHandle.argtypes = [wt.HANDLE]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x00000020
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
BM_CLICK = 0x00F5
MK_LBUTTON = 0x0001
KEYEVENTF_KEYUP = 0x0002
VK_F24 = 0x87
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

DIM_SEEN = [False]
_pid_cache = set()

# 新插件用的配置键（用户 config 里本来没有，写配置时追加；还原按原始字节，不会污染）
P1_KEYS = ['Plugin_Sound', 'ForegroundGuardEnabled', 'ForegroundWhitelist',
           'TypingGuardEnabled', 'SittingOptionEnabled']


# ---------------------------------------------------------------- 基础辅助


def ex_style(hwnd):
    return user32.GetWindowLongW(hwnd, GWL_EXSTYLE)


def child_count(hwnd):
    n = [0]

    def cb(ch, lparam):
        n[0] += 1
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return n[0]


def window_text(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def enum_children(hwnd):
    out = []

    def cb(ch, lparam):
        out.append((ch, class_name(ch), window_text(ch),
                    bool(user32.IsWindowVisible(ch))))
        return True

    user32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return out


def process_name_of_pid(pid):
    """pid → basename，取不到返回 None。"""
    if not pid:
        return None
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wt.DWORD(1024)
        if not kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return None
        return os.path.basename(buf.value)
    finally:
        kernel32.CloseHandle(h)


def candidate_process_names(limit=10):
    """收集"可能成为前台"的进程名集合 = 当前前台 + 所有拥有可见窗口的进程。

    为什么需要它（踩过一次）：
      产品只在**临近到点**才查前台进程（IsNearDue 为真时），而无人值守跑脚本时
      前台会自己漂移（实测 WorkBuddy.exe → chrome.exe，第 0.0s 就变了）。
      只写单个进程名时，用例会退化成"环境不成立 SKIP"，
      而这**测不出功能有没有做到**。

    白名单本来就支持多值（分号分隔），所以填"真实的候选集合"：
    判据仍然严格是「前台在白名单内 → 不打扰」，只是把"用户可能切到哪些窗口"
    如实写进去，消除了前台漂移造成的假 SKIP。
    """
    names = []
    _h, fg_name, _f = foreground_info()
    if fg_name:
        names.append(normalize_whitelist_name(fg_name))
    seen = set()
    for t in list_windows():
        pid = t[3]
        if not pid or pid in seen:
            continue
        seen.add(pid)
        n = normalize_whitelist_name(process_name_of_pid(pid))
        if n and n not in names:
            names.append(n)
        if len(names) >= limit:
            break
    return names


def list_windows():
    out = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        r = wt.RECT()
        if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return True
        pid = wt.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        out.append((hwnd, r.right - r.left, r.bottom - r.top, pid.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def own_windows():
    if not _pid_cache:
        return []
    return [t for t in list_windows() if t[3] in _pid_cache]


def overlay_windows():
    """休息遮罩 = 大尺寸 + 非 TRANSPARENT + 子窗口 ≥1（DimForm 会被排除）。"""
    out = []
    for hwnd, w, h, pid in own_windows():
        if not (w > 800 and h > 500):
            continue
        if ex_style(hwnd) & WS_EX_TRANSPARENT:
            DIM_SEEN[0] = True
            continue
        if child_count(hwnd) < 1:
            continue
        out.append((hwnd, w, h, pid))
    return out


def preview_windows():
    return [t for t in own_windows() if 200 < t[1] <= 900 and t[2] <= 300]


def dim_windows():
    return [(hwnd, w, h) for hwnd, w, h, _p in own_windows()
            if w > 800 and h > 500 and (ex_style(hwnd) & WS_EX_TRANSPARENT)
            and child_count(hwnd) == 0]


# ---------------------------------------------------------------- 前台进程


def foreground_info():
    """返回 (hwnd, 进程名, 全路径)。取不到时后两项为 None。"""
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return hwnd, None, None
    pid = wt.DWORD(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not pid.value:
        return hwnd, None, None
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not h:
        return hwnd, None, None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wt.DWORD(1024)
        if not kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return hwnd, None, None
        full = buf.value
        return hwnd, os.path.basename(full), full
    finally:
        kernel32.CloseHandle(h)


def normalize_whitelist_name(name):
    """按产品侧口径归一化白名单项：basename + **必须带 .exe**。

    实现侧用 `Process.GetProcessById(pid).ProcessName + ".exe"`，所以比对用的是
    "带扩展名的进程名"。这里补一次 .exe，避免取到的 basename 恰好没扩展名时
    永远匹配不上（那会表现为"白名单没生效"的假 FAIL）。
    """
    if not name:
        return ''
    n = name.strip()
    if not n.lower().endswith('.exe'):
        n += '.exe'
    return n


# ---------------------------------------------------------------- 注入


def key_tap(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.02)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


def user_idle_ms():
    li = LASTINPUTINFO()
    li.cbSize = ctypes.sizeof(li)
    if not user32.GetLastInputInfo(ctypes.byref(li)):
        return 1 << 30
    return int(kernel32.GetTickCount() - li.dwTime)


# ---------------------------------------------------------------- 点击子窗口


def click_child(hwnd, method='mouse'):
    if method == 'bm':
        user32.PostMessageW(hwnd, BM_CLICK, 0, 0)
        return
    cr = wt.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(cr)):
        cr.right, cr.bottom = 10, 10
    x = max(1, (cr.right - cr.left) // 2)
    y = max(1, (cr.bottom - cr.top) // 2)
    lp = (y << 16) | (x & 0xFFFF)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)


def find_child_by_text(hwnd, needle):
    """在子窗口里按文本包含关系找控件（「坐一分钟」这类按钮文案会带图标/后缀，
    用包含匹配比精确匹配稳）。返回 (hwnd, class, text) 或 None。"""
    for ch, cls, text, _vis in enum_children(hwnd):
        if text and needle in text:
            return (ch, cls, text)
    return None


# ---------------------------------------------------------------- 配置


CFG_ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
             'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
             'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
             'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
             'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax'] + P1_KEYS


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


# ---------------------------------------------------------------- 进程


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
    _pid_cache.clear()
    t0 = time.time()
    while time.time() - t0 < 12:
        if not eyelite_pids():
            return True
        time.sleep(0.25)
    return False


def launch():
    env = os.environ.copy()
    for k in ('EYE_LITE_FORCE_QUNS', 'EYE_LITE_FORCE_QUNS_FILE',
              'EYE_LITE_SUPPRESS_MAX_MS', 'EYE_LITE_FAKE_IDLE_MS',
              'EYE_LITE_FAKE_IDLE_FILE'):
        env.pop(k, None)       # 别从父 shell 继承到旧测试钩子
    proc = subprocess.Popen([EXE], cwd=ROOT, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _pid_cache.add(proc.pid)
    return proc


def alive(pid):
    return pid in eyelite_pids()


def wait_overlay(timeout_s, poll=0.1):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if overlay_windows():
            return time.time() - t0
        time.sleep(poll)
    return None


def wait_overlay_gone(timeout_s, poll=0.05):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if not overlay_windows():
            return time.time() - t0
        time.sleep(poll)
    return None


def wait_preview(timeout_s, poll=0.08):
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if preview_windows():
            return time.time() - t0
        time.sleep(poll)
    return None


def watch(seconds, progress=15.0, tag=''):
    """轮询 seconds 秒，返回 (first_overlay, first_preview)。"""
    t0 = time.time()
    fo = fp = None
    last = t0
    while time.time() - t0 < seconds:
        if fo is None and overlay_windows():
            fo = time.time() - t0
            return fo, fp
        if fp is None and preview_windows():
            fp = time.time() - t0
        if time.time() - last >= progress:
            last = time.time()
            print('    %s%.0fs  无遮罩' % (tag, time.time() - t0))
        time.sleep(0.1)
    return fo, fp


# ---------------------------------------------------------------- 用例


def case_c3(base, results, whitelist_mode='process'):
    """C3 前台白名单：实验组 95s 不弹 + 对照组 ≈60s 弹。

    实验组每轮轮询都采样前台进程名；中途离开白名单 → 环境不成立（return None），
    判"仪器/环境不成立"，不产出 FAIL（避免把用户切窗口当成产品缺陷）。
    """
    print('\n--- C3 前台白名单 ---')
    fg_hwnd, fg_name, fg_full = foreground_info()
    print('  当前前台：%s (hwnd=%s) 全路径=%s' % (fg_name, fg_hwnd, fg_full))
    if not fg_name:
        print('  取不到前台进程名 → 仪器不成立，跳过 C3')
        return None
    # 口径：basename + 扩展名，大小写不敏感；多项用 ';' 分隔。
    # 默认填"候选集合"而不是单个进程 —— 见 candidate_process_names() 的说明。
    if whitelist_mode == 'path':
        wl = normalize_whitelist_name(fg_full or fg_name)
    else:
        cands = [c for c in candidate_process_names() if c]
        wl = ';'.join(cands) if cands else normalize_whitelist_name(fg_name)
    wl_set = set(x.strip().lower() for x in wl.split(';') if x.strip())
    print('  白名单将写入：%r（%d 项）' % (wl, len(wl_set)))

    # ---- 对照组：白名单为空 → 必须 ≈60s 弹 ----
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    cfg = dict(base)
    cfg.update({'ForegroundGuardEnabled': '1', 'ForegroundWhitelist': ''})
    write_cfg(cfg)
    proc = launch()
    print('  对照组 cfg: ForegroundWhitelist=""（空）→ 期望 ≈60s 弹')
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失')
        return None
    ctl_ov, _ = watch(100.0, tag='对照等遮罩')
    ctl_ok = ctl_ov is not None and 45.0 <= ctl_ov <= 75.0
    print('  对照组遮罩=%s' % (('%.1fs' % ctl_ov) if ctl_ov is not None else '未出现'))

    # ---- 实验组：白名单 = 当前前台进程 ----
    if not stop_eyelite():
        print('  EyeLite 实例停不下来')
        return None
    cfg = dict(base)
    cfg.update({'ForegroundGuardEnabled': '1', 'ForegroundWhitelist': wl})
    write_cfg(cfg)
    proc = launch()
    print('  实验组 cfg: ForegroundWhitelist=%r → 期望 95s 内不弹' % wl)
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失')
        return None

    t0 = time.time()
    deviated = None
    ov = None
    while time.time() - t0 < 95.0:
        if overlay_windows():
            ov = time.time() - t0
            break
        _h, cur, _f = foreground_info()
        # 偏离判定也要大小写不敏感，且要对**整个白名单集合**比（白名单是多值的）
        if cur and cur.lower() not in wl_set and deviated is None:
            deviated = (time.time() - t0, cur)
        time.sleep(0.1)
    still = alive(proc.pid)
    print('  实验组：遮罩=%s 进程存活=%s 前台偏离=%s'
          % (('有@%.1fs' % ov) if ov is not None else '无(95s)', still,
             ('@%.1fs→%s' % deviated) if deviated else '无'))

    if not ctl_ok:
        results.append(('C3 前台白名单', False,
                        '对照组失效（应 ≈60s 弹，实际 %s）——白名单机制无法验证'
                        % (('%.1fs' % ctl_ov) if ctl_ov is not None else '未弹')))
        return None
    if deviated is not None:
        results.append(('C3 前台白名单', None,
                        'SKIP 环境不成立：第 %.1fs 前台变成了 %s（离开白名单），'
                        'gate 会立刻放行' % deviated))
        return None
    ok = ov is None and still
    results.append(('C3 前台白名单', ok,
                    '对照组 %.1fs 弹（计时在跑）｜实验组（白名单=%s）95s 内遮罩=%s'
                    % (ctl_ov or -1, wl,
                       ('有@%.1fs' % ov) if ov is not None else '无')))
    return ok


def case_c3cap(base, results):
    """C3 上限：白名单一直生效 → 超过 10 分钟必须出现（约 11 分钟，默认不跑）。

    判据：遮罩出现时刻应在 (600, 720] 区间内 —— 太早说明上限没生效（还在无限延后），
    太晚/不出现说明卡死。
    """
    print('\n--- C3cap 前台白名单上限（≥10 分钟必须放行）---')
    _h, fg_name, _f = foreground_info()
    if not fg_name:
        print('  取不到前台进程名 → 仪器不成立')
        return None
    wl = normalize_whitelist_name(fg_name)
    if not stop_eyelite():
        return None
    cfg = dict(base)
    cfg.update({'ForegroundGuardEnabled': '1', 'ForegroundWhitelist': wl})
    write_cfg(cfg)
    proc = launch()
    print('  cfg: ForegroundWhitelist=%r，最多等 720s' % wl)
    time.sleep(1.5)
    ov = wait_overlay(720.0, poll=0.3)
    still = alive(proc.pid)
    if ov is None:
        results.append(('C3cap 白名单上限 10 分钟', False,
                        '720s 内始终不弹（进程存活=%s）' % still))
        return
    ok = (600.0 < ov <= 720.0) and still
    results.append(('C3cap 白名单上限 10 分钟', ok,
                    '遮罩 %.1fs 出现（期望 600~720s）' % ov))


def case_c4(base, results):
    """C4 打字段：注入期把 idle 压到 <300ms，遮罩必须被延后。

    实验组：45s~75s 每 150ms 注入 F24 → 期望遮罩 >75s 且 ≤135s（停手后 60s 内）
    对照组：不注入 → ≈60s
    注入期间若 idle 采样显示大量 ≥300ms（注入没被算作输入），判仪器不成立。
    """
    print('\n--- C4 打字段（TypingGuardEnabled）---')

    # ---- 对照组：不注入 ----
    if not stop_eyelite():
        return None
    cfg = dict(base)
    cfg.update({'TypingGuardEnabled': '1'})
    write_cfg(cfg)
    proc = launch()
    print('  对照组 cfg: TypingGuardEnabled=1，不注入 → 期望 ≈60s 弹')
    time.sleep(1.5)
    if not alive(proc.pid):
        print('  进程启动后消失')
        return None
    ctl_ov, _ = watch(100.0, tag='对照等遮罩')
    ctl_ok = ctl_ov is not None and 45.0 <= ctl_ov <= 75.0
    print('  对照组遮罩=%s' % (('%.1fs' % ctl_ov) if ctl_ov is not None else '未出现'))

    # ---- 实验组：持续注入 ----
    if not stop_eyelite():
        return None
    write_cfg(cfg)
    proc = launch()
    print('  实验组：45s~75s 每 150ms 注入 F24 → 期望遮罩 >75s 且 ≤135s')
    time.sleep(1.5)
    if not alive(proc.pid):
        return None

    t0 = time.time()
    idle_bad = 0
    idle_samples = 0
    ov = None
    while time.time() - t0 < 160.0:
        el = time.time() - t0
        if 45.0 <= el <= 75.0:
            key_tap(VK_F24)            # 不产生字符，不会打进用户文档
            idle = user_idle_ms()
            idle_samples += 1
            if idle >= 300:
                idle_bad += 1
            time.sleep(0.15)
        else:
            time.sleep(0.1)
        if overlay_windows():
            ov = time.time() - t0
            break
    still = alive(proc.pid)
    ratio = (idle_bad / idle_samples) if idle_samples else 1.0
    print('  实验组：遮罩=%s 进程存活=%s  注入期 idle<300ms 占比=%.0f%%（%d/%d）'
          % (('有@%.1fs' % ov) if ov is not None else '无', still,
             100.0 * (1 - ratio), idle_samples - idle_bad, idle_samples))

    if not ctl_ok:
        results.append(('C4 打字段（注入期延后）', False,
                        '对照组失效（应 ≈60s 弹，实际 %s）——计时链路有问题'
                        % (('%.1fs' % ctl_ov) if ctl_ov is not None else '未弹')))
        return None
    if ratio > 0.5:
        results.append(('C4 打字段（注入期延后）', None,
                        'SKIP 仪器不成立：注入期只有 %.0f%% 的采样 idle<300ms，'
                        '注入没被系统算作输入' % (100.0 * (1 - ratio))))
        return None
    if ov is None:
        results.append(('C4 打字段（注入期延后）', False,
                        '停手后仍 160s 不弹（进程存活=%s）' % still))
        return None
    ok = (ov > 75.0) and (ov <= 135.0) and still
    results.append(('C4 打字段（注入期延后）', ok,
                    '对照组 %.1fs 弹｜实验组遮罩 %.1fs（期望 >75s 且 ≤135s）'
                    % (ctl_ov or -1, ov)))


def case_g3(base, results):
    """G3 坐一分钟：开关开 → 遮罩上能找到「坐一分钟」且点击后 ≤3s 消失；开关关 → 找不到。

    RestSeconds 用 30s：给"枚举 → 点击 → 观察消失"留时间（8s 太紧）。
    """
    print('\n--- G3 坐一分钟（SittingOptionEnabled）---')
    found_txt = []

    # ---- 实验组：开关开 ----
    if not stop_eyelite():
        return None
    cfg = dict(base)
    cfg.update({'SittingOptionEnabled': '1', 'RestSeconds': '30'})
    write_cfg(cfg)
    proc = launch()
    print('  实验组 cfg: SittingOptionEnabled=1 RestSeconds=30')
    time.sleep(1.5)
    if not alive(proc.pid):
        return None
    ov = wait_overlay(100.0)
    if ov is None:
        results.append(('G3 坐一分钟', False, '遮罩未出现，无法验证按钮'))
        return None
    print('  遮罩 %.1fs 出现' % ov)
    pv = overlay_windows()
    hwnd = pv[0][0]
    kids = enum_children(hwnd)
    texts = [t for (_c, _k, t, _v) in kids if t]
    print('  遮罩子窗口文本：%s' % texts)
    hit = find_child_by_text(hwnd, '坐一分钟')
    if hit is None:
        results.append(('G3 坐一分钟', False,
                        'SittingOptionEnabled=1 但遮罩上找不到「坐一分钟」（子窗口文本=%s）'
                        % texts))
        return None
    print('  找到按钮：%s「%s」' % (hit[1], hit[2]))
    click_child(hit[0], 'mouse')
    time.sleep(0.05)
    click_child(hit[0], 'bm')
    gone = wait_overlay_gone(3.0)
    print('  点击后遮罩%s' % (('在 %.2fs 内消失' % gone) if gone is not None else '未消失'))
    exp_ok = gone is not None
    found_txt.append(hit[2])

    # ---- 对照组：开关关 → 找不到 ----
    if not stop_eyelite():
        return None
    cfg = dict(base)
    cfg.update({'SittingOptionEnabled': '0', 'RestSeconds': '30'})
    write_cfg(cfg)
    proc = launch()
    print('  对照组 cfg: SittingOptionEnabled=0 → 期望**找不到**「坐一分钟」')
    time.sleep(1.5)
    if not alive(proc.pid):
        return None
    ov2 = wait_overlay(100.0)
    if ov2 is None:
        results.append(('G3 坐一分钟', False, '对照组遮罩未出现'))
        return None
    hwnd2 = overlay_windows()[0][0]
    texts2 = [t for (_c, _k, t, _v) in enum_children(hwnd2) if t]
    print('  对照组遮罩 %.1fs 出现，子窗口文本=%s' % (ov2, texts2))
    absent = find_child_by_text(hwnd2, '坐一分钟') is None

    ok = exp_ok and absent
    results.append(('G3 坐一分钟', ok,
                    '开=找到「%s」且点击后 %s 消失｜关=找不到（%s）'
                    % (found_txt[0] if found_txt else '?',
                       ('%.2fs 内' % gone) if gone is not None else '未',
                       '对照组通过' if absent else '对照组也找到了，判据无判别力')))


def case_a5(base, results):
    """A5 仪式音：**间接验证** —— 只断言"会响的那条路径仍然按时走完"。

    声音本身无法用窗口/钩子观测，这条最多证明"没有因为放音把预告/遮罩搞坏"。
    报告必须写明"声音未直接验证"。
    """
    print('\n--- A5 仪式音（间接：只验流程未被打断）---')
    if not stop_eyelite():
        return None
    cfg = dict(base)
    cfg.update({'Plugin_Sound': '1', 'PreviewSeconds': '30', 'RestSeconds': '8'})
    write_cfg(cfg)
    proc = launch()
    print('   cfg: Plugin_Sound=1 PreviewSeconds=30 RestSeconds=8')
    time.sleep(1.5)
    if not alive(proc.pid):
        return None
    t_pv = wait_preview(100.0)
    if t_pv is None:
        results.append(('A5 仪式音（间接）', False,
                        'Plugin_Sound=1 时预告横幅 100s 内未出现'))
        return None
    t_ov = wait_overlay(60.0)
    still = alive(proc.pid)
    extra = [t for t in own_windows() if (t[1], t[2]) != (0, 0)]
    ok = (t_ov is not None) and still
    results.append(('A5 仪式音（间接）', ok,
                    '预告 %.1fs、遮罩 %s、进程存活=%s、窗口数=%d'
                    '（**声音未直接验证**）'
                    % (t_pv, ('%.1fs' % t_ov) if t_ov is not None else '未出现',
                       still, len(extra))))


# ---------------------------------------------------------------- main


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe not found at %s' % EXE)
        return 2

    want = [a.lower() for a in sys.argv[1:]] or ['all']
    if 'all' in want:
        plan = ['c3', 'c4', 'g3', 'a5']
    else:
        plan = [c for c in ('c3', 'c3cap', 'c4', 'g3', 'a5') if c in want]
    if not plan:
        print('usage: p1-batch1-check.py [all|c3|c3cap|c4|g3|a5]')
        return 2

    if not stop_eyelite():
        print('FAIL: 有残留 EyeLite 进程，无法开始（单实例会让所有用例假通过/假失败）')
        return 2

    backup_raw = read_cfg_raw()
    orig = read_cfg()
    print('orig config: WorkMinutes=%s RestSeconds=%s PreviewSeconds=%s '
          'IdleMinutes=%s DimAlphaMax=%s'
          % (orig.get('WorkMinutes'), orig.get('RestSeconds'),
             orig.get('PreviewSeconds'), orig.get('IdleMinutes'),
             orig.get('DimAlphaMax')))

    # 共同底配置：只留计时所需的最小差异，闸门/渐暗/番茄钟全隔离，
    # 免得 P0 的机制混进来变成"另一条延后原因"。
    base = dict(orig)
    base.update({
        'WorkMinutes': '1',
        'PreviewSeconds': '0',
        'RestSeconds': '8',
        'PomodoroEnabled': '0',
        'IdleMinutes': '0',
        'SedentaryEnabled': '0',
        'SoundEnabled': '0',
        'AllowSkip': '1',
        'StrictMode': '0',
        'SuppressOnFullscreen': '0',
        'DimAlphaMax': '0',
        'Plugin_Sound': '0',
        'ForegroundGuardEnabled': '0',
        'ForegroundWhitelist': '',
        'TypingGuardEnabled': '0',
        'SittingOptionEnabled': '0',
    })

    results = []
    try:
        if 'c3' in plan:
            case_c3(base, results)
        if 'c3cap' in plan:
            case_c3cap(base, results)
        if 'c4' in plan:
            case_c4(base, results)
        if 'g3' in plan:
            case_g3(base, results)
        if 'a5' in plan:
            case_a5(base, results)
    finally:
        stop_eyelite()
        cleanup_probe()
        restore_cfg_raw(backup_raw)

    print()
    for name, ok, detail in results:
        verdict = 'SKIP' if ok is None else ('PASS' if ok else 'FAIL')
        print('%-26s %s  %s' % (name, verdict, detail))
    print('%-26s %s  %s' % ('压暗层 DimForm（信息项）',
                            'yes' if DIM_SEEN[0] else 'no',
                            '出现过，但它不是遮罩'))

    judged = [r for r in results if r[1] is not None]
    skipped = [r for r in results if r[1] is None]
    npass = sum(1 for r in judged if r[1])
    print('\n=== P1-批1 summary: %d/%d PASS%s ==='
          % (npass, len(judged),
             ('  + %d SKIP（环境/仪器不成立，不计 PASS）' % len(skipped))
             if skipped else ''))
    for name, ok, detail in judged:
        if not ok:
            print('  FAILED: %s (%s)' % (name, detail))
    for name, ok, detail in skipped:
        print('  SKIPPED: %s (%s)' % (name, detail))
    if skipped:
        return 2
    return 0 if (len(judged) == len(plan) and npass == len(plan)) else 1


def cleanup_probe():
    """本次没有临时文件，但 p0c/p0d 可能留下目录；只在空目录时删。"""
    try:
        if os.path.isdir(PROBE_DIR) and not os.listdir(PROBE_DIR):
            os.rmdir(PROBE_DIR)
    except Exception:
        pass


if __name__ == '__main__':
    sys.exit(main())
