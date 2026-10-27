using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Core.Tools.Builtin;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

public class GlobMatchTests
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "halo-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonElement Args(string pattern) =>
        JsonDocument.Parse("{\"pattern\":\"" + pattern + "\"}").RootElement.Clone();

    private static async Task<string> RunAsync(string root, string pattern)
    {
        var tool = new GlobTool();
        var ctx = new ToolContext { Cwd = root };
        var result = await tool.ExecuteAsync(Args(pattern), ctx, CancellationToken.None);
        return result.Output;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Star_matches_txt_but_not_log()
    {
        var root = NewRoot();
        try
        {
            var sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.txt"), "x");
            File.WriteAllText(Path.Combine(sub, "b.log"), "x");

            var output = await RunAsync(root, "*.txt");

            Assert.Contains("a.txt", output, StringComparison.Ordinal);
            Assert.DoesNotContain("b.log", output, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Question_mark_matches_single_char()
    {
        var root = NewRoot();
        try
        {
            var sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.txt"), "x");
            File.WriteAllText(Path.Combine(sub, "ab.txt"), "x");

            var output = await RunAsync(root, "?.txt");

            Assert.Contains("a.txt", output, StringComparison.Ordinal);
            Assert.DoesNotContain("ab.txt", output, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Adversarial_star_pattern_returns_quickly()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, new string('a', 30) + ".txt"), "x");
            var pattern = "*a*a*a*a*a*a*a*a*a*aab";

            var sw = Stopwatch.StartNew();
            var output = await RunAsync(root, pattern);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "glob took " + sw.Elapsed);
            Assert.Contains("没有匹配", output, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
