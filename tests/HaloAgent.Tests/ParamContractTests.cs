#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.Validation;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>参数契约：file_path 规范字段 + 旧 path 别名兼容 + 全工具顶层 schema 形态 + goal update percent。</summary>
public sealed class ParamContractTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _projectDir;
    private readonly ToolRegistry _registry;
    private readonly ToolContext _context;

    public ParamContractTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HaloParamContract_" + Guid.NewGuid().ToString("N")
        );
        _projectDir = Path.Combine(_tempRoot, "proj");
        Directory.CreateDirectory(_projectDir);
        File.WriteAllText(Path.Combine(_projectDir, "hello.txt"), "hello contract\nsecond line\n");
        _registry = new ToolRegistry();
        _context = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = "ws",
            SessionId = "sess",
            DataRoot = _tempRoot,
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
    }

    private Task<ToolResult> Call(string tool, string argsJson) =>
        _registry.DispatchAsync(
            new ToolCallDto("c-" + Guid.NewGuid().ToString("N"), tool, argsJson),
            _context
        );

    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void DirectoryBrowsing_PrefersCanonicalPathField()
    {
        var r = ToolCallValidator.Validate("list_dir", "{\"file_path\":\".\"}");
        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("ALIASED_PARAM"));
        Assert.Contains("file_path", r.NormalizedJson);
    }

    [Fact]
    public async Task DirectoryBrowsing_PrefersCanonicalPathFieldExecutes()
    {
        var r = await Call("list_dir", Args(new { file_path = "." }));
        Assert.True(r.Success, r.Error);
        Assert.DoesNotContain(
            r.ValidatedWarnings ?? Enumerable.Empty<string>(),
            w => w.Contains("ALIASED_PARAM")
        );
    }

    [Fact]
    public void DirectoryBrowsing_LegacyPathFieldStillLists()
    {
        var r = ToolCallValidator.Validate("list_dir", "{\"path\":\".\"}");
        Assert.True(r.Success, r.Message);
        Assert.Contains("file_path", r.NormalizedJson);
    }

    [Fact]
    public async Task DirectoryBrowsing_LegacyPathFieldStillExecutes()
    {
        var r = await Call("list_dir", "{\"path\":\".\"}");
        Assert.True(r.Success, r.Error);
    }

    [Fact]
    public void ContentSearch_PrefersCanonicalPathField()
    {
        var r = ToolCallValidator.Validate(
            "grep_search",
            "{\"pattern\":\"hello\",\"file_path\":\".\"}"
        );
        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("ALIASED_PARAM"));
    }

    [Fact]
    public async Task ContentSearch_PrefersCanonicalPathFieldExecutes()
    {
        var r = await Call("grep_search", Args(new { pattern = "hello", file_path = "." }));
        Assert.True(r.Success, r.Error);
        Assert.Contains("hello.txt", r.Output);
        Assert.DoesNotContain(
            r.ValidatedWarnings ?? Enumerable.Empty<string>(),
            w => w.Contains("ALIASED_PARAM")
        );
    }

    [Fact]
    public void ContentSearch_LegacyPathFieldStillSearches()
    {
        var r = ToolCallValidator.Validate("grep_search", "{\"pattern\":\"hello\",\"path\":\".\"}");
        Assert.True(r.Success, r.Message);
    }

    [Fact]
    public async Task ContentSearch_LegacyPathFieldStillExecutes()
    {
        var r = await Call("grep_search", Args(new { pattern = "hello", path = "." }));
        Assert.True(r.Success, r.Error);
        Assert.Contains("hello.txt", r.Output);
    }

    [Fact]
    public void AllTools_ExportWellFormedTopLevelSchema()
    {
        var defs = _registry.GetDefinitions();
        Assert.NotEmpty(defs);
        foreach (var d in defs)
        {
            using var doc = JsonDocument.Parse(d.ParametersJson);
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.True(root.TryGetProperty("type", out var t) && t.GetString() == "object");
            Assert.True(
                root.TryGetProperty("properties", out var props)
                    && props.ValueKind == JsonValueKind.Object
            );
            Assert.True(
                root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            );
            Assert.True(
                root.TryGetProperty("additionalProperties", out var ap)
                    && ap.ValueKind == JsonValueKind.False
            );
            foreach (var prop in props.EnumerateObject())
            {
                Assert.True(prop.Value.ValueKind == JsonValueKind.Object);
                Assert.True(prop.Value.TryGetProperty("type", out var pt));
                string? ptName = pt.GetString();
                Assert.Contains(
                    ptName,
                    new[] { "string", "integer", "boolean", "array", "object" }
                );
                if (prop.Value.TryGetProperty("enum", out var en))
                {
                    Assert.Equal(JsonValueKind.Array, en.ValueKind);
                    Assert.NotEmpty(en.EnumerateArray());
                }
            }
            foreach (var name in req.EnumerateArray())
            {
                Assert.True(
                    props.TryGetProperty(name.GetString() ?? string.Empty, out _),
                    $"required '{name}' missing in properties of {d.Name}"
                );
            }
        }

        using var listDoc = JsonDocument.Parse(
            defs.First(d => d.Name == "list_dir").ParametersJson
        );
        var listProps = listDoc.RootElement.GetProperty("properties");
        Assert.True(listProps.TryGetProperty("file_path", out _));
        Assert.False(listProps.TryGetProperty("path", out _));

        using var grepDoc = JsonDocument.Parse(
            defs.First(d => d.Name == "grep_search").ParametersJson
        );
        var grepProps = grepDoc.RootElement.GetProperty("properties");
        Assert.True(grepProps.TryGetProperty("file_path", out _));
        Assert.True(grepProps.TryGetProperty("pattern", out _));
        Assert.False(grepProps.TryGetProperty("path", out _));
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static GoalService NewGoalService(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "halopc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new GoalService(root);
    }

    [Fact]
    public void SessionGoal_UpdateAppliesExplicitPercent()
    {
        var svc = NewGoalService(out _);
        var cur = new SessionGoal { Percent = 10 };
        cur = svc.ApplyAction(cur, "update", J("{\"percent\":100}"));
        Assert.Equal(100, cur.Percent);
    }

    [Fact]
    public void SessionGoal_UpdateWithoutPercentKeepsExisting()
    {
        var svc = NewGoalService(out _);
        var cur = new SessionGoal { Percent = 42 };
        cur = svc.ApplyAction(cur, "update", J("{\"note\":\"n\"}"));
        Assert.Equal(42, cur.Percent);
        Assert.Equal("n", cur.Note);
    }

    [Fact]
    public void SessionGoal_UpdateClampsOutOfRangePercent()
    {
        var svc = NewGoalService(out _);
        var cur = new SessionGoal { Percent = 10 };
        cur = svc.ApplyAction(cur, "update", J("{\"percent\":150}"));
        Assert.Equal(100, cur.Percent);
        cur = svc.ApplyAction(cur, "update", J("{\"percent\":-5}"));
        Assert.Equal(0, cur.Percent);
    }

    [Fact]
    public void SessionGoal_ProgressStillUpdatesPercent()
    {
        var svc = NewGoalService(out _);
        var cur = new SessionGoal { Percent = 10 };
        cur = svc.ApplyAction(cur, "progress", J("{\"percent\":40}"));
        Assert.Equal(40, cur.Percent);
        cur = svc.ApplyAction(cur, "progress", J("{\"percent\":150}"));
        Assert.Equal(100, cur.Percent);
    }
}
