# need21-02 目标架构与契约

> 本篇定义重构终态：数据流、模块职责、状态归属、顺序/键/testid 三契约。实现细节（markdown/懒挂载/滚动）见 03，store diff 见 04。

---

## 1. 目标数据流（单轨）

```text
┌──────────── 后端 ────────────┐
│ AgentOrchestrator            │  textAcc/reasoningAcc/pendingTools 照旧
│ StreamToRowProjector         │  Text/Reasoning/Tool 行分离照旧（state=streaming）
│ RowProjectionStore           │  帧出口照旧；C# 侧 ToolCallArgs 节流至 ≥150ms（唯一后端改动，见 §5）
└──────────────┬───────────────┘
               │ subscribeProjection(onFrame/onResubscribe)
┌──────────────▼───────────────┐
│ projectionFrameQueue         │  rAF 合并一帧 + prioritizeDeltas + coalesceDeltas（保留）
└──────────────┬───────────────┘
┌──────────────▼───────────────┐
│ projectionStore (Map行表)    │  唯一真相源；只增量 upsert，不重建数组
└──────────────┬───────────────┘
┌──────────────▼───────────────┐
│ buildTurnUnits (增量)        │  输入 rows，输出 TurnUnit[]；仅末 turn 重算
└──────────────┬───────────────┘
┌──────────────▼───────────────┐
│ MessageTimeline(单分支)      │  turn级懒挂载 + pill（轻量状态，无body）
│  └─ TurnUnitView            │  blocks 定序分发
│      ├─ TextBlockView       │  流式纯文本 / 终态一次markdown
│      ├─ ReasoningBlock     │  保留（默认收起，自动收起逻辑保留）
│      └─ ToolGroupCard      │  Header常驻 / Body进视口挂载
└──────────────────────────────┘
```

删除的数据流：`llm:stream-event → streamingText/toolCards → active 正文区`；`projection:applied → syncMirror → sessionMessages → timelineItems`。

## 2. 模块职责（重构前后对照）

| 模块 | 重构前 | 重构后 |
|---|---|---|
| `projectionStore` | 快照+帧存储（数组快照，`rowsOf` 返回数组） | **唯一真相源**：`Map<rowId,Row>` + `order[]` + `seq`（见 04 §1） |
| `agentStore` | 瞬态渲染态（累积串+卡）+ 遥测 + 控制 | 只留**遥测 + 控制**（`isGenerating/streamingActive/telemetry/turnNotice/lastError/subAgents`）+ pill 计数；`handleTurnEvent` 保留，`llm:stream-event` 只记 `liveEst`（4Hz 上限） |
| `workspaceStore` | 会话管理 + 投影订阅 + 兼容镜像 | 会话管理 + 投影订阅；**删除 `syncMirror` 与 `sessionMessages` 渲染用途**（标题/导出/分支/重放保留，走按需 `loadSessionMessages`） |
| `MessageTimeline` | 三分支 + 双数据源 | 单分支（TurnUnit）+ pill；数据源只 `projectionStore` |
| `buildTurnUnits` | 全量纯函数 | 增量（缓存 `seq/lastRowId`，仅末 turn 重算，见 04 §4） |
| `MarkdownStream` | 流式全文 markdown | 两档（见 03 §1） |
| `useTimelineScroll` | 无条件跟随 | 贴底门控（见 03 §4） |

## 3. 状态归属表（渲染只读左列）

| 状态 | 所有者 | 渲染读取 | 备注 |
|---|---|---|---|
| 行（text/reasoning/tool/progress） | `projectionStore` | `TurnUnitView` 系 | 原地 upsert，引用稳定 |
| turn 运行态 | 行 `header.state`（无 header 时 `visible.some(isRunningWork)`，`buildTurnUnits` L270-276 逻辑保留） | `TurnUnitView/TailMarkers` | 不变 |
| 遥测 | `agentStore.telemetry` + 行 `extra.telemetry` | 轮尾遥测行 + 顶栏 Σ | 不变 |
| pill（工具数/已收字数/耗时） | 新建 `useLiveStatus`（从 streaming 行派生，2Hz） | pill | 不经过 agentStore 累积串 |
| 用户输入气泡 | 投影 `userInput` 行（+ 乐观 ghost 行保留） | `UserBubble` | `pushOptimistic` 保留，2s 无确认只重订阅不重发 |
| 图片缩略图 | `useImageThumbs` 缓存 | `UserBubble/ToolCallCard` | 不变 |

## 4. 三契约（视觉/交互不变的锁）

### 4.1 顺序契约

* 输入输出均按 `rowId` 升序；`assembleBlocks`（`buildTurnUnits.ts` L193-213）是唯一组装点，不做任何重排；`toolCall` 相邻性由其保证，`groupToolRows`（L166-187）`canMerge + 20 上限` 规则不变。
* 每 turn 块序列形如：`[text?][reasoning?][toolGroup+]…` 按实际到达序，禁止把 `toolGroup` 顶到 `text` 之前（单测锁死，见 05 P0）。
* CUA 分组视觉分段（`TurnUnitView` `needsDivider`，`assistantResponseId` 变化处加线）保留。

### 4.2 键契约（`projectionRows.ts` R1/R4 收紧为强制）

* `text/reasoning/subagent` 用 `rowId`，`toolGroup` 用 `gkey`（`assistantResponseId ?? turnId:rowId`），全仓禁 `index` key（含 `textPair` 的 `pair-` 键保留）。
* `rowId` 由后端分配，前端永不自增；历史派生走 `deterministicRowId`（保留）；`removed` 永不复用。

### 4.3 选择器与行为契约

* `data-testid`：`message-timeline / reasoning-block / tool-group-card / tool-call-card / markdown-stream / active-turn-pill（新 pill 沿用 `active-turn-container` id 做兼容别名，见 05 P1）/ msg-images 等**一个不改**。
* `data-row-id / data-kind / data-status / data-gkey / data-count / data-has-failure` 保留。
* 折叠默认态保留：`ReasoningBlock` 默认收起 + 终态自动收起；`ToolGroupCard` 默认收起（`collapsedByDefault: true`）；`ToolCallCard` `isFileEdit/isExec` 展开其余折叠；有失败自动展开并 `只看失败`（保留）。
* 右键菜单（复制/重放/分支/删除）行为不变；复制文本源从 `turnMergedTextOf` 切到 `mergeTurnTextFromBlocks`，P1 验收逐字 diff（见 06 §2）。

## 5. 后端改动（唯一允许的一项，可选但推荐）

* `ToolCallArgs` 发布节流全量化：C# 侧文件写已有 150ms 节流（`AgentOrchestrator.cs` L498-501 `isFileWrite ? throttleHit : true`），改为**全部工具统一 150ms 节流**（`p.LastPublishTick` 复用）。落盘 `ArgsBuilder` 全量不受影响，只是前端收到片数减少。
* 不动：`TextDelta/ReasoningDelta` 粒度（由前端 rAF 合并消化）、落盘 `textAcc/reasoningAcc`、用量统计、25 轮上限与 `TurnTruncated` 文案。
