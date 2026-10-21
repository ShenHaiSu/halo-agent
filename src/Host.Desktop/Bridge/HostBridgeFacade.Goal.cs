using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;

namespace Host.Desktop.Bridge;

/// <summary>need16 Goal 桥接（GetGoal/SaveGoal/ClearGoal；子 id 统一归一到父）。</summary>
public sealed partial class HostBridgeFacade
{
    private GoalService GoalSvc => new GoalService(_storageManager);

    private async Task<(string wsMain, string sessMain)> ResolveGoalTargetAsync(
        string workspaceId,
        string sessionId
    )
    {
        string sess = (sessionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sess))
            return (workspaceId, sess);
        try
        {
            var meta = await _workspaceSessionService
                .GetSessionAsync(workspaceId, sess)
                .ConfigureAwait(false);
            if (meta != null && !string.IsNullOrWhiteSpace(meta.ParentSessionId))
                return (workspaceId, meta.ParentSessionId!.Trim());
        }
        catch { }
        return (workspaceId, sess);
    }

    public async Task<BridgeResponse<SessionGoal>> GetGoal(string workspaceId, string sessionId)
    {
        try
        {
            var (_, target) = await ResolveGoalTargetAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            var (goal, corrupted) = GoalSvc.TryLoadGoal(workspaceId, target);
            if (corrupted)
                return BridgeResponse<SessionGoal>.Success(goal, "目标文件损坏，已视为空，可重置");
            return BridgeResponse<SessionGoal>.Success(goal);
        }
        catch (Exception ex)
        {
            return BridgeResponse<SessionGoal>.Fail("GOAL_LOAD_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<SessionGoal>> SaveGoal(
        string workspaceId,
        string sessionId,
        string goalJson
    )
    {
        try
        {
            var (_, target) = await ResolveGoalTargetAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(goalJson))
                return BridgeResponse<SessionGoal>.Fail("GOAL_VALIDATION", "目标 JSON 不能为空");
            SessionGoal? incoming;
            try
            {
                incoming = JsonSerializer.Deserialize<SessionGoal>(
                    goalJson,
                    HaloJsonOptions.IndentedCamelCase
                );
            }
            catch (Exception)
            {
                return BridgeResponse<SessionGoal>.Fail("GOAL_VALIDATION", "目标 JSON 解析失败");
            }
            if (incoming == null)
                return BridgeResponse<SessionGoal>.Fail("GOAL_VALIDATION", "目标 JSON 解析为空");
            incoming.Milestones ??= new List<GoalMilestone>();
            var saved = GoalSvc.SaveGoal(workspaceId, target, incoming, "user");
            try
            {
                JsEventDispatcher.Publish("goal:updated", new { sessionId = target, goal = saved });
            }
            catch { }
            return BridgeResponse<SessionGoal>.Success(saved, "目标已保存，下一轮生效");
        }
        catch (Exception ex)
        {
            return BridgeResponse<SessionGoal>.Fail("GOAL_SAVE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<bool>> ClearGoal(string workspaceId, string sessionId)
    {
        try
        {
            var (_, target) = await ResolveGoalTargetAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            GoalSvc.SaveGoal(workspaceId, target, new SessionGoal(), "user");
            try
            {
                JsEventDispatcher.Publish(
                    "goal:updated",
                    new { sessionId = target, goal = new SessionGoal() }
                );
            }
            catch { }
            return BridgeResponse<bool>.Success(true, "目标已清空");
        }
        catch (Exception ex)
        {
            return BridgeResponse<bool>.Fail("GOAL_CLEAR_ERROR", ex.Message);
        }
    }
}
