using System.Text.Json;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need16 goal_tool：主写子读（子写网关式强拒绝 + [GOAL_DENIED] 前缀）。</summary>
[HaloAgent.Tools.Tool("goal_tool")]
public sealed class GoalTool : IToolHandler
{
    private readonly Func<string, string, Services.GoalService>? _serviceFactory;

    public GoalTool()
        : this(serviceFactory: null) { }

    public GoalTool(Func<string, string, Services.GoalService>? serviceFactory = null)
    {
        _serviceFactory = serviceFactory;
    }

    // NEED17-05 §5.1: Exposure=Direct（子只读鉴权不动；激活集裁剪由基础组 01 落子）。
    public string Name => "goal_tool";
    public const string Need17Exposure = "Direct";

    // NEED17-05 §5.3: 供 06 动态装配的 PromptBrief/PromptRules（ToolDefinition 字段待基础组 01 扩展，此处先以常量承载，装配表见 SystemPromptBuilder）。
    public const string PromptBrief = "Read or update the session goal";
    public static readonly string[] PromptRules =
    {
        "Writes take effect next turn; sub sessions are read-only."
    };

    public string Description =>
        "Manage the main-session goal: actions get/set/update/progress/add_milestone/complete_milestone/clear. Sub-agents: get only. "
        + "(子会话内仅 get 可用，写动作将被拒绝。) "
        + "Writes take effect next turn; sub sessions are read-only.";

    public ToolDefinition GetDefinition() =>
        new(
            Name,
            Description,
            new ToolSchemaBuilder()
                .AddString(
                    "action",
                    "get|set|update|progress|add_milestone|complete_milestone|clear. Required. Sub-agents: get only.",
                    required: true,
                    enumValues: new[]
                    {
                        "get",
                        "set",
                        "update",
                        "progress",
                        "add_milestone",
                        "complete_milestone",
                        "clear"
                    }
                )
                .AddString("title", string.Empty)
                .AddString("description", string.Empty)
                .AddArray("milestones", string.Empty, itemsType: "string")
                .AddString("milestone", "id or title for complete_milestone")
                .AddInteger("percent", string.Empty, minimum: 0, maximum: 100)
                .AddString("note", string.Empty)
                .Build()
        );

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // need22-P0 S3：NormalizedJson 单次解析（ToolArgs 统一入口；异常交业务层兜底为空对象）
            JsonElement args = new JsonElement();
            try
            {
                using var toolArgs = ToolArgs.Parse(call.ArgumentsJson);
                if (toolArgs.Root.ValueKind == JsonValueKind.Object)
                {
                    // Clone 出 Root：脱离 using 生命周期（ToolArgs 释放后仍可用）
                    using JsonDocument snapshot = JsonDocument.Parse(toolArgs.Root.GetRawText());
                    args = snapshot.RootElement.Clone();
                }
            }
            catch
            {
                args = new JsonElement();
            }
            string action = "get";
            if (
                args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("action", out var av)
                && av.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(av.GetString())
            )
                action = av.GetString()!.Trim();
            string act = action.ToLowerInvariant();
            bool isSub = string.Equals(
                context.AgentRole,
                "sub",
                StringComparison.OrdinalIgnoreCase
            );
            string ws = context.WorkspaceId ?? string.Empty;
            // 子会话读父 Goal；子无父兜底读自身
            string sess =
                (
                    !string.IsNullOrWhiteSpace(context.ParentSessionId)
                        ? context.ParentSessionId!
                        : context.SessionId
                ) ?? string.Empty;
            if (isSub && !string.IsNullOrWhiteSpace(context.ParentSessionId))
                sess = context.ParentSessionId!.Trim();
            else if (isSub && string.IsNullOrWhiteSpace(sess))
                sess = context.SessionId ?? string.Empty;
            if (isSub && act != "get")
            {
                sw.Stop();
                return Task.FromResult(
                    ToolResult.Fail(
                        "[GOAL_DENIED] 子代理仅可 goal_tool/get 查看父目标，不可写入。本次已拒绝，请在总结中引用目标文本交主代理修改。",
                        durationMs: sw.ElapsedMilliseconds
                    )
                );
            }
            var svc =
                _serviceFactory != null
                    ? _serviceFactory(ws, sess)
                    : new Services.GoalService(
                        string.IsNullOrWhiteSpace(context.DataRoot)
                            ? System.IO.Path.GetTempPath()
                            : context.DataRoot
                    );

            (SessionGoal goal, bool corrupted) loaded;
            try
            {
                loaded = svc.TryLoadGoal(ws, sess);
            }
            catch
            {
                loaded = (new SessionGoal(), false);
            }

            var cur = loaded.goal;
            if (act == "get")
            {
                string note = loaded.corrupted ? "[目标文件损坏，已视为空，可重置] " : string.Empty;
                return Task.FromResult(
                    ToolResult.Ok(note + SlimJson(cur), durationMs: sw.ElapsedMilliseconds)
                );
            }

            try
            {
                var next = svc.ApplyAction(cur, act, args);
                var saved = svc.SaveGoal(ws, sess, next, "main");
                try
                {
                    JsEventDispatcher.Publish(
                        "goal:updated",
                        new { sessionId = sess, goal = SlimObj(saved) }
                    );
                }
                catch { }
                sw.Stop();
                return Task.FromResult(
                    ToolResult.Ok(SlimJson(saved), durationMs: sw.ElapsedMilliseconds)
                );
            }
            catch (Services.GoalValidationException gex)
            {
                sw.Stop();
                string prefix =
                    gex.Code == "GOAL_MILESTONE_MISSING"
                        ? "[GOAL_MILESTONE_MISSING] "
                        : "[GOAL_VALIDATION] ";
                return Task.FromResult(
                    ToolResult.Fail(prefix + gex.Message, durationMs: sw.ElapsedMilliseconds)
                );
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(
                ToolResult.Fail(
                    "[GOAL_VALIDATION] goal_tool 执行失败：" + ex.Message,
                    durationMs: sw.ElapsedMilliseconds
                )
            );
        }
    }

    internal static object SlimObj(SessionGoal g)
    {
        return new
        {
            title = g.Title ?? string.Empty,
            description = g.Description ?? string.Empty,
            percent = g.Percent,
            note = g.Note ?? string.Empty,
            updatedAtUtc = g.UpdatedAtUtc,
            milestones = (g.Milestones ?? new List<GoalMilestone>())
                .Select(m => new
                {
                    id = m.Id,
                    title = m.Title,
                    done = m.Done
                })
                .ToList(),
        };
    }

    internal static string SlimJson(SessionGoal g)
    {
        string json = JsonSerializer.Serialize(
            SlimObj(g),
            Infrastructure.Storage.HaloJsonOptions.CompactCamelCase
        );
        if (json.Length > 2000)
            json = json[..2000];
        return json;
    }
}
