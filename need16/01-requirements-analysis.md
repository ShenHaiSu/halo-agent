# 01 需求分析（Goal 工具 × 子会话历史 × 诊断中心重设计）

> 读者：全员（冻结依据）。后篇一切实现以本篇为准；歧义先改本篇再施工。

## 1. 背景与现状证据

| 需求 | 现状（精确文件） | 缺口 |
|---|---|---|
| 主 Agent 目标管理 | 无 Goal 概念：全仓 `grep goal` 仅命中 `SpawnSubAgentTool.cs:28` 的 task 描述词，无模型/存储/工具 | 需要 `goal_tool` + 持久化 + 前端展示 |
| 子代理可读性 | `SubAgentCoordinator.cs` 建子会话 + `HostBridgeFacade.SubAgent.cs:44 GetSubSessions` 可查列表 + `WorkspaceModels.cs:96-107` 父子字段齐；但前端只能在诊断 E 卡看运行表（`AgentBehaviorView.vue:343-361`），**看不到子历史正文** | 需要主→子历史只读界面 + 面包屑 |
| 诊断中心 | `SettingsCenterDrawer.vue:57-61` agent Tab 挂 `AgentBehaviorView.vue`（695 行单文件，A 说明/B 超时/C 轮次/D 子代理/E 诊断表纵向堆叠），`width:1020/max-width:94vw` 左 Tab 116px；痛点见 06 §1 | 需要 IA 重做 + 布局规整 |

可复用的地基（不重造轮子）：

- 工具体系：`IToolHandler/ToolDefinition/ToolRegistry/ToolRegistryFactory`（主包/子包角色过滤，见 `ToolRegistryFactory.cs:12-43`）。
- 子会话存储：`WorkspaceSessionService.CreateSubSessionAsync/GetSubSessionsAsync/GetSubSessionAsync/DeleteSessionTreeAsync` + `SessionRepository` 的 `sessions-index.json/meta.json/messages.jsonl`（见 `WorkspaceSessionService.cs:27-33`、`SessionRepository.cs:16-45`）。
- 事件：`JsEventDispatcher.Publish("agent:turn-event", …)` + 前端 `haloEventBus`（见 `SubAgentCoordinator.cs:422-429`、`agentStore.ts:552` 子事件分支）。
- Bridge 铁律：所有耗时 IO 返回 `Task<BridgeResponse<T>>`（见 `HostBridgeFacade.cs:19-25`）。
- 前端状态：`workspaceStore`（持久：workspaces/sessions/messages/budget）+ `agentStore`（瞬态：toolCards/telemetry/subAgents）二分（见 `workspaceStore.ts:15-33`、`agentStore.ts:85-99`）。

## 2. 术语冻结

| 术语 | 定义 |
|---|---|
| Goal（目标） | 主会话级的一条自然语言目标 + 可选里程碑列表 + 0–100 进度；**每主会话一条**（v1 不做多 Goal）；子会话无独立 Goal，只读父 Goal |
| goal_tool | 主可写（set/get/update/progress）、子只读（get；写拒）的新工具，`ToolRegistry` 常显 |
| 浮动窗 | 主工作台右下角可伸缩面板，三态：`expanded（完整）/collapsed（标题条）/mini（圆点徽标）`，展示 Goal 内容与进度 |
| 子会话历史界面 | 只读复用主时间轴组件渲染子 `messages.jsonl` 的界面；面包屑 `ws / session / sub(label)`；有“返回主会话” |
| 诊断三区 | 重设计后的 agent Tab：`① 运行配置区 ② 实时监控区 ③ 历史诊断区` |

## 3. 需求逐条冻结

### 3.1 Goal 工具（G1–G7）

- G1 `goal_tool` action 枚举：`get/set/update/progress/add_milestone/complete_milestone/clear`；`set` 全量覆盖，`update` 增量（title/description 任一非空即改），`progress` 只改 `percent+note`。
- G2 数据模型（见 02 §2）：`{ title, description, milestones[{id,title,done,updatedAtUtc}], percent, note, updatedAtUtc, updatedBy }`；`percent` 0–100 越界钳制；`complete_milestone` 自动重算 `percent=done/total*100`（无里程碑时不自动改）。
- G3 权限：主（`AgentRole=main`）全动作；子（`sub`）仅 `get`，其余返回 `[GOAL_DENIED] …` 且**不落盘不抛错**；`ToolRegistryFactory.ForSub` 注册“只读桩”（description 标注禁用，见 02 §3）。
- G4 持久化：边车 `sessions/<ws>/<sess>/goal.json`，原子写（`AtomicFileWriter`），损坏→空态 + 横幅（对标 `agent.json` 回退语义）。
- G5 提示词注入：`SystemPromptBuilder.BuildCore` 新增 `goalBlock`（stable 区，skill/memory 之后、ephemeral 之前），缺 Goal 时不占位；子会话 prompt 额外追加“父 Goal 只读引用”（见 02 §4）。
- G6 事件：写入后 `Publish("goal:updated", { sessionId, goal })`；前端 `goalStore` 订阅刷新 + 浮动窗徽标闪一下（不打断输入）。
- G7 查询面：Bridge `GetGoal/SetGoal` 供前端浮动窗“人工编辑”兜底（用户也可在浮动窗直接改，不必经 LLM）。

### 3.2 子会话历史界面（H1–H8）

- H1 入口三处必做：① `ToolCallCard(spawn_subagent)` 展开态“查看子会话 →”按钮；② 诊断 E 卡/新监控区运行表行内“历史”按钮；③ barrier 等待条“查看子进度”链接。三处统一进同一子历史路由状态。
- H2 面包屑：`WorkbenchTopBar` 左侧由两段变三段：`ws / session / sub:label(shortId)`；第三段可点击下拉切换同父其它子；右侧“← 返回主会话”按钮常显。
- H3 状态模型：前端新增 `subSessionStore`（或复用 `workspaceStore` 扩展字段，见 04 §3 决策）：`{ parentSessionId, activeSubId, subList, subMessages, loading }`；主会话消息/预算/输入舱状态**原位保留**，返回即恢复（不重拉）。
- H4 只读语义：子界面隐藏 `PromptComposer`（或禁用 + 提示“子会话只读”），禁用右键截断/分支/删除（`IsHistoryBusy` 恒真等价），`CancelSubAgent` 仍可用（运行中显示“掐断”）。
- H5 复用：子历史正文复用 `MessageTimeline` 的 `timelineItems` 归并逻辑（抽 `useTimelineGroups` composable 或 prop 注入 messages，避免 fork 两份渲染）。
- H6 空态/错误：子无消息→“子尚未输出”；子 running→顶部进度条 + 自动 3s 轮询 `getSessionMessages`（切回主即停）；失败→重试按钮。
- H7 面包屑短 ID：`subId.slice(-6)` + label（≤20 字）双显，避免长 ID 撑爆 TopBar（TopBar 现有 `max-w-[140px]/[200px]` 截断沿用）。
- H8 删除语义不变：删主=级联删树（已有 `DeleteSessionTreeAsync`）；子界面不提供删除入口。

### 3.3 诊断中心重设计（S1–S6）

- S1 范围锁定：只重做 `agent` Tab（`AgentBehaviorView.vue`），不动其它 8 个 Tab；`SettingsCenterDrawer.vue` 的 1020px/左 Tab 结构可微调但不换交互范式。
- S2 新 IA（详情见 06 §2）：`运行配置（超时/轮次）— 子代理配置（开关/配额/权限）— 实时监控（子运行表+Goal 摘要+预算快照）— 历史诊断（拒绝记录/用量入口）`；A 说明卡折叠为顶部通栏提示。
- S3 组件拆分：单文件 695 行拆为 `AgentBehaviorView.vue（壳+tab内锚点）+ cards/AgentTimeoutCard/IterationsCard/SubAgentConfigCard/SubRuntimeMonitorCard/GoalSummaryCard`；表格列对齐、空态、暗色 token 统一。
- S4 数据零迁移：`agent.json` 字段不动；新增 Goal 摘要只读（读 `goal.json`），不进 agent.json。
- S5 交互规整：所有数字输入 `suffix+预设+恢复默认` 三件套对齐；危险开关红色描边；保存/脏态全局置顶（sticky action bar）。
- S6 可访问性：`data-testid` 保留老 ID（`agent-save/agent-sub-*`）以兼容旧单测/脚本，新增用 `agent-goal-*` 前缀。

## 4. 非目标（明确不做）

1. 多 Goal/Goal 版本历史/Goal 模板市场。
2. 子会话内发消息、截断、分支、删除（v1 只读；取消除外）。
3. 子再设 Goal、子再 spawn（已有深度锁）、子 ask（v1 锁定）。
4. 按工作区/会话/模型多套 Goal 覆盖；跨会话 Goal 共享。
5. 诊断中心其它 Tab、Drawer 整体换框架、移动端适配。
6. 本轮热切换 Goal 配置（统一下一轮生效）。

## 5. 边角表（实现必须覆盖，验收逐条勾）

| # | 场景 | 预期 |
|---|---|---|
| E1 | `goal.json` 缺失 | 空态“尚未设定目标”，浮动窗 mini 态显示“设目标”引导 |
| E2 | `goal.json` 损坏（非 JSON） | 后端 `LoadGoal` 回空态 + `GOAL_CORRUPT` 横幅，前端可“重置” |
| E3 | 子调 `goal_tool/set` | `[GOAL_DENIED]` 自愈文本回 LLM，主可转述；不落盘 |
| E4 | 主调 `goal_tool/progress percent=150` | 钳制 100 + 注记 `note` 保留 |
| E5 | 主会话删树时有 Goal | `goal.json` 随目录删除，无残留 |
| E6 | 子 running 时进子历史 | 顶部进度条 + 3s 轮询；切回主即停；取消按钮可用 |
| E7 | 子 failed/cancelled 后进子历史 | 状态徽标 + 失败原因行（复用 `SubAgentResult.errorMessage` 落盘 notice） |
| E8 | 同父 8 子（上限） | 面包屑下拉滚动；运行表分页/滚动；浮动窗不受影响 |
| E9 | 流式生成中切换主→子 | 主流式继续（事件按 sessionId 分桶，见 `agentStore` turn-event 分发），子只读拉取不互扰 |
| E10 | 诊断中心 1020px/94vw 小窗 | 三区纵向堆叠无横溢；表格横向滚动容器内 |
| E11 | 暗色/亮色切换 | 浮动窗/诊断卡全走 `bg-app/bg-surface/border-subtle/text-primary` token，无硬编码色 |
| E12 | 老 `agent.json` 缺字段 | 回默认（既有语义），诊断新版不崩 |

## 6. 验收口径

- 需求冻结以本篇为准；后篇与本篇冲突时以本篇为准并回改后篇。
- 联调矩阵见 `07`：G 矩阵（G1–G12）、H 矩阵（H1–H10）、S 矩阵（S1–S8）。
