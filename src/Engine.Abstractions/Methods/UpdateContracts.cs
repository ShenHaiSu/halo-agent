namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ update.check ============================

/// <summary>update.check 结果。</summary>
public sealed record UpdateCheckResult
{
    /// <summary>是否有可用更新。</summary>
    public bool Available { get; init; }
    /// <summary>最新版本号（无更新时为 null）。</summary>
    public string? LatestVersion { get; init; }
    /// <summary>更新说明（可为空）。</summary>
    public string? Notes { get; init; }
}

// ============================ update.install ============================

/// <summary>update.install 结果。</summary>
public sealed record UpdateInstallResult
{
    /// <summary>是否已开始安装。</summary>
    public bool Started { get; init; }
}
