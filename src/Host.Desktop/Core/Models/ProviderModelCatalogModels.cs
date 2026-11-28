using System.Text.Json;
using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>
/// 供应商模型目录条目状态：发现态 / 已存在 / 已忽视。
/// 仅描述本次拉取快照与本地配置的对照结果，不写入 providers.json。
/// </summary>
[JsonConverter(typeof(CatalogModelStateJsonConverter))]
public enum CatalogModelState
{
    New = 0,
    Existing = 1,
    Ignored = 2
}

/// <summary>
/// 目录状态枚举的宽容 JSON 转换：大小写无关的 new/existing/ignored 字符串与 0/1/2 数值互转，默认输出小写字符串。
/// 上游 owned_by/created 不可信的教训同样适用：未知状态一律回退为 New，由前端再做业务归一。
/// </summary>
public sealed class CatalogModelStateJsonConverter : JsonConverter<CatalogModelState>
{
    public override CatalogModelState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? raw = reader.GetString()?.Trim().ToLowerInvariant();
            return raw switch
            {
                "existing" => CatalogModelState.Existing,
                "ignored" => CatalogModelState.Ignored,
                _ => CatalogModelState.New,
            };
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int num))
        {
            return num switch
            {
                1 => CatalogModelState.Existing,
                2 => CatalogModelState.Ignored,
                _ => CatalogModelState.New,
            };
        }

        return CatalogModelState.New;
    }

    public override void Write(
        Utf8JsonWriter writer,
        CatalogModelState value,
        JsonSerializerOptions options
    )
    {
        string text = value switch
        {
            CatalogModelState.Existing => "existing",
            CatalogModelState.Ignored => "ignored",
            _ => "new",
        };
        writer.WriteStringValue(text);
    }
}

/// <summary>
/// 单条发现模型：唯一可信键为 Id（上游 data[].id 修整后），其余仅透传展示，不参与任何业务判断。
/// </summary>
public sealed class DiscoveredProviderModel
{
    public string Id { get; set; } = string.Empty;

    public string? OwnedBy { get; set; }

    public long? Created { get; set; }

    public CatalogModelState State { get; set; } = CatalogModelState.New;

    public bool ExistsEnabled { get; set; }
}

/// <summary>
/// 单次拉取结果快照：不落盘，仅驻留内存供用户批量决策。
/// </summary>
public sealed class FetchProviderModelsResult
{
    public string ProviderId { get; set; } = string.Empty;

    public string ModelsUrl { get; set; } = string.Empty;

    public string EffectiveProxySummary { get; set; } = string.Empty;

    public long ElapsedMs { get; set; }

    public List<DiscoveredProviderModel> Models { get; set; } = new();
}

/// <summary>
/// 批量引入请求：只带 providerId 与模型 Id 清单，密钥永不经前端。
/// </summary>
public sealed class ImportDiscoveredModelsRequest
{
    public string ProviderId { get; set; } = string.Empty;

    public List<string> ModelIds { get; set; } = new();

    public bool ImportDisabled { get; set; }
}

/// <summary>
/// 供应商模型目录错误码（中英码分离，前端只判码，文案见设计文档错误码表）。
/// </summary>
public static class CatalogErrorCodes
{
    public const string ProviderNotFound = "CATALOG_PROVIDER_NOT_FOUND";
    public const string MissingApiKey = "CATALOG_MISSING_API_KEY";
    public const string UpstreamAuth = "CATALOG_UPSTREAM_401_403";
    public const string UpstreamNotFound = "CATALOG_UPSTREAM_404";
    public const string UpstreamError = "CATALOG_UPSTREAM_5XX";
    public const string ParseError = "CATALOG_PARSE_ERROR";
    public const string NetworkError = "CATALOG_NETWORK_ERROR";
    public const string Timeout = "CATALOG_TIMEOUT";
    public const string RequestInvalid = "CATALOG_REQUEST_INVALID";
}
