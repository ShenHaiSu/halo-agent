#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need10-04 §2：Skill/Memory UI Bridge 验收（T1-T6）。</summary>
public sealed class Stage10SkillMemoryUiTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SkillMemoryService _skills;
    private readonly WorkspaceSessionService _wsService;
    private readonly HostBridgeFacade _bridge;
    private readonly string _projectDir;

    public Stage10SkillMemoryUiTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage10Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage10Bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);

        _skills = new SkillMemoryService(_storage);
        var repo = new SessionRepository(_storage);
        _wsService = new WorkspaceSessionService(repo);
        _bridge = new HostBridgeFacade(null, _storage, workspaceSessionService: _wsService, skillMemoryService: _skills);

        _projectDir = Path.Combine(_tempRoot, "Projects", "SkillMemUi");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot)) Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    private async Task<string> AddWorkspaceAsync()
    {
        var ws = await _wsService.AddOrUpdateWorkspaceAsync(_projectDir, "ui-test");
        return ws.Id;
    }

    [Fact(DisplayName = "T1: List_all_无工作区只返Global")]
    public async Task T1_ListAll_NoWorkspace_ReturnsGlobalOnly()
    {
        _skills.CreateOrUpdate(SkillMemoryScope.Global, SkillMemoryKind.Skill, "t1-demo", "# T1\nbody one");
        var res = await _bridge.ListSkillMemory("skill", "all", null);
        Assert.True(res.Ok);
        Assert.Contains(res.Data!, d => d.Name == "t1-demo" && d.Scope == "global");
        Assert.Equal("(no workspace)", res.Message);
        var proj = await _bridge.ListSkillMemory("skill", "project", null);
        Assert.True(proj.Ok);
        Assert.Empty(proj.Data!);
    }

    [Fact(DisplayName = "T2: Scope_all_写拒绝")]
    public async Task T2_WriteWithScopeAll_Rejected()
    {
        _skills.CreateOrUpdate(SkillMemoryScope.Global, SkillMemoryKind.Skill, "t2-demo", "# T2\nbody");
        var set = await _bridge.SetSkillMemoryEnabled("skill", "all", "t2-demo", false, null);
        Assert.False(set.Ok);
        Assert.Equal("SKILL_VALIDATION", set.Code);
        var del = await _bridge.DeleteSkillMemory("skill", "all", "t2-demo", null);
        Assert.False(del.Ok);
        Assert.Equal("SKILL_VALIDATION", del.Code);
    }

    [Theory(DisplayName = "T3: 非法名拒绝")]
    [InlineData("../x")]
    [InlineData("CON")]
    [InlineData("bad/name")]
    [InlineData("a")]
    public async Task T3_InvalidNames_Rejected(string name)
    {
        var detail = await _bridge.GetSkillMemoryDetail("skill", "global", name, null);
        Assert.False(detail.Ok);
        Assert.True(detail.Code == "SKILL_VALIDATION" || detail.Code == "PATH_TRAVERSAL");
        var del = await _bridge.DeleteSkillMemory("skill", "global", name, null);
        Assert.False(del.Ok);
        Assert.True(del.Code == "SKILL_VALIDATION" || del.Code == "PATH_TRAVERSAL");
    }

    [Fact(DisplayName = "T4: 删除留bak")]
    public async Task T4_Delete_KeepsBak()
    {
        _skills.CreateOrUpdate(SkillMemoryScope.Global, SkillMemoryKind.Skill, "t4-demo", "# T4\nbye");
        var del = await _bridge.DeleteSkillMemory("skill", "global", "t4-demo", null);
        Assert.True(del.Ok);
        Assert.NotNull(del.Data!.BakFileName);
        Assert.Contains(".deleted-", del.Data!.BakFileName);
        Assert.Contains(".bak", del.Data!.BakFileName);
        var baks = Directory.GetFileSystemEntries(_storage.GlobalSkillsDirectory, "t4-demo.deleted-*.bak");
        Assert.Single(baks);
        var list = await _bridge.ListSkillMemory("skill", "global", null);
        Assert.DoesNotContain(list.Data!, d => d.Name == "t4-demo");
    }

    [Fact(DisplayName = "T5: 开关保留正文")]
    public async Task T5_SetEnabled_KeepsBody()
    {
        _skills.CreateOrUpdate(SkillMemoryScope.Global, SkillMemoryKind.Memory, "t5-demo", "# T5\nkeep me");
        var before = _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Memory, "t5-demo", null)!;
        var off = await _bridge.SetSkillMemoryEnabled("memory", "global", "t5-demo", false, null);
        Assert.True(off.Ok);
        var after = _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Memory, "t5-demo", null)!;
        Assert.False(after.Enabled);
        Assert.Equal(before.BodyMarkdown, after.BodyMarkdown);
        Assert.Equal(before.Hash, after.Hash);
        var on = await _bridge.SetSkillMemoryEnabled("memory", "global", "t5-demo", true, null);
        Assert.True(on.Ok);
        var back = _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Memory, "t5-demo", null)!;
        Assert.True(back.Enabled);
        Assert.Equal(before.BodyMarkdown, back.BodyMarkdown);
    }

    [Fact(DisplayName = "T6: Project覆盖Global读合并")]
    public async Task T6_MergedAll_ProjectOverridesGlobal()
    {
        string wsId = await AddWorkspaceAsync();
        var ws = await _wsService.GetWorkspaceAsync(wsId);
        Assert.NotNull(ws);
        _skills.CreateOrUpdate(SkillMemoryScope.Global, SkillMemoryKind.Skill, "dup-name", "# G\nfrom global");
        _skills.CreateOrUpdate(SkillMemoryScope.Project, SkillMemoryKind.Skill, "dup-name", "# P\nfrom project", ws!.RootPath);
        var all = await _bridge.ListSkillMemory("skill", "all", wsId);
        Assert.True(all.Ok);
        var dups = all.Data!.Where(d => d.Name == "dup-name").ToList();
        Assert.Single(dups);
        Assert.Equal("project", dups[0].Scope);
        // E3：无工作区 + project 写 → SKILL_VALIDATION
        var badWrite = await _bridge.SetSkillMemoryEnabled("skill", "project", "dup-name", false, null);
        Assert.False(badWrite.Ok);
        Assert.Equal("SKILL_VALIDATION", badWrite.Code);
        // E6：删除不存在条目 → SKILL_NOT_FOUND
        var noDel = await _bridge.DeleteSkillMemory("skill", "global", "no-such-item", null);
        Assert.False(noDel.Ok);
        Assert.Equal("SKILL_NOT_FOUND", noDel.Code);
    }
}
