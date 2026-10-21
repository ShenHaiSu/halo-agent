using HaloAgent.Bridge.Contracts;
using Xunit;

namespace HaloAgent.Tools.Tests;

/// <summary>
/// need21-B03/B05: 桥事件网关测试（Services/Tools 经 BridgeEvents 发布，不起 Cef/WinForms 即可跑）。
/// </summary>
public sealed class BridgeEventsTests
{
    private sealed class RecordingPublisher : IBridgeEventPublisher
    {
        public readonly List<(string EventType, object? Payload)> Calls = new();

        public void Publish(string eventType, object? payload) => Calls.Add((eventType, payload));
    }

    [Fact]
    public void DefaultFactory_PublishNeverThrows()
    {
        var prev = BridgeEvents.Factory;
        try
        {
            BridgeEvents.Factory = () => NullBridgeEventPublisher.Instance;
            var ex = Record.Exception(
                () => BridgeEvents.Publish("agent:test-event", new { a = 1 })
            );
            Assert.Null(ex);
        }
        finally
        {
            BridgeEvents.Factory = prev;
        }
    }

    [Fact]
    public void CustomFactory_ReceivesExactEvent()
    {
        var prev = BridgeEvents.Factory;
        var rec = new RecordingPublisher();
        try
        {
            BridgeEvents.Factory = () => rec;
            var payload = new { sessionId = "s1" };
            BridgeEvents.Publish("goal:updated", payload);
            Assert.Single(rec.Calls);
            Assert.Equal("goal:updated", rec.Calls[0].EventType);
            Assert.Same(payload, rec.Calls[0].Payload);
        }
        finally
        {
            BridgeEvents.Factory = prev;
        }
    }

    [Fact]
    public void ThrowingFactory_IsSwallowed()
    {
        var prev = BridgeEvents.Factory;
        try
        {
            BridgeEvents.Factory = () => new ThrowingPublisher();
            var ex = Record.Exception(() => BridgeEvents.Publish("agent:test-event", null));
            Assert.Null(ex);
        }
        finally
        {
            BridgeEvents.Factory = prev;
        }
    }

    private sealed class ThrowingPublisher : IBridgeEventPublisher
    {
        public void Publish(string eventType, object? payload) =>
            throw new InvalidOperationException("boom");
    }
}
