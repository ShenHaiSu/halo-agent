using System.Text.Json;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>
/// 存储层统一 JSON 配置：snake_case。
/// 与通信用的 camelCase（P1 HaloJson.Options）分开，两套必须区分。
/// </summary>
public static class StorageJson
{
    // net6 没有 JsonNamingPolicy.SnakeCaseLower（.NET 8 才引入），自带一个等价实现。
    private sealed class SnakeLowerPolicy : JsonNamingPolicy
    {
        public static readonly SnakeLowerPolicy Instance = new();

        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>给人看的配置文件用（缩进）。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
#if NET8_0_OR_GREATER
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
#else
        PropertyNamingPolicy = SnakeLowerPolicy.Instance,
#endif
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// JSONL（会话历史 / manifest）用：与 Options 一致但不缩进。
    /// JSONL 必须一行一条，缩进会把一条记录拆成多行导致解析失败。
    /// </summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
