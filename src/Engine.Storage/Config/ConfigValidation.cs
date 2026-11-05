using System.Text.RegularExpressions;

namespace HaloAgent.Engine.Storage.Config;

/// <summary>整树校验（06 校验矩阵）。失败返回 errors(provider,model,field,message)。</summary>
public sealed record ConfigValidationError(string Provider, string? Model, string Field, string Message);

public static class ConfigValidation
{
    private static readonly Regex ProviderIdRe = new(@"^[a-z0-9][a-z0-9-_]{0,63}$", RegexOptions.Compiled);
    // model.id 放宽：字母数字 / : . - _，首尾非空白
    private static readonly Regex ModelIdRe = new(@"^[A-Za-z0-9][A-Za-z0-9/:\.\-_]{0,127}$", RegexOptions.Compiled);

    public static IReadOnlyList<ConfigValidationError> ValidateTree(ConfigModel cfg)
    {
        var errs = new List<ConfigValidationError>();

        // providers
        foreach (var (pid, p) in cfg.Providers)
        {
            if (string.IsNullOrWhiteSpace(pid) || !ProviderIdRe.IsMatch(pid))
                errs.Add(new(pid, null, "id", "provider.id 非法：^[a-z0-9][a-z0-9-_]{0,63}$"));
             // 别名原则：null/空/与 id 精确相等(Ordinal)均视为无别名，不报错；长度仍 ≤64。
             if (p.DisplayName is not null && p.DisplayName.Length > 64)
                 errs.Add(new(pid, null, "displayName", "别称不超过 64 字"));
            if (string.IsNullOrWhiteSpace(p.BaseUrl))
                errs.Add(new(pid, null, "baseUrl", "baseUrl 必填"));
            else
            {
                var u = p.BaseUrl.Trim().TrimEnd('/');
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    errs.Add(new(pid, null, "baseUrl", "baseUrl 必须是绝对 http(s) 地址"));
              // network 枚举校验（非法直接拦；未知回落只发生在读归一，写前此处已拦）
              var _net = (p.Network ?? "").Trim().ToLowerInvariant();
              if (_net is not ("follow" or "proxy" or "direct"))
                  errs.Add(new(pid, null, "network", "network 须为 follow/proxy/direct"));
              // P26：response_store 三档校验（非法直接拦）
              var _rs = (p.ResponseStore ?? "").Trim().ToLowerInvariant();
              if (_rs is not ("default" or "true" or "false"))
                  errs.Add(new(pid, null, "response_store", "response_store 须为 default/true/false"));
             }
            // headers
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in p.Headers)
            {
                if (string.IsNullOrWhiteSpace(k) || k.Any(c => char.IsWhiteSpace(c) || c == ':'))
                {
                    errs.Add(new(pid, null, "headers", $"请求头名非法：{k}"));
                    continue;
                }
                if (k.Length > 128)
                    errs.Add(new(pid, null, "headers", $"请求头名超长（≤128）：{k}"));
                if (!seen.Add(k))
                    errs.Add(new(pid, null, "headers", $"请求头名重复（大小写不敏感）：{k}"));
                if (ConfigStore.IsReservedHeader(k))
                {
                    errs.Add(new(pid, null, "headers", $"保留头不可自定义：{k}"));
                    continue;
                }
                if (v is null) { errs.Add(new(pid, null, "headers", $"请求头值不能为空：{k}")); continue; }
                if (v.Length > 4096)
                    errs.Add(new(pid, null, "headers", $"请求头值超长（≤4k）：{k}"));
                if (v.Length == 0)
                    errs.Add(new(pid, null, "headers", $"请求头值为空（默认硬拒绝）：{k}"));
            }
            // models
            var mids = new HashSet<string>(StringComparer.Ordinal);
            if (p.Models.Count > 50)
            {
                // warning 级别由调用方转 notes；此处不计入 errors
            }
            foreach (var m in p.Models)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || m.Id.Trim() != m.Id)
                    errs.Add(new(pid, m.Id, "id", "model.id 非空且首尾非空白"));
                else if (!ModelIdRe.IsMatch(m.Id.Trim()) && m.Id.Trim().Length <= 128)
                {
                    // 允许空格以外的可打印？按 06：放宽字符集；首尾非空白 + ≤128 + 允许/ : . -
                    // 若含非法字符则报错
                    if (m.Id.Trim().Length > 128 || m.Id.Any(c => char.IsWhiteSpace(c)))
                        errs.Add(new(pid, m.Id, "id", "model.id 非法：同 provider 内唯一，≤128，允许字母数字/ : . - _"));
                }
                if (m.Id.Length > 128)
                    errs.Add(new(pid, m.Id, "id", "model.id 超长（≤128）"));
                if (!mids.Add(m.Id))
                    errs.Add(new(pid, m.Id, "id", "model.id 在同一 provider 内必须唯一"));
                var proto = (m.Protocol ?? "chat").ToLowerInvariant();
                if (proto != "chat" && proto != "responses")
                    errs.Add(new(pid, m.Id, "protocol", "protocol 须为 chat 或 responses"));
                if (m.ContextWindow is not null && (m.ContextWindow < 1000 || m.ContextWindow > 2_000_000))
                    errs.Add(new(pid, m.Id, "contextWindow", "contextWindow 须为 null 或 1000~2000000"));
                if (m.MaxTokens is not null && (m.MaxTokens < 1 || m.MaxTokens > 128_000))
                    errs.Add(new(pid, m.Id, "maxTokens", "maxTokens 须为 null 或 1~128000"));
            }
        }

         // active
         var ap = cfg.ActiveProvider?.Trim() ?? "";
         var am = cfg.ActiveModel?.Trim() ?? "";
         if (string.IsNullOrEmpty(ap) && string.IsNullOrEmpty(am))
         {
             // 未配置态：允许（整树空 providers 也允许保存）
         }
         else if (string.IsNullOrEmpty(ap) || string.IsNullOrEmpty(am))
         {
             errs.Add(new(ap, am, "active", "active_provider 与 active_model 必须同时存在或同时为空"));
         }
         else
         {
             if (!cfg.Providers.TryGetValue(ap, out var p))
                 errs.Add(new(ap, am, "active", $"active provider 不存在：{ap}"));
             else if (p.Models.Count == 0)
                 errs.Add(new(ap, am, "active", "active 不可指向空 provider"));
             else if (!p.Models.Any(m => string.Equals(m.Id, am, StringComparison.Ordinal)))
                 errs.Add(new(ap, am, "active", $"active model 不存在：{ap}/{am}"));
         }
 
         // 行为 5 项同样纳入整树校验（两套范围不漂移）
         errs.AddRange(ValidateBehavior(cfg));
 
         return errs;
     }
 
     /// null = 缺席（沿用默认），不拦；persona 格式必查，存在性仅当 personaExists 传入时查
     /// （Storage 不引用 Engine.Core，内置存在性由调用方 Server 层注入，避免铁律破环）。</summary>
     public static IReadOnlyList<ConfigValidationError> ValidateBehavior(ConfigModel cfg, Func<string, bool>? personaExists = null)
     {
         return ValidateBehaviorValues(cfg.MaxTurns, cfg.Retry, cfg.Persona, cfg.CompactKeepTail, cfg.AutoCompactOnLimit, personaExists);
     }
 
     /// <summary>行为 5 字段值校验（供 saveBehavior 直接用原始值，避免 ConfigModel 装箱）。</summary>
     public static IReadOnlyList<ConfigValidationError> ValidateBehaviorValues(int? maxTurns, int? retry, string? persona, int? compactKeepTail, bool? autoCompact, Func<string, bool>? personaExists = null)
     {
         var errs = new List<ConfigValidationError>();
         if (maxTurns.HasValue)
         {
             var v = maxTurns.Value;
             if (!(v == 0 || (v >= 1 && v <= 500)))
                 errs.Add(new("", null, "maxTurns", "maxTurns 须为整数且 0 或 1~500（0=不限）"));
         }
         if (retry.HasValue)
         {
             var v = retry.Value;
             if (v < 0 || v > 8)
                 errs.Add(new("", null, "retry", "retry 须为 0~8"));
         }
         if (compactKeepTail.HasValue)
         {
             var v = compactKeepTail.Value;
             if (v < 1 || v > 10)
                 errs.Add(new("", null, "compactKeepTail", "compactKeepTail 须为 1~10"));
         }
         // autoCompactOnLimit：bool，无校验
         var name = (persona ?? "").Trim();
         if (name.Length == 0)
         {
             // 空串通过（=无人格）
         }
         else
         {
             if (name.Length > 64)
                 errs.Add(new("", null, "persona", "persona 不超过 64 字"));
             else if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                 || name.Contains('/') || name.Contains('\\')
                 || name is "." or "..")
                 errs.Add(new("", null, "persona", "persona 含非法文件名字符"));
             else if (personaExists is not null && !personaExists(name))
                 errs.Add(new("", null, "persona", $"persona 不存在：{name}"));
         }
         return errs;
     }
 
 

      /// <summary>maxTokens &gt; contextWindow 时 warning（不拦）。alias==id 聚合为单条提示。代理 warning 与 TryResolve 同源。</summary>
      public static List<string> Warnings(ConfigModel cfg)
      {
          var w = new List<string>();
          int aliasEqId = 0;
          var (geff, gwarn) = ConfigStore.ResolveGlobalEffective(cfg.Proxy ?? new ProxyConfig());
          _ = geff;
          if (!string.IsNullOrEmpty(gwarn)) w.Add(gwarn);
          foreach (var (pid, p) in cfg.Providers)
          {
              if (string.Equals(p.DisplayName?.Trim(), pid, StringComparison.Ordinal)) aliasEqId++;
              if (p.Models.Count > 50)
                  w.Add($"服务商「{pid}」模型数 {p.Models.Count} 超过 50（建议精简）");
              foreach (var m in p.Models)
              {
                  if (string.Equals(m.DisplayName?.Trim(), m.Id, StringComparison.Ordinal)) aliasEqId++;
                  if (m.ContextWindow is not null && m.MaxTokens is not null && m.MaxTokens > m.ContextWindow)
                      w.Add($"模型 {pid}/{m.Id} 的 maxTokens 大于 contextWindow（仅提示，不拦截）");
              }
              var network = ConfigStore.NormalizeProviderNetwork(p.Network, p.UseProxyLegacy);
              if (network == "proxy")
              {
                  var (_, pwarn) = ConfigStore.TryResolveEffectiveProxy(cfg.Proxy ?? new ProxyConfig(), network);
                  if (!string.IsNullOrEmpty(pwarn) && !w.Contains(pwarn))
                      w.Add(pwarn);
              }
          }
          if (aliasEqId > 0)
              w.Add($"有 {aliasEqId} 个别名与 id 相同，已视为无别名（仅提示，不拦截）");
          return w;
      }
 }
