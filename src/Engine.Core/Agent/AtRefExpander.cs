namespace HaloAgent.Engine.Core.Agent;

/// <summary>
/// 展开提示词里的 @引用。
/// 规则：@file → 注入文件内容（前 800 行，超 2MB 提示用 read_file）；
/// @dir/ → 注入目录清单（前 200 项）；@"含空格路径" 同上；
/// 不存在的路径保留原文并报告到 missing；a@b.com 不动（@ 前是字母数字算邮箱）。
/// </summary>
public static class AtRefExpander
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxShownLines = 800;
    private const int MaxDirEntries = 200;

    public static (string expanded, List<string> missing) Expand(string prompt, string cwd)
    {
        var output = new System.Text.StringBuilder(prompt.Length + 512);
        var missing = new List<string>();
        int i = 0;

        while (i < prompt.Length)
        {
            char c = prompt[i];
            if (c != '@')
            {
                output.Append(c);
                i++;
                continue;
            }

            // @"..." 形式
            if (i + 1 < prompt.Length && prompt[i + 1] == '"')
            {
                int j = i + 2;
                var path = new System.Text.StringBuilder();
                bool closed = false;
                while (j < prompt.Length)
                {
                    if (prompt[j] == '"') { closed = true; break; }
                    if (prompt[j] == '\n' || prompt[j] == '\r') break;
                    path.Append(prompt[j]);
                    j++;
                }

                if (closed && path.Length > 0)
                {
                    var expanded = ExpandOne(path.ToString(), cwd);
                    if (expanded is not null) output.Append(expanded).Append('\n');
                    else
                    {
                        missing.Add("@\"" + path.ToString() + "\"");
                        output.Append("@\"").Append(path.ToString()).Append('"');
                    }
                    i = j + 1;
                    continue;
                }

                output.Append('@');
                i++;
                continue;
            }

            // 普通 @path（到空白/标点为止）
            int k = i + 1;
            var sb = new System.Text.StringBuilder();
            while (k < prompt.Length)
            {
                var ch = prompt[k];
                if (char.IsWhiteSpace(ch) ||
                    ch == ',' || ch == '。' || ch == '；' || ch == ';' ||
                    ch == '」' || ch == ')' || ch == ']' || ch == '}' ||
                    ch == '：' || ch == ':')
                    break;
                sb.Append(ch);
                k++;
            }

            if (sb.Length == 0)
            {
                output.Append('@');
                i++;
                continue;
            }

            var refPath = sb.ToString();
            var content = ExpandOne(refPath, cwd);
            if (content is not null)
            {
                output.Append(content).Append('\n');
            }
            else
            {
                // 邮箱检测：@ 前是字母数字 → 不动
                bool isEmail = i > 0 && (char.IsLetterOrDigit(prompt[i - 1]) || prompt[i - 1] == '.' || prompt[i - 1] == '_' || prompt[i - 1] == '-');
                if (isEmail) output.Append('@').Append(refPath);
                else
                {
                    missing.Add("@" + refPath);
                    output.Append('@').Append(refPath);
                }
            }
            i = k;
        }

        return (output.ToString(), missing);
    }

    private static string? ExpandOne(string raw, string cwd)
    {
        var path = ResolvePath(raw, cwd);

        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                    return "[文件 " + path + " 过大(" + (info.Length / 1024 / 1024) + "MB)，请用 read_file 工具分页读取]";

                var lines = File.ReadAllLines(path);
                var shown = lines.Take(MaxShownLines);
                var sb = new System.Text.StringBuilder();
                sb.Append("【引用文件 ").Append(path).Append("】\n```\n");
                sb.Append(string.Join("\n", shown));
                if (lines.Length > MaxShownLines)
                    sb.Append("\n…(共 " + lines.Length + " 行，仅显示前 " + MaxShownLines + " 行)…");
                sb.Append("\n```");
                return sb.ToString();
            }

            if (Directory.Exists(path))
            {
                var entries = Directory.EnumerateFileSystemEntries(path)
                    .Take(MaxDirEntries)
                    .Select(p =>
                    {
                        var name = Path.GetFileName(p);
                        return Directory.Exists(p) ? name + "/" : name;
                    })
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return "【引用目录 " + path + " 的内容(前 " + entries.Count + " 项)】\n" +
                       string.Join("\n", entries);
            }
        }
        catch { }

        return null;
    }

    private static string ResolvePath(string raw, string cwd)
    {
        if (raw.StartsWith("~/") || raw.StartsWith("~\\"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, raw[2..]);
        }
        return Path.IsPathRooted(raw) ? raw : Path.Combine(cwd, raw);
    }
}
