using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>
/// 契约守卫：确保 contract/agent.schema.json、C# DTO、TS 类型三方一致。
///
/// 这些测试属于"元测试"——它们测的不是功能，而是"契约有没有漂移"。
/// 任何一个失败都意味着：有人改了契约的一边，忘了另一边。
/// </summary>
public class SchemaConformanceTests
{
    private static readonly JsonDocument Schema = LoadSchema();

    private static JsonDocument LoadSchema()
    {
        var path = FindSchemaPath();
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindSchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "contract", "agent.schema.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 contract/agent.schema.json");
    }

    private static JsonElement Methods =>
        Schema.RootElement.GetProperty("methods");

    private static JsonElement Events =>
        Schema.RootElement.GetProperty("events");

    private static JsonElement ErrorCodes =>
        Schema.RootElement.GetProperty("errorCodes");

    // ============================================================
    //  schema → C#
    // ============================================================

    [Fact]
    public void Every_schema_method_is_declared()
    {
        var schemaMethods = Methods.EnumerateObject().Select(p => p.Name).ToHashSet();

        // Router 里注册的方法（P1 阶段由 StubMethods 提供）
        var implemented = KnownMethods.All.ToHashSet();

        var missing = schemaMethods.Except(implemented).ToList();
        Assert.True(missing.Count == 0,
            $"schema 声明了但 C# 未实现的方法：{string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_csharp_method_is_in_schema()
    {
        var schemaMethods = Methods.EnumerateObject().Select(p => p.Name).ToHashSet();
        var orphan = KnownMethods.All.Except(schemaMethods).ToList();
        Assert.True(orphan.Count == 0,
            $"C# 实现了但 schema 未声明的方法：{string.Join(", ", orphan)}");
    }

    [Fact]
    public void Event_kinds_match_schema()
    {
        var schemaEvents = Events.EnumerateObject().Select(p => p.Name).ToHashSet();
        var codeEvents = EventKinds.All.ToHashSet();

        Assert.Equal(schemaEvents.Count, codeEvents.Count);
        Assert.True(schemaEvents.SetEquals(codeEvents),
            $"事件集合不一致。仅 schema 有：{string.Join(",", schemaEvents.Except(codeEvents))}；" +
            $"仅代码有：{string.Join(",", codeEvents.Except(schemaEvents))}");
    }

    [Fact]
    public void Error_codes_match_schema()
    {
        var schemaCodes = ErrorCodes.EnumerateObject().Select(p => p.Name).ToHashSet();
        var codeCodes = RpcError.All.ToHashSet();

        Assert.True(schemaCodes.SetEquals(codeCodes),
            $"错误码不一致。仅 schema 有：{string.Join(",", schemaCodes.Except(codeCodes))}；" +
            $"仅代码有：{string.Join(",", codeCodes.Except(schemaCodes))}");
    }

    // ============================================================
    //  DTO 形状
    // ============================================================

    [Fact]
    public void StateSnapshot_serializes_with_expected_field_names()
    {
        var snapshot = new HaloAgent.Engine.Abstractions.Types.StateSnapshot();
        var json = JsonSerializer.Serialize(snapshot, HaloJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 顶层必填字段（与 schema 的 required 一致）
        foreach (var required in new[]
        {
            "protocol", "engine", "session", "agent", "usage", "queue", "messages",
        })
        {
            Assert.True(root.TryGetProperty(required, out _),
                $"StateSnapshot 缺少必需字段 {required}");
        }

        // agent 子字段
        var agent = root.GetProperty("agent");
        foreach (var f in new[]
        {
            "busy", "mode", "modeLabel", "model", "provider", "baseUrl",
            "apiKeySet", "maxTurns", "round",
        })
        {
            Assert.True(agent.TryGetProperty(f, out _), $"agent 缺少字段 {f}");
        }
    }

    [Fact]
    public void PendingAsk_uses_expected_field_names()
    {
        var ask = HaloAgent.Engine.Abstractions.Types.PendingAsk.Permission(
            "q1", "command", "标题", "正文", null, true);
        var json = JsonSerializer.Serialize(ask, HaloJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("q1", root.GetProperty("askId").GetString());
        Assert.Equal("command", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("allowAlways").GetBoolean());
    }

    [Fact]
    public void Event_payloads_roundtrip()
    {
        var cases = new (string kind, object payload)[]
        {
            (EventKinds.TextDelta, new TextDeltaPayload { Text = "你好" }),
            (EventKinds.ToolStarted, new ToolStartedPayload
            {
                ToolCallId = "c1", Name = "read_file", Args = "{}", IdleMs = null,
            }),
            (EventKinds.ToolFinished, new ToolFinishedPayload
            {
                ToolCallId = "c1", Name = "read_file", Ok = true, Output = "内容",
            }),
            (EventKinds.RoundStarted, new RoundStartedPayload { Used = 1, Limit = 200 }),
            (EventKinds.Usage, new UsagePayload { Prompt = 100, Completion = 20 }),
            (EventKinds.Notice, new NoticePayload { Text = "提示", Level = "info" }),
        };

        foreach (var (kind, payload) in cases)
        {
            var el = JsonSerializer.SerializeToElement(payload, HaloJson.Options);
            var frame = new EventFrame { Seq = 1, Kind = kind, Payload = el };
            var bytes = FrameCodec.ToBytes(frame);
            var parsed = FrameCodec.Parse(bytes.AsSpan(0, bytes.Length - 1), out var err);

            Assert.Null(err);
            var ev = Assert.IsType<EventFrame>(parsed);
            Assert.Equal(kind, ev.Kind);
        }
    }
}

/// <summary>
/// 已实现方法清单（P1 阶段手写；P2 由 Router 反射生成）。
/// </summary>
internal static class KnownMethods
{
    public static readonly string[] All =
    {
        // system
        "system.ping", "system.info", "system.protocol", "system.shutdown", "system.diagnostics",
        // agent
        "agent.prompt", "agent.queue", "agent.unqueue", "agent.cancel",
        "agent.answer", "agent.drain", "agent.resync",
        // session
        "session.new", "session.resume", "session.list", "session.rename",
        "session.delete", "session.clear", "session.compact", "session.setCwd",
        // state
        "state.snapshot", "state.setMode", "state.setPersona", "state.setProvider",
        "state.setMaxTurns", "state.setRetry",
        // settings
        "settings.get", "settings.saveModel", "settings.saveNetwork",
        "settings.probeModels", "settings.saveExtraHeaders",
        "settings.mcp.get", "settings.mcp.save",
        // asset
        "asset.skills.list", "asset.skills.get", "asset.skills.save",
        "asset.skills.delete", "asset.skills.run",
        "asset.personas.list", "asset.personas.get", "asset.personas.save",
        "asset.memory.list", "asset.memory.get", "asset.memory.save", "asset.memory.delete",
        // tool
        "tool.list", "tool.undoList", "tool.undoRollback", "tool.mcpStatus",
        // fs
        "fs.listDir", "fs.completePaths", "fs.validatePath",
        "fs.browse", "fs.pickFiles", "fs.pickSavePath", "fs.readImageAsBase64",
        // update
        "update.check", "update.install",
    };
}
