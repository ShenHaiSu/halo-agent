using System.Text.Json;
using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>
/// 模型通信协议类型（严格对齐需求 #2 与 #7：chat -> /v1/chat/completions, resp -> /v1/responses）
/// </summary>
[JsonConverter(typeof(LlmProtocolTypeJsonConverter))]
public enum LlmProtocolType
{
    Chat = 0, // OpenAI Chat Completions 协议 (/v1/chat/completions)
    Resp = 1  // OpenAI Responses API 协议 (/v1/responses)
}

/// <summary>
/// 支持大小写无关的 "chat" / "resp" 字符串及 0 / 1 数值反序列化，默认输出小写 "chat" / "resp"
/// </summary>
public sealed class LlmProtocolTypeJsonConverter : JsonConverter<LlmProtocolType>
{
    public override LlmProtocolType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? raw = reader.GetString()?.Trim().ToLowerInvariant();
            return raw switch
            {
                "resp" or "responses" or "response" => LlmProtocolType.Resp,
                _ => LlmProtocolType.Chat
            };
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int num))
        {
            return num == 1 ? LlmProtocolType.Resp : LlmProtocolType.Chat;
        }

        return LlmProtocolType.Chat;
    }

    public override void Write(Utf8JsonWriter writer, LlmProtocolType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == LlmProtocolType.Resp ? "resp" : "chat");
    }
}

/// <summary>
/// 单个大模型参数配置
/// </summary>
public sealed class ModelConfig
{
    /// <summary>
    /// 模型调用标识，例如 "gpt-4o", "o3-mini", "deepseek-chat"
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 界面展示别名（可选，为空则显示 Id）
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型：Chat ("chat") 或 Resp ("resp")
    /// </summary>
    public LlmProtocolType Protocol { get; set; } = LlmProtocolType.Chat;

    /// <summary>
    /// 最大上下文窗口 Token 数（用于上下文预算与裁剪，默认 128,000）
    /// </summary>
    public int MaxContextTokens { get; set; } = 128000;

    /// <summary>
    /// 单次回复最大输出 Token 数（用于请求体 max_tokens / max_output_tokens，默认 4,096）
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// 默认采样温度（可选，部分推理模型可留空）
    /// </summary>
    public double? Temperature { get; set; } = 0.2;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// need09-03：是否支持视觉输入（多模态看图）。默认 false（纯文本模型）。
    /// </summary>
    public bool SupportsVision { get; set; } = false;

    /// <summary>
    /// need09-03：每轮最多发送图片数。0 = 默认 <see cref="VisionDefaults.DefaultMaxImages"/>（注意：与 need08 的 0=无限不同，图片无限会撑爆上下文）。
    /// 合法值 0..16，超限拒绝 PROVIDER_VALIDATION。
    /// </summary>
    public int MaxImagesPerTurn { get; set; } = 4;

    /// <summary>
    /// need09-03：图片清晰度透传（chat image_url.detail）。auto|low|high，缺失/非法网关回退 high（不拒绝，原图 base64 直发不压缩）。
    /// </summary>
    public string? VisionDetail { get; set; } = "high";
     /// <summary>
     /// 历史是否携带思考链：true=assistant.ReasoningContent 随上下文发出（默认开；空则省略零抖动）。
     /// </summary>
     public bool IncludeReasoningInHistory { get; set; } = true;

    public ModelConfig Clone()
    {
        return new ModelConfig
        {
            Id = Id,
            DisplayName = DisplayName,
            Protocol = Protocol,
            MaxContextTokens = MaxContextTokens,
            MaxOutputTokens = MaxOutputTokens,
            Temperature = Temperature,
            Enabled = Enabled,
            SupportsVision = SupportsVision,
            MaxImagesPerTurn = MaxImagesPerTurn,
            VisionDetail = VisionDetail,
            IncludeReasoningInHistory = IncludeReasoningInHistory
        };
    }
}

/// <summary>
/// need09-03：视觉能力位默认值与归一化（前后端同构，见 docs/need09/03 §1.3）。
/// </summary>
public static class VisionDefaults
{
    /// <summary>默认每轮图片数（MaxImagesPerTurn=0 即此值，非无限）。</summary>
    public const int DefaultMaxImages = 4;
    /// <summary>每轮图片数上限（含 0 哨兵在内合法区间为 0..16）。</summary>
    public const int MaxAllowedImages = 16;
    public const string DefaultDetail = "high";

    /// <summary>0/负数 → 默认 4；正数原样返回（上限由校验层拒绝，不在此钳制）。</summary>
    public static int ResolveMaxImages(int configured) => configured <= 0 ? DefaultMaxImages : configured;

    /// <summary>auto|low|high 通过（大小写不敏感，显式 auto 保留），缺失/非法一律回退 high（不拒绝）。</summary>
    public static string NormalizeDetail(string? detail)
    {
        string d = (detail ?? string.Empty).Trim().ToLowerInvariant();
        return d switch
        {
            "low" => "low",
            "high" => "high",
            "auto" => "auto",
            _ => "high",
        };
    }
}

/// <summary>
/// need09-03：服务商/模型校验失败（错误码 PROVIDER_VALIDATION）。
/// </summary>
public sealed class ProviderConfigValidationException : Exception
{
    public ProviderConfigValidationException(string field, string message)
        : base(message)
    {
        Field = field ?? string.Empty;
        Code = "PROVIDER_VALIDATION";
    }

    public string Field { get; }
    public string Code { get; }
}

/// <summary>
/// 自定义请求头条目（支持 ${session-hash} 等预设变量）
/// </summary>
public sealed class CustomHeaderEntry
{
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 支持包含预设变量的模板字符串，如 "sess_${session-hash}"
    /// </summary>
    public string ValueTemplate { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 若勾选为敏感头，日志审计时自动掩码脱敏
    /// </summary>
    public bool IsSecret { get; set; } = false;

    public CustomHeaderEntry Clone()
    {
        return new CustomHeaderEntry
        {
            Key = Key,
            ValueTemplate = ValueTemplate,
            Enabled = Enabled,
            IsSecret = IsSecret
        };
    }
}

/// <summary>
/// 大模型服务商配置（持久化于 config/providers.json）
/// </summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "OpenAI Compatible";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 基础 URL，例如 "https://api.openai.com/v1"
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// 经 Windows DPAPI 加密后的 Base64 密文（落盘至 providers.json）
    /// </summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    /// <summary>
    /// 前端编辑时传入的明文 API Key（落盘前会被 DPAPI 加密至 ApiKeyEncrypted 并置空，绝不落盘明文）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 供前端展示的脱敏掩码（如 sk-1****9876，落盘时忽略）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyMasked { get; set; }

    /// <summary>
    /// 标识当前是否已配置有效 API Key（供前端只读判断，落盘时忽略）
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasApiKey { get; set; }

    /// <summary>
    /// 代理模式：默认 Inherit（继承全局代理配置）
    /// </summary>
    public ProviderProxyMode ProxyMode { get; set; } = ProviderProxyMode.Inherit;

    /// <summary>
    /// 当 ProxyMode == Custom 时生效的独立代理配置
    /// </summary>
    public ManualProxyConfig? CustomProxy { get; set; }

    /// <summary>
    /// 是否启用自定义请求头
    /// </summary>
    public bool EnableCustomHeaders { get; set; } = false;

    /// <summary>
    /// 自定义请求头列表
    /// </summary>
    public List<CustomHeaderEntry> CustomHeaders { get; set; } = new();

    /// <summary>
    /// 该 Provider 下的可用模型列表
    /// </summary>
    public List<ModelConfig> Models { get; set; } = new();

    public ProviderConfig Clone()
    {
        return new ProviderConfig
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            BaseUrl = BaseUrl,
            ApiKeyEncrypted = ApiKeyEncrypted,
            ApiKey = ApiKey,
            ApiKeyMasked = ApiKeyMasked,
            HasApiKey = HasApiKey,
            ProxyMode = ProxyMode,
            CustomProxy = CustomProxy?.Clone(),
            EnableCustomHeaders = EnableCustomHeaders,
            CustomHeaders = CustomHeaders.Select(h => h.Clone()).ToList(),
            Models = Models.Select(m => m.Clone()).ToList()
        };
    }

    /// <summary>
    /// 获取解密后的明文 API Key（优先读取内存中的 ApiKey，若为空则通过 DPAPI 解密 ApiKeyEncrypted）
    /// </summary>
    public string GetEffectiveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(ApiKeyEncrypted))
        {
            return Infrastructure.Storage.SecretProtector.Unprotect(ApiKeyEncrypted);
        }

        return string.Empty;
    }
}

/// <summary>
/// providers.json 根文档结构
/// </summary>
public sealed class ProvidersConfigDocument
{
    public string ActiveProviderId { get; set; } = string.Empty;

    public string ActiveModelId { get; set; } = string.Empty;

    public List<ProviderConfig> Providers { get; set; } = new();
}
