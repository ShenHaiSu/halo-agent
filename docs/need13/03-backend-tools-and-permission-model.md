# need13-03 后端：工具与权限模型（spawn + 角色过滤 + 可解释拒绝）

> 前置：`01` 已冻结。本篇按文件顺序施工，冲突以本篇“精确到行”为准。

## 1. 总览与文件清单

| 动作 | 文件 | 说明 |
|---|---|---|
| 修改 | `src/Host.Desktop/Tools/IToolHandler.cs` | `ToolContext` 加 `AgentRole/ParentSessionId` + 6 策略快照字段 |
| 新增 | `src/Host.Desktop/Tools/ToolRegistryFactory.cs` | `ForMain(policy)/ForSub(policy)` 工厂；子 registry 包只读 skill/memory + 禁 ask/spawn |
| 新增 | `src/Host.Desktop/Tools/BuiltIn/SpawnSubAgentTool.cs` | `spawn_subagent` 定义 + 参数校验（执行由 Coordinator 承接，见 04） |
| 修改 | `src/Host.Desktop/Tools/BuiltIn/SkillToolShared.cs` | 写动作入口按 `context.AgentRole+policy` 拦截 |
| 修改 | `src/Host.Desktop/Tools/BuiltIn/AskUserTool.cs` | 首行角色检查：sub 直接拒 |
| 修改 | `src/Host.Desktop/Services/SystemPromptBuilder.cs` | 追加 `SubAgentPolicyBlock`（主）与 `SubAgentSuffixBlock`（子） |
| 修改 | `src/Host.Desktop/Tools/ToolRegistry.cs` | `HintForTool` 加 spawn 提示（或迁至工厂，保持自愈链） |

## 2. 上下文（改 `IToolHandler.cs:ToolContext`）

```csharp
public sealed class ToolContext {
  // ...存量 WorkspaceRoot/WorkspaceId/SessionId/DataRoot/ConfiguredPythonPath/
  //      PythonProbeOverride/ExecDefaultTimeoutMsOverride/ExecMaxTimeoutMsOverride/
  //      CancellationToken/SkillMemoryScopeDefault...
  /// <summary>need13：main|sub（默认 main，老调用零改动）。</summary>
  public string AgentRole { get; init; } = "main";
  /// <summary>need13：sub 寄宿的主会话（main 为 null）。</summary>
  public string? ParentSessionId { get; init; }
  /// <summary>need13：轮首策略快照（见 05 §3，Orchestrator 每轮填一次）。</summary>
  public bool SubAgentEnabled { get; set; } = true;
  public int SubMaxParallel { get; set; } = 3;
  public int SubMaxIterations { get; set; } = 15;
  public bool SubAllowExecCommand { get; set; } = true;
  public bool SubAllowFileWrite { get; set; } = true;
  public bool SubAllowSkillWrite { get; set; } = false;
  public bool SubAllowMemoryWrite { get; set; } = false;
}
```

`SessionTempDir` 已按 `SessionId` 隔离（`sessions/<ws>/<sess>/tmp`），子天然独立，无需改动。

## 3. 工具定义与工厂

### 3.1 `spawn_subagent`（新 `SpawnSubAgentTool.cs`，仅 Main registry 注册）

```csharp
public sealed class SpawnSubAgentTool : IToolHandler {
  public string Name => "spawn_subagent";
  public string Description =>
    "Spawn a clean-context sub-agent under current session. " +
    "Use for parallelizable independent work. " +
    "The sub cannot see main history (pass all background in 'task'), " +
    "has read-only skills/memory, cannot ask user, cannot spawn further subs. " +
    "Returns after sub finishes; multiple spawns in one turn run in parallel and barrier-wait.";
  public ToolDefinition GetDefinition() => new(Name, Description,
    "{\"type\":\"object\",\"properties\":{"
    + "\"task\":{\"type\":\"string\",\"description\":\"Sub task prompt. Include goal, scope paths, constraints, expected output format. Required.\"},"
    + "\"label\":{\"type\":\"string\",\"description\":\"Short label for UI (<=20 chars). Optional.\"},"
    + "\"model\":{\"type\":\"string\",\"description\":\"Optional model id override for sub (must be enabled). Defaults to main model.\"},"
    + "\"providerId\":{\"type\":\"string\"},"
    + "\"maxIterations\":{\"type\":\"integer\",\"description\":\"Sub turn cap, 1..50, default 15.\"}"
    + "},\"required\":[\"task\"]}");
  public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct)
    => Task.FromResult(ToolResult.Fail("[SUBAGENT_INTERNAL] spawn 由 Orchestrator barrier 承接，不应直调。"));
}
```

实际执行不在此 tool 内跑，而是 `AgentOrchestrator` 在分流处拦截 `functionName=="spawn_subagent"`（见 04 §3），此 `ExecuteAsync` 只做兜底（防 `HostBridgeFacade.ExecuteTool` 单跑误调）。参数校验（`task` 非空/`maxIterations` 钳制/`model` 存在性）由 Orchestrator 分流处做，失败即当场 `Fail` 回填，不建会话。

关闭时 description 动态追加（`GetDefinition(policy)` 重载或工厂拼串）：`" [当前已禁用：设置>智能体行为>子代理>总开关=false，调用将直接拒绝]"`。

### 3.2 `ToolRegistryFactory`（新建）

```csharp
public static class ToolRegistryFactory {
  public static ToolRegistry ForMain(SubPolicySnapshot policy) {
    var r = new ToolRegistry(registerBuiltIns: true);
    r.Register(new SpawnSubAgentTool(policy)); // 主独有
    return r;
  }
  public static ToolRegistry ForSub(SubPolicySnapshot policy) {
    var r = new ToolRegistry(registerBuiltIns: false);
    r.Register(new ReadFileTool()); r.Register(new ListDirTool());
    r.Register(new GrepSearchTool()); r.Register(new ViewImageTool());
    if (policy.SubAllowExecCommand) r.Register(new ExecCommandTool());
    if (policy.SubAllowFileWrite) { r.Register(new WriteFileTool()); r.Register(new EditFileTool()); }
    r.Register(new ReadOnlySkillTool());   // 仅 list/get，写直接 Fail（§3.3）
    r.Register(new ReadOnlyMemoryTool());  // 同上
    r.Register(new DisabledAskUserTool()); // 直接 Fail([SUBAGENT_ASK_DENIED])
    r.Register(new DisabledSpawnTool());   // 直接 Fail([SUBAGENT_NESTED_DENIED])
    return r;
  }
}
```

两种实现二选一：包装类（`ReadOnlySkillTool` 包 `SkillToolShared.ExecuteKindAsync`，写动作先拦）或 `SkillToolShared` 内鉴权（推荐后者，少两个类，见 §3.3）。`ask/spawn` 用小包装类（各 15 行）语义最清晰。

### 3.3 skill/memory 写拦截（改 `SkillToolShared.cs:ExecuteKindAsync` 入口）

```csharp
// action ∈ {create,update,delete,enable,disable} 即写；list/get 即读
bool isWrite = !string.Equals(action, "list", OrdinalIgnoreCase)
            && !string.Equals(action, "get", OrdinalIgnoreCase);
if (isWrite && string.Equals(context.AgentRole, "sub", OrdinalIgnoreCase)) {
  bool allowed = isSkill ? context.SubAllowSkillWrite : context.SubAllowMemoryWrite;
  if (!allowed)
    return ToolResult.Fail(
      $"[PERMISSION_DENIED:{(isSkill?"skill_tool":"memory_tool")}:write] 子代理禁止写 {(isSkill?"skill":"memory")}（设置>智能体行为>子代理>skill/memory写=false）。list/get 可用，本次 {action} 已拒绝。请改用只读查询，或记入总结交主代理修改。");
}
// scope 额外收紧：sub 且 scope=global 的写请求同样拒绝（即使开关开了）
```

`SkillMemoryScopeDefault` 子强制 `project`（Orchestrator 建子 ctx 时写死）。

### 3.4 ask 禁止（改 `AskUserTool.cs:ExecuteAsync` 首行）

```csharp
if (string.Equals(context.AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
  return ToolResult.Fail("[SUBAGENT_ASK_DENIED] 子代理禁止 ask_user（v1 锁定）。本次已拒绝，不会弹窗。请按保守默认继续，并在总结中注明假设，交用户在主会话确认。",
    durationMs: sw.ElapsedMilliseconds);
```

`AskUserGate` 本体不动（key 已是 sessionId，主子天然隔离，子根本到不了 gate）。

### 3.5 套娃禁止（`DisabledSpawnTool`，仅 Sub registry）

```csharp
return ToolResult.Fail("[SUBAGENT_NESTED_DENIED] 子代理内禁止再 spawn（深度=1）。本次已拒绝。请在当前子内串行完成，必要时记入总结交主代理拆分。");
```

## 4. 错误码与话术（冻结文案，评审逐字对）

统一格式：`[CODE] 原因（设置路径/配额数字）。本次已拒绝，未建会话/未弹窗。请不要重试 X，直接 Y，并在回复中 Z。`

| CODE | 触发 | 话术全文 |
|---|---|---|
| `SUBAGENT_DISABLED` | 主调 spawn 但 `SubAgentEnabled=false` | `[SUBAGENT_DISABLED] 子代理已关闭（设置>智能体行为>子代理>总开关=false）。本次 spawn 已拒绝，未创建子会话。请不要重试 spawn，直接用主代理能力继续，并在回复中告知用户可在设置中重新开启。` |
| `SUBAGENT_QUOTA_EXCEEDED` | 本轮第 N 个 spawn 超 `subMaxParallel` | `[SUBAGENT_QUOTA_EXCEEDED] 本轮子代理并发超限（max=3，本轮第4个）。本次 spawn 已拒绝。请不要重试，先处理已启动的子结果，不足部分拆到下一轮或串行直做。`（数字按快照渲染） |
| `SUBAGENT_NESTED_DENIED` | 子内 spawn | 见 §3.5 |
| `PERMISSION_DENIED:skill_tool:write` | 子 skill 写 | 见 §3.3 |
| `PERMISSION_DENIED:memory_tool:write` | 子 memory 写 | 见 §3.3 |
| `SUBAGENT_ASK_DENIED` | 子 ask | 见 §3.4 |
| `SUBAGENT_CANCELLED` | 子随主取消 | `[SUBAGENT_CANCELLED] 子 {label} 已随主取消被掐断（用户取消）。残缺结果不回填。`（主不落盘，直接 TurnCancelled，见 04 §5） |
| `SUBAGENT_VALIDATION` | task 空/超长、model 非法 | `[SUBAGENT_VALIDATION] spawn 参数非法：{task为空|task超8000字|模型xxx不存在已回退主模型}。`（模型非法时回退+warning 继续，不硬拒） |

`HintForTool("spawn_subagent")` 追加：`" Hint: 超限/关闭时不要重试 spawn；把任务拆小或串行直做。"` 落盘 `Tool(callId, errorText)` 原样进历史，LLM 下一 iteration 必见。

## 5. Prompt 预声明（改 `SystemPromptBuilder.cs`）

主策略块（稳定区尾部，轮首快照渲染，关闭/超限数字动态）：

```text
# 子代理策略（本轮快照）
- 可用：是/否（否=设置已关闭，禁止 spawn，若用户问并行请引导去设置开启）
- 并发上限：3；子轮次上限：15；子权限：exec=开 file=开 skill写=关 memory写=关 ask=禁 套娃=禁
- spawn 时必须把必要背景写进 task（子看不到主历史）；子以正常文本总结汇报，barrier 等全部完成才继续。
```

子后缀块（`BuildPreview` 后追加，仅 sub）：

```text
# 子代理约束
- 你是寄宿于主会话的子代理，会话干净，无主历史。
- 禁止修改 skill/memory（只有 list/get 可用）；禁止 ask_user（按保守默认继续并在总结注明假设）；禁止再 spawn 子代理。
- 结束时必须输出一段自然语言总结：结论/变更文件/验证方式/未完成与风险。
```

## 6. 本篇验收（给 07 用）

- [ ] 主 registry 含 spawn，子 registry 无 spawn 真身只有禁用桩。
- [ ] 子 skill/memory 写拒（4 动作 × 双工具），list/get 放行。
- [ ] 子 ask 首行拒，未进 gate（`AskUserGate.HasPending(subId)==false`）。
- [ ] 套娃拒；关闭时主 spawn 拒且未建会话（`GetSubSessionsAsync` 为空）。
- [ ] `[CODE]` 前缀逐字对；`HintForTool` 无死循环重试（mock LLM 连调 3 次 spawn 均拒后转串行）。
- [ ] Prompt 两块渲染正确，开关数字与快照一致。
