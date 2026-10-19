# need13-05 后端：配置与桥接（agent.json 7 字段 + Bridge + mock）

> 前置：`01` 已冻结。本篇按文件顺序施工，冲突以本篇“精确到行”为准。

## 1. 总览与文件清单

| 动作 | 文件 | 说明 |
|---|---|---|
| 修改 | `src/Host.Desktop/Core/Models/AgentBehaviorModels.cs` | `AgentConfigDocument` 加 7 字段；`AgentDefaults` 加 4 常量 |
| 修改 | `src/Host.Desktop/Infrastructure/Config/AgentConfigService.cs` | `CreateDefault/Clone/NormalizeAndValidate/ResetAgentConfig` 同步扩展 |
| 修改 | `src/Host.Desktop/Bridge/HostBridgeFacade.Agent.cs` | 错误码前缀 `AGENT_` 不变；`Save/Reset` 走新校验 |
| 新增 | `src/Host.Desktop/Bridge/HostBridgeFacade.SubAgent.cs` | `GetSubSessions/CancelSubAgent/CancelTree/GetSubRuntime` |
| 修改 | `src/Web.Client/src/bridge/hostBridge.ts` | `AgentConfigDocument` 加 7 字段 + 4 新桥方法 + mock |
| 回归 | `Stage08AgentConfigTests` | 老用例零修改全绿（缺字段回默认路径覆盖 E1） |

## 2. 模型（改 `AgentBehaviorModels.cs`）

```csharp
public class AgentConfigDocument {
  public int Version { get; set; } = 1;
  public int ExecDefaultTimeoutMs { get; set; } = 30000;
  public int ExecMaxTimeoutMs { get; set; } = 300000;
  public int MaxIterations { get; set; } = 25;
  // need13：子代理（缺字段=老文件，全部回默认，不算损坏）
  public bool SubAgentEnabled { get; set; } = true;
  public int SubMaxParallel { get; set; } = 3;
  public int SubMaxIterations { get; set; } = 15;
  public bool SubAllowExecCommand { get; set; } = true;
  public bool SubAllowFileWrite { get; set; } = true;
  public bool SubAllowSkillWrite { get; set; } = false;
  public bool SubAllowMemoryWrite { get; set; } = false;
  public bool SubAllowAskUser { get; set; } = false; // v1 锁定 false
  public string Scope { get; set; } = "global";
  public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
public static class AgentDefaults {
  public const int DefaultTimeoutMs = 30000;
  public const int MaxTimeoutMs = 300000;
  public const int MaxIterations = 25;
  public const int HardTimeoutCeilMs = 3600000;
  public const int HardIterationsCeil = 500;
  public const int SubMaxParallelDefault = 3;
  public const int SubMaxParallelCeil = 8;
  public const int SubMaxIterationsDefault = 15;
  public const int SubMaxIterationsCeil = 50;
  public const int SubHardParallelCeil = 8;
  public const int SubHardIterationsCeil = 100;
}
```

## 3. 服务（改 `AgentConfigService.cs`）

- `CreateDefault()`：7 字段按上表默认。
- `Clone()`：JSON 往返（新字段自动携带）。
- `NormalizeAndValidate(incoming, forSave)` 追加：

```csharp
if (doc.SubMaxParallel < 1 || doc.SubMaxParallel > AgentDefaults.SubHardParallelCeil)
  throw new AgentConfigValidationException("subMaxParallel",
    $"子代理并发非法：{doc.SubMaxParallel}（允许 1..{AgentDefaults.SubHardParallelCeil}）");
if (doc.SubMaxIterations < 1 || doc.SubMaxIterations > AgentDefaults.SubHardIterationsCeil)
  throw new AgentConfigValidationException("subMaxIterations",
    $"子代理轮次非法：{doc.SubMaxIterations}（允许 1..{AgentDefaults.SubHardIterationsCeil}）");
// v1 锁定：true 归一为 false + Warn（不抛错，保证手改不炸会话）
if (doc.SubAllowAskUser) {
  doc.SubAllowAskUser = false;
  HaloLogger.App.Warn("[Agent] subAllowAskUser=true 已归一为 false（v1 锁定子禁问）");
}
doc.Scope = "global"; doc.Version = 1; // 存量归一保留
```

- `ResetAgentConfig(field)` 新增主 ID（大小写不敏感，去空格）：

| 项 | 主 ID | 别名 | 恢复为 |
|---|---|---|---|
| 总开关 | `subEnabled` | `subAgentEnabled/sub-enabled` | `true` |
| 并发 | `subParallel` | `subMaxParallel/sub-parallel` | `3` |
| 子轮次 | `subIterations` | `subMaxIterations/sub-iterations` | `15` |
| 权限 4+1 项 | `subPerms` | `sub-perms/subPermissions` | `exec=true,file=true,skill=false,mem=false,ask=false` |
| 全部 | 空/`all`/`default` | — | 3 旧 + 7 新全回默认 |

未知 `field` 仍抛 `AGENT_VALIDATION(未知恢复项)`。损坏/空/缺失语义不变（回默认 + `fallbackReason` + `corrupt` 保留）。

轮首快照（`AgentOrchestrator` 追加 user 后、循环前，与 `effectiveMax/cfgD/cfgM` 同批只读一次，见 04 §3）：

```csharp
var cfg = _agentConfigService?.GetInternal() ?? new AgentConfigDocument();
policy = new SubPolicySnapshot(cfg); // 7 字段一次拷贝，本轮内 spawn/skill/memory 判定全用它
```

生效时机：保存后下一轮起生效；barrier 等待中变更不影响本轮。

## 4. 桥接（新 `HostBridgeFacade.SubAgent.cs` + 改 `HostBridgeFacade.cs`）

```csharp
public Task<BridgeResponse<List<SessionMetadata>>> GetSubSessions(string workspaceId, string parentSessionId);
public Task<BridgeResponse<bool>> CancelSubAgent(string workspaceId, string subSessionId);
public Task<BridgeResponse<bool>> CancelAgentTree(string mainSessionId); // = CancelTree（诊断卡整树掐断）
public Task<BridgeResponse<SubRuntimeInfo>> GetSubRuntime(string workspaceId, string parentSessionId);
// SubRuntimeInfo { subs:[{subId,label,status,iteration,tool,elapsedMs}], policy:{enabled,maxParallel,...}, lastRejection:{code,atUtc,callId} }
```

- `CancelAgentTurn(sessionId)` 本体改为调 `CancelTreeAsync`（04 §5），签名不变，前端零改。
- `StartAgentTurn` 重入先 `CancelTree(mainId)`（防旧子泄漏）。
- `DeleteSession` 主入口改调 `DeleteSessionTreeAsync`（02 §3.3）。
- `JsEventDispatcher.Publish("config:agent-changed", saved)` 在 Save/Reset 后照常，前端 `storage:changed` 监听重载（含新字段）。

## 5. 前端桥与 mock（改 `hostBridge.ts`）

```ts
export interface AgentConfigDocument {
  version:number; execDefaultTimeoutMs:number; execMaxTimeoutMs:number; maxIterations:number;
  subAgentEnabled:boolean; subMaxParallel:number; subMaxIterations:number;
  subAllowExecCommand:boolean; subAllowFileWrite:boolean;
  subAllowSkillWrite:boolean; subAllowMemoryWrite:boolean; subAllowAskUser:boolean;
  scope:string; updatedAtUtc:string; isDefault?:boolean; fallbackReason?:string|null;
}
// HostBridge 追加：
getSubSessions: (ws:string, parent:string) => Promise<BridgeResponse<SessionMetadata[]>>;
cancelSubAgent: (ws:string, sub:string) => Promise<BridgeResponse<boolean>>;
cancelAgentTree: (main:string) => Promise<BridgeResponse<boolean>>;
getSubRuntime: (ws:string, parent:string) => Promise<BridgeResponse<SubRuntimeInfo>>;
```

mock（与 `getPromptConfig` mock 同区）：默认 7 字段按 §2 默认回显；`save` 回显 incoming；`reset(field?)` 按 §3 表只回该项/全回；`getSubSessions` 返 `[]`，`getSubRuntime` 返空表（明确注释 `// mock 仅示意，联调以真后端为准`）。

## 6. 本篇验收（给 07 用）

- [ ] 缺字段老文件读出 7 默认，无横幅，会话正常。
- [ ] `subMaxParallel=0/9`、`subMaxIterations=0/101` 分别拒绝且 field 正确。
- [ ] `subAllowAskUser=true` 保存后读回 false + Warn 日志。
- [ ] 按项恢复 4 组各只回该组；`空/all` 全回 10 字段。
- [ ] 保存后本轮 barrier 不受影响，下一轮新策略生效。
- [ ] mock 与真后端同字段同文案。
