#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Adapters;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>need05-02：稳定前缀（至少 4 个）。</summary>
public sealed class Stage05StablePrefixTests
{
    private readonly TokenBudgetService _budgetService = new();

    private static ModelConfig SmallModel() =>
        new()
        {
            Id = "small",
            Protocol = LlmProtocolType.Chat,
            MaxContextTokens = 8192,
            MaxOutputTokens = 2048
        };

    [Fact(DisplayName = "S05-02: 未达高水位原样返回，前缀零抖动")]
    public void Truncate_Below_HighWater_Returns_Identical_Reference()
    {
        var anchor = UnifiedChatMessage.User("锚点任务");
        var reply = UnifiedChatMessage.Assistant("回复");
        reply.InputTokens = 4096; // 50% of 8192
        var messages = new List<UnifiedChatMessage> { anchor, reply };

        var result = _budgetService.TruncateToBudget(SmallModel(), messages);
        Assert.Equal(messages.Count, result.Count);
        for (int i = 0; i < messages.Count; i++)
            Assert.Same(messages[i], result[i]); // 内容逐条同一对象
    }

    [Fact(DisplayName = "S05-02: 超高水位一次裁到低水位，原子组无孤儿")]
    public void Truncate_Above_HighWater_Cuts_To_LowWater_Atomically()
    {
        var messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User("锚点：分析项目代码") };
        for (int i = 0; i < 30; i++)
        {
            var calls = new List<UnifiedToolCall>
            {
                new($"call_{i}", null, "read_file", "{\"p\":1}")
            };
            messages.Add(UnifiedChatMessage.Assistant($"scan {i}", calls));
            messages.Add(UnifiedChatMessage.Tool($"call_{i}", new string('x', 200), "read_file"));
        }
        var latest = UnifiedChatMessage.User("最新结论：给出补丁");
        messages.Add(latest);
        // 占用 95%：快照打在末条
        latest.InputTokens = (int)(8192 * 0.95);

        var result = _budgetService.TruncateToBudget(SmallModel(), messages);
        // 含锚点 + notice + 最新组
        Assert.Equal("锚点：分析项目代码", result[0].Content);
        Assert.Contains(
            result,
            m => m.Role == "system" && m.Content != null && m.Content.Contains("较早的历史对话已自动折叠省略")
        );
        Assert.Equal("最新结论：给出补丁", result[^1].Content);
        // tool 组无孤儿
        var remainingCalls = result
            .Where(m => m.ToolCalls != null && m.ToolCalls.Count > 0)
            .ToList();
        var remainingResults = result.Where(m => m.Role == "tool").ToList();
        if (remainingCalls.Count == 0)
        {
            Assert.Empty(remainingResults);
        }
        else
        {
            var callIds = remainingCalls
                .SelectMany(m => m.ToolCalls!)
                .Select(c => c.CallId)
                .ToHashSet();
            var resCallIds = remainingResults.Select(r => r.ToolCallId!).ToHashSet();
            Assert.Subset(callIds, resCallIds);
        }
    }

    [Fact(DisplayName = "S05-02: system 稳定头不含日期，日期只在尾部")]
    public async Task SystemPrompt_Stable_Head_Excludes_Date()
    {
        var ws = new WorkspaceInfo { Name = "w", RootPath = "C:\\proj" };
        string p1 = await SystemPromptBuilder.BuildAsync(ws, "Python 3.11.9 (py.exe)");
        string p2 = await SystemPromptBuilder.BuildAsync(ws, "Python 3.12.0 (other.exe)");

        Assert.StartsWith("You are Halo-Agent", p1);
        Assert.Contains("cmd.exe", p1);
        string head1 = p1.Split("## 4. Session Ephemeral")[0];
        string head2 = p2.Split("## 4. Session Ephemeral")[0];
        Assert.Equal(head1, head2); // 去掉尾段后两串完全相等
        Assert.DoesNotContain("Current Date", head1);
        Assert.Contains("Current Date", p1); // 日期只在尾部
    }

    [Fact(DisplayName = "S05-02: 双协议工具按 name 升序序列化")]
    public void Tools_Serialized_In_Name_Order()
    {
        var tools = new List<UnifiedToolDefinition>
        {
            UnifiedToolDefinition.Create("zeta_tool", "z", "{}"),
            UnifiedToolDefinition.Create("alpha_tool", "a", "{}"),
            UnifiedToolDefinition.Create("mid_tool", "m", "{}"),
        };
        var request = new UnifiedLlmRequest
        {
            RequestId = "r1",
            Provider = new ProviderConfig
            {
                Id = "p",
                Name = "P",
                BaseUrl = "https://x/v1"
            },
            Model = new ModelConfig { Id = "m", Protocol = LlmProtocolType.Chat },
            HeaderContext = new HeaderResolutionContext(
                "w",
                "n",
                "p",
                "s",
                "h",
                "p",
                "pn",
                "m",
                "r1"
            ),
            SystemInstructions = "sys",
            Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User("hi") },
            Tools = tools,
        };

        string chatJson = new ChatCompletionsAdapter().SerializeRequest(request);
        using (var doc = JsonDocument.Parse(chatJson))
        {
            var names = doc
                .RootElement.GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("function").GetProperty("name").GetString())
                .ToList();
            Assert.Equal(new[] { "alpha_tool", "mid_tool", "zeta_tool" }, names);
        }

        string respJson = new ResponsesApiAdapter().SerializeRequest(request);
        using (var doc = JsonDocument.Parse(respJson))
        {
            var names = doc
                .RootElement.GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString())
                .ToList();
            Assert.Equal(new[] { "alpha_tool", "mid_tool", "zeta_tool" }, names);
        }
    }
}
