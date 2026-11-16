using System.Text;
using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>
/// need17-02：中央校验管线（纯函数 + 注册表驱动，无 IO）。
/// 四步（冻结顺序）：Parse → Shim → Required（含 ConditionalRequired）→ Type/Enum/约束 + 未知字段。
/// 未知工具（不在注册表）一律拒绝 UNKNOWN_TOOL（need22-P0 S3；透传分支已删除）。
/// </summary>
public static partial class ToolCallValidator
{
    /// <summary>need17-02：错误消息内 JSON 摘要上限（超 500 字符截断 + ...）。</summary>
    public const int SummaryMaxChars = 500;

    /// <summary>need17-02：错误码附录（冻结；PYTHON_NOT_AVAILABLE 由 07 专项在 exec_command 内抛，此处仅预留注释）。</summary>
    public static class Codes
    {
        public const string InvalidJsonArguments = "INVALID_JSON_ARGUMENTS";
        public const string MissingRequiredParam = "MISSING_REQUIRED_PARAM";
        public const string TypeMismatch = "TYPE_MISMATCH";
        public const string EnumViolation = "ENUM_VIOLATION";
        public const string ConstraintViolation = "CONSTRAINT_VIOLATION";
        public const string UnknownArgument = "UNKNOWN_ARGUMENT";
        public const string UnknownTool = "UNKNOWN_TOOL";
        // need17-07 预留：PYTHON_NOT_AVAILABLE（exec_command 业务层抛，见 07 专项）。
        // need22-P0 S2/S1 新增（只增不改协议）：COMMAND_NOT_ALLOWED / COMMAND_TOO_LONG /
        // PATH_OUTSIDE_WORKSPACE / INVALID_TIMEOUT / INVALID_CALL_ID / BACKUP_QUOTA_EXCEEDED /
        // COMMAND_REDIRECT_OUTSIDE 由各业务层经 FailCode 抛出。
    }

    internal static readonly Dictionary<string, string> AliasToCanonical =
        new(StringComparer.Ordinal)
        {
            ["path"] = "file_path",
            ["file"] = "file_path",
            ["filePath"] = "file_path",
            ["cmd"] = "command",
            ["timeout"] = "timeout_ms",
            ["timeoutMs"] = "timeout_ms",
            ["oldString"] = "old_string",
            ["newString"] = "new_string",
            ["newText"] = "new_text",
            ["allowOverwrite"] = "allow_overwrite",
        };

    internal static readonly string[] SafetyFieldTable =
    {
        "allow_overwrite",
        "expected_hash",
        "dry_run",
        "allow_empty",
        "limit",
        "offset",
        "old_string",
        "new_string",
        "new_text",
        "file_path",
        "content",
        "replace_all",
        "match_mode",
        "mode",
        "max_bytes",
        "regex_expand_groups",
        "normalize_eol",
        "expand_tabs",
        "trim_trailing_space",
        "ignore_leading_whitespace",
        "command",
        "runner",
        "workdir",
        "timeout_ms",
        "backup_path",
        "restore_to",
    };

    internal static readonly string[] ReadOnlyTools =
    {
        "read_file",
        "list_dir",
        "grep_search",
        "view_image"
    };

    internal static readonly string[] WriteTools =
    {
        "write_file",
        "edit_file",
        "edit_lines",
        "edit",
        "undo_edit",
        "edit_batch",
    };

    internal static int EditDistance(string a, string b)
    {
        a = (a ?? string.Empty).ToLowerInvariant();
        b = (b ?? string.Empty).ToLowerInvariant();
        int n = a.Length;
        int m = b.Length;
        if (n == 0)
            return m;
        if (m == 0)
            return n;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++)
            prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            var t = prev;
            prev = cur;
            cur = t;
        }
        return prev[m];
    }

    internal static string? FindClosestSafetyField(string unknown)
    {
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (string s in SafetyFieldTable)
        {
            int d = EditDistance(unknown, s);
            if (d < bestDist)
            {
                bestDist = d;
                best = s;
            }
        }
        return bestDist <= 2 ? best : null;
    }

    internal static bool HitsSafetySubstringGuard(string unknown)
    {
        string u = (unknown ?? string.Empty).ToLowerInvariant();
        return u.Contains("dry")
            || u.Contains("overwrite")
            || u.Contains("hash")
            || u.Contains("empty");
    }

    private static ToolParamSpec S(
        string name,
        string type = "string",
        string[]? enums = null,
        int? minLength = null,
        int? maxLength = null,
        long? minimum = null,
        long? maximum = null
    ) =>
        new()
        {
            Name = name,
            Type = type,
            EnumValues = enums,
            MinLength = minLength,
            MaxLength = maxLength,
            Minimum = minimum,
            Maximum = maximum
        };

    /// <summary>
    /// need17-02：工具 Schema 注册表（镜像各工具 GetDefinition 的参数语义；03/04/05 分册收紧 Schema 时同步更新此表）。
    /// 宽容点（冻结）：offset/limit/max_depth/timeout_ms 只约束 integer 类型、不限 min/max（0/负数留给业务层）。
    /// </summary>
    internal static readonly Dictionary<string, ToolSchemaSpec> Schemas =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["read_file"] = new ToolSchemaSpec
            {
                ToolName = "read_file",
                Required = new[] { "file_path" },
                Params = P(S("file_path"), S("offset", "integer"), S("limit", "integer")),
            },
            ["list_dir"] = new ToolSchemaSpec
            {
                ToolName = "list_dir",
                Required = Array.Empty<string>(),
                Params = P(
                    S("path"),
                    S("file_path"),
                    S("recursive", "boolean"),
                    S("max_depth", "integer")
                ),
            },
            ["grep_search"] = new ToolSchemaSpec
            {
                ToolName = "grep_search",
                Required = new[] { "pattern" },
                Params = P(S("pattern"), S("path"), S("file_path"), S("include_glob")),
            },
            ["view_image"] = new ToolSchemaSpec
            {
                // S3 §4.1：结果侧四态错误码（IMAGE_TOO_LARGE / NOT_AN_IMAGE / IMAGE_MISSING / PATH_TRAVERSAL）
                // 由 ViewImageTool 经 ToolResult.FailCode 抛出；此处只做参数校验，校验逻辑不变。
                ToolName = "view_image",
                Required = new[] { "file_path" },
                Params = P(S("file_path"), S("path"), S("detail")),
            },
            ["exec_command"] = new ToolSchemaSpec
            {
                ToolName = "exec_command",
                Required = new[] { "command" },
                Params = P(
                    S("command"),
                    S("runner", enums: new[] { "cmd", "python" }),
                    S("workdir"),
                    S("timeout_ms", "integer")
                ),
            },
            ["edit_file"] = new ToolSchemaSpec
            {
                ToolName = "edit_file",
                Required = new[] { "file_path", "old_string", "new_string" },
                Params = P(
                    S("file_path"),
                    S("old_string"),
                    S("new_string"),
                    S("replace_all", "boolean"),
                    S("match_mode", enums: new[] { "normalized", "exact", "regex" }),
                    S("regex_expand_groups", "boolean"),
                    S("expected_hash"),
                    S("dry_run", "boolean"),
                    S("normalize_eol", "boolean"),
                    S("expand_tabs", "integer"),
                    S("trim_trailing_space", "boolean"),
                    S("ignore_leading_whitespace", "boolean")
                ),
            },
            ["edit_lines"] = new ToolSchemaSpec
            {
                // need17联调：limit 缺失留给业务层 MISSING_LIMIT（02 不前置），保证老回归绿；Schema 声明仍要求 limit（GetDefinition 侧）。
                // need22-P0 S3：补 limit 下限 minimum:0（与业务层一致；缺失仍走业务层 MISSING_LIMIT）。
                ToolName = "edit_lines",
                Required = new[] { "file_path", "offset", "new_text" },
                Params = P(
                    S("file_path"),
                    S("offset", "integer"),
                    S("limit", "integer", minimum: 0),
                    S("new_text"),
                    S("expected_hash"),
                    S("allow_empty", "boolean"),
                    S("dry_run", "boolean")
                ),
            },
            ["write_file"] = new ToolSchemaSpec
            {
                ToolName = "write_file",
                Required = new[] { "file_path", "content" },
                Params = P(
                    S("file_path"),
                    S("content"),
                    S("allow_overwrite", "boolean"),
                    S("expected_hash"),
                    S("max_bytes", "integer", maximum: 1000000),
                    S("dry_run", "boolean")
                ),
            },
            ["edit_batch"] = new ToolSchemaSpec
            {
                ToolName = "edit_batch",
                Required = new[] { "file_path", "edits" },
                Params = P(
                    S("file_path"),
                    S("mode", enums: new[] { "lines", "text" }),
                    S("edits", "array"),
                    S("expected_hash"),
                    S("allow_empty", "boolean"),
                    S("dry_run", "boolean")
                ),
            },
            ["undo_edit"] = new ToolSchemaSpec
            {
                ToolName = "undo_edit",
                Required = new[] { "backup_path" },
                Params = P(S("backup_path"), S("restore_to")),
            },
            ["edit"] = new ToolSchemaSpec
            {
                ToolName = "edit",
                Required = new[] { "file_path", "mode" },
                Params = P(
                    S("file_path"),
                    S("mode", enums: new[] { "text", "lines" }),
                    S("old_string"),
                    S("new_string"),
                    S("replace_all", "boolean"),
                    S("match_mode", enums: new[] { "exact", "normalized", "regex" }),
                    S("offset", "integer"),
                    S("limit", "integer", minimum: 0),
                    S("new_text"),
                    S("dry_run", "boolean"),
                    S("expected_hash")
                ),
            },
            ["ask_user"] = new ToolSchemaSpec
            {
                // need17联调：Required 保持空（handler 同时接受 questions 数组/questionsYaml/单 question；收紧会误杀存量 questions-only 调用）。
                // GetDefinition 侧 required [question] 只做模型教学。object 形态 question 由 02 豁免交业务层（见第 5 步）。
                ToolName = "ask_user",
                Required = Array.Empty<string>(),
                Params = P(
                    S("question", minLength: 1),
                    S("questions", "array"),
                    S("questionsYaml"),
                    S("timeoutSec", "integer", minimum: 1, maximum: 3600)
                ),
            },
            ["skill_tool"] = new ToolSchemaSpec
            {
                ToolName = "skill_tool",
                Required = new[] { "action" },
                Params = P(
                    S(
                        "action",
                        enums: new[]
                        {
                            "list",
                            "get",
                            "create",
                            "update",
                            "delete",
                            "enable",
                            "disable"
                        }
                    ),
                    S("scope"),
                    S("name"),
                    S("content"),
                    S("pin"),
                    S("priority", "integer", minimum: 0, maximum: 1000),
                    S("enabled", "boolean")
                ),
            },
            ["memory_tool"] = new ToolSchemaSpec
            {
                ToolName = "memory_tool",
                Required = new[] { "action" },
                Params = P(
                    S(
                        "action",
                        enums: new[]
                        {
                            "list",
                            "get",
                            "set",
                            "create",
                            "update",
                            "delete",
                            "enable",
                            "disable"
                        }
                    ),
                    S("scope"),
                    S("name"),
                    S("body"),
                    S("content"),
                    S("pin"),
                    S("priority", "integer", minimum: 0, maximum: 1000),
                    S("enabled", "boolean")
                ),
            },
            ["spawn_subagent"] = new ToolSchemaSpec
            {
                ToolName = "spawn_subagent",
                Required = new[] { "task" },
                Params = P(
                    S("task", minLength: 1),
                    S("label", maxLength: 20),
                    S("role"),
                    S("model"),
                    S("providerId"),
                    S("maxIterations", "integer", minimum: 1, maximum: 50)
                ),
            },
            ["goal_tool"] = new ToolSchemaSpec
            {
                ToolName = "goal_tool",
                Required = new[] { "action" },
                Params = P(
                    S(
                        "action",
                        enums: new[]
                        {
                            "get",
                            "set",
                            "update",
                            "progress",
                            "add_milestone",
                            "complete_milestone",
                            "clear"
                        }
                    ),
                    S("title"),
                    S("description"),
                    S("milestones", "array"),
                    S("milestone"),
                    S("percent", "integer", minimum: 0, maximum: 100),
                    S("note")
                ),
            },
        };

    private static Dictionary<string, ToolParamSpec> P(params ToolParamSpec[] specs)
    {
        var d = new Dictionary<string, ToolParamSpec>(StringComparer.Ordinal);
        foreach (var s in specs)
        {
            d[s.Name] = s;
        }
        return d;
    }

    public static ToolCallValidationResult Validate(
        string toolName,
        string? rawArgsJson,
        bool closedWorld = true,
        bool enableRepair = true
    )
    {
        string tool = (toolName ?? string.Empty).Trim();
        string raw = string.IsNullOrWhiteSpace(rawArgsJson) ? "{}" : rawArgsJson!;

        if (!Schemas.TryGetValue(tool, out var spec))
        {
            return Fail(
                Codes.UnknownTool,
                $"未知工具 '{tool}'。请修正工具名后重试（可用工具以 ToolRegistry 声明为准）。",
                tool,
                new { tool }
            );
        }

        // 1. Parse with conservative repair fallback.
        Dictionary<string, JsonElement> args;
        var repairWarnings = new List<string>();
        try
        {
            args = ParseObject(raw);
        }
        catch (JsonException ex)
        {
            if (enableRepair && ToolJsonRepair.TryRepair(raw, out string fixedRaw))
            {
                try
                {
                    args = ParseObject(fixedRaw);
                    repairWarnings.Add(
                        "REPAIRED_JSON：参数 JSON 经保守修复（裸换行/trailing comma），请直接传合法 JSON。"
                    );
                }
                catch (JsonException ex2)
                {
                    return InvalidJsonFail(tool, raw, ex2);
                }
            }
            else
            {
                return InvalidJsonFail(tool, raw, ex);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Fail(
                Codes.InvalidJsonArguments,
                $"参数不是合法 JSON 对象（{ex.Message}）。收到的参数：{Summarize(raw)}。请传 JSON 对象后重试。",
                tool,
                null
            );
        }

        // 2. Shim.
        var shim = ApplyShim(tool, args);
        var dict = shim.Args;
        var warnings = new List<string>(shim.Warnings);
        warnings.AddRange(repairWarnings);

        if (
            (
                tool.Equals("list_dir", StringComparison.OrdinalIgnoreCase)
                || tool.Equals("grep_search", StringComparison.OrdinalIgnoreCase)
            )
            && dict.TryGetValue("file_path", out var fpEl)
            && fpEl.ValueKind == JsonValueKind.String
            && (!dict.TryGetValue("path", out var pEl) || pEl.ValueKind != JsonValueKind.String)
        )
        {
            dict["path"] = fpEl;
        }

        // 3. Required (content/ new_text allow empty string).
        var required = new List<string>(spec.Required);
        foreach (var extra in ConditionalRequired.GetAdditionalRequired(tool, dict))
        {
            if (!required.Contains(extra, StringComparer.Ordinal))
            {
                required.Add(extra);
            }
        }
        foreach (var name in required)
        {
            if (IsMissing(dict, name, exceptEmptyString: name is "new_text" or "content"))
            {
                return Fail(
                    Codes.MissingRequiredParam,
                    $"缺少必需参数 '{name}'（工具 '{tool}' 需要 {string.Join(",", required)}）。收到的参数：{Summarize(SerializeArgs(dict))}。请补上该参数后重试。",
                    tool,
                    new
                    {
                        tool,
                        missing = required
                            .Where(n =>
                                IsMissing(dict, n, exceptEmptyString: n is "new_text" or "content")
                            )
                            .ToArray()
                    }
                );
            }
        }

        // 4. Unknown fields: tiered policy.
        foreach (var key in dict.Keys.ToList())
        {
            if (spec.Params.ContainsKey(key) || AliasToCanonical.ContainsKey(key))
            {
                continue;
            }
            bool readOnly = ReadOnlyTools.Contains(tool, StringComparer.OrdinalIgnoreCase);
            bool writeTool = WriteTools.Contains(tool, StringComparer.OrdinalIgnoreCase);
            if (!closedWorld)
            {
                warnings.Add($"UNKNOWN_ARGUMENT (ignored, strict off)：未知参数 '{key}' 已忽略（回滚模式）。");
                dict.Remove(key);
                continue;
            }
            if (readOnly)
            {
                warnings.Add($"STRIPPED_UNKNOWN_PARAM：未知参数 '{key}' 已剥离忽略。");
                dict.Remove(key);
                continue;
            }
            if (writeTool)
            {
                string? guess = FindClosestSafetyField(key);
                if (guess != null)
                {
                    return Fail(
                        Codes.UnknownArgument,
                        $"未知参数 '{key}'（工具 '{tool}' 不支持该字段）。你是不是想传 `{guess}`？请修正字段名后重试。收到的参数：{Summarize(SerializeArgs(dict))}。",
                        tool,
                        new
                        {
                            tool,
                            unknown = key,
                            guess
                        }
                    );
                }
                if (HitsSafetySubstringGuard(key))
                {
                    return Fail(
                        Codes.UnknownArgument,
                        $"未知参数 '{key}' 涉及安全字段拼写（工具 '{tool}' 不支持该字段）。请检查 dry_run / allow_overwrite / expected_hash / allow_empty 拼写后重试。收到的参数：{Summarize(SerializeArgs(dict))}。",
                        tool,
                        new { tool, unknown = key }
                    );
                }
                warnings.Add($"STRIPPED_UNKNOWN_PARAM：未知参数 '{key}' 已剥离忽略。");
                dict.Remove(key);
                continue;
            }
            return Fail(
                Codes.UnknownArgument,
                $"未知参数 '{key}'（工具 '{tool}' 不支持该字段）。请删掉该字段后重试。收到的参数：{Summarize(SerializeArgs(dict))}。",
                tool,
                new { tool, unknown = key }
            );
        }
        // 5. Type/Enum/constraints.
        foreach (var kv in spec.Params)
        {
            if (!dict.TryGetValue(kv.Key, out var el))
            {
                continue;
            }
            if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            {
                dict.Remove(kv.Key);
                continue;
            }
            if (
                tool.Equals("ask_user", StringComparison.OrdinalIgnoreCase)
                && kv.Key.Equals("question", StringComparison.Ordinal)
                && el.ValueKind == JsonValueKind.Object
            )
            {
                continue;
            }
            var ps = kv.Value;
            var check = CheckField(tool, ps, el, dict);
            if (check.Error != null)
            {
                return check.Error;
            }
            foreach (var w in check.Warnings)
            {
                warnings.Add(w);
            }
        }

        var normalized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var kv in dict)
        {
            if (
                AliasToCanonical.ContainsKey(kv.Key)
                && !kv.Key.Equals("path", StringComparison.Ordinal)
            )
            {
                continue;
            }
            normalized[kv.Key] = kv.Value;
        }

        return new ToolCallValidationResult
        {
            Success = true,
            NormalizedJson = SerializeArgs(normalized),
            Warnings = warnings,
        };
    }

    private static Dictionary<string, JsonElement> ParseObject(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"收到 {doc.RootElement.ValueKind}");
        }
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            args[prop.Name] = prop.Value.Clone();
        }
        return args;
    }

    private static ToolCallValidationResult InvalidJsonFail(
        string tool,
        string raw,
        JsonException ex
    )
    {
        string err = ex.Message ?? string.Empty;
        if (err.Length > 200)
        {
            err = err.Substring(0, 200);
        }
        return Fail(
            Codes.InvalidJsonArguments,
            $"参数 JSON 解析失败：{err}。收到的参数：{Summarize(raw)}。请修正 JSON 后重试。",
            tool,
            null
        );
    }
}
