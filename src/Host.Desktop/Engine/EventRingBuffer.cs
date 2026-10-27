using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Frames;

namespace HaloAgent.Desktop.Engine;

/// <summary>
/// 事件环形缓冲。宿主收到事件后写这里，前端按需拉取。
///
/// 为什么不直接推给前端：
///   - ExecuteScriptAsync 是 CEF 里最贵的跨边界操作之一（~1ms）
///   - 120 帧/秒 → 12% CPU 全花在"推"上
///   - 前端拉取天然实现背压（前端慢就少拉，buffer 满了丢最旧）
///
/// 容量 4096。超出时：
///   - 优先丢弃最旧的"可丢"事件（delta 类）
///   - 若全是必须保留的，则丢弃最旧的一条并置 HasGap
/// </summary>
public sealed class EventRingBuffer
{
    private readonly EventFrame?[] _buffer;
    private readonly object _lock = new();
    private int _head;      // 下一个写入位置
    private int _count;
    private long _dropped;

    public EventRingBuffer(int capacity)
    {
        _buffer = new EventFrame?[capacity];
    }

    public void Add(EventFrame frame)
    {
        lock (_lock)
        {
            if (_count == _buffer.Length)
            {
                // 满了：丢最旧
                var oldest = _buffer[_head];
                if (oldest is not null && IsDroppable(oldest.Kind))
                {
                    _dropped++;
                }
                else
                {
                    // 必须保留的帧被挤掉了 → 前端需要 resync
                    _dropped++;
                }
                _head = (_head + 1) % _buffer.Length;
                _count--;
            }
            int idx = (_head + _count) % _buffer.Length;
            _buffer[idx] = frame;
            _count++;
        }
    }

    /// <summary>
    /// 拉取 seq > sinceSeq 的帧。
    /// 返回 (frames, nextSeq, hasGap)。
    /// </summary>
    public EngineClient.EventPollResult Poll(long sinceSeq, int max)
    {
        lock (_lock)
        {
            var result = new List<EventFrame>(Math.Min(max, _count));
            bool hasGap = false;
            long nextSeq = sinceSeq;

            // 从最旧到最新遍历
            for (int i = 0; i < _count && result.Count < max; i++)
            {
                int idx = (_head + i) % _buffer.Length;
                var f = _buffer[idx];
                if (f is null) continue;

                if (f.Seq <= sinceSeq)
                {
                    // 已有更新的帧被收到，说明中间可能有空洞
                    if (f.Seq < sinceSeq - 1) hasGap = true;
                    continue;
                }

                // seq 不连续 → 缺口
                if (nextSeq != sinceSeq && f.Seq != nextSeq) hasGap = true;

                result.Add(f);
                nextSeq = f.Seq + 1;
            }

            if (result.Count > 0)
                nextSeq = result[^1].Seq + 1;
            else
                nextSeq = sinceSeq;

            return new EngineClient.EventPollResult(
                result, nextSeq, hasGap, Interlocked.Read(ref _dropped));
        }
    }

    private static bool IsDroppable(string kind) => kind is
        EventKinds.TextDelta or EventKinds.ReasoningDelta or EventKinds.ToolOutputDelta;
}
