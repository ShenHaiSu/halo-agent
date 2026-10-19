# 04 · 轮末“思考+工具归并收缩”方案（默认收起，不再全量展开）

## 1. 现状（已读代码）

- 持久态每条 `assistant`：思考折叠舱（L25-41，`collapsedReasoning[msg.id]`，初始值 `undefined → 展开`）+ 正文 + `toolCalls` 小 tag（L51-55）+ telemetry footer；`tool` 结果是独立灰块（L66-75），与思考舱无归属关系。
- 流式态：思考舱常展（L84-95 无折叠）+ `ToolCallCard` 组（默认 `edit/write/exec` 展开，`ToolCallCard.vue` L141）+ 正文。
- `agentStore.finishTurn` L423-434 只清 `streamingText/Reasoning`，**保留 `toolCards`** 供回看 → 轮末时间轴上同时存在“持久 assistant+tool 灰块”与“流式 toolCards 全量卡片”，用户觉得“思考过程的 tool 调用默认全量展示”。
- `collapsedReasoning` 初始 `undefined` 取反逻辑导致**历史轮次默认展开**（`!undefined === true`），与用户期望相反。

## 2. 设计目标

1. 轮末自动归并：同一轮的 `assistant(reasoning+content+toolCalls) + 后续 tool*` 在视图上属于**一个轮次容器**，容器 header 显示 `深度思考 · N 个工具 · 耗时 Xs`，默认收缩。
2. 历史默认收、新轮默认收、用户手动展开优先级最高（记住每轮 `expanded`，切卡不丢，本轮内用户点开后不再被事件强行收起）。
3. 数据零迁移：不改 jsonl 落盘结构（`assistant.ToolCalls + tool消息` 已足够归并），纯前端按 `toolCallId` 聚合；后端不动。

## 3. 详细设计

### 3.1 轮次聚合（前端 computed，不动后端）

新增 `turnGroups: Computed<TurnGroup[]>`（`MessageTimeline.vue` 或抽 `utils/turnGroup.ts` 纯函数 + 单测）：

```ts
interface TurnGroup {
  key: string;                 // 首条 assistant id
  assistantId: string;
  reasoning: string | null;
  content: string | null;
  toolCalls: { id: string; name: string }[];
  toolMessages: UnifiedChatMessage[];  // role==='tool' 且 toolCallId 命中本组
  telemetry: { inputTokens; cachedTokens; outputTokens } | null;
  createdAtUtc: string;
}
```

聚合规则（按 jsonl 顺序扫一遍）：

1. 遇 `assistant` 起新组；随后连续 `tool` 消息若 `toolCallId` 在本组 `toolCalls[].id` 中（或即使 `toolCallId` 为空但紧邻，也归入最近组，兼容老数据），则并入本组 `toolMessages`，不再作为顶层消息渲染；
2. `user/system` 切断分组（各为顶层）；
3. 流式快照（见 3.3）在轮末以同样结构追加为一组，保证“刚结束的轮”与“刷新后读盘的轮”长得一样。

### 3.2 容器视图（持久区 L23-63 重构）

```html
<div v-for="g in turnGroups" :key="g.key" class="space-y-2">
  <!-- assistant 正文（永远可见，不进折叠） -->
  <div v-if="g.content" class="markdown-body ..." v-html="renderMarkdown(g.content)" />
  <!-- 思考+工具归并舱（默认收缩） -->
  <div v-if="g.reasoning || g.toolMessages.length" class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70 overflow-hidden" data-testid="turn-thinking-tools">
    <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="toggleTurn(g.key)">
      <span>🧠 思考与工具调用 · {{ g.toolMessages.length }} 个工具{{ turnDuration(g) }}</span>
      <span>{{ isTurnExpanded(g.key) ? '点击折叠' : '点击展开' }} + ChevronDown</span>
    </div>
    <n-collapse-transition :show="isTurnExpanded(g.key)">
      <div class="px-3 pb-2 space-y-2 max-h-72 overflow-y-auto">
        <div v-if="g.reasoning" class="whitespace-pre-wrap ...">{{ g.reasoning }}</div>
        <ToolCallCard v-for="t in g.toolMessages" :key="t.toolCallId" :card="toCard(t)" :default-collapsed="true" />
        <!-- 无 ToolCallCard 富信息时的降级：tool 灰块原文（保证老数据/超长输出仍可看） -->
      </div>
    </n-collapse-transition>
  </div>
  <div v-if="hasTelemetry(g)" class="telemetry footer">…</div>
</div>
```

- `toCard(t)`: 将持久 `tool` 消息映射为 `AgentToolCard`（`callId=toolCallId, functionName=name, argsJson=从同组 assistant.toolCalls 同 id 取 arguments, resultText=content`，`status=done`），复用 `ToolCallCard` 富展示（含复制/耗时位；耗时无则隐藏）。
- `isTurnExpanded` 默认 `false`（与现状默认展开相反，**这是用户核心诉求**）；`toggleTurn` 写入 `expandedTurns: Record<key, boolean>`（`reactive`，切卡保留在内存即可，不落盘）。
- 例外：若某组 `toolMessages` 中有 `error`（内容含 `[Tool Error`），该组首次渲染时 header 右侧加红点 `有失败`，但仍默认收缩（只提示不打扰）。

### 3.3 流式 → 持久的移交（`agentStore.finishTurn`）

- `finishTurn(sid)` 在清空前对本桶 `toolCards` 做快照 `lastTurnSnapshot = { toolCards: [...], reasoning, text }`，随 `TurnCompleted` 事件经 `haloEventBus.emitLocal('agent:turn-archived', { sessionId, snapshot })` 通知时间轴；`MessageTimeline` 收到后若 `persistedMessages` 尚未包含（轮末拉取延迟），先用快照渲染归并舱占位，待 `loadSessionMessages` 到达后自动被真实 `turnGroups` 替换（以 `callId` 集合相等判替换完成，避免闪烁）。
- 03 篇 `finishTurn` 清 `toolCards` 的动作保留，04 篇在其之前加“快照”一步，两篇无冲突（03 管清，04 管快照）。
- 进行中（`isGenerating`）的本轮容器同样默认收缩工具组（复用 03 篇 `active-turn-container` + `defaultCollapsed=true`），思考行只显示 `思考中… · 已调用 N 个工具`，不展开输出。

## 4. 施工步骤

1. 抽 `utils/turnGroup.ts`（`buildTurnGroups(messages): TurnGroup[]` 纯函数）+ 3 个单测（单 assistant 无工具 / assistant+2tool / toolCallId 为空紧邻归组）。
2. `ToolCallCard.vue`：确认 `defaultCollapsed` prop（03 篇已加）+ 持久映射 `toCard` 时 `expanded=false`。
3. `MessageTimeline.vue`：持久区改 `v-for="g in turnGroups"`；删旧 `collapsedReasoning`（或迁移为 `expandedTurns`，注意默认反转）；流式区接 `turn-archived` 快照占位。
4. `agentStore.ts`：`finishTurn` 加快照 + 事件；`TurnCancelled/Error/Truncated` 同样快照（用户同样不想看全量工具刷屏）。

## 5. 验收清单

- [ ] 任意历史轮次默认只见正文 + `思考与工具调用 · N 个工具` 一行；点 header 展开后见 reasoning 全文 + N 个工具卡（可再逐个展开看输出）。
- [ ] 轮末瞬间：流式容器收起，快照占位与随后到达的持久组无缝替换（无双影、无闪烁超过 1 帧）。
- [ ] 新轮进行中：工具组默认收缩，header 计数 `0→N` 递增，正文在下方正常流式，不被工具输出顶走。
- [ ] 失败工具所在组 header 有红点提示；`vue-tsc -b && vite build` 通过。

## 6. 风险与回退

- `toolCallId` 为空的老数据靠“紧邻归组”兜底，误归概率低（`assistant(toolCalls)+tool*` 原子组与 `TokenBudgetService.GroupIntoAtomicTurns` 同语义，前后端一致）。
- 若用户坚持“某类工具默认展开”（如 `exec_command` 看日志），加白名单 `autoExpandTools = ['exec_command']` 一行配置即可，本次默认全收，白名单列为可选，不列入必验；回退即恢复 `collapsedReasoning` 旧逻辑 + 顶层 `tool` 灰块，删除 `turnGroups` 一处引用即可。
