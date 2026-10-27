using Xunit;

namespace HaloAgent.Engine.Server.Tests.Guards;

public sealed class NoStubInProductionTests
{
    [Fact]
    public void Production_route_table_has_no_stub_handlers()
    {
        var path = FindRepoFile("src/Engine.Server/Routing/EngineAssembly.cs");
        var src = File.ReadAllText(path);

        var offenders = new[] { "stub.EmptyOk", "stub.EmptyList", "stub.NotImplemented" }
            .Where(token => src.Contains(token, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "EngineAssembly 生产路由表仍挂有 stub（违反架构红线 R-1 / ADR-001）：\n  "
            + string.Join("\n  ", offenders)
            + "\n请改为 real.* 实现，或明确的 NotConfigured 降级。");
    }

    [Fact]
    public void No_register_line_uses_stub_prefix()
    {
        var src = File.ReadAllText(FindRepoFile("src/Engine.Server/Routing/EngineAssembly.cs"));
        // 仅拦截假成功桩；system.* 的 SystemPing/SystemInfo 等是真实实现，允许保留 stub 前缀
        var lines = src.Split('\n')
            .Select((l, i) => (l, n: i + 1))
            .Where(x => x.l.Contains("(\"", StringComparison.Ordinal)
                        && (x.l.Contains("stub.EmptyOk", StringComparison.Ordinal)
                            || x.l.Contains("stub.EmptyList", StringComparison.Ordinal)
                            || x.l.Contains("stub.NotImplemented", StringComparison.Ordinal)))
            .Select(x => $"{x.n}: {x.l.Trim()}")
            .ToList();

        Assert.True(lines.Count == 0, "发现 stub 注册行：\n" + string.Join("\n", lines));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
