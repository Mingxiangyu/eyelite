#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P0 验收总回归：一条命令按顺序跑完全部脚本，逐项收集结果。

为什么需要这一层：
  · 部分脚本只在自己的 docstring 里"声明"前置配置、并不真的写入，
    直接跑会拿用户真实配置（WorkMinutes=20）去等遮罩，得到一堆假 FAIL。
    这里统一在每次运行前写入前置、运行后按**原始字节**还原。
  · 每个脚本都要独占 EyeLite（单实例），必须串行，且跑完清残留。
  · 最终结论必须以**同一个产物指纹**为前提，所以开头结尾各核一次 md5。

用法：
    python tools/p0-regression.py            # 跑全部
    python tools/p0-regression.py p0c p0e    # 只跑名字里含这些片段的
"""

import hashlib
import os
import re
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
SRC = os.path.join(ROOT, 'src', 'Program.cs')
CFG_DIR = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite')
CFG = os.path.join(CFG_DIR, 'config.ini')
PY = sys.executable

# 每次运行前统一写入的前置（脚本自己会覆盖的，就让它覆盖）
BASE_CFG = {
    'WorkMinutes': '1',
    'RestSeconds': '8',
    'PomodoroEnabled': '0',
    'PomodoroWork': '25',
    'PomodoroRest': '5',
    'AllowSkip': '1',
    'PostponeMinutes': '5',
    'IdleMinutes': '0',
    'SoundEnabled': '0',
    'StrictMode': '0',
    'StartWithWindows': '1',
    'PreviewSeconds': '10',
    'SedentaryEnabled': '0',
    'SedentaryMinutes': '45',
    'SedentarySeconds': '120',
    'CameraCountEnabled': '0',
    'SuppressOnFullscreen': '1',
    'DimAlphaMax': '45',
    # P1 新增键：**默认全部关掉**。
    # 理由：C3（前台白名单）/ C4（打字段）都会让遮罩出现时刻偏离 60s，
    # 而 P0 回归量的是核心行为。守卫由 tools/p1-batch1-check.py 单独验证。
    'ForegroundGuardEnabled': '0',
    'TypingGuardEnabled': '0',
    'SittingOptionEnabled': '0',
    'Plugin_Sound': '0',
    'Plugin_EdgeGlow': '0',
    'Plugin_Ritual': '0',
    'Plugin_Stats': '0',
    'Plugin_Movement': '0',
    'Plugin_Guard': '0',
}

ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
         'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
         'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
         'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
         'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax',
         'ForegroundGuardEnabled', 'TypingGuardEnabled', 'SittingOptionEnabled',
         'Plugin_Sound', 'Plugin_EdgeGlow', 'Plugin_Ritual', 'Plugin_Stats',
         'Plugin_Movement', 'Plugin_Guard']


def md5(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 16), b''):
            h.update(chunk)
    return h.hexdigest()


def read_bytes():
    try:
        with open(CFG, 'rb') as f:
            return f.read()
    except Exception:
        return b''


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


def stop_eyelite():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    time.sleep(1.0)


TESTS = [
    ('p0a 计时精度',        'tools/p0a-timing-check.py', []),
    ('p0e 严格档输入',      'tools/p0e-input-check.py', []),
    ('p0c 闸门 6 例',       'tools/p0c-gate-check.py', []),
    ('p0c 抑制 + 强制放行', 'tools/p0c-suppress-check.py', ['all']),
    ('p0b 预告 5 例',       'tools/p0b-preview-check.py', []),
    ('p0b 三出口',          'tools/p0b-exits-check.py', []),
    ('p0a2 A2/A3 + 内存',   'tools/p0a2-a3-check.py', []),
    ('p0d 空闲计时',        'tools/p0d-idle-check.py', []),
]


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe 不存在')
        return 2

    filters = sys.argv[1:]
    exe_md5 = md5(EXE)
    exe_size = os.path.getsize(EXE)
    src_size = os.path.getsize(SRC)
    src_md5 = md5(SRC)
    print('=' * 72)
    print('被测产物: EyeLite.exe %d bytes | md5 %s' % (exe_size, exe_md5))
    print('参考源码: Program.cs %d bytes | md5 %s（仅供追溯，不作为有效性依据）'
          % (src_size, src_md5))
    print('=' * 72)

    # 有效性依据只看 **exe**：源码可能在测试期间被另一路并行修改，
    # 拿源码 md5 判定"结果是否有效"会产生必然的假警报（踩过）。
    print('>> 有效性依据 = exe 的 size+md5 在整轮运行中不变；源码 md5 变化不影响结论。')

    stop_eyelite()
    raw = read_bytes()
    print('config 原始备份: %d bytes' % len(raw))

    results = []
    try:
        for title, rel, extra in TESTS:
            if filters and not any(f in rel for f in filters):
                continue
            script = os.path.join(ROOT, rel)
            if not os.path.exists(script):
                print('\n[SKIP] %s —— 脚本不存在: %s' % (title, rel))
                results.append((title, None, 'missing'))
                continue

            print('\n' + '#' * 72)
            print('# %s  (%s)' % (title, rel))
            print('#' * 72)

            stop_eyelite()
            write_cfg(BASE_CFG)

            t0 = time.time()
            try:
                p = subprocess.run([PY, '-u', script] + extra,
                                   cwd=ROOT, capture_output=True, timeout=1500)
                out = p.stdout.decode('utf-8', 'ignore')
                err = p.stderr.decode('utf-8', 'ignore')
                code = p.returncode
            except subprocess.TimeoutExpired:
                out = ''
                err = 'TIMEOUT after 1500s'
                code = -9
            dur = time.time() - t0

            print(out.rstrip())
            if err.strip():
                print('--- stderr ---')
                print(err.rstrip())

            # 汇总行与 FAIL 行
            summ = [l for l in out.splitlines() if 'summary' in l.lower() or 'PASS ===' in l]
            fails = [l for l in out.splitlines() if 'FAIL' in l]
            print('\n>>> %s: exit=%s, %.0fs' % (title, code, dur))
            for l in summ:
                print('    ' + l.strip())
            for l in fails[:10]:
                print('    ' + l.strip())

            results.append((title, code, '%.0fs' % dur))
            stop_eyelite()
            if raw:
                with open(CFG, 'wb') as f:
                    f.write(raw)
    finally:
        stop_eyelite()
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('\nconfig 已还原: %d bytes (备份 %d bytes)' % (os.path.getsize(CFG), len(raw)))

    exe_md5_end = md5(EXE)
    exe_size_end = os.path.getsize(EXE)

    print('\n' + '=' * 72)
    print('逐项结果')
    print('=' * 72)
    for title, code, dur in results:
        if code == 0:
            verdict = 'PASS'
        elif code is None:
            verdict = 'MISSING'
        else:
            verdict = 'FAIL(exit=%s)' % code
        print('%-24s %-14s %s' % (title, verdict, dur))

    ok = sum(1 for r in results if r[1] == 0)
    print('\n通过 %d/%d 个脚本' % (ok, len(results)))
    unchanged = (exe_md5_end == exe_md5 and exe_size_end == exe_size)
    print('\n被测产物（运行后复核）: exe %d bytes | md5 %s %s'
          % (exe_size_end, exe_md5_end,
             '(未变 OK，本报告有效)' if unchanged else '(已变!! 本报告作废)'))
    return 0 if ok == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
