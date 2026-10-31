using HaloAgent.Engine.Abstractions.Types;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests;

public class PrefsStoreTests
{
    [Fact]
    public void Load_missing_file_returns_null()
    {
        using var dir = new TempDataDir();
        Assert.Null(PrefsStore.LoadPermissionMode());
    }

    [Fact]
    public void Load_malformed_file_returns_null()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.PrefsFile, "{ this is not json");
        Assert.Null(PrefsStore.LoadPermissionMode());
    }

    [Fact]
    public void Load_unknown_value_returns_null()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.PrefsFile, "{\"permission_mode\":\"super-admin\"}");
        Assert.Null(PrefsStore.LoadPermissionMode());
    }

    [Theory]
    [InlineData(PermissionMode.Ask, "ask")]
    [InlineData(PermissionMode.AcceptEdits, "acceptEdits")]
    [InlineData(PermissionMode.BypassPermissions, "bypassPermissions")]
    [InlineData(PermissionMode.Yolo, "yolo")]
    public void Save_then_load_roundtrips_all_modes(PermissionMode mode, string wire)
    {
        using var dir = new TempDataDir();
        Assert.True(PrefsStore.SavePermissionMode(mode));
        Assert.Equal(mode, PrefsStore.LoadPermissionMode());
        Assert.Contains($"\"{wire}\"", File.ReadAllText(Paths.PrefsFile));
    }

    [Fact]
    public void Save_preserves_other_keys()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.PrefsFile, "{\"theme\":\"dark\",\"permission_mode\":\"ask\"}");
        Assert.True(PrefsStore.SavePermissionMode(PermissionMode.Yolo));
        Assert.Equal(PermissionMode.Yolo, PrefsStore.LoadPermissionMode());
        Assert.Contains("\"theme\"", File.ReadAllText(Paths.PrefsFile));
    }

    [Fact]
    public void Save_rebuilds_corrupt_file()
    {
        using var dir = new TempDataDir();
        File.WriteAllText(Paths.PrefsFile, "{ broken");
        Assert.True(PrefsStore.SavePermissionMode(PermissionMode.AcceptEdits));
        Assert.Equal(PermissionMode.AcceptEdits, PrefsStore.LoadPermissionMode());
    }
}
