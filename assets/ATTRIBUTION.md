# 素材署名与许可（可直接贴进「关于」页）

本程序的动作演示图片来自下面两个开源项目。图片**原样嵌入，未做任何裁剪、缩放、
调色或重新压缩**（`changes: ""`）。

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

## 2. 颈部 / 腕部动作照片 —— yuhonas/free-exercise-db

- 作品：free-exercise-db（`Side_Neck_Stretch`、`Wrist_Circles` 两组图片）
- 作者：yuhonas/free-exercise-db 贡献者
- 来源：<https://github.com/yuhonas/free-exercise-db>
- 许可：**Unlicense（公有领域）**（<https://unlicense.org/>）
- 我们的改动：**无**（当前阶段原样放入；此源允许任意裁剪/抠图/调色，
  后续渲染阶段可按需处理，无任何义务）

---

## 3. 逐个动作对照表

| 中文动作 | 目录 | 上游条目 | 帧数 | 许可 |
|---|---|---|---|---|
| 起身伸展 | `moves/stand-reach/` | worlds-greatest-stretch | 3 | CC BY-SA 4.0 |
| 深蹲 10 次 | `moves/squat/` | bodyweight-squat | 3 | CC BY-SA 4.0 |
| 肩颈绕环 | `moves/shoulder-circles/` | arm-circles | 3 | CC BY-SA 4.0 |
| 原地踏步 | `moves/march-in-place/` | high-knees | 3 | CC BY-SA 4.0 |
| 靠墙站立 | `moves/wall-stand/` | wall-sit | 3 | CC BY-SA 4.0 |
| 肩胛后缩 | `moves/scapular-retraction/` | cross-body-shoulder-stretch | 3 | CC BY-SA 4.0 |
| 扩胸 | `moves/chest-open/` | doorway-chest-stretch | 3 | CC BY-SA 4.0 |
| 颈部放松 | `moves/neck-release/` | Side_Neck_Stretch | 2 | Unlicense |
| 手腕绕环 | `moves/wrist-circles/` | Wrist_Circles | 2 | Unlicense |

机器可读版本见 `moves/manifest.json`（含每条目的 `attribution` 字段与字节数）。
许可证原文见 `LICENSE-workout-guide.txt` 与 `LICENSE-free-exercise-db.txt`。
