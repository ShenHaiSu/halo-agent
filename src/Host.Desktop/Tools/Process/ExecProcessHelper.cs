using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.Process;

/// <summary>need21-B04: exec 进程辅助（探针/流/清理，原 ExecCommandTool 私有方法整体搬迁）。</summary>
internal static class ExecProcessHelper
{
    private static readonly Regex AbsPathProbe =
        new(@"(?i)(?<![A-Za-z0-9_])([A-Za-z]:[\\/]|\\\\[A-Za-z0-9])", RegexOptions.Compiled);

    private static readonly Regex RedirectProbe =
        new(@"(^|[^\d\w])\d?\s*>>?\s*(""[^""]+""|\S+)", RegexOptions.Compiled);

    /// <summary>
    /// need22-P0 S2：cmd 策略门（白名单首 token + 绝对路径工作区约束 + 重定向目标约束）。
    /// 通过返回 null；拒绝返回 FailCode 结果。
    /// </summary>
    internal static ToolResult? CheckCmdPolicy(
        string command,
        string workDir,
        string workspaceRoot,
        ToolContext context,
        Stopwatch sw
    )
    {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
        if (context.AllowedCommandsOverride != null && context.AllowedCommandsOverride.Count > 0)
        {
            foreach (string c in context.AllowedCommandsOverride)
            {
                if (!string.IsNullOrWhiteSpace(c))
                {
                    allowed.Add(c.Trim());
                }
            }
        }
        else
        {
            foreach (string c in ToolPolicy.DefaultAllowedCommands)
                allowed.Add(c);
            foreach (string c in ToolPolicy.CmdBuiltinCommands)
                allowed.Add(c);
        }

        // 按 & | ; 切分命令段（双引号内不切），每段首 token 都必须在白名单
        foreach (string segment in SplitSegments(command))
        {
            string first = ExtractFirstToken(segment);
            if (string.IsNullOrWhiteSpace(first))
            {
                continue;
            }

            string name = NormalizeExeName(first);
            if (!allowed.Contains(name))
            {
                return ToolResult.FailCode(
                    "COMMAND_NOT_ALLOWED",
                    $"COMMAND_NOT_ALLOWED：命令首 token '{first}' 不在白名单（允许：{string.Join("/", ToolPolicy.DefaultAllowedCommands)} + 安全 cmd 内建；可经 AllowedCommands 配置增补）。请改用白名单命令后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }
        }

        // 绝对路径参数必须落在 workDir 内（防 type/copy 读取工作区外敏感文件）
        foreach (Match m in AbsPathProbe.Matches(command))
        {
            string candidate = TakePathToken(command, m.Index);
            string abs;
            try
            {
                abs = Path.IsPathFullyQualified(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(Path.Combine(workDir, candidate));
            }
            catch
            {
                continue;
            }

            var (resolved, _) = PathSafetyValidator.NormalizeAndResolve(abs);
            if (PathSafetyValidator.IsOutsideWorkspace(workDir, resolved))
            {
                return ToolResult.FailCode(
                    PathSafetyValidator.Codes.OutsideWorkspace,
                    $"{PathSafetyValidator.Codes.OutsideWorkspace}：命令含工作区外绝对路径：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, resolved)}'。请改用工作区内相对路径后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }
        }

        // 重定向目标必须落在 workDir 内（防 echo ... > C:\... 绕过写保护）
        foreach (Match m in RedirectProbe.Matches(command))
        {
            string target = m.Groups[2].Value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(target) || target.StartsWith("&"))
            {
                continue; // 句柄合并（2>&1）放行
            }

            if (
                string.Equals(target, "nul", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, "con", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            string abs;
            try
            {
                abs = Path.IsPathFullyQualified(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(workDir, target));
            }
            catch
            {
                continue;
            }

            if (PathSafetyValidator.IsOutsideWorkspace(workDir, abs))
            {
                return ToolResult.FailCode(
                    "COMMAND_REDIRECT_OUTSIDE",
                    $"COMMAND_REDIRECT_OUTSIDE：重定向目标在工作区外：'{PathSafetyValidator.ToDisplayPath(workspaceRoot, abs)}'。文件写出请改用 write_file 后重试。",
                    durationMs: sw.ElapsedMilliseconds
                );
            }
        }

        return null;
    }

    internal static IEnumerable<string> SplitSegments(string command)
    {
        var segs = new List<string>();
        bool inQuote = false;
        int start = 0;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && (c == '&' || c == '|' || c == ';'))
            {
                segs.Add(command.Substring(start, i - start));
                // 跳过连续分隔符（&& / ||）
                while (i + 1 < command.Length && command[i + 1] == c)
                {
                    i++;
                }
                start = i + 1;
            }
        }
        segs.Add(command.Substring(start));
        return segs;
    }

    internal static string ExtractFirstToken(string segment)
    {
        string s = (segment ?? string.Empty).TrimStart();
        // 去 cmd 抑制回显前缀 @ 与括号块
        s = s.TrimStart('@', ' ', '\t', '(', ' ');
        if (s.Length == 0)
        {
            return string.Empty;
        }

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : s.Trim('"');
        }

        int i = 0;
        while (
            i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '/' && s[i] != '&' && s[i] != '|'
        )
        {
            // 首 token 允许含 :（盘符）与 \（路径），遇空白/开关分隔即止；/ 仅当非首字符时截断
            if (s[i] == '/' && i == 0)
            {
                break;
            }
            if (
                s[i] == '/'
                && i > 0
                && (
                    s.StartsWith("for", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("if", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                break;
            }
            i++;
        }
        // for /L ... / if /I ...：开关在首 token 后，上面已在 / 处截断
        string token = s.Substring(0, i).Trim();
        // for 的情况首 token 即 for（/L 是参数）
        int sp = token.IndexOfAny(new[] { ' ', '\t' });
        return sp >= 0 ? token.Substring(0, sp) : token;
    }

    internal static string NormalizeExeName(string firstToken)
    {
        string t = (firstToken ?? string.Empty).Trim().Trim('"');
        // 取文件名（去目录），去扩展名
        try
        {
            string file = t.Replace('/', '\\');
            int slash = file.LastIndexOf('\\');
            if (slash >= 0)
            {
                file = file.Substring(slash + 1);
            }
            int dot = file.LastIndexOf('.');
            if (dot > 0)
            {
                string ext = file.Substring(dot).ToLowerInvariant();
                if (ext is ".exe" or ".cmd" or ".bat" or ".com" or ".ps1")
                {
                    file = file.Substring(0, dot);
                }
            }
            return file.ToLowerInvariant();
        }
        catch
        {
            return t.ToLowerInvariant();
        }
    }

    internal static string TakePathToken(string command, int index)
    {
        int start = index;
        int i = index;
        bool quoted = i < command.Length && command[i] == '"';
        if (quoted)
        {
            i++;
            start = i;
            while (i < command.Length && command[i] != '"')
                i++;
            return command.Substring(start, Math.Max(0, i - start));
        }
        while (
            i < command.Length
            && !char.IsWhiteSpace(command[i])
            && command[i] != '"'
            && command[i] != '&'
            && command[i] != '|'
            && command[i] != '<'
            && command[i] != '>'
        )
        {
            i++;
        }
        return command.Substring(start, Math.Max(0, i - start));
    }

    internal static bool LooksLikeScriptFileInvocation(string command, string workDir)
    {
        string trimmed = command.Trim();
        // 单行且引用 .py 文件 → 视为脚本调用；多行一律视为内联代码
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            return false;
        }

        if (!trimmed.Contains(".py", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 首个 token 为已存在的 .py 文件
        string firstToken =
            trimmed
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim('"') ?? string.Empty;
        if (firstToken.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string candidate = Path.IsPathFullyQualified(firstToken)
                    ? firstToken
                    : Path.Combine(workDir, firstToken);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    internal static async Task<byte[]> ReadStreamBytesAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream
                    .ReadAsync(buffer, 0, buffer.Length, ct)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte>();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    internal static async Task<string> CollectPartialAsync(
        Task<byte[]> stdoutTask,
        Task<byte[]> stderrTask
    )
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略部分收集阶段的等待异常
        }

        byte[] outBytes = stdoutTask.IsCompletedSuccessfully
            ? stdoutTask.Result
            : Array.Empty<byte>();
        byte[] errBytes = stderrTask.IsCompletedSuccessfully
            ? stderrTask.Result
            : Array.Empty<byte>();
        string text = SmartConsoleDecoder.DecodeBytes(outBytes);
        string err = SmartConsoleDecoder.DecodeBytes(errBytes);
        if (!string.IsNullOrWhiteSpace(err))
        {
            text += (text.Length > 0 ? "\n" : string.Empty) + "[stderr]\n" + err;
        }

        return text;
    }

    internal static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // 忽略强杀阶段异常（Job 关闭已完成主要回收）
            }
        }
    }

    internal static string SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Guid.NewGuid().ToString("N");
        }

        var sb = new System.Text.StringBuilder(id.Length);
        foreach (char c in id.Trim())
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }

        string result = sb.ToString();
        return result.Length > 32 ? result.Substring(0, 32) : result;
    }
}
