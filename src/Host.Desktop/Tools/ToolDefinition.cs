using Host.Desktop.Core.Models;

namespace Host.Desktop.Tools;

/// <summary>
/// 工具定义：名称 + 描述 + JSON Schema 参数表。
/// 支持同时导出 OpenAI Chat Completions (chat) 与 Responses API (resp) 双协议 Schema。
/// need17-01：声明层扩展（Strict/Exposure/Category/PromptBrief/PromptRules/ConcurrencyHint 占位）。
/// 老三字段构造与两序列化签名不变；新增元数据全部可选，老构造零改动。
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
    /// need17-01：单工具 strict 覆盖。null=跟随全局 StrictToolsMode（Prefer 默认发送 strict:true）。
    /// </summary>
    public bool? Strict { get; set; }

    /// <summary>need17-01：工具曝光度。Hidden 仍可 Dispatch 但不进 GetActiveDefinitions（edit 分发器用）。</summary>
    public ToolExposure Exposure { get; set; } = ToolExposure.Direct;

    /// <summary>need17-01：工具分类（core | search | overlay），仅用于提示词装配。</summary>
    public string Category { get; set; } = "core";

    /// <summary>need17-01/06：工具段一行简介；为空则提示词工具段省略。</summary>
    public string PromptBrief { get; set; } = string.Empty;

    /// <summary>need17-01/06：规则段（选型/约束教学行），由 06 动态装配消费。</summary>
    public string[] PromptRules { get; set; } = Array.Empty<string>();

    /// <summary>need17-01：并发提示占位（06 落子，声明层先占位）。</summary>
    public string ConcurrencyHint { get; set; } = string.Empty;

    /// <summary>
    /// need17-01：按全局模式解析本工具是否发送 strict。
    /// Strict 显式值优先；null 时 Off=不发送，其余（Prefer/Require）=发送。
    /// </summary>
    public bool ResolveStrict(StrictToolsMode mode) => Strict ?? (mode != StrictToolsMode.Off);

    /// <summary>
    /// 导出 Chat Completions 协议工具项：{"type":"function","function":{...}}。
    /// need17-01：Prefer 默认按 ResolveStrict(Prefer) 在 function 内写 "strict": true。
    /// </summary>
    public string ToChatToolJson()
    {
        return ToChatToolJson(StrictToolsMode.Prefer);
    }

    /// <summary>need17-01：按指定全局模式导出（Off=回滚开关，还原 need17 前行为）。</summary>
    public string ToChatToolJson(StrictToolsMode mode)
    {
        string json =
            "{\"type\":\"function\",\"function\":"
            + "{\"name\":"
            + JsonQuote(Name)
            + ",\"description\":"
            + JsonQuote(Description)
            + ",\"parameters\":"
            + ParametersJson
            + "}}";
        if (ResolveStrict(mode))
        {
            // 在 function 对象内注入 "strict": true（Chat 协议位置）。
            json = json[..^2] + ",\"strict\":true}}";
        }
        return json;
    }

    /// <summary>
    /// 导出 Responses API 协议工具项：{"type":"function","name":...,"description":...,"parameters":{...}}。
    /// need17-01：Prefer 默认顶层写 "strict": true。
    /// </summary>
    public string ToResponsesToolJson()
    {
        return ToResponsesToolJson(StrictToolsMode.Prefer);
    }

    /// <summary>need17-01：按指定全局模式导出（Off=回滚开关，还原 need17 前行为）。</summary>
    public string ToResponsesToolJson(StrictToolsMode mode)
    {
        string json =
            "{\"type\":\"function\""
            + ",\"name\":"
            + JsonQuote(Name)
            + ",\"description\":"
            + JsonQuote(Description)
            + ",\"parameters\":"
            + ParametersJson
            + "}";
        if (ResolveStrict(mode))
        {
            // 顶层注入 "strict": true（Responses 协议位置）。
            json = json[..^1] + ",\"strict\":true}";
        }
        return json;
    }

    internal static string JsonQuote(string? value)
    {
        if (value == null)
        {
            return "null";
        }

        return "\""
            + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
            + "\"";
    }
}

/// <summary>need17-01：工具曝光度。Hidden=可执行可转调但不声明；Disabled=Dispatch 直接拒（need17 预留）。</summary>
public enum ToolExposure
{
    Direct = 0,
    Hidden = 1,
    Disabled = 2,
}
