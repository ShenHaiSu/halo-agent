namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need09-07：skill_tool（能力包自主 CRUD，action 枚举内聚，默认写 project 域）。</summary>
public sealed class SkillTool : IToolHandler
{
    public string Name => "skill_tool";

    public string Description =>
        "Manage reusable skill packs (HowTo prompt fragments): list/get/create/update/delete/enable/disable. " +
        "Scope defaults to project; pass scope=global explicitly for global skills. Changes take effect next turn.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"action\":{\"type\":\"string\",\"description\":\"list|get|create|update|delete|enable|disable. Default list.\"},"
        + "\"scope\":{\"type\":\"string\",\"description\":\"global|project. Default project. list accepts all.\"},"
        + "\"name\":{\"type\":\"string\",\"description\":\"Skill name (required for get/create/update/delete/enable/disable, optional for list).\"},"
        + "\"content\":{\"type\":\"string\",\"description\":\"readme body for create/update (with optional frontmatter).\"},"
        + "\"pin\":{\"type\":\"string\",\"description\":\"auto|full (create/update only, default auto).\"},"
        + "\"priority\":{\"type\":\"integer\",\"description\":\"0..1000, default 100.\"},"
        + "\"enabled\":{\"type\":\"boolean\",\"description\":\"Initial switch for create, default true.\"}"
        + "},\"required\":[\"action\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default) =>
        SkillToolShared.ExecuteKindAsync(isSkill: true, call, context, ct);
}
