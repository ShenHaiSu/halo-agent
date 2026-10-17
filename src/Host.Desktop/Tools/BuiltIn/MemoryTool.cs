namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need09-07：memory_tool（ facts/偏好持久记忆自主 CRUD，默认 pin=pinned，默认写 project 域）。</summary>
public sealed class MemoryTool : IToolHandler
{
    public string Name => "memory_tool";

    public string Description =>
        "Manage persistent memory facts (project brief, stack, taboos, commands): list/get/create/update/delete/enable/disable. " +
        "Scope defaults to project; pass scope=global explicitly for global memory. Changes take effect next turn.";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"action\":{\"type\":\"string\",\"description\":\"list|get|create|update|delete|enable|disable. Default list.\"},"
        + "\"scope\":{\"type\":\"string\",\"description\":\"global|project. Default project. list accepts all.\"},"
        + "\"name\":{\"type\":\"string\",\"description\":\"Memory name (required for get/create/update/delete/enable/disable, optional for list).\"},"
        + "\"content\":{\"type\":\"string\",\"description\":\"readme body for create/update (with optional frontmatter).\"},"
        + "\"pin\":{\"type\":\"string\",\"description\":\"pinned|archived|auto (create/update only, default pinned).\"},"
        + "\"priority\":{\"type\":\"integer\",\"description\":\"0..1000, default 100.\"},"
        + "\"enabled\":{\"type\":\"boolean\",\"description\":\"Initial switch for create, default true.\"}"
        + "},\"required\":[\"action\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default) =>
        SkillToolShared.ExecuteKindAsync(isSkill: false, call, context, ct);
}
