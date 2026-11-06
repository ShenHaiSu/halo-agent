using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Util;

namespace HaloAgent.Engine.Core.Agent;

/// <summary>
/// 历史投影裁剪（默认关）：只裁剪发给模型的上下文，不动落盘历史。
/// 原则：已发送历史不可变（append-only）；两次压缩之间只追加不改写，前缀缓存命中最大化。
/// 兜底超长只允许在压缩时一次性重写，禁止每轮滑动改写。默认关闭，度量后再议是否开。
/// 磁盘历史保持全文，可经 ToolResultAsync 按需拉取。
/// </summary>
public static class ContextProjector
{
    public const int KeepRecentFullToolOutputs = 2;
    public const int PrunedToolOutputMaxChars = 2000;

    public static List<ChatMessage> ProjectForLlm(IReadOnlyList<ChatMessage> source, bool enabled)
    {
        if (!enabled) return source.ToList();
        // 仅投影：截断较旧的 tool 结果，保留最近 N 条全文
        var toolIndices = new List<int>();
        for (int i = 0; i < source.Count; i++)
            if (source[i].Role == Role.Tool) toolIndices.Add(i);
        var keep = new HashSet<int>(toolIndices.TakeLast(KeepRecentFullToolOutputs));
        var result = new List<ChatMessage>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            var m = source[i];
            if (m.Role == Role.Tool && !keep.Contains(i))
            {
                var content = m.Content ?? string.Empty;
                if (content.Length > PrunedToolOutputMaxChars)
                {
                    var pruned = TextUtil.TruncateMiddle(content, PrunedToolOutputMaxChars);
                    m = m with { Content = pruned + "\n…(历史 tool 结果已裁剪，全文可按需拉取)" };
                }
            }
            result.Add(m);
        }
        return result;
    }
}
