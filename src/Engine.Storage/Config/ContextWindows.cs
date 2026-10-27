namespace HaloAgent.Engine.Storage.Config;

public static class ContextWindows
{
    /// <summary>
    /// 按模型名匹配上下文窗口。
    /// **查不到返回 null（未知）**，不兜底 32k —— 猜错比没有更误事。
    /// </summary>
    public static int? ForModel(string model)
    {
        var m = model.ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(k => m.Contains(k, StringComparison.Ordinal));

        if (Has("claude")) return 200_000;
        if (Has("gpt-5")) return 400_000;
        if (Has("gpt-4.1")) return 1_048_576;
        if (Has("o4-mini", "o3", "o1")) return 200_000;
        if (Has("gpt-4o")) return 128_000;
        if (Has("gemini", "qwen-plus")) return 1_000_000;
        if (Has("qwen3-max", "qwen3.8-max")) return 1_000_000;
        if (Has("qwen-max")) return 32_768;
        if (Has("qwen3")) return 40_960;
        if (Has("deepseek-v4", "deepseek-flash")) return 1_000_000;
        if (Has("deepseek")) return 65_536;
        if (Has("llama3.1", "llama3.2")) return 131_072;
        if (Has("llama3")) return 8_192;
        return null;
    }
}
