#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 正文通道内嵌思维链标签的转写验收：既锁定「有标签就分流」，
/// 也锁定「无标签时事件序列与旧行为逐项一致」——后者是防止转写把普通模型带偏的契约。
/// </summary>
public sealed class ThinkTagStreamFilterTests
{
    private static List<ThinkTagSegment> Feed(ThinkTagStreamFilter filter, params string[] chunks)
    {
        var all = new List<ThinkTagSegment>();
        foreach (string chunk in chunks)
        {
            all.AddRange(filter.Append(chunk));
        }

        all.AddRange(filter.FlushEnd());
        return all;
    }

    private static (bool IsReasoning, string Text)[] Shape(IEnumerable<ThinkTagSegment> segments)
    {
        return segments.Select(s => (s.IsReasoning, s.Text)).ToArray();
    }

    [Fact(DisplayName = "正文无标签：分段与输入分块逐一对应，思维链段数为 0")]
    public void NoTags_SegmentsMirrorChunks()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "你好！", "今天天气不错"));

        Assert.Equal(new[] { (false, "你好！"), (false, "今天天气不错") }, shape);
        Assert.Equal(0, filter.ReasoningSegmentCount);
    }

    [Fact(DisplayName = "单包完整标签：标签内进思维链，标签外留正文")]
    public void SingleChunk_TagIsSplitOut()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<think>先想想</think>你好"));

        Assert.Equal(new[] { (true, "先想想"), (false, "你好") }, shape);
        Assert.Equal(1, filter.ReasoningSegmentCount);
    }

    [Fact(DisplayName = "开标签跨包拆分：残尾拼接后再判定，不残留半个标签")]
    public void OpenTagSplitAcrossChunks_IsRejoined()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<thi", "nk>abc</think>正文"));

        Assert.Equal(new[] { (true, "abc"), (false, "正文") }, shape);
    }

    [Fact(DisplayName = "闭标签跨包拆分：思维链与正文分属两段")]
    public void CloseTagSplitAcrossChunks_IsRejoined()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<think>abc</thi", "nk>正文"));

        Assert.Equal(new[] { (true, "abc"), (false, "正文") }, shape);
    }

    [Fact(DisplayName = "疑似前缀但不是标签：不吞字节")]
    public void AngleBracketWithoutTag_IsKeptAsText()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "a < b"));

        Assert.Equal(new[] { (false, "a < b") }, shape);
    }

    [Fact(DisplayName = "多段交替：顺序与原文出现顺序一致")]
    public void MultipleBlocks_KeepOrder()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<think>r1</think>t1<think>r2</think>t2"));

        Assert.Equal(new[] { (true, "r1"), (false, "t1"), (true, "r2"), (false, "t2") }, shape);
    }

    [Fact(DisplayName = "未闭合流结束：已收内容全部算思维链，不丢内容")]
    public void UnclosedThink_KeepsContentAsReasoning()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<think>没说完"));

        Assert.Equal(new[] { (true, "没说完") }, shape);
    }

    [Fact(DisplayName = "空思维链块：不产生空段事件")]
    public void EmptyThinkBlock_ProducesNoEmptySegment()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<think></think>正文"));

        Assert.Equal(new[] { (false, "正文") }, shape);
        Assert.Equal(0, filter.ReasoningSegmentCount);
    }

    [Fact(DisplayName = "大小写变体与带属性写法：按普通正文处理（口径锁定）")]
    public void CaseAndAttributeVariants_StayAsText()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<THINK>x</THINK>", "<think a>b</think>"));

        Assert.Equal(new[] { (false, "<THINK>x</THINK>"), (false, "<think a>b</think>") }, shape);
        Assert.Equal(0, filter.ReasoningSegmentCount);
    }

    [Fact(DisplayName = "标签在一段内被截断：残尾补发不丢字节")]
    public void PendingTagPrefixAtStreamEnd_IsFlushed()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "正文<think"));

        Assert.Equal(new[] { (false, "正文"), (false, "<think") }, shape);
    }

    [Fact(DisplayName = "工具调用穿插：思维链状态跨包保持，工具通道不参与切分")]
    public void ThinkState_SurvivesToolCallInterleave()
    {
        var filter = new ThinkTagStreamFilter();

        var first = Shape(filter.Append("<think>先查目录"));
        Assert.Equal(new[] { (true, "先查目录") }, first);
        Assert.True(filter.InThink);

        // 工具调用本身走适配器独立分支，filter 只吃 content；这里只证明状态未丢。
        var second = Shape(filter.Append("再想想</think>结果如下"));
        Assert.Equal(new[] { (true, "再想想"), (false, "结果如下") }, second);
        Assert.False(filter.InThink);
    }

    [Fact(DisplayName = "适配器集成：content 内思维链 + 工具穿插，事件序与参数完整")]
    public async Task AdapterSplitsInlineThinkAroundToolCall()
    {
        string sseStream =
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>查目录\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_think_1\",\"type\":\"function\",\"function\":{\"name\":\"exec_command\",\"arguments\":\"{\\\"cmd\\\":\\\"dir\\\"}\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"再想想</think>结果如下\"}}]}\n\n"
            + "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n"
            + "data: [DONE]\n\n";

        var events = await ParseAsync(sseStream);

        Assert.Equal(
            new[]
            {
                UnifiedStreamEventKind.StreamStarted,
                UnifiedStreamEventKind.ReasoningDelta,
                UnifiedStreamEventKind.ToolCallStarted,
                UnifiedStreamEventKind.ToolCallArgsDelta,
                UnifiedStreamEventKind.ReasoningDelta,
                UnifiedStreamEventKind.TextDelta,
                UnifiedStreamEventKind.ToolCallCompleted,
                UnifiedStreamEventKind.StreamCompleted
            },
            events.Select(e => e.Kind).ToArray()
        );

        Assert.Equal(
            "查目录再想想",
            string.Concat(
                events
                    .Where(e => e.Kind == UnifiedStreamEventKind.ReasoningDelta)
                    .Select(e => e.TextDelta)
            )
        );

        var text = string.Concat(
            events.Where(e => e.Kind == UnifiedStreamEventKind.TextDelta).Select(e => e.TextDelta)
        );
        Assert.Equal("结果如下", text);
        Assert.DoesNotContain("<think", text);
        Assert.DoesNotContain("</think>", text);

        var toolDone = events.Single(e => e.Kind == UnifiedStreamEventKind.ToolCallCompleted);
        Assert.Equal("call_think_1", toolDone.ToolCall?.CallId);
        Assert.Equal("{\"cmd\":\"dir\"}", toolDone.ToolCall?.ArgumentsJson);
    }

    [Fact(DisplayName = "回归：无标签流的完整事件序列与三通道并行行为不变")]
    public async Task NoTagStream_KeepsLegacyEventSequence()
    {
        string sseStream =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"思考中...\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"你好！\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_abc123\",\"type\":\"function\",\"function\":{\"name\":\"exec_command\",\"arguments\":\"{\\\"cmd\\\":\\\"\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"dir\\\"}\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":80}}}\n\n"
            + "data: [DONE]\n\n";

        var events = await ParseAsync(sseStream);

        Assert.Equal(
            new[]
            {
                UnifiedStreamEventKind.StreamStarted,
                UnifiedStreamEventKind.ReasoningDelta,
                UnifiedStreamEventKind.TextDelta,
                UnifiedStreamEventKind.ToolCallStarted,
                UnifiedStreamEventKind.ToolCallArgsDelta,
                UnifiedStreamEventKind.ToolCallArgsDelta,
                UnifiedStreamEventKind.UsageUpdated,
                UnifiedStreamEventKind.ToolCallCompleted,
                UnifiedStreamEventKind.StreamCompleted
            },
            events.Select(e => e.Kind).ToArray()
        );

        Assert.Equal("思考中...", events[1].TextDelta);
        Assert.Equal("你好！", events[2].TextDelta);
        Assert.Equal(
            80,
            events
                .Single(e => e.Kind == UnifiedStreamEventKind.UsageUpdated)
                .Usage?.CachedInputTokens
        );
        Assert.Equal(
            "{\"cmd\":\"dir\"}",
            events
                .Single(e => e.Kind == UnifiedStreamEventKind.ToolCallCompleted)
                .ToolCall?.ArgumentsJson
        );
    }

    [Fact(DisplayName = "开关关闭：标签原样留在正文，行为回到转写之前")]
    public async Task DisabledMode_KeepsTagsInText()
    {
        string sseStream =
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>先想想</think>你好\"}}]}\n\n"
            + "data: [DONE]\n\n";

        var events = await ParseAsync(sseStream, ThinkTagMode.Off);

        Assert.DoesNotContain(events, e => e.Kind == UnifiedStreamEventKind.ReasoningDelta);
        var text = string.Concat(
            events.Where(e => e.Kind == UnifiedStreamEventKind.TextDelta).Select(e => e.TextDelta)
        );
        Assert.Equal("<think>先想想</think>你好", text);
    }

    [Fact(DisplayName = "思维链内被截断的闭标签残尾：按思维链补发，不漏字节")]
    public void PendingInsideThink_IsFlushedAsReasoning()
    {
        var filter = new ThinkTagStreamFilter();

        var shape = Shape(Feed(filter, "<think>abc</thi"));

        Assert.Equal(new[] { (true, "abc"), (true, "</thi") }, shape);
    }

    [Fact(DisplayName = "残尾补发只发生一次：重复 FlushEnd 不再吐出内容")]
    public void FlushEnd_IsIdempotent()
    {
        var filter = new ThinkTagStreamFilter();
        filter.Append("正文<thi");

        var first = filter.FlushEnd();
        var second = filter.FlushEnd();

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact(DisplayName = "块尾疑似标签前缀：顺延到下包发出，字节不丢也不重复")]
    public void BoundaryPrefix_DefersWithinSameBytes()
    {
        var filter = new ThinkTagStreamFilter();

        var firstChunk = Shape(filter.Append("合计 <"));
        var secondChunk = Shape(filter.Append(" 100"));
        var tail = Shape(filter.FlushEnd());

        // 分段边界被挪了一格（"<" 顺延），拼接结果与输入逐字节相同。
        Assert.Equal(new[] { (false, "合计 ") }, firstChunk);
        Assert.Equal(new[] { (false, "< 100") }, secondChunk);
        Assert.Empty(tail);
        Assert.Equal("合计 < 100", string.Concat(firstChunk.Concat(secondChunk).Select(s => s.Text)));
    }

    [Fact(DisplayName = "开关为 On 时与默认行为一致（同一路径，仅语义声明不同）")]
    public async Task OnMode_MatchesDefaultBehaviour()
    {
        string sseStream =
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>先想</think>正文\"}}]}\n\n"
            + "data: [DONE]\n\n";

        var events = await ParseAsync(sseStream, ThinkTagMode.On);

        Assert.Contains(
            events,
            e => e.Kind == UnifiedStreamEventKind.ReasoningDelta && e.TextDelta == "先想"
        );
        Assert.Equal(
            "正文",
            string.Concat(
                events
                    .Where(e => e.Kind == UnifiedStreamEventKind.TextDelta)
                    .Select(e => e.TextDelta)
            )
        );
    }

    [Fact(DisplayName = "同一适配器实例上交错跑两条流：标签状态互不串味")]
    public async Task ConcurrentStreams_DoNotShareTagState()
    {
        const string tagged =
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>甲想</think>甲说\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"<thi\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"nk>乙想</think>乙说\"}}]}\n\n"
            + "data: [DONE]\n\n";
        const string plain =
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>丙想</think>丙说\"}}]}\n\n"
            + "data: [DONE]\n\n";

        var autoTask = ParseAsync(tagged, ThinkTagMode.Auto);
        var offTask = ParseAsync(plain, ThinkTagMode.Off);
        var results = await Task.WhenAll(autoTask, offTask);

        var autoEvents = results[0];
        var offEvents = results[1];

        Assert.Equal(
            "甲想乙想",
            string.Concat(
                autoEvents
                    .Where(e => e.Kind == UnifiedStreamEventKind.ReasoningDelta)
                    .Select(e => e.TextDelta)
            )
        );
        Assert.Equal(
            "甲说乙说",
            string.Concat(
                autoEvents
                    .Where(e => e.Kind == UnifiedStreamEventKind.TextDelta)
                    .Select(e => e.TextDelta)
            )
        );

        // 关闭转写的那条流完全不受另一条流的标签状态影响。
        Assert.DoesNotContain(offEvents, e => e.Kind == UnifiedStreamEventKind.ReasoningDelta);
        Assert.Equal(
            "<think>丙想</think>丙说",
            string.Concat(
                offEvents
                    .Where(e => e.Kind == UnifiedStreamEventKind.TextDelta)
                    .Select(e => e.TextDelta)
            )
        );
    }

    [Fact(DisplayName = "残尾先于工具闭合发出：分块边界上的字符不会被排到工具卡之后")]
    public async Task PendingTail_IsEmittedBeforeToolCompletion()
    {
        string sseStream =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_tail_1\",\"type\":\"function\",\"function\":{\"name\":\"exec_command\",\"arguments\":\"{}\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"content\":\"结果<thi\"}}]}\n\n"
            + "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n"
            + "data: [DONE]\n\n";

        var events = await ParseAsync(sseStream);

        var text = string.Concat(
            events.Where(e => e.Kind == UnifiedStreamEventKind.TextDelta).Select(e => e.TextDelta)
        );
        Assert.Equal("结果<thi", text);

        int textIndex = events.FindIndex(e => e.Kind == UnifiedStreamEventKind.TextDelta);
        int toolDoneIndex = events.FindIndex(e =>
            e.Kind == UnifiedStreamEventKind.ToolCallCompleted
        );
        Assert.True(textIndex >= 0 && toolDoneIndex >= 0);
        Assert.True(textIndex < toolDoneIndex, "残尾文本必须先于工具闭合事件下发，否则时间线会把旧文本画到工具卡下方");
    }

    private static async Task<List<UnifiedStreamEvent>> ParseAsync(
        string sseStream,
        ThinkTagMode mode = ThinkTagMode.Auto
    )
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseStream));
        var adapter = new ChatCompletionsAdapter();
        var events = new List<UnifiedStreamEvent>();

        await foreach (var evt in adapter.ParseStreamAsync(stream, mode))
        {
            events.Add(evt);
        }

        return events;
    }
}

/// <summary>
/// 网关层接线验收：转写模式由请求里的模型配置下发到 Chat 适配器，
/// 单测直接调用适配器时走不到这段路由，故在此串联一遍。
/// </summary>
public sealed class ThinkTagGatewayWiringTests : IDisposable
{
    private const string ThinkSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"<think>先想</think>正文\"}}]}\n\n"
        + "data: [DONE]\n\n";

    private readonly string _tempRoot;
    private readonly StoragePathManager _storage;

    public ThinkTagGatewayWiringTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentThinkTag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_tempRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响结论
        }
    }

    [Fact(DisplayName = "网关：模型配置为默认时正文标签被转写；关闭后原样透传")]
    public async Task GatewayHonorsModelThinkTagMode()
    {
        var autoEvents = await StreamThroughGatewayAsync(ThinkTagMode.Auto);
        Assert.Contains(
            autoEvents,
            e => e.Kind == UnifiedStreamEventKind.ReasoningDelta && e.TextDelta == "先想"
        );
        Assert.DoesNotContain("<think", ConcatText(autoEvents));

        var offEvents = await StreamThroughGatewayAsync(ThinkTagMode.Off);
        Assert.DoesNotContain(offEvents, e => e.Kind == UnifiedStreamEventKind.ReasoningDelta);
        Assert.Equal("<think>先想</think>正文", ConcatText(offEvents));
    }

    private static string ConcatText(List<UnifiedStreamEvent> events)
    {
        return string.Concat(
            events.Where(e => e.Kind == UnifiedStreamEventKind.TextDelta).Select(e => e.TextDelta)
        );
    }

    private async Task<List<UnifiedStreamEvent>> StreamThroughGatewayAsync(ThinkTagMode mode)
    {
        using var http = new HttpClient(new SingleResponseHandler(ThinkSse));
        var gateway = new LlmGatewayClient(_storage, customHttpClient: http);

        var request = new UnifiedLlmRequest
        {
            RequestId = $"req_think_{mode}",
            Provider = new ProviderConfig
            {
                Id = "think_provider",
                Name = "ThinkProvider",
                BaseUrl = "https://think.test/v1"
            },
            Model = new ModelConfig
            {
                Id = "think-model",
                Protocol = LlmProtocolType.Chat,
                ThinkTagMode = mode
            },
            Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User("你好") }
        };

        var events = new List<UnifiedStreamEvent>();
        await foreach (var evt in gateway.StreamAsync(request))
        {
            events.Add(evt);
        }

        return events;
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly string _sse;

        public SingleResponseHandler(string sse) => _sse = sse;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken
        )
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_sse, Encoding.UTF8, "text/event-stream")
                }
            );
        }
    }
}
