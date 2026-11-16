using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// delete_file：先备份后删除文件（或受控的小目录）。默认只预览，真删必须显式传 dry_run=false。
/// 不支持通配符：受控的单目标语义是本工具与放开 del 的根本区别。
/// </summary>
[HaloAgent.Tools.Tool("delete_file")]
public sealed class DeleteFileTool : IToolHandler
{
    public const int RecursiveMaxFiles = 50;

    public string Name => "delete_file";

    public string Description =>
        "Delete a file in the workspace after writing a backup copy. "
        + "Defaults to dry_run=true (preview only); pass dry_run=false explicitly to actually delete. "
        + "Pass recursive=true to remove a small directory (at most 50 files); each file is backed up before removal. "
        + "Wildcards are not supported. Error codes: IS_A_DIRECTORY|DELETE_TARGET_MISSING|EXPECTED_HASH_MISMATCH|"
        + "PATH_OUTSIDE_WORKSPACE|RECURSIVE_LIMIT_EXCEEDED.";

    public ToolDefinition GetDefinition()
    {
        string schema = new ToolSchemaBuilder()
            .AddString(
                "file_path",
                "Workspace-relative or absolute path of the file (or directory with recursive=true) to delete. Wildcards are not supported.",
                required: true
            )
            .AddString(
                "expected_hash",
                "Optional full-file sha256 shown by read_file. When supplied, a mismatch aborts the delete. Not applicable to recursive deletes."
            )
            .AddBoolean(
                "dry_run",
                "If true (default), only preview what would be deleted; no backup is written and nothing is removed.",
                defaultValue: true
            )
            .AddBoolean(
                "recursive",
                "If true, allow a directory target and delete at most 50 contained files, backing up each one first.",
                defaultValue: false
            )
            .Build();
        return new(Name, Description, schema);
    }

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? filePathArg = null;
            string? expectedHash = null;
            bool dryRun = true;
            bool recursive = false;
            try
            {
                using var args = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = args.Root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    filePathArg = args.GetString("file_path");
                    expectedHash = args.GetString("expected_hash");
                    if (args.TryGetBool("dry_run", out bool parsedDryRun))
                        dryRun = parsedDryRun;
                    if (args.TryGetBool("recursive", out bool parsedRecursive))
                        recursive = parsedRecursive;
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"参数 JSON 解析失败：{ex.Message}。请检查 file_path/dry_run/recursive 字段后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(filePathArg))
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        "缺少必需参数 'file_path'。请先用 list_dir 确认目标路径后重试。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (filePathArg.Contains('*') || filePathArg.Contains('?'))
            {
                return Task.FromResult(
                    ToolResult.FailCode(
                        "WILDCARD_NOT_SUPPORTED",
                        $"WILDCARD_NOT_SUPPORTED：delete_file 不支持通配符（'{filePathArg}'）。请逐个指定要删除的文件。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string workspaceRoot = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Directory.GetCurrentDirectory()
                : context.WorkspaceRoot;
            string dataRoot = string.IsNullOrWhiteSpace(context.DataRoot)
                ? Path.GetTempPath()
                : context.DataRoot;
            var (ok, err, fullPath, _) = PathSafetyValidator.ValidateWrite(
                workspaceRoot,
                filePathArg
            );
            if (!ok)
            {
                return Task.FromResult(ToolResult.Fail(err!, durationMs: sw.ElapsedMilliseconds));
            }

            bool isDirectory = Directory.Exists(fullPath);
            var targets = new List<string>();
            if (isDirectory)
            {
                if (!recursive)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "IS_A_DIRECTORY",
                            $"IS_A_DIRECTORY：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}' 是目录。"
                                + "请用 list_dir 查看内容后逐个删除文件，或显式传 recursive=true 删除整个小目录。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
                if (!string.IsNullOrWhiteSpace(expectedHash))
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "EXPECTED_HASH_NOT_APPLICABLE",
                            "EXPECTED_HASH_NOT_APPLICABLE：目录删除无法用单个 expected_hash 校验，请去掉该参数后重试。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                try
                {
                    targets.AddRange(
                        Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
                    );
                }
                catch (Exception ex)
                {
                    return Task.FromResult(
                        ToolResult.Fail(
                            $"目录枚举失败，删除已中止：{ex.Message}",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }

                if (targets.Count > RecursiveMaxFiles)
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "RECURSIVE_LIMIT_EXCEEDED",
                            $"RECURSIVE_LIMIT_EXCEEDED：目录内共 {targets.Count} 个文件，超过单次上限 {RecursiveMaxFiles}。"
                                + "未删除任何文件，请缩小范围后重试。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }
            else
            {
                if (!File.Exists(fullPath))
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "DELETE_TARGET_MISSING",
                            $"DELETE_TARGET_MISSING：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath)}' 不存在，无可删除目标。"
                                + "请用 list_dir 确认路径后重试。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
                targets.Add(fullPath);
            }

            string display = PathSafetyValidator.ToDisplayPath(workspaceRoot, fullPath);
            long totalBytes = 0;
            try
            {
                foreach (string target in targets)
                    totalBytes += new FileInfo(target).Length;
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"目标状态读取失败，删除已中止：{ex.Message}",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                string currentHash;
                try
                {
                    currentHash = FileToolShared.ComputeHash(File.ReadAllBytes(fullPath));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(
                        ToolResult.Fail(
                            $"目标 hash 计算失败，删除已中止：{ex.Message}",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
                if (
                    !string.Equals(
                        currentHash,
                        expectedHash.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return Task.FromResult(
                        ToolResult.FailCode(
                            "EXPECTED_HASH_MISMATCH",
                            $"EXPECTED_HASH_MISMATCH：目标文件已变化（当前 hash: {FileToolShared.TruncateHash(currentHash)}，"
                                + $"期望: {FileToolShared.TruncateHash(expectedHash)}）。请用 read_file 重新确认后重试。",
                            durationMs: sw.ElapsedMilliseconds
                        )
                    );
                }
            }

            if (dryRun)
            {
                string preview =
                    targets.Count == 1
                        ? $"将删除：{display}（{totalBytes} 字节，dry_run=true，未落盘）"
                        : $"将删除：{display}（{targets.Count} 个文件，共 {totalBytes} 字节，dry_run=true，未落盘）";
                return Task.FromResult(ToolResult.Ok(preview, durationMs: sw.ElapsedMilliseconds));
            }

            // 先备份全部目标，任何一份备份失败都中止删除，绝不出现"无退路"的部分删除。
            var backups = new List<string>();
            try
            {
                foreach (string target in targets)
                {
                    var (bakPath, metaPath, _) = BackupStore.WriteBackup(
                        target,
                        dataRoot,
                        context.WorkspaceId,
                        context.SessionId,
                        "delete_file"
                    );
                    if (string.IsNullOrEmpty(bakPath) || string.IsNullOrEmpty(metaPath))
                        throw new IOException($"未能为 '{target}' 生成备份与元数据");
                    backups.Add(bakPath);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"备份失败，删除已中止（未删除任何文件）：{ex.Message}",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            try
            {
                foreach (string target in targets)
                    File.Delete(target);
                if (isDirectory)
                    Directory.Delete(fullPath, recursive: true);
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    ToolResult.Fail(
                        $"删除中途失败：{ex.Message}；已写备份 {backups.Count} 份：{string.Join(", ", backups.Select(b => DisplayBackup(dataRoot, b)))}。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }

            string backupDir = DisplayBackup(
                dataRoot,
                BackupStore.SessionBackupDir(dataRoot, context.WorkspaceId, context.SessionId)
            );
            string done =
                targets.Count == 1
                    ? $"已删除：{display}（{totalBytes} 字节，已备份 {DisplayBackup(dataRoot, backups[0])}）"
                    : $"已删除：{display}（{targets.Count} 个文件，共 {totalBytes} 字节，已逐文件备份到 {backupDir}）";
            ToolResult result = ToolResult.Ok(done, durationMs: sw.ElapsedMilliseconds);
            result.FileBytes = totalBytes;
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ToolResult.Fail(
                    $"delete_file 执行异常：{ex.Message}。请修正参数后重试。",
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }
        finally
        {
            sw.Stop();
        }
    }

    private static string DisplayBackup(string dataRoot, string path)
    {
        try
        {
            return Path.GetRelativePath(dataRoot, path).Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }
}
