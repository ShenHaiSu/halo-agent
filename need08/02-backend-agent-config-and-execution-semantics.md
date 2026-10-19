# need08-02 后端：Agent 配置与双无限语义实现

> 前置：`01` 已冻结。本篇按文件顺序施工，冲突以本篇“精确到行”为准。

## 1. 总览与文件清单

新增 2 文件，修改 5 文件，不动 prompt/network/storage 语义。

| 动作 | 文件 | 说明 |
|------|------|------|
| 新增 | `src/Host.Desktop/Core/Models/AgentBehaviorModels.cs` | `AgentConfigDocument + AgentDefaults + AgentConfigValidationException` |
| 新增 | `src/Host.Desktop/Infrastructure/Config/AgentConfigService.cs` | 对标 `PromptConfigService.cs`：`EnsureInitialized/LoadWithMeta/GetInternal/Save/Reset` + `lock` |
| 修改 | `src/Host.Desktop/Tools/BuiltIn/ExecCommandTool.cs` | 超时解析 `0=无限` + 配置注入 + schema 文案 |
| 修改 | `src/Host.Desktop/Tools/ToolRegistry.cs` + `ToolContext` 定义处 | `ToolContext` 新增超时快照字段；`DispatchAsync` 透传（若 Context 无快照则读服务，见 §4） |
| 修改 | `src/Host.Desktop/Services/AgentOrchestrator.cs` | 每轮首读配置快照；`0=无限`循环重构；`IterationStarted(maxIterations:0)`；无限 Warn 日志 |
| 修改 | `src/Host.Desktop/Bridge/HostBridgeFacade.cs` + 新增 `HostBridgeFacade.Agent.cs` | `AgentConfigService` 注入 + `Get/Save/ResetAgentConfig` + `config:agent-changed` + 存储迁移 `EnsureInitialized` |
| 回归 | `Stage05ToolExecutionTests / Stage06AgentOrchestratorTests` | 老用例必须仍绿（常量回退路径覆盖） |

## 2. 配置模型（新文件 `Core/Models/AgentBehaviorModels.cs`）

```csharp
public class AgentConfigDocument
{
    public int Version { get; set; } = 1;
    public int ExecDefaultTimeoutMs { get; set; } = AgentDefaults.DefaultTimeoutMs; // 30000, 0=无限
    public int ExecMaxTimeoutMs { get; set; } = AgentDefaults.MaxTimeoutMs;         // 300000, 0=不限上限
    public int MaxIterations { get; set; } = AgentDefaults.MaxIterations;          // 25, 0=无限
    public string Scope { get; set; } = "global";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    // 后续杂项扩展位：本期只声明不使用，反序列化宽容忽略未知字段即可
    // public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public static class AgentDefaults
{
    public const int DefaultTimeoutMs = 30000;   // 与 ExecCommandTool 旧常量同值
    public const int MaxTimeoutMs = 300000;      // 与 ExecCommandTool 旧常量同值
    public const int MaxIterations = 25;         // 与 AgentOrchestrator 旧默认同值
    public const int HardTimeoutCeilMs = 3600000; // 有限值硬顶 1h（01 §6）
    public const int HardIterationsCeil = 500;    // 有限值硬顶 500
}

public sealed class AgentConfigValidationException : Exception
{
    public string Field { get; }
    public AgentConfigValidationException(string field, string message) : base(message) { Field = field; }
}
```

JSON 一律小驼峰（复用 `HaloJsonOptions.IndentedCamelCase`；`Save/Reset` 入参走 `AgentJsonOptions` 宽容解析，对标 `PromptJsonOptions.Lenient`，至少做到 `PropertyNameCaseInsensitive=true + DateTime 宽容`）。

## 3. 配置服务（新文件 `Infrastructure/Config/AgentConfigService.cs`，逐行对标 `PromptConfigService.cs`）

- `AgentConfigFilePath => Path.Combine(_storagePathManager.ConfigDirectory, "agent.json")`。
- `EnsureInitialized()`：建目录 + 缺失写默认（`AtomicFileWriter.WriteJsonAtomic`）。
- `LoadWithMeta() => (doc, isDefault, fallbackReason)`：缺失→写默认返回 `(def,true,null)`；空文件→`(def,true,"agent.json 为空，已回退默认")`；损坏→拷贝 `agent.json.corrupt-yyyyMMddHHmmss` 后 `(def,true,"agent.json 损坏，已回退默认…")`；超限/非法→`(def,true,"agent.json 配置超限（字段 …），已回退默认")`。**永不抛错中断会话**。
- `SaveAgentConfig(incoming)`：`lock(_syncLock)` → `NormalizeAndValidate(forSave:true)` → `UpdatedAtUtc=UtcNow` → `Copy→.bak`（失败仅 Warn 继续）→ 原子写 → `Clone` 返回。
 - `ResetAgentConfig(string? field)`：已决策**按项恢复**（对标 `PromptConfigService.ResetPromptConfig(blockId)`）。空/`all`/`default` → 全量恢复默认；`defaultTimeout` 系 → 仅该项回 `30000`；`maxTimeout` 系 → 仅该项回 `300000`；`iterations` 系 → 仅该项回 `25`（主 ID + 别名见 01 §4.3，大小写不敏感，去空格）；未知 → 抛 `AgentConfigValidationException("field", $"未知恢复项：{field}")`。实现：`lock` 内读当前 → 改一项 → 走 `SaveAgentConfig`（复用校验+`.bak`+原子写）。
- `NormalizeAndValidate` 校验矩阵（`forSave` 与非 `forSave` 共用，失败抛 `AgentConfigValidationException(field,msg)`）：

| 字段 | 合法 | 非法→异常 field |
|------|------|----------------|
| `ExecDefaultTimeoutMs` | `0` 或 `1..3600000` | 负数/`>3600000` → `execDefaultTimeoutMs` |
| `ExecMaxTimeoutMs` | `0` 或 `1..3600000` | 负数/`>3600000` → `execMaxTimeoutMs` |
| `MaxIterations` | `0` 或 `1..500` | 负数/`>500` → `maxIterations` |
| 组合 | `M>0 && D>M` → 非法 | → `execDefaultTimeoutMs`（msg 必须含 `默认超时不得大于上限`） |
| `Version/Scope/UpdatedAtUtc` | 归一：`Version=1, Scope="global", default时间→UtcNow` | 永不抛错 |

## 4. 命令执行 `0=无限`（改 `ExecCommandTool.cs:47-104,185-209`）

### 4.1 配置如何到达 Tool

推荐方案（最小侵入、可单测）：扩展 `ToolContext`（与 `ToolRegistry.DispatchAsync` 同改）：

```csharp
public sealed class ToolContext {
    // …现有 WorkspaceRoot/WorkspaceId/SessionId/DataRoot/CancellationToken/ConfiguredPythonPath…
    public int? ExecDefaultTimeoutMsOverride { get; set; }  // null=读服务/常量回退
    public int? ExecMaxTimeoutMsOverride { get; set; }
}
```

- `AgentOrchestrator` 每次调度前填快照（§5 的轮首快照中的两项）。
- `HostBridgeFacade.ExecuteTool`（调试单跑）现场读 `AgentConfigService.GetInternal()` 填快照。
- `ToolRegistry.DispatchAsync` 不做读取，只透传 `context`；`ExecCommandTool` 内 `null→回退常量`，保证老单测（不填快照）行为不变。

替代方案（否决备查）：Tool 内直接读文件/单例服务——破坏可测性且每 call 读盘，并发 save 竞态难控。

### 4.2 解析算法（替换 `:99-104`）

```csharp
int cfgD = context.ExecDefaultTimeoutMsOverride ?? DefaultTimeoutMs; // DefaultTimeoutMs 常量保留
int cfgM = context.ExecMaxTimeoutMsOverride ?? MaxTimeoutMs;
int timeoutMs = parsedOrMissing; // 缺省/非数字 => cfgD；<0 => cfgD（01 §4.1）
bool infinite = false;
if (timeoutMs == 0) infinite = true;                       // 本 call 显式无限，跳过上限
else if (cfgM == 0) timeoutMs = timeoutMs;                 // 上限关闭；有限硬顶已在 save 拦，本处再 clamp HardCeil 兜底
else timeoutMs = Math.Min(timeoutMs, cfgM);
if (!infinite && timeoutMs <= 0) timeoutMs = cfgD;          // 防御（理论不可达）
// 硬顶兜底（防手改 Bridge JSON 绕过 save 校验直调 ExecuteTool）：
// if (!infinite) timeoutMs = Math.Clamp(timeoutMs, 1, AgentDefaults.HardTimeoutCeilMs);
```

`AgentDefaults` 与 `ExecCommandTool` 常量值保持一致，Tool 侧引用 `AgentDefaults.HardTimeoutCeilMs` 做最终钳制（避免循环依赖：`AgentBehaviorModels` 在 Core 层，Tool 可引用）。

### 4.3 执行分支（改 `:185-209`）

```csharp
if (infinite)
{
    HaloLogger.Tool.Info($"exec_command infinite wait (call {call.Id})，仅用户取消可终止");
    await process.WaitForExitAsync(token).ConfigureAwait(false); // 不建 timeoutCts
    // 直接跳到“正常退出”收尾逻辑（流 15s 有界等待 + 解码 + Guard），不进超时分支
}
else
{
    using var timeoutCts = ...; timeoutCts.CancelAfter(timeoutMs);
    try { await process.WaitForExitAsync(timeoutCts.Token); }
    catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* 旧超时分支原样 */ }
}
```

注意：`token` 仍是 `linkedCts(userCancel+contextCancel)`，取消路径 `job.Dispose()+TryKill` 必须在 `catch(OperationCanceledException)` 总分支保留，无限模式取消同样杀树。

### 4.4 schema 文案（改 `:35`）

由静态 `"Defaults to 30000 (30s), maximum 300000 (5m)."` 改为 `"0 = infinite wait (cancellable by user). If omitted defaults to server config (default 30000). Positive values are capped by server max (default 300000, 0 = no cap)."`。不做动态注入（`GetDefinitions` 无 context，不改签名）。

## 5. 轮次 `0=无限`（改 `AgentOrchestrator.cs:61-80,161-162,227,447-459,534-536`）

- 新增可选依赖（构造函数末尾追加，保持旧签名兼容）：`AgentConfigService? agentConfigService = null`。`HostBridgeFacade` 透传；老单测不传则走旧 `maxIterations` 构造值。
- 轮首快照（`RunAgentTurnAsync` 的 `(1)追加user之后、(2)循环之前`，只读一次）：
  ```csharp
  int effectiveMax = MaxIterations;
  int cfgD = 0, cfgM = 0;
  try {
      var cfg = _agentConfigService?.GetInternal() ?? new AgentConfigDocument{
          ExecDefaultTimeoutMs = AgentDefaults.DefaultTimeoutMs,
          ExecMaxTimeoutMs = AgentDefaults.MaxTimeoutMs, MaxIterations = MaxIterations };
      effectiveMax = cfg.MaxIterations; // 0=无限；负数理论不可达（save 已拦），防御性 <=0 一律按无限? 不——负数按 25 回退并 Warn
      cfgD = cfg.ExecDefaultTimeoutMs; cfgM = cfg.ExecMaxTimeoutMs;
  } catch { /* 回退构造值+常量 */ }
  if (effectiveMax == 0) HaloLogger.App.Warn($"[Agent] infinite iterations for session {sessionId}，仅取消/模型停手可结束");
  ```
- 循环重构（保持事件契约）：`for (int iteration = 1; effectiveMax <= 0 || iteration <= effectiveMax; iteration++)`。循环内 `if (iteration >= effectiveMax && effectiveMax > 0)` 才进截断分支（原 `:448` 加 `effectiveMax > 0` 条件）；方法末尾兜底 `:534-536` 改为 `if (effectiveMax > 0)` 才 `Truncated`，否则理论不可达（无限循环内必 return）。
- `IterationStarted` payload 原样透出 `maxIterations = effectiveMax`（`0` 由前端渲染 `∞`，见 03）。
- 截断通知文案不变；无限模式下**永不落**该通知。
- 每次 `DispatchAsync` 前的 `ToolContext` 填 `ExecDefaultTimeoutMsOverride=cfgD, ExecMaxTimeoutMsOverride=cfgM`。
- `MaxIterations` 公开属性语义：保持构造值（兼容老测试 `Assert.Equal(3,…)`）；新增 `EffectiveMaxIterations` 内部字段或日志字段？推荐新增只读属性 `ResolvedMaxIterations` 供测试断言（老属性不动）。

## 6. Bridge 与装配（新 `Bridge/HostBridgeFacade.Agent.cs` + 改 `HostBridgeFacade.cs:31-32,50,65,242-243`）

- 字段/属性/构造：`_agentConfigService` + `AgentConfigService` 公开属性；构造末追加 `AgentConfigService? agentConfigService = null` 并 `?? new AgentConfigService(_storageManager)`。
- 三方法（对标 `HostBridgeFacade.Prompt.cs:29-97`，错误码前缀 `AGENT_`）：
  - `GetAgentConfig() → BridgeResponse<AgentConfigWithMeta>`（含 `isDefault/fallbackReason`）。
  - `SaveAgentConfig(string configJson)` → 宽容反序列化 → `SaveAgentConfig` → `JsEventDispatcher.Publish("config:agent-changed", saved)` → 成功语 `"智能体行为配置已保存，下一轮对话起生效"`；`AgentConfigValidationException → Fail("AGENT_VALIDATION", $"字段 [{field}]：{msg}")`。
   - `ResetAgentConfig(string? field)` → 按项/全量恢复默认 + 同事件；未知项 `Fail("AGENT_VALIDATION", "未知恢复项：…")`。
- `ChangeStoragePath` 成功后加 `try { _agentConfigService.EnsureInitialized(); } catch { }`（与 prompt 同行）。
- `StartAgentTurn` 本体不动（快照在 Orchestrator 内读，保证“保存不中断本轮”）。

## 7. 日志与审计

- 无限进入：`Tool.Info infinite wait` + `App.Warn infinite iterations`（带 sessionId），便于事后审计是谁开的无限。
- 保存/恢复：`App.Info "[Agent] agent.json 已保存（原子写盘）"`，对标 Prompt。
- 损坏回退：`App.Warn "[Agent] agent.json …已回退默认"` + 保留 corrupt 文件名进日志。

## 8. 本篇验收（给 04 用）

 - [ ] 缺省 agent.json 自动生成且三默认值正确。
 - [ ] 校验矩阵（§3 表格每一行）单测全过，错误 field 定位正确。
 - [ ] `T=0/D=0/M=0` 组合矩阵（01 §4.1 表）解析单测全过。
 - [ ] 按项恢复：`defaultTimeout/maxTimeout/iterations` 各改偏后单项 reset 只回该项；`空/all` 全回；未知项拒绝。
 - [ ] 无限 exec 真不设 timeout（mock 进程 60s 不杀 + 取消 1s 内杀树）。
 - [ ] 无限 iterations 真不截断（mock 永远返回 toolcall，前 N 轮无 Truncated；取消即停）。
 - [ ] 老 Stage05/06 用例零修改全绿。
