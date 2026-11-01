namespace HaloAgent.Engine.Core.Agent;

/// <summary>
/// 把 OpenAI 格式的 ChatMessage 投影成渲染友好的 MessageView。
/// 核心工作：工具调用与结果合并（assistant + tool → 一张工具卡片）。
/// P15：新增 ProjectTurns（turnId + ord 分组 → TurnView）。
/// </summary>
public static class MessageProjector
{
    /// <summary>P15-D8 首屏头部长度。</summary>
    public const int ParamHead = 2000;

    /// <summary>把完整消息列表投影成渲染列表（合并工具调用与结果）。</summary>
    public static List<HaloAgent.Engine.Abstractions.Types.MessageView> Project(
        IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages)
    {
        var result = new List<HaloAgent.Engine.Abstractions.Types.MessageView>(messages.Count);
        var toolResults = new Dictionary<string, (bool ok, string output)>(StringComparer.Ordinal);
        var toolFullOutputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var m in messages)
        {
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.Tool && m.ToolCallId is not null)
            {
                var output = m.Content ?? string.Empty;
                var ok = !output.StartsWith("⏹", StringComparison.Ordinal)
                         && !output.StartsWith("操作未执行", StringComparison.Ordinal)
                         && !output.StartsWith("工具参数解析失败", StringComparison.Ordinal)
                         && !output.Contains("执行失败:", StringComparison.Ordinal);
                toolResults[m.ToolCallId] = (ok, output);
                toolFullOutputs[m.ToolCallId] = output;
            }
        }

        foreach (var m in messages)
        {
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.System) continue;
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.Tool) continue;

            var view = new HaloAgent.Engine.Abstractions.Types.MessageView
            {
                Id = "m-" + Guid.NewGuid().ToString("N")[..8],
                Role = m.RoleWire,
                Text = m.ContentParts is { Count: > 0 } ? string.Join("\n", m.ContentParts.Where(p => p.Type == "text").Select(p => p.Text ?? "")) : (m.Content ?? string.Empty),
                Reasoning = m.ReasoningContent,
                ReasoningKind = m.ReasoningContent is null ? "full" : "summary",
                HasImages = m.HasImages,
                AttachmentCount = m.ContentParts is null ? 0 : m.ContentParts.Count(p => p.Type == "image_url"),
                TsMs = m.TsMs,
                Ord = m.Ord,
            };


            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new List<HaloAgent.Engine.Abstractions.Types.ToolCallView>(m.ToolCalls.Count);
                foreach (var tc in m.ToolCalls)
                {
                    toolResults.TryGetValue(tc.Id, out var res);
                    var (kind, rawType, args) = HaloAgent.Engine.Core.Llm.ResponsesTranslators.UnpackUnknownArgs(tc.Function.Arguments);
                    var fullArgs = args ?? "";
                    var hasRes = toolResults.ContainsKey(tc.Id);
                    var fullOut = hasRes ? res.output : null;
                    calls.Add(new HaloAgent.Engine.Abstractions.Types.ToolCallView
                    {
                        Id = tc.Id,
                        Name = kind == "unknown" ? (rawType ?? tc.Function.Name) : tc.Function.Name,
                        Args = TruncateHead(fullArgs, ParamHead),
                        Ok = hasRes ? res.ok : null,
                        Output = fullOut is null ? null : TruncateHead(fullOut, ParamHead),
                        Kind = kind,
                        RawType = rawType,
                        Payload = null,
                        ArgsTotal = fullArgs.Length,
                        OutputTotal = fullOut?.Length ?? 0,
                        Truncated = fullArgs.Length > ParamHead || (fullOut?.Length ?? 0) > ParamHead,
                    });
                }
                view = view with { ToolCalls = calls };
            }

            result.Add(view);
        }

        return result;
    }

    /// <summary>快照投影：语义同 Project（合并 + 跳过 system/tool 独立消息 + ok 判定复用），另加稳定 Id/TsMs。</summary>
    /// <remarks>
    /// P08-fix 01：快照类输出（snapshot/resume/resync/compact.kept）统一走这里；
    /// ToView 降级为增量场景专用，不再用于快照。
    /// - 稳定 Id：m-&lt;序号&gt;（1 起，替代随机，保证折叠/锚点/重试查找稳定；toolCallId 本就不动）。
    /// - TsMs：按序号递增（存储无 ts 时的最小方案，以当前时间倒推，步长 1s，保证有序且非 0）。
    /// </remarks>
    public static List<HaloAgent.Engine.Abstractions.Types.MessageView> ProjectSnapshot(
        IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages)
    {
        var merged = Project(messages);
        if (merged.Count == 0) return merged;
        // P14-D5：全真且单调 → 直接透传真值，只稳定 Id；否则降级走现存 UtcNow 倒推（纯旧文件兼容）。
        if (merged.All(v => v.TsMs > 0) && IsMonotonic(merged))
        {
            for (var i = 0; i < merged.Count; i++)
                merged[i] = merged[i] with { Id = "m-" + (i + 1) };
            return merged;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseTs = now - (long)merged.Count * 1000L;
        if (baseTs < 0) baseTs = 0;
        for (var i = 0; i < merged.Count; i++)
        {
            merged[i] = merged[i] with
            {
                Id = "m-" + (i + 1),
                TsMs = baseTs + (long)(i + 1) * 1000L,
            };
        }
        return merged;
    }

    private static bool IsMonotonic(IReadOnlyList<HaloAgent.Engine.Abstractions.Types.MessageView> views)
    {
        for (var i = 1; i < views.Count; i++)
            if (views[i].TsMs < views[i - 1].TsMs) return false;
        return true;
    }

    /// <summary>单条投影（不含工具合并，用于增量场景）。P14：透传真值。</summary>
    public static HaloAgent.Engine.Abstractions.Types.MessageView ToView(
        HaloAgent.Engine.Core.Llm.ChatMessage m) => new()
    {
        Id = "m-" + Guid.NewGuid().ToString("N")[..8],
        Role = m.RoleWire,
        Text = m.ContentParts is { Count: > 0 } ? string.Join("\n", m.ContentParts.Where(p => p.Type == "text").Select(p => p.Text ?? "")) : (m.Content ?? string.Empty),
        Reasoning = m.ReasoningContent,
        HasImages = m.HasImages,
        AttachmentCount = m.ContentParts is null ? 0 : m.ContentParts.Count(p => p.Type == "image_url"),
        TsMs = m.TsMs,
        Ord = m.Ord,
    };

    private static string TruncateHead(string s, int max) =>
        s.Length <= max ? s : s[..max];

    /// <summary>
    /// P15：按 (turnId, ord) 分组投影为 TurnView 列表。
    /// - 有 TurnId：同 TurnId 聚合；无 TurnId（旧数据）：按 user 切回合。
    /// - 组内：首条 user → User；后续 user → Injected；assistant 按 ord 排序，
    ///   末条进 Final，其余进 Process；system/tool 孤儿丢弃。
    /// - Final 必有 assistant 占位（空回合亦合成），无 null。
    /// </summary>
    public static List<HaloAgent.Engine.Abstractions.Types.TurnView> ProjectTurns(
        IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages)
    {
        var flat = Project(messages);
        // 需要 ord 信息：重建 ord 映射（Project 已带 Ord）
        // 按原始顺序分组：以 user 为回合边界（新数据 TurnId 一致，旧数据 likewise）
        var turns = new List<HaloAgent.Engine.Abstractions.Types.TurnView>();
        HaloAgent.Engine.Abstractions.Types.MessageView? curUser = null;
        var curInjected = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
        var curAssistants = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
        string? curTurnId = null;
        var turnSeq = 0;

        void FlushTurn()
        {
            if (curUser is null && curAssistants.Count == 0 && curInjected.Count == 0) return;
            turnSeq++;
            var tid = curTurnId ?? ("t-hist-" + turnSeq);
            HaloAgent.Engine.Abstractions.Types.MessageView final;
            List<HaloAgent.Engine.Abstractions.Types.MessageView> process;
            if (curAssistants.Count == 0)
            {
                final = new HaloAgent.Engine.Abstractions.Types.MessageView
                {
                    Id = "m-" + tid + "-final",
                    Role = "assistant",
                    Text = "",
                    TsMs = curUser?.TsMs ?? 0,
                    Ord = 0,
                };
                process = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
            }
            else
            {
                // 按 Ord 排序（稳定），末条为 Final
                curAssistants.Sort((a, b) => a.Ord.CompareTo(b.Ord));
                final = curAssistants[^1];
                process = curAssistants.Take(curAssistants.Count - 1).ToList();
            }
            turns.Add(new HaloAgent.Engine.Abstractions.Types.TurnView
            {
                TurnId = tid,
                User = curUser ?? new HaloAgent.Engine.Abstractions.Types.MessageView { Id = "m-" + tid + "-user", Role = "user", Text = "", TsMs = 0, Ord = 0 },
                Injected = curInjected.ToList(),
                Process = process,
                Final = final,
            });
            curUser = null;
            curInjected = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
            curAssistants = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
            curTurnId = null;
        }

        // 取 TurnId：从原始 ChatMessage 按顺序映射。flat 与 messages（过滤后）顺序一致但 system/tool 被跳过，
        // 故用队列按序消费非 system/tool 的 TurnId。
        var turnIdQueue = new Queue<string?>();
        foreach (var m in messages)
        {
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.System) continue;
            if (m.Role == HaloAgent.Engine.Core.Llm.Role.Tool) continue;
            turnIdQueue.Enqueue(m.TurnId);
        }

        foreach (var v in flat)
        {
            var tid = turnIdQueue.Count > 0 ? turnIdQueue.Dequeue() : null;
            if (v.Role == "user")
            {
                if (curUser is null)
                {
                    curUser = v;
                    curTurnId = tid;
                }
                else if (tid is not null && curTurnId is not null && string.Equals(tid, curTurnId, StringComparison.Ordinal))
                {
                    // 同回合后续 user → Injected
                    curInjected.Add(v with { Injected = true });
                }
                else
                {
                    // 新回合开始
                    FlushTurn();
                    curUser = v;
                    curTurnId = tid;
                    turnSeq = turns.Count; // 保持 t-hist 连续（FlushTurn 内 ++）
                }
            }
            else if (v.Role == "assistant")
            {
                if (curUser is null)
                {
                    // 孤儿 assistant（无 user 先行）：合成空 user 开新回合
                    curUser = new HaloAgent.Engine.Abstractions.Types.MessageView { Id = v.Id + "-u", Role = "user", Text = "", TsMs = v.TsMs, Ord = 0 };
                    curTurnId = tid;
                }
                curAssistants.Add(v);
            }
            else
            {
                // system/tool 孤儿丢弃（flat 已无 system/tool，此分支仅防御）
            }
        }
        FlushTurn();

        // 稳定 Id：按回合顺序重排 m-<n>（保证折叠/锚点稳定）
        var seq = 0;
        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            seq++;
            var user = t.User with { Id = "m-" + seq };
            seq++;
            var inj = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
            foreach (var u in t.Injected) { seq++; inj.Add(u with { Id = "m-" + seq }); }
            var proc = new List<HaloAgent.Engine.Abstractions.Types.MessageView>();
            foreach (var p in t.Process) { seq++; proc.Add(p with { Id = "m-" + seq }); }
            seq++;
            var fin = t.Final with { Id = "m-" + seq };
            turns[i] = t with { User = user, Injected = inj, Process = proc, Final = fin };
        }
        return turns;
    }

    /// <summary>P15：快照用 ProjectTurns（与 ProjectSnapshot 同源，稳定 Id）。</summary>
    public static List<HaloAgent.Engine.Abstractions.Types.TurnView> ProjectSnapshotTurns(
        IReadOnlyList<HaloAgent.Engine.Core.Llm.ChatMessage> messages)
        => ProjectTurns(messages);
}
