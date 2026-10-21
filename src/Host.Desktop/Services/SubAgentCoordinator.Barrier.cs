using System.Collections.Concurrent;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

public sealed partial class SubAgentCoordinator
{
    /// <summary>need22-P1 T4：Barrier 总体超时（默认=子数×单子超时+buffer；超时子截断+timedOut:true，主轮继续；响应 mainCt 取消）。</summary>
    public async Task<List<SubAgentResult>> BarrierAsync(
        string workspaceId,
        string parentSessionId,
        IReadOnlyList<SubHandle> handles,
        IReadOnlyList<(string CallId, string FailText)> rejected,
        CancellationToken mainCt,
        TimeSpan? overallTimeout = null
    )
    {
        var rejectMap = rejected.ToDictionary(
            x => x.CallId,
            x => x.FailText,
            StringComparer.Ordinal
        );
        TimeSpan budget = overallTimeout ?? ResolveBarrierBudget(handles.Count);
        var timedOutHandles = new HashSet<SubHandle>();
        if (handles.Count > 0)
        {
            Publish(
                parentSessionId,
                "SubAgentsBarrier",
                new { total = handles.Count, waiting = true }
            );
            Task allTask = Task.WhenAll(handles.Select(h => h.Task));
            Task first = await Task.WhenAny(allTask, Task.Delay(budget, mainCt))
                .ConfigureAwait(false);
            mainCt.ThrowIfCancellationRequested();
            if (first != allTask)
            {
                // 总体超时：掐断未完成子，标记 timedOut，主轮继续（残留任务由其 CTS 取消后自行收敛，CTS 在方法末尾统一 Dispose）。
                foreach (var h in handles)
                {
                    if (!h.Task.IsCompleted)
                    {
                        timedOutHandles.Add(h);
                        try
                        {
                            h.Cts.Cancel();
                        }
                        catch { }
                        // need23：落拒绝记录（设置中心·子运行监控卡可见）
                        RecordRejection(
                            "SUBAGENT_BARRIER_TIMEOUT",
                            h.CallId,
                            $"[SUBAGENT_BARRIER_TIMEOUT] 子代理等待超时（budget={budget.TotalMilliseconds}ms），已掐断并按截断结果回填。"
                        );
                    }
                }
                Publish(
                    parentSessionId,
                    "SubAgentsBarrierTimeout",
                    new
                    {
                        total = handles.Count,
                        timedOut = timedOutHandles.Count,
                        budgetMs = (long)budget.TotalMilliseconds
                    }
                );
            }
            else
            {
                try
                {
                    await allTask.ConfigureAwait(false);
                }
                catch
                { /* 单 handle 归因在下环处理 */
                }
            }
        }
        var results = new List<SubAgentResult>();
        foreach (var h in handles)
        {
            mainCt.ThrowIfCancellationRequested();
            AgentTurnResult r;
            bool timedOut = timedOutHandles.Contains(h);
            if (timedOut && !h.Task.IsCompleted)
            {
                // 超时残留子不再等待（已发 Cancel）：异常由延续观察，Barrier 直接截断回填，主轮继续。
                _ = h.Task.ContinueWith(
                    t =>
                    {
                        try
                        {
                            var _ = t.Exception;
                        }
                        catch { }
                    },
                    TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously
                );
                var (pin, pout, pcached) = await ReadTotalUsageAsync(workspaceId, h.SubSessionId)
                    .ConfigureAwait(false);
                string psummary = await ReadLastAssistantTextAsync(workspaceId, h.SubSessionId)
                    .ConfigureAwait(false);
                results.Add(
                    new SubAgentResult
                    {
                        CallId = h.CallId,
                        SubSessionId = h.SubSessionId,
                        Label = h.Label,
                        Success = false,
                        Summary =
                            TruncateSummary(psummary, 2000)
                            + "\n[子超时截断：Barrier 总体超时，主轮已继续，残缺结果仅供参考。]",
                        ToolExecutions = 0,
                        Iterations = 0,
                        InputTokens = pin,
                        OutputTokens = pout,
                        CachedTokens = pcached,
                        ErrorCode = "SUB_TIMEOUT",
                        ErrorMessage = $"[SUB_TIMEOUT] 子 {h.Label} 超出 Barrier 总体超时，已截断，主轮继续。",
                        TimedOut = true,
                    }
                );
                continue;
            }
            try
            {
                r = await h.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (mainCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                r = AgentTurnResult.Cancelled(0, 0);
            }
            catch (Exception ex)
            {
                r = AgentTurnResult.Failed("SUB_EXEC_ERROR", ex.Message);
            }
            string summary = await ReadLastAssistantTextAsync(workspaceId, h.SubSessionId)
                .ConfigureAwait(false);
            // need22-P1 T7：累加子会话全部消息用量（多轮子耗全量，非末条）。
            var (inp, outp, cached) = await ReadTotalUsageAsync(workspaceId, h.SubSessionId)
                .ConfigureAwait(false);
            if (timedOut)
            {
                // need22-P1 T4：超时子按 summary 截断回填 + timedOut:true，主轮继续。
                results.Add(
                    new SubAgentResult
                    {
                        CallId = h.CallId,
                        SubSessionId = h.SubSessionId,
                        Label = h.Label,
                        Success = false,
                        Summary =
                            TruncateSummary(summary, 2000)
                            + "\n[子超时截断：Barrier 总体超时，主轮已继续，残缺结果仅供参考。]",
                        ToolExecutions = r.ToolExecutions,
                        Iterations = r.Iterations,
                        InputTokens = inp,
                        OutputTokens = outp,
                        CachedTokens = cached,
                        ErrorCode = "SUB_TIMEOUT",
                        ErrorMessage = $"[SUB_TIMEOUT] 子 {h.Label} 超出 Barrier 总体超时，已截断，主轮继续。",
                        TimedOut = true,
                    }
                );
                continue;
            }
            string? errCode = null,
                errMsg = null;
            if (r.WasCancelled)
            {
                errCode = "SUBAGENT_CANCELLED";
                errMsg = $"[SUBAGENT_CANCELLED] 子 {h.Label} 已随主取消被掐断（用户取消）。残缺结果不回填。";
            }
            else if (r.WasTruncated)
            {
                errCode = "TURN_TRUNCATED";
                errMsg = "子轮次耗尽被截断。";
            }
            else if (!r.Success)
            {
                errCode = r.ErrorCode ?? "STREAM_ERROR";
                errMsg = r.ErrorMessage;
            }
            results.Add(
                new SubAgentResult
                {
                    CallId = h.CallId,
                    SubSessionId = h.SubSessionId,
                    Label = h.Label,
                    Success = r.Success && !r.WasTruncated,
                    Summary = summary,
                    ToolExecutions = r.ToolExecutions,
                    Iterations = r.Iterations,
                    InputTokens = inp,
                    OutputTokens = outp,
                    CachedTokens = cached,
                    ErrorCode = r.Success && !r.WasTruncated ? null : errCode,
                    ErrorMessage = r.Success && !r.WasTruncated ? null : errMsg,
                }
            );
        }
        Publish(
            parentSessionId,
            "SubAgentsBarrierReleased",
            new
            {
                subs = results
                    .Select(x => new
                    {
                        callId = x.CallId,
                        subId = x.SubSessionId,
                        success = x.Success,
                        timedOut = x.TimedOut
                    })
                    .ToArray(),
                billedPrompt = results.Sum(x => x.InputTokens),
                billedCompletion = results.Sum(x => x.OutputTokens),
                timedOut = results.Count(x => x.TimedOut),
            }
        );
        var list = _trees.GetOrAdd(parentSessionId, _ => new List<SubHandle>());
        lock (list)
        {
            foreach (var h in handles)
                list.Remove(h);
        }
        // need22-P1 T2：Barrier 结束统一释放子 CTS（已从树摘除；运行中残留已先 Cancel，Dispose 不影响其 Token 查询）。
        foreach (var h in handles)
        {
            try
            {
                h.Cts.Dispose();
            }
            catch { }
        }
        return results;
    }
}
