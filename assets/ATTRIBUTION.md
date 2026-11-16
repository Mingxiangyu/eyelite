# 素材署名与许可（可直接贴进「关于」页）

本程序的动作演示图片来自下面三个开源项目。

* **workout-guide 的图**：原样嵌入，未做裁剪 / 缩放 / 调色 / 重压缩。
* **everkinetic 的颈部图**：**已转换**（白底黑线 → 白线 + 透明底、等比缩放到 512 方图），
  属于衍生作品，**同样以 CC BY-SA 4.0 提供**。转换脚本：`tools/convert-ek.py`。

---

## 1. 动作姿态插图 —— bryllim/workout-guide

- 作品：Workout Guide visual assets（动作姿态插图）
- 作者：**Bryl Lim**（<https://bryllim.com>）
- 来源：<https://github.com/bryllim/workout-guide>
- 许可：**CC BY-SA 4.0**（<https://creativecommons.org/licenses/by-sa/4.0/>）
- 我们的改动：**无**。文件从上游 `packages/workout-guide/assets/<动作>/frame-N.png`
  逐字节复制，未转换格式、未缩放、未裁剪。
- 上游说明：其中部分姿态源自 [Everkinetic](https://github.com/everkinetic/data)
  （同为 CC BY-SA 4.0）；本项目使用的条目在上游 `manifest.json` 中未标注
  `source`/`changes` 字段，即由 Bryl Lim 原创绘制。

> **许可义务（必须遵守）**：署名、附许可链接、注明是否修改。
> 若将来对这些图片做任何修改（裁剪/改色/重压缩/描边），**修改后的图片也必须以
> CC BY-SA 4.0 发布**。程序本身（源代码）不是图片的衍生作品，不受此约束。

## 2. 颈部放松 —— everkinetic/data（已转换的衍生图）

- 作品：Everkinetic 动作插图（`dist/png/0002-relaxation.png` / `0002-tension.png`）
- 来源：<https://github.com/everkinetic/data>
- 许可：**CC BY-SA 4.0**（<https://creativecommons.org/licenses/by-sa/4.0/>）
- 我们的改动：**有**。
  - 原图是**白底黑线**且非方图（631×675 / 581×718）；
  - 转换为 **白线 + 透明底**（alpha = 255 − luma，阈值 40/120 保留抗锯齿）；
  - 等比缩放到 **512×512**、居中、底部对齐；
  - 由 `tools/convert-ek.py` 完成，产物为 `assets/moves/neck-release/frame{1,2}.png`。
- **因属衍生作品，这些图同样以 CC BY-SA 4.0 提供**（不是 Unlicense，也不是"无改动"）。

---

## 3. 逐个动作对照表

| 中文动作 | 目录 | 上游条目 | 帧数 | 许可 |
|---|---|---|---|---|
| 起身伸展 | `moves/stand-reach/` | jumping-jack（f1，单帧复用 3 次） | 3 | CC BY-SA 4.0 |
| 深蹲 10 次 | `moves/squat/` | bodyweight-squat | 3 | CC BY-SA 4.0 |
| 肩颈绕环 | `moves/shoulder-circles/` | arm-circles | 3 | CC BY-SA 4.0 |
| 原地踏步 | `moves/march-in-place/` | high-knees | 3 | CC BY-SA 4.0 |
| 靠墙静蹲 | `moves/wall-stand/` | wall-sit（f1/f2 静蹲 + f3 站起） | 3 | CC BY-SA 4.0 |
| 单臂横跨胸前 | `moves/scapular-retraction/` | cross-body-shoulder-stretch | 3 | CC BY-SA 4.0 |
| 躯干转体 | `moves/torso-twist/` | russian-twist | 3 | CC BY-SA 4.0 |
| 扩胸 | `moves/chest-open/` | doorway-chest-stretch | 3 | CC BY-SA 4.0 |
| 颈部放松 | `moves/neck-release/` | everkinetic 0002（**已转换，衍生图**） | 2（第 3 帧复用第 1 帧） | CC BY-SA 4.0 |

> 已移除：**手腕绕环**（全开源无可用素材：workout-guide 的 wrist-curl/wrist-extension
> 是握哑铃的健身房动作，腕部只占几个像素；everkinetic 289 条里 `wrist` 零命中）。
> 它原来的位置由「躯干转体」替代。

机器可读版本见 `moves/manifest.json`（含每条目的 `attribution` 字段与字节数）。
许可证原文见 `LICENSE-workout-guide.txt`（`LICENSE-free-exercise-db.txt` 现已不再使用，保留仅为历史记录）。
