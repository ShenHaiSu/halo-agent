using System.Text;
using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>need17-02：单字段约束声明（注册表驱动）。Type 取 string|integer|boolean|array|object。</summary>
public sealed class ToolParamSpec
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public string[]? EnumValues { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public long? Minimum { get; init; }
    public long? Maximum { get; init; }
}

/// <summary>need17-02：单工具 Schema 声明（required + 字段表；闭合世界）。</summary>
public sealed class ToolSchemaSpec
{
    public string ToolName { get; init; } = string.Empty;
    public string[] Required { get; init; } = Array.Empty<string>();
    public Dictionary<string, ToolParamSpec> Params { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>need17-02：校验结果（纯数据；失败时 Code/Message/PayloadJson 走 FailCode）。</summary>
public sealed class ToolCallValidationResult
{
    public bool Success { get; init; }
    public string NormalizedJson { get; init; } = "{}";
    public List<string> Warnings { get; init; } = new();
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? PayloadJson { get; init; }
}
