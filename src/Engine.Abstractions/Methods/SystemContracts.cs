namespace HaloAgent.Engine.Abstractions.Methods;

// ============================ system.ping ============================

/// <summary>system.ping 结果。</summary>
public sealed record SystemPingResult
{
    /// <summary>固定 true。</summary>
    public bool Pong { get; init; }
    /// <summary>引擎版本。</summary>
    public string Version { get; init; } = "";
    /// <summary>协议版本。</summary>
    public int Protocol { get; init; }
    /// <summary>进程 id。</summary>
    public int Pid { get; init; }
    /// <summary>运行时长（毫秒）。</summary>
    public long UptimeMs { get; init; }
    /// <summary>服务端时间（毫秒时间戳）。</summary>
    public long ServerTimeMs { get; init; }
}

// ============================ system.info ============================

/// <summary>system.info 结果。</summary>
public sealed record SystemInfoResult
{
    /// <summary>引擎版本。</summary>
    public string Version { get; init; } = "";
    /// <summary>协议版本。</summary>
    public int Protocol { get; init; }
    /// <summary>进程 id。</summary>
    public int Pid { get; init; }
    /// <summary>操作系统版本。</summary>
    public string OsVersion { get; init; } = "";
    /// <summary>是否为 Win7。</summary>
    public bool IsWin7 { get; init; }
    /// <summary>操作系统是否为 64 位。</summary>
    public bool Is64BitOs { get; init; }
    /// <summary>进程是否为 64 位。</summary>
    public bool Is64BitProcess { get; init; }
    /// <summary>运行时描述。</summary>
    public string Runtime { get; init; } = "";
    /// <summary>数据目录。</summary>
    public string DataDir { get; init; } = "";
    /// <summary>启动时刻（毫秒时间戳）。</summary>
    public long StartedAtMs { get; init; }
}

// ============================ system.protocol ============================

/// <summary>system.protocol 结果。</summary>
public sealed record SystemProtocolResult
{
    /// <summary>协议版本。</summary>
    public int Version { get; init; }
    /// <summary>编码（固定 “ndjson”）。</summary>
    public string Encoding { get; init; } = "ndjson";
    /// <summary>单帧上限（字节）。</summary>
    public int MaxFrameBytes { get; init; }
    /// <summary>全部方法名。</summary>
    public IReadOnlyList<string> Methods { get; init; } = Array.Empty<string>();
    /// <summary>全部事件名。</summary>
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();
}

// ============================ system.shutdown ============================

/// <summary>system.shutdown 参数。</summary>
public sealed record SystemShutdownParams
{
    /// <summary>是否优雅关闭（可为空）。</summary>
    public bool? Graceful { get; init; }
}

/// <summary>通用 { ok } 结果。</summary>
public sealed record OkResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }
}

// ============================ system.diagnostics ============================

/// <summary>system.diagnostics 结果（空对象，预留诊断字段）。</summary>
public sealed record SystemDiagnosticsResult
{
}
