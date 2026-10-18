# need13-04 后端：编排器与协调器（Barrier 等待 + 级联取消 + 用量归因）

> 前置：`01/02/03` 已冻结。本篇按文件顺序施工，冲突以本篇“精确到行”为准。

## 1. 总览与文件清单

| 动作 | 文件 | 说明 |
|---|---|---|
| 新增 | `src/Host.Desktop/Services/SubAgentCoordinator.cs` | `Create/Barrier/WhenAll/CancelTree/Reconcile` + 内存注册表 + 事件发布 |
| 修改 | `src/Host.Desktop/Services/AgentOrchestrator.cs` | iteration 内 spawn 分流；子实例构造；`ToolContext` 角色/策略快照；`IterationStarted` 透出子策略 |
| 修改 | `src/Host.Desktop/Bridge/HostBridgeFacade.cs` | `StartAgentTurn` 重入先 `CancelTree`；`CancelAgentTurn` 升级级联；`DeleteSession` 走树删 |
| 修改 | `src/Host.Desktop/Services/UsageMetricsService.cs` | 子行 `SessionId=subId` 照写；新增 `GetRollupAsync(parentId)` 聚合查询（可选） |
| 不动 | `AskUserGate`、`TokenBudgetService`、`LlmGatewayClient` | key/口径天然隔离，无需改动 |

## 2. `SubAgentCoordinator`（新建，约 220 行）

```csharp
public sealed class SubHandle {
  public string SubSessionId { get; init; } = "";
  public string CallId { get; init; } = "";
  public string Label { get; init; } = "";
  public Task<AgentTurnResult> Task { get; init; } = Task.FromResult(AgentTurnResult.Cancelled(0,0));
  public CancellationTokenSource Cts { get; init; } = new();
  public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
}
public sealed class SubAgentCoordinator {
  private readonly ConcurrentDictionary<string, List<SubHandle>> _trees = new(); // parentId -> handles
  private readonly IWorkspaceSessionService _sessions;
  // CreateSubsAsync(ws, mainId, spawnCalls, mainModel, policy, mainCt) -> (handles, quotaRejected)
  // BarrierAsync(parentId, handles, mainSessionAppend) -> List<SubAgentResult>
  // CancelTree(parentId) / CancelSub(subId) / ReconcileAsync() / GetActive(parentId)
}
```

职责边界：Coordinator 只管**建会话 + 起 Task + 等 + 取消 + 事件**，不管 LLM 文本（文本由各子 `AgentOrchestrator` 实例产出）。

### 2.1 建子（`CreateSubsAsync`）

```
输入：ws, mainId, spawnCalls[{callId,task,label,model?,providerId?,maxIterations?}], mainModel{provider,model}, policy快照, mainCt
1. take = spawnCalls.Take(policy.SubMaxParallel)；余下记 quotaRejected（不建会话，直接 Fail 回包，见 §3.2）
2. foreach (take):
   a. 校验 task（空→VALIDATION 拒；>8000字→截断+warning）；label 缺省取 task前12字；maxIterations 钳 1..50 缺省 policy.SubMaxIterations
   b. model 覆盖：若指定则查 ProviderConfigService 存在且 Enabled，否则回退 mainModel + warning
   c. meta = await sessions.CreateSubSessionAsync(ws, title:"sub: "+label, providerId, modelId, parent=mainId, role=sub, label, callId, task截2000)
   d. Publish TurnEvent(mainId, "SubAgentsStarted", {subs:[{callId,subId,label}]})
   e. subCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt)；登记 _trees[mainId].Add(handle)
   f. handle.Task = Task.Run(() => RunSubTurnAsync(...subCts.Token...))
3. 返回 (handles, quotaRejected)
```

`SubStatus` 流转：`pending(建完)` → `running(Task起)` → `succeeded/failed/cancelled(Barrier收齐时按 AgentTurnResult 写回 meta)`。

### 2.2 子执行（`RunSubTurnAsync`，Coordinator 内私有）

```csharp
async Task<AgentTurnResult> RunSubTurnAsync(ws, subId, task, providerId, modelId, subMaxIter, policy, subCt) {
  var subReg = ToolRegistryFactory.ForSub(policy);
  var subCtxRole = ("sub", parentSessionId: mainId);
  // systemPrompt = BuildPreview(...) + SubAgentSuffixBlock（03 §5）
  var subOrch = new AgentOrchestrator(_sessions, _budget, _gateway, subReg, _storage,
      _providers, _prompts, maxIterations: subMaxIter,
      agentConfigService: _agentConfig, usageMetrics: _usage);
  // 注意：必须 new 实例，不可复用主实例（ResolvedMaxIterations 是实例可变属性，见 AgentOrchestrator.cs:65）
  Publish(mainId, "SubAgentProgress", {subId, status:"running"});
  var r = await subOrch.RunAgentTurnAsync(ws, subId, task, providerId, modelId, subCt);
  // 写回 SubStatus；Publish Progress done
  return r;
}
```

`RunAgentTurnAsync` 复用现有全链路：轮首 `Heal` → `Append user(task)` → 快照 skill/memory（只读渲染）→ 预算裁剪 → 双协议流式 → 工具（角色过滤后）→ 落盘 → `TurnCompleted/Cancelled/Failed/Truncated`。**子事件双写**：子 orchestrator 的 `PublishTurnEvent(subId, ...)` 照常（供展开查看）；Coordinator 另发 `agent:sub-event {parentId, subId, kind}` 聚合（供主会话进度条）。

结束判定：`RunAgentTurnAsync` 返回即子结束；`summary` = 该子 `messages.jsonl` 末条 `role=assistant` 且无 `tool_calls` 的 `Content`（无则取末条文本 + `[子未输出总结]` 标记）。

### 2.3 Barrier（`BarrierAsync`，永远等待）

```csharp
async Task<List<SubAgentResult>> BarrierAsync(parentId, handles, mainCt) {
  // 无超时 await Task.WhenAll(handles.Select(h => h.Task))；响应 mainCt 取消
  // 全部完成后：foreach h: result = await h.Task（已完成取 Result；Cancelled 抛 OperationCanceledException→记 cancelled）
  //   summary = ReadLastAssistantText(subId)；usage = ReadLastUsage(subId)（末轮 Input/Output/Cached + Σ toolExecutions）
  //   写回 meta.SubStatus；Publish(mainId, "SubAgentsBarrierReleased", {subs:[{callId,subId,success}]})
  //   _trees[parentId] 移除已完成（保留 cancelled 供诊断 5 分钟，可配 TTL 后清）
  // 返回与 spawnCalls 等长对齐的 SubAgentResult[]（含 quotaRejected 的 Fail 项，见 §3.2）
}
```

等待不消费主 `iteration`；`usageRows` 子行已在子轮内 `EnqueueFlush(subId)`，主不再重复记。

`SubAgentResult` JSON（落主历史 `Tool(callId, json, "spawn_subagent")`）：

```json
{"subSessionId":"sess_...","label":"...","success":true,"summary":"...全文...",
 "toolExecutions":5,"iterations":3,
 "usage":{"input":12000,"output":800,"cached":9000},
 "errorCode":null,"errorMessage":null,"warning":null}
```

失败/截断：`success=false + errorCode(TURN_TRUNCATED/STREAM_ERROR/...) + summary(已有文本)`，主 LLM 自愈决策。

## 3. 主循环分流（改 `AgentOrchestrator.cs:627-705`）

```csharp
// (7) 依次调度之前插入分流：
var spawnCalls = completedCalls.Where(c => c.FunctionName=="spawn_subagent").ToList();
var normalCalls = completedCalls.Where(c => c.FunctionName!="spawn_subagent").ToList();
// 3a. normalCalls 按现有 foreach 串行 Dispatch（§627-705 原样，ToolContext 填 AgentRole=main+策略快照）
// 3b. 若 spawnCalls 非空：
if (spawnCalls.Count > 0) {
  if (!policy.SubAgentEnabled) {
    foreach (var s in spawnCalls) Append Tool(mainId, s.CallId, "[SUBAGENT_DISABLED]...");
  } else {
    var (handles, rejected) = await coordinator.CreateSubsAsync(...Take(policy.SubMaxParallel)...);
    foreach (var r in rejected) Append Tool(mainId, r.CallId, "[SUBAGENT_QUOTA_EXCEEDED]...");
    Publish(mainId, "SubAgentsBarrier", {total: handles.Count, waiting:true});
    List<SubAgentResult> results;
    try { results = await coordinator.BarrierAsync(mainId, handles, cancellationToken); }
    catch (OperationCanceledException) { /* 主被取消：残缺不回填，直接 TurnCancelled（§5）*/ throw; }
    foreach (var res in results) Append Tool(mainId, res.CallId, Json(res));
    Publish(mainId, "SubAgentsBarrierReleased", ...);
  }
}
// 3c. 进入下一 iteration（LLM 看到全部子结果+拒绝回包后继续）
```

`policy` 即轮首快照（`_agentConfigService.GetInternal()` 一次读出的 7 字段，见 05 §3），本轮内所有 spawn/skill/memory 判定共用它。

## 4. 前端事件契约（`JsEventDispatcher.Publish("agent:turn-event", {sessionId, kind, payload})`）

| kind | sessionId 桶 | payload | 前端动作 |
|---|---|---|---|
| `SubAgentsStarted` | main | `{subs:[{callId,subId,label}], total, maxParallel}` | 主时间线插“子任务 1/3 已启动”卡 |
| `SubAgentProgress`（经 `agent:sub-event` 聚合） | main | `{subId,label,status,iteration,tool,elapsedMs}` | 进度条/旋转态 |
| `SubAgentsBarrier` | main | `{total, waiting:true}` | 主输入框置 `waiting-subagents` 态 |
| `SubAgentsBarrierReleased` | main | `{subs:[{callId,subId,success}], billedPrompt, billedCompletion}` | 解除等待，主继续流式 |
| 存量子事件（`IterationStarted/TextDelta/ToolCall*`） | subId | 原样 | 展开子视图时拉取 |

## 5. 取消级联（改 `HostBridgeFacade.cs:809-908`）

```csharp
public Task<BridgeResponse<bool>> CancelAgentTurn(string sessionId) // sessionId=mainId
  => CancelTreeAsync(sessionId);
async Task CancelTreeAsync(mainId) {
  coordinator.CancelTree(mainId); // 快照子 CTS 逐个 Cancel（先子后主）
  if (_activeSessionTokens.TryRemove("agent:"+mainId, out var mainCts)) { mainCts.Cancel(); mainCts.Dispose(); }
  AskUserGate.RemoveTimeout(mainId, ...); // 主若在 ask_user 阻塞一并解开（子无 gate，无需管）
  return Success(true, "已发送取消指令（含 N 个子代理）");
}
```

- `StartAgentTurn` 重入（同 main 再发一轮）先 `CancelTree(mainId)` 防旧子泄漏，再开新 CTS。
- 主在 barrier 中被取消：`BarrierAsync` 的 `WhenAll` 抛 `OperationCanceledException` → 主不回填残缺结果，直接 `Publish TurnCancelled` + `return Cancelled`（与现有 `AgentOrchestrator.cs:509-527` 取消落盘语义一致）；子各自走 `TurnCancelled + 部分落盘`。
- `DeleteSession(main)`：先 `await CancelTree` 再 `DeleteSessionTreeAsync`（02 §3.3）。
- 诊断卡单掐子 `CancelSubAgent(subId)`：只取消该子，主 barrier 继续等该子 `Cancelled` 结果，不提前放行（语义保持“等全部”）。

## 6. 用量与日志

- 子 `CollectUsageRow(rows, ..., sessionId=subId)` 照常 `EnqueueFlush`，`llm-trace-*.log` 已含 `session=` 字段，`grep session=subId` 即子链，`grep` 主子对照看 cache 隔离。
- 主 `TurnCompleted.payload` 新增 `subAgents:{count, billedPromptΣ, billedCompletionΣ}`（rollup 求和，供顶栏展示“本轮含子耗”）。
- 可选 `UsageMetricsService.GetRollupAsync(ws, parentId)`：`WHERE SessionId IN (parent+subs)` 求和，供用量 Tab 扩展（见 06 §4）。

## 7. 启动自愈（`Reconcile`）

`Program.cs` 或 `HostBridgeFacade` 构造后调一次 `coordinator.ReconcileAsync()`：扫 `sessions-index` 全部 `AgentRole=sub && SubStatus=running` → 置 `cancelled` + 落一条 assistant `"[系统恢复] 该子代理因宿主重启已终止，未完成部分请由主重新 spawn。"`。下轮可重建。

## 8. 本篇验收（给 07 用）

- [ ] 3 子 barrier 收齐才进下一 iteration（mock gateway：主首轮回 3 spawn，子各 2 轮 tool 后总结）。
- [ ] 超限 2 个当场拒，先到 3 个跑，主收齐 5 回包。
- [ ] `CancelTree` 后主子全 `TurnCancelled`，`_trees` 清空，无残留 Task。
- [ ] 子 `Truncated/Error` 回填 `success=false`，主 LLM 下一轮可见并改路。
- [ ] 主子事件桶隔离正确（subId 事件不污染主时间线）。
- [ ] 重启后 running 子被自愈为 cancelled。
