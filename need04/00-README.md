# need04 施工方案总览（会话时间链路 / Markdown 排版 / 流式布局统一 / 思考+工具归并收缩 / 上下文口径修正）

> 输出路径：`docs/need04/`；只做方案，不改代码。
> 基线：2026-10-15 时刻实际代码（`WorkspaceSessionSidebar.vue`、`MessageTimeline.vue`、`ToolCallCard.vue`、`workspaceStore.ts`、`agentStore.ts`、`WorkbenchTopBar.vue`、`main.css`、`WorkspaceSessionService.cs`、`SessionRepository.cs`、`TokenBudgetService.cs`、`AgentOrchestrator.cs`）。
> 与 `docs/need02/need03` 关系：01 篇是 need02-03 / need03-01 的**第三次收敛**（前两次只修了展示层，这次修“记录链路”）；其余篇为新增闭环。

## 需求一览与文档拆分

| # | 用户原话要点 | 方案文档 | 主要触点 |
|---|---|---|---|
| 1 | 新建会话/会话内交互后，会话卡时间仍显示“时间未知” | `01-session-time-record-fix.md` | `WorkspaceSessionService.Create/Append` L183-312、`SessionRepository`、`hostBridge.SessionMetadata` L272-286、`workspaceStore.loadSessions/appendMessage/createSession`、`agentStore.finishTurn/sendPrompt`、`timeFormat.ts` |
| 2 | Markdown 缺样式：有序/无序列表、表格框线、分割线间距、标题加粗字号 | `02-markdown-body-style.md` | `MessageTimeline.vue` L44-48/L103-107/L216-228、`main.css` L130-137、`markdown-it` 配置 L158-172 |
| 3 | 一轮中 tool 结果先在上方、写正文、写完又跳上方、下拉框又到下方，非常割裂 | `03-turn-stream-layout-unify.md` | `MessageTimeline.vue` L83-111、`agentStore` 事件 `ToolCallStarted/Updated/ToolExecutionCompleted/AssistantMessagePersisted` L268-362、`refreshAfterTool` L410-421 |
| 4 | 一轮结束应把 tool 调用合并进思考容器、默认收缩，不再全量展开 | `04-thinking-tool-collapse.md` | `MessageTimeline.vue` L23-63（持久态）+ L83-100（流式）、`ToolCallCard.vue` 默认展开 L141-142、`AgentOrchestrator` 持久化 L399-528 |
| 5 | 上下文用量累积错误：`IN 48083 cache 47601 OUT 3721` 是当前会话总量，不应每轮累加 | `05-context-usage-calc-fix.md` | `TokenBudgetService.EstimateMessageTokens/CalculateBudgetStatus` L95-123/L279-308、`WorkbenchTopBar.vue` L205-217、`MessageTimeline.vue` L57-62/L121-128、`agentStore.telemetry` |

## 全局施工原则（五篇通用）

1. **零破坏**：不动 C# DTO 字段名与 JSON 结构（只允新增可选字段/事件载荷）；Naive-UI 2.45.3 + CefSharp Chrome 109，只用 `var()/flex/grid`，禁用 `:has()/container queries/color-mix()`。
2. **暗亮同测**：凡动样式 `html.dark(:root)` 与 `html.light` 各截一图；硬编码色收敛为 `main.css` 语义变量。
3. **不横溢**：时间轴容器保持 `min-w-0 max-w-full overflow-x-hidden`；长串 `break-all + overflow-wrap:anywhere`；表格外包横向滚动层。
4. **可测性**：沿用 `data-testid`（`session-meta-line/message-timeline/tool-call-card/context-quota-meter-badge/terminal-output`）；前端合闸 `vue-tsc -b && vite build`，后端改动另加 `dotnet build`。
5. **顺序建议**：05（口径先定，避免其他篇引用错误数字）→ 01（链路打底）→ 02（纯 CSS，可并行）→ 03（布局重排）→ 04（归并收缩，依赖 03 的容器）。

## 验收总表（跳转各篇 §验收）

- [ ] 01：新建会话即有合法双时间；发消息后“活跃”秒级刷新且切卡/刷新不丢；脏数据（空/`0001-01-01`）永不显示“时间未知”给新用户，可观测、可迁移（见 01 篇）。
- [ ] 02：`ul/ol/table/hr/h1-h6/blockquote/a/code` 暗亮均可读，表格框线完整、列表符号正确、标题层级一眼可辨（见 02 篇）。
- [ ] 03：一轮内只有一个“本轮容器”，顺序恒为 `思考 → 工具（按 callId 稳定插入）→ 正文`，无上下跳动、无重复渲染（见 03 篇）。
- [ ] 04：轮末工具卡自动并入思考容器并默认收缩；展开后仍可看参数/输出/耗时；历史轮次默认全部收缩（见 04 篇）。
- [ ] 05：顶栏水位 = 会话总量（末轮 `InputTokens` 口径），不再随轮次膨胀；`IN/cache/OUT` 语义与后端一致且三端（持久 footer / 本轮 footer / 顶栏）同源（见 05 篇）。
