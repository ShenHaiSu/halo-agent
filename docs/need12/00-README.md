# need12 施工方案总览（流式写入字节展示 + SQLite 用量分析）

> 输出路径：`docs/need12/`；方案先行，只写文档不改代码。
> 基线：`WriteFileTool`（原子落盘、无进度事件）、`AgentOrchestrator`（`ToolCallArgsDelta → ToolCallUpdated` 透传、无字节数；`turnBilledIn/Out` 仅内存累计、无持久化）、`LlmGatewayClient`（`UsageUpdated` 已解析 `prompt_tokens/completion_tokens/cached_tokens` 双协议归一为 `UnifiedTokenUsage`）、`SettingsCenterDrawer` 8 Tab（无用量页）、`hostBridge.ts` 无用量方法、前端无图表库、后端无 SQLite 依赖。
> 前置冻结：need01/04/05 上下文与缓存口径（`hitRate = cached/input`）、need07 右键分支语义、need09 图片与降级语义、need10 Skill/Memory Tab 施工范式（原生 `<table>`、Tab 懒加载、`data-testid`）。

## 1. 一句话目标

1. **写入不再“假死”**：`write_file`（兼 `edit_file`）参数流式到达时，前端以 **100–200ms 节流**实时显示 `已接收 xx Bytes → x.xx KB = xxx Bytes → x.xx MB = xxx KB = xxx Bytes` 的递进式字节计数（人类可读 + 精确值并存），附带“爽感”动效；大文件不再缓慢干等。
2. **用量可观测**：每个网络请求（provider 名、model 名、input/output/cached/hitRate、日期/时间/日期时间）经 **SQLite（`metrics/usage.db`）** 持久化，**每轮对话结束批量一写**（非高频逐请求写）；在设置与诊断中心新增第 9 Tab **“用量分析”**，用 **Apache ECharts 5.5.x（canvas）** + 瓦片 + 原生表格实现预设时间（今日/昨日/近3/7/30日/本周/本月/全部）× Provider × Model 筛选的命中率、用量柱状图（按模型堆叠）与 hover 明细。

## 2. 需求与文档映射（按顺序施工）

| 需求 | 文档 | 回答的问题 | 主要触点 |
|---|---|---|---|
| R0 现状与口径冻结 | `01-requirements-analysis.md` | 现状链路、字段口径、非目标 | Orchestrator/Gateway/ToolCard/Drawer |
| R1 流式字节展示 | `02-streaming-write-progress-design.md` | 字节从哪来、事件载荷、格式化函数、节流与动效 | `AgentOrchestrator`、`ToolCallCard`、`agentStore`、`formatBytes.ts` |
| R2 用量库后端 | `03-usage-db-backend-design.md` | 表结构、索引、WAL、轮末批量写、迁移 | `UsageMetricsService`、`usage.db`、`StoragePathManager`、`Microsoft.Data.Sqlite 6.0.x` |
| R3 用量分析前端 | `04-usage-analytics-frontend-design.md` | Tab 板式、瓦片、ECharts 选型与配置、筛选器 | `UsageAnalyticsView`、`usageAnalyticsStore`、ECharts 5.5 |
| R4 Bridge 契约 | `05-bridge-api-contracts.md` | 方法签名、DTO、错误码、事件 | `HostBridgeFacade.Usage.cs`、`hostBridge.ts` |
| R5 联调验收 | `06-integration-test-plan.md` | 怎么证明做对了、性能基线、合闸命令 | `dotnet test`、`vue-tsc`、`data-testid` |

依赖：`01 → (02 ∥ 03) → 04 → 05 → 06`。`02` 与 `03` 无交叉可并行；`04` 依赖 `05` 的 DTO 先冻结（可用 Mock 并行）。

## 3. 范围一句话

- **做**：`ToolCallUpdated` 扩展 `receivedBytes/argsBytes`（增量 UTF-8 累加、节流发布）；`ToolCallCard` 字节计数行（`write_file/edit_file` 专属，取消/完成态收敛）；`usage.db` 单表 + 索引 + WAL + 轮末单事务批量写 + 后台单写者队列；第 9 Tab（筛选条/瓦片/堆叠柱状/命中率折线/Provider 环形/原生明细表）；Bridge 聚合查询（服务端聚合、不裸传全量行）；ECharts 5.5.x 引入（按需引入、chrome109 锁定）。
- **不做**：断点续传/真实进度条百分比（总量未知，不伪造）；按 token 计费金额（无单价表）；云同步/导出 CSV（可二期）；改 `messages.jsonl`/LLM 日志格式；升级 .NET/CefSharp/Vite target；`n-data-table`（沿用 need10 禁令，明细用原生 table）。

## 4. 通用施工原则（六篇通用，冲突以各篇“精确到文件/行”为准）

1. **永不阻塞主链路**：字节统计、DB 写、聚合查询任何异常只记 Warn，前端降级为“-”，会话照常跑。
2. **口径唯一**：`hitRate = cached/input（input=0 → 0）`，与 `UnifiedTokenUsage.CacheHitRate` 同公式；综合命中率 = `Σcached/Σinput`（加权），禁止对行命中率取算术平均。
3. **低频写**：DB 写只发生在轮次结束（`TurnCompleted/Truncated/Error/Cancelled-before-start` 除外皆写，失败/取消按“有 usage 才写”），单轮单事务；禁止在 `UsageUpdated` 到达瞬间同步写盘。
4. **服务端聚合**：趋势/堆叠/瓦片一律 SQL `GROUP BY` 后返回，前端只做渲染；明细表分页上限 200/页。
5. **Win7/CefSharp109 红线**：不升级框架；ECharts 用 canvas 渲染、关闭动画阈值外不做花哨动效；`Microsoft.Data.Sqlite` 锁 `6.0.x`（随 net6，不追 8/9）；前端构建 target 不动。
6. **可测性**：所有新增 UI 带 `data-testid`（见 `06`）；后端 `dotnet build + test`，前端 `vue-tsc -b && vite build` 全绿方可合闸。
