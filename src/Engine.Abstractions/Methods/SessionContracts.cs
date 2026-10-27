using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ session.new ============================

/// <summary>session.new 结果。</summary>
public sealed record SessionNewResult
{
    /// <summary>新会话 id。</summary>
    public string Id { get; init; } = "";
}

// ============================ session.resume ============================

/// <summary>session.resume 参数。</summary>
public sealed record SessionResumeParams
{
    /// <summary>会话 id。</summary>
    public string Id { get; init; } = "";
}

/// <summary>session.resume 结果。</summary>
public sealed record SessionResumeResult
{
    /// <summary>消息条数。</summary>
    public int Count { get; init; }
    /// <summary>消息。</summary>
    public IReadOnlyList<MessageView> Messages { get; init; } = Array.Empty<MessageView>();
}

// ============================ session.list ============================

/// <summary>session.list 参数。</summary>
public sealed record SessionListParams
{
    /// <summary>搜索关键字（可为空）。</summary>
    public string? Query { get; init; }
}

/// <summary>session.list 结果。</summary>
public sealed record SessionListResult
{
    /// <summary>会话摘要列表。</summary>
    public IReadOnlyList<SessionSummary> Items { get; init; } = Array.Empty<SessionSummary>();
}

// ============================ session.rename ============================

/// <summary>session.rename 参数。</summary>
public sealed record SessionRenameParams
{
    /// <summary>会话 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>新备注名。</summary>
    public string Note { get; init; } = "";
}

/// <summary>session.rename 结果。</summary>
public sealed record SessionRenameResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ session.delete ============================

/// <summary>session.delete 参数。</summary>
public sealed record SessionDeleteParams
{
    /// <summary>会话 id。</summary>
    public string Id { get; init; } = "";
}

/// <summary>session.delete 结果。</summary>
public sealed record SessionDeleteResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ session.clear ============================

/// <summary>session.clear 结果。</summary>
public sealed record SessionClearResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ session.compact ============================

/// <summary>session.compact 结果。</summary>
public sealed record SessionCompactResult
{
    /// <summary>移除条数。</summary>
    public int Removed { get; init; }
    /// <summary>摘要。</summary>
    public string Summary { get; init; } = "";
}

// ============================ session.setCwd ============================

/// <summary>session.setCwd 参数。</summary>
public sealed record SessionSetCwdParams
{
    /// <summary>新工作目录。</summary>
    public string Path { get; init; } = "";
}

/// <summary>session.setCwd 结果。</summary>
public sealed record SessionSetCwdResult
{
    /// <summary>生效后的工作目录。</summary>
    public string Cwd { get; init; } = "";
}
