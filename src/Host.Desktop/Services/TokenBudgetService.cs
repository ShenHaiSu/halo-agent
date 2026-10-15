using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;

namespace Host.Desktop.Services;

/// <summary>
/// 原子消息轮次事务组（确保带 tool_calls 的 assistant 消息与随后的 tool 结果消息不可分割）
/// </summary>
public sealed class AtomicMessageTurnGroup
{
    public List<UnifiedChatMessage> Messages { get; } = new();

    public int EstimatedTokens =>
        Messages.Sum(m => TokenBudgetService.EstimateMessageTokens(m));
}

public interface ITokenBudgetService
{
    int EstimateTokens(string? text);
    int EstimateMessageTokens(UnifiedChatMessage message);
    List<AtomicMessageTurnGroup> GroupIntoAtomicTurns(IReadOnlyList<UnifiedChatMessage> messages);
    List<UnifiedChatMessage> BuildWithinBudget(IReadOnlyList<UnifiedChatMessage> messages, int maxBudget);
    List<UnifiedChatMessage> TruncateToBudget(ModelConfig model, IReadOnlyList<UnifiedChatMessage> messages, int safetyBuffer = 1024);
    ContextBudgetStatusDto CalculateBudgetStatus(ModelConfig model, IReadOnlyList<UnifiedChatMessage> messages, string? sessionHash = null);
    Task<ContextBudgetStatusDto> GetBudgetStatusAsync(string providerId, string modelId, string workspaceId, string sessionId, CancellationToken ct = default);
}

/// <summary>
/// 基于模型配额与滑动窗口的 Token 预算与成对裁剪服务
/// </summary>
public sealed class TokenBudgetService : ITokenBudgetService
{
    public const string TruncationNotice = "[注：为适配当前模型最大上下文限制，较早的历史对话已自动折叠省略]";
    public const int DefaultSafetyBuffer = 1024;

    private readonly ProviderConfigService? _providerConfigService;
    private readonly IWorkspaceSessionService? _workspaceSessionService;

    public TokenBudgetService(
        ProviderConfigService? providerConfigService = null,
        IWorkspaceSessionService? workspaceSessionService = null)
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
        if (string.IsNullOrEmpty(text)) return 0;

        int asciiCount = 0;
        int cjkCount = 0;
        int otherCount = 0;

        foreach (char c in text)
        {
            if ((c >= 0x4E00 && c <= 0x9FFF) ||
                (c >= 0x3400 && c <= 0x4DBF) ||
                (c >= 0x20000 && c <= 0x2A6DF) ||
                (c >= 0xF900 && c <= 0xFAFF) ||
                (c >= 0x3000 && c <= 0x303F))
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
    int ITokenBudgetService.EstimateMessageTokens(UnifiedChatMessage message) => EstimateMessageTokens(message);

     /// <summary>
     /// need04-05：纯启发式估算（不再消费 InputTokens/OutputTokens 快照，避免总量被当增量累加）。
     /// 需要会话总量请用 <see cref="ResolveUsedInputTokens"/>。
     /// </summary>
     public static int EstimateMessageTokens(UnifiedChatMessage message)
    {
         if (message == null) return 0;
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
         return tokens;
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
                 if (v.HasValue && v.Value > 0) return (int)v.Value;
            }
        }
         if (messages == null || messages.Count == 0) return 0;
         return messages.Sum(m => EstimateMessageTokens(m));
    }
 
     /// <summary>need04-05：末条 assistant 的 CachedTokens（钳制 ≤ usedInput），无则 0。</summary>
     public static int ResolveCachedTokens(IReadOnlyList<UnifiedChatMessage>? messages, int usedInput)
    {
         if (messages != null)
        {
             for (int i = messages.Count - 1; i >= 0; i--)
            {
                 var v = messages[i]?.CachedTokens;
                 if (v.HasValue && v.Value > 0) return Math.Min((int)v.Value, Math.Max(0, usedInput));
            }
        }
         return 0;
    }

    /// <summary>
    /// 将消息序列分组为原子轮次组（Atomic Turns）：
    /// 普通 User 消息独立一组；
    /// 不带 ToolCalls 的 Assistant 独立一组；
    /// 带 ToolCalls 的 Assistant 与其后的 Tool 消息组合并为一个不可分割的组。
    /// </summary>
    public List<AtomicMessageTurnGroup> GroupIntoAtomicTurns(IReadOnlyList<UnifiedChatMessage> messages)
    {
        var groups = new List<AtomicMessageTurnGroup>();
        if (messages == null || messages.Count == 0) return groups;

        int i = 0;
        while (i < messages.Count)
        {
            var current = messages[i];

            if (string.Equals(current.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                current.ToolCalls != null && current.ToolCalls.Count > 0)
            {
                var group = new AtomicMessageTurnGroup();
                group.Messages.Add(current);
                i++;

                // 收集紧随其后的所有对应 tool 角色消息
                while (i < messages.Count && string.Equals(messages[i].Role, "tool", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 保持工具调用成对完整性并保留首尾锚点的滑动窗口裁剪算法
    /// </summary>
    public List<UnifiedChatMessage> BuildWithinBudget(IReadOnlyList<UnifiedChatMessage> messages, int maxBudget)
    {
        if (messages == null || messages.Count == 0)
        {
            return new List<UnifiedChatMessage>();
        }

        var groups = GroupIntoAtomicTurns(messages);
        int totalTokens = groups.Sum(g => g.EstimatedTokens);
        if (totalTokens <= maxBudget)
        {
            return messages.ToList();
        }

        // 寻找首个包含 user 角色的组作为锚点（包含其之前可能的 system 消息组）
        int firstUserGroupIndex = groups.FindIndex(g => g.Messages.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)));
        if (firstUserGroupIndex < 0)
        {
            firstUserGroupIndex = 0;
        }

        int lastGroupIndex = groups.Count - 1;

        // 若总组数过少，无法剔除中间组
        if (firstUserGroupIndex >= lastGroupIndex - 1)
        {
            return messages.ToList();
        }

        // 必须保留的锚点 Tokens
        int anchorTokens = groups.Take(firstUserGroupIndex + 1).Sum(g => g.EstimatedTokens);
        int latestTokens = groups[lastGroupIndex].EstimatedTokens;

        var noticeMessage = UnifiedChatMessage.System(TruncationNotice);
        int noticeTokens = EstimateMessageTokens(noticeMessage);

        int budgetForIntermediate = maxBudget - anchorTokens - latestTokens - noticeTokens;
        var retainedIntermediateIndices = new List<int>();

        // 从最新的中间组向较旧的中间组倒序累加
        for (int i = lastGroupIndex - 1; i > firstUserGroupIndex; i--)
        {
            int groupTokens = groups[i].EstimatedTokens;
            if (budgetForIntermediate >= groupTokens)
            {
                retainedIntermediateIndices.Add(i);
                budgetForIntermediate -= groupTokens;
            }
            else
            {
                // 超出预算，中断，放弃更早的中间组
                break;
            }
        }

        // 若保留的中间组数量小于总中间组数量，说明触发了裁剪
        int totalIntermediateCount = lastGroupIndex - 1 - firstUserGroupIndex;
        bool hasTruncated = retainedIntermediateIndices.Count < totalIntermediateCount;

        var result = new List<UnifiedChatMessage>();

        // 1. 添加首部锚点组
        for (int i = 0; i <= firstUserGroupIndex; i++)
        {
            result.AddRange(groups[i].Messages);
        }

        // 2. 若发生裁剪，添加过渡提示
        if (hasTruncated)
        {
            result.Add(noticeMessage);
        }

        // 3. 按原始顺序添加保留的中间组
        retainedIntermediateIndices.Sort();
        foreach (int idx in retainedIntermediateIndices)
        {
            result.AddRange(groups[idx].Messages);
        }

        // 4. 添加最新活动组
        result.AddRange(groups[lastGroupIndex].Messages);

        return result;
    }

    /// <summary>
    /// 根据当前模型配置执行裁剪
    /// AvailableInputBudget = MaxContextTokens - MaxOutputTokens - SafetyBuffer
    /// </summary>
    public List<UnifiedChatMessage> TruncateToBudget(
        ModelConfig model,
        IReadOnlyList<UnifiedChatMessage> messages,
        int safetyBuffer = DefaultSafetyBuffer)
    {
        int maxContext = model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000;
        int maxOutput = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
        int availableInputBudget = Math.Max(1000, maxContext - maxOutput - safetyBuffer);

        return BuildWithinBudget(messages, availableInputBudget);
    }

    /// <summary>
    /// 计算上下文配额状态 DTO
    /// </summary>
    public ContextBudgetStatusDto CalculateBudgetStatus(
        ModelConfig model,
        IReadOnlyList<UnifiedChatMessage> messages,
        string? sessionHash = null)
    {
        int maxContext = model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000;
        int maxOutput = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
        int safetyBuffer = DefaultSafetyBuffer;
        // need04-05：快照语义：IN=末条精确快照（无则估算）；available 必须减去 used；分母统一 maxContext
        int usedInput = ResolveUsedInputTokens(messages);
        int availableInput = Math.Max(0, maxContext - maxOutput - safetyBuffer - usedInput);
        double percentage = maxContext > 0
            ? Math.Min(100.0, Math.Round((double)usedInput / maxContext * 100.0, 1))
            : 0.0;
        bool isNearLimit = percentage >= 85.0;
        return new ContextBudgetStatusDto
        {
            TotalContextTokens = maxContext,
            MaxOutputTokens = maxOutput,
            SafetyBuffer = safetyBuffer,
            UsedInputTokens = usedInput,
            AvailableInputTokens = availableInput,
            UsagePercentage = percentage,
            IsNearLimit = isNearLimit,
            SessionHash = sessionHash,
            Protocol = model.Protocol == LlmProtocolType.Resp ? "resp" : "chat"
        };
    }
 
    public async Task<ContextBudgetStatusDto> GetBudgetStatusAsync(
        string providerId,
        string modelId,
        string workspaceId,
        string sessionId,
        CancellationToken ct = default)
    {
        ModelConfig model = new() { Id = modelId, MaxContextTokens = 128000, MaxOutputTokens = 4096, Protocol = LlmProtocolType.Chat };
        if (_providerConfigService != null)
        {
            try
            {
                 var doc = _providerConfigService.GetProvidersConfigInternal();
                 var provider = doc.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
                 var m = provider?.Models.FirstOrDefault(x => string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));
                 if (m != null)
                 {
                     model = m;
                 }
            }
            catch
            {
                // 使用默认回退
            }
        }
        List<UnifiedChatMessage> messages = new();
        string? sessionHash = null;
        if (_workspaceSessionService != null && !string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                messages = await _workspaceSessionService.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
                var sessions = await _workspaceSessionService.GetSessionsByWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
                var currentSession = sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
                sessionHash = currentSession?.SessionHash;
            }
            catch
            {
                // 使用默认回退
            }
        }
        return CalculateBudgetStatus(model, messages, sessionHash);
    }
}
