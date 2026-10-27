using System.Text.Json;

namespace HaloAgent.Engine.Server.Routing;

/// <summary>
/// 方法处理器签名。
/// params 为 null 表示无参数（或参数为 null）。
/// 返回值会被序列化成 res 帧的 r 字段。
/// </summary>
public delegate Task<object?> MethodHandler(JsonElement? parameters, CancellationToken ct);

/// <summary>
/// 方法路由表。
///
/// 设计要点：
///   1. **严格**：未知方法返回 method.notFound（不静默失败）
///   2. **可枚举**：所有已注册方法可通过 Methods 列出（system.protocol 用）
///   3. **可替换**：P2 阶段用真实实现替换 stub，路由表结构不变
/// </summary>
public sealed class MethodRouter
{
    private readonly Dictionary<string, MethodHandler> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Methods => _handlers.Keys;

    public void Register(string method, MethodHandler handler)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("方法名不能为空", nameof(method));
        if (!_handlers.TryAdd(method, handler))
            throw new InvalidOperationException($"方法重复注册: {method}");
    }

    /// <summary>批量注册（stub 用）。</summary>
    public void RegisterAll(params (string method, MethodHandler handler)[] entries)
    {
        foreach (var (m, h) in entries) Register(m, h);
    }

    public bool TryGet(string method, out MethodHandler handler)
        => _handlers.TryGetValue(method, out handler!);
}
