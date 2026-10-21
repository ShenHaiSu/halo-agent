using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need09-07：memory_tool（ facts/偏好持久记忆自主 CRUD，默认 pin=pinned，默认写 project 域）。</summary>
public sealed class MemoryTool : IToolHandler
{
    // NEED17-05 §5.1: Exposure=Direct（编码任务默认不打扰；激活集/preset 裁剪由基础组 01 落子，本文件只定声明）。
    public const string Need17Exposure = "Direct";

    // NEED17-05 §5.3: 供 06 动态装配的 PromptBrief/PromptRules（ToolDefinition 字段待基础组 01 扩展，此处先以常量承载，装配表见 SystemPromptBuilder）。
    public const string PromptBrief = "List, read, or save persistent memory facts";
    public static readonly string[] PromptRules =
    {
        "Pinned Memory is authoritative; never invent persistent facts."
    };

    public string Name => "memory_tool";

    public string Description =>
        "Manage persistent memory facts (project brief, stack, taboos, commands): list/get/create/update/delete/enable/disable. "
        + "Scope defaults to project; pass scope=global explicitly for global memory. Changes take effect next turn. "
        + "action 'set' requires name and body (missing name/body is rejected as MISSING_REQUIRED_PARAM).";

    // NEED17-05 §5.2: name/body 按 action 条件必填无法用 JSON Schema 表达，Schema 保持可选；
    // 条件必填由基础组 02 Validator（ConditionalRequired 小表，码复用 MISSING_REQUIRED_PARAM）执行，见 05 实现注记。
    public ToolDefinition GetDefinition() =>
        new(
            Name,
            Description,
            new ToolSchemaBuilder()
                .AddString(
                    "action",
                    "list|get|set|create|update|delete|enable|disable. Required. set requires name and body.",
                    required: true,
                    enumValues: new[]
                    {
                        "list",
                        "get",
                        "set",
                        "create",
                        "update",
                        "delete",
                        "enable",
                        "disable"
                    }
                )
                .AddString("scope", "global|project. Default project. list accepts all.")
                .AddString("name", "Memory name (required for get/set, optional for list).")
                .AddString("body", "Memory body (required for set).")
                .AddString("content", "Alias of body for create/update compatibility.")
                .AddString("pin", "pinned|archived|auto (create/update only, default pinned).")
                .AddInteger("priority", "0..1000, default 100.", minimum: 0, maximum: 1000)
                .AddBoolean("enabled", "Initial switch for create, default true.")
                .Build()
        );

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    ) => SkillToolShared.ExecuteKindAsync(isSkill: false, call, context, ct);
}
