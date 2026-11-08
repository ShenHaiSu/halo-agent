namespace HaloAgent.Engine;

/// <summary>
/// Session and message identity: directory-shaped session ids compatible with
/// the desktop store, plus stable message ids for cursor paging.
/// </summary>
public static class SessionIdentity
{
    public const string DefaultWorkspace = "default";

    private const string SessionPrefix = "sess_";

    /// <summary>
    /// Builds a new directory-shaped id, e.g. sess_20261108_a1b2c3d4.
    /// Same layout as the desktop store so both sides read each other.
    /// </summary>
    public static string NewSessionId(DateTimeOffset? now = null)
    {
        string stamp = (now ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd");
        string suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{SessionPrefix}{stamp}_{suffix}";
    }

    /// <summary>Builds a new message id, e.g. msg_a1b2c3d4e5f6.</summary>
    public static string NewMessageId()
    {
        return "msg_" + Guid.NewGuid().ToString("N")[..11];
    }

    /// <summary>True for directory-shaped ids (sess_yyyyMMdd_xxxxxxxx).</summary>
    public static bool IsDirectoryShaped(string? sessionId)
    {
        if (
            string.IsNullOrEmpty(sessionId)
            || !sessionId.StartsWith(SessionPrefix, StringComparison.Ordinal)
        )
        {
            return false;
        }
        // sess_ (5) + yyyyMMdd (8) + _ (1) + suffix (8) = 22
        return sessionId.Length == 22;
    }

    /// <summary>True for the previous engine generation (guid without dashes).</summary>
    public static bool IsLegacyEngineShaped(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }
        return sessionId.Length == 32
            && sessionId.All(c => Uri.IsHexDigit(c))
            && !sessionId.StartsWith(SessionPrefix, StringComparison.Ordinal);
    }

    public static string NormalizeWorkspace(string? workspaceId)
    {
        return string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspace : workspaceId;
    }
}
