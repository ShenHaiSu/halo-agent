using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tests.TestSupport;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage.Config;
using HaloAgent.Engine.Storage.Usage;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Agent;

/// <summary>
/// P24 中止生成：取消权威化 / 收尾 / 契约贯通的红灯与回归测试。
/// T0 要求：先写，必须确认红（红灯原因必须是预期的断言，不是超时/编译错误）。
/// </summary>
public sealed class P24CancelTests
{
    // ---------- P24-R1：取消与 IOException 同现，不得进入重试 ----------

    [Fact]
    public async Task R1_cancel_with_io_exception_does_not_retry()
    {
        using var f = new AgentFixture(retry: 3);

        // 第一次调用：不可取消地睡 200ms，然后无条件抛 IOException（内层 OCE）
        // —— 模拟真实流读取被取消时的异常形态。
        f.Llm.Enqueue(r =>
        {
            r.UncancelableDelay = true;                   // ★ P0-1
            r.Delay = TimeSpan.FromMilliseconds(200);
            r.Throw = new IOException("The read operation failed", new OperationCanceledException());
        });
        // 若错误地进入重试，会消费这一条 → PendingScripts 变 0
        f.Llm.Enqueue(r => { r.Content = "SHOULD-NOT-HAPPEN"; });

        var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);

        await Task.Delay(60);                             // 落在 IO 抛出之前
        f.Session.CancelTurn();                           // ★ 取消与 IOException 同现

        // 修前：IOException 经 catch(Exception)→HandleRetryAsync→最终以 OCE 抛出（D2 分支）
        //       或直接以 IOException 抛出 → 两者都应被这里接住
        await Assert.ThrowsAnyAsync<Exception>(() => task);

        // ★ 核心断言 1：只发生 1 次 LLM 调用（没有重试）
        Assert.Single(f.Llm.Calls);
        // ★ 核心断言 2：第二条脚本没被消费
        Assert.Equal(1, f.Llm.PendingScripts);
        // ★ 核心断言 3：绝不出现「重试中」帧（这是 00 §七 的验收判据）
        Assert.DoesNotContain(EventKinds.LlmRetrying, f.EventTrace());
    }

    [Fact]
    public async Task R1b_cancel_with_io_exception_does_not_retry_responses()
    {
        using var f = new AgentFixture(retry: 3);
        f.Session.ReconfigureActive(new ResolvedActiveModel { Protocol = "responses", Retry = 3 });

        f.Llm.Enqueue(r =>
        {
            r.UncancelableDelay = true;
            r.Delay = TimeSpan.FromMilliseconds(200);
            r.Throw = new IOException("The read operation failed", new OperationCanceledException());
        });
        f.Llm.Enqueue(r => { r.Content = "SHOULD-NOT-HAPPEN"; });

        var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);

        await Task.Delay(60);
        f.Session.CancelTurn();

        await Assert.ThrowsAnyAsync<Exception>(() => task);

        Assert.Single(f.Llm.Calls);
        Assert.Equal(1, f.Llm.PendingScripts);
        Assert.DoesNotContain(EventKinds.LlmRetrying, f.EventTrace());
    }

    [Fact]
    public async Task E1c_empty_reply_retry_respects_cancel()
    {
        using var f = new AgentFixture(retry: 3);
        // 空回复（无 content 无 tool）→ 进入 IsEmptyReply 分支 → HandleRetryAsync
        f.Llm.Enqueue(r => { r.Content = ""; r.Delay = TimeSpan.FromMilliseconds(150); r.UncancelableDelay = true; });
        f.Llm.Enqueue(r => { r.Content = "SHOULD-NOT-HAPPEN"; });

        var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);
        await Task.Delay(50);
        f.Session.CancelTurn();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(f.Llm.Calls);                       // 空回复后没有再发
        Assert.Equal(1, f.Llm.PendingScripts);
        Assert.DoesNotContain(EventKinds.LlmRetrying, f.EventTrace());
    }

    /// <summary>
    /// P24-R1c：取消与 IO 异常同现且重试已耗尽时，必须归一化为 OCE（而不是把 IOException 抛给上层）。
    /// 这是 D1 改动 A 的直接判据：上层 RunTurnAsync:541 的 catch(OCE) 必须能稳定命中。
    /// </summary>
    [Fact]
    public async Task R1c_cancel_with_io_exception_normalizes_to_oce()
    {
        using var f = new AgentFixture(retry: 0);
        f.Llm.Enqueue(r =>
        {
            r.UncancelableDelay = true;
            r.Delay = TimeSpan.FromMilliseconds(200);
            r.Throw = new IOException("The read operation failed", new OperationCanceledException());
        });

        var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);
        await Task.Delay(60);
        f.Session.CancelTurn();

        // 修前：catch(Exception) 直接 throw; → IOException 透出（落进通用异常分支，误报「请求失败」）
        // 修后：归一化为 OperationCanceledException
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.IsType<OperationCanceledException>(ex);
    }

    // ---------- P24-R2：流式中取消必须发 turn.finished ----------

    [Fact]
    public async Task R2_cancel_mid_stream_emits_turn_finished()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => { r.Content = "long"; r.Delay = TimeSpan.FromSeconds(5); });

        using var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);
        await Task.Delay(120);
        f.Session.CancelTurn();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var fin = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin);                                     // ★ 修前为 null → 失败
        var p = AgentFixture.PayloadOf<TurnFinishedPayload>(fin!);
        Assert.True(p.Truncated);
        Assert.True(p.ByUser);                                   // ★ D4：用户中断
        Assert.Contains("中断", p.Text);
        Assert.True(p.FinalUsed >= 1);                           // 前端要求
    }

    [Fact]
    public async Task E4_cancel_during_tool_emits_byUser()
    {
        var slow = new SlowTool { Duration = TimeSpan.FromSeconds(30) };
        using var f = new AgentFixture(extraTools: new ITool[] { slow });
        f.Llm.Enqueue(r =>
        {
            r.AddCall("c1", "slow_probe", "{}");
            r.AddCall("c2", "list_directory", "{\"path\":\".\"}");
        });

        using var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);
        await Task.Delay(400);
        f.Session.CancelTurn();                                  // 工具执行中取消

        // 工具边界取消被消化为截断收尾（不抛，与 S07 同语义）
        var result = await task;
        Assert.True(result.Truncated);
        Assert.Contains("中断", result.Text);

        var fin = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin);
        var p = AgentFixture.PayloadOf<TurnFinishedPayload>(fin!);
        Assert.True(p.Truncated);
        Assert.True(p.ByUser);
        Assert.Contains("中断", p.Text);
        Assert.True(slow.WasCanceled);
        // 剩余 tool_calls 有占位响应（与 S07 同断言）
        var toolMsgs = f.MessagesForTest().Where(m => m.Role == Role.Tool).ToList();
        Assert.Equal(2, toolMsgs.Count);
    }

    [Fact]
    public async Task E5_normal_finish_has_byUser_false()
    {
        using var f = new AgentFixture();
        f.Llm.EnqueueContent("done");

        var result = await f.Session.RunTurnAsync("跑", CancellationToken.None);
        Assert.False(result.Truncated);

        var fin = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin);
        var p = AgentFixture.PayloadOf<TurnFinishedPayload>(fin!);
        Assert.False(p.Truncated);
        Assert.False(p.ByUser);
    }

    [Fact]
    public async Task E5b_length_truncation_has_byUser_false()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => { r.Content = "x"; r.TruncatedByLength = true; });

        // 注意：无工具轮的 TurnResult.Truncated 恒为 false（既有语义，不在本阶段改）；
        // E5b 只断言事件载荷的截断语义。
        _ = await f.Session.RunTurnAsync("跑", CancellationToken.None);

        var fin = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin);
        var p = AgentFixture.PayloadOf<TurnFinishedPayload>(fin!);
        Assert.True(p.Truncated);
        Assert.False(p.ByUser);                                  // 超长截断不是用户中断
    }

    [Fact]
    public async Task E5c_error_path_has_byUser_false()
    {
        using var f = new AgentFixture(retry: 0);
        f.Llm.EnqueueThrow(new InvalidOperationException("boom"));

        await Assert.ThrowsAnyAsync<Exception>(() => f.Session.RunTurnAsync("跑", CancellationToken.None));

        var fin = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin);
        var p = AgentFixture.PayloadOf<TurnFinishedPayload>(fin!);
        Assert.True(p.Truncated);
        Assert.False(p.ByUser);                                  // 异常不是用户中断
        Assert.Contains("boom", p.Reason ?? "");
    }

    [Fact]
    public async Task E9_turn_llm_calls_do_not_grow_after_cancel()
    {
        // 第一轮成功（计数=1，含工具调用以进入第二轮），第二轮流式中取消。
        // 若取消后又发起请求，会消费第三条脚本 → PendingScripts 变 0 且计数增长。
        using var f = new AgentFixture(retry: 3);
        f.Llm.Enqueue(r => { r.AddCall("c1", "list_directory", "{\"path\":\".\"}"); });
        f.Llm.Enqueue(r => { r.Content = "long"; r.Delay = TimeSpan.FromSeconds(5); });
        f.Llm.Enqueue(r => { r.Content = "SHOULD-NOT-HAPPEN"; });

        using var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);
        await Task.Delay(400);
        f.Session.CancelTurn();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await f.WaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);

        Assert.Equal(2, f.Llm.Calls.Count);                     // 两轮各 1 次，无第三次
        Assert.Equal(1, f.Llm.PendingScripts);                  // 第三条脚本没被消费
        Assert.Equal(1, f.Session.TurnLlmCallsForTest);         // 只有第一轮成功计次
        Assert.DoesNotContain(EventKinds.LlmRetrying, f.EventTrace());
    }

    private sealed class SlowTool : ITool
    {
        public string Name => "slow_probe";
        public string Description => "测试用慢工具";
        public JsonElement ParameterSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
        public PermissionChecker.Operation Operation => PermissionChecker.Operation.ReadFile;
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(30);
        public bool WasCanceled { get; private set; }

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Duration, ct).ConfigureAwait(false);
                return ToolResult.Success("slow-ok");
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
        }
    }

    /// <summary>
    /// P24-E2b：取消收尾只记一行 usage（OCE 分支已 RecordTurnUsage，FinishAsync 内必须幂等 no-op）。
    /// 第一轮工具调用带用量（把 _turnHasUsage 置位），第二轮流式中取消。
    /// </summary>
    [Fact]
    public async Task E2b_cancel_records_usage_exactly_once()
    {
        using var f = new AgentFixture();
        f.Llm.Enqueue(r => { r.AddCall("c1", "list_directory", "{\"path\":\".\"}"); r.Usage = new Usage(10, 5, 1); });
        f.Llm.Enqueue(r => { r.Content = "long"; r.Delay = TimeSpan.FromSeconds(5); });

        using var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);
        await Task.Delay(400);
        f.Session.CancelTurn();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await f.WaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);

        var (items, total) = f.Usage.Query(new UsageQuery { SessionId = f.Store.SessionId, PageSize = 10 });
        Assert.Equal(1, total);                       // 只落一行，不双记
        Assert.Equal(1, items[0].Truncated);          // 取消按截断落库
    }

    [Fact]
    public async Task E3_cancel_during_backoff_throws_immediately()
    {
        using var f = new AgentFixture();
        f.Llm.EnqueueThrow(new EndpointStatusException(429, "rate limited"));   // 触发重试 → 退避 ≥800ms
        f.Llm.EnqueueContent("never");

        using var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("跑", cts.Token);

        // 等到进入退避（llm.retrying 已发）
        await f.WaitForKindAsync(EventKinds.LlmRetrying, timeoutMs: 2000);

        var sw = Stopwatch.StartNew();
        f.Session.CancelTurn();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 300, $"退避中取消耗时 {sw.ElapsedMilliseconds}ms，应 <300ms");
        Assert.Single(f.Llm.Calls);     // 没有第二次请求
    }

    /// <summary>
    /// P24-E6：取消落在权限等待中 → 询问立即返回拒绝，不 hang。
    /// 钉死 .NET CancellationToken.Register 在已取消 token 上同步回调的语义
    ///（CancelTurn 在 slot 建立后到达，走 _pendingAsk?.Cancel()；建立前到达则靠 Register 兜住）。
    /// </summary>
    [Fact]
    public async Task E6_cancel_before_ask_slot_denies_permission()
    {
        using var f = new AgentFixture(mode: PermissionMode.Ask);
        f.Llm.Enqueue(r => { r.AddCall("c1", "write_file", "{\"path\":\"a.txt\",\"content\":\"hi\"}"); });

        using var cts = new CancellationTokenSource();
        var task = f.Session.RunTurnAsync("写", cts.Token);

        var ask = await f.TryWaitForKindAsync(EventKinds.AskPermission, timeoutMs: 2000);
        Assert.NotNull(ask);
        Assert.NotNull(f.Session.PendingAsk);                    // 槽已建立
        f.Session.CancelTurn();                                  // 权限询问挂起中取消

        // 询问被拒绝后，拒绝分支的 EmitToolStartedAsync 用已取消的 ct 会抛 OCE，
        // 最终走 RunTurnAsync 的 OCE 收尾（带 turn.finished{byUser}），而不是静默返回。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var fin = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin);
        var p = AgentFixture.PayloadOf<TurnFinishedPayload>(fin!);
        Assert.True(p.Truncated);
        Assert.True(p.ByUser);
        Assert.Contains("中断", p.Text);
    }

    /// <summary>
    /// P24-E10：取消后 _pendingAsk 不残留；下一回合的新询问能正常回答（不 stale）。
    /// 修前 CancelTurn 只 _pendingAsk?.Cancel() 不清槽：若 waiter 因 PublishStateAsync 在 try 外
    /// 抛出而跳过 finally，槽残留到下一 turn（确认卡点了没反应 / answer 误报 stale）。
    /// </summary>
    [Fact]
    public async Task E10_cancel_then_new_ask_not_stale()
    {
        using var f = new AgentFixture(mode: PermissionMode.Ask);
        f.Llm.Enqueue(r => { r.AddCall("c1", "write_file", "{\"path\":\"a.txt\",\"content\":\"hi\"}"); });

        using var cts1 = new CancellationTokenSource();
        var t1 = f.Session.RunTurnAsync("写", cts1.Token);
        var ask1 = await f.TryWaitForKindAsync(EventKinds.AskPermission, timeoutMs: 2000);
        Assert.NotNull(ask1);
        Assert.NotNull(f.Session.PendingAsk);

        f.Session.CancelTurn();
        // ★ D8-改动2：CancelTurn 必须立即清槽，不能依赖 waiter 的 finally。
        //   修前此处仍非 null（waiter 的 catch/finally 还没跑到）→ 红。
        Assert.Null(f.Session.PendingAsk);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        var fin1 = await f.TryWaitForKindAsync(EventKinds.TurnFinished, timeoutMs: 2000);
        Assert.NotNull(fin1);
        Assert.True(AgentFixture.PayloadOf<TurnFinishedPayload>(fin1!).ByUser);

        // Turn 2：新回合触发询问，能正常回答（不 stale），文件真实落盘
        f.Llm.Enqueue(r => { r.AddCall("c2", "write_file", "{\"path\":\"b.txt\",\"content\":\"hello\"}"); });
        f.Llm.Enqueue(r => { r.Content = "写好了"; });
        var t2 = f.Session.RunTurnAsync("再写", CancellationToken.None);
        var ask2 = await WaitForNthKindAsync(f, EventKinds.AskPermission, 2, timeoutMs: 3000);
        Assert.NotNull(ask2);

        var askId2 = f.Session.PendingAsk?.AskId;
        Assert.NotNull(askId2);
        var (accepted, reason) = f.Session.AnswerAsk(askId2!, allow: true, always: false);
        Assert.True(accepted);                                   // ★ 残留槽会导致 stale → 红
        Assert.Null(reason);

        var result2 = await t2;
        Assert.False(result2.Truncated);
        Assert.True(File.Exists(Path.Combine(f.Cwd, "b.txt")));
    }

    private static async Task<RecordingEventSink.RecordedEvent?> WaitForNthKindAsync(
        AgentFixture f, string kind, int n, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hits = f.Sink.Snapshot().Where(e => e.Kind == kind).ToList();
            if (hits.Count >= n) return hits[^1];
            await Task.Delay(20).ConfigureAwait(false);
        }
        return null;
    }
}
