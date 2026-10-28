using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Desktop.Engine;

namespace HaloAgent.Desktop.Bridge;

/// <summary>
/// 引擎传输桥接（window.haloAgent，恰好 3 方法）。
/// 54 个引擎方法不逐个包一层，只暴露通用 request(method, params)。
/// fs.* 由宿主路由（HostMethodRouter），其余转引擎。
/// </summary>
/// <remarks>
/// 铁律（dist 骨架屏根因修复）：返回值一律是 JSON 字符串，前端 JSON.parse。
/// JsonElement / DTO 绝不能直接过 CefSharp 桥——CefSharp 对复杂返回值的
/// 序列化不稳定（嵌套丢字段）且 DTO 按 C# 原名（PascalCase）返回，
/// 而前端全按 camelCase 消费，直接返回会导致数据静默全空。
/// 异常仍用 throw（CefSharp 会 reject JS Promise，前端包成可读错误）。
/// </remarks>
public sealed class AgentBridge
{
    private readonly Func<EngineClient?> _clientAccessor;
    private readonly HostFileDialogs _dialogs;
    private readonly Action<bool>? _onBusy;

    public AgentBridge(Func<EngineClient?> clientAccessor, HostFileDialogs dialogs, Action<bool>? onBusy = null)
    {
        _clientAccessor = clientAccessor;
        _dialogs = dialogs;
        _onBusy = onBusy;
    }

    public Task<bool> IsReady() =>
        Task.FromResult(_clientAccessor()?.IsConnected == true);

    /// <summary>
    /// 发一条 P1 方法请求。返回引擎应答 result 的 JSON 字符串
    ///（camelCase，由引擎侧 HaloJson 原样产生，直接取原文过桥）。
    /// 引擎业务错误 / 未就绪一律 throw（不断言、不吞错）。
    /// </summary>
    public async Task<string> Request(string method, object? prms)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("method 不能为空");

        if (HostMethodRouter.IsHostMethod(method))
            return ToJsonString(await _dialogs.RouteAsync(method, prms).ConfigureAwait(false));

        var client = _clientAccessor()
            ?? throw new EngineNotReadyException("引擎尚未就绪");

        var json = prms is null
            ? default(JsonElement?)
            : JsonSerializer.SerializeToElement(prms, BridgeJson.Options);

        var sw = Stopwatch.StartNew();
        try
        {
            // RequestRawAsync 产出 JsonElement：直接取原文字符串过桥，
            // 不经 CefSharp 二次编组（编组复杂对象会丢字段/改名）。
            var result = await client.RequestRawAsync(method, json, CancellationToken.None)
                .ConfigureAwait(false);
            sw.Stop();
            AppLog.Info($"[bridge] {method} 完成 {sw.ElapsedMilliseconds}ms");
            return ToJsonString(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLog.Warn($"[bridge] {method} 失败 {sw.ElapsedMilliseconds}ms: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 拉事件（前端按 nextSeq 循环调用）。返回 PollResult 的 JSON 字符串
    ///（{"events":[{t,seq,k,p}...],"nextSeq":N}，camelCase，与管道协议一致）。
    /// 引擎未连接时返回空批，不断言。
    /// </summary>
    public Task<string> PollEvents(int sinceSeq)
    {
        var client = _clientAccessor();
        if (client is null)
        {
            var empty = new PollResult { Events = Array.Empty<object>(), NextSeq = sinceSeq };
            return Task.FromResult(JsonSerializer.Serialize(empty, BridgeJson.Options));
        }

        var (events, next) = client.DrainEvents(sinceSeq);

        // 顺手通知宿主忙碌状态（零额外开销；仅依赖 turn.started/turn.finished/agent.busy）。
        if (_onBusy is not null)
        {
            foreach (var e in events)
            {
                var kind = e.Kind;
                if (kind is "agent.busy" or "turn.started") _onBusy(true);
                else if (kind is "turn.finished") _onBusy(false);
            }
        }

        // EventFrame 自带 [JsonPropertyName("t"/"seq"/"k"/"p")]，
        // BridgeJson 的 camelCase 只影响外层 events/nextSeq。
        var batch = new PollResult { Events = events.Cast<object>().ToArray(), NextSeq = next };
        return Task.FromResult(JsonSerializer.Serialize(batch, BridgeJson.Options));
    }

    /// <summary>桥返回值统一转 JSON 文本；null/Undefined 一律 "null"。</summary>
    private static string ToJsonString(object? result)
    {
        if (result is JsonElement el
            && el.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            return el.GetRawText();
        if (result is null)
            return "null";
        // 防御：理论上到不了这里（RequestRawAsync/RouteAsync 只产出 JSON 兼容值）。
        // 万一有，将其序列化而不是直接过桥，避免 CefSharp 编组未定义行为。
        return JsonSerializer.Serialize(result, BridgeJson.Options);
    }
}
