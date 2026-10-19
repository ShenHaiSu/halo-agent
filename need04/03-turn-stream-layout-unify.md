# 03 · 一轮对话“流式布局统一”方案（tool 上下跳、正文割裂）

## 1. 现状（已读代码，复述用户看到的割裂帧）

`MessageTimeline.vue` 流式区当前顺序（L83-111）：

```
流式思考舱（streamingReasoning）
流式工具卡片组（toolCards 全量，L98-100）
流式正文（streamingText，L103-107）
```

持久区（L14-81）每条 `persistedMessages` 按 jsonl 落盘顺序渲染：`user → assistant(含toolCalls小tag) → tool → tool …`，其中 `tool` 用独立灰块（L66-75）。

`agentStore` 事件时序（一轮多 iteration）：

1. `IterationStarted → ReasoningDelta* → TextDelta* → ToolCallStarted/Updated`（卡片出现在**正文上方**，用户先看到工具）；
2. `ToolExecutionCompleted` 即调 `refreshAfterTool → loadSessionMessages`（L352/L410），持久区瞬间多出 `assistant+tool` 灰块，而流式区 `toolCards` 还在 → **同一工具出现两次（上持久灰块 + 上流式卡片），正文被夹在中间**；
3. 下一 iteration 的 `TextDelta` 续写 `streamingText`（仍在工具卡下方）→ 用户观感“正文写完，工具又跳到上方，下拉框又到下方”。

另有两处放大器：`ToolCallCard` 默认 `edit/write/exec` 展开（L141），首屏即全量输出；`truncateToolText 4000 / card 8000` 两档截断不一致，高度跳变。

## 2. 设计目标（单容器、单顺序、单真相）

1. **一轮一容器**：流式期间只允许一个 `本轮容器（TurnContainer）` 存在，内部顺序恒为 `思考摘要行 → 工具组 → 正文`，DOM 位置永不上下互换；正文增量只做文本追加，不触发布局重排。
2. **持久与流式互斥**：凡 `toolCards` 中已存在的 `callId`，持久区对应 `tool` 灰块在**本轮结束前隐藏**（去重），轮末再一次性对账，避免“双影”。
3. **按 callId 稳定插入**：工具按首次 `ToolCallStarted` 顺序占位，后续 `Updated/Completed` 只原位更新状态/输出，不 `push` 重排、不 `sort`。

## 3. 详细设计

### 3.1 模板重排（`MessageTimeline.vue`）

将 L83-111 改为单个 `data-testid="active-turn-container"`：

```html
<div v-if="hasActiveTurn" data-testid="active-turn-container" class="rounded-lg border border-subtle bg-surface/40 px-3 py-2 space-y-2">
  <!-- ① 思考行（常驻容器顶，流式/持久复用，默认收缩逻辑见04篇） -->
  <ActiveThinkingHeader :reasoning="agentStore.streamingReasoning" :tool-count="agentStore.toolCards.length" />
  <!-- ② 工具组（只此一处，禁止别处再渲染 toolCards） -->
  <div v-if="agentStore.toolCards.length" class="space-y-1.5" data-testid="active-turn-tools">
    <ToolCallCard v-for="card in agentStore.toolCards" :key="card.callId" :card="card" :default-collapsed="true" />
  </div>
  <!-- ③ 正文（工具下方固定位，增量追加） -->
  <div v-if="agentStore.streamingText" class="markdown-body ..." v-html="renderMarkdown(agentStore.streamingText)" />
  <div v-else-if="agentStore.streamingActive && agentStore.isGenerating">Agent 推理中…</div>
</div>
```

- 删除原来散装的三块（思考舱/tool组/正文各占一段 `space-y-4`）；容器外 `space-y` 收紧，避免空容器占位（`v-if="hasActiveTurn"`，定义见 3.2）。
- `ToolCallCard` 新增 prop `defaultCollapsed`（03 篇先加，04 篇再定默认收缩策略）：本轮容器内一律收缩渲染首屏高度（只 header 行），输出区折叠，从根上消灭“调用完毕正文渲染、结果又顶到上方”的高度跳变。

### 3.2 状态机（`agentStore`，不动导出名）

- 新增 `hasActiveTurn = streamingActive || streamingText || streamingReasoning || toolCards.length>0`（模板用它控制容器显隐，替代三处各自 `v-if`）。
- `refreshAfterTool` 加节流 + 去重语义：
  1. `ToolExecutionCompleted` 不再每次 `loadSessionMessages`（这是跳动主因），改为只更新本桶 `card.resultText/status` + 打 `dirtyPersisted=true`；
  2. 只有 `AssistantMessagePersisted`（每 iteration 一次）才 `loadSessionMessages`，且加载后做 `callId` 对账：持久区 `tool` 消息若 `toolCallId` 仍在 `toolCards` 中，则模板侧隐藏（`hiddenToolCallIds` computed），不删数据只藏显示；
  3. `TurnCompleted/Cancelled/Error/Truncated → finishTurn` 时 `dirtyPersisted` 若真则最后拉一次全量，然后清空 `toolCards`（移交 04 篇归并容器，03 篇先做到“轮末流式容器消失、持久区只剩一份”）。
- `ToolCallStarted/Updated/Completed` 按 `callId` 幂等 `upsert`，`iteration` 只做分组展示用，不参与排序；乱序到达（如 `Completed` 先于 `Started`）时补建占位卡，保证顺序 = 首次见到的顺序。

### 3.3 持久区去重（模板 + computed，不动后端）

- `persistedMessages` 保持 jsonl 顺序不变；新增 `visiblePersistedMessages`：过滤掉 `role==='tool' && hiddenToolCallIds.has(toolCallId)` 的条目；`assistant` 的 `toolCalls` 小 tag（L51-55）在本轮进行中隐藏（避免与工具组重复），轮末恢复（供 04 篇归并计数用）。
- `hasTelemetry` footer 不动（05 篇再统语义），但位置收进各消息内部不变，避免 footer 被误认为“正文结束符”导致用户觉得“结束后工具又冒出来”——03 篇只保证位置不动，不改数字。

## 4. 施工步骤

1. `ToolCallCard.vue`：加 `defaultCollapsed?: boolean` prop，`expanded = ref(!defaultCollapsed && defaultExpanded)`；03 篇调用处全传 `true`（04 篇再按“轮末归并”细化）。
2. `agentStore.ts`：加 `hasActiveTurn/hiddenToolCallIds/dirtyPersisted`；`refreshAfterTool(sid, reason)` 按 `ToolExecutionCompleted→只内存更新 / AssistantMessagePersisted→拉一次` 分流；`finishTurn` 收尾拉一次 + 清 `toolCards`（清之前把快照交给 04 篇的归并结构，见 04 篇 §3.3，避免 03/04 合并冲突：03 先清，04 接快照）。
3. `MessageTimeline.vue`：拼装 `active-turn-container` 单容器，删散装三块；持久区改用 `visiblePersistedMessages`；`truncateToolText` 与卡片 `8000` 对齐为同一常量（建议 `6000`，减少高度跳变）。
4. 联调脚本：一轮含 `2 iteration × 各 2 工具`（`read_file+exec_command`  mix），观察 DOM：容器只出现一次，工具数从 1→4 单调加，正文持续向下追加，无滚动抢位（`auto-scroll` 若存在则只在用户位于底部时跟随，见风险）。

## 5. 验收清单

- [ ] 一轮内 `tool-call-card` 节点数单调不减、`callId` 无重复、无“持久灰块+流式卡片”双影（Elements 搜 `data-tool` 计数 == 后端 jsonl `tool` 条数）。
- [ ] 工具永远在正文上方同一容器内；正文 `TextDelta` 到达时容器不跳位（录屏逐帧无上下互换）。
- [ ] `ToolExecutionCompleted` 到达时时间轴不闪烁、不回到顶部（滚动位置保持，允许 ±20px）。
- [ ] 轮末流式容器消失，持久区恰好多出一组 `assistant(+toolCalls)+tool*`，无残留 `active-turn-container`。
- [ ] `vue-tsc -b && vite build` 通过。

## 6. 风险与回退

- 若 `refreshAfterTool` 节流导致后台会话消息延迟，允许后台桶 `dirtyPersisted` 在切卡时补拉一次（`selectSession` 已拉全量，天然对账）。
- 自动滚动抢位是常见次生 bug：施工时确认现有滚动逻辑（若在 `MessageTimeline` 或父容器）改为“仅当距底 <120px 才跟随”，否则提示 `有新消息` 不强跳；回退即恢复每次 `ToolExecutionCompleted` 全量拉取，割裂重现但数据不少。
