using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;

namespace HaloAgent.Engine.Server.Routing;

public sealed partial class RealMethods
{
    public Task<object?> SettingsGetAsync(JsonElement? p, CancellationToken ct)
    {
        var cfg = ConfigStore.Load();

        // 旧形状嗅探：空新模型 + migrationRequired=true（Load 已备份一次）
        bool migrationRequired = IsOldShapeOnDisk();

        var mode = (cfg.Proxy.Mode ?? "auto").ToLowerInvariant();
        if (mode is not ("auto" or "direct" or "manual")) mode = "auto";

        var providers = cfg.Providers
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ProviderView
            {
                Id = kv.Key,
                DisplayName = string.IsNullOrWhiteSpace(kv.Value.DisplayName) ? kv.Key : kv.Value.DisplayName!.Trim(),
                BaseUrl = kv.Value.BaseUrl ?? "",
                ApiKeySet = !string.IsNullOrEmpty(kv.Value.ApiKey),
                HeadersEnabled = kv.Value.HeadersEnabled,
                Headers = (kv.Value.Headers ?? new Dictionary<string, string>())
                    .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(h => new HeaderPair { Name = h.Key, Value = h.Value }).ToList(),
                UseProxy = kv.Value.UseProxy,
                ModelCount = kv.Value.Models.Count,
                Models = kv.Value.Models.Select(m => new ModelView
                {
                    Id = m.Id,
                    DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName!.Trim(),
                    Protocol = (m.Protocol ?? "chat").ToLowerInvariant(),
                    ContextWindow = m.ContextWindow,
                    MaxTokens = m.MaxTokens,
                    AcceptsImages = m.AcceptsImages,
                    IncludeReasoningHistory = m.IncludeReasoningHistory,
                }).ToList(),
            })
            .ToList();

        return Task.FromResult<object?>(new SettingsGetResult
        {
            ActiveProvider = cfg.ActiveProvider ?? "",
            ActiveModel = cfg.ActiveModel ?? "",
            MigrationRequired = migrationRequired,
            Providers = providers,
            Proxy = new ProxySettings { Mode = mode, Url = cfg.Proxy.Url },
            Retry = cfg.Retry ?? 0,
            MaxTurns = cfg.MaxTurns ?? 200,
            Persona = cfg.Persona ?? "",
            DataDir = _dataDir,
            ConfigPath = Path.Combine(_dataDir, "config.json"),
        });
    }

    private bool IsOldShapeOnDisk()
    {
        try
        {
            var path = Paths.ConfigFile;
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            return !ConfigStore.IsNewShapeJson(text) && text.TrimStart().StartsWith("{");
        }
        catch { return false; }
    }

    public Task<object?> SettingsSaveProvidersAsync(JsonElement? p, CancellationToken ct)
    {
        if (p is null || !p.Value.TryGetProperty("providers", out var pv) || pv.ValueKind != JsonValueKind.Object)
            throw RpcException.ParamsInvalid("providers（对象映射，键为 provider id）必填");

        // 方案A：migrationRequired 两步确认后前端传 force=true，才允许覆盖旧结构/损坏配置。
        bool force = p.Value.TryGetProperty("force", out var forceEl) && forceEl.ValueKind == JsonValueKind.True;

        var warnings = new List<string>();
        ConfigModel cfg;
        try { cfg = ConfigStore.LoadForWrite(force); }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcError.Internal,
                "配置文件损坏或为旧结构，已拒绝保存以免覆盖：" + ex.Message);
        }
        if (force)
            warnings.Add("已按二次确认覆盖旧结构或损坏的配置（原文件已自动备份为 .bak.*）");

        var next = new ConfigModel
        {
            ActiveProvider = cfg.ActiveProvider,
            ActiveModel = cfg.ActiveModel,
            MaxTurns = cfg.MaxTurns,
            Persona = cfg.Persona,
            Retry = cfg.Retry,
            Proxy = cfg.Proxy,
        };

        // apiKey 缺席或空 = 不改；clearKey=true 清除（与 apiKey 同传时 clear 优先 + warning）
        foreach (var prop in pv.EnumerateObject())
        {
            var pid = prop.Name.Trim();
            var entry = ParseProviderEntry(prop.Value);
            cfg.Providers.TryGetValue(pid, out var existing);

            var pcfg = new ProviderConfig
            {
                DisplayName = entry.DisplayName ?? existing?.DisplayName,
                BaseUrl = (entry.BaseUrl ?? existing?.BaseUrl ?? "").Trim().TrimEnd('/'),
                ApiKey = existing?.ApiKey,
                HeadersEnabled = entry.HeadersEnabled ?? existing?.HeadersEnabled ?? true,
                Headers = entry.Headers ?? new Dictionary<string, string>(existing?.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                UseProxy = entry.UseProxy ?? existing?.UseProxy ?? true,
                Models = entry.Models?.Select(m => new ModelConfig
                {
                    Id = m.Id.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? null : m.DisplayName!.Trim(),
                    Protocol = ((m.Protocol ?? "chat").Trim().ToLowerInvariant()),
                    ContextWindow = m.ContextWindow,
                    MaxTokens = m.MaxTokens,
                    AcceptsImages = m.AcceptsImages ?? false,
                    IncludeReasoningHistory = m.IncludeReasoningHistory ?? true,
                }).ToList() ?? new List<ModelConfig>(),
            };

            if (entry.ClearKey)
            {
                pcfg.ApiKey = null;
                if (entry.ApiKey is not null)
                    warnings.Add($"服务商「{pid}」clearKey 优先于 apiKey（已清除）");
            }
            else if (!string.IsNullOrWhiteSpace(entry.ApiKey))
            {
                pcfg.ApiKey = entry.ApiKey!.Trim();
            }

            next.Providers[pid] = pcfg;
        }

        // 删除语义：整包替换——params 里没有的 provider 视为删除。若要保留，必须整树下发。
        // 为避免误删，这里要求调用方下发全量；缺席即删（按契约整树保存）。
        var errs = ConfigValidation.ValidateTree(next);
        if (errs.Count > 0)
        {
            // 整包拒绝：落盘无变化
            throw new RpcException(RpcError.ParamsInvalid,
                "整树校验失败（已拒绝保存，落盘无变化）: " +
                string.Join("；", errs.Select(e => $"[{e.Provider}/{e.Model ?? "-"}.{e.Field}] {e.Message}")));
        }
        warnings.AddRange(ConfigValidation.Warnings(next));

        ConfigStore.Save(next);

        return Task.FromResult<object?>(new SettingsSaveProvidersResult
        {
            Ok = true,
            Notes = Array.Empty<string>(),
            Warnings = warnings,
            Errors = Array.Empty<SettingsFieldError>(),
        });
    }

    private sealed record ParsedProvider(
        string? DisplayName, string? BaseUrl, string? ApiKey, bool ClearKey,
        bool? HeadersEnabled, Dictionary<string, string>? Headers, bool? UseProxy,
        List<ModelSaveEntry>? Models);

    private static ParsedProvider ParseProviderEntry(JsonElement el)
    {
        string? displayName = el.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        string? baseUrl = el.TryGetProperty("baseUrl", out var bv) ? bv.GetString() : null;
        string? apiKey = el.TryGetProperty("apiKey", out var ak) ? ak.GetString() : null;
        bool clearKey = el.TryGetProperty("clearKey", out var ck) && ck.ValueKind == JsonValueKind.True;
        bool? headersEnabled = el.TryGetProperty("headersEnabled", out var he)
            ? (he.ValueKind == JsonValueKind.True ? true : he.ValueKind == JsonValueKind.False ? false : (bool?)null) : null;
        Dictionary<string, string>? headers = null;
        if (el.TryGetProperty("headers", out var hv) && hv.ValueKind == JsonValueKind.Array)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hv.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object) continue;
                var key = h.TryGetProperty("key", out var k) ? k.GetString()?.Trim()
                    : h.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
                var val = h.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (val is null) continue;
                headers[key!] = val;
            }
        }
        bool? useProxy = el.TryGetProperty("useProxy", out var up)
            ? (up.ValueKind == JsonValueKind.True ? true : up.ValueKind == JsonValueKind.False ? false : (bool?)null) : null;
        List<ModelSaveEntry>? models = null;
        if (el.TryGetProperty("models", out var mv) && mv.ValueKind == JsonValueKind.Array)
        {
            models = new List<ModelSaveEntry>();
            foreach (var m in mv.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                models.Add(new ModelSaveEntry
                {
                    Id = m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    DisplayName = m.TryGetProperty("displayName", out var mdn) ? mdn.GetString() : null,
                    Protocol = m.TryGetProperty("protocol", out var pr) ? pr.GetString() : null,
                    ContextWindow = m.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number ? cw.GetInt32() : null,
                    MaxTokens = m.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number ? mt.GetInt32() : null,
                    AcceptsImages = m.TryGetProperty("acceptsImages", out var ai)
                        ? (ai.ValueKind == JsonValueKind.True ? true : ai.ValueKind == JsonValueKind.False ? false : (bool?)null) : null,
                    IncludeReasoningHistory = m.TryGetProperty("includeReasoningHistory", out var ir)
                        ? (ir.ValueKind == JsonValueKind.True ? true : ir.ValueKind == JsonValueKind.False ? false : (bool?)null) : null,
                });
            }
        }
        return new ParsedProvider(displayName, baseUrl, apiKey, clearKey, headersEnabled, headers, useProxy, models);
    }

    public Task<object?> SettingsSetActiveAsync(JsonElement? p, CancellationToken ct)
    {
        var providerId = p?.TryGetProperty("providerId", out var pv) == true ? pv.GetString()
            ?? (p?.TryGetProperty("provider", out var pv2) == true ? pv2.GetString() : null) : null;
        var modelId = p?.TryGetProperty("modelId", out var mv) == true ? mv.GetString()
            ?? (p?.TryGetProperty("model", out var mv2) == true ? mv2.GetString() : null) : null;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            throw RpcException.ParamsInvalid("providerId 与 modelId 必填");

        if (_session.IsBusy)
            throw new RpcException(RpcError.StateBusy, "有回合正在执行，切换被拒绝");

        providerId = providerId!.Trim();
        modelId = modelId!.Trim();

        ConfigModel cfg;
        try { cfg = ConfigStore.LoadForWrite(); }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcError.Internal, "配置文件损坏或为旧结构，已拒绝切换：" + ex.Message);
        }

        if (!cfg.Providers.TryGetValue(providerId, out var pdef))
            throw RpcException.ParamsInvalid("未知服务商: " + providerId);
        var mdef = pdef.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        if (mdef is null)
            throw RpcException.ParamsInvalid($"服务商「{providerId}」下未知模型: {modelId}");
        if (string.Equals((mdef.Protocol ?? "chat").ToLowerInvariant(), "responses", StringComparison.Ordinal))
            throw new RpcException(RpcError.NotSupported, "Responses 协议尚未支持（当前仅 chat 可用）。");

        cfg.ActiveProvider = providerId;
        cfg.ActiveModel = modelId;
        var errs = ConfigValidation.ValidateTree(cfg);
        if (errs.Count > 0)
            throw RpcException.ParamsInvalid("active 校验失败: " +
                string.Join("；", errs.Select(e => $"[{e.Provider}/{e.Model ?? "-"}.{e.Field}] {e.Message}")));

        ConfigStore.Save(cfg);

        // 热切：Resolve + Reconfigure（proxy 变化重建 HttpClient）
        ResolvedActiveModel resolved;
        try { resolved = ConfigStore.Resolve(cfg); }
        catch (Exception ex)
        {
            throw new RpcException(RpcError.Internal, "切换后解析失败：" + ex.Message);
        }
        try
        {
            _session.ReconfigureActive(resolved);
        }
        catch (NotSupportedException ex)
        {
            throw new RpcException(RpcError.NotSupported, ex.Message);
        }
        _ = PublishSnapshotAsync();

        return Task.FromResult<object?>(new SettingsSetActiveResult { Ok = true });
    }

    private Task PublishSnapshotAsync()
    {
        try
        {
            var snap = _session.BuildSnapshot();
            return Task.CompletedTask;
        }
        catch { return Task.CompletedTask; }
    }

    public Task<object?> SettingsSaveNetworkAsync(JsonElement? p, CancellationToken ct)
    {
        ConfigModel cfg;
        try { cfg = ConfigStore.LoadForWrite(); }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcError.Internal,
                "配置文件损坏，已拒绝保存以免覆盖：" + ex.Message);
        }

        var notes = new List<string>();

        var mode = p?.TryGetProperty("mode", out var mv) == true ? mv.GetString() : null;
        if (mode is not null)
        {
            var normalized = mode.ToLowerInvariant() switch
            {
                "custom" => "manual",
                "system" => "auto",
                "auto" or "direct" or "manual" => mode.ToLowerInvariant(),
                _ => throw RpcException.ParamsInvalid(
                    "mode 只能是 auto/direct/manual（收到 " + mode + "）"),
            };
            cfg.Proxy.Mode = normalized;
        }

        if (p?.TryGetProperty("url", out var uv) == true)
            cfg.Proxy.Url = NullIfBlank(uv.GetString());

        if (p?.TryGetProperty("retry", out var rv) == true && rv.ValueKind == JsonValueKind.Number)
            cfg.Retry = Math.Clamp(rv.GetInt32(), 0, 8);

        // 瘦身：headers/headersEnabled/providerHeaders 已删除；收到则直接拒绝（破坏性口径）
        if (p?.TryGetProperty("headers", out _) == true ||
            p?.TryGetProperty("headersEnabled", out _) == true ||
            p?.TryGetProperty("providerHeaders", out _) == true)
            throw RpcException.ParamsInvalid("saveNetwork 已瘦身：仅支持 mode/url/retry，请求头请走 saveProviders");

        ConfigStore.Save(cfg);

        return Task.FromResult<object?>(new SettingsSaveNetworkResult
        {
            Ok = true,
            Notes = notes,
        });
    }

    public async Task<object?> SettingsProbeModelsAsync(JsonElement? p, CancellationToken ct)
    {
        var baseUrl = p?.TryGetProperty("baseUrl", out var bv) == true ? bv.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            throw RpcException.ParamsInvalid("baseUrl 必须是合法绝对地址");

        var apiKey = p?.TryGetProperty("apiKey", out var kv) == true ? kv.GetString() : null;
        var providerId = p?.TryGetProperty("providerId", out var piv) == true ? piv.GetString() : null;
        bool clearKey = p?.TryGetProperty("clearKey", out var ckv) == true && ckv.ValueKind == JsonValueKind.True;

        // apiKey 回落：newKey 空 + providerId 非空 + 库中有 key + 未点 clearKey 才回落（全程不打明文日志）。
        if (string.IsNullOrWhiteSpace(apiKey) && !clearKey && !string.IsNullOrWhiteSpace(providerId))
        {
            try
            {
                var cfgKey = ConfigStore.Load();
                if (cfgKey.Providers.TryGetValue(providerId!.Trim(), out var kd) && !string.IsNullOrEmpty(kd.ApiKey))
                    apiKey = kd.ApiKey;
            }
            catch { }
        }

        List<KeyValuePair<string, string>> headers = new();
        JsonElement hv = default;
        bool hasHeadersField = p?.TryGetProperty("headers", out hv) == true && hv.ValueKind == JsonValueKind.Array;
        if (hasHeadersField)
        {
            foreach (var el in hv.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var key = el.TryGetProperty("key", out var k) ? k.GetString()?.Trim()
                    : el.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
                var val = el.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(key) || val is null) continue;
                headers.Add(new(key!, val));
            }
            // 空数组视同缺失：回落库中头（与前端空数组省传对称，后端兜底）。
            if (headers.Count == 0 && !string.IsNullOrWhiteSpace(providerId))
            {
                try
                {
                    var cfgHdr = ConfigStore.Load();
                    if (cfgHdr.Providers.TryGetValue(providerId!.Trim(), out var pd))
                        headers = pd.Headers.Select(kv2 => new KeyValuePair<string, string>(kv2.Key, kv2.Value)).ToList();
                }
                catch { }
            }
        }
        else if (!string.IsNullOrWhiteSpace(providerId))
        {
            try
            {
                var cfgProbe = ConfigStore.Load();
                if (cfgProbe.Providers.TryGetValue(providerId!.Trim(), out var pd))
                    headers = pd.Headers.Select(kv2 => new KeyValuePair<string, string>(kv2.Key, kv2.Value)).ToList();
            }
            catch { }
        }

        // 代理：表单试算值优先（useProxy），否则读库折叠
        string? proxyUrl = null;
        bool? useProxyParam = p?.TryGetProperty("useProxy", out var upv) == true
            ? (upv.ValueKind == JsonValueKind.True ? true : upv.ValueKind == JsonValueKind.False ? false : (bool?)null) : null;
        try
        {
            var cfgForProxy = ConfigStore.Load();
            var (avail, gurl) = ConfigStore.GlobalProxy(cfgForProxy);
            bool useProxy = useProxyParam
                ?? (providerId is not null && cfgForProxy.Providers.TryGetValue(providerId.Trim(), out var ppd) ? ppd.UseProxy : true);
            proxyUrl = (avail && useProxy) ? gurl : null;
        }
        catch { }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var client = new OpenAiCompatibleClient(new LlmConfig
        {
            BaseUrl = baseUrl!.Trim(),
            Model = "",
            ApiKey = apiKey,
            ProxyUrl = proxyUrl,
            Headers = headers,
        });

        try
        {
            var models = await client.ListModelsAsync(cts.Token).ConfigureAwait(false);
            return new SettingsProbeModelsResult { Models = models.ToList() };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new RpcException(RpcError.LlmTimeout,
                "探测模型超时（10 秒）", "检查网络或服务地址");
        }
        catch (HttpRequestException ex)
        {
            throw new RpcException(RpcError.LlmError,
                "探测失败：" + ex.Message, "检查 BaseUrl 与 API Key");
        }
        finally
        {
            client.Dispose();
        }
    }

    public Task<object?> SettingsMcpGetAsync(JsonElement? p, CancellationToken ct)
    {
        var path = Path.Combine(_dataDir, "mcp.json");
        var text = "";
        if (File.Exists(path))
        {
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                throw new RpcException(RpcError.Internal, "读取 mcp.json 失败：" + ex.Message);
            }
        }
        return Task.FromResult<object?>(new SettingsMcpGetResult { Path = path, Text = text });
    }

    public Task<object?> SettingsMcpSaveAsync(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var tv) == true ? tv.GetString() : null;
        if (text is null)
            throw RpcException.ParamsInvalid("text 不能为空");

        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw RpcException.ParamsInvalid("mcp.json 顶层必须是对象");
            }
            catch (JsonException ex)
            {
                throw new RpcException(RpcError.ParamsInvalid,
                    "mcp.json 不是合法 JSON：" + ex.Message, "第 " + ex.LineNumber + " 行");
            }
        }

        var path = Path.Combine(_dataDir, "mcp.json");
        Directory.CreateDirectory(_dataDir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);

        return Task.FromResult<object?>(new SettingsMcpSaveResult
        {
            Ok = true,
            Notes = new[] { "已保存；重启应用后生效" },
        });
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
