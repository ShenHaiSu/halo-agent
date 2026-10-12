# Stage 02 - 03：多 Provider/Model 配置体系、DPAPI 加密与自定义请求头变量引擎

> **所属阶段**：Stage 02
> **核心对应需求**：需求 #7（多 Provider 与多 Model 协议/上下文/输出 Token 配置）与需求 #8（Provider 自定义请求头与 `${session-hash}` 等预设变量）

---

## 一、 多 Provider 与 Model 领域模型设计（`ProviderModels.cs`）

```csharp
namespace Host.Desktop.Core.Models;

/// <summary>
/// 模型通信协议类型（严格对齐需求 #2 与 #7）
/// </summary>
public enum LlmProtocolType
{
    Chat = 0, // OpenAI Chat Completions 协议 (/v1/chat/completions)
    Resp = 1  // OpenAI Responses API 协议 (/v1/responses)
}

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
}

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
}

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
}
```

---

## 二、 Windows DPAPI 本机密钥加密（`SecretProtector.cs`）

为了避免 `providers.json` 中的 `ApiKey` 与代理密码以明文暴露在磁盘上，使用 Windows 7 SP1 / 10 / 11 全系内置的 **DPAPI (`ProtectedData`)**：

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Host.Desktop.Infrastructure.Storage;

public static class SecretProtector
{
    // 绑定应用级附加熵
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("HaloAgent.Win7Desktop.SecretSalt.v1");

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = ProtectedData.Protect(plainBytes, AppEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    public static string Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return string.Empty;
        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherBase64);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, AppEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // 若用户跨机器直接拷贝了配置文件导致当前用户解密失败，返回空串并提示重新输入 API Key
            return string.Empty;
        }
    }

    public static string MaskSecret(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (plainText.Length <= 8) return "********";
        return $"{plainText[..4]}****{plainText[^4..]}";
    }
}
```

---

## 三、 自定义请求头与预设变量解析引擎（`HeaderVariableResolver.cs`）

### 1. 业务背景与价值（需求 #8 深化）
许多大模型网关、聚合代理（如 OpenRouter、Cloudflare AI Gateway、企业内部推理网关、Anthropic/DeepSeek 缓存路由层）支持或强制要求在 HTTP Header 中传入固定会话哈希（如 `X-Session-Hash: ${session-hash}`、`prompt-cache-key: ${session-hash}` 或 `x-client-session-id: ${session-id}`），以便将同一会话的后续多轮 ReAct 请求路由到同一台 KV Cache 显存节点，从而将 **Prompt Cache 命中率提升至 80%+ 并大幅降低首字延迟与调用成本**。

### 2. 支持的标准预设变量清单
| 预设变量语法 | 解析值说明 | 示例输出值 | 典型适用场景 |
| :--- | :--- | :--- | :--- |
| **`${session-hash}`** | **当前会话的确定性 16 位小写 SHA-256 哈希值**（由 `SHA256(WorkspaceId + ":" + SessionId)[0..16]` 生成，会话存续期间永久固定不变） | `a94f8c2d1e07b35a` | 强制要求会话哈希路由、Prompt Cache 黏性负载均衡 |
| **`${session-id}`** | 当前会话的唯一 ID | `sess_01jpx8k9m2` | 会话级日志追踪与会话绑定 |
| **`${workspace-hash}`** | 当前工作路径（项目文件夹规范化绝对路径）的 16 位 SHA-256 哈希 | `7c3b1904e2a8d11f` | 项目级缓存分组或配额统计 |
| **`${workspace-name}`** | 当前工作区文件夹名称（ASCII 安全转义） | `halo-agent` | 按项目维度统计 Token 消耗 |
| **`${model-id}`** | 当前请求调用的 Model ID | `gpt-4o` | 网关动态路由标识 |
| **`${provider-name}`** | 当前 Provider 名称 | `MyGateway` | 多上游网关区分 |
| **`${request-id}`** | 本次单次 HTTP 请求生成的唯一 GUID（无连字符） | `4f82c190a7b34d219e018c72a1b4f902` | 请求幂等性校验与全链路排障 |
| **`${timestamp}`** | 当前 UTC Unix 秒级时间戳 | `1774857600` | 签名验证或防重放网关 |

### 3. 变量解析器核心实现
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Host.Desktop.Infrastructure.Network;

public sealed record HeaderResolutionContext(
    string WorkspaceId,
    string WorkspaceName,
    string WorkspacePath,
    string SessionId,
    string SessionHash,
    string ProviderName,
    string ModelId,
    string RequestId
);

public static class HeaderVariableResolver
{
    private static readonly Regex VariablePattern = new(@"\$\{([a-zA-Z0-9_-]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// 计算确定性 16 位小写 Hex 哈希（保证同一会话始终生成完全相同的哈希值）
    /// </summary>
    public static string ComputeDeterministicHash16(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string ResolveTemplate(string template, HeaderResolutionContext ctx)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${"))
            return template;

        return VariablePattern.Replace(template, match =>
        {
            string varName = match.Groups[1].Value.ToLowerInvariant();
            return varName switch
            {
                "session-hash" => ctx.SessionHash,
                "session-id" => ctx.SessionId,
                "workspace-hash" => ComputeDeterministicHash16(ctx.WorkspacePath.ToLowerInvariant()),
                "workspace-name" => Uri.EscapeDataString(ctx.WorkspaceName),
                "model-id" => ctx.ModelId,
                "provider-name" => Uri.EscapeDataString(ctx.ProviderName),
                "request-id" => ctx.RequestId,
                "timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                _ => match.Value // 未知变量保持原样，不破坏用户输入
            };
        });
    }

    /// <summary>
    /// 将解析后的自定义请求头安全注入到 HttpRequestMessage
    /// </summary>
    public static void ApplyCustomHeaders(
        HttpRequestMessage request,
        ProviderConfig provider,
        HeaderResolutionContext ctx)
    {
        if (!provider.EnableCustomHeaders || provider.CustomHeaders.Count == 0)
            return;

        foreach (var entry in provider.CustomHeaders)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Key))
                continue;

            string headerName = entry.Key.Trim();
            // 保护核心协议头不被误覆盖导致崩溃
            if (headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                headerName.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string resolvedValue = ResolveTemplate(entry.ValueTemplate, ctx);
            request.Headers.Remove(headerName);
            request.Headers.TryAddWithoutValidation(headerName, resolvedValue);
        }
    }
}
```

### 4. BaseUrl 智能归一化规则
用户输入 `BaseUrl` 时习惯各异（如输入 `https://api.openai.com`、`https://api.openai.com/` 或 `https://api.openai.com/v1`）。网关拼接端点时提供统一规范化逻辑：
* 若 `BaseUrl` 已以 `/v1` 或 `/v1/` 结尾：
  * `chat` 协议端点 = `{BaseUrl.TrimEnd('/')}/chat/completions`
  * `resp` 协议端点 = `{BaseUrl.TrimEnd('/')}/responses`
* 若 `BaseUrl` 以 `#` 结尾（高级强制原始路径模式）：去掉 `#` 后直接按用户填写的前缀拼接 `/chat/completions` 或 `/responses`。
* 其他情况默认自动补齐 `/v1/chat/completions` 或 `/v1/responses`。
