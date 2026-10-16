#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

 /// <summary>need06-05：前置提示词可配化验收（T1-T12）。T13：Bridge 回传 DateTime 宽容解析回归。</summary>
public sealed class Stage06PromptTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly PromptConfigService _promptService;
    private readonly WorkspaceInfo _workspace;

    public Stage06PromptTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage06Prompt_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage06PromptBs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
        _promptService = new PromptConfigService(_storageManager);
        string projDir = Path.Combine(_tempRoot, "Projects", "PromptTest");
        Directory.CreateDirectory(projDir);
        _workspace = _service.AddOrUpdateWorkspaceAsync(projDir, "Prompt测试项目").GetAwaiter().GetResult();
        SystemPromptBuilder.ClearPythonCacheForTests();
        PromptLastSentStore.ClearForTests();
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

    [Fact]
    public async Task T01_Default_Render_Equals_Legacy_Template()
    {
        const string py = "Python 3.11.9 (C:\\Python311\\python.exe)";
        string legacy = await SystemPromptBuilder.BuildAsync(_workspace, py);
        var preview = SystemPromptBuilder.BuildPreview(PromptDefaults.CreateDefault(), _workspace, py, dateOverride: DateTime.Now.ToString("yyyy-MM-dd"));
        Assert.Equal(legacy, preview.AssembledText);
    }

    [Fact]
    public void T02_MasterSwitch_Off_Returns_Empty()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.EnableHaloPrefix = false;
        var preview = SystemPromptBuilder.BuildPreview(cfg, _workspace, "py", "2026-01-01");
        Assert.Equal(string.Empty, preview.AssembledText);
        Assert.Empty(preview.Blocks);
    }

    [Fact]
    public void T03_Disable_CmdPython_Removes_Critical_Rule()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.EnableCmdPython = false;
        var preview = SystemPromptBuilder.BuildPreview(cfg, _workspace, "py", "2026-01-01");
        Assert.DoesNotContain("CRITICAL RULE", preview.AssembledText);
        Assert.DoesNotContain("runner:", preview.AssembledText);
        Assert.DoesNotContain("powershell", preview.AssembledText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## 1.", preview.AssembledText);
        Assert.Contains("## 3.", preview.AssembledText);
    }

    [Fact]
    public void T04_Disable_Identity_Removes_Halo_Line()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.EnableIdentity = false;
        var preview = SystemPromptBuilder.BuildPreview(cfg, _workspace, "py", "2026-01-01");
        Assert.DoesNotContain("You are Halo-Agent", preview.AssembledText);
        Assert.Contains("## 1.", preview.AssembledText);
    }

    [Fact]
    public void T05_Disable_Ephemeral_Removes_Marker()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.EnableEphemeral = false;
        var preview = SystemPromptBuilder.BuildPreview(cfg, _workspace, "py", "2026-01-01");
        Assert.DoesNotContain("## 4. Session Ephemeral", preview.AssembledText);
        var (stable, ephemeral) = PrefixCachePredictor.SplitSystem(preview.AssembledText);
        Assert.Equal(string.Empty, ephemeral);
        Assert.NotEqual(string.Empty, stable);
    }

    [Fact]
    public void T06_Custom_Head_Prepends_And_Tail_Appends()
    {
        var head = PromptDefaults.CreateDefault();
        head.EnableCustom = true;
        head.CustomPlacement = "head";
        head.CustomText = "> 团队规范：先读后改";
        var hp = SystemPromptBuilder.BuildPreview(head, _workspace, "py", "2026-01-01");
        Assert.StartsWith("> 团队规范：先读后改", hp.AssembledText);

        var tail = PromptDefaults.CreateDefault();
        tail.EnableCustom = true;
        tail.CustomPlacement = "tail";
        tail.CustomText = "> 团队规范：先读后改";
        var tp = SystemPromptBuilder.BuildPreview(tail, _workspace, "py", "2026-01-01");
        Assert.EndsWith("> 团队规范：先读后改\n", tp.AssembledText);
    }

    [Fact]
    public void T07_Unknown_Variable_Preserved_And_Reported()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.EnableCustom = true;
        cfg.CustomPlacement = "tail";
        cfg.CustomText = "hello {{FooBar}} world";
        var preview = SystemPromptBuilder.BuildPreview(cfg, _workspace, "py", "2026-01-01");
        Assert.Contains("{{FooBar}}", preview.AssembledText);
        Assert.Contains("FooBar", preview.UnknownVariables);
    }

    [Fact]
    public void T08_Save_Rejects_Oversize_Block()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.EnableCustom = true;
        cfg.CustomText = new string('x', 8001);
        var ex = Assert.Throws<PromptConfigValidationException>(() => _promptService.SavePromptConfig(cfg));
        Assert.Equal("custom", ex.BlockId);
    }

    [Fact]
    public void T09_Corrupt_Json_Falls_Back_Default()
    {
        string path = _promptService.PromptConfigFilePath;
        File.WriteAllText(path, "{bad json{{{");
        var doc = _promptService.GetPromptConfigInternal();
        Assert.NotNull(doc);
        Assert.True(doc.EnableHaloPrefix);
        var (_, isDefault, fallback) = _promptService.LoadWithMeta();
        Assert.True(isDefault);
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        Assert.True(File.Exists(path), "原损坏文件应保留");
        var dir = Path.GetDirectoryName(path)!;
        var corrupts = Directory.GetFiles(dir, "prompt.json.corrupt-*");
        Assert.NotEmpty(corrupts);
    }

    [Fact]
    public void T10_Adapters_Omit_System_When_Empty()
    {
         var provider = new ProviderConfig { Id = "p", Name = "P", BaseUrl = "https://api.openai.com/v1" };
         var model = new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Chat, MaxContextTokens = 128000, MaxOutputTokens = 4096 };
         var headerCtx = new Host.Desktop.Infrastructure.Network.HeaderResolutionContext("ws", "n", "p", "s", "h", "pid", "pn", "mid", "r1");
         var req = new UnifiedLlmRequest { RequestId = "r1", Provider = provider, Model = model, HeaderContext = headerCtx, SystemInstructions = string.Empty, Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") }, Tools = Array.Empty<UnifiedToolDefinition>() };
         string chatJson = new ChatCompletionsAdapter().SerializeRequest(req);
         Assert.DoesNotContain("\"role\":\"system\"", chatJson);
         var respModel = new ModelConfig { Id = "o3-mini", Protocol = LlmProtocolType.Resp, MaxContextTokens = 200000, MaxOutputTokens = 16384 };
         var req2 = new UnifiedLlmRequest { RequestId = "r1", Provider = provider, Model = respModel, HeaderContext = headerCtx, SystemInstructions = string.Empty, Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") }, Tools = Array.Empty<UnifiedToolDefinition>() };
         string respJson = new ResponsesApiAdapter().SerializeRequest(req2);
         Assert.DoesNotContain("instructions", respJson);
     }
     [Fact]
     public async Task T11_Preview_Equals_Orchestrator_Payload()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "T11");
        var gateway = new CaptureGateway();
        var orchestrator = new AgentOrchestrator(_service, _budgetService, gateway, new ToolRegistry(registerBuiltIns: false), _storageManager, null, _promptService);
        var result = await orchestrator.RunAgentTurnAsync(_workspace.Id, session.Id, "hello preview");
        Assert.True(result.Success);
        Assert.NotNull(gateway.Captured);
        var (doc, _, _) = _promptService.LoadWithMeta();
        var preview = SystemPromptBuilder.BuildPreview(doc, _workspace, null, null);
        // 同一 BuildCore 路径：比较除日期/Python 易变行外的 stable 段一致
        var (stableSent, _) = PrefixCachePredictor.SplitSystem(gateway.Captured!.SystemInstructions);
        var (stablePreview, _) = PrefixCachePredictor.SplitSystem(preview.AssembledText);
        Assert.Equal(stablePreview, stableSent);
    }

    [Fact]
    public void T12_Reset_Single_Block_Keeps_Others()
    {
        var cfg = PromptDefaults.CreateDefault();
        cfg.IdentityText = "IDENTITY-CHANGED";
        cfg.CmdPythonText = "CMDPY-CHANGED";
        _promptService.SavePromptConfig(cfg);
        var reset = _promptService.ResetPromptConfig("identity");
        Assert.Equal(PromptDefaults.IdentityText, reset.IdentityText);
        Assert.Equal("CMDPY-CHANGED", reset.CmdPythonText);
    }
 
     [Fact]
     public void T13_Lenient_DateTime_Parses_Bridge_Shapes()
     {
         string CfgWith(string updatedAtUtcJsonValue) =>
             "{\"version\":1,\"enableHaloPrefix\":true,\"enableIdentity\":true,\"enableRuntime\":true,\"enableCmdPython\":true,\"enableToolGuide\":true,\"enableEphemeral\":true,\"enableCustom\":false,\"customPlacement\":\"tail\",\"customText\":\"\",\"identityText\":\"x\",\"runtimeTemplate\":\"x\",\"cmdPythonText\":\"x\",\"toolGuideTemplate\":\"x\",\"ephemeralTemplate\":\"x\",\"scope\":\"global\",\"updatedAtUtc\":" + updatedAtUtcJsonValue + "}";
         // 标准 ISO（真后端落盘格式）
         var iso = PromptJsonOptions.DeserializeConfigDocument(CfgWith("\"2026-10-16T12:34:56.789Z\""));
         Assert.True(iso.EnableHaloPrefix);
         // Microsoft /Date(ms)/（CefSharp 绑定器常见形状）
         var ajax = PromptJsonOptions.DeserializeConfigDocument(CfgWith("\"/Date(1729000000000)/\""));
         Assert.True(ajax.EnableHaloPrefix);
         Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1729000000000).UtcDateTime, ajax.UpdatedAtUtc);
         // Unix 毫秒数字
         var epoch = PromptJsonOptions.DeserializeConfigDocument(CfgWith("1729000000000"));
         Assert.True(epoch.EnableHaloPrefix);
         // 空字符串回 default（由 Save 归一为 UtcNow，不炸）
         var empty = PromptJsonOptions.DeserializeConfigDocument(CfgWith("\"\""));
         Assert.Equal(default, empty.UpdatedAtUtc);
         // 真垃圾仍抛 JsonException（Bridge 转 PROMPT_VALIDATION）
         Assert.Throws<System.Text.Json.JsonException>(() => PromptJsonOptions.DeserializeConfigDocument(CfgWith("\"not-a-date\"")));
     }

    private sealed class CaptureGateway : ILlmGatewayClient
    {
        public UnifiedLlmRequest? Captured { get; private set; }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Captured = request;
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("ok");
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
