using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Sessions;

/// <summary>P18：RewriteAfterCompact 原子重写（meta 保留、system 不落盘、行数正确）。</summary>
public sealed class RewriteAfterCompactTests : IDisposable
{
    private readonly string _dir;
    private readonly string _prevDataDir;

    public RewriteAfterCompactTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "halo-rw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR")!;
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _prevDataDir);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string[] ReadLinesShared(string path)
    {
        var list = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null) list.Add(line);
        return list.ToArray();
    }
    private static string ReadTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
    [Fact]
    public void Rewrite_keeps_meta_and_drops_system()
    {
        var store = new SessionStore();
        store.StartNew();
        var path = store.HistoryPath!;
        store.Record(StoredMessage.User("u1"), headless: false, cwd: _dir);
        store.Record(StoredMessage.Assistant("a1"), headless: false, cwd: _dir);
        store.Record(StoredMessage.User("u2"), headless: false, cwd: _dir);
        int before = ReadLinesShared(path).Length;
        Assert.True(before >= 4); // meta + 3

        store.RewriteAfterCompact(new List<StoredMessage>
        {
            StoredMessage.System("sys-should-drop"),
            StoredMessage.User("【上下文摘要】s"),
            StoredMessage.User("u2"),
        });

        var lines = ReadLinesShared(path);
        Assert.Equal(3, lines.Length); // meta + 2
        var (meta, msgs, _) = SessionStore.ReadHistory(path);
        Assert.NotNull(meta);
        Assert.Equal(_dir, meta!.Meta!.Cwd);
        Assert.Equal(2, msgs.Count);
        Assert.DoesNotContain(msgs, m => m.Role == StoredRole.System);
        Assert.Contains(msgs, m => m.Content != null && m.Content.Contains("上下文摘要"));

        // 后续 Record 仍可追加
        store.Record(StoredMessage.Assistant("a2"), headless: false, cwd: _dir);
        Assert.Equal(4, ReadLinesShared(path).Length);
        store.Dispose();
    }
}
