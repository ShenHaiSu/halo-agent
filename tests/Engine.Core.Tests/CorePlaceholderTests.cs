using Xunit;

namespace HaloAgent.Engine.Core.Tests;

/// <summary>P0 占位测试：保证测试工程可构建、可运行。真正的内核测试在 P2 补齐。</summary>
public class CorePlaceholderTests
{
    [Fact]
    public void Placeholder_project_loads()
    {
        Assert.NotNull(typeof(Core.CorePlaceholder));
    }
}
