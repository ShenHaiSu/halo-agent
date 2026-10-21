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

public sealed partial class TokenBudgetService : ITokenBudgetService
{
    /// <summary>need09-02：存量原子组裁剪内核（行为与老 BuildWithinBudget 完全一致）。</summary>
    private List<UnifiedChatMessage> BuildWithinBudgetAtomic(
        IReadOnlyList<UnifiedChatMessage> messages,
        int maxBudget
    )
    {
        var groups = GroupIntoAtomicTurns(messages);
        int totalTokens = groups.Sum(g => g.EstimatedTokens);
        if (totalTokens <= maxBudget)
        {
            return messages.ToList();
        }

        // 寻找首个包含 user 角色的组作为锚点（包含其之前可能的 system 消息组）
        int firstUserGroupIndex = groups.FindIndex(g =>
            g.Messages.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
        );
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

    /// <summary>need09-02：浅克隆消息（Id/时间戳保留；ToolCalls 列表复制），供图片占位裁剪使用，不碰调用方原始对象。</summary>
    private static List<UnifiedChatMessage> CloneMessages(
        IReadOnlyList<UnifiedChatMessage> messages
    )
    {
        var list = new List<UnifiedChatMessage>(messages.Count);
        foreach (var m in messages)
        {
            list.Add(
                new UnifiedChatMessage
                {
                    Id = m.Id,
                    CreatedAtUtc = m.CreatedAtUtc,
                    Role = m.Role,
                    Content = m.Content,
                    ReasoningContent = m.ReasoningContent,
                    ToolCalls = m.ToolCalls != null ? new List<UnifiedToolCall>(m.ToolCalls) : null,
                    ToolCallId = m.ToolCallId,
                    Name = m.Name,
                    InputTokens = m.InputTokens,
                    OutputTokens = m.OutputTokens,
                    CachedTokens = m.CachedTokens,
                }
            );
        }
        return list;
    }

    /// <summary>need09-02：从最旧消息开始把存活图片引用替换为占位，直到达标或无引用可丢；返回是否发生过替换。</summary>
    private static bool DropOldestImageRefs(List<UnifiedChatMessage> working, int maxBudget)
    {
        bool dropped = false;
        for (int i = 0; i < working.Count; i++)
        {
            if (working.Sum(m => EstimateMessageTokens(m)) <= maxBudget)
                break;
            var msg = working[i];
            if (!string.IsNullOrEmpty(msg.Content) && CountLiveImageRefs(msg.Content) > 0)
            {
                msg.Content = ImageRefRegex.Replace(msg.Content, ImageOmittedPlaceholder);
                dropped = true;
            }
            if (
                !string.IsNullOrEmpty(msg.ReasoningContent)
                && CountLiveImageRefs(msg.ReasoningContent) > 0
            )
            {
                msg.ReasoningContent = ImageRefRegex.Replace(
                    msg.ReasoningContent,
                    ImageOmittedPlaceholder
                );
                dropped = true;
            }
        }
        return dropped;
    }

    /// <summary>
    /// need09-02：极端预算下截断 memoryPinned 渲染块：按 `### ` 节切分，最长节先丢（同长按名升序），
    /// 保持原节顺序；全部丢光则整块消失（返回 string.Empty）。确定性冻结。
    /// </summary>
    public static string? TruncateMemoryPinnedForBudget(string? block, int tokensToFree)
    {
        if (string.IsNullOrWhiteSpace(block) || tokensToFree <= 0)
            return block;
        string norm = block.Replace("\r\n", "\n");
        string[] lines = norm.Split('\n');
        string? header = null;
        int start = 0;
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("## ", StringComparison.Ordinal))
        {
            header = lines[0];
            start = 1;
        }
        var sections = new List<(string name, List<string> body, int tokens)>();
        var cur = new List<string>();
        string curName = string.Empty;
        for (int i = start; i < lines.Length; i++)
        {
            string ln = lines[i];
            bool isSectionHead = ln.TrimStart().StartsWith("### ", StringComparison.Ordinal);
            if (isSectionHead && cur.Count > 0)
            {
                string text = string.Join("\n", cur);
                sections.Add((curName, new List<string>(cur), EstimateTokens(text)));
                cur.Clear();
            }
            if (isSectionHead && cur.Count == 0)
                curName = ln.Trim();
            cur.Add(ln);
        }
        if (cur.Count > 0)
        {
            string text = string.Join("\n", cur);
            sections.Add((curName, new List<string>(cur), EstimateTokens(text)));
        }
        if (sections.Count == 0)
            return EstimateTokens(norm) <= tokensToFree ? string.Empty : block;
        int freed = 0;
        var drop = new HashSet<int>();
        foreach (
            int idx in sections
                .Select((s, i) => i)
                .OrderByDescending(i => sections[i].tokens)
                .ThenBy(i => sections[i].name, StringComparer.Ordinal)
        )
        {
            if (freed >= tokensToFree)
                break;
            drop.Add(idx);
            freed += sections[idx].tokens;
        }
        if (drop.Count >= sections.Count)
            return string.Empty;
        var kept = new List<string>();
        if (header != null)
            kept.Add(header);
        for (int i = 0; i < sections.Count; i++)
            if (!drop.Contains(i))
                kept.AddRange(sections[i].body);
        return string.Join("\n", kept);
    }

    /// <summary>need05-02：块状高低水位淘汰阈值：占用/maxContext ≥92% 才触发裁剪。</summary>
    public const double HighWaterMark = 0.92;

    /// <summary>need05-02：触发后一次裁到 70% 低水位，避免逐轮微调抖动前缀。</summary>
    public const double LowWaterMark = 0.70;

    /// <summary>
    /// need05-02：根据当前模型配置执行裁剪（块状高低水位）。
    /// 未达 92% 高水位：原样返回，前缀零抖动；达线：一次裁到 70% 低水位（复用原子组裁剪）。
    /// need09-02：常驻 stable（Skill 索引 + pinned Memory）计入 usedInput：有精确快照时快照已含全部，
    /// 无快照回退启发式时再叠加 residentStableTokens（缺省 0，老行为不变）。
    /// </summary>
    public List<UnifiedChatMessage> TruncateToBudget(
        ModelConfig model,
        IReadOnlyList<UnifiedChatMessage> messages,
        int safetyBuffer = DefaultSafetyBuffer,
        int residentStableTokens = 0
    )
    {
        int maxContext = model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000;
        int maxOutput = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
        var (prompt, _, _) = ResolveOccupancy(messages);
        int used = prompt ?? (ResolveUsedInputTokens(messages) + Math.Max(0, residentStableTokens));
        double ratio = maxContext > 0 ? (double)used / maxContext : 0;
        if (ratio < HighWaterMark)
            return messages.ToList(); // 未达线：原样返回，前缀零抖动
        int target = (int)(maxContext * LowWaterMark);
        int budget = Math.Max(1000, target - maxOutput - safetyBuffer);
        return BuildWithinBudget(messages, budget); // 达线才进现有原子组裁剪
    }

    /// <summary>
    /// 计算上下文配额状态 DTO
    /// need09-02：residentStableTokens（Skill 索引 + pinned Memory 常驻）计入 usedInput（仅无精确快照的启发式回退路径；缺省 0）。
    /// </summary>
    public ContextBudgetStatusDto CalculateBudgetStatus(
        ModelConfig model,
        IReadOnlyList<UnifiedChatMessage> messages,
        string? sessionHash = null,
        int residentStableTokens = 0
    )
    {
        int maxContext = model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000;
        int maxOutput = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
        int safetyBuffer = DefaultSafetyBuffer;
        var (prompt, cached, isEst) = ResolveOccupancy(messages);
        int msgCount = messages?.Count ?? 0;
        int resident = Math.Max(0, residentStableTokens);
        int usedInput =
            prompt
            ?? (msgCount == 0 ? resident : messages!.Sum(m => EstimateMessageTokens(m)) + resident);
        int availableInput = Math.Max(0, maxContext - maxOutput - safetyBuffer - usedInput);
        double percentage =
            maxContext > 0
                ? Math.Min(100.0, Math.Round((double)usedInput / maxContext * 100.0, 1))
                : 0.0;
        bool isNearLimit = percentage >= 85.0;
        var dto = new ContextBudgetStatusDto
        {
            TotalContextTokens = maxContext,
            MaxOutputTokens = maxOutput,
            SafetyBuffer = safetyBuffer,
            UsedInputTokens = usedInput,
            AvailableInputTokens = availableInput,
            UsagePercentage = percentage,
            IsNearLimit = isNearLimit,
            SessionHash = sessionHash,
            Protocol = model.Protocol == LlmProtocolType.Resp ? "resp" : "chat",
            LastPromptTokens = prompt,
            LastCachedTokens = cached,
            CacheHitRate =
                (prompt.HasValue && cached.HasValue && prompt.Value > 0)
                    ? (double)cached.Value / prompt.Value
                    : (double?)null,
            UsedInputIsEstimated = isEst && msgCount > 0,
            OccupancyState = prompt.HasValue ? "known" : (msgCount == 0 ? "known" : "unknown"),
        };
        if (
            !string.IsNullOrWhiteSpace(sessionHash)
            && PrefixCachePredictor.TryGetPrediction(
                sessionHash,
                out var pHit,
                out var pCached,
                out var divBlock
            )
        )
        {
            dto.PredictedCacheHitRate = pHit;
            dto.PredictedCachedTokens = pCached;
            dto.PrefixDivergenceBlock = divBlock;
        }
        return dto;
    }

    public async Task<ContextBudgetStatusDto> GetBudgetStatusAsync(
        string providerId,
        string modelId,
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    )
    {
        ModelConfig model =
            new()
            {
                Id = modelId,
                MaxContextTokens = 128000,
                MaxOutputTokens = 4096,
                Protocol = LlmProtocolType.Chat
            };
        if (_providerConfigService != null)
        {
            try
            {
                var doc = _providerConfigService.GetProvidersConfigInternal();
                var provider = doc.Providers.FirstOrDefault(p =>
                    string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase)
                );
                var m = provider?.Models.FirstOrDefault(x =>
                    string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase)
                );
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
        long billedPrompt = 0;
        long billedCompletion = 0;
        if (
            _workspaceSessionService != null
            && !string.IsNullOrWhiteSpace(workspaceId)
            && !string.IsNullOrWhiteSpace(sessionId)
        )
        {
            try
            {
                messages = await _workspaceSessionService
                    .GetSessionMessagesAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
                var sessions = await _workspaceSessionService
                    .GetSessionsByWorkspaceAsync(workspaceId, ct)
                    .ConfigureAwait(false);
                var currentSession = sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase)
                );
                sessionHash = currentSession?.SessionHash;
                // need05-01：Σ账单透传（旧 meta 缺键即 0）
                billedPrompt = currentSession?.CumulativePromptTokens ?? 0;
                billedCompletion = currentSession?.CumulativeCompletionTokens ?? 0;
            }
            catch
            {
                // 使用默认回退
            }
        }
        var dto = CalculateBudgetStatus(model, messages, sessionHash);
        dto.BilledPromptTokens = billedPrompt;
        dto.BilledCompletionTokens = billedCompletion;
        return dto;
    }
}
