namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>一条备注（info/warn/error + 文本）。</summary>
public sealed record Note
{
    /// <summary>“info” | “warn” | “error”。</summary>
    public string Level { get; init; } = "info";
    /// <summary>文本。</summary>
    public string Text { get; init; } = "";
}

/// <summary>会话摘要（session.list 的条目）。</summary>
public sealed record SessionSummary
{
    /// <summary>会话 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>标题。</summary>
    public string Title { get; init; } = "";
    /// <summary>备注名（可为空）。</summary>
    public string? Note { get; init; }
    /// <summary>工作目录（可为空）。</summary>
    public string? Cwd { get; init; }
    /// <summary>是否为无头会话。</summary>
    public bool Headless { get; init; }
    /// <summary>修改时刻（毫秒时间戳）。</summary>
    public long MtimeMs { get; init; }
    /// <summary>用户消息数。</summary>
    public int UserMsgs { get; init; }
    /// <summary>助手消息数。</summary>
    public int AssistantMsgs { get; init; }
}

/// <summary>技能摘要（asset.skills.list 的条目）。</summary>
public sealed record SkillSummary
{
    /// <summary>技能名。</summary>
    public string Name { get; init; } = "";
    /// <summary>描述。</summary>
    public string Description { get; init; } = "";
    /// <summary>来源：“builtin” | “user” | “project”。</summary>
    public string Source { get; init; } = "user";
    /// <summary>是否禁止模型调用。</summary>
    public bool DisableModelInvocation { get; init; }
    /// <summary>是否有入口文件。</summary>
    public bool HasEntry { get; init; }
}

/// <summary>记忆摘要（asset.memory.list 的条目）。</summary>
public sealed record MemorySummary
{
    /// <summary>文件名。</summary>
    public string Filename { get; init; } = "";
    /// <summary>记忆名。</summary>
    public string Name { get; init; } = "";
    /// <summary>描述。</summary>
    public string Description { get; init; } = "";
    /// <summary>记忆类型。</summary>
    public string Mtype { get; init; } = "";
    /// <summary>作用域：“global” | “project”。</summary>
    public string Scope { get; init; } = "global";
    /// <summary>更新时刻（毫秒时间戳）。</summary>
    public long UpdatedMs { get; init; }
}

/// <summary>工具信息（tool.list 的条目）。</summary>
public sealed record ToolInfo
{
    /// <summary>工具名。</summary>
    public string Name { get; init; } = "";
    /// <summary>描述。</summary>
    public string Description { get; init; } = "";
    /// <summary>来源：“builtin” | “mcp” | “skill”。</summary>
    public string Source { get; init; } = "builtin";
}

/// <summary>快照信息（tool.undoList 的条目）。</summary>
public sealed record SnapshotInfo
{
    /// <summary>快照序号（≥1）。</summary>
    public int Index { get; init; }
    /// <summary>所属会话。</summary>
    public string Session { get; init; } = "";
    /// <summary>原始路径/标识。</summary>
    public string Orig { get; init; } = "";
    /// <summary>动作描述。</summary>
    public string Action { get; init; } = "";
    /// <summary>毫秒时间戳。</summary>
    public long TsMs { get; init; }
    /// <summary>是否为外部快照。</summary>
    public bool External { get; init; }
}

/// <summary>请求头键值对。value 支持 ${ENV} 与会话模板，settings.get 回显模板原文。</summary>
public sealed record HeaderPair
{
    /// <summary>头名。</summary>
    public string Name { get; init; } = "";
    /// <summary>头值（模板原文，不回显展开后实值）。</summary>
    public string Value { get; init; } = "";
}
