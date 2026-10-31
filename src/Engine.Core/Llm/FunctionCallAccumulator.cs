using System.Text.Json;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>Responses function_call 累积器（按 output_index/item_id 累积，取值按 responses 键）。</summary>
public sealed class FunctionCallAccumulator
{
    private sealed class Cell
    {
        public string ItemId = "";
        public string CallId = "";
        public string Name = "";
        public System.Text.StringBuilder Arguments = new();
    }
    private readonly List<Cell> _cells = new();
    private readonly Dictionary<string, int> _byItem = new(StringComparer.Ordinal);

    public void Merge(string? outputIndex, string? itemId, string? callId, string? name, string? argsFragment)
    {
        string key = itemId ?? ("idx:" + (outputIndex ?? _cells.Count.ToString()));
        if (!_byItem.TryGetValue(key, out var idx))
        {
            idx = _cells.Count;
            _cells.Add(new Cell { ItemId = itemId ?? "" });
            _byItem[key] = idx;
        }
        var c = _cells[idx];
        if (!string.IsNullOrEmpty(callId)) c.CallId = callId;
        if (!string.IsNullOrEmpty(name)) c.Name = name;
        if (!string.IsNullOrEmpty(argsFragment)) c.Arguments.Append(argsFragment);
    }

    public List<ToolCall> Finish()
    {
        var result = new List<ToolCall>();
        foreach (var c in _cells)
        {
            if (string.IsNullOrEmpty(c.Name)) continue;
            var id = !string.IsNullOrEmpty(c.CallId) ? c.CallId : (!string.IsNullOrEmpty(c.ItemId) ? c.ItemId : "call_" + c.Name);
            result.Add(new ToolCall { Id = id, Type = "function", Function = new FunctionCall { Name = c.Name, Arguments = c.Arguments.Length == 0 ? "{}" : c.Arguments.ToString() } });
        }
        return result;
    }

 ///
 /// <summary>权威设置某调用的参数（.done / 快照用，直接替换，不追加）。无 cell 时按 key 建一个。</summary>
 public void SetArguments(string? outputIndex, string? itemId, string args)
 {
     string key = itemId ?? ("idx:" + (outputIndex ?? _cells.Count.ToString()));
     if (!_byItem.TryGetValue(key, out var idx))
     {
         idx = _cells.Count;
         _cells.Add(new Cell { ItemId = itemId ?? "" });
         _byItem[key] = idx;
     }
     _cells[idx].Arguments.Clear();
     if (!string.IsNullOrEmpty(args)) _cells[idx].Arguments.Append(args);
 }
 ///
 /// <summary>权威设置某调用的名字/callId（.added / .done 用；非空才覆盖）。</summary>
 public void SetIdentity(string? outputIndex, string? itemId, string? callId, string? name)
 {
     if (string.IsNullOrEmpty(itemId) && string.IsNullOrEmpty(callId) && string.IsNullOrEmpty(name)) return;
     string key = itemId ?? ("idx:" + (outputIndex ?? _cells.Count.ToString()));
     if (!_byItem.TryGetValue(key, out var idx))
     {
         idx = _cells.Count;
         _cells.Add(new Cell { ItemId = itemId ?? "" });
         _byItem[key] = idx;
     }
     var c = _cells[idx];
     if (!string.IsNullOrEmpty(callId)) c.CallId = callId;
     if (!string.IsNullOrEmpty(name)) c.Name = name;
 }
     public void Clear() { _cells.Clear(); _byItem.Clear(); }
}
