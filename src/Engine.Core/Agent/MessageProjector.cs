namespace HaloAgent.Engine.Core.Agent;

/// <summary>
/// 把 OpenAI 格式的 ChatMessage 投影成渲染友好的 MessageView。
/// 核心工作：工具调用与结果合并（assistant + tool → 一张工具卡片）。
/// </summary>
public static class MessageProjector
{
    /// <summary>把完整消息列表投影成渲染列表（合并工具调用与结果）。</summary>
    public static List<HaloAgent.Engine.Abstractions.Types.MessageView> Project(
        IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages)
    {
        var result = new List<HaloAgent.Engine.Abstractions.Types.MessageView>(messages.Count);
        var toolResults = new Dictionary<string, (bool ok, string output)>(StringComparer.Ordinal);

        foreach (var m in messages)
        {
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.Tool && m.ToolCallId is not null)
            {
                var output = m.Content ?? string.Empty;
                var ok = !output.StartsWith("⏹", StringComparison.Ordinal)
                         && !output.StartsWith("操作未执行", StringComparison.Ordinal)
                         && !output.StartsWith("工具参数解析失败", StringComparison.Ordinal)
                         && !output.Contains("执行失败:", StringComparison.Ordinal);
                toolResults[m.ToolCallId] = (ok, output);
            }
        }

        foreach (var m in messages)
        {
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.System) continue;
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.Tool) continue;

            var view = new HaloAgent.Engine.Abstractions.Types.MessageView
            {
                Id = "m-" + Guid.NewGuid().ToString("N")[..8],
                Role = m.RoleWire,
                Text = m.Content ?? string.Empty,
                Reasoning = m.ReasoningContent,
                TsMs = 0,
            };

            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new List<HaloAgent.Engine.Abstractions.Types.ToolCallView>(m.ToolCalls.Count);
                foreach (var tc in m.ToolCalls)
                {
                    toolResults.TryGetValue(tc.Id, out var res);
                    calls.Add(new HaloAgent.Engine.Abstractions.Types.ToolCallView
                    {
                        Id = tc.Id,
                        Name = tc.Function.Name,
                        Args = tc.Function.Arguments,
                        Ok = toolResults.ContainsKey(tc.Id) ? res.ok : null,
                        Output = toolResults.ContainsKey(tc.Id) ? res.output : null,
                    });
                }
                view = view with { ToolCalls = calls };
            }

            result.Add(view);
        }

        return result;
    }

    /// <summary>单条投影（不含工具合并，用于增量场景）。</summary>
    public static HaloAgent.Engine.Abstractions.Types.MessageView ToView(
        HaloAgent.Engine.Core.Llm.ChatMessage m) => new()
    {
        Id = "m-" + Guid.NewGuid().ToString("N")[..8],
        Role = m.RoleWire,
        Text = m.Content ?? string.Empty,
        Reasoning = m.ReasoningContent,
        TsMs = 0,
    };
}
