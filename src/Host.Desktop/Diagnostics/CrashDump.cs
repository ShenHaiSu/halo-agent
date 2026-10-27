using System.Text;

namespace HaloAgent.Desktop.Diagnostics;

/// <summary>把未处理异常写成便于阅读的文本（不用 minidump：C# 逻辑异常堆栈+环境就够）。
/// 自动产生的报告不含用户名/会话内容/API Key。</summary>
public static class CrashDump
{
    private static readonly object Gate = new();

    public static string Write(Exception? ex)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloAgent", "crashes");
        var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
        var sb = new StringBuilder();
        sb.AppendLine("=========== HaloAgent 崩溃报告 ===========");
        sb.AppendLine($"时间       : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"版本       : {BuildInfo.Version}");
        sb.AppendLine($"进程位数   : {(Environment.Is64BitProcess ? "64" : "32")}");
        sb.AppendLine($"OS         : {Environment.OSVersion}");
        sb.AppendLine($"程序目录   : {AppContext.BaseDirectory}");
        sb.AppendLine($"工作目录   : {Environment.CurrentDirectory}");
        sb.AppendLine($"托管内存   : {GC.GetTotalMemory(false) / 1024 / 1024} MB");
        sb.AppendLine();
        if (ex is null) sb.AppendLine("（没有异常对象）");
        else { sb.AppendLine("---------- 异常 ----------"); AppendException(sb, ex, 0); }
        var text = sb.ToString();
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return "(无法写入崩溃报告)";
            }
        }
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}类型: {ex.GetType().FullName}");
        sb.AppendLine($"{indent}消息: {ex.Message}");
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 1)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine($"{indent}--- 内部异常 ---");
                AppendException(sb, inner, depth + 1);
            }
        }
        else if (ex.InnerException is not null)
        {
            sb.AppendLine($"{indent}--- 内部异常 ---");
            AppendException(sb, ex.InnerException, depth + 1);
        }
        sb.AppendLine($"{indent}堆栈:");
        foreach (var line in (ex.StackTrace ?? "(无)").Split('\n'))
            sb.AppendLine($"{indent}  {line.TrimEnd()}");
        sb.AppendLine();
    }
}
