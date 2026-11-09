using System.Text.Json;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>
/// Additive-only freeze guard for the 0.1.0 -&gt; 0.1.1 contract step:
/// the frozen name lists may only grow, envelope shape stays fixed,
/// and rejected payload names must not appear.
/// </summary>
public sealed class FreezeGuardTests
{
    private static readonly string[] BaselineMethods =
    {
        "system.ping",
        "system.info",
        "session.new",
        "session.resume",
        "session.rename",
        "session.delete",
        "session.clear",
        "session.list",
        "session.get",
        "session.history",
        "session.compact",
        "session.setCwd",
        "agent.prompt",
        "agent.queue",
        "agent.cancel",
        "agent.retry",
        "agent.drain",
        "agent.answer",
        "agent.unqueue",
        "state.snapshot",
        "state.mode",
        "state.persona",
        "state.maxTurns",
        "tool.undoRollback",
        "settings.saveProviders",
        "settings.setActive",
        "settings.saveNetwork",
        "settings.saveBehavior",
        "asset.skills.list",
        "asset.skills.get",
        "asset.memory.list",
        "asset.memory.save",
        "asset.personas.list",
        "asset.personas.get",
        "usage.query",
        "usage.clear",
        "host.pickDirectory",
        "host.openPath",
    };

    private static readonly string[] BaselineEvents =
    {
        "turn.started",
        "text.delta",
        "text.done",
        "tool.start",
        "tool.delta",
        "tool.end",
        "tool.confirm",
        "turn.done",
        "turn.failed",
        "turn.cancelled",
        "session.updated",
        "session.compacted",
        "state.changed",
        "usage.tick",
    };

    private static readonly string[] BaselineErrorCodes =
    {
        "INVALID_PARAMS",
        "NOT_FOUND",
        "TURN_IN_PROGRESS",
        "COMPACTING",
        "RATE_LIMITED",
        "PROVIDER_AUTH",
        "NETWORK_FAIL",
        "TOOL_DENIED",
        "TOOL_TIMEOUT",
        "CANCELLED",
        "INTERNAL",
    };

    [Fact]
    public void FrozenVersion_Is_AdditiveBump()
    {
        using var schema = ContractFiles.LoadSchema();
        string version = schema.RootElement.GetProperty("contractVersion").GetString()!;
        Assert.Equal("0.1.1", version);
    }

    [Fact]
    public void Methods_Contain_Title_And_Count39()
    {
        using var schema = ContractFiles.LoadSchema();
        var methods = ContractFiles.ReadStringArray(schema.RootElement, "methods");
        Assert.Equal(39, methods.Count);
        Assert.Contains("session.title", methods);
    }

    [Fact]
    public void Methods_Are_Superset_Of_Baseline()
    {
        using var schema = ContractFiles.LoadSchema();
        var methods = ContractFiles.ReadStringArray(schema.RootElement, "methods");
        foreach (string name in BaselineMethods)
        {
            Assert.Contains(name, methods);
        }

        var declared = ReadAddedMethods(schema.RootElement);
        foreach (string name in methods)
        {
            if (!BaselineMethods.Contains(name))
            {
                Assert.Contains(name, declared);
            }
        }
    }

    [Fact]
    public void Events_Stay_Frozen()
    {
        using var schema = ContractFiles.LoadSchema();
        var events = ContractFiles.ReadStringArray(schema.RootElement, "events");
        Assert.Equal(BaselineEvents, events);
    }

    [Fact]
    public void ErrorCodes_Stay_Frozen()
    {
        using var schema = ContractFiles.LoadSchema();
        var codes = ContractFiles.ReadStringArray(schema.RootElement, "errorCodes");
        Assert.Equal(BaselineErrorCodes, codes);
        Assert.DoesNotContain("BACKPRESSURE", codes);
        Assert.DoesNotContain("UNKNOWN_TOOL", codes);
    }

    [Fact]
    public void Envelope_Required_Fields_Stay_Fixed()
    {
        using var schema = ContractFiles.LoadSchema();
        var definitions = schema.RootElement.GetProperty("definitions");
        Assert.Equal(new[] { "t", "id", "m" }, ReadRequired(definitions, "requestFrame"));
        Assert.Equal(new[] { "t", "id", "ok" }, ReadRequired(definitions, "responseFrame"));
        Assert.Equal(new[] { "t", "seq", "k" }, ReadRequired(definitions, "eventFrame"));
    }

    [Fact]
    public void RejectedPayloadNames_Are_Absent()
    {
        using var schema = ContractFiles.LoadSchema();
        var methods = ContractFiles.ReadStringArray(schema.RootElement, "methods");
        Assert.DoesNotContain("argsDelta", methods);
        Assert.DoesNotContain("argsJson", methods);

        string optionals = schema
            .RootElement.GetProperty("freeze")
            .GetProperty("optionals")
            .GetRawText();
        Assert.DoesNotContain("argsDelta", optionals, StringComparison.Ordinal);
        Assert.DoesNotContain("argsJson", optionals, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKPRESSURE", optionals, StringComparison.Ordinal);
    }

    [Fact]
    public void Routing_Mapping_Is_Documented()
    {
        using var schema = ContractFiles.LoadSchema();
        var routing = schema.RootElement.GetProperty("freeze").GetProperty("routing");
        Assert.Contains(
            "TOOL_DENIED",
            routing.GetProperty("unknownTool").GetString()!,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "RATE_LIMITED",
            routing.GetProperty("backpressure").GetString()!,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "turn.done",
            routing.GetProperty("truncation").GetString()!,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ParityTemplate_Exists_And_Starts_Unchecked()
    {
        string path = Path.Combine(
            ContractFiles.FindRepoRoot(),
            "contract",
            "parity-template.json"
        );
        Assert.True(File.Exists(path), "contract/parity-template.json is missing.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var areas = doc.RootElement.GetProperty("areas");
        int count = 0;
        foreach (var area in areas.EnumerateObject())
        {
            foreach (var item in area.Value.EnumerateArray())
            {
                count++;
                Assert.False(
                    item.GetProperty("checked").GetBoolean(),
                    $"parity item {area.Name}/{item.GetProperty("id").GetString()} must start unchecked."
                );
            }
        }

        Assert.True(count >= 8, "parity template must list the baseline stations.");
    }

    private static IReadOnlyList<string> ReadAddedMethods(JsonElement root)
    {
        var items = new List<string>();
        foreach (
            var item in root.GetProperty("freeze").GetProperty("addedMethods").EnumerateArray()
        )
        {
            items.Add(item.GetString()!);
        }

        return items;
    }

    private static IReadOnlyList<string> ReadRequired(JsonElement definitions, string name)
    {
        var items = new List<string>();
        foreach (var item in definitions.GetProperty(name).GetProperty("required").EnumerateArray())
        {
            items.Add(item.GetString()!);
        }

        return items;
    }
}
