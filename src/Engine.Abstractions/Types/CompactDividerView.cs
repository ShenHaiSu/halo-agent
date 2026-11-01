namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>
/// 上下文压缩分隔栏视图数据。
/// </summary>
public sealed record CompactDividerView
{
    /// <summary>本会话内的压缩序号（从 1 开始递增）。</summary>
    public int Seq { get; init; }

    /// <summary>模型生成的精炼中文要点摘要内容。</summary>
    public string Summary { get; init; } = "";

    /// <summary>压缩折叠的历史回合数。</summary>
    public int RemovedTurns { get; init; }

    /// <summary>紧跟其后保留的完整回合数。</summary>
    public int KeptTurns { get; init; }

    /// <summary>发生压缩时的时间戳 (毫秒)。</summary>
    public long TsMs { get; init; }
}
