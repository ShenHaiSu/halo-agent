using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>顺序执行单次、map 与递归 reduce 摘要请求，并逐请求记录用量。</summary>
internal sealed class CompactionSummaryExecutor
{
    private const int MaxReduceDepth = 2;
    private const int ReduceLabelReserveTokens = 64;
    private readonly ILlmGatewayClient _gateway;
    private readonly ITokenBudgetService _budgetService;
    private readonly UsageMetricsService? _usageMetrics;

    public CompactionSummaryExecutor(
        ILlmGatewayClient gateway,
        ITokenBudgetService budgetService,
        UsageMetricsService? usageMetrics = null
    )
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
        _usageMetrics = usageMetrics;
    }

    public async Task<CompactionSummaryResult> ExecuteAsync(
        CompactionSummaryPlan plan,
        CompactionSummaryExecutionContext context,
        string? previousSummary,
        CancellationToken ct = default
    )
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        long inputTokens = 0;
        long outputTokens = 0;
        long cachedTokens = 0;
        int callCount = 0;
        SummaryCallResult finalCall;
        if (!plan.RequiresMapReduce)
        {
            string body = string.Join("\n\n", plan.Chunks[0].Units.Select(unit => unit.Text));
            finalCall = await CallAsync(
                    plan,
                    context,
                    CompactionService.CompressorSystemPrompt,
                    body,
                    ct
                )
                .ConfigureAwait(false);
            inputTokens += finalCall.Usage?.InputTokens ?? UsageRecorder.EstimateInputText(body);
            outputTokens +=
                finalCall.Usage?.OutputTokens ?? UsageRecorder.EstimateOutput(finalCall.Text);
            cachedTokens += finalCall.Usage?.CachedInputTokens ?? 0;
            callCount++;
        }
        else
        {
            var mapSummaries = new List<SummaryPiece>();
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = plan.Chunks[i];
                string body =
                    $"[片段 {chunk.Index}/{chunk.Total}]\n以下内容按原时间顺序排列，请保留任务约束、关键操作与未完成事项。\n\n"
                    + string.Join("\n\n", chunk.Units.Select(unit => unit.Text));
                var call = await CallAsync(plan, context, MapSummarySystemPrompt, body, ct)
                    .ConfigureAwait(false);
                EnsureNonEmpty(call.Text);
                inputTokens += call.Usage?.InputTokens ?? UsageRecorder.EstimateInputText(body);
                outputTokens += call.Usage?.OutputTokens ?? UsageRecorder.EstimateOutput(call.Text);
                cachedTokens += call.Usage?.CachedInputTokens ?? 0;
                callCount++;
                mapSummaries.Add(
                    new SummaryPiece(
                        i + 1,
                        call.Text.Trim(),
                        call.Usage?.OutputTokens ?? UsageRecorder.EstimateOutput(call.Text)
                    )
                );
            }

            finalCall = await ReduceAsync(plan, context, previousSummary, mapSummaries, ct)
                .ConfigureAwait(false);
            inputTokens += finalCall.InputTokens;
            outputTokens += finalCall.OutputTokens;
            cachedTokens += finalCall.CachedTokens;
            callCount += finalCall.LlmCallCount;
        }

        string summary = finalCall.Text.Trim();
        if (string.IsNullOrWhiteSpace(summary))
            throw new CompactionSummaryException("COMPACT_SUMMARY_EMPTY", "摘要模型返回为空");
        int finalSummaryTokens =
            finalCall.Usage != null && finalCall.Usage.OutputTokens > 0
                ? finalCall.Usage.OutputTokens
                : UsageRecorder.EstimateOutput(summary);
        return new CompactionSummaryResult
        {
            Summary = summary,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedTokens = cachedTokens,
            LlmCallCount = callCount,
            FinalSummaryTokens = finalSummaryTokens,
            ChunkCount = plan.Chunks.Count,
            SummarizedMessageCount = plan.Coverage.SummarizedMessageCount,
            ExcludedMessageCount = plan.Coverage.ExcludedMessageCount,
            TruncatedMessageCount = plan.Coverage.TruncatedMessageCount,
        };
    }

    private async Task<SummaryCallResult> ReduceAsync(
        CompactionSummaryPlan plan,
        CompactionSummaryExecutionContext context,
        string? previousSummary,
        IReadOnlyList<SummaryPiece> pieces,
        CancellationToken ct
    )
    {
        long inputTokens = 0;
        long outputTokens = 0;
        long cachedTokens = 0;
        int callCount = 0;
        var current = pieces.ToList();
        SummaryCallResult finalCall = null!;
        int depth = 0;
        while (current.Count > 1)
        {
            if (depth >= MaxReduceDepth)
                throw new CompactionSummaryException(
                    "COMPACT_SUMMARY_TOO_LARGE",
                    "摘要归并达到深度上限，未写入压缩桩"
                );
            var groups = GroupReducePieces(plan, previousSummary, current);
            var next = new List<SummaryPiece>();
            for (int i = 0; i < groups.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var group = groups[i];
                if (group.Count == 1)
                {
                    next.Add(group[0]);
                    continue;
                }
                string body = BuildReduceBody(previousSummary, group, i + 1, groups.Count);
                var call = await CallAsync(plan, context, ReduceSummarySystemPrompt, body, ct)
                    .ConfigureAwait(false);
                EnsureNonEmpty(call.Text);
                inputTokens += call.Usage?.InputTokens ?? UsageRecorder.EstimateInputText(body);
                outputTokens += call.Usage?.OutputTokens ?? UsageRecorder.EstimateOutput(call.Text);
                cachedTokens += call.Usage?.CachedInputTokens ?? 0;
                callCount++;
                finalCall = call;
                next.Add(
                    new SummaryPiece(
                        next.Count + 1,
                        call.Text.Trim(),
                        call.Usage?.OutputTokens ?? UsageRecorder.EstimateOutput(call.Text)
                    )
                );
            }
            current = next;
            depth++;
        }

        if (current.Count == 0 || finalCall == null)
            throw new CompactionSummaryException("COMPACT_SUMMARY_EMPTY", "摘要归并没有产生有效结果");
        return finalCall with
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedTokens = cachedTokens,
            LlmCallCount = callCount,
        };
    }

    private List<List<SummaryPiece>> GroupReducePieces(
        CompactionSummaryPlan plan,
        string? previousSummary,
        IReadOnlyList<SummaryPiece> pieces
    )
    {
        int prefixTokens = string.IsNullOrWhiteSpace(previousSummary)
            ? 0
            : _budgetService.EstimateTokens("[上轮上下文摘要]\n" + previousSummary.Trim());
        var groups = new List<List<SummaryPiece>>();
        var current = new List<SummaryPiece>();
        int currentTokens = prefixTokens + ReduceLabelReserveTokens;
        foreach (var piece in pieces)
        {
            if (
                piece.EstimatedTokens
                > plan.ChunkTokenLimit - prefixTokens - ReduceLabelReserveTokens
            )
                throw new CompactionSummaryException("COMPACT_SUMMARY_TOO_LARGE", "单个归并摘要超过输入预算");
            int separator = current.Count > 0 ? 1 : 0;
            if (
                current.Count > 0
                && currentTokens + separator + piece.EstimatedTokens > plan.ChunkTokenLimit
            )
            {
                groups.Add(current);
                current = new List<SummaryPiece>();
                currentTokens = prefixTokens + ReduceLabelReserveTokens;
                separator = 0;
            }
            current.Add(piece);
            currentTokens += separator + piece.EstimatedTokens;
        }
        if (current.Count > 0)
            groups.Add(current);
        return groups;
    }

    private static string BuildReduceBody(
        string? previousSummary,
        IReadOnlyList<SummaryPiece> pieces,
        int groupIndex,
        int groupTotal
    )
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"[归并片段 {groupIndex}/{groupTotal}]");
        if (!string.IsNullOrWhiteSpace(previousSummary))
            builder.AppendLine("[上轮上下文摘要]").AppendLine(previousSummary.Trim());
        foreach (var piece in pieces)
        {
            builder.AppendLine($"[片段 {piece.Index}]");
            builder.AppendLine(piece.Text);
        }
        return builder.ToString();
    }

    private static void EnsureNonEmpty(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new CompactionSummaryException("COMPACT_SUMMARY_EMPTY", "摘要模型返回为空");
    }

    private async Task<SummaryCallResult> CallAsync(
        CompactionSummaryPlan plan,
        CompactionSummaryExecutionContext context,
        string systemPrompt,
        string body,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        string requestId = Guid.NewGuid().ToString("N");
        var header = context.Header with { RequestId = requestId };
        var request = new UnifiedLlmRequest
        {
            RequestId = requestId,
            Provider = context.Provider,
            Model = context.Model,
            HeaderContext = header,
            SystemInstructions = systemPrompt,
            Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User(body) },
            Tools = Array.Empty<UnifiedToolDefinition>(),
        };
        var text = new System.Text.StringBuilder();
        UnifiedTokenUsage? usage = null;
        string status = "ok";
        string error = string.Empty;
        try
        {
            await foreach (
                var streamEvent in _gateway.StreamAsync(request, ct).ConfigureAwait(false)
            )
            {
                if (
                    streamEvent.Kind == UnifiedStreamEventKind.TextDelta
                    && !string.IsNullOrEmpty(streamEvent.TextDelta)
                )
                    text.Append(streamEvent.TextDelta);
                else if (
                    streamEvent.Kind == UnifiedStreamEventKind.UsageUpdated
                    && streamEvent.Usage != null
                )
                    usage = streamEvent.Usage;
                else if (streamEvent.Kind == UnifiedStreamEventKind.StreamError)
                {
                    status = "error";
                    error = streamEvent.ErrorMessage ?? "摘要模型调用失败";
                    break;
                }
                else if (streamEvent.Kind == UnifiedStreamEventKind.StreamCancelled)
                {
                    status = "cancelled";
                    error = "压缩被取消";
                    break;
                }
                else if (streamEvent.Kind == UnifiedStreamEventKind.StreamTruncated)
                {
                    status = "error";
                    error = "摘要模型输出被截断";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            error = "压缩被取消";
        }
        catch (Exception ex)
        {
            status = "error";
            error = ex.Message;
        }

        string output = text.ToString();
        try
        {
            var rows = new List<PendingUsageRow>();
            UsageRecorder.CollectAuto(
                rows,
                context.Provider,
                context.Model,
                usage,
                UsageRecorder.EstimateInput(request.Messages, request.SystemInstructions),
                UsageRecorder.EstimateOutput(output),
                status,
                requestId,
                context.SessionId,
                context.WorkspaceId,
                UsageRecorder.SourceCompact
            );
            UsageRecorder.Flush(_usageMetrics, rows, context.SessionId, context.WorkspaceId);
        }
        catch { }

        if (status == "cancelled")
            throw new CompactionSummaryException(
                "COMPACT_SUMMARY_CANCELLED",
                string.IsNullOrEmpty(error) ? "压缩被取消" : error
            );
        if (status == "error")
            throw new CompactionSummaryException(
                "COMPACT_LLM_ERROR",
                string.IsNullOrEmpty(error) ? "摘要模型调用失败" : error
            );
        return new SummaryCallResult(output, usage);
    }

    private sealed record SummaryPiece(int Index, string Text, int EstimatedTokens);

    private sealed record SummaryCallResult(
        string Text,
        UnifiedTokenUsage? Usage,
        long InputTokens = 0,
        long OutputTokens = 0,
        long CachedTokens = 0,
        int LlmCallCount = 0
    );

    private const string MapSummarySystemPrompt =
        "你是会话上下文压缩器的分片处理阶段。请把给定片段压缩为精炼中文要点，"
        + "保留任务目标、用户决定、关键路径、命令、参数、结果与未完成事项，保持片段内时间顺序，"
        + "不得补充输入中没有的信息。只输出要点。";

    private const string ReduceSummarySystemPrompt =
        "你是会话上下文压缩器的归并阶段。请按片段顺序合并全部要点，去除重复但不得删除仍有效的任务约束，"
        + "保持时间顺序与因果关系，区分已完成与未完成，不得引入新事实。只输出最终精炼要点。";
}
