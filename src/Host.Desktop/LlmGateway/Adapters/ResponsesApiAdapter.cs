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
/// OpenAI Responses API 协议适配器 (/v1/responses)
/// 覆盖需求 #2 与 #7：处理 instructions、扁平化 input 多态列表、扁平化 tools 以及语义化 SSE 事件状态机。
/// </summary>
public sealed partial class ResponsesApiAdapter : ILlmProtocolAdapter
{
    public LlmProtocolType Protocol => LlmProtocolType.Resp;

    public string ResolveEndpointUrl(string baseUrl) =>
        HeaderVariableResolver.ResolveEndpointUrl(baseUrl, LlmProtocolType.Resp);

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

            // 2. 流式开关
            writer.WriteBoolean("stream", true);

            // 3. 系统提示词 (顶层 instructions 字段)
            if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
            {
                writer.WriteString("instructions", request.SystemInstructions);
            }

            // 4. 最大输出 Token (max_output_tokens 字段)
            if (request.Model.MaxOutputTokens > 0)
            {
                writer.WriteNumber("max_output_tokens", request.Model.MaxOutputTokens);
            }

            // 5. 采样温度
            if (request.Model.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", request.Model.Temperature.Value);
            }

            // FIX(tool-400)：先内存愈合配对再序列化；tool 附图收齐后在整段 output 块尾发一个 user，避免插断 function_call*→output 序列。
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
            var mmPlan = MultimodalPlanner.PlanTurn(planRequest);
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
            string visionDetail = VisionDefaults.NormalizeDetail(request.Model.VisionDetail);
            // S3 §4.3：截断告知显式化（与 Chat 同口径）：超 MaxImagesPerTurn 时并入发包副本，不落盘。
            int maxImagesPerTurn = VisionDefaults.ResolveMaxImages(request.Model.MaxImagesPerTurn);
            var outMessages =
                mmPlan.Dropped > 0
                    ? MultimodalPlanner.ApplyDropNotice(
                        sendMessages,
                        mmPlan.Dropped,
                        mmPlan.Total,
                        maxImagesPerTurn
                    )
                    : sendMessages;
            var pendingToolImages = new List<ResolvedImage>();
            var pendingToolRefs = new List<(string CallId, string? Name, string? Text)>();
            // S3 §4.3：tool 附图原始收集张数（含被截断丢掉的），供告知句算差值。
            int pendingCollectedImages = 0;
            void FlushPendingToolImages()
            {
                if (pendingToolImages.Count == 0)
                    return;
                writer.WriteStartObject();
                writer.WriteString("type", "message");
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "input_text");
                writer.WriteString(
                    "text",
                    ChatCompletionsAdapter.BuildTrailingToolImageText(
                        pendingToolRefs,
                        pendingToolImages.Count,
                        ChatCompletionsAdapter.CountDroppedImages(
                            pendingCollectedImages,
                            pendingToolImages.Count
                        ),
                        maxImagesPerTurn
                    )
                );
                writer.WriteEndObject();
                foreach (var r in pendingToolImages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_image");
                    writer.WriteString("image_url", r.DataUrl!);
                    writer.WriteString("detail", visionDetail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                pendingToolImages.Clear();
                pendingToolRefs.Clear();
                pendingCollectedImages = 0;
            }
            int mmIndex = -1;
            writer.WriteStartArray("input");
            foreach (var msg in outMessages)
            {
                mmIndex++;
                var kept = mmPlan
                    .Kept[mmIndex]
                    .Where(r => !string.IsNullOrWhiteSpace(r.DataUrl))
                    .ToList();
                bool isTool = msg.Role.Equals("tool", StringComparison.OrdinalIgnoreCase);
                if (!isTool && pendingToolImages.Count > 0)
                    FlushPendingToolImages();
                if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "message");
                    writer.WriteString("role", "user");
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_text");
                    writer.WriteString("text", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    foreach (var r in kept)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "input_image");
                        writer.WriteString("image_url", r.DataUrl!);
                        writer.WriteString("detail", visionDetail);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                else if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    // 历史思考链默认随上下文发出；空/开关关则省略零抖动
                    if (
                        request.Model.IncludeReasoningInHistory
                        && !string.IsNullOrWhiteSpace(msg.ReasoningContent)
                    )
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "reasoning");
                        writer.WriteStartArray("summary");
                        writer.WriteStartObject();
                        writer.WriteString("type", "summary_text");
                        writer.WriteString("text", msg.ReasoningContent);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    if (!string.IsNullOrEmpty(msg.Content))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "message");
                        writer.WriteString("role", "assistant");
                        writer.WriteStartArray("content");
                        writer.WriteStartObject();
                        writer.WriteString("type", "output_text");
                        writer.WriteString("text", msg.Content);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                    {
                        foreach (var tc in msg.ToolCalls)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "function_call");
                            writer.WriteString("call_id", tc.CallId);
                            writer.WriteString("name", tc.FunctionName);
                            writer.WriteString("arguments", tc.ArgumentsJson ?? "{}");
                            writer.WriteEndObject();
                        }
                    }
                }
                else if (isTool)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("call_id", msg.ToolCallId ?? string.Empty);
                    writer.WriteString("output", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    if (kept.Count > 0)
                    {
                        pendingToolImages.AddRange(kept);
                        pendingToolRefs.Add(
                            (msg.ToolCallId ?? string.Empty, msg.Name, msg.Content)
                        );
                        // S3 §4.3：原始张数按消息 Images 计（含被截断丢掉的），供告知句算差值。
                        pendingCollectedImages += msg.Images?.Count ?? kept.Count;
                    }
                }
                else if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    // 若有额外 system 消息，作为 developer message 追加至 input
                    if (pendingToolImages.Count > 0)
                        FlushPendingToolImages();
                    writer.WriteStartObject();
                    writer.WriteString("type", "message");
                    writer.WriteString("role", "developer");
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_text");
                    writer.WriteString("text", msg.Content ?? string.Empty);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    continue;
                }
            }
            FlushPendingToolImages();
            writer.WriteEndArray(); // input

            // 7. 扁平化工具定义列表（非嵌套式）
            // need05-02：按 name 升序稳定排序后序列化（内容不动），保证请求前缀后段字节稳定
            if (request.Tools != null && request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.ParametersSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
