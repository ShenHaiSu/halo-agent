namespace HaloAgent.Engine.Storage.Config;

public static class BuiltinProviders
{
    public static Dictionary<string, ProviderDef> All() => new(StringComparer.Ordinal)
    {
        ["ollama"] = new()
        {
            BaseUrl = "http://localhost:11434/v1",
            Model = "qwen3:8b",
        },
        ["dashscope"] = new()
        {
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen-plus",
            ApiKeyEnv = "DASHSCOPE_API_KEY",
        },
        ["deepseek"] = new()
        {
            BaseUrl = "https://api.deepseek.com/v1",
            Model = "deepseek-chat",
            ApiKeyEnv = "DEEPSEEK_API_KEY",
        },
        ["openrouter"] = new()
        {
            BaseUrl = "https://openrouter.ai/api/v1",
            Model = "qwen/qwen3-8b",
            ApiKeyEnv = "OPENROUTER_API_KEY",
        },
        ["zai"] = new()
        {
            BaseUrl = "https://api.z.ai/api/v1",
            Model = "deepseek-ai/DeepSeek-R1-Distill-Qwen-32B",
            ApiKeyEnv = "ZAI_API_KEY",
        },
    };
}
