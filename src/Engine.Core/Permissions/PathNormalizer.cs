namespace HaloAgent.Engine.Core.Permissions;

/// <summary>
/// 目标路径判定结果。
///
/// 与"是否危险"分开：这里只回答"这个路径是什么"，
/// 危险判定在 DangerousCommandInspector 里做。
/// </summary>
public abstract record PathResolution
{
    /// <summary>成功解析出规范化路径</summary>
    public sealed record Ok(string Path) : PathResolution;

    /// <summary>无法确定（含未知变量 / ~user / 通配符+.. / 无法解析）</summary>
    public sealed record Unknown(string Reason) : PathResolution;

    public sealed record Invalid(string Reason) : PathResolution;
}

public static class PathNormalizer
{
    /// <summary>
    /// 把命令里的目标词解析为规范化绝对路径。
    ///
    /// 规则：
    ///   1. 含 $ 或 ` → Unknown（未展开的变量/命令替换）
    ///   2. ~user（非 ~ 或 ~/）→ Unknown（无法确定家目录）
    ///   3. 展开 ~ → home
    ///   4. 相对路径 → join(cwd)
    ///   5. 词法折叠 . / .. / 多斜杠（不碰文件系统）
    ///
    /// 通配符处理（重要）：
    ///   - 含 * ? [：
    ///       - 若同时含 .. → Unknown（通配符可能爬出去）
    ///       - 否则取"通配符之前的部分"作为目录，返回该目录
    ///         （通配符不匹配 . 或 ..，所以安全目录下的通配符爬不出去）
    /// </summary>
    public static PathResolution Resolve(string raw, string cwd, string? home)
    {
        var t = raw.Trim();
        if (t.Length == 0) return new PathResolution.Invalid("空路径");

        // ① 未展开的变量 / 命令替换
        if (t.Contains('$') || t.Contains('`'))
            return new PathResolution.Unknown("含未解析的变量或命令替换");

        // ② ~user 形式
        if (t.StartsWith('~') && t != "~" && !t.StartsWith("~/") && !t.StartsWith("~\\"))
            return new PathResolution.Unknown("含 ~user 形式，无法确定家目录");

        // ③ 通配符
        bool hasGlob = t.IndexOfAny(new[] { '*', '?', '[' }) >= 0;
        if (hasGlob)
        {
            if (t.Split('/', '\\').Any(seg => seg == ".."))
                return new PathResolution.Unknown("通配符与 .. 混用，无法确定最终目标");

            // 取第一个通配符之前的部分作为"目录前缀"
            int firstGlob = t.IndexOfAny(new[] { '*', '?', '[' });
            var basePrefix = t[..firstGlob];
            if (basePrefix.Length == 0 || basePrefix.EndsWith('/') || basePrefix.EndsWith('\\'))
            {
                // "/*" 或 "~/x/*" 形式：目录就是前缀本身
                var dir = ResolveNoGlob(basePrefix.TrimEnd('/', '\\'), cwd, home);
                return dir;
            }
            // "abc*" 形式：目录是它的父目录
            var parent = System.IO.Path.GetDirectoryName(basePrefix);
            return ResolveNoGlob(parent ?? ".", cwd, home);
        }

        // ④ 普通路径
        return ResolveNoGlob(t, cwd, home);
    }

    private static PathResolution ResolveNoGlob(string raw, string cwd, string? home)
    {
        var t = raw.Trim();
        if (t.Length == 0) t = ".";

        // 展开 ~
        string expanded;
        if (t == "~")
        {
            if (home is null) return new PathResolution.Unknown("无法确定家目录");
            expanded = home;
        }
        else if (t.StartsWith("~/") || t.StartsWith("~\\"))
        {
            if (home is null) return new PathResolution.Unknown("无法确定家目录");
            expanded = System.IO.Path.Combine(home, t[2..]);
        }
        else
        {
            expanded = t;
        }

        // 绝对化
        string absolute;
        try
        {
            absolute = System.IO.Path.IsPathRooted(expanded)
                ? expanded
                : System.IO.Path.Combine(cwd, expanded);
        }
        catch (Exception ex)
        {
            return new PathResolution.Invalid($"路径拼接失败：{ex.Message}");
        }

        // 词法折叠
        var normalized = LexicalNormalize(absolute);
        return new PathResolution.Ok(normalized);
    }

    /// <summary>
    /// 纯词法折叠：不访问文件系统，不做符号链接解析。
    ///
    /// 与 Path.GetFullPath 的区别：
    ///   - GetFullPath 会访问文件系统（在 Windows 上可能触发 UNC 解析、长路径抛出）
    ///   - 这里只做字符串级别的 . / .. 折叠，确定且快速
    /// </summary>
    public static string LexicalNormalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        bool isWindows = OperatingSystem.IsWindows();
        char sep = isWindows ? '\\' : '/';

        // 处理盘符（C:）
        string? root = null;
        string rest;
        if (isWindows && path.Length >= 2 && path[1] == ':')
        {
            root = path[..2].ToUpperInvariant();   // "C:"
            rest = path.Length > 2 ? path[2..] : "";
        }
        else if (path.StartsWith('/') || path.StartsWith('\\'))
        {
            root = sep.ToString();
            rest = path[1..];
        }
        else
        {
            rest = path;
        }

        var parts = new List<string>();
        foreach (var seg in rest.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                // 到根再 .. 就停在根（POSIX 语义）
                continue;
            }
            parts.Add(seg);
        }

        var joined = string.Join(sep, parts);
        if (root is null) return joined;
        if (string.IsNullOrEmpty(joined)) return root;
        return root.EndsWith(sep) ? root + joined : root + sep + joined;
    }

     /// <summary>比较两个规范化路径是否相同（Windows 上忽略 `/` 与 `\` 的写法差异）。</summary>
     public static bool PathEquals(string a, string b)
     {
         if (OperatingSystem.IsWindows())
         {
             // Windows 同时接受两种分隔符；调用方可能混用（如 Linux 风格的 Cwd）。
             a = a.Replace('/', '\\');
             b = b.Replace('/', '\\');
             return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
         }

         return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
     }

     /// <summary>
     /// 判断 path 是否等于 basePath 或在其**上级**（即 basePath 是 path 的前缀）。
     /// 用于"目标是否包含当前工作目录"这类判定。
     /// </summary>
     public static bool IsSameOrAncestor(string path, string basePath)
     {
         if (OperatingSystem.IsWindows())
         {
             path = path.Replace('/', '\\');
             basePath = basePath.Replace('/', '\\');
             var pp = path.TrimEnd('\\');
             var bp = basePath.TrimEnd('\\');
             if (bp.Length == 0) return false;
             if (pp.Equals(bp, StringComparison.OrdinalIgnoreCase)) return true;
             return pp.StartsWith(bp + "\\", StringComparison.OrdinalIgnoreCase);
         }

         var p = path.TrimEnd('/');
         var b = basePath.TrimEnd('/');
         if (b.Length == 0) return false;
         if (p.Equals(b, StringComparison.Ordinal)) return true;
         return p.StartsWith(b + "/", StringComparison.Ordinal);
     }
}
