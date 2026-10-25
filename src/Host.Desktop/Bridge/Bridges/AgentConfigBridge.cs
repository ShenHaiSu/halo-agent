using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Core = Host.Desktop.Core;

namespace Host.Desktop.Bridge;

public sealed class AgentConfigWithMeta : AgentConfigDocument
{
    public bool IsDefault { get; set; }
    public string? FallbackReason { get; set; }
}

/// <summary>need08 §6：智能体行为配置 Bridge（对标 HostBridgeFacade.Prompt.cs）。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<AgentConfigWithMeta>> GetAgentConfig()
    {
        try
        {
            var (doc, isDefault, fallback) = _agentConfigService.LoadWithMeta();
            return Task.FromResult(
                BridgeResponse<AgentConfigWithMeta>.Success(
                    ToAgentWithMeta(doc, isDefault, fallback)
                )
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetAgentConfig failed", ex);
            return Task.FromResult(
                BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_GET_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<AgentConfigWithMeta>> SaveAgentConfig(string configJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", "配置 JSON 不能为空")
                );
            AgentConfigDocument? incoming;
            try
            {
                incoming = AgentJsonOptions.DeserializeConfigDocument(configJson);
            }
            catch (Exception ex)
            {
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Fail(
                        "AGENT_VALIDATION",
                        "配置 JSON 解析失败：" + ex.Message
                    )
                );
            }
            if (incoming == null)
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_VALIDATION", "配置 JSON 解析为空")
                );
            try
            {
                var saved = _agentConfigService.SaveAgentConfig(incoming);
                JsEventDispatcher.Publish("config:agent-changed", saved);
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Success(
                        ToAgentWithMeta(saved, false, null),
                        "智能体行为配置已保存，下一轮对话起生效"
                    )
                );
            }
            catch (AgentConfigValidationException vex)
            {
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Fail(
                        "AGENT_VALIDATION",
                        $"字段 [{vex.Field}]：{vex.Message}"
                    )
                );
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SaveAgentConfig failed", ex);
            return Task.FromResult(
                BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_SAVE_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<AgentConfigWithMeta>> ResetAgentConfig(string? field)
    {
        try
        {
            try
            {
                var reset = _agentConfigService.ResetAgentConfig(field);
                JsEventDispatcher.Publish("config:agent-changed", reset);
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Success(
                        ToAgentWithMeta(reset, false, null),
                        "已恢复默认"
                    )
                );
            }
            catch (AgentConfigValidationException vex)
            {
                return Task.FromResult(
                    BridgeResponse<AgentConfigWithMeta>.Fail(
                        "AGENT_VALIDATION",
                        $"字段 [{vex.Field}]：{vex.Message}"
                    )
                );
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("ResetAgentConfig failed", ex);
            return Task.FromResult(
                BridgeResponse<AgentConfigWithMeta>.Fail("AGENT_RESET_ERROR", ex.Message)
            );
        }
    }

    private static AgentConfigWithMeta ToAgentWithMeta(
        AgentConfigDocument doc,
        bool isDefault,
        string? fallback
    )
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            doc,
            HaloJsonOptions.IndentedCamelCase
        );
        var meta =
            System.Text.Json.JsonSerializer.Deserialize<AgentConfigWithMeta>(
                json,
                HaloJsonOptions.IndentedCamelCase
            ) ?? new AgentConfigWithMeta();
        meta.IsDefault = isDefault;
        meta.FallbackReason = fallback;
        return meta;
    }

    /// <summary>need09-05：startAgentTurn 附件落盘 + 占位符重写。attachmentsJson: [{fileName,mime?,base64|dataUrl}]；
    /// S6 起 userText 内 `[image:i]` 按下标重写为机器可解析形态 `[image:<短id>]`（保留位置与 imageId，不再整体展平为纯文本）。</summary>
    internal List<Core.Models.ChatImageRef> SaveTurnAttachments(
        string workspaceId,
        string sessionId,
        string? attachmentsJson,
        ref string userText
    )
    {
        var refs = new List<Core.Models.ChatImageRef>();
        if (string.IsNullOrWhiteSpace(attachmentsJson))
            return refs;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(attachmentsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return refs;
            string dataRoot = _storageManager.CurrentDataRoot;
            int index = -1;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                index++;
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                string fileName =
                    el.TryGetProperty("fileName", out var fnEl)
                    && fnEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (fnEl.GetString() ?? $"pasted-{index}.png")
                        : $"pasted-{index}.png";
                string? mimeRaw = null;
                if (
                    el.TryGetProperty("mime", out var mEl)
                    && mEl.ValueKind == System.Text.Json.JsonValueKind.String
                )
                    mimeRaw = mEl.GetString();
                string? b64 = null;
                if (
                    el.TryGetProperty("base64", out var bEl)
                    && bEl.ValueKind == System.Text.Json.JsonValueKind.String
                )
                    b64 = bEl.GetString();
                if (
                    string.IsNullOrWhiteSpace(b64)
                    && el.TryGetProperty("dataUrl", out var dEl)
                    && dEl.ValueKind == System.Text.Json.JsonValueKind.String
                )
                    b64 = dEl.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                    continue;
                string b64trim = b64.Trim();
                string mime = (mimeRaw ?? string.Empty).Trim().ToLowerInvariant();
                int comma = b64trim.IndexOf(',');
                if (b64trim.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                {
                    string head = b64trim.Substring(0, comma);
                    if (string.IsNullOrWhiteSpace(mime))
                    {
                        int semi = head.IndexOf(';');
                        if (
                            head.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            && semi > 5
                        )
                            mime = head.Substring(5, semi - 5).Trim().ToLowerInvariant();
                    }
                    b64trim = b64trim.Substring(comma + 1);
                }
                if (string.IsNullOrWhiteSpace(mime))
                    mime = Infrastructure.Storage.ImageAttachmentStore.MimeFromExtension(fileName);
                // S2 base64 有界：字符串 O(1) 前筛 + 解码后复核；超限走现有 continue
                // （静默跳过语义本阶段不动，S3 统一加告知），不抛、不中断整轮。
                if (
                    !Infrastructure.Storage.ImageAttachmentStore.TryDecodeBase64Bounded(
                        b64trim,
                        Infrastructure.Storage.ImageAttachmentStore.MaxImageBytes,
                        out byte[]? raw0
                    )
                    || raw0 == null
                )
                    continue;
                byte[] raw = raw0;
                if (!Infrastructure.Storage.ImageAttachmentStore.CheckMagic(raw, mime))
                    continue;
                // need09-fix：贴图与 tool 统一无损直存（原字节落盘，sha 去重），detail 只影响发模参数，不降质、不转码。
                var (bytes, normMime, w, h) =
                    Infrastructure.Storage.ImageAttachmentStore.PrepareLossless(raw, mime);
                var (imgRef, _) = Infrastructure.Storage.ImageAttachmentStore.SaveImage(
                    dataRoot,
                    workspaceId,
                    sessionId,
                    fileName,
                    normMime,
                    bytes,
                    w,
                    h
                );
                refs.Add(imgRef);
                try
                {
                    // S6 §4.1：保留**机器可解析**的有序引用形态（`[image:<短id>]`），不再重写成
                    // `[图片：fileName]` 纯文本——只有保留 imageId 才能在 TurnSetup 排序、
                    // adapter 交错、转录渲染三处用同一个顺序函数对齐位置。
                    // 人类可读性由转录层换回 fileName（前端 imagesOf + orderImagesByContent）。
                    string marker = Core.Models.ImagePlaceholder.Marker(imgRef.ImageId);
                    if (marker.Length > 0)
                        userText = userText.Replace("[image:" + index + "]", marker);
                }
                catch
                { /* 占位符重写永不阻断 */
                }
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[HostBridge] 附件解析落盘失败：" + ex.Message);
        }
        return refs;
    }

    /// <summary>need09-05：取会话图片缩略图（data:image/png;base64，长边≤256；只传缩略图防 CefSharp 字符串超限）。</summary>
    public Task<BridgeResponse<string>> GetImageThumbnail(
        string workspaceId,
        string sessionId,
        string imageId
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imageId))
                return Task.FromResult(
                    BridgeResponse<string>.Fail("INVALID_IMAGE", "imageId 不能为空")
                );
            string? abs = _sessionRepository.FindAttachmentPath(
                workspaceId,
                sessionId,
                imageId.Trim()
            );
            if (abs == null || !File.Exists(abs))
                return Task.FromResult(
                    BridgeResponse<string>.Fail("IMAGE_MISSING", "图片已丢失（文件被删除）")
                );
            // S2 有界读：缩略图同样走 take(limit+1) 限长读（附件链路，无界读零残留）。
            if (
                !Infrastructure.Storage.ImageAttachmentStore.TryReadBounded(
                    abs,
                    Infrastructure.Storage.ImageAttachmentStore.MaxImageBytes,
                    out byte[]? bytes
                )
                || bytes == null
            )
                return Task.FromResult(BridgeResponse<string>.Fail("IMAGE_DECODE_ERROR", "图片解码失败"));
            string? url = Infrastructure.Storage.ImageAttachmentStore.MakeThumbnailDataUrl(bytes);
            if (url == null)
                return Task.FromResult(BridgeResponse<string>.Fail("IMAGE_DECODE_ERROR", "图片解码失败"));
            return Task.FromResult(BridgeResponse<string>.Success(url));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetImageThumbnail failed", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("IMAGE_THUMB_ERROR", ex.Message));
        }
    }

    /// <summary>need09-06：前端提交 ask_user 答案（ticket 必须匹配，否则 ASK_TICKET_MISMATCH）。</summary>
    public Task<BridgeResponse<bool>> ResolveAskUser(
        string sessionId,
        string ticketId,
        string answersJson
    )
    {
        try
        {
            var (ok, code, message) = Host.Desktop.Tools.BuiltIn.AskUserGate.TryResolve(
                sessionId,
                ticketId,
                answersJson
            );
            if (!ok)
                return Task.FromResult(BridgeResponse<bool>.Fail(code, message));
            return Task.FromResult(BridgeResponse<bool>.Success(true, message));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("ResolveAskUser failed", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("ASK_RESOLVE_ERROR", ex.Message));
        }
    }
}
