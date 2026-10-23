# need21-04 三 store 手术 + 事件批处理（diff 级）

> 本篇是 P1/P2 的照抄执行単位。符号约定：`[-]` 删除、`[+]` 新增、`[~]` 修改。类型定义以 `projectionRows.ts` 为准，不改字段。

---

## 1. `projectionStore.ts`（1012 行）：数组 → Map 行表

### 1.1 状态形状

```ts
// [-] snapshot: { rows: ProjectionRow[] }
// [+] snapshot: { rows: Map<number, ProjectionRow>; order: number[]; seq: number }
```

* `rowsOf(sid)` 返回**只读数组视图**（`order.map(id => rows.get(id))`，缓存 `seq`，同 `seq` 返回同一数组引用 → 下游 computed 不失效）。
* `onFrame/onFrameCoalesced/onSnapshot/mergeRange` 内 `applyDeltas` 语义不变（`coalesceDeltas` L251-330 四规则 R1-R4 原样保留），只是写入目标从数组 splice 改为 Map set + `order` 维护。
* `removed`：Map delete + `order` 剔除，rowId 永不复用（R4，已合规）。
* `optimistic` ghost 行保留（`pushOptimistic`、`OPTIMISTIC_CONFIRM_TIMEOUT_MS=2000` 不变）。

### 1.2 `projection:applied` 载荷追加（只加字段）

```ts
// haloEventBus.emitLocal('projection:applied', { sessionId, seq, kind })
haloEventBus.emitLocal('projection:applied', { sessionId, seq, kind, changedTurnIds: [...] }) // [+]
```

* `changedTurnIds` 供 `buildTurnUnits` 增量判定（§4）与 pill 计数，避免下游全量扫描。

### 1.3 `rowsToMessages` 保留但降级

* 函数保留（标题/导出/分支/`exportSessionMarkdown` 依赖），但**移出热路径**：只在 `loadSessionMessages` 回退链与后台任务调用，不再被 `syncMirror` 每帧调用（`syncMirror` 删除，见 §3）。

## 2. `agentStore.ts`（956 行）：删渲染态，留遥测与控制

### 2.1 删除（渲染用途）

```ts
// [-] AgentTurnState.streamingText / streamingReasoning / toolCards（字段删除）
// [-] llm:stream-event 的 TextDelta/ReasoningDelta 累积分支（L397-414 整段删除）
```

* `AgentToolCard` 类型保留（`ToolCallCard.legacyToRow` 在 P4 删除前仍需它做桥，见 05 P4），但 store 内不再维护实例数组。
* `refreshFromProjection`（L320-349）删除；`handleTurnEvent` 内 `ToolCallUpdated/ToolCallArgs/ToolExecutionCompleted → refreshFromProjection` 三处调用改为 `touch` + pill 计数更新。
* `finishTurn`（L703-716）的三行清空改为只清遥测/状态位；`resetTurnState`（L373）保留（`isGenerating/streamingActive/telemetry` 重置）。

### 2.2 保留（遥测与控制，一字不改）

* `telemetry`（含 `liveEst`）、`isGenerating/streamingActive`、`turnNotice/lastError`、`subAgents`、`turnStartMarks`、`FirstToken/UsageUpdated/TurnCompleted/TurnTruncated/TurnCancelled/TurnError` 全部分支。
* `sendPrompt`（L725-788）流程不变（含乐观 ghost、首帧等待、`loadSessionMessages` 回退）；`cancelAgentTurn`、`replayUserMessage/replayAssistantTurn`、`isCompacting` 不变。
* `llm:stream-event` 保留**最小订阅**：仅 `estTokens(evt.textDelta)` 累加 `liveEst`，4Hz 节流（`performance.now` 差值门），超频直接丢弃（遥测估算允许丢精度）。

### 2.3 新增 `useLiveStatus.ts`（约 50 行，pill 数据源）

```ts
export function useLiveStatus(sessionId: ComputedRef<string>) {
  // 输入：projectionStore.rowsOf(sid) + agentStore.isGenerating
  // 输出（2Hz 刷新）：{ runningToolCount, streamingChars, elapsedMs, phase: 'reasoning'|'acting'|'answering'|'idle' }
  // phase 判定：有 running tool 行 → acting；有 streaming reasoning 无 tool → reasoning；有 streaming text → answering
}
```

* 不读 agentStore 累积串，不订阅 `llm:stream-event`。

## 3. `workspaceStore.ts`（863 行）：删镜像，保留会话管理

### 3.1 删除

```ts
// [-] function syncMirror (L422-431) 整段删除
// [-] resubscribeFromSeq 内 syncMirror(sid)（L451）→ 改为只触发 projection:applied（订阅侧已 emit）
// [-] loadSessionMessages 内两处 sessionMessages.value = rowsToMessages(...)（L531）→ 改为仅投影 onSnapshot，不写镜像
//     例外：回退链（真快照不可用 → messages.jsonl）仍写 sessionMessages（后台任务用），但标注 // non-render
// [-] loadRowRange 内 syncMirror(sid)（L576）→ 删除（range 只进 projectionStore.mergeRange）
```

### 3.2 `sessionMessages` 去留

* 变量保留（`ref<UnifiedChatMessage[]>` L103），但**渲染禁读**：`MessageTimeline` 不再 `storeToRefs(sessionMessages)`，改读 `projectionStore.rowsOf(activeSessionId)`。
* 保留的写者：`loadSessionMessages` 回退链、`appendMessage`（用户消息乐观）、标题生成（L611-635 `generateTitleAfterFirstRound` 读 `sessionMessages` 改为读投影镜像函数，见下）。
* 新增 `sessionSnapshotForTitle(sid)`：按需 `rowsToMessages(proj.rowsOf(sid))` 一次性转换，不进响应式（标题只需首条 user + 首条 assistant）。

### 3.3 订阅链不变

* `bindProjectionSubscription`（L462-496）+ `projectionFrameQueue.enqueueFrame` + `onFrameCoalesced` 全保留；`resubscribeFromSeq`/`ensureProjectionBridge` 不变。
* `refreshBudget` 调用点不变（D组 06 “轮末只刷预算” 纪律保留）。

## 4. `buildTurnUnits` 增量（`utils/buildTurnUnits.ts`，289 行）

纯函数签名不变（单测零改），内部加模块级弱缓存：

```ts
// [+] module cache
let lastKey = '';            // `${sessionId}:${seq}:${rows.length}:${lastRowId}`
let lastUnits: TurnUnit[] = [];
let blockCache = new Map<string, WorkBlock[]>();  // gkey/rowId → blocks（引用复用）

export function buildTurnUnits(rows, opts?) {
  const key = ...;
  if (key === lastKey) return lastUnits;            // 同帧重复调用零成本
  // 仅重算 changedTurnIds 覆盖的 turn（来自 1.2），其余 turn 从 lastUnits 按 key 原样复用
  // toolGroup 复用：gkey 命中 blockCache 且组内 rowId 集合不变 → 直接复用 group 对象
}
```

* `visibleWorkFilter / canMerge / groupToolRows / assembleBlocks / MAX_TOOL_GROUP_SIZE=20 / TOOL_MERGE_WINDOW_MS` 一字不改（顺序契约，见 02 §4.1）。
* `messagesToRows`（`utils/turnUnits.ts` L40-177）P1 起停止被渲染调用，P4 删除；`turnUnits.ts` 的 re-export 保留到 P4（避免中途改 import 链）。

## 5. 事件批处理（前端 + 一个可选后端）

前端（必做，P2）：

* `haloEventBus.on('llm:stream-event')` 内加 rAF 合并：delta 先入队，帧末一次 `liveEst += sum` + 一次 `touch`（沿用 `projectionFrameQueue.scheduleFlush` 模式，新建 20 行小队列或复用该文件导出 `enqueueLight`）。
* `useLiveStatus` 2Hz（`setInterval` 500ms + `onUnmounted` 清理，条件 hook 禁令遵守：hook 无条件调用）。

后端（可选但推荐，唯一 C# 改动）：

```csharp
// AgentOrchestrator.cs L498-501
// [-] bool throttleHit = (now - p.LastPublishTick) >= 150 || ...; if (isFileWrite ? throttleHit : true)
// [+] bool throttleHit = (now - p.LastPublishTick) >= 150 || evt.TextDelta.Length > 4096; if (throttleHit)
```

* 落盘 `ArgsBuilder` 全量不受影响；`ToolCallUpdated`（`handleTurnEvent` L489）收到片数从数十降到个位数。
