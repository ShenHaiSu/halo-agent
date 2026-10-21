namespace HaloAgent.Bridge.Contracts;

/// <summary>
/// need21-B03: 原始桥事件发布抽象（替代 Services/Tools 内直调 JsEventDispatcher）。
/// 由 Bridge 层经模块初始化器注入真实实现；单测默认 Null（事件丢失计数，不抛）。
/// </summary>
public interface IBridgeEventPublisher
{
    void Publish(string eventType, object? payload);
}

/// <summary>无操作发布器（纯单测/无 UI 上下文用）。</summary>
public sealed class NullBridgeEventPublisher : IBridgeEventPublisher
{
    public static readonly NullBridgeEventPublisher Instance = new();

    public void Publish(string eventType, object? payload) { }
}

/// <summary>
/// 桥事件静态网关。Services/Tools 只依赖此网关（Contracts 零依赖，被所有引用），
/// 不得直引 Host.Desktop.Bridge。
/// </summary>
public static class BridgeEvents
{
    public static Func<IBridgeEventPublisher> Factory { get; set; } =
        () => NullBridgeEventPublisher.Instance;

    public static void Publish(string eventType, object? payload)
    {
        try
        {
            Factory().Publish(eventType, payload);
        }
        catch
        {
            // 事件失败永不回抛（与直调时代 try/catch 语义一致）。
        }
    }
}
