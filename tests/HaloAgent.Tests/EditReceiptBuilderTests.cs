#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 回执生成器的纯逻辑守卫：行数口径、行号换算、跨 edit 不串位、删除/插入边界、摘要截断。
/// 全部脱离真实文件系统，直接验证计数与文本一致性。
/// </summary>
public sealed class EditReceiptBuilderTests
{
    private static FileToolShared.DetectedText Lf(bool withBom = false) =>
        new()
        {
            Encoding = new UTF8Encoding(false),
            EncodingLabel = withBom ? "UTF-8-BOM" : "UTF-8",
            NewLine = "\n",
            NewLineLabel = "LF",
            Preamble = Array.Empty<byte>(),
        };

    // ---------- CountTextLines / CountSpanLines ----------

    [Fact]
    public void CountTextLines_Empty_ReturnsZero()
    {
        Assert.Equal(0, EditReceiptBuilder.CountTextLines(string.Empty));
    }

    [Fact]
    public void CountTextLines_TrailingNewline_NotCountedAsNewLine()
    {
        // 与 EditDiffBuilder.SplitLogicalLines 口径一致：末尾换行不额外计行。
        Assert.Equal(2, EditReceiptBuilder.CountTextLines("a\nb\n"));
        Assert.Equal(2, EditReceiptBuilder.CountTextLines("a\nb"));
        Assert.Equal(1, EditReceiptBuilder.CountTextLines("a\n"));
    }

    [Fact]
    public void CountTextLines_CrlfNormalizedToLf()
    {
        Assert.Equal(3, EditReceiptBuilder.CountTextLines("a\r\nb\r\nc"));
    }

    [Fact]
    public void CountSpanLines_UsesOriginalRange()
    {
        const string raw = "L1\nL2\nL3\nL4\n";
        int s = raw.IndexOf("L2", StringComparison.Ordinal);
        int e = raw.IndexOf("L4", StringComparison.Ordinal);
        Assert.Equal(2, EditReceiptBuilder.CountSpanLines(raw, s, e));
    }

    // ---------- LinesOfOffset ----------

    [Fact]
    public void LinesOfOffset_SingleEdit_Precise()
    {
        const string updated = "AAA\nBBBBBB\nCCC\n";
        int start = updated.IndexOf("BBBBBB", StringComparison.Ordinal);
        Assert.Equal(
            new[] { 2, 2 },
            EditReceiptBuilder.LinesOfOffset(updated, start, "BBBBBB".Length)
        );
    }

    [Fact]
    public void LinesOfOffset_MultipleEditsShift_NoCrossTalk()
    {
        // 前置 edit 插入 +5 行，第二个 edit 的落点必须按 updated 文本换算，不按原文。
        var sb = new StringBuilder();
        sb.Append("insert1\ninsert2\ninsert3\ninsert4\ninsert5\n"); // 5 行插入
        sb.Append("origin1\norigin2\norigin3\n"); // 原文前 3 行
        int target = sb.Length;
        sb.Append("TARGET_A\nTARGET_B\nTARGET_C\n"); // 3 行写入
        sb.Append("origin4\n");
        string updated = sb.ToString();

        Assert.Equal(
            new[] { 9, 11 },
            EditReceiptBuilder.LinesOfOffset(
                updated,
                target,
                "TARGET_A\nTARGET_B\nTARGET_C\n".Length
            )
        );
    }

    [Fact]
    public void LinesOfOffset_PureInsert_EmptyRange()
    {
        const string updated = "A\nB\nC\n";
        int at = updated.IndexOf("B", StringComparison.Ordinal);
        Assert.Equal(new[] { 2, 1 }, EditReceiptBuilder.LinesOfOffset(updated, at, 0));
    }

    [Fact]
    public void LinesOfOffset_TextEndsWithNewline_NoExtraLine()
    {
        const string updated = "X\nY\nZ\n";
        int at = updated.IndexOf("Y", StringComparison.Ordinal);
        // 写入 "Y\n" 只占 1 逻辑行，at_lines 跨度必须为 1（不是 [2,3]）。
        int len = updated.IndexOf("Z", StringComparison.Ordinal) - at;
        Assert.Equal(new[] { 2, 2 }, EditReceiptBuilder.LinesOfOffset(updated, at, len));
    }

    // ---------- Build ----------

    [Fact]
    public void Build_ResultingText_EqualsOffsetRange()
    {
        const string updated = "AAA\nBBBBBB\nCCC\n";
        int start = updated.IndexOf("BBBBBB", StringComparison.Ordinal);
        var applied = new List<EditReceiptBuilder.AppliedEdit>
        {
            new(0, 1, start, "BBBBBB".Length, 1, "BBBBBB"),
        };

        var receipt = EditReceiptBuilder.Build(
            "sample.txt",
            "text",
            false,
            updated,
            Lf(),
            "HASH123",
            expectedHashChecked: true,
            backup: null,
            applied
        );

        Assert.True(receipt.Ok);
        Assert.Equal("edit_batch", receipt.Tool);
        Assert.Equal("sample.txt", receipt.FilePath);
        Assert.Equal("text", receipt.Mode);
        Assert.False(receipt.DryRun);
        var edit = Assert.Single(receipt.Edits);
        Assert.Equal(0, edit.Index);
        Assert.Equal(1, edit.AnchorOccurrences);
        Assert.Equal(new[] { 2, 2 }, edit.AtLines);
        Assert.Equal(1, edit.ReplacedLines);
        Assert.Equal(1, edit.WrittenLines);
        Assert.Equal("BBBBBB", edit.ResultingText);
        Assert.Equal(updated.Substring(start, "BBBBBB".Length), edit.ResultingText);
        Assert.True(receipt.Verify.ExpectedHashChecked);
        Assert.Equal("HASH123", receipt.Verify.HashAfter);
        Assert.Equal("UTF-8", receipt.Verify.Encoding);
        Assert.Equal("LF", receipt.Verify.LineEndings);
        Assert.Equal(3, receipt.Verify.TotalLines);
        Assert.True(receipt.Verify.AllEditsMutuallyDisjoint);
    }

    [Fact]
    public void Build_Delete_WrittenLinesZero_EmptyRange()
    {
        // 删除 : 原文 "A\nKEEP\nB\n"，删掉 KEEP 后落盘 "A\nB\n"，删除点在 L2。
        const string updated = "A\nB\n";
        int at = updated.IndexOf("B", StringComparison.Ordinal);
        var applied = new List<EditReceiptBuilder.AppliedEdit>
        {
            new(0, 1, at, 0, 1, string.Empty),
        };

        var receipt = EditReceiptBuilder.Build(
            "sample.txt",
            "text",
            false,
            updated,
            Lf(),
            "H",
            false,
            null,
            applied
        );

        var edit = Assert.Single(receipt.Edits);
        Assert.Equal(0, edit.WrittenLines);
        Assert.Equal(string.Empty, edit.ResultingText);
        Assert.Equal(new[] { 2, 1 }, edit.AtLines);
        Assert.Equal(1, edit.ReplacedLines);
        Assert.False(receipt.Verify.ExpectedHashChecked);
    }

    [Fact]
    public void Build_DisjointFlag_ReflectsDuplicateIndexes()
    {
        var applied = new List<EditReceiptBuilder.AppliedEdit>
        {
            new(0, 1, 0, 1, 1, "a"),
            new(1, 1, 2, 1, 1, "b"),
        };
        var disjoint = EditReceiptBuilder.Build(
            "f",
            "text",
            false,
            "a\nb\n",
            Lf(),
            "h",
            false,
            null,
            applied
        );
        Assert.True(disjoint.Verify.AllEditsMutuallyDisjoint);

        applied[1] = applied[0] with { };
        var overlap = EditReceiptBuilder.Build(
            "f",
            "text",
            false,
            "a\nb\n",
            Lf(),
            "h",
            false,
            null,
            applied
        );
        Assert.False(overlap.Verify.AllEditsMutuallyDisjoint);
    }

    // ---------- JSON 往返 ----------

    [Fact]
    public void ToJson_IsCamelCase_AndRoundTrips()
    {
        const string updated = "A\nB\n";
        var applied = new List<EditReceiptBuilder.AppliedEdit> { new(0, 1, 0, 1, 1, "A") };
        var receipt = EditReceiptBuilder.Build(
            "path/to/f.txt",
            "lines",
            true,
            updated,
            Lf(withBom: true),
            "abc",
            true,
            "backups/f.bak",
            applied
        );

        string json = receipt.ToJson();
        Assert.Contains("\"filePath\"", json);
        Assert.Contains("\"hashAfter\"", json);
        Assert.Contains("\"allEditsMutuallyDisjoint\"", json);
        Assert.DoesNotContain("\"file_path\"", json);

        var back = EditReceipt.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal("path/to/f.txt", back!.FilePath);
        Assert.True(back.DryRun);
        Assert.Equal("UTF-8-BOM", back.Verify.Encoding);
        Assert.Equal("backups/f.bak", back.Backup);
    }

    // ---------- BuildDigest ----------

    [Fact]
    public void BuildDigest_PerEditBlocksAndVerifyLine()
    {
        const string updated = "AAA\nBBBBBB\nCCC\nDD\n";
        var applied = new List<EditReceiptBuilder.AppliedEdit>
        {
            new(0, 1, 4, "BBBBBB".Length, 1, "BBBBBB"),
            new(2, 1, 15, 2, 1, "DD"),
        };
        var receipt = EditReceiptBuilder.Build(
            "s.txt",
            "text",
            false,
            updated,
            Lf(),
            "HASH1234567890",
            true,
            null,
            applied
        );

        string digest = EditReceiptBuilder.BuildDigest(receipt);
        Assert.Contains("edit[0] ", digest);
        Assert.Contains("edit[2] ", digest);
        Assert.Contains("anchor×1", digest);
        Assert.Contains("1 行 → 1 行", digest);
        Assert.Contains("verify:", digest);
        Assert.Contains("hash_after=HASH123456", digest);
        Assert.Contains("written: BBBBBB", digest);
    }

    [Fact]
    public void BuildDigest_Truncation_KeepsTruthInStructuredField()
    {
        // 单个 resulting_text 超过预览预算：摘要必须截断并提示，结构化字段保持完整。
        string longText = string.Join("\n", Enumerable.Range(0, 500).Select(i => "line_" + i));
        var applied = new List<EditReceiptBuilder.AppliedEdit>
        {
            new(0, 1, 0, longText.Length, 500, longText)
        };
        var receipt = EditReceiptBuilder.Build(
            "s.txt",
            "text",
            false,
            longText,
            Lf(),
            "h",
            false,
            null,
            applied
        );

        string digest = EditReceiptBuilder.BuildDigest(receipt);
        Assert.Contains("完整内容见 SuccessPayloadJson.resulting_text", digest);
        Assert.Equal(longText, receipt.Edits[0].ResultingText);
        // 真相层保留完整行数与文本（camelCase JSON），不因摘要截断而丢失。
        Assert.Equal(500, receipt.Edits[0].WrittenLines);
        Assert.Contains("\"writtenLines\":500", receipt.ToJson());
        Assert.Equal(longText, EditReceipt.FromJson(receipt.ToJson())!.Edits[0].ResultingText);
    }
}
