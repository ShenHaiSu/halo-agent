using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need09-07：skill_tool（能力包自主 CRUD，action 枚举内聚，默认写 project 域）。</summary>
public sealed class SkillTool : IToolHandler
{
    // NEED17-05 §5.1: Exposure=Direct（编码任务默认不打扰；激活集/preset 裁剪由基础组 01 落子，本文件只定声明）。
    public const string Need17Exposure = "Direct";

    // NEED17-05 §5.3: 供 06 动态装配的 PromptBrief/PromptRules（ToolDefinition 字段待基础组 01 扩展，此处先以常量承载，装配表见 SystemPromptBuilder）。
    public const string PromptBrief = "List skills or load a skill body before applying";
    public static readonly string[] PromptRules =
    {
        "Consult ## Available Skills index first; load body via skill_tool before applying."
    };

    public string Name => "skill_tool";

    public string Description =>
        "Manage reusable skill packs (HowTo prompt fragments): list/get/create/update/delete/enable/disable. "
        + "Scope defaults to project; pass scope=global explicitly for global skills. Changes take effect next turn. "
        + "Consult ## Available Skills index first; load body via skill_tool before applying.";

    public ToolDefinition GetDefinition() =>
        new(
            Name,
            Description,
            new ToolSchemaBuilder()
                .AddString(
                    "action",
                    "list|get|create|update|delete|enable|disable. Required.",
                    required: true,
                    enumValues: new[]
                    {
                        "list",
                        "get",
                        "create",
                        "update",
                        "delete",
                        "enable",
                        "disable"
                    }
                )
                .AddString("scope", "global|project. Default project. list accepts all.")
                .AddString("name", "Skill name (required for get, optional for list).")
                .AddString("content", "readme body for create/update (with optional frontmatter).")
                .AddString("pin", "auto|full (create/update only, default auto).")
                .AddInteger("priority", "0..1000, default 100.", minimum: 0, maximum: 1000)
                .AddBoolean("enabled", "Initial switch for create, default true.")
                .Build()
        );

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    ) => SkillToolShared.ExecuteKindAsync(isSkill: true, call, context, ct);
}
