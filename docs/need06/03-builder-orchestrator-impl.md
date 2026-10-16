# 03 · 后端实现（Builder 重构 · 编排接入 · 预览 API · 前缀联动）

> 前置：`02` 已冻结。改动顺序：`PromptModels → PromptConfigService → SystemPromptBuilder → AgentOrchestrator → HostBridgeFacade.Prompt → Program DI → 单测`。

## 1. `SystemPromptBuilder` 重构（核心，零破坏）

### 1.1 签名规划

现 `Services/SystemPromptBuilder.cs:14-88` 是 `static class + static BuildAsync(workspace, pythonStatusOverride)`。改为：

```csharp
public sealed class SystemPromptBuilder {
  public static string GetPythonStatus();                                   // 不动（03 仅加缓存，见 §1.3）
  public static Task<string> BuildAsync(WorkspaceInfo ws, string? py=null); // 兼容壳：内部调默认配置 BuildCore
  public static PromptPreviewDto BuildPreview(PromptConfigDocument cfg, WorkspaceInfo ws, string? pyOverride, string? dateOverride);
  internal static (string assembled, List<PromptBlockPreview> blocks, List<string> unknownVars)
    BuildCore(PromptConfigDocument cfg, PromptContext ctx);                  // 唯一拼装路径，预览与实发共用
}
public sealed record PromptContext(string OsVersion, string OsArch, string WsName, string WsRoot, string PythonStatus, string Today);
```

兼容壳 `BuildAsync` 行为：取 `PromptConfigService` 不可用（单测无 DI）时用 `PromptDefaults.CreateDefault()`，保证老单测 `prompt.StartsWith("You are Halo-Agent")` 继续绿。

### 1.2 `BuildCore` 伪实现（逐行照抄）

```csharp
if (!cfg.EnableHaloPrefix) return ("", emptyBlocks, unknown);
var parts = new List<(string id,string text,bool stable)>();
RenderTemplateToText(tpl, ctx, out unknown);  // {{Var}} 替换，未知保留原样+记名
if (cfg.EnableCustom && !string.IsNullOrWhiteSpace(cfg.CustomText))
  headOrTail = RenderTemplateToText(cfg.CustomText, ctx);
if (cfg.EnableIdentity) parts.Add(("identity", cfg.IdentityText.TrimEnd(), true));
if (cfg.EnableRuntime) parts.Add(("runtime", Render(cfg.RuntimeTemplate), true));
if (cfg.EnableCmdPython) parts.Add(("cmdPython", cfg.CmdPythonText.TrimEnd(), true));
if (cfg.EnableToolGuide) parts.Add(("toolGuide", Render(cfg.ToolGuideTemplate), true));
if (cfg.EnableEphemeral) {
  var eph = Render(cfg.EphemeralTemplate);
  if (!eph.Contains(PrefixCachePredictor.EphemeralMarker, Ordinal)) eph = "## 4. Session Ephemeral\n" + eph.TrimStart();
  parts.Add(("ephemeral", eph.TrimEnd(), false));
}
// custom 块 IsStable = placement==head
assembled = string.Join("\n\n", orderedParts.Select(p=>p.text.TrimEnd())) // 全关→""
```

`Render` 细节：先做 `{{Var}}` 全量替换（6 键），再收集残留 `{{...}}` 进 `unknownVars`（正则 `\{\{\s*([A-Za-z0-9_]+)\s*\}\}`）。`TrimEnd` 只去 `\r\n`，不碰行首空格（保 Markdown 代码块）。

### 1.3 Python 探测缓存（预览 <300ms 必须）

现 `GetPythonStatus()` 每次 `Process --version`（`SystemPromptBuilder.cs:90-135`，`WaitForExit(5000)`）。预览高频触发必须缓存：

```csharp
private static string? _pyCache; private static long _pyCacheTick;
public static string GetPythonStatus() {
  if (_pyCache != null && Environment.TickCount64 - _pyCacheTick < 60_000) return _pyCache;
  ...原逻辑...; _pyCache = display; _pyCacheTick = now; return display;
}
public static void ClearPythonCacheForTests();
```

`BuildPreview` 允许传 `pyOverride`（前端诊断"固定 Python 状态对比"用，可空）。

## 2. `AgentOrchestrator` 接入（`Services/AgentOrchestrator.cs:90-228`）

### 2.1 构造注入（可选依赖，防单测爆炸）

```csharp
private readonly PromptConfigService? _promptConfigService;
// 新增可选形参 PromptConfigService? promptConfigService = null，赋值即可；Program.cs 注册单例并传入。
```

### 2.2 `RunAgentTurnAsync` 改动（仅替换 153-166 段，其余不动）

```csharp
string systemPrompt;
PromptConfigDocument? promptCfg = null;
try {
  promptCfg = _promptConfigService?.GetPromptConfigInternal() ?? PromptDefaults.CreateDefault();
  var ws = workspace; // 已加载
  var preview = SystemPromptBuilder.BuildPreview(promptCfg, ws, pyOverride: null, dateOverride: null);
  systemPrompt = preview.AssembledText;   // 总开关关→""
} catch (OperationCanceledException) { ...原取消路径... }
catch (Exception ex) {
  // need06 回退：配置异常绝不炸轮
  try { systemPrompt = await SystemPromptBuilder.BuildAsync(workspace).ConfigureAwait(false); }
  catch (Exception ex2) { return AgentTurnResult.Failed("SYSTEM_PROMPT_ERROR", ex2.Message); }
  HaloLogger.App.Warn("[Prompt] 配置渲染失败，已回退默认模板", ex);
}
```

下游 `193-228` 保持原样，但加两行注释 + 一个守卫：`requestMessages` 当 `systemPrompt` 为空时**不加** `System` 首条（否则发空 system 抖前缀）；`SystemInstructions = systemPrompt` 照传（适配器现有 `IsNullOrWhiteSpace` 守卫会自动省略字段，`ChatCompletionsAdapter.cs:67` / `ResponsesApiAdapter.cs:46` 不动）。

```csharp
var requestMessages = new List<UnifiedChatMessage>(budgeted.Count + 1);
if (!string.IsNullOrWhiteSpace(systemPrompt)) requestMessages.Add(UnifiedChatMessage.System(systemPrompt));
requestMessages.AddRange(budgeted);
```

`catch → Failed("SYSTEM_PROMPT_ERROR")` 语义保留（仅默认模板本身炸才走）。

### 2.3 上轮实际快照（诊断区 D 用）

在 `LlmGatewayClient.ExecuteStreamPipelineAsync` 现有 `LogRequestTrace` 处**顺带**把 `request.SystemInstructions` 的 `(prefixHash, length, reqId, sessionId, utc)` 写入一个内存 `LastSentPromptSnapshot`（`ConcurrentDictionary<sessionId, snapshot>`，仅保留最近 1 条/会话）。`PreviewPrompt` 的诊断字段直接读它；不落盘、不进 `messages.jsonl`。若无快照，前端显示空态。

## 3. `HostBridgeFacade.Prompt.cs`（新增 partial，对标 `HostBridgeFacade.SessionTitle.cs`）

```csharp
public partial class HostBridgeFacade {
  public Task<BridgeResponse<PromptConfigWithMeta>> GetPromptConfig();
  public Task<BridgeResponse<PromptConfigWithMeta>> SavePromptConfig(string configJson);
  public Task<BridgeResponse<PromptConfigWithMeta>> ResetPromptConfig(string? blockId);
  public Task<BridgeResponse<PromptPreviewDto>> PreviewPrompt(string workspaceId);
  public Task<BridgeResponse<PromptDiagnosticsDto>> GetPromptDiagnostics(string workspaceId, string? sessionId);
}
public sealed class PromptConfigWithMeta : PromptConfigDocument { public bool IsDefault {get;set;} public string? FallbackReason {get;set;} }
public sealed class PromptDiagnosticsDto {
  public PromptPreviewDto CurrentPreview { get; set; } = new();
  public LastSentPromptSnapshot? LastSent { get; set; }  // reqId/sessionId/prefixHash/charLength/sentAtUtc
  public bool PreviewEqualsLastSent { get; set; }
  public string CompressorPromptPreview { get; set; } = ""; // CompactionService.CompressorSystemPrompt 原样（只读）
  public string TitlePromptPreview { get; set; } = "";      // SessionTitle 标题提示词骨架（只读，脱除对话正文）
}
```

`SavePromptConfig` 反序列化失败 → `BridgeResponse(success:false, errorCode:"PROMPT_VALIDATION", message: BlockId+Message)`；成功返回生效后文档。`PreviewPrompt(workspaceId)` 对未知 workspace 返回 `WORKSPACE_NOT_FOUND`（不回退，避免预览撒谎）；空 workspaceId 则用 `Environment.CurrentDirectory` 做中性预览并在 `FallbackReason` 注明。

`Program.cs`：注册 `PromptConfigService` 单例，`AgentOrchestrator` 构造追加实参；`EnsureInitialized()` 在启动时调用一次。

## 4. 前缀/预算联动（need05 不回归）

1. `PrefixCachePredictor.EphemeralMarker` 不动；`SplitSystem` 不动；`custom-tail` 自动归入 ephemeral 侧（`IsStable=false`），`custom-head` 归 stable 侧——`BuildSnapshot` 无需改（它按 marker 切）。
2. `TokenBudgetService.TruncateToBudget/HighWaterMark` 不动；`CalculateBudgetStatus` 可选新增 `SystemPromptTokens` 字段（只增量，不动旧字段）。
3. 开关/文本变更 → 下一轮 `prefixHash` 变化是**预期内断裂**；`LogRequestTrace` 已有 `prefixHash`，无需新日志；前端诊断文案负责解释（见 04 §5）。

## 5. 附属提示词只读预览（01 §5.3 落实）

- `CompactionService.CompressorSystemPrompt`（`Services/CompactionService.cs:186` 附近常量）：`GetPromptDiagnostics` 原样返回全文。
- 标题提示词（`Bridge/HostBridgeFacade.SessionTitle.cs:100-121` 的 `titleSystemPrompt + titleUserPrompt` 骨架）：返回骨架模板 + `（对话正文已脱除）` 后缀，不返回真实对话。
- 二者本期**无开关无编辑**，前端以禁用态折叠展示 + `本期仅预览` 角标。

## 6. 验收

- [ ] 默认配置 `BuildPreview == 老 BuildAsync` 逐字节一致（同输入）。
- [ ] 总开关关 → `AssembledText=""`，`SerializeRequest(chat/resp)` 不含 `system/instructions`。
- [ ] 单关 `cmdPython/identity` 无残留标题/空壳；`ephemeral` 关 → 无 `## 4.` 标记（此时 `SplitSystem` 全归 stable）。
- [ ] `prompt.json` 损坏时 `RunAgentTurnAsync` 仍成功（回退默认）且有 Warn 日志。
- [ ] `PreviewPrompt` 与实发同一 `BuildCore`（代码审查：仅一处 `string.Join("\n\n")`）。
