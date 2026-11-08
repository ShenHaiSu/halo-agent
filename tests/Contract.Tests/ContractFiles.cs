using System.Text.Json;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>
/// Shared file access for the frozen contract: locates the repository root
/// from the test output directory and reads the schema plus generated types.
/// </summary>
internal static class ContractFiles
{
    public static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "HaloAgent.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new DirectoryNotFoundException(
            "Cannot locate HaloAgent.sln; repository root not found."
        );
    }

    public static JsonDocument LoadSchema()
    {
        string path = Path.Combine(FindRepoRoot(), "contract", "agent.schema.json");
        string text = File.ReadAllText(path);
        return JsonDocument.Parse(text);
    }

    public static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        Assert.True(
            root.TryGetProperty(name, out var element),
            $"Schema is missing top-level array '{name}'."
        );
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, item.ValueKind);
            items.Add(item.GetString()!);
        }

        return items;
    }

    public static string ReadGeneratedTypes()
    {
        string path = Path.Combine(
            FindRepoRoot(),
            "src",
            "Web.Client",
            "src",
            "contract",
            "agent.d.ts"
        );
        return File.ReadAllText(path);
    }
}
