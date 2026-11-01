using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Storage.Sessions;

/// <summary>
/// 存储层上下文压缩标记行 DTO（单向追加 marker:compact）。
/// </summary>
public sealed class StoredCompactMarker
{
    [JsonPropertyName("marker")]
    public string Marker { get; set; } = "compact";

    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("removedTurns")]
    public int RemovedTurns { get; set; }

    [JsonPropertyName("keptTurns")]
    public int KeptTurns { get; set; }

    [JsonPropertyName("tsMs")]
    public long TsMs { get; set; }
}
