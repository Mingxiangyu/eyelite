# 素材署名与许可（可直接贴进「关于」页）

本程序的动作演示图片全部来自 **bryllim/workout-guide**，**原样嵌入**，
未做裁剪 / 缩放 / 调色 / 重压缩。

> 曾短暂使用过 everkinetic 的颈部图（转换后），因"只有半身像、动作读不出"已**弃用并删除**，
> 因此本文件不再包含 everkinetic 的署名 —— 不为未使用的素材署名。

---

## 1. 动作姿态插图 —— bryllim/workout-guide

- 作品：Workout Guide visual assets（动作姿态插图）
- 作者：**Bryl Lim**（<https://bryllim.com>）
- 来源：<https://github.com/bryllim/workout-guide>
- 许可：**CC BY-SA 4.0**（<https://creativecommons.org/licenses/by-sa/4.0/>）
- 我们的改动：**无**。文件从上游 `packages/workout-guide/assets/<动作>/frame-N.png`
  逐字节复制，未转换格式、未缩放、未裁剪。
- 上游说明：本项目使用的这些条目，在上游 `manifest.json` 中均未标注 `source`/`changes`
  字段 —— 即由 Bryl Lim 原创绘制，不涉及第三方素材。

> **许可义务（必须遵守）**：署名、附许可链接、注明是否修改。
> 若将来对这些图片做任何修改（裁剪/改色/重压缩/描边），**修改后的图片也必须以
> CC BY-SA 4.0 发布**。程序本身（源代码）不是图片的衍生作品，不受此约束。


## 3. 逐个动作对照表

| 中文动作 | 目录 | 上游条目 | 帧数 | 许可 |
|---|---|---|---|---|
| 起身伸展 | `moves/stand-reach/` | jumping-jack（f1，单帧复用 3 次） | 3 | CC BY-SA 4.0 |
| 深蹲 10 次 | `moves/squat/` | bodyweight-squat | 3 | CC BY-SA 4.0 |
| 肩颈绕环 | `moves/shoulder-circles/` | arm-circles | 3 | CC BY-SA 4.0 |
| 原地踏步 | `moves/march-in-place/` | high-knees | 3 | CC BY-SA 4.0 |
| 靠墙静蹲 | `moves/wall-stand/` | wall-sit（f1/f2 静蹲 + f3 站起） | 3 | CC BY-SA 4.0 |
| 单臂横跨胸前 | `moves/scapular-retraction/` | cross-body-shoulder-stretch | 3 | CC BY-SA 4.0 |
| 侧弓步 | `moves/lateral-lunge/` | lateral-lunge | 3 | CC BY-SA 4.0 |
| 扩胸 | `moves/chest-open/` | doorway-chest-stretch | 3 | CC BY-SA 4.0 |
| 扶墙小腿拉伸 | `moves/calf-stretch/` | wall-calf-stretch | 3 | CC BY-SA 4.0 |

> 已移除过两个动作：
> * **手腕绕环** —— 全开源无可用素材（workout-guide 的腕部动作是握哑铃的健身房动作；
>   everkinetic 289 条里 `wrist` 零命中）；
> * **颈部放松** —— workout-guide 无颈部条目，everkinetic 只有半身像，读不出动作。
>
> 它们的位置分别由「侧弓步」「扶墙小腿拉伸」替代。

机器可读版本见 `moves/manifest.json`（含每条目的 `attribution` 字段与字节数）。
许可证原文见 `LICENSE-workout-guide.txt`（`LICENSE-free-exercise-db.txt` 现已不再使用，保留仅为历史记录）。
