namespace HaloAgent.Engine.Core.Util;

public static class TextUtil
{
    /// <summary>
    /// 按<b>字符</b>截断（不是字节），避免切坏中文/emoji。
    /// 只在真的超长时追加 suffix。
    ///
    /// 实现要点：一次迭代同时判断“是否超长”和“取前 N 个”，
    /// 不调两次 chars().Count()。
    /// </summary>
    public static string TruncateChars(string? s, int max, string suffix)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (max <= 0) return suffix;

        // 快路径：ASCII 且长度不超（绝大多数情况）
        if (s.Length <= max) return s;

        int count = 0;
        int cutIndex = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            if (count == max) break;
            count++;
            cutIndex += rune.Utf16SequenceLength;
        }

        // 没有更多字符（正好等于 max）
        if (cutIndex >= s.Length) return s;
        return s[..cutIndex] + suffix;
    }

    /// <summary>
    /// 保留头尾、中间折叠（shell 长输出用）。
    /// </summary>
    public static string TruncateMiddle(string s, int max, string marker = "\n…(输出过长，中间部分已省略)…\n")
    {
        if (string.IsNullOrEmpty(s)) return "";

        // 按字符计
        int total = CountChars(s);
        if (total <= max) return s;

        int half = max / 2;
        var head = TakeChars(s, half);
        var tail = TakeLastChars(s, half);
        return head + marker + tail;
    }

    public static string TakeLast(string s, int n)
    {
        if (string.IsNullOrEmpty(s) || n <= 0) return "";
        var runes = new List<string>(Math.Min(n, s.Length));
        foreach (var r in s.EnumerateRunes()) runes.Add(r.ToString());
        var take = Math.Min(n, runes.Count);
        return string.Concat(runes.Skip(runes.Count - take));
    }

    private static int CountChars(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    private static string TakeChars(string s, int n)
    {
        int count = 0, idx = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            if (count == n) break;
            count++;
            idx += rune.Utf16SequenceLength;
        }
        return s[..idx];
    }

    private static string TakeLastChars(string s, int n)
    {
        var runes = new List<string>(n);
        foreach (var rune in s.EnumerateRunes()) runes.Add(rune.ToString());
        var take = Math.Min(n, runes.Count);
        return string.Concat(runes.Skip(runes.Count - take));
    }

    /// <summary>把窗口路径规范化为正斜杠（输出给模型看时统一）。</summary>
    public static string ToPortablePath(string p) => p.Replace('\\', '/');
}
