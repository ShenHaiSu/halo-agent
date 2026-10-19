# need13 施工方案总览（主子代理 Main/Sub Agent：寄宿子会话 + Barrier 等待 + 可配开关 + 可解释拒绝）

> 输出路径：`docs/need13/`；方案先行，代码按篇施工。
> 基线：`need08` 已落地时刻的代码（`AgentConfigDocument{ExecDefaultTimeoutMs/ExecMaxTimeoutMs/MaxIterations}+AgentConfigService(原子写+.bak+corrupt保留+fallbackReason)+AgentBehaviorView(A/B/C三卡)+ToolRegistry(10工具)+AgentOrchestrator(ReAct循环+轮首快照+Heal+预算裁剪)+WorkspaceSessionService/SessionRepository(messages.jsonl+meta+sessions-index+_sessionLocks)+HostBridgeFacade.StartAgentTurn/CancelAgentTurn(agent:sessionId单CTS)+AskUserGate(sessionId单pending)+LlmGateway双协议无状态路由`）。
> 目标读者：后端 + 前端施工同学；按本文顺序施工即合闸。
> 前置讨论结论：同会话第一轮 `resp`、第二轮 `chat`（及反向）**双向可直接切换、无需迁移**（见 `01 §1`）；主子代理按本系列文档落地。

## 1. 一句话目标

用户手动建的会话都是 `MainAgent`；`Main` 通过工具调用 `spawn_subagent` 创建上下文干净、权限更小的 `SubAgent`；`Sub` 独立 `sessionId` 但寄宿于主下（级联删除/级联取消/聚合展示）；`Sub` 以正常文本总结汇报，主收齐全部子结果才继续（`Barrier` 永远等待，仅用户取消可掐断整树）；子代理总开关/配额/权限子集进**设置与诊断中心 `智能体行为(agent)` Tab** 只加卡片不加 Tab；关闭或超限时 `tool` 仍可见但调用被拒，主可转述原因给用户。

## 2. 文档拆分（必读顺序）

| # | 文档 | 回答的问题 | 主要触点 |
|---|---|---|---|
| 1 | `01-requirements-analysis.md` | 做什么/不做什么；双协议切换为什么可行（证据链）；主子需求逐条冻结；可配置项清单 | 全局；需求冻结依据 |
| 2 | `02-backend-data-model-and-storage.md` | 父子会话怎么存、怎么查、怎么删；`SessionMetadata` 扩展；`sessions-index` 兼容；附件/历史/标题规则 | `WorkspaceModels`、`SessionRepository`、`WorkspaceSessionService` |
| 3 | `03-backend-tools-and-permission-model.md` | `spawn_subagent` 定义；`ToolContext` 角色；`ToolRegistryFactory`；skill/memory/ask/套娃四处鉴权；`[CODE]` 话术 | `ToolContext/IToolHandler/ToolRegistry`、`SkillToolShared`、`AskUserTool`、`SystemPromptBuilder` |
| 4 | `04-backend-coordinator-and-orchestrator.md` | `SubAgentCoordinator`；`Orchestrator` 分流；`Barrier/WhenAll`；`CancelTree`；`Reconcile`；用量归因 | `AgentOrchestrator`、`HostBridgeFacade`、`UsageMetricsService`、`JsEventDispatcher` |
| 5 | `05-backend-config-and-bridge.md` | `agent.json` 7 字段扩展；校验/恢复/快照；Bridge 新增方法；`mock` 对齐 | `AgentBehaviorModels/Service`、`HostBridgeFacade.Agent/SubAgent`、`hostBridge.ts` |
| 6 | `06-frontend-settings-and-diagnostics.md` | `智能体行为` D/E 卡长什么样；store；主会话子进度；诊断只读表 | `SettingsCenterDrawer`、`AgentBehaviorView`、`agentConfigStore`、`agentStore/workspaceStore` |
| 7 | `07-integration-test-plan.md` | 怎么证明做对了（单测+前端+手工矩阵+合闸命令+回滚） | `tests/HaloAgent.Tests`、`vue-tsc/vite/dotnet`、手工矩阵 |

依赖关系：`01 → 02/03/04/05 → 06 → 07`。`01` 未冻结不得开工后端；单测未绿不得写前端；禁止跳过 `07` 合闸。

## 3. 范围一句话（详情见 01）

- **做**：`SessionMetadata` 加 5 字段（扁平寄宿）；`spawn_subagent` 工具；角色过滤 registry；`Coordinator+Barrier+CancelTree`；`agent.json` 加 7 字段 + D/E 卡；`[SUBAGENT_*]/[PERMISSION_DENIED]` 可解释拒绝；子用量 rollup；孤儿子自愈。
- **不做**：按工作区/会话/模型多套覆盖；子再套娃（深度锁 1）；子直连 `ask_user`（v1 锁定禁）；本轮热切换配置；子独立侧边栏会话项（默认只主可见）；向量检索/跨会话记忆共享。

## 4. 全局施工原则（七篇通用，冲突以各篇“精确到行”为准）

1. **零破坏只增量**：不动 `messages.jsonl` 行格式、不动 `prompt.json/providers.json/network/storage` 语义；`AgentOrchestrator(int maxIterations=25)` 签名保留；`ToolResult` 签名不变；老 `agent.json` 缺字段即回默认不算损坏。
2. **下一轮生效**：配置保存后下一轮 `RunAgentTurnAsync` 轮首快照起生效；执行中本轮（含 barrier 等待中）不受影响；`fallbackReason` 横幅常驻。
3. **失败回默认不断会话**：文件缺失/空/损坏/超限一律回退默认 + 横幅，会话可正常发起；`Save` 一律 `Copy→.bak→原子写`。
4. **可见但可拒**：`spawn_subagent` 常显（关闭时 description 追加 `[当前已禁用]`），调用走网关式强拒绝 + `[CODE]` 前缀，主下一轮必见可转述；禁止静默隐藏导致不可解释。
5. **永远等待仅取消可破**：`Barrier=Task.WhenAll` 无超时；`CancelTree` 是唯一出口；取消语义与现有 `TurnCancelled + 部分落盘` 一致。
6. **可测性**：后端 `dotnet build + dotnet test`；前端 `vue-tsc -b && vite build`；`data-testid` 清单见 `06 §5`；`07` 全表跑通方可合闸。

## 5. 强制施工顺序

```text
Step A 冻结 01（双协议证据 + 主子需求 + 配置清单 + 边角表全员评审）
Step B 后端 02（SessionMetadata+Repository+Service 父子链）
Step C 后端 03（ToolContext角色+RegistryFactory+四处鉴权+话术）
Step D 后端 04（Coordinator+Orchestrator分流+Barrier+CancelTree+用量）
Step E 后端 05（agent.json 7字段+校验+reset+Bridge+mock）
  └─ 跑：dotnet build + dotnet test（新增 Stage13* 全绿，老用例零修改全绿）
Step F 前端 06（store+Drawer D/E卡+主会话子进度+诊断表）
  └─ 跑：vue-tsc -b && vite build
Step G 联调 07（H1-H16 手工矩阵 + 双端一致 + 合闸命令）
```

## 6. 验收总表（详情跳转各篇）

- [ ] 01：双协议双向切换证据闭环；主子 5 要素（干净/小权限/独立id寄宿/文本汇报/ barrier+级联取消）无歧义；配置 7 字段默认值冻结；边角表有明确预期。
- [ ] 02：老会话读出即 main；父子建/查/删/级联删正确；`SessionHash` 独立；附件隔离；标题不污染。
- [ ] 03：子写 skill/memory 拒、子 ask 拒、套娃拒、关闭时主 spawn 拒且未建会话；`[CODE]` 话术逐字对；prompt 预声明渲染正确。
- [ ] 04：多子 barrier 收齐才继续；超限部分拒先到者跑；取消掐断整树；孤儿子自愈；用量分桶正确。
- [ ] 05：`agent.json` 缺字段兼容、超限拒、`ask=true` 归一 false、按项恢复只回该项、下一轮生效。
- [ ] 06：D/E 卡位置/控件/预设/警告/`data-testid` 全对；mock 与真后端一致；1020px/94vw 无横溢。
- [ ] 07：`dotnet + vue-tsc + vite` 全绿；H1-H16 全过；回滚路径验证通过。
