using System.Text.Json;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// File persistence for the session ledger: append plus atomic rewrite.
/// Backup precedes rewrite; rollback restores the backup. Surplus fields are
/// tolerated on load so older readers stay compatible with newer writers.
/// </summary>
public static class JsonlHistoryFile
{
    public static void Save(
        string directory,
        string sessionId,
        string title,
        IReadOnlyList<StoredMessage> messages
    )
    {
        Directory.CreateDirectory(directory);
        string metaPath = Path.Combine(directory, sessionId + ".meta.json");
        string dataPath = Path.Combine(directory, sessionId + ".messages.jsonl");
        string backupPath = dataPath + ".bak";

        if (File.Exists(dataPath))
        {
            File.Copy(dataPath, backupPath, overwrite: true);
        }

        var meta = JsonSerializer.SerializeToElement(
            new
            {
                sessionId,
                title,
                savedAt = DateTimeOffset.UtcNow
            },
            HaloJson.Options
        );
        string tempMeta = metaPath + ".tmp";
        File.WriteAllText(tempMeta, JsonSerializer.Serialize(meta, HaloJson.Options));
        File.Move(tempMeta, metaPath, overwrite: true);

        string tempData = dataPath + ".tmp";
        using (var writer = new StreamWriter(tempData, append: false))
        {
            foreach (var item in messages)
            {
                var row = new
                {
                    index = item.Index,
                    role = item.Role,
                    text = item.Text,
                    turnId = item.TurnId
                };
                writer.WriteLine(JsonSerializer.Serialize(row, HaloJson.Options));
            }
        }
        File.Move(tempData, dataPath, overwrite: true);
    }

    public static bool TryLoad(
        string directory,
        string sessionId,
        out string title,
        out List<StoredMessage> messages
    )
    {
        title = string.Empty;
        messages = new List<StoredMessage>();
        try
        {
            string metaPath = Path.Combine(directory, sessionId + ".meta.json");
            string dataPath = Path.Combine(directory, sessionId + ".messages.jsonl");
            if (!File.Exists(metaPath) || !File.Exists(dataPath))
            {
                return false;
            }
            using (var meta = JsonDocument.Parse(File.ReadAllText(metaPath)))
            {
                if (meta.RootElement.TryGetProperty("title", out var titleNode))
                {
                    title = titleNode.GetString() ?? string.Empty;
                }
            }
            foreach (string line in File.ReadLines(dataPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    using var row = JsonDocument.Parse(line);
                    var root = row.RootElement;
                    long index = root.TryGetProperty("index", out var indexNode)
                        ? indexNode.GetInt64()
                        : messages.Count + 1;
                    string role = root.TryGetProperty("role", out var roleNode)
                        ? roleNode.GetString() ?? "user"
                        : "user";
                    string text = root.TryGetProperty("text", out var textNode)
                        ? textNode.GetString() ?? string.Empty
                        : string.Empty;
                    string turnId = root.TryGetProperty("turnId", out var turnNode)
                        ? turnNode.GetString() ?? string.Empty
                        : string.Empty;
                    messages.Add(
                        new StoredMessage(index, role, text, turnId, DateTimeOffset.UtcNow)
                    );
                }
                catch (JsonException)
                {
                    continue;
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
