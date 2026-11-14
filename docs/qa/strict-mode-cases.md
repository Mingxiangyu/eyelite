# EyeLite 回归用例集

> 维护约定：用例按 **P0-x 批次**编号，与调研文档 §八 的落地顺序一一对应。
> 每条用例必须写明**判定方式**——靠肉眼看的记为「人工」，靠脚本判定的一律给出脚本路径。
> **未实测的用例不得标 PASS**，只写「未测」。

---

## A11 · 严格模式跳过键（历史用例）

| ID | 用例 | 判定 | 状态 |
|---|---|---|---|
| A11-1 | 休息中按 Esc → 休息结束 | 人工 | ✅ PASS |
| A11-2 | 休息中按空格 → 休息结束 | 人工 | ✅ PASS |
| A11-3 | 休息中按其它键 → 不结束、不影响输入 | 人工 | ✅ PASS |

**实测方式**：`tools/a11-case.py`（钩子链观测判定，不靠肉眼）。
历史结论：`7333d76` 提交前实测 R1 / R2 各 3 轮全 PASS。

---

## P0-A / P0-D · 计时与有效时间

| ID | 用例 | 期望 | 判定 | 状态 |
|---|---|---|---|---|
| P0-A-1 | 工作计时到点，遮罩出现 | 60 ± 3 s | 脚本 | ✅ PASS |
| P0-A-2 | 休息时长准确 | 8 ± 2 s | 脚本 | ✅ PASS |
| P0-D-1 | 离开超过 `IdleMinutes` → 计时暂停 | 剩余时间不再减少 | 人工 | ⬜ 未测 |
| P0-D-2 | 离开超过整个休息时长 → 周期直接重置（不弹遮罩） | 回来后不立刻弹 | 人工 | ⬜ 未测 |
| P0-D-3 | 休眠后回来，活跃时长**不包含**睡眠时段 | 剩余时间不会跳到 0 | 人工 | ⬜ 未测 |

**实测方式**：`tools/p0a-timing-check.py`（只读观测，不注入按键不抢焦点）。
P0-A-1 / P0-A-2 实测 3 轮全 PASS：遮罩 60.76 / 60.70 / 60.72 s，休息 7.77 / 7.86 / 7.76 s。

> P0-D-1 ~ P0-D-3 需人工参与（离开 / 休眠），脚本无法代劳。

---

## P0-B · 30 秒预告 + 三出口

| ID | 用例 | 期望 | 判定 | 状态 |
|---|---|---|---|---|
| P0-B-1 | 预告横幅在到点前 `PreviewSeconds` 秒出现 | 50 ± 4 s（工作 60 s / 预告 30 s → 约 30 s 处） | 脚本 | ✅ PASS |
| P0-B-2 | 预告期**键盘零干预** | 钩子链观测不到吞键 | 脚本 | ✅ PASS |
| P0-B-3 | 预告期**不抢焦点** | 前台窗口句柄不变 | 脚本 | ✅ PASS |
| P0-B-4 | 到点后预告转全屏遮罩 | 横幅消失、遮罩出现 | 脚本 | ✅ PASS |
| P0-B-5 | 遮罩持续时长准确 | 8 ± 2 s | 脚本 | ✅ PASS |
| P0-B-6 | 「延后」= 阈值后移，本轮不结束 | 剩余时间按 `PostponeMinutes` 增加 | 人工 | ⬜ 未测 |
| P0-B-7 | 「跳过」= 满周期重新计时 | 剩余时间回到整周期 | 人工 | ⬜ 未测 |
| P0-B-8 | 「立即休息」= 立刻进入遮罩 | 遮罩立即出现 | 人工 | ⬜ 未测 |

**实测方式**：`tools/p0b-preview-check.py`。
实测 2 轮全 PASS：T1 50.54 s / T2 观测到 1 次钩子事件 / T3 前台未变 / T4 10.33 s 转遮罩 / 遮罩 8.78 s。

> **仪器可信度前置条件**：任何靠钩子链观测的用例，必须先跑 `tools/p0b-hook-selfcheck.py` 确认仪器本身能收到事件。
> 仪器故障会伪装成产品缺陷（ctypes 回调签名写错时回调抛异常被静默吞掉，表现为"钩子装上了但永远收不到事件"）。

---

## P0-C · 系统通知状态闸门（FullscreenDndGate）

判定方式：脚本 `tools/p0c-gate-check.py`，用环境变量 `EYE_LITE_FORCE_QUNS` 强制状态覆盖真实 API。

| ID | 用例 | QUNS | 期望 | 状态 |
|---|---|---|---|---|
| P0-C-1 | 运行时全屏 D3D → **不弹**遮罩、**不弹**预告 | 4 | 无遮罩、无横幅 | ✅ 实测 PASS |
| P0-C-2 | 使用 D3D 全屏 → 同上 | 2 | 同上 | ✅ 实测 PASS |
| P0-C-3 | 演示模式 → 同上 | 3 | 同上 | ✅ 实测 PASS |
| P0-C-4 | **系统免打扰 → 照常弹**（本机真实状态） | 5 | 有横幅 + 有遮罩 | ✅ 实测 PASS |
| P0-C-5 | 正常状态 → 照常弹 | 0 | 有横幅 + 有遮罩 | ✅ 实测 PASS |
| P0-C-6 | 关闭 `SuppressOnFullscreen` + QUNS=4 → 照常弹 | 4 | 有横幅 + 有遮罩 | ✅ 实测 PASS |
| P0-C-7 | 抑制期**不装键盘钩子**、不吞按键 | 4 | 钩子链观测不到吞键 | ⬜ 未测 |
| P0-C-8 | 抑制解除后立刻补弹，不重复计时 | 4 → 0 | 解除后立刻有遮罩 | ⬜ 未测 |
| P0-C-9 | 抑制超过 30 分钟强制放行 | 4 | 30 min 后仍弹 | ⬜ 未测（需 30 min） |

**实测结果**（`tools/p0c-gate-check.py`，2026-11-14，三轮迭代后 6/6 PASS）

```
T1 QUNS=4 RUNNING_D3D_FULL_SCREEN   PASS  preview=no overlay=no  (expect overlay=False)
T2 QUNS=2 USING_D3D_FULL_SCREEN     PASS  preview=no overlay=no  (expect overlay=False)
T3 QUNS=3 PRESENTATION_MODE         PASS  preview=no overlay=no  (expect overlay=False)
T4 QUNS=5 QUIET_TIME (本机真实状态)  PASS  preview=28.9s overlay=58.7s (expect overlay=True)
T5 QUNS=0 NOTIFICATION_FORCED_ON    PASS  preview=29.7s overlay=59.5s (expect overlay=True)
T6 QUNS=4 + 关闭开关                 PASS  preview=28.6s overlay=58.5s (expect overlay=True)

=== P0-C summary: 6/6 PASS ===
```

注意 T4/T5/T6 的 `preview` 与 `overlay` **都必须有**：抑制用例要求两者**都没有**
（横幅也要被压住），放行用例要求两者**都有**（走完整 P0-B 预告流程）。
早先版本只断言 overlay，漏掉了横幅，已修。

**仪器踩坑记录（两处，都是"仪器缺陷伪装成产品缺陷"）**

1. `overlay_windows()` 用 `w > 800 and h > 500` 判定，把 **Windows 桌面窗口
   （Program Manager，3840×2160）** 当成了 EyeLite 遮罩 → 全部用例误报
   `overlay@0.0s`。修法：`GetWindowThreadProcessId` 取 PID，只认 EyeLite 自己的窗口。
2. `eyelite_pid()` 每次轮询都调 `tasklist`（0.3~1 秒），在 0.15 秒的轮询间隔里
   把观测节奏拖垮，导致"遮罩明明在，脚本却说没有"（T4 误报 FAIL）。
   修法：PID 缓存（`Popen` 直接拿 `proc.pid`）+ 轮询降到 0.05 秒。

**抑制源白名单（实测确定，契约 v11 §10.1.1）**

| QUNS | 含义 | 抑制 |
|---|---|---|
| 0 | `NOTIFICATION_FORCED_ON` | ❌ |
| 1 | `BUSY` | ✅ |
| 2 | `USING_D3D_FULL_SCREEN` | ✅ |
| 3 | `PRESENTATION_MODE` | ✅ |
| 4 | `RUNNING_D3D_FULL_SCREEN` | ✅ |
| 5 | `QUIET_TIME` | ❌ **实测修正** |
| 6 | `BUSY_DND` | ❌ **实测修正** |

> **为什么 5 / 6 不抑制**：本机 `QUNS` 恒为 5（Win11 系统级免打扰常开，无 `FocusAssist` 注册表项）。
> 照调研文档 C1 原文把它当硬抑制源，本机 EyeLite 会**永不提醒**。详见契约 v11 §10.1.1。

---

## P0-E · 严格模式降档

| ID | 用例 | 期望 | 判定 | 状态 |
|---|---|---|---|---|
| P0-E-1 | 默认配置（`StrictMode=0`）下，休息期间普通按键**照常输入** | 能正常打字 | 脚本 `T1` | ✅ 实测 PASS（2026-11-14） |
| P0-E-2 | 默认配置下，Esc / 空格仍能跳过 | 休息结束 | 脚本 | ✅ 实测 PASS（T4 覆盖） |
| P0-E-3 | `StrictMode=1` 时键盘被锁 | 无法打字，Esc / 空格可退出 | 脚本 `T3` 对照组 | ✅ 实测 PASS（吞键已证实可见） |
| P0-E-4 | **Ctrl 不卡住**：休息期间 Ctrl 成对通过或成对吞掉，绝不半吞 | down/up 必须成对 | 脚本 `T2` / `T3b` | ✅ 实测 PASS（2026-11-14） |
| P0-E-5 | 休息结束后物理键全部释放 | 七键全 `up` | 脚本 `T2b` / `T4` | ✅ 实测 PASS（2026-11-14） |

**实测结果**（`tools/p0e-input-check.py`，2026-11-14）

```
observer hook installed (WH_KEYBOARD_LL), 前置于 EyeLite 的钩子
instrument selfcheck: PASS（F24 down/up 均被 observer 看到，2 条）
orig config: StrictMode=0 WorkMinutes=20 RestSeconds=20

T3  严格档对照组（仪器应看得见吞键）  PASS  遮罩61.3s F24 被放行=0 条(期望0)
T3b 严格档裸 Ctrl 配对               PASS  down=0 up=0 共0条
T1  默认档遮罩期按键送达（输入未被吞）  PASS  遮罩61.5s F24 放行=2 条（期望 2）
T2  默认档裸 Ctrl 必须送达且成对      PASS  down=1 up=1 共2条
T2b 默认档连续按键后物理键无残留        PASS  stuck=无
T4  休息结束后物理键全部释放           PASS  stuck=无

=== P0-E summary: 5/5 PASS（不含对照组）===
```

**判定方法（v2，钩子链）**：低级键盘钩子按「最后安装的最先调用」执行，
任一钩子返回非 0 则链终止。脚本在**启动 EyeLite 之前**装好 observer，
于是 EyeLite 休息期装的钩子排在它之后（先被调用）——observer 收到 = 放行，
收不到 = 被吞。全程不建窗口、不抢前台、只注入不产生字符的 `VK_F24` 与裸 `Ctrl`。

> **为什么必须有 T3 对照组**：没有它，"默认档没吞键"可能只是仪器瞎了。
> 先证明仪器在严格档下确实"看不见"按键，再证明默认档下"看得见"，结论才成立。

> **P0-E-4 的成对性判据**：只放行 down 不放行 up（或反之）会让应用侧认为
> Ctrl 一直按着 —— 这正是用户报告过的「ctrl 一直卡住」的根因形态。
> 因此合法结果只有两种：**2 条且 down/up 各 1**，或 **0 条全吞**。半吞一律 FAIL。

**仪器踩坑记录（三次，同源）**：都是"仪器缺陷伪装成产品缺陷"。

1. `SetWindowsHookExW` 的 `hinst` 传 32 位残句柄 → `err=126 ERROR_MOD_NOT_FOUND`。
   根因：`GetModuleHandleW` 未声明 `restype`，64 位句柄被截断。
2. `keybd_event(VK_CONTROL)` 实际产出的 `vkCode` 是 **`VK_LCONTROL(0xA2)`**，
   不是 `VK_CONTROL(0x11)`。只比 `0x11` 会把消息全漏掉，表现为
   "默认档也放行 0 条"这种**假 PASS**。修法：`CTRL_VKS = (0x11, 0xA2)`。
3. v1 用 `SetForegroundWindow` 抢前台做判定 —— 既抢不到（前台锁），
   又**与"不得影响用户输入"这条待验证目标自相矛盾**。改用钩子链后彻底不需要前台。

---

## P0-F · 等 AI 输出的停顿检测

尚未实现。用例待方案定稿后补（三个待拍板决策点见调研文档 §八·补充）。

---

## 通用纪律

1. **观测优先于注入**：能用只读观测（窗口枚举 / 计时 / 进程内存）判定的，不要注入按键。
2. **仪器先自检**：任何依赖钩子链的判定，先跑仪器自检脚本。
3. **窗口识别必须按 PID 过滤**：Windows 桌面（`Program Manager` / `WorkerW`）本身就是一个
   3840×2160 的可见顶层窗口，不按进程过滤会把它当成遮罩，表现为"遮罩立刻出现"，
   所有用例全错。这个坑已经踩过一次。
4. **配置必须还原**：任何改过 `%APPDATA%\EyeLite\config.ini` 的测试，结束后必须还原用户原值
   （含未识别的自定义键，不能只写已知键列表）。
