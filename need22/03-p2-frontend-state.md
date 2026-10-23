# need22-03 P2 前端状态与交互收敛

> 等级：P1。前置：P1（后端 `TURN_IN_PROGRESS` 等明确错误码到位后联调更稳）。

## 1. 前置检查

- [ ] 复现双影：流式期持久 turn 与 `activeTurn` 累积串同时可见（`MessageTimeline.vue:84,189` + `agentStore.ts:149,189`）。
- [ ] 复现竞态：快速切换会话，`getSessionMessages` 后到覆盖当前视图（`workspaceStore.ts:243` + `agentStore.ts:276,607`）。
- [ ] 复现错票：A 会话问询未决时切 B 会话，`AskUserModal.pending` 显示归属（`AskUserModal.vue:124,233` + `askUserStore.ts:82`）。

## 2. 既定目标

1. 单一真相源：`turns` 只有一份，`activeSessionId` 只算一次，删除 `hiddenToolCallIds` 式去重。
2. 请求幂等 + 可取消：`sendPrompt` 带 `requestId`；`select/load/heal/budget` 带取消令牌。
3. 问询不错票、不丢票：按 `modalSessionId` 归属，后台问询有全局提示。
4. 中断语义清晰：`stopGenerating` 等后端确认后再清本地态。

## 3. 改动清单

| # | 文件 | 现状 | 改为 |
|---|---|---|---|
| F1 | `stores/agentStore.ts:149,189`、`stores/workspaceStore.ts:22`、`components/chat/MessageTimeline.vue` | `agent.turns` 瞬态 + `workspace.sessionMessages` 持久双轨，`activeSessionId` 各算一次，靠 `hiddenToolCallIds` 去重；`HistoryTocPanel.vue:105` 与 Timeline 各算一遍 `buildTurnGroups` | 收敛 `TurnStore`（或 `agent.turns` 为唯一真相，`sessionMessages` 只做持久投影）；`activeSessionId` 提升到单一 `sessionStore`；`buildTurnGroups` 只算一次并共享 |
| F2 | `stores/agentStore.ts:656,659` | `sendPrompt` 仅 `isGenerating` 互斥，无 `requestId` | `sendPrompt` 生成 `requestId`（UUID），后端回显，前端去重；连点第二发直接返回既有 `requestId` |
| F3 | `stores/agentStore.ts:276,607,624`、`stores/workspaceStore.ts:243` | `select/load/heal/budget` 三连无取消令牌，后到覆盖 | 引入 `AbortController`（每次 `selectSession` 递增 epoch，旧请求 abort）；视图只接受当前 epoch |
| F4 | `components/chat/AskUserModal.vue:124,233`、`stores/askUserStore.ts:82` | `pending` 优先当前会话而非 `modalSessionId`，A 问卷切 B 易错票 | 问询按 `sessionId` 归属渲染；`modalSessionId` 为准；非当前会话问询在顶栏 badge 提示，可一键跳回 |
| F5 | `stores/agentStore.ts:535,619,730` | `stopGenerating` 本地清 `pending` 不等后端；`finishTurn` 失败仍清空流式卡 | `stop` 改为 await 后端 `TurnCancelled` 事件后再清；`finishTurn` 失败保留流式卡 + 错误 toast + 重试按钮 |
| F6 | `views/WorkbenchView.vue:3`、`components/workbench/WorkbenchTopBar.vue` | 问询/`barrier` 直接 `contenteditable=false` 全屏遮罩，`mask-closable=false` 且取消=取消整轮 | 问询改为非阻塞右侧栏（可继续浏览历史）；取消分两级：取消问询 vs 取消整轮，文案明确 |

## 4. 验收标准与验收方法

| 验收项 | 标准 | 方法 |
|---|---|---|
| C1 无双影 | 流式期正文只出现一次 | 双协议（chat/responses）各一轮录屏 + DOM `data-row-id` 去重断言 |
| C2 无竞态 | 快切 5 次会话，视图与顶栏 session 一致 | 自动化快切脚本 + `activeSessionId` 断言 |
| C3 幂等 | 连点发送只产生一轮 | 双发脚本 + 后端轮数计数 |
| C4 问询 | A 问询切 B 不错票，切回可继续 | 双会话问询 fixture + 归属断言 |
| C5 中断 | 取消后无残留流式卡，失败有重试 | 取消/断网两组用例 |

## 5. 回滚

* F1 独立分支（改动面最大），其余 F2–F6 可独立合入；F1 回滚即恢复双轨 + `hiddenToolCallIds`。
