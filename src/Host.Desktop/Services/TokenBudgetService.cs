using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// 基于模型配额与滑动窗口的 Token 预算与成对裁剪服务
/// </summary>
public sealed partial class TokenBudgetService : ITokenBudgetService
{
    public const string TruncationNotice = "[注：为适配当前模型最大上下文限制，较早的历史对话已自动折叠省略]";
    public const int DefaultSafetyBuffer = 1024;

    private readonly ProviderConfigService? _providerConfigService;
    private readonly IWorkspaceSessionService? _workspaceSessionService;

    public TokenBudgetService(
        ProviderConfigService? providerConfigService = null,
        IWorkspaceSessionService? workspaceSessionService = null
    )
    {
        _providerConfigService = providerConfigService;
        _workspaceSessionService = workspaceSessionService;
    }

    /// <summary>
    /// 纯 C# 离线启发式 Token 估算：
    /// ASCII 英文字符/代码符号：约 3.6 chars / token
    /// CJK 中日韩字符：约 1.55 tokens / 字符
    /// 其余字符：约 1.2 tokens / 字符
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int asciiCount = 0;
        int cjkCount = 0;
        int otherCount = 0;

        foreach (char c in text)
        {
            if (
                (c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0x3400 && c <= 0x4DBF)
                || (c >= 0x20000 && c <= 0x2A6DF)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0x3000 && c <= 0x303F)
            )
            {
                cjkCount++;
            }
            else if (c <= 127)
            {
                asciiCount++;
            }
            else
            {
                otherCount++;
            }
        }

        double tokens = (asciiCount / 3.6) + (cjkCount * 1.55) + (otherCount * 1.2);
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    int ITokenBudgetService.EstimateTokens(string? text) => EstimateTokens(text);

    int ITokenBudgetService.EstimateMessageTokens(UnifiedChatMessage message) =>
        EstimateMessageTokens(message);

    /// <summary>
    /// need04-05：纯启发式估算（不再消费 InputTokens/OutputTokens 快照，避免总量被当增量累加）。
    /// 需要会话总量请用 <see cref="ResolveUsedInputTokens"/>。
    /// need09-02：图片开销 = 每张存活引用 image:&lt;sha16&gt;:&lt;w&gt;x&lt;h&gt;:&lt;mime&gt; 计 <see cref="ImageTokensPerImage"/>（默认约1000，可配）；
    /// 已被裁剪为 <see cref="ImageOmittedPlaceholder"/> 的占位只按文本计费，不再加图片开销。
    /// </summary>
    public static int EstimateMessageTokens(UnifiedChatMessage message)
    {
        if (message == null)
            return 0;
        int tokens = 6; // 固定消息结构开销
        tokens += EstimateTokens(message.Role);
        tokens += EstimateTokens(message.Content);
        tokens += EstimateTokens(message.ReasoningContent);
        tokens += EstimateTokens(message.Name);
        if (message.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            foreach (var call in message.ToolCalls)
            {
                tokens += 12; // 每个 ToolCall 结构开销
                tokens += EstimateTokens(call.CallId);
                tokens += EstimateTokens(call.FunctionName);
                tokens += EstimateTokens(call.ArgumentsJson);
            }
        }
        var (liveImages, _) = CountMessageImages(message);
        tokens += liveImages * Math.Max(0, ImageTokensPerImage);
        return tokens;
    }

    /// <summary>need09-02：图片引用被裁剪后的占位（保留文字，只去图片开销）。</summary>
    public const string ImageOmittedPlaceholder = "[image omitted]";

    /// <summary>need09-02：单张存活图片引用的固定开销（tokens，默认约1000，可配）。</summary>
    public static int ImageTokensPerImage { get; set; } = 1000;
    private static readonly Regex ImageRefRegex =
        new(@"image:[0-9A-Fa-f]{16}:\d+x\d+:[A-Za-z0-9/+.\-]+", RegexOptions.Compiled);

    /// <summary>need09-02：统计文本中的存活图片引用数（归一化形如 image:&lt;sha16&gt;:&lt;w&gt;x&lt;h&gt;:&lt;mime&gt;）。</summary>
    public static int CountLiveImageRefs(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        try
        {
            return ImageRefRegex.Matches(text).Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>need09-02：统计文本中的图片占位数。</summary>
    public static int CountOmittedImagePlaceholders(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int n = 0,
            idx = 0;
        while ((idx = text.IndexOf(ImageOmittedPlaceholder, idx, StringComparison.Ordinal)) >= 0)
        {
            n++;
            idx += ImageOmittedPlaceholder.Length;
        }
        return n;
    }

    /// <summary>need09-02：统计单条消息的（存活引用， 占位）数（扫描 Content + ReasoningContent）。</summary>
    public static (int live, int omitted) CountMessageImages(UnifiedChatMessage? message)
    {
        if (message == null)
            return (0, 0);
        string combined =
            (message.Content ?? string.Empty) + "\n" + (message.ReasoningContent ?? string.Empty);
        return (CountLiveImageRefs(combined), CountOmittedImagePlaceholders(combined));
    }

    /// <summary>
    /// need04-05：会话总量 = 末条带精确值的 assistant 快照；无则回退启发式 Sum。
    /// 网关回的 InputTokens 是“当轮请求总量（含全部历史）”，禁止 Sum 累加。
    /// </summary>
    public static int ResolveUsedInputTokens(IReadOnlyList<UnifiedChatMessage>? messages)
    {
        if (messages != null)
        {
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var v = messages[i]?.InputTokens;
                if (v.HasValue && v.Value > 0)
                    return (int)v.Value;
            }
        }
        if (messages == null || messages.Count == 0)
            return 0;
        return messages.Sum(m => EstimateMessageTokens(m));
    }

    /// <summary>need04-05：末条 assistant 的 CachedTokens（钳制 ≤ usedInput），无则 0。</summary>
    public static int ResolveCachedTokens(
        IReadOnlyList<UnifiedChatMessage>? messages,
        int usedInput
    )
    {
        if (messages != null)
        {
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var v = messages[i]?.CachedTokens;
                if (v.HasValue && v.Value > 0)
                    return Math.Min((int)v.Value, Math.Max(0, usedInput));
            }
        }
        return 0;
    }

    /// <summary>
    /// need05-01：与 ResolveUsedInputTokens 并列，返回是否估算 + 可空快照。
    /// 口径：IN=占用快照（末轮 prompt），cache⊆IN，无精确值 = unknown。
    /// </summary>
    public static (int? prompt, int? cached, bool isEstimated) ResolveOccupancy(
        IReadOnlyList<UnifiedChatMessage>? messages
    )
    {
        int? p = null;
        int? c = null;
        if (messages != null)
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m == null)
                    continue;
                if (p == null && m.InputTokens.HasValue && m.InputTokens.Value > 0)
                    p = (int)m.InputTokens.Value;
                if (c == null && m.CachedTokens.HasValue && m.CachedTokens.Value > 0)
                    c = (int)m.CachedTokens.Value;
                if (p != null)
                    break; // 以末个 prompt 快照为准；cached 取该轮或更近轮的最大可见值后钳制
            }
        if (p == null)
            return (null, null, true);
        if (c.HasValue)
            c = Math.Min(c.Value, Math.Max(0, p.Value));
        return (p, c, false);
    }

    /// <summary>
    /// 将消息序列分组为原子轮次组（Atomic Turns）：
    /// 普通 User 消息独立一组；
    /// 不带 ToolCalls 的 Assistant 独立一组；
    /// 带 ToolCalls 的 Assistant 与其后的 Tool 消息组合并为一个不可分割的组。
    /// </summary>
    public List<AtomicMessageTurnGroup> GroupIntoAtomicTurns(
        IReadOnlyList<UnifiedChatMessage> messages
    )
    {
        var groups = new List<AtomicMessageTurnGroup>();
        if (messages == null || messages.Count == 0)
            return groups;

        int i = 0;
        while (i < messages.Count)
        {
            var current = messages[i];

            if (
                string.Equals(current.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && current.ToolCalls != null
                && current.ToolCalls.Count > 0
            )
            {
                var group = new AtomicMessageTurnGroup();
                group.Messages.Add(current);
                i++;

                // 收集紧随其后的所有对应 tool 角色消息
                while (
                    i < messages.Count
                    && string.Equals(messages[i].Role, "tool", StringComparison.OrdinalIgnoreCase)
                )
                {
                    group.Messages.Add(messages[i]);
                    i++;
                }

                groups.Add(group);
            }
            else
            {
                var group = new AtomicMessageTurnGroup();
                group.Messages.Add(current);
                groups.Add(group);
                i++;
            }
        }

        return groups;
    }

    /// <summary>need09-02：裁剪结果（消息 + 可能被极端截断的 memoryPinned 块 + 图片是否被占位）。</summary>
    public sealed record BudgetCutResult(
        List<UnifiedChatMessage> Messages,
        string? MemoryPinnedBlock,
        bool ImagesTruncated
    );

    /// <summary>
    /// 保持工具调用成对完整性并保留首尾锚点的滑动窗口裁剪算法（存量行为：无图片/无 memory 时与老版本逐条同一引用）。
    /// </summary>
    public List<UnifiedChatMessage> BuildWithinBudget(
        IReadOnlyList<UnifiedChatMessage> messages,
        int maxBudget
    ) => BuildWithinBudgetEx(messages, maxBudget).Messages;

    /// <summary>
    /// need09-02：三阶裁剪（冻结顺序）：①丢最旧图片引用（替换为 [image omitted] 占位，保留文字）→
    /// ②丢中间 tool 组（存量原子组逻辑）→ ③极端时截 memoryPinned 超长条（省出的配额让给历史）。
    /// system.stable（含 Skill 索引）永不参与裁剪——本方法只动 history 消息与可选传入的 memory 块。
    /// </summary>
    public BudgetCutResult BuildWithinBudgetEx(
        IReadOnlyList<UnifiedChatMessage> messages,
        int maxBudget,
        string? memoryPinnedBlock = null
    )
    {
        if (messages == null || messages.Count == 0)
        {
            return new BudgetCutResult(new List<UnifiedChatMessage>(), memoryPinnedBlock, false);
        }

        int totalTokens = messages.Sum(m => EstimateMessageTokens(m));
        if (totalTokens <= maxBudget)
        {
            return new BudgetCutResult(messages.ToList(), memoryPinnedBlock, false);
        }

        // ① 先丢最旧图片引用（调用方原始消息不被修改；无引用时直接沿用原引用，走存量路径）
        bool anyRefs = messages.Any(m => CountMessageImages(m).live > 0);
        List<UnifiedChatMessage> working = anyRefs ? CloneMessages(messages) : messages.ToList();
        bool imagesTruncated = anyRefs && DropOldestImageRefs(working, maxBudget);
        if (working.Sum(m => EstimateMessageTokens(m)) <= maxBudget)
        {
            return new BudgetCutResult(working, memoryPinnedBlock, imagesTruncated);
        }

        // ② 丢中间 tool 组（存量原子组逻辑）
        List<UnifiedChatMessage> trimmed = BuildWithinBudgetAtomic(working, maxBudget);

        // ③ 极端时截 memoryPinned 超长条，省出的配额让给历史，重试一次原子组裁剪
        string? mem = memoryPinnedBlock;
        if (
            !string.IsNullOrWhiteSpace(mem)
            && trimmed.Sum(m => EstimateMessageTokens(m)) > maxBudget
        )
        {
            int deficit = trimmed.Sum(m => EstimateMessageTokens(m)) - maxBudget;
            string? cut = TruncateMemoryPinnedForBudget(mem, deficit);
            int freed = EstimateTokens(mem) - EstimateTokens(cut);
            mem = cut;
            if (freed > 0)
            {
                trimmed = BuildWithinBudgetAtomic(working, maxBudget + freed);
            }
        }
        return new BudgetCutResult(trimmed, mem, imagesTruncated);
    }
}
