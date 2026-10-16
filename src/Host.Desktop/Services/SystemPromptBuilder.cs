using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Tools.Process;

namespace Host.Desktop.Services;

/// <summary>need06-03：提示词渲染上下文（变量替换输入）。</summary>
public sealed record PromptContext(
    string OsVersion,
    string OsArch,
    string WsName,
    string WsRoot,
    string PythonStatus,
    string Today);

/// <summary>
/// need06-03：动态系统提示词引擎（配置化重构）。
/// 唯一拼装路径 BuildCore：预览与实发共用；老 BuildAsync 保留为兼容壳（默认配置渲染）。
/// </summary>
public sealed class SystemPromptBuilder
{
    private static string? _pyCache;
    private static long _pyCacheTick;
    private static readonly object PyCacheLock = new();
    private static readonly Regex UnknownVarRegex = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    public static string GetPythonStatus()
    {
        lock (PyCacheLock)
        {
            if (_pyCache != null && Environment.TickCount64 - _pyCacheTick < 60_000)
                return _pyCache;
        }
        string display;
        try
        {
            var probe = CommandRunnerProbe.ProbePython();
            if (probe == null || !probe.Found || string.IsNullOrWhiteSpace(probe.ExePath))
            {
                display = "(Python not detected - fall back to cmd.exe)";
            }
            else
            {
                string version = TryGetPythonVersion(probe.ExePath, probe.LauncherArgsPrefix);
                display = string.IsNullOrWhiteSpace(version) ? probe.DisplayName : $"{version} ({probe.ExePath})";
            }
        }
        catch
        {
            display = "(Python not detected - fall back to cmd.exe)";
        }
        lock (PyCacheLock)
        {
            _pyCache = display;
            _pyCacheTick = Environment.TickCount64;
        }
        return display;
    }

    public static void ClearPythonCacheForTests()
    {
        lock (PyCacheLock)
        {
            _pyCache = null;
            _pyCacheTick = 0;
        }
    }

    /// <summary>兼容壳：默认配置渲染，保证老单测与老调用方逐字节一致。</summary>
    public static Task<string> BuildAsync(WorkspaceInfo workspace, string? pythonStatusOverride = null)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        var cfg = PromptDefaults.CreateDefault();
        var ctx = CreateContext(workspace, pythonStatusOverride, dateOverride: null);
        var (assembled, _, _) = BuildCore(cfg, ctx);
        return Task.FromResult(assembled);
    }

    public static PromptPreviewDto BuildPreview(
        PromptConfigDocument cfg,
        WorkspaceInfo ws,
        string? pyOverride = null,
        string? dateOverride = null,
        string? workspaceIdOverride = null)
    {
        cfg ??= PromptDefaults.CreateDefault();
        if (ws == null) throw new ArgumentNullException(nameof(ws));
        var ctx = CreateContext(ws, pyOverride, dateOverride);
        var (assembled, blocks, unknownVars) = BuildCore(cfg, ctx);
        bool isDefault = IsDefaultConfig(cfg);
        int total = TokenBudgetService.EstimateTokens(assembled);
        int stable = blocks.Where(b => b.Enabled && b.IsStable).Sum(b => b.EstimatedTokens);
        string hash = HeaderVariableResolver.ComputeDeterministicHash16(assembled ?? string.Empty);
        return new PromptPreviewDto
        {
            AssembledText = assembled,
            Blocks = blocks,
            TotalEstimatedTokens = total,
            StableEstimatedTokens = stable,
            PrefixHash = hash,
            WorkspaceId = workspaceIdOverride ?? ws.Id ?? string.Empty,
            UnknownVariables = unknownVars,
            IsDefault = isDefault,
            FallbackReason = null,
        };
    }

    internal static (string assembled, List<PromptBlockPreview> blocks, List<string> unknownVars) BuildCore(
        PromptConfigDocument cfg, PromptContext ctx)
    {
        var blocks = new List<PromptBlockPreview>();
        var unknown = new List<string>();
        if (!cfg.EnableHaloPrefix)
            return (string.Empty, blocks, unknown);

        var ordered = new List<(string id, string text, bool stable, bool enabled)>();
        string customPlacement = string.Equals(cfg.CustomPlacement, "head", StringComparison.OrdinalIgnoreCase) ? "head" : "tail";
        string customRendered = string.Empty;
        bool hasCustom = cfg.EnableCustom && !string.IsNullOrWhiteSpace(cfg.CustomText);
        if (hasCustom)
            customRendered = RenderTemplate(cfg.CustomText, ctx, unknown).TrimEnd();

        if (hasCustom && customPlacement == "head")
            ordered.Add(("custom", customRendered, true, true));
        if (cfg.EnableIdentity && !string.IsNullOrWhiteSpace(cfg.IdentityText))
            ordered.Add(("identity", (cfg.IdentityText ?? string.Empty).TrimEnd(), true, true));
        if (cfg.EnableRuntime)
        {
            string rt = RenderTemplate(cfg.RuntimeTemplate ?? string.Empty, ctx, unknown).TrimEnd();
            if (!string.IsNullOrWhiteSpace(rt)) ordered.Add(("runtime", rt, true, true));
        }
        if (cfg.EnableCmdPython && !string.IsNullOrWhiteSpace(cfg.CmdPythonText))
            ordered.Add(("cmdPython", (cfg.CmdPythonText ?? string.Empty).TrimEnd(), true, true));
        if (cfg.EnableToolGuide)
        {
            string tg = RenderTemplate(cfg.ToolGuideTemplate ?? string.Empty, ctx, unknown).TrimEnd();
            if (!string.IsNullOrWhiteSpace(tg)) ordered.Add(("toolGuide", tg, true, true));
        }
        if (cfg.EnableEphemeral)
        {
            string eph = RenderTemplate(cfg.EphemeralTemplate ?? string.Empty, ctx, unknown).TrimEnd();
            if (!string.IsNullOrWhiteSpace(eph))
            {
                if (!eph.Contains(PrefixCachePredictor.EphemeralMarker, StringComparison.Ordinal))
                    eph = "## 4. Session Ephemeral\n" + eph.TrimStart();
                ordered.Add(("ephemeral", eph, false, true));
            }
        }
        if (hasCustom && customPlacement == "tail")
            ordered.Add(("custom", customRendered, false, true));

        foreach (var (id, text, stable, _) in ordered)
        {
            blocks.Add(new PromptBlockPreview
            {
                BlockId = id,
                Enabled = true,
                RenderedText = text,
                EstimatedTokens = TokenBudgetService.EstimateTokens(text),
                IsStable = stable,
            });
        }

        // 关断块也记录（Enabled=false），满足 T2 断言 Blocks 全 Disabled
        if (blocks.Count == 0 && cfg.EnableHaloPrefix)
        {
            foreach (string id in new[] { "identity", "runtime", "cmdPython", "toolGuide", "ephemeral", "custom" })
                blocks.Add(new PromptBlockPreview { BlockId = id, Enabled = false, RenderedText = string.Empty, EstimatedTokens = 0, IsStable = !string.Equals(id, "ephemeral", StringComparison.Ordinal) });
            return (string.Empty, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());
        }
        if (!cfg.EnableHaloPrefix)
            return (string.Empty, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());

        string assembled = ordered.Count == 0
            ? string.Empty
            : string.Join("\n\n", ordered.Select(p => p.text.TrimEnd().TrimEnd('\r', '\n'))) + "\n";
        return (assembled, blocks, unknown.Distinct(StringComparer.Ordinal).ToList());
    }

    private static PromptContext CreateContext(WorkspaceInfo ws, string? pyOverride, string? dateOverride)
    {
        string osVersion = RuntimeInformation.OSDescription?.Trim() ?? Environment.OSVersion.VersionString;
        string arch = RuntimeInformation.OSArchitecture.ToString();
        string wsName = string.IsNullOrWhiteSpace(ws.Name) ? "workspace" : ws.Name.Trim();
        string wsRoot = string.IsNullOrWhiteSpace(ws.RootPath) ? Environment.CurrentDirectory : ws.RootPath;
        string pythonStatus = string.IsNullOrWhiteSpace(pyOverride) ? GetPythonStatus() : pyOverride.Trim();
        string today = string.IsNullOrWhiteSpace(dateOverride) ? DateTime.Now.ToString("yyyy-MM-dd") : dateOverride.Trim();
        return new PromptContext(osVersion, arch, wsName, wsRoot, pythonStatus, today);
    }

    internal static string RenderTemplate(string template, PromptContext ctx, List<string>? unknownSink = null)
    {
        string s = template ?? string.Empty;
        s = s.Replace("{{OsVersion}}", ctx.OsVersion)
             .Replace("{{OsArch}}", ctx.OsArch)
             .Replace("{{WorkspaceName}}", ctx.WsName)
             .Replace("{{WorkspaceRoot}}", ctx.WsRoot)
             .Replace("{{PythonStatus}}", ctx.PythonStatus)
             .Replace("{{CurrentDate}}", ctx.Today);
        foreach (Match m in UnknownVarRegex.Matches(s))
        {
            string name = m.Groups[1].Value;
            unknownSink?.Add(name);
        }
        return s;
    }

    private static bool IsDefaultConfig(PromptConfigDocument cfg)
    {
        var def = PromptDefaults.CreateDefault();
        return cfg.EnableHaloPrefix == def.EnableHaloPrefix
            && cfg.EnableIdentity && cfg.EnableRuntime && cfg.EnableCmdPython && cfg.EnableToolGuide && cfg.EnableEphemeral
            && !cfg.EnableCustom
            && string.Equals(cfg.CustomPlacement, "tail", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(cfg.CustomText)
            && string.Equals(cfg.IdentityText, PromptDefaults.IdentityText, StringComparison.Ordinal)
            && string.Equals(cfg.RuntimeTemplate, PromptDefaults.RuntimeTemplate, StringComparison.Ordinal)
            && string.Equals(cfg.CmdPythonText, PromptDefaults.CmdPythonText, StringComparison.Ordinal)
            && string.Equals(cfg.ToolGuideTemplate, PromptDefaults.ToolGuideTemplate, StringComparison.Ordinal)
            && string.Equals(cfg.EphemeralTemplate, PromptDefaults.EphemeralTemplate, StringComparison.Ordinal);
    }

    private static string TryGetPythonVersion(string exePath, string launcherPrefix)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = exePath;
            string args = string.IsNullOrWhiteSpace(launcherPrefix)
                ? "--version"
                : launcherPrefix.Trim() + " --version";
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            CommandRunnerProbe.ApplyStandardEnv(process.StartInfo);
            if (!process.Start())
            {
                return string.Empty;
            }

            string stdout = process.StandardOutput.ReadToEnd() ?? string.Empty;
            string stderr = process.StandardError.ReadToEnd() ?? string.Empty;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { /* ignore */ }
                return string.Empty;
            }

            string combined = (stdout + "\n" + stderr).Trim();
            foreach (string rawLine in combined.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("Python", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Length > 64 ? line[..64] : line;
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
