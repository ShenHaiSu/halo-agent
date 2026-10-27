namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// Core 本地的会话模板头实现（与 Storage.Config.ConfigStore.HeaderTemplate 同语义）。
///
/// 为避免 Core → Storage 依赖（分层：LLM 层自包含），这里自带一份实现，
/// 不引用 Storage 侧的版本。两边语义必须一致：
/// `session_hash = SHA256(sessionId)[:16]，小写 hex`。
/// </summary>
public static class HeaderTemplate
{
    public const string SessionIdToken = "{{session_id}}";
    public const string SessionHashToken = "{{session_hash}}";

    public static bool ContainsSessionTemplate(string v) =>
        v.Contains(SessionIdToken, StringComparison.OrdinalIgnoreCase) ||
        v.Contains(SessionHashToken, StringComparison.OrdinalIgnoreCase);

    public static string SessionHash(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return "";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>展开一批模板头；无会话时含模板的头整条跳过（Validate/ListModels 用）。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ExpandAll(
        IReadOnlyList<KeyValuePair<string, string>> templates, string? sessionId)
    {
        if (templates.Count == 0) return templates;
        var sid = sessionId ?? "";
        var hash = SessionHash(sessionId);
        var out_ = new List<KeyValuePair<string, string>>(templates.Count);
        foreach (var (k, v) in templates)
        {
            if (ContainsSessionTemplate(v) && string.IsNullOrEmpty(sid)) continue;
            var e = v.Replace(SessionIdToken, sid, StringComparison.OrdinalIgnoreCase)
                     .Replace(SessionHashToken, hash, StringComparison.OrdinalIgnoreCase);
            out_.Add(new(k, e));
        }
        return out_;
    }
}
