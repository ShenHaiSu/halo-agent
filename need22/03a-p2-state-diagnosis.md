# need22-03a P2 前端状态诊断与代码证据

> 只摆证据。路径前缀 `develop/src/Web.Client/src/`。

## 1. 多真相源：双影的结构性原因

| 位置 | 现状 |
|---|---|
| `stores/agentStore.ts:149,189` | `turns[sid]` 瞬态（流式） |
| `stores/workspaceStore.ts:22` | `sessionMessages` 持久（落盘回放） |
| `components/chat/MessageTimeline.vue:84,189` | 两套同时渲染，靠 `hiddenToolCallIds` 去重（`agentStore` 别名 `activeTurn` 兼容） |
| `composables/useTimelineGroups.ts` vs `utils/turnGroup.ts` | `HistoryTocPanel.vue:105` 与 Timeline 各算一遍 `buildTurnGroups`，口径可漂移 |
| `stores/askUserStore.ts:82`、`subSessionStore.ts:5`、`uiStore`（`jumpLock`）、`PromptComposer`（`savedDrafts/thumbnailCache`） | 问询/子会话/滚动锁/草稿各自为政，无统一 `sessionEpoch` |

后果：流式期持久 turn 与 active 累积串同时可见（正文出现两次）；目录与正文乱序。

## 2. 竞态：无取消令牌

| 位置 | 现状 |
|---|---|
| `agentStore.ts:276,607,624,659` + `workspaceStore.ts:243` | `select/load/heal/budget` 三连无 `AbortController`；快速切换后到的 `getSessionMessages` 覆盖当前视图 |
| `agentStore.ts:656 sendPrompt` | 仅 `isGenerating` 互斥，无 `requestId` 幂等；连点产生两轮 |
| `AskUserModal.vue:124,233` | `pending` 优先当前会话而非 `modalSessionId`；A 问卷切 B 易错票 |
| `agentStore.ts:624 refreshAfterTool` | 后台直接 `return` 跳过对账，切回靠 `selectSession` 再拉，窗口期不一致 |

## 3. 中断语义不清

| 位置 | 现状 |
|---|---|
| `agentStore.ts:535,619,730` | `stopGenerating` 本地清 `pending` 不等后端；`finishTurn` 失败仍清空流式卡，无重试 |
| `WorkbenchView.vue:3` + `WorkbenchTopBar.vue` | 问询/`barrier` 直接 `contenteditable=false` 全屏遮罩，`mask-closable=false` 且取消=取消整轮，误触代价高 |
| `stores/workspaceStore.ts:549` | `budget/refreshAll` 失败静默 |

## 4. 定级

| 等级 | 项 |
|---|---|
| P1 阻塞 | 双轨双影、快切覆盖、问询错票 |
| P2 一般 | 全屏遮罩、中断不等后端 |
