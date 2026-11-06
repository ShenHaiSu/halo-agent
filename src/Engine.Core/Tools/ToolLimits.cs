namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 工具限额常量。只此一份。
///
/// ============ 硬约束 ============
/// 这些数字会插值进 Description（模型看到的）和错误文案（模型读到的），
/// 严禁在别处再写一遍字面量。改这里 = 改行为 + 改文案，不会漂移。
///
/// ============ 为什么按「字符」而不是「字节」============
/// 按字节截断会切坏中文/emoji（产生 U+FFFD）。与 TextUtil 的按 Rune 截断保持一致。
/// </summary>
public static class ToolLimits
{
     // ---------- read_file ----------
     /// <summary>单次读取的默认行数上限。</summary>
     public const int ReadDefaultMaxLines = 350;
     /// <summary>单次读取的字符上限（超出则尾部省略并给续读提示）。</summary>
    public const int ReadMaxChars = 50_000;
    /// <summary>单行超过该字符数时单独截断并提示。</summary>
    public const int ReadMaxLineChars = 2000;

    // ---------- grep_search ----------
    /// <summary>默认返回条数。</summary>
    public const int GrepDefaultLimit = 100;
    /// <summary>允许的最大条数（模型可上调，但不超过此值）。</summary>
    public const int GrepMaxLimit = 500;
    /// <summary>单行命中内容的最大字符数。</summary>
    public const int GrepMaxLineChars = 500;
    /// <summary>单文件参与扫描的最大字节数。</summary>
    public const int GrepMaxFileBytes = 8 * 1024 * 1024;
     /// <summary>正则匹配超时（秒），防灾难性回溯。</summary>
     public const int GrepRegexTimeoutSeconds = 5;
     /// <summary>命中上下文行上限（前后各最多 N 行，防 token 爆炸）。</summary>
     public const int GrepMaxContextLines = 5;

    // ---------- glob ----------
    public const int GlobDefaultLimit = 1000;
    public const int GlobMaxLimit = 5000;

    // ---------- list_directory ----------
    public const int LsDefaultLimit = 500;
    public const int LsMaxLimit = 2000;

    // ---------- run_shell_command ----------
    /// <summary>回灌给模型的正文上限（保留头尾）。</summary>
    public const int ShellContextMaxChars = 20_000;
    /// <summary>被杀时回灌的「死前遗言」尾部字符数。</summary>
    public const int ShellDeathTailChars = 1500;

    // ---------- web_fetch ----------
    public const int WebFetchDefaultMaxChars = 8000;
    public const int WebFetchMaxChars = 200_000;
}
