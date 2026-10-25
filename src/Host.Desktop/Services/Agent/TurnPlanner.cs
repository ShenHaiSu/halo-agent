using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HaloAgent.Services.Agent;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

/// <summary>need21-B03: 轮次规划/提示构造。</summary>
public sealed partial class AgentOrchestrator
{
    public static string HintForTool(string functionName, string? errorCode = null)
    {
        if (!string.IsNullOrEmpty(errorCode))
        {
            switch (errorCode)
            {
                case "OLD_STRING_NOT_FOUND":
                    return " Hint: open read_file at the suggested line and copy-paste the anchor verbatim, or use edit_lines(offset,limit).";
                case "ANCHOR_AMBIGUOUS":
                    return " Hint: lengthen the anchor to include the enclosing function/class name, or use edit_lines.";
                case "NEED_CONFIRM_OVERWRITE":
                    return " Hint: retry write_file with allow_overwrite:true, or use edit_file/edit_lines for partial edits.";
                case "MISSING_LIMIT":
                    return " Hint: set limit = lines to replace (0 = insert).";
                case "ANCHOR_TOO_SHORT":
                    return " Hint: lengthen the anchor to at least 8 non-whitespace chars with 2-5 context lines, or use edit_lines(offset,limit).";
                case "EXPECTED_HASH_MISMATCH":
                    return " Hint: re-read the file with read_file to get the latest hash, then retry.";
                case "CONTENT_TOO_LARGE":
                    return " Hint: split the change into smaller edit_file/edit_lines calls.";
                // NEED17-06 §6.3 + 02 码表：新增结构码 Hint（一句英文，拼在 tool 消息尾）。
                case "MISSING_REQUIRED_PARAM":
                    return " Hint: re-issue the same call with the missing field added; do not change other fields.";
                case "TYPE_MISMATCH":
                case "ENUM_VIOLATION":
                case "CONSTRAINT_VIOLATION":
                case "UNKNOWN_ARGUMENT":
                    return " Hint: fix the named field per schema (type/enum/limit) and retry; see the received-args echo.";
                case "TOOL_NOT_ENABLED":
                    return " Hint: the tool is not in this turn's preset; use an active tool or ask for Full preset.";
                case "INVALID_JSON_ARGUMENTS":
                    return " Hint: resend with complete JSON (no truncation); keep strings escaped.";
                // S3 §4.1：view_image 四态错误码 Hint（英文一句，模型可判下一步动作）。
                case "IMAGE_TOO_LARGE":
                    return " Hint: the image exceeds the per-image limit; compress/resize it or pick a smaller file, then retry.";
                case "NOT_AN_IMAGE":
                    return " Hint: only jpg/png/webp are readable (svg/gif/bmp rejected); convert the file first or pick another image.";
                case "IMAGE_MISSING":
                    return " Hint: verify the path with list_dir, then retry with the exact file name.";
                case "PATH_TRAVERSAL":
                    return " Hint: use a path inside the workspace root (no ../, no system dirs, no index files like _index.json).";
                // NEED17-07 §7.1：全仓库唯一中文 Hint（逐字）。07 快照/Gate 落子后保持文案不变。
                case "PYTHON_NOT_AVAILABLE":
                    return " Hint: 本机无 Python，请用 runner:'cmd' 继续；需要 Python 请按上方指引安装后重启 HaloAgent。";
                case "OUT_OF_RANGE":
                case "NO_CHANGE":
                case "EMPTY_NEW_TEXT":
                case "REGEX_ZERO_LENGTH":
                    break;
            }
        }

        return functionName switch
        {
            "edit_file"
                => " Hint: The failure payload lists the nearest line numbers; call read_file on the suggested line, fix the anchor whitespace, or use edit_lines(offset, limit) to edit by line number.",
            "edit_lines"
                => " Hint: Check offset/limit against read_file's 1-based line numbers, and pass expected_hash when retrying to avoid overwriting concurrent changes.",
            "write_file"
                => " Hint: Verify the parent directory exists (list_dir) and check path spelling before retrying.",
            "read_file"
                => " Hint: Use list_dir to confirm the file exists and check relative path spelling.",
            "exec_command"
                => " Hint: Prefer runner cmd with standard cmd.exe syntax; avoid PowerShell-only syntax.",
            "list_dir" => " Hint: Verify the directory path is relative to the workspace root.",
            "grep_search"
                => " Hint: Check the search pattern and target directory, then retry with a simpler pattern.",
            _ => " Hint: Check tool arguments and retry."
        };
    }

    /// <summary>need15 P2-3：失败负载直接给出下一步调用草稿（纯文本，不自动执行，≤300 字符）。</summary>
    public static string BuildNextActionHint(
        ToolResult toolResult,
        string functionName,
        string? argumentsJson,
        string? errorCode = null
    )
    {
        string code =
            errorCode
            ?? toolResult.ErrorCode
            ?? ToolResult.TryExtractCode(toolResult.Error)
            ?? string.Empty;
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        try
        {
            if (code is "OLD_STRING_NOT_FOUND" or "ANCHOR_AMBIGUOUS")
            {
                string payload = toolResult.ErrorPayloadJson ?? toolResult.Error ?? string.Empty;
                int bestLine = 1;
                int needleLines = 1;
                string path = functionName;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(ExtractJsonObject(payload));
                    var root = doc.RootElement;
                    if (
                        root.TryGetProperty("nearest", out var nearest)
                        && nearest.ValueKind == System.Text.Json.JsonValueKind.Array
                        && nearest.GetArrayLength() > 0
                    )
                    {
                        bestLine =
                            nearest[0].TryGetProperty("line", out var l)
                            && l.TryGetInt32(out int lv)
                                ? lv
                                : 1;
                    }

                    if (
                        root.TryGetProperty("old_string_lines", out var nl)
                        && nl.TryGetInt32(out int nlv)
                    )
                    {
                        needleLines = Math.Max(1, nlv);
                    }

                    if (
                        root.TryGetProperty("file", out var f)
                        && f.ValueKind == System.Text.Json.JsonValueKind.String
                    )
                    {
                        path = f.GetString() ?? path;
                    }
                }
                catch { }

                // 回退：从原始参数里取 file_path
                if (
                    (path == functionName || string.IsNullOrWhiteSpace(path))
                    && !string.IsNullOrWhiteSpace(argumentsJson)
                )
                {
                    try
                    {
                        using var adoc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                        if (
                            adoc.RootElement.TryGetProperty("file_path", out var fp)
                            && fp.ValueKind == System.Text.Json.JsonValueKind.String
                        )
                        {
                            path = fp.GetString() ?? path;
                        }
                    }
                    catch { }
                }

                string draft =
                    "{\"file_path\":\""
                    + path.Replace("\\", "/").Replace("\"", "\\\"")
                    + "\",\"offset\":"
                    + bestLine
                    + ",\"limit\":"
                    + needleLines
                    + ",\"new_text\":\"<paste full replacement here>\"}";
                string hint = "\nNextAction (copy-paste and adjust): edit_lines(" + draft + ")";
                return hint.Length > 400 ? hint.Substring(0, 400) : hint;
            }

            // NEED17-06 §6.3：MISSING_REQUIRED_PARAM 最小补全草稿（{缺失字段:"<fill>"} ≤300 字符，纯文本不执行）。
            if (code is "MISSING_REQUIRED_PARAM")
            {
                string draft = BuildMissingFieldDraft(toolResult, argumentsJson);
                if (!string.IsNullOrEmpty(draft))
                    return "\nNextAction (fill and resend): " + draft;
            }

            if (code is "NEED_CONFIRM_OVERWRITE")
            {
                return "\nNextAction: retry the SAME write_file call with \"allow_overwrite\":true added.";
            }

            if (code is "MISSING_LIMIT")
            {
                return "\nNextAction: retry edit_lines with explicit \"limit\" (replace N lines, or 0 to insert).";
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>need17-06 §6.3：缺失字段补全草稿（{缺失字段:"<fill>"}，≤300 字符，纯文本不执行）。字段来源：payload missing[]/field → Error 引号 → 通用占位。</summary>

    private async Task<(PhaseOutcome Outcome, RequestBatch? Batch)> BuildRequestAsync(
        TurnContext ctx,
        int iteration,
        string workspaceId,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
            // need12-P0：循环入口即取消，保住前面 iteration 已落袋行。
            UsageRecorder.Flush(_usageMetrics, ctx.UsageRows, sessionId, workspaceId);
            return (
                PhaseOutcome.End(AgentTurnResult.Cancelled(iteration - 1, ctx.ToolExecutions)),
                null
            );
        }
        List<UnifiedChatMessage> history;
        try
        {
            history = await _sessionService
                .GetSessionMessagesAsync(workspaceId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            // FIX(tool-400)：同轮内上次迭代可能留下孤儿（取消/截断死在 assistant→tool 之间），本轮发包前内存愈合，不写盘。
            try
            {
                var (healed, _, _) = WorkspaceSessionService.RebuildHealedHistory(history);
                history = healed;
            }
            catch
            { /* 愈合失败则用原历史，适配器发包前仍有守卫 */
            }
        }
        catch (OperationCanceledException)
        {
            PublishTurnEvent(sessionId, "TurnCancelled", new { iteration });
            return (
                PhaseOutcome.End(AgentTurnResult.Cancelled(iteration - 1, ctx.ToolExecutions)),
                null
            );
        }
        // need22-B02：user 文本里的 [[skill:scope/name]] 在发包前就地展开为 readme 正文全文；
        // 落盘历史保持 marker 原文（历史可读），展开只发生在内存里的发包列表。
        // 顺序：必须在 TruncateToBudget 之前 ⇒ 上下文预算按展开后的真实体积裁剪。
        try
        {
            history = ExpandSkillMarkers(history, ctx.Workspace?.RootPath);
        }
        catch (Exception ex)
        {
            Infrastructure.Logging.HaloLogger.App.Warn("[SkillActivation] 展开阶段异常，已降级为原文发送", ex);
        }
        // need05-02：budgeted 已是稳定前缀（平时全量、超 92% 高水位才块裁）；requestMessages 顺序即 [system.stable+ephemeral]+历史，不再调序。
        // need09-02：常驻 L0 计入 usedInput 分子（自然推高水位）。
        List<UnifiedChatMessage> budgeted = _budgetService.TruncateToBudget(
            ctx.Model,
            history,
            1024,
            ctx.ResidentStableTokens
        );

        var requestMessages = new List<UnifiedChatMessage>(budgeted.Count + 1);
        if (!string.IsNullOrWhiteSpace(ctx.SystemPrompt))
            requestMessages.Add(UnifiedChatMessage.System(ctx.SystemPrompt));
        requestMessages.AddRange(budgeted);
        // S3 §4.4：非视觉剥离告知（发包内存副本，不落盘）。
        // 落盘的 assistant 通知保留（人类可见）；这里另给模型一句，避免「模型以为本轮有图」而幻觉。
        string? nonVisionNotice = null;
        // need09-03：非视觉模型含图 → 剥离图片 + 通知 + 纯文本降级（仅一次；历史落盘不受影响，切换模型可恢复）
        if (!ctx.SnapVision && requestMessages.Any(m => m.Images != null && m.Images.Count > 0))
        {
            // 就地剥离（requestMessages 为发包基列表；模型侧另由 StripForNonVision 前置告知句）。
            nonVisionNotice = MultimodalPlanner.DropNotice.NonVisionStripped(
                ctx.Model.Id,
                requestMessages.Where(m => m.Images != null).Sum(m => m.Images!.Count)
            );
            foreach (var m in requestMessages)
                m.Images = null;
            var notice = UnifiedChatMessage.Assistant(nonVisionNotice);
            try
            {
                await _sessionService
                    .AppendSessionMessageAsync(workspaceId, sessionId, notice, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            { /* 通知落盘失败不中断本轮 */
            }
        }
        // S5 §4.3：图片三道盘按**冻结顺序**执行（顺序写死，单测锁定）：
        //   ① 历史盘（raw，逆序，保最新丢最旧） → ② 当轮盘（raw，当前 user 轮 + view_image） → ③ 张数盘（MaxImagesPerTurn）
        // 字节盘先行、张数盘最后：先按体积挤掉最旧历史，再让产品级张数护栏兜底。
        // 与 S1 的 token 账关系：BuildWithinBudget（token 侧，含 DropOldestImageRefs 文本占位）在上方先跑，
        // 这里只做字节账，两笔账方向一致（都保最新丢最旧），互不冲突。
        // 切分边界：ctx.UserMessage.Id —— 该消息及其之后为本轮（当轮盘），之前的为历史（历史盘）。
        // SessionImageStash.Take 取出的正是当前轮图，TurnSetup 已并入 ctx.UserMessage.Images 并落盘。
        string historyDropNotice = string.Empty;
        bool isCurrentTurnMsg(UnifiedChatMessage m) =>
            m != null
            && ctx.UserMessage != null
            && string.Equals(m.Id, ctx.UserMessage.Id, StringComparison.Ordinal);

        if (ctx.SnapVision)
        {
            var historyDropped = new List<(string FileName, string ImageId)>();
            // ① 历史盘：逆序 raw 扣减；未准入的 Images 从发包副本摘除（落盘不动）
            {
                var historyRefs = new List<ChatImageRef>();
                foreach (var m in requestMessages)
                {
                    if (isCurrentTurnMsg(m) || m.Images == null)
                        continue;
                    foreach (var img in m.Images)
                    {
                        if (img != null && !string.IsNullOrWhiteSpace(img.ImageId))
                            historyRefs.Add(img);
                    }
                }
                if (historyRefs.Count > 0)
                {
                    var seq = historyRefs.Select(r => (r.ImageId.Trim(), r.SizeBytes)).ToList();
                    var (admit, droppedIds) = ImageBudgetPlanner.PlanReverse(
                        seq,
                        ImageBudgetPlanner.ClampHistoryCap(VisionDefaults.MaxHistoryImageBytes)
                    );
                    if (droppedIds.Count > 0)
                    {
                        foreach (var id in droppedIds)
                        {
                            var hit = historyRefs.FirstOrDefault(r =>
                                string.Equals(r.ImageId.Trim(), id, StringComparison.Ordinal)
                            );
                            historyDropped.Add((hit?.FileName ?? string.Empty, id));
                        }
                        var stripped = ImageBudgetPlanner.StripDropped(requestMessages, admit);
                        requestMessages.Clear();
                        requestMessages.AddRange(stripped);
                        historyDropNotice = MultimodalPlanner.DropNotice.HistoryBudgetExceeded(
                            VisionDefaults.MaxHistoryImageBytes,
                            historyDropped
                        );
                        Infrastructure.Logging.HaloLogger.App.Warn(
                            $"[Image] 历史盘超预算（{VisionDefaults.MaxHistoryImageBytes} raw），已丢弃最旧 {droppedIds.Count} 张（发包副本摘除，落盘保留）"
                        );
                    }
                }
            }

            // ③ 张数盘：最后执行（保最新丢最旧）；② 当轮盘在下方 imgResolver 内按 raw 扣减
            if (ctx.SnapMaxImages > 0)
            {
                int totalImgs = requestMessages
                    .Where(m => m.Images != null)
                    .Sum(m => m.Images!.Count);
                if (totalImgs > ctx.SnapMaxImages)
                {
                    var keepSet = new HashSet<ChatImageRef>();
                    for (
                        int ri = requestMessages.Count - 1;
                        ri >= 0 && keepSet.Count < ctx.SnapMaxImages;
                        ri--
                    )
                    {
                        var imgs = requestMessages[ri].Images;
                        if (imgs == null)
                            continue;
                        for (
                            int ki = imgs.Count - 1;
                            ki >= 0 && keepSet.Count < ctx.SnapMaxImages;
                            ki--
                        )
                            keepSet.Add(imgs[ki]);
                    }
                    foreach (var m in requestMessages)
                    {
                        if (m.Images == null)
                            continue;
                        var kept = m.Images.Where(r => keepSet.Contains(r)).ToList();
                        m.Images = kept.Count == 0 ? null : kept;
                    }
                    Infrastructure.Logging.HaloLogger.App.Warn(
                        $"[Image] 本轮图片 {totalImgs} 张超限（上限 {ctx.SnapMaxImages}），已保留最新 {ctx.SnapMaxImages} 张"
                    );
                }
            }
        }
        string requestId = Guid.NewGuid().ToString("N");
        string sessionHash = !string.IsNullOrWhiteSpace(ctx.Session!.SessionHash)
            ? ctx.Session!.SessionHash
            : HeaderVariableResolver.ComputeSessionHash(workspaceId, sessionId);

        var headerContext = new HeaderResolutionContext(
            ctx.Workspace!.Id,
            ctx.Workspace!.Name ?? string.Empty,
            ctx.Workspace!.RootPath ?? string.Empty,
            sessionId,
            sessionHash,
            ctx.Provider.Id,
            ctx.Provider.Name,
            ctx.Model.Id,
            requestId
        );

        // need17-01/联调：声明走本轮冻结集（与执行同源；用户 preset 生效，不再是写死的 Coding）。
        var toolDefs = _toolRegistry
            .GetActiveDefinitions(ctx.TurnActiveSet)
            .Select(d => UnifiedToolDefinition.Create(d.Name, d.Description, d.ParametersJson))
            .ToList();
        // need11-01：读盘→dataURL 本轮内存缓存（重启/多会话安全，无全局锁；null 也缓存避免重复读丢失文件）
        // S5 §4.3 ②：当轮盘（raw 口径）——当前 user 轮 + 本 turn view_image 工具图共享，
        // 上限 VisionDefaults.MaxTurnImageBytes（32M 十进制 raw）；历史图不占此盘（历史已由①按历史盘过滤）。
        // 扣减口径从 url.Length（含 base64，约 4/3 膨胀）改为 img.SizeBytes（raw），与上层历史盘同口径。
        // dataUrl 仍缓存（避免重复读盘），只是预算按 raw 计。
        var imgCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        long imgUsedBytes = 0;
        const long ImgBudgetBytes = VisionDefaults.MaxTurnImageBytes;
        // S3 §4.2：熔断显式化——被预算跳过的图收集 (fileName, imageId)，发包前告知模型。
        // 只收集不改变 resolver 语义（仍返回 null，adapter 的 Where(!IsNullOrWhiteSpace(DataUrl)) 过滤不动）。
        var skippedKeys = new List<(string FileName, string ImageId)>();
        Func<ChatImageRef, string?> imgResolver = img =>
        {
            if (img == null || string.IsNullOrWhiteSpace(img.ImageId))
                return null;
            string key = img.ImageId.Trim();
            if (imgCache.TryGetValue(key, out var hit))
                return hit;
            long rawSize = img.SizeBytes > 0 ? img.SizeBytes : 0;
            if (imgUsedBytes + rawSize > ImgBudgetBytes)
            {
                // 当轮盘超限：不再读盘（省 LOH），直接判未发送并进入告知
                Infrastructure.Logging.HaloLogger.App.Warn(
                    $"[Image] 当轮图片盘超预算（{ImgBudgetBytes} raw），已跳过 {key}（raw={rawSize}，前端可经 GetImageThumbnail 懒取）"
                );
                imgCache[key] = null;
                skippedKeys.Add((img.FileName ?? string.Empty, key));
                return null;
            }
            string? url = ImageAttachmentStore.ResolveDataUrl(
                ctx.DataRoot,
                workspaceId,
                sessionId,
                img
            );
            if (url != null)
                imgUsedBytes += rawSize;
            imgCache[key] = url;
            return url;
        };

        // S3 §4.2/§4.4：组装发包消息副本（requestMessages 原对象落盘语义不动）。
        // 顺序：剥离告知前置首条内容 → 熔断告知追加末条；两者都只进内存副本。
        // S5 §4.4：告知文案区分两盘——当轮盘「本轮图片字节超预算」/ 历史盘「历史图片超预算未发送 N 张」。
        string budgetNotice = MultimodalPlanner.DropNotice.BudgetExceeded(
            ImgBudgetBytes,
            skippedKeys
        );
        if (historyDropNotice.Length > 0)
            budgetNotice =
                budgetNotice.Length > 0
                    ? historyDropNotice + "\n" + budgetNotice
                    : historyDropNotice;
        List<UnifiedChatMessage> sendMessages = requestMessages;
        if (!string.IsNullOrEmpty(nonVisionNotice) || budgetNotice.Length > 0)
        {
            sendMessages = new List<UnifiedChatMessage>(requestMessages.Count);
            for (int i = 0; i < requestMessages.Count; i++)
            {
                var src = requestMessages[i];
                bool isFirst = i == 0;
                bool isLast = i == requestMessages.Count - 1;
                var copy = src;
                if (isFirst && !string.IsNullOrEmpty(nonVisionNotice))
                {
                    copy = MultimodalPlanner.DropNotice.WithNotice(
                        copy,
                        nonVisionNotice,
                        append: false
                    );
                }
                if (isLast && budgetNotice.Length > 0)
                {
                    copy = MultimodalPlanner.DropNotice.WithNotice(
                        copy,
                        budgetNotice,
                        append: true
                    );
                }
                sendMessages.Add(copy);
            }
        }
        var request = new UnifiedLlmRequest
        {
            RequestId = requestId,
            Provider = ctx.Provider,
            Model = ctx.Model,
            HeaderContext = headerContext,
            SystemInstructions = ctx.SystemPrompt,
            Messages = sendMessages,
            Tools = toolDefs,
            ImageResolver = imgResolver,
        };
        ctx.RequestMessages = requestMessages;
        ctx.RequestId = requestId;
        return (PhaseOutcome.Continue(), new RequestBatch { Request = request });
    }

    /// <summary>
    /// need22-B02：把 user 消息里的技能标记就地展开为 readme 正文全文（仅内存，不落盘）。
    /// 只处理 user 消息（assistant/tool 文本里的 marker 不展开）；拷贝消息而非原地改，
    /// 显式复制 init 只读属性 Id / CreatedAtUtc（图片引用共享，不深拷贝）。
    /// </summary>
    private List<UnifiedChatMessage> ExpandSkillMarkers(
        List<UnifiedChatMessage> history,
        string? workspaceRoot
    )
    {
        if (history.Count == 0)
            return history;
        var expander = new SkillActivationExpander(
            (scope, name) => ResolveSkillBody(scope, name, workspaceRoot)
        );
        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg == null)
                continue;
            if (!string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!SkillActivationExpander.HasMarker(msg.Content))
                continue;

            string expanded = expander.Expand(msg.Content, workspaceRoot);
            if (string.Equals(expanded, msg.Content, StringComparison.Ordinal))
                continue;

            // need22-B02：拷贝消息，不改原对象（history 里其它逻辑可能复用同一实例）
            var copy = new UnifiedChatMessage
            {
                Id = msg.Id,
                Role = msg.Role,
                Content = expanded,
                ReasoningContent = msg.ReasoningContent,
                ToolCalls = msg.ToolCalls,
                ToolCallId = msg.ToolCallId,
                Name = msg.Name,
                Images = msg.Images,
                CreatedAtUtc = msg.CreatedAtUtc,
            };
            history[i] = copy;
        }
        return history;
    }

    /// <summary>
    /// need22-B02：取技能正文（BodyMarkdown = 去掉 frontmatter 后的 body）。
    /// 只经 SkillMemoryService.Get 取数（名字合法性/目录逃逸/readme 缺失/正文超限均已在服务内校验）；
    /// 已停用（Enabled=false）视为不可读；任何异常都吞掉并 Warn ⇒ 轮次不中断。
    /// </summary>
    private string? ResolveSkillBody(string scope, string name, string? workspaceRoot)
    {
        try
        {
            var svc = _skillMemoryService;
            if (svc == null)
            {
                if (_storageManager == null)
                    return null;
                svc = _fallbackSkillMemoryService;
                if (svc == null)
                {
                    svc = new SkillMemoryService(_storageManager);
                    _fallbackSkillMemoryService = svc;
                }
            }
            var sc = string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)
                ? SkillMemoryScope.Project
                : SkillMemoryScope.Global;
            var entry = svc.Get(sc, SkillMemoryKind.Skill, name, workspaceRoot);
            if (entry == null || !entry.Enabled)
                return null; // 不存在或已停用 ⇒ 不展开
            return entry.BodyMarkdown;
        }
        catch (Exception ex)
        {
            Infrastructure.Logging.HaloLogger.App.Warn(
                $"[SkillActivation] 读取技能失败：{scope}/{name}",
                ex
            );
            return null;
        }
    }

    private async Task<(PhaseOutcome Outcome, bool ProceedToDispatch)> DecideAsync(
        TurnContext ctx,
        int iteration,
        string workspaceId,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        // (6) 无 ToolCalls（含空名调用已单独处理）则 TurnCompleted 结束
        if (ctx.CompletedCalls.Count == 0 && ctx.EmptyNameCids.Count == 0)
        {
            // need13/T7：主 TurnCompleted 回填真实子耗 rollup（Barrier 累加，非 0；供顶栏“本轮含子耗”）
            PublishTurnEvent(
                sessionId,
                "TurnCompleted",
                new
                {
                    iterations = iteration,
                    ctx.ToolExecutions,
                    protocol = ctx.Model.Protocol == LlmProtocolType.Resp ? "resp" : "chat",
                    inputTokens = ctx.CurrentUsage?.InputTokens ?? 0,
                    outputTokens = ctx.CurrentUsage?.OutputTokens ?? 0,
                    cachedTokens = ctx.CurrentUsage?.CachedInputTokens ?? 0,
                    billedPrompt = ctx.TurnBilledIn,
                    billedCompletion = ctx.TurnBilledOut,
                    subAgents = new
                    {
                        count = ctx.SubTotalCount,
                        billedPrompt = ctx.SubBilledIn,
                        billedCompletion = ctx.SubBilledOut
                    },
                    updatedAtUtc = DateTime.UtcNow.ToString("o")
                }
            );
            FlushUsageRows(ctx.UsageRows, sessionId, workspaceId);
            return (
                PhaseOutcome.End(AgentTurnResult.Completed(iteration, ctx.ToolExecutions)),
                false
            );
        }

        // 达到最大迭代仍有工具调用 → 截断暂停（need08：无限模式 ctx.EffectiveMax<=0 永不进）
        if (ctx.EffectiveMax > 0 && iteration >= ctx.EffectiveMax)
        {
            try
            {
                var notice = UnifiedChatMessage.Assistant(
                    $"已达到单轮最大迭代次数（{ctx.EffectiveMax}），已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。"
                );
                await _sessionService
                    .AppendSessionMessageAsync(
                        workspaceId,
                        sessionId,
                        notice,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch
            { /* ignore */
            }
            PublishTurnEvent(
                sessionId,
                "TurnTruncated",
                new
                {
                    iterations = iteration,
                    ctx.ToolExecutions,
                    maxIterations = ctx.EffectiveMax
                }
            );
            FlushUsageRows(ctx.UsageRows, sessionId, workspaceId);
            return (
                PhaseOutcome.End(AgentTurnResult.Truncated(iteration, ctx.ToolExecutions)),
                false
            );
        }

        // NEED17-06 §6.4：截断回复保护（只加保护，不加并行；该轮不计 ctx.ToolExecutions）
        if (ctx.StreamTruncated && ctx.CompletedCalls.Count > 0)
        {
            foreach (var call in ctx.CompletedCalls)
            {
                string discarded =
                    $"[Tool Discarded - {call.FunctionName}]: 调用未执行：回复因输出长度被截断，参数可能不完整。请用完整参数重发（可缩小单次改动）。";
                await AppendToolTextAsync(
                        workspaceId,
                        sessionId,
                        call.CallId,
                        call.FunctionName,
                        discarded,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                PublishTurnEvent(
                    sessionId,
                    "ToolExecutionCompleted",
                    new
                    {
                        iteration,
                        callId = call.CallId,
                        functionName = call.FunctionName,
                        success = false,
                        exitCode = -1,
                        durationMs = 0L,
                        argsJson = call.ArgumentsJson,
                        resultText = TruncateForEvent(discarded, 8000),
                        updatedAtUtc = DateTime.UtcNow.ToString("o")
                    }
                );
            }
            return (PhaseOutcome.Continue(), false);
        }
        return (PhaseOutcome.Continue(), true);
    }
}
