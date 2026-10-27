namespace HaloAgent.Engine.Core.Agent.Prompts;

/// <summary>
/// 内置人格（4 个）。人格文案是纯文本文件内容，不含任何"系统指令"；
/// 引擎在系统提示里明确"人设不能覆盖安全边界"。
/// 用户可以删/改/新增 .md 文件，删掉即回退内置。
/// </summary>
public static class PersonaBuiltin
{
    private const string SuDuSunYou =
        "你现在是用户的「毒舌损友」: 说话直接、爱吐槽，但真心为用户好。\n" +
        "嘴上嫌弃，行动上绝不糊弄——给的方案必须是对的、能用的。\n" +
        "吐槽针对事情和方法，不针对人；用户明显在难受时收起嘴，正常说话。\n";

    private const string NaiXinLaoShi =
        "你现在是用户的「耐心老师」: 讲清原理再给步骤，一次只推进一步。\n" +
        "多用类比和最小可运行示例，避免术语堆叠。\n" +
        "用户卡住时先确认他卡在哪一步，而不是重复讲一遍。\n";

    private const string ReXueJiKe =
        "你现在是用户的「热血极客」: 语气带劲、爱用工程直觉，但绝不牺牲正确性。\n" +
        "优先给能跑起来的最短路径，然后说明后续能优化的方向。\n" +
        "适度使用感叹号和口语化表达，但结论要落在可执行的动作上。\n";

    private const string JiJianLengDan =
        "你现在是用户的「极简冷淡风」: 惜字如金，只给结论和必要步骤，不寒暄、不铺陈、不解释废话。\n" +
        "能用一行说清绝不说两行，直接命令式表达（比如「归档完成。用时 3s。」），不使用感叹号与热情语气。\n";

    private static readonly Dictionary<string, string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["毒舌损友"] = SuDuSunYou,
        ["耐心老师"] = NaiXinLaoShi,
        ["热血极客"] = ReXueJiKe,
        ["极简冷淡风"] = JiJianLengDan,
    };

    public static string? GetBuiltin(string name) =>
        Builtins.TryGetValue(name, out var v) ? v : null;

    public static IReadOnlyList<string> Names => Builtins.Keys.ToList();

    /// <summary>把内置人格物化到用户目录（缺失才写，不覆盖用户改动）。</summary>
    public static void EnsureFiles(string dataDir)
    {
        var dir = Path.Combine(dataDir, "personas");
        try { Directory.CreateDirectory(dir); } catch { return; }

        foreach (var (name, text) in Builtins)
        {
            var path = Path.Combine(dir, name + ".md");
            if (File.Exists(path)) continue;
            try { File.WriteAllText(path, text + "\n"); } catch { }
        }
    }

    /// <summary>列出可用人格（用户目录 + 内置，去重）。</summary>
    public static List<string> List(string dataDir)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        var dir = Path.Combine(dataDir, "personas");
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
        }

        foreach (var n in Builtins.Keys) names.Add(n);
        return names.ToList();
    }
}
