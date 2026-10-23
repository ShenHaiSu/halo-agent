# 04 预算公式 + usage-anchor 估算 + 校准（P1-2）

第二参考：`contextAndRender/04-context-budget-compaction.md` 前半。

## 1. 前置检查

- [ ] 通读 `TokenBudgetService.cs::EstimateTokens/EstimateMessageTokens/ResolveUsedInputTokens/ResolveOccupancy` 与 `Cuts.cs::BuildWithinBudgetEx/TruncateToBudget`：确认当前是固定 buffer + 三阶裁剪，无按 window 公式。
- [ ] 通读 `UsageRecorder.cs`：确认 usage 快照字段（`InputTokens/OutputTokens/CachedTokens`）的写入时机与来源（网关真值 vs 估算）。
- [ ] 列出所有 `ModelConfig` 的 `MaxContextTokens/MaxOutputTokens` 为空时的 fallback（当前默认 128k/4k 的位置）。

## 2. 阶段目标

- 预算全部从 `window` 推导，无魔法阈值；父子同公式。
- 估算以网关 `usage` 为锚，锚后才估算，抖动清零。
- 长期漂移有校准收敛；输出上限纳入预算 `max()`。

## 3. 实现办法

### 3.1 新建 `ContextBudgetLimits.cs`

照抄公式（允许改名，不许改语义）：

```
window = model.MaxContextTokens ?? 128000
outputBudget = min(model.MaxOutputTokens ?? 8192, window*0.25)
reserveFloor = min(16384, window*0.5)
headroom = min(window-1, max(reserveFloor, outputBudget, window*0.05))
hardLimit = window - headroom
keepRecent = clamp(hardLimit*0.2, 8000, 64000) 且 ≤ hardLimit*0.5
autoThreshold = hardLimit*0.9
retainedUser ≤ min(20000, hardLimit*0.5)
```

病态小窗钳 ≥1。删除 `DefaultSafetyBuffer=1024` 旧语义。

### 3.2 重写估算 `EstimateContextTokens(messages, model)`

- 只取 `system/user/assistant/toolResult` 进 provider 估算；`custom/bash/branchSummary` 用 `len/3.5`；未知 role 抛本地错误（fail fast，不送网关）。
- `GetLastAssistantUsage` 找末个带 provider usage 的 assistant：有锚 `= usage真值 + Σ(锚后 estimate)`；无锚全量估。
- 系数对齐：英文 `3.5 chars/token`（当前 3.6 改为 3.5，破坏性但必须），图片块按 4800 字符。
- 入口校验坏记录（形状坏直接本地错误，见 03 journal 同源）。

### 3.3 新建 `ContextCalibration.cs`

- 双序列：`trailingRatio（有锚）` 与 `unanchored（无锚）`；窗口 16 中位数。
- `Correct()`：有锚只 scale 尾、无锚同 scale 带内用 ratio 否则 `+overhead（中位数，上限 32k）`。
- 不对称收敛：上行即时、下行需 3 连击且每步 ≤15% + 冻结 2 连 misreport + 硬地板 `0.85×raw`/顶 `6×` + boundary guard；`[0.5,3]` 外丢弃。
- 校准只影响估算，不改账单真值。

### 3.4 输出上限 `OutputCap.cs`

- `EstimateOutputCapInput = systemPrompt + tool schemas + hosted-search 投影`；`contextBudget = max(calibratedMessages, outputCapInput)`。

## 4. 验收标准

- [ ] 单测：32K/128K/1M 三档公式快照；小窗无 0/负数。
- [ ] 单测：有锚只估锚后（锚前改动不影响结果）；无锚全量；未知 role 抛错。
- [ ] 集成：连续 5 轮有锚估算误差带内，不在阈值附近反复横跳触发 compaction。
- [ ] `ResolveUsedInputTokens` 禁止 Sum 累加（总量快照语义保留）；旧 3.6 系数已替换。
- [ ] 90% 线：到线后下一 turn 边界内联触发（无后台线程 variant），见 05。

## 5. 风险

- 系数 3.6→3.5 与图片开销变更会改变旧会话水位显示，一次性跳变，允许。
