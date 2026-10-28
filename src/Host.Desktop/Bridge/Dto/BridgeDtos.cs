namespace HaloAgent.Desktop.Bridge;

 /// <summary>P3 桥接 DTO。铁律：DTO 不直接过 CefSharp 桥，一律经 BridgeJson.Options 序列化为 JSON 字符串，前端 JSON.parse（camelCase）。直接返回 DTO 会按 C# 原名（PascalCase）到达前端，导致数据静默全空。</summary>
public sealed class WindowStateDto
{
    public bool Maximized { get; set; }
    public bool Minimized { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dpi { get; set; }
    public int TitleBarHeight { get; set; }
}

public sealed class HostSystemInfo
{
    public string OsDescription { get; set; } = "";
    public bool Is64BitOs { get; set; }
    public bool Is64BitProcess { get; set; }
    public bool IsWindows7 { get; set; }
    public string CefVersion { get; set; } = "";
    public string HostVersion { get; set; } = "";
    public int Dpi { get; set; }
    public bool IsDev { get; set; }
    public bool IsFirstRun { get; set; }
    public string DataDir { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string GpuMode { get; set; } = "";
    public ScreenDto[] Screens { get; set; } = Array.Empty<ScreenDto>();
}

public sealed class ScreenDto
{
    /// <summary>[x, y, width, height]（数组少一层跨进程序列化）。</summary>
    public int[] Bounds { get; set; } = Array.Empty<int>();
    public string DeviceName { get; set; } = "";
    public bool Primary { get; set; }
}

public sealed class EngineStatusDto
{
    /// <summary>starting | ready | restarting | failed | stopped</summary>
    public string State { get; set; } = "stopped";
    public int? Pid { get; set; }
    public string? Reason { get; set; }
}

public sealed class PollResult
{
    public object[] Events { get; set; } = Array.Empty<object>();
    public long NextSeq { get; set; }
}

/// <summary>引擎未就绪：前端据此提示重试（区别于引擎业务错误）。</summary>
public sealed class EngineNotReadyException : InvalidOperationException
{
    public EngineNotReadyException(string message) : base(message) { }
}
