using HaloAgent.Engine.Storage.Usage;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests;

/// <summary>P16 UsageStore：建库/写入/分页/双键聚合/ux重放幂等/clear/stats。</summary>
public sealed class UsageStoreTests
{
    private static UsageStore NewStore(out TempDataDir tmp)
    {
        tmp = new TempDataDir();
        var s = new UsageStore(Path.Combine(tmp.Dir, "usage.db"));
        s.EnsureCreated();
        return s;
    }

    private static UsageEvent Ev(string sid, string tid, string pid, string mid, long p, long c, long o, long ts)
    {
        return new UsageEvent
        {
            TsMs = ts, SessionId = sid, TurnId = tid,
            ProviderId = pid, ProviderDisplay = pid, ModelId = mid, ModelDisplay = mid,
            Protocol = "chat", PromptTokens = p, CachedTokens = c, CompletionTokens = o,
            TotalTokens = p + o, Truncated = 0, RecordedMs = ts,
        };
    }

    [Fact]
    public void Record_and_query_pagination()
    {
        var store = NewStore(out var tmp);
        using (tmp)
        {
            store.Record(Ev("s1", "t1", "openai", "gpt-4o-mini", 100, 10, 20, 1000));
            store.Record(Ev("s1", "t2", "openai", "gpt-4o-mini", 200, 20, 30, 2000));
            var (items, total) = store.Query(new UsageQuery { Page = 1, PageSize = 50 });
            Assert.Equal(2, total);
            Assert.Equal(2, items.Count);
            // desc：t2 在前
            Assert.Equal("t2", items[0].TurnId);
            var (p2, t2) = store.Query(new UsageQuery { Page = 2, PageSize = 1 });
            Assert.Equal(2, t2);
            Assert.Single(p2);
        }
    }

    [Fact]
    public void Replay_same_turn_is_idempotent()
    {
        var store = NewStore(out var tmp);
        using (tmp)
        {
            var e = Ev("s1", "t1", "openai", "m", 100, 10, 20, 1000);
            store.Record(e);
            store.Record(e);
            var (_, total) = store.Query(new UsageQuery());
            Assert.Equal(1, total);
        }
    }

    [Fact]
    public void Summary_groups_by_dual_key_not_model_only()
    {
        var store = NewStore(out var tmp);
        using (tmp)
        {
            store.Record(Ev("s1", "t1", "openai", "same-model", 100, 10, 10, 1000));
            store.Record(Ev("s1", "t2", "other", "same-model", 200, 40, 20, 2000));
            var s = store.Summary(new UsageQuery());
            Assert.Equal(300, s.Input);
            Assert.Equal(50, s.Cached);
            Assert.Equal(2, s.Requests);
            Assert.Equal(2, s.ByModel.Count);
            Assert.Contains(s.ByModel, m => m.ProviderId == "openai" && m.Requests == 1);
            Assert.Contains(s.ByModel, m => m.ProviderId == "other" && m.Requests == 1);
        }
    }

    [Fact]
    public void Clear_before_removes_old()
    {
        var store = NewStore(out var tmp);
        using (tmp)
        {
            store.Record(Ev("s1", "t1", "p", "m", 10, 0, 5, 1000));
            store.Record(Ev("s1", "t2", "p", "m", 10, 0, 5, 9000));
            var removed = store.ClearBefore(5000);
            Assert.Equal(1, removed);
            var (_, total) = store.Query(new UsageQuery());
            Assert.Equal(1, total);
        }
    }

    [Fact]
    public void Stats_returns_size_and_rows()
    {
        var store = NewStore(out var tmp);
        using (tmp)
        {
            store.Record(Ev("s1", "t1", "p", "m", 10, 0, 5, 1000));
            var (size, rows) = store.Stats();
            Assert.Equal(1, rows);
            Assert.True(size > 0);
        }
    }
}
