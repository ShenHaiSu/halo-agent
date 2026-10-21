using System.Text.Json;
using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>
/// need08：智能体行为配置文档（全局 config/agent.json，唯一事实源）。
/// 超时单位毫秒；0=无限（默认超时 0=无限等待，上限 0=不限上限，轮次 0=无限轮次）。
/// </summary>
public class AgentConfigDocument
{
    public int Version { get; set; } = 1;
    public int ExecDefaultTimeoutMs { get; set; } = AgentDefaults.DefaultTimeoutMs;
    public int ExecMaxTimeoutMs { get; set; } = AgentDefaults.MaxTimeoutMs;
    public int MaxIterations { get; set; } = AgentDefaults.MaxIterations;

    /// <summary>need22-P1 T3：单轮总体超时毫秒（默认 10min；&lt;=0 读时回默认；老文件缺字段反序列化为 0 同样回默认）。</summary>
    public int TurnTimeoutMs { get; set; } = AgentDefaults.TurnTimeoutMs;

    /// <summary>need22-P1 T4：Barrier 总体超时 buffer 毫秒（总额外余量；&lt;=0 读时回默认）。</summary>
    public int SubBarrierBufferMs { get; set; } = AgentDefaults.SubBarrierBufferMs;

    // need13：子代理（缺字段=老文件，全部回默认，不算损坏）
    public bool SubAgentEnabled { get; set; } = true;
    public int SubMaxParallel { get; set; } = 3;
    public int SubMaxIterations { get; set; } = 15;
    public bool SubAllowExecCommand { get; set; } = true;
    public bool SubAllowFileWrite { get; set; } = true;
    public bool SubAllowSkillWrite { get; set; } = false;
    public bool SubAllowMemoryWrite { get; set; } = false;
    public bool SubAllowAskUser { get; set; } = false; // v1 锁定 false

    // need17-01：网关 strict 发送模式（Prefer 默认；Require 联调用；Off 回滚开关）。
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StrictToolsMode StrictToolsMode { get; set; } = StrictToolsMode.Prefer;

    // need17-01：工具激活集（preset/allowlist/denylist，互斥规则见 ToolPreset；null/空=默认 Coding）。
    public string? ToolsPreset { get; set; }
    public List<string>? ToolsAllowlist { get; set; }
    public List<string>? ToolsDenylist { get; set; }
    public string Scope { get; set; } = "global";

    /// <summary>need22-P0 S2：exec_command 首 token 白名单增补（null/空=默认白名单；不校验元素，透传）。</summary>
    public List<string>? AllowedCommands { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    // 后续杂项扩展位：本期只声明不使用，反序列化宽容忽略未知字段即可
    // public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>need17-01：网关 strict 发送模式。Prefer=发送并在 400 提及 strict 时去 strict 重发一次（默认）；Require=联调用；Off=回滚。</summary>
public enum StrictToolsMode
{
    Prefer = 0,
    Require = 1,
    Off = 2,
}

public static class AgentDefaults
{
    public const int DefaultTimeoutMs = 30000;
    public const int MaxTimeoutMs = 300000;
    public const int MaxIterations = 25;
    public const int HardTimeoutCeilMs = 3600000; // 有限值硬顶 1h（01 §6）
    public const int HardIterationsCeil = 500; // 有限值硬顶 500
    public const int SubMaxParallelDefault = 3;
    public const int SubMaxParallelCeil = 8;
    public const int SubMaxIterationsDefault = 15;
    public const int SubMaxIterationsCeil = 50;
    public const int SubHardParallelCeil = 8;
    public const int SubHardIterationsCeil = 100;

    /// <summary>need22-P1 T3：单轮总体超时默认 10min（config/agent.json: turnTimeoutMs 可配）。</summary>
    public const int TurnTimeoutMs = 600000;

    /// <summary>need22-P1 T3：单轮总体超时保存下限 1s（测试与极端场景可用；生产建议 ≥60s，默认 10min）。</summary>
    public const int TurnTimeoutMinMs = 1000;

    /// <summary>need22-P1 T4：Barrier 总体超时 buffer 默认 30s。</summary>
    public const int SubBarrierBufferMs = 30000;
}

public sealed class AgentConfigValidationException : Exception
{
    public string Field { get; }

    public AgentConfigValidationException(string field, string message)
        : base(message)
    {
        Field = field ?? string.Empty;
    }
}

/// <summary>
/// need08：命令超时解析纯函数（01 §4.1 矩阵逐字实现，可单测）。
/// D=配置默认值，M=配置上限，T=单次 tool call 的 timeout_ms（null=缺省/非数字）。
/// 返回 (effectiveMs, infinite)：infinite=true 表示本 call 无限等待。
/// </summary>
public static class AgentTimeoutResolver
{
    public static (int EffectiveMs, bool Infinite) Resolve(
        int? requestedMs,
        int cfgDefaultMs,
        int cfgMaxMs
    )
    {
        int d = cfgDefaultMs;
        int m = cfgMaxMs;

        // 缺省/非数字 → D；负数视为缺省
        int t;
        if (!requestedMs.HasValue || requestedMs.Value < 0)
        {
            t = d;
            // D=0 且 T 缺省 → 无限
            if (t == 0)
            {
                return (0, true);
            }
        }
        else
        {
            t = requestedMs.Value;
        }

        // T=0 → 本 call 无限等待，跳过上限（显式无限优先于 M）
        if (t == 0)
        {
            return (0, true);
        }

        // T>0, M=0 → 上限关闭，不钳制；有限硬顶兜底
        if (m == 0)
        {
            return (Math.Clamp(t, 1, AgentDefaults.HardTimeoutCeilMs), false);
        }

        // T>0, M>0 → 保留旧钳制语义
        int e = Math.Min(t, m);
        if (e <= 0)
        {
            e = d == 0 ? 0 : Math.Clamp(d, 1, AgentDefaults.HardTimeoutCeilMs);
            if (e == 0)
            {
                return (0, true);
            }
        }
        else
        {
            e = Math.Clamp(e, 1, AgentDefaults.HardTimeoutCeilMs);
        }

        return (e, false);
    }
}

/// <summary>
/// need08：智能体行为配置 JSON 宽容解析（对标 PromptJsonOptions.Lenient）。
/// </summary>
public static class AgentJsonOptions
{
    public static readonly JsonSerializerOptions Lenient = CreateLenient();

    private static JsonSerializerOptions CreateLenient()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new LenientDateTimeConverter());
        return options;
    }

    public static AgentConfigDocument DeserializeConfigDocument(string json)
    {
        var doc = JsonSerializer.Deserialize<AgentConfigDocument>(json, Lenient);
        return doc ?? throw new JsonException("智能体行为配置 JSON 解析为空");
    }
}
