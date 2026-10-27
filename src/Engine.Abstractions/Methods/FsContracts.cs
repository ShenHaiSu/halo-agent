namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ fs.listDir ============================

/// <summary>fs.listDir 参数。</summary>
public sealed record FsListDirParams
{
    /// <summary>目录路径。</summary>
    public string Path { get; init; } = "";
}

/// <summary>fs.listDir 结果。</summary>
public sealed record FsListDirResult
{
    /// <summary>目录条目。</summary>
    public IReadOnlyList<FsDirEntry> Items { get; init; } = Array.Empty<FsDirEntry>();
}

/// <summary>目录条目。</summary>
public sealed record FsDirEntry
{
    /// <summary>文件名。</summary>
    public string Name { get; init; } = "";
    /// <summary>是否为目录。</summary>
    public bool IsDir { get; init; }
    /// <summary>大小（字节）。</summary>
    public long Size { get; init; }
}

// ============================ fs.completePaths ============================

/// <summary>fs.completePaths 参数。</summary>
public sealed record FsCompletePathsParams
{
    /// <summary>路径前缀。</summary>
    public string Prefix { get; init; } = "";
}

/// <summary>fs.completePaths 结果。</summary>
public sealed record FsCompletePathsResult
{
    /// <summary>补全候选。</summary>
    public IReadOnlyList<FsCompletion> Items { get; init; } = Array.Empty<FsCompletion>();
}

/// <summary>路径补全候选。</summary>
public sealed record FsCompletion
{
    /// <summary>路径。</summary>
    public string Path { get; init; } = "";
    /// <summary>是否为目录。</summary>
    public bool IsDir { get; init; }
}

// ============================ fs.validatePath ============================

/// <summary>fs.validatePath 参数。</summary>
public sealed record FsValidatePathParams
{
    /// <summary>待校验路径。</summary>
    public string Path { get; init; } = "";
}

/// <summary>fs.validatePath 结果。</summary>
public sealed record FsValidatePathResult
{
    /// <summary>是否有效。</summary>
    public bool Valid { get; init; }
    /// <summary>规范化后的路径。</summary>
    public string Normalized { get; init; } = "";
    /// <summary>是否为目录。</summary>
    public bool IsDir { get; init; }
    /// <summary>错误信息（可为空）。</summary>
    public string? Error { get; init; }
}
