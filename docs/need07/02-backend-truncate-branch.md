# need07-02 后端：截断与分支（Service + Bridge）

> 前置：`01` 已冻结。本篇精确到文件与方法签名，后端按此施工。

## 1. 设计一句话

复用 `SessionRepository.Backup/RewriteMessagesJsonlAsync` 原子重写范式与 `WorkspaceSessionService.GetSessionAppendLock` 互斥锁，新增两个领域动作——**截断（重放用）**与**分支（复制前缀建新会话）**，经 `HostBridgeFacade` 暴露给前端；`AgentOrchestrator` 不新增入口（重放复发走既有 `StartAgentTurn`）。

## 2. 接口新增（精确到行）

### 2.1 `Services/WorkspaceSessionService.cs`

在 `IWorkspaceSessionService` 追加：

```csharp
Task<HistoryTruncateResult> TruncateSessionHistoryAsync(
    string workspaceId, string sessionId, string anchorMessageId,
    bool removeAnchor, CancellationToken ct = default);

Task<SessionMetadata> BranchSessionAsync(
    string workspaceId, string sourceSessionId, string anchorMessageId,
    bool includeAnchor, CancellationToken ct = default);
```

新增 DTO（放 `Core/Models/WorkspaceModels.cs` 尾部）：

```csharp
public sealed class HistoryTruncateResult {
  public bool Success { get; init; }
  public string? ErrorCode { get; init; }
  public int KeptMessages { get; init; }
  public int RemovedMessages { get; init; }
  public string? ReplayText { get; init; }      // 重放用：被删锚点对应的复发原文
  public string? BackupFileName { get; init; }
  public static HistoryTruncateResult Ok(int kept,int removed,string? replay,string? bak)
    => new(){ Success=true, KeptMessages=kept, RemovedMessages=removed, ReplayText=replay, BackupFileName=bak };
  public static HistoryTruncateResult Fail(string code)
    => new(){ Success=false, ErrorCode=code };
}
```

### 2.2 `Bridge/HostBridgeFacade.cs`（新增 partial `HostBridgeFacade.History.cs`）

```csharp
public async Task<BridgeResponse<HistoryTruncateResult>> TruncateSessionHistory(
    string workspaceId, string sessionId, string anchorMessageId, bool removeAnchor);
public async Task<BridgeResponse<SessionMetadata>> BranchSession(
    string workspaceId, string sourceSessionId, string anchorMessageId, bool includeAnchor);
```

忙态守卫：若 `_activeSessionTokens` 含 `agent:{sessionId}` 或 `compact:{sessionId}`，直接返回 `Fail("SESSION_BUSY","生成或压缩进行中，请稍后再试")`，不动盘。

## 3. 切面算法（与 01 §3.2 同构，后端为权威）

```text
msgs = GetMessagesAsync(ws, sess)                      // 按 jsonl 顺序
idx  = msgs.FindIndex(id == anchorMessageId)           // 找不到 → ANCHOR_NOT_FOUND
role = msgs[idx].Role

// 展开尾（assistant 吞后续 tool*）
end = idx
if role == assistant:
  t = idx + 1
  while t < msgs.Count && msgs[t].Role == tool: t++
  end = t - 1                                           // 含整轮；assistant 无后续 tool 则 end==idx

// 截断：removeAnchor==true → keep=[0,cut)；false → keep=[0,end]
//   其中 cut = 重放 user 时 cut=idx；重放 assistant 时 cut=前驱 user 索引 j
//   前驱 user：从 idx（assistant 情形从 idx，user 情形从 idx-1）向前找第一条 role==user；找不到 → NO_PRIOR_USER
// 分支：includeAnchor==true → prefix=[0,end]；false → prefix=[0,idx)
```

重放装配（Service 内一次算完，前端只调一次）：

- `replayUser(U)`：`cut = idx(U)`，`replayText = U.content`，`keep=[0,cut)`。
- `replayAssistant(A)`：`j = prevUser(A)`，`replayText = msgs[j].content`，`keep=[0,j)`。
- 空 `replayText`（仅空白）→ `EMPTY_REPLAY_TEXT`，不动盘。

分支装配：

- `prefix` 按上表切片后**原样克隆**（保留 `Id/Role/Content/Reasoning/ToolCalls/ToolCallId/Name/CreatedAt`；`Input/Output/CachedTokens` 一并保留供预算快照连续）。
- 新会话经既有 `CreateSessionAsync(ws, title)` 建，标题 = `源标题 + "·分支"`（源为“新建会话”则新标题亦“新建会话”，沿用 T1/T2 自动标题链路，不强写）。
- `RewriteMessagesJsonlAsync(ws, newId, prefix)` 全量写入；`meta.MessageCount=prefix.Count`；`TotalInput/Cached` 取前缀末条 assistant 快照（无则 0，`unknown` 交前端画 `?`）；`Cumulative*` 归零（新会话账单从零起）；`Provider/Model` 继承源会话。

## 4. 落盘与审计（照抄 compact 范式，不自创）

1. 持 `GetSessionAppendLock(sessionId)`（截断）/ 源 + 新两把锁按字典序持取（分支只写新会话，读源不持锁，与 `Append` 的 jsonl 读共享 `FileShare.ReadWrite`，天然无撕裂；若求极致可持源读锁——本期不持，读后校验 `meta` 仍存在即可）。
2. `BackupMessagesJsonlAsync` → 备份名改前缀以区分用途：
   - 截断：`messages.replay-bak-{yyyyMMddHHmmss}.jsonl`
   - 分支：新会话内写 `messages.branch-src-{sourceSessionIdShort}.json`（存 `{sourceSessionId, anchorMessageId, copiedAt}` 小 JSON，供审计，不污染 jsonl）。
   - 通用 `BackupMessagesJsonlAsync` 文件名由调用方传入后缀——需给它加一个可选参数 `string? filePrefix = null`，默认 `messages.compact-bak-` 保持老调用零改动。
3. `RewriteMessagesJsonlAsync` 原子重写（`tmp→move`）。
4. `RefreshMetaIndexAsync` 刷 `meta.json + sessions-index.json`（`MessageCount/UpdatedAtUtc/TotalInput/TotalCached` 重算；`Cumulative*` 截断保留、分支清零）。
5. 日志：`HaloLogger.App.Info("[History] truncate/branch ...")`。

## 5. 错误码表（前后端共用，前端按码吐司）

| 码 | 含义 | 前端文案 |
|----|------|---------|
| `SESSION_BUSY` | 生成/压缩进行中 | 生成中不可操作，请稍后再试 |
| `ANCHOR_NOT_FOUND` | 锚点 id 不在 jsonl | 历史已变化，请刷新后重试 |
| `NO_PRIOR_USER` | assistant 前无 user | 找不到上一条用户消息，无法重放 |
| `EMPTY_REPLAY_TEXT` | 复发原文空 | 该消息内容为空，无法重放 |
| `SESSION_NOT_FOUND` | 会话/工作区不存在 | 会话不存在 |
| `REWRITE_ERROR` | 重写异常（备份已留） | 重写失败，备份已保留 |

## 6. 并发与幂等

- 截断与 `AppendSessionMessageAsync` 持**同一把** `_sessionAppendLocks[sessionId]`，与压缩同锁，故重放截断与推理落盘、压缩重写三者天然互斥。
- 前端重放编排必须串行：`await truncate → await loadSessionMessages → await sendPrompt(replayText)`；`sendPrompt` 内 `resetTurnState + startAgentTurn` 即重建 user，不可与截断并行。
- 分支可与源会话推理并行（只读源）；但为降低心智负担，前端仍在生成中禁用分支（与 01 §3.4 一致），后端不强制拒绝分支并行——仅拒绝截断并行？**冻结决策：两者都拒绝**（同一 `SESSION_BUSY`），实现最简、心智最稳。

## 7. 单测（`tests/HaloAgent.Tests/Stage07HistoryTests.cs`，新增）

1. `Truncate_KeepsPrefix_RemovesAnchorAndAfter`：U1 A1 U2 A2 → 锚 U2 → kept=[U1 A1]，removed=2，replay=U2.content。
2. `Truncate_Assistant_ExpandsOverTools`：U1 A1 T1 T2 U2 → 锚 A1 → 前驱 U1 → kept=[]（j=0），removed 全删。
3. `Truncate_AnchorMissing_NoWrite`：备份数不变，返回 ANCHOR_NOT_FOUND。
4. `Branch_User_ExcludesAnchor_PrefixCopied`：锚 U2 → 新会话消息 == [U1 A1]，原会话不变，草稿由前端负责（后端不管草稿）。
5. `Branch_Assistant_IncludesWholeTurn`：锚 A1（含 T1）→ 新会话尾为 T1。
6. `Busy_Guarded`：伪造 `_activeSessionTokens` 含 agent 键 → SESSION_BUSY（Facade 层测，或 Service 层预留 `isBusy` 注入点）。

老用例（compact/budget/session）必须全绿；`BackupMessagesJsonlAsync` 加参后老调用零改。
