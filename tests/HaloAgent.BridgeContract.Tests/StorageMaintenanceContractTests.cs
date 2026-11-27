using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.BridgeContract.Tests;

/// <summary>历史库空间治理契约：双口径明细与维护结果统一小驼峰，老字段零抖动。</summary>
public sealed class StorageMaintenanceContractTests
{
    [Fact]
    public void BreakdownKeepsPhysicalShapeAndOptionalDetails()
    {
        var breakdown = new StorageBreakdown
        {
            RootPath = "D:\\Data",
            TotalBytes = 100,
            Items = new()
            {
                new StorageBreakdownItem
                {
                    Name = "sessions",
                    SizeBytes = 60,
                    FileCount = 3,
                    DatabaseDetails = new()
                    {
                        new WorkspaceDatabaseStorageDetail
                        {
                            WorkspaceId = "ws_a",
                            DatabasePath = "D:\\Data\\sessions\\ws_a\\history.db",
                            PhysicalBytes = 50,
                            PageSizeBytes = 4096,
                            PageCount = 8,
                            FreePageCount = 2,
                            UsedPageBytes = 24576,
                            ReclaimablePageBytes = 8192,
                            BackupBytes = 10,
                            WalBytes = 0,
                            ShmBytes = 32,
                            TempBytes = 0,
                            Measured = true,
                        },
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(breakdown, HaloJsonOptions.CompactCamelCase);
        Assert.Contains("\"sizeBytes\":60", json);
        Assert.Contains("\"databaseDetails\":[{", json);
        Assert.Contains("\"workspaceId\":\"ws_a\"", json);
        Assert.Contains("\"reclaimablePageBytes\":8192", json);
        Assert.Contains("\"measured\":true", json);
    }

    [Fact]
    public void CompactionAndCleanupResultsUseCamelCase()
    {
        string compactJson = JsonSerializer.Serialize(
            new WorkspaceCompactionResult
            {
                Success = true,
                BeforeBytes = 100,
                AfterBytes = 60,
                WalCheckpointed = true,
            },
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Contains("\"success\":true", compactJson);
        Assert.Contains("\"beforeBytes\":100", compactJson);
        Assert.Contains("\"reclaimedBytes\":40", compactJson);
        Assert.Contains("\"walCheckpointed\":true", compactJson);

        string cleanupJson = JsonSerializer.Serialize(
            new WorkspaceBackupCleanupResult
            {
                Success = true,
                FreedBytes = 32,
                BackupExisted = true,
            },
            HaloJsonOptions.CompactCamelCase
        );
        Assert.Contains("\"freedBytes\":32", cleanupJson);
        Assert.Contains("\"backupExisted\":true", cleanupJson);
    }
}
