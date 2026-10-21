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

    /// <summary>别名→主名（未知字段检查时豁免；shim 已做搬运，normalized 仅保留主名与 path 双键过渡）。</summary>
    internal static readonly Dictionary<string, string> AliasToCanonical =
        new(StringComparer.Ordinal)
        {
            ["path"] = "file_path",
            ["cmd"] = "command",
            ["timeout"] = "timeout_ms",
            ["timeoutMs"] = "timeout_ms",
            ["oldString"] = "old_string",
            ["newString"] = "new_string",
            ["allowOverwrite"] = "allow_overwrite",
        };

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
                    S("content", minLength: 1),
                    S("allow_overwrite", "boolean"),
                    S("expected_hash"),
                    S("max_bytes", "integer", maximum: 1000000),
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

    /// <summary>need17-02：四步校验入口。成功返回归一化 JSON + warnings；失败返回码化错误（不抛）。closedWorld=false（Strict Off 回滚）时未知字段降级为 warning，不拒绝。</summary>
    public static ToolCallValidationResult Validate(
        string toolName,
        string? rawArgsJson,
        bool closedWorld = true
    )
    {
        string tool = (toolName ?? string.Empty).Trim();
        string raw = string.IsNullOrWhiteSpace(rawArgsJson) ? "{}" : rawArgsJson!;

        if (!Schemas.TryGetValue(tool, out var spec))
        {
            // need22-P0 S3：未知工具一律拒绝 UNKNOWN_TOOL，删除透传分支。
            return Fail(
                Codes.UnknownTool,
                $"未知工具 '{tool}'。请修正工具名后重试（可用工具以 ToolRegistry 声明为准）。",
                tool,
                new { tool }
            );
        }

        // 1. Parse：空串→{}；失败→INVALID_JSON_ARGUMENTS（含解析错误前 200 字符 + 原 args 前 500 字符）。
        Dictionary<string, JsonElement> args;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail(
                    Codes.InvalidJsonArguments,
                    $"参数不是合法 JSON 对象（收到 {doc.RootElement.ValueKind}）。收到的参数：{Summarize(raw)}。请传 JSON 对象后重试。",
                    tool,
                    null
                );
            }
            args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                args[prop.Name] = prop.Value.Clone();
            }
        }
        catch (JsonException ex)
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

        // 2. Shim：别名搬运、形状修复、类型宽容转换（产出 normalized + warnings）。
        var shim = ApplyShim(tool, args);
        var dict = shim.Args;
        var warnings = new List<string>(shim.Warnings);

        // file_path→path 反向双键（过渡）：list_dir/grep_search 业务层仍读 legacy 'path'（04 收敛后移除）。
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

        // 3. Required：缺失必填→MISSING_REQUIRED_PARAM（含缺失字段名 + 该工具必填全表 + 参数摘要）。
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
            if (IsMissing(dict, name, exceptEmptyString: name == "new_text"))
            {
                return Fail(
                    Codes.MissingRequiredParam,
                    $"缺少必需参数 '{name}'（工具 '{tool}' 需要 {string.Join(",", required)}）。收到的参数：{Summarize(SerializeArgs(dict))}。请补上该参数后重试。",
                    tool,
                    new
                    {
                        tool,
                        missing = required
                            .Where(n => IsMissing(dict, n, exceptEmptyString: n == "new_text"))
                            .ToArray()
                    }
                );
            }
        }

        // 4. 未知字段（additionalProperties:false 生效）→UNKNOWN_ARGUMENT（别名键豁免；Off 回滚时降级为 warning）。
        foreach (var key in dict.Keys.ToList())
        {
            if (!spec.Params.ContainsKey(key) && !AliasToCanonical.ContainsKey(key))
            {
                if (!closedWorld)
                {
                    warnings.Add($"UNKNOWN_ARGUMENT (ignored, strict off)：未知参数 '{key}' 已忽略（回滚模式）。");
                    continue;
                }
                return Fail(
                    Codes.UnknownArgument,
                    $"未知参数 '{key}'（工具 '{tool}' 不支持该字段）。请删掉该字段后重试。收到的参数：{Summarize(SerializeArgs(dict))}。",
                    tool,
                    new { tool, unknown = key }
                );
            }
        }
        // 5. Type/Enum/约束：逐字段校验；数字宽容成功记 warning，不拒绝。
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
            // need17联调：ask_user question 的 object 形态是存量兼容别名（handler 可解析），02 豁免类型检查交业务层。
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

        // normalized 输出：剥离别名残留键（path 双键过渡保留）。
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
}
