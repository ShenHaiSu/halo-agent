namespace Host.Desktop.Services;

/// <summary>
/// P007：压缩切点旧逻辑已下线（切片唯一定义见 Services/Context/ActiveContext）。
/// 本类仅保留被摘要输入装配复用的常量；字符串嗅探（SummaryMarker / FindPrevSummaryIndex / IsTailCheckpointed）已删除，边界只认 kind。
/// </summary>
public static class CompactionPlan
{
    /// <summary>摘要分片上限（≤16 块）。</summary>
    public const int MaxSummaryChunks = 16;

    /// <summary>toolResult 降级截断字符数（+ 后缀）。</summary>
    public const int ToolResultReduceChars = 500;

    /// <summary>toolResult 降级后缀。</summary>
    public const string ToolResultTruncatedSuffix = "…[truncated]";

    /// <summary>单消息截断下限字符数（≥64）。</summary>
    public const int SingleMessageMinChars = 64;
}
