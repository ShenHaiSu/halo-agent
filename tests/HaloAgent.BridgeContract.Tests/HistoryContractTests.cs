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
        Assert.Contains("\"turnDelete\":true", json);
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

    [Fact]
    public void ProjectedUserOnlySummaryKeepsDtoShapeAndCamelCase()
    {
        // 与历史孤儿合成轮同形状：cancelled 零块、空 lastRequestId、无助手锚点。
        var summary = new TurnSummary
        {
            TurnId = "uturn-user-old",
            UserContent = "旧消息",
            Status = "cancelled",
            LastRequestId = string.Empty,
            UserMessageId = "user-old",
            AssistantMessageId = null,
            CreatedAtUtc = "2026-11-26T00:00:00.0000000Z",
            UpdatedAtUtc = "2026-11-26T00:00:00.0000000Z",
            AnchorKey = "uturn-user-old",
        };
        string json = JsonSerializer.Serialize(summary, HaloJsonOptions.CompactCamelCase);

        Assert.Contains("\"turnId\":\"uturn-user-old\"", json);
        Assert.Contains("\"userMessageId\":\"user-old\"", json);
        Assert.Contains("\"assistantMessageId\":null", json);
        Assert.Contains("\"lastRequestId\":\"\"", json);
        Assert.Contains("\"status\":\"cancelled\"", json);
        Assert.Contains("\"turnSeq\":0", json);
        Assert.DoesNotContain("TurnId", json); // 无 PascalCase 残留。
    }
}
