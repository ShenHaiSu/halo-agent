#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HaloAgent.Services.Agent;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

public sealed class TurnCancellationHistoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly SessionHistoryStore _store;
    private readonly WorkspaceInfo _workspace;

    public TurnCancellationHistoryTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloCancelHistory_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloCancelHistoryBoot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
        _store = new SessionHistoryStore(_storageManager);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(Path.Combine(_tempRoot, "Project"), "取消历史测试")
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        TryDelete(_tempRoot);
        TryDelete(_bootstrapRoot);
    }

    [Fact]
    public async Task EnsureTurnEndedAsync_MissingTurnCreatesUserOnlySummary()
    {
        var session = await CreateSessionAsync("sess-cancel");
        var fallback = CreateFallback(session.Id, "user-first", "request-cancel");

        bool changed = await _store.EnsureTurnEndedAsync(
            _workspace.Id,
            session.Id,
            fallback.TurnId,
            TimelineTurnStatus.Cancelled,
            fallback,
            CancellationToken.None
        );

        Assert.True(changed);
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal("uturn-user-first", turn.TurnId);
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal("request-cancel", turn.LastRequestId);
        Assert.Equal(fallback.UserContent, turn.UserContent);
        Assert.Equal(0, turn.Blocks);
        Assert.Equal(0, turn.Tools);
        Assert.Equal(0, turn.Chars);
        Assert.Empty(await _store.GetBlockIdsByTurnAsync(_workspace.Id, session.Id, turn.TurnId));
    }

    [Fact]
    public async Task EnsureTurnEndedAsync_RepeatedCallIsIdempotent()
    {
        var session = await CreateSessionAsync("sess-cancel");
        var first = CreateFallback(session.Id, "user-first", "request-cancel");
        var second = CreateFallback(session.Id, "user-first", "request-cancel");

        Assert.True(
            await _store.EnsureTurnEndedAsync(
                _workspace.Id,
                session.Id,
                first.TurnId,
                TimelineTurnStatus.Cancelled,
                first,
                CancellationToken.None
            )
        );
        Assert.True(
            await _store.EnsureTurnEndedAsync(
                _workspace.Id,
                session.Id,
                second.TurnId,
                TimelineTurnStatus.Cancelled,
                second,
                CancellationToken.None
            )
        );

        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal(1, turn.TurnSeq);
        Assert.Equal("request-cancel", turn.LastRequestId);
    }

    [Fact]
    public async Task EnsureTurnEndedAsync_ExistingTurnKeepsStatsAndDemotesFinal()
    {
        var session = await CreateSessionAsync("sess-existing");
        const string turnId = "uturn-user-existing";
        const string finalBlockId = "block-final";
        var user = new UnifiedChatMessage
        {
            Id = "user-existing",
            Role = "user",
            Content = "完整用户内容"
        };
        var assistant = UnifiedChatMessage.Assistant("完整回答");
        var blocks = new List<TimelineBlock>
        {
            new()
            {
                BlockId = "block-tool",
                TurnId = turnId,
                Iteration = 1,
                Sequence = 1,
                Kind = TimelineBlockKind.ToolCall,
                Tag = TimelineBlockTag.Process,
                CallId = "call-existing",
                FunctionName = "read_file",
                CharCount = 12,
                Preview = "args",
                Content = "{}",
            },
            new()
            {
                BlockId = finalBlockId,
                TurnId = turnId,
                Iteration = 1,
                Sequence = 2,
                Kind = TimelineBlockKind.Text,
                Tag = TimelineBlockTag.Final,
                CharCount = 4,
                Preview = "answer",
                Content = "answer",
            },
        };
        await _store.AppendTurnAsync(
            _workspace.Id,
            session.Id,
            new[] { user, assistant },
            blocks,
            new TurnSummary
            {
                TurnId = turnId,
                IterEnd = 1,
                UserPreview = "完整用户内容",
                UserContent = user.Content!,
                FinalBlockId = finalBlockId,
                FinalPreview = "answer",
                Blocks = 2,
                Tools = 1,
                Failed = 1,
                DurationMs = 321,
                Chars = 16,
                Status = TimelineTurnStatus.Completed,
                LastRequestId = "request-existing",
            }
        );

        bool changed = await _store.EnsureTurnEndedAsync(
            _workspace.Id,
            session.Id,
            turnId,
            TimelineTurnStatus.Cancelled,
            CreateFallback(session.Id, "user-existing", "request-cancel"),
            CancellationToken.None
        );

        Assert.True(changed);
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Null(turn.FinalBlockId);
        Assert.Equal(2, turn.Blocks);
        Assert.Equal(1, turn.Tools);
        Assert.Equal(1, turn.Failed);
        Assert.Equal(321, turn.DurationMs);
        Assert.Equal(16, turn.Chars);
        Assert.Equal(user.Content, turn.UserContent);
        Assert.Equal("request-existing", turn.LastRequestId);
        var (contents, _) = await _store.GetBlockContentsAsync(
            _workspace.Id,
            session.Id,
            new[] { finalBlockId }
        );
        Assert.Equal(TimelineBlockTag.Process, contents[finalBlockId].Tag);
    }

    [Fact]
    public async Task EnsureTurnEndedAsync_MissingTurnWithoutFallbackReturnsFalse()
    {
        var session = await CreateSessionAsync("sess-no-fallback");

        bool changed = await _store.EnsureTurnEndedAsync(
            _workspace.Id,
            session.Id,
            "uturn-absent",
            TimelineTurnStatus.Cancelled,
            null,
            CancellationToken.None
        );

        Assert.False(changed);
        Assert.Empty(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
    }

    [Fact]
    public async Task EnsureTurnEndedAsync_FallbackTurnIdMismatchIsRejected()
    {
        var session = await CreateSessionAsync("sess-mismatch");
        var fallback = CreateFallback(session.Id, "user-first", "request-cancel");

        await Assert.ThrowsAsync<ArgumentException>(
            () =>
                _store.EnsureTurnEndedAsync(
                    _workspace.Id,
                    session.Id,
                    "uturn-other",
                    TimelineTurnStatus.Cancelled,
                    fallback,
                    CancellationToken.None
                )
        );

        Assert.Empty(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
    }

    [Fact]
    public async Task EnsureTurnEndedAsync_ConcurrentCallsCreateSingleTurn()
    {
        var session = await CreateSessionAsync("sess-concurrent");
        const string turnId = "uturn-user-first";

        bool[] results = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ =>
                    _store.EnsureTurnEndedAsync(
                        _workspace.Id,
                        session.Id,
                        turnId,
                        TimelineTurnStatus.Cancelled,
                        CreateFallback(session.Id, "user-first", "request-cancel"),
                        CancellationToken.None
                    )
                )
        );

        Assert.All(results, Assert.True);
        Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
    }

    [Fact]
    public async Task CancelBeforeFirstAssistant_MaterializesUserTurn()
    {
        var session = await CreateSessionAsync("sess-cancel");
        var gateway = new BlockingGateway();
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(_service, gateway, sink);
        using var cts = new CancellationTokenSource();

        Task<AgentTurnResult> run = orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "第一条消息",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );
        Assert.True(await gateway.WaitStartedAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        AgentTurnResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.WasCancelled);
        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, session.Id);
        var user = Assert.Single(messages);
        Assert.Equal("user", user.Role);
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal("uturn-" + user.Id, turn.TurnId);
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal(0, turn.Blocks);
        Assert.Equal(0, turn.Tools);
        Assert.Equal("request-cancel", turn.LastRequestId);
        Assert.Empty(await _store.GetBlockIdsByTurnAsync(_workspace.Id, session.Id, turn.TurnId));
        var payload = ReadCancelledPayload(sink);
        Assert.True(ReadBoolean(payload, "userMessagePersisted"));
        Assert.True(ReadBoolean(payload, "timelineFinalized"));
    }

    [Fact]
    public async Task TurnCancelled_PublishedAfterTimelineFinalized()
    {
        var session = await CreateSessionAsync("sess-event-order");
        var sink = new RecordingSink();
        TurnSummary? visibleAtEvent = null;
        Exception? callbackError = null;
        sink.OnCancelled = () =>
        {
            try
            {
                visibleAtEvent = _store
                    .GetTimelineSummaryAsync(_workspace.Id, session.Id)
                    .GetAwaiter()
                    .GetResult()
                    .SingleOrDefault();
            }
            catch (Exception ex)
            {
                callbackError = ex;
            }
            return Task.CompletedTask;
        };
        var orchestrator = NewOrchestrator(_service, new ImmediateCancelledGateway(), sink);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "首回复前停止",
            turnRequestId: "request-cancel"
        );

        Assert.True(result.WasCancelled);
        Assert.Null(callbackError);
        Assert.NotNull(visibleAtEvent);
        Assert.Equal(TimelineTurnStatus.Cancelled, visibleAtEvent!.Status);
        var payload = ReadCancelledPayload(sink);
        Assert.True(ReadBoolean(payload, "timelineFinalized"));
        string json = JsonSerializer.Serialize(payload, HaloJsonOptions.CompactCamelCase);
        Assert.Contains("\"userMessagePersisted\":true", json);
        Assert.Contains("\"timelineFinalized\":true", json);
        Assert.DoesNotContain("UserMessagePersisted", json);
        Assert.DoesNotContain("TimelineFinalized", json);
    }

    [Fact]
    public async Task CancelAfterPartialStream_DoesNotPersistDraftBlocksOrAssistant()
    {
        var session = await CreateSessionAsync("sess-partial");
        var gateway = new BlockingGateway(emitPartial: true);
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(_service, gateway, sink);
        using var cts = new CancellationTokenSource();

        Task<AgentTurnResult> run = orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "流式输出后停止",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );
        Assert.True(await gateway.WaitStartedAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        AgentTurnResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.WasCancelled);
        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, session.Id);
        Assert.Equal("user", Assert.Single(messages).Role);
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal(0, turn.Blocks);
        Assert.Empty(await _store.GetBlockIdsByTurnAsync(_workspace.Id, session.Id, turn.TurnId));
    }

    [Fact]
    public async Task CancelDuringTurnPersist_DoesNotPublishFalseCompletion()
    {
        var session = await CreateSessionAsync("sess-turn-persist");
        using var cts = new CancellationTokenSource();
        var service = CreateCancellingService(cts, string.Empty, cancelAfterAssistantCommit: true);
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(service, new ImmediateCompletedGateway(), sink);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "assistant 已提交但摘要未提交",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );

        Assert.True(result.WasCancelled);
        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, session.Id);
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, message => message.Role == "user");
        Assert.Contains(messages, message => message.Role == "assistant");
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal(0, turn.Blocks);
        Assert.DoesNotContain(sink.Events, item => item.Kind == "AssistantMessagePersisted");
        var payload = ReadCancelledPayload(sink);
        Assert.True(ReadBoolean(payload, "userMessagePersisted"));
        Assert.True(ReadBoolean(payload, "timelineFinalized"));
    }

    [Fact]
    public async Task CancelBeforeStart_ReportsUserNotPersistedAndCreatesNoTurn()
    {
        var session = await CreateSessionAsync("sess-pre-start");
        var gateway = new BlockingGateway();
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(_service, gateway, sink);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "不应落盘",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );

        Assert.True(result.WasCancelled);
        Assert.Equal(0, gateway.Calls);
        Assert.Empty(await _service.GetSessionMessagesAsync(_workspace.Id, session.Id));
        Assert.Empty(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        var payload = ReadCancelledPayload(sink);
        Assert.False(ReadBoolean(payload, "userMessagePersisted"));
        Assert.False(ReadBoolean(payload, "timelineFinalized"));
    }

    [Fact]
    public async Task CancelDuringAppend_DoesNotCreatePhantomUserOrTurn()
    {
        var session = await CreateSessionAsync("sess-pre-append");
        using var cts = new CancellationTokenSource();
        var service = CreateCancellingService(
            cts,
            nameof(IWorkspaceSessionService.AppendSessionMessageAsync)
        );
        var gateway = new BlockingGateway();
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(service, gateway, sink);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "append 前取消",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );

        Assert.True(result.WasCancelled);
        Assert.Equal(0, gateway.Calls);
        Assert.Empty(await _service.GetSessionMessagesAsync(_workspace.Id, session.Id));
        Assert.Empty(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        var payload = ReadCancelledPayload(sink);
        Assert.False(ReadBoolean(payload, "userMessagePersisted"));
        Assert.False(ReadBoolean(payload, "timelineFinalized"));
    }

    [Fact]
    public async Task CancelDuringBuildRequest_MaterializesCancelledUserTurn()
    {
        var session = await CreateSessionAsync("sess-build-request");
        using var cts = new CancellationTokenSource();
        var service = CreateCancellingService(
            cts,
            nameof(IWorkspaceSessionService.GetActiveContextMessagesAsync)
        );
        var gateway = new BlockingGateway();
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(service, gateway, sink);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "构建请求时取消",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );

        Assert.True(result.WasCancelled);
        Assert.Equal(0, gateway.Calls);
        var user = Assert.Single(await _service.GetSessionMessagesAsync(_workspace.Id, session.Id));
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal("uturn-" + user.Id, turn.TurnId);
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        Assert.Equal("request-cancel", turn.LastRequestId);
        var payload = ReadCancelledPayload(sink);
        Assert.True(ReadBoolean(payload, "userMessagePersisted"));
        Assert.True(ReadBoolean(payload, "timelineFinalized"));
    }

    [Fact]
    public async Task CancelWithoutTimelineStorage_ExplicitlyReportsNotFinalized()
    {
        var session = await CreateSessionAsync("sess-no-storage");
        var gateway = new BlockingGateway();
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(_service, gateway, sink, useStorage: false);
        using var cts = new CancellationTokenSource();

        Task<AgentTurnResult> run = orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "存储不可用",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );
        Assert.True(await gateway.WaitStartedAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        AgentTurnResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.WasCancelled);
        Assert.Single(await _service.GetSessionMessagesAsync(_workspace.Id, session.Id));
        Assert.Empty(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        var payload = ReadCancelledPayload(sink);
        Assert.True(ReadBoolean(payload, "userMessagePersisted"));
        Assert.False(ReadBoolean(payload, "timelineFinalized"));
    }

    [Fact]
    public async Task UnhandledCancellation_FinalizesUserTurnBeforeRethrow()
    {
        var session = await CreateSessionAsync("sess-unhandled-cancel");
        var gateway = new BlockingGateway();
        var sink = new RecordingSink();
        var budget = DispatchProxy.Create<ITokenBudgetService, ThrowingBudgetServiceProxy>();
        ((ThrowingBudgetServiceProxy)(object)budget).Inner = _budgetService;
        var orchestrator = NewOrchestrator(_service, gateway, sink, budgetService: budget);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () =>
                orchestrator.RunAgentTurnAsync(
                    _workspace.Id,
                    session.Id,
                    "未捕获取消安全网",
                    turnRequestId: "request-cancel"
                )
        );

        Assert.Equal(0, gateway.Calls);
        var user = Assert.Single(await _service.GetSessionMessagesAsync(_workspace.Id, session.Id));
        var turn = Assert.Single(await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id));
        Assert.Equal("uturn-" + user.Id, turn.TurnId);
        Assert.Equal(TimelineTurnStatus.Cancelled, turn.Status);
        var payload = ReadCancelledPayload(sink);
        Assert.True(ReadBoolean(payload, "userMessagePersisted"));
        Assert.True(ReadBoolean(payload, "timelineFinalized"));
        string json = JsonSerializer.Serialize(payload, HaloJsonOptions.CompactCamelCase);
        Assert.Contains("\"reason\":\"unhandled-cancel\"", json);
    }

    [Fact]
    public async Task NextTurn_SeesCancelledUserExactlyOnceAndKeepsBothSummaries()
    {
        var session = await CreateSessionAsync("sess-next-turn");
        var gateway = new SequentialGateway();
        var sink = new RecordingSink();
        var orchestrator = NewOrchestrator(_service, gateway, sink);
        using var cts = new CancellationTokenSource();

        Task<AgentTurnResult> firstRun = orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            session.Id,
            "旧消息",
            cancellationToken: cts.Token,
            turnRequestId: "request-cancel"
        );
        Assert.True(await gateway.WaitStartedAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        Assert.True((await firstRun.WaitAsync(TimeSpan.FromSeconds(10))).WasCancelled);

        AgentTurnResult secondResult = await orchestrator
            .RunAgentTurnAsync(_workspace.Id, session.Id, "新消息", turnRequestId: "request-complete")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(secondResult.Success);
        Assert.NotNull(gateway.SecondRequestMessages);
        var requestUsers = gateway
            .SecondRequestMessages!.Where(message => message.Role == "user")
            .ToList();
        Assert.Equal(1, requestUsers.Count(message => message.Content == "旧消息"));
        Assert.Equal(1, requestUsers.Count(message => message.Content == "新消息"));
        var turns = await _store.GetTimelineSummaryAsync(_workspace.Id, session.Id);
        Assert.Equal(2, turns.Count);
        Assert.Contains(
            turns,
            turn =>
                turn.Status == TimelineTurnStatus.Cancelled
                && turn.LastRequestId == "request-cancel"
        );
        Assert.Contains(
            turns,
            turn =>
                turn.Status == TimelineTurnStatus.Completed
                && turn.LastRequestId == "request-complete"
        );
    }

    private async Task<SessionMetadata> CreateSessionAsync(string title)
    {
        return await _service.CreateSessionAsync(_workspace.Id, title);
    }

    private AgentOrchestrator NewOrchestrator(
        IWorkspaceSessionService service,
        ILlmGatewayClient gateway,
        IAgentEventSink sink,
        bool useStorage = true,
        ITokenBudgetService? budgetService = null
    ) =>
        new(
            service,
            budgetService ?? _budgetService,
            gateway,
            new ToolRegistry(),
            useStorage ? _storageManager : null,
            sink: sink
        );

    private IWorkspaceSessionService CreateCancellingService(
        CancellationTokenSource cts,
        string cancelMethod,
        bool cancelAfterAssistantCommit = false
    )
    {
        var proxy = DispatchProxy.Create<IWorkspaceSessionService, CancellingServiceProxy>();
        var control = (CancellingServiceProxy)(object)proxy;
        control.Inner = _service;
        control.CancelSource = cts;
        control.CancelMethod = cancelMethod;
        control.CancelAfterAssistantCommit = cancelAfterAssistantCommit;
        return proxy;
    }

    private static TurnSummary CreateFallback(string sessionId, string userId, string requestId)
    {
        var ctx = new TurnContext
        {
            WorkspaceId = "workspace-test",
            SessionId = sessionId,
            UserMessage = new UnifiedChatMessage
            {
                Id = userId,
                Role = "user",
                Content = "用户消息 " + userId,
            },
            TurnRequestId = requestId,
        };
        return TimelineFinalizer.FinalizeUserTurn(ctx, TimelineTurnStatus.Cancelled).Turn;
    }

    private static object ReadCancelledPayload(RecordingSink sink)
    {
        var events = sink.Events.Where(item => item.Kind == "TurnCancelled").ToList();
        var item = Assert.Single(events);
        Assert.NotNull(item.Payload);
        return item.Payload!;
    }

    private static bool ReadBoolean(object payload, string propertyName)
    {
        var property = payload.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<bool>(property!.GetValue(payload));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    public class CancellingServiceProxy : DispatchProxy
    {
        public IWorkspaceSessionService Inner { get; set; } = null!;
        public CancellationTokenSource CancelSource { get; set; } = null!;
        public string CancelMethod { get; set; } = string.Empty;
        public bool CancelAfterAssistantCommit { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("DispatchProxy target method is missing.");
            if (string.Equals(targetMethod.Name, CancelMethod, StringComparison.Ordinal))
            {
                CancelSource.Cancel();
                var token = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
                token.ThrowIfCancellationRequested();
            }
            object? result;
            try
            {
                result = targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            if (
                CancelAfterAssistantCommit
                && string.Equals(
                    targetMethod.Name,
                    nameof(IWorkspaceSessionService.AppendSessionMessageAsync),
                    StringComparison.Ordinal
                )
                && args?.ElementAtOrDefault(2) is UnifiedChatMessage { Role: "assistant" }
                && result is Task<bool> commitTask
            )
            {
                _ = commitTask.ContinueWith(
                    _ => CancelSource.Cancel(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            }
            return result;
        }
    }

    public class ThrowingBudgetServiceProxy : DispatchProxy
    {
        public ITokenBudgetService Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
                throw new InvalidOperationException("DispatchProxy target method is missing.");
            if (
                string.Equals(
                    targetMethod.Name,
                    nameof(ITokenBudgetService.TruncateToBudget),
                    StringComparison.Ordinal
                )
            )
            {
                throw new OperationCanceledException();
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    private sealed class RecordingSink : IAgentEventSink
    {
        private readonly object _sync = new();
        private readonly List<(string SessionId, string Kind, object? Payload)> _events = new();

        public Func<Task>? OnCancelled { get; set; }

        public IReadOnlyList<(string SessionId, string Kind, object? Payload)> Events
        {
            get
            {
                lock (_sync)
                    return _events.ToArray();
            }
        }

        public Task EmitAsync(string sessionId, string kind, object? payload)
        {
            lock (_sync)
                _events.Add((sessionId, kind, payload));
            if (
                string.Equals(kind, "TurnCancelled", StringComparison.Ordinal)
                && OnCancelled != null
            )
                return OnCancelled();
            return Task.CompletedTask;
        }

        public void EmitStreamDelta(
            string sessionId,
            string kind,
            string textDelta,
            string? blockId = null,
            long seq = 0,
            string? tag = null
        ) { }
    }

    private class BlockingGateway : ILlmGatewayClient
    {
        private readonly bool _emitPartial;
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public BlockingGateway(bool emitPartial = false) => _emitPartial = emitPartial;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<bool> WaitStartedAsync(TimeSpan timeout)
        {
            Task completed = await Task.WhenAny(_started.Task, Task.Delay(timeout));
            return completed == _started.Task;
        }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _calls);
            if (_emitPartial)
            {
                yield return UnifiedStreamEvent.Reasoning("未提交的思考");
                yield return UnifiedStreamEvent.Text("未提交的回答");
            }
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            ct.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ImmediateCancelledGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return UnifiedStreamEvent.Cancelled();
        }
    }

    private sealed class ImmediateCompletedGateway : ILlmGatewayClient
    {
        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield return UnifiedStreamEvent.Text("完整回答");
            yield return UnifiedStreamEvent.Completed();
        }
    }

    private sealed class SequentialGateway : ILlmGatewayClient
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public List<UnifiedChatMessage>? SecondRequestMessages { get; private set; }

        public Task<bool> WaitStartedAsync(TimeSpan timeout) => WaitFirstStartedAsync(timeout);

        private async Task<bool> WaitFirstStartedAsync(TimeSpan timeout)
        {
            Task completed = await Task.WhenAny(_firstStarted.Task, Task.Delay(timeout));
            return completed == _firstStarted.Task;
        }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                ct.ThrowIfCancellationRequested();
                yield break;
            }

            SecondRequestMessages = request.Messages.ToList();
            yield return UnifiedStreamEvent.Text("第二条回答");
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
