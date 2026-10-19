# 01 · Token 用量与上下文用量的正确计算和显示（账单 vs 占用分离 + 未知态 + ≈估算）

> 目标：终结“总量/增量/估算”三混用。学 `znaide` 的 `TokenStats{input,output累计 / last_prompt占用 / round_est估算}`（`crates/tui/src/app.rs:1870-1948`），但保留本项目 `prompt cache` 能力。
> 口径（全仓统一，后文不再解释）：`IN=占用快照=末轮prompt`；`OUT=本轮completion`；`Σ=计费累计`；`cache⊆IN`；无精确值=`unknown`。

## 1. 现状与问题（need04-05 之后仍缺的三块）

| 缺口 | 现状 | 后果 |
|---|---|---|
| 无计费累计字段 | `SessionMetadata.TotalInputTokens` 被 04-05 改为快照覆盖，`Σ` 无处可查 | 用户无法对账“我到底花了多少”，`znaide` 的 `累计 in/out/Σ` 无对应 |
| 无未知态 | 无快照时 `ResolveUsedInputTokens` 回退 `Sum(估算)` 并当精确值显示百分比 | `resume` 老会话/压缩后明明未知却画 `38%`，`znaide` 画 `ctx ?` 更诚实 |
| 无流式估算契约 | 流式中 `telemetry` 为 0，到 `UsageUpdated` 才跳变；估算只在裁剪内部用 | 状态栏跳变大，`znaide` 用 `round_est + ≈` 过渡 |

## 2. 设计（只新增可选字段，不改旧字段名）

### 2.1 后端 DTO（全部 `?/Nullable + 默认值`，旧客户端忽略即兼容）

`src/Host.Desktop/Core/Models/WorkspaceModels.cs`：

```csharp
// SessionMetadata 新增（全部可选，旧 meta.json 缺键即 0/null）：
public long CumulativePromptTokens { get; set; }      // Σ账单：每次 usage.prompt 累加，compact/clear 外永不减少
public long CumulativeCompletionTokens { get; set; }  // Σ账单：每次 usage.completion 累加
// TotalInputTokens 语义冻结为“占用快照”（04-05 已是覆盖语义，本篇只补注释，不改行为）
// TotalCachedTokens 语义冻结为“占用快照的子集”，TotalOutputTokens 冻结为“Σ”（保持 +=）

// ContextBudgetStatusDto 新增：
public int? LastPromptTokens { get; set; }            // null = unknown（无快照），非 null = 精确占用
public int? LastCachedTokens { get; set; }           // null = unknown；非 null 时恒 ≤ LastPromptTokens
public double? CacheHitRate { get; set; }             // = cached/input，input<=0 或任一 null 则 null
public bool UsedInputIsEstimated { get; set; }        // true = UsedInputTokens 来自估算回退
public string OccupancyState { get; set; } = "known"; // "known" | "unknown" | "estimated"
public long BilledPromptTokens { get; set; }          // 拷贝 meta.CumulativePromptTokens
public long BilledCompletionTokens { get; set; }
```

`src/Host.Desktop/LlmGateway/Models/UnifiedLlmModels.cs`：`UnifiedTokenUsage.CacheHitRate` 保持 `double` 计算不变（`Input<=0→0.0`），仅在 DTO 装配时按“任一 null 则 null”映射，避免 `0%` 伪装未知。

### 2.2 后端计算函数（`TokenBudgetService.cs`，在 04-05 两个函数旁新增，不改其签名）

```csharp
// 新增：与 ResolveUsedInputTokens 并列，返回是否估算 + 可空快照
public static (int? prompt, int? cached, bool isEstimated) ResolveOccupancy(IReadOnlyList<UnifiedChatMessage>? messages)
{
    int? p = null; int? c = null;
    if (messages != null) for (int i = messages.Count - 1; i >= 0; i--)
    {
        var m = messages[i]; if (m == null) continue;
        if (p == null && m.InputTokens.HasValue && m.InputTokens.Value > 0) p = (int)m.InputTokens.Value;
        if (c == null && m.CachedTokens.HasValue && m.CachedTokens.Value > 0) c = (int)m.CachedTokens.Value;
        if (p != null) break; // 以末个 prompt 快照为准；cached 取该轮或更近轮的最大可见值后钳制
    }
    if (p == null) return (null, null, true);
    if (c.HasValue) c = Math.Min(c.Value, Math.Max(0, p.Value));
    return (p, c, false);
}
```

`CalculateBudgetStatus` 改为（旧字段赋值保留，新字段追加，共约 15 行）：

```csharp
var (prompt, cached, isEst) = ResolveOccupancy(messages);
int usedInput = prompt ?? messages.Sum(m => EstimateMessageTokens(m)); // 兼容旧字段
int availableInput = Math.Max(0, maxContext - maxOutput - safetyBuffer - usedInput);
double percentage = maxContext > 0 ? Math.Min(100.0, Math.Round((double)usedInput / maxContext * 100.0, 1)) : 0.0;
// 新增字段：
LastPromptTokens = prompt; LastCachedTokens = cached;
CacheHitRate = (prompt.HasValue && cached.HasValue && prompt.Value > 0) ? (double)cached.Value / prompt.Value : (double?)null;
UsedInputIsEstimated = isEst;
OccupancyState = prompt.HasValue ? "known" : (messages.Count == 0 ? "known" : "unknown");
// 空会话 messages.Count==0 视为 known+0%，与 znaide 默认 Some(0) 对齐；有消息无快照才 unknown
// Billed* 由 GetBudgetStatusAsync 读 meta 后填入（见 §3 文件 3）
```

`percentage/available` 公式不动（04-05 已对），`isNearLimit>=85` 不动。

### 2.3 `AgentOrchestrator.cs:399-443`：区分“占用发布”与“账单累计”

* `assistantMessage.Input/Output/CachedTokens = usage.*` 不变（占用快照落盘）。
* `AssistantMessagePersisted/TurnCompleted` 的 `payload` 新增键（旧键保留）：`billedPrompt/billedCompletion`（本轮内多次 LLM 调用的 `Σ`，含工具循环的多 call；单 call 轮即等于 usage）。实现：在 `run` 循环外加 `long turnBilledIn/out=0`，每次 `usage!=null` 到账即 `+=`；`TurnCompleted.payload={..., inputTokens=末call.prompt(占用), billedPrompt=turnBilledIn, billedCompletion=turnBilledOut}`。失败/重试丢弃的半截流不计入（`streamError/cancel` 分支不加）。
* 多 call 轮的 `TurnCompleted.inputTokens` 仍是末 call 占用（不是 Σ），注释写明，避免重蹈 04-05 膨胀。

## 3. 后端改造步骤（按文件顺序，先改后测）

1. `Core/Models/WorkspaceModels.cs`：按 §2.1 加 2+7 个字段，注释写清快照/累计语义。`ExportSessionMarkdownAsync` 头部 `- Total Tokens: In X / Out Y` 改为 `- 占用 IN X（快照） / 本轮 OUT 按轮 / 累计 Σ P+C`（读新增字段，旧 meta 缺键显示 `?`）。
2. `Services/TokenBudgetService.cs`：加 `ResolveOccupancy` + 改 `CalculateBudgetStatus`（§2.2）。`EstimateTokens/EstimateMessageTokens/BuildWithinBudget` 一字不改。
3. `Services/WorkspaceSessionService.AppendSessionMessageAsync:264-269`：在现有 3 行下追加 2 行：
   ```csharp
   if (message.InputTokens.HasValue && message.InputTokens.Value > 0) meta.CumulativePromptTokens += message.InputTokens.Value;
   if (message.OutputTokens.HasValue && message.OutputTokens.Value > 0) meta.CumulativeCompletionTokens += message.OutputTokens.Value;
   // user/tool（含估算回退分支）不碰 Cumulative；compact 重写时保留 Cumulative（见 03 篇）；clear 时清零
   ```
4. `Services/TokenBudgetService.GetBudgetStatusAsync:331-373`：取到 `messages` 后另读 `meta`（已有 `GetSessionsByWorkspaceAsync` 可取到当前 `meta`，或加一次 `GetSessionAsync`），把 `Cumulative*` 填入 DTO 的 `Billed*`。无 meta 则 0。
5. `Services/AgentOrchestrator.cs`：按 §2.3 加 `turnBilledIn/out` 与 2 个事件载荷键。`LlmGatewayClient.cs:281` 的 `hitRate` 日志保留（已是 `cached/in*100`），追加 `billed` 字段到 `[RES]` 行（可选但推荐，联调 04 用）。
6. 跑 `dotnet build`，再跑单测（§5）全绿后方可进入 §4。

## 4. 前端改造步骤（后端合闸后）

1. `bridge/hostBridge.ts`：`ContextBudgetStatusDto` 加 §2.1 同名可选字段；`SessionMetadata` 加 `cumulativePromptTokens/cumulativeCompletionTokens`；`mock getContextBudget:1223-1240` 的 `usagePercentage` 保持 `used/total`（04-05 已改），`mock` 返回新增字段 `lastPromptTokens=used, occupancyState='estimated', billed*=1200/300` 固定值；`AgentTurnEvent payload` 类型加 `billedPrompt/billedCompletion?: number`。
2. `stores/agentStore.ts`：`AgentTelemetry` 加 `billedPrompt/billedCompletion: number; occupancyState: string; liveEst: number`。`UsageUpdated/AssistantMessagePersisted/TurnCompleted` 覆盖 `input/output/cached` 不变，另存 `billed*`；新增流式估算：`TextDelta/ReasoningDelta` 到达时 `liveEst += estTokens(delta)`（与 znaide 同公式，见下），`Usage` 到账/`TurnFinished` 清零。`estTokens` 前端函数（与 znaide `app.rs:2119` 同口径）：
   ```ts
   function estTokens(s: string): number { let a=0,o=0; for (const ch of s) ch.codePointAt(0)!<=127?a++:o++; return o + Math.ceil(a/4); }
   ```
3. `stores/workspaceStore.ts`：不改逻辑，仅保证 `refreshBudget` 后 `budgetStatus` 含新字段透传（`pinia` 原样存）。
4. `components/workbench/WorkbenchTopBar.vue:205-218`：
   * `usedTokens` 显示规则：`occupancyState==='unknown'` 或 `lastPromptTokens==null` → 显示 `?` 且 `usagePercentage` 显示 `?`（灰色，`statusColor='#6b7280'`）；空会话（`used==0 && !estimated`）显示 `0/128K(0%)` 蓝色。
   * 悬停卡新增两行：`计费累计 in/out/Σ（billed*）` 与 `命中率 cache/input（CacheHitRate，无则 -）`；`Available` 行保留。流式中若 `liveEst>0`，在 `Used` 后缀 `≈+est`（与 znaide `≈` 同语义）。
5. `components/chat/MessageTimeline.vue:51-55,89-96`：持久 footer 文案冻结为 `IN {input} · Cache {cached} · {p}%`（`p=cached/input`，分母 0 显示 `-` 而非 `0%`，以区分未知）；本轮 footer `In: {input} (Cache {cached} · {p}%)` 同公式；`OUT` 显示本轮值；`Σ` 不在此显示（只在顶栏悬停卡）。
6. 跑 `vue-tsc -b && vite build`。

## 5. 后端单测（新增文件 `tests/HaloAgent.Tests/Stage05TokenUsageTests.cs`，至少 6 个）

* `Occupancy_Uses_Last_Snapshot_Not_Sum`：3 条 assistant 各 `Input=48083`，`ResolveOccupancy→48083`，`CalculateBudgetStatus.UsedInputTokens==48083` 且 `OccupancyState==known`。
* `Occupancy_Unknown_When_No_Snapshot`：全 `null` 消息 → `LastPromptTokens==null, OccupancyState==unknown, UsedInputIsEstimated==true`。
* `Cached_Clamped_To_Input`：`cached=90000,input=48083 → LastCached=48083, CacheHitRate==1.0`。
* `Billing_Accumulates_While_Occupancy_Snapshots`：连 `Append` 3 次 `Input=48K/49K/50K` → `TotalInput==50K` 但 `CumulativePrompt==147K`。
* `Empty_Session_Is_Known_Zero`：空列表 → `OccupancyState==known, Used==0`。
* `Turn_Billing_Sums_MultiCall`（可用 `AgentOrchestrator` 桩或直接测载荷装配函数）：两 call `prompt 48K/49K` → `TurnCompleted.inputTokens==49K, billedPrompt==97K`。

旧 `Stage04WorkspaceAndBudgetTests.AC03` 中 `Assert.Equal(8192-2048-1024, AvailableInputTokens)` 仍成立（`messages` 无快照走估算分支时 `available` 随估算变，此断言的 `messages` 均为无快照小文本，保持通过；若补快照字段则按新公式更新期望值——本篇不改该用例数据）。

## 6. 验收

- [ ] 同一会话 3 轮 `TurnCompleted.inputTokens` 缓增（48K→49K→50K），`billedPrompt` 单调 Σ；顶栏 `Used` 与末轮 footer `IN` 一致（<1%）。
- [ ] `cache≤IN`；无快照会话顶栏显示 `?` 灰色而非 `38%`；空会话显示 `0%`。
- [ ] 流式中顶栏 `Used≈` 带估算，到账切真实无跳变回 0。
- [ ] `meta.json Cumulative*` 在 `ExportSessionMarkdown` 与顶栏悬停卡一致；`dotnet test` 新增 6 用例全绿。
