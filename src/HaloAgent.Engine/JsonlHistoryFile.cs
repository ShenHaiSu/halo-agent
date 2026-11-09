using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// File persistence for the session ledger: append plus atomic rewrite.
/// Backup precedes rewrite; rollback restores the backup. Surplus fields are
/// tolerated on load so older readers stay compatible with newer writers.
/// Reads use shared file handles so a background flush never blocks paging.
/// </summary>
public static class JsonlHistoryFile
{
    public sealed record LoadReport(
        string SessionId,
        bool Found,
        int Messages,
        int SkippedLines,
        bool HealedDescriptor
    );

    public sealed record ScanReport(int Sessions, int Messages, int SkippedLines, int Aliases);

    public static void Save(
        string directory,
        string sessionId,
        string title,
        IReadOnlyList<StoredMessage> messages
    )
    {
        Save(
            directory,
            new SessionDescriptor(sessionId, title, SessionIdentity.DefaultWorkspace),
            messages
        );
    }

    public static void Save(
        string directory,
        SessionDescriptor descriptor,
        IReadOnlyList<StoredMessage> messages
    )
    {
        Directory.CreateDirectory(directory);
        string metaPath = Path.Combine(directory, descriptor.SessionId + ".meta.json");
        string dataPath = Path.Combine(directory, descriptor.SessionId + ".messages.jsonl");
        string backupPath = dataPath + ".bak";

        if (File.Exists(dataPath))
        {
            File.Copy(dataPath, backupPath, overwrite: true);
        }

        var meta = JsonSerializer.SerializeToElement(
            new
            {
                sessionId = descriptor.SessionId,
                title = descriptor.Title,
                workspaceId = descriptor.WorkspaceId,
                parentSessionId = descriptor.ParentSessionId,
                providerId = descriptor.ProviderId,
                modelId = descriptor.ModelId,
                agentRole = descriptor.AgentRole,
                isPinned = descriptor.IsPinned,
                titleLocked = descriptor.TitleLocked,
                createdAtUtc = descriptor.CreatedAtUtc,
                updatedAtUtc = descriptor.UpdatedAtUtc,
                messageCount = messages.Count,
                savedAt = DateTimeOffset.UtcNow,
            },
            HaloJson.Options
        );
        AtomicWriteText(metaPath, JsonSerializer.Serialize(meta, HaloJson.Options));

        string tempData = dataPath + ".tmp";
        using (var writer = new StreamWriter(tempData, append: false))
        {
            foreach (var item in messages)
            {
                var row = new
                {
                    id = item.Id,
                    index = item.Index,
                    role = item.Role,
                    text = item.Text,
                    turnId = item.TurnId,
                    toolCallId = item.ToolCallId,
                    name = item.Name,
                    toolCalls = item.ToolCalls,
                    images = item.Images,
                    inputTokens = item.InputTokens,
                    outputTokens = item.OutputTokens,
                    cachedTokens = item.CachedTokens,
                    reasoningContent = item.ReasoningContent,
                    validatedWarnings = item.ValidatedWarnings,
                    errorCode = item.ErrorCode,
                    createdAtUtc = item.CreatedAt ?? item.StoredAt,
                };
                writer.WriteLine(JsonSerializer.Serialize(row, HaloJson.Options));
            }
        }
        File.Move(tempData, dataPath, overwrite: true);
    }

    private static void AtomicWriteText(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public static bool TryLoad(
        string directory,
        string sessionId,
        out string title,
        out List<StoredMessage> messages
    )
    {
        var report = TryLoad(directory, sessionId, out title, out messages, out _);
        return report.Found;
    }

    public static LoadReport TryLoad(
        string directory,
        string sessionId,
        out string title,
        out List<StoredMessage> messages,
        out SessionDescriptor? descriptor
    )
    {
        title = string.Empty;
        messages = new List<StoredMessage>();
        descriptor = null;
        int skipped = 0;
        try
        {
            string metaPath = Path.Combine(directory, sessionId + ".meta.json");
            string dataPath = Path.Combine(directory, sessionId + ".messages.jsonl");
            if (!File.Exists(metaPath) || !File.Exists(dataPath))
            {
                return new LoadReport(sessionId, false, 0, 0, false);
            }
            string metaText;
            using (
                var metaStream = new FileStream(
                    metaPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read
                )
            )
            using (var reader = new StreamReader(metaStream))
            {
                metaText = reader.ReadToEnd();
            }
            string workspaceId = SessionIdentity.DefaultWorkspace;
            string? parentSessionId = null;
            string? providerId = null;
            string? modelId = null;
            string agentRole = "main";
            bool isPinned = false;
            bool titleLocked = false;
            DateTimeOffset? createdAtUtc = null;
            DateTimeOffset? updatedAtUtc = null;
            using (var meta = JsonDocument.Parse(metaText))
            {
                var root = meta.RootElement;
                if (root.TryGetProperty("title", out var titleNode))
                {
                    title = titleNode.GetString() ?? string.Empty;
                }
                workspaceId =
                    ReadOptionalString(root, "workspaceId") ?? SessionIdentity.DefaultWorkspace;
                parentSessionId = ReadOptionalString(root, "parentSessionId");
                providerId = ReadOptionalString(root, "providerId");
                modelId = ReadOptionalString(root, "modelId");
                agentRole = ReadOptionalString(root, "agentRole") ?? "main";
                isPinned =
                    root.TryGetProperty("isPinned", out var pinnedNode)
                    && pinnedNode.ValueKind == JsonValueKind.True;
                titleLocked =
                    root.TryGetProperty("titleLocked", out var lockedNode)
                    && lockedNode.ValueKind == JsonValueKind.True;
                createdAtUtc = ReadOptionalDate(root, "createdAtUtc");
                updatedAtUtc = ReadOptionalDate(root, "updatedAtUtc");
            }
            descriptor = new SessionDescriptor(
                sessionId,
                title,
                string.IsNullOrWhiteSpace(workspaceId)
                    ? SessionIdentity.DefaultWorkspace
                    : workspaceId,
                parentSessionId,
                providerId,
                modelId,
                string.IsNullOrWhiteSpace(agentRole) ? "main" : agentRole,
                isPinned,
                titleLocked,
                createdAtUtc,
                updatedAtUtc
            );
            using (
                var dataStream = new FileStream(
                    dataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read
                )
            )
            using (var reader = new StreamReader(dataStream))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    try
                    {
                        messages.Add(ParseRow(line));
                    }
                    catch (JsonException)
                    {
                        skipped++;
                    }
                }
            }
            return new LoadReport(sessionId, true, messages.Count, skipped, false);
        }
        catch (IOException)
        {
            return new LoadReport(sessionId, false, 0, skipped, false);
        }
        catch (UnauthorizedAccessException)
        {
            return new LoadReport(sessionId, false, 0, skipped, false);
        }
    }

    private static StoredMessage ParseRow(string line)
    {
        using var row = JsonDocument.Parse(line);
        var root = row.RootElement;
        long index =
            root.TryGetProperty("index", out var indexNode)
            && indexNode.ValueKind == JsonValueKind.Number
                ? indexNode.GetInt64()
                : 0;
        string role = ReadOptionalString(root, "role") ?? "user";
        string text = ReadOptionalString(root, "text") ?? string.Empty;
        string turnId = ReadOptionalString(root, "turnId") ?? string.Empty;
        string id = ReadOptionalString(root, "id") ?? SessionIdentity.NewMessageId();
        var createdAt =
            ReadOptionalDate(root, "createdAtUtc")
            ?? ReadOptionalDate(root, "storedAt")
            ?? DateTimeOffset.UtcNow;
        return new StoredMessage(
            index,
            role,
            text,
            turnId,
            createdAt,
            id,
            ReadOptionalString(root, "toolCallId"),
            ReadOptionalString(root, "name"),
            ReadToolCalls(root),
            ReadImages(root),
            ReadOptionalLong(root, "inputTokens"),
            ReadOptionalLong(root, "outputTokens"),
            ReadOptionalLong(root, "cachedTokens"),
            ReadOptionalString(root, "reasoningContent"),
            ReadOptionalString(root, "validatedWarnings"),
            ReadOptionalString(root, "errorCode"),
            createdAt
        );
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static long? ReadOptionalLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return null;
        }
        return node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out long value)
            ? value
            : null;
    }

    private static DateTimeOffset? ReadOptionalDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return null;
        }
        if (
            node.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(node.GetString(), out var parsed)
        )
        {
            return parsed;
        }
        return null;
    }

    private static IReadOnlyList<SessionToolCall>? ReadToolCalls(JsonElement root)
    {
        if (
            !root.TryGetProperty("toolCalls", out var node)
            || node.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }
        var calls = new List<SessionToolCall>();
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string id = entry.TryGetProperty("id", out var idNode)
                ? idNode.GetString() ?? string.Empty
                : string.Empty;
            string name = entry.TryGetProperty("name", out var nameNode)
                ? nameNode.GetString() ?? string.Empty
                : string.Empty;
            string args = entry.TryGetProperty("arguments", out var argsNode)
                ? argsNode.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            calls.Add(new SessionToolCall(id, name, args));
        }
        return calls.Count == 0 ? null : calls;
    }

    private static IReadOnlyList<SessionImageReference>? ReadImages(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var images = new List<SessionImageReference>();
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string imageId = entry.TryGetProperty("imageId", out var idNode)
                ? idNode.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(imageId))
            {
                continue;
            }
            string fileName = entry.TryGetProperty("fileName", out var nameNode)
                ? nameNode.GetString() ?? "image.bin"
                : "image.bin";
            string mime = entry.TryGetProperty("mime", out var mimeNode)
                ? mimeNode.GetString() ?? string.Empty
                : string.Empty;
            string relPath = entry.TryGetProperty("relPath", out var pathNode)
                ? pathNode.GetString() ?? string.Empty
                : string.Empty;
            images.Add(new SessionImageReference(imageId, fileName, mime, relPath));
        }
        return images.Count == 0 ? null : images;
    }

    /// <summary>
    /// Startup sweep: restores every persisted session into the ledger,
    /// reading both engine flat files and the desktop workspace tree,
    /// and links previous-generation ids to their directory-shaped twins.
    /// Corrupt lines are skipped and counted, never fatal.
    /// </summary>
    public static ScanReport ScanAndRestore(string directory, SessionLedger ledger)
    {
        int sessions = 0;
        int messages = 0;
        int skipped = 0;
        int aliases = 0;
        if (!Directory.Exists(directory))
        {
            return new ScanReport(0, 0, 0, 0);
        }
        foreach (string metaPath in Directory.EnumerateFiles(directory, "*.meta.json"))
        {
            string file = Path.GetFileNameWithoutExtension(metaPath);
            if (file.EndsWith(".meta", StringComparison.Ordinal))
            {
                file = file.Substring(0, file.Length - ".meta".Length);
            }
            var report = TryLoad(
                directory,
                file,
                out string title,
                out var rows,
                out var descriptor
            );
            if (!report.Found)
            {
                continue;
            }
            // Previous-generation flat files also seed a directory-shaped twin,
            // so new readers converge on one layout without a rewrite migration;
            // the old id stays readable through the alias table.
            string effectiveId = file;
            if (SessionIdentity.IsLegacyEngineShaped(file))
            {
                effectiveId = SessionIdentity.NewSessionId();
                ledger.RegisterAlias(file, effectiveId);
                aliases++;
            }
            var seed =
                descriptor ?? new SessionDescriptor(file, title, SessionIdentity.DefaultWorkspace);
            ledger.Restore(effectiveId, seed with { SessionId = effectiveId }, rows);
            sessions++;
            messages += rows.Count;
            skipped += report.SkippedLines;
        }
        ScanDesktopTree(directory, ledger, ref sessions, ref messages, ref skipped);
        aliases += ledger.HealAliases();
        return new ScanReport(sessions, messages, skipped, aliases);
    }

    private static void ScanDesktopTree(
        string directory,
        SessionLedger ledger,
        ref int sessions,
        ref int messages,
        ref int skipped
    )
    {
        string sessionsRoot = Path.Combine(directory, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return;
        }
        foreach (string workspaceDir in Directory.EnumerateDirectories(sessionsRoot))
        {
            string workspaceId = Path.GetFileName(workspaceDir);
            foreach (string sessionDir in Directory.EnumerateDirectories(workspaceDir))
            {
                string sessionId = Path.GetFileName(sessionDir);
                if (sessionId.Equals("attachments", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string metaPath = Path.Combine(sessionDir, "meta.json");
                string dataPath = Path.Combine(sessionDir, "messages.jsonl");
                if (!File.Exists(dataPath))
                {
                    continue;
                }
                string title = sessionId;
                if (File.Exists(metaPath))
                {
                    try
                    {
                        using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                        if (meta.RootElement.TryGetProperty("title", out var titleNode))
                        {
                            title = titleNode.GetString() ?? sessionId;
                        }
                    }
                    catch (JsonException)
                    {
                        skipped++;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                }
                var rows = new List<StoredMessage>();
                try
                {
                    using var stream = new FileStream(
                        dataPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    );
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }
                        try
                        {
                            rows.Add(ParseDesktopRow(line));
                        }
                        catch (JsonException)
                        {
                            skipped++;
                        }
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                long baseIndex = 0;
                foreach (var row in rows)
                {
                    if (row.Index > baseIndex)
                    {
                        baseIndex = row.Index;
                    }
                }
                if (baseIndex == 0)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        rows[i] = rows[i] with { Index = i + 1 };
                    }
                }
                ledger.Restore(
                    sessionId,
                    new SessionDescriptor(
                        sessionId,
                        title,
                        workspaceId,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        UpdatedAtUtc: DateTimeOffset.UtcNow
                    ),
                    rows
                );
                sessions++;
                messages += rows.Count;
            }
        }
    }

    private static StoredMessage ParseDesktopRow(string line)
    {
        using var row = JsonDocument.Parse(line);
        var root = row.RootElement;
        string id = ReadOptionalString(root, "id") ?? SessionIdentity.NewMessageId();
        string role = ReadOptionalString(root, "role") ?? "user";
        string text =
            ReadOptionalString(root, "content") ?? ReadOptionalString(root, "text") ?? string.Empty;
        string toolCallId = ReadOptionalString(root, "toolCallId") ?? string.Empty;
        string name = ReadOptionalString(root, "name") ?? string.Empty;
        long index = ReadOptionalLong(root, "index") ?? 0;
        var createdAt = ReadOptionalDate(root, "createdAtUtc") ?? DateTimeOffset.UtcNow;
        IReadOnlyList<SessionToolCall>? toolCalls = null;
        if (
            root.TryGetProperty("toolCalls", out var callsNode)
            && callsNode.ValueKind == JsonValueKind.Array
        )
        {
            var calls = new List<SessionToolCall>();
            foreach (var entry in callsNode.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string callId = entry.TryGetProperty("id", out var idNode)
                    ? idNode.GetString() ?? string.Empty
                    : string.Empty;
                string callName = entry.TryGetProperty("name", out var nameNode)
                    ? nameNode.GetString() ?? string.Empty
                    : string.Empty;
                string args = entry.TryGetProperty("arguments", out var argsNode)
                    ? argsNode.ValueKind == JsonValueKind.String
                        ? argsNode.GetString() ?? string.Empty
                        : argsNode.GetRawText()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(callId) && string.IsNullOrWhiteSpace(callName))
                {
                    continue;
                }
                calls.Add(new SessionToolCall(callId, callName, args));
            }
            toolCalls = calls.Count == 0 ? null : calls;
        }
        return new StoredMessage(
            index,
            role,
            text,
            string.Empty,
            createdAt,
            id,
            string.IsNullOrWhiteSpace(toolCallId) ? null : toolCallId,
            string.IsNullOrWhiteSpace(name) ? null : name,
            toolCalls,
            ReadImages(root),
            ReadOptionalLong(root, "inputTokens"),
            ReadOptionalLong(root, "outputTokens"),
            ReadOptionalLong(root, "cachedTokens"),
            ReadOptionalString(root, "reasoningContent"),
            null,
            null,
            createdAt
        );
    }

    public static bool TryRollback(string directory, string sessionId)
    {
        try
        {
            string dataPath = Path.Combine(directory, sessionId + ".messages.jsonl");
            string backupPath = dataPath + ".bak";
            if (!File.Exists(backupPath))
            {
                return false;
            }
            File.Copy(backupPath, dataPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
