namespace HaloAgent.Engine.Storage.Sessions;

/// <summary>
/// 历史修复：补齐"孤儿 tool_calls"。
///
/// ============ 问题 ============
/// 如果历史里有"assistant 声明了 tool_calls 但没有对应 tool 响应"（Esc 中断留下的），
/// resume 后第一次调模型就会被端点拒绝（400）：
///    "An assistant message with 'tool_calls' must be followed by tool messages
///     responding to each 'tool_call_id'"
///
/// ============ 修复算法 ============
/// 遍历消息：
///   - 维护 pending（已声明未响应的 tool_call_id）
///   - 遇到非 tool 消息且 pending 非空 → 先补齐所有欠的响应（占位文本）
///   - assistant 带 tool_calls → 把 id 加进 pending
///   - tool 消息 → 从 pending 移除；若不在 pending（无主）→ 丢弃
///   - 结尾还有欠的 → 补齐
///
/// ============ 为什么占位文本要统一 ============
/// 模型看到"用户中断，该工具调用未执行"会理解为"这个工具没跑"，
/// 而不是"工具返回了空结果"。语义正确，模型不会误判。
/// </summary>
public static class HistoryRepair
{
    public const string PlaceholderToolReply = "⏹ 用户中断，该工具调用未执行。";

    /// <summary>原地修复。返回补了几条。</summary>
    public static int Repair(List<StoredMessage> messages)
    {
        var pending = new List<string>();
        var output = new List<StoredMessage>(messages.Count);
        int fixedCount = 0;

        void FlushPending()
        {
            foreach (var id in pending)
            {
                output.Add(StoredMessage.Tool(id, PlaceholderToolReply));
                fixedCount++;
            }
            pending.Clear();
        }

        foreach (var m in messages)
        {
            // 非 tool 消息是 tool 链的终止符：先把欠的补齐
            if (m.Role != StoredRole.Tool && pending.Count > 0)
                FlushPending();

            switch (m.Role)
            {
                case StoredRole.Assistant:
                    if (m.ToolCalls is { Count: > 0 })
                    {
                        foreach (var c in m.ToolCalls)
                        {
                            if (!pending.Contains(c.Id)) pending.Add(c.Id);
                        }
                    }
                    output.Add(m);
                    break;

                case StoredRole.Tool:
                    var id = m.ToolCallId;
                    if (id is not null && pending.Remove(id))
                    {
                        output.Add(m);
                    }
                    // 无主的 tool 消息：丢弃（留着也会被端点拒）
                    break;

                default:
                    output.Add(m);
                    break;
            }
        }

        // 历史结尾还欠着（最后一条是带 tool_calls 的 assistant）
        FlushPending();

        messages.Clear();
        messages.AddRange(output);
        return fixedCount;
    }
}
