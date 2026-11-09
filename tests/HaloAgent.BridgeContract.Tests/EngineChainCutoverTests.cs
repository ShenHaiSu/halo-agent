using Host.Desktop.Engine;
using Xunit;

namespace HaloAgent.BridgeContract.Tests;

/// <summary>
/// Engine chain cutover: without explicit configuration every batch rides
/// the frame chain, an explicit legacy request parks every batch on the
/// legacy chain, and each batch still flips on its own for rollback.
/// </summary>
public sealed class EngineChainCutoverTests
{
    private const string RoutingVariable = "HALO_ENGINE_ROUTING";

    private static string? SetRoutingVariable(string? value)
    {
        string? previous = Environment.GetEnvironmentVariable(RoutingVariable);
        Environment.SetEnvironmentVariable(RoutingVariable, value);
        return previous;
    }

    private static void RestoreRoutingVariable(string? previous)
    {
        Environment.SetEnvironmentVariable(RoutingVariable, previous);
    }

    [Fact]
    public void MissingConfigurationRoutesEveryBatchToEngine()
    {
        string? previous = SetRoutingVariable(null);
        try
        {
            var routing = EngineRouting.FromEnvironment();
            Assert.True(routing.IsBatchEnabled("A"));
            Assert.True(routing.IsBatchEnabled("B"));
            Assert.True(routing.IsBatchEnabled("C"));
            Assert.True(routing.RoutesToEngine("session.new"));
            Assert.True(routing.RoutesToEngine("agent.prompt"));
            Assert.True(routing.RoutesToEngine("usage.query"));
            Assert.True(routing.RoutesToEngine("settings.saveBehavior"));
            Assert.True(routing.RoutesToEngine("asset.skills.list"));
            Assert.False(routing.RoutesToEngine("host.pickDirectory"));
        }
        finally
        {
            RestoreRoutingVariable(previous);
        }
    }

    [Fact]
    public void BlankConfigurationKeepsFrameDefault()
    {
        string? previous = SetRoutingVariable("   ");
        try
        {
            var routing = EngineRouting.FromEnvironment();
            Assert.True(routing.IsBatchEnabled("A"));
            Assert.True(routing.IsBatchEnabled("B"));
            Assert.True(routing.IsBatchEnabled("C"));
        }
        finally
        {
            RestoreRoutingVariable(previous);
        }
    }

    [Theory]
    [InlineData("LEGACY")]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("disabled")]
    public void ExplicitLegacyRequestParksEveryBatch(string keyword)
    {
        string? previous = SetRoutingVariable(keyword);
        try
        {
            var routing = EngineRouting.FromEnvironment();
            Assert.False(routing.IsBatchEnabled("A"));
            Assert.False(routing.IsBatchEnabled("B"));
            Assert.False(routing.IsBatchEnabled("C"));
            Assert.False(routing.RoutesToEngine("session.new"));
            Assert.False(routing.RoutesToEngine("usage.query"));
            Assert.False(routing.RoutesToEngine("asset.skills.list"));
        }
        finally
        {
            RestoreRoutingVariable(previous);
        }
    }

    [Fact]
    public void SingleBatchFlipsWithoutTouchingOthers()
    {
        string? previous = SetRoutingVariable("A");
        try
        {
            var routing = EngineRouting.FromEnvironment();
            Assert.True(routing.IsBatchEnabled("A"));
            Assert.False(routing.IsBatchEnabled("B"));
            Assert.False(routing.IsBatchEnabled("C"));
            Assert.True(routing.RoutesToEngine("session.new"));
            Assert.False(routing.RoutesToEngine("usage.query"));
            Assert.False(routing.RoutesToEngine("asset.skills.list"));
        }
        finally
        {
            RestoreRoutingVariable(previous);
        }
    }

    [Fact]
    public void ExplicitEmptySelectionStaysOnLegacyChain()
    {
        var routing = new EngineRouting();
        Assert.False(routing.RoutesToEngine("session.new"));
        Assert.False(routing.RoutesToEngine("agent.prompt"));
        Assert.False(routing.RoutesToEngine("usage.query"));
    }
}
