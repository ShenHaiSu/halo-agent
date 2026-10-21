#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need11-fix 回归：view_image 的 ToolResult.Images 必须落到 tool 消息上，否则双协议附图分支走不到。</summary>
public sealed class Stage11ToolImageWiringTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;
    private readonly string _projectDir;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;

    public Stage11ToolImageWiringTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage11Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage11Bootstrap_{Guid.NewGuid():N}"
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
        _projectDir = Path.Combine(_tempRoot, "Projects", "ViewImageTest");
        Directory.CreateDirectory(_projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "ViewImage测试项目")
            .GetAwaiter()
            .GetResult();
        _session = _service
            .CreateSessionAsync(_workspace.Id, "Stage11 工具附图接线")
            .GetAwaiter()
            .GetResult();
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
        catch { }
    }

    private static byte[] TinyPng() =>
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
        );

    [Fact(DisplayName = "N11-FIX-1: view_image 成功后 tool 消息携带 Images（真盘可定位）")]
    public async Task ViewImageSuccess_ToolMessageKeepsImages()
    {
        string pngPath = Path.Combine(_projectDir, "a.png");
        await File.WriteAllBytesAsync(pngPath, TinyPng());

        var script = new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>();
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone("call_view_1", "view_image", "{\"path\":\"a.png\"}"),
            UnifiedStreamEvent.Completed(),
        });
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.Text("已看到图片。"),
            UnifiedStreamEvent.Completed(),
        });

        var gateway = new CaptureGateway(script);
        var cfgSvc = new Host.Desktop.Infrastructure.Config.AgentConfigService(_storageManager);
        cfgSvc.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 25,
                ToolsPreset = "full"
            }
        ); // need17一次性更新：默认 Coding 预设下 view_image 不在激活集，附图接线改走 Full 预设。
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(),
            _storageManager,
            agentConfigService: cfgSvc
        );
        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            _session.Id,
            "看看 a.png"
        );

        Assert.True(result.Success);
        Assert.Equal(1, result.ToolExecutions);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        var toolMsg = messages.FirstOrDefault(m =>
            string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ToolCallId, "call_view_1", StringComparison.Ordinal)
        );
        Assert.NotNull(toolMsg);
        Assert.NotNull(toolMsg!.Images);
        Assert.Single(toolMsg.Images!);
        Assert.False(string.IsNullOrWhiteSpace(toolMsg.Images![0].ImageId));
        string? abs = _repository.FindAttachmentPath(
            _workspace.Id,
            _session.Id,
            toolMsg.Images![0].ImageId
        );
        Assert.NotNull(abs);
        Assert.True(File.Exists(abs!));
    }

    [Fact(DisplayName = "N11-FIX-2: 接线后的引用双协议均可读出 data: 图（Chat/Resp）")]
    public async Task WiredRef_Renders_InBothProtocols_ViaDiskResolver()
    {
        string pngPath = Path.Combine(_projectDir, "b.png");
        await File.WriteAllBytesAsync(pngPath, TinyPng());

        var script = new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>();
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone("call_view_2", "view_image", "{\"path\":\"b.png\"}"),
            UnifiedStreamEvent.Completed(),
        });
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.Text("看完了。"),
            UnifiedStreamEvent.Completed(),
        });

        var gateway2 = new CaptureGateway(script);
        var cfgSvc2 = new Host.Desktop.Infrastructure.Config.AgentConfigService(_storageManager);
        cfgSvc2.SaveAgentConfig(
            new AgentConfigDocument
            {
                ExecDefaultTimeoutMs = 30000,
                ExecMaxTimeoutMs = 300000,
                MaxIterations = 25,
                ToolsPreset = "full"
            }
        ); // need17一次性更新：同上，Full 预设。
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway2,
            new ToolRegistry(),
            _storageManager,
            agentConfigService: cfgSvc2
        );
        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            _session.Id,
            "看看 b.png"
        );
        Assert.True(result.Success);
        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        var toolMsg = messages.First(m =>
            string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ToolCallId, "call_view_2", StringComparison.Ordinal)
        );
        Assert.NotNull(toolMsg.Images);
        string dataRoot = _storageManager.CurrentDataRoot;

        // Chat：tool 附图展开为 tool_call 输出 + 块尾单个 user(text + image_url/detail)
        // FIX-tool-400：请求必须配对，单测同样先给归属 assistant，再跟 tool。
        var chatAssistant = UnifiedChatMessage.Assistant(
            null,
            new List<UnifiedToolCall> { new("call_view_2", null, "view_image", "{}") }
        );
        var chatModel = new ModelConfig
        {
            Id = "gpt-4o",
            Protocol = LlmProtocolType.Chat,
            SupportsVision = true,
            MaxImagesPerTurn = 4,
            VisionDetail = "high"
        };
        var chatReq = ImgReq(
            chatModel,
            new List<UnifiedChatMessage> { chatAssistant, toolMsg },
            img => ImageAttachmentStore.ResolveDataUrl(dataRoot, _workspace.Id, _session.Id, img)
        );
        string chatPayload = new ChatCompletionsAdapter().SerializeRequest(chatReq);
        using (var cdoc = JsonDocument.Parse(chatPayload))
        {
            var msgs = cdoc.RootElement.GetProperty("messages").EnumerateArray().ToList();
            // sys + assistant + tool + trailing-user = 4
            Assert.Equal(4, msgs.Count);
            Assert.Equal("tool", msgs[2].GetProperty("role").GetString());
            var withImg = msgs[3];
            Assert.Equal("user", withImg.GetProperty("role").GetString());
            var parts = withImg.GetProperty("content").EnumerateArray().ToList();
            Assert.Equal("text", parts[0].GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(parts[0].GetProperty("text").GetString()));
            Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
            Assert.StartsWith(
                "data:image/png;base64,",
                parts[1].GetProperty("image_url").GetProperty("url").GetString()
            );
            Assert.Equal(
                "high",
                parts[1].GetProperty("image_url").GetProperty("detail").GetString()
            );
        }
        // Resp：function_call + function_call_output + 块尾 user(input_text 首位非空 + input_image/detail)
        var respAssistant = UnifiedChatMessage.Assistant(
            null,
            new List<UnifiedToolCall> { new("call_view_2", null, "view_image", "{}") }
        );
        var respModel = new ModelConfig
        {
            Id = "o3",
            Protocol = LlmProtocolType.Resp,
            SupportsVision = true,
            MaxImagesPerTurn = 4,
            VisionDetail = "low"
        };
        var respReq = ImgReq(
            respModel,
            new List<UnifiedChatMessage> { respAssistant, toolMsg },
            img => ImageAttachmentStore.ResolveDataUrl(dataRoot, _workspace.Id, _session.Id, img)
        );
        string respPayload = new ResponsesApiAdapter().SerializeRequest(respReq);
        using (var rdoc = JsonDocument.Parse(respPayload))
        {
            var input = rdoc.RootElement.GetProperty("input").EnumerateArray().ToList();
            Assert.Equal(3, input.Count);
            Assert.Equal("function_call", input[0].GetProperty("type").GetString());
            Assert.Equal("function_call_output", input[1].GetProperty("type").GetString());
            var parts = input[2].GetProperty("content").EnumerateArray().ToList();
            Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(parts[0].GetProperty("text").GetString()));
            Assert.Equal("input_image", parts[1].GetProperty("type").GetString());
            Assert.StartsWith(
                "data:image/png;base64,",
                parts[1].GetProperty("image_url").GetString()
            );
            Assert.Equal("low", parts[1].GetProperty("detail").GetString());
        }
    }

    [Fact(DisplayName = "N11-FIX-3: view_image 失败不挂图（tool 消息 Images 为空）")]
    public async Task ViewImageFail_ToolMessageHasNoImages()
    {
        var script = new Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>>();
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.ToolDone(
                "call_view_9",
                "view_image",
                "{\"path\":\"not-exist.png\"}"
            ),
            UnifiedStreamEvent.Completed(),
        });
        script.Enqueue(_ => new List<UnifiedStreamEvent>
        {
            UnifiedStreamEvent.Text("文件不存在，下次再看。"),
            UnifiedStreamEvent.Completed(),
        });

        var gateway = new CaptureGateway(script);
        var orchestrator = new AgentOrchestrator(
            _service,
            _budgetService,
            gateway,
            new ToolRegistry(),
            _storageManager
        );
        AgentTurnResult result = await orchestrator.RunAgentTurnAsync(
            _workspace.Id,
            _session.Id,
            "看看不存在的图"
        );

        Assert.True(result.Success);
        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        var toolMsg = messages.First(m =>
            string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ToolCallId, "call_view_9", StringComparison.Ordinal)
        );
        Assert.True(toolMsg.Images == null || toolMsg.Images.Count == 0);
    }

    private static UnifiedLlmRequest ImgReq(
        ModelConfig model,
        List<UnifiedChatMessage> messages,
        Func<ChatImageRef, string?> resolver
    )
    {
        var provider = new ProviderConfig
        {
            Id = "p1",
            Name = "P",
            BaseUrl = "https://api.openai.com/v1"
        };
        return new UnifiedLlmRequest
        {
            RequestId = "req-fix",
            Provider = provider,
            Model = model,
            HeaderContext = new Host.Desktop.Infrastructure.Network.HeaderResolutionContext(
                "ws",
                "ws",
                "/tmp",
                "sess",
                "hash",
                "p1",
                "P",
                model.Id,
                "req-fix"
            ),
            SystemInstructions = "sys",
            Messages = messages,
            Tools = Array.Empty<UnifiedToolDefinition>(),
            ImageResolver = resolver,
        };
    }

    private sealed class CaptureGateway : ILlmGatewayClient
    {
        private readonly Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>> _script;
        public List<UnifiedLlmRequest> Seen { get; } = new();

        public CaptureGateway(Queue<Func<UnifiedLlmRequest, List<UnifiedStreamEvent>>> script) =>
            _script = script;

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Seen.Add(request);
            List<UnifiedStreamEvent> events =
                _script.Count > 0
                    ? _script.Dequeue()(request)
                    : new List<UnifiedStreamEvent>
                    {
                        UnifiedStreamEvent.Text("done"),
                        UnifiedStreamEvent.Completed()
                    };
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
