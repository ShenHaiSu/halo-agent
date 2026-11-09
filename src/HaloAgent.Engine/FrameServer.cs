using System.Text.Json;
using System.Threading.Channels;
using HaloAgent.Engine.Abstractions;

namespace HaloAgent.Engine;

/// <summary>
/// Single-reader frame router with a bounded queue and a serialized write path.
/// Reads arrive in order through a channel of 64 slots; handlers run on the
/// worker pool; all replies and events leave through one ordered journal.
/// The turn cancel entry never takes the write gate so it stays the fastest path.
/// </summary>
public sealed class FrameServer : IAsyncDisposable
{
    public const int QueueBound = 64;

    private sealed record QueueItem(
        RequestFrame Request,
        TaskCompletionSource<ResponseFrame> Reply
    );

    private readonly Channel<QueueItem> _queue;
    private readonly Dictionary<string, FrameHandler> _routes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly EngineState _state = new();
    private readonly IEventJournal _events;
    private readonly IEngineLog _log;
    private readonly EngineContext _context;
    private readonly SessionLedger _sessions = new();
    private readonly TurnCoordinator _turns;
    private readonly UsageLedger _usage = new();
    private readonly TurnQueue _turnQueue = new();
    private readonly AttachmentIndex _attachments = new();
    private readonly ConfigAssetLedger _assets = new();
    private static readonly HashSet<string> ReadOnlyMethods =
        new(StringComparer.Ordinal)
        {
            "system.ping",
            "system.info",
            "session.list",
            "session.get",
            "session.history",
            "state.snapshot",
            "usage.query",
            "asset.skills.list",
            "asset.skills.get",
            "asset.memory.list",
            "asset.personas.list",
            "asset.personas.get",
        };
    private CancellationTokenSource? _loopScope;
    private Task? _loopTask;

    public FrameServer(IEventJournal? events = null, IEngineLog? log = null)
    {
        _events = events ?? new MemoryEventJournal();
        _log = log ?? NullEngineLog.Instance;
        _context = new EngineContext(_state, _events, _log);
        _turns = new TurnCoordinator(_state);
        _queue = Channel.CreateBounded<QueueItem>(
            new BoundedChannelOptions(QueueBound)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        SkeletonRoutes.RegisterInto(_routes);
        EngineRoutes.RegisterInto(
            _routes,
            _state,
            _sessions,
            _turns,
            _usage,
            _attachments,
            _events,
            _turnQueue,
            _assets
        );
    }

    public EngineState State => _state;
    public IEventJournal Events => _events;
    public SessionLedger Sessions => _sessions;
    public TurnCoordinator Turns => _turns;
    public UsageLedger Usage => _usage;
    public TurnQueue TurnQueue => _turnQueue;
    public AttachmentIndex Attachments => _attachments;
    public ConfigAssetLedger ConfigAssets => _assets;
    public IEngineContext Context => _context;

    public void ConfigureGateway(IEngineGateway? gateway)
    {
        _context.Gateway = gateway;
    }

    public void ConfigureBudget(IEngineBudget? budget)
    {
        _context.Budget = budget;
    }

    public void ConfigureTurnOptions(EngineTurnOptions? options)
    {
        _context.TurnOptions = options ?? new EngineTurnOptions();
        try
        {
            _usage.SetMaxTokens(_context.TurnOptions.BudgetMaxTokens);
        }
        catch { }
    }

    public void ConfigureDataRoot(string dataRoot)
    {
        _context.DataRoot = dataRoot ?? string.Empty;
        try
        {
            _assets.Load(_context.DataRoot);
        }
        catch { }
    }

    public void ConfigureToolRunner(IEngineToolRunner? runner)
    {
        _context.ToolRunner = runner;
    }

    public void Register(string method, FrameHandler handler)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Method name is required.", nameof(method));
        }
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }
        _routes[method] = handler;
    }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }
        _loopScope = new CancellationTokenSource();
        _loopTask = Task.Run(() => ReadLoopAsync(_loopScope.Token));
    }

    public async Task<ResponseFrame> SubmitAsync(
        RequestFrame request,
        CancellationToken cancellationToken = default
    )
    {
        if (_loopTask is null)
        {
            Start();
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return ResponseFrame.Fail(request.Id, "INVALID_PARAMS", "Method name is required.");
        }

        if (_state.TryGetEcho(request.Id, out var cached) && cached is not null)
        {
            return cached;
        }

        if (string.Equals(request.Method, "agent.cancel", StringComparison.Ordinal))
        {
            return await HandleCancelFastAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var reply = new TaskCompletionSource<ResponseFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await _queue
            .Writer.WriteAsync(new QueueItem(request, reply), cancellationToken)
            .ConfigureAwait(false);
        ResponseFrame response = await reply.Task.ConfigureAwait(false);
        _state.StoreEcho(request.Id, response);
        return response;
    }

    public EventBatch ReadEvents(long sinceSequence, int maxCount) =>
        _events.ReadSince(sinceSequence, maxCount);

    /// <summary>
    /// Persists every known session ledger to the directory with atomic
    /// rewrite plus backup, so a failed compaction or rewrite can be rolled back.
    /// Each entry carries its descriptor, so workspace scope and provider
    /// selection survive a restart. Replies stay in memory; the write gate
    /// serializes persistence behind normal traffic.
    /// </summary>
    public void SaveSessions(string directory)
    {
        foreach (var descriptor in _sessions.ListPairs())
        {
            if (!_sessions.TryGetDescriptor(descriptor.SessionId, out var full) || full is null)
            {
                JsonlHistoryFile.Save(
                    directory,
                    descriptor.SessionId,
                    descriptor.Title,
                    _sessions.Snapshot(descriptor.SessionId)
                );
                continue;
            }
            JsonlHistoryFile.Save(directory, full, _sessions.Snapshot(descriptor.SessionId));
        }
        try
        {
            _assets.Save(directory);
        }
        catch { }
    }

    /// <summary>Reloads persisted ledgers back into memory; corrupt lines are skipped.</summary>
    public void LoadSessions(string directory)
    {
        LoadSessionsWithReport(directory);
    }

    /// <summary>
    /// Reloads persisted ledgers and reports what the sweep found:
    /// restored sessions and messages, skipped corrupt lines and healed aliases.
    /// Reads both engine flat files and the desktop workspace tree so the two
    /// generations stay mutually readable during the shadow period.
    public JsonlHistoryFile.ScanReport LoadSessionsWithReport(string directory)
    {
        foreach (var entry in _sessions.ListPairs().ToList())
        {
            _sessions.Delete(entry.SessionId);
        }
        try
        {
            _assets.Load(directory);
        }
        catch { }
        if (!Directory.Exists(directory) && !Directory.Exists(Path.Combine(directory, "sessions")))
        {
            return new JsonlHistoryFile.ScanReport(0, 0, 0, 0);
        }
        return JsonlHistoryFile.ScanAndRestore(directory, _sessions);
    }

    /// <summary>Restores one session from its backup file after a bad rewrite.</summary>
    public bool RollbackSession(string directory, string sessionId)
    {
        bool restored = JsonlHistoryFile.TryRollback(directory, sessionId);
        if (
            restored
            && JsonlHistoryFile.TryLoad(
                directory,
                sessionId,
                out _,
                out var messages,
                out var descriptor
            )
                is { Found: true }
        )
        {
            _sessions.Restore(
                sessionId,
                descriptor
                    ?? new Abstractions.SessionDescriptor(
                        sessionId,
                        string.Empty,
                        SessionIdentity.DefaultWorkspace
                    ),
                messages
            );
        }
        return restored;
    }

    private async Task<ResponseFrame> HandleCancelFastAsync(
        RequestFrame request,
        CancellationToken cancellationToken
    )
    {
        string turnId = ReadString(request.Payload, "turnId");
        string sessionIdArg = ReadString(request.Payload, "sessionId");
        if (string.IsNullOrWhiteSpace(turnId) && string.IsNullOrWhiteSpace(sessionIdArg))
        {
            return ResponseFrame.Fail(
                request.Id,
                "INVALID_PARAMS",
                "turnId is required for cancel."
            );
        }

        if (!string.IsNullOrWhiteSpace(turnId))
        {
            bool signaled = _turns.MarkCancelled(turnId);
            string? sessionId = _turns.TrySessionOf(turnId, out string? found) ? found : null;
            if (
                !_turns.TrySessionOf(turnId, out _)
                && _state.TryTakeTurnSession(turnId, out string? legacySession)
            )
            {
                sessionId = legacySession;
            }
            _turns.RemoveTimeout(turnId);
            _events.Append(
                "turn.cancelled",
                new
                {
                    turnId,
                    sessionId,
                    signaled
                }
            );
            var response = signaled
                ? ResponseFrame.Success(request.Id, new { turnId, signaled })
                : ResponseFrame.Fail(
                    request.Id,
                    "NOT_FOUND",
                    "Turn is unknown or already finished.",
                    turnId
                );
            _state.StoreEcho(request.Id, response);
            await Task.CompletedTask;
            return response;
        }

        string rootSession = sessionIdArg.Trim();
        IReadOnlyList<string> tree = _sessions.ListTreeSessions(rootSession);
        if (tree.Count == 0)
        {
            var missing = ResponseFrame.Fail(request.Id, "NOT_FOUND", "Session is unknown.");
            _state.StoreEcho(request.Id, missing);
            return missing;
        }
        var treeSet = new HashSet<string>(tree, StringComparer.Ordinal);
        IReadOnlyList<string> cancelled = _turns.CancelTreeBySessions(treeSet.Contains);
        foreach (string cancelledTurn in cancelled)
        {
            _turns.RemoveTimeout(cancelledTurn);
            string? turnSession = _turns.TrySessionOf(cancelledTurn, out string? located)
                ? located
                : rootSession;
            _events.Append(
                "turn.cancelled",
                new
                {
                    turnId = cancelledTurn,
                    sessionId = turnSession,
                    signaled = true,
                    reason = "cancelled",
                }
            );
        }
        int compactCancelled = 0;
        foreach (string member in tree)
        {
            if (_sessions.TryCancelCompaction(member))
            {
                compactCancelled++;
                _events.Append(
                    "session.updated",
                    new { sessionId = member, reason = "compactCancelled" }
                );
            }
            _turnQueue.Clear(member);
        }
        var treeResponse =
            cancelled.Count > 0 || compactCancelled > 0
                ? ResponseFrame.Success(
                    request.Id,
                    new
                    {
                        sessionId = rootSession,
                        cancelledTurns = cancelled,
                        compactCancelled
                    }
                )
                : ResponseFrame.Fail(
                    request.Id,
                    "NOT_FOUND",
                    "No running turn in this session tree."
                );
        _state.StoreEcho(request.Id, treeResponse);
        await Task.CompletedTask;
        return treeResponse;
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        await foreach (QueueItem item in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            ResponseFrame response;
            try
            {
                if (!_routes.TryGetValue(item.Request.Method, out var handler))
                {
                    response = ResponseFrame.Fail(
                        item.Request.Id,
                        "NOT_FOUND",
                        $"Unknown method '{item.Request.Method}'."
                    );
                }
                else if (ReadOnlyMethods.Contains(item.Request.Method))
                {
                    response = await handler(item.Request, _context, token).ConfigureAwait(false);
                }
                else
                {
                    await _writeGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        response = await handler(item.Request, _context, token)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeGate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                response = ResponseFrame.Fail(
                    item.Request.Id,
                    "CANCELLED",
                    "Request was cancelled."
                );
            }
            catch (Exception ex)
            {
                _log.Error("Frame handler failed.", ex);
                response = ResponseFrame.Fail(item.Request.Id, "INTERNAL", "Handler failed.");
            }

            item.Reply.TrySetResult(response);
        }
    }

    internal static string ReadString(JsonElement payload, string name)
    {
        if (
            payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.String
        )
        {
            return node.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        if (_loopScope is not null)
        {
            _loopScope.Cancel();
        }
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
        _writeGate.Dispose();
        _loopScope?.Dispose();
    }
}
