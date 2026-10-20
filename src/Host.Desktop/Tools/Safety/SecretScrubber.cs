using System.Text.RegularExpressions;

namespace Host.Desktop.Tools.Safety;

/// <summary>
/// need22-P0 S5：落盘前敏感信息脱敏器（spill/备份审计/memory 提示统一口径）。
/// 命中任一模式即视为疑似密钥；脱敏为不可逆占位，原文不保留。
/// </summary>
public static class SecretScrubber
{
    private static readonly Regex[] SecretPatterns =
    {
        // OpenAI / 通用 sk- 密钥
        new(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        // Bearer Token
        new(@"Bearer\s+[A-Za-z0-9_\-\.~\+/=]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // AWS Access Key / Secret
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),
        new(@"aws_secret_access_key\s*[:=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 通用赋值形态：api_key / apikey / secret / password / passwd / token = 值
        new(@"(?i)(api[_-]?key|secret|password|passwd|pwd|access[_-]?token)\s*[:=]\s*['""]?[^\s'"";,}]+", RegexOptions.Compiled),
        // xox-（Slack）、ghp-/github_pat_（GitHub）、AIza（Google）
        new(@"xox[baprs]-[A-Za-z0-9\-]+", RegexOptions.Compiled),
        new(@"ghp_[A-Za-z0-9]+|github_pat_[A-Za-z0-9_]+", RegexOptions.Compiled),
        new(@"AIza[0-9A-Za-z_\-]{20,}", RegexOptions.Compiled),
    };

    /// <summary>是否命中任一密钥模式。</summary>
    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (Regex re in SecretPatterns)
        {
            try
            {
                if (re.IsMatch(text))
                {
                    return true;
                }
            }
            catch
            {
                // 单条正则异常不阻断整体判定
            }
        }

        return false;
    }

    /// <summary>
    /// 脱敏：命中片段替换为 [REDACTED_SECRET]（原文不保留）。
    /// 返回 (脱敏后文本, 命中次数)。
    /// </summary>
    public static (string Scrubbed, int Hits) Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text ?? string.Empty, 0);
        }

        int hits = 0;
        string result = text;
        foreach (Regex re in SecretPatterns)
        {
            try
            {
                result = re.Replace(result, m =>
                {
                    hits++;
                    return "[REDACTED_SECRET]";
                });
            }
            catch
            {
                // 忽略单条正则异常
            }
        }

        return (result, hits);
    }
}
