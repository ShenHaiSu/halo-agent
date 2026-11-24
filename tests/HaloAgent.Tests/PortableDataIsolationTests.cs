using System;
using System.IO;
using Host.Desktop.Infrastructure.Process;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

[Collection("Serial-HaloLogger")]
public sealed class PortableDataIsolationTests : IDisposable
{
    private readonly string _root;
    private readonly string _exeDirectory;
    private readonly string? _prevBootstrapEnv;

    public PortableDataIsolationTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "HaloAgentPortableData_" + Guid.NewGuid().ToString("N")
        );
        _exeDirectory = Path.Combine(_root, "dist");
        Directory.CreateDirectory(_exeDirectory);
        _prevBootstrapEnv = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
        Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", _prevBootstrapEnv);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WriteMarker(string content) =>
        File.WriteAllText(
            Path.Combine(_exeDirectory, StoragePathManager.PortableDataMarkerFileName),
            content
        );

    [Fact]
    public void NoMarker_UsesGlobalBootstrap()
    {
        var policy = StoragePathManager.ResolveDataLocation(exeDirectory: _exeDirectory);

        Assert.False(policy.IsPortable);
        Assert.EndsWith(
            Path.Combine("HaloAgent", "bootstrap.json"),
            policy.BootstrapPath,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void PortableMarker_OverridesEnvironment_AndUsesPrivatePaths()
    {
        WriteMarker("{\"useLocalData\": true}");
        var injected = Path.Combine(_root, "installed-bootstrap.json");
        Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", injected);

        var policy = StoragePathManager.ResolveDataLocation(exeDirectory: _exeDirectory);
        var privateRoot = Path.Combine(_exeDirectory, ".local-data");

        Assert.True(policy.IsPortable);
        Assert.Equal(Path.Combine(privateRoot, "bootstrap.json"), policy.BootstrapPath);
        Assert.Equal(Path.Combine(privateRoot, "Data"), policy.DataRootPath);
        Assert.Equal(policy.DataRootPath, policy.FallbackDataRootPath);
        Assert.Equal(
            policy.BootstrapPath,
            StoragePathManager.ResolveProbeDataLocation(_exeDirectory).BootstrapPath
        );
        Assert.Equal(
            policy.BootstrapPath,
            SingleInstanceGuard.ResolveBootstrapProbePath(_exeDirectory)
        );
        Assert.False(File.Exists(injected));
    }

    [Theory]
    [InlineData("{\"useLocalData\": false}")]
    [InlineData("{\"other\": true}")]
    [InlineData("not-json")]
    public void InvalidMarker_FailsClosed(string content)
    {
        WriteMarker(content);
        var probe = StoragePathManager.ResolveProbeDataLocation(_exeDirectory);
        Assert.True(probe.IsPortable);
        Assert.Throws<InvalidDataException>(
            () => StoragePathManager.Initialize(exeDirectory: _exeDirectory)
        );
        Assert.False(Directory.Exists(Path.Combine(_exeDirectory, ".local-data")));
    }

    [Fact]
    public void Initialize_Portable_WritesOnlyPrivateBootstrap()
    {
        WriteMarker("{\"useLocalData\": true}");
        var manager = StoragePathManager.Initialize(exeDirectory: _exeDirectory);

        Assert.True(manager.IsUsingFallback == false);
        Assert.Equal(Path.Combine(_exeDirectory, ".local-data", "Data"), manager.CurrentDataRoot);
        Assert.Equal(
            Path.Combine(_exeDirectory, ".local-data", "bootstrap.json"),
            manager.BootstrapFilePath
        );
        Assert.True(File.Exists(manager.BootstrapFilePath));
        Assert.True(Directory.Exists(manager.ConfigDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "installed-bootstrap.json")));
    }
}
