namespace Host.Desktop.Tools;

/// <summary>
/// 工具定义：名称 + 描述 + JSON Schema 参数表。
/// 支持同时导出 OpenAI Chat Completions (chat) 与 Responses API (resp) 双协议 Schema。
/// </summary>
public sealed class ToolDefinition
{
    public ToolDefinition()
    {
        Name = string.Empty;
        Description = string.Empty;
        ParametersJson = "{\"type\":\"object\",\"properties\":{}}";
    }

    public ToolDefinition(string name, string description, string parametersJson)
    {
        Name = name ?? string.Empty;
        Description = description ?? string.Empty;
        ParametersJson = string.IsNullOrWhiteSpace(parametersJson)
            ? "{\"type\":\"object\",\"properties\":{}}"
            : parametersJson;
    }

    public string Name { get; set; }
    public string Description { get; set; }

    /// <summary>
    /// JSON Schema 对象字符串（parameters 部分）。
    /// </summary>
    public string ParametersJson { get; set; }

    /// <summary>
    /// 导出 Chat Completions 协议工具项：{"type":"function","function":{...}}。
    /// </summary>
    public string ToChatToolJson()
    {
        return "{\"type\":\"function\",\"function\":"
            + "{\"name\":" + JsonQuote(Name)
            + ",\"description\":" + JsonQuote(Description)
            + ",\"parameters\":" + ParametersJson + "}}";
    }

    /// <summary>
    /// 导出 Responses API 协议工具项：{"type":"function","name":...,"description":...,"parameters":{...}}。
    /// </summary>
    public string ToResponsesToolJson()
    {
        return "{\"type\":\"function\""
            + ",\"name\":" + JsonQuote(Name)
            + ",\"description\":" + JsonQuote(Description)
            + ",\"parameters\":" + ParametersJson + "}";
    }

    internal static string JsonQuote(string? value)
    {
        if (value == null)
        {
            return "null";
        }

        return "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t") + "\"";
    }
}
