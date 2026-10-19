# 02 Goal 后端设计（模型 × 存储 × 工具 × 编排 × 桥接）

> 触点：`Core/Models/SessionGoal.cs`（新）、`Services/GoalService.cs`（新）、`Tools/BuiltIn/GoalTool.cs`（新）、`Tools/ToolRegistry.cs`、`Tools/ToolRegistryFactory.cs`、`Services/SystemPromptBuilder.cs`、`Services/AgentOrchestrator.cs`、`Bridge/HostBridgeFacade.Goal.cs`（新 partial）、`Infrastructure/Storage/SessionRepository.cs`（扩展）、`Infrastructure/Config/AgentConfigService.cs`（不动）。

## 1. 设计总览

```text
LLM --tool_call(goal_tool)--> ToolRegistry.DispatchAsync --> GoalTool.ExecuteAsync
        │ ToolContext{AgentRole, WorkspaceId, SessionId, ParentSessionId}
        ├─ main: GoalService.ApplyAction(...) → goal.json 原子写 → ToolResult(ok JSON)
        │                                   └─ JsEventDispatcher goal:updated
        └─ sub : 仅 get 放行；写动作 → [GOAL_DENIED] 自愈文本（不落盘）
轮首: Orchestrator 读 GoalService.LoadGoal → SystemPromptBuilder.BuildCore(goalBlock) → 稳定前缀
前端人工改: hostBridge.getGoal/setGoal → 同一 GoalService → 同一事件
```

## 2. 数据模型（新文件 `Core/Models/SessionGoal.cs`）

```csharp
public sealed class GoalMilestone {
    public string Id { get; set; } = "ms_" + Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public bool Done { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
public sealed class SessionGoal {
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<GoalMilestone> Milestones { get; set; } = new();
    public int Percent { get; set; } // 0–100，钳制
    public string Note { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "main"; // main|user|sub-read
    public bool HasContent => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Description) || Milestones.Count > 0;
    public int DoneCount => Milestones.Count(m => m.Done);
}
```

- JSON 宽容：沿用 `HaloJsonOptions.IndentedCamelCase`；缺字段即默认（对标 `AgentConfigDocument` 兼容策略）。
- `percent` 钳制 `Math.Clamp(v,0,100)`；`complete_milestone` 后 `percent = total==0 ? percent : round(done*100/total)`。
- `UpdatedBy` 取值：`main`（LLM 主）、`user`（浮动窗人工）、写拒时不更新。

## 3. `GoalService`（新，`Services/GoalService.cs`）

```csharp
public sealed class GoalService {
    public GoalService(StoragePathManager storage);
    public SessionGoal LoadGoal(string workspaceId, string sessionId); // 缺失→空Goal；损坏→空Goal+抛 GoalCorruptException(内含path)
    public (SessionGoal goal, bool corrupted) TryLoadGoal(...);
    public SessionGoal ApplyAction(SessionGoal cur, string action, JsonElement args); // 纯函数，可单测：set/update/progress/add_milestone/complete_milestone/clear/get
    public SessionGoal SaveGoal(string ws, string sess, SessionGoal goal, string updatedBy); // 原子写 + UpdatedAtUtc=now
    public string GoalFilePath(string ws, string sess) => Path.Combine(repo.GetSessionDirectory(ws,sess), "goal.json");
}
```

- 原子写：复用 `AtomicFileWriter.WriteJsonAtomic`（与 `agent.json` 同语义，先 `.bak` 再写，见 `AgentConfigService.cs:52-63`）。
- 线程安全：复用仓储 `_sessionLocks` 思路，本 Service 内 `ConcurrentDictionary<string,SemaphoreSlim>` 按 `ws/sess` 加锁（读可并行、写串行；简单起见读写同锁）。
- 删除：不单独删文件；`DeleteSessionTreeAsync` 删目录即带走（E5 自然满足）；`clear` 动作=写空 Goal（保留文件，`HasContent=false`）。

`ApplyAction` 矩阵：

| action | 参数 | 语义 |
|---|---|---|
| `get` | — | 原样返回 |
| `set` | `title, description, milestones[string[]], percent?, note?` | 全量覆盖（milestones 重建 id） |
| `update` | `title?, description?, note?` | 非空即改；空串=清空该字段（与 skill/memory 语义对齐要在 description 注明） |
| `progress` | `percent, note?` | 只改进度+注记 |
| `add_milestone` | `title` | 追加未完成里程碑 |
| `complete_milestone` | `id|title` 任一 | 命中置 done+重算 percent；未命中→ `GOAL_MILESTONE_MISSING` 自愈文本 |
| `clear` | — | 空 Goal |

## 4. `GoalTool`（新，`Tools/BuiltIn/GoalTool.cs`）

```csharp
public sealed class GoalTool : IToolHandler {
    public string Name => "goal_tool";
    public string Description => "Manage the main-session goal ... actions: get/set/update/progress/add_milestone/complete_milestone/clear. Sub-agents: get only.";
    public ToolDefinition GetDefinition() => new(Name, Description,
      "{\"type\":\"object\",\"properties\":{"
      + "\"action\":{\"type\":\"string\",\"description\":\"get|set|update|progress|add_milestone|complete_milestone/clear. Default get.\"},"
      + "\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},"
      + "\"milestones\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
      + "\"milestone\":{\"type\":\"string\",\"description\":\"id or title for complete_milestone\"},"
      + "\"percent\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},"
      + "\"note\":{\"type\":\"string\"}"
      + "},\"required\":[\"action\"]}");
    public async Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct);
}
```

执行流程（`ExecuteAsync` 内）：

1. 解析 `action`（缺省 `get`，大小写不敏感；未知→ `GOAL_VALIDATION` 自愈文本）。
2. `ws/sess` 归一：子会话请求 Goal **一律读父**（`context.ParentSessionId ?? context.SessionId`；子无父兜底读自身，见 E-子直读）。
3. 子写拦截：`context.AgentRole==sub && action!=get` → `ToolResult.Fail("[GOAL_DENIED] 子代理仅可 get 查看父目标，不可写入。本次已拒绝，请在总结中引用目标文本交主代理修改。")`。
4. 主写：`Load→ApplyAction→SaveGoal(updatedBy:"main")→Publish goal:updated→ToolResult.Ok(goalJson 精简版)`。
5. `get`：返回精简 JSON `{ title, description, percent, note, milestones[{id,title,done}], updatedAtUtc }`（2000 字截断，超长走 `ToolOutputGuard` 既有落盘）。

注册：

- `ToolRegistry` 构造新增 `Register(new GoalTool())`（14→15 工具；注释数同步改）。
- `ToolRegistryFactory.ForMain` 保持（`registerBuiltIns:true` 已含）；`ForSub` 新增 `r.Register(new GoalReadOnlyTool())` 只读桩——或复用同一 `GoalTool` 靠 `AgentRole` 内鉴权（**推荐后者**：少一个类，description 动态追加 `[子会话内仅 get 可用]`；见 Spawn 禁用桩 vs 内鉴权两种先例，本处选内鉴权以减少 schema 分叉）。

## 5. 提示词注入（`SystemPromptBuilder.cs` 扩展）

- `BuildPreview/BuildCore` 新增可选参 `string? goalBlock`（默认 null，老调用零改动；对标 `skillIndexBlock/memoryPinnedBlock` 模式，见 `SystemPromptBuilder.cs:87-100`）。
- `BuildGoalBlock(SessionGoal? g)` 纯函数：
  - 空/`!HasContent` → `string.Empty`（不占位）。
  - 非空 → `## 5. Session Goal（本轮目标，goal_tool 可改）\n- 标题：…\n- 描述：…\n- 进度：{percent}%（{done}/{total} 里程碑）{note}\n- 里程碑：- [x]/[ ] …` + 末尾 `Goal 变更经 goal_tool 下一轮生效；子会话只读。`
- 排序：`ordered` 中插在 `("memoryPinned",…)` 之后、`("ephemeral",…)` 之前，`stable:true`（计入稳定前缀与缓存预测）。
- 子会话：Orchestrator 为 sub 构造 prompt 时同样传父 GoalBlock，并在其后追加一行 `（你是子代理：仅可 goal_tool/get 查看，不可写入。）`。
- `Orchestrator` 轮首（`AgentOrchestrator.cs:167-199` 快照区旁）：`try { goal = goalService.LoadGoal(ws, mainSessionId); goalBlock = BuildGoalBlock(goal);} catch → null`（失败回空不断会话）。

## 6. Bridge（新 partial `HostBridgeFacade.Goal.cs`）

```csharp
public Task<BridgeResponse<SessionGoal>> GetGoal(string workspaceId, string sessionId);
public Task<BridgeResponse<SessionGoal>> SaveGoal(string workspaceId, string sessionId, string goalJson); // 人工全量保存，UpdatedBy=user
public Task<BridgeResponse<bool>> ClearGoal(string workspaceId, string sessionId);
```

- 入参 `sessionId` 若为子 id：统一归一到父（读 meta `ParentSessionId`，与工具侧一致）。
- `SaveGoal` 解析失败→ `GOAL_VALIDATION`；写成功→ `Publish goal:updated`（与工具侧同一事件，前端不区分来源）。
- 事件载荷：`JsEventDispatcher.Publish("goal:updated", new { sessionId, goal = slim })`；前端 `haloEventBus.on('goal:updated', …)`（hostBridge 透传 `agent:turn-event` 之外的第二事件类型，需确认 `JsEventDispatcher` 支持任意 kind——need13 已有 `SubAgentsBarrierReleased` 先例，可直接复用）。

## 7. 并发与异常

- 同会话并发写：`GoalService` 按 `ws/sess` 信号量串行；后写覆盖先写（LWW），`UpdatedAtUtc` 判定。
- 执行中本轮不受影响：Orchestrator 轮首快照 GoalBlock，本轮内 `goal_tool` 写入仅事件通知前端，**不重拼本轮 system prompt**（与配置“下一轮生效”一致）。
- 文件损坏：`TryLoadGoal → (empty, corrupted:true)`；`GetGoal` 返回 `ok:true + data:empty + message:"目标文件损坏，已视为空，可重置"`（message 横幅位）；工具侧 `get` 同样返回空 Goal + 首行注记。

## 8. 单测清单（对应 07 G-后端）

1. `ApplyAction` 七动作矩阵（含越界钳制/里程碑重算/未命中自愈）。
2. 子写拒：`AgentRole=sub` 六写动作全拒且文件 mtime 不变。
3. 子读父：`ParentSessionId` 归一正确。
4. 损坏自愈：写半截 JSON→`TryLoad` 空态 + corrupted 标记。
5. `BuildGoalBlock` 空/满/超长（里程碑 50 条截断 20 + `…余N条`）。
6. 老 `ToolRegistry` 工具数 14→15；`ForSub` 含 `goal_tool` 且 description 含只读注记。
