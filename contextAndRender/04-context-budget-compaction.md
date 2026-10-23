# 高缓存命中（下）：预算 / 估算 / Compaction

源码：`packages/agent-runtime/src/context-budget.ts`，
`context-calibration.ts`，`pi-runtime-estimates.ts`，
`compaction-request.ts`，`compaction-tail.ts`，
`compaction-summary-input.ts`，`hosted-search-replay.ts`。

## 1. 预算公式：从 model window 推导一切

`contextBudgetLimitsFor(model)`（`context-budget.ts:105-138`），只依赖 `contextWindow/maxTokens`：

```ts
contextWindow = model.contextWindow || 128000
modelOutputBudget = min(maxTokens || 8192, window*0.25)
reserveFloor = min(16384, window*0.5)          // COMPACTION_RESERVE_FLOOR_TOKENS
requestHeadroom = min(window-1, max(reserveFloor, modelOutputBudget, window*0.05))
hardLimit = window - headroom                   // 发请求的安全边界
keepRecentTokens = clamp(hardLimit*0.2, 8000, 64000) 且 ≤ hardLimit*0.5
autoCompactionThreshold = hardLimit * 0.9
retainedUserMessage ≤ min(20000, hardLimit*0.5) // 对齐 Codex COMPACT_USER_MESSAGE_MAX_TOKENS
```

- 父子代理共用同一公式（文件头注释：子预算比父松会导致父认为不安全而子还发请求）。
- 32K 小窗与 1M 大窗都可用：比例 + 上下钳，不是固定 token 数。
- 病态小窗钳到 ≥1，不产生 0/负数。

## 2. 估算：usage-anchor，只估增量

`pi-runtime-estimates.ts:65-99 :: estimateContextTokens`：

1. 只取 `system/user/assistant/toolResult` 进 provider 估算；`custom/bashExecution/branchSummary/compactionSummary` 用 `len/4` 启发式；未知 role 抛 `LocalRequestError("context-validation")`（fail fast，不送网关）。
2. `getLastAssistantUsageInfo(providerMessages)` 找最后一个带 provider usage 的 assistant：
   - 无 usage：全量 `Σ estimateTokens`。
   - 有 usage：`tokens = calculateContextTokens(usage) + Σ(锚点之后 estimateTokens)`。
3. 文本 token 系数与 pi-ai 1.1.0 对齐：`ESTIMATED_TEXT_CHARS_PER_TOKEN = 3.5`，图片块按 4800 字符。

意义：provider 回传的 usage 是真值，锚点之前的估算误差全部清零，只有尾部是估算。预算抖动大幅下降，不会在边界反复横跳触发 compaction。

`context-calibration.ts`：用真实 usage 回填校准估算系数（低频、滑动平均，具体系数看该文件），防止长期漂移。

## 3. 何时压：90% 线 + turn 边界内联

- `automaticCompactionThresholdFor = hardLimit*0.9`（L70-74）：提前到硬边界之前，让“估算漂移 + 单轮增长”有缓冲，provider 请求永远不是第一个发现溢出的人。
- 无后台线程 variant：到线后在**下一 turn 边界内联**执行，保证 transcript 状态机简单（注释 L45-50）。

## 4. 压什么：tail + 摘要 + 边界裁剪

- `keepRecentTokens` 保留最近尾巴（工具调用链不断），`compaction-tail.ts` 负责切出“不断链”的尾部。
- `compaction-summary-input.ts` 组装摘要输入：checkpoint（已剥 `context_budget`，见 03）+ tail + 用户消息上限 `retainedUserMessageBudget`。
- `compaction-request.ts :: compactionRequestOptions`（L47-69）：摘要请求复用**同一 header 管道**（OpenCode session 头 + 自定义头 + sessionAffinity），`sessionId` 用运行时自己的，保证摘要与正文打到同一网关后端。
- Responses 系（`openai-responses/openai-codex-responses`）摘要须补 `prompt_cache_key`（`withSummaryPromptCacheKey` L89-105）：摘要 adapter 要 `"none"` retention 会丢 adapter 自带的 key，这里用 `clampOpenAIPromptCacheKey(sessionId)` 补回，否则 Codex 网关 400 `invalid_responses_request`。只补缺失的 key，不动已有 key。

`withCompactionRequestHeaders`（L113-172）用 `Proxy` 只包 `completeSimple`，其余 `Models` 成员原样透传，adapter 升级隔离在此一处。

## 5. hosted-search 的 token 记账

- `estimateContextTokens(messages, model)` 需要完整 `Model<Api>`（带 `api/provider/id`）才能做 replay 减免：Responses adapter 只对**产出该 `web_search_call` 的同一模型**回放，换模型则全额计费（`context-budget.ts:140-157` 注释）。
- 只有部分 window 事实（catalog 条目）时走保守估计：全部搜索块计费，不假设能省。
- `validateHostedSearchMessages` 在估算入口校验形状，坏记录早抛本地错误。

## 6. 历史恢复：best-effort，不挡首轮

`sidecar.ts :: runtimeFor` 中：

```ts
detail = await hostProxy.call("session.get", {id}) // 取 messages+compaction
restored = filter out 当前 prompt 行（按 userMessageId，防双算 history 字节预算）
supportsVision = visionFromModelConfig(provider.modelConfig)
history = await hydrateAttachmentHistory(restored, {scratchDir, projectPath, attachmentsDir, supportsVision})
```

- 当前 prompt 单独发送，恢复时先剔除其持久化行再做附件水合，否则占 history 字节预算。
- `hydrateAttachmentHistory` 按 vision 能力决定附件进内联还是引用。
- try/catch 包裹：恢复失败仍可干净起新轮（best-effort）。

## 复刻清单

1. 抄 `contextBudgetLimitsFor` 五行公式 + 90% 线，不要配阈值。
2. 估算必须 `usage-anchor`：有 usage 就只估锚点后。
3. 摘要请求走同一 header/sessionId/`prompt_cache_key` 管道。
4. 恢复时先剔当前 prompt 行，再水合，再估算。
## 7. 增补：journal / 校准 / 输出上限 / 切点（子代理调研回填）

- Journal（`system-transcript-journal.ts`）：`persist()` 在 dispatch 前持久化 provider-neutral 声明，行结构 `{id, role:system, modelSystem:{version:1, messageJson, beforeMessageId, afterMessageId}}`，失败抛 `request-preparation LocalRequestError` 阻止发送；`orderSystemRows()` 按 before/after 重排 + 序列化去重；`restore()/rememberCheckpoint()` 防重放。
- 校准（`context-calibration.ts`）：双序列 `trailingRatio`（有锚）与 `unanchored`（无锚）；`correct()` 有锚只 scale 尾、无锚同 scale 带内用 ratio 否则 `+overhead（中位数，上限 32k）`；不对称收敛（上行即时、下行需 3 连击 + 每步 ≤15% + 冻结 2 连 misreport + 硬地板 0.85×raw / 顶 6× + boundary guard），窗口 16 中位数，misreport 带 `[0.5,3]` 丢弃。
- 输出上限（`output-cap.ts`）：`estimateOutputCapInputTokens / effectiveModelContextWindow`，`contextBudget()` 取 `max(calibratedMessages, outputCapInput（含 systemPrompt + tool schemas + hosted-search replay 投影）)`。
- 切点（`pi-runtime-compaction-plan.ts`）：`prepareCompaction()` 跳过空/尾已 checkpoint；上 checkpoint 尾作 virtual entries；`findCutPoint` 从尾按 `estimateTokens` 累积至 `keepRecent` 落到合法切点（user/assistant/custom/bash/branch_summary），`findTurnStartIndex` 判 splitTurn；Codex 形 `codexShapedPreparation` 合并三段全摘要，retained 只留 active_turn 最新 user 防孤儿 toolCall。
- 摘要输入（`compaction-summary-input.ts`）：`estimateSummaryPromptTokens = max(history + prevSummary, turnPrefix) / 3.5`；`inputLimit = hard + headroom - min(0.8*headroom, maxTokens) - 2048`；超限先 `reduceSummaryInput（toolResult → 500ch + 后缀，去 thinking）`，再 `planSummaryChunks（×0.9 margin，≤16 块，单消息截断）`；重试 3 次 2/4/8s。
- 失败兜底（`compaction-tail.ts`）：`selectRecentTail` 取最新连续 user/assistant/toolResult 窗 + `pruneOrphanToolResults` + 去 error/aborted/deferred + 单消息 `truncateMessageToTail（chars = tok×3.5，≥64）`，`latestUserGoal` 另保半预算 user；`COMPACTION_RETAINED_TAIL_SHAPE = recent_window` 标记防回退。
- 会话上下文顺序（`session-context.ts`）：`buildContextEntries` 切到末个 compaction；`sessionEntryToContextMessages` 顺序 `systemMessageJson → compactionSummary → retainedReasoning（DeepSeek）→ retainedTail`，过滤 error/aborted/deferred/空 assistant。
- 工具激活快照（`fixed-tool-declarations.ts`）：`TOOL_ACTIVATION_SECTION` 存 `{v:1, snapshot:sha256(account, model, api, endpoint, tools, deferred), active:sorted}`，`restoredToolActivation` 快照不符 fail-closed。
