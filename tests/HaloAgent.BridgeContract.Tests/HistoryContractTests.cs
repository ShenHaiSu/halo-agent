using System.Text.Json;
using HaloAgent.Bridge.Contracts;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.BridgeContract.Tests;

/// <summary>历史扩展 DTO 的桥契约：camelCase、能力位、版本 2 与回滚残留字段。</summary>
public sealed class HistoryContractTests
{
    [Fact]
    public void ProtocolVersionStaysTwo()
    {
        Assert.Equal(2, BridgeVersion.ProtocolVersion);
    }

    [Fact]
    public void CapabilitiesSerializeInCamelCase()
    {
        string json = JsonSerializer.Serialize(
            HistoryCapabilities.Supported,
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Contains("\"historyAnchors\":true", json);
        Assert.Contains("\"replayImages\":true", json);
        Assert.Contains("\"atomicHistoryMutex\":true", json);
        Assert.Contains("\"strictBranchRollback\":true", json);
    }

    [Fact]
    public void ReplayImageAndSummaryAnchorsUseCamelCase()
    {
        var image = new HistoryReplayImageDto
        {
            ImageId = "img_1",
            FileName = "a.png",
            Mime = "image/png",
            Lost = true,
        };
        string imageJson = JsonSerializer.Serialize(image, HaloJsonOptions.CompactCamelCase);
        Assert.Contains("\"imageId\":\"img_1\"", imageJson);
        Assert.Contains("\"sizeBytes\":0", imageJson);
        Assert.Contains("\"lost\":true", imageJson);

        string summaryJson = JsonSerializer.Serialize(
            new TurnSummary { UserMessageId = "u1", AssistantMessageId = "a2" },
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Contains("\"userMessageId\":\"u1\"", summaryJson);
        Assert.Contains("\"assistantMessageId\":\"a2\"", summaryJson);
    }

    [Fact]
    public void RollbackFailureCarriesResidualSessionId()
    {
        string json = JsonSerializer.Serialize(
            Host.Desktop.Core.Models.BridgeResponse<SessionMetadata>.Fail(
                "BRANCH_ROLLBACK_FAILED",
                "清理失败",
                "sess_residual",
                "Directory"
            ),
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Contains("\"newSessionId\":\"sess_residual\"", json);
        Assert.Contains("\"failedStage\":\"Directory\"", json);
    }

    [Fact]
    public void SuccessEnvelopeKeepsLegacyShape()
    {
        string json = JsonSerializer.Serialize(
            Host.Desktop.Core.Models.BridgeResponse<string>.Success("ok", "done"),
            HaloJsonOptions.CompactCamelCase
        );
        Assert.DoesNotContain("newSessionId", json);
        Assert.DoesNotContain("failedStage", json);
    }
}
