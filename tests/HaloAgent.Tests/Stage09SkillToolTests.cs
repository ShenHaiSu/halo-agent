#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need09-07/08 §1：skill/memory 工具 CRUD/默认域/配额/校验。</summary>
public sealed class Stage09SkillToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;

    public Stage09SkillToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage09Skill_{Guid.NewGuid():N}");
        _workspace = Path.Combine(_tempRoot, "ws");
        _dataRoot = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    private ToolContext Ctx(bool withWorkspace = true) =>
        new()
        {
            WorkspaceRoot = withWorkspace ? _workspace : string.Empty,
            WorkspaceId = "ws_test",
            SessionId = "sess_test",
            DataRoot = _dataRoot,
        };

    private static ToolCallDto SkillCall(string argsJson) => new("c1", "skill_tool", argsJson);

    private static ToolCallDto MemoryCall(string argsJson) => new("c1", "memory_tool", argsJson);

    [Fact(DisplayName = "S09-S1: 缺 scope 默认写 project")]
    public async Task MissingScope_DefaultsToProject()
    {
        var tool = new SkillTool();
        var created = await tool.ExecuteAsync(
            SkillCall(
                "{\"action\":\"create\",\"name\":\"git-helper\",\"content\":\"# Git\\n常用流程\"}"
            ),
            Ctx()
        );
        Assert.True(created.Success, created.Error);
        Assert.Contains("scope=project", created.Output);
        Assert.Contains("下轮生效", created.Output);
        Assert.True(
            File.Exists(Path.Combine(_workspace, ".haloagent", "skills", "git-helper", "readme.md"))
        );
        Assert.True(File.Exists(Path.Combine(_workspace, ".haloagent", "skills", "_index.json")));
    }

    [Fact(DisplayName = "S09-S2: list/get 往返 + 表格 20 截断语义")]
    public async Task ListGet_Roundtrip()
    {
        var tool = new SkillTool();
        await tool.ExecuteAsync(
            SkillCall("{\"action\":\"create\",\"name\":\"aa-first\",\"content\":\"# AA\\n正文一\"}"),
            Ctx()
        );
        await tool.ExecuteAsync(
            SkillCall(
                "{\"action\":\"create\",\"name\":\"zz-second\",\"content\":\"# ZZ\\n正文二\",\"scope\":\"global\"}"
            ),
            Ctx()
        );
        var listed = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"list\",\"scope\":\"all\"}"),
            Ctx()
        );
        Assert.True(listed.Success, listed.Error);
        Assert.Contains("| scope | name | pin | enabled | estTokens |", listed.Output);
        Assert.Contains("aa-first", listed.Output);
        Assert.Contains("zz-second", listed.Output);
        var got = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"get\",\"name\":\"aa-first\"}"),
            Ctx()
        );
        Assert.True(got.Success, got.Error);
        Assert.Contains("正文一", got.Output);
        Assert.Contains("pin=auto", got.Output);
    }

    [Fact(DisplayName = "S09-S3: update 不存在的拒绝（不转 create）")]
    public async Task UpdateMissing_Rejected_NoAutoCreate()
    {
        var tool = new SkillTool();
        var result = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"update\",\"name\":\"no-such\",\"content\":\"x\"}"),
            Ctx()
        );
        Assert.False(result.Success);
        Assert.Contains("拒绝自动转 create", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_workspace, ".haloagent", "skills", "no-such")));
    }

    [Fact(DisplayName = "S09-S4: content 超 8000 拒绝")]
    public async Task OversizeContent_Rejected()
    {
        var tool = new SkillTool();
        var result = await tool.ExecuteAsync(
            SkillCall(
                "{\"action\":\"create\",\"name\":\"big-one\",\"content\":\""
                    + new string('x', 8001)
                    + "\"}"
            ),
            Ctx()
        );
        Assert.False(result.Success);
        Assert.Contains("正文超限", result.Error);
    }

    [Fact(DisplayName = "S09-S5: 非法名 SKILL_VALIDATION + 路径遍历 PATH_TRAVERSAL")]
    public async Task BadNames_Rejected()
    {
        var tool = new SkillTool();
        var bad = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"create\",\"name\":\"Bad Name!\",\"content\":\"x\"}"),
            Ctx()
        );
        Assert.False(bad.Success);
        Assert.Contains("SKILL_VALIDATION", bad.Error);
        var trav = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"get\",\"name\":\"../evil\"}"),
            Ctx()
        );
        Assert.False(trav.Success);
        Assert.Contains("PATH_TRAVERSAL", trav.Error);
        var con = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"create\",\"name\":\"con\",\"content\":\"x\"}"),
            Ctx()
        );
        Assert.False(con.Success);
        Assert.Contains("SKILL_VALIDATION", con.Error);
    }

    [Fact(DisplayName = "S09-S6: memory 默认 pin=pinned + enable/disable + delete 留 bak")]
    public async Task Memory_DefaultPinned_EnableDelete()
    {
        var tool = new MemoryTool();
        var created = await tool.ExecuteAsync(
            MemoryCall(
                "{\"action\":\"create\",\"name\":\"proj-brief\",\"content\":\"# 简介\\n技术栈\"}"
            ),
            Ctx()
        );
        Assert.True(created.Success, created.Error);
        var got = await tool.ExecuteAsync(
            MemoryCall("{\"action\":\"get\",\"name\":\"proj-brief\"}"),
            Ctx()
        );
        Assert.True(got.Success, got.Error);
        Assert.Contains("pin=pinned", got.Output);
        var disabled = await tool.ExecuteAsync(
            MemoryCall("{\"action\":\"disable\",\"name\":\"proj-brief\"}"),
            Ctx()
        );
        Assert.True(disabled.Success, disabled.Error);
        Assert.Contains("停用", disabled.Output);
        var deleted = await tool.ExecuteAsync(
            MemoryCall("{\"action\":\"delete\",\"name\":\"proj-brief\"}"),
            Ctx()
        );
        Assert.True(deleted.Success, deleted.Error);
        Assert.Contains("备份", deleted.Output);
        var baks = Directory.GetDirectories(
            Path.Combine(_workspace, ".haloagent", "memory"),
            "*.bak"
        );
        Assert.Single(baks);
    }

    [Fact(DisplayName = "S09-S7: 无工作区 list=all 只返 Global")]
    public async Task NoWorkspace_ListAll_GlobalOnly()
    {
        var tool = new SkillTool();
        await tool.ExecuteAsync(
            SkillCall(
                "{\"action\":\"create\",\"name\":\"g-one\",\"content\":\"# G\\n正文\",\"scope\":\"global\"}"
            ),
            Ctx()
        );
        var listed = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"list\",\"scope\":\"all\"}"),
            Ctx(withWorkspace: false)
        );
        Assert.True(listed.Success, listed.Error);
        Assert.Contains("g-one", listed.Output);
        Assert.Contains("(no workspace)", listed.Output);
        var createP = await tool.ExecuteAsync(
            SkillCall("{\"action\":\"create\",\"name\":\"p-one\",\"content\":\"x\"}"),
            Ctx(withWorkspace: false)
        );
        Assert.False(createP.Success);
    }

    [Fact(DisplayName = "S09-S8: pin=full 警告 token 常驻")]
    public async Task PinFull_WarnsTokens()
    {
        var tool = new SkillTool();
        var created = await tool.ExecuteAsync(
            SkillCall(
                "{\"action\":\"create\",\"name\":\"full-one\",\"content\":\"# F\\n正文\",\"pin\":\"full\"}"
            ),
            Ctx()
        );
        Assert.True(created.Success, created.Error);
        Assert.Contains("pin=full", created.Output);
        Assert.Contains("tokens/轮", created.Output);
        var badPin = await tool.ExecuteAsync(
            SkillCall(
                "{\"action\":\"create\",\"name\":\"bad-pin\",\"content\":\"x\",\"pin\":\"always\"}"
            ),
            Ctx()
        );
        Assert.False(badPin.Success);
        Assert.Contains("SKILL_VALIDATION", badPin.Error);
    }
}
