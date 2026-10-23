using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need21-B04: Skill/Memory 调度（action 分流读/写；读委托 SkillFileReader，写委托 SkillWriter）。</summary>
public static class SkillDispatcher
{
    public static async Task<ToolResult> ExecuteKindAsync(
        bool isSkill,
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string kind = SkillFileReader.KindLabel(isSkill);
            string action = "list",
                scope = string.Empty,
                name = string.Empty,
                content = string.Empty;
            bool hasContent = false,
                hasScope = false,
                hasName = false;
            string? pin = null;
            int? priority = null;
            bool? enabled = null;
            try
            {
                // need22-P0 S3：NormalizedJson 单次解析（ToolArgs 统一入口）
                using var toolArgs = ToolArgs.Parse(call.ArgumentsJson);
                JsonElement root = toolArgs.Root;
                if (root.ValueKind != JsonValueKind.Object)
                    return ToolResult.Fail(
                        $"参数必须是对象。正确示例：{{\"action\":\"list\",\"scope\":\"project\"}}。[SKILL_VALIDATION]",
                        durationMs: sw.ElapsedMilliseconds
                    );
                string? a = toolArgs.GetString("action");
                if (!string.IsNullOrWhiteSpace(a))
                    action = a.Trim().ToLowerInvariant();
                scope = toolArgs.GetString("scope") ?? string.Empty;
                hasScope = toolArgs.Root.TryGetProperty("scope", out _);
                name = toolArgs.GetString("name") ?? string.Empty;
                hasName = toolArgs.Root.TryGetProperty("name", out _);
                string? c = toolArgs.GetString("content");
                if (c != null || toolArgs.Root.TryGetProperty("content", out _))
                {
                    content = c ?? string.Empty;
                    hasContent =
                        toolArgs.Root.TryGetProperty("content", out var cc)
                        && cc.ValueKind == JsonValueKind.String;
                }
                // memory 存量别名：body 与 content 二选一（validator 已做条件必填，handler 双认）
                if (!hasContent && toolArgs.GetString("body") is string b)
                {
                    content = b;
                    hasContent = true;
                }
                pin = toolArgs.GetString("pin");
                if (toolArgs.TryGetInt32("priority", out int pv))
                    priority = pv;
                if (toolArgs.TryGetBool("enabled", out bool eb))
                    enabled = eb;
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。正确示例：{{\"action\":\"list\",\"scope\":\"project\"}}。[SKILL_VALIDATION]",
                    durationMs: sw.ElapsedMilliseconds
                );
            }

            string dataRoot = string.IsNullOrWhiteSpace(context.DataRoot)
                ? Path.GetTempPath()
                : context.DataRoot;
            bool allowAll = action == "list";
            string scopeResolved = SkillValidator.ResolveScope(
                hasScope ? scope : null,
                context,
                allowAll
            );
            if (scopeResolved is not ("global" or "project" or "all"))
                return ToolResult.Fail(
                    $"scope 非法：'{scope}'（仅允许 global|project{(allowAll ? "|all" : string.Empty)}）。[SKILL_VALIDATION]",
                    durationMs: sw.ElapsedMilliseconds
                );
            if (!allowAll && scopeResolved == "all")
                scopeResolved = "project";

            // need13：子写 skill/memory 鉴权（读 list/get 放行；写 create/update/delete/enable/disable 按快照拦截）
            bool isSubWrite =
                !string.Equals(action, "list", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "get", StringComparison.OrdinalIgnoreCase);
            if (
                isSubWrite
                && string.Equals(context.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
            )
            {
                bool allowed = isSkill ? context.SubAllowSkillWrite : context.SubAllowMemoryWrite;
                if (!allowed)
                    return ToolResult.Fail(
                        $"[PERMISSION_DENIED:{(isSkill ? "skill_tool" : "memory_tool")}:write] 子代理禁止写 {(isSkill ? "skill" : "memory")}（设置>智能体行为>子代理>{(isSkill ? "skill" : "memory")}写=false）。list/get 可用，本次 {action} 已拒绝。请改用只读查询，或记入总结交主代理修改。",
                        durationMs: sw.ElapsedMilliseconds
                    );
                // 子写 global 域同样拒绝（即使开关开了，见 03 §3.3）
                if (string.Equals(scopeResolved, "global", StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Fail(
                        $"[PERMISSION_DENIED:{(isSkill ? "skill_tool" : "memory_tool")}:write] 子代理禁止写全局 {(isSkill ? "skill" : "memory")}（scope=global）。本次 {action} 已拒绝。请改用 project 域或记入总结交主代理修改。",
                        durationMs: sw.ElapsedMilliseconds
                    );
            }

            switch (action)
            {
                case "list":
                {
                    var entries = SkillFileReader.ListEntries(
                        isSkill,
                        scopeResolved,
                        context,
                        dataRoot
                    );
                    string note =
                        (scopeResolved == "all" && !SkillFileReader.HasWorkspace(context))
                            ? "（Project 域：(no workspace) 为空，只返 Global）"
                            : string.Empty;
                    return ToolResult.Ok(
                        SkillFileReader.FormatList(isSkill, entries, note),
                        durationMs: sw.ElapsedMilliseconds
                    );
                }
                case "get":
                {
                    if (!hasName || string.IsNullOrWhiteSpace(name))
                        return ToolResult.Fail(
                            $"get 需要 name。请先 action=list 确认名称。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    string? nameErr = SkillValidator.ValidateName(name);
                    if (nameErr != null)
                        return ToolResult.Fail(nameErr, durationMs: sw.ElapsedMilliseconds);
                    var e = SkillFileReader.GetEntry(
                        isSkill,
                        scopeResolved,
                        name,
                        context,
                        dataRoot
                    );
                    if (e == null)
                        return ToolResult.Fail(
                            $"不存在的 {kind} '{SkillValidator.NormalizeName(name)}'（scope={scopeResolved}）。请先 action=list 确认。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    return ToolResult.Ok(
                        SkillFileReader.FormatGet(e),
                        durationMs: sw.ElapsedMilliseconds
                    );
                }
                case "create":
                    return FromWrite(
                        SkillWriter.CreateOrUpdate(
                            isSkill,
                            scopeResolved,
                            hasName ? name : string.Empty,
                            hasContent ? content : null,
                            pin,
                            priority,
                            enabled,
                            context,
                            dataRoot,
                            isCreate: true
                        ),
                        sw
                    );
                case "set" when !isSkill:
                {
                    // need22-P0 S3：memory 存量 set 别名 = upsert（存在则 update，不存在则 create；与 validator 对齐）
                    bool exists =
                        !string.IsNullOrWhiteSpace(name)
                        && SkillFileReader.GetEntry(false, scopeResolved, name, context, dataRoot)
                            != null;
                    return FromWrite(
                        SkillWriter.CreateOrUpdate(
                            false,
                            scopeResolved,
                            hasName ? name : string.Empty,
                            hasContent ? content : null,
                            pin,
                            priority,
                            enabled,
                            context,
                            dataRoot,
                            isCreate: !exists
                        ),
                        sw
                    );
                }
                case "update":
                    return FromWrite(
                        SkillWriter.CreateOrUpdate(
                            isSkill,
                            scopeResolved,
                            hasName ? name : string.Empty,
                            hasContent ? content : null,
                            pin,
                            priority,
                            enabled,
                            context,
                            dataRoot,
                            isCreate: false
                        ),
                        sw
                    );
                case "delete":
                {
                    if (!hasName || string.IsNullOrWhiteSpace(name))
                        return ToolResult.Fail(
                            $"delete 需要 name。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    return FromWrite(
                        SkillWriter.Delete(isSkill, scopeResolved, name, context, dataRoot),
                        sw
                    );
                }
                case "enable":
                case "disable":
                {
                    if (!hasName || string.IsNullOrWhiteSpace(name))
                        return ToolResult.Fail(
                            $"{action} 需要 name。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    if (hasContent)
                        return ToolResult.Fail(
                            $"{action} 不接受 content（改正文请用 update）。[SKILL_VALIDATION]",
                            durationMs: sw.ElapsedMilliseconds
                        );
                    return FromWrite(
                        SkillWriter.SetEnabled(
                            isSkill,
                            scopeResolved,
                            name,
                            action == "enable",
                            context,
                            dataRoot
                        ),
                        sw
                    );
                }
                default:
                    return ToolResult.Fail(
                        $"未知 action '{action}'（仅允许 list|get|create|update|delete|enable|disable，默认 list）。[SKILL_VALIDATION]",
                        durationMs: sw.ElapsedMilliseconds
                    );
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("操作已被取消。", durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                $"执行异常：{ex.Message}。请修正参数后重试。[SKILL_VALIDATION]",
                durationMs: sw.ElapsedMilliseconds
            );
        }
        finally
        {
            sw.Stop();
        }
    }

    private static ToolResult FromWrite(SkillWriter.WriteResult r, Stopwatch sw) =>
        r.Ok
            ? ToolResult.Ok(r.Message, durationMs: sw.ElapsedMilliseconds)
            : ToolResult.Fail(r.Message, durationMs: sw.ElapsedMilliseconds);
}
