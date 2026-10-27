using System.Text.Json;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// 流式 tool_calls 累积器。
///
/// ============ 流式协议的特点 ============
/// 一个工具调用会被拆成多片：
///   {"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}
///   {"index":0,"function":{"arguments":"{\"pa"}}
///   {"index":0,"function":{"arguments":"th\":\"a.txt\"}"}}
/// 必须：
///   - 按 index 定位（index 相同就是同一个调用）
///   - id/type/name 只在首片出现，后续片没有 → 只在非空时覆盖
///   - arguments 是**拼接**（不是覆盖）
///   - 有些端点一次性给完整对象参数 → 序列化后拼接
/// </summary>
public sealed class ToolCallAccumulator
{
    private sealed class AccCall
    {
        public string Id = "";
        public string Type = "";
        public string Name = "";
        public System.Text.StringBuilder Arguments = new();
    }

    private readonly List<AccCall> _calls = new();

    public void Merge(JsonElement tc)
    {
        int index = tc.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
            ? (int)idx.GetInt64()
            : 0;

        while (_calls.Count <= index) _calls.Add(new AccCall());
        var cell = _calls[index];

        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var s = id.GetString();
            if (!string.IsNullOrEmpty(s)) cell.Id = s;
        }
        if (tc.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
        {
            var s = ty.GetString();
            if (!string.IsNullOrEmpty(s)) cell.Type = s;
        }

        if (!tc.TryGetProperty("function", out var fn)) return;

        if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            var s = n.GetString();
            if (!string.IsNullOrEmpty(s)) cell.Name = s;
        }

        if (fn.TryGetProperty("arguments", out var a))
        {
            cell.Arguments.Append(a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : a.GetRawText());
        }
    }

    /// <summary>返回累积完成的调用列表（丢弃没有 name 的残缺项）。</summary>
    public List<ToolCall> Finish()
    {
        var result = new List<ToolCall>(_calls.Count);
        foreach (var c in _calls)
        {
            if (string.IsNullOrEmpty(c.Name)) continue;
            result.Add(new ToolCall
            {
                Id = string.IsNullOrEmpty(c.Id) ? "call_" + c.Name : c.Id,
                Type = string.IsNullOrEmpty(c.Type) ? "function" : c.Type,
                Function = new FunctionCall
                {
                    Name = c.Name,
                    Arguments = c.Arguments.Length == 0 ? "{}" : c.Arguments.ToString(),
                },
            });
        }
        return result;
    }
}
