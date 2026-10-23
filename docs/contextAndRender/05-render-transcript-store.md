# 高性能渲染（上）：Transcript 窗口与 Store 切片

源码：`apps/desktop/src/components/SessionPane.tsx`，
`features/chat/transcript/ChatTranscript.tsx`，
`features/chat/transcript/hooks/useTranscriptScroll.ts`，
`hooks/use-transcript-view.ts`，`lib/transcript-reading.ts`，
`lib/session-transcript.ts`，`lib/frame-batcher.ts`，
`stores/slices/transcript-slice.ts`，`stores/slices/events-slice.ts`。

## 1. Retained SessionPane：切会话不重建 DOM

`SessionPane.tsx:10-22`（ADR 0137）：

- 每个 retained 会话挂一个 `SessionPane`，`key = sessionId`，DOM/滚动偏移/挂载行窗口终身属于该会话。
- 切会话 = 显隐切换（`data-visible + inert + aria-hidden`），不是把同一 transcript 组件指向另一份数据。消灭了“白闪一下再画”。
- 隐藏 pane 保留 layout box（scroll offset 不丢），但移出可访问树与命中测试；minimap 在隐藏时不测量（否则缓存垃圾偏移，见 `ChatTranscript.tsx:245-250` 注释）。

`paneVisible=false` 时 transcript 不追流、不重锚（`ChatTranscript props.paneVisible`），因为隐藏 scroller 无可视 viewport 可修正。

## 2. `useTranscriptView`：live vs retained 快照

`hooks/use-transcript-view.ts:6-24`：

```ts
live = activeSessionId===sessionId ? state.messages : retainedTranscripts[sessionId]
view = transcriptViews[sessionId] // {focus, parentMessage, restored, hasMoreBefore/After, loading}
messages = transcriptViewMessages(live, view) // 阅读窗口裁剪
historical = Boolean(view.focus || view.restored)
```

- 活动会话读 `messages`（流式投影），非活动读 retained 快照。离开会话不清空 pane（`SessionPane` L19-21）。
- 搜索/定位进入 `readingWindow`：`transcriptRunning = isRunning && !readingWindow`，流状态灯、permission 卡、plan 状态在历史窗口全部降级隐藏，避免对着旧消息操作。

## 3. `useTranscriptScroll`：历史/尾部分离

`AssistantTurn.tsx :: TranscriptHistory + TranscriptTail` + `useTranscriptScroll.ts`（33KB，大文件，核心思想）：

- `historyEntries`（已定稿）与 `tailEntry`（正在流）分离。`collectLiveDelegationEntries(historyEntries, tailEntry, running)` 再用 `reuseReadonlyMap` 保引用（`ChatTranscript.tsx:162-168`）——引用不变则下游 memo 全跳过。
- 滚动：`follow` 模式（跟尾）vs 自由阅读模式。流更新只在 follow 时自动滚；用户上翻后 `releaseFollow`，`showJump`（回到底部按钮）出现，不抢滚动。
- `runtimeStatusLane = transcriptRunning` 常占位（L188-193，issue #323）：整轮期间状态条占位，idle 不渲染，避免行高抖动推走用户正在读的行。
- 分页：`hasMoreBefore/After + onLoadOlder/onLoadNewer`，`loadTranscriptPage(sessionId, "before"|"after")`，`TRANSCRIPT_SKELETON_ROWS` 骨架屏，`veilCovering/veilPhase` settle 幕布防抖动。
- `mergeLiveSessionMessages(full, live)`（`session-transcript.ts`）：重载/删除/回滚后以持久层为准合并流增量，`deleteMessage` 等先读 `loadFullSessionMessages` 再对 post-await store 做合并（#1614 教训：不能用 pre-await 快照覆盖后到的 `message_end/tool_end`）。

## 4. Zustand 切片：细粒度 selector

- `app-store.ts` 按 `session-slice/transcript-slice/events-slice/queue-slice/...` 拆分，组件只订阅需要的叶节点：
  `useAppStore(s => s.runningSessions[id])`、`s => s.latestTurnResults[id]`、`s => s.agentStatuses[id]?.activity` 等（`ChatTranscript.tsx:102-124`）。
- `TranscriptDisclosureProvider(sessionId)` + `TranscriptSearchContext` + `DisclosureAnchorContext` 用 context 下钻搜索/披露状态，不进全局 store，避免整树重渲染。
- `SessionPane` 自身 `memo`，props 只有 `sessionId/visible`；transcript 数据走内部 hook，不经父透传。

## 5. `frame-batcher`：流事件压到 rAF

`lib/frame-batcher.ts:10-63 :: createFrameBatcher(flush)`：

- `Map<key,value>` 同 key 只留最新（同流目标合并），多 key 保插入序。
- `requestAnimationFrame` 合批，无 rAF 环境回落 16ms timeout。`flushNow()` 给 terminal 事件（turn 结束）用，终态不等下一帧。
- 用途：`agent.event` 高频增量（token 流、tool 进度）进 batcher 再 `set` store，把每秒数十次 set 压到每帧一次。`enqueue(key, value, merge?)` 支持同 key 自定义合并（如文本 append 而非替换）。

IPC 侧（`events-slice.ts`）：main→renderer 的 `agent.event` 按 session 分发，只 patch 对应 session 的消息数组，不做全量 `messages` 替换；`settleStoppedAssistantMetrics` 在 abort 时就地结算指标而非重拉。

## 复刻清单

1. 会话 pane 按 id 常驻 + 显隐切换，不要共享 transcript 实例。
2. 历史/尾部分离 + `reuseReadonlyMap` 保引用，历史行 memo 冻结。
3. follow/自由双滚动模式 + 状态条常占位防抖。
4. store 只订叶节点 + context 下钻搜索态。
5. 高频流事件过 `frame-batcher`（rAF 合批 + 终态 flushNow）。
## 6. 增补：投影 / 窗口 / 持久化与 IPC 真相（子代理调研回填）

- 增量投影（`lib/transcript-projection.ts`）：WeakMap 快照缓存 + 64 块 diff（`incremental / replaceMessages`，仅 tail 重建，history 引用复用）+ generation 守卫 owner cursor；`lib/session-transcript-updates.ts` 64 块冻结索引（`BLOCK_SIZE = 64` + id/tool 索引，`registerRewrite / Replacements / Append` 只 bump generation，不扫历史）。
- 窗口（`lib/transcript-window.ts`）：`INITIAL_MOUNT = 15` 首屏 / `WINDOW_MIN = 60` 稳态 / `STEP = 40` 顶部渐进，mount-loaded-first、fetch-second；`lib/transcript-settle.ts` settle 幕布 3 稳定帧 / 600ms 上限 / 160ms 淡出 + 骨架行。
- 缓存（`stores/runtime/session-runtime.ts`）：`sessionTranscriptCache` Map LRU `LIMIT = 20` + `sessionHistoryCache` + `liveSessionTranscripts` Set + retained pane `LIMIT = 3`（`test/session-switch-performance.test.mjs:63` 断言）；`syncTranscriptProjection` 是唯一规范化写边界。
- 流合批真相（`stores/slices/events-slice.ts:113-310`）：`createFrameBatcher` 按 `message:{sid}:{mid}` / `tool:{sid}:{callId}` key 合批，同 key 经 `mergeAgentEventEnvelopes`（`@pi-desktop/shared/message-stream.ts:91`）合并 delta，非流事件先 `flushNow`；`message_update` 走 `applyMessageUpdate`（delta-append / snapshot-replace）+ `upsertLiveSessionMessage`（引用不变返原数组）。
- 平滑文本（`hooks/useSmoothText.ts`）：rAF 自适应 reveal（~60ch/s 基速、积压>30ch 按 500ms 追平、≤60Hz、>32KB 直通、失活/结束 flush）。
- Minimap（`components/ConversationMinimap.tsx`）：仅 overflow + marker 达标挂载；cached offsets + 二分 O(log n) active 跟踪；scroll/resize/hover 全 rAF 节流；magnify 走 CSS 变量命令式写；点击经 `data-minimap-id` 精确跳转。
- IPC/持久化真相：main → renderer `agent.event` 无节流直发（`electron/main/runtime/sidecar.ts:105-220`，remote-bridge 同），频率控制全在 renderer store 侧；transcript 持久化在 main 经 `persistenceOutbox` 按 `message:{sid}:{mid}` 去重 + `inflightCheckpointer`（message_end 先 checkpoint 再 append，D327；agent_end 前 drain 再归档），空 aborted 气泡/陈旧 terminal 直接丢。
