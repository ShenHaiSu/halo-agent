namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// Shell 命令词法分析器。
///
/// ============ 为什么不能简单按空白切 ============
/// 需要正确处理：
///   - 单引号（内部不转义，原样保留）
///   - 双引号（内部 \ 转义下一个字符）
///   - 引号外的 \ 转义
///   - 重定向操作符（&gt; &gt;&gt; &lt; &lt;&lt; 2&gt; &amp;&gt;）要作为独立 token 保留
///     —— 因为 "echo x &gt; /dev/sda" 的危险目标是 /dev/sda
///   - 命令分隔符（; &amp;&amp; || | &amp; 换行）要切段
///
/// ============ 不做的事 ============
///   - 变量展开（在 ExpandVariables 里做，因为需要先收集赋值）
///   - 命令替换 $( ) 的解析（整体留在词里，后续按"含变量"处理 → Uncertain）
///   - 通配符展开（不碰文件系统；判定时按"前缀目录"保守处理）
/// </summary>
public static class ShellLexer
{
    /// <summary>
    /// 切成"简单命令段"。每段是一个词数组。
    /// 分隔符：; &amp;&amp; || | &amp; 换行
    /// </summary>
    public static List<List<string>> SplitSegments(string command)
    {
        var segments = new List<List<string>>();
        var current = new List<string>();
        var word = new System.Text.StringBuilder();
        char? quote = null;
        int i = 0;

        void FlushWord()
        {
            if (word.Length > 0)
            {
                current.Add(word.ToString());
                word.Clear();
            }
        }

        void FlushSegment()
        {
            FlushWord();
            if (current.Count > 0)
            {
                segments.Add(new List<string>(current));
                current.Clear();
            }
        }

        while (i < command.Length)
        {
            char c = command[i];

            // ---- 引号内 ----
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                    i++;
                    continue;
                }
                // 双引号内 \ 转义
                if (quote == '"' && c == '\\' && i + 1 < command.Length)
                {
                    word.Append(command[i + 1]);
                    i += 2;
                    continue;
                }
                word.Append(c);
                i++;
                continue;
            }

            // ---- 引号外 ----
            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    i++;
                    break;

                 case '\\':
                     // Windows 上反斜杠是路径分隔符，不是转义符（cmd / PowerShell 都不用它转义），
                     // 必须原样保留，否则 `C:\build\*` 会被吃成 `C:build*` 导致目标判定错位。
                     // POSIX 语义（转义）只在非 Windows 上保留。
                     if (OperatingSystem.IsWindows())
                     {
                         word.Append('\\');
                         i++;
                         break;
                     }

                     if (i + 1 < command.Length)
                     {
                         word.Append(command[i + 1]);
                         i += 2;
                     }
                     else
                     {
                         // 末尾孤立反斜杠：保留
                         word.Append('\\');
                         i++;
                     }
                     break;

                case ' ':
                case '\t':
                case '\r':
                    FlushWord();
                    i++;
                    break;

                case '\n':
                case ';':
                    FlushSegment();
                    i++;
                    break;

                case '|':
                    // || 与 | 都切段
                    if (i + 1 < command.Length && command[i + 1] == '|') i += 2;
                    else i++;
                    FlushSegment();
                    break;

                case '&':
                    // && 与 & 都切段
                    if (i + 1 < command.Length && command[i + 1] == '&') i += 2;
                    else i++;
                    FlushSegment();
                    break;

                case '>':
                case '<':
                {
                    // 重定向操作符独立成词（目标要看）
                    FlushWord();
                    var op = new System.Text.StringBuilder();
                    op.Append(c);
                    i++;
                    // 吸收连续的 > < | 与紧随的数字（2> 中的 2 实际上在前面，这里处理 >> 与 >| ）
                    while (i < command.Length &&
                           (command[i] == c || (command[i] == '|' && c == '>')))
                    {
                        op.Append(command[i]);
                        i++;
                    }
                    current.Add(op.ToString());
                    break;
                }

                default:
                    word.Append(c);
                    i++;
                    break;
            }
        }

        FlushSegment();
        return segments;
    }

    /// <summary>
    /// 收集同一命令串内的字面变量赋值（`T=/; rm -rf $T` 这种）。
    /// 值含 `$` 或反引号则忽略（无法静态确定）。
    /// 同时预置 HOME 与 PWD。
    /// </summary>
    public static Dictionary<string, string> CollectAssignments(
        List<List<string>> segments, string cwd, string? home)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(home))
            vars["HOME"] = home;
        vars["PWD"] = cwd;

        foreach (var seg in segments)
        {
            foreach (var w in seg)
            {
                var eq = w.IndexOf('=');
                if (eq <= 0 || eq == w.Length - 1) continue;

                var name = w[..eq];
                var value = w[(eq + 1)..];

                if (!IsValidVarName(name)) continue;
                if (value.Contains('$') || value.Contains('`')) continue;

                vars[name] = value;
            }
        }
        return vars;
    }

    /// <summary>
    /// 展开 `$VAR` / `${VAR}`。
    /// 未知变量**原样保留**（后续 classifyTarget 见到 `$` 会判 Uncertain）。
    /// </summary>
    public static string ExpandVariables(string word, Dictionary<string, string> vars)
    {
        if (!word.Contains('$')) return word;

        var sb = new System.Text.StringBuilder(word.Length);
        int i = 0;
        while (i < word.Length)
        {
            char c = word[i];
            if (c != '$')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // ${VAR} 形式
            if (i + 1 < word.Length && word[i + 1] == '{')
            {
                int close = word.IndexOf('}', i + 2);
                if (close < 0)
                {
                    // 未闭合：原样保留
                    sb.Append(word[i..]);
                    break;
                }
                var name = word[(i + 2)..close];
                sb.Append(vars.TryGetValue(name, out var v) ? v : "$" + "{" + name + "}");
                i = close + 1;
                continue;
            }

            // $VAR 形式
            int j = i + 1;
            var nameSb = new System.Text.StringBuilder();
            while (j < word.Length && IsVarNameChar(word[j], nameSb.Length == 0))
            {
                nameSb.Append(word[j]);
                j++;
            }

            if (nameSb.Length == 0)
            {
                // $? $$ $1 之类：原样保留
                sb.Append('$');
                i++;
                continue;
            }

            var varName = nameSb.ToString();
            sb.Append(vars.TryGetValue(varName, out var val) ? val : "$" + varName);
            i = j;
        }
        return sb.ToString();
    }

    private static bool IsValidVarName(string s)
    {
        if (s.Length == 0) return false;
        if (!(IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        for (int i = 1; i < s.Length; i++)
            if (!IsVarNameChar(s[i], isFirst: false)) return false;
        return true;
    }

    private static bool IsVarNameChar(char c, bool isFirst) =>
        isFirst ? (IsAsciiLetter(c) || c == '_')
                : (IsAsciiLetterOrDigit(c) || c == '_');

    // .NET 6 没有 char.IsAsciiLetter / IsAsciiLetterOrDigit（.NET 7+），手写判断。
    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
    private static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || IsAsciiDigit(c);
}
