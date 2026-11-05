using System.Text.Json;

namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 已校验的工具参数。
///
/// ============ 解决什么问题 ============
/// 旧路径：JsonHelpers.GetStringOrNull 把「缺失」与「类型不符」都返回 null
/// → 工具只能报「path 不能为空」，但模型实际传的是数字 42，文案误导。
///
/// 新路径：先从 ParameterSchema 校验出结构化错误（带点号路径与期望类型），
/// 校验通过后再提供强类型读取；工具不再自己判空。
///
 /// ============ coerce 规则（与参考文档对齐）============
 ///   - 数字字符串 "3" → 3（integer/number）
 ///   - "true"/"false" → bool
 ///   - 单值 → 单元素数组（目标 schema 是 array 时）
 ///   - null 视为「未提供」（可选参数直接丢弃；必填参数报缺失）
 ///   - 不做 union 分支穷举（HALO 现无 union schema，遇到 type 数组只按「任一匹配即通过」处理）
 ///   - 数值范围约束：支持 minimum / maximum（integer/number），越界即报错
 ///     （供 read_file offset/limit 等使用；错误文案带期望范围与实际值）
///
/// ============ required 语义 ============
/// 只判存在性与类型，不判空串。空串语义由工具自己决定
/// （如 run_shell_command 明确拒绝空 command）。不要在这里加非空校验。
/// </summary>
public sealed class ToolArguments
{
    private readonly Dictionary<string, JsonElement> _values;

    private ToolArguments(Dictionary<string, JsonElement> values) => _values = values;

    /// <summary>
    /// 解析 + 校验。失败抛 ToolValidationException（Message 为完整三段式文案）。
    /// raw 由 ToolArgumentParser 产出，已是 object。
    /// </summary>
    public static ToolArguments Bind(string toolName, JsonElement schema, JsonElement raw)
    {
        var coerced = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var errors = new List<ToolValidationError>();
        var received = raw.GetRawText();

        if (raw.ValueKind != JsonValueKind.Object)
        {
            throw BuildValidationFailure(toolName, new List<ToolValidationError>
            {
                new("root", "期望 object，实际 " + KindName(raw.ValueKind)),
            }, received);
        }

        var required = ReadRequired(schema);
        var props = ReadProperties(schema);

        foreach (var (name, propSchema) in props)
        {
            var has = raw.TryGetProperty(name, out var value);
            if (!has || value.ValueKind == JsonValueKind.Null)
            {
                if (required.Contains(name))
                    errors.Add(new(name, "缺失必填参数"));
                continue;
            }

            if (TryCoerce(value, propSchema, name, errors, out var coercedValue))
                coerced[name] = coercedValue;
        }

        // 未知字段：不报错（保持宽容，模型常带多余字段）
        if (errors.Count > 0)
            throw BuildValidationFailure(toolName, errors, received);

        return new ToolArguments(coerced);
    }

    // ---------------- 强类型读取（工具用）----------------

    /// <summary>可选字符串。未提供或空串 → null。</summary>
    public string? StringOrNull(string name)
    {
        if (!_values.TryGetValue(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>必填字符串。调用前 schema 已保证存在，故直接取值。</summary>
    public string RequiredString(string name)
    {
        if (!_values.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"schema 与实现不一致：{name} 应为必填 string");
        return v.GetString()!;
    }

    public long? LongOrNull(string name)
    {
        if (!_values.TryGetValue(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    }

    public bool? BoolOrNull(string name)
    {
        if (!_values.TryGetValue(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>是否提供了某参数（区分「未提供」与「提供了空串」）。</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    // ---------------- 内部：schema 解析与 coerce ----------------

    private static HashSet<string> ReadRequired(JsonElement schema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("required", out var req) &&
            req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) set.Add(item.GetString()!);
        }
        return set;
    }

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement schema)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out var props) &&
            props.ValueKind == JsonValueKind.Object)
        {
            // ★ 必须 Clone：JsonElement 的生命周期挂在原始 JsonDocument 上，
            //   调用方一旦 dispose，后续读取就会抛 ObjectDisposedException。
            foreach (var p in props.EnumerateObject()) map[p.Name] = p.Value.Clone();
        }
        return map;
    }

    private static bool TryCoerce(
        JsonElement value, JsonElement propSchema,
        string path, List<ToolValidationError> errors, out JsonElement result)
    {
        result = value;

        var expected = ReadExpectedType(propSchema);   // 可能是 null（未声明 type）
        if (expected.Count == 0)
        {
            // 无 type 声明：原样接受，但仍跑 enum 检查（若有）
            return CheckEnum(value, propSchema, path, errors);
        }

         foreach (var t in expected)
         {
             if (MatchesType(value, t))
             {
                 if (!CheckEnum(value, propSchema, path, errors)) return false;
                 return CheckRange(value, propSchema, path, errors);
             }
         }

         // 单一期望类型且允许 coerce
         if (expected.Count == 1 && TryCoerceTo(value, expected[0], out var coerced))
         {
             result = coerced;
             if (!CheckEnum(result, propSchema, path, errors)) return false;
             return CheckRange(result, propSchema, path, errors);
         }

        errors.Add(new(path, $"期望 {string.Join(" 或 ", expected)}，实际 {KindName(value.ValueKind)}"));
        return false;
    }

     private static bool TryCoerceTo(JsonElement value, string target, out JsonElement result)
     {
         result = value;
 
         if (target == "integer" || target == "number")
         {
             if (value.ValueKind == JsonValueKind.String &&
                 double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
             {
                 if (target == "integer" && Math.Abs(d % 1) > double.Epsilon) return false;
                 if (target == "integer" && (d > long.MaxValue || d < long.MinValue)) return false;
                 result = JsonSerializer.SerializeToElement(target == "integer" ? (object)(long)d : d);
                 return true;
             }
             return false;
         }
 
         if (target == "boolean")
         {
             if (value.ValueKind == JsonValueKind.String)
             {
                 var s = value.GetString()?.Trim().ToLowerInvariant();
                 if (s is "true" or "1") { result = JsonSerializer.SerializeToElement(true); return true; }
                 if (s is "false" or "0") { result = JsonSerializer.SerializeToElement(false); return true; }
             }
             return false;
         }
 
         // 数字/布尔不自动转字符串：{"path":42} 必须报“期望 string，实际 number”，不能静默变 "42"。
         if (target == "string") return false;
 
         if (target == "array")
         {
             // 单值 → 单元素数组
             result = JsonSerializer.SerializeToElement(new[] { value });
             return true;
         }
 
         return false;
     }

    private static bool MatchesType(JsonElement value, string t) => t switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool CheckEnum(
        JsonElement value, JsonElement propSchema, string path, List<ToolValidationError> errors)
    {
        if (propSchema.ValueKind != JsonValueKind.Object) return true;
        if (!propSchema.TryGetProperty("enum", out var en) || en.ValueKind != JsonValueKind.Array)
            return true;

        var raw = value.GetRawText();
        var allowed = new List<string>();
        foreach (var item in en.EnumerateArray())
        {
            allowed.Add(item.GetRawText());
            if (item.GetRawText() == raw) return true;
        }

        errors.Add(new(path, $"期望其中之一 {string.Join(", ", allowed)}，实际 {raw}"));
        return false;
    }
     /// <summary>
     /// 数值范围约束：支持 JSON Schema 的 minimum / maximum（integer/number 生效）。
     /// 越界时报错（文案带期望范围与实际值），供模型自纠。
     /// </summary>
     private static bool CheckRange(
         JsonElement value, JsonElement propSchema, string path, List<ToolValidationError> errors)
     {
         if (propSchema.ValueKind != JsonValueKind.Object) return true;
         bool hasMin = propSchema.TryGetProperty("minimum", out var minEl)
             && minEl.ValueKind == JsonValueKind.Number && minEl.TryGetDouble(out _);
         bool hasMax = propSchema.TryGetProperty("maximum", out var maxEl)
             && maxEl.ValueKind == JsonValueKind.Number && maxEl.TryGetDouble(out _);
         if (!hasMin && !hasMax) return true;
         if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var actual)) return true;
         if (hasMin)
         {
             double min = minEl.GetDouble();
             if (actual < min)
             {
                 errors.Add(new(path, $"期望 >= {minEl.GetRawText()}，实际 {value.GetRawText()}"));
                 return false;
             }
         }
         if (hasMax)
         {
             double max = maxEl.GetDouble();
             if (actual > max)
             {
                 errors.Add(new(path, $"期望 <= {maxEl.GetRawText()}，实际 {value.GetRawText()}"));
                 return false;
             }
         }
         return true;
     }

    private static List<string> ReadExpectedType(JsonElement propSchema)
    {
        var list = new List<string>();
        if (propSchema.ValueKind != JsonValueKind.Object) return list;
        if (!propSchema.TryGetProperty("type", out var t)) return list;

        if (t.ValueKind == JsonValueKind.String)
        {
            list.Add(t.GetString()!);
        }
        else if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in t.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        }
        return list;
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    // ---------------- 文案组装 ----------------

    /// <summary>
    /// 三段式：固定前缀 + 逐条「  - path: message」+ 收到的原始参数。
    /// 前缀 `参数校验失败` 是固定可 grep 的，别改。
    /// </summary>
    internal static ToolValidationException BuildValidationFailure(
        string toolName, List<ToolValidationError> errors, string receivedJson)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("工具 \"").Append(toolName).Append("\" 参数校验失败:\n");
        foreach (var e in errors)
            sb.Append("  - ").Append(e.Path).Append(": ").Append(e.Message).Append('\n');
        sb.Append("\n收到的参数:\n").Append(receivedJson);

        return new ToolValidationException(sb.ToString(), errors);
    }
}
