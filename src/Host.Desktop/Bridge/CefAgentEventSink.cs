using System.Runtime.CompilerServices;
using HaloAgent.Services.Agent;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Bridge;

/// <summary>
/// need21-B03 Step1: Bridge 层事件槽实现（唯一碰 JsEventDispatcher/Cef 的类）。
/// Services 经 IAgentEventSink 注入此实现，反向依赖清零。
/// </summary>
internal static class AgentEventSinkBootstrap
{
    [ModuleInitializer]
    internal static void Register() =>
        HaloAgent.Services.Agent.AgentEventSinkDefaults.Factory = () => new CefAgentEventSink();
}

public sealed class CefAgentEventSink : IAgentEventSink
{
    public Task EmitAsync(string sessionId, string kind, object? payload)
    {
        try
        {
            JsEventDispatcher.Publish(
                "agent:turn-event",
                new
                {
                    sessionId,
                    kind,
                    payload,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            );
        }
        catch { }
        return Task.CompletedTask;
    }

    public void EmitStreamDelta(
        string sessionId,
        string kind,
        string textDelta,
        string? blockId = null,
        long seq = 0,
        string? tag = null
    )
    {
        try
        {
            UnifiedStreamEvent evt =
                kind == "ReasoningDelta"
                    ? UnifiedStreamEvent.Reasoning(textDelta)
                    : UnifiedStreamEvent.Text(textDelta);
            if (!string.IsNullOrWhiteSpace(blockId) || seq != 0 || !string.IsNullOrWhiteSpace(tag))
            {
                evt = evt with { BlockId = blockId, Sequence = seq, Tag = tag };
            }
            var dto = SessionStreamEventDto.FromUnifiedEvent(sessionId, evt);
            JsEventDispatcher.Publish("llm:stream-event", dto);
        }
        catch { }
    }
}
