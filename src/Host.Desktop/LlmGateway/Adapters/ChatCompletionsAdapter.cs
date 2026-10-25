using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.LlmGateway.Sse;

namespace Host.Desktop.LlmGateway.Adapters;

/// <summary>
/// OpenAI Chat Completions 协议适配器 (/v1/chat/completions)
/// 覆盖需求 #2 与 #7：处理经典 messages 数组、嵌套 tools 结构与 choices[0].delta 流式事件聚合。
/// </summary>
public sealed partial class ChatCompletionsAdapter : ILlmProtocolAdapter
{
    public LlmProtocolType Protocol => LlmProtocolType.Chat;

    public string ResolveEndpointUrl(string baseUrl) =>
        HeaderVariableResolver.ResolveEndpointUrl(baseUrl, LlmProtocolType.Chat);

    public string SerializeRequest(UnifiedLlmRequest request, bool includeUsageInStream = true)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            // 1. 模型标识
            writer.WriteString("model", request.Model.Id);

            // 2. 流式开关与用量包含选项
            writer.WriteBoolean("stream", true);
            if (includeUsageInStream)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            // 3. 最大输出 Token
            if (request.Model.MaxOutputTokens > 0)
            {
                writer.WriteNumber("max_tokens", request.Model.MaxOutputTokens);
            }

            // 4. 采样温度
            if (request.Model.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", request.Model.Temperature.Value);
            }

            // 5. 对话消息列表
            writer.WriteStartArray("messages");

            // 5.1 系统提示词 (System Prompt)
            if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", request.SystemInstructions);
                writer.WriteEndObject();
            }

            // 5.2 历史与当前消息（need09-05：含图 user 走 content 数组；无图保持 content:string 零抖动）
            // FIX(tool-400)：先内存愈合配对（缺回包补 synthetic、无主 tool 丢弃），再序列化，保证线包永不非法；
            // tool 附图不再就地插 user（会劈开 tool 块导致 400），改为收齐后在整段 tool 块尾发一个 user。
            var sendMessages = ChatPairingGuard.HealForSend(request.Messages);
            if (sendMessages.Count != (request.Messages?.Count ?? 0))
            {
                try
                {
                    Host.Desktop.Infrastructure.Logging.HaloLogger.Llm.Warn(
                        $"[REQ] reqId={request.RequestId} pairing-healed messages={request.Messages?.Count ?? 0}->{sendMessages.Count}"
                    );
                }
                catch { }
            }
            var planRequest = new UnifiedLlmRequest
            {
                RequestId = request.RequestId,
                Provider = request.Provider,
                Model = request.Model,
                HeaderContext = request.HeaderContext,
                SystemInstructions = request.SystemInstructions,
                Messages = sendMessages,
                Tools = request.Tools,
                ImageResolver = request.ImageResolver,
            };
            // S6 §4.2：user 直挂图延迟解析（lazyUserResolve）——规划期不读盘，adapter 按占位符顺序逐块解析，
            // 从而让 S5 当轮盘摘除的图能在原位渲染 [image omitted]（而不是整段静默消失）。
            var mmPlan = MultimodalPlanner.PlanTurn(planRequest, lazyUserResolve: true);
            string visionDetail = VisionDefaults.NormalizeDetail(request.Model.VisionDetail);
            // S3 §4.3：截断告知显式化——超 MaxImagesPerTurn 时把「另有 M 张未发送」并入发包文本。
            // 只改发包内存副本（sendMessages），不落盘；张数算法仍由 PlanTurn 保最新丢最旧。
            int maxImagesPerTurn = VisionDefaults.ResolveMaxImages(request.Model.MaxImagesPerTurn);
            var outMessages =
                mmPlan.Dropped > 0
                    ? MultimodalPlanner.ApplyDropNotice(
                        sendMessages,
                        mmPlan.Dropped,
                        mmPlan.Total,
                        maxImagesPerTurn,
                        mmPlan.Kept
                    )
                    : sendMessages;
            if (mmPlan.Dropped > 0)
            {
                try
                {
                    Host.Desktop.Infrastructure.Logging.HaloLogger.Llm.Info(
                        "[REQ] reqId="
                            + request.RequestId
                            + " model="
                            + request.Model.Id
                            + " imgTotal="
                            + mmPlan.Total
                            + " imgTruncated="
                            + mmPlan.Dropped
                    );
                }
                catch { }
            }
            var pendingToolImages = new List<ResolvedImage>();
            var pendingToolRefs = new List<(string CallId, string? Name, string? Text)>();
            // S3 §4.3：tool 附图「原始收集张数」（含被 MaxImagesPerTurn 丢掉的部分），
            // 与实发 pendingToolImages.Count 相减即本批丢失数（Planner 只从最早处丢）。
            int pendingCollectedImages = 0;
            void FlushPendingToolImages()
            {
                if (pendingToolImages.Count == 0)
                    return;
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString(
                    "text",
                    BuildTrailingToolImageText(
                        pendingToolRefs,
                        pendingToolImages.Count,
                        CountDroppedImages(pendingCollectedImages, pendingToolImages.Count),
                        maxImagesPerTurn
                    )
                );
                writer.WriteEndObject();
                foreach (var r in pendingToolImages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", r.DataUrl!);
                    writer.WriteString("detail", visionDetail);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                pendingToolImages.Clear();
                pendingToolRefs.Clear();
                pendingCollectedImages = 0;
            }
            int mmIndex = -1;
            // S3 §4.3：outMessages 与 sendMessages 等长同序（仅 Content 副本被补告知句），
            // Kept 按 sendMessages 规划，故索引对齐不变。
            foreach (var msg in outMessages)
            {
                mmIndex++;
                // S6：user 直挂图走 lazy（DataUrl 待 adapter 按占位符顺序解析），不能按 DataUrl 非空预过滤；
                // tool 附图仍是规划期解析，保留原过滤（S3 语义：解析失败的图不发）。
                var kept = mmPlan
                    .Kept[mmIndex]
                    .Where(r =>
                        string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase)
                            ? r.Ref != null
                            : !string.IsNullOrWhiteSpace(r.DataUrl)
                    )
                    .ToList();
                bool isTool = msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase);
                bool isAssistant = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
                bool isUser = msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
                if (!isTool && pendingToolImages.Count > 0)
                {
                    // tool 块结束：先把收齐的图以单个 user 发出，再写当前消息
                    FlushPendingToolImages();
                }
                writer.WriteStartObject();
                writer.WriteString("role", msg.Role);
                if (isAssistant)
                {
                    // 历史思考链默认随上下文发出（DeepSeek reasoning_content；空/开关关则省略零抖动）
                    bool includeReasoning =
                        request.Model.IncludeReasoningInHistory
                        && !string.IsNullOrWhiteSpace(msg.ReasoningContent);
                    // 若有 ToolCalls，content 统一输出有效文本或 ""（兼容严格校验第三方网关）
                    if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    {
                        writer.WriteString("content", msg.Content ?? string.Empty);
                        if (includeReasoning)
                            writer.WriteString("reasoning_content", msg.ReasoningContent);
                        writer.WriteStartArray("tool_calls");
                        foreach (var tc in msg.ToolCalls)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("id", tc.CallId);
                            writer.WriteString("type", "function");
                            writer.WriteStartObject("function");
                            writer.WriteString("name", tc.FunctionName);
                            writer.WriteString("arguments", tc.ArgumentsJson ?? "{}");
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteString("content", msg.Content ?? string.Empty);
                        if (includeReasoning)
                            writer.WriteString("reasoning_content", msg.ReasoningContent);
                    }
                }
                else if (isTool)
                {
                    writer.WriteString("tool_call_id", msg.ToolCallId ?? string.Empty);
                    writer.WriteString("content", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    if (kept.Count > 0)
                    {
                        // 只收齐、不就地写 user，保证 tool 块连续
                        pendingToolImages.AddRange(kept);
                        pendingToolRefs.Add(
                            (msg.ToolCallId ?? string.Empty, msg.Name, msg.Content)
                        );
                        // S3 §4.3：原始张数按消息 Images 计（含被截断丢掉的），供告知句算差值。
                        pendingCollectedImages +=
                            msg.Images?.Count(i =>
                                i != null && !string.IsNullOrWhiteSpace(i.ImageId)
                            ) ?? kept.Count;
                    }
                    continue;
                }
                else if (isUser && (kept.Count > 0 || msg.Images is { Count: > 0 }))
                {
                    // S6 §4.2：user 块按占位符顺序交错 text/image（无锚点 ⇒ 退化文后追加，与 S6 前字节一致）。
                    // resolver 每个 imageId 只调一次（S5 当轮盘按 raw 扣减，重复调用会重复计费预算）。
                    // 锚点全集 = 本消息 Images ∪ 规划器 Kept：前者用于把「已被预算/读盘摘除」的占位符
                    // 就地渲染成 [image omitted]（摘除发生在规划器里，Kept 里没有它），后者用于无占位符老数据。
                    var userKept = kept;
                    var anchors = new List<ChatImageRef>(userKept.Count + (msg.Images?.Count ?? 0));
                    var anchorIds = new HashSet<string>(StringComparer.Ordinal);
                    void AddAnchor(ChatImageRef? img)
                    {
                        string id = (img?.ImageId ?? string.Empty).Trim();
                        if (id.Length == 0 || !anchorIds.Add(id))
                            return;
                        anchors.Add(img!);
                    }
                    foreach (var r in userKept)
                        AddAnchor(r.Ref);
                    if (msg.Images != null)
                    {
                        foreach (var img in msg.Images)
                            AddAnchor(img);
                    }
                    var urlCache = new Dictionary<string, string?>(StringComparer.Ordinal);
                    string? ResolveOnce(ChatImageRef img)
                    {
                        string id = (img.ImageId ?? string.Empty).Trim();
                        if (urlCache.TryGetValue(id, out var cached))
                            return cached;
                        string? found = userKept
                            .FirstOrDefault(r =>
                                string.Equals(
                                    (r.Ref?.ImageId ?? string.Empty).Trim(),
                                    id,
                                    StringComparison.Ordinal
                                )
                            )
                            ?.DataUrl;
                        if (string.IsNullOrWhiteSpace(found) && request.ImageResolver != null)
                            found = request.ImageResolver(img);
                        if (
                            !string.IsNullOrWhiteSpace(found)
                            && !found.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            string mime = string.IsNullOrWhiteSpace(img.Mime)
                                ? "image/jpeg"
                                : img.Mime.Trim();
                            found = "data:" + mime + ";base64," + found;
                        }
                        found = string.IsNullOrWhiteSpace(found) ? null : found.Trim();
                        urlCache[id] = found;
                        return found;
                    }
                    var blocks = ImagePlaceholder.BuildContentBlocks(
                        msg.Content,
                        anchors,
                        ResolveOnce
                    );
                    writer.WriteStartArray("content");
                    foreach (var block in blocks)
                    {
                        if (block.Kind == ImageContentBlockKind.Text)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", block.Text ?? string.Empty);
                            writer.WriteEndObject();
                        }
                        else
                        {
                            string? url = ResolveOnce(block.Image!);
                            if (string.IsNullOrWhiteSpace(url))
                                continue;
                            writer.WriteStartObject();
                            writer.WriteString("type", "image_url");
                            writer.WriteStartObject("image_url");
                            writer.WriteString("url", url);
                            writer.WriteString("detail", visionDetail);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("content", msg.Content ?? string.Empty);
                }
                writer.WriteEndObject();
            }
            // 尾部 tool 块后补收齐的图
            FlushPendingToolImages();

            writer.WriteEndArray(); // messages

            // 6. 工具定义列表（嵌套式）
            // need05-02：按 name 升序稳定排序后序列化（内容不动），保证请求前缀后段字节稳定
            if (request.Tools != null && request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.ParametersSchema.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
