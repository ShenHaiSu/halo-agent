using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

public sealed partial class SessionRepository : ISessionRepository
{
    public async Task<long> AppendCompactStubAsync(
        string workspaceId,
        string sessionId,
        UnifiedChatMessage stubMessage,
        TurnSummary stubTurn,
        CancellationToken ct = default
    )
    {
        if (stubMessage == null)
            throw new ArgumentNullException(nameof(stubMessage));
        if (stubTurn == null)
            throw new ArgumentNullException(nameof(stubTurn));
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = new SessionHistoryStore(new SessionHistoryDb(_storageManager));
            return await store
                .InsertCompactStubAsync(workspaceId, sessionId, stubMessage, stubTurn, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
