using Xunit;

namespace HaloAgent.Engine.Storage.Tests;

/// <summary>P0 占位测试：保证测试工程可构建、可运行。真正的存储测试在 P2 补齐。</summary>
public class StoragePlaceholderTests
{
    [Fact]
    public void Default_data_dir_is_under_user_profile()
    {
        var dir = StoragePlaceholder.DefaultDataDir();
        Assert.EndsWith(".haloagent", dir);
    }
}
