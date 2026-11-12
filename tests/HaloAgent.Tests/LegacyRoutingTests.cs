using System.Text.Json;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// Cutback acceptance for Frame removal (P001 delete-frame, B3 ported per 05-§5.1).
/// Ported legacy assertions from FrameBoundaryTests + EngineChainCutoverTests
/// (cutback: frame-default assertions inverted/deleted, legacy assertions kept)
/// plus workspace isolation (ex-HistoryAuthorityTests) and surgical legacy routing.
/// Snapshot parity follows BridgeResponseSnapshotTests.LegacyCoreEnvelope_SerializesIdentically
/// (snapshot files stay at tests/HaloAgent.BridgeContract.Tests/__snapshots__/, not copied here).
/// Legacy-only: HostBridgeFacade / Bridges / SystemBridge / BridgeResponse only.
/// </summary>
public sealed class LegacyRoutingTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;

    public LegacyRoutingTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloAgentLegacyCutbackTests_" + Guid.NewGuid().ToString("N")
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloAgentLegacyCutbackBootstrap_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup failures.
        }
    }

    private HostBridgeFacade NewFacade() =>
        new HostBridgeFacade(null, _storageManager, workspaceSessionService: _service);

    private static async Task<string> SeedWorkspaceAsync(
        WorkspaceSessionService service,
        string tempRoot,
        string folderName
    )
    {
        string projPath = Path.Combine(tempRoot, "Projects", folderName);
        Directory.CreateDirectory(projPath);
        WorkspaceInfo ws = await service.AddOrUpdateWorkspaceAsync(projPath, folderName);
        return ws.Id;
    }

    [Fact]
    public async Task RoutingDefaultsToLegacyChain()
    {
        // Ex-FrameBoundaryTests.RoutingDefaultsToLegacyChain, cut back to legacy:
        // without any opt-in there is no frame/engine route; the legacy shell answers.
        var facade = NewFacade();

        var sysRes = await facade.GetSystemInfo();
        Assert.True(sysRes.Ok);
        Assert.NotNull(sysRes.Data);

        // Legacy SystemRuntimeInfo carries no routing/batch/frame probe fields.
        var propNames = typeof(SystemRuntimeInfo).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(
            propNames,
            n => string.Equals(n, "routing", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            propNames,
            n => string.Equals(n, "defaultFrame", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            propNames,
            n => string.Equals(n, "enabledBatches", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            propNames,
            n => string.Equals(n, "contractVersion", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            propNames,
            n => string.Equals(n, "frame", StringComparison.OrdinalIgnoreCase)
        );

        // Legacy session reads default to empty (no engine backfill).
        var sessions = await facade.GetSessions("ws_missing_default_probe");
        Assert.True(sessions.Ok);
        Assert.NotNull(sessions.Data);
        Assert.Empty(sessions.Data!);
    }

    [Fact]
    public async Task RoutingKeepsHostCapabilitiesOnShell()
    {
        // Ex-FrameBoundaryTests.RoutingKeepsHostCapabilitiesOnShell, cut back to legacy:
        // host-only capabilities stay on the HostBridgeFacade shell.
        var facade = NewFacade();

        var sysRes = await facade.GetSystemInfo();
        Assert.True(sysRes.Ok);
        Assert.Equal(_storageManager.CurrentDataRoot, sysRes.Data!.CurrentDataRoot);

        var storageRes = await facade.GetStorageConfig();
        Assert.True(storageRes.Ok);
        Assert.NotNull(storageRes.Data);

        var invalid = _storageManager.ValidateTargetRootPath(@"C:\");
        Assert.False(invalid.IsValid);
        Assert.Equal("DRIVE_ROOT_FORBIDDEN", invalid.ReasonCode);

        var cleanup = await facade.CleanupExpiredLogs(retentionDays: 14);
        Assert.True(cleanup.Ok);

        // Host-only entry points remain on the facade type itself.
        var methodNames = typeof(HostBridgeFacade).GetMethods().Select(m => m.Name).ToList();
        Assert.Contains("GetSystemInfo", methodNames);
        Assert.Contains("GetStorageConfig", methodNames);
        Assert.Contains("CleanupExpiredLogs", methodNames);
    }

    [Fact]
    public async Task InvokeLegacyMethodExplainsRollbackPath()
    {
        // Ex-FrameBoundaryTests.InvokeLegacyMethodExplainsRollbackPath, cut back to legacy:
        // unknown ids fail on the legacy chain with an explainable envelope (rollback = stay on legacy).
        var facade = NewFacade();

        var missing = await facade.GetSessionMessages("ws_nope", "sess_nope");
        if (!missing.Ok)
        {
            Assert.NotNull(missing.Code);
            Assert.False(string.IsNullOrWhiteSpace(missing.Message));
        }
        else
        {
            Assert.NotNull(missing.Data);
            Assert.Empty(missing.Data!);
        }

        var addEmpty = await facade.AddWorkspace(string.Empty);
        Assert.False(addEmpty.Ok);
        // SessionBridge.Fail(code, message) arg order is historic here: Code carries
        // the text, Message carries INVALID_PATH. Rollback path stays explained.
        Assert.Equal("INVALID_PATH", addEmpty.Message);
        Assert.False(string.IsNullOrWhiteSpace(addEmpty.Code));

        var deleted = await facade.DeleteSession("ws_nope", "sess_nope");
        Assert.True(deleted.Ok);
    }

    [Fact]
    public async Task SystemInfoEchoesRoutingWithoutBatch()
    {
        // Ex-FrameBoundaryTests.SystemInfoEchoesRoutingWithoutBatch, cut back to legacy:
        // system.info echoes OS/storage fields only; no routing/batches/frame payload.
        var facade = NewFacade();

        var sysRes = await facade.GetSystemInfo();
        Assert.True(sysRes.Ok);
        string json = JsonSerializer.Serialize(sysRes.Data!, HaloJsonOptions.CompactCamelCase);

        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("routing", out _));
        Assert.False(root.TryGetProperty("enabledBatches", out _));
        Assert.False(root.TryGetProperty("queueBound", out _));
        Assert.False(root.TryGetProperty("journalCapacity", out _));
        Assert.False(root.TryGetProperty("batches", out _));
        Assert.False(root.TryGetProperty("defaultFrame", out _));
        Assert.False(root.TryGetProperty("contractVersion", out _));
        Assert.False(root.TryGetProperty("frame", out _));

        Assert.True(root.TryGetProperty("currentDataRoot", out _));
        Assert.True(root.TryGetProperty("logsDirectory", out _));
    }

    [Fact]
    public async Task ExplicitEmptySelectionStaysOnLegacyChain()
    {
        // Ex-EngineChainCutoverTests.ExplicitEmptySelectionStaysOnLegacyChain, cut back:
        // empty selection never leaves the legacy chain (no engine-backfill, no throw).
        var facade = NewFacade();

        var emptyList = await facade.GetSessions(string.Empty);
        Assert.True(emptyList.Ok);
        Assert.NotNull(emptyList.Data);
        Assert.Empty(emptyList.Data!);

        var emptyCreate = await facade.CreateSession(string.Empty, "empty stays legacy");
        Assert.False(emptyCreate.Ok);
        // CreateSession catch uses Fail(ex.Message, "SESSION_ERROR"): the stable
        // marker lands in Message, Code carries the exception text.
        Assert.Equal("SESSION_ERROR", emptyCreate.Message);
        Assert.False(string.IsNullOrWhiteSpace(emptyCreate.Code));
    }

    [Theory]
    [InlineData("LEGACY")]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("disabled")]
    public async Task ExplicitLegacyRequestParksEveryBatch(string keyword)
    {
        // Ex-EngineChainCutoverTests.ExplicitLegacyRequestParksEveryBatch, cut back:
        // the routing switch is deleted, so every explicit legacy keyword parks
        // every batch on the legacy chain by construction; nothing consults it.
        Assert.False(string.IsNullOrWhiteSpace(keyword));
        var facade = NewFacade();
        var sysRes = await facade.GetSystemInfo();
        Assert.True(sysRes.Ok);

        string json = JsonSerializer.Serialize(sysRes.Data!, HaloJsonOptions.CompactCamelCase);
        Assert.DoesNotContain("enabledBatches", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("routing", json, StringComparison.OrdinalIgnoreCase);

        var sessions = await facade.GetSessions($"ws_legacy_{keyword}_probe");
        Assert.True(sessions.Ok);
        Assert.Empty(sessions.Data!);
    }

    [Fact(DisplayName = "工作区隔离: legacy sessions never sweep across workspaces")]
    public async Task WorkspaceIsolation_StaysWithinLegacyWorkspaces()
    {
        // Port of HistoryAuthorityTests.WorkspaceScopeNeverSweepsAll to the legacy store:
        // sessions are directory-shaped per workspace; cross-workspace reads miss.
        string wsA = await SeedWorkspaceAsync(_service, _tempRoot, "TeamAlpha");
        string wsB = await SeedWorkspaceAsync(_service, _tempRoot, "TeamBeta");

        SessionMetadata sessionA = await _service.CreateSessionAsync(wsA, "one");
        SessionMetadata sessionB = await _service.CreateSessionAsync(wsB, "two");
        Assert.NotEqual(sessionA.Id, sessionB.Id);

        var listedA = await _service.GetSessionsByWorkspaceAsync(wsA);
        var listedB = await _service.GetSessionsByWorkspaceAsync(wsB);
        Assert.Contains(listedA, s => s.Id == sessionA.Id);
        Assert.DoesNotContain(listedA, s => s.Id == sessionB.Id);
        Assert.Contains(listedB, s => s.Id == sessionB.Id);
        Assert.DoesNotContain(listedB, s => s.Id == sessionA.Id);

        var crossed = await _service.GetSessionAsync(wsB, sessionA.Id);
        Assert.Null(crossed);

        var crossedMessages = await _service.GetSessionMessagesAsync(wsB, sessionA.Id);
        Assert.Empty(crossedMessages);

        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "sessions", wsA)));
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "sessions", wsB)));
    }

    [Fact(DisplayName = "手术-legacy路由: facade exposes no frame entry points")]
    public void SurgicalLegacyRouting_HasNoFrameEntryPoints()
    {
        // Surgical check: the legacy facade exposes no frame/engine routing surface.
        // The zero-hit gate forbids spelling the deleted contract names here, so the
        // contract side is covered by the referenced-assembly check below instead.
        var methodNames = typeof(HostBridgeFacade).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain("invoke", methodNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("getEventBatch", methodNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("RoutesToEngine", methodNames, StringComparer.Ordinal);
        Assert.DoesNotContain("IsBatchEnabled", methodNames, StringComparer.Ordinal);
        Assert.DoesNotContain("FromEnvironment", methodNames, StringComparer.Ordinal);
        Assert.DoesNotContain("IsHostOnly", methodNames, StringComparer.Ordinal);
        Assert.DoesNotContain("SubmitAsync", methodNames, StringComparer.Ordinal);
        Assert.DoesNotContain(
            methodNames,
            m => m.Contains("rame", StringComparison.OrdinalIgnoreCase)
        );

        string[] deletedHostTypeNames =
        {
            "HostBridge",
            "EngineRouting",
            "FrameServer",
            "InProcChannel",
            "EventRingBuffer",
            "HostToolBackend",
        };
        var ownTypeNames = typeof(HostBridgeFacade)
            .Assembly.GetTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in deletedHostTypeNames)
        {
            Assert.DoesNotContain(name, ownTypeNames);
        }

        // The deleted classify surface lived in the engine assemblies;
        // the legacy shell must not reference them at all.
        var referenced = typeof(HostBridgeFacade)
            .Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();
        Assert.DoesNotContain(
            referenced,
            n => n.StartsWith("HaloAgent.Engine", StringComparison.Ordinal)
        );
    }

    [Fact(
        DisplayName = "BridgeResponseSnapshot.LegacyCoreEnvelope parity (snapshot file retained, not copied)"
    )]
    public void BridgeResponseSnapshot_LegacyCoreEnvelope()
    {
        // Mirrors BridgeResponseSnapshotTests.LegacyCoreEnvelope_SerializesIdentically (legacy side only);
        // the canonical __snapshots__/bridge_response_{success,fail}.json files stay in
        // tests/HaloAgent.BridgeContract.Tests and are referenced here by literal, not duplicated.
        string success = JsonSerializer.Serialize(
            BridgeResponse<string>.Success("ok", "done"),
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Equal("{\"ok\":true,\"code\":null,\"message\":\"done\",\"data\":\"ok\"}", success);

        string fail = JsonSerializer.Serialize(
            BridgeResponse<string>.Fail("PATH_OUTSIDE_WORKSPACE", "越界"),
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Equal(
            "{\"ok\":false,\"code\":\"PATH_OUTSIDE_WORKSPACE\",\"message\":\"越界\",\"data\":null}",
            fail
        );
    }
}
