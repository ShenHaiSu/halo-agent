# need21-01 现状诊断与代码证据

> 本篇只摆证据，不给方案。行号锚点以 `develop` 当前快照为准，路径前缀均为 `src/Web.Client/src/`（另有注明除外）。

---

## 1. 双轨三套渲染并存

`components/chat/MessageTimeline.vue`（239 行）内有三套渲染：

| 套 | 位置 | 默认 | 说明 |
|---|---|---|---|
| A 旧三桶 | L20-72 `v-else` 分支 | **默认开**（`useTurnUnits=false`，`L142-146` 需 `localStorage zcode-turn-units=1` 才切新路） | `timelineItems` ← `useLegacyTurnGroups` ← `buildTurnGroups(persistedMessages)` |
| B active 累积串 | L73-89 `active-turn-container` | 流式期可见 | `agentStore.toolCards` + `agentStore.streamingText` |
| C 新 TurnUnit | L13-19 `v-if="useTurnUnits"` | 默认关 | `buildTurnUnits(messagesToRows(persistedMessages))` → `TurnUnitView` |

旧路径 `showActiveContainer`（L158-164）：

```ts
if (!useTurnUnits.value) return true;   // ← 默认分支恒 true
```

即默认情况下流式期 A 与 B **同时可见**。A 的持久 turn（`L39-42` content 在上 + `L43-64` 思考舱在下）与 B 的累计串上下叠加，是“正文出现两次”的结构性原因。

## 2. 累积串跨 iteration 不清零（“先tool后全部正文、无换行” 的直接证据）

`stores/agentStore.ts`（956 行）：

| 行为 | 位置 | 说明 |
|---|---|---|
| 累积 | L403-414 | `t.streamingText += evt.textDelta` / `t.streamingReasoning += evt.textDelta`，裸拼接 |
| 置空 | L373-378 `resetTurnState` | 只在 `sendPrompt(L744)` 调用 |
| `IterationStarted` 不清 | L457-461 | 只置 `streamingActive/isGenerating`，不清文本、不清卡 |
| `AssistantMessagePersisted` 不清 | L543-554 | 只更新遥测 + `refreshAfterTool`（预算），不清文本 |
| 清零 | L703-716 `finishTurn` | `TurnCompleted` 后才 `toolCards=[]; streamingText=''` |

B 容器结构（`MessageTimeline.vue` L73-89）：

* L76 标题“思考中…已调用 N 个工具”→ 用户口中的“思考区块”即此容器；
* L79-81 `toolCards` 在上，L82-85 `streamingText` 在下 → “先 tool 后正文”；
* `streamingText` 是一轮内**所有 iteration 的 TextDelta 之和**，iteration 之间无任何分隔符 → “所有正文、无换行拼接”。

后端 corroboration：`Host.Desktop/Services/AgentOrchestrator.cs` L423-424 两个 `StringBuilder`（`textAcc/reasoningAcc`）按事件种类分别累积，L657-676 每 iteration 落盘一条 assistant 消息（`textAcc→content, reasoningAcc→reasoningContent`）。持久态本身是分离的（`ChatCompletionsAdapter.cs` L336-360 `reasoning_content→Reasoning()` / `content→Text()`；`NonStreamNormalizer.cs` L38-46 同理），重复只发生在前端 B 容器。

真实 trace 佐证（`E:\AppData\HaloAgentData\traces\sess_20261020_4a83a723`，chat 协议 34 轮）：仅 3 轮含 `TextDelta`，其余纯 `ReasoningDelta` + 工具（如 `03:16:29` 一轮内连续 100+ `ReasoningDelta` 后接 `exec_command/read_file`），终轮 3126 个 `TextDelta`。B 容器的 `streamingText` = 全部 34 轮 `TextDelta` 之和。

附带缺陷：`streamingReasoning` 有收无显——`agentStore` L409-414 累积了，但 `MessageTimeline` L82 只渲染 `streamingText`，deepseek 类模型的思考在流式期实际不可见。

## 3. 工具不重复、正文重复的不对称去重

`composables/useLegacyTurnGroups.ts` L40-52：持久工具消息中与 live 卡同 `callId` 的被过滤（`liveCallIds`），但 **text 没有对等去重**。所以工具只在 B 出现一次，正文在 A、B 各出现一次。

复制口径反而是正确的（未被使用于渲染）：`utils/toolCallRowToCard.ts` L204-219 `mergeTurnText` 与 `utils/toolRenderers/registry.ts` L187-212 `mergeTurnTextFromBlocks` 均用 `join('\n\n')`，`【思考】/【工具名】/【子智能体】` 分段——“无换行拼接”在复制链路不存在，只存在于 B 容器的 `+=` 累积。

## 4. 性能热点（长会话卡顿的四条链）

按热度排序，均为流式期独有（终态只算一次 `finalHtml` 并缓存，`MarkdownStream.vue` L93-107，所以“持久态不卡”）：

| # | 热点 | 证据 | 复杂度 |
|---|---|---|---|
| H1 | 逐 token `touch` | `agentStore.ts` L403-414 每个 delta 一次 `touch(t)` → 整树 computed 失效；`useTimelineScroll.ts` L28-33 每次 `allKeys` 变化双 rAF + 200ms 保底 timer | 每字 O(订阅数) |
| H2 | Markdown 全文重排 | `MarkdownStream.vue` L76-88 流式每 30ms（`useRafThrottle.ts` 全文件）对**全文** `md.render(tolerantMarkdown)` + hljs；hljs 按 `lang+hash` 缓存但文本增长中永远 miss；`limit` 缺省 8000，`truncateByParagraph` 每次 `lastIndexOf` 全文扫描 | 单轮 O(L²) |
| H3 | 全量镜像重建 | `workspaceStore.ts` L422-427 `syncMirror` 每次 `rowsToMessages` 全量重建 `sessionMessages` → `buildTurnGroups`(347 行)/`timelineItems` 全重算 | 每帧 O(N) |
| H4 | 工具参数逐片刷新 | 一 tool 数十个 `ToolCallArgsDelta`（trace 可见）→ 每次 `refreshFromProjection`（`agentStore.ts` L320-349）全行扫描 + 卡片 `touch`；`useToolArgs.ts` 有 `rowId+len` Map 缓存（L109-123，上限 300）但扫描本身未节流 | 每片 O(R) |

既有缓解（保留并沿用）：投影帧 rAF 合并 + `prioritizeDeltas` + `coalesceDeltas`（`projectionFrameQueue.ts` 全文件 136 行；`projectionStore.ts` L251-330 四规则 R1-R4）；`ToolCallCard` HeaderMemo/StatusBar/BodyLazy 三段隔离 + Body `IntersectionObserver`（`rootMargin 240px`）懒挂载（`ToolCallCard.vue` L12-53/273-296）；工具 header 定稿前零重算（`headerSummary/diffStats` L159-193 依赖 `complete` 翻转）。

## 5. 顺序与键的现状（重构后要锁死的）

* 新路径顺序正确：`buildTurnUnits.ts` L193-213 `assembleBlocks` 按 `rowId` 排序后 `toolCall` 进缓冲、遇 `text/reasoning/subagent` 即断组；`blockKey` 用 `rowId/gkey`（L215-220），已禁 `index`。
* 旧路径 `buildTurnGroups`（`turnGroup.ts` L168-331）按消息到达序组装，`RUNNING_STATES(L27)` 运行权威只看 `turnHeader.state`，空 reasoning 裁剪（L248-250）——语义正确但与 C 路重复实现，属删除对象。
* 规范已写好：`stores/projectionRows.ts`（254 行）R1-R5（禁 index key / `data-row-id` / hook 纪律 / rowId 永不自增 / 追加只进 streaming 行）——重构后作为唯一类型契约保留。
