using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.ModelCatalog;

/// <summary>
/// 供应商模型拉取编排：取服务商 → 解密密钥 → 组 GET /models 请求 → 经代理中枢发送 → 解析 → 差分。
/// 只读快照，不落盘；密钥与秘密头永不记日志。
/// </summary>
public sealed class ProviderModelCatalogService
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    private readonly ProviderConfigService _providerConfigService;
    private readonly HaloHttpClientFactory _httpClientFactory;
    private readonly ModelCatalogIgnoreStore _ignoreStore;

    public ProviderModelCatalogService(
        ProviderConfigService providerConfigService,
        HaloHttpClientFactory httpClientFactory,
        ModelCatalogIgnoreStore ignoreStore
    )
    {
        _providerConfigService =
            providerConfigService ?? throw new ArgumentNullException(nameof(providerConfigService));
        _httpClientFactory =
            httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _ignoreStore = ignoreStore ?? throw new ArgumentNullException(nameof(ignoreStore));
    }

    public async Task<FetchProviderModelsResult> FetchAsync(
        string? providerId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.RequestInvalid,
                "服务商 Id 不能为空"
            );
        }

        ProviderConfig? provider = _providerConfigService.GetProviderById(providerId.Trim());
        if (provider == null)
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.ProviderNotFound,
                "服务商不存在，请刷新后重试"
            );
        }

        string modelsUrl = HeaderVariableResolver.ResolveModelsEndpointUrl(provider.BaseUrl);
        string apiKey = _providerConfigService.GetDecryptedApiKey(provider);
        bool missingKey = string.IsNullOrWhiteSpace(apiKey);
        string proxySummary = _httpClientFactory.ResolveEffectiveProxy(provider).ToDisplaySummary();

        var sw = Stopwatch.StartNew();
        string body;
        try
        {
            body = await DownloadModelsAsync(provider, modelsUrl, apiKey, ct).ConfigureAwait(false);
        }
        catch (ProviderModelCatalogException)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            HaloLogger.App.Warn($"[ModelCatalog] provider={provider.Name} 拉取超时或取消：{ex.Message}");
            throw new ProviderModelCatalogException(CatalogErrorCodes.Timeout, "连接超时，请检查代理与网络");
        }
        catch (HttpRequestException ex)
        {
            HaloLogger.App.Warn($"[ModelCatalog] provider={provider.Name} 网络失败：{ex.Message}");
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.NetworkError,
                $"连接失败：{ex.Message}"
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Warn($"[ModelCatalog] provider={provider.Name} 拉取异常：{ex.Message}");
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.NetworkError,
                $"连接失败：{ex.Message}"
            );
        }
        finally
        {
            sw.Stop();
        }

        var discovered = ProviderModelsParser.Parse(body, out _).ToList();
        if (discovered.Count == 0 && IsUnparsable(body))
        {
            HaloLogger.App.Warn($"[ModelCatalog] provider={provider.Name} 返回不可解析，已截断记录");
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.ParseError,
                "供应商返回不可解析，已记录日志"
            );
        }

        HashSet<string> ignored = _ignoreStore.Load(provider.Id);
        ProviderModelDiffCalculator.ApplyDiff(discovered, provider.Models, ignored);

        HaloLogger.App.Info(
            $"[ModelCatalog] provider={provider.Name} count={discovered.Count} elapsed={sw.ElapsedMilliseconds}ms proxy={proxySummary}"
        );

        return new FetchProviderModelsResult
        {
            ProviderId = provider.Id,
            ModelsUrl = modelsUrl,
            EffectiveProxySummary = proxySummary,
            ElapsedMs = sw.ElapsedMilliseconds,
            Models = discovered
        };
    }

    private async Task<string> DownloadModelsAsync(
        ProviderConfig provider,
        string modelsUrl,
        string apiKey,
        CancellationToken ct
    )
    {
        using var client = _httpClientFactory.CreateClient(provider);
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        request.Headers.UserAgent.ParseAdd("HaloAgent/1.0 (Win7-Win11; CefSharp109)");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var headerCtx = HeaderVariableResolver.CreateContext(
            workspacePath: Environment.CurrentDirectory,
            sessionId: "probe-model-catalog",
            providerId: provider.Id,
            providerName: provider.Name,
            modelId: provider.Models.FirstOrDefault()?.Id ?? "probe-model"
        );
        HeaderVariableResolver.ApplyCustomHeaders(request, provider, headerCtx);

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        int statusCode = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized || (int)response.StatusCode == 403)
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.UpstreamAuth,
                $"鉴权失败：检查 Key 与自定义头（Models 端点：{modelsUrl}）"
            );
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.UpstreamNotFound,
                $"供应商无此路径：检查 BaseUrl（Models 端点：{modelsUrl}）"
            );
        }

        if (statusCode >= 500)
        {
            HaloLogger.App.Warn($"[ModelCatalog] provider={provider.Name} 上游异常 HTTP {statusCode}");
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.UpstreamError,
                $"上游异常 HTTP {statusCode}，稍后重试"
            );
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        char[] buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        int totalBytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            totalBytes += System.Text.Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (totalBytes > MaxBodyBytes)
            {
                throw new ProviderModelCatalogException(
                    CatalogErrorCodes.ParseError,
                    "供应商返回超限，已记录日志"
                );
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static bool IsUnparsable(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        string trimmed = body.TrimStart();
        return !trimmed.StartsWith("{", StringComparison.Ordinal)
            && !trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    public ProvidersConfigDocument Import(ImportDiscoveredModelsRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ProviderId))
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.RequestInvalid,
                "服务商 Id 不能为空"
            );
        }

        var distinctIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in request.ModelIds ?? new List<string>())
        {
            string id = (raw ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;
            }

            distinctIds.Add(id);
        }

        if (distinctIds.Count == 0)
        {
            throw new ProviderModelCatalogException(CatalogErrorCodes.RequestInvalid, "请选择至少一个模型");
        }

        ProviderConfig? provider = _providerConfigService.GetProviderById(
            request.ProviderId.Trim()
        );
        if (provider == null)
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.ProviderNotFound,
                "服务商不存在，请刷新后重试"
            );
        }

        foreach (string id in distinctIds)
        {
            ModelConfig? existing = provider.Models.FirstOrDefault(m =>
                string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)
            );
            if (existing != null)
            {
                if (!request.ImportDisabled && !existing.Enabled)
                {
                    existing.Enabled = true;
                }

                continue;
            }

            // 新模型默认值与新建模型行同源，避免两套魔法数分叉。
            provider.Models.Add(
                new ModelConfig
                {
                    Id = id,
                    DisplayName = id,
                    Protocol = LlmProtocolType.Chat,
                    MaxContextTokens = 128000,
                    MaxOutputTokens = 4096,
                    Temperature = 0.2,
                    Enabled = !request.ImportDisabled,
                    SupportsVision = false,
                    MaxImagesPerTurn = 4,
                    VisionDetail = "high",
                    IncludeReasoningInHistory = true,
                    ThinkTagMode = ThinkTagMode.Auto
                }
            );
        }

        HashSet<string> ignored = _ignoreStore.Load(provider.Id);
        foreach (string id in distinctIds)
        {
            ignored.Remove(id);
        }

        _ignoreStore.Save(provider.Id, ignored);
        return _providerConfigService.UpsertProvider(provider);
    }

    public HashSet<string> SetIgnored(
        string? providerId,
        IEnumerable<string>? modelIds,
        bool ignored
    )
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.RequestInvalid,
                "服务商 Id 不能为空"
            );
        }

        ProviderConfig? provider = _providerConfigService.GetProviderById(providerId.Trim());
        if (provider == null)
        {
            throw new ProviderModelCatalogException(
                CatalogErrorCodes.ProviderNotFound,
                "服务商不存在，请刷新后重试"
            );
        }

        var set = _ignoreStore.Load(provider.Id);
        if (modelIds != null)
        {
            foreach (string? raw in modelIds)
            {
                string id = (raw ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (ignored)
                {
                    set.Add(id);
                }
                else
                {
                    set.Remove(id);
                }
            }
        }

        _ignoreStore.Save(provider.Id, set);
        return set;
    }
}

/// <summary>
/// 供应商模型目录业务异常：携带前端只判码的错误码。
/// </summary>
public sealed class ProviderModelCatalogException : Exception
{
    public ProviderModelCatalogException(string code, string message)
        : base(message)
    {
        Code = code ?? CatalogErrorCodes.RequestInvalid;
    }

    public string Code { get; }
}
