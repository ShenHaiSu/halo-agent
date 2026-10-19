using System.Text;
using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>
/// need17-01：Schema 生成器（Utf8JsonWriter 构建，单元可测），替代手拼 JSON 转义串。
/// 顶层固定 {"type":"object","properties":{...},"required":[...],"additionalProperties":false}。
/// 字段支持 type/description/enum/minLength/maxLength/minimum/maximum/default。
/// 参数语义不变，只换生成方式；描述文本按 03/04/05 分册逐字写入（主 Agent 侧调用）。
/// </summary>
public sealed class ToolSchemaBuilder
{
    private sealed class FieldSpec
    {
        public string Name = string.Empty;
        public string Type = "string";
        public string Description = string.Empty;
        public bool Required;
        public string[]? EnumValues;
        public int? MinLength;
        public int? MaxLength;
        public long? Minimum;
        public long? Maximum;
        public string? DefaultRawJson;
        public string? ItemsType;
    }

    private readonly List<FieldSpec> _fields = new();

    public ToolSchemaBuilder AddString(
        string name,
        string description,
        bool required = false,
        string[]? enumValues = null,
        int? minLength = null,
        int? maxLength = null,
        string? defaultValue = null)
    {
        _fields.Add(new FieldSpec
        {
            Name = name,
            Type = "string",
            Description = description ?? string.Empty,
            Required = required,
            EnumValues = enumValues,
            MinLength = minLength,
            MaxLength = maxLength,
            DefaultRawJson = defaultValue == null ? null : JsonSerializer.Serialize(defaultValue),
        });
        return this;
    }

    public ToolSchemaBuilder AddInteger(
        string name,
        string description,
        bool required = false,
        long? minimum = null,
        long? maximum = null,
        long? defaultValue = null)
    {
        _fields.Add(new FieldSpec
        {
            Name = name,
            Type = "integer",
            Description = description ?? string.Empty,
            Required = required,
            Minimum = minimum,
            Maximum = maximum,
            DefaultRawJson = defaultValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return this;
    }

    public ToolSchemaBuilder AddBoolean(string name, string description, bool required = false, bool? defaultValue = null)
    {
        _fields.Add(new FieldSpec
        {
            Name = name,
            Type = "boolean",
            Description = description ?? string.Empty,
            Required = required,
            DefaultRawJson = defaultValue == null ? null : (defaultValue.Value ? "true" : "false"),
        });
        return this;
    }

    /// <summary>数组字段（itemsType 如 "object"/"string"；暂只做一层声明，深层元素校验由业务层负责）。</summary>
    public ToolSchemaBuilder AddArray(string name, string description, bool required = false, string itemsType = "object")
    {
        _fields.Add(new FieldSpec
        {
            Name = name,
            Type = "array",
            Description = description ?? string.Empty,
            Required = required,
            ItemsType = itemsType,
        });
        return this;
    }

    /// <summary>对象字段（结构由业务层校验，声明层只约束类型）。</summary>
    public ToolSchemaBuilder AddObject(string name, string description, bool required = false)
    {
        _fields.Add(new FieldSpec
        {
            Name = name,
            Type = "object",
            Description = description ?? string.Empty,
            Required = required,
        });
        return this;
    }

    /// <summary>
    /// 构建闭合 Schema 字符串（required 按加入顺序；additionalProperties 恒为 false）。
    /// </summary>
    public string Build()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            foreach (var f in _fields)
            {
                w.WriteStartObject(f.Name);
                w.WriteString("type", f.Type);
                if (!string.IsNullOrEmpty(f.Description))
                {
                    w.WriteString("description", f.Description);
                }
                if (f.EnumValues != null && f.EnumValues.Length > 0)
                {
                    w.WriteStartArray("enum");
                    foreach (var e in f.EnumValues)
                    {
                        w.WriteStringValue(e);
                    }
                    w.WriteEndArray();
                }
                if (f.MinLength.HasValue)
                {
                    w.WriteNumber("minLength", f.MinLength.Value);
                }
                if (f.MaxLength.HasValue)
                {
                    w.WriteNumber("maxLength", f.MaxLength.Value);
                }
                if (f.Minimum.HasValue)
                {
                    w.WriteNumber("minimum", f.Minimum.Value);
                }
                if (f.Maximum.HasValue)
                {
                    w.WriteNumber("maximum", f.Maximum.Value);
                }
                if (f.DefaultRawJson != null)
                {
                    w.WritePropertyName("default");
                    using var def = JsonDocument.Parse(f.DefaultRawJson);
                    def.RootElement.WriteTo(w);
                }
                if (f.Type == "array" && !string.IsNullOrEmpty(f.ItemsType))
                {
                    w.WriteStartObject("items");
                    w.WriteString("type", f.ItemsType);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartArray("required");
            foreach (var f in _fields)
            {
                if (f.Required)
                {
                    w.WriteStringValue(f.Name);
                }
            }
            w.WriteEndArray();
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
