namespace HaloAgent.Engine.Abstractions.Frames;

/// <summary>
/// 接收结果。三态：正常帧 / 对端关闭 / 坏帧。
/// 为什么不用 Frame? + 异常：
///   - 用 null 表示 EOF 时，无法表达“读到坏帧但应该继续”。
///   - 用异常表示坏帧会导致高频异常（性能与日志噪音都很糟）。
/// </summary>
public readonly struct ReceiveResult
{
    /// <summary>正常帧（IsClosed/IsMalformed 时为 null）。</summary>
    public Frame? Frame { get; }
    /// <summary>对端已关闭。</summary>
    public bool IsClosed { get; }
    /// <summary>坏帧（连接仍可用，继续收下一帧）。</summary>
    public bool IsMalformed { get; }
    /// <summary>坏帧原因。</summary>
    public string? Error { get; }

    private ReceiveResult(Frame? frame, bool closed, bool malformed, string? error)
    {
        Frame = frame; IsClosed = closed; IsMalformed = malformed; Error = error;
    }

    /// <summary>正常帧。</summary>
    public static ReceiveResult Ok(Frame frame) => new(frame, false, false, null);
    /// <summary>对端关闭。</summary>
    public static ReceiveResult Closed() => new(null, true, false, null);
    /// <summary>坏帧。</summary>
    public static ReceiveResult Malformed(string error) => new(null, false, true, error);
}
