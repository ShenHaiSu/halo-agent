# 05 子会话历史后端设计（详情接口 × 只读语义）

> 触点：`Bridge/HostBridgeFacade.SubAgent.cs`（扩展）、`Services/WorkspaceSessionService.cs`（小扩）、`Core/Models/WorkspaceModels.cs`（DTO 新类，可放 `SubAgentDtos.cs` 新文件）、`Services/UsageMetricsService.cs`（只读复用）。

## 1. 新增 DTO（新文件 `Core/Models/SubAgentDtos.cs` 或并入现有 Sub* DTO 区）

```csharp
public sealed class SubSessionDetailDto {
    public string SubId { get; set; } = string.Empty;
    public string ParentSessionId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown"; // pending|running|succeeded|failed|cancelled(+内存active校准)
    public string? TaskPrompt { get; set; }
    public string? CreatedByCallId { get; set; }
    public int MessageCount { get; set; }
    public int Iterations { get; set; }        // 末 assistant 累计或 coordinator 内存（running 时内存优先）
    public int ToolExecutions { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long BilledPrompt { get; set; }
    public long BilledCompletion { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
```

## 2. 新增 Bridge 方法（`HostBridgeFacade.SubAgent.cs` 追加）

```csharp
public async Task<BridgeResponse<SubSessionDetailDto>> GetSubSessionDetail(string workspaceId, string subSessionId);
public async Task<BridgeResponse<List<UnifiedChatMessage>>> GetSubSessionMessages(string workspaceId, string subSessionId, int? limit = null, string? beforeMessageId = null);
```

- `GetSubSessionDetail` 流程：`GetSessionAsync`（不存在→`SUB_NOT_FOUND`）→ 校验 `ParentSessionId` 非空（主会话误传→`NOT_A_SUB`）→ `GetSubSessionsAsync(parent)` 取兄弟数（可选）→ 读尾部消息算用量（复用 `SubAgentCoordinator.ReadLastUsageAsync` 逻辑抽 `SubStatsReader` 静态类，主/子共用）→ 内存 `GetActive(parent)` 校准 `running` → 组装。
- `GetSubSessionMessages`：直接透传 `GetSessionMessagesAsync` + 可选尾部分页（`limit` 默认 200，`beforeMessageId` 前翻；v1 先全量+前端虚拟化都可，但接口先留分页位）。
- 只读守卫在**前端**（04 §6）；后端复用既有 `IsHistoryBusy` 思路：新增 `IsSubReadOnly(subId)=true` 文档化注释，`Truncate/Branch/Delete` 若被误调带子 id→ 返回 `SUB_READONLY`（加三行守卫，防未来前端 bug 误伤）。

## 3. `WorkspaceSessionService` 小扩（可选，后端施工自选）

- `GetSubSessionDetailAsync(ws, subId)`：把上述组装逻辑下沉到 Service，Bridge 只做 `try/catch→BridgeResponse`（与 `GetSubSessionsAsync` 分层一致）。
- 不动 `DeleteSessionTreeAsync`（级联语义已对）；`goal.json` 随目录删（02-E5）。

## 4. 用量归因（复用，不新表）

- 子用量已随子消息 `InputTokens/OutputTokens/CachedTokens` 落盘（`AssistantMessagePersisted` 事件写，见 `agentStore.ts:460-471` 对应后端落盘）；详情接口读尾部即得。
- 主 rollup（barrier `billedPrompt/Completion` 求和，见 `SubAgentCoordinator.cs:292-297`）保持；详情另给单子 `Billed*`（`CumulativePrompt/Completion` 快照，`SessionMetadata` 已有字段，直接映射）。

## 5. 并发与取消语义（与 need13 一字不差）

- running 子：`CancelSubAgent(ws, subId)` 唯一出口（barrier 继续等 Cancelled 结果，不提前放行，见 `HostBridgeFacade.SubAgent.cs:57-76` 注释）。
- `ReconcileAsync` 孤儿子自愈不变；详情对 `cancelled` 子照常返回历史（`SubStatus` 落盘值）。
- 事件：子进度仍走 `agent:turn-event/SubAgentProgress`；子历史界面轮询只调 `GetSubSessionMessages`（不订阅流式增量，v1 简单可靠）。

## 6. 错误码表（新增）

| code | 含义 |
|---|---|
| `SUB_NOT_FOUND` | 子会话不存在 |
| `NOT_A_SUB` | 传的是主会话 id |
| `SUB_READONLY` | 对子调了截断/分支/删除 |
| `SUB_LIST_ERROR / SUB_RUNTIME_ERROR` | 沿用既有 |

## 7. 单测清单（对应 07 H-后端）

1. 主 id 调详情→ `NOT_A_SUB`。
2. 不存在子→ `SUB_NOT_FOUND`。
3. 对子调 `Truncate/Branch/Delete` → `SUB_READONLY` 且消息数不变。
4. running 子详情 `status=running`（内存校准），完成后变终态。
5. 分页：`limit=50` 尾部正确；`beforeMessageId` 前翻无重无漏。
