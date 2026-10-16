# 02 · 稳定上下文前缀拼接（请求头历史字节不变，提 prompt cache 命中率）

> 目标：让上游 `prompt cache`（`session-hash` 头 + 前缀 KV 复用）真正命中。`znaide` 靠“全量直发 + 只在 /compact 动前缀”天然稳定；本项目有自动裁剪，必须把裁剪做成“块状、低频、字节稳定”。
> 核心约束：**同会话相邻两轮若未触发裁剪/压缩，`SerializeRequest` 输出的 `system+历史头` 字节必须完全一致。**

## 1. 现状与命中率杀手（按伤害排序）

| # | 杀手 | 位置 | 说明 |
|---|---|---|---|
| K1 | 每轮都可能微调裁剪边界 | `TokenBudgetService.BuildWithinBudget:197-281` | 逐组倒序填充预算，来多少历史剩多少预算就留多少组，边界每轮抖 1 组即前缀全失效 |
| K2 | `system` 含每日易变行 | `SystemPromptBuilder.BuildAsync:46-81` | `Current Date: yyyy-MM-dd` 与 `Python 3.11.9(path)` 变动即首字节就不同，`cache` 从 0 开始 |
| K3 | 工具定义顺序未冻结 | `AgentOrchestrator:211-224` + `ToolRegistry` | `GetDefinitions` 若按注册顺序且未来增删工具，前缀后段抖动；`Responses/Chat` 两协议工具形状不同但同名必须同序 |
| K4 | 无前缀可观测性 | `LlmGatewayClient.LogResponseTrace:267-294` | 只记 `in/cached/hitRate`，无法定位“哪一轮开始 miss、前缀哪段变了” |

## 2. 设计：稳定前缀装配线

### 2.1 装配顺序冻结（双协议统一，序列化器只许调序不许改形）

```text
[0] system.stable   ← OS/Arch/工作区/工具铁律（永不含日期/版本号，见 §2.2）
[1] anchorGroups    ← 首个含 user 的原子组及之前 system 组（BuildWithinBudget 的 firstUserGroupIndex 语义保留）
[2] summaryBlock    ← /compact 摘要（如有，仅一条 user 角色，格式见 03 篇；无则缺席，不占位）
[3] retainedGroups  ← 按原时间升序 emit（先旧后新），候选按“新→旧”填充
[4] latestGroup     ← 最新一组，永不裁
[5] system.ephemeral← 日期/Python 状态/会话 hash 说明（仅尾部，见 §2.2）
[6] tools           ← 按 name 升序稳定排序后的定义（内容不动）
```

* `ChatCompletionsAdapter.SerializeRequest:62-119` 与 `ResponsesApiAdapter:62-131`：`messages` 循环不改形状，仅保证调用方传入的 `requestMessages` 已按上序排好；`tools` 循环前加 `OrderBy(t=>t.Name, Ordinal)`（适配器内排，避免改 `ToolRegistry` 返回语义）。
* `tool` 结果 `content` 过长截断（`ToolOutputGuard`）阈值不动，避免改阈值导致历史字节变化；本篇不调任何截断长度。

### 2.2 `SystemPromptBuilder` 拆稳定/易变两段（不改模板措辞，只搬行）

```csharp
// BuildAsync 返回改为两段拼接，但对调用方仍是一个 string（零破坏）：
// stable = §1 Runtime（删 Current Date 行）+ §2 + §3 全文
// ephemeral = "\n## 4. Session Ephemeral (不参与前缀命中，仅供模型参考)\n- Current Date: {today}\n- Detected Python Runtime: {pythonStatus}\n- SessionHash: 由网关头携带，此处不重复\n"
```

效果：首字节稳定的 `stable` 越长，`prompt cache` 命中段越长；日期每天变只影响尾部。`GetPythonStatus()` 逻辑不动。

### 2.3 块状高低水位淘汰（替代逐轮微调，`TokenBudgetService` 内新增）

```csharp
public const double HighWaterMark = 0.92; // 占用/maxContext ≥92% 才触发裁剪
public const double LowWaterMark = 0.70;  // 一次裁到 70% 低水位
// TruncateToBudget 改为：
public List<UnifiedChatMessage> TruncateToBudget(ModelConfig m, IReadOnlyList<UnifiedChatMessage> msgs, int safety=1024)
{
    int maxCtx = m.MaxContextTokens > 0 ? m.MaxContextTokens : 128000;
    int used = ResolveUsedInputTokens(msgs); // 精确快照优先（01 篇后可用 ResolveOccupancy().prompt ?? 估算）
    double ratio = maxCtx > 0 ? (double)used / maxCtx : 0;
    if (ratio < HighWaterMark) return msgs.ToList(); // 未达线：原样返回，前缀零抖动
    int maxOut = m.MaxOutputTokens > 0 ? m.MaxOutputTokens : 4096;
    int target = (int)(maxCtx * LowWaterMark);
    int budget = Math.Max(1000, target - maxOut - safety);
    return BuildWithinBudget(msgs, budget); // 达线才进现有原子组裁剪
}
```

* `BuildWithinBudget(messages,maxBudget)` 本体逻辑不动（锚点+notice+最新组语义保留，`AC03` 用例继续通过）。
* `AgentOrchestrator:187` 的 `TruncateToBudget(model,history)` 调用不动，行为自动变为“平时全量、超线块裁”，与 `znaide` “平时不动、手动 compact”在效果上对齐（本项目多一步自动兜底）。
* `notice` 文案 `TruncationNotice` 不动，避免摘要块与裁剪块文案混淆。

### 2.4 可观测：前缀哈希进 `llm-trace`

`LlmGatewayClient.ExecuteStreamPipelineAsync:119` 在 `SerializeRequest` 后加：

```csharp
string prefixHash = HeaderVariableResolver.ComputeDeterministicHash16(requestPayload); // 整包哈希，稳定即命中前提
// LogRequestTrace 追加 prefixHash + msgCount + inputEst(EstimateMessageTokens 求和，仅参考)
```

`[RES]` 行已有 `hitRate`，`[REQ]` 行加 `prefixHash` 后，联调时 `grep reqId` 即可看“哈希变没变 vs 命中率掉没掉”的因果。

## 3. 后端改造步骤

1. `Services/SystemPromptBuilder.cs:57-81`：按 §2.2 把 `Current Date/Python` 两行移入 `## 4. Session Ephemeral` 尾段；其余字符串一字不改。单测断言 `prompt.StartsWith("You are Halo-Agent")` 与 `Contains("cmd.exe")` 保持通过。
2. `Services/TokenBudgetService.cs`：加 `HighWaterMark/LowWaterMark` 常量 + 按 §2.3 重写 `TruncateToBudget`（约 12 行）；`BuildWithinBudget/GroupIntoAtomicTurns` 不动。
3. `LlmGateway/Adapters/*Adapter.cs`：`tools` 序列化前 `OrderBy(Name)`；`messages` 循环不动。`SerializeRequest` 输出字节顺序即 §2.1。
4. `LlmGateway/LlmGatewayClient.cs:119,251-265`：加 `prefixHash` 计算与 `[REQ]` 日志字段；`[RES]` 不动。
5. `Services/AgentOrchestrator.cs:187-193`：`requestMessages = [system] + budgeted` 不动，但注释写明传入已是稳定前缀；`toolDefs` 取后按 `Name` 排序（与适配器内排序双保险，取一处即可，推荐只在适配器排、此处加注释）。
6. 跑 `dotnet build` + 单测（§5）全绿。

## 4. 前端改造步骤（只显示，不参与拼接）

1. `WorkbenchTopBar` 悬停卡（01 篇已加两行）在本篇追加一行：`前缀GORITHM：稳定前缀 · 高92%/低70%块淘汰` 纯文案 + `sessionHash` 复制按钮保留（已存在 `copySessionHash`，不动）。
2. `MessageTimeline` 持久 footer 的 `协议:` 显示保持 `budgetStatus.protocol ?? telemetry.protocol`（04-05 已定），本篇不改；新增 `title` 悬停：`IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ`（一句话文案，避免用户把轮内多 call 误判为 bug）。
3. `hostBridge mock`：`getContextBudget` 返回的 `used` 估算逻辑不动；`startAgentTurn` mock 的 `payload.inputTokens` 保持单调缓增（已是 `120`，建议改为按调用次数 `120+轮次*8`，见 04 篇联调脚本，未改前不阻塞）。
4. `vue-tsc -b && vite build`。

## 5. 后端单测（并入 `Stage05TokenUsageTests.cs` 或新建 `Stage05StablePrefixTests.cs`，至少 4 个）

* `Truncate_Below_HighWater_Returns_Identical_Reference`：`used/maxCtx=50%` → 返回与输入 `SequenceEqual`（引用可不同，内容逐条同一对象）。
* `Truncate_Above_HighWater_Cuts_To_LowWater_Atomically`：构造 20 组 tool 链，`used=95%` → 结果含锚点+notice+最新组，且 tool 组无孤儿（复用 AC03 的 `(d)` 断言）。
* `SystemPrompt_Stable_Head_Excludes_Date`：`BuildAsync` 两次（昨天/今天日期覆写）→ 去掉 `## 4.` 尾段后两串完全相等。
* `Tools_Serialized_In_Name_Order`：乱序 `ToolDef` → `Chat/Responses` 两适配器输出中 `name` 出现顺序均为升序。

## 6. 验收

- [ ] 未达 92% 的 3 轮请求 `prefixHash`（`llm-trace [REQ]`）完全相同且 `hitRate` 逐轮上升或持平。
- [ ] 达 92% 的那轮一次裁到 ~70%，`notice` 出现且 tool 组无孤儿；下一轮哈希变化但再下轮稳定（块状，无逐轮抖）。
- [ ] `system` 日期跨天只变尾部哈希段（整包哈希会变，但上游前缀命中段不受影响——以后端 `stable` 长度注释为准，不做字节级断言）。
- [ ] 新增 4 用例全绿，`AC03` 原用例不改数据通过。
