using HaloAgent.Engine.Core.Tests.TestSupport;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>P10-fix3 C/D/E：SetCwd 加固 + LoadHistory 回填 + UpdateCwdAtomic。</summary>
public sealed class P10Fix3SetCwdTests
{
    [Fact]
    public void SetCwd_Normalizes_Relative_And_TildePaths()
    {
        using var fx = new AgentFixture();
        var sub = Path.Combine(fx.Cwd, "a");
        Directory.CreateDirectory(sub);
        // 相对路径按旧 cwd 解析
        fx.Session.SetCwd(Path.Combine("a", "..", "a"));
        Assert.Equal(Path.GetFullPath(sub), fx.Session.Cwd);
    }

    [Fact]
    public void SetCwd_MissingDir_Throws_DirectoryNotFound()
    {
        using var fx = new AgentFixture();
        var before = fx.Session.Cwd;
        Assert.Throws<DirectoryNotFoundException>(() =>
            fx.Session.SetCwd(Path.Combine(fx.DataDir, "no-such-dir-xyz")));
        Assert.Equal(before, fx.Session.Cwd);
    }

    [Fact]
    public void SetCwd_Root_And_SystemDir_Rejected()
    {
        using var fx = new AgentFixture();
        var before = fx.Session.Cwd;
        var root = Path.GetPathRoot(fx.Cwd)!;
        Assert.Throws<ArgumentException>(() => fx.Session.SetCwd(root));
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(sysRoot) && Directory.Exists(sysRoot))
            Assert.Throws<ArgumentException>(() => fx.Session.SetCwd(sysRoot));
        Assert.Equal(before, fx.Session.Cwd);
    }

    [Fact]
    public void SetCwd_NullChar_Rejected()
    {
        using var fx = new AgentFixture();
        Assert.Throws<ArgumentException>(() => fx.Session.SetCwd("a\0b"));
    }

    [Fact]
    public void LoadHistory_Restores_MetaCwd()
    {
        using var fx = new AgentFixture();
        var target = Path.Combine(fx.DataDir, "restored");
        Directory.CreateDirectory(target);
        // 先切目录并落一条消息，确保首行 meta 含新 cwd
        fx.Session.SetCwd(target);
        fx.Store.Record(
            HaloAgent.Engine.Storage.Sessions.StoredMessage.User("hi"),
            headless: false, cwd: fx.Session.Cwd);
        var path = fx.Store.HistoryPath!;
        // 换一个 fixture 读同一文件：应回填 meta.cwd
        using var fx2 = new AgentFixture();
        Assert.NotEqual(target, fx2.Session.Cwd);
        fx2.Session.LoadHistory(path);
        Assert.Equal(Path.GetFullPath(target), fx2.Session.Cwd);
    }

    [Fact]
    public void LoadHistory_MissingDir_Keeps_Current()
    {
        using var fx = new AgentFixture();
        var before = fx.Session.Cwd;
        var ghost = Path.Combine(fx.DataDir, "ghost");
        Directory.CreateDirectory(ghost);
        fx.Session.SetCwd(ghost);
        fx.Store.Record(
            HaloAgent.Engine.Storage.Sessions.StoredMessage.User("hi"),
            headless: false, cwd: fx.Session.Cwd);
        var path = fx.Store.HistoryPath!;
        Directory.Delete(ghost, recursive: true);
        using var fx2 = new AgentFixture();
        var cur2 = fx2.Session.Cwd;
        fx2.Session.LoadHistory(path);
        Assert.Equal(cur2, fx2.Session.Cwd);
    }

    [Fact]
    public void UpdateCwdAtomic_Rewrites_FirstLine_Only()
    {
        using var fx = new AgentFixture();
        var target = Path.Combine(fx.DataDir, "atomic");
        Directory.CreateDirectory(target);
        fx.Session.SetCwd(target);
        fx.Store.Record(
            HaloAgent.Engine.Storage.Sessions.StoredMessage.User("hi"),
            headless: false, cwd: fx.Session.Cwd);
        var path = fx.Store.HistoryPath!;
        var newer = Path.Combine(fx.DataDir, "atomic2");
        Directory.CreateDirectory(newer);
        fx.Store.UpdateCwdAtomic(newer);
        var (meta, messages, _) = HaloAgent.Engine.Storage.Sessions.SessionStore.ReadHistory(path);
        Assert.Equal(Path.GetFullPath(newer), meta?.Meta?.Cwd);
        Assert.Single(messages);
    }
}
