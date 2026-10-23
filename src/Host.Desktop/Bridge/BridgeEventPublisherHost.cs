using System.Runtime.CompilerServices;
using HaloAgent.Bridge.Contracts;

namespace Host.Desktop.Bridge;

/// <summary>
/// need21-B03: Bridge 层真实事件发布实现（唯一碰 JsEventDispatcher 的发布路径之一，
/// 与 CefAgentEventSink 并列；Services/Tools 经 BridgeEvents 网关调用，反向依赖清零）。
/// </summary>
internal sealed class CefBridgeEventPublisher : IBridgeEventPublisher
{
    public void Publish(string eventType, object? payload) =>
        JsEventDispatcher.Publish(eventType, payload);
}

internal static class BridgeEventPublisherBootstrap
{
    [ModuleInitializer]
    internal static void Register() => BridgeEvents.Factory = () => new CefBridgeEventPublisher();
}
