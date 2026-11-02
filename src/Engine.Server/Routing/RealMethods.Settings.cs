 using System.Text.Json;
 using HaloAgent.Engine.Abstractions.Frames;
 using HaloAgent.Engine.Abstractions.Methods;
 using HaloAgent.Engine.Abstractions.Types;
 using HaloAgent.Engine.Core.Llm;
 using HaloAgent.Engine.Storage;
 using HaloAgent.Engine.Storage.Config;
 using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Server.Routing;

public sealed partial class RealMethods
{
     public Task<object?> SettingsGetAsync(JsonElement? p, CancellationToken ct)
     {
         var cfg = ConfigStore.Load();
         // 旧形状嗅探：空新模型 + migrationRequired=true（Load 已备份一次）
         bool migrationRequired = IsOldShapeOnDisk();
         var mode = ConfigStore.NormalizeGlobalMode(cfg.Proxy?.Mode);
         var providers = cfg.Providers
             .OrderBy(kv => kv.Key, StringComparer.Ordinal)
              .Select(kv =>
              {
                  var network = ConfigStore.NormalizeProviderNetwork(kv.Value.Network, kv.Value.UseProxyLegacy);
                  // P18：每个 Provider 下发 effective（inUse/source/maskedUrl + warning）
                  var (eff, warn) = ConfigStore.TryResolveEffectiveProxy(cfg.Proxy ?? new ProxyConfig(), network);
                  string source;
                  if (!string.IsNullOrWhiteSpace(kv.Value.BaseUrl) && ConfigStore.IsBypassedUrl(kv.Value.BaseUrl))
                  {
                      eff = HaloAgent.Engine.Abstractions.Types.EffectiveProxy.None;
                      source = HaloAgent.Engine.Abstractions.Types.ProxySources.BypassLocal;
                  }
                  else if (network == "direct") source = HaloAgent.Engine.Abstractions.Types.ProxySources.ProviderDirect;
                  else if (network == "proxy") source = HaloAgent.Engine.Abstractions.Types.ProxySources.ProviderProxy;
                  else source = mode switch
                  {
                      "manual" => HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalManual,
                      "system" => HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalSystem,
                      _ => HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalDirect,
                  };
                  string? masked = eff.Kind == HaloAgent.Engine.Abstractions.Types.ProxyKind.Manual
                      ? HaloAgent.Engine.Core.Net.HttpClientFactory.MaskProxy(eff.Url ?? "") : null;
                  return new ProviderView
                  {
                      Id = kv.Key,
                      DisplayName = NormalizeAlias(kv.Key, kv.Value.DisplayName),
                      BaseUrl = kv.Value.BaseUrl ?? "",
                      ApiKeySet = !string.IsNullOrEmpty(kv.Value.ApiKey),
                      HeadersEnabled = kv.Value.HeadersEnabled,
                      Headers = (kv.Value.Headers ?? new Dictionary<string, string>())
                          .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                          .Select(h => new HeaderPair { Name = h.Key, Value = h.Value }).ToList(),
                      Network = network,
 #pragma warning disable CS0618
                      UseProxy = network != "direct",
 #pragma warning restore CS0618
                      Effective = new ProviderEffective
                      {
                          InUse = eff.InUse,
                          Source = source,
                          MaskedUrl = masked,
                          Warning = string.IsNullOrEmpty(warn) ? null : warn,
                      },
                      ModelCount = kv.Value.Models.Count,
                      Models = kv.Value.Models.Select(m => new ModelView
                      {
                          Id = m.Id,
                          DisplayName = NormalizeAlias(m.Id, m.DisplayName),
                          Protocol = (m.Protocol ?? "chat").ToLowerInvariant(),
                          ContextWindow = m.ContextWindow,
                          MaxTokens = m.MaxTokens,
                          AcceptsImages = m.AcceptsImages,
                          IncludeReasoningHistory = m.IncludeReasoningHistory,
                      }).ToList(),
                  };
              })
             .ToList();
         return Task.FromResult<object?>(new SettingsGetResult
         {
             ActiveProvider = cfg.ActiveProvider ?? "",
             ActiveModel = cfg.ActiveModel ?? "",
             MigrationRequired = migrationRequired,
             Providers = providers,
              Proxy = new ProxySettings { Mode = mode, Url = cfg.Proxy?.Url },
             Retry = Math.Clamp(cfg.Retry ?? 0, 0, 8),
             MaxTurns = Math.Clamp(cfg.MaxTurns ?? 200, 0, 500),
             Persona = cfg.Persona ?? "",
             CompactKeepTail = Math.Clamp(cfg.CompactKeepTail ?? 2, 1, 10),
             AutoCompactOnLimit = cfg.AutoCompactOnLimit ?? false,
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

         // 覆盖语义：migrationRequired 或 config.overwriteRequired 两步确认后前端传 force=true，才允许覆盖旧结构/损坏配置。
         bool force = p.Value.TryGetProperty("force", out var forceEl) && forceEl.ValueKind == JsonValueKind.True;
 
         var warnings = new List<string>();
         ConfigModel cfg;
         try { cfg = ConfigStore.LoadForWrite(force); }
         catch (InvalidOperationException ex)
         {
             throw new RpcException(RpcError.ConfigOverwriteRequired,
                 "配置文件为旧结构或已损坏，已拒绝保存以免覆盖：" + ex.Message,
                 "原文件已自动备份，确认用当前整树覆盖请再点一次保存");
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
             CompactKeepTail = cfg.CompactKeepTail,
             AutoCompactOnLimit = cfg.AutoCompactOnLimit,
             Proxy = cfg.Proxy,
         };

        // apiKey 缺席或空 = 不改；clearKey=true 清除（与 apiKey 同传时 clear 优先 + warning）
        foreach (var prop in pv.EnumerateObject())
        {
            var pid = prop.Name.Trim();
             var entry = ParseProviderEntry(prop.Value);
             if (entry.NetworkRaw is not null && entry.Network is not ("follow" or "proxy" or "direct"))
                 throw RpcException.ParamsInvalid($"服务商「{pid}」network 非法（须为 follow/proxy/direct，收到 {entry.NetworkRaw}）");
             cfg.Providers.TryGetValue(pid, out var existing);

              var pcfg = new ProviderConfig
              {
                  DisplayName = entry.HasDisplayName ? NormalizeAlias(pid, entry.DisplayName) : existing?.DisplayName,
                  BaseUrl = (entry.BaseUrl ?? existing?.BaseUrl ?? "").Trim().TrimEnd('/'),
                  ApiKey = existing?.ApiKey,
                  HeadersEnabled = entry.HeadersEnabled ?? existing?.HeadersEnabled ?? true,
                  Headers = entry.Headers ?? new Dictionary<string, string>(existing?.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                  Network = entry.Network ?? ConfigStore.NormalizeProviderNetwork(existing?.Network, existing?.UseProxyLegacy ?? entry.UseProxy),
                 Models = entry.Models?.Select(m => new ModelConfig
                 {
                     Id = m.Id.Trim(),
                     DisplayName = NormalizeAlias(m.Id.Trim(), m.DisplayName),
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
         // P12-02：保存即生效（此前只落盘不进内存，改 key/baseUrl 后需重启才生效）。
         // active 指针沿用旧值；Resolve 失败（删了 active 目标/旧形状）则显式置未配置并 warning。
         try
         {
            var resolvedSave = ConfigStore.Resolve(next);
            try { _session.ReconfigureActive(resolvedSave); }
            catch (NotSupportedException ex)
            {
                _session.ClearActive();
                warnings.Add("已保存，但生效模型协议暂不支持，已置为未配置态（" + ex.Message + "）");
            }
         }
         catch (Exception ex)
         {
             _session.ClearActive();
             warnings.Add("已保存，但 active 指向无效，已置为未配置态，请重选 active 后再试（" + ex.Message + "）");
         }
         return Task.FromResult<object?>(new SettingsSaveProvidersResult
         {
             Ok = true,
             Notes = Array.Empty<string>(),
             Warnings = warnings,
             Errors = Array.Empty<SettingsFieldError>(),
         });
    }

      private sealed record ParsedProvider(
          string? DisplayName, bool HasDisplayName, string? BaseUrl, string? ApiKey, bool ClearKey,
          bool? HeadersEnabled, Dictionary<string, string>? Headers, string? Network, string? NetworkRaw, bool? UseProxy,
          List<ModelSaveEntry>? Models);
 
     private static ParsedProvider ParseProviderEntry(JsonElement el)
     {
          bool hasDisplayName = el.TryGetProperty("displayName", out var dn);
          string? displayName = hasDisplayName ? dn.GetString() : null;
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
         // P18：network 三选项（读时兼容旧 useProxy）
         string? networkRaw = el.TryGetProperty("network", out var nw) ? nw.GetString() : null;
         string? network = null;
         if (networkRaw is not null)
         {
             var n = networkRaw.Trim().ToLowerInvariant();
             network = n is ("follow" or "proxy" or "direct") ? n : n; // 非法值原样透出，由调用方拒存
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
          return new ParsedProvider(displayName, hasDisplayName, baseUrl, apiKey, clearKey, headersEnabled, headers, network, networkRaw, useProxy, models);
      }

     /// <summary>P14 别名原则：无别名一律存 null。空白/与 id 精确相等(Ordinal)均视为无别名。</summary>
     private static string? NormalizeAlias(string id, string? displayName)
     {
         if (string.IsNullOrWhiteSpace(displayName)) return null;
         var t = displayName.Trim();
         return string.Equals(t, id, StringComparison.Ordinal) ? null : t;
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
         _ = PublishAgentChangedAsync($"已切换模型：{providerId}/{modelId}");
         return Task.FromResult<object?>(new SettingsSetActiveResult { Ok = true });
     }
     // P12-02：此前为空实现（BuildSnapshot 后直接丢弃）。RealMethods 无 EventBus 成员，
     // 经 _session 复用存量 notice 事件提示，不新增事件 kind、不改契约。
     private Task PublishAgentChangedAsync(string text)
     {
         try
         {
             return _session.PublishAgentChangedAsync(text, CancellationToken.None);
         }
         catch (Exception ex)
         {
             try { _log.LogDebug(ex, "发布模型切换提示失败"); } catch { }
             return Task.CompletedTask;
         }
     }

     /// <summary>P22 行为页保存：5 项全局行为（LoadForWrite → Validate → Save → 活应用）。</summary>
     public Task<object?> SettingsSaveBehaviorAsync(JsonElement? p, CancellationToken ct)
     {
         ConfigModel cfg;
         try { cfg = ConfigStore.LoadForWrite(); }
         catch (InvalidOperationException ex)
         {
             throw new RpcException(RpcError.Internal,
                 "配置文件损坏，已拒绝保存以免覆盖：" + ex.Message);
         }
         int? maxTurns = null; int? retry = null; string? persona = null;
         int? compactKeepTail = null; bool? autoCompact = null;
         if (p is not null)
         {
             if (p.Value.TryGetProperty("maxTurns", out var mv))
             {
                 if (mv.ValueKind == JsonValueKind.Null) maxTurns = null;
                 else if (mv.ValueKind == JsonValueKind.Number && mv.TryGetInt32(out var v)) maxTurns = v;
                 else throw RpcException.ParamsInvalid("maxTurns 须为整数且 0 或 1~500（0=不限）");
             }
             if (p.Value.TryGetProperty("retry", out var rv))
             {
                 if (rv.ValueKind == JsonValueKind.Null) retry = null;
                 else if (rv.ValueKind == JsonValueKind.Number && rv.TryGetInt32(out var v)) retry = v;
                 else throw RpcException.ParamsInvalid("retry 须为 0~8");
             }
             if (p.Value.TryGetProperty("persona", out var pv))
             {
                 if (pv.ValueKind == JsonValueKind.Null) persona = null;
                 else if (pv.ValueKind == JsonValueKind.String) persona = pv.GetString() ?? "";
                 else throw RpcException.ParamsInvalid("persona 须为字符串");
             }
             if (p.Value.TryGetProperty("compactKeepTail", out var cv))
             {
                 if (cv.ValueKind == JsonValueKind.Null) compactKeepTail = null;
                 else if (cv.ValueKind == JsonValueKind.Number && cv.TryGetInt32(out var v)) compactKeepTail = v;
                 else throw RpcException.ParamsInvalid("compactKeepTail 须为 1~10");
             }
             if (p.Value.TryGetProperty("autoCompactOnLimit", out var av))
             {
                 if (av.ValueKind == JsonValueKind.True) autoCompact = true;
                 else if (av.ValueKind == JsonValueKind.False) autoCompact = false;
                 else if (av.ValueKind == JsonValueKind.Null) autoCompact = null;
                 else throw RpcException.ParamsInvalid("autoCompactOnLimit 须为布尔");
             }
         }
         var errs = ConfigValidation.ValidateBehaviorValues(maxTurns, retry, persona, compactKeepTail, autoCompact, PersonaExistsForBehavior);
         if (errs.Count > 0)
         {
             var first = errs[0];
             if (first.Field == "persona" && first.Message.StartsWith("persona 不存在"))
                 throw new RpcException(RpcError.ParamsInvalid, "参数不合法: [persona] " + first.Message, "到设置→资源→人格新建");
             throw RpcException.ParamsInvalid($"[{first.Field}] {first.Message}");
         }
         bool personaCleared = false;
         if (maxTurns.HasValue) cfg.MaxTurns = maxTurns.Value;
         if (retry.HasValue) cfg.Retry = retry.Value;
         if (persona is not null)
         {
             var trimmed = persona.Trim();
             if (trimmed.Length == 0 && (cfg.Persona ?? "").Length > 0) personaCleared = true;
             cfg.Persona = trimmed;
         }
         if (compactKeepTail.HasValue) cfg.CompactKeepTail = compactKeepTail.Value;
         if (autoCompact.HasValue) cfg.AutoCompactOnLimit = autoCompact.Value;
         ConfigStore.Save(cfg);
         var warnings = new List<string>(ConfigValidation.Warnings(cfg));
         if (personaCleared) warnings.Add("已清空默认人格（note）");
         try
         {
             var resolved = ConfigStore.Resolve(cfg);
             try { _session.ReconfigureActive(resolved); }
             catch (NotSupportedException ex)
             {
                 _session.ClearActive();
                 warnings.Add("已保存，但生效模型协议暂不支持，已置为未配置态（" + ex.Message + "）");
             }
         }
         catch (Exception ex)
         {
             _session.ClearActive();
             warnings.Add("已保存，但 active 指向无效，已置为未配置态，请重选 active 后再试（" + ex.Message + "）");
         }
         return Task.FromResult<object?>(new SettingsSaveBehaviorResult
         {
             Ok = true,
             Warnings = warnings.Distinct().ToList(),
         });
     }
 
     private bool PersonaExistsForBehavior(string name)
     {
         try
         {
             if (HaloAgent.Engine.Core.Agent.Prompts.PersonaBuiltin.GetBuiltin(name) is not null) return true;
             var list = HaloAgent.Engine.Storage.Personas.PersonaStore.List(_dataDir);
             return list.Contains(name, StringComparer.Ordinal);
         }
         catch { return false; }
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
         var proxyCfg = cfg.Proxy ??= new ProxyConfig();
         var notes = new List<string>();
         var warnings = new List<string>();
         // P18：热切只看 mode/url（retry 不触发）
         var oldMode = ConfigStore.NormalizeGlobalMode(cfg.Proxy?.Mode);
         var oldUrl = (cfg.Proxy?.Url ?? "").Trim();
         var mode = p?.TryGetProperty("mode", out var mv) == true ? mv.GetString() : null;
         if (mode is not null)
         {
             var m = mode.Trim().ToLowerInvariant();
             string normalized = m switch
             {
                 // P18 新三档
                 "system" => "system",
                 "direct" => "direct",
                 "manual" => "manual",
                 // 旧映射保留
                 "custom" => "manual",
                 // 旧 auto 实际=直连，守恒 + warning
                 "auto" => "direct",
                 _ => throw RpcException.ParamsInvalid(
                     "mode 只能是 system/direct/manual（收到 " + mode + "）"),
             };
             if (m == "auto")
                 warnings.Add("全局代理 auto 已按直连归一为 direct（旧 auto 实际即直连）");
             proxyCfg.Mode = normalized;
         }
         if (p?.TryGetProperty("url", out var uv) == true)
             proxyCfg.Url = NullIfBlank(uv.GetString());
         if (p?.TryGetProperty("retry", out var rv) == true && rv.ValueKind == JsonValueKind.Number)
         {
             if (!rv.TryGetInt32(out var rvi) || rvi < 0 || rvi > 8)
                 throw RpcException.ParamsInvalid("retry 须为 0~8");
             cfg.Retry = Math.Clamp(rvi, 0, 8);
             warnings.Add("retry 已搬家至行为页，此处为兼容保留");
         }
         // 瘦身：headers/headersEnabled/providerHeaders 已删除；收到则直接拒绝（破坏性口径）
         if (p?.TryGetProperty("headers", out _) == true ||
             p?.TryGetProperty("headersEnabled", out _) == true ||
             p?.TryGetProperty("providerHeaders", out _) == true)
             throw RpcException.ParamsInvalid("saveNetwork 已瘦身：仅支持 mode/url/retry，请求头请走 saveProviders");
         // P18：manual 下 url 必填 + http(s) 校验，失败即拒存
         if (ConfigStore.NormalizeGlobalMode(proxyCfg.Mode) == "manual")
         {
             if (string.IsNullOrWhiteSpace(proxyCfg.Url) || !ConfigStore.IsValidProxyUrl(proxyCfg.Url!))
                 throw RpcException.ParamsInvalid("manual 模式下 url 必填且须为 http(s):// 开头");
         }
         ConfigStore.Save(cfg);
         // P18：代理组合 warning（与 TryResolve 同源）进 Warnings
         warnings.AddRange(ConfigValidation.Warnings(cfg));
         // P18 热切：仅 mode/url 变化触发 Resolve→Reconfigure（retry 不触发）；失败回 warning 不回滚保存
         var newMode = ConfigStore.NormalizeGlobalMode(proxyCfg.Mode);
         var newUrl = (proxyCfg.Url ?? "").Trim();
         if (!string.Equals(oldMode, newMode, StringComparison.Ordinal) || !string.Equals(oldUrl, newUrl, StringComparison.Ordinal))
         {
             try
             {
                 var resolvedNet = ConfigStore.Resolve(cfg);
                 try { _session.ReconfigureActive(resolvedNet); }
                 catch (NotSupportedException ex)
                 {
                     _session.ClearActive();
                     warnings.Add("已保存，但生效模型协议暂不支持，已置为未配置态（" + ex.Message + "）");
                 }
             }
             catch (Exception ex)
             {
                 // 代理组合永不抛；仅 active 非法（删 active 目标等）才到此分支
                 _session.ClearActive();
                 warnings.Add("已保存，但 active 指向无效，已置为未配置态，请重选 active 后再试（" + ex.Message + "）");
             }
         }
         return Task.FromResult<object?>(new SettingsSaveNetworkResult
         {
             Ok = true,
             Notes = notes,
             Warnings = warnings.Distinct().ToList(),
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

         // P18 代理：表单 network 试算值优先（读时兼容旧 useProxy），否则读库折叠；回包带 via + latencyMs
         HaloAgent.Engine.Abstractions.Types.EffectiveProxy effProxy = HaloAgent.Engine.Abstractions.Types.EffectiveProxy.None;
         string effSource = HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalDirect;
         string effWarn = "";
         string? networkParam = p?.TryGetProperty("network", out var nwv) == true ? nwv.GetString() : null;
 #pragma warning disable CS0618
         bool? useProxyParam = p?.TryGetProperty("useProxy", out var upv) == true
             ? (upv.ValueKind == JsonValueKind.True ? true : upv.ValueKind == JsonValueKind.False ? false : (bool?)null) : null;
 #pragma warning restore CS0618
         if (networkParam is not null && networkParam.Trim().ToLowerInvariant() is not ("follow" or "proxy" or "direct"))
             throw RpcException.ParamsInvalid("network 须为 follow/proxy/direct（收到 " + networkParam + "）");
         try
         {
             var cfgForProxy = ConfigStore.Load();
             string network = networkParam is not null
                 ? ConfigStore.NormalizeProviderNetwork(networkParam)
                 : useProxyParam is not null
                     ? (useProxyParam == true ? "follow" : "direct")
                     : (providerId is not null && cfgForProxy.Providers.TryGetValue(providerId.Trim(), out var ppd)
                         ? ConfigStore.NormalizeProviderNetwork(ppd.Network, ppd.UseProxyLegacy) : "follow");
             (effProxy, effWarn) = ConfigStore.TryResolveEffectiveProxy(cfgForProxy.Proxy ?? new ProxyConfig(), network);
             // 旁路：probe 目标命中本地表则直连
             if (ConfigStore.IsBypassedUrl(baseUrl))
             {
                 effProxy = HaloAgent.Engine.Abstractions.Types.EffectiveProxy.None;
                 effSource = HaloAgent.Engine.Abstractions.Types.ProxySources.BypassLocal;
             }
             else if (network == "direct") effSource = HaloAgent.Engine.Abstractions.Types.ProxySources.ProviderDirect;
             else if (network == "proxy") effSource = HaloAgent.Engine.Abstractions.Types.ProxySources.ProviderProxy;
             else effSource = ConfigStore.NormalizeGlobalMode(cfgForProxy.Proxy?.Mode) switch
             {
                 "manual" => HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalManual,
                 "system" => HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalSystem,
                 _ => HaloAgent.Engine.Abstractions.Types.ProxySources.GlobalDirect,
             };
         }
         catch { }
         var sw = System.Diagnostics.Stopwatch.StartNew();
         using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
         cts.CancelAfter(TimeSpan.FromSeconds(10));
         var client = new OpenAiCompatibleClient(new LlmConfig
         {
             BaseUrl = baseUrl!.Trim(),
             Model = "",
             ApiKey = apiKey,
             Proxy = effProxy,
             Headers = headers,
         });

         try
         {
             var models = await client.ListModelsAsync(cts.Token).ConfigureAwait(false);
             // P16-F3：responses 协议加带会话实调 ping（GET /models 不需要 session 头，绿了也可能是假绿）。
             // 只在库中该 provider 已配 responses 模型时才 ping（新 provider 无模型可参照时沿用旧行为，不改契约）。
             string? responsesModel = null;
             try
             {
                 var cfgModels = ConfigStore.Load();
                 if (!string.IsNullOrWhiteSpace(providerId)
                     && cfgModels.Providers.TryGetValue(providerId.Trim(), out var pd)
                     && pd.Models is { Count: > 0 })
                     responsesModel = pd.Models
                         .FirstOrDefault(m => string.Equals(m.Protocol, "responses", StringComparison.OrdinalIgnoreCase))?.Id;
             }
             catch { }
             if (!string.IsNullOrWhiteSpace(responsesModel))
             {
                 using var rclient = new OpenAiCompatibleClient(new LlmConfig
                 {
                     BaseUrl = baseUrl!.Trim(),
                     Model = responsesModel!.Trim(),
                     ApiKey = apiKey,
                     Proxy = effProxy,
                     Headers = headers,
                     Protocol = LlmProtocol.Responses,
                 });
                 try
                 {
                     // 注意传非空 sessionId，否则模板头被跳过、ping 了个寂寞。
                     var pingInput = new object[]
                     {
                         new Dictionary<string, object?>
                         {
                             ["type"] = "message",
                             ["role"] = "user",
                             ["content"] = new object[]
                             {
                                 new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = "ping" },
                             },
                         },
                     };
                     await rclient.ResponsesAsync(null, "ping", pingInput, null,
                         cts.Token, "probe-" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                 }
                 catch (EndpointStatusException ex)
                 {
                     throw new RpcException(RpcError.LlmError,
                         "模型可用但 responses 不通(" + ex.Status + ")：检查 x-opencode-session 头",
                         TruncateForProbe(ex.Body));
                 }
             }
             sw.Stop();
             string? maskedProbe = effProxy.Kind == HaloAgent.Engine.Abstractions.Types.ProxyKind.Manual
                 ? HaloAgent.Engine.Core.Net.HttpClientFactory.MaskProxy(effProxy.Url ?? "") : null;
             return new SettingsProbeModelsResult
             {
                 Models = models.ToList(),
                 Via = new ProbeVia { Source = effSource, MaskedUrl = maskedProbe },
                 LatencyMs = sw.ElapsedMilliseconds,
             };
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
     private static string TruncateForProbe(string s) => s.Length <= 300 ? s : s[..300] + "…";

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
