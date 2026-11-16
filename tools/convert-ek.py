#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
convert-ek.py —— 把 everkinetic 的「白底黑线」PNG 转成我们用的「白线 + 透明底」512 方图。

只跑一次，产物直接入库（assets/moves/neck-release/frame1.png / frame2.png）。

为什么要转：
  · everkinetic 的 dist/png 是**白底黑线**，而本项目所有素材都是**白线 + 透明底**；
    直接塞进去会变成一块白方块，在深色 UI 上极其突兀。
  · 它的图也不是 512 方图（颈部这张是 631×675），而加载器按方图画布贴图。

算法（与需求逐条对应）：
  1. 解 PNG（PIL），取 RGB；
  2. alpha = 255 - luma（黑线 → 不透明 255，白底 → 透明 0）；
  3. 阈值化：alpha < 40 → 0；alpha > 120 → 255；中间线性（保留抗锯齿边缘）；
  4. 颜色统一为纯白 #FFFFFF（alpha 承载形状，RGB 全 255）；
  5. 裁掉透明边 → 等比缩放到 512×512 内 → **居中、底部对齐**（脚/下巴贴画布底边，
     与 workout-guide 那批图的构图一致：底部对齐、左右居中）；
  6. 存 32 位 PNG。

许可提示：everkinetic 是 CC BY-SA 4.0，本脚本产出的是**衍生图**，
必须同样以 CC BY-SA 4.0 提供（见 assets/ATTRIBUTION.md）。

用法：
    python tools/convert-ek.py <relaxation.png> <tension.png> <输出目录>
"""

import os
import sys

from PIL import Image


CANVAS = 512
A_LO = 40      # 低于它一律当透明
A_HI = 120     # 高于它一律当实心


def to_alpha(img):
    """白底黑线 → 只带 alpha 的白线图。"""
    rgb = img.convert("RGB")
    w, h = rgb.size
    px = rgb.load()

    out = Image.new("RGBA", (w, h), (255, 255, 255, 0))
    op = out.load()

    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            # luma：人眼权重（整数近似，避免浮点开销）
            luma = (r * 299 + g * 587 + b * 114) // 1000
            a = 255 - luma
            if a < A_LO:
                a = 0
            elif a > A_HI:
                a = 255
            else:
                # 线性映射到 0..255，保住抗锯齿边
                a = (a - A_LO) * 255 // (A_HI - A_LO)
            if a:
                op[x, y] = (255, 255, 255, a)
    return out


def fit_bottom_center(img):
    """裁透明边 → 等比缩放到 CANVAS 内 → 居中、底部对齐。"""
    bbox = img.getbbox()
    if bbox:
        img = img.crop(bbox)

    w, h = img.size
    scale = min(CANVAS / float(w), CANVAS / float(h))
    nw, nh = max(1, int(round(w * scale))), max(1, int(round(h * scale)))
    img = img.resize((nw, nh), Image.LANCZOS)

    canvas = Image.new("RGBA", (CANVAS, CANVAS), (255, 255, 255, 0))
    canvas.paste(img, ((CANVAS - nw) // 2, CANVAS - nh), img)
    return canvas


def main():
    if len(sys.argv) != 4:
        print(__doc__)
        return 2

    srcs = [sys.argv[1], sys.argv[2]]
    out_dir = sys.argv[3]
    os.makedirs(out_dir, exist_ok=True)

    for i, src in enumerate(srcs, start=1):
        img = Image.open(src)
        conv = fit_bottom_center(to_alpha(img))
        dst = os.path.join(out_dir, "frame%d.png" % i)
        conv.save(dst, "PNG")
        print("%s  (%dx%d)  ->  %s  512x512" % (src, img.size[0], img.size[1], dst))

    return 0


if __name__ == "__main__":
    sys.exit(main())
