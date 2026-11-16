#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
为 README 生成功能截图。

为什么不用 PIL：本机工作用的 Python 里没有 PIL，而这个项目一直坚持零第三方依赖，
所以这里用 ctypes 调 Win32 + 标准库 zlib 手写 PNG（IHDR/IDAT/IEND 三个块）。

为什么要 PrintWindow 而不是截屏：
  · 截屏会把用户的桌面、别的窗口、壁纸一起拍进公开仓库 —— 隐私问题；
  · 而且本会话里 BitBlt 抓屏拿到过**过期的桌面内容**（已踩过坑，见 tools/p3-motion-check.py 头部）。
PrintWindow(PW_RENDERFULLCONTENT=2) 让目标窗口自己绘制，既不含他人内容，也不受遮挡影响。

用法：
    python tools/shoot.py <输出目录>
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import sys
import time
import zlib

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)   # 否则拿到虚拟化坐标
except Exception:
    pass

u = ctypes.WinDLL('user32', use_last_error=True)
g = ctypes.WinDLL('gdi32', use_last_error=True)
CB = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u.EnumWindows.argtypes = [CB, wt.LPARAM]
u.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
u.GetWindowLongW.restype = ctypes.c_long
u.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', ctypes.c_long), ('biHeight', ctypes.c_long),
                ('biPlanes', wt.WORD), ('biBitCount', wt.WORD), ('biCompression', wt.DWORD),
                ('biSizeImage', wt.DWORD), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', wt.DWORD),
                ('biClrImportant', wt.DWORD)]


def pid_of_name(name):
    """返回所有名为 name 的进程 pid（用 tasklist，避免额外依赖）。"""
    import subprocess
    out = subprocess.run(['tasklist', '/fi', 'imagename eq ' + name, '/fo', 'csv', '/nh'],
                         capture_output=True).stdout.decode('utf-8', 'ignore')
    pids = []
    for line in out.splitlines():
        if name not in line:
            continue
        parts = [x.strip('" ') for x in line.split('","')]
        if len(parts) > 1 and parts[1].isdigit():
            pids.append(int(parts[1]))
    return pids


def kill_all():
    """截图期间独占：清掉所有 EyeLite 实例。
    为什么用 -im：EyeLite 是单实例互斥的，新起的进程会立刻退出并把请求转给旧实例，
    于是"按 p.pid 找窗口"必然落空、旧实例还会越留越多。截图会话开始/结束各清一次。"""
    import subprocess
    subprocess.run(['taskkill', '/im', 'EyeLite.exe', '/f'], capture_output=True)
    time.sleep(0.8)


def windows_of(pid):
    out = []

    live = set(pid_of_name('EyeLite.exe'))
    if pid:
        live.add(pid)

    def cb(h, l):
        if not u.IsWindowVisible(h):
            return True
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value not in live:
            return True
        r = wt.RECT()
        u.GetWindowRect(h, ctypes.byref(r))
        w, hh = r.right - r.left, r.bottom - r.top
        if w >= 120 and hh >= 60:
            ex = u.GetWindowLongW(h, GWL_EXSTYLE)
            out.append((h, w, hh, bool(ex & WS_EX_TRANSPARENT)))
        return True

    u.EnumWindows(CB(cb), 0)
    out.sort(key=lambda x: x[1] * x[2], reverse=True)
    return out


def grab_bgra(hwnd, w, hgt):
    hdc = u.GetDC(0)
    mem = g.CreateCompatibleDC(hdc)
    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(bi)
    bi.biWidth = w
    bi.biHeight = -hgt          # 负 = 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    bits = ctypes.c_void_p()
    hbmp = g.CreateDIBSection(mem, ctypes.byref(bi), 0, ctypes.byref(bits), None, 0)
    g.SelectObject(mem, hbmp)
    ok = u.PrintWindow(hwnd, mem, 2)
    buf = ctypes.string_at(bits, w * hgt * 4)
    g.DeleteObject(hbmp)
    g.DeleteDC(mem)
    u.ReleaseDC(0, hdc)
    return ok, buf


def write_png(path, w, hgt, buf, bg=(24, 26, 30), ignore_alpha=False):
    """BGRA -> RGB（按 bg 合成，处理半透明），再写 PNG。"""
    rows = []
    for y in range(hgt):
        row = bytearray()
        row.append(0)                       # filter type 0
        base = y * w * 4
        for x in range(w):
            b = buf[base + x * 4]
            gg = buf[base + x * 4 + 1]
            r = buf[base + x * 4 + 2]
            a = buf[base + x * 4 + 3]
            if a != 255 and not ignore_alpha:
                k = a / 255.0
                r = int(r * k + bg[0] * (1 - k))
                gg = int(gg * k + bg[1] * (1 - k))
                b = int(b * k + bg[2] * (1 - k))
            row += bytes((r, gg, b))
        rows.append(bytes(row))
    raw = b''.join(rows)

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, hgt, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw, 9))
    png += chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)


def downscale(buf, w, hgt, k):
    """k 倍整数抽样，够用且不引入插值依赖。"""
    nw, nh = w // k, hgt // k
    out = bytearray(nw * nh * 4)
    for y in range(nh):
        sy = y * k
        for x in range(nw):
            si = (sy * w + x * k) * 4
            di = (y * nw + x) * 4
            out[di:di + 4] = buf[si:si + 4]
    return nw, nh, bytes(out)


CFG = os.path.join(os.environ.get('APPDATA', ''), 'EyeLite', 'config.ini')


def write_cfg(d):
    """**关键**：截图依赖的是 config.ini（WorkMinutes/PreviewSeconds/RestSeconds），
    不是环境变量。早先只设 env 导致"预告/遮罩"永远等不到——设了也是白设。"""
    if not os.path.isdir(os.path.dirname(CFG)):
        os.makedirs(os.path.dirname(CFG))
    with open(CFG, 'w', encoding='utf-8') as f:
        NL = chr(13) + chr(10)
        f.write('# EyeLite config' + NL + '[General]' + NL)
        for k, v in d.items():
            if k.startswith('EYE_LITE_'):
                continue
            f.write('%s=%s' % (k, v) + NL)


def shoot(exe, env_extra, out_path, want, timeout=70.0, max_w=1280, label='', settle=0.4, screen=False, ignore_alpha=False):
    """启动 exe，等到 want(w,h) 成立，截图保存。want=None 表示取最大窗口。"""
    import subprocess
    kill_all()                      # 清掉上一条遗留的实例（单实例互斥）
    write_cfg(env_extra)            # 配置必须真写进 config.ini
    e = os.environ.copy()
    e.update(env_extra)
    p = subprocess.Popen([exe], env=e, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    t0 = time.time()
    target = None
    while time.time() - t0 < timeout:
        ws = windows_of(p.pid)
        for (h, w, hh, tr) in ws:
            if want is None:
                if not tr:
                    target = (h, w, hh); break
            elif want(w, hh, tr):
                target = (h, w, hh); break
        if target:
            break
        time.sleep(0.15)
    if not target:
        seen = windows_of(p.pid)
        kill_all()
        print('  [MISS] %-16s 没等到目标窗口；实际看到: %s' % (label, seen[:6]))
        return False

    h, w, hh = target
    time.sleep(settle)      # 等待控件完成首次绘制；设置页尤其需要                      # 让首帧绘制完
    ok, buf = grab_bgra(h, w, hh)      # 只允许 PrintWindow：屏幕取样会抓到桌面上的其它窗口
    if not ok:
        print('  [WARN] %-16s PrintWindow 返回 0（仍然保存）' % label)

    k = 1
    while w // k > max_w:
        k += 1
    if k > 1:
        w, hh, buf = downscale(buf, w, hh, k)
    write_png(out_path, w, hh, buf, ignore_alpha=ignore_alpha)
    kill_all()
    print('  [OK]   %-16s %4dx%-4d  %6.1f KB  -> %s'
          % (label, w, hh, os.path.getsize(out_path) / 1024.0, os.path.basename(out_path)))
    return True


def main():
    outdir = sys.argv[1] if len(sys.argv) > 1 else 'docs/screenshots'
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    exe = os.path.join(root, 'EyeLite.exe')
    if not os.path.isdir(outdir):
        os.makedirs(outdir)

    base = {'SoundEnabled': '0', 'StrictMode': '0', 'IdleMinutes': '0',
            'MeetingGuardEnabled': '0', 'SuppressOnFullscreen': '0',
            'PomodoroEnabled': '0', 'SedentaryEnabled': '0'}

    def cfg(**kw):
        d = dict(base)
        d.update(kw)
        return d

    shots = [
        # 设置页：780x1302（1.5x 下会更宽，按宽高比认）
        ('01-settings.png', cfg(WorkMinutes='20', RestSeconds='20', DimAlphaMax='45',
                                **{'EYE_LITE_OPEN_SETTINGS': '1'}),
         lambda w, h, tr: (not tr) and w > 700 and h > 800, '设置页', 2.0, True),
        # 预告横幅：400x136 逻辑 -> 600x204 物理
        # FAKE_WORK_MS 是"一次性预置累加器"，不是"设定周期"：
        # WorkMinutes=1（上限 60s）预置 52s -> 剩余 8s，正好落进 10s 预告窗，1~2 秒内出横幅
        # 预告横幅：**不用 FAKE 钩子**，按真实时序等 50 秒（钩子在这条路径上实测抓不到窗口）。
        # RestSeconds 设大一点，避免横幅刚出就被遮罩顶掉。
        ('02-preview.png', cfg(WorkMinutes='1', PreviewSeconds='10', RestSeconds='20', DimAlphaMax='0'),
         lambda w, h, tr: 430 <= w <= 600 and 140 <= h <= 220, '预告横幅'),
        # 休息遮罩：预置 58s -> 2 秒后到点，遮罩持续 RestSeconds=8s
        ('03-rest-overlay.png', cfg(WorkMinutes='1', RestSeconds='8', DimAlphaMax='0',
                                    **{'EYE_LITE_FAKE_WORK_MS': '58000'}),
         lambda w, h, tr: (not tr) and w >= 1500, '休息遮罩', 0.8, False, True),
        # 例程窗（素材）
        ('04-routine-asset.png', cfg(WorkMinutes='20', DimAlphaMax='0',
                                     **{'EYE_LITE_FAKE_ACTION': '深蹲 10 次'}),
         lambda w, h, tr: (not tr) and 1000 <= w <= 1150 and 900 <= h <= 1000, '例程·素材'),
        # 例程窗（自绘）
        ('05-routine-drawn.png', cfg(WorkMinutes='20', DimAlphaMax='0',
                                     **{'EYE_LITE_FAKE_ACTION': '肩颈绕环'}),
         lambda w, h, tr: (not tr) and 1000 <= w <= 1150 and 900 <= h <= 1000, '例程·自绘'),
    ]

    only = sys.argv[2] if len(sys.argv) > 2 else ''
    raw = None
    try:
        raw = open(CFG, 'rb').read()
    except Exception:
        pass

    ok = 0
    n = 0
    for item in shots:
        name, env, want, label = item[0], item[1], item[2], item[3]
        settle = item[4] if len(item) > 4 else 0.4
        use_screen = bool(item[5]) if len(item) > 5 else False
        ign_a = bool(item[6]) if len(item) > 6 else False
        if only and only not in name:
            continue
        n += 1
        path = os.path.join(outdir, name)
        if shoot(exe, env, path, want, label=label, settle=settle,
                 screen=use_screen, ignore_alpha=ign_a):
            ok += 1
    print('\n完成 %d/%d' % (ok, len(shots)))
    return 0 if ok == len(shots) else 1


if __name__ == '__main__':
    sys.exit(main())
