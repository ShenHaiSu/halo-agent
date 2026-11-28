using System.Text.Json;
using Host.Desktop.Core.Models;

namespace Host.Desktop.ModelCatalog;

/// <summary>
/// 上游 /models 返回解析器（纯函数）：只信 data[].id，其余字段仅透传展示。
/// 上游 owned_by/created 两家互相打脸（实测证据见设计附录），解析结果不携带任何能力推断。
/// </summary>
public static class ProviderModelsParser
{
    /// <summary>单条 id 长度上限：超长视为脏数据单条丢弃，避免撑爆 providers.json。</summary>
    public const int MaxModelIdLength = 256;

    /// <summary>上游响应体上限：2MB，调用方读取超限即判解析失败。</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<DiscoveredProviderModel> Parse(string? json, out int skipped)
    {
        skipped = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<DiscoveredProviderModel>();
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<DiscoveredProviderModel>();
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            JsonElement data = FindDataArray(root);
            if (data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DiscoveredProviderModel>();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<DiscoveredProviderModel>();
            foreach (JsonElement el in data.EnumerateArray())
            {
                string? id = ExtractId(el);
                id = id?.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    skipped++;
                    continue;
                }

                if (id.Length > MaxModelIdLength)
                {
                    skipped++;
                    continue;
                }

                if (!seen.Add(id))
                {
                    skipped++;
                    continue;
                }

                output.Add(
                    new DiscoveredProviderModel
                    {
                        Id = id,
                        OwnedBy = ExtractOwnedBy(el),
                        Created = ExtractCreated(el),
                        State = CatalogModelState.New,
                        ExistsEnabled = false
                    }
                );
            }

            output.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            return output;
        }
    }

    public static IReadOnlyList<DiscoveredProviderModel> Parse(string? json)
    {
        return Parse(json, out _);
    }

    private static JsonElement FindDataArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array
        )
        {
            return data;
        }

        return default;
    }

    private static string? ExtractId(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }

        if (
            el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("id", out JsonElement idProp)
            && idProp.ValueKind == JsonValueKind.String
        )
        {
            return idProp.GetString();
        }

        return null;
    }

    private static string? ExtractOwnedBy(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (
            el.TryGetProperty("owned_by", out JsonElement owned)
            && owned.ValueKind == JsonValueKind.String
        )
        {
            string? text = owned.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (
            el.TryGetProperty("ownedBy", out JsonElement ownedCamel)
            && ownedCamel.ValueKind == JsonValueKind.String
        )
        {
            string? text = ownedCamel.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static long? ExtractCreated(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (
            el.TryGetProperty("created", out JsonElement created)
            && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out long epoch)
            && epoch > 0
        )
        {
            return epoch;
        }

        return null;
    }
}
