using System;
using System.Collections.Generic;
using System.Text;

namespace Host.Desktop.LlmGateway.Adapters;

/// <summary>
/// 正文通道内嵌 <c>&lt;think&gt;…&lt;/think&gt;</c> 的流式提取器。
/// <para>
/// 只吃 content 通道的增量：<c>reasoning_content / reasoning</c> 已经是专用字段，
/// tool 调用的 arguments 里出现同名标签是合法的 JSON 字符串，把它们喂进来会把参数吃成思维链。
/// </para>
/// <para>
/// 每路流一个实例：标签状态与跨包残尾都是流内状态，静态共享会让并发流的思维链串味。
/// </para>
/// <para>
/// 无标签时输出与输入 1:1；只有分块边界恰好落在疑似标签前缀上（如以 "&lt;thi" 结尾）时，
/// 该前缀会顺延到下一包一并发出——字节序列不变，只是事件边界挪了一格。
/// </para>
/// </summary>
public sealed class ThinkTagStreamFilter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    // 残尾只需保留到「不足一个完整标签」的长度：闭标签最长（8 字符），故上界为 7。
    private const int MaxPendingLength = 7;

    private bool _inThink;
    private string _pending = string.Empty;

    /// <summary>当前是否处于思维链内（供手工穿插工具调用时核对状态未丢）。</summary>
    public bool InThink => _inThink;

    /// <summary>已切出的思维链段数（无标签流恒为 0，可用于排查标签口径误判）。</summary>
    public int ReasoningSegmentCount { get; private set; }

    /// <summary>
    /// 追加一段 content 增量，返回按出现顺序切出的段（空段已剔除）。
    /// </summary>
    public IReadOnlyList<ThinkTagSegment> Append(string? chunk)
    {
        var segments = new List<ThinkTagSegment>();
        if (string.IsNullOrEmpty(chunk))
        {
            return segments;
        }

        // 上次留下的残尾可能是本包标签的开头，先拼接再扫描，否则跨包标签会被切成普通文本。
        string buffer = _pending + chunk;
        _pending = string.Empty;

        bool currentIsReasoning = _inThink;
        int index = 0;
        var current = new StringBuilder();

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            if (currentIsReasoning)
            {
                ReasoningSegmentCount++;
            }

            segments.Add(new ThinkTagSegment(currentIsReasoning, current.ToString()));
            current.Clear();
        }

        while (index < buffer.Length)
        {
            if (!_inThink && StartsAt(buffer, index, OpenTag))
            {
                FlushCurrent();
                _inThink = true;
                currentIsReasoning = true;
                index += OpenTag.Length;
                continue;
            }

            if (_inThink && StartsAt(buffer, index, CloseTag))
            {
                FlushCurrent();
                _inThink = false;
                currentIsReasoning = false;
                index += CloseTag.Length;
                continue;
            }

            // 尾部不足一个标签且构成某个标签的前缀：本包无法判定，留到下包续接。
            if (
                buffer.Length - index <= MaxPendingLength
                && IsPendingPrefix(buffer, index, _inThink)
            )
            {
                _pending = buffer.Substring(index);
                break;
            }

            current.Append(buffer[index]);
            index++;
        }

        FlushCurrent();
        return segments;
    }

    /// <summary>
    /// 流结束兜底：把未判定完的残尾按当前段归属发出，避免截断在标签中间时丢字节
    /// （未闭合的思维链因此不会丢内容）。
    /// </summary>
    public IReadOnlyList<ThinkTagSegment> FlushEnd()
    {
        var segments = new List<ThinkTagSegment>();
        if (_pending.Length > 0)
        {
            if (_inThink)
            {
                ReasoningSegmentCount++;
            }

            segments.Add(new ThinkTagSegment(_inThink, _pending));
            _pending = string.Empty;
        }

        return segments;
    }

    // 序数比较：标签口径按全小写严格匹配，大小写变体与带属性写法一律当普通文本，不做模糊容忍。
    private static bool StartsAt(string buffer, int index, string tag)
    {
        if (buffer.Length - index < tag.Length)
        {
            return false;
        }

        for (int offset = 0; offset < tag.Length; offset++)
        {
            if (buffer[index + offset] != tag[offset])
            {
                return false;
            }
        }

        return true;
    }

    // 只在标签确实可能出现的位置扣留：正文区只等开标签，思维链区只等闭标签，
    // 反过来的那半边标签即便真出现也只会被当普通文本，没必要压着不发。
    private static bool IsPendingPrefix(string buffer, int index, bool inThink)
    {
        return inThink ? IsPrefix(buffer, index, CloseTag) : IsPrefix(buffer, index, OpenTag);
    }

    private static bool IsPrefix(string buffer, int index, string tag)
    {
        int length = buffer.Length - index;
        if (length >= tag.Length)
        {
            return false;
        }

        for (int offset = 0; offset < length; offset++)
        {
            if (buffer[index + offset] != tag[offset])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>切出的一段正文：<paramref name="IsReasoning"/> 为 true 表示应作为思考增量下发。</summary>
public readonly record struct ThinkTagSegment(bool IsReasoning, string Text);
