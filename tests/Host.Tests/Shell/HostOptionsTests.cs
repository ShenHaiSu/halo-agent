using HaloAgent.Desktop.Shell;
using Xunit;

namespace HaloAgent.Host.Tests.Shell;

/// <summary>HostOptions 命令行契约（12 参数 + 环境变量 + 非法拒绝）。</summary>
public sealed class HostOptionsTests
{
    [Fact]
    public void Defaults_are_sane()
    {
        var o = HostOptions.Parse(Array.Empty<string>());
        Assert.False(o.IsDev);
        Assert.False(o.Diagnostics);
        Assert.False(o.DisableGpu);
        Assert.False(o.ForceSoftware);
        Assert.False(o.NoEngine);
        Assert.False(o.ResetWindow);
        Assert.Null(o.EngineArgs);
        Assert.Empty(o.OpenPaths);
        Assert.False(string.IsNullOrWhiteSpace(o.DataDir));
        Assert.False(string.IsNullOrWhiteSpace(o.LogDir));
        Assert.False(string.IsNullOrWhiteSpace(o.Cwd));
        Assert.True(Path.IsPathRooted(o.DataDir));
    }

    [Fact]
    public void All_flags_parse()
    {
        var o = HostOptions.Parse(new[]
        {
            "--diagnostics", "--disable-gpu", "--force-software",
            "--no-engine", "--reset-window",
            "--data-dir=C:\\t\\d", "--log-dir=C:\\t\\l", "--cwd=C:\\t",
            "--engine-args=--foo",
        });
        Assert.True(o.Diagnostics);
        Assert.True(o.DisableGpu);
        Assert.True(o.ForceSoftware);
        Assert.True(o.NoEngine);
        Assert.True(o.ResetWindow);
        Assert.Equal("--foo", o.EngineArgs);
        Assert.EndsWith("d", o.DataDir.TrimEnd('\\'));
    }

    [Fact]
    public void Space_separated_value_parse()
    {
        var o = HostOptions.Parse(new[] { "--data-dir", "C:\\t\\d", "--dev-url", "http://localhost:5173" });
        Assert.EndsWith("d", o.DataDir.TrimEnd('\\'));
        Assert.Equal("http://localhost:5173", o.DevUrl);
    }

    [Fact]
    public void Positional_paths_collected()
    {
        var o = HostOptions.Parse(new[] { "a.txt", "b dir" });
        Assert.Equal(2, o.OpenPaths.Count);
    }

    [Fact]
    public void Unknown_flag_throws()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--unknown-flag" }));
    }

    [Fact]
    public void Missing_value_throws()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--dev-url" }));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--data-dir" }));
    }

    [Fact]
    public void Dev_rejected_in_release_without_flag()
    {
        // Release 宿主 + 无 dev.flag 时 --dev 必须被拒；Debug 构建则放行。
        if (HaloAgent.Desktop.BuildInfo.AllowsDevMode)
        {
            var o = HostOptions.Parse(new[] { "--dev" });
            Assert.True(o.IsDev);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--dev" }));
        }
    }

    [Fact]
    public void Bad_dev_url_scheme_throws()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--dev-url=ftp://x" }));
    }

    [Fact]
    public void MergeGpuFlags_or_together()
    {
        var o = HostOptions.Parse(new[] { "--disable-gpu" });
        var (d, f) = HostOptions.MergeGpuFlags(o);
        Assert.True(d);
        Assert.False(f);
    }
}
