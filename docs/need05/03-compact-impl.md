# 03 · `/compact` 上下文压缩的具体实现（模型摘要 + 原子重写 + 不可中断）

> 学 `znaide compact_context`（`crates/core/src/session.rs:303-404`：`system+摘要+最近4条`，失败不动，`jsonl` 不动，累计不清零，`last_prompt=None` 待校准），但适配本项目 `messages.jsonl` 落盘 + `tool原子组` + `双协议` + `CEF事件` 体系。
> 本项目与 znaide 的一处 deliberate 差异：znaide 历史文件不动（靠 resume 找回）；本项目 `messages.jsonl` 即请求源，必须重写，否则下轮仍发全量。只有 `bak` 保留可审计。

## 1. 行为契约（前后端共同遵守，8 条）

1. 触发：用户显式 `/compact`（或顶栏压缩按钮），或占用≥90%时顶栏提示“可以 /compact”（只提示不自动压，与 znaide `ctx_warn_shown` 同逻辑）。
2. 范围：`system锚点永留`；旧消息压成**一条 `user` 摘要块**；尾部保留 `KEEP_LAST_GROUPS=6` 个原子组（约 6-12 条消息，含 tool 链完整组；不足则有多少留多少）。
3. 不可中断：压缩开始后 `CancelAgentTurn` 不得中断（`znaide` 同语义“收纳中不可中断”），前端按钮置灰。
4. 失败原子性：摘要模型调用失败/空回复/取消→ `messages.jsonl`、`meta.json`、`Cumulative*` 一律不动，仅发 `CompactFailed` 事件。
5. 账单不清零：`CumulativePrompt/Completion`、`TotalOutputTokens(Σ)` 保留；占用快照 `TotalInput/TotalCached` 保持旧值不动，`LastPrompt` 下轮 usage 到账前为 `unknown`（前端画 `?`，见 01 篇）。
6. 历史可审计：重写前 `messages.jsonl → messages.compact-bak-{yyyyMMddHHmmss}.jsonl` 全量备份，同目录。
7. 并发互斥：压缩与 `RunAgentTurn/AppendSessionMessageAsync` 同 `sessionId` 互斥（复用 `WorkspaceSessionService._sessionAppendLocks` 语义，压缩持同一把锁）。
8. 摘要语言：中文要点列表，必须含任务目标/进展、关键路径与结论、用户决定偏好、未办事项四段（模板见 §2.2）。

## 2. 后端设计

### 2.1 新增 `Services/CompactionService.cs`（唯一新增服务文件）

```csharp
public interface ICompactionService {
  Task<CompactionResult> CompactAsync(string workspaceId, string sessionId,
    string? providerId = null, string? modelId = null, int keepLastGroups = 6, CancellationToken ct = default);
}
public sealed class CompactionResult {
  public bool Success { get; init; } public string? ErrorCode { get; init; }
  public int RemovedMessages { get; init; } public int KeptMessages { get; init; }
  public string SummaryPreview { get; init; } = ""; // 首 200 字，事件展示用
}
```

`CompactAsync` 伪代码（共 9 步，失败任一步直接 `return Fail` 且不动盘）：

```text
1. load messages = GetSessionMessagesAsync；若 Count <= keepLastGroups+2（system+摘要位）→ return Success{Removed=0}（无事可做，前端提示“还短无需压缩”）
2. groups = TokenBudgetService.GroupIntoAtomicTurns(排除首条system后的全部)；anchor = 首个含user组及之前；tail = 末 keepLastGroups 组；old = anchor之后..tail之前（若 old 为空 → Removed=0 返回）
3. body = old 每条 "[用户/助手/工具结果] {Content前2000字}" 按时间拼接（tool 参数 ArgumentsJson 同截 2000）
4. sumMsgs = [system压缩器提示词(下), user(body)]；经 LlmGatewayClient 发起一次非流式调用：
   优先复用 ILlmGatewayClient.StreamAsync 累积文本（避免新增 HTTP 通道），tools:null，ct 透传但前端不提供取消入口
5. reply.content 为空/ whitespace → Fail("COMPACT_EMPTY")；成功摘要 summary 非空才继续
6. summaryMsg = UnifiedChatMessage.User("【上下文摘要：压缩自此前 {old消息条数} 条，由模型自动生成，细节以备份为准】\n"+summary)
7. newMessages = [origSystem] + anchorGroups + [summaryMsg] + tailGroups展开（按原顺序）
8. 原子落盘：备份 jsonl→bak；WholeFileRewrite(messages.jsonl, newMessages逐行CompactCamelCase)；meta.MessageCount=newCount；meta.UpdatedAtUtc=now；Cumulative*/Total* 不动；SaveSessionMeta + sessions-index 同步（复用 Append 后半段逻辑，抽小函数，避免 duplicated 锁——拿 _sessionAppendLocks[sessionId] 全程持锁）
9. return Success{Removed=old条数, Kept=tail条数, SummaryPreview=summary[..200]}
```

压缩器 `system` 提示词（冻结文案，施工直接粘）：

```text
你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表，必须保留：
- 任务目标与当前进展到哪一步
- 已完成的关键操作与结果（重要的文件路径、命令、数据结论）
- 用户的明确决定、偏好与要求
- 尚未完成、需要继续处理的事项
只输出要点，不要复述对话原文，不要添加对话里没有的内容。
```

### 2.2 落盘与事件（`HostBridgeFacade` 新增 2 方法 + 3 事件）

```csharp
// HostBridgeFacade.cs #region Stage 04 旁新增 #region need05 Compact：
public Task<BridgeResponse<CompactionResult>> CompactSession(string workspaceId, string sessionId, string? providerId, string? modelId, int keepLastGroups = 6);
// 实现：后台 Task.Run(() => _compactionService.CompactAsync(...))，前后 Publish agent:turn-event：
//   CompactStarted{sessionId} → CompactCompleted{removed,kept,summaryPreview} / CompactFailed{errorCode,errorMessage}
// 不复用 _activeSessionTokens 的 agent: 键（避免与推理互相取消），另起 "compact:"+sessionId 键且不提供 Cancel 入口。
public Task<BridgeResponse<string>> GetCompactBackupList(string workspaceId, string sessionId); // 可选：列出 bak 文件名，审计用
```

`DI`：`HostBridgeFacade` 构造加 `ICompactionService? compactionService=null` 可选参，默认 `new CompactionService(workspaceSessionService, tokenBudgetService, llmGatewayClient, providerConfigService)`。`AgentOrchestrator` 不直接依赖压缩服务（避免循环），压缩走 `HostBridgeFacade` 编排。

### 2.3 存储细节（`SessionRepository` 新增 2 方法，不改旧方法）

```csharp
Task<string> BackupMessagesJsonlAsync(ws, sess); // 拷贝 → messages.compact-bak-{UTC:yyyyMMddHHmmss}.jsonl，返回备份文件名
Task RewriteMessagesJsonlAsync(ws, sess, IReadOnlyList<UnifiedChatMessage> msgs); // 原子写：tmp→move，避免半截文件
```

复用 `AtomicFileWriter` 风格；`Rewrite` 期间持 `SessionRepository._sessionLocks[sessionId]`（与 `AppendMessageAsync` 同锁，天然互斥）。

## 3. 后端改造步骤

1. `Core/Models/WorkspaceModels.cs`：无新增字段（复用 01 篇字段）；`UnifiedChatMessage` 不动（摘要即普通 `user` 消息，靠前缀文案识别）。
2. 新建 `Services/CompactionService.cs`（§2.1 全文，约 180 行，含 `KEEP_DEFAULT=6/TRUNC_PER_MSG=2000` 常量）。
3. `Infrastructure/Storage/SessionRepository.cs`：加 §2.3 两方法（约 40 行）。
4. `Bridge/HostBridgeFacade.cs`：加 `CompactSession/GetCompactBackupList` + 构造注入（约 60 行）；事件名冻结为 `CompactStarted/CompactCompleted/CompactFailed`（`agent:turn-event` 通道，`kind` 即此三串）。
5. `Services/WorkspaceSessionService.cs`：把 `Append` 后半段“存 meta+刷 index”抽为 `internal RefreshMetaIndexAsync` 供压缩复用（纯搬代码，不改语义）；`Clear` 方法本期不做（`/clear` 不在三个目标内，禁止顺手加）。
6. `dotnet build` + 单测（§5）全绿。

## 4. 前端改造步骤

1. `bridge/hostBridge.ts`：`HostBridgeApi` 加 `compactSession/workspaceId,sessionId,providerId?,modelId?,keepLastGroups?` 与 `getCompactBackupList`；`mock` 实现：`compactSession` 返回 `{success:true, removedMessages:2, keptMessages:4}` 并往 `mockMessages[sessionId]` 插入一条 `【上下文摘要…】` 假消息；`haloEventBus` 事件类型加三 `kind`。
2. `stores/agentStore.ts`：`handleTurnEvent` 加三分支：`CompactStarted→isGenerating=true, turnNotice='压缩中…不可中断'`；`CompactCompleted→isGenerating=false, turnNotice='✔ 已压缩：{removed}条收敛为摘要'` + `refreshAfterTool(sid,'finish')` 重拉消息与预算；`CompactFailed→isGenerating=false, lastError`。压缩期间 `sendPrompt/stopGenerating` 守卫：若 `turnNotice` 含 `压缩中` 则 `sendPrompt` 返回 false、`stopGenerating` 空转（不可中断契约）。
3. `stores/workspaceStore.ts`：加 `compactSession(sessionId)` 封装（取 `configStore.activeProvider/Model` 传参，调 `bridge.compactSession`，成功后 `loadSessionMessages+refreshBudget`），供命令与按钮共用。
4. UI（两处入口，缺一不可）：
   * 输入框命令：`PromptComposer`（或现有斜杠分发处）的 `/compact` → `workspaceStore.compactSession`，无输入框命令体系则在发送前拦截 `text.trim()==='/compact'` 同走此路；发送后输入框清空并 `touchSessionClock`。
   * 顶栏悬停卡按钮：“压缩上下文 (/compact)” `n-button`，`disabled=isGenerating&&压缩中`，点击同调；`MessageTimeline` 在 `ContextCompacted` 后首条 `Notice` 已由事件驱动（`agentStore` 的 `finishTurn` 不动，压缩不走 `finishTurn` 的快照清空，避免与推理快照混）。
5. `vue-tsc -b && vite build`。

## 5. 后端单测（新建 `tests/HaloAgent.Tests/Stage05CompactTests.cs`，至少 5 个，需桩网关）

* 桩 `FakeGateway: ILlmGatewayClient`：`StreamAsync` 按预设 `content="要点1\n要点2"` 与 `usage{prompt=100,completion=20}` 回放。
* `Compact_Short_History_NoOp`：5 条消息 `keepLastGroups=6` → `Removed==0` 且 `jsonl` 行数不变、无 `bak`。
* `Compact_Rewrites_To_SystemPlusSummaryPlusTail`：15 条含 tool 链 → 成功后 `messages[0].Role==system`、`messages` 含 `【上下文摘要` 一条 user、尾部 tool 组无孤儿（复用 02 篇孤儿断言）、`bak` 文件存在。
* `Compact_Failure_Keeps_Everything`：网关抛错 → 返回失败且 `jsonl` 字节与 `meta.Cumulative*` 完全不变。
* `Compact_Preserves_Billing`：压缩前后 `CumulativePrompt/Completion` 相等；`TotalInput` 不变（未知态由下轮校准）。
* `Compact_Respects_Keep_Groups`：`keepLastGroups=2` → 尾部恰为末 2 原子组展开。

## 6. 验收

- [ ] 正常会话 `/compact` 一次成功：时间轴出现摘要 `Notice`、尾部消息保留、`bak` 可导出对照、顶栏占用变 `?` 灰色、下轮发送后恢复精确百分比且累计 `Σ` 未清零。
- [ ] 短会话 `/compact` 提示“还短无需压缩”且不动盘；失败（断网/空回复）提示原因且不动盘。
- [ ] 压缩中发送被拒、中断按钮无效（或按了也无效果且有文案说明），压缩结束后自动恢复。
- [ ] 5 用例全绿；`messages.jsonl` 任一时刻可被 `GetMessagesAsync` 完整解析（无半截行）。
