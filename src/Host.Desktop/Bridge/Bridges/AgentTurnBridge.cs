using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;

namespace Host.Desktop.Bridge;

/// <summary>need21-B02: Agent 轮次（含去重字典与测试钩子）。</summary>
public sealed partial class HostBridgeFacade
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeSessionTokens =
        new();
    private readonly ConcurrentDictionary<string, string> _activeAgentRequestIds = new();
    private readonly ConcurrentDictionary<string, string> _recentAgentRequestIds = new();

    private void RememberAgentRequestId(string reqId, string turnKey)
    {
        try
        {
            if (_recentAgentRequestIds.Count > 5000)
                _recentAgentRequestIds.Clear();
            _recentAgentRequestIds[reqId] = turnKey;
        }
        catch { }
    }

    public Task<BridgeResponse<string>> StartAgentTurn(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        string? attachmentsJson = null,
        string? requestId = null
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    BridgeResponse<string>.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空")
                );
            }
            if (string.IsNullOrWhiteSpace(userText))
            {
                return Task.FromResult(BridgeResponse<string>.Fail("EMPTY_PROMPT", "用户输入不能为空"));
            }
            string turnKey = "agent:" + sessionId.Trim();
            // need22-P1 T1：sendPrompt requestId 回显去重（重复投递直接成功回显，不重起一轮）。
            string turnRequestId = string.IsNullOrWhiteSpace(requestId)
                ? Guid.NewGuid().ToString("N")
                : requestId.Trim();
            if (_recentAgentRequestIds.ContainsKey(turnRequestId))
            {
                return Task.FromResult(
                    BridgeResponse<string>.Success(
                        sessionId.Trim(),
                        $"重复请求已去重（requestId={turnRequestId} 回显）"
                    )
                );
            }
            // need22-P1 T5：压缩中查询可用，但新轮明确拒绝（COMPACTING 码；可先 CancelCompaction 再发轮）。
            if (_activeSessionTokens.ContainsKey("compact:" + sessionId.Trim()))
            {
                return Task.FromResult(
                    BridgeResponse<string>.Fail("COMPACTING", "该会话正在压缩上下文（查询可用），新一轮请稍候或先取消压缩")
                );
            }
            // need22-P1 T1：同 session 重入不再静默丢旧轮 → 返回 TURN_IN_PROGRESS（附 activeRequestId），旧轮继续跑。
            // 旧轮走正常 TurnCancelled 落盘仅发生在显式 CancelAgentTree 时。
            if (_activeSessionTokens.ContainsKey(turnKey))
            {
                string activeRequestId = _activeAgentRequestIds.TryGetValue(turnKey, out var ar)
                    ? ar
                    : "unknown";
                return Task.FromResult(
                    BridgeResponse<string>.Fail(
                        "TURN_IN_PROGRESS",
                        $"上一轮仍在进行中（activeRequestId={activeRequestId}），本发已拒绝，旧轮不受影响"
                    )
                );
            }
            var cts = new CancellationTokenSource();
            _activeSessionTokens[turnKey] = cts;
            _activeAgentRequestIds[turnKey] = turnRequestId;
            RememberAgentRequestId(turnRequestId, turnKey);
            string wsId = workspaceId.Trim();
            string sessId = sessionId.Trim();
            string prompt = userText;
            string? provId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
            string? modId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
            try
            {
                var imgRefs = SaveTurnAttachments(wsId, sessId, attachmentsJson, ref prompt);
                if (imgRefs.Count > 0)
                    Infrastructure.Storage.SessionImageStash.Put(sessId, imgRefs);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn("[HostBridge] 附件落盘失败（文字仍发送）：" + ex.Message);
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await AgentOrchestrator
                            .RunAgentTurnAsync(wsId, sessId, prompt, provId, modId, cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        JsEventDispatcher.Publish(
                            "agent:turn-event",
                            new { sessionId = sessId, kind = "TurnCancelled" }
                        );
                    }
                    catch (Exception ex)
                    {
                        HaloLogger.App.Error(
                            $"[HostBridge] Agent turn failed for session '{sessId}'",
                            ex
                        );
                        JsEventDispatcher.Publish(
                            "agent:turn-event",
                            new
                            {
                                sessionId = sessId,
                                kind = "TurnError",
                                payload = new { errorMessage = ex.Message }
                            }
                        );
                    }
                    finally
                    {
                        _activeSessionTokens.TryRemove(turnKey, out _);
                        _activeAgentRequestIds.TryRemove(turnKey, out _);
                        cts.Dispose();
                    }
                },
                CancellationToken.None
            );

            return Task.FromResult(BridgeResponse<string>.Success(sessId, "Agent 推理已启动"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("StartAgentTurn failed", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("AGENT_START_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// 取消正在运行的 Agent ReAct 循环：触发 CancellationTokenSource.Cancel() 并杀子进程（经 ToolContext 传递）。
    /// need13：升级为级联 CancelTree（先子后主）。
    /// </summary>
    public Task<BridgeResponse<bool>> CancelAgentTurn(string sessionId)
    {
        return CancelAgentTree(sessionId);
    }

    /// <summary>need13：级联取消整树（先子 CTS，后主 CTS；主若在 ask_user 阻塞一并解开）。</summary>
    public Task<BridgeResponse<bool>> CancelAgentTree(string mainSessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mainSessionId))
            {
                return Task.FromResult(
                    BridgeResponse<bool>.Fail("INVALID_SESSION_ID", "会话 ID 不能为空")
                );
            }

            string mainId = mainSessionId.Trim();
            int subN = 0;
            try
            {
                subN = _subCoordinator?.CancelTree(mainId) ?? 0;
            }
            catch { }
            string turnKey = "agent:" + mainId;
            bool hadMain = false;
            if (_activeSessionTokens.TryRemove(turnKey, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                hadMain = true;
            }
            try
            {
                Host.Desktop.Tools.BuiltIn.AskUserGate.RemoveTimeout(
                    mainId,
                    string.Empty,
                    "cancelled"
                );
            }
            catch { }
            HaloLogger.App.Info($"[HostBridge] 已请求取消 Session {mainId} 的 Agent 推理（含 {subN} 个子代理）");
            if (hadMain || subN > 0)
                return Task.FromResult(
                    BridgeResponse<bool>.Success(true, $"已发送取消指令（含 {subN} 个子代理）")
                );
            return Task.FromResult(BridgeResponse<bool>.Success(false, "没有正在运行的 Agent 任务"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CancelAgentTurn failed for '{mainSessionId}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("CANCEL_ERROR", ex.Message));
        }
    }

    /// <summary>need22-P1 T5：取消进行中的压缩（CTS 接入取消；服务侧落 COMPACT_CANCELLED 并发布 CompactFailed）。</summary>
    public Task<BridgeResponse<bool>> CancelCompaction(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(
                    BridgeResponse<bool>.Fail("INVALID_SESSION_ID", "会话 ID 不能为空")
                );
            }
            string compactKey = "compact:" + sessionId.Trim();
            if (_activeSessionTokens.TryRemove(compactKey, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch { }
                try
                {
                    cts.Dispose();
                }
                catch { }
                HaloLogger.App.Info($"[HostBridge] 已请求取消 Session {sessionId.Trim()} 的压缩");
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已发送压缩取消指令"));
            }
            return Task.FromResult(BridgeResponse<bool>.Success(false, "没有正在进行的压缩任务"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CancelCompaction failed for '{sessionId}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("CANCEL_COMPACT_ERROR", ex.Message));
        }
    }

    /// <summary>need22-P1：测试钩子（进行中 Agent 轮数；COMPACTING/重入断言用）。</summary>
    internal int ActiveTurnCountForTests
    {
        get
        {
            int n = 0;
            foreach (var kv in _activeSessionTokens)
                if (kv.Key.StartsWith("agent:", StringComparison.Ordinal))
                    n++;
            return n;
        }
    }

    /// <summary>need22-P1：测试钩子（指定会话是否压缩中）。</summary>
    internal bool IsCompactingForTests(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId)
        && _activeSessionTokens.ContainsKey("compact:" + sessionId.Trim());
}
