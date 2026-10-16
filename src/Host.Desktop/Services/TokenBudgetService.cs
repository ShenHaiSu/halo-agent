using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
 using System.Text.RegularExpressions;
using Host.Desktop.Core.Models;
 using Host.Desktop.LlmGateway.Models;
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
     List<UnifiedChatMessage> TruncateToBudget(ModelConfig model, IReadOnlyList<UnifiedChatMessage> messages, int safetyBuffer = 1024, int residentStableTokens = 0);
     ContextBudgetStatusDto CalculateBudgetStatus(ModelConfig model, IReadOnlyList<UnifiedChatMessage> messages, string? sessionHash = null, int residentStableTokens = 0);
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
     /// need09-02：图片开销 = 每张存活引用 image:&lt;sha16&gt;:&lt;w&gt;x&lt;h&gt;:&lt;mime&gt; 计 <see cref="ImageTokensPerImage"/>（默认约1000，可配）；
     /// 已被裁剪为 <see cref="ImageOmittedPlaceholder"/> 的占位只按文本计费，不再加图片开销。
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
         var (liveImages, _) = CountMessageImages(message);
         tokens += liveImages * Math.Max(0, ImageTokensPerImage);
         return tokens;
     }

     /// <summary>need09-02：图片引用被裁剪后的占位（保留文字，只去图片开销）。</summary>
     public const string ImageOmittedPlaceholder = "[image omitted]";
     /// <summary>need09-02：单张存活图片引用的固定开销（tokens，默认约1000，可配）。</summary>
     public static int ImageTokensPerImage { get; set; } = 1000;
     private static readonly Regex ImageRefRegex = new(@"image:[0-9A-Fa-f]{16}:\d+x\d+:[A-Za-z0-9/+.\-]+", RegexOptions.Compiled);

     /// <summary>need09-02：统计文本中的存活图片引用数（归一化形如 image:&lt;sha16&gt;:&lt;w&gt;x&lt;h&gt;:&lt;mime&gt;）。</summary>
     public static int CountLiveImageRefs(string? text)
     {
         if (string.IsNullOrEmpty(text)) return 0;
         try { return ImageRefRegex.Matches(text).Count; }
         catch { return 0; }
     }

     /// <summary>need09-02：统计文本中的图片占位数。</summary>
     public static int CountOmittedImagePlaceholders(string? text)
     {
         if (string.IsNullOrEmpty(text)) return 0;
         int n = 0, idx = 0;
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
         if (message == null) return (0, 0);
         string combined = (message.Content ?? string.Empty) + "\n" + (message.ReasoningContent ?? string.Empty);
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
    /// need05-01：与 ResolveUsedInputTokens 并列，返回是否估算 + 可空快照。
    /// 口径：IN=占用快照（末轮 prompt），cache⊆IN，无精确值 = unknown。
    /// </summary>
    public static (int? prompt, int? cached, bool isEstimated) ResolveOccupancy(IReadOnlyList<UnifiedChatMessage>? messages)
    {
        int? p = null; int? c = null;
        if (messages != null) for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i]; if (m == null) continue;
            if (p == null && m.InputTokens.HasValue && m.InputTokens.Value > 0) p = (int)m.InputTokens.Value;
            if (c == null && m.CachedTokens.HasValue && m.CachedTokens.Value > 0) c = (int)m.CachedTokens.Value;
            if (p != null) break; // 以末个 prompt 快照为准；cached 取该轮或更近轮的最大可见值后钳制
        }
        if (p == null) return (null, null, true);
        if (c.HasValue) c = Math.Min(c.Value, Math.Max(0, p.Value));
        return (p, c, false);
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

     /// <summary>need09-02：裁剪结果（消息 + 可能被极端截断的 memoryPinned 块 + 图片是否被占位）。</summary>
     public sealed record BudgetCutResult(List<UnifiedChatMessage> Messages, string? MemoryPinnedBlock, bool ImagesTruncated);

     /// <summary>
     /// 保持工具调用成对完整性并保留首尾锚点的滑动窗口裁剪算法（存量行为：无图片/无 memory 时与老版本逐条同一引用）。
     /// </summary>
     public List<UnifiedChatMessage> BuildWithinBudget(IReadOnlyList<UnifiedChatMessage> messages, int maxBudget)
         => BuildWithinBudgetEx(messages, maxBudget).Messages;

     /// <summary>
     /// need09-02：三阶裁剪（冻结顺序）：①丢最旧图片引用（替换为 [image omitted] 占位，保留文字）→
     /// ②丢中间 tool 组（存量原子组逻辑）→ ③极端时截 memoryPinned 超长条（省出的配额让给历史）。
     /// system.stable（含 Skill 索引）永不参与裁剪——本方法只动 history 消息与可选传入的 memory 块。
     /// </summary>
     public BudgetCutResult BuildWithinBudgetEx(IReadOnlyList<UnifiedChatMessage> messages, int maxBudget, string? memoryPinnedBlock = null)
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
         if (!string.IsNullOrWhiteSpace(mem) && trimmed.Sum(m => EstimateMessageTokens(m)) > maxBudget)
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

     /// <summary>need09-02：存量原子组裁剪内核（行为与老 BuildWithinBudget 完全一致）。</summary>
     private List<UnifiedChatMessage> BuildWithinBudgetAtomic(IReadOnlyList<UnifiedChatMessage> messages, int maxBudget)
     {
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

     /// <summary>need09-02：浅克隆消息（Id/时间戳保留；ToolCalls 列表复制），供图片占位裁剪使用，不碰调用方原始对象。</summary>
     private static List<UnifiedChatMessage> CloneMessages(IReadOnlyList<UnifiedChatMessage> messages)
     {
         var list = new List<UnifiedChatMessage>(messages.Count);
         foreach (var m in messages)
         {
             list.Add(new UnifiedChatMessage
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
             });
         }
         return list;
     }

     /// <summary>need09-02：从最旧消息开始把存活图片引用替换为占位，直到达标或无引用可丢；返回是否发生过替换。</summary>
     private static bool DropOldestImageRefs(List<UnifiedChatMessage> working, int maxBudget)
     {
         bool dropped = false;
         for (int i = 0; i < working.Count; i++)
         {
             if (working.Sum(m => EstimateMessageTokens(m)) <= maxBudget) break;
             var msg = working[i];
             if (!string.IsNullOrEmpty(msg.Content) && CountLiveImageRefs(msg.Content) > 0)
             {
                 msg.Content = ImageRefRegex.Replace(msg.Content, ImageOmittedPlaceholder);
                 dropped = true;
             }
             if (!string.IsNullOrEmpty(msg.ReasoningContent) && CountLiveImageRefs(msg.ReasoningContent) > 0)
             {
                 msg.ReasoningContent = ImageRefRegex.Replace(msg.ReasoningContent, ImageOmittedPlaceholder);
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
         if (string.IsNullOrWhiteSpace(block) || tokensToFree <= 0) return block;
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
             if (isSectionHead && cur.Count == 0) curName = ln.Trim();
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
         foreach (int idx in sections
             .Select((s, i) => i)
             .OrderByDescending(i => sections[i].tokens)
             .ThenBy(i => sections[i].name, StringComparer.Ordinal))
         {
             if (freed >= tokensToFree) break;
             drop.Add(idx);
             freed += sections[idx].tokens;
         }
         if (drop.Count >= sections.Count) return string.Empty;
         var kept = new List<string>();
         if (header != null) kept.Add(header);
         for (int i = 0; i < sections.Count; i++)
             if (!drop.Contains(i)) kept.AddRange(sections[i].body);
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
         int residentStableTokens = 0)
     {
         int maxContext = model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000;
         int maxOutput = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
         var (prompt, _, _) = ResolveOccupancy(messages);
         int used = prompt ?? (ResolveUsedInputTokens(messages) + Math.Max(0, residentStableTokens));
         double ratio = maxContext > 0 ? (double)used / maxContext : 0;
         if (ratio < HighWaterMark) return messages.ToList(); // 未达线：原样返回，前缀零抖动
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
          int residentStableTokens = 0)
      {
          int maxContext = model.MaxContextTokens > 0 ? model.MaxContextTokens : 128000;
          int maxOutput = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
          int safetyBuffer = DefaultSafetyBuffer;
          var (prompt, cached, isEst) = ResolveOccupancy(messages);
          int msgCount = messages?.Count ?? 0;
          int resident = Math.Max(0, residentStableTokens);
          int usedInput = prompt ?? (msgCount == 0 ? resident : messages!.Sum(m => EstimateMessageTokens(m)) + resident);
         int availableInput = Math.Max(0, maxContext - maxOutput - safetyBuffer - usedInput);
         double percentage = maxContext > 0
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
             CacheHitRate = (prompt.HasValue && cached.HasValue && prompt.Value > 0) ? (double)cached.Value / prompt.Value : (double?)null,
             UsedInputIsEstimated = isEst && msgCount > 0,
             OccupancyState = prompt.HasValue ? "known" : (msgCount == 0 ? "known" : "unknown"),
         };
         if (!string.IsNullOrWhiteSpace(sessionHash)
             && PrefixCachePredictor.TryGetPrediction(sessionHash, out var pHit, out var pCached, out var divBlock))
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
        long billedPrompt = 0; long billedCompletion = 0;
        if (_workspaceSessionService != null && !string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                messages = await _workspaceSessionService.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
                var sessions = await _workspaceSessionService.GetSessionsByWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
                var currentSession = sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
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
