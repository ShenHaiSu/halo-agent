# need16 施工方案总览（Goal 工具 + 子会话历史界面 + 智能体行为重设计）

> 输出路径：`docs/need16/`；方案先行，代码按篇施工。
> 基线：`need13` 已落地时刻的代码（`ToolRegistry(14 工具)+ToolRegistryFactory(主/子角色过滤)+SubAgentCoordinator(建会话+Task+Barrier+CancelTree+Reconcile)+SessionMetadata{ParentSessionId/AgentRole/SubStatus/SubLabel/CreatedByCallId/TaskPrompt}+HostBridgeFacade.SubAgent(GetSubSessions/CancelSubAgent/GetSubRuntime)+AgentBehaviorView(A/B/C/D/E 五卡)+WorkbenchTopBar(ws/session 面包屑)+MessageTimeline(轮次归并)+ToolCallCard+workspaceStore/agentStore/hostBridge.ts`）。
> 目标读者：后端 + 前端施工同学；按本文顺序施工即合闸。
> 阅读顺序：`00 → 01 → 02/03（Goal 后端/前端，可并行）→ 04/05（子会话历史前端/后端，可并行）→ 06（诊断中心重设计）→ 07（联调验收）`。

## 1. 一句话目标

1. **Goal 工具**：给主 Agent 新增 `goal_tool`（设定/查询/修改目标 + 里程碑 + 进度），`goal.json` 边车落盘，前端右下角可伸缩浮动窗实时展示目标内容与进度；
2. **子会话历史界面**：主会话界面内给每个 `spawn_subagent` 调用块加上“进入子会话”入口，点击后切换到全新的只读历史界面，顶部面包屑从 `ws/session` 变为 `ws/session/sub`，可返回；
3. **智能体行为重设计**：把设置与诊断中心 `智能体行为(agent)` Tab 从 A–E 纵向长表单重做为“配置—监控—诊断”三区信息架构，修对齐/密度/空态/暗色一致性问题。

## 2. 文档拆分（必读顺序）

| # | 文档 | 回答的问题 | 主要触点 |
|---|---|---|---|
| 1 | `01-requirements-analysis.md` | 做什么/不做什么；三需求逐条冻结；术语与边角表 | 全局；需求冻结依据 |
| 2 | `02-goal-backend.md` | `goal_tool` 定义；`goal.json` 存储；权限模型；Orchestrator/提示词注入；Bridge+事件 | `Tools/BuiltIn/GoalTool.cs`、`Services/GoalService.cs`、`Core/Models/SessionGoal.cs`、`SessionRepository`、`SystemPromptBuilder`、`HostBridgeFacade.Goal.cs` |
| 3 | `03-goal-floating-window.md` | 浮动窗交互（展开/收缩/mini）；`goalStore`；与时间轴/TopBar 的布局关系；暗色/响应式 | `components/goal/GoalFloatPanel.vue`、`stores/goalStore.ts`、`hostBridge.ts`、`WorkbenchView.vue` |
| 4 | `04-subsession-history-frontend.md` | 面包屑三段式；主→子路由/状态切换；只读历史复用；入口清单（ToolCallCard/诊断表/barrier 条） | `WorkbenchTopBar.vue`、`MessageTimeline.vue`、`ToolCallCard.vue`、`stores/subSessionStore.ts`、`WorkspaceSessionSidebar.vue`（不动） |
| 5 | `05-subsession-history-backend.md` | 子历史查询 API 补齐；详情 DTO；只读语义与并发守卫；用量归因 | `WorkspaceSessionService`、`SessionRepository`、`HostBridgeFacade.SubAgent.cs`（扩展）、`UsageMetricsService` |
| 6 | `06-agent-behavior-redesign.md` | 现状痛点逐条；新 IA 与线框；组件拆分；store/bridge 复用；样式规范 | `SettingsCenterDrawer.vue`、`AgentBehaviorView.vue` 拆分、`agentConfigStore.ts`、`UsageAnalyticsView.vue`（参考） |
| 7 | `07-integration-test-plan.md` | 怎么证明做对了（单测+前端类型+手工矩阵+合闸命令+回滚） | `tests/HaloAgent.Tests`、`vue-tsc`/`vite`/`dotnet`、手工矩阵 |

依赖关系：`01 → (02,03 可并行；04,05 可并行) → 06（可与 02–05 并行设计，但施工建议在 04 之后以复用子会话组件）→ 07`。`01` 未冻结不得开工；后端单测未绿不得写前端；禁止跳过 `07` 合闸。

## 3. 范围一句话（详情见 01）

- **做**：`goal_tool` 主写子读；`goal.json` 边车 + 原子写；浮动窗三态；子历史只读复用 `getSessionMessages` + 新增详情接口；面包屑三段；诊断中心 IA 重做（只动 `agent` Tab，不动其它 Tab）。
- **不做**：多 Goal 并存/版本分支；子会话内再发消息（v1 只读）；子再设 Goal（深度锁）；诊断中心其它 Tab 重构；向量检索/跨会话 Goal 共享。

## 4. 全局施工原则（七篇通用，冲突以各篇“精确到行”为准）

1. **零破坏只增量**：不动 `messages.jsonl` 行格式、不动 `agent.json/prompt.json/providers.json` 语义；`ToolRegistry` 老工具零改动；`SessionMetadata` 不加 Goal 大字段（Goal 正文只存边车）；老 `goal.json` 缺失即“未设定”空态。
2. **下一轮生效**：Goal 写入后下一轮系统提示词即见；配置类保存仍走“下一轮生效”；执行中本轮（含 barrier）不受影响。
3. **失败回默认不断会话**：Goal 文件损坏→空态 + 横幅；Bridge 一律 `BridgeResponse<T>` + `CODE` 前缀；前端空态可重试。
4. **可见但可拒**：`goal_tool` 常显；子写 Goal 走网关式强拒绝 + `[GOAL_DENIED]` 前缀，主下一轮必见可转述；禁止静默隐藏。
5. **只读优先**：子历史界面默认只读（禁发/禁截断/禁分支/禁删除），取消子仍走原 `CancelSubAgent`；主历史一切不变。
6. **可测性**：后端 `dotnet build + dotnet test`；前端 `vue-tsc -b && vite build`；`data-testid` 清单见各篇 §5；`07` 全表跑通方可合闸。

## 5. 强制施工顺序

```text
Step A 冻结 01（需求+术语+边角表全员评审）
Step B 后端 02（Goal 模型+Service+Tool+Bridge+事件）+ 前端 03（goalStore+浮动窗）并行
Step C 后端 05（子详情接口+只读守卫）+ 前端 04（面包屑+子历史+入口）并行
Step D 前端 06（诊断中心重设计，复用 B/C 组件规范）
  └─ 跑：dotnet build + dotnet test / vue-tsc -b && vite build
Step E 联调 07（G/H/S 三矩阵 + 双端一致 + 合闸命令）
```

## 6. 验收总表（详情跳转各篇）

- [ ] 01：三需求边界无歧义；边角表有明确预期；术语（Goal/子会话/诊断三区）冻结。
- [ ] 02：主写子读鉴权正确；`goal.json` 原子写+损坏自愈；提示词注入位置正确；事件推送可达。
- [ ] 03：浮动窗三态/拖拽/缩放/暗色全对；`data-testid` 全对；1020px/94vw 无横溢；流式中不跳动。
- [ ] 04：面包屑三段+返回正确；主→子→主状态不丢；只读守卫全对；入口三处（工具卡/诊断表/barrier 条）可达。
- [ ] 05：详情接口字段全；并发/取消语义与 need13 一致；用量分桶正确。
- [ ] 06：新 IA 三区落地；组件拆分完成；样式 token 全对齐；老配置零迁移。
- [ ] 07：`dotnet + vue-tsc + vite` 全绿；G/H/S 矩阵全过；回滚路径验证通过。
