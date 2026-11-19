using System.Text.Json;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>单个 edit 落盘后的真相数据：位置、计数与实际写入文本。</summary>
internal sealed record EditReceiptEdit(
    int Index,
    int AnchorOccurrences,
    int[] AtLines,
    int ReplacedLines,
    int WrittenLines,
    string ResultingText
);

/// <summary>整批编辑的校验数据：hash 对账链、编码/EOL、总行数与区间互斥性。</summary>
internal sealed record EditReceiptVerify(
    bool ExpectedHashChecked,
    string HashAfter,
    string Encoding,
    string LineEndings,
    int TotalLines,
    bool AllEditsMutuallyDisjoint
);

/// <summary>
/// 批量编辑成功（含 dry_run 预览）时的结构化回执：判定层 + 真相层。
/// 采用 camelCase 序列化，与 ErrorPayloadJson 内的字段风格一致；老消费方忽略本字段。
/// </summary>
internal sealed record EditReceipt(
    bool Ok,
    string Tool,
    string FilePath,
    string Mode,
    bool DryRun,
    IReadOnlyList<EditReceiptEdit> Edits,
    EditReceiptVerify Verify,
    string? Backup
)
{
    private static readonly JsonSerializerOptions Opts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    /// <summary>供测试与下游按同一口径反序列化回执。</summary>
    public static EditReceipt? FromJson(string json) =>
        JsonSerializer.Deserialize<EditReceipt>(json, Opts);
}
