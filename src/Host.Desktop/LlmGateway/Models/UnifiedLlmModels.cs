using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;

namespace Host.Desktop.LlmGateway.Models;

/// <summary>
/// 统一大模型请求上下文（完全解耦底层 Chat 与 Responses 协议）
/// </summary>
public sealed class UnifiedLlmRequest
{
    public string RequestId { get; init; } = string.Empty;
    public ProviderConfig Provider { get; init; } = new();
    public ModelConfig Model { get; init; } = new();
    public HeaderResolutionContext HeaderContext { get; init; } =
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty
        );
    public string SystemInstructions { get; init; } = string.Empty;
    public IReadOnlyList<UnifiedChatMessage> Messages { get; init; } =
        Array.Empty<UnifiedChatMessage>();
    public IReadOnlyList<UnifiedToolDefinition> Tools { get; init; } =
        Array.Empty<UnifiedToolDefinition>();

    /// <summary>need09-05：图片引用 → dataURL 解析器（默认 ImageDataRegistry；单测可注入假数据）。返回 null 表示该图无字节（发送时省略，文本保留）。</summary>
    [JsonIgnore]
    public Func<ChatImageRef, string?>? ImageResolver { get; init; }
}

/// <summary>need09-03/05：非视觉模型收到含图轮次时由 Adapter 抛出；Orchestrator 捕获后纯文本降级重发一次。</summary>
public sealed class NonVisionModelException : Exception
{
    public NonVisionModelException(string modelId, int imageCount)
        : base($"模型 '{modelId}' 不支持视觉输入，本轮含 {imageCount} 张图片，已拦截发送。")
    {
        ModelId = modelId ?? string.Empty;
        ImageCount = imageCount;
    }

    public string ModelId { get; }
    public int ImageCount { get; }
}

/// <summary>need09-05：图片字节内存注册表（生产由 SessionRepository 读盘回填；单测注入假 dataURL）。</summary>
public static class ImageDataRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        string
    > _map = new(StringComparer.Ordinal);

    public static void Register(string imageId, string dataUrl)
    {
        if (!string.IsNullOrWhiteSpace(imageId) && !string.IsNullOrWhiteSpace(dataUrl))
            _map[imageId.Trim()] = dataUrl.Trim();
    }

    public static string? Resolve(ChatImageRef? img)
    {
        if (img == null || string.IsNullOrWhiteSpace(img.ImageId))
            return null;
        return _map.TryGetValue(img.ImageId, out var url) ? url : null;
    }

    public static void Clear() => _map.Clear();
}

/// <summary>need09-05：adapter 共享的多模态轮次规划结果项。</summary>
public sealed record ResolvedImage(ChatImageRef Ref, string? DataUrl);

/// <summary>
/// S5 §4.2：图片字节盘规划（纯函数，无 IO，可单测）。
/// 两盘拆分的唯一实现点：
/// - 历史盘：历史消息（旧→新输入，逆序扣减，最新优先保），上限 <see cref="VisionDefaults.MaxHistoryImageBytes"/>；
/// - 当轮盘：当前 user 轮 + 本 turn view_image 工具图，上限 <see cref="VisionDefaults.MaxTurnImageBytes"/>。
/// 口径为 raw 字节（<see cref="ChatImageRef.SizeBytes"/>），不用 dataUrl(含 base64) 长度：
/// base64 约 4/3 膨胀，按 url.Length 计会导致实际可发容量只有账面 3/4。
/// 元数据撒谎方向不影响安全：报大只会挤掉自己的名额（永不超读），S2 有界读兜底。
/// 张数盘（MaxImagesPerTurn）不在此处，由 <see cref="MultimodalPlanner"/> 最后执行。
/// </summary>
public static class ImageBudgetPlanner
{
    /// <summary>
    /// 逆序贪心：从最新到最旧逐张扣减，放得下就准入，放不下就跳过（继续尝试更旧的？否——保最新丢最旧，
    /// 一旦超限即停，与参考 attachment-history 语义一致：被跳过的旧图不再续补，避免出现“旧图挤掉新图）。
    /// 输入 sequence 按**时间从旧到新**排序（调用方保证）。
    /// </summary>
    /// <param name="sequence">(imageId, sizeBytes)，旧→新。</param>
    /// <param name="capBytes">本盘上限（历史盘调用方须先 Math.Min 夹到常量以下）。</param>
    public static (HashSet<string> Admit, List<string> Dropped) PlanReverse(
        IReadOnlyList<(string ImageId, long SizeBytes)> sequence,
        long capBytes
    )
    {
        var admit = new HashSet<string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        if (sequence == null || sequence.Count == 0)
            return (admit, dropped);

        long used = 0;
        for (int i = sequence.Count - 1; i >= 0; i--)
        {
            string id = (sequence[i].ImageId ?? string.Empty).Trim();
            if (id.Length == 0)
                continue;
            long size = sequence[i].SizeBytes > 0 ? sequence[i].SizeBytes : 0;
            if (used + size <= capBytes)
            {
                used += size;
                admit.Add(id);
            }
            else
            {
                dropped.Add(id);
            }
        }
        dropped.Reverse(); // 输出按旧→新，日志/告知阅读顺序与历史一致
        return (admit, dropped);
    }

    /// <summary>
    /// 历史盘上限夹取：调用方只能下调不能上调（配置/单测传入值 &gt; 常量时按常量）。
    /// </summary>
    public static long ClampHistoryCap(long requested) =>
        requested <= 0
            ? VisionDefaults.MaxHistoryImageBytes
            : Math.Min(requested, VisionDefaults.MaxHistoryImageBytes);

    /// <summary>
    /// 从发包副本中摘除未准入的图片引用（落盘对象不动）：返回新消息列表，
    /// 只对 Images 非空的条目做 Eq 引用过滤；全部被摘则 Images=null（零抖动语义）。
    /// </summary>
    public static List<UnifiedChatMessage> StripDropped(
        IReadOnlyList<UnifiedChatMessage> messages,
        HashSet<string> admit
    )
    {
        var result = new List<UnifiedChatMessage>(messages?.Count ?? 0);
        if (messages == null)
            return result;
        foreach (var src in messages)
        {
            if (src?.Images == null || src.Images.Count == 0)
            {
                result.Add(src!);
                continue;
            }
            var kept = new List<ChatImageRef>(src.Images.Count);
            foreach (var img in src.Images)
            {
                if (
                    img != null
                    && !string.IsNullOrWhiteSpace(img.ImageId)
                    && admit.Contains(img.ImageId.Trim())
                )
                    kept.Add(img);
            }
            if (kept.Count == src.Images.Count)
            {
                result.Add(src);
                continue;
            }
            result.Add(
                new UnifiedChatMessage
                {
                    Id = src.Id,
                    CreatedAtUtc = src.CreatedAtUtc,
                    Role = src.Role,
                    Content = src.Content,
                    ReasoningContent = src.ReasoningContent,
                    ToolCalls = src.ToolCalls,
                    ToolCallId = src.ToolCallId,
                    Name = src.Name,
                    InputTokens = src.InputTokens,
                    OutputTokens = src.OutputTokens,
                    CachedTokens = src.CachedTokens,
                    Images = kept.Count == 0 ? null : kept,
                }
            );
        }
        return result;
    }
}

/// <summary>need09-05：adapter 共享的多模态轮次规划（门控加取最新 N 保 latest 丢最旧加dataURL 解析）。</summary>
public static class MultimodalPlanner
{
    /// <summary>
    /// S3 §4.2/§4.3：丢图告知文本的唯一构造点（发包内存告知，永不落盘）。
    /// 三种丢图各一句，均以 `[图片省略]`/`[图片已忽略]` 起头便于模型与日志检索。
    /// </summary>
    public static class DropNotice
    {
        /// <summary>熔断（本轮图片字节超预算，原图未发送）。skipped 为 (fileName, imageId)。</summary>
        public static string BudgetExceeded(
            long budgetBytes,
            IReadOnlyList<(string FileName, string ImageId)> skipped
        )
        {
            if (skipped == null || skipped.Count == 0)
                return string.Empty;
            // S5 §4.4：口径统一为十进制 raw（32_000_000 → 32MB），与历史盘告知同口径。
            int mb = (int)Math.Round(budgetBytes / 1_000_000d, MidpointRounding.AwayFromZero);
            var labels = new List<string>(skipped.Count);
            foreach (var (fileName, imageId) in skipped)
            {
                string name = string.IsNullOrWhiteSpace(fileName) ? "图片" : fileName.Trim();
                string id = ShortId(imageId);
                labels.Add(string.IsNullOrEmpty(id) ? name : $"{name}({id})");
            }
            return $"[图片省略] 本轮图片字节超预算（{mb}MB），以下 {skipped.Count} 张图未发送："
                + string.Join("、", labels)
                + "；可改用缩略图/小图重试。";
        }

        /// <summary>
        /// S5 §4.4：历史盘丢图告知（与当轮盘可区分）。口径 raw，十进制 MB。
        /// 建议方向不同：历史盘丢图 → 下轮精简历史；当轮盘丢图 → 用小图/缩略图重试。
        /// </summary>
        public static string HistoryBudgetExceeded(
            long budgetBytes,
            IReadOnlyList<(string FileName, string ImageId)> dropped
        )
        {
            if (dropped == null || dropped.Count == 0)
                return string.Empty;
            int mb = (int)Math.Round(budgetBytes / 1_000_000d, MidpointRounding.AwayFromZero);
            var labels = new List<string>(dropped.Count);
            foreach (var (fileName, imageId) in dropped)
            {
                string name = string.IsNullOrWhiteSpace(fileName) ? "图片" : fileName.Trim();
                string id = ShortId(imageId);
                labels.Add(string.IsNullOrEmpty(id) ? name : $"{name}({id})");
            }
            return $"[图片省略] 历史图片超预算未发送 {dropped.Count} 张（历史盘 {mb}MB，raw 计），以下最早的历史图未发送："
                + string.Join("、", labels)
                + "；如需继续分析这些图，请让用户在本轮重新附图，或先精简更早的历史。";
        }

        /// <summary>张数截断（超 MaxImagesPerTurn，保最新丢最旧）。</summary>
        public static string CountTruncated(int sent, int dropped, int max)
        {
            if (dropped <= 0)
                return string.Empty;
            return $"（实发 {sent} 张，另有 {dropped} 张因超 MaxImagesPerTurn({max})未发送）";
        }

        /// <summary>非视觉剥离（当前模型不支持看图，图片未发送但文字照常）。</summary>
        public static string NonVisionStripped(string modelId, int stripped) =>
            $"[图片已忽略] 当前模型 {modelId} 不支持看图，本轮 {stripped} 张图片未发送，文字照常发送。";

        /// <summary>
        /// S3 §4.4：非视觉剥离的发包副本——图片全剥离（原列表不动），并在首条消息正文前置告知句。
        /// 返回剥离后的消息副本；stripped=0 时原样返回入参（零抖动）。
        /// </summary>
        public static (IReadOnlyList<UnifiedChatMessage> Messages, int Stripped) StripForNonVision(
            IReadOnlyList<UnifiedChatMessage> messages,
            string modelId
        )
        {
            if (messages == null || messages.Count == 0)
                return (messages ?? Array.Empty<UnifiedChatMessage>(), 0);
            int stripped = 0;
            foreach (var m in messages)
            {
                if (m?.Images != null)
                    stripped += m.Images.Count;
            }
            if (stripped == 0)
                return (messages, 0);

            string notice = NonVisionStripped(modelId, stripped);
            var result = new List<UnifiedChatMessage>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                var src = messages[i];
                var copy = new UnifiedChatMessage
                {
                    Id = src.Id,
                    Role = src.Role,
                    Content = src.Content,
                    ReasoningContent = src.ReasoningContent,
                    ToolCalls = src.ToolCalls,
                    ToolCallId = src.ToolCallId,
                    Name = src.Name,
                    // 剥离图片：副本不带 Images（落盘历史不受影响）
                    Images = null,
                    CreatedAtUtc = src.CreatedAtUtc,
                };
                result.Add(i == 0 ? WithNotice(copy, notice, append: false) : copy);
            }
            return (result, stripped);
        }

        /// <summary>
        /// S3 §4.2：熔断告知的发包副本——把「字节超预算未发送」句追加到末条消息正文（不落盘）。
        /// skipped 为空时原样返回入参（零抖动）。
        /// </summary>
        public static IReadOnlyList<UnifiedChatMessage> WithBudgetNotice(
            IReadOnlyList<UnifiedChatMessage> messages,
            long budgetBytes,
            IReadOnlyList<(string FileName, string ImageId)> skipped
        )
        {
            if (messages == null || messages.Count == 0)
                return messages ?? Array.Empty<UnifiedChatMessage>();
            string notice = BudgetExceeded(budgetBytes, skipped);
            if (notice.Length == 0)
                return messages;

            var result = new List<UnifiedChatMessage>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                var src = messages[i];
                result.Add(i == messages.Count - 1 ? WithNotice(src, notice, append: true) : src);
            }
            return result;
        }

        /// <summary>imageId 前 6 位（判定/检索用，不含敏感路径）。</summary>
        internal static string ShortId(string? imageId)
        {
            string id = (imageId ?? string.Empty).Trim();
            if (id.Length == 0)
                return string.Empty;
            return id.Length <= 6 ? id : id.Substring(0, 6);
        }

        /// <summary>
        /// S3：把告知句并入一条内存消息副本（不落盘）。图像引用共享，只替换 Content。
        /// append=true 追加到文末（user 直挂图截断用），false 前置（剥离通知用）。
        /// </summary>
        public static UnifiedChatMessage WithNotice(
            UnifiedChatMessage src,
            string notice,
            bool append
        )
        {
            var copy = new UnifiedChatMessage
            {
                Id = src.Id,
                Role = src.Role,
                Content = append
                    ? ((src.Content ?? string.Empty) + notice)
                    : (notice + (src.Content ?? string.Empty)),
                ReasoningContent = src.ReasoningContent,
                ToolCalls = src.ToolCalls,
                ToolCallId = src.ToolCallId,
                Name = src.Name,
                Images = src.Images,
                CreatedAtUtc = src.CreatedAtUtc,
            };
            return copy;
        }
    }

    /// <summary>规划本轮图片：非视觉模型含图抛 NonVisionModelException；超 Max 取最新 N 张。返回与 Messages 等长对齐的 kept 列表。</summary>
    /// <param name="request">请求。</param>
    /// <param name="lazyUserResolve">
    /// S6：user 直挂图**不在规划期解析**（交给 adapter 按占位符顺序逐块解析）。原因有二：
    /// ① S5 当轮盘（32M raw）在 resolver 内部扣减，规划期提前解析会把预算提前吃光；
    /// ② 被预算/读盘摘除的图必须能被 adapter 看见（解析结果为 null），才能在其占位符处渲染
    ///    `[image omitted]`，而不是整段文本静默消失。
    /// tool 附图不受影响（仍在此处解析，trailing user 块直接取 DataUrl）。
    /// </param>
    public static (List<List<ResolvedImage>> Kept, int Dropped, int Total) PlanTurn(
        UnifiedLlmRequest request,
        bool lazyUserResolve = false
    )
    {
        var kept = new List<List<ResolvedImage>>();
        int total = 0;
        foreach (var msg in request.Messages)
        {
            var list = new List<ResolvedImage>();
            if (msg != null && msg.Images != null)
            {
                bool carries =
                    msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    || msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase);
                if (carries)
                {
                    bool lazy =
                        lazyUserResolve
                        && msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
                    foreach (var img in msg.Images)
                    {
                        if (img == null || string.IsNullOrWhiteSpace(img.ImageId))
                            continue;
                        total++;
                        if (lazy)
                        {
                            // 只保留引用，dataUrl 由 adapter 逐块解析（顺序与占位符一致）
                            list.Add(new ResolvedImage(img, null));
                            continue;
                        }
                        string? url =
                            request.ImageResolver != null
                                ? request.ImageResolver(img)
                                : ImageDataRegistry.Resolve(img);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            url = url.Trim();
                            if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                string mime = string.IsNullOrWhiteSpace(img.Mime)
                                    ? "image/jpeg"
                                    : img.Mime.Trim();
                                url = "data:" + mime + ";base64," + url;
                            }
                        }
                        else
                        {
                            url = null;
                        }
                        list.Add(new ResolvedImage(img, url));
                    }
                }
            }
            kept.Add(list);
        }
        if (total > 0 && !request.Model.SupportsVision)
            throw new NonVisionModelException(request.Model.Id, total);
        int max = VisionDefaults.ResolveMaxImages(request.Model.MaxImagesPerTurn);
        int dropped = 0;
        if (total > max)
        {
            dropped = total - max;
            int skip = dropped;
            for (int i = 0; i < kept.Count && skip > 0; i++)
            {
                while (kept[i].Count > 0 && skip > 0)
                {
                    kept[i].RemoveAt(0);
                    skip--;
                }
            }
        }
        return (kept, dropped, total);
    }

    /// <summary>
    /// S3 §4.3：把「超 MaxImagesPerTurn 未发送」告知并入发包消息副本（不落盘）。
    /// 张数算法与 PlanTurn 完全一致（保最新丢最旧），这里只补文字告知。
    /// 返回新列表：无告知时原样返回入参引用，避免无谓拷贝与前缀抖动。
    /// </summary>
    public static IReadOnlyList<UnifiedChatMessage> ApplyDropNotice(
        IReadOnlyList<UnifiedChatMessage> messages,
        int dropped,
        int total,
        int max,
        IReadOnlyList<List<ResolvedImage>>? kept = null
    )
    {
        if (messages == null || dropped <= 0 || total <= 0)
            return messages ?? Array.Empty<UnifiedChatMessage>();
        string notice = DropNotice.CountTruncated(total - dropped, dropped, max);
        if (notice.Length == 0)
            return messages;

        // 告知挂在「最早的一条含图 user 消息」上（user 直挂图截断同理：内容文本尾追加）。
        // tool 附图不走这里：它的告知由 BuildTrailingToolImageText 统一承载。
        // S6：消息**自身还有图可发**（Images 与 Kept 终态有交集）时，按占位符位置追加到文尾占位符之后，
        // 避免截断出「有 text 块但无图块」的 content 数组；自身一图不剩时改用纯文本 + 文尾追加，保持 S6 前的线上形态。
        var result = new List<UnifiedChatMessage>(messages.Count);
        bool placed = false;
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            result.Add(msg);
            if (
                !placed
                && msg != null
                && msg.Images != null
                && msg.Images.Count > 0
                && string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase)
            )
            {
                // 「自身还有图可发」= 本消息的 Images 里至少有一张在 Kept[i] 中且已解析出 dataUrl。
                bool stillCarries =
                    kept != null
                    && i < kept.Count
                    && kept[i].Any(r => r.Ref != null && !string.IsNullOrWhiteSpace(r.DataUrl))
                    && kept[i]
                        .Any(r =>
                            r.Ref != null
                            && msg.Images.Any(img =>
                                img != null
                                && (
                                    ReferenceEquals(img, r.Ref)
                                    || string.Equals(
                                        (img.ImageId ?? string.Empty).Trim(),
                                        (r.Ref.ImageId ?? string.Empty).Trim(),
                                        StringComparison.Ordinal
                                    )
                                )
                            )
                        );
                var target = msg;
                if (!stillCarries && !string.IsNullOrEmpty(msg.Content))
                {
                    // 纯文本形态：交给 adapter 走 content:string + 文尾图块的既有分支
                    target = new UnifiedChatMessage
                    {
                        Id = msg.Id,
                        CreatedAtUtc = msg.CreatedAtUtc,
                        Role = msg.Role,
                        Content = msg.Content,
                        ReasoningContent = msg.ReasoningContent,
                        ToolCalls = msg.ToolCalls,
                        ToolCallId = msg.ToolCallId,
                        Name = msg.Name,
                        InputTokens = msg.InputTokens,
                        OutputTokens = msg.OutputTokens,
                        CachedTokens = msg.CachedTokens,
                        Images = null,
                    };
                }
                result[result.Count - 1] = DropNotice.WithNotice(target, notice, append: true);
                placed = true;
            }
        }
        if (!placed)
        {
            // 没有可挂的 user 图消息（例如全在 tool 上）⇒ 不追加，避免凭空多一条 user。
            return messages;
        }
        return result;
    }
}

/// <summary>
/// 发包前配对守卫（纯函数，无 Services 依赖，双协议适配器共用）：
/// assistant(tool_calls) 后必须紧跟覆盖全部 call_id 的 tool 块；缺回包在块尾补 synthetic，
/// 无主 tool 直接丢弃，保证任何历史脏数据都发不出坏包。
/// 合成文本与 WorkspaceSessionService.SyntheticCancelledToolText 同格式（前端可识别）。
/// </summary>
public static class ChatPairingGuard
{
    public const string SyntheticPrefix = "[Tool Cancelled";

    public static string SyntheticText(string? functionName)
    {
        string name = string.IsNullOrWhiteSpace(functionName) ? "tool" : functionName!.Trim();
        return $"[Tool Cancelled - {name}]: 该工具调用在实际执行前被中断（用户手动停止或进程异常退出），未产生任何副作用。请基于已有上下文继续任务；如仍需要该操作，请重新发起工具调用。";
    }

    /// <summary>返回修好配对的内存副本（不改输入）；已配对时返回原引用拷贝。</summary>
    public static List<Host.Desktop.Core.Models.UnifiedChatMessage> HealForSend(
        System.Collections.Generic.IReadOnlyList<Host.Desktop.Core.Models.UnifiedChatMessage>? messages
    )
    {
        var src = messages ?? System.Array.Empty<Host.Desktop.Core.Models.UnifiedChatMessage>();
        int n = src.Count;
        if (n == 0)
            return new List<Host.Desktop.Core.Models.UnifiedChatMessage>();
        var uncoveredByAssistant = new System.Collections.Generic.Dictionary<
            int,
            System.Collections.Generic.List<(string CallId, string? Name)>
        >();
        var orphanToolIndexes = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            var m = src[i];
            if (m == null)
                continue;
            string role = (m.Role ?? string.Empty).ToLowerInvariant();
            if (string.Equals(role, "assistant", System.StringComparison.Ordinal))
            {
                var calls = m
                    .ToolCalls?.Where(c => c != null && !string.IsNullOrWhiteSpace(c.CallId))
                    .ToList();
                if (calls == null || calls.Count == 0)
                    continue;
                var covered = new System.Collections.Generic.HashSet<string>(
                    System.StringComparer.Ordinal
                );
                for (int j = i + 1; j < n; j++)
                {
                    var t = src[j];
                    if (
                        t == null
                        || !string.Equals(
                            (t.Role ?? string.Empty).ToLowerInvariant(),
                            "tool",
                            System.StringComparison.Ordinal
                        )
                    )
                        break;
                    if (!string.IsNullOrWhiteSpace(t.ToolCallId))
                        covered.Add(t.ToolCallId!);
                }
                var uncovered = calls
                    .Where(c => !covered.Contains(c.CallId))
                    .Select(c => (c.CallId, (string?)c.FunctionName))
                    .ToList();
                if (uncovered.Count > 0)
                    uncoveredByAssistant[i] = uncovered;
            }
            else if (string.Equals(role, "tool", System.StringComparison.Ordinal))
            {
                bool owned = false;
                if (!string.IsNullOrWhiteSpace(m.ToolCallId))
                {
                    int k = i - 1;
                    while (
                        k >= 0
                        && src[k] != null
                        && string.Equals(
                            (src[k].Role ?? string.Empty).ToLowerInvariant(),
                            "tool",
                            System.StringComparison.Ordinal
                        )
                    )
                        k--;
                    if (
                        k >= 0
                        && src[k] != null
                        && string.Equals(
                            (src[k].Role ?? string.Empty).ToLowerInvariant(),
                            "assistant",
                            System.StringComparison.Ordinal
                        )
                    )
                    {
                        owned =
                            src[k]
                                .ToolCalls?.Any(c =>
                                    c != null
                                    && string.Equals(
                                        c.CallId,
                                        m.ToolCallId,
                                        System.StringComparison.Ordinal
                                    )
                                ) == true;
                    }
                }
                if (!owned)
                    orphanToolIndexes.Add(i);
            }
        }
        if (uncoveredByAssistant.Count == 0 && orphanToolIndexes.Count == 0)
        {
            return new List<Host.Desktop.Core.Models.UnifiedChatMessage>(src);
        }
        // 合成一律追加在存量 tool 块尾（[assistant, toolA, syntheticB]），不插在块中。
        var fixed_ = new List<Host.Desktop.Core.Models.UnifiedChatMessage>(
            n + uncoveredByAssistant.Values.Sum(v => v.Count)
        );
        int idx = 0;
        while (idx < n)
        {
            if (orphanToolIndexes.Contains(idx))
            {
                idx++;
                continue;
            }
            var m = src[idx];
            fixed_.Add(m!);
            if (uncoveredByAssistant.TryGetValue(idx, out var uncovered))
            {
                int j = idx + 1;
                while (
                    j < n
                    && src[j] != null
                    && string.Equals(
                        (src[j].Role ?? string.Empty).ToLowerInvariant(),
                        "tool",
                        System.StringComparison.Ordinal
                    )
                )
                {
                    if (orphanToolIndexes.Contains(j))
                    {
                        j++;
                        continue;
                    }
                    fixed_.Add(src[j]!);
                    j++;
                }
                foreach (var (callId, name) in uncovered)
                {
                    fixed_.Add(
                        Host.Desktop.Core.Models.UnifiedChatMessage.Tool(
                            callId,
                            SyntheticText(name),
                            name
                        )
                    );
                }
                idx = j;
            }
            else
            {
                idx++;
            }
        }
        return fixed_;
    }
}

/// <summary>
/// 统一工具描述符
/// </summary>
public sealed class UnifiedToolDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 标准 JSON Schema 对象（type: "object", properties: {...}, required: [...]）
    /// </summary>
    public JsonElement ParametersSchema { get; init; }

    public static UnifiedToolDefinition Create(string name, string description, string jsonSchema)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(jsonSchema) ? "{}" : jsonSchema
        );
        return new UnifiedToolDefinition
        {
            Name = name,
            Description = description,
            ParametersSchema = doc.RootElement.Clone()
        };
    }
}

/// <summary>
/// 统一流式事件类型枚举
/// </summary>
public enum UnifiedStreamEventKind
{
    StreamStarted, // 流已建立
    ReasoningDelta, // 思考过程增量（兼容 DeepSeek reasoning_content / OpenAI reasoning summary）
    TextDelta, // 正文文本增量
    ToolCallStarted, // 模型决定发起某个工具调用（已拿到 callId 与 functionName）
    ToolCallArgsDelta, // 工具参数 JSON 增量片段
    ToolCallCompleted, // 单个工具调用参数接收完毕
    UsageUpdated, // Token 消耗统计（含 Prompt Cache 命中统计）
    StreamCompleted, // 整轮响应完成
    StreamTruncated, // NEED17-06：finish_reason=length（回复因输出长度被截断，tool args 可能不完整）
    StreamCancelled, // 用户主动取消中途生成
    StreamError // 协议层或网络层错误
}

/// <summary>
/// 统一流式事件载荷
/// </summary>
public sealed record UnifiedStreamEvent(
    UnifiedStreamEventKind Kind,
    string? TextDelta = null,
    UnifiedToolCall? ToolCall = null,
    UnifiedTokenUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null
)
{
    public static UnifiedStreamEvent Started() => new(UnifiedStreamEventKind.StreamStarted);

    public static UnifiedStreamEvent Text(string delta) =>
        new(UnifiedStreamEventKind.TextDelta, TextDelta: delta);

    public static UnifiedStreamEvent Reasoning(string delta) =>
        new(UnifiedStreamEventKind.ReasoningDelta, TextDelta: delta);

    public static UnifiedStreamEvent ToolStarted(
        string callId,
        string functionName,
        string? itemId = null
    ) =>
        new(
            UnifiedStreamEventKind.ToolCallStarted,
            ToolCall: new UnifiedToolCall(callId, itemId, functionName, string.Empty)
        );

    public static UnifiedStreamEvent ToolArgs(
        string callId,
        string argsDelta,
        string? itemId = null
    ) =>
        new(
            UnifiedStreamEventKind.ToolCallArgsDelta,
            TextDelta: argsDelta,
            ToolCall: new UnifiedToolCall(callId, itemId, string.Empty, argsDelta)
        );

    public static UnifiedStreamEvent ToolDone(
        string callId,
        string functionName,
        string argumentsJson,
        string? itemId = null
    ) =>
        new(
            UnifiedStreamEventKind.ToolCallCompleted,
            ToolCall: new UnifiedToolCall(callId, itemId, functionName, argumentsJson)
        );

    public static UnifiedStreamEvent Tokens(int input, int output, int cached, int total) =>
        new(
            UnifiedStreamEventKind.UsageUpdated,
            Usage: new UnifiedTokenUsage(input, output, cached, total)
        );

    public static UnifiedStreamEvent Completed() => new(UnifiedStreamEventKind.StreamCompleted);

    public static UnifiedStreamEvent Truncated(int toolCalls) =>
        new(
            UnifiedStreamEventKind.StreamTruncated,
            ErrorCode: "STREAM_TRUNCATED",
            ErrorMessage: toolCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );

    public static UnifiedStreamEvent Cancelled() => new(UnifiedStreamEventKind.StreamCancelled);

    public static UnifiedStreamEvent Error(string code, string message) =>
        new(UnifiedStreamEventKind.StreamError, ErrorCode: code, ErrorMessage: message);
}

/// <summary>
/// 统一工具调用数据
/// </summary>
public sealed record UnifiedToolCall(
    string CallId, // 对应 Chat 的 tool_call.id 或 Responses 的 call_id
    string? ItemId, // 对应 Responses API 的 fc_xxx item id（可选）
    string FunctionName,
    string ArgumentsJson
);

/// <summary>
/// 统一 Token 用量与 Prompt Cache 命中指标
/// </summary>
public sealed record UnifiedTokenUsage(
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens, // 重点：记录 Prompt Cache 命中的 Token 数，验证 ${session-hash} 收益
    int TotalTokens
)
{
    public double CacheHitRate =>
        InputTokens > 0 ? Math.Clamp((double)CachedInputTokens / InputTokens, 0.0, 1.0) : 0.0;
}

/// <summary>
/// 投递至前端的会话流式事件 DTO，支持 16ms 节流消费周期的增量文本合并优化 (Delta Coalescing)
/// </summary>
public sealed class SessionStreamEventDto
{
    public string SessionId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? TextDelta { get; set; }
    public UnifiedToolCall? ToolCall { get; set; }
    public UnifiedTokenUsage? Usage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static SessionStreamEventDto FromUnifiedEvent(
        string sessionId,
        UnifiedStreamEvent evt
    ) =>
        new()
        {
            SessionId = sessionId,
            Kind = evt.Kind.ToString(),
            TextDelta = evt.TextDelta,
            ToolCall = evt.ToolCall,
            Usage = evt.Usage,
            ErrorCode = evt.ErrorCode,
            ErrorMessage = evt.ErrorMessage
        };
}
