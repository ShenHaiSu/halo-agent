using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need21-B04: edit_file 纯辅助（诊断/提示/哈希/换行探测，原 EditFileTool 私有方法整体搬迁）。</summary>
internal static class EditFileHelpers
{
    internal static string GetBackupDisplay(string dataRoot, string bakAbsPath)
    {
        try
        {
            string root = string.IsNullOrWhiteSpace(dataRoot)
                ? Path.GetTempPath()
                : Path.GetFullPath(dataRoot);
            string rel = Path.GetRelativePath(root, bakAbsPath);
            if (!rel.StartsWith("..", StringComparison.Ordinal))
            {
                return rel;
            }
        }
        catch { }

        return bakAbsPath;
    }

    internal static string NormalizeHash(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("sha256:".Length).Trim();
        }

        return trimmed;
    }

    internal static bool IsBool(JsonElement el) =>
        el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False;

    /// <summary>命中区间内的换行风格（决定 new_string 的落盘换行）；单行命中返回 null 走文件主风格。</summary>
    internal static string? DetectSpanEol(string raw, int start, int end)
    {
        if (start < 0 || end > raw.Length || start >= end)
        {
            return null;
        }

        for (int i = start; i < end; i++)
        {
            if (raw[i] == '\n')
            {
                return i > start && raw[i - 1] == '\r' ? "\r\n" : "\n";
            }
        }

        return null;
    }

    /// <summary>R4：硬性把成功输出压到 maxChars 以内，并保留 truncated 标记。</summary>
    internal static string ClampOutput(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        const string notice =
            "\n... [truncated: true] 输出已截断（上限 2000 字符）；请用 read_file(offset, limit) 查看完整上下文。";
        int keep = maxChars - notice.Length;
        if (keep <= 0)
        {
            return text.Substring(0, maxChars);
        }

        return text.Substring(0, keep) + notice;
    }

    /// <summary>
    /// R3：old_string 未命中时的结构化诊断——Top-3 相似行（行号 + 相似度 + 空白符可视化）+ 一条可执行建议。
    /// </summary>
    internal static string BuildNotFoundDiagnostic(
        string relativePath,
        string raw,
        string oldString,
        FileToolShared.DetectedText detected,
        string matchMode,
        EditMatchEngine.MatchOptions options
    )
    {
        string[] fileLines = EditMatchEngine.SplitLfLines(raw);
        int fileLines2 = fileLines.Length;
        int needleLines = EditMatchEngine
            .SplitLfLines(FileToolShared.NormalizeToLf(oldString))
            .Length;
        List<EditMatchEngine.NearestCandidate> nearest = EditMatchEngine.FindNearest(
            raw,
            oldString,
            options,
            3
        );

        var sb = new StringBuilder();
        sb.Append("{\"ok\":false");
        sb.Append(",\"code\":\"OLD_STRING_NOT_FOUND\"");
        sb.Append(",\"file\":").Append(JsonSerializer.Serialize(relativePath));
        sb.Append(",\"file_eol\":").Append(JsonSerializer.Serialize(detected.NewLineLabel));
        sb.Append(",\"file_lines\":").Append(fileLines2);
        sb.Append(",\"old_string_lines\":").Append(needleLines);
        sb.Append(",\"match_mode\":").Append(JsonSerializer.Serialize(matchMode));
        sb.Append(",\"match_flags\":{\"normalize_eol\":")
            .Append(options.NormalizeEol ? "true" : "false")
            .Append(",\"expand_tabs\":")
            .Append(options.ExpandTabs)
            .Append(",\"trim_trailing_space\":")
            .Append(options.TrimTrailingSpace ? "true" : "false")
            .Append(",\"ignore_leading_whitespace\":")
            .Append(options.IgnoreLeadingWhitespace ? "true" : "false")
            .Append('}');

        sb.Append(",\"nearest\":[");
        for (int i = 0; i < nearest.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"line\":")
                .Append(nearest[i].Line)
                .Append(",\"score\":")
                .Append(
                    Math.Round(nearest[i].Score, 2)
                        .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                )
                .Append(",\"preview\":")
                .Append(JsonSerializer.Serialize(nearest[i].Preview))
                .Append('}');
        }

        sb.Append(']');
        sb.Append(",\"hint\":")
            .Append(
                JsonSerializer.Serialize(
                    BuildHint(relativePath, nearest, detected, needleLines, matchMode)
                )
            );
        sb.Append('}');
        return sb.ToString();
    }

    internal static string BuildHint(
        string relativePath,
        List<EditMatchEngine.NearestCandidate> nearest,
        FileToolShared.DetectedText detected,
        int needleLines,
        string matchMode
    )
    {
        var parts = new List<string>();
        EditMatchEngine.NearestCandidate? best = nearest.Count > 0 ? nearest[0] : null;

        if (best != null && best.LeadingHasTab)
        {
            parts.Add($"疑似 Tab/空格差异：第 {best.Line} 行行首为 Tab（已按 expand_tabs 归一仍未命中）。");
        }

        if (detected.NewLine == "\r\n")
        {
            parts.Add("文件为 CRLF 换行（已按 normalize_eol 归一）。");
        }

        if (matchMode == "exact")
        {
            parts.Add("当前 match_mode=exact 为字节级严格匹配，可改回默认 normalized 重试。");
        }

        if (best != null)
        {
            int limit = Math.Max(1, needleLines);
            parts.Add(
                $"最相近的是第 {best.Line} 行（相似度 {Math.Round(best.Score, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}），"
                    + $"建议改用 edit_lines(file_path=\"{relativePath}\", offset={best.Line}, limit={limit}) 一次成功。"
            );
            if (best.Score >= 0.8 && matchMode != "exact")
            {
                parts.Add("相似度已很高却未命中，通常是行首缩进差异，可加 ignore_leading_whitespace=true 重试。");
            }
        }
        else
        {
            parts.Add("未找到任何相近行，请用 read_file 重新查看目标区域后再改（或直接用 edit_lines 按行号改）。");
        }

        return string.Join(" ", parts);
    }

    internal const int MaxSuccessOutputChars = 2000;

    /// <summary>R5：replace_all 时锚点去空白后的最小长度。</summary>
    internal const int MinAnchorCharsForReplaceAll = 8;

    /// <summary>need15 P1-2：通用短锚拦截阈值（去空白后非空白字符数）。</summary>
    internal const int MinAnchorNonBlankChars = 4;

    /// <summary>need15 P0-4：replace_all 批量警告阈值（可热调）。</summary>
    internal static int MaxBulkWarnThreshold = 20;

    /// <summary>Tab 展开倍数上限（防呆）。</summary>
    internal const int MaxExpandTabs = 16;
}
