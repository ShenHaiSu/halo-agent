#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// Stage27：工作区别名（纯前端视觉）。
/// 验收要点：
/// 1) Alias 正确持久化并可查询/修改；
/// 2) 别名绝不进入提示词 {{WorkspaceName}}、请求头 ${workspace-name}、会话标题上下文；
/// 3) Name / RootPath / WorkspaceHash 等既有字段不受影响；
/// 4) 老数据（无 Alias 键）兼容。
/// </summary>
public sealed class Stage27WorkspaceAliasTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;

    public Stage27WorkspaceAliasTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage27Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage27Bootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );

        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
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
        {
            // 测试临时目录清理失败可忽略
        }
    }

    private async Task<WorkspaceInfo> SeedWorkspaceAsync(string folderName = "AliasProject")
    {
        string projPath = Path.Combine(_tempRoot, "Projects", folderName);
        Directory.CreateDirectory(projPath);
        return await _service.AddOrUpdateWorkspaceAsync(projPath, customName: null);
    }

    private string WorkspacesIndexPath =>
        Path.Combine(_tempRoot, "workspaces", "workspaces-index.json");

    private static string Sha256OfFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ─────────────────────────── 基础 CRUD ───────────────────────────

    [Fact(DisplayName = "S27-AC01: 设置别名后正确持久化，且 Name/RootPath/Hash 等字段原封不动")]
    public async Task AC01_SetAlias_Persists_Without_Touching_Other_Fields()
    {
        var ws = await SeedWorkspaceAsync();
        string beforeName = ws.Name;
        string beforeRoot = ws.RootPath;
        string beforeHash = ws.WorkspaceHash;
        DateTime beforeCreated = ws.CreatedAtUtc;
        Assert.Null(ws.Alias);
        Assert.Equal(beforeName, ws.DisplayName); // 未设别名时 DisplayName 回退 Name

        var updated = await _service.SetWorkspaceAliasAsync(ws.Id, "  前端别名  ");

        Assert.NotNull(updated);
        Assert.Equal("前端别名", updated!.Alias); // 首尾空白被 Trim
        Assert.Equal("前端别名", updated.DisplayName);
        Assert.Equal(beforeName, updated.Name); // Name 不变（提示词/请求头数据源）
        Assert.Equal(beforeRoot, updated.RootPath);
        Assert.Equal(beforeHash, updated.WorkspaceHash);
        Assert.Equal(beforeCreated, updated.CreatedAtUtc);

        // 重新从磁盘加载，确认真的落盘
        var reloaded = await _service.GetWorkspaceAsync(ws.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("前端别名", reloaded!.Alias);
        Assert.Equal(beforeName, reloaded.Name);

        // index 文件里确实出现 alias 键（全局 STJ 采用 camelCase 策略）
        string json = await File.ReadAllTextAsync(WorkspacesIndexPath);
        Assert.Contains("\"alias\"", json);
        Assert.Contains("前端别名", json);
    }

    [Fact(DisplayName = "S27-AC02: 空串/空白/null 均清除别名并回退显示 Name")]
    public async Task AC02_ClearAlias_Falls_Back_To_Name()
    {
        var ws = await SeedWorkspaceAsync();
        await _service.SetWorkspaceAliasAsync(ws.Id, "临时别名");

        foreach (string? clearing in new[] { "", "   ", null })
        {
            await _service.SetWorkspaceAliasAsync(ws.Id, "临时别名"); // 复位
            var cleared = await _service.SetWorkspaceAliasAsync(ws.Id, clearing);

            Assert.NotNull(cleared);
            Assert.Null(cleared!.Alias);
            Assert.Equal(cleared.Name, cleared.DisplayName);
        }

        var reloaded = await _service.GetWorkspaceAsync(ws.Id);
        Assert.Null(reloaded!.Alias);
        Assert.Equal(reloaded.Name, reloaded.DisplayName);
    }

    [Fact(DisplayName = "S27-AC03: 超长别名截断到 40 字符（不报错）")]
    public async Task AC03_Overlong_Alias_Is_Truncated()
    {
        var ws = await SeedWorkspaceAsync();
        string longAlias = new string('长', 200);

        var updated = await _service.SetWorkspaceAliasAsync(ws.Id, longAlias);

        Assert.NotNull(updated);
        Assert.Equal(40, updated!.Alias!.Length);
        Assert.Equal(new string('长', 40), updated.Alias);
    }

    [Fact(DisplayName = "S27-AC04: 工作区不存在返回 null，且 index 文件字节不变")]
    public async Task AC04_Unknown_Workspace_Returns_Null_And_No_Write()
    {
        await SeedWorkspaceAsync();
        string hashBefore = Sha256OfFile(WorkspacesIndexPath);

        var result = await _service.SetWorkspaceAliasAsync("ws_notexist", "任意别名");
        var emptyId = await _service.SetWorkspaceAliasAsync("   ", "任意别名");

        Assert.Null(result);
        Assert.Null(emptyId);
        Assert.Equal(hashBefore, Sha256OfFile(WorkspacesIndexPath));
    }

    // ────────────── 核心隔离性：别名绝不进入上下文 ──────────────

    [Fact(DisplayName = "S27-AC05: 别名不进入请求头 ${workspace-name}（真实转发路径与 CreateContext 路径）")]
    public async Task AC05_Alias_Never_Leaks_Into_Request_Headers()
    {
        var ws = await SeedWorkspaceAsync("HeaderProbe");
        await _service.SetWorkspaceAliasAsync(ws.Id, "秘密别名XYZ");
        var aliased = (await _service.GetWorkspaceAsync(ws.Id))!;

        // ① TurnPlanner 实际使用的构造方式：直接喂 Workspace.Name
        var plannerCtx = new HeaderResolutionContext(
            aliased.Id,
            aliased.Name,
            aliased.RootPath,
            "sess_test",
            "hash0123456789ab",
            "provider_1",
            "Provider",
            "model-x",
            "req_1"
        );
        string resolved = HeaderVariableResolver.ResolveTemplate("${workspace-name}", plannerCtx);
        Assert.DoesNotContain("秘密别名XYZ", Uri.UnescapeDataString(resolved));
        Assert.Equal(Uri.EscapeDataString(aliased.Name), resolved);

        // ② HeaderVariableResolver.CreateContext 的路径分支：仍取文件夹名
        var factoryCtx = HeaderVariableResolver.CreateContext(
            workspacePath: aliased.RootPath,
            sessionId: "sess_test"
        );
        Assert.DoesNotContain("秘密别名XYZ", factoryCtx.WorkspaceName);
        Assert.Equal(Path.GetFileName(aliased.RootPath), factoryCtx.WorkspaceName);
    }

    [Fact(DisplayName = "S27-AC06: 别名不进入系统提示词 {{WorkspaceName}}")]
    public async Task AC06_Alias_Never_Leaks_Into_System_Prompt()
    {
        var ws = await SeedWorkspaceAsync("PromptProbe");
        await _service.SetWorkspaceAliasAsync(ws.Id, "不该出现的别名");
        var aliased = (await _service.GetWorkspaceAsync(ws.Id))!;

        string prompt = await SystemPromptBuilder.BuildAsync(aliased);

        Assert.DoesNotContain("不该出现的别名", prompt);
        Assert.Contains(aliased.Name, prompt); // 提示词仍用 Name
    }

    // ─────────────────── 兼容性与不互踩 ───────────────────

    [Fact(DisplayName = "S27-AC07: 老数据（无 Alias 键）反序列化兼容")]
    public async Task AC07_Legacy_Index_Without_Alias_Is_Compatible()
    {
        var ws = await SeedWorkspaceAsync("LegacyProbe");
        string wsId = ws.Id;

        // 手写一份"老版本"index：删掉 Alias 键（不用原始字符串字面量，兼容项目语言版本）
        string escapedRoot = ws.RootPath.Replace("\\", "\\\\");
        string legacyJson =
            "[\n"
            + "  {\n"
            + $"    \"Id\": \"{wsId}\",\n"
            + "    \"Name\": \"LegacyProbe\",\n"
            + $"    \"RootPath\": \"{escapedRoot}\",\n"
            + $"    \"WorkspaceHash\": \"{ws.WorkspaceHash}\",\n"
            + "    \"IsPinned\": false,\n"
            + "    \"CreatedAtUtc\": \"2026-01-01T00:00:00Z\",\n"
            + "    \"LastOpenedAtUtc\": \"2026-01-01T00:00:00Z\"\n"
            + "  }\n"
            + "]";
        await File.WriteAllTextAsync(WorkspacesIndexPath, legacyJson);

        var reloaded = await _service.GetWorkspaceAsync(wsId);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Alias);
        Assert.Equal("LegacyProbe", reloaded.Name);
        Assert.Equal(reloaded.Name, reloaded.DisplayName);
        Assert.Equal(ws.WorkspaceHash, reloaded.WorkspaceHash);
    }

    [Fact(DisplayName = "S27-AC08: AddOrUpdateWorkspace 改 Name 时不清空既有 Alias")]
    public async Task AC08_Upsert_Keeps_Existing_Alias()
    {
        var ws = await SeedWorkspaceAsync("UpsertProbe");
        await _service.SetWorkspaceAliasAsync(ws.Id, "保留我");

        var upserted = await _service.AddOrUpdateWorkspaceAsync(ws.RootPath, "新的文件夹名");

        Assert.Equal(ws.Id, upserted.Id);
        Assert.Equal("保留我", upserted.Alias); // 别名不被 upsert 冲掉
        Assert.Equal("新的文件夹名", upserted.Name); // 只改 Name
    }

    // ───────────────────────── Bridge 层 ─────────────────────────

    [Fact(DisplayName = "S27-AC09: Bridge RenameWorkspace 参数校验与正常返回")]
    public async Task AC09_Bridge_RenameWorkspace_Contract()
    {
        var bridge = new HostBridgeFacade(null, _storageManager, workspaceSessionService: _service);

        var emptyId = await bridge.RenameWorkspace("   ", "x");
        Assert.False(emptyId.Ok);
        Assert.Equal("INVALID_WORKSPACE_ID", emptyId.Code);

        var missing = await bridge.RenameWorkspace("ws_nope", "x");
        Assert.False(missing.Ok);
        Assert.Equal("WORKSPACE_NOT_FOUND", missing.Code);
        Assert.Contains("ws_nope", missing.Message);

        var ws = await SeedWorkspaceAsync("BridgeProbe");
        var ok = await bridge.RenameWorkspace(ws.Id, "Bridge 别名");
        Assert.True(ok.Ok);
        Assert.NotNull(ok.Data);
        Assert.Equal("Bridge 别名", ok.Data!.Alias);
        Assert.Equal("Bridge 别名", ok.Data.DisplayName);
        Assert.Equal("BridgeProbe", ok.Data.Name);

        // 清空别名
        var cleared = await bridge.RenameWorkspace(ws.Id, "");
        Assert.True(cleared.Ok);
        Assert.Null(cleared.Data!.Alias);
        Assert.Equal("BridgeProbe", cleared.Data.DisplayName);
    }

    [Fact(DisplayName = "S27-AC10: GetWorkspaces 带出 Alias/DisplayName，排序与列表不受影响")]
    public async Task AC10_GetWorkspaces_Exposes_Alias()
    {
        var wsA = await SeedWorkspaceAsync("ListA");
        var wsB = await SeedWorkspaceAsync("ListB");
        await _service.SetWorkspaceAliasAsync(wsA.Id, "列表别名A");

        var all = await _service.GetAllWorkspacesAsync();

        Assert.Equal(2, all.Count);
        var a = all.Single(w => w.Id == wsA.Id);
        var b = all.Single(w => w.Id == wsB.Id);
        Assert.Equal("列表别名A", a.Alias);
        Assert.Equal("列表别名A", a.DisplayName);
        Assert.Equal("ListA", a.Name);
        Assert.Null(b.Alias);
        Assert.Equal("ListB", b.DisplayName);
    }
}
