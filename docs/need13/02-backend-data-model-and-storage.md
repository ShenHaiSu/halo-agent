# need13-02 后端：数据模型与存储（父子寄宿会话）

> 前置：`01` 已冻结。本篇按文件顺序施工，冲突以本篇“精确到行”为准。

## 1. 总览与文件清单

| 动作 | 文件 | 说明 |
|---|---|---|
| 修改 | `src/Host.Desktop/Core/Models/WorkspaceModels.cs` | `SessionMetadata` 加 5 字段（全可选，老 JSON 缺省即 main） |
| 修改 | `src/Host.Desktop/Services/WorkspaceSessionService.cs` | `CreateSession` 重载；`GetSessionsByWorkspaceAsync` 默认滤子；新增 `GetSubSessions/DeleteSessionTree`；`Branch` 不继承父子链 |
| 修改 | `src/Host.Desktop/Infrastructure/Storage/SessionRepository.cs` | 无结构变更；`DeleteSessionAsync` 复用；`ExportSessionMarkdown` 主导出附子 summary（可选增强） |
| 不动 | `messages.jsonl` 行格式、`meta.json` 必填键、`attachments/` 布局 | 零迁移 |

存储保持**扁平**：`sessions/<ws>/<sessId>/messages.jsonl+meta.json+attachments/` 不变，子也是平级目录。父子关系只靠 `sessions-index.json` 里 `ParentSessionId` 字段 + 内存 `SubAgentRegistry` 加速。理由：复用 `_sessionLocks[sessionId]` 分桶锁、原子重写语义、附件隔离、`ImageAttachmentStore`，零迁移。

## 2. 模型（改 `WorkspaceModels.cs:SessionMetadata`）

```csharp
/// <summary>need13：父会话 ID（null/空 = Main；非空 = 寄宿于该主的子会话）。</summary>
public string? ParentSessionId { get; set; }
/// <summary>need13：main|sub（缺省 main，老数据兼容）。</summary>
public string AgentRole { get; set; } = "main";
/// <summary>need13：spawn 时 label（≤20 字，索引用）。</summary>
public string? SubLabel { get; set; }
/// <summary>need13：pending|running|succeeded|failed|cancelled（诊断用，内存+落盘双写）。</summary>
public string? SubStatus { get; set; }
/// <summary>need13：由主轮哪次 tool_call 创建（callId，链路追踪）。</summary>
public string? CreatedByCallId { get; set; }
/// <summary>need13：task 截断 2000 字（索引用，全文不存 meta）。</summary>
public string? TaskPrompt { get; set; }
```

序列化：小驼峰（`HaloJsonOptions.IndentedCamelCase`），`null` 容忍。老 `meta.json` 无此 6 键 → 反序列化即 `null/main`，行为等价 main，无需迁移脚本。

`SessionHash`：子独立生成 `ComputeDeterministicHash16($"{workspaceId}:{subSessionId}")`（复用 `CreateSessionAsync` 现有逻辑），天然隔离 `${session-hash}` 与 prompt-cache。

## 3. 服务（改 `WorkspaceSessionService.cs`）

### 3.1 `CreateSessionAsync` 新增重载（老签名保留）

```csharp
public Task<SessionMetadata> CreateSessionAsync(string workspaceId, string? title = null,
  string? providerId = null, string? modelId = null, CancellationToken ct = default)
  => CreateSubSessionAsync(workspaceId, title, providerId, modelId,
     parentSessionId: null, agentRole: "main", subLabel: null, createdByCallId: null, taskPrompt: null, ct);

public async Task<SessionMetadata> CreateSubSessionAsync(string workspaceId, string? title,
  string? providerId, string? modelId, string? parentSessionId, string agentRole,
  string? subLabel, string? createdByCallId, string? taskPrompt, CancellationToken ct = default)
{
  // 1. 复用现有 CreateSessionAsync 建 meta（含 SessionHash/时间戳/index 落盘）
  // 2. 若 parentSessionId 非空：meta.ParentSessionId/AgentRole="sub"/SubLabel/SubStatus="pending"/
  //    CreatedByCallId/TaskPrompt(截2000)/TitleLocked=true（子标题不参与自动精炼，见 §5）
  // 3. SaveSessionMetaAsync + 刷新 index
}
```

子标题规则：`"sub: " + (label ?? task前12字)`，`TitleLocked=true`。主标题逻辑（T1 首句前 5 字 + T2 精炼）跳过 `AgentRole=sub` 的会话（改 `SessionTitle` 相关 `Append` 处加 `if (meta.AgentRole=="sub") return;` 一行）。

### 3.2 查询：默认只主 + 显式查子

```csharp
public async Task<List<SessionMetadata>> GetSessionsByWorkspaceAsync(workspaceId, ct)
  // 现有排序不变，末尾加 .Where(s => s.AgentRole != "sub")；
public Task<List<SessionMetadata>> GetSubSessionsAsync(string workspaceId, string parentSessionId, ct)
  // index 全量读后 Where(ParentSessionId == parent)，按 CreatedAtUtc 正序；
public Task<SessionMetadata?> GetSubSessionAsync(...) // 单查供诊断卡；
```

前端侧边栏、标题精炼、用量顶栏默认走“只主”口径，子只在展开/诊断时拉取。

### 3.3 删除：级联树

```csharp
public async Task<bool> DeleteSessionTreeAsync(string workspaceId, string sessionId, ct)
{
  // 1. var subs = await GetSubSessionsAsync(ws, sessionId);
  // 2. 通知 Coordinator.CancelTree(sessionId)（无 Coordinator 引用时跳过，由 Bridge 层先调，见 04 §5）；
  // 3. foreach sub: await _sessionRepository.DeleteSessionAsync(ws, sub.Id);
  // 4. await _sessionRepository.DeleteSessionAsync(ws, sessionId);
}
```

`DeleteSessionAsync(main)` 的 Bridge 入口改调 `DeleteSessionTreeAsync`。`RemoveWorkspaceAsync` 不变（整目录删天然含子）。

### 3.4 `Branch/Truncate/Heal` 规则

- `BranchSessionAsync`：新会话 `ParentSessionId=null, AgentRole=main`（分支永远是新 main，不继承父子链），`prefix` 拷贝不变。
- `Truncate/DeleteMessage/Heal`：按 `sessionId` 单桶操作，子历史独立愈合，与主互不干扰。
- `ExportSessionMarkdownAsync(main)`：可选增强——末尾附 `## Sub-Agents` 章节（每个子 `label/status/summary` 前 2000 字），实现时读 `GetSubSessionsAsync` + 各子末条 assistant 文本；失败则跳过附录不影响主导出。

## 4. 并发与锁

复用 `SessionRepository._sessionLocks[sessionId]`（按 session 分桶）与 `WorkspaceSessionService._sessionAppendLocks`。主子不同 `sessionId` → 不同锁，并发 `Append` 无互斥；同 `workspaceId` 的 `sessions-index` 写走 `_workspaceSessionIndexLocks[ws]` 串行，多子同时创建时排队写 index，可接受（建会话是低频操作）。

`Backup/Rewrite` 原子语义不变；`attachments/` 按 `GetAttachmentsDirectory(ws, subId)` 天然隔离。

## 5. 日志

- 建子：`App.Info($"[SubAgent] created sub={subId} parent={mainId} label={label} call={callId}")`。
- 级联删：`App.Info($"[SubAgent] delete-tree parent={mainId} subs={n}")`。
- 子标题跳过精炼：`debug` 一行即可。

## 6. 本篇验收（给 07 用）

- [ ] 老 `meta.json`（无 5 键）读出即 main，侧边栏无子泄漏。
- [ ] `CreateSubSessionAsync` 落盘 5 字段正确，`SessionHash` 与主不同。
- [ ] `GetSessionsByWorkspaceAsync` 不含子；`GetSubSessionsAsync` 按创建时间正序。
- [ ] `DeleteSessionTreeAsync` 删主带走全部子盘 + index 无残留。
- [ ] `Branch` 出来的是 main；子标题不被自动精炼覆盖。
