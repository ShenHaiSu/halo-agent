using System.Security.Cryptography;
using System.Text;
using HaloAgent.Engine.Storage.Undo;
using Xunit;

namespace HaloAgent.Engine.Storage.Tests.Undo;

public class UndoTests
{
    private static string ShaOfFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ShaOfText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Overwrite_then_rollback_restores_identical_sha()
    {
        using var dir = new TempDataDir();
        var file = Path.Combine(dir.Dir, "work.txt");
        File.WriteAllText(file, "v1");

        var snap = UndoStore.Backup("s1", file, "edit");
        Assert.NotNull(snap);

        File.WriteAllText(file, "v2");
        UndoStore.Rollback(snap!);

        Assert.Equal(ShaOfText("v1"), ShaOfFile(file));
    }

    [Fact]
    public void Backup_missing_file_returns_null_and_rollback_restores_deleted_file()
    {
        using var dir = new TempDataDir();
        var file = Path.Combine(dir.Dir, "new.txt");

        Assert.Null(UndoStore.Backup("s1", file, "edit"));

        File.WriteAllText(file, "v1");
        var snap = UndoStore.Backup("s1", file, "edit");
        Assert.NotNull(snap);

        File.Delete(file);
        UndoStore.Rollback(snap!);

        Assert.True(File.Exists(file));
        Assert.Equal("v1", File.ReadAllText(file));
    }

    [Fact]
    public void Snapshots_are_trimmed_to_500_and_manifest_stays_within_limit()
    {
        using var dir = new TempDataDir();
        var file = Path.Combine(dir.Dir, "work.txt");

        for (int i = 0; i < 505; i++)
        {
            File.WriteAllText(file, "v" + i);
            Assert.NotNull(UndoStore.Backup("s1", file, "edit" + i));
        }

        Assert.True(UndoStore.List().Count <= 500);
        Assert.True(File.ReadAllLines(Paths.UndoManifest).Length <= 500);
    }
}
