# need13-01 需求分析（双协议切换结论 + 主子代理冻结需求 + 可配置项）

> 前置：`00-README.md`。本篇冻结后才允许开工 02/03/04/05/06。

## 1. Q1：同会话 `resp`→`chat` / `chat`→`resp` 能否工作？

### 1.1 结论（冻结）

| 方向 | 结论 | 代价 |
|---|---|---|
| 第一轮 `resp`、第二轮 `chat` | ✅ 正常工作 | 切换后首轮 prompt-cache 必 miss 一次；`ItemId(fc_xxx)` 被丢弃（无害） |
| 第一轮 `chat`、第二轮 `resp` | ✅ 正常工作 | 同上首轮 miss；`chat` 的 `call_xxx` 直接复用为 `resp` 的 `call_id`（上游当不透明键） |

双向对称成立，不存在“只能单向”。反向切换无需任何迁移，开箱即用。

### 1.2 证据链（5 条，精确到行）

**E1. 网关路由是每请求无状态的，与会话无关。**

`LlmGateway/LlmGatewayClient.cs:110-119`：

```csharp
if (!_adapters.TryGetValue(request.Model.Protocol, out var adapter))
    adapter = _adapters[LlmProtocolType.Chat];
string endpointUrl = adapter.ResolveEndpointUrl(request.Provider.BaseUrl);
string requestPayload = adapter.SerializeRequest(request, includeUsageInStream);
```

`Protocol` 取自本轮 `request.Model.Protocol`（即本轮所选 `ModelConfig.Protocol`）。无 session 亲和、无连接复用、无 `previous_response_id` 链。`chat→/v1/chat/completions`，`resp→/v1/responses` 由 `ResolveEndpointUrl` 当场决定。

**E2. 历史落盘是协议无关的统一模型。**

`Core/Models/WorkspaceModels.cs:UnifiedChatMessage` 只有 `role/user|assistant|tool|system + content + reasoningContent + ToolCalls[List<UnifiedToolCall{CallId,ItemId?,FunctionName,ArgumentsJson}>] + ToolCallId + Name + Input/Output/CachedTokens + Images`。

`AgentOrchestrator.cs:556-576,676-681` 每轮落盘 `assistant(text, completedCalls, reasoning)` + `tool(callId, text, name)`。两个 Adapter 只是同一份 `List<UnifiedChatMessage>` 的两种视图：

- `ChatCompletionsAdapter.cs:89-107`：`assistant{content, tool_calls[{id,name,arguments}]}` + `tool{tool_call_id,content}`。
- `ResponsesApiAdapter.cs:96-131`：`input[{message},{function_call{call_id,name,arguments}},{function_call_output{call_id,output}}]`。

切换协议 = 换序列化器重放同一 `messages.jsonl`。

**E3. `CallId/ItemId` 映射兼容。**

- `ResponsesApiAdapter.cs:282-300`：`item_id(fc_xxx)` 建状态，`call_id(call_xxx)` 对外；`ToolStarted(callId,name,itemId)` 两者都透出。
- `AgentOrchestrator.cs:391-476,553`：`PendingToolCall{CallId,ItemId}` 全保留，`completedCalls.Add(new UnifiedToolCall(cid, itemId, ...))` 落盘。
- `Chat` 发送只用 `CallId`（`ChatCompletionsAdapter.cs:99`），`ItemId` 被忽略无害；`Resp` 发送用 `CallId` 做 `call_id`（`ResponsesApiAdapter.cs:118`），`chat` 产的 `call_xxx` 可直接用，上游当不透明键回显。

**E4. 每轮模型重新解析，天然支持轮间换模型/换协议。**

`AgentOrchestrator.cs:158,743-789 ResolveProviderAndModel(providerId,modelId,session)`：`pid = providerId ?? session.ProviderId ?? firstEnabled`，`mid = modelId ?? session.ModelId ?? firstEnabled`。`Bridge/HostBridgeFacade.cs:809 StartAgentTurn(ws,sess,prompt,provId,modId)` 把前端当前选中模型透传进来。只要第二轮传入另一个 `modelId`（不同 `Protocol`），本轮 `request.Model` 即新协议，`Tools`（`ToolRegistry.GetDefinitions→UnifiedToolDefinition` 按 name 排序）与 `SystemInstructions`（chat 放 system 消息 / resp 放顶层 instructions）两协议共用同一份。

注意：轮内多 iteration 协议固定。`request` 在 `for(iteration...)` 外构造一次（`AgentOrchestrator.cs:339-349`），循环内复用。切换只能发生在轮边界，不能在一次 ReAct 中途换。这是正确的，不用改。

**E5. 愈合/预算/用量/多模态全是协议无关的。**

- 愈合 `WorkspaceSessionService.cs:539-575`：按 `assistant.ToolCalls[].CallId` vs `tool.ToolCallId` 配对，只看字符串。
- 预算 `TokenBudgetService.TruncateToBudget`：按 `EstimateTokens` 估算，与序列化字节无关。
- 用量：chat(`prompt/completion/cached`) vs resp(`input/output/input_details.cached`) 都归一到 `UnifiedTokenUsage{Input,Output,Cached,Total}`，`AgentOrchestrator.cs:477-484,714-733` 累加一致。
- 图片 `MultimodalPlanner.PlanTurn` 两 Adapter 共用；非视觉抛 `NonVisionModelException` 后 Orchestrator 剥离重试（`AgentOrchestrator.cs:278-286`）。
- 思考链 resp(`reasoning_summary_text.delta`) vs chat(`reasoning_content`) 都归一 `ReasoningDelta`，落盘 `ReasoningContent` 跨协议可读。

### 1.3 切换时的 3 个真实成本（功能正确，仅性能/体验）

1. **首轮必失一次 prompt-cache**：`LlmGatewayClient.cs:132 prefixHash=Hash(requestPayload)`，载荷含协议 framing（messages vs input，tools 嵌套 vs 扁平），换协议哈希必变。`stableHash`（system 稳定区）不变但网关侧按整包前缀匹配，命中率掉一次，下一轮恢复。不影响正确性。
2. **`404` 提示单向**：`ResponsesApiAdapter.FormatErrorResponse` 对 404 提示“切 chat 重试”；chat 无对应提示（chat 是最兼容协议）。属预期 UX。
3. **`stream_options` 重试只影响 chat**（`LlmGatewayClient.cs:164-176` 400 去 `stream_options` 重发一次），resp 无此分支。切换后行为差异仅此一处。

### 1.4 对主子代理的推论

子默认继承主模型，但允许 `task` 内单次 `model` 覆盖（含跨协议）。主 `resp` + 子 `chat` 混跑合法，用量按 `sessionId` 分桶、`SessionHash` 独立，cache 互不串扰（见 04 §6）。

---

## 2. Q2：主子代理冻结需求（5 要素）

| # | 要素 | 冻结语义 |
|---|---|---|
| R1 | 用户手动建的都是 Main | `SessionMetadata.AgentRole` 缺省即 `main`；`GetSessionsByWorkspaceAsync` 默认只返 main；侧边栏不变 |
| R2 | Main 经工具调用建 Sub，Sub 上下文干净 | `spawn_subagent(task,label,model?,maxIterations?)`；新建 `sessionId`，`messages.jsonl` 为空，仅首条 `user(task)`；不复制主历史，背景由 `task` 文本显式携带 |
| R3 | Sub 权限是 Main 的子集 | 禁 skill/memory 写、禁 `ask_user`、禁套娃 spawn；读/查/看图/文件读写（后两者可配，见 §3）默认开 |
| R4 | Sub 独立 id 但寄宿主下 | 扁平目录 `sessions/<ws>/<subId>/`；`ParentSessionId=mainId`；级联删除/级联取消/聚合展示；`SessionHash=Hash(ws:subId)` 独立 |
| R5 | Sub 文本汇报 + Barrier + 级联掐断 | Sub 结束标志 = 最后一条 assistant 无 tool_calls；`summary` 全量回填主 `Tool(callId, resultJSON)`；`await Task.WhenAll` 永远等待无超时；仅 `CancelTree(main)` 可破；主 barrier 等待不消费主 iteration |

## 3. 可配置项清单（v1 冻结，存 `config/agent.json`）

| 字段 | 类型 | 默认 | 合法 | 说明 |
|---|---|---|---|---|
| `subAgentEnabled` | bool | true | — | 总开关。false 时主仍看得见 `spawn_subagent` 但调用直接拒（`[SUBAGENT_DISABLED]`），不建会话不耗 token |
| `subMaxParallel` | int | 3 | 1..8，硬顶 8 | 同一主轮 barrier 并发上限。超限的 spawn 当轮直接拒不排队 |
| `subMaxIterations` | int | 15 | 1..50，硬顶 100 | 子单轮 ReAct 上限，独立于主 `maxIterations(25)` |
| `subAllowExecCommand` | bool | true | — | 子可用 `exec_command` |
| `subAllowFileWrite` | bool | true | — | 子可用 `write_file/edit_file`；关则子只读 |
| `subAllowSkillWrite` | bool | false | — | 子 `skill_tool` 写动作；默认禁，开则红字警告 |
| `subAllowMemoryWrite` | bool | false | — | 子 `memory_tool` 写动作；默认禁 |
| `subAllowAskUser` | bool | false | **v1 锁定 false** | 子禁 `ask_user`（barrier 会假死）；UI 置灰，后端配 true 也归一 false+Warn |

`exec` 超时复用现有 `execDefaultTimeoutMs/execMaxTimeoutMs`，不另设。子模型默认继承主模型。

### 3.1 为什么进设置与诊断中心？

`need08-01 §2` 已冻结：后续杂项一律以新卡片追加进 `智能体行为(agent)` Tab，不新增 Tab 不改名。子代理开关/配额/权限正是“智能体行为”，放 D/E 卡语义最正。位置见 `06`。

### 3.2 为什么关闭时仍显示 tool（可解释拒绝）？

隐藏省约 300 tokens/轮但不可解释；常显 + 网关式强拒绝可解释。双保险：prompt 预声明（`SystemPromptBuilder` 追加策略块，主连试都不用试就知道关了）+ dispatch 强拒绝（`[CODE]` 前缀回包，主下一轮必见可转述）。话术见 `03 §4`。

## 4. 明确不做（防蔓延）

1. 不做按工作区/会话/模型/协议多套覆盖；`scope` 写死 `global`。
2. 不做子再套娃（深度锁 1，见 03 §3.3）。
3. 不做子直连 `ask_user`（v1 锁定，见 03 §3.4）。
4. 不做本轮热切换：保存后下一轮快照生效，barrier 等待中变更不影响本轮。
5. 不做子独立侧边栏项：默认只主可见，子经主展开/诊断表查看（见 06 §4）。
6. 不做跨会话向量检索/记忆共享；背景靠 `task` 文本显式携带。
7. 不改网络超时与模型预算；不碰 `prompt.json/providers.json` 语义。

## 5. 边角表（02/03/04/05/06/07 必须覆盖）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 老 `agent.json` 无 7 字段 | 全部回默认，不算损坏，无横幅，会话正常 |
| E2 | `subMaxParallel=0/9`、`subMaxIterations=0/101` | `AGENT_VALIDATION` 拒绝，field 分别为两字段 |
| E3 | `subAllowAskUser=true` 手改落盘 | 读时归一 false + Warn 日志，不抛错不中断 |
| E4 | `subAgentEnabled=false` 时主调 spawn | 当轮 `Fail([SUBAGENT_DISABLED])`，未建会话，无 token 消耗，主可转述 |
| E5 | 本轮 5 个 spawn 而 `max=3` | 先到 3 个建子跑，余 2 当场 `Fail([SUBAGENT_QUOTA_EXCEEDED])`；主收齐 5 个回包（3 结果+2 拒绝） |
| E6 | 子内调 spawn/ask/skill 写 | 逐个 `Fail([SUBAGENT_NESTED_DENIED]/[SUBAGENT_ASK_DENIED]/[PERMISSION_DENIED])`，子继续不挂起 |
| E7 | barrier 等待中用户取消主 | `CancelTree`：子全取消→主 `TurnCancelled`；残缺子结果不回填 |
| E8 | barrier 等待中改配置并保存 | 本轮快照不变；已跑子继续，新 spawn 按新快照判 |
| E9 | 宿主重启后有 `running` 子残留 | 启动 `Reconcile()` 置 cancelled + 落关闭 notice，下轮可重建 |
| E10 | 子 `TurnTruncated`/StreamError | `success=false` + `errorCode` 回填，主自愈决策重试或拆小 |
| E11 | 删除主会话时有运行中子 | 先 `CancelTree` 再删盘（含子盘），再刷 index |
| E12 | 主 `resp` + 子 `chat` 混跑 | 用量分桶正确，cache 各自独立，前缀哈希互不影响 |
