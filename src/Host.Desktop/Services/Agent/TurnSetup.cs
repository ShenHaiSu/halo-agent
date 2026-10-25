using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;

namespace Host.Desktop.Services;

/// <summary>
/// need21-B03: 轮首准备阶段（原 RunAgentTurnCoreAsync 顶部 214-573 行整体搬迁）。
/// 校验→愈合→append user→快照→systemPrompt→账单/配置快照→激活集；终端直接返回。
/// </summary>
public sealed partial class AgentOrchestrator
{
    private async Task<TurnPrepared> PrepareTurnAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken
    )
    {
        TurnPrepared End(AgentTurnResult t) => new() { Terminal = t };
        if (cancellationToken.IsCancellationRequested)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-before-start" });
            return End(AgentTurnResult.Cancelled(0, 0));
        }
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return End(AgentTurnResult.Failed("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空"));
        if (string.IsNullOrWhiteSpace(userText))
            return End(AgentTurnResult.Failed("EMPTY_PROMPT", "用户输入不能为空"));

        var ctx = new TurnContext
        {
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            UserText = userText,
            ProviderId = providerId,
            ModelId = modelId,
            CancellationToken = cancellationToken,
        };
        try
        {
            ctx.Workspace = await _sessionService
                .GetWorkspaceAsync(workspaceId, cancellationToken)
                .ConfigureAwait(false);
            ctx.Session = await _sessionService
                .GetSessionAsync(workspaceId, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { reason = "cancelled-during-load" });
            return End(AgentTurnResult.Cancelled(0, 0));
        }
        if (ctx.Workspace == null)
            return End(AgentTurnResult.Failed("WORKSPACE_NOT_FOUND", $"工作区不存在：'{workspaceId}'"));
        if (ctx.Session == null)
            return End(AgentTurnResult.Failed("SESSION_NOT_FOUND", $"会话不存在：'{sessionId}'"));

        try
        {
            await _sessionService
                .HealSessionHistoryAsync(workspaceId, sessionId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { }

        var userMessage = UnifiedChatMessage.User(userText.Trim());
        try
        {
            var pendingImgs = Infrastructure.Storage.SessionImageStash.Take(sessionId);
            if (pendingImgs != null && pendingImgs.Count > 0)
            {
                // S6 §4.1-2：按占位符在 userText 中的出现顺序排序（imageId 不在文本中的图沉底，保兼容）。
                // 与 adapter 交错（ChatCompletionsAdapter/ResponsesApiAdapter）共用同一纯函数，
                // 落盘顺序 == 发包顺序 == 转录顺序。
                userMessage.Images = Core.Models.ImagePlaceholder.OrderImagesByPlaceholder(
                    userMessage.Content,
                    pendingImgs
                );
            }
        }
        catch (Exception exImgs)
        {
            HaloLogger.App.Warn("[Image] 附件暂存取出失败，已降级为纯文本发送", exImgs);
        }
        try
        {
            await _sessionService
                .AppendSessionMessageAsync(workspaceId, sessionId, userMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(
                sessionId,
                "TurnCancelled",
                new { reason = "cancelled-during-append" }
            );
            return End(AgentTurnResult.Cancelled(0, 0));
        }
        ctx.UserMessage = userMessage;

        var (provider, model) = ResolveProviderAndModel(providerId, modelId, ctx.Session);
        ctx.Provider = provider;
        ctx.Model = model;
        ctx.SnapVision = model.SupportsVision;
        ctx.SnapMaxImages = VisionDefaults.ResolveMaxImages(model.MaxImagesPerTurn);
        try
        {
            if (_storageManager != null)
            {
                var skillMemory = new SkillMemoryService(_storageManager);
                var merged = skillMemory.GetMergedForPrompt(ctx.Workspace.RootPath);
                var skillEntries = merged
                    .GlobalSkills.Concat(merged.ProjectSkills)
                    .Where(e => e != null && e.Enabled)
                    .Select(e => new SystemPromptBuilder.StableSkillEntry(
                        e.Scope,
                        e.Name,
                        e.Summary,
                        e.Priority
                    ));
                ctx.SkillIndexBlock = SystemPromptBuilder.BuildSkillIndexBlock(skillEntries);
                if (
                    merged
                        .GlobalSkills.Concat(merged.ProjectSkills)
                        .Any(e =>
                            e != null
                            && e.Enabled
                            && string.Equals(e.Pin, "full", StringComparison.OrdinalIgnoreCase)
                        )
                )
                    HaloLogger.App.Warn(
                        "[Skill] 存在 pin=full 的 Skill，本期仅渲染索引行（正文按需经 skill_tool 拉取）"
                    );
                var memEntries = merged
                    .GlobalMemory.Concat(merged.ProjectMemory)
                    .Where(e =>
                        e != null
                        && e.Enabled
                        && !string.Equals(e.Pin, "archived", StringComparison.OrdinalIgnoreCase)
                    )
                    .Select(e => new SystemPromptBuilder.StableMemoryEntry(
                        e.Scope,
                        e.Name,
                        e.BodyMarkdown,
                        e.Priority
                    ));
                ctx.MemoryPinnedBlock = SystemPromptBuilder.BuildMemoryPinnedBlock(memEntries);
                if (!string.IsNullOrEmpty(ctx.SkillIndexBlock))
                    ctx.ResidentStableTokens += TokenBudgetService.EstimateTokens(
                        ctx.SkillIndexBlock
                    );
                if (!string.IsNullOrEmpty(ctx.MemoryPinnedBlock))
                    ctx.ResidentStableTokens += TokenBudgetService.EstimateTokens(
                        ctx.MemoryPinnedBlock
                    );
            }
        }
        catch (Exception exSnap)
        {
            HaloLogger.App.Warn("[Skill] Skill/Memory 快照失败，已回退空域继续会话", exSnap);
            ctx.SkillIndexBlock = null;
            ctx.MemoryPinnedBlock = null;
            ctx.ResidentStableTokens = 0;
        }

        if (string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase)) { }
        else if (_agentConfigService != null)
        {
            try
            {
                SubPolicy = new SubPolicySnapshot(_agentConfigService.GetInternal());
            }
            catch { }
        }
        try
        {
            if (!string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
                _toolRegistry.Register(new SpawnSubAgentTool(SubPolicy.SubAgentEnabled));
        }
        catch { }
        ctx.SubPolicyBlock = SystemPromptBuilder.BuildSubAgentPolicyBlock(
            SubPolicy.SubAgentEnabled,
            SubPolicy.SubMaxParallel,
            SubPolicy.SubMaxIterations,
            SubPolicy.SubAllowExecCommand,
            SubPolicy.SubAllowFileWrite,
            SubPolicy.SubAllowSkillWrite,
            SubPolicy.SubAllowMemoryWrite
        );
        try
        {
            if (_storageManager != null)
            {
                string mainId = sessionId;
                try
                {
                    if (
                        ctx.Session != null
                        && !string.IsNullOrWhiteSpace(ctx.Session.ParentSessionId)
                    )
                        mainId = ctx.Session.ParentSessionId!;
                }
                catch { }
                var goalSvc = new GoalService(_storageManager);
                var (g, _) = goalSvc.TryLoadGoal(workspaceId, mainId);
                ctx.GoalBlock = SystemPromptBuilder.BuildGoalBlock(g);
                if (
                    string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(ctx.GoalBlock)
                )
                    ctx.GoalBlock += "\n（你是子代理：仅可 goal_tool/get 查看，不可写入。）";
            }
        }
        catch
        {
            ctx.GoalBlock = null;
        }

        try
        {
            var promptCfg =
                _promptConfigService?.GetPromptConfigInternal() ?? PromptDefaults.CreateDefault();
            var preview = SystemPromptBuilder.BuildPreview(
                promptCfg,
                ctx.Workspace,
                pyOverride: null,
                dateOverride: null,
                skillIndexBlock: ctx.SkillIndexBlock,
                memoryPinnedBlock: ctx.MemoryPinnedBlock,
                goalBlock: ctx.GoalBlock
            );
            ctx.SystemPrompt = preview.AssembledText;
            if (!string.IsNullOrWhiteSpace(ctx.SubPolicyBlock))
                ctx.SystemPrompt = string.IsNullOrWhiteSpace(ctx.SystemPrompt)
                    ? ctx.SubPolicyBlock + "\n"
                    : ctx.SystemPrompt + "\n" + ctx.SubPolicyBlock + "\n";
            if (string.Equals(AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
                ctx.SystemPrompt += "\n" + SystemPromptBuilder.SubAgentSuffixBlock + "\n";
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(
                sessionId,
                "TurnCancelled",
                new { reason = "cancelled-during-prompt" }
            );
            return End(AgentTurnResult.Cancelled(0, 0));
        }
        catch (Exception ex)
        {
            try
            {
                ctx.SystemPrompt = await SystemPromptBuilder
                    .BuildAsync(ctx.Workspace)
                    .ConfigureAwait(false);
            }
            catch (Exception ex2)
            {
                return End(AgentTurnResult.Failed("SYSTEM_PROMPT_ERROR", ex2.Message));
            }
            HaloLogger.App.Warn("[Prompt] 配置渲染失败，已回退默认模板", ex);
        }

        ctx.DataRoot = _storageManager?.CurrentDataRoot ?? string.Empty;
        int effectiveMax = MaxIterations;
        ctx.CfgExecDefaultMs = AgentDefaults.DefaultTimeoutMs;
        ctx.CfgExecMaxMs = AgentDefaults.MaxTimeoutMs;
        try
        {
            if (_agentConfigService != null)
            {
                var cfg = _agentConfigService.GetInternal();
                ctx.CfgExecDefaultMs = cfg.ExecDefaultTimeoutMs;
                ctx.CfgExecMaxMs = cfg.ExecMaxTimeoutMs;
                ctx.CfgAllowedCommands = cfg.AllowedCommands;
                ctx.CfgToolsPreset = cfg.ToolsPreset;
                ctx.CfgToolsAllow = cfg.ToolsAllowlist;
                ctx.CfgToolsDeny = cfg.ToolsDenylist;
                ctx.CfgClosedWorld = cfg.StrictToolsMode != StrictToolsMode.Off;
                if (cfg.MaxIterations == 0)
                    effectiveMax = 0;
                else if (cfg.MaxIterations > 0)
                    effectiveMax = Math.Min(cfg.MaxIterations, AgentDefaults.HardIterationsCeil);
                else
                {
                    HaloLogger.App.Warn(
                        $"[Agent] agent.json 轮次非法（{cfg.MaxIterations}），已回退构造值 {MaxIterations}"
                    );
                    effectiveMax = MaxIterations;
                }
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn("[Agent] 智能体行为配置读取失败，已回退构造值", ex);
            effectiveMax = MaxIterations;
        }
        ctx.EffectiveMax = effectiveMax;
        ResolvedMaxIterations = effectiveMax;
        if (effectiveMax == 0)
            HaloLogger.App.Warn($"[Agent] infinite iterations for session {sessionId}，仅取消/模型停手可结束");
        try
        {
            var toolsCfgDoc = new AgentConfigDocument
            {
                ToolsPreset = ctx.CfgToolsPreset,
                ToolsAllowlist = ctx.CfgToolsAllow,
                ToolsDenylist = ctx.CfgToolsDeny
            };
            var resolved = ToolPreset.ResolveActiveSet(_toolRegistry.ToolNames, toolsCfgDoc);
            ctx.TurnActiveSet = new HashSet<string>(resolved, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            ctx.TurnActiveSet = new HashSet<string>(
                _toolRegistry.ToolNames,
                StringComparer.OrdinalIgnoreCase
            );
        }
        return new TurnPrepared { Ctx = ctx };
    }

    private (ProviderConfig Provider, ModelConfig Model) ResolveProviderAndModel(
        string? providerId,
        string? modelId,
        SessionMetadata session
    )
    {
        ProviderConfig? provider = null;
        if (_providerConfigService != null)
        {
            try
            {
                string pid = !string.IsNullOrWhiteSpace(providerId)
                    ? providerId!
                    : (session.ProviderId ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(pid))
                {
                    provider = _providerConfigService.GetProviderById(pid);
                }
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
        }

        provider ??= new ProviderConfig
        {
            Id = string.IsNullOrWhiteSpace(providerId) ? "provider_default" : providerId!,
            Name = "DefaultProvider",
            BaseUrl = "https://api.openai.com/v1"
        };

        string mid = !string.IsNullOrWhiteSpace(modelId)
            ? modelId!
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

        return (provider, model);
    }
}
