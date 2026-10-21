using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

public sealed partial class SessionRepository : ISessionRepository
{
    /// <summary>
    /// need05-03：原子重写 messages.jsonl（tmp→move，避免半截文件；任一时刻可被 GetMessagesAsync 完整解析）。
    /// </summary>
    public async Task RewriteMessagesJsonlAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> messages,
        CancellationToken ct = default
    )
    {
        string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(jsonlPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string tmpPath = jsonlPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (
                    var fs = new FileStream(
                        tmpPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        useAsync: true
                    )
                )
                {
                    foreach (var m in messages)
                    {
                        ct.ThrowIfCancellationRequested();
                        string line =
                            JsonSerializer.Serialize(m, HaloJsonOptions.CompactCamelCase) + "\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(line);
                        await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                    }
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }
                File.Move(tmpPath, jsonlPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    { /* ignore */
                    }
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        var meta = await LoadSessionMetaAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        var messages = await GetMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"# {meta?.Title ?? "会话记录"}");
        sb.AppendLine();
        sb.AppendLine($"- **Session ID**: `{sessionId}`");
        if (!string.IsNullOrEmpty(meta?.SessionHash))
        {
            sb.AppendLine($"- **Session Hash**: `{meta.SessionHash}`");
        }
        if (!string.IsNullOrEmpty(meta?.ProviderId) || !string.IsNullOrEmpty(meta?.ModelId))
        {
            sb.AppendLine($"- **Model**: `{meta?.ProviderId} / {meta?.ModelId}`");
        }
        sb.AppendLine($"- **Created At**: `{meta?.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC`");
        sb.AppendLine($"- **Message Count**: {messages.Count}");
        // need05-01：占用/累计分开口径（旧 meta 缺键显示 ?，绝不把未知画成 0）
        string occIn =
            (meta != null && meta.TotalInputTokens > 0) ? meta.TotalInputTokens.ToString() : "?";
        string billP =
            (meta != null && (meta.CumulativePromptTokens > 0 || meta.TotalInputTokens > 0))
                ? meta.CumulativePromptTokens.ToString()
                : "?";
        string billC =
            (meta != null && (meta.CumulativeCompletionTokens > 0 || meta.TotalOutputTokens > 0))
                ? meta.CumulativeCompletionTokens.ToString()
                : "?";
        sb.AppendLine($"- 占用 IN {occIn}（快照=末轮 prompt） / 本轮 OUT 按轮 / 累计 Σ {billP}+{billC}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            sb.AppendLine($"### [{msg.Role.ToUpperInvariant()}] `{msg.CreatedAtUtc:HH:mm:ss}`");
            if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
            {
                sb.AppendLine();
                sb.AppendLine("> **思考过程**:");
                foreach (var line in msg.ReasoningContent.Split('\n'))
                {
                    sb.AppendLine($"> {line.TrimEnd()}");
                }
            }
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                sb.AppendLine();
                sb.AppendLine(msg.Content);
            }
            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**工具调用**:");
                foreach (var tc in msg.ToolCalls)
                {
                    sb.AppendLine($"- `{tc.FunctionName}` (ID: `{tc.CallId}`):");
                    sb.AppendLine("```json");
                    sb.AppendLine(tc.ArgumentsJson);
                    sb.AppendLine("```");
                }
            }
            if (!string.IsNullOrWhiteSpace(msg.ToolCallId))
            {
                sb.AppendLine($"- 对应调用 ID: `{msg.ToolCallId}`");
            }
            if (msg.Images != null && msg.Images.Count > 0)
            {
                foreach (var img in msg.Images)
                {
                    if (img == null)
                        continue;
                    if (img.IsMissing)
                        sb.AppendLine($"[图片已丢失：{img.FileName}]");
                    else
                        sb.AppendLine($"![{img.FileName}]({img.RelPath})");
                }
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
