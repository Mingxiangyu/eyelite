#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成 assets/app.ico —— 程序的 exe 图标（任务栏 / 资源管理器 / Alt+Tab 都用它）。

为什么要单独做这一步：`tray.Icon` 只是**托盘**里的小图标，它是运行时画的；
而 exe 文件本身的图标是**编译期**嵌进去的 win32 资源。没有它，设置窗在任务栏上、
任务管理器里都显示系统默认的空白图标 —— 用户反馈的"任务栏没 logo"就是这件事。

造型与托盘进度环一致（同色系、同为圆环），保证"托盘里那个"和"任务栏上那个"是同一个东西：
    底色  深墨蓝 #1F2937（与主按钮 Ui.PrimaryBg 同色）
    圆环  薄荷绿 #5FD3A6（与 Ui.Accent 同色）
    环宽  外径的 0.20，环心在外径的 0.70 处

用纯 Python 手写 PNG（标准库 zlib）+ ICO 容器，不依赖 PIL。
ICO 里放多档尺寸：16/24/32/48/64/128/256 —— Windows 会按场景各取一档，
只放一个大图会被缩放成糊的。

用法：python tools/make-icon.py
"""
import os
import struct
import zlib

BG = (0x1F, 0x29, 0x37)      # 深墨蓝
RING = (0x5F, 0xD3, 0xA6)    # 薄荷绿
SIZES = [16, 24, 32, 48, 64, 128, 256]


def render(size):
    """画一张 size×size 的 RGBA 图（返回原始像素行，每行含 filter 字节 0）。"""
    n = float(size)
    cx = cy = (n - 1) / 2.0
    r_outer = n * 0.5 - n * 0.045          # 留一点边，避免圆环切线被切
    r_ring = r_outer * 0.70
    half_w = r_outer * 0.10                # 环半宽 = 外径的 0.10（整宽 0.20）

    # 抗锯齿靠"到环中心线的距离"做一像素过渡
    aa = max(1.0, n / 32.0)

    rows = []
    for y in range(size):
        row = bytearray()
        row.append(0)                       # PNG filter type 0
        for x in range(size):
            dx = x - cx
            dy = y - cy
            dist = (dx * dx + dy * dy) ** 0.5

            # 外圆（底色）—— 边缘 1px 抗锯齿
            a_disc = clamp((r_outer - dist) / aa)
            # 圆环带：|dist - r_ring| < half_w
            ring_d = abs(dist - r_ring)
            a_ring = clamp((half_w - ring_d) / aa)

            if a_disc <= 0.0:
                row += b'\x00\x00\x00\x00'
                continue

            r, g, b = BG
            if a_ring > 0.0:
                # 在底色上叠环色
                k = a_ring
                r = int(r * (1 - k) + RING[0] * k)
                g = int(g * (1 - k) + RING[1] * k)
                b = int(b * (1 - k) + RING[2] * k)
            row += bytes((r, g, b, int(a_disc * 255)))
        rows.append(bytes(row))
    return b''.join(rows)


def clamp(v):
    if v < 0.0:
        return 0.0
    if v > 1.0:
        return 1.0
    return v


def png_bytes(size):
    raw = render(size)

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    out = b'\x89PNG\r\n\x1a\n'
    out += chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))  # 8bit RGBA
    out += chunk(b'IDAT', zlib.compress(raw, 9))
    out += chunk(b'IEND', b'')
    return out


def build_ico(path):
    images = [png_bytes(s) for s in SIZES]

    header = struct.pack('<HHH', 0, 1, len(SIZES))     # reserved / type=1(icon) / count
    offset = 6 + 16 * len(SIZES)

    entries = b''
    for s, data in zip(SIZES, images):
        dim = 0 if s >= 256 else s                     # ICO 里 0 表示 256
        entries += struct.pack('<BBBBHHII',
                               dim, dim, 0, 0,         # w, h, 调色板数, reserved
                               1, 32,                  # planes, bpp
                               len(data), offset)
        offset += len(data)

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(header + entries + b''.join(images))
    return os.path.getsize(path)


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(root, 'assets', 'app.ico')
    n = build_ico(out)
    print('已生成: %s  (%d 字节, %d 档: %s)'
          % (out, n, len(SIZES), '/'.join(str(s) for s in SIZES)))
