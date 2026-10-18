# need17-01 · 声明层：从裸字符串到强约束声明

> 前置：无（P0 首批）。对应 pi `toolDocs/01`（工具契约）+ `03`（声明与约束采样）+ `00 §2`（注册/激活/声明/提示四层）+ `10 §10.6`（默认工具合并规则）。
> 目标：发给模型的 declaration 从“仅供参考”变为“强制结构”，未知字段、多传、漏传在网关侧即被拒；工具面从 15 常驻变为 preset 可控。

---

## 1.1 `ToolDefinition` 扩展（向后兼容，只加不改）

现状 `Tools/ToolDefinition.cs:7-68`：`Name/Description/ParametersJson` 三件套，`ToChatToolJson/ToResponsesToolJson` 手拼。

方案：保留三字段与两序列化方法签名，新增可选元数据（执行层不用、只用于声明与提示装配）：

```csharp
public sealed class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "{...}";
    // 新增（全部可选，有默认值，老构造零改动）：
    public bool? Strict { get; set; }          // null=按全局 StrictToolsMode
    public ToolExposure Exposure { get; set; } = ToolExposure.Direct;
    public string Category { get; set; } = "core";   // core | search | overlay
    public string PromptBrief { get; set; } = string.Empty;  // 工具段一行简介，无则提示词省略
    public string[] PromptRules { get; set; } = Array.Empty<string>(); // 规则段
}
public enum ToolExposure { Direct, Hidden, Disabled }
```

* `Strict=true` 的工具：Chat 协议在 `function` 内写 `"strict": true`，Responses 协议顶层写 `"strict": true`（见 1.2）。
* `Exposure=Hidden`：仍可执行、可被转调，但不进 `GetActiveDefinitions()`（`edit` 分发器用此值，见 1.4）。
* `Exposure=Disabled`：`Dispatch` 直接拒（预留给未来开关，need17 不启用）。

### 验收

* `Stage17DeclarationTests.Strict_Serialization`：core 4 工具双协议 JSON 含 `"strict":true`；overlay 工具不含。
* 老单测 `GetChatToolsJson` 期望串更新（一次性，注明 need17）。

## 1.2 Schema 生成器：消灭手拼字符串

现状：15 个 `GetDefinition()` 手拼 JSON 转义串，无 `additionalProperties`，`required` 随意（如 `list_dir` 无 required）。

方案：新增 `Tools/Validation/ToolSchemaBuilder.cs`（`Utf8JsonWriter` 构建，单元可测）：

* 顶层固定：`{"type":"object","properties":{...},"required":[...],"additionalProperties":false}`。
* 字段支持：`type/description/enum/minLength/maxLength/minimum/maximum/default`。
* 每个工具把 `GetDefinition` 改为 builder 调用（参数语义不变，只换生成方式；描述文本按 03/04/05 分册逐字写入）。

网关宽容（呼应 pi strict-preference）：新增配置 `StrictToolsMode: Prefer(默认) | Require | Off`（`AgentConfigDocument` 可选字段，默认 Prefer）：

* Prefer：发送 `strict:true`；若网关回 400 且错误提及 strict/unknown keyword，`LlmGatewayClient` 自动去 strict 重发一次并 Warn 日志（仅一次，不循环）。
* Require：不支持 strict 的网关直接启动校验报错（开发联调 用）。
* Off：回滚开关，还原 need17 前行为（见 08）。

### 验收

* `Stage17DeclarationTests.AllSchemas_ValidAndClosed`：全部激活工具 `ParametersJson` 可解析、顶层含 `additionalProperties:false`、有 `required`（list_dir 补上，见 04）。
* `Prefer_Downgrade_Retries_WithoutStrict`：mock 400→去 strict 重发成功（`LlmGatewayClient` 单测或适配器单测，选一处实现）。

## 1.3 激活集：preset + allowlist/denylist（照抄 pi 10.6 语义）

现状：`ToolRegistry(true)` 注册 15 个，`GetDefinitions()` 全下发，无 preset。

方案：新增 `Tools/ToolPreset.cs`：

```csharp
public static class ToolPreset
{
    public static readonly string[] Coding   = { "read_file","exec_command","edit_file","edit_lines","write_file" };
    public static readonly string[] ReadOnly = { "read_file","list_dir","grep_search","view_image" };
    // Full = 全部 Direct 工具；Overlay（ask/skill/memory/spawn/goal）仅 Full 或显式点名。
}
```

* `ToolRegistry` 新增：`IReadOnlyList<ToolDefinition> GetActiveDefinitions(ISet<string> active)` + `DispatchAsync` 未激活工具名返回 `TOOL_NOT_ENABLED`（结构化提示可用 preset/点名，而非“未知工具”）。
* 配置语义（`agent.json` 新增 `toolsPreset | toolsAllowlist | toolsDenylist`，三者互斥规则照抄 pi）：纯名单=替换继承；纯加减（`+grep_search`/`-spawn_subagent`）=增量；混合纯名与加减=启动校验非法并阻断（文案中文）。
* 默认：`preset=Coding` + overlay 按现有 `SubPolicy` 叠加（主包 spawn/ask/skill/memory/goal 行为不变，只是声明层可裁剪；`ForSub` 保持 `ToolRegistryFactory` 现状，声明同样走激活集过滤）。
* `AgentOrchestrator` L375 处 `GetDefinitions()` 改为 `GetActiveDefinitions(activeSet)`（activeSet 轮首计算一次，本轮冻结）。

### 验收

* `Preset_Coding_HidesOverlay`：Coding 下无 ask/spawn/skill/memory/goal；`Dispatch` 调被藏工具返回 `TOOL_NOT_ENABLED` 且含可用列表。
* 非法混合配置启动阻断单测。

## 1.4 写入口收敛与参数名统一（冻结决策）

1. `edit`（`Tools/BuiltIn/EditTool.cs`）：标记 `Exposure=Hidden`，主 preset 不再声明；`Description` 首行加 `DEPRECATED: prefer edit_file/edit_lines.`；`Dispatch` 仍执行（老调用不断）；提示词不再提（06 同步）。
2. 参数名统一：全工具主名 `file_path`，`path` 保留为**兼容别名**（02 的 shim 层搬运，Schema 只写 `file_path`）。涉及：`grep_search`、`list_dir`、`view_image`（03/04 分册各写 Schema 差分）。
3. `write_file` 与 `edit_file/edit_lines` 分工冻结（一句话，进 06 ToolGuide）：`write_file` 仅新建或全量重写（需 `allow_overwrite:true`）；局部改一律 `edit_file/edit_lines`。

### 验收

* `Edit_HiddenButExecutable`：Coding 声明无 `edit`，但 `Dispatch(edit,合法参数)` 仍成功。
* `Path_Alias_Accepted`：`read_file({path:...})` 成功且 `ValidatedWarnings` 含 `ALIASED_PARAM`（02 实现 warning 通道）。
