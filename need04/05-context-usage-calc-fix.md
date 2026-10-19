# 05 · 上下文用量“口径修正”方案（末轮总量被当每轮累加）

## 1. 现状（已读代码，用户报的数对得上）

用户看到一轮末尾 `IN 48083 cache 47601 OUT 3721` —— 这是 LLM 网关回的**当轮请求总量**（`UnifiedTokenUsage`: `inputTokens=全量prompt tokens（含全部历史+本轮user+工具结果）`，`cachedInputTokens=其中命中缓存部分`，`outputTokens=本轮生成`）。

三处消费口径各说各话：

| 位置 | 当前算法 | 错误 |
|---|---|---|
| 后端 `TokenBudgetService.EstimateMessageTokens` L95-123 | 若消息带 `InputTokens/OutputTokens` 则返回 `Input+Output`；否则启发式估算 | 把“总量快照”当“增量”：`CalculateBudgetStatus` L289 `messages.Sum(EstimateMessageTokens)` 把每一轮的 48K 总量加 N 次 → 3 轮即 144K，远超实际 128K 上限，水位爆表 |
| 后端 `CalculateBudgetStatus` L279-308 | `usedInput = Sum(...)`，`percentage = usedInput/maxContext`，`availableInput = maxContext-maxOut-safety`（固定值，与 used 无关） | `AvailableInputTokens` 恒定不变（`128000-4096-1024`），`percentage` 可超 100%（靠 `Math.Min(100)` 硬钳）；`isNearLimit` 阈值 85% 与顶栏 70/90 两套阈值打架 |
| 前端 `agentStore.telemetry` L227-231/L356-367 | `UsageUpdated/AssistantMessagePersisted/TurnCompleted` 直接覆盖 `input/output/cached` | 本轮 footer（`MessageTimeline` L122-128）与持久 footer（L57-62 取 `msg.inputTokens`）语义不同：前者是末轮总量，后者是该条落盘快照；顶栏 `WorkbenchTopBar` L205-217 取 `budgetStatus.usedInputTokens`（即错误的 Sum 值），三数互不一致 |
| 后端 `AppendSessionMessageAsync` L264-267 | `meta.TotalInputTokens += message.InputTokens` | 每次把 48K 总量往 `Total*` 上加 → `SessionMetadata.TotalInputTokens` 同样膨胀；`ExportSessionMarkdown` L375 展示的 `In/Out` 也随之失真 |

一句话：**网关给的是水位快照（末轮 InputTokens 即会话总量），全链路却按流量增量在累加**。

## 2. 设计目标（口径统一为“快照语义”）

1. `IN = 当前会话实际进模型的 token 总量 = 末条 assistant 消息的 InputTokens`（无精确值时才用启发式估算代替）；
2. `OUT = 本轮生成 tokens（末轮 OutputTokens），会话累计 OUT 另计 `TotalOutputTokens`（按轮增量加，见 3.1，不与 IN 混用）；
3. `cache = IN 的子集`（`cachedTokens ≤ inputTokens` 恒成立，前端展示 `IN xxx (Cache yyy · z%)` 三段一致）；
4. 顶栏水位 / 本轮 footer / 持久 footer 三端同源：全部从同一函数 `resolveSessionUsage(messages)` 取数。

## 3. 详细设计

### 3.1 后端 `TokenBudgetService`（核心，2 个函数）

```csharp
// 新增：会话总量 = 末条带精确值的 assistant 快照；无则回退启发式 Sum
public static int ResolveUsedInputTokens(IReadOnlyList<UnifiedChatMessage> messages)
{
    for (int i = messages.Count - 1; i >= 0; i--)
        if (messages[i].InputTokens.HasValue && messages[i].InputTokens.Value > 0)
            return (int)messages[i].InputTokens.Value;
    return messages.Sum(m => EstimateMessageTokens(m)); // 无快照（纯离线/老数据）才估算
}
// EstimateMessageTokens 去掉“优先用 Input+Output”分支，永远启发式估算
// （调用方按需选用 ResolveUsedInputTokens 或 EstimateMessageTokens，不再二义）
```

- `CalculateBudgetStatus` 改为：
  - `usedInput = ResolveUsedInputTokens(messages)`；
  - `cached = 末条 assistant.CachedTokens ?? 0`（钳 `≤ usedInput`）；
  - `percentage = Round(usedInput / maxContext * 100, 1)`（分母用 `maxContext` 还是 `availableInput` 二选一，全链路统一为 `maxContext`，顶栏公式同步，见 3.2）；
  - `availableInput = Max(0, maxContext - maxOut - safety - usedInput)`（原来漏减 `usedInput`，这是“剩余可用恒定不变”的 bug 本体）；
  - `isNearLimit = percentage >= 85`（后端只给事实位；顶栏 70/90 变色阈值保留在前端，不强制统一）。
- `WorkspaceSessionService.AppendSessionMessageAsync` 的 `meta.Total*` 累加改为：
  - `TotalOutputTokens += message.OutputTokens ?? 估算`（OUT 是增量，可加）；
  - `TotalInputTokens = message.InputTokens ?? (TotalInputTokens + 估算)`（IN 是快照：有精确值则覆盖，无则增量估算；禁止 `+= 快照`）；
  - `TotalCachedTokens = message.CachedTokens ?? TotalCachedTokens`（同快照语义）。
- `GetBudgetStatusAsync` 不变（仍读全量 messages 后调 `CalculateBudgetStatus`），修正后自动正确；`TruncateToBudget/BuildWithinBudget` 用的 `EstimateMessageTokens` 改后变为纯估算，不再受快照污染，裁剪行为更稳（原来带快照的消息权重畸大，中间组会被过度裁掉）。

### 3.2 前端三端统一

- `WorkbenchTopBar.vue` L211-217 `usagePercentage`：分母与后端一致用 `totalTokens`（`maxContext`），分子 `usedTokens`（`budgetStatus.usedInputTokens`，修正后即总量）；删除本地 `(+maxOut+safety)/total` 的旧公式分支（保留作 `budgetStatus` 为空时的降级，但同样改为 `used/total`）。
- `MessageTimeline.vue`：
  - 持久 footer（L57-62）：语义改为“该轮快照”，文案 `IN {input} · Cache {cached} · OUT {output}`（`cache≤IN`，`OUT` 为该轮生成）；`protocolOf` 当前读全局 `telemetry.protocol` 是错的（串轮），改为 `budgetStatus.protocol ?? telemetry.protocol`，或后端在消息上加 `protocol` 字段后优先读消息（可选）。
  - 本轮 footer（L121-128）：`In: {input} (Cache {percent})` 的 `percent` 当前是 `cached/input` 百分比，而持久是绝对值，两处统一为 `Cache {n} · {p}%` 同一格式；`cachePercent` 分母为 0 时显示 `0%` 已有，保持。
- `agentStore.telemetry` 事件赋值不动（网关快照直写即正确），但 `TurnCompleted` 后不再把 `telemetry` 累加到任何地方（确认无 `+=` 残留，全局搜 `telemetry.inputTokens +` 应零命中）。

### 3.3 Mock 对齐（`hostBridge.ts` L1223-1240）

- Mock `getContextBudget` 当前 `used = Sum(len*0.5)+120` 是估算口径，与修正后的“末条快照优先”一致（mock 消息无 InputTokens，走估算分支），无需改算法；但 `usagePercentage` 改为 `used/total`（删 `+4096+1024`），与 3.2 同公式，避免 Web 调试态与真机口径分裂。

## 4. 施工步骤

1. 后端：`TokenBudgetService` 加 `ResolveUsedInputTokens` → `EstimateMessageTokens` 去快照分支 → `CalculateBudgetStatus` 三行修正（used/cached/available）→ `WorkspaceSessionService.Append` 的 `Total*` 快照/增量分流 → `dotnet build` + 跑现有 `TokenBudget` 相关单测（若有则全绿，无则加 2 个：`Sum快照三轮不膨胀 / 无快照回退估算`）。
2. 前端：`WorkbenchTopBar` 百分比公式统一 → `MessageTimeline` 两处 footer 文案统一 → 全局搜 `+= .*InputTokens` 确认无累加残留 → `vue-tsc -b && vite build`。
3. 对账验证：同一会话连续发 3 轮，记录每轮 `TurnCompleted.inputTokens`（应单调缓增趋稳，如 48K→49K→50K，而非 48K→96K→144K）；顶栏 `%` 与末轮 footer `IN` 一致；`meta.json TotalInputTokens ≈ 末轮 IN`（允许估算误差 ±5%）。

## 5. 验收清单

- [ ] 复现用户数：`IN 48083 cache 47601 OUT 3721` 出现后，顶栏显示 `≈48K / 128K (38%)`（不是 `144K/128K(100%)`）；再发两轮，顶栏缓慢上涨而非翻倍。
- [ ] `cache ≤ IN` 恒成立；`cache%` 与绝对值两处一致；`OUT` 每轮独立（不累加进 IN）。
- [ ] 顶栏 / 本轮 footer / 持久 footer / `meta.json` 四处 `IN` 同源（同一轮内差值 <1% 或纯估算误差）。
- [ ] `availableInput = maxContext-maxOut-safety-used` 随轮次递减，不再恒定；`isNearLimit` 与顶栏变色方向一致（后端 85% 预警早于前端 90% 红线，属预期差一级）。
- [ ] 后端单测 + `vue-tsc -b && vite build` + `dotnet build` 全过。

## 6. 风险与回退

- 老会话消息无 `InputTokens`（`null`）时走估算分支，水位会比精确值偏小（启发式对中文 1.55 系数偏保守），属可接受误差；随新轮次产生精确快照后自动校准，无需迁移老 jsonl。
- `EstimateMessageTokens` 去快照分支后，`BuildWithinBudget` 对“带精确值的新消息”权重下降（从 48K 快照权重降为估算几 K），裁剪保留的中间组会变多（更保守、更符合用户利益）；若模型上下文极小（`≤8K`）且历史极长，保留组数上限建议加 `最多保留最近 40 组` 熔断（可选，不列入必验）。
- 回退：恢复 `EstimateMessageTokens` 快照分支与 `Sum` 口径，一处提交即回；`AvailableInput` 公式回退同步（两行联动，勿只回一半）。
