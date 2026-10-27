using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace HaloAgent.Engine.Server.Tests.Guards;

public sealed class MutatingMethodsGuardTests
{
    private static IReadOnlyList<string> MutatingMethods()
    {
        var f = typeof(FrameServer).GetField(
            "MutatingMethods", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((IEnumerable<string>)f.GetValue(null)!).ToList();
    }

    private static IReadOnlyCollection<string> RegisteredMethods()
    {
        var path = FindRepoFile("src/Engine.Server/Routing/EngineAssembly.cs");
        var src = File.ReadAllText(path);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src, @"\(""(?<m>[a-z]+\.[a-zA-Z\.]+)""\s*,"))
        {
            set.Add(m.Groups["m"].Value);
        }
        return set;
    }

    [Fact]
    public void Every_mutating_name_is_registered()
    {
        var registered = RegisteredMethods();
        var orphans = MutatingMethods().Where(m => !registered.Contains(m)).ToList();

        Assert.True(orphans.Count == 0,
            "MutatingMethods 含未注册的方法名（拼写漂移）：\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Every_write_method_is_locked()
    {
        var registered = RegisteredMethods();
        var mutating = MutatingMethods().ToHashSet(StringComparer.Ordinal);

        var writeVerbs = new[]
        {
            ".save", ".delete", ".set", ".rename", ".run", ".install",
            ".rollback", ".clear", ".compact",
        };
        var extraWriteMethods = new[]
        {
            "agent.prompt", "agent.queue", "agent.unqueue", "agent.cancel",
            "agent.answer", "agent.drain", "session.new", "session.resume",
            "system.shutdown",
        };

        var expected = registered
            .Where(m => writeVerbs.Any(v => m.EndsWith(v, StringComparison.Ordinal))
                        || extraWriteMethods.Contains(m))
            .ToList();

        var missing = expected.Where(m => !mutating.Contains(m)).ToList();

        Assert.True(missing.Count == 0,
            "以下写操作方法未加入 MutatingMethods（存在并发竞态风险）：\n  "
            + string.Join("\n  ", missing));
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
