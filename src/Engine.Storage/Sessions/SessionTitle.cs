namespace HaloAgent.Engine.Storage.Sessions;

/// <summary>
/// 会话标题算法（唯一真相源）。
/// 语义冻结：Trim → '\n'→' ' → 超 60 字截断 + '…'；空/空白返回 "(空会话)"。
/// 前端乐观函数必须与本实现同语义，最终以本函数经事件下发的值为准。
/// </summary>
public static class SessionTitle
{
    public const int MaxChars = 60;
    public const string EmptyTitle = "(空会话)";
    public const string Ellipsis = "…";

    public static string Build(string? raw)
    {
        var t = (raw ?? "").Trim().Replace('\n', ' ');
        if (t.Length == 0) return EmptyTitle;
        if (t.Length > MaxChars) t = t[..MaxChars] + Ellipsis;
        return t;
    }

    /// <summary>是否为空标题（供单条摘要复用）。</summary>
    public static bool IsEmpty(string title) => title == EmptyTitle;
}
