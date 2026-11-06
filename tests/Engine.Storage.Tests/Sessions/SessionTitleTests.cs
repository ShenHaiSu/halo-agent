using HaloAgent.Engine.Storage.Sessions;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Sessions;

public sealed class SessionTitleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  \n ")]
    public void Build_NullOrWhitespace_ReturnsEmpty(string? raw)
    {
        Assert.Equal(SessionTitle.EmptyTitle, SessionTitle.Build(raw));
    }

    [Fact]
    public void Build_TrimsAndJoinsNewlines()
    {
        Assert.Equal("hi there", SessionTitle.Build("  hi\nthere "));
        // \r 保持现状（只换 \n），锁定行为防“顺手修”。
        Assert.Equal("a\r b", SessionTitle.Build("a\r\nb"));
    }

    [Fact]
    public void Build_Exactly60_NotTruncated()
    {
        var raw = new string('a', 60);
        Assert.Equal(raw, SessionTitle.Build(raw));
    }

    [Fact]
    public void Build_61_TruncatedWithEllipsis()
    {
        var raw = new string('a', 61);
        var title = SessionTitle.Build(raw);
        Assert.Equal(new string('a', 60) + "…", title);
        Assert.Equal(61, title.Length);
    }

    [Fact]
    public void Build_CjkCountsByUtf16()
    {
        var raw = new string('中', 61);
        var title = SessionTitle.Build(raw);
        Assert.Equal(new string('中', 60) + "…", title);
    }

    [Fact]
    public void ListHistory_TitleUnchangedAfterRefactor()
    {
        using var dir = new TempDataDir();
        var store = new SessionStore();
        store.StartNew();
        var path = store.HistoryPath!;
        var first = "hello title";
        store.Record(StoredMessage.User(first), headless: false, cwd: dir.Dir);
        store.Record(StoredMessage.Assistant("hi"), headless: false, cwd: dir.Dir);
        store.Dispose();

        var entries = SessionStore.ListHistory();
        var hit = Assert.Single(entries, e => e.Path == path);
        Assert.Equal(SessionTitle.Build(first), hit.Title);
        Assert.Equal(1, hit.UserMsgs);
        Assert.Equal(1, hit.AssistantMsgs);
    }

    [Fact]
    public void GetSummary_CurrentSession_ReturnsTitle()
    {
        using var dir = new TempDataDir();
        var store = new SessionStore();
        store.StartNew();
        var first = "hello summary";
        store.Record(StoredMessage.User(first), headless: false, cwd: dir.Dir);
        store.Record(StoredMessage.Assistant("hi"), headless: false, cwd: dir.Dir);

        var summary = store.GetSummary(store.SessionId);
        Assert.NotNull(summary);
        Assert.Equal(SessionTitle.Build(first), summary!.Title);
        Assert.Equal(1, summary.UserMsgs);
        Assert.Equal(1, summary.AssistantMsgs);
        store.Dispose();
    }

    [Fact]
    public void GetSummary_MissingFile_ReturnsNull()
    {
        using var dir = new TempDataDir();
        var store = new SessionStore();
        store.StartNew();
        Assert.Null(store.GetSummary(store.SessionId));
        store.Dispose();
    }

    [Fact]
    public void GetSummary_ForeignId_ReturnsNull()
    {
        using var dir = new TempDataDir();
        var store = new SessionStore();
        store.StartNew();
        store.Record(StoredMessage.User("hi"), headless: false, cwd: dir.Dir);
        Assert.Null(store.GetSummary("s-foreign"));
        store.Dispose();
    }

    [Fact]
    public void GetSummary_EmptyFile_ReturnsEmptyTitle()
    {
        using var dir = new TempDataDir();
        var store = new SessionStore();
        store.StartNew();
        store.Record(StoredMessage.Assistant("hi"), headless: false, cwd: dir.Dir);
        var summary = store.GetSummary(store.SessionId);
        Assert.NotNull(summary);
        Assert.Equal(SessionTitle.EmptyTitle, summary!.Title);
        store.Dispose();
    }
}
