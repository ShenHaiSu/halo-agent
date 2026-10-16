using System.Text;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Safety;

namespace Host.Desktop.Tools;

/// <summary>
/// 统一工具注册调度中心：注册 10 个标准基础工具（6 存量 + view_image/ask_user/skill_tool/memory_tool），
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
            Register(new ListDirTool());
            Register(new GrepSearchTool());
            Register(new ViewImageTool());
            Register(new AskUserTool());
            Register(new SkillTool());
            Register(new MemoryTool());
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
            return _handlers.Values.Select(h =>
            {
                try
                {
                    return h.GetDefinition();
                }
                catch (Exception)
                {
                    return new ToolDefinition(h.Name, h.Description, "{\"type\":\"object\",\"properties\":{}}");
                }
            }).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        }
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
    /// </summary>
    public async Task<ToolResult> DispatchAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string toolName = call?.Name?.Trim() ?? string.Empty;
        string callId = string.IsNullOrWhiteSpace(call?.Id) ? Guid.NewGuid().ToString("N") : call.Id;

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

            return ToolResult.Fail(
                $"未知工具 '{toolName}'。可用工具：{available}。请修正工具名后重试。",
                durationMs: sw.ElapsedMilliseconds);
        }

        ToolResult result;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.CancellationToken);
            result = await handler.ExecuteAsync(call!, context, linkedCts.Token).ConfigureAwait(false);
            result ??= ToolResult.Fail($"工具 '{toolName}' 返回空结果，请重试。");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ToolResult.Fail(
                $"工具 '{toolName}' 执行已被取消。",
                durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            HaloLogger.Tool.Error($"Tool '{toolName}' (call {callId}) threw: {ex.Message}", ex);
            return ToolResult.Fail(
                $"工具 '{toolName}' 执行异常：{ex.Message}。请修正参数后重试；若持续失败，请改用其他工具或缩小操作范围。",
                durationMs: sw.ElapsedMilliseconds);
        }

        // 成功输出统一过守卫（超长落盘，幂等：未超限原样返回）
        try
        {
            if (!string.IsNullOrEmpty(result.Output))
            {
                var (display, spilled) = ToolOutputGuard.ApplyGuard(
                    result.Output, context.DataRoot, context.WorkspaceId, context.SessionId, callId);
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

        if (result.DurationMs <= 0)
        {
            result.DurationMs = sw.ElapsedMilliseconds;
        }

        sw.Stop();
        try
        {
            HaloLogger.Tool.Info(
                $"Tool '{toolName}' (call {callId}) finished: success={result.Success} exit={result.ExitCode} cost={result.DurationMs}ms"
                + (result.SpilledFile != null ? $" spilled={result.SpilledFile}" : string.Empty));
        }
        catch (Exception)
        {
            // 日志失败不影响结果
        }

        return result;
    }
}
