#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
逐个插件实测「开启后多占多少内存 / 多少 GDI 对象」，用来回填设置页「扩展」卡的占用了标注。

纪律：这张卡上只允许写实测值，没测过的写「待测」，**绝不写估算**。
所以这个脚本就是那张卡的数据来源，必须可复现。

做法：
  · 基线 = 6 个插件全关（DimAlphaMax=0，排除压暗层干扰）
  · 逐项只开一个插件，等它真正显示出自己的载体（微光/演示窗/仪式窗）后采样
  · 采样 PrivateUsage（私有提交，与 checkmem.ps1 同口径）与 GDI 对象数
  · 每项都重启进程，避免互相污染；结束按原始字节还原 config

用法：python tools/plugin-mem-check.py
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
CFG = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite', 'config.ini')

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32 = ctypes.WinDLL('user32', use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [('cb', wt.DWORD), ('PageFaultCount', wt.DWORD),
                ('PeakWorkingSetSize', ctypes.c_size_t),
                ('WorkingSetSize', ctypes.c_size_t),
                ('QuotaPeakPagedPoolUsage', ctypes.c_size_t),
                ('QuotaPagedPoolUsage', ctypes.c_size_t),
                ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t),
                ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
                ('PagefileUsage', ctypes.c_size_t),
                ('PeakPagefileUsage', ctypes.c_size_t),
                ('PrivateUsage', ctypes.c_size_t)]


ORDER = ['WorkMinutes', 'RestSeconds', 'PomodoroEnabled', 'PomodoroWork',
         'PomodoroRest', 'AllowSkip', 'PostponeMinutes', 'IdleMinutes',
         'SoundEnabled', 'StrictMode', 'StartWithWindows', 'PreviewSeconds',
         'SedentaryEnabled', 'SedentaryMinutes', 'SedentarySeconds',
         'CameraCountEnabled', 'SuppressOnFullscreen', 'DimAlphaMax']


def write_cfg(d):
    if not os.path.isdir(os.path.dirname(CFG)):
        os.makedirs(os.path.dirname(CFG))
    with open(CFG, 'w', encoding='utf-8') as f:
        f.write('# EyeLite config\n[General]\n')
        for k in ORDER:
            if k in d:
                f.write('%s=%s\n' % (k, d[k]))
        for k in d:
            if k not in ORDER:
                f.write('%s=%s\n' % (k, d[k]))


def read_raw():
    try:
        return open(CFG, 'rb').read()
    except Exception:
        return b''


def pmc(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        return None, None
    try:
        c = PROCESS_MEMORY_COUNTERS_EX()
        c.cb = ctypes.sizeof(c)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(h, ctypes.byref(c), c.cb):
            return None, None
        gdi = user32.GetGuiResources(h, 0)
        return c.PrivateUsage / (1024.0 * 1024.0), gdi
    finally:
        kernel32.CloseHandle(h)


def stop():
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    time.sleep(1.0)


BASE = {
    'WorkMinutes': '20', 'RestSeconds': '20', 'PreviewSeconds': '0',
    'IdleMinutes': '0', 'SoundEnabled': '0', 'StrictMode': '0',
    'PomodoroEnabled': '0', 'SedentaryEnabled': '0',
    'SuppressOnFullscreen': '0', 'DimAlphaMax': '0',
    'Plugin_Sound': '0', 'Plugin_EdgeGlow': '0', 'Plugin_Ritual': '0',
    'Plugin_Stats': '0', 'Plugin_Movement': '0', 'Plugin_Guard': '0',
}

# (名称, 开启用配置, 对照用配置, 额外环境变量, 采样前等待秒数)
#
# **每项都必须有"同配置但插件关"的对照**。第一版没有对照，结果把"预告横幅窗体"
# 和"休息遮罩窗体"的首次创建成本算进了插件头上（Sound +3.1M、Movement +4.6M），
# 那些其实是不开插件也会付的钱。差分的对象必须是"同一时刻、同一批窗体，只差一个插件"。
CASES = [
    ('Stats 三态统计',
     {'Plugin_Stats': '1'}, {'Plugin_Stats': '0'}, {}, 8.0),
    ('Sound 仪式音',
     {'Plugin_Sound': '1', 'WorkMinutes': '1', 'PreviewSeconds': '1'},
     {'Plugin_Sound': '0', 'WorkMinutes': '1', 'PreviewSeconds': '1'},
     {'EYE_LITE_FAKE_WORK_MS': '59500'}, 8.0),
    ('EdgeGlow 边缘微光',
     {'Plugin_EdgeGlow': '1', 'WorkMinutes': '1'},
     {'Plugin_EdgeGlow': '0', 'WorkMinutes': '1'},
     {'EYE_LITE_FAKE_WORK_MS': '50000'}, 8.0),
    ('Ritual 仪式窗',
     {'Plugin_Ritual': '1', 'SedentaryEnabled': '1', 'SedentaryMinutes': '15',
      'SedentarySeconds': '60'},
     {'Plugin_Ritual': '0', 'SedentaryEnabled': '1', 'SedentaryMinutes': '15',
      'SedentarySeconds': '60'},
     {'EYE_LITE_FAKE_SED_MS': '900000'}, 8.0),
    ('Movement 演示窗',
     {'Plugin_Movement': '1', 'SedentaryEnabled': '1', 'SedentaryMinutes': '15',
      'SedentarySeconds': '60'},
     {'Plugin_Movement': '0', 'SedentaryEnabled': '1', 'SedentaryMinutes': '15',
      'SedentarySeconds': '60'},
     {'EYE_LITE_FAKE_SED_MS': '900000'}, 8.0),
    ('Guard 跳过配额',
     {'Plugin_Guard': '1'}, {'Plugin_Guard': '0'}, {}, 8.0),
]


def sample(over, env_extra, wait):
    stop()
    cfg = dict(BASE)
    cfg.update(over)
    write_cfg(cfg)
    env = os.environ.copy()
    for k in ('EYE_LITE_FAKE_WORK_MS', 'EYE_LITE_FAKE_SED_MS',
              'EYE_LITE_FAKE_IDLE_MS', 'EYE_LITE_FORCE_QUNS'):
        env.pop(k, None)
    env.update(env_extra)
    p = subprocess.Popen([EXE], env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(wait)
    mem, gdi = pmc(p.pid)
    stop()
    return mem, gdi


def main():
    if not os.path.exists(EXE):
        print('FAIL: EyeLite.exe 不存在')
        return 2

    raw = read_raw()
    try:
        print('%-18s %10s %10s %8s %8s' % ('插件', '开(Δ)MB', '关(Δ)MB', '净ΔMB', '净ΔGDI'))
        print('-' * 62)
        for name, on_cfg, off_cfg, env_extra, wait in CASES:
            on_mem, on_gdi = sample(on_cfg, env_extra, wait)
            off_mem, off_gdi = sample(off_cfg, env_extra, wait)
            if on_mem is None or off_mem is None:
                print('%-18s %10s %10s %8s %8s' % (name, 'N/A', 'N/A', 'N/A', 'N/A'))
                continue
            dm = on_mem - off_mem
            dg = (on_gdi or 0) - (off_gdi or 0)
            print('%-18s %+9.1fM %+9.1fM %+7.1fM %+8d'
                  % (name, on_mem, off_mem, dm, dg))

        print('')
        print('净Δ = 开插件 − 同配置但关插件。窗体创建成本已被对照抵消。')
        print('回填设置页按 0.5MB 粒度取整；|净Δ|<0.4M 视为噪声，写 <0.5M。')
        return 0
    finally:
        stop()
        if raw:
            with open(CFG, 'wb') as f:
                f.write(raw)
            print('')
            print('config restored: %d bytes' % len(raw))


if __name__ == '__main__':
    sys.exit(main())
