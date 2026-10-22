using System;
using System.Collections.Generic;

namespace Host.Desktop.Services;

/// <summary>
/// bigChange P1-1：能力门 TranscriptCompat（简化版：只有 Chat/Resp）。
/// supportsMidConvoSystem / supportsToolChanges 必须绑定 modelId + protocol + endpoint(归一化) 三元组；
/// 换模型/换端点即判 false，防止“A 模型能力调 B 网关”整轮 400。
/// </summary>
public static class TranscriptCompat
{
    private static readonly HashSet<string> CapableTriples = new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    /// <summary>端点归一化：trim + 去尾部 / + 小写。</summary>
    public static string NormalizeEndpoint(string? endpoint)
    {
        string e = (endpoint ?? string.Empty).Trim();
        while (e.EndsWith("/", StringComparison.Ordinal) && e.Length > 1)
            e = e[..^1];
        return e.ToLowerInvariant();
    }

    public static string TripleKey(string? modelId, string? protocol, string? endpoint) =>
        (modelId ?? string.Empty).Trim()
        + "|"
        + (protocol ?? string.Empty).Trim().ToLowerInvariant()
        + "|"
        + NormalizeEndpoint(endpoint);

    /// <summary>注册一个已验证支持的（模型，协议，端点）三元组。</summary>
    public static void RegisterCapable(string? modelId, string? protocol, string? endpoint)
    {
        lock (Lock)
            CapableTriples.Add(TripleKey(modelId, protocol, endpoint));
    }

    /// <summary>对话中途是否可改 system（必须命中注册三元组，否则 false）。</summary>
    public static bool SupportsMidConvoSystem(string? modelId, string? protocol, string? endpoint)
    {
        lock (Lock)
            return CapableTriples.Contains(TripleKey(modelId, protocol, endpoint));
    }

    /// <summary>对话中途是否可改 tools（必须命中注册三元组，否则 false）。</summary>
    public static bool SupportsToolChanges(string? modelId, string? protocol, string? endpoint)
    {
        lock (Lock)
            return CapableTriples.Contains(TripleKey(modelId, protocol, endpoint));
    }

    public static void ClearForTests()
    {
        lock (Lock)
            CapableTriples.Clear();
    }
}
