using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ tool.list ============================

/// <summary>tool.list 结果。</summary>
public sealed record ToolListResult
{
    /// <summary>工具信息列表。</summary>
    public IReadOnlyList<ToolInfo> Items { get; init; } = Array.Empty<ToolInfo>();
}

// ============================ tool.undoList ============================

/// <summary>tool.undoList 结果。</summary>
public sealed record ToolUndoListResult
{
    /// <summary>快照信息列表。</summary>
    public IReadOnlyList<SnapshotInfo> Items { get; init; } = Array.Empty<SnapshotInfo>();
}

// ============================ tool.undoRollback ============================

/// <summary>tool.undoRollback 参数。</summary>
public sealed record ToolUndoRollbackParams
{
    /// <summary>快照序号（≥1）。</summary>
    public int Index { get; init; }
}

/// <summary>tool.undoRollback 结果。</summary>
public sealed record ToolUndoRollbackResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
    /// <summary>回滚到的原始标识。</summary>
    public string Orig { get; init; } = "";
}

// ============================ tool.mcpStatus ============================

/// <summary>tool.mcpStatus 结果。</summary>
public sealed record ToolMcpStatusResult
{
    /// <summary>服务端状态列表。</summary>
    public IReadOnlyList<McpServerStatus> Servers { get; init; } = Array.Empty<McpServerStatus>();
}

/// <summary>MCP 服务端状态。</summary>
public sealed record McpServerStatus
{
    /// <summary>服务端名。</summary>
    public string Name { get; init; } = "";
    /// <summary>是否已连接。</summary>
    public bool Connected { get; init; }
    /// <summary>工具数量。</summary>
    public int ToolCount { get; init; }
    /// <summary>错误信息（可为空）。</summary>
    public string? Error { get; init; }
}
