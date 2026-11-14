# `IRestGate` 契约（v6 · 冻结）

> 状态：**已冻结**。P0 改造启动前不再变更。
> 本文替代此前散落在沟通中的所有口头约定；与任何口头说法冲突时以本文为准。
>
> **本文件随仓库公开**，只含接口与语义定义，不含内部路径、账号信息或第三方项目内部实现。

## 通则（适用于全文，也适用于没写在这里的代码）

1. **任何"派生状态"都不许单独存储。**
   能用已有变量算出来的，就不要另存一份。两份真相一旦不同步，就会变成最难查的那类 bug——它不报错，只是行为偶尔不对。

   | 正确 | 错误 |
   |---|---|
   | `due` 由 `activeSinceBreak >= interval` 派生 | 另存一个 `bool due` 锁存位，同时维护计数器 |
   | 布局高度由内容实际尺寸算出 | 另存一个 `contentH` 变量跟着改 |

2. **两件事混在一个变量里，最后两边都做不好。**
   例：「这次休息还了没有」（`due` 清除时机）与「用户跳得太随意」（跳过配额）是两个关注点，分属核心与 `Enforcer` 插件，不共用一个字段。

3. **未验证的系统行为不得作为发布基线。**
   凡判定逻辑依赖未验证假设，一律标在 §14 的「未验证假设」清单里，验证通过前不发布。

## 0. 语言与构建约束（先看这个，它决定所有代码写法）

本项目用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 手工编译，
**只引用 4 个程序集**：`System.dll`、`System.Core.dll`、`System.Drawing.dll`、`System.Windows.Forms.dll`。
无 NuGet、无 Roslyn、无 MSBuild。

因此语言上限是 **C# 5**。

### 验收标准（唯一一条）

> **本文所有代码样例必须能被 `v4.0.30319\csc.exe` 原样编译通过。**
> 任何一条样例编不过，就是本文档的缺陷，不是实现的问题。

### 不可用语法清单

现有 `src/Program.cs` 中以下写法**零使用**，是刻意保持的：

| 不可用 | 起于 | 替代写法 |
|---|---|---|
| `$"..."` 字符串插值 | C# 6 | `string.Format` 或 `+` 拼接 |
| `nameof(x)` | C# 6 | 字符串字面量 |
| `?.` / `?[` 空条件运算符 | C# 6 | 显式判空 |
| `??=` 空合并赋值 | C# 8 | 先判空再赋值 |
| 表达式体成员 `int X => ...` | C# 6 | `{ get { return ...; } }` |
| 自动属性初始化 `int X { get; set; } = 5` | C# 6 | 构造函数内赋值 |
| `using static` | C# 6 | 完整类型名 |
| `out var` | C# 7 | 先声明变量再 `out` |
| 元组 `(a, b)` / `ValueTuple` | C# 7 | 自定义 `struct` 或两个变量 |
| 局部函数 | C# 7 | 私有方法 |
| `is T x` 模式 / `is not` | C# 7 / C# 9 | 先判类型再强转 |
| `default` 字面量、`throw` 表达式 | C# 7.1 / C# 7 | `default(T)`、独立 `throw` 语句 |
| `readonly struct` | C# 7.2 | 普通 `struct` + `readonly` 字段 |
| `switch` 表达式 | C# 8 | `switch` 语句 |
| 索引 / 范围 `[1..^1]`、`^1` | C# 8 | `Substring` / 显式下标 |
| `record` / `record struct` | C# 9 | 普通 `struct` 或 `sealed class` |
| 目标类型 `new()` | C# 9 | `new Foo()` |
| `init` 访问器 | C# 9 | `readonly` 字段 + 构造函数赋值（**唯一可用形态**） |

### 不可用 API

| 不可用 | 原因 | 替代 |
|---|---|---|
| `Environment.TickCount64` | .NET Core 3.0 / Standard 2.1 才有；**跨 4.8 小版本不可靠** | `Stopwatch.GetTimestamp()` |
| `SystemEvents.*` | 项目内禁用：原版工具的句柄泄漏病根是只 `+=` 不 `-=` | 见 §5 休眠补洞 |

### 可用

泛型、`var`、Lambda、LINQ（`System.Core`）、`async/await`（C# 5）、`[DllImport]`、`TimeSpan`、`Stopwatch`、`IDisposable`、`volatile`、`Interlocked`。

## 1. 接口定义

```csharp
public enum GateMode
{
    None   = 0,   // 未抑制
    Defer  = 1,   // 人在屏幕前，但不便打断 —— 计时继续，只是不弹
    Pause  = 2    // 人不在屏幕前 —— 计时冻结，恢复后不补算
}

public struct GateState
{
    public readonly bool     IsSuppressing;
    public readonly GateMode Mode;
    public readonly string   ReasonKey;             // 本地化 key；可为 null
    public readonly string   ReasonFallbackText;    // 本地化缺档时的插件兜底串；可为 null
    public readonly TimeSpan MaxReportedOverdue;    // 见 §3，只影响"逾期数字"，不影响是否提醒

    public GateState(bool isSuppressing, GateMode mode, string reasonKey,
                     string reasonFallbackText, TimeSpan maxReportedOverdue)
    {
        IsSuppressing      = isSuppressing;
        Mode               = mode;
        ReasonKey          = reasonKey;
        ReasonFallbackText = reasonFallbackText;
        MaxReportedOverdue = maxReportedOverdue;
    }

    public static readonly GateState Pass =
        new GateState(false, GateMode.None, null, null, TimeSpan.Zero);
}

public interface IRestGate
{
    string   Id             { get; }   // 稳定标识，用于设置持久化与日志
    string   DisplayNameKey { get; }   // 本地化 key
    int      Priority       { get; }   // 多 gate 同时抑制时，决定"文案报哪一个"
    TimeSpan PollInterval   { get; }   // 期望轮询间隔，核心 clamp 到 [250ms, 1h]
    bool     IsEnabled      { get; }   // false 时核心完全跳过轮询

    // 约定：非阻塞、无 UI、无分配热点、< 50ms、不得抛异常（抛了算违约，核心兜底）
    GateState Evaluate(DateTime utcNow);
}
```

**插件只需要诚实回答"这一秒在不在抑制 + 意图是什么"，不需要判断"这是会议还是打字"。**

## 2. 语义模型：Mode 由"人在不在"决定

| Mode | 判据 | 计时 | 账本 bucket |
|---|---|---|---|
| `Pause` | 人不在屏幕前 | 冻结，不补算 | `Idle` |
| `Defer` | 人在看、但不便打断 | 继续累加，只是不弹 | `Deferred` |

> **命名约定**：账本 bucket 叫 `Deferred` 而不是 `SuppressedPaused`。
> `SuppressedPaused` 是错的——`Defer` 下计时**根本没暂停**，名字会直接诱导实现者写"既然是 Paused 就冻结计时"，正好是我们要避免的 bug。
> `Defer → Deferred`、`Pause → Idle`，一一对应，不允许混用。

- `Pause` 只有两个合法来源：**空闲**（`GetLastInputInfo`）+ **睡眠**（墙钟差）。**不允许出现第三种。**
- 锁屏不需要单独事件：锁屏后必然无输入，由空闲检测覆盖（延迟 = 空闲阈值）。
- `Defer` 来源：会议、全屏/投屏/演示、勿扰、打字中、拖拽中。
- 判据是物理事实（人在不在），不是时长阈值。任何"门超过 X 分钟就变 Pause"的写法都是错的。

## 3. 计时公式

`Defer` **永不降级为 `Pause`**，只是在逾期量超限时"锁存"回到期状态，避免长门结束后欠账堆成山。

```csharp
// 1) 累加：只有 Pause 冻结
if (suppressMode != GateMode.Pause)
    activeSinceBreak += dt;

// 2) 放行条件：到期 且 无抑制
bool due       = activeSinceBreak >= interval;
bool mayPrompt = due && suppressMode == GateMode.None;

// 3) 逾期锁存：Defer 下逾期量达到上限，把计数锁存回"刚好到期"
if (suppressMode == GateMode.Defer
    && activeSinceBreak - interval >= maxReportedOverdue)
    activeSinceBreak = interval;
```

**边界必须用 `>=` 不能用 `>`**：若 `interval` 与 `maxReportedOverdue` 同为 15 分钟，用 `>` 会等到第 31 分钟才锁存，第一个周期多逾期 1 分钟。

### `maxReportedOverdue` 到底管什么

**它不影响是否提醒，也不影响提醒时机。** 门一开就一定提醒一次（前提是 `due`），这一点与它无关。

它只影响两件事：
1. 逾期数字能涨到多少（`>=` 锁存后，**显示值永远严格小于上限**）；
2. 锁存周期。

> 因此文案规则「逾期 ≥ 15 分钟时只显示"已延后"、不报数字」与本文**不冲突**：
> 按公式，逾期永远到不了上限，那个分支是不可达的防御性代码。保留它无害，但主路径永远显示具体数字。
> 字段名从 `MaxDeferBudget` 改为 `MaxReportedOverdue` 也是为此——原名会让人误以为"最多能拖多久然后强制弹"，而实际语义相反：**它不强制，只是不再累加报数**。

### 作用域：per-gate 声明 + 全局上限 clamp，多 gate 取 min

```
生效上限 = min( 该 gate 声明的 MaxReportedOverdue , 全局上限 )
```

- 全局上限默认 **15 分钟**（防插件写 2 小时）。
- per-gate 举例：打字 gate 60 秒，会议 / 全屏 gate 15 分钟。
- 多 gate 同时 `Defer` 时，取所有抑制者中**最小**的生效上限。

**取 min 的理由：不允许一个宽松的 gate 给另一个严格的 gate 续命。** 只要还有严格 gate 在抑制，就不该因为全屏 gate 声明了 15 分钟而让欠账一直涨。

### 行为推演（`interval` = 20 分钟，上限 = 15 分钟）

| 场景 | 结果 |
|---|---|
| 20 分钟短会 | 逾期未达上限，不锁存 → 会后立即提醒一次 20 秒微休息 |
| 90 分钟全屏演示 | 每 15 分钟锁存一次 → 演示结束后**恰好提醒一次**，文案「已逾期 <15 分钟」 |
| 打字 40 秒 | `Defer` 门自然放行，无锁存 |

门结束后**不会连弹多次**：锁存后 `activeSinceBreak == interval`，弹一次即进入休息态。这条由公式保证，不靠调参。

### 不变量：逾期计数 ≠ 到期状态

> **逾期计数清零，不改变「到期」状态。
> 「到期」只由"真正弹出一次休息"清除——注意是「弹出」，不是「完成」。**

这两件事必须分开存、分开想。混在一起是本节最容易写错的地方。

**为什么是「弹出」而不是「完成」**：用户跳过休息，也算这次提醒已经交付了。若要求"完成"才清除，跳过之后 `due` 仍为真，下一秒会再弹——无限骚扰。跳过的重复抑制是**跳过配额**（Enforcer 插件）的职责，不是靠 `due` 不清除来实现。

**反例（必须避免）**：若把"达到上限"理解成"债务消失、到期状态一并清掉"，则：

| 场景 | 错误实现 | 正确行为 |
|---|---|---|
| 16 分钟的会 | 不提醒 | **提醒一次** |
| 30 分钟的会 | 提醒（跨了两个周期） | **提醒一次** |

同样是超时开会，一个不弹一个弹——**相位依赖，用户无法预测，我们也无法解释**。"随机"比"少弹"更糟。

所以：上限到点只把**逾期计数**封顶清零，`due` 始终为 `true`，门一开必弹一次。

> **措辞纪律**：本文统一用"锁存（latch）"与"逾期计数封顶/清零"。
> **禁止**使用"抹平债务""丢弃欠账"这类说法——它们暗示"到期状态也被清掉"，会直接诱导出上表的错误实现。

### 实现形态：单变量，不要引入第二个 `due` 布尔位

锁存的准确动作是 **`activeSinceBreak = interval`**（回落"刚好到期"，`due` 由 `>= interval` 派生，仍为真）。

**不要**写成 `activeSinceBreak = 0` 再额外维护一个 `due` 布尔锁存位。两种写法行为等价，但后者是两个必须互相保持一致的状态变量——正是本契约反复强调要避免的"两处状态机"。一个变量能表达的事，不要用两个。

"重置周期"（`activeSinceBreak = 0`）只发生在**弹出提醒**那一刻，不发生在锁存时。

## 4. 时钟源

```csharp
// 单调锚点：算"活跃时长"
private long anchorTs;       // Stopwatch 时间戳
private long activeMsAccum;  // 已累计活跃毫秒

private static long NowMs()
{
    return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
}
```

- 用**静态** `Stopwatch.GetTimestamp()` 做锚点差，**不要用 `Stopwatch` 实例的 `Start/Stop`**（语义易错）。
- 单次 tick 内**只取一次** `NowMs()`。取两次会产生微小负差，钳到 0 会让秒数来回跳。
- QPC 在 S3 休眠期间**不前进**，醒来天然不补算——这是我们要的行为。
- WinForms `Timer` **只负责"重算 + 重绘"**，不负责计时。它迟到只是界面刷新晚，不能产生计时误差。
- 双精度中转有 ~0.1ms 级误差，对秒级倒计时无影响；本项目不需要 1ms 精度。

## 5. 四段账本

每秒恰好落入**一个** bucket：

```csharp
if (isOnBreak)                           seg = SessionSegment.Break;    // 必须排第一
else if (suppressMode == GateMode.Pause) seg = SessionSegment.Idle;
else if (suppressMode == GateMode.Defer) seg = SessionSegment.Deferred;
else                                     seg = SessionSegment.Active;
```

```csharp
public enum SessionSegment
{
    Active   = 0,
    Break    = 1,
    Deferred = 2,
    Idle     = 3
}
```

`Break` 排第一的理由：休息中途人走开了，那是"休息"不是"空闲"。现有实现的 `if (paused || (!isResting && IsIdleTooLong())) return;` 已经在做同一个判断（`isResting` 时不做空闲判断），本条只是把它落成显式 bucket。

### 统计口径

锚点是"**眼睛是否在屏幕上**"，不是"是否在专注工作"：

| 段 | 计入 `screen_time` | 理由 |
|---|---|---|
| `Active` | ✅ | 人在看 |
| `Deferred` | ✅ **要计入** | 人是看着屏幕的——会议、全屏演示、投屏都一样 |
| `Idle` | ❌ | 人不在 |
| `Break` | ❌ | 在休息 |

- `Deferred` 单独成段的目的是**统计"有多少时间因为不便打断而没提醒"**，不是为了从 `screen_time` 里扣掉它。
- 用眼暴露时长 = `Active` + `Deferred`。
- "专注度"是另一个指标，不混进这个账本。

存储：每天 4 个 `long`（秒），32 字节/天，一年 < 12KB。**不存明细事件流。**

### 休眠补洞（不需要电源消息）

**不用 `WM_POWERBROADCAST`，也不需要新建 `NativeWindow` 收电源消息。** 用**双时钟差**判定：

```csharp
long wallDelta = (long)(DateTime.UtcNow - lastTickUtc).TotalSeconds;
long qpcDelta  = (NowMs() - lastTickMs) / 1000;

if (wallDelta - qpcDelta > 60)   { /* 情形一：真休眠 → 补 Idle 段 wallDelta 秒 */ }
else if (qpcDelta > 60)          { /* 情形二：进程卡顿/挂起 → 补 Idle 段 qpcDelta 秒 */ }

lastTickUtc = DateTime.UtcNow;
lastTickMs  = NowMs();
```

为什么要两条，而不是单看墙钟差：

| 情形 | `wallDelta` | `qpcDelta` | 判据 | 结果 |
|---|---|---|---|---|
| 真休眠，且 QPC 跨睡眠**停走** | 8h | ≈0 | `wallDelta - qpcDelta > 60` | 补 Idle 8h ✓ |
| 真休眠，但 QPC 跨睡眠**继续走**（部分硬件 / invariant TSC） | 8h | 8h | 差值≈0 → 落到第二条 `qpcDelta > 60` | 补 Idle 8h ✓ |
| 正常 tick（1s） | 1s | 1s | 都不命中 | 不补 ✓ |
| 进程被系统挂起 90s | 90s | 90s | 落在第二条 | 补 Idle 90s ✓ |

**两条规则的真实价值，是不需要事先验证"QPC 是否跨 S3 停走"这个未验证的系统行为**——两种硬件行为都能正确覆盖。按"未验证的判定逻辑不发布"的项目规则，这一点比省几行代码重要得多。

### 已知残留误判（接受，并兜底）

**手工把系统时间往前调（或大幅 NTP 校时）与真休眠，在时钟读数上不可区分。** 单看墙钟差会误判；只看 QPC 差则完全看不到。这是纯时钟方案的固有边界，不是实现缺陷。

兜底措施：
1. 单次补记的 `Idle` 段**封顶 24 小时**，超出按 24 小时记并记一条日志。
2. `wallDelta <= 0`（时间被往回调）直接忽略，不补负值。
3. 记事日志里记录原始 `wallDelta` / `qpcDelta`，便于事后排查。

**补记的时段一律归 `Idle`（`Paused` 语义）**，绝不能把合盖 8 小时当成活跃时间补进账本，否则用户第二天会看到"连续用眼 8 小时"。

本项目内部禁止 `SystemEvents.*`，此方案不碰它。

### `IdleGate` 被关掉时的账本退化

设置里允许关闭空闲检测（远程桌面等场景键鼠输入不真实）。此时：

- `Pause` 永不发生 → `Idle` 段恒为 0，账本退化为三段（`Active` / `Break` / `Deferred`）。
- `screen_time` 会偏高，这是用户选择的结果，不是 bug。
- **设置页那个开关旁边就要有说明**，不能等用户一周后在统计页发现数据虚高。
  文案：`关闭后不再区分"你在不在电脑前"，用眼时长统计会偏高。`

## 6. 调度器职责（`RestGateScheduler`）

1. **只有一个定时器**。`PollInterval` 是 per-gate 属性，但用一张 `nextDueUtc` 表调度；gate 移除 = due 条目消失。**插件从不拥有自己的定时器**，所以"卸载后轮询还在跑"结构上不可能。
2. 全部 gate `IsEnabled == false` 时，定时器 `Change(Infinite, Infinite)` 停掉，不空转。
3. **后台线程求值 + 快照缓存**。计时循环只读快照，绝不直接调插件。
4. **熔断**：单次 `Evaluate` 超 50ms 或抛异常 → 该 gate 视为"未抑制"（fail-open）+ 禁用 5 分钟 + 只记一条日志。
5. **`PollInterval` clamp** 到 `[250ms, 1h]`，超界时日志写明「请求 X，已 clamp 至 Y」。上限必要：插件写 `Timeout.Infinite` 会让抑制状态永久卡死。
6. **抑制状态变化时立刻刷新托盘**。这是调度器职责，不下放给插件——否则一定有人漏。

`IdleGate` 的 `PollInterval` 取 **1000ms**：它直接决定账本的秒级归属精度，5 秒轮询会让锁屏后的前 5 秒记进 `Active`。

### 快照对象（`CurrentState`）—— 线程安全的唯一约定

定时器回调跑在**线程池线程**上，UI 线程每秒读快照。**禁止用 `lock`**（每秒抢锁会拖累 UI）。约定：

- 快照是**不可变对象**，字段全 `readonly`。
- 交换用 `volatile` 引用赋值 / `Interlocked.Exchange`，整体替换，永不原地修改。
- C# 5 没有 `record`，用 `sealed class` 或 `struct` + 整体引用替换。

快照必须包含以下字段，否则计时侧只能自己维护门状态，变成两处状态机必然不同步：

| 字段 | 用途 |
|---|---|
| `EffectiveMode` | 聚合后的最终 Mode（已按 Pause > Defer > None 裁过） |
| `EffectiveMaxReportedOverdue` | 聚合后的最小上限（已 clamp） |
| `ReasonKey` / `ReasonFallbackText` | 取自 Priority 最高的抑制者 |
| `GateActiveSinceUtc` | 当前抑制态**从何时开始**（gate false→true 的时刻） |
| `SuppressingGateId` | 哪个 gate 在抑制（仅日志/诊断，不进 UI） |

`deferredSince` / "门已持续多久" 一律由调度器维护并放进快照——**计时侧只读结果，不存状态**。

### 聚合顺序（Mode 优先于 Priority）

```
第一步：任一 Pause → 整体 Pause（冻结计时，账本 Idle）
第二步：否则任一 Defer → 计时继续、不弹；UI 原因取 Defer 抑制者中 Priority 最高者
第三步：否则 → 放行
```

Mode 决定"计不计时"，Priority 只决定"文案报哪一个"，两级不交叉。
反例（必须避免）：打字 gate（Defer，Priority 高）压过会议 gate（Pause，Priority 低）→ UI 说"打字中"，实际在暂停计时，用户看不懂。

### 迟滞（hysteresis）归属：进入快、退出慢，是目前的设计

抑制态必须防抖，否则数据源抖动会让托盘图标和计时反复横跳。参数分两层：

| 参数 | 归属 | 默认 | 理由 |
|---|---|---|---|
| **最小抑制持续** | **核心**（调度器） | 2s | 跨 gate 的统一不变式：短于 2s 的抑制视为抖动，直接忽略。不能交给插件各自判断 |
| **释放滞后 `ReleaseDebounce`** | **SDK 基类 `DebouncedGateBase`** | 60s | 每个数据源的抖动特性不同（摄像头时间戳、注册表写盘延迟、进程退出），只有实现者知道该等多久。核心不替它决定，但**要求**它必须防抖 |

**产生的可见行为**：进（≈ 数据新鲜度 + 2s）、出（≈ 60s）。**散会后约 60 秒才恢复提醒，是设计不是缺陷。**

这条必须写进验收标准，否则 QA 会报"散会了还不弹"。同时提醒实现者：`ReleaseDebounce` 是**默认值**，继承类可覆盖，但不得设为 0——设 0 等于把防抖责任推回核心，而核心只知道 2s 这一层。

## 7. `ReasonKey` 渲染规则

`ReasonKey` 是 `string`（命名空间前缀约定，如 `Gate.Meeting.Camera`），**核心不内置插件 key 枚举**，避免核心反向依赖插件。

渲染四档，**任何一档都不允许隐藏菜单项**：

| 情况 | 行为 |
|---|---|
| key 能解析 | 显示插件本地化文案 |
| key 非空但本地化缺档 | 退 `ReasonFallbackText` → 再退核心兜底「因外部条件延后」，记日志 |
| key 为 null / 空 | 同上兜底，记日志 |
| **插件已卸载但抑制态未清** | **不是文案问题，是 bug**：记错误日志 + 下次聚合强制清态。**绝不能用兜底文案糊过去** |

最后一行单独列出的理由：用「因外部条件延后」掩盖状态残留，会变成"用户看到一句莫名其妙的话，我们查不出原因"，比不显示更糟。

隐藏菜单项 = 用户看到托盘进了抑制态却看不到原因，比显示笼统文案更糟。

## 8. Lifecycle / 卸载

1. **卸载是同步操作**，不是 fire-and-forget：移除 gate → 清 `deferredSince` → **立即重跑一次聚合** → 返回。返回后计时器必处于明确定义状态，不存在"暂停中"僵尸态。
2. **休息中途被卸载**：当前休息照常结束，不打断用户；该 gate 从下一轮起不再参与。
3. `IRestGate` 继承 `IDisposable`，但**只用于释放插件自持资源**（如注册表句柄缓存），不用于停定时器——定时器不属于插件。

## 9. gate 只拦"开始"，不碰"结束"

gate 只在 `WaitingForBreak → BreakDue` 这一个跃迁点求值。

- 休息已开始后，gate 状态变化**不得**撤掉覆盖层或延长休息。
- 门关闭后也**不得**重置正在进行的休息。

违反此条会产生「开会时休息界面卡在屏幕上 40 分钟」的死状态。

## 10. 内建 gate、插件边界与打包形态

### 10.1 两个 gate 进核心代码

**决策：以下两个进核心代码，不在插件体系内，但设置里独立可开关。**

| gate | 进核心的理由 |
|---|---|
| `IdleGate`（`GetLastInputInfo`） | 不是"抑制器"，是**计时语义的前提**——`screen_time` 的定义依赖它。可卸载则 `screen_time` 未定义 |
| `FullscreenDndGate`（`SHQueryUserNotificationState`） | 不是增值功能，是**防止产品变坏的护栏**。缺失版不是"功能少的版本"，是"坏掉的版本" |

**可配置 ≠ 可卸载**：两个独立开关必须有（有人就想让提醒在投屏时也弹；有人用远程桌面键鼠输入不真实），但代码始终在包里。

### 10.2 插件仅限自研，不做第三方生态

**决策：插件 = 我们自己发布的模块。不开放第三方。**

理由：
1. `.NET Framework` 同一 `AppDomain` 内加载的代码拥有完全信任权限（读写文件、联网、装钩子）。**没有沙箱就没有第三方生态。**
2. 自动门禁已取消（Cecil 不进发行版）。"允许第三方"+ "无门禁"= 裸奔，两件事必须一致。
3. 产品规模不匹配：两千行、单文件、零依赖的托盘工具，没有维护插件市场的能力。

因此准入门槛只有 2 条：**C# 5 可编译**、**接口只读**。

### 10.3 打包形态：单文件指核心 exe

**决策：核心 `EyeLite.exe` 保持单文件零依赖；插件是 `plugins\` 下的独立 DLL，用 `Assembly.LoadFrom` 加载。**

必须写进本文档、也必须写进 README 的一句话：

> **禁用插件后需要重启才真正释放内存。**
> `.NET Framework` 无法卸载已加载的程序集——`Assembly.LoadFrom` 进来的 DLL 会驻留到进程退出。
> 所以"禁用"的准确语义是**不再启动、不再占用运行时资源**，真正回收内存要重启一次。

这是 .NET Framework 的硬限制，不是实现偷懒。说清楚比让用户以为"点一下开关就省 200MB"、然后发现没变要好。

配套：
- `build-plugins.cmd`，仍然双击就能编。
- 插件加载失败一律 fail-open（熔断已覆盖）。
- `plugins\` 目录缺失 = 全部按默认配置运行，核心不受影响。

**构建链的一个具体坑（会卡住第一天）**：插件 DLL 要在编译期引用 `IRestGate`，而它住在 `EyeLite.exe` 里。可行做法是 `/reference:EyeLite.exe`——`csc` 支持引用 exe 并取其类型。要求插件与随包发布的 exe **同版本编译**，不要跨版本混用。

### 10.4 已知平台限制（不是 bug）

> **不保证可见**：独占全屏 D3D（部分游戏）会覆盖任何置顶窗口。此时提醒**仍在计时、仍会结束**，只是肉眼看不到。不做特殊处理，**也不为此引入 DirectX 覆盖层**。

最后一句话重要：防止后来有人为了"游戏里也能看见"去引 DX hook，那是另一个复杂度台阶。投屏 / 演示场景不受影响（那是窗口化全屏，可以盖住）。

## 11. 只读约束

插件仅自研，因此不再需要自动化门禁。

| 层 | 机制 | 状态 |
|---|---|---|
| 编译期 | `IRestGate` 只有 `Evaluate`，无任何写 / 命令入口 | 保留 |
| 自动扫描 | Cecil 扫 `Registry.SetValue` 等 | **不做**。与单文件零依赖冲突，且挡不住反射 / `DynamicInvoke`，会给人虚假的安全感 |
| 代码评审 | 下述开发红线 | 保留 |

### 开发红线（对自己同样有效）

1. **绝不写** 系统隐私能力使用记录（`ConsentStore` 一类）。写它等于伪造系统隐私状态。
2. 进程名 / 包名**只在内存里判定**，不落盘、不上报、UI 不显示具体应用名。
3. 插件不得携带 native 依赖 DLL——静态检查本就不存在，藏进去更没人看得见。

## 12. 已确认的系统行为（实测）

系统隐私能力使用时间戳需扫两处，只扫非打包分支会漏掉全部 UWP 应用：

- **打包应用**（商店版会议 / 相机 / 游戏栏等）→ 时间戳直接挂在 `<能力>\<PackageFamilyName>` 父层。
- **非打包应用** → `<能力>\NonPackaged\<转义路径>`，其中路径的 `\` 被替换为 `#`，**盘符冒号保留**（形如 `C:#Apps#App#App.exe`）。

## 13. 落地层级与分批

**账本属于核心，不属于插件层。** 理由与 §10.1 同源——`IdleGate` 在核心里定义 `screen_time`，账本不能反过来依赖插件。

但不能一次做完，按当前仓库实际状态（单文件、无 gate、无 session 结构）分两批：

| 批次 | 内容 | 依赖 | 回归面 |
|---|---|---|---|
| **A（可立即做）** | 时钟源改为 `Stopwatch` 锚点 | 无 | 小，约 30 行；正常负载下行为与现在一致，只在 tick 迟到时更准 |
| **B（gate 落地后）** | 四段账本 + 快照读取 + 休眠补洞 | `IRestGate` / `RestGateScheduler` 存在 | 中 |

批次 B 之前，`Deferred` 段没有定义来源（没有 gate 就没有抑制），强行先落地只会得到"永远为 0 的计数器 + 语义未定的字段"。

**已撤回的字段**：`SessionStartedAtUtc`。它是投机性预留——"本轮工作开始"还是"本轮休息开始"、跨休息怎么算，都没有真实需求能定。等统计插件有真实需求再加。

## 14. 未验证假设（谁拥有谁维护）

通则 3 的执行清单。**每一项在验证通过前都不得作为发布基线。**

归口原则：**谁拥有这块代码，谁维护这一行**。核心负责的假设不再借放在插件的 PRD 里——插件的 PRD 只留一行交叉引用，否则核心发版时没人会回去翻那份文档。

| ID | 假设 | 等级 | 归口 | 验证方法 | 不成立时的影响 |
|---|---|---|---|---|---|
| **U1** | 专注助手（勿扰）确实映射为 `QUNS_QUIET_TIME`，且各 Windows 版本一致 | **未验证** | 核心 `FullscreenDndGate` | 真机：开启专注助手 → 调 `SHQueryUserNotificationState` → 观察返回值 | 专注助手期间仍会弹提醒；判据需改成别的信号 |
| **U2** | `ConsentStore` 写入延迟在秒级，60s 释放滞后足够覆盖抖动 | 部分验证（格式已验证，**延迟未测**） | `MeetingGate` 插件 | 记录写入延迟分布，取 P99 × 4 作为下限 | 抑制进入/退出判断偏移，需调整 `ReleaseDebounce` |
| **U3** | 空闲阈值能代表"人在不在"，足以覆盖锁屏与睡眠 | 未验证（阈值合理性） | 核心 `IdleGate` | 真机：锁屏 N 分钟、合盖 N 分钟，核对账本归属 | 账本 `Active` / `Idle` 归属偏差，`screen_time` 失真 |
| **U4** | `GetLastInputInfo` 在锁屏 / Windows Hello 解锁期间不返回虚假输入 | 未验证 | 核心 `IdleGate` | 与 MeetingSense PRD 的 D5 合并执行 | 锁屏被误记为 `Active`；Hello 摄像头可能被误判为会议占用 |

### 已被设计消除、无需验证的假设（记录在此，避免重复纠结）

| 假设 | 原风险 | 如何被消除 |
|---|---|---|
| QPC（`Stopwatch`）跨 S3 睡眠是否停走 | 若不停走，单看墙钟差会漏判休眠 | §5 改为**双时钟差两条规则**：两种硬件行为都能落到 `Idle`，不再依赖该假设 |

这一行是本节存在的意义：**能通过改设计消除的假设，比"验证一下"更值得做**——消除之后就不会再有人回来问它。

## 15. 决策记录（原未决项，均已拍板）

| 项 | 结论 |
|---|---|
| "插件"是否允许第三方 | **不允许**，仅自研（§10.2） |
| 插件加载方式 | `plugins\` 下独立 DLL + `Assembly.LoadFrom`；核心 exe 仍单文件（§10.3） |
| 两个内建 gate 归属 | 进核心代码，可开关不可卸载（§10.1） |
| `maxReportedOverdue` 作用域 | per-gate + 全局上限 clamp，多 gate 取 min（§3） |
| 自动门禁 | 不做（§11） |
| 休眠检测 | 双时钟差两条规则；**不验证 QPC 跨 S3 行为**（§5） |
| 迟滞参数归属 | 最小持续归核心，释放滞后归 SDK 基类（§6） |
| 逾期计数与到期状态 | 两个独立概念，计数清零不动 `due`（§3） |

**当前无未决项。** 接口如需变更，必须同步更新本节与 §16。

## 16. 变更记录

| 版本 | 变更 | 起因 |
|---|---|---|
| v1 | 首版：`IRestGate`；Defer / Pause 按时长划分 | — |
| v2 | Mode 判据改为"**人在不在**"；全屏 / 会议从 Pause 改 Defer；`Defer` 不再降级，改周期性锁存 | 产品侧指出"会议是屏幕时间，Pause 会漏算用眼暴露" |
| v2 | `record struct` → 普通 `struct`；补 C# 5 禁用特性 | 实测 `build.cmd` 用 `csc` v4.0.30319，语言上限 C# 5 |
| v2 | `Environment.TickCount64` → `Stopwatch.GetTimestamp()` | 跨 4.8 小版本不可靠 |
| v3 | 账本 bucket `SuppressedPaused` → `Deferred` | 原名字暗示"暂停"，与实际语义相反 |
| v3 | 补 `ReasonFallbackText`；渲染分四档；"卸载未清态"归为 bug 不归为文案 | 产品侧：兜底文案会掩盖状态残留 |
| v3 | 补 `maxReportedOverdue` 作用域与多 gate 取 min | 产品侧三次追问 |
| v3 | 休眠补洞从 `WM_POWERBROADCAST` 改 tick 间墙钟差，补齐一律归 `Idle` | 避免新建 `NativeWindow`；`SystemEvents.*` 项目内禁用 |
| v3 | 补快照字段与线程安全约定（禁 `lock`，引用整体替换） | 工程侧：定时器回调在线程池 |
| v3 | 补落地分批；撤回 `SessionStartedAtUtc` | 工程侧：当前仓库无 gate / session，语义未定 |
| v4 | §0 升级为显眼语法清单 + "唯一验收标准" | 团队负责人补充 8 项 C# 6–9 禁用语法 |
| v4 | `MaxDeferBudget` → `MaxReportedOverdue`，并写清"不影响是否提醒" | 产品侧：原名会让人以为是"最多拖多久然后强制弹" |
| v4 | 锁存边界 `>` → `>=` | 工程侧：`interval` 与上限相等时第一周期多逾期 1 分钟 |
| v4 | 新增 §10.2 / §10.3 插件边界与打包形态，含"禁用需重启才释放内存" | 团队负责人拍板仅自研 + 插件独立 DLL |
| v4 | §15 决策记录：所有未决项闭环 | — |
| v5 | 新增不变量「逾期计数 ≠ 到期状态」+ 相位反例 + 措辞纪律（禁用"抹平/丢弃债务"） | 产品侧：两种读法结果相反（16 分钟会议弹 / 不弹） |
| v5 | 休眠判据改为**双时钟差两条规则**；补"手工改时间不可区分"的残留误判与兜底 | 团队负责人：单看墙钟差会把校时误判为休眠；双规则同时消除了"QPC 是否跨 S3 停走"这个未验证假设 |
| v5 | 新增迟滞归属表：最小持续（核心）vs 释放滞后（SDK 基类） | 产品侧：确认"散会后 60 秒才恢复"是设计，且需归位到某层 |
| v6 | 不变量澄清：「到期」由**弹出**清除，不是「弹出并完成」；补"跳过也算已交付"的理由 | PRD §4 与本文冲突；按"完成"清除会导致跳过之后下一秒再弹 |
| v6 | 新增「实现形态：单变量」——锁存是 `= interval` 而非 `= 0`，不引入第二个 `due` 布尔位 | PRD §4 措辞"锁存回 0 并重新计时"会被读成清零，与本节公式冲突 |
