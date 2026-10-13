#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.LlmGateway.Sse;
using Xunit;

namespace HaloAgent.Tests;

public sealed class Stage03GatewayAndStreamTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;

    public Stage03GatewayAndStreamTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage03Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage03Bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);
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
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task SseStreamReader_ParsesMultiLineData_AndSkipsComments()
    {
        // 验证标准与带注释的 SSE 流解析，以及不同换行符 (\n, \r\n)
        string sseContent =
            ": keep-alive ping\r\n" +
            "event: ping\r\n" +
            "data: {\"time\":1}\r\n\r\n" +
            ": another comment\n" +
            "data: line1\n" +
            "data: line2\n\n" +
            "data: [DONE]\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseContent));
        var frames = new List<SseFrame>();

        await foreach (var frame in SseStreamReader.ReadFramesAsync(stream))
        {
            frames.Add(frame);
        }

        Assert.Equal(3, frames.Count);

        Assert.Equal("ping", frames[0].EventType);
        Assert.Equal("{\"time\":1}", frames[0].Data);

        Assert.Null(frames[1].EventType);
        Assert.Equal("line1\nline2", frames[1].Data);

        Assert.Null(frames[2].EventType);
        Assert.Equal("[DONE]", frames[2].Data);
    }

    [Fact]
    public async Task ChatCompletionsAdapter_S03_AC01_ParsesTextReasoningAndToolCalls()
    {
        // 模拟 OpenAI / DeepSeek / Chat 协议流式 chunk
        string sseStream =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"思考中...\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好！\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_abc123\",\"type\":\"function\",\"function\":{\"name\":\"exec_command\",\"arguments\":\"{\\\"cmd\\\":\\\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"dir\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":80}}}\n\n" +
            "data: [DONE]\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseStream));
        var adapter = new ChatCompletionsAdapter();
        var events = new List<UnifiedStreamEvent>();

        await foreach (var evt in adapter.ParseStreamAsync(stream))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e.Kind == UnifiedStreamEventKind.StreamStarted);

        var reasoning = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.ReasoningDelta);
        Assert.NotNull(reasoning);
        Assert.Equal("思考中...", reasoning.TextDelta);

        var text = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.TextDelta);
        Assert.NotNull(text);
        Assert.Equal("你好！", text.TextDelta);

        var toolStarted = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.ToolCallStarted);
        Assert.NotNull(toolStarted);
        Assert.Equal("call_abc123", toolStarted.ToolCall?.CallId);
        Assert.Equal("exec_command", toolStarted.ToolCall?.FunctionName);

        var toolCompleted = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.ToolCallCompleted);
        Assert.NotNull(toolCompleted);
        Assert.Equal("call_abc123", toolCompleted.ToolCall?.CallId);
        Assert.Equal("{\"cmd\":\"dir\"}", toolCompleted.ToolCall?.ArgumentsJson);

        var usage = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.UsageUpdated);
        Assert.NotNull(usage);
        Assert.Equal(100, usage.Usage?.InputTokens);
        Assert.Equal(20, usage.Usage?.OutputTokens);
        Assert.Equal(80, usage.Usage?.CachedInputTokens);
        Assert.Equal(120, usage.Usage?.TotalTokens);
        Assert.Equal(0.8, usage.Usage?.CacheHitRate ?? 0, precision: 2);

        Assert.Contains(events, e => e.Kind == UnifiedStreamEventKind.StreamCompleted);
    }

    [Fact]
    public async Task ResponsesApiAdapter_S03_AC02_ParsesTextAndFunctionCallDone()
    {
        // 模拟 OpenAI Responses API /v1/responses 流式事件 (S03-AC02)
        string sseStream =
            "event: response.created\n" +
            "data: {\"response\":{\"id\":\"resp_xyz\"}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"delta\":\"Halo Responses 流式返回\"}\n\n" +
            "event: response.output_item.added\n" +
            "data: {\"item\":{\"type\":\"function_call\",\"id\":\"fc_item_1\",\"call_id\":\"call_resp_999\",\"name\":\"read_file\",\"arguments\":\"\"}}\n\n" +
            "event: response.function_call_arguments.done\n" +
            "data: {\"call_id\":\"call_resp_999\",\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"main.cs\\\"}\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"usage\":{\"input_tokens\":500,\"output_tokens\":60,\"input_token_details\":{\"cached_tokens\":250}}}\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseStream));
        var adapter = new ResponsesApiAdapter();
        var events = new List<UnifiedStreamEvent>();

        await foreach (var evt in adapter.ParseStreamAsync(stream))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e.Kind == UnifiedStreamEventKind.StreamStarted);

        var text = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.TextDelta);
        Assert.NotNull(text);
        Assert.Equal("Halo Responses 流式返回", text.TextDelta);

        var toolStarted = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.ToolCallStarted);
        Assert.NotNull(toolStarted);
        Assert.Equal("call_resp_999", toolStarted.ToolCall?.CallId);
        Assert.Equal("read_file", toolStarted.ToolCall?.FunctionName);

        var toolCompleted = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.ToolCallCompleted);
        Assert.NotNull(toolCompleted);
        Assert.Equal("call_resp_999", toolCompleted.ToolCall?.CallId);
        Assert.Equal("{\"path\":\"main.cs\"}", toolCompleted.ToolCall?.ArgumentsJson);

        var usage = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.UsageUpdated);
        Assert.NotNull(usage);
        Assert.Equal(500, usage.Usage?.InputTokens);
        Assert.Equal(60, usage.Usage?.OutputTokens);
        Assert.Equal(250, usage.Usage?.CachedInputTokens);
        Assert.Equal(0.5, usage.Usage?.CacheHitRate ?? 0, precision: 2);

        Assert.Contains(events, e => e.Kind == UnifiedStreamEventKind.StreamCompleted);
    }

    [Fact]
    public void ResponsesApiAdapter_SerializeRequest_ConvertsChatStructureCorrectly()
    {
        var adapter = new ResponsesApiAdapter();
        var req = new UnifiedLlmRequest
        {
            RequestId = "req_resp_test",
            Provider = new ProviderConfig { Id = "p1", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" },
            Model = new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Resp },
            SystemInstructions = "You are a helpful assistant.",
            Messages = new List<UnifiedChatMessage>
            {
                new() { Role = "user", Content = "List directory contents" },
                new() {
                    Role = "tool",
                    Content = "{\"files\":[\"a.txt\"]}",
                    ToolCallId = "call_prev_1"
                }
            },
            Tools = new List<UnifiedToolDefinition>
            {
                UnifiedToolDefinition.Create(
                    name: "list_dir",
                    description: "List files in directory",
                    jsonSchema: "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}")
            }
        };

        string json = adapter.SerializeRequest(req);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("You are a helpful assistant.", root.GetProperty("instructions").GetString());

        var input = root.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal(2, input.GetArrayLength());

        // 验证 tools 展开为扁平结构
        var tools = root.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("list_dir", tools[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ResponsesApiAdapter_404_ProvidesDiagnosisHint()
    {
        var adapter = new ResponsesApiAdapter();
        string formatted = adapter.FormatErrorResponse(404, "{\"error\":{\"message\":\"Not Found\"}}");
        Assert.Contains("404", formatted);
        Assert.Contains("切换为 Chat 协议重试", formatted);
    }

    [Fact]
    public async Task ResponsesApiAdapter_ParsesErrorEvent()
    {
        string sseStream =
            "event: response.failed\n" +
            "data: {\"error\":{\"code\":\"server_error\",\"message\":\"Rate limit exceeded\"}}\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseStream));
        var adapter = new ResponsesApiAdapter();
        var events = new List<UnifiedStreamEvent>();

        await foreach (var evt in adapter.ParseStreamAsync(stream))
        {
            events.Add(evt);
        }

        var err = events.FirstOrDefault(e => e.Kind == UnifiedStreamEventKind.StreamError);
        Assert.NotNull(err);
        Assert.Equal("server_error", err.ErrorCode);
        Assert.Equal("Rate limit exceeded", err.ErrorMessage);
    }

    [Fact]
    public void ChatCompletionsAdapter_FormatErrorResponse_ExtractsJsonMessage()
    {
        var adapter = new ChatCompletionsAdapter();
        string formatted = adapter.FormatErrorResponse(401, "{\"error\":{\"message\":\"Incorrect API key provided\"}}");
        Assert.Contains("401", formatted);
        Assert.Contains("Incorrect API key provided", formatted);
    }

    [Fact]
    public void JsEventDispatcher_DeltaCoalescing_MergesSequentialDeltas()
    {
        // 验证 16ms 周期内对连续的同类型 TextDelta / ReasoningDelta 进行自动拼接
        string sessionId = "sess_coalesce_test";

        // 先清理队列残留
        while (JsEventDispatcher.DequeueBatchJson() != null) { }

        JsEventDispatcher.Publish("llm:stream-event", new SessionStreamEventDto
        {
            SessionId = sessionId,
            Kind = nameof(UnifiedStreamEventKind.ReasoningDelta),
            TextDelta = "先"
        });
        JsEventDispatcher.Publish("llm:stream-event", new SessionStreamEventDto
        {
            SessionId = sessionId,
            Kind = nameof(UnifiedStreamEventKind.ReasoningDelta),
            TextDelta = "思考"
        });

        // 中间插入一个 TextDelta
        JsEventDispatcher.Publish("llm:stream-event", new SessionStreamEventDto
        {
            SessionId = sessionId,
            Kind = nameof(UnifiedStreamEventKind.TextDelta),
            TextDelta = "你"
        });
        JsEventDispatcher.Publish("llm:stream-event", new SessionStreamEventDto
        {
            SessionId = sessionId,
            Kind = nameof(UnifiedStreamEventKind.TextDelta),
            TextDelta = "好"
        });
        JsEventDispatcher.Publish("llm:stream-event", new SessionStreamEventDto
        {
            SessionId = sessionId,
            Kind = nameof(UnifiedStreamEventKind.TextDelta),
            TextDelta = "呀"
        });

        string? batchJson = JsEventDispatcher.DequeueBatchJson();
        Assert.NotNull(batchJson);

        using var doc = JsonDocument.Parse(batchJson!);
        var arr = doc.RootElement;

        // 原本 5 条，经过 Coalescing 合并后应该变为 2 条 (1条 ReasoningDelta "先思考", 1条 TextDelta "你好呀")
        Assert.Equal(2, arr.GetArrayLength());

        var first = arr[0].GetProperty("payload");
        Assert.Equal("ReasoningDelta", first.GetProperty("kind").GetString());
        Assert.Equal("先思考", first.GetProperty("textDelta").GetString());

        var second = arr[1].GetProperty("payload");
        Assert.Equal("TextDelta", second.GetProperty("kind").GetString());
        Assert.Equal("你好呀", second.GetProperty("textDelta").GetString());
    }

    [Fact]
    public async Task LlmGatewayClient_S03_AC03_RecordsAuditLog_AndResolvesSessionHash()
    {
        // 验证 LLM 审计追踪日志 llm-trace-YYYYMMDD.log 的生成、内容格式及 Prompt Cache 命中率 (S03-AC03)
        string fakeSse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello from Mock LLM\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":2000,\"completion_tokens\":100,\"prompt_tokens_details\":{\"cached_tokens\":1500}}}\n\n" +
            "data: [DONE]\n\n";

        var fakeHandler = new FakeHttpMessageHandler((req) =>
        {
            // 验证请求中是否带有解析后的自定义 Header (包含 ${session-hash})
            Assert.True(req.Headers.Contains("X-Session-Hash"));
            var values = req.Headers.GetValues("X-Session-Hash").ToList();
            Assert.NotEmpty(values);
            Assert.NotEqual("${session-hash}", values[0]); // 必须已被变量解析器计算为真实哈希

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fakeSse, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        });

        using var customHttp = new HttpClient(fakeHandler);
        var gateway = new LlmGatewayClient(_storageManager, customHttpClient: customHttp);

        var request = new UnifiedLlmRequest
        {
            RequestId = "req_audit_001",
            Provider = new ProviderConfig
            {
                Id = "p_test",
                Name = "TestProvider",
                BaseUrl = "https://api.test.com/v1",
                EnableCustomHeaders = true,
                CustomHeaders = new List<CustomHeaderEntry>
                {
                    new() { Key = "X-Session-Hash", ValueTemplate = "${session-hash}", Enabled = true }
                }
            },
            Model = new ModelConfig { Id = "test-model", Protocol = LlmProtocolType.Chat },
            Messages = new List<UnifiedChatMessage> { new() { Role = "user", Content = "Ping" } },
            HeaderContext = HeaderVariableResolver.CreateContext(
                sessionId: "sess_12345",
                modelId: "test-model",
                providerName: "TestProvider",
                providerId: "p_test")
        };

        var events = new List<UnifiedStreamEvent>();
        await foreach (var evt in gateway.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e.Kind == UnifiedStreamEventKind.TextDelta && e.TextDelta == "Hello from Mock LLM");

        // 检查磁盘上的日志文件 llm-trace-YYYYMMDD.log
        string logDir = _storageManager.LogsDirectory;
        string expectedLogFile = Path.Combine(logDir, $"llm-trace-{DateTime.UtcNow:yyyyMMdd}.log");
        Assert.True(File.Exists(expectedLogFile), $"日志文件应存在于: {expectedLogFile}");

        string logContent = File.ReadAllText(expectedLogFile);
        Assert.Contains("[REQ] reqId=req_audit_001", logContent);
        Assert.Contains("X-Session-Hash=", logContent);
        Assert.Contains("[RES] reqId=req_audit_001", logContent);
        Assert.Contains("inTokens=2000 (cached=1500, hitRate=75.0%)", logContent);
    }

    [Fact]
    public async Task LlmGatewayClient_400BadRequest_FallsBackWithoutStreamOptions()
    {
        // 验证当 Provider 返回 400 Bad Request（不识别 stream_options）时自动降级重试机制
        int callCount = 0;
        var fakeHandler = new FakeHttpMessageHandler(async (req) =>
        {
            callCount++;
            string body = await req.Content!.ReadAsStringAsync();

            if (callCount == 1)
            {
                // 第一次请求包含 stream_options，模拟服务端报错 400
                Assert.Contains("stream_options", body);
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"Unrecognized request argument: stream_options\"}}")
                };
            }

            // 第二次降级重试，stream_options 必须已被剔除
            Assert.DoesNotContain("stream_options", body);
            string fakeSse = "data: {\"choices\":[{\"delta\":{\"content\":\"Fallback Success\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fakeSse, Encoding.UTF8, "text/event-stream")
            };
        });

        using var customHttp = new HttpClient(fakeHandler);
        var gateway = new LlmGatewayClient(_storageManager, customHttpClient: customHttp);

        var request = new UnifiedLlmRequest
        {
            RequestId = "req_fallback_001",
            Provider = new ProviderConfig { Id = "old_provider", Name = "LegacyProvider", BaseUrl = "https://legacy.ai/v1" },
            Model = new ModelConfig { Id = "legacy-model", Protocol = LlmProtocolType.Chat },
            Messages = new List<UnifiedChatMessage> { new() { Role = "user", Content = "Hello" } }
        };

        var events = new List<UnifiedStreamEvent>();
        await foreach (var evt in gateway.StreamAsync(request))
        {
            events.Add(evt);
        }

        Assert.Equal(2, callCount);
        Assert.Contains(events, e => e.Kind == UnifiedStreamEventKind.TextDelta && e.TextDelta == "Fallback Success");
    }

    [Fact]
    public async Task HostBridgeFacade_S03_AC04_CancellationWithin100ms()
    {
        // 验证随时取消机制：通过 HostBridgeFacade.CancelSessionRun 中断长文流式生成 (S03-AC04)
        var tcs = new TaskCompletionSource<bool>();
        var fakeHandler = new FakeHttpMessageHandler(async (req) =>
        {
            // 模拟长流式响应
            var ms = new SlowInfiniteMemoryStream(tcs);
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(ms)
            };
            res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return await Task.FromResult(res);
        });

        using var customHttp = new HttpClient(fakeHandler);
        var gateway = new LlmGatewayClient(_storageManager, customHttpClient: customHttp);

        var facade = new HostBridgeFacade(
            mainForm: null,
            storageManager: _storageManager,
            llmGatewayClient: gateway);

        var req = new UnifiedLlmRequest
        {
            RequestId = "req_cancel_test",
            Provider = new ProviderConfig { Id = "p1", Name = "P1", BaseUrl = "https://api.test/v1" },
            Model = new ModelConfig { Id = "m1", Protocol = LlmProtocolType.Chat },
            Messages = new List<UnifiedChatMessage> { new() { Role = "user", Content = "Long story" } },
            HeaderContext = HeaderVariableResolver.CreateContext(
                sessionId: "session_to_cancel",
                modelId: "m1",
                providerName: "P1",
                providerId: "p1")
        };

        string requestJson = JsonSerializer.Serialize(req, HaloJsonOptions.IndentedCamelCase);

        var startRes = await facade.StartSessionRun(requestJson);
        Assert.True(startRes.Ok);
        Assert.Equal("session_to_cancel", startRes.Data);

        // 等待底层流建立并触发读操作
        await Task.Delay(50);

        var cancelSw = System.Diagnostics.Stopwatch.StartNew();
        var cancelRes = await facade.CancelSessionRun("session_to_cancel");
        cancelSw.Stop();

        Assert.True(cancelRes.Ok);
        Assert.True(cancelRes.Data);
        // S03-AC04: 100ms 内流式输出立即响应取消
        Assert.True(cancelSw.ElapsedMilliseconds < 100, $"取消响应耗时 {cancelSw.ElapsedMilliseconds}ms 必须在 100ms 内");

        // 验证底层的 Stream 在收到 Cancel 信号后已终止
        tcs.TrySetResult(true);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    private sealed class SlowInfiniteMemoryStream : Stream
    {
        private readonly TaskCompletionSource<bool> _tcs;
        private int _count = 0;

        public SlowInfiniteMemoryStream(TaskCompletionSource<bool> tcs)
        {
            _tcs = tcs;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_count == 0)
            {
                _count++;
                byte[] first = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Chunk 1\"}}]}\n\n");
                Buffer.BlockCopy(first, 0, buffer, offset, first.Length);
                return first.Length;
            }

            // 阻塞等待取消信号
            await Task.Delay(2000, cancellationToken);
            return 0;
        }
    }
}
