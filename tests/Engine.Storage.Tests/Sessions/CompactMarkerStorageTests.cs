using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Sessions;

/// <summary>
/// 上下文压缩标记存储层测试：单向追加落盘、历史回读与结构完整性。
/// </summary>
public sealed class CompactMarkerStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _prevDataDir;

    public CompactMarkerStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "halo-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _prevDataDir = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
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

    [Fact]
    public void SessionStore_AppendOnly_CompactMarker()
    {
        var store = new SessionStore();
        store.StartNew();
        var path = store.HistoryPath!;

        // 模拟前几轮正常消息
        store.Record(StoredMessage.User("提问1"), headless: false, cwd: _dir);
        store.Record(StoredMessage.Assistant("回答1"), headless: false, cwd: _dir);
        store.Record(StoredMessage.User("提问2"), headless: false, cwd: _dir);
        store.Record(StoredMessage.Assistant("回答2"), headless: false, cwd: _dir);

        var linesBefore = ReadLinesShared(path).Length;
        // meta(1) + 4 msgs = 5
        Assert.Equal(5, linesBefore);

        // 追加 CompactMarker
        var marker = new StoredCompactMarker
        {
            Marker = "compact",
            Seq = 1,
            Summary = "前2轮的要点归纳",
            RemovedTurns = 1,
            KeptTurns = 1,
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        store.RecordCompactMarker(marker, headless: false, cwd: _dir);

        var linesAfterMarker = ReadLinesShared(path).Length;
        // 单向追加，行数增加 1，绝不覆写变少
        Assert.Equal(linesBefore + 1, linesAfterMarker);

        // 继续进行后续对话追加
        store.Record(StoredMessage.User("提问3"), headless: false, cwd: _dir);
        store.Record(StoredMessage.Assistant("回答3"), headless: false, cwd: _dir);

        var linesFinal = ReadLinesShared(path).Length;
        Assert.Equal(linesAfterMarker + 2, linesFinal);

        // 通过 ReadHistory 重新读取
        var (meta, messages, repaired) = SessionStore.ReadHistory(path);

        Assert.NotNull(meta);
        Assert.Equal(0, repaired);
        // 总消息条数：4条原始消息 + 1条marker + 2条后续消息 = 7条
        Assert.Equal(7, messages.Count);

        // 验证每条消息的内容与顺序
        Assert.Equal("提问1", messages[0].Content);
        Assert.Equal("回答1", messages[1].Content);
        Assert.Equal("提问2", messages[2].Content);
        Assert.Equal("回答2", messages[3].Content);

        // 验证 Marker 行
        Assert.True(messages[4].IsCompactMarker);
        Assert.Equal("compact", messages[4].Marker);
        Assert.Equal(1, messages[4].Seq);
        Assert.Equal("前2轮的要点归纳", messages[4].Summary);
        Assert.Equal(1, messages[4].RemovedTurns);
        Assert.Equal(1, messages[4].KeptTurns);

        // 验证 Marker 之后的后续消息
        Assert.Equal("提问3", messages[5].Content);
        Assert.Equal("回答3", messages[6].Content);
    }
}
