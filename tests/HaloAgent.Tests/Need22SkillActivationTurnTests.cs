#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

// 并行禁用由 Stage01FoundationTests.cs 的 [assembly: CollectionBehavior] 全局控制。

/// <summary>
/// need22-B02 §4：技能激活发包接入集成测试（T1~T8）。
/// 核心不变式：落盘 messages.jsonl 永远是 marker，发包才是展开块。
/// </summary>
public sealed class Need22SkillActivationTurnTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly SkillMemoryService _skills;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;

    public Need22SkillActivationTurnTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentNeed22Turn_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentNeed22TurnBootstrap_{Guid.NewGuid():N}"
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
        _budgetService = new TokenBudgetService();
        _skills = new SkillMemoryService(_storageManager);

        _projectDir = Path.Combine(_tempRoot, "Projects", "Need22TurnTest");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "Need22 测试项目")
            .GetAwaiter()
            .GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "新建会话").GetAwaiter().GetResult();

        // 供展开用例使用的真实 global 技能（正文含两行，便于断言全文展开）。
        _skills.CreateOrUpdate(
            SkillMemoryScope.Global,
            SkillMemoryKind.Skill,
            "turn-skill",
            "第一行正文。\n第二行正文。"
        );
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

    /// <summary>need22-B02：反射调用 AgentOrchestrator 私有展开步骤（测试白盒）。</summary>
    private static List<UnifiedChatMessage> InvokeExpand(
        AgentOrchestrator orchestrator,
        List<UnifiedChatMessage> history,
        string? workspaceRoot
    )
    {
        var mi = typeof(AgentOrchestrator).GetMethod(
            "ExpandSkillMarkers",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(mi);
        var result = mi.Invoke(orchestrator, new object?[] { history, workspaceRoot });
        return (List<UnifiedChatMessage>)result!;
    }

    private AgentOrchestrator NewOrchestrator(ILlmGatewayClient gateway) =>
        new(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(registerBuiltIns: false),
            _storageManager
        );

    // ---------- T1：发包内容含展开块（端到端一轮） ----------

    [Fact(DisplayName = "N22-T1: 发包给模型的 user 消息含 <skill> 展开块与正文全文")]
    public async Task T1_RequestContainsExpandedBlock()
    {
        var gateway = new CapturingFakeGateway();
        var orchestrator = NewOrchestrator(gateway);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            _session.Id,
            "[[skill:global/turn-skill]] 帮我改代码"
        );

        Assert.True(result.Success);
        Assert.NotNull(gateway.LastRequest);
        var userMsg = gateway.LastRequest!.Messages.First(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains("<skill name=\"turn-skill\" scope=\"global\">", userMsg.Content);
        Assert.Contains("第一行正文。", userMsg.Content);
        Assert.Contains("第二行正文。", userMsg.Content);
        Assert.DoesNotContain("[[skill:", userMsg.Content);
    }

    // ---------- T2~T5：ExpandSkillMarkers 白盒行为 ----------

    [Fact(DisplayName = "N22-T2: assistant 消息里的 marker 不展开")]
    public void T2_AssistantMessageNotExpanded()
    {
        var orchestrator = NewOrchestrator(new CapturingFakeGateway());
        var history = new List<UnifiedChatMessage>
        {
            new() { Role = "user", Content = "[[skill:global/turn-skill]] 用户", },
            new() { Role = "assistant", Content = "助手回复 [[skill:global/turn-skill]]", },
        };
        var result = InvokeExpand(orchestrator, history, _projectDir);
        Assert.Contains("<skill name=\"turn-skill\"", result[0].Content);
        Assert.Equal("助手回复 [[skill:global/turn-skill]]", result[1].Content);
        Assert.DoesNotContain("<skill ", result[1].Content);
    }

    [Fact(DisplayName = "N22-T3: 无 marker 的消息内容逐字不变")]
    public void T3_NoMarkerUnchanged()
    {
        var orchestrator = NewOrchestrator(new CapturingFakeGateway());
        var history = new List<UnifiedChatMessage>
        {
            new() { Role = "user", Content = "普通消息，没有标记。" },
        };
        var result = InvokeExpand(orchestrator, history, _projectDir);
        Assert.Equal("普通消息，没有标记。", result[0].Content);
    }

    [Fact(DisplayName = "N22-T4: 展开后 Id / CreatedAtUtc 保持不变（拷贝语义正确）")]
    public void T4_IdAndCreatedAtPreserved()
    {
        var orchestrator = NewOrchestrator(new CapturingFakeGateway());
        var created = new DateTime(2026, 10, 21, 8, 30, 0, DateTimeKind.Utc);
        var origin = new UnifiedChatMessage
        {
            Id = "msg_fixed_id_001",
            Role = "user",
            Content = "[[skill:global/turn-skill]] 用户",
            CreatedAtUtc = created,
        };
        var result = InvokeExpand(
            orchestrator,
            new List<UnifiedChatMessage> { origin },
            _projectDir
        );
        Assert.NotSame(origin, result[0]); // 拷贝而非原地改
        Assert.Equal("msg_fixed_id_001", result[0].Id);
        Assert.Equal(created, result[0].CreatedAtUtc);
        Assert.Contains("<skill name=\"turn-skill\"", result[0].Content);
    }

    [Fact(DisplayName = "N22-T5: 展开后 Images 引用不丢")]
    public void T5_ImagesPreserved()
    {
        var orchestrator = NewOrchestrator(new CapturingFakeGateway());
        var images = new List<ChatImageRef>
        {
            new()
            {
                ImageId = "img_test_001",
                FileName = "a.png",
                RelPath = "attachments/a.png"
            },
        };
        var origin = new UnifiedChatMessage
        {
            Role = "user",
            Content = "[[skill:global/turn-skill]] 带图",
            Images = images,
        };
        var result = InvokeExpand(
            orchestrator,
            new List<UnifiedChatMessage> { origin },
            _projectDir
        );
        Assert.NotNull(result[0].Images);
        Assert.Single(result[0].Images!);
        Assert.Equal("img_test_001", result[0].Images![0].ImageId);
    }

    // ---------- T6：落盘不变式（核心） ----------

    [Fact(DisplayName = "N22-T6: 落盘 messages.jsonl 的 user 文本仍是 marker，不含展开块")]
    public async Task T6_PersistedHistoryKeepsMarker()
    {
        var gateway = new CapturingFakeGateway();
        var orchestrator = NewOrchestrator(gateway);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            _session.Id,
            "[[skill:global/turn-skill]] 帮我改代码"
        );
        Assert.True(result.Success);

        // 1) 读回历史（服务层读的就是 messages.jsonl）
        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        var userMsg = messages.First(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains("[[skill:", userMsg.Content);
        Assert.DoesNotContain("<skill ", userMsg.Content);

        // 2) 直接读落盘文件复核（核心不变式 N1）
        string jsonlPath = Path.Combine(
            _storageManager.SessionsDirectory,
            _workspace.Id,
            _session.Id,
            "messages.jsonl"
        );
        Assert.True(File.Exists(jsonlPath));
        string raw = await File.ReadAllTextAsync(jsonlPath);
        Assert.Contains("[[skill:", raw);
        Assert.DoesNotContain("<skill ", raw);
    }

    // ---------- T7：标题不被 marker 污染 ----------

    [Fact(DisplayName = "N22-T7: 会话标题不被 marker 污染（不是 [[sk 开头）")]
    public async Task T7_TitleNotPolluted()
    {
        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            _session.Id,
            UnifiedChatMessage.User("[[skill:global/foo]] 帮我改代码")
        );
        var meta = await _service.GetSessionAsync(_workspace.Id, _session.Id);
        Assert.NotNull(meta);
        Assert.DoesNotContain("[[sk", meta!.Title);
        Assert.StartsWith("帮我改代码", meta.Title);
    }

    // ---------- T8：取数失败降级（轮次不中断） ----------

    [Fact(DisplayName = "N22-T8: 技能不存在 ⇒ 轮次仍能发包，内容保留 marker 原文")]
    public async Task T8_MissingSkillDegradesGracefully()
    {
        var gateway = new CapturingFakeGateway();
        var orchestrator = NewOrchestrator(gateway);

        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            _session.Id,
            "[[skill:global/ghost-skill]] 帮我改代码"
        );

        Assert.True(result.Success);
        var userMsg = gateway.LastRequest!.Messages.First(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains("[[skill:global/ghost-skill]]", userMsg.Content);
        Assert.DoesNotContain("<skill ", userMsg.Content);
    }

    // ---------- Fake 网关：记录请求并回一段文本，无真实网络 ----------

    private sealed class CapturingFakeGateway : ILlmGatewayClient
    {
        public UnifiedLlmRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            LastRequest = request;
            await Task.Yield();
            yield return UnifiedStreamEvent.Text("已收到。");
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
