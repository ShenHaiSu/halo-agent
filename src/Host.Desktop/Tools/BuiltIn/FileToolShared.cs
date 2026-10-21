using System.Text;
using Host.Desktop.Tools.Process;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// 文件工具共享：BOM/UTF-8/GBK 三级编码探测、换行符识别、原子写回。
/// Win7 老项目大量 GBK 源码，此处默认兼容。
/// </summary>
internal static class FileToolShared
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal sealed class DetectedText
    {
        public string Content { get; init; } = string.Empty;
        public Encoding Encoding { get; init; } = new UTF8Encoding(false);
        public string EncodingLabel { get; init; } = "UTF-8";
        public string NewLine { get; init; } = "\n";
        public string NewLineLabel { get; init; } = "LF";
        public byte[] Preamble { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// 读取全文并探测编码与换行符。调用方需先做二进制检测与存在性校验。
    /// </summary>
    internal static DetectedText ReadAllTextWithDetection(string fullPath)
    {
        byte[] raw = File.ReadAllBytes(fullPath);

        Encoding encoding = Utf8NoBom;
        string label = "UTF-8";
        byte[] preamble = Array.Empty<byte>();
        int contentOffset = 0;

        // 1. BOM 头检测
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            label = "UTF-8-BOM";
            preamble = new byte[] { 0xEF, 0xBB, 0xBF };
            contentOffset = 3;
        }
        else if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            label = "UTF-16LE";
            preamble = new byte[] { 0xFF, 0xFE };
            contentOffset = 2;
        }
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            label = "UTF-16BE";
            preamble = new byte[] { 0xFE, 0xFF };
            contentOffset = 2;
        }
        else
        {
            // 2. 无 BOM：严格 UTF-8 尝试
            if (SmartConsoleDecoder.TryDecodeStrictUtf8(raw, out _))
            {
                encoding = Utf8NoBom;
                label = "UTF-8";
            }
            else
            {
                // 3. 回退 CP936 (GBK/GB18030 兼容)
                encoding = SmartConsoleDecoder.GbkEncoding;
                label = "GBK";
            }
        }

        string content =
            contentOffset > 0
                ? encoding.GetString(raw, contentOffset, raw.Length - contentOffset)
                : encoding.GetString(raw);

        string newLine = content.Contains("\r\n") ? "\r\n" : "\n";
        return new DetectedText
        {
            Content = content,
            Encoding = encoding,
            EncodingLabel = label,
            NewLine = newLine,
            NewLineLabel = newLine == "\r\n" ? "CRLF" : "LF",
            Preamble = preamble,
        };
    }

    /// <summary>
    /// 二进制文件检测：前 4096 字节是否包含 \0。
    /// </summary>
    internal static bool LooksBinary(string fullPath)
    {
        try
        {
            using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            byte[] buffer = new byte[4096];
            int read = fs.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 按原编码与原换行符风格原子写回（临时文件 + Move 覆盖）。
    /// </summary>
    internal static void AtomicWriteAllText(
        string fullPath,
        string content,
        Encoding encoding,
        byte[]? preamble = null
    )
    {
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] body = encoding.GetBytes(content);
        byte[] payload;
        if (preamble != null && preamble.Length > 0)
        {
            payload = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, payload, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, payload, preamble.Length, body.Length);
        }
        else
        {
            payload = body;
        }

        string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (
                var fs = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough
                )
            )
            {
                fs.Write(payload, 0, payload.Length);
                fs.Flush(flushToDisk: true);
            }
            // need22-P0 S4：WriteThrough + fsync（参考 AtomicFileWriter），失败不留半文件。
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 忽略残留临时文件清理异常
            }
        }
    }

    /// <summary>
    /// 将文本换行统一转换为目标换行符。
    /// </summary>
    internal static string ConvertNewLines(string text, string targetNewLine)
    {
        string normalized = text.Replace("\r\n", "\n");
        return targetNewLine == "\r\n" ? normalized.Replace("\n", "\r\n") : normalized;
    }

    internal static string NormalizeToLf(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n");

    /// <summary>
    /// 统计归一化文本中 needle 的非重叠出现次数。
    /// </summary>
    internal static int CountOccurrences(string haystackLf, string needleLf)
    {
        if (string.IsNullOrEmpty(needleLf))
        {
            return 0;
        }

        int count = 0;
        int index = 0;
        while (true)
        {
            index = haystackLf.IndexOf(needleLf, index, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            index += needleLf.Length;
        }

        return count;
    }

    /// <summary>need15 P1-3：字节 sha256（64 hex 小写），供 read/write/edit 统一 hash 口径。</summary>
    internal static string ComputeHash(byte[] payload)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(payload);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>need15 P1-3：按落盘字节口径计算文本 hash（含 preamble），与文件字节 sha256 一致。</summary>
    internal static string ComputeTextHash(string text, DetectedText detected)
    {
        byte[] body = detected.Encoding.GetBytes(text);
        if (detected.Preamble.Length == 0)
        {
            return ComputeHash(body);
        }

        byte[] payload = new byte[detected.Preamble.Length + body.Length];
        Buffer.BlockCopy(detected.Preamble, 0, payload, 0, detected.Preamble.Length);
        Buffer.BlockCopy(body, 0, payload, detected.Preamble.Length, body.Length);
        return ComputeHash(payload);
    }

    /// <summary>need22-P0 S5：hash 截断 12 位（错误回显/成功回执统一口径）。</summary>
    internal static string TruncateHash(string? hash, int keep = 12)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "unknown";
        }

        string t = hash.Trim();
        return t.Length > keep ? t.Substring(0, keep) : t;
    }

    /// <summary>
    /// 生成单处替换的 Unified Diff（上下文 ±3 行）。
    /// </summary>
    internal static string BuildUnifiedDiff(
        string relativePath,
        string[] oldLinesLf,
        string[] newLinesLf,
        int oldStartLine /* 1-based */
        ,
        int oldLineCount,
        int newStartLine /* 1-based */
        ,
        int newLineCount
    )
    {
        int oldCtxStart = Math.Max(1, oldStartLine - 3);
        int oldCtxEnd = Math.Min(oldLinesLf.Length, oldStartLine + oldLineCount - 1 + 3);
        int newCtxEnd = Math.Min(newLinesLf.Length, newStartLine + newLineCount - 1 + 3);

        var sb = new StringBuilder();
        sb.Append("--- a/").AppendLine(relativePath);
        sb.Append("+++ b/").AppendLine(relativePath);
        sb.Append("@@ -")
            .Append(oldCtxStart)
            .Append(',')
            .Append(Math.Max(0, oldCtxEnd - oldCtxStart + 1));
        sb.Append(" +")
            .Append(Math.Max(1, newStartLine - 3))
            .Append(',')
            .Append(Math.Max(0, newCtxEnd - Math.Max(1, newStartLine - 3) + 1));
        sb.AppendLine(" @@");

        // 上下文 + 变更行：用前后缀公共行数推导变更区
        int prefix = 0;
        while (
            oldCtxStart - 1 + prefix < oldStartLine - 1
            && prefix < oldLinesLf.Length
            && prefix < newLinesLf.Length
            && oldLinesLf[oldCtxStart - 1 + prefix]
                == newLinesLf[Math.Max(1, newStartLine - 3) - 1 + prefix]
        )
        {
            prefix++;
        }

        int suffix = 0;
        while (
            suffix < (oldCtxEnd - (oldStartLine + oldLineCount - 1))
            && suffix < (newCtxEnd - (newStartLine + newLineCount - 1))
        )
        {
            int oldIdx = oldCtxEnd - 1 - suffix;
            int newIdx = newCtxEnd - 1 - suffix;
            if (oldIdx < 0 || newIdx < 0 || oldLinesLf[oldIdx] != newLinesLf[newIdx])
            {
                break;
            }

            suffix++;
        }

        for (int i = oldCtxStart; i < oldStartLine; i++)
        {
            sb.Append(' ').AppendLine(oldLinesLf[i - 1]);
        }

        for (int i = 0; i < oldLineCount; i++)
        {
            sb.Append('-').AppendLine(oldLinesLf[oldStartLine - 1 + i]);
        }

        for (int i = 0; i < newLineCount; i++)
        {
            sb.Append('+').AppendLine(newLinesLf[newStartLine - 1 + i]);
        }

        for (int i = oldStartLine + oldLineCount; i <= oldCtxEnd; i++)
        {
            sb.Append(' ').AppendLine(oldLinesLf[i - 1]);
        }

        return sb.ToString();
    }
}
