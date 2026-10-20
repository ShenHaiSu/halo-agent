using System.Diagnostics;

namespace Host.Desktop.Tools.Process;

/// <summary>
/// CMD 与 Python 运行时探测与启动器。
/// cmd 第一优先（跨代一致），python 第二优先（脚本能力互补），规避 PowerShell 版本差异。
/// net6.0-windows 兼容，仅使用托管 API 与 PATH 扫描（Win7 安全）。
/// </summary>
public static class CommandRunnerProbe
{
    private static readonly object CacheLock = new();
    private static ProbeResult? _cachedProbe;

    /// <summary>
    /// Python 探测结果。
    /// </summary>
    public sealed class ProbeResult
    {
        public bool Found { get; init; }
        public string? ExePath { get; init; }
        public string LauncherArgsPrefix { get; init; } = string.Empty;
        public string Kind { get; init; } = "missing";

        public static ProbeResult Missing() => new() { Found = false };

        public string DisplayName =>
            !Found ? "(未检测到 Python)" :
            Kind == "launcher" ? $"py launcher ({ExePath} -3)" :
            ExePath ?? "(未知)";
    }

    /// <summary>
    /// 获取 cmd.exe 可执行文件路径（need22-P0 S2：固定 %SystemRoot%\System32\cmd.exe，不读 ComSpec 环境变量，防劫持）。
    /// </summary>
    public static string GetComSpec()
    {
        try
        {
            string? sysRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(sysRoot))
            {
                string fixed_ = Path.Combine(sysRoot.Trim(), "System32", "cmd.exe");
                if (File.Exists(fixed_))
                {
                    return fixed_;
                }

                return fixed_;
            }
        }
        catch
        {
            // 环境变量读取异常时降级固定路径
        }

        return @"C:\Windows\System32\cmd.exe";
    }

    /// <summary>
    /// 构造 cmd 执行的 ProcessStartInfo：cmd.exe /d /s /c。
    /// /d 禁止 AutoRun 注册表项干扰，/s /c 严格保留引号语义。
    /// </summary>
    public static ProcessStartInfo BuildCmdStartInfo(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetComSpec(),
            Arguments = "/d /s /c \"" + command + "\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        ApplyStandardEnv(psi);
        return psi;
    }

    /// <summary>
    /// 构造 python 执行的 ProcessStartInfo。arguments 由调用方按探测结果组装完整参数。
    /// </summary>
    public static ProcessStartInfo BuildPythonStartInfo(string pythonExe, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        ApplyStandardEnv(psi);
        return psi;
    }

    /// <summary>
    /// 按探测结果组装 python 启动参数（含 py launcher 的 -3 前缀与 -u 无缓冲）。
    /// </summary>
    public static string BuildPythonArguments(ProbeResult probe, string scriptPathOrArgs)
    {
        string prefix = string.IsNullOrWhiteSpace(probe.LauncherArgsPrefix)
            ? string.Empty
            : probe.LauncherArgsPrefix.Trim() + " ";
        return prefix + scriptPathOrArgs;
    }

    /// <summary>
    /// 注入标准环境变量：PYTHONIOENCODING=utf-8、PYTHONUTF8=1、CI=true。
    /// </summary>
    public static void ApplyStandardEnv(ProcessStartInfo psi)
    {
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["CI"] = "true";
    }

    /// <summary>
    /// 按优先级探测 Python：设置路径 &gt; PATH python.exe（排除 WindowsApps 空壳）&gt; py -3。
    /// 结果进程级缓存，测试可通过 InvalidateCache() 刷新。
    /// </summary>
    public static ProbeResult ProbePython(
        string? configuredPath = null,
        string? pathEnvOverride = null,
        bool checkLauncher = true)
    {
        // 显式传入任一覆盖参数时不走缓存（供测试精确控制）。
        bool useCache = configuredPath == null && pathEnvOverride == null && checkLauncher;
        if (useCache)
        {
            lock (CacheLock)
            {
                if (_cachedProbe != null)
                {
                    return _cachedProbe;
                }
            }
        }

        ProbeResult result = ProbePythonNoCache(configuredPath, pathEnvOverride, checkLauncher);

        if (useCache)
        {
            lock (CacheLock)
            {
                _cachedProbe = result;
            }
        }

        return result;
    }

    /// <summary>
    /// 清空进程级探测缓存。
    /// </summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedProbe = null;
        }
    }

    private static ProbeResult ProbePythonNoCache(
        string? configuredPath,
        string? pathEnvOverride,
        bool checkLauncher)
    {
        // 1. 用户在设置中心显式指定的 Python 路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string trimmed = configuredPath.Trim().Trim('"');
            if (File.Exists(trimmed) && VerifyPythonExe(trimmed, string.Empty))
            {
                return new ProbeResult { Found = true, ExePath = trimmed, Kind = "configured" };
            }

            // 配置路径可能是目录（含 python.exe）
            string inDir = Path.Combine(trimmed, "python.exe");
            if (Directory.Exists(trimmed) && File.Exists(inDir) && VerifyPythonExe(inDir, string.Empty))
            {
                return new ProbeResult { Found = true, ExePath = inDir, Kind = "configured" };
            }
        }

        // 2. PATH 中的 python.exe（排除 Windows Store 空壳别名）
        string? pathPython = FindOnPath("python.exe", pathEnvOverride);
        if (!string.IsNullOrWhiteSpace(pathPython) && VerifyPythonExe(pathPython, string.Empty))
        {
            return new ProbeResult { Found = true, ExePath = pathPython, Kind = "path" };
        }

        // 3. Windows Python Launcher: py.exe -3
        if (checkLauncher)
        {
            string? pyLauncher = FindOnPath("py.exe", pathEnvOverride);
            if (!string.IsNullOrWhiteSpace(pyLauncher) && VerifyPythonExe(pyLauncher, "-3"))
            {
                return new ProbeResult { Found = true, ExePath = pyLauncher, LauncherArgsPrefix = "-3", Kind = "launcher" };
            }
        }

        return ProbeResult.Missing();
    }

    /// <summary>
    /// 在 PATH（或覆盖目录列表）中查找可执行文件，跳过 WindowsApps 空壳目录。
    /// </summary>
    public static string? FindOnPath(string fileName, string? pathEnvOverride = null)
    {
        try
        {
            string rawPath = pathEnvOverride ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            char[] separators = new[] { ';' };
            foreach (string dir in rawPath.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmedDir = dir.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(trimmedDir))
                {
                    continue;
                }

                // 排除 Windows Store 应用执行别名目录（python.exe 空壳，启动会弹应用商店）
                if (trimmedDir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.Combine(trimmedDir, fileName);
                }
                catch (Exception)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // PATH 解析异常时返回 null，由上层降级处理
        }

        return null;
    }

    /// <summary>
    /// 执行 `exe [extraArgs] --version` 验证为真实解释器（输出包含 "Python"）。
    /// </summary>
    private static bool VerifyPythonExe(string exePath, string extraArgs)
    {
        try
        {
            // 路径仍指向 WindowsApps 时直接拒绝（双保险）
            if (exePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = exePath;
            process.StartInfo.Arguments = string.IsNullOrWhiteSpace(extraArgs)
                ? "--version"
                : extraArgs.Trim() + " --version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            ApplyStandardEnv(process.StartInfo);

            if (!process.Start())
            {
                return false;
            }

            // 同步读取版本输出（数据量极小，无死锁风险），8 秒超时
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(8000);
            if (!exited)
            {
                try { process.Kill(); } catch { /* 忽略 */ }
                return false;
            }

            string combined = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
            return combined.Contains("Python", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
