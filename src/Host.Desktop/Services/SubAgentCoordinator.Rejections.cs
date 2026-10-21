using System.Collections.Concurrent;

namespace Host.Desktop.Services;

/// <summary>
/// need23：子代理拒绝记录（一次拒绝 = 一条；供设置中心·智能体行为·子运行监控卡的"最近拒绝 / 拒绝与自愈记录"）。
/// 记录来源：spawn 配额超限、spawn 参数非法、Barrier 总体超时。
/// </summary>
public sealed class SubRejectionRecord
{
    /// <summary>拒绝码（SUBAGENT_QUOTA_EXCEEDED / SUBAGENT_VALIDATION / SUBAGENT_BARRIER_TIMEOUT）。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>触发的工具调用 id（无则空串）。</summary>
    public string CallId { get; init; } = string.Empty;

    /// <summary>人类可读原因。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>发生时间（UTC）。</summary>
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>need23：SubAgentCoordinator 的拒绝记录侧（与建子/Barrier 主体分离，便于单测聚焦）。</summary>
public sealed partial class SubAgentCoordinator
{
    /// <summary>need23：拒绝记录保留上限（环形，超出丢最旧）。</summary>
    private const int MaxRejectionRecords = 50;

    /// <summary>need23：拒绝记录环形缓冲（进程内；Coordinator 为单例，跨轮次可见）。</summary>
    private readonly ConcurrentQueue<SubRejectionRecord> _rejections = new();

    /// <summary>need23：记录一次子代理拒绝（线程安全；不抛异常，绝不影响主流程）。</summary>
    public void RecordRejection(string code, string callId, string message)
    {
        try
        {
            _rejections.Enqueue(
                new SubRejectionRecord
                {
                    Code = code ?? string.Empty,
                    CallId = callId ?? string.Empty,
                    Message = message ?? string.Empty,
                    AtUtc = DateTime.UtcNow,
                }
            );
            while (_rejections.Count > MaxRejectionRecords && _rejections.TryDequeue(out _))
            {
                /* 丢最旧 */
            }
        }
        catch
        {
            /* 记录失败不影响主流程 */
        }
    }

    /// <summary>need23：取最近的拒绝记录（新的在前，最多 max 条）。</summary>
    public IReadOnlyList<SubRejectionRecord> GetRecentRejections(int max = 20)
    {
        try
        {
            var list = _rejections.ToList();
            if (list.Count > max)
                list = list.Skip(list.Count - max).ToList();
            list.Reverse();
            return list;
        }
        catch
        {
            return Array.Empty<SubRejectionRecord>();
        }
    }
}
