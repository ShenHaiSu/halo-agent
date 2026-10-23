using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

public sealed partial class EditLinesTool : IToolHandler
{
    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString("file_path", "Target file path.", required: true)
            .AddInteger(
                "offset",
                "1-based first line to replace, identical to read_file.",
                required: true,
                minimum: 1
            )
            .AddInteger(
                "limit",
                "Lines to REPLACE; 0 = insert before offset. Required (missing limit keeps MISSING_LIMIT business message).",
                required: true,
                minimum: 0
            )
            .AddString(
                "new_text",
                "Full replacement text; LF newlines converted to file EOL; empty needs allow_empty:true.",
                required: true
            )
            .AddString(
                "expected_hash",
                "Optional sha256 of the file (or of the covered lines) taken from a previous result; mismatch rejects the edit."
            )
            .AddBoolean(
                "allow_empty",
                "Required true to write an empty new_text (line deletion) and avoid accidental mass deletion."
            )
            .AddBoolean("dry_run", "If true, only compute and preview the diff without writing.")
            .Build();
        return new(Name, Description, schema);
    }

    internal readonly struct RawLine
    {
        public RawLine(int start, int contentLength, int eolLength)
        {
            Start = start;
            ContentLength = contentLength;
            EolLength = eolLength;
        }

        /// <summary>该行内容在原文本中的起始偏移。</summary>
        public int Start { get; }

        /// <summary>行内容长度（不含换行符）。</summary>
        public int ContentLength { get; }

        /// <summary>行尾换行符长度（0 = 文件末行无换行）。</summary>
        public int EolLength { get; }
    }

    /// <summary>
    /// 按物理行切分原文本（与 read_file 的 content.Split('\n') 行数完全一致）：
    /// 支持 CRLF / LF / 混合换行，记录每行内容起点、内容长度与换行符长度。
    /// </summary>
    internal static List<RawLine> SplitRawLines(string raw)
    {
        var list = new List<RawLine>();
        int i = 0;
        int start = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\r' || c == '\n')
            {
                int eolLength = c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n' ? 2 : 1;
                list.Add(new RawLine(start, i - start, eolLength));
                i += eolLength;
                start = i;
                continue;
            }

            i++;
        }

        list.Add(new RawLine(start, raw.Length - start, 0));
        return list;
    }

    private static string LineContent(string raw, RawLine line) =>
        line.ContentLength <= 0 ? string.Empty : raw.Substring(line.Start, line.ContentLength);

    private static bool EndsWithEol(string text) =>
        text.Length > 0 && (text[text.Length - 1] == '\n' || text[text.Length - 1] == '\r');

    internal static string ComputeHash(byte[] payload) => FileToolShared.ComputeHash(payload);

    /// <summary>整文件字节 sha256（expected_hash 的主口径，与磁盘字节完全一致）。</summary>
    private static string HashFileBytes(string fullPath, FileToolShared.DetectedText detected)
    {
        try
        {
            return ComputeHash(File.ReadAllBytes(fullPath));
        }
        catch
        {
            // 极端情况（文件被外部锁）退化为按解码文本计算
            return ComputeHash(Encoding.UTF8.GetBytes(detected.Content));
        }
    }

    /// <summary>按原编码（含 BOM）计算写出后文本的字节 hash，与 HashFileBytes 口径一致。</summary>
    internal static string HashText(string text, FileToolShared.DetectedText detected) =>
        FileToolShared.ComputeTextHash(text, detected);

    private static string GetBackupDisplay(string dataRoot, string bakAbsPath)
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

    private static string NormalizeHash(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("sha256:".Length).Trim();
        }

        return trimmed;
    }
}
