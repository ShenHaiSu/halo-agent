using Xunit;

namespace HaloAgent.Engine.Storage.Tests;

public class PathsTests
{
    [Fact]
    public void SessionsDir_is_under_data_dir()
    {
        Assert.StartsWith(Paths.DataDir, Paths.SessionsDir, StringComparison.Ordinal);
    }
}
