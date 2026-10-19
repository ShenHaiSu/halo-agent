using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need15 P2-2：undo_edit——从备份恢复文件（误删一步恢复）。
/// backup_path 必须位于 &lt;DataRoot&gt;/backups/ 下；落盘同样走 AtomicWriteAllText + 再备当前（撤销的撤销链）。
/// </summary>
public sealed class UndoEditTool : IToolHandler
{
    public string Name => "undo_edit";

    // NEED17 PromptBrief: Restore a file from backup
    public string Description =>
        "Restore a file from a backup created by edit_file/edit_lines/write_file. " +
        "Restores the most recent backup of file_path made by edit_*/write_file. Use after a bad edit instead of hand-reverting. " +
        "backup_path must be under <DataRoot>/backups/. The current file is backed up again before restore. " +
        "Example: {\"backup_path\":\"backups/ws/sess/20260101-120000-000_abcdef12_notes.txt.bak\"}";

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString("backup_path", "Backup file path (under <DataRoot>/backups/).", required: true)
            .AddString("restore_to", "Optional target file path. Defaults to the original src recorded in the .meta.json.")
            .Build();
        return new(Name, Description, schema);
    }

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? backupPathArg = null;
            string? restoreToArg = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("backup_path", out JsonElement bpEl) && bpEl.ValueKind == JsonValueKind.String)
                {
                    backupPathArg = bpEl.GetString();
                }

                if (root.TryGetProperty("restore_to", out JsonElement rtEl) && rtEl.ValueKind == JsonValueKind.String)
                {
                    restoreToArg = rtEl.GetString();
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string dataRoot = string.IsNullOrWhiteSpace(context.DataRoot) ? Path.GetTempPath() : context.DataRoot;
            string backupArgResolved = backupPathArg ?? string.Empty;
            // 允许相对 DataRoot 的备份路径
            if (!string.IsNullOrWhiteSpace(backupPathArg) && !Path.IsPathFullyQualified(backupPathArg))
            {
                backupArgResolved = Path.Combine(dataRoot, backupPathArg);
            }

            var (allowed, error, bakFull) = PathSafetyValidator.ValidateBackupRead(dataRoot, backupArgResolved);
            if (!allowed)
            {
                return Task.FromResult(ToolResult.Fail(error!, durationMs: sw.ElapsedMilliseconds));
            }

            string targetPath;
            if (!string.IsNullOrWhiteSpace(restoreToArg))
            {
                string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                    ? Directory.GetCurrentDirectory()
                    : context.WorkspaceRoot;
                var (ok, err, full, _) = PathSafetyValidator.ValidateWrite(workspaceRoot, restoreToArg);
                if (!ok)
                {
                    return Task.FromResult(ToolResult.Fail(err!, durationMs: sw.ElapsedMilliseconds));
                }

                targetPath = full;
            }
            else
            {
                targetPath = ResolveOriginalFromMeta(bakFull) ?? GuessOriginal(bakFull, context);
                if (targetPath == null)
                {
                    return Task.FromResult(ToolResult.Fail(
                        "无法从备份推断原始路径（缺少 .meta.json 且未传 restore_to）。请显式传 restore_to。",
                        durationMs: sw.ElapsedMilliseconds));
                }

                // 恢复目标仍需过写保护
                string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                    ? Directory.GetCurrentDirectory()
                    : context.WorkspaceRoot;
                var (ok, err, full, _) = PathSafetyValidator.ValidateWrite(workspaceRoot, targetPath);
                if (!ok)
                {
                    return Task.FromResult(ToolResult.Fail(err!, durationMs: sw.ElapsedMilliseconds));
                }

                targetPath = full;
            }

            byte[] bakBytes;
            try
            {
                bakBytes = File.ReadAllBytes(bakFull);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"备份读取失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            // 再备当前（撤销的撤销链）
            try
            {
                if (File.Exists(targetPath))
                {
                    BackupStore.WriteBackup(targetPath, context.DataRoot, context.WorkspaceId, context.SessionId, "undo_edit");
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"备份当前文件失败，恢复已中止：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 备份为原始字节，直接按字节恢复（保留编码/BOM）
                string tmp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tmp, bakBytes);
                    File.Move(tmp, targetPath, overwrite: true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tmp))
                        {
                            File.Delete(tmp);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"恢复写回失败：{ex.Message}。",
                    durationMs: sw.ElapsedMilliseconds));
            }

            string hash = FileToolShared.ComputeHash(bakBytes);
            string display = PathSafetyValidator.GetWorkspaceRelativePath(
                string.IsNullOrWhiteSpace(context.WorkspaceRoot) ? Directory.GetCurrentDirectory() : context.WorkspaceRoot,
                targetPath);
            var sb = new StringBuilder();
            sb.Append("已从 backup 恢复 '").Append(display).Append("'（")
                .Append(bakBytes.Length).Append(" 字节，hash:").Append(hash).Append("）");
            var ok2 = ToolResult.Ok(sb.ToString(), durationMs: sw.ElapsedMilliseconds);
            try
            {
                ok2.FileBytes = new FileInfo(targetPath).Length;
            }
            catch
            {
            }

            return Task.FromResult(ok2);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"undo_edit 执行异常：{ex.Message}。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }
    }

    private static string? ResolveOriginalFromMeta(string bakFull)
    {
        try
        {
            string metaPath = bakFull + ".meta.json";
            if (!File.Exists(metaPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (doc.RootElement.TryGetProperty("Src", out var src) && src.ValueKind == JsonValueKind.String)
            {
                string? s = src.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            if (doc.RootElement.TryGetProperty("src", out var src2) && src2.ValueKind == JsonValueKind.String)
            {
                return src2.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? GuessOriginal(string bakFull, ToolContext ctx)
    {
        // 文件名形如 <stamp>_<guid8>_<basename>.bak，取最后一段为 basename
        try
        {
            string name = Path.GetFileName(bakFull);
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            string[] parts = name.Split('_');
            if (parts.Length >= 3)
            {
                string baseName = string.Join("_", parts.Skip(2));
                string wsRoot = string.IsNullOrWhiteSpace(ctx.WorkspaceRoot) ? Directory.GetCurrentDirectory() : ctx.WorkspaceRoot;
                return Path.Combine(wsRoot, baseName);
            }
        }
        catch
        {
        }

        return null;
    }
}
