using System.Diagnostics;
using System.Text.Json;
using HaloAgent.Desktop.Engine;

namespace HaloAgent.Desktop.Bridge;

/// <summary>
/// 引擎传输桥接（window.haloAgent，恰好 3 方法）。
/// 54 个引擎方法不逐个包一层，只暴露通用 request(method, params)。
/// fs.* 由宿主路由（HostMethodRouter），其余转引擎。
/// </summary>
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

    /// <summary>发一条 P1 方法请求。params 用 object 接收再序列化回 JsonElement。</summary>
    public async Task<object?> Request(string method, object? prms)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("method 不能为空");

        if (HostMethodRouter.IsHostMethod(method))
            return await _dialogs.RouteAsync(method, prms).ConfigureAwait(false);

        var client = _clientAccessor()
            ?? throw new EngineNotReadyException("引擎尚未就绪");

        var json = prms is null
            ? default(JsonElement?)
            : JsonSerializer.SerializeToElement(prms, BridgeJson.Options);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await client.RequestRawAsync(method, json, CancellationToken.None)
                .ConfigureAwait(false);
            sw.Stop();
            AppLog.Info($"[bridge] {method} 完成 {sw.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLog.Warn($"[bridge] {method} 失败 {sw.ElapsedMilliseconds}ms: {ex.Message}");
            throw;
        }
    }

    /// <summary>拉事件（前端按 nextSeq 循环调用；只做队列出队+序列化，不深拷贝）。</summary>
    public Task<PollResult> PollEvents(int sinceSeq)
    {
        var client = _clientAccessor();
        if (client is null)
            return Task.FromResult(new PollResult { Events = Array.Empty<object>(), NextSeq = sinceSeq });

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

        return Task.FromResult(new PollResult { Events = events.Cast<object>().ToArray(), NextSeq = next });
    }
}
