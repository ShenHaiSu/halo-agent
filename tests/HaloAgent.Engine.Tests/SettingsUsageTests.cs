using System.Text.Json;
using HaloAgent.Engine.Abstractions;
using Xunit;

namespace HaloAgent.Engine.Tests;

/// <summary>
/// Configuration batch with true file-backed storage, asset reads with a
/// declared desktop-only write path, and the system probe carrying routing,
/// versions and the default-frame switch.
/// </summary>
public sealed class SettingsUsageTests
{
    private static JsonElement Payload(object value)
    {
        string raw = JsonSerializer.Serialize(value);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement node, string name)
    {
        if (
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(name, out var child)
            && child.ValueKind == JsonValueKind.String
        )
        {
            return child.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    [Fact]
    public async Task SystemInfoCarriesRoutingVersionsAndFrameDefault()
    {
        await using var server = new FrameServer();
        var info = await server.SubmitAsync(RequestFrame.WithPayload(701, "system.info"));
        Assert.True(info.Ok);
        var root = info.Result!.Value;
        Assert.Equal("0.1.1", Text(root, "contractVersion"));
        Assert.Equal(2, root.GetProperty("bridgeVersion").GetInt32());
        var routing = root.GetProperty("routing");
        Assert.True(routing.TryGetProperty("enabledBatches", out _));
        Assert.True(routing.GetProperty("defaultFrame").GetBoolean());
    }

    [Fact]
    public async Task BehaviorSwitchFlipsFrameDefaultOnTheProbe()
    {
        await using var server = new FrameServer();
        var saved = await server.SubmitAsync(
            RequestFrame.WithPayload(
                702,
                "settings.saveBehavior",
                new { behavior = new { frameDefault = false } }
            )
        );
        Assert.True(saved.Ok);
        var info = await server.SubmitAsync(RequestFrame.WithPayload(703, "system.info"));
        Assert.True(info.Ok);
        Assert.False(
            info.Result!.Value.GetProperty("routing").GetProperty("defaultFrame").GetBoolean()
        );
    }

    [Fact]
    public async Task ProvidersRoundTripsAndRejectsEmptyDocuments()
    {
        await using var server = new FrameServer();
        var bad = await server.SubmitAsync(
            RequestFrame.WithPayload(704, "settings.saveProviders", new { })
        );
        Assert.False(bad.Ok);
        Assert.Equal("INVALID_PARAMS", bad.Error!.Code);
        var saved = await server.SubmitAsync(
            RequestFrame.WithPayload(
                705,
                "settings.saveProviders",
                new { providers = new { count = 2 } }
            )
        );
        Assert.True(saved.Ok);
        var active = await server.SubmitAsync(
            RequestFrame.WithPayload(
                706,
                "settings.setActive",
                new { providerId = "openai", modelId = "gpt-4o" }
            )
        );
        Assert.True(active.Ok);
        Assert.Equal("openai", Text(active.Result!.Value, "providerId"));
    }

    [Fact]
    public void NetworkRoundTripsAndSecretsNeverPersistAsPlaintext()
    {
        var ledger = new ConfigAssetLedger();
        ledger.SaveProviders(Payload(new { providers = new { apiKey = "secret", count = 1 } }));
        string stored = ledger.ProvidersDocument();
        Assert.DoesNotContain("secret", stored);
        ledger.SaveNetwork(
            Payload(new { network = new { host = "proxy.local", password = "pw" } })
        );
        Assert.DoesNotContain("pw", ledger.NetworkDocument());
    }

    [Fact]
    public async Task ConfigPersistsAcrossSaveAndLoad()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "halo-config-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            await using var server = new FrameServer();
            server.ConfigureDataRoot(directory);
            var saved = await server.SubmitAsync(
                RequestFrame.WithPayload(
                    707,
                    "settings.saveBehavior",
                    new { behavior = new { frameDefault = false, theme = "dark" } }
                )
            );
            Assert.True(saved.Ok);
            server.SaveSessions(directory);
            await using var revived = new FrameServer();
            revived.LoadSessions(directory);
            var info = await revived.SubmitAsync(RequestFrame.WithPayload(708, "system.info"));
            Assert.True(info.Ok);
            Assert.False(
                info.Result!.Value.GetProperty("routing").GetProperty("defaultFrame").GetBoolean()
            );
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task AssetReadsServeSeededEntriesAndWritesStayDeclared()
    {
        await using var server = new FrameServer();
        server.ConfigAssets.SeedSkills(
            new[] { new ConfigAssetLedger.LedgerEntry("notes", "Notes", "Write notes", "project") }
        );
        server.ConfigAssets.SeedMemory(
            new[] { new ConfigAssetLedger.LedgerEntry("style", "Style", "Prefer short", "project") }
        );
        var skills = await server.SubmitAsync(RequestFrame.WithPayload(709, "asset.skills.list"));
        Assert.True(skills.Ok);
        Assert.Equal(1, skills.Result!.Value.GetProperty("items").GetArrayLength());
        var skill = await server.SubmitAsync(
            RequestFrame.WithPayload(710, "asset.skills.get", new { name = "notes" })
        );
        Assert.True(skill.Ok);
        var missing = await server.SubmitAsync(
            RequestFrame.WithPayload(711, "asset.skills.get", new { name = "missing" })
        );
        Assert.False(missing.Ok);
        Assert.Equal("NOT_FOUND", missing.Error!.Code);
        var memory = await server.SubmitAsync(RequestFrame.WithPayload(712, "asset.memory.list"));
        Assert.True(memory.Ok);
        var personas = await server.SubmitAsync(
            RequestFrame.WithPayload(713, "asset.personas.list")
        );
        Assert.True(personas.Ok);
        Assert.True(personas.Result!.Value.GetProperty("items").GetArrayLength() >= 1);
        var persona = await server.SubmitAsync(
            RequestFrame.WithPayload(714, "asset.personas.get", new { name = "default" })
        );
        Assert.True(persona.Ok);
        var write = await server.SubmitAsync(
            RequestFrame.WithPayload(715, "asset.memory.save", new { name = "style" })
        );
        Assert.True(write.Ok);
        Assert.Equal("asset.memory.save", Text(write.Result!.Value, "method"));
    }

    [Fact]
    public async Task HostOnlyStaysOnTheShell()
    {
        await using var server = new FrameServer();
        var picked = await server.SubmitAsync(
            RequestFrame.WithPayload(716, "host.pickDirectory", new { })
        );
        Assert.False(picked.Ok);
        Assert.Equal("NOT_FOUND", picked.Error!.Code);
        var opened = await server.SubmitAsync(
            RequestFrame.WithPayload(717, "host.openPath", new { path = "C:\\temp" })
        );
        Assert.False(opened.Ok);
        Assert.Equal("NOT_FOUND", opened.Error!.Code);
    }

    [Fact]
    public async Task IndependentSwitchesFallBackToStubWithoutTouchingTheOtherBatch()
    {
        string? previous = Environment.GetEnvironmentVariable("HALO_ENGINE_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("HALO_ENGINE_CONFIG", "0");
            await using var server = new FrameServer();
            server.ConfigAssets.SeedSkills(
                new[]
                {
                    new ConfigAssetLedger.LedgerEntry("notes", "Notes", "Write notes", "project")
                }
            );
            var stubbed = await server.SubmitAsync(
                RequestFrame.WithPayload(
                    718,
                    "settings.saveProviders",
                    new { providers = new { count = 1 } }
                )
            );
            Assert.True(stubbed.Ok);
            Assert.Equal("settings.saveProviders", Text(stubbed.Result!.Value, "method"));
            var skills = await server.SubmitAsync(
                RequestFrame.WithPayload(719, "asset.skills.list")
            );
            Assert.True(skills.Ok);
            Assert.True(skills.Result!.Value.TryGetProperty("items", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALO_ENGINE_CONFIG", previous);
        }
    }

    [Fact]
    public async Task UsageQueryKeepsBudgetFieldsFromTrueTokens()
    {
        await using var server = new FrameServer();
        var queried = await server.SubmitAsync(
            RequestFrame.WithPayload(720, "usage.query", new { })
        );
        Assert.True(queried.Ok);
        var root = queried.Result!.Value;
        Assert.True(root.TryGetProperty("maxTokens", out _));
        Assert.True(root.TryGetProperty("usagePercentage", out _));
        Assert.True(root.TryGetProperty("occupancyState", out _));
        Assert.True(root.TryGetProperty("usedIsEstimated", out _));
    }
}
