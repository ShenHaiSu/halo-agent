using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>
/// need03-03 T2 后端标题精炼（方案 B）：用本轮同模型发一次小请求（无工具、maxTokens 50、20s 熔断），
/// 解析结构化结果后经 UpdateSessionTitleAsync 回写（自动置 TitleLocked）。失败返回 Fail，前端静默保留 T1 的 5 字。
/// </summary>
public sealed partial class HostBridgeFacade
{
    public async Task<BridgeResponse<string>> GenerateSessionTitle(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<string>.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空");
            }
            string wsId = workspaceId.Trim();
            string sessId = sessionId.Trim();

            var session = await _workspaceSessionService
                .GetSessionAsync(wsId, sessId)
                .ConfigureAwait(false);
            if (session == null)
            {
                return BridgeResponse<string>.Fail("SESSION_NOT_FOUND", "会话不存在");
            }
            if (session.TitleLocked)
            {
                return BridgeResponse<string>.Success(session.Title ?? string.Empty, "标题已冻结");
            }

            var messages = await _workspaceSessionService
                .GetSessionMessagesAsync(wsId, sessId)
                .ConfigureAwait(false);
            var firstUser = messages.FirstOrDefault(m =>
                string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
            );
            if (firstUser == null || string.IsNullOrWhiteSpace(firstUser.Content))
            {
                return BridgeResponse<string>.Fail("NO_USER_MESSAGE", "首轮用户消息不存在");
            }
            var firstAssistant = messages.LastOrDefault(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            );
            string userText = firstUser.Content!.Trim();
            string assistantText = (firstAssistant?.Content ?? string.Empty).Trim();
            string roundText = "<user>: " + userText + "\n<assistant>: " + assistantText;
            if (roundText.Length > 2000)
                roundText = roundText.Substring(0, 2000);

            // 同模型解析：session.ProviderId/ModelId 优先，显式传入覆盖
            ProviderConfig? provider = null;
            try
            {
                string pid = !string.IsNullOrWhiteSpace(providerId)
                    ? providerId!.Trim()
                    : (session.ProviderId ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(pid))
                    provider = _providerConfigService.GetProviderById(pid);
                if (provider == null)
                {
                    var doc = _providerConfigService.GetProvidersConfigInternal();
                    provider =
                        doc.Providers.FirstOrDefault(p => p.Enabled)
                        ?? doc.Providers.FirstOrDefault();
                }
            }
            catch
            {
                provider = null;
            }
            if (provider == null)
            {
                return BridgeResponse<string>.Fail("NO_PROVIDER", "无可用模型服务商");
            }
            string mid = !string.IsNullOrWhiteSpace(modelId)
                ? modelId!.Trim()
                : (session.ModelId ?? string.Empty);
            ModelConfig? model = null;
            if (!string.IsNullOrWhiteSpace(mid))
            {
                model = provider.Models.FirstOrDefault(m =>
                    string.Equals(m.Id, mid, StringComparison.OrdinalIgnoreCase)
                );
            }
            model ??=
                provider.Models.FirstOrDefault(m => m.Enabled)
                ?? provider.Models.FirstOrDefault()
                ?? new ModelConfig
                {
                    Id = "gpt-4o",
                    Protocol = LlmProtocolType.Chat,
                    MaxContextTokens = 128000,
                    MaxOutputTokens = 4096
                };
            if (string.IsNullOrWhiteSpace(provider.GetEffectiveApiKey()))
            {
                return BridgeResponse<string>.Fail("NO_API_KEY", "服务商未配置 API Key");
            }

            var workspace = await _workspaceSessionService
                .GetWorkspaceAsync(wsId)
                .ConfigureAwait(false);
            string sessionHash = !string.IsNullOrWhiteSpace(session.SessionHash)
                ? session.SessionHash
                : HeaderVariableResolver.ComputeSessionHash(wsId, sessId);
            string requestId = Guid.NewGuid().ToString("N");
            var headerContext = new HeaderResolutionContext(
                wsId,
                workspace?.Name ?? string.Empty,
                workspace?.RootPath ?? string.Empty,
                sessId,
                sessionHash,
                provider.Id,
                provider.Name,
                model.Id,
                requestId
            );

            const string titleSystemPrompt =
                "你是会话标题生成器。只根据用户首轮对话生成 4-10 个汉字的简短标题，不加标点、不加引号、不解释，只输出标题本身。若无法概括则输出首条消息前5字。";
            string titleUserPrompt = "对话全文如下：\n---\n" + roundText + "\n---";
            var titleModel = new ModelConfig
            {
                Id = model.Id,
                DisplayName = model.DisplayName,
                Protocol = model.Protocol,
                MaxContextTokens = model.MaxContextTokens,
                MaxOutputTokens = 50,
                Temperature = 0.2,
                Enabled = true
            };
            var llmRequest = new UnifiedLlmRequest
            {
                RequestId = requestId,
                Provider = provider,
                Model = titleModel,
                HeaderContext = headerContext,
                SystemInstructions = titleSystemPrompt,
                Messages = new List<UnifiedChatMessage>
                {
                    UnifiedChatMessage.System(titleSystemPrompt),
                    UnifiedChatMessage.User(titleUserPrompt)
                },
                Tools = Array.Empty<UnifiedToolDefinition>()
            };

            var sb = new System.Text.StringBuilder();
            UnifiedTokenUsage? titleUsage = null;
            string titleStatus = "ok";
            string titleFailCode = string.Empty;
            string titleFailMsg = string.Empty;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await foreach (
                    var evt in _llmGatewayClient
                        .StreamAsync(llmRequest, cts.Token)
                        .ConfigureAwait(false)
                )
                {
                    if (
                        evt.Kind == UnifiedStreamEventKind.TextDelta
                        && !string.IsNullOrEmpty(evt.TextDelta)
                    )
                    {
                        sb.Append(evt.TextDelta);
                        if (sb.Length > 200)
                            break;
                    }
                    else if (evt.Kind == UnifiedStreamEventKind.UsageUpdated && evt.Usage != null)
                    {
                        titleUsage = evt.Usage;
                    }
                    else if (evt.Kind == UnifiedStreamEventKind.StreamCompleted)
                    {
                        break;
                    }
                    else if (evt.Kind == UnifiedStreamEventKind.StreamCancelled)
                    {
                        titleStatus = "cancelled";
                        titleFailCode = "TITLE_CANCELLED";
                        titleFailMsg = "标题请求被取消";
                        break;
                    }
                    else if (evt.Kind == UnifiedStreamEventKind.StreamError)
                    {
                        titleStatus = "error";
                        titleFailCode = evt.ErrorCode ?? "TITLE_LLM_ERROR";
                        titleFailMsg = evt.ErrorMessage ?? "标题请求失败";
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                titleStatus = "cancelled";
                titleFailCode = "TITLE_TIMEOUT";
                titleFailMsg = "标题请求超时（20s）";
            }
            // need12-P1/P2：标题小请求同样落袋（成功/取消/失败都记；无实测用估算）。
            try
            {
                var titleRows = new List<PendingUsageRow>();
                UsageRecorder.CollectAuto(
                    titleRows,
                    provider,
                    model,
                    titleUsage,
                    UsageRecorder.EstimateInput(llmRequest.Messages, llmRequest.SystemInstructions),
                    UsageRecorder.EstimateOutput(sb.ToString()),
                    titleStatus,
                    requestId,
                    sessId,
                    wsId,
                    UsageRecorder.SourceTitle
                );
                UsageRecorder.Flush(UsageMetricsServiceInternal, titleRows, sessId, wsId);
            }
            catch
            { /* 用量落袋永不影响标题主流程 */
            }
            if (titleStatus != "ok")
            {
                return BridgeResponse<string>.Fail(titleFailCode, titleFailMsg);
            }

            string parsed = ParseSessionTitle(sb.ToString());
            if (string.IsNullOrEmpty(parsed))
            {
                return BridgeResponse<string>.Fail("TITLE_PARSE_EMPTY", "标题解析为空");
            }

            bool ok = await _workspaceSessionService
                .UpdateSessionTitleAsync(wsId, sessId, parsed)
                .ConfigureAwait(false);
            if (!ok)
            {
                return BridgeResponse<string>.Fail("TITLE_SAVE_FAILED", "标题回写失败");
            }
            return BridgeResponse<string>.Success(parsed, "标题已精炼");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GenerateSessionTitle failed for session '{sessionId}'", ex);
            return BridgeResponse<string>.Fail("TITLE_ERROR", ex.Message);
        }
    }

    private static string ParseSessionTitle(string raw)
    {
        string t =
            (raw ?? string.Empty)
                .Trim()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(t))
            return string.Empty;
        t = System.Text.RegularExpressions.Regex.Replace(
            t,
            "^(标题|题目|名称|title)[:：]\\s*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );
        t = t.Trim('"', '\'', '「', '『', '“', '‘', '(', '[', '【')
            .TrimEnd('"', '\'', '」', '』', '”', '’', ')', ']', '】');
        t = System.Text.RegularExpressions.Regex.Replace(
            t,
            "[。！？!?;；，,、.\\-—…·\\s]+$",
            string.Empty
        );
        t = System.Text.RegularExpressions.Regex.Replace(t, "^[-*•\\d.\\s]+", string.Empty).Trim();
        if (string.IsNullOrEmpty(t))
            return string.Empty;
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(t);
        if (elements.Length < 2)
            return string.Empty;
        if (elements.Length <= 12)
            return t;
        return t.Substring(elements[0], elements[12] - elements[0]);
    }
}
