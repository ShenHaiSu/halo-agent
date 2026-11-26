using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class CompactionHistoryRepairTests
{
    [Fact]
    public async Task Repair_Latest_Terminal_Orphan_Is_Backed_Up_And_Idempotent()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        var stub = UnifiedChatMessage.User("orphan-summary");
        stub.Kind = MessageKind.CompactStub;
        stub.MetaJson = JsonSerializer.Serialize(
            new CompactStubMeta { V = 1, RemovedCount = 12 },
            HaloJsonOptions.CompactCamelCase
        );
        await fixture.Repository.AppendMessageAsync(workspaceId, sessionId, stub);
        var repair = new CompactionHistoryRepair(fixture.Service);

        var analysis = await repair.AnalyzeAsync(workspaceId, sessionId);
        Assert.Equal(1, analysis.OrphanStubCount);
        Assert.Equal(1, analysis.EligibleOrphanStubCount);
        Assert.Equal(0, analysis.RepairedTurnCount);
        Assert.Null(analysis.BackupPath);

        var applied = await repair.RepairAsync(workspaceId, sessionId, true);
        Assert.Equal(1, applied.RepairedTurnCount);
        Assert.NotNull(applied.BackupPath);
        Assert.True(File.Exists(applied.BackupPath));
        var turns = await fixture.CreateStore().GetTimelineSummaryAsync(workspaceId, sessionId);
        var turn = Assert.Single(turns);
        Assert.Equal("cturn-" + stub.Id, turn.TurnId);
        Assert.Equal(TurnKind.Compact, turn.Kind);
        Assert.Equal(0, turn.Blocks);
        Assert.Null(turn.FinalBlockId);
        Assert.Equal(12, turn.RemovedMessages);
        var meta = await fixture.Service.GetSessionAsync(workspaceId, sessionId);
        Assert.Equal(1, meta!.MessageCount);
        Assert.Equal(CompactMode.Stub, meta.CompactMode);

        var repeated = await repair.RepairAsync(workspaceId, sessionId, true);
        Assert.Equal(0, repeated.RepairedTurnCount);
        Assert.Single(await fixture.CreateStore().GetTimelineSummaryAsync(workspaceId, sessionId));
        Assert.Single(await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId));
    }

    [Fact]
    public async Task Repair_Does_Not_Touch_Non_Terminal_Orphan()
    {
        using var fixture = new CompactTestWorkspace();
        var (workspaceId, sessionId) = await fixture.NewSessionAsync();
        var stub = UnifiedChatMessage.User("non-terminal-orphan");
        stub.Kind = MessageKind.CompactStub;
        await fixture.Repository.AppendMessageAsync(workspaceId, sessionId, stub);
        await fixture.Repository.AppendMessageAsync(
            workspaceId,
            sessionId,
            UnifiedChatMessage.User("later-message")
        );
        var repair = new CompactionHistoryRepair(fixture.Service);

        var report = await repair.RepairAsync(workspaceId, sessionId, true);

        Assert.Equal(1, report.OrphanStubCount);
        Assert.Equal(0, report.EligibleOrphanStubCount);
        Assert.Equal(0, report.RepairedTurnCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Null(report.BackupPath);
        Assert.Empty(await fixture.CreateStore().GetTimelineSummaryAsync(workspaceId, sessionId));
        Assert.Equal(
            2,
            (await fixture.Service.GetSessionMessagesAsync(workspaceId, sessionId)).Count
        );
    }
}
