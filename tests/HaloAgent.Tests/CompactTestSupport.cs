using System.Runtime.CompilerServices;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Microsoft.Data.Sqlite;

namespace HaloAgent.Tests;

internal sealed class CompactTestWorkspace : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "HaloAgentCompact_" + Guid.NewGuid().ToString("N")
    );
    private readonly string _bootstrapRoot = Path.Combine(
        Path.GetTempPath(),
        "HaloAgentCompactBoot_" + Guid.NewGuid().ToString("N")
    );

    public CompactTestWorkspace()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        Storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        Repository = new SessionRepository(Storage);
        Service = new WorkspaceSessionService(Repository);
        Budget = new TokenBudgetService(null, Service);
    }

    public StoragePathManager Storage { get; }
    public SessionRepository Repository { get; }
    public WorkspaceSessionService Service { get; }
    public TokenBudgetService Budget { get; }

    public async Task<(string WorkspaceId, string SessionId)> NewSessionAsync()
    {
        string project = Path.Combine(_tempRoot, "Project_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        var workspace = await Service.AddOrUpdateWorkspaceAsync(project, "compact 测试工作区");
        var session = await Service.CreateSessionAsync(workspace.Id, "compact 测试会话");
        return (workspace.Id, session.Id);
    }

    public async Task AppendHistoryAsync(
        string workspaceId,
        string sessionId,
        int rounds,
        string marker = "round"
    )
    {
        await Service.AppendSessionMessageAsync(
            workspaceId,
            sessionId,
            UnifiedChatMessage.System("你是助手")
        );
        for (int i = 0; i < rounds; i++)
        {
            await Service.AppendSessionMessageAsync(
                workspaceId,
                sessionId,
                UnifiedChatMessage.User($"{marker}-{i:00}")
            );
            await Service.AppendSessionMessageAsync(
                workspaceId,
                sessionId,
                UnifiedChatMessage.Assistant($"{marker}-answer-{i:00}")
            );
        }
    }

    public void SetCompactMode(string mode)
    {
        var settings = Storage.LoadAppSettings();
        settings.CompactMode = mode;
        Storage.SaveAppSettingsAsync(settings).GetAwaiter().GetResult();
    }

    public SessionHistoryStore CreateStore() => new(new SessionHistoryDb(Storage));

    public async Task ExecuteSqlAsync(string workspaceId, string sql)
    {
        using var connection = new SessionHistoryDb(Storage).OpenWorkspace(workspaceId);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<long> QueryLongAsync(string workspaceId, string sql)
    {
        using var connection = new SessionHistoryDb(Storage).OpenWorkspace(workspaceId);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }

    public WorkspaceSessionService CreateMetadataFailingService()
    {
        var repository = new MetadataFailingRepository(Repository);
        return new WorkspaceSessionService(repository);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
        try
        {
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }
}

internal sealed class MetadataFailingRepository : ISessionRepository
{
    private readonly ISessionRepository _inner;

    public MetadataFailingRepository(ISessionRepository inner) => _inner = inner;

    public StoragePathManager Storage => _inner.Storage;

    public Task<List<WorkspaceInfo>> LoadWorkspacesIndexAsync(CancellationToken ct = default) =>
        _inner.LoadWorkspacesIndexAsync(ct);

    public Task<WorkspaceInfo?> MutateWorkspacesIndexAsync(
        Func<List<WorkspaceInfo>, WorkspaceInfo?> mutate,
        CancellationToken ct = default
    ) => _inner.MutateWorkspacesIndexAsync(mutate, ct);

    public Task SaveWorkspacesIndexAsync(
        List<WorkspaceInfo> workspaces,
        CancellationToken ct = default
    ) => _inner.SaveWorkspacesIndexAsync(workspaces, ct);

    public Task<List<SessionMetadata>> LoadSessionsIndexAsync(
        string workspaceId,
        CancellationToken ct = default
    ) => _inner.LoadSessionsIndexAsync(workspaceId, ct);

    public Task<List<SessionMetadata>> LoadSessionsIndexStrictAsync(
        string workspaceId,
        CancellationToken ct = default
    ) => _inner.LoadSessionsIndexStrictAsync(workspaceId, ct);

    public Task SaveSessionsIndexAsync(
        string workspaceId,
        List<SessionMetadata> sessions,
        CancellationToken ct = default
    ) => _inner.SaveSessionsIndexAsync(workspaceId, sessions, ct);

    public Task MutateSessionsIndexAsync(
        string workspaceId,
        Action<List<SessionMetadata>> mutate,
        CancellationToken ct = default
    ) => _inner.MutateSessionsIndexAsync(workspaceId, mutate, ct);

    public Task<SessionMetadata?> LoadSessionMetaAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.LoadSessionMetaAsync(workspaceId, sessionId, ct);

    public Task SaveSessionMetaAsync(
        string workspaceId,
        SessionMetadata meta,
        CancellationToken ct = default
    ) => throw new IOException("metadata-write-fault");

    public Task AppendMessageAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage message,
        CancellationToken ct = default
    ) => _inner.AppendMessageAsync(workspaceId, sessionId, message, ct);

    public Task<long> AppendCompactStubAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage stubMessage,
        TurnSummary stubTurn,
        CancellationToken ct = default
    ) => _inner.AppendCompactStubAsync(workspaceId, sessionId, stubMessage, stubTurn, ct);

    public Task<List<UnifiedChatMessage>> GetMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.GetMessagesAsync(workspaceId, sessionId, ct);

    public Task<List<UnifiedChatMessage>> GetActiveContextMessagesAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.GetActiveContextMessagesAsync(workspaceId, sessionId, ct);

    public async IAsyncEnumerable<UnifiedChatMessage> StreamMessagesAsync(
        string workspaceId,
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await foreach (
            var message in _inner
                .StreamMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false)
        )
            yield return message;
    }

    public Task<bool> DeleteSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.DeleteSessionAsync(workspaceId, sessionId, ct);

    public Task<SessionDeletionStorageResult> DeleteSessionTreeAsync(
        string workspaceId,
        IReadOnlyList<SessionDeletionNode> nodes,
        CancellationToken ct = default
    ) => _inner.DeleteSessionTreeAsync(workspaceId, nodes, ct);

    public Task<HistoryRollbackResult> RollbackCreatedSessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.RollbackCreatedSessionAsync(workspaceId, sessionId, ct);

    public Task<bool> DeleteWorkspaceDirectoryAsync(
        string workspaceId,
        CancellationToken ct = default
    ) => _inner.DeleteWorkspaceDirectoryAsync(workspaceId, ct);

    public Task<string> ExportSessionMarkdownAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => _inner.ExportSessionMarkdownAsync(workspaceId, sessionId, ct);

    public string GetWorkspaceDirectory(string workspaceId) =>
        _inner.GetWorkspaceDirectory(workspaceId);

    public string GetSessionDirectory(string workspaceId, string sessionId) =>
        _inner.GetSessionDirectory(workspaceId, sessionId);

    public string GetAttachmentsDirectory(string workspaceId, string sessionId) =>
        _inner.GetAttachmentsDirectory(workspaceId, sessionId);

    public Task<ChatImageRef> SaveImageAttachmentAsync(
        string workspaceId,
        string sessionId,
        string fileName,
        string mimeType,
        byte[] bytes,
        int width,
        int height,
        CancellationToken ct = default
    ) =>
        _inner.SaveImageAttachmentAsync(
            workspaceId,
            sessionId,
            fileName,
            mimeType,
            bytes,
            width,
            height,
            ct
        );

    public string? FindAttachmentPath(string workspaceId, string sessionId, string imageId) =>
        _inner.FindAttachmentPath(workspaceId, sessionId, imageId);
}

internal sealed class RecordingCompactGateway : ILlmGatewayClient
{
    private readonly Func<
        int,
        UnifiedLlmRequest,
        CancellationToken,
        IAsyncEnumerable<UnifiedStreamEvent>
    > _handler;
    public List<UnifiedLlmRequest> Requests { get; } = new();

    public RecordingCompactGateway(
        Func<
            int,
            UnifiedLlmRequest,
            CancellationToken,
            IAsyncEnumerable<UnifiedStreamEvent>
        >? handler = null
    ) => _handler = handler ?? ((_, _, _) => Stream("compact-summary", 100, 20, 5));

    public static async IAsyncEnumerable<UnifiedStreamEvent> Stream(
        string text,
        int inputTokens,
        int outputTokens,
        int cachedTokens,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return UnifiedStreamEvent.Text(text);
        yield return UnifiedStreamEvent.Tokens(
            inputTokens,
            outputTokens,
            cachedTokens,
            inputTokens + outputTokens
        );
        yield return UnifiedStreamEvent.Completed();
    }

    public IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
        UnifiedLlmRequest request,
        CancellationToken ct = default
    )
    {
        int index = Requests.Count;
        Requests.Add(request);
        return _handler(index, request, ct);
    }
}
