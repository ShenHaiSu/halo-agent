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
/// 原子消息轮次事务组（确保带 tool_calls 的 assistant 消息与随后的 tool 结果消息不可分割）
/// </summary>
public sealed class AtomicMessageTurnGroup
{
    public List<UnifiedChatMessage> Messages { get; } = new();

    public int EstimatedTokens => Messages.Sum(m => TokenBudgetService.EstimateMessageTokens(m));
}

public interface ITokenBudgetService
{
    int EstimateTokens(string? text);
    int EstimateMessageTokens(UnifiedChatMessage message);
    List<AtomicMessageTurnGroup> GroupIntoAtomicTurns(IReadOnlyList<UnifiedChatMessage> messages);
    List<UnifiedChatMessage> BuildWithinBudget(
        IReadOnlyList<UnifiedChatMessage> messages,
        int maxBudget
    );
    List<UnifiedChatMessage> TruncateToBudget(
        ModelConfig model,
        IReadOnlyList<UnifiedChatMessage> messages,
        int safetyBuffer = 1024,
        int residentStableTokens = 0
    );
    ContextBudgetStatusDto CalculateBudgetStatus(
        ModelConfig model,
        IReadOnlyList<UnifiedChatMessage> messages,
        string? sessionHash = null,
        int residentStableTokens = 0
    );
    Task<ContextBudgetStatusDto> GetBudgetStatusAsync(
        string providerId,
        string modelId,
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );
}
