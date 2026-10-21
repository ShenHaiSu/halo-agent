#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need09-01 §10 边角表：双域 Skill/Memory 存储验收。
/// </summary>
public sealed class Stage09SkillMemoryStorageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SkillMemoryService _skills;
    private readonly string _projectDir;

    public Stage09SkillMemoryStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage09Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage09Bootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );

        _skills = new SkillMemoryService(_storage);

        _projectDir = Path.Combine(_tempRoot, "Projects", "SkillMemTest");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        { /* 忽略测试临时目录清理异常 */
        }
    }

    [Fact(DisplayName = "S09: Global 目录随 Initialize 自动补建")]
    public void Init_GlobalDirsExist()
    {
        Assert.True(Directory.Exists(_storage.GlobalSkillsDirectory));
        Assert.True(Directory.Exists(_storage.GlobalMemoryDirectory));
        Assert.True(File.Exists(Path.Combine(_storage.GlobalSkillsDirectory, "_index.json")));
        Assert.True(File.Exists(Path.Combine(_storage.GlobalMemoryDirectory, "_index.json")));
    }

    [Theory(DisplayName = "S09-E1: 非法名拒绝 SKILL_VALIDATION 且不写盘")]
    [InlineData("../x")]
    [InlineData("CON")]
    [InlineData("com1")]
    [InlineData("LPT1")]
    [InlineData("a")]
    [InlineData("_index")]
    [InlineData(".hidden")]
    [InlineData("bad/name")]
    [InlineData("..")]
    [InlineData("")]
    public void InvalidNames_Rejected(string name)
    {
        var before = Directory
            .GetDirectories(_storage.GlobalSkillsDirectory)
            .Select(d => d.ToLowerInvariant())
            .ToHashSet();
        var ex = Assert.Throws<SkillMemoryValidationException>(
            () =>
                _skills.CreateOrUpdate(
                    SkillMemoryScope.Global,
                    SkillMemoryKind.Skill,
                    name,
                    "# t\nbody"
                )
        );
        Assert.Contains("SKILL_VALIDATION", ex.Message);
        var after = Directory
            .GetDirectories(_storage.GlobalSkillsDirectory)
            .Select(d => d.ToLowerInvariant())
            .ToHashSet();
        Assert.Equal(before, after); // 不写盘：域内无新增目录
    }

    [Fact(DisplayName = "S09-E1: 超长名（49字符）拒绝")]
    public void TooLongName_Rejected()
    {
        string name = new string('a', 49);
        var ex = Assert.Throws<SkillMemoryValidationException>(
            () =>
                _skills.CreateOrUpdate(SkillMemoryScope.Global, SkillMemoryKind.Skill, name, "body")
        );
        Assert.Contains("SKILL_VALIDATION", ex.Message);
    }

    [Fact(DisplayName = "S09: 大小写归一（MySkill 与 myskill 同一名）")]
    public void CaseInsensitive_Normalized()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "MySkill",
            "# Title\nhello"
        );
        var got = _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Skill, "myskill", null);
        Assert.NotNull(got);
        Assert.Equal("myskill", got!.Name);
        // 后写覆盖前者归一化为小写：只存在小写目录
        Assert.True(Directory.Exists(Path.Combine(_storage.GlobalSkillsDirectory, "myskill")));
        var list = _skills.List(SkillMemoryScope.Global, SkillMemoryKind.Skill, null);
        Assert.Single(list.Where(e => e.Name == "myskill"));
    }

    [Fact(DisplayName = "S09-E2: 缺 readme 的目录被跳过")]
    public void MissingReadme_Skipped()
    {
        Directory.CreateDirectory(Path.Combine(_storage.GlobalSkillsDirectory, "no-readme"));
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "good-one",
            "# 好\n正文"
        );
        var list = _skills.List(SkillMemoryScope.Global, SkillMemoryKind.Skill, null);
        Assert.DoesNotContain(list, e => e.Name == "no-readme");
        Assert.Contains(list, e => e.Name == "good-one");
    }

    [Fact(DisplayName = "S09-E3: _index.json 损坏自动重建，会话不报错")]
    public void CorruptIndex_Rebuilt()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "idx-ok",
            "# T\nbody"
        );
        string indexPath = Path.Combine(_storage.GlobalSkillsDirectory, "_index.json");
        File.WriteAllText(indexPath, "{{{not-json###");
        var list = _skills.List(SkillMemoryScope.Global, SkillMemoryKind.Skill, null);
        Assert.Contains(list, e => e.Name == "idx-ok");
        string rebuilt = File.ReadAllText(indexPath);
        Assert.Contains("\"version\": 1", rebuilt);
    }

    [Fact(DisplayName = "S09-E4: Project 空域不阻塞 Global")]
    public void ProjectEmpty_DoesNotBlockGlobal()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "g-only",
            "# G\nbody"
        );
        string ghostWs = Path.Combine(_tempRoot, "NoSuchProject");
        var merged = _skills.GetMergedForPrompt(ghostWs);
        Assert.Contains(merged.GlobalSkills, e => e.Name == "g-only");
        Assert.Empty(merged.ProjectSkills);
        Assert.Empty(merged.ProjectMemory);
    }

    [Fact(DisplayName = "S09-E5: 同名 Project 覆盖 Global（global 侧去重）")]
    public void SameName_ProjectOverridesGlobal()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "dup-name",
            "# G\n global body"
        );
        _skills.CreateOrUpdate(
            SkillMemoryScope.Project,
            SkillMemoryKind.Skill,
            "dup-name",
            "# P\nproject body",
            _projectDir
        );
        // 排序键 name 升序
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "aaa-first",
            "# A\nbody"
        );
        var merged = _skills.GetMergedForPrompt(_projectDir);
        Assert.DoesNotContain(merged.GlobalSkills, e => e.Name == "dup-name");
        var proj = Assert.Single(merged.ProjectSkills, e => e.Name == "dup-name");
        Assert.Contains("project body", proj.BodyMarkdown);
        Assert.Equal(
            merged.GlobalSkills.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal),
            merged.GlobalSkills.Select(e => e.Name)
        );
    }

    [Theory(DisplayName = "S09-E7: 资产 ../ 逃逸拒绝 PATH_TRAVERSAL")]
    [InlineData("../../secret.txt")]
    [InlineData("../escape")]
    [InlineData("assets/../../escape")]
    public void AssetTraversal_Rejected(string evil)
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "asset-vic",
            "# T\nbody"
        );
        var ex = Assert.Throws<SkillMemoryValidationException>(
            () =>
                _skills.ResolveAssetPath(
                    SkillMemoryScope.Global,
                    SkillMemoryKind.Skill,
                    "asset-vic",
                    evil,
                    null
                )
        );
        Assert.Contains("PATH_TRAVERSAL", ex.Message);
    }

    [Fact(DisplayName = "S09: 正常资产路径解析成功")]
    public void AssetResolve_Ok()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "asset-ok",
            "# T\nbody"
        );
        string abs = _skills.ResolveAssetPath(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "asset-ok",
            "assets/run.py",
            null
        );
        Assert.StartsWith(
            Path.GetFullPath(Path.Combine(_storage.GlobalSkillsDirectory, "asset-ok")),
            abs,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.EndsWith("run.py", abs);
    }

    [Fact(DisplayName = "S09-§7: 迁移白名单含 skills/memory 且迁移不丢")]
    public async Task Migration_WhitelistAndCarry()
    {
        Assert.Contains("skills", StoragePathManager.MigratableSubDirectoryNames);
        Assert.Contains("memory", StoragePathManager.MigratableSubDirectoryNames);
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "mig-skill",
            "# M\nbody"
        );
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Memory,
            "mig-mem",
            "remember this"
        );

        string newRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage09New_{Guid.NewGuid():N}"
        );
        try
        {
            var result = await _storage.ChangeDataRootAsync(newRoot, migrateExistingData: true);
            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(newRoot, "skills", "mig-skill", "readme.md")));
            Assert.True(File.Exists(Path.Combine(newRoot, "memory", "mig-mem", "readme.md")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(newRoot))
                    Directory.Delete(newRoot, recursive: true);
            }
            catch { }
        }
    }

    [Fact(DisplayName = "S09-§6: 单 readme 8000 字符超限拒绝")]
    public void Quota_ReadmeTooLong()
    {
        var ex = Assert.Throws<SkillMemoryValidationException>(
            () =>
                _skills.CreateOrUpdate(
                    SkillMemoryScope.Global,
                    SkillMemoryKind.Skill,
                    "too-big",
                    new string('x', 8001)
                )
        );
        Assert.Contains("SKILL_VALIDATION", ex.Message);
    }

    [Fact(DisplayName = "S09-§5: 删除留 .deleted bak 一份")]
    public void Delete_KeepsSingleBak()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "del-me",
            "# T\nbody"
        );
        _skills.Delete(SkillMemoryScope.Global, SkillMemoryKind.Skill, "del-me");
        Assert.Null(_skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Skill, "del-me", null));
        var baks = Directory.GetFileSystemEntries(
            _storage.GlobalSkillsDirectory,
            "del-me.deleted-*.bak"
        );
        Assert.Single(baks);
        Assert.True(File.Exists(Path.Combine(baks[0], "readme.md")));
    }

    [Fact(DisplayName = "S09: SetEnabled 开关翻转")]
    public void SetEnabled_Flips()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Memory,
            "toggle-me",
            "body text"
        );
        _skills.SetEnabled(
            SkillMemoryScope.Global,
            SkillMemoryKind.Memory,
            "toggle-me",
            null,
            false
        );
        Assert.False(
            _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Memory, "toggle-me", null)!.Enabled
        );
        _skills.SetEnabled(
            SkillMemoryScope.Global,
            SkillMemoryKind.Memory,
            "toggle-me",
            null,
            true
        );
        Assert.True(
            _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Memory, "toggle-me", null)!.Enabled
        );
    }

    [Fact(DisplayName = "S09: hand-written frontmatter 5 键解析 + skill.json 优先")]
    public void Frontmatter_And_JsonPrecedence()
    {
        string dir = Path.Combine(_storage.GlobalSkillsDirectory, "fm-test");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "readme.md"),
            "---\nversion: 3\nenabled: false\npriority: 7\npin: full\nscope_hint: \"手写场景\"\nunknown_key: 123\n---\n\n# 标题\n首句简介。第二句。\n"
        );
        var e = _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Skill, "fm-test", null);
        Assert.NotNull(e);
        Assert.Equal(3, e!.Version);
        Assert.False(e.Enabled);
        Assert.Equal(7, e.Priority);
        Assert.Equal("full", e.Pin);
        Assert.Equal("手写场景", e.Summary);
        // skill.json 优先：覆盖 enabled
        File.WriteAllText(Path.Combine(dir, "skill.json"), "{\"enabled\": true}");
        var e2 = _skills.Get(SkillMemoryScope.Global, SkillMemoryKind.Skill, "fm-test", null);
        Assert.NotNull(e2);
        Assert.True(e2!.Enabled);
        Assert.Equal(3, e2.Version); // json 缺键回落 frontmatter
    }

    [Fact(DisplayName = "S09: hash=SHA16(规范化正文)，\\r\\n 与 \\n 等价")]
    public void Hash_Normalized()
    {
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "hash-a",
            "line1\r\nline2"
        );
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "hash-b",
            "line1\nline2"
        );
        string ha = _skills
            .Get(SkillMemoryScope.Global, SkillMemoryKind.Skill, "hash-a", null)!
            .Hash;
        string hb = _skills
            .Get(SkillMemoryScope.Global, SkillMemoryKind.Skill, "hash-b", null)!
            .Hash;
        Assert.Equal(ha, hb);
        Assert.Equal(16, ha.Length);
    }

    [Fact(DisplayName = "S09: WorkspaceSessionService 建/删工作区的 .haloagent 语义")]
    public async Task Workspace_HaloagentLifecycle()
    {
        var repo = new SessionRepository(_storage);
        var wsService = new WorkspaceSessionService(repo);
        string wsRoot = Path.Combine(_tempRoot, "Projects", "WsLife");
        Directory.CreateDirectory(wsRoot);

        WorkspaceInfo ws = await wsService.AddOrUpdateWorkspaceAsync(wsRoot, "ws-life");
        Assert.True(Directory.Exists(Path.Combine(wsRoot, ".haloagent", "skills")));
        Assert.True(Directory.Exists(Path.Combine(wsRoot, ".haloagent", "memory")));

        bool removed = await wsService.RemoveWorkspaceAsync(ws.Id);
        Assert.True(removed);
        Assert.True(Directory.Exists(Path.Combine(wsRoot, ".haloagent", "skills"))); // 不删 .haloagent

        // 容忍 .haloagent 不存在：删掉后仍视为空域
        Directory.Delete(Path.Combine(wsRoot, ".haloagent"), recursive: true);
        var merged = _skills.GetMergedForPrompt(wsRoot);
        Assert.Empty(merged.ProjectSkills);
        Assert.Empty(merged.ProjectMemory);
    }
}
