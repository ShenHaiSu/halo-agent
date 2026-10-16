# 02 · 提示词配置模型与持久化（JSON Schema · 默认值 · 迁移校验）

> 前置：`01` 已冻结。本篇冻结后 03/04 方可开工；本篇是全期唯一事实源。

## 1. 设计总则

1. 沿用 `ProviderConfigService` 已验证范式：`{DataRoot}/config/prompt.json` + `AtomicFileWriter.WriteJsonAtomic` + `lock(_syncLock)` + 启动 `EnsureInitialized`（证据：`Infrastructure/Config/ProviderConfigService.cs:28-70`）。
2. 默认值必须使渲染结果与现 `SystemPromptBuilder.cs:57-88` 模板**逐字节一致**（含换行 `\n` 与空行位置），否则即破坏 `need05/02` 前缀基线。
3. 块是唯一可配单位；块 ID 全仓固定 6 个，不可增删（自定义块是第 6 块，文本自由但 ID 固定）。

## 2. 块划分（冻结，不可改 ID/顺序）

| 块 ID | 对应现模板 | stable/ephemeral | 默认开关 | 可编辑文本 |
|---|---|---|---|---|
| `identity` | `You are Halo-Agent, an expert...running natively on Windows.\n` | stable | on | 是（单段） |
| `runtime` | `## 1. Runtime & Environment Facts` + 4 行（OS/工作区名/路径/默认 shell） | stable | on | 是（含 `{{}}` 变量模板） |
| `cmdPython` | `## 2. Command Execution Strategy (CRITICAL RULE)` + 3 条优先级 | stable | on | 是 |
| `toolGuide` | `## 3. Standard Tool Usage Guidelines` + 4 条 | stable | on | 是 |
| `ephemeral` | `\n## 4. Session Ephemeral...` + 日期/Python/SessionHash 三行 | ephemeral（永不进 stable） | on | 是（仅模板骨架可改，变量行不可删变量名，见 §4） |
| `custom` | 新增，默认空 | 跟随 `customPlacement`：`head`→最前（stable 头部）、`tail`→最后（ephemeral 之后） | off | 是（Markdown，默认 `""`） |

装配顺序（冻结）：
```text
[custom(head, 若开)] + [identity?] + [runtime?] + [cmdPython?] + [toolGuide?] + [ephemeral?] + [custom(tail, 若开)]
```
块间连接规则：取各块渲染后 `TrimEnd('\n')`，以 `\n\n` 连接（恰好复刻现模板的空行节奏）；全关 → `""`；单块关 → 不留标题不留多余空行。

`PreviewCachePredictor.SplitSystem` 的 `EphemeralMarker="## 4. Session Ephemeral"`（`Services/PrefixCachePredictor.cs:19`）保持不变：无论用户怎么改尾部模板，只要含该标记即切分；若用户删掉标记，后端自动在 `ephemeral` 块首补标记（保证切分不断）。

## 3. 文件与 DTO（精确到名）

### 3.1 新增 `src/Host.Desktop/Core/Models/PromptModels.cs`

```csharp
public sealed class PromptConfigDocument {
  public int Version { get; set; } = 1;
  public bool EnableHaloPrefix { get; set; } = true;   // F1 总开关
  public bool EnableIdentity { get; set; } = true;
  public bool EnableRuntime { get; set; } = true;
  public bool EnableCmdPython { get; set; } = true;
  public bool EnableToolGuide { get; set; } = true;
  public bool EnableEphemeral { get; set; } = true;
  public bool EnableCustom { get; set; } = false;
  public string CustomPlacement { get; set; } = "tail"; // head|tail
  public string CustomText { get; set; } = string.Empty;
  public string IdentityText { get; set; } = PromptDefaults.IdentityText;
  public string RuntimeTemplate { get; set; } = PromptDefaults.RuntimeTemplate;
  public string CmdPythonText { get; set; } = PromptDefaults.CmdPythonText;
  public string ToolGuideTemplate { get; set; } = PromptDefaults.ToolGuideTemplate;
  public string EphemeralTemplate { get; set; } = PromptDefaults.EphemeralTemplate;
  public string Scope { get; set; } = "global";          // 预留，本期只认 global
  public DateTime UpdatedAtUtc { get; set; }
}
public static class PromptDefaults {
  public const int MaxBlockChars = 8000;
  public const int MaxAssembledChars = 32000;
  public static readonly string IdentityText = "You are Halo-Agent, an expert software engineering and system automation AI agent running natively on Windows.";
  public static readonly string RuntimeTemplate = "## 1. Runtime & Environment Facts\n- Host OS: {{OsVersion}} (Architecture: {{OsArch}})\n- Active Workspace Name: {{WorkspaceName}}\n- Active Workspace Root Path: {{WorkspaceRoot}}\n- Default Command Shell: `cmd.exe` (Windows Command Prompt)";
  public static readonly string CmdPythonText = "## 2. Command Execution Strategy (CRITICAL RULE)\nBecause PowerShell versions vary drastically between Windows 7 (PowerShell 2.0) and Windows 10/11 (PowerShell 5.1+)...（与现 66-70 行逐字一致，冻结时从源码复制）";
  public static readonly string ToolGuideTemplate = "## 3. Standard Tool Usage Guidelines\n- Before modifying...(与现 72-76 行逐字一致，{{WorkspaceRoot}} 变量化)";
  public static readonly string EphemeralTemplate = "## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n- Current Date: {{CurrentDate}}\n- Detected Python Runtime: {{PythonStatus}}\n- SessionHash: 由网关头携带,此处不重复";
  public static PromptConfigDocument CreateDefault() => new() { UpdatedAtUtc = DateTime.UtcNow };
}
public sealed class PromptBlockPreview {
  public string BlockId { get; set; } = "";  // identity/runtime/cmdPython/toolGuide/ephemeral/custom
  public bool Enabled { get; set; }
  public string RenderedText { get; set; } = "";
  public int EstimatedTokens { get; set; }   // TokenBudgetService.EstimateTokens
  public bool IsStable { get; set; }          // custom-head→true, custom-tail→false
}
public sealed class PromptPreviewDto {
  public string AssembledText { get; set; } = "";
  public List<PromptBlockPreview> Blocks { get; set; } = new();
  public int TotalEstimatedTokens { get; set; }
  public int StableEstimatedTokens { get; set; }
  public string PrefixHash { get; set; } = "";  // ComputeDeterministicHash16(assembled)
  public string WorkspaceId { get; set; } = "";
  public List<string> UnknownVariables { get; set; } = new();
  public bool IsDefault { get; set; }
  public string? FallbackReason { get; set; }  // null=正常；缺失/损坏/超限时填原因
}
// Bridge 出参统一包：GetPromptConfig 返回 PromptConfigDocument + IsDefault + FallbackReason
```

变量白名单（冻结 5 个，大小写敏感）：`{{WorkspaceName}} {{WorkspaceRoot}} {{OsVersion}} {{OsArch}} {{PythonStatus}} {{CurrentDate}}`（实现按 `OsVersion/OsArch/WorkspaceName/WorkspaceRoot/PythonStatus/CurrentDate` 六键，文档对外说五变量是把 OS 合述，前端提示写六个）——未知 `{{X}}` 原样保留 + 进 `UnknownVariables`。

### 3.2 新增 `src/Host.Desktop/Infrastructure/Config/PromptConfigService.cs`

对标 `ProviderConfigService` 的锁 + 原子写 + 脱敏（本配置无密钥，无需 DPAPI）：

```csharp
public sealed class PromptConfigService {
  public string PromptConfigFilePath => Path.Combine(_storage.ConfigDirectory, "prompt.json");
  public PromptConfigDocument GetPromptConfigForClient();   // 损坏回退默认 + FallbackReason
  public PromptConfigDocument GetPromptConfigInternal();    // 同上（本期两者一致，保留双口径对齐 Provider 范式）
  public PromptConfigDocument SavePromptConfig(PromptConfigDocument incoming); // 校验→归一→原子写→.bak→返回
  public PromptConfigDocument ResetPromptConfig(string? blockId); // null/""=全部；否则单块恢复默认文本+开默认开关
  public void EnsureInitialized();
}
```

校验规则（`Save` 逐条，失败抛 `PromptConfigValidationException(BlockId, Message)`，Bridge 转 `400 + ErrorCode=PROMPT_VALIDATION`）：
1. `CustomPlacement` 仅 `head/tail`，余归一为 `tail`。
2. 每文本块 `≤8000` 字符；拼装后 `≤32000`（按默认上下文试渲染，超限拒绝，提示哪块超限）。
3. `Version` 仅认 `1`，余按 `1` 归一；`Scope` 仅认 `global`。
4. `null` 文本归一为 `""`；开关缺失按默认 `true`（除 `EnableCustom=false`）。

迁移/回退：
- 文件缺失 → `CreateDefault()` + 写盘 + `IsDefault=true`。
- JSON 损坏/反序列化失败 → 内存默认 + `FallbackReason="prompt.json 损坏，已回退默认（原文件保留为 prompt.json.corrupt-<timestamp>）"` + `HaloLogger.App.Warn`；不覆盖原文件。
- 超限渲染（理论仅手工改文件触发）→ 回退默认 + 同机制横幅。

`Save` 成功后：覆盖式拷贝旧文件到 `prompt.json.bak`（一份），再 `WriteJsonAtomic` 新文件。

## 4. 默认文本冻结清单（施工时逐字节复制，CR 在此只做示意）

- `IdentityText` = 现 58 行单行英文，一字不改。
- `RuntimeTemplate` = 现 60-64 行，其中 `{osVersion}/{arch}/{wsName}/{wsRoot}` 替换为 `{{OsVersion}}/{{OsArch}}/{{WorkspaceName}}/{{WorkspaceRoot}}`，其余标点空格不变。
- `CmdPythonText` = 现 66-70 行逐字（含 `runner: \"cmd\"` 转义后的原文），不变。
- `ToolGuideTemplate` = 现 72-76 行，其中 `{wsRoot}` → `{{WorkspaceRoot}}`，不变。
- `EphemeralTemplate` = 现 80-85 行，其中 `{today}/{pythonStatus}` → `{{CurrentDate}}/{{PythonStatus}}`，标题行 `## 4. Session Ephemeral (只供模型参考,不参与前缀命中)` 保留现标点（含半角逗号）。

单测必须断言：`CreateDefault()` 经 `BuildCore` 渲染 == 现 `BuildAsync` 输出（同 workspace/pythonStatus/date 下逐字节相等），见 05。

## 5. 验收

- [ ] `prompt.json` 缺失/损坏/超限三态均回退默认 + 日志 + `FallbackReason` 非空。
- [ ] 默认渲染与老模板逐字节一致（单测）。
- [ ] `Save` 非法 `placement/超限/Version` 被拒绝或归一，且错误定位到块。
- [ ] `ResetPromptConfig(block)` 单块恢复后其余块不动；全量恢复 == `CreateDefault()`（除时间戳）。
