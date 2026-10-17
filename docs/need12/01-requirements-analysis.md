# 01 需求解析与现状代码分析

## 1.1 两条需求原文拆解

### 需求 1：write 流式字节展示（“爽感”计数器）

| 子项 | 含义 | 验收锚点 |
|---|---|---|
| 不缓慢等待 | 大 `content` 经 LLM 流式 `ToolCallArgsDelta` 到达时就要动，不能等 `ToolCallCompleted` 才一次性渲染 | 首字节 ≤1 个节流周期（100–200ms）内可见数字跳动 |
| 单位递进 | `Byte → KB → MB` 平滑过渡 | `xx Bytes` → `x.xx KB = xxx Bytes` → `x.xx MB = xxx KB = xxx Bytes`，人类可读在前、精确值在后 |
| 模型单次写不到 MB | MB 分支是防御性设计（聚合/超大文件），不断言常见路径能到 MB | MB 格式仍要实现并单测覆盖，但默认走 Byte/KB |
| 保留前面简易阅读 + 后面精确值 | 同一行同时给两种信息，不做切换丢失 | 格式函数输出单字符串含两部分，不截断精确值 |

### 需求 2：SQLite 用量分析 + “用量分析” Tab

| 子项 | 含义 | 验收锚点 |
|---|---|---|
| 每请求一记 | provider 名、model 名、input、output、cached、hitRate、日期、时间、日期时间 | 字段全、可按 provider+model 联合筛选（同名模型不串） |
| 低频写 | 轮次结束统计完一并写，不逐 `UsageUpdated` 写 | 单轮多 LLM call 只产生 1 次 DB 事务 |
| Tab 位置 | 设置与诊断中心新增 Tab“用量分析” | 第 9 Tab，与 8 旧 Tab 并列，懒加载 |
| 图表 + 瓦片 + 筛选 | 命中率、用量柱状图（内按模型占比）、hover 图例 | ECharts 实现（见 04），筛选：今日/昨日/近3/7/30日/本周/本月/全部 + Provider + Model |
| 高性能前端组件 | 数据量大不卡 | 服务端聚合 + canvas + 分页明细（见 04/06 性能基线） |

## 1.2 现状链路（精确到文件/行，均为当前代码实测）

### A. write_file 现状：无进度、终态才一句话

- `src/Host.Desktop/Tools/BuiltIn/WriteFileTool.cs:23-136`：`ExecuteAsync` 同步原子写（`FileToolShared.AtomicWriteAllText`），成功只回 `已创建/已覆盖 'x'：N 行 / M 字节`，**无中途进度**。
- 参数到达链路（LLM → 前端）：
  1. `LlmGatewayClient.ExecuteStreamPipelineAsync` 把 adapter 的 `ToolCallArgsDelta` 原样进 Channel（不计数）。
  2. `AgentOrchestrator.cs:406-435`：`ToolCallArgsDelta` 仅 `pending.ArgsBuilder.Append(delta)` + `PublishTurnEvent(sessionId,"ToolCallUpdated",{callId,functionName,argsDelta})` ——**argsDelta 全量透传但无字节数**，且逐 chunk 发布（高频）。
  3. 前端 `stores/agentStore.ts:343 ToolCallUpdated`（实测仅拼 args，未做字节显示）；`components/chat/ToolCallCard.vue:56,185-231` 对 `write_file/edit_file` 只渲染 Diff/标题，无字节行。
- 结论：**字节数在后端唾手可得（`argsDelta` 的 UTF-8 长度累加即可），缺的是“计数 + 节流 + 格式化 + 展示行”四件套**。无需改 LLM 协议、无需改工具执行本身。

### B. token 用量现状：有口径、无持久化

- 双协议已归一（实测）：
  - `ChatCompletionsAdapter.cs:260-280` 解析 `usage.prompt_tokens/completion_tokens/prompt_tokens_details.cached_tokens`（兼容 `prompt_cache_hit_tokens`），缺 `usage:null` 保护已做。
  - `ResponsesApiAdapter.cs:367-399` 解析 `usage.input_tokens/output_tokens/input_tokens_details.cached_tokens`。
  - 统一为 `UnifiedTokenUsage(Input,Output,Cached,Total)` + `CacheHitRate = cached/input`（`UnifiedLlmModels.cs:215-225`）。
- `LlmGatewayClient.cs:219-224` 每轮取最后一次 `UsageUpdated` 为该 call 用量；`LogResponseTrace` 写 `llm-trace` 日志（文本 grep 用，不可做结构化查询）。
- `AgentOrchestrator.cs:205-207,444-450,523-548`：`turnBilledIn/Out += usage` 累加整轮账单 → `AssistantMessagePersisted/TurnCompleted` 事件带 `inputTokens/outputTokens/cachedTokens/billedPrompt/billedCompletion`。**整轮 N 次 LLM call 的逐 call 明细在循环内可拿到，但循环结束即丢弃**——用量库只需在此处“接住”即可，不用动网关。
- 存储现状：`StoragePathManager` 管 `config/workspaces/sessions/logs/cef-cache`，无 `metrics` 目录、无 SQLite 依赖（全仓 grep `sqlite` 零命中，`.csproj` 仅 CefSharp + ProtectedData）。

### C. 设置中心现状：第 8 Tab，可直接加第 9 个

- `SettingsCenterDrawer.vue:15-68`：`n-tabs placement=left type=card`，8 个 `n-tab-pane`（appearance/providers/network/storage/runtime/prompt/agent/skill-memory），`width=1020/max-94vw`，Tab 内容区 `overflow-x-hidden`。加 Tab 只需加一个 pane + 导入组件 + 扩展 `SettingsCenterTab` 联合类型。
- `hostBridge.ts`（2053 行）：已有 config/storage/system 等 Bridge 方法范式（`BridgeResponse<T>`），新增用量方法照抄即可。
- 前端依赖（`package.json`）：`vue 3.5 + naive-ui 2.40 + tailwind 3.4`，**无图表库**，构建 `target/cssTarget=chrome109`、`chunkSizeWarningLimit=2500`。

## 1.3 口径冻结（后续文档不再争论）

| 字段 | 口径 | 来源 |
|---|---|---|
| provider 名称/ID | `ProviderConfig.Name/Id` 快照（改名后历史不追溯，保证“当时是谁就是谁”） | Orchestrator 轮内 `provider` 对象 |
| model 名称 | `ModelConfig.Id`（即请求实际用的模型 ID；显示名如有则另存 `modelDisplay`） | 同上 |
| input/output/cached | 该 **单次 HTTP 请求**的 `UsageUpdated` 终值；缺 usage（如被取消/400）记 0 并 `status` 标记 | Gateway |
| hitRate（行） | `input>0 ? cached/input : 0`，存 `REAL 0-1`，显示 `×100%` 一位小数 | 与 `CacheHitRate` 同式 |
| 综合命中率（聚合） | `Σcached/Σinput` 加权，**禁止** `AVG(行hitRate)` | SQL 聚合 |
| 日期/时间/日期时间 | **本地时区** `yyyy-MM-dd / HH:mm:ss / yyyy-MM-dd HH:mm:ss` 三列冗余（需求明示三列都要）+ `utcIso` 审计列 | 轮末落库时 `DateTime.Now` 一次生成 |
| 一条记录 = 一次 HTTP 请求 | ReAct 一轮多 call 记多行（`request_id` 区分），轮末批量插多行 | Orchestrator 循环内收集 |

## 1.4 非目标（防蔓延）

1. 不做金额计费（无单价表，做了也是错价）。
2. 不伪造进度百分比（总量未知；只做字节计数 + 不定态动效）。
3. 不改 `messages.jsonl`、`llm-trace` 日志格式（用量库是新增旁路，不是替换审计）。
4. 不引服务端分页之外的重型 ORM；不升级任何框架版本。
