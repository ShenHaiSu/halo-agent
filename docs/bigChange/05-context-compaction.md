# 05 上下文压缩 Compaction v2（P1-3）

第二参考：`contextAndRender/04` 后半。本阶段动数据格式，破坏性最大。

## 1. 前置检查

- [ ] 03+04 已合入：`SystemTranscriptCheckpoint`（已剥 `context`）与 `ContextBudgetLimits.keepRecent/autoThreshold` 可用。
- [ ] 通读 `CompactionService.cs::BuildSnapshotAsync/RewriteAsync`：确认当前按 `GroupIntoAtomicTurns + keepLastGroups(按组数)` 切分、摘要输入为全量拼接——本阶段全部替换。
- [ ] 确认 `messages.jsonl` 信封格式与 `BackupMessagesJsonlAsync/RewriteMessagesJsonlAsync` 原子性。
- [ ] 确认压缩请求当前走独立 `UnifiedLlmRequest`，未复用会话 Header/亲和——本阶段统一。

## 2. 阶段目标

- 切点按 token 从尾累积到合法边界（不断工具链），不再按组数。
- 摘要输入有预算、有降级、有分片；失败不动盘。
- 摘要请求走同一 Header 管道 + 同一 `sessionId`（同后端）；原子重写 + 压缩期追加不丢。

## 3. 实现办法

### 3.1 切点 `CompactionPlan.cs`（新建，替换 group 数逻辑）

- `PrepareCompaction()`：空/尾已 checkpoint 跳过；上 checkpoint 尾作 virtual entries。
- `FindCutPoint`：从尾按 `EstimateTokens` 累积至 `keepRecent`，落到合法切点（`user/assistant/custom/bash/branchSummary` 边界，`splitTurn` 判半轮）；Codex 形三段合并、retained 只留 active 最新 user 防孤儿 toolCall。
- `SelectRecentTail`：最新连续 `user/assistant/toolResult` 窗 + `pruneOrphanToolResults` + 去 `error/aborted/deferred` + 单消息 `TruncateMessageToTail(chars=tok×3.5, ≥64)`；`latestUserGoal` 另保半预算 user；打标 `recent_window` 防回退。

### 3.2 摘要输入 `CompactionSummaryInput.cs`

- `estimateSummaryTokens = max(history + prevSummary, turnPrefix)/3.5`；`inputLimit = hard + headroom - min(0.8*headroom, maxTokens) - 2048`。
- 超限先 `ReduceSummaryInput（toolResult→500ch+后缀，去 thinking）`，再 `PlanSummaryChunks（×0.9 margin，≤16 块，单消息截断）`；重试 3 次 2s/4s/8s。
- 顺序：`systemJson → compactionSummary → retainedReasoning → retainedTail`，过滤 error/aborted/deferred/空 assistant。

### 3.3 请求与重写

- `CompactionRequestOptions`：复用 01 Header 管道 + 03 sessionId + 02 亲和；`Tools` 置空；`completeSimple` 用 Proxy 只包摘要调用，`Models` 其余透传（adapter 升级隔离）。
- 三段式沿用（快照→释锁→LLM→重加锁重写），压缩期 `append-only` 尾巴保留；`bak` 保留可审计；`Cumulative/Total` 账单不清零。

## 4. 验收标准

- [ ] 单测：工具链中间切不断（assistant+tool 同留同删）；孤儿 toolCall 被剪；单消息超限截断后仍合法。
- [ ] 单测：超限输入先降级再分片，≤16 块；摘要为空/取消/失败不动盘、无 bak 增量。
- [ ] 集成：压缩后继续对话，工具可接着调；压缩期并发追加的消息不丢。
- [ ] 集成：摘要请求 Header 与正文一致（同 sessionHash），trace 可查。
- [ ] 旧按组数切分代码删除；`KeepDefaultGroups/TruncPerMessage` 常量删除或重定义（破坏性）。

## 5. 风险

- 旧压缩备份格式不变（仍 jsonl bak），但切点变化导致首次压缩摘要内容不同，允许。
