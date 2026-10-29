namespace HaloAgent.Engine.Storage.Config;

/// <summary>P10 种子：新文件首次创建时写入，不再与用户层做字段合并。</summary>
public static class BuiltinProviders
{
    public static Dictionary<string, ProviderConfig> Seeds() => new(StringComparer.Ordinal)
    {
        ["ollama"] = new ProviderConfig
        {
            DisplayName = "Ollama 本地",
            BaseUrl = "http://localhost:11434/v1",
            ApiKey = null,
            UseProxy = false,
            Models = new List<ModelConfig>
            {
                new() { Id = "qwen3:8b", Protocol = "chat" },
            },
        },
        ["dashscope"] = new ProviderConfig
        {
            DisplayName = "DashScope",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            UseProxy = true,
            Models = new List<ModelConfig>
            {
                new() { Id = "qwen-plus", Protocol = "chat" },
            },
        },
        ["deepseek"] = new ProviderConfig
        {
            DisplayName = "DeepSeek",
            BaseUrl = "https://api.deepseek.com/v1",
            UseProxy = true,
            Models = new List<ModelConfig>
            {
                new() { Id = "deepseek-chat", Protocol = "chat" },
            },
        },
        ["openrouter"] = new ProviderConfig
        {
            DisplayName = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            UseProxy = true,
            Models = new List<ModelConfig>
            {
                new() { Id = "qwen/qwen3-8b", Protocol = "chat" },
            },
        },
        ["zai"] = new ProviderConfig
        {
            DisplayName = "ZAI",
            BaseUrl = "https://api.z.ai/api/v1",
            UseProxy = true,
            Models = new List<ModelConfig>
            {
                new() { Id = "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B", Protocol = "chat" },
            },
        },
    };

    /// <summary>兼容旧调用：返回种子（不再合并）。</summary>
    public static Dictionary<string, ProviderConfig> All() => Seeds();
}
