using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Network;

/// <summary>
/// 自定义请求头预设变量解析上下文
/// </summary>
public sealed record HeaderResolutionContext(
    string WorkspaceId,
    string WorkspaceName,
    string WorkspacePath,
    string SessionId,
    string SessionHash,
    string ProviderId,
    string ProviderName,
    string ModelId,
    string RequestId
);

/// <summary>
/// 自定义请求头变量解析引擎与 BaseUrl 智能归一化工具（覆盖需求 #7 与 #8）。
/// 支持 ${session-hash}、${session-id}、${workspace-hash}、${workspace-name}、${model-id}、${provider-name}、${provider-id}、${request-id}、${timestamp}、${timestamp-ms}、${os-version}。
/// </summary>
public static class HeaderVariableResolver
{
    private static readonly Regex VariablePattern =
        new(@"\$\{([a-zA-Z0-9_-]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// 计算确定性 16 位小写 Hex SHA-256 哈希值（保证同一输入始终生成完全相同的 16 位小写十六进制哈希）
    /// </summary>
    public static string ComputeDeterministicHash16(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 计算指定工作区与会话 ID 的确定性 16 位小写会话哈希（SHA256(WorkspaceId + ":" + SessionId)[0..16]）
    /// </summary>
    public static string ComputeSessionHash(string workspaceId, string sessionId)
    {
        string normalizedWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? "default-workspace"
            : workspaceId.Trim();
        string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? "default-session"
            : sessionId.Trim();
        return ComputeDeterministicHash16($"{normalizedWorkspaceId}:{normalizedSessionId}");
    }

    /// <summary>
    /// 构建标准请求头变量解析上下文（每次调用默认生成全新的单次请求 RequestId，而同一会话的 SessionHash 保持恒定）
    /// </summary>
    public static HeaderResolutionContext CreateContext(
        string? workspacePath = null,
        string? sessionId = null,
        string? providerId = null,
        string? providerName = null,
        string? modelId = null,
        string? requestId = null,
        string? workspaceId = null
    )
    {
        string resolvedWorkspacePath = string.IsNullOrWhiteSpace(workspacePath)
            ? Environment.CurrentDirectory
            : workspacePath.Trim();

        string normalizedPathForHash = resolvedWorkspacePath
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();
        string resolvedWorkspaceId = !string.IsNullOrWhiteSpace(workspaceId)
            ? workspaceId.Trim()
            : ComputeDeterministicHash16(normalizedPathForHash);

        string folderName = Path.GetFileName(resolvedWorkspacePath.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = "workspace";
        }

        string resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? "sess_default"
            : sessionId.Trim();

        string sessionHash = ComputeSessionHash(resolvedWorkspaceId, resolvedSessionId);

        return new HeaderResolutionContext(
            WorkspaceId: resolvedWorkspaceId,
            WorkspaceName: folderName,
            WorkspacePath: resolvedWorkspacePath,
            SessionId: resolvedSessionId,
            SessionHash: sessionHash,
            ProviderId: string.IsNullOrWhiteSpace(providerId)
                ? "provider_default"
                : providerId.Trim(),
            ProviderName: string.IsNullOrWhiteSpace(providerName)
                ? "DefaultProvider"
                : providerName.Trim(),
            ModelId: string.IsNullOrWhiteSpace(modelId) ? "gpt-4o" : modelId.Trim(),
            RequestId: string.IsNullOrWhiteSpace(requestId)
                ? Guid.NewGuid().ToString("N")
                : requestId.Trim()
        );
    }

    /// <summary>
    /// 解析包含预设变量（如 ${session-hash}）的请求头模板字符串
    /// </summary>
    public static string ResolveTemplate(string template, HeaderResolutionContext ctx)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
        {
            return template ?? string.Empty;
        }

        return VariablePattern.Replace(
            template,
            match =>
            {
                string varName = match.Groups[1].Value.ToLowerInvariant();
                string resolvedSessionHash = !string.IsNullOrWhiteSpace(ctx.SessionHash)
                    ? ctx.SessionHash
                    : ComputeSessionHash(ctx.WorkspaceId, ctx.SessionId);

                return varName switch
                {
                    "session-hash" => resolvedSessionHash,
                    "session-id" => ctx.SessionId,
                    "workspace-hash"
                        => ComputeDeterministicHash16(
                            (ctx.WorkspacePath ?? string.Empty)
                                .Replace('/', '\\')
                                .TrimEnd('\\')
                                .ToLowerInvariant()
                        ),
                    "workspace-name" => Uri.EscapeDataString(ctx.WorkspaceName ?? string.Empty),
                    "model-id" => ctx.ModelId ?? string.Empty,
                    "provider-id" => Uri.EscapeDataString(ctx.ProviderId ?? string.Empty),
                    "provider-name" => Uri.EscapeDataString(ctx.ProviderName ?? string.Empty),
                    "request-id" or "req-id" => ctx.RequestId,
                    "timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                    "timestamp-ms" => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                    "os-version" => Uri.EscapeDataString(Environment.OSVersion.VersionString),
                    _ => match.Value // 未知变量保持原样，不破坏用户输入
                };
            }
        );
    }

    /// <summary>
    /// 将 Provider 配置的自定义请求头解析并安全注入到 HttpRequestMessage 中
    /// </summary>
    public static Dictionary<string, string> ApplyCustomHeaders(
        HttpRequestMessage request,
        ProviderConfig provider,
        HeaderResolutionContext ctx
    )
    {
        var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (
            provider == null
            || !provider.EnableCustomHeaders
            || provider.CustomHeaders == null
            || provider.CustomHeaders.Count == 0
        )
        {
            return applied;
        }

        foreach (var entry in provider.CustomHeaders)
        {
            if (entry == null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            string headerName = entry.Key.Trim();
            // 保护核心 HTTP 协议头不被误覆盖导致底层异常
            if (
                headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            string resolvedValue = ResolveTemplate(entry.ValueTemplate, ctx);
            request.Headers.Remove(headerName);
            request.Headers.TryAddWithoutValidation(headerName, resolvedValue);
            applied[headerName] = resolvedValue;
        }

        return applied;
    }

    /// <summary>
    /// 根据 BaseUrl 智能归一化规则计算指定协议（chat / resp）的完整请求端点 URL
    /// </summary>
    public static string ResolveEndpointUrl(string? baseUrl, LlmProtocolType protocol)
    {
        string suffix = protocol == LlmProtocolType.Resp ? "/responses" : "/chat/completions";
        string prefix = NormalizeVersionedPrefix(baseUrl);
        return $"{prefix}{suffix}";
    }

    /// <summary>
    /// 根据 BaseUrl 智能归一化规则计算 /models 探测端点 URL
    /// </summary>
    public static string ResolveModelsEndpointUrl(string? baseUrl)
    {
        string prefix = NormalizeVersionedPrefix(baseUrl);
        return $"{prefix}/models";
    }

    /// <summary>
    /// 规范化 BaseUrl 版本前缀：
    /// 1. 若以 '#' 结尾：去掉 '#' 与末尾 '/' 后直接作为前缀（高级原始路径模式）
    /// 2. 若已以 '/v1'（或 '/v1/'）结尾：保留该 '/v1' 前缀
    /// 3. 其他情况：自动补齐 '/v1'
    /// </summary>
    public static string NormalizeVersionedPrefix(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "https://api.openai.com/v1";
        }

        string trimmed = baseUrl.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"https://{trimmed}";
        }

        if (trimmed.EndsWith("#", StringComparison.Ordinal))
        {
            return trimmed[..^1].TrimEnd('/');
        }

        trimmed = trimmed.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/v1";
    }
}
