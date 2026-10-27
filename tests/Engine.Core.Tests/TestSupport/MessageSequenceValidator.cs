using HaloAgent.Engine.Core.Llm;

namespace HaloAgent.Engine.Core.Tests.TestSupport;

/// <summary>
/// 七条不变式（I-1 ~ I-7）的可执行判据。只在测试里用，不进生产路径。
///
/// 相对 06-验收目标 A-09 示例代码的两处有意偏离（均与生产实现对齐）：
///  1. arguments 为空串不算违规：ToolArgumentParser.Parse("") 归一化为 {}（I-5），
///     历史里保留模型原文是刻意设计（S16 场景覆盖）。
///  2. arguments 内容本身不做 JSON 合法性校验：解析失败走 I-4（错误回给模型、回合继续），
///     历史保留原始调用是合法状态（S15 场景覆盖）；合法性由 ToolArgumentParser 单测覆盖。
///     仅 arguments 字段缺失（null）算违规。
/// </summary>
public static class MessageSequenceValidator
{
    /// <summary>校验 OpenAI 协议的消息序列合法性。返回违规列表（空 = 合法）。</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<ChatMessage> messages)
    {
        var violations = new List<string>();
        var pending = new List<string>(); // 已声明未响应的 tool_call id
        int i = 0;

        foreach (var m in messages)
        {
            i++;
            if (m.Role != Role.Tool && pending.Count > 0)
            {
                violations.Add($"#{i} 出现 role={m.RoleWire}，但仍有未响应的 tool_calls: " +
                               string.Join(",", pending));
                pending.Clear();
            }

            if (m.Role == Role.Assistant && m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(tc.Id))
                        violations.Add($"#{i} tool_call.id 为空");
                    else if (pending.Contains(tc.Id))
                        violations.Add($"#{i} tool_call.id 重复: {tc.Id}");
                    else
                        pending.Add(tc.Id);

                    if (string.IsNullOrWhiteSpace(tc.Function?.Name))
                        violations.Add($"#{i} tool_call.function.name 为空");

                    // I-5：空串由执行期归一化为 {}，不算违规；只有字段缺失（null）才算。
                    if (tc.Function?.Arguments is null)
                        violations.Add($"#{i} tool_call.arguments 缺失");
                }
            }
            else if (m.Role == Role.Tool)
            {
                if (string.IsNullOrWhiteSpace(m.ToolCallId))
                    violations.Add($"#{i} tool 消息缺 tool_call_id");
                else if (!pending.Remove(m.ToolCallId))
                    violations.Add($"#{i} tool 消息无主（孤儿）: {m.ToolCallId}");
            }
            else if (m.Role is Role.User or Role.System)
            {
                if (string.IsNullOrEmpty(m.Content))
                    violations.Add($"#{i} {m.RoleWire} content 为空");
            }
            else if (m.Role == Role.Assistant)
            {
                // 纯 assistant：content/reasoning 至少其一非空，否则是无意义消息。
                if (string.IsNullOrEmpty(m.Content) && string.IsNullOrEmpty(m.ReasoningContent))
                    violations.Add($"#{i} assistant 既无 content 也无 tool_calls");
            }
        }

        if (pending.Count > 0)
            violations.Add("序列结尾仍有未响应的 tool_calls: " + string.Join(",", pending));

        // I-2：每个 user 之后必须紧跟 system/assistant，不能紧跟 tool
        for (int k = 1; k < messages.Count; k++)
        {
            if (messages[k - 1].Role == Role.User && messages[k].Role == Role.Tool)
                violations.Add($"#{k + 1} user 之后紧跟 tool（排队注入插错位置）");
        }

        return violations;
    }
}
