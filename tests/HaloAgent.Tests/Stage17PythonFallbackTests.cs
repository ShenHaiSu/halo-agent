using System;
using System.IO;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.Process;
using Xunit;

namespace HaloAgent.Tests;

// need17-07 验收：Python 缺失兜底（文案逐字 + 秒拒 + 注记 + 隔离）。
public sealed class Stage17PythonFallbackTests : IDisposable
{
    public void Dispose() { try { PythonAvailabilitySnapshot.ClearOverrideForTests(); } catch { } }
    private static PythonAvailabilitySnapshot NoPy() => new() { Found = false, ExePath = null, DisplayName = "(not found)", ProbedAtUtc = DateTimeOffset.UtcNow };
    private static PythonAvailabilitySnapshot HasPy() => new() { Found = true, ExePath = "C:\\Python\\python.exe", DisplayName = "Python", ProbedAtUtc = DateTimeOffset.UtcNow };
    private static ToolContext Ctx() => new() { WorkspaceRoot = Path.GetTempPath(), WorkspaceId = "w", SessionId = "s", DataRoot = Path.GetTempPath() };

    [Fact] public void FrozenMessage_Verbatim() { Assert.Contains("当前电脑环境中没有检测到或者没有安装python", PythonAvailabilityGate.FrozenMessage()); }
    [Fact] public void Fail_Code() { var r = PythonAvailabilityGate.Fail(); Assert.Equal("PYTHON_NOT_AVAILABLE", r.ErrorCode); Assert.StartsWith("PYTHON_NOT_AVAILABLE", r.Error); Assert.Equal(-1, r.ExitCode); }
    [Fact] public void Hint_Verbatim() { Assert.Equal(" Hint: 本机无 Python，请用 runner:'cmd' 继续；需要 Python 请按上方指引安装后重启 HaloAgent。", PythonAvailabilityGate.Hint()); }
    [Fact] public void ShouldReject_Table()
    {
        Assert.True(PythonAvailabilityGate.ShouldReject("python", NoPy()));
        Assert.False(PythonAvailabilityGate.ShouldReject("python", HasPy()));
        Assert.False(PythonAvailabilityGate.ShouldReject("cmd", NoPy()));
        Assert.False(PythonAvailabilityGate.ShouldReject("powershell", NoPy()));
    }
    [Fact] public void Refresh_DoesNotThrow() { PythonAvailabilitySnapshot.Refresh(); Assert.NotNull(PythonAvailabilitySnapshot.Current); }
    [Fact] public void Override_Isolated() { PythonAvailabilitySnapshot.OverrideForTests(NoPy()); Assert.False(PythonAvailabilitySnapshot.Current.Found); PythonAvailabilitySnapshot.ClearOverrideForTests(); }
    [Fact] public async Task FastReject_NoProcess()
    {
        var reg = new ToolRegistry();
        var ctx = new ToolContext { WorkspaceRoot = Path.GetTempPath(), WorkspaceId = "w", SessionId = "s", DataRoot = Path.GetTempPath(), PythonAvailabilityOverride = NoPy(), PythonProbeOverride = () => CommandRunnerProbe.ProbeResult.Missing() };
        var r = await reg.DispatchAsync(new ToolCallDto("c1", "exec_command", "{\"command\":\"print(1)\",\"runner\":\"python\"}"), ctx);
        Assert.False(r.Success); Assert.Equal("PYTHON_NOT_AVAILABLE", r.ErrorCode);
    }
    [Fact] public void Schema_Warns_When_NoPython()
    {
        // need17-07 §7.4：快照缺失时 Schema + 描述双注记（07 指定中文）；先断 plumbing（注入可见），再断注记。
        PythonAvailabilitySnapshot.OverrideForTests(NoPy());
        try
        {
            Assert.False(PythonAvailabilitySnapshot.Current.Found);
            var def = new Host.Desktop.Tools.BuiltIn.ExecCommandTool().GetDefinition();
            Assert.Contains("runner:'python' 不可用", def.ParametersJson);
            Assert.Contains("runner:'python' 不可用", def.Description);
        }
        finally { PythonAvailabilitySnapshot.ClearOverrideForTests(); }
    }
    [Fact] public void Prompt_Warns_When_NoPython()
    {
        PythonAvailabilitySnapshot.OverrideForTests(NoPy());
        try
        {
            var cfg = Host.Desktop.Core.Models.PromptDefaults.CreateDefault();
            var ws = new Host.Desktop.Core.Models.WorkspaceInfo { Id = "w", Name = "w", RootPath = Path.GetTempPath() };
            var preview = Host.Desktop.Services.SystemPromptBuilder.BuildPreview(cfg, ws, "py", "2026-01-01");
            Assert.Contains("Python was NOT detected", preview.AssembledText);
        }
        finally { PythonAvailabilitySnapshot.ClearOverrideForTests(); }
    }
}
