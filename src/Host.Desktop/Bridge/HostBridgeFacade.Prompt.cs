using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

public sealed class PromptConfigWithMeta : PromptConfigDocument
{
    public bool IsDefault { get; set; }
    public string? FallbackReason { get; set; }
}

public sealed class PromptDiagnosticsDto
{
    public PromptPreviewDto CurrentPreview { get; set; } = new();
    public LastSentPromptSnapshot? LastSent { get; set; }
    public bool PreviewEqualsLastSent { get; set; }
    public string CompressorPromptPreview { get; set; } = string.Empty;
    public string TitlePromptPreview { get; set; } = string.Empty;
}

/// <summary>need06-03 §3：提示词配置 Bridge（对标 HostBridgeFacade.SessionTitle.cs）。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<PromptConfigWithMeta>> GetPromptConfig()
    {
        try
        {
            var (doc, isDefault, fallback) = PromptConfigService.LoadWithMeta();
            return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Success(ToWithMeta(doc, isDefault, fallback)));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetPromptConfig failed", ex);
            return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_GET_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<PromptConfigWithMeta>> SavePromptConfig(string configJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_VALIDATION", "配置 JSON 不能为空"));
             PromptConfigDocument? incoming;
             try
             {
                 incoming = PromptJsonOptions.DeserializeConfigDocument(configJson);
             }
             catch (Exception ex)
             {
                 return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_VALIDATION", "配置 JSON 解析失败：" + ex.Message));
             }
             if (incoming == null)
                 return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_VALIDATION", "配置 JSON 解析为空"));
             try {
                var saved = _promptConfigService.SavePromptConfig(incoming);
                JsEventDispatcher.Publish("config:prompt-changed", saved);
                return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Success(ToWithMeta(saved, false, null), "提示词配置已保存，下一轮对话起生效"));
            }
            catch (PromptConfigValidationException vex)
            {
                return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_VALIDATION", $"块 [{vex.BlockId}]：{vex.Message}"));
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SavePromptConfig failed", ex);
            return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_SAVE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<PromptConfigWithMeta>> ResetPromptConfig(string? blockId)
    {
        try
        {
            try
            {
                var reset = _promptConfigService.ResetPromptConfig(blockId);
                JsEventDispatcher.Publish("config:prompt-changed", reset);
                return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Success(ToWithMeta(reset, false, null), "已恢复默认"));
            }
            catch (PromptConfigValidationException vex)
            {
                return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_VALIDATION", $"块 [{vex.BlockId}]：{vex.Message}"));
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("ResetPromptConfig failed", ex);
            return Task.FromResult(BridgeResponse<PromptConfigWithMeta>.Fail("PROMPT_RESET_ERROR", ex.Message));
        }
    }

    public async Task<BridgeResponse<PromptPreviewDto>> PreviewPrompt(string workspaceId)
    {
        try
        {
            var (doc, isDefault, fallback) = _promptConfigService.LoadWithMeta();
            WorkspaceInfo? ws = null;
            if (!string.IsNullOrWhiteSpace(workspaceId))
                ws = await _workspaceSessionService.GetWorkspaceAsync(workspaceId.Trim()).ConfigureAwait(false);
            if (ws == null)
            {
                if (string.IsNullOrWhiteSpace(workspaceId))
                {
                    ws = new WorkspaceInfo { Name = "workspace", RootPath = Environment.CurrentDirectory };
                    fallback = string.IsNullOrWhiteSpace(fallback)
                        ? "当前无工作区，为中性预览"
                        : fallback + "；当前无工作区，为中性预览";
                }
                else
                {
                    return BridgeResponse<PromptPreviewDto>.Fail("WORKSPACE_NOT_FOUND", $"工作区不存在：'{workspaceId}'");
                }
            }
            var preview = SystemPromptBuilder.BuildPreview(doc, ws, null, null, ws.Id);
            preview.IsDefault = isDefault;
            preview.FallbackReason = fallback;
            return BridgeResponse<PromptPreviewDto>.Success(preview);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("PreviewPrompt failed", ex);
            return BridgeResponse<PromptPreviewDto>.Fail("PROMPT_PREVIEW_ERROR", ex.Message);
        }
    }

     public async Task<BridgeResponse<PromptDiagnosticsDto>> GetPromptDiagnostics(string workspaceId, string? sessionId)
     {
         try
         {
             var previewRes = await PreviewPrompt(workspaceId).ConfigureAwait(false);
             if (!previewRes.Ok || previewRes.Data == null)
                 return BridgeResponse<PromptDiagnosticsDto>.Fail(previewRes.Code ?? "PROMPT_PREVIEW_ERROR", previewRes.Message ?? "预览失败");
            var last = PromptLastSentStore.Get(sessionId);
            bool equals = last != null
                && string.Equals(last.PrefixHash, previewRes.Data.PrefixHash, StringComparison.Ordinal);
            var dto = new PromptDiagnosticsDto
            {
                CurrentPreview = previewRes.Data,
                LastSent = last,
                PreviewEqualsLastSent = equals,
                 CompressorPromptPreview = Services.CompactionService.CompressorSystemPrompt,
                TitlePromptPreview = "你是会话标题生成器。只根据用户首轮对话生成 4-10 个汉字的简短标题，不加标点、不加引号、不解释，只输出标题本身。若无法概括则输出首条消息前5字。（对话正文已脱除）",
            };
            return BridgeResponse<PromptDiagnosticsDto>.Success(dto);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetPromptDiagnostics failed", ex);
            return BridgeResponse<PromptDiagnosticsDto>.Fail("PROMPT_DIAG_ERROR", ex.Message);
        }
    }

    private static PromptConfigWithMeta ToWithMeta(PromptConfigDocument doc, bool isDefault, string? fallback)
    {
        string json = JsonSerializer.Serialize(doc, HaloJsonOptions.IndentedCamelCase);
        var meta = JsonSerializer.Deserialize<PromptConfigWithMeta>(json, HaloJsonOptions.IndentedCamelCase)
            ?? new PromptConfigWithMeta();
        meta.IsDefault = isDefault;
        meta.FallbackReason = fallback;
        return meta;
    }
}
