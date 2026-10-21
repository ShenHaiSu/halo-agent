using System.Text;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools;

/// <summary>
/// 统一工具注册调度中心：注册 15 个标准基础工具（6 存量 + view_image/ask_user/skill_tool/memory_tool + need13 spawn_subagent + need14 edit_lines + need15 undo_edit/edit + need16 goal_tool），
/// DispatchAsync 捕获异常转自愈提示，输出统一过 ToolOutputGuard，
/// 支持 GetDefinitions() 导出 chat/resp 双协议 Schema。
/// 并发安全（无共享可变状态，处理器均为无状态）。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registerLock = new();

    public ToolRegistry(bool registerBuiltIns = true)
    {
        if (registerBuiltIns)
        {
            Register(new ExecCommandTool());
            Register(new ReadFileTool());
            Register(new WriteFileTool());
            Register(new EditFileTool());
            Register(new EditLinesTool());
            Register(new ListDirTool());
            Register(new GrepSearchTool());
            Register(new ViewImageTool());
            Register(new AskUserTool());
            Register(new SkillTool());
            Register(new MemoryTool());
            // need13：主 registry 常显 spawn_subagent（可见但可拒；关闭/超限走网关式强拒绝）
            Register(new SpawnSubAgentTool(enabled: true));
            Register(new UndoEditTool());
            Register(new EditTool());
            // need16：goal_tool 常显（主写子读，子写走 AgentRole 内鉴权强拒绝）
            Register(new GoalTool());
        }
    }

    /// <summary>
    /// 注册工具处理器（同名覆盖）。
    /// </summary>
    public void Register(IToolHandler handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_registerLock)
        {
            _handlers[handler.Name] = handler;
        }
    }

    /// <summary>
    /// 已注册的工具名列表。
    /// </summary>
    public IReadOnlyList<string> ToolNames
    {
        get
        {
            lock (_registerLock)
            {
                return _handlers.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// 导出全部工具定义（按 name 升序稳定排序，need09-07 新工具自动归位）。
    /// </summary>
    public IReadOnlyList<ToolDefinition> GetDefinitions()
    {
        lock (_registerLock)
        {
            return _handlers
                .Values.Select(h =>
                {
                    try
                    {
                        return h.GetDefinition();
                    }
                    catch (Exception)
                    {
                        return new ToolDefinition(
                            h.Name,
                            h.Description,
                            "{\"type\":\"object\",\"properties\":{}}"
                        );
                    }
                })
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// need17-01/联调：导出激活工具定义。Hidden/Disabled 永不声明（01 冻结：edit 可执行、可转调，但不声明）；
    /// active==null 视为全部 Direct。Orchestrator 在轮首计算 activeSet、本轮冻结后传入。
    /// </summary>
    public IReadOnlyList<ToolDefinition> GetActiveDefinitions(ISet<string>? active)
    {
        var all = GetDefinitions();
        HashSet<string>? activeSet =
            active != null
                ? new HashSet<string>(
                    active.Where(n => !string.IsNullOrWhiteSpace(n)),
                    StringComparer.OrdinalIgnoreCase
                )
                : null;
        return all.Where(d =>
            {
                if (d == null || d.Exposure != ToolExposure.Direct)
                {
                    return false;
                }
                if (activeSet != null)
                {
                    return activeSet.Contains(d.Name);
                }
                return true;
            })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 导出 Chat Completions (chat) 协议 tools JSON 数组字符串。
    /// </summary>
    public string GetChatToolsJson()
    {
        var defs = GetDefinitions();
        var sb = new StringBuilder("[");
        for (int i = 0; i < defs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(defs[i].ToChatToolJson());
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 导出 Responses API (resp) 协议 tools JSON 数组字符串。
    /// </summary>
    public string GetResponsesToolsJson()
    {
        var defs = GetDefinitions();
        var sb = new StringBuilder("[");
        for (int i = 0; i < defs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(defs[i].ToResponsesToolJson());
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 调度执行：未知工具与异常统一转为自然语言自愈提示，成功输出统一过 ToolOutputGuard。
    /// need17-02：老签名保留（activeTools=null=不做激活过滤，仅中央校验；老调用零改动）。
    /// </summary>
    public Task<ToolResult> DispatchAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        return DispatchAsync(call, context, activeTools: null, ct);
    }

    /// <summary>
    /// need17-01/02/联调：带激活集的调度执行。
    /// 查 handler（找不到→UNKNOWN_TOOL；Disabled/不在激活集→TOOL_NOT_ENABLED）
    /// → ToolCallValidator.Validate（失败直接返回，不进 ExecuteAsync，不计时；closedWorld=false 即 Strict Off 回滚，未知字段降级 warning）
    /// → 成功用归一化参数执行；warnings 拼到结果尾并回填 ValidatedWarnings。
    /// </summary>
    public async Task<ToolResult> DispatchAsync(
        ToolCallDto call,
        ToolContext context,
        ISet<string>? activeTools,
        CancellationToken ct = default,
        bool closedWorld = true
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string toolName = call?.Name?.Trim() ?? string.Empty;
        string callId = string.IsNullOrWhiteSpace(call?.Id)
            ? Guid.NewGuid().ToString("N")
            : call.Id;

        IToolHandler? handler;
        lock (_registerLock)
        {
            _handlers.TryGetValue(toolName, out handler);
        }

        if (handler == null)
        {
            sw.Stop();
            string available;
            lock (_registerLock)
            {
                available = string.Join("、", _handlers.Keys);
            }

            return ToolResult.FailCode(
                "UNKNOWN_TOOL",
                $"未知工具 '{toolName}'。可用工具：{available}。请修正工具名后重试。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }

        // need17-01：Disabled 预留拒绝（need17 不启用，仅通道）。
        ToolExposure exposure = ToolExposure.Direct;
        try
        {
            exposure = handler.GetDefinition()?.Exposure ?? ToolExposure.Direct;
        }
        catch (Exception)
        {
            exposure = ToolExposure.Direct;
        }
        if (exposure == ToolExposure.Disabled)
        {
            sw.Stop();
            return ToolResult.FailCode(
                "TOOL_NOT_ENABLED",
                $"工具 '{toolName}' 已被禁用（Exposure=Disabled）。请改用其他工具后重试。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }

        // need17-01：激活集过滤（activeTools==null=不过滤，老调用零改动；Hidden 工具被显式点名仍可执行）。
        if (
            activeTools != null
            && !activeTools.Any(n => string.Equals(n, toolName, StringComparison.OrdinalIgnoreCase))
        )
        {
            sw.Stop();
            string available = string.Join(
                "、",
                activeTools.OrderBy(n => n, StringComparer.Ordinal)
            );
            return ToolResult.FailCode(
                "TOOL_NOT_ENABLED",
                $"工具 '{toolName}' 存在但未在本轮启用。本轮可用工具：{available}。请切换 toolsPreset 或在 toolsAllowlist 中点名该工具后重试。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }
        // need17-02：中央校验（失败不进 ExecuteAsync，不计时）。
        var validation = ToolCallValidator.Validate(toolName, call?.ArgumentsJson, closedWorld);
        if (!validation.Success)
        {
            sw.Stop();
            return ToolResult.FailCode(
                validation.Code ?? "INVALID_JSON_ARGUMENTS",
                validation.Message ?? "参数校验失败。",
                payloadJson: validation.PayloadJson,
                durationMs: sw.ElapsedMilliseconds
            );
        }

        ToolCallDto execCall = call!;
        if (
            !string.Equals(validation.NormalizedJson, call?.ArgumentsJson, StringComparison.Ordinal)
        )
        {
            execCall = new ToolCallDto(call!.Id, call!.Name, validation.NormalizedJson);
        }

        ToolResult result;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                context.CancellationToken
            );
            result = await handler
                .ExecuteAsync(execCall, context, linkedCts.Token)
                .ConfigureAwait(false);
            result ??= ToolResult.Fail($"工具 '{toolName}' 返回空结果，请重试。");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ToolResult.Fail($"工具 '{toolName}' 执行已被取消。", durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            HaloLogger.Tool.Error($"Tool '{toolName}' (call {callId}) threw: {ex.Message}", ex);
            return ToolResult.Fail(
                $"工具 '{toolName}' 执行异常：{ex.Message}。请修正参数后重试；若持续失败，请改用其他工具或缩小操作范围。",
                durationMs: sw.ElapsedMilliseconds
            );
        }

        // 成功输出统一过守卫（超长落盘，幂等：未超限原样返回）
        try
        {
            if (!string.IsNullOrEmpty(result.Output))
            {
                var (display, spilled) = ToolOutputGuard.ApplyGuard(
                    result.Output,
                    context.DataRoot,
                    context.WorkspaceId,
                    context.SessionId,
                    callId
                );
                result.Output = display;
                if (!string.IsNullOrEmpty(spilled))
                {
                    result.SpilledFile ??= spilled;
                }
            }
        }
        catch (Exception)
        {
            // 守卫自身永不破坏调用结果
        }

        // need17-02：warnings 拼到结果尾（仅提示，不判失败；守卫预算不变，warnings 在守卫之后追加）。
        if (validation.Warnings.Count > 0)
        {
            result.ValidatedWarnings = new List<string>(validation.Warnings);
            string tail = string.Join("\n", validation.Warnings.Select(w => "Warning: " + w));
            if (result.Success)
            {
                result.Output = string.IsNullOrEmpty(result.Output)
                    ? tail
                    : result.Output + "\n" + tail;
            }
            else
            {
                result.Error = string.IsNullOrEmpty(result.Error)
                    ? tail
                    : result.Error + "\n" + tail;
            }
        }

        if (result.DurationMs <= 0)
        {
            result.DurationMs = sw.ElapsedMilliseconds;
        }

        sw.Stop();
        try
        {
            HaloLogger.Tool.Info(
                $"Tool '{toolName}' (call {callId}) finished: success={result.Success} exit={result.ExitCode} cost={result.DurationMs}ms"
                    + (result.SpilledFile != null ? $" spilled={result.SpilledFile}" : string.Empty)
            );
        }
        catch (Exception)
        {
            // 日志失败不影响结果
        }

        return result;
    }
}
