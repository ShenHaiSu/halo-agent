using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace HaloAgent.Contract.Tests;

public sealed class ThreeWayConsistencyTests
{
    private static JsonDocument Schema => LoadSchema();

    private static JsonDocument LoadSchema()
    {
        var path = FindRepoFile("contract/agent.schema.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Registered_engine_methods_match_known_methods()
    {
        var schemaMethods = Schema.RootElement.GetProperty("methods")
            .EnumerateObject().Select(p => p.Name).ToHashSet();

        var engineInSchema = schemaMethods
            .Where(m =>
            {
                var def = Schema.RootElement.GetProperty("methods").GetProperty(m);
                if (def.TryGetProperty("implementedIn", out var impl))
                    return impl.GetString() != "host";
                return true;
            })
            .ToHashSet(StringComparer.Ordinal);

        var known = KnownMethods.All
            .Where(m => !m.StartsWith("fs.browse") && !m.StartsWith("fs.pick")
                        && m != "fs.readImageAsBase64")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            known.OrderBy(x => x).ToArray(),
            engineInSchema.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Engine_assembly_registers_all_engine_methods()
    {
        var src = File.ReadAllText(FindRepoFile("src/Engine.Server/Routing/EngineAssembly.cs"));
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src, @"\(""(?<m>[a-z]+\.[a-zA-Z\.]+)""\s*,"))
            registered.Add(m.Groups["m"].Value);

        var schemaMethods = Schema.RootElement.GetProperty("methods")
            .EnumerateObject().Select(p => p.Name).ToHashSet();
        var engineInSchema = schemaMethods
            .Where(m =>
            {
                var def = Schema.RootElement.GetProperty("methods").GetProperty(m);
                if (def.TryGetProperty("implementedIn", out var impl))
                    return impl.GetString() != "host";
                return true;
            })
            .ToHashSet(StringComparer.Ordinal);

        var missing = engineInSchema.Except(registered).ToList();
        Assert.True(missing.Count == 0,
            "契约有但引擎未注册（D-03 那种漏项）：\n  " + string.Join("\n  ", missing));
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
