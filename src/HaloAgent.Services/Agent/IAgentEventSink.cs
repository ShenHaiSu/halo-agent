namespace HaloAgent.Services.Agent;

/// <summary>
/// need21-B03 Step1: 编排事件外发抽象（替代 Services 内直调 UI 事件派发器）。
/// 由宿主桥层实现并注入。
/// </summary>
public interface IAgentEventSink
{
    Task EmitAsync(string sessionId, string kind, object? payload);
    void EmitStreamDelta(string sessionId, string kind, string textDelta);
}

/// <summary>无操作事件槽（纯单测用，避免 UI 依赖）。</summary>
public sealed class NullAgentEventSink : IAgentEventSink
{
    public static readonly NullAgentEventSink Instance = new();

    public Task EmitAsync(string sessionId, string kind, object? payload) => Task.CompletedTask;

    public void EmitStreamDelta(string sessionId, string kind, string textDelta) { }
}

/// <summary>
/// 默认槽工厂。默认 Null；宿主程序集通过模块初始化器将其切换为真实桥实现，
/// 存量 Stage 测试（经 JsEventDispatcher 断言事件）零改动即绿。
/// </summary>
public static class AgentEventSinkDefaults
{
    public static Func<IAgentEventSink> Factory { get; set; } = () => NullAgentEventSink.Instance;
}
