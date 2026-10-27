namespace HaloAgent.Engine.Storage.Tests;

/// <summary>HALO_DATA_DIR 隔离：每个测试独占临时数据目录，退出后还原并清理。</summary>
internal sealed class TempDataDir : IDisposable
{
    private readonly string? _old;

    public string Dir { get; }

    public TempDataDir()
    {
        _old = Environment.GetEnvironmentVariable("HALO_DATA_DIR");
        Dir = Path.Combine(Path.GetTempPath(), "halo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", Dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALO_DATA_DIR", _old);
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
        catch
        {
        }
    }
}
