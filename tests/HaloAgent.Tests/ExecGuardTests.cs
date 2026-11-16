#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Host.Desktop.Tools;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Safety;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>exec 探针层：引号内搜索模式放行、区外路径拦截（mock 工作区，只断言探针层）。</summary>
public sealed class ExecGuardTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _projectDir;

    public ExecGuardTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloExecGuard_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_tempRoot, "ws");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private ToolContext Ctx(List<string>? allowedOverride = null) =>
        new()
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = "ws",
            SessionId = "ss",
            DataRoot = _tempRoot,
            AllowedCommandsOverride = allowedOverride,
        };

    private ToolResult? Probe(string command, List<string>? allowedOverride = null) =>
        ExecProcessHelper.CheckCmdPolicy(
            command,
            _projectDir,
            _projectDir,
            Ctx(allowedOverride),
            Stopwatch.StartNew()
        );

    [Fact(DisplayName = "引号内搜索模式放行：单反斜杠片段不触发拦截")]
    public void QuotedSearchPattern_SingleBackslash_Allowed()
    {
        ToolResult? r = Probe("findstr /i /v \"a \\obj\\ b\"");
        Assert.Null(r);
    }

    [Fact(DisplayName = "引号内搜索模式放行：盘符路径作搜索串不触发拦截")]
    public void QuotedSearchPattern_DrivePath_Allowed()
    {
        ToolResult? r = Probe("findstr \"C:\\\\Users\"");
        Assert.Null(r);
    }

    [Fact(DisplayName = "引号内搜索模式放行：覆盖配置下搜索工具同样放行")]
    public void QuotedSearchPattern_OverrideAllowlist_Allowed()
    {
        ToolResult? r = Probe("rg \"C:\\\\Windows\\\\x\"", new List<string> { "rg" });
        Assert.Null(r);
    }

    [Fact(DisplayName = "区外拦截：未加引号盘符路径")]
    public void OutsidePath_BareDrivePath_Blocked()
    {
        ToolResult? r = Probe("type C:\\Windows\\win.ini");
        Assert.NotNull(r);
        Assert.False(r!.Success);
        Assert.Equal(PathSafetyValidator.Codes.OutsideWorkspace, r.ErrorCode);
    }

    [Fact(DisplayName = "区外拦截：完整共享路径")]
    public void OutsidePath_UncShare_Blocked()
    {
        ToolResult? r = Probe("copy \\\\server\\share\\x .");
        Assert.NotNull(r);
        Assert.False(r!.Success);
        Assert.Equal(PathSafetyValidator.Codes.OutsideWorkspace, r.ErrorCode);
    }

    [Fact(DisplayName = "区外拦截：父目录穿越")]
    public void OutsidePath_ParentTraversal_Blocked()
    {
        ToolResult? r = Probe("type ..\\..\\secret.txt");
        Assert.NotNull(r);
        Assert.False(r!.Success);
        Assert.Equal(PathSafetyValidator.Codes.OutsideWorkspace, r.ErrorCode);
    }

    [Fact(DisplayName = "区外拦截：根盘符读出")]
    public void OutsidePath_RootDriveRead_Blocked()
    {
        ToolResult? r = Probe("dir C:\\ > out.txt");
        Assert.NotNull(r);
        Assert.False(r!.Success);
        Assert.Equal(PathSafetyValidator.Codes.OutsideWorkspace, r.ErrorCode);
    }
}
