# 06 Transcript 窗口与 Store 切片（P2-1）

第二参考：`contextAndRender/05-render-transcript-store.md`。Vue+Pinia 重写实现。

## 1. 前置检查

- [ ] 通读 `useAgentStream.ts + model.ts + turnEvents.ts`：标出当前 `turns/streamingText/streamingReasoning/telemetry` 的写入点（`flushSegBuf/SEG_FLUSH_MS=100`）与 `haloEventBus` 事件分发。
- [ ] 通读 `useTimeline.ts + useTimelineGroups.ts + TimelineView.vue + turnGroup.ts`：确认当前 `timelineItems` 全量重算、无 history/tail 分离。
- [ ] 确认无虚拟化、无视口门控、无 LRU（`useTimelineNav.ts` 仅有零星 rAF）。

## 2. 阶段目标

- 历史冻结、尾部独活：流更新只动 tail，历史行引用不变。
- 高频流事件 rAF 合批进 Pinia，每秒数十次 set 压到每帧一次。
- 切会话不重建 DOM（KeepAlive + v-show），滚动/搜索态不进全局 store。

## 3. 实现办法

### 3.1 投影与窗口（`features/session/utils/` 新建）

- `transcriptProjection.ts`：WeakMap 快照缓存 + 64 块 diff（`incremental/replaceMessages`，仅 tail 重建，history 引用复用）+ generation 守卫 + `BLOCK_SIZE=64` 冻结索引。
- `transcriptWindow.ts`：`INITIAL_MOUNT=15 / WINDOW_MIN=60 / STEP=40`，mount 先载首屏再 fetch；`transcriptSettle.ts` 3 稳定帧/600ms 上限/160ms 淡出 + 骨架行。
- `transcriptCache.ts`：`Map LRU LIMIT=20` + retained pane `LIMIT=3`；唯一规范写边界 `syncTranscriptProjection`。

### 3.2 Store 合批（改 `useAgentStream/useAgentTurn/useAgentCards`）

- 新建 `frameBatcher.ts`：`Map<key,最新值>` 同 key 合并，`requestAnimationFrame` 合批（无 rAF 回落 16ms），`flushNow()` 给 terminal 事件。
- 事件 key：`message:{sid}:{mid} / tool:{callId}`，同 key 经 `mergeEnvelopes` 合并 delta（文本 append 非替换）；非流事件先 `flushNow`。
- 组件只订叶节点：`storeToRefs` 订 `turns[sid]/telemetry/activity`，禁止整树订阅；搜索/披露态用 provide/inject 下钻，不进 Pinia。
- IPC 侧（`hostBridge` 事件回调）只做分发 patch，不做全量 `messages` 替换；abort 就地结算指标不重拉。

### 3.3 视图与滚动

- `TimelineView` 拆 `TranscriptHistory + TranscriptTail`；`historyEntries` memo 冻结，`tailEntry` 随流更新；key=`message.id/turn.id`；`reuseRef` 保引用（引用不变下游全跳过）。
- `useTimelineNav` 升级双模式：`follow`（跟尾）vs 自由阅读；上翻即 `releaseFollow` + `showJump` 回底部；状态条整轮常占位防抖；分页 `hasMoreBefore/After + onLoadOlder/onLoadNewer` + veil 幕布防抖。
- 会话 pane：`KeepAlive + v-show + aria-hidden`，切会话显隐不重建；隐藏 pane 不追流、不重锚、无视口测量。
- `mergeLiveMessages(full, live)`：重载/删除/回滚后以持久层为准合并增量，禁止用 pre-await 快照覆盖后到的 `message_end/tool_end`。

## 4. 验收标准

- [ ] 性能：200 轮会话流式时历史行零重渲染（Vue DevTools / 计数器验证），tail 帧 ≤16ms。
- [ ] 单测：同 key delta 合并为 append；引用不变时下游 memo 跳过；terminal 事件 `flushNow` 不等下一帧。
- [ ] 行为：上翻不抢滚动，回底部按钮出现；状态条占位无行高抖动；切会话滚动偏移保留、无白闪。
- [ ] 旧全量 `timelineItems` 重算路径删除（破坏性）；`SEG_FLUSH_MS` 定时器由 frameBatcher 接管或删除。
- [ ] 搜索/定位进入 readingWindow 时流状态灯、permission 卡降级隐藏（不对旧消息操作）。

## 5. 风险

- KeepAlive 常驻 3 个 pane 占内存，上限 3 + LRU 20，超限即释，允许。
