using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Config;

/// <summary>
/// Provider 端点与自定义 Header 预览结果
/// </summary>
public sealed class ProviderEndpointPreviewDto
{
    public string BaseUrl { get; set; } = string.Empty;
    public string NormalizedVersionPrefix { get; set; } = string.Empty;
    public string ChatEndpointUrl { get; set; } = string.Empty;
    public string RespEndpointUrl { get; set; } = string.Empty;
    public string ModelsEndpointUrl { get; set; } = string.Empty;
    public string EffectiveProxySummary { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SessionHash { get; set; } = string.Empty;
    public string WorkspaceName { get; set; } = string.Empty;
    public string WorkspaceHash { get; set; } = string.Empty;
    public Dictionary<string, string> ResolvedHeaders { get; set; } = new();
}

/// <summary>
/// 全局网络设置与多服务商（Provider & Model）持久化配置服务。
/// 负责管理 {DataRootPath}/config/network.json 与 {DataRootPath}/config/providers.json，
/// 并强制执行 API Key / 代理密码的 DPAPI 加密落盘与原子写入。
/// </summary>
public sealed class ProviderConfigService
{
    private readonly StoragePathManager _storagePathManager;
    private readonly HaloHttpClientFactory _httpClientFactory;
    private readonly object _syncLock = new();

    public string NetworkConfigFilePath => Path.Combine(_storagePathManager.ConfigDirectory, "network.json");

    public string ProvidersConfigFilePath => Path.Combine(_storagePathManager.ConfigDirectory, "providers.json");

    public ProviderConfigService(StoragePathManager storagePathManager, HaloHttpClientFactory httpClientFactory)
    {
        _storagePathManager = storagePathManager ?? throw new ArgumentNullException(nameof(storagePathManager));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

        EnsureInitialized();
    }

    /// <summary>
    /// 确保当前数据根目录下的 network.json 与 providers.json 已初始化，并同步至 HaloHttpClientFactory
    /// </summary>
    public void EnsureInitialized()
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_storagePathManager.ConfigDirectory);

            var network = LoadNetworkSettingsInternal();
            _httpClientFactory.UpdateGlobalSettings(network);

            _ = LoadProvidersDocumentInternal();
        }
    }

    /// <summary>
    /// 获取全局网络与代理设置（返回给前端时已掩码脱敏代理密码）
    /// </summary>
    public GlobalNetworkSettings GetNetworkSettingsForClient()
    {
        lock (_syncLock)
        {
            var settings = LoadNetworkSettingsInternal();
            return SanitizeNetworkSettingsForClient(settings);
        }
    }

    /// <summary>
    /// 获取原始全局网络与代理设置（供内部网络层使用）
    /// </summary>
    public GlobalNetworkSettings GetNetworkSettingsInternal()
    {
        lock (_syncLock)
        {
            return LoadNetworkSettingsInternal();
        }
    }

    /// <summary>
    /// 保存全局网络与代理设置（自动执行代理密码 DPAPI 加密、原子写盘并热更新 HaloHttpClientFactory）
    /// </summary>
    public GlobalNetworkSettings SaveNetworkSettings(GlobalNetworkSettings incoming)
    {
        if (incoming == null)
        {
            throw new ArgumentNullException(nameof(incoming));
        }

        lock (_syncLock)
        {
            var existing = AtomicFileWriter.ReadJsonOrDefault<GlobalNetworkSettings>(NetworkConfigFilePath);
            var toSave = incoming.Clone();
            toSave.CustomProxy ??= new ManualProxyConfig();
            toSave.CustomProxy.Scheme = EffectiveProxyDescriptor.NormalizeScheme(toSave.CustomProxy.Scheme);
            toSave.CustomProxy.Host = string.IsNullOrWhiteSpace(toSave.CustomProxy.Host) ? "127.0.0.1" : toSave.CustomProxy.Host.Trim();
            toSave.CustomProxy.Port = toSave.CustomProxy.Port is > 0 and <= 65535 ? toSave.CustomProxy.Port : 7890;
            toSave.DefaultConnectTimeoutSeconds = Math.Clamp(toSave.DefaultConnectTimeoutSeconds, 3, 120);
            toSave.DefaultRequestTimeoutSeconds = Math.Clamp(toSave.DefaultRequestTimeoutSeconds, 5, 3600);

            NormalizeProxySecretForStorage(toSave.CustomProxy, existing?.CustomProxy);

            AtomicFileWriter.WriteJsonAtomic(NetworkConfigFilePath, toSave);
            _httpClientFactory.UpdateGlobalSettings(toSave);

            HaloLogger.App.Info($"Global network proxy settings saved: Mode={toSave.Mode}, Custom={toSave.CustomProxy.Scheme}://{toSave.CustomProxy.Host}:{toSave.CustomProxy.Port}");
            return SanitizeNetworkSettingsForClient(toSave);
        }
    }

    /// <summary>
    /// 获取所有 Provider 与模型配置文档（返回给前端时已剥离明文密钥并附带掩码）
    /// </summary>
    public ProvidersConfigDocument GetProvidersConfigForClient()
    {
        lock (_syncLock)
        {
            var doc = LoadProvidersDocumentInternal();
            return SanitizeProvidersDocumentForClient(doc);
        }
    }

    /// <summary>
    /// 获取内部完整 Provider 配置文档（包含 DPAPI 密文）
    /// </summary>
    public ProvidersConfigDocument GetProvidersConfigInternal()
    {
        lock (_syncLock)
        {
            return LoadProvidersDocumentInternal();
        }
    }

    /// <summary>
    /// 根据 Provider ID 获取单个 Provider 配置（内部使用）
    /// </summary>
    public ProviderConfig? GetProviderById(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        lock (_syncLock)
        {
            var doc = LoadProvidersDocumentInternal();
            return doc.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    /// <summary>
    /// 保存完整的 Providers 配置文档（自动执行所有 Provider API Key 与独立代理密码的 DPAPI 加密与原子落盘）
    /// </summary>
    public ProvidersConfigDocument SaveProvidersConfig(ProvidersConfigDocument incoming)
    {
        if (incoming == null)
        {
            throw new ArgumentNullException(nameof(incoming));
        }

        lock (_syncLock)
        {
            var existingDoc = AtomicFileWriter.ReadJsonOrDefault<ProvidersConfigDocument>(ProvidersConfigFilePath);
            var existingMap = existingDoc?.Providers?
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);

            var normalizedProviders = new List<ProviderConfig>();
            foreach (var rawProvider in incoming.Providers ?? new List<ProviderConfig>())
            {
                if (rawProvider == null)
                {
                    continue;
                }

                existingMap.TryGetValue(rawProvider.Id ?? string.Empty, out var previousProvider);
                var cleanProvider = NormalizeProviderForStorage(rawProvider, previousProvider);
                normalizedProviders.Add(cleanProvider);
            }

            string activeProviderId = incoming.ActiveProviderId ?? string.Empty;
            string activeModelId = incoming.ActiveModelId ?? string.Empty;

            if (normalizedProviders.Count > 0)
            {
                var activeProvider = normalizedProviders.FirstOrDefault(p =>
                    string.Equals(p.Id, activeProviderId, StringComparison.OrdinalIgnoreCase))
                    ?? normalizedProviders.FirstOrDefault(p => p.Enabled)
                    ?? normalizedProviders[0];

                activeProviderId = activeProvider.Id;

                if (activeProvider.Models.Count > 0 &&
                    !activeProvider.Models.Any(m => string.Equals(m.Id, activeModelId, StringComparison.OrdinalIgnoreCase)))
                {
                    activeModelId = (activeProvider.Models.FirstOrDefault(m => m.Enabled) ?? activeProvider.Models[0]).Id;
                }
            }

            var docToSave = new ProvidersConfigDocument
            {
                ActiveProviderId = activeProviderId,
                ActiveModelId = activeModelId,
                Providers = normalizedProviders
            };

            AtomicFileWriter.WriteJsonAtomic(ProvidersConfigFilePath, docToSave);
            HaloLogger.App.Info($"Providers config saved: Count={normalizedProviders.Count}, ActiveProvider={activeProviderId}, ActiveModel={activeModelId}");

            return SanitizeProvidersDocumentForClient(docToSave);
        }
    }

    /// <summary>
    /// 新增或更新单个 Provider 配置（自动加密 API Key 并落盘）
    /// </summary>
    public ProvidersConfigDocument UpsertProvider(ProviderConfig incomingProvider)
    {
        if (incomingProvider == null)
        {
            throw new ArgumentNullException(nameof(incomingProvider));
        }

        lock (_syncLock)
        {
            var doc = LoadProvidersDocumentInternal();
            int idx = doc.Providers.FindIndex(p => string.Equals(p.Id, incomingProvider.Id, StringComparison.OrdinalIgnoreCase));
            ProviderConfig? previous = idx >= 0 ? doc.Providers[idx] : null;

            var normalized = NormalizeProviderForStorage(incomingProvider, previous);
            if (idx >= 0)
            {
                doc.Providers[idx] = normalized;
            }
            else
            {
                doc.Providers.Add(normalized);
            }

            if (string.IsNullOrWhiteSpace(doc.ActiveProviderId))
            {
                doc.ActiveProviderId = normalized.Id;
            }

            if (string.Equals(doc.ActiveProviderId, normalized.Id, StringComparison.OrdinalIgnoreCase) &&
                normalized.Models.Count > 0 &&
                !normalized.Models.Any(m => string.Equals(m.Id, doc.ActiveModelId, StringComparison.OrdinalIgnoreCase)))
            {
                doc.ActiveModelId = normalized.Models[0].Id;
            }

            AtomicFileWriter.WriteJsonAtomic(ProvidersConfigFilePath, doc);
            HaloLogger.App.Info($"Provider upserted: Id={normalized.Id}, Name={normalized.Name}, Models={normalized.Models.Count}");
            return SanitizeProvidersDocumentForClient(doc);
        }
    }

    /// <summary>
    /// 删除指定的 Provider 配置
    /// </summary>
    public ProvidersConfigDocument DeleteProvider(string providerId)
    {
        lock (_syncLock)
        {
            var doc = LoadProvidersDocumentInternal();
            doc.Providers.RemoveAll(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

            if (string.Equals(doc.ActiveProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                var nextActive = doc.Providers.FirstOrDefault(p => p.Enabled) ?? doc.Providers.FirstOrDefault();
                doc.ActiveProviderId = nextActive?.Id ?? string.Empty;
                doc.ActiveModelId = nextActive?.Models.FirstOrDefault()?.Id ?? string.Empty;
            }

            AtomicFileWriter.WriteJsonAtomic(ProvidersConfigFilePath, doc);
            HaloLogger.App.Info($"Provider deleted: Id={providerId}");
            return SanitizeProvidersDocumentForClient(doc);
        }
    }

    /// <summary>
    /// 切换当前激活的 Provider 与 Model
    /// </summary>
    public ProvidersConfigDocument SetActiveModel(string providerId, string modelId)
    {
        lock (_syncLock)
        {
            var doc = LoadProvidersDocumentInternal();
            var targetProvider = doc.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (targetProvider != null)
            {
                doc.ActiveProviderId = targetProvider.Id;
                var targetModel = targetProvider.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                                  ?? targetProvider.Models.FirstOrDefault();
                doc.ActiveModelId = targetModel?.Id ?? modelId ?? string.Empty;
                AtomicFileWriter.WriteJsonAtomic(ProvidersConfigFilePath, doc);
            }

            return SanitizeProvidersDocumentForClient(doc);
        }
    }

    /// <summary>
    /// 解密并获取指定 Provider 的明文 API Key（仅限 C# 宿主内部发请求时调用，严禁暴露给前端日志）
    /// </summary>
    public string GetDecryptedApiKey(ProviderConfig provider)
    {
        if (provider == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKey) && !provider.ApiKey.Contains("****", StringComparison.Ordinal))
        {
            return provider.ApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEncrypted))
        {
            return SecretProtector.Unprotect(provider.ApiKeyEncrypted);
        }

        var persisted = GetProviderById(provider.Id);
        return persisted != null ? SecretProtector.Unprotect(persisted.ApiKeyEncrypted) : string.Empty;
    }

    /// <summary>
    /// 预览指定 Provider 的完整端点 URL、生效代理与自定义请求头变量替换结果
    /// </summary>
    public ProviderEndpointPreviewDto PreviewProviderEndpointsAndHeaders(
        ProviderConfig provider,
        string? modelId = null,
        string? sessionId = null,
        string? workspacePath = null)
    {
        string effectiveModelId = !string.IsNullOrWhiteSpace(modelId)
            ? modelId.Trim()
            : provider.Models.FirstOrDefault()?.Id ?? "gpt-4o";

        var ctx = HeaderVariableResolver.CreateContext(
            workspacePath: workspacePath ?? Environment.CurrentDirectory,
            sessionId: sessionId ?? "sess_stage02_preview",
            providerId: provider.Id,
            providerName: provider.Name,
            modelId: effectiveModelId);

        using var dummyRequest = new HttpRequestMessage(HttpMethod.Post, HeaderVariableResolver.ResolveEndpointUrl(provider.BaseUrl, LlmProtocolType.Chat));
        var resolvedHeaders = HeaderVariableResolver.ApplyCustomHeaders(dummyRequest, provider, ctx);

        var effectiveProxy = _httpClientFactory.ResolveEffectiveProxy(provider);

        return new ProviderEndpointPreviewDto
        {
            BaseUrl = provider.BaseUrl,
            NormalizedVersionPrefix = HeaderVariableResolver.NormalizeVersionedPrefix(provider.BaseUrl),
            ChatEndpointUrl = HeaderVariableResolver.ResolveEndpointUrl(provider.BaseUrl, LlmProtocolType.Chat),
            RespEndpointUrl = HeaderVariableResolver.ResolveEndpointUrl(provider.BaseUrl, LlmProtocolType.Resp),
            ModelsEndpointUrl = HeaderVariableResolver.ResolveModelsEndpointUrl(provider.BaseUrl),
            EffectiveProxySummary = effectiveProxy.ToDisplaySummary(),
            SessionId = ctx.SessionId,
            SessionHash = ctx.SessionHash,
            WorkspaceName = ctx.WorkspaceName,
            WorkspaceHash = ctx.WorkspaceId,
            ResolvedHeaders = resolvedHeaders
        };
    }

    private GlobalNetworkSettings LoadNetworkSettingsInternal()
    {
        var loaded = AtomicFileWriter.ReadJsonOrDefault<GlobalNetworkSettings>(NetworkConfigFilePath);
        if (loaded != null)
        {
            loaded.CustomProxy ??= new ManualProxyConfig();
            return loaded;
        }

        var defaults = new GlobalNetworkSettings
        {
            Mode = GlobalProxyMode.System,
            CustomProxy = new ManualProxyConfig
            {
                Scheme = "http",
                Host = "127.0.0.1",
                Port = 7890,
                RequiresAuth = false,
                BypassList = "localhost;127.*;10.*;192.168.*",
                BypassOnLocal = true
            },
            DefaultConnectTimeoutSeconds = 15,
            DefaultRequestTimeoutSeconds = 300
        };

        AtomicFileWriter.WriteJsonAtomic(NetworkConfigFilePath, defaults);
        return defaults;
    }

    private ProvidersConfigDocument LoadProvidersDocumentInternal()
    {
        var loaded = AtomicFileWriter.ReadJsonOrDefault<ProvidersConfigDocument>(ProvidersConfigFilePath);
        if (loaded != null && loaded.Providers != null)
        {
            return loaded;
        }

        var defaultDoc = CreateDefaultProvidersDocument();
        AtomicFileWriter.WriteJsonAtomic(ProvidersConfigFilePath, defaultDoc);
        return defaultDoc;
    }

    private static ProvidersConfigDocument CreateDefaultProvidersDocument()
    {
        var openAiProvider = new ProviderConfig
        {
            Id = "provider-openai",
            Name = "OpenAI / 兼容网关",
            Enabled = true,
            BaseUrl = "https://api.openai.com/v1",
            ApiKeyEncrypted = string.Empty,
            ProxyMode = ProviderProxyMode.Inherit,
            EnableCustomHeaders = true,
            CustomHeaders = new List<CustomHeaderEntry>
            {
                new()
                {
                    Key = "X-Session-Hash",
                    ValueTemplate = "${session-hash}",
                    Enabled = true,
                    IsSecret = false
                },
                new()
                {
                    Key = "X-Client-Workspace",
                    ValueTemplate = "${workspace-name}",
                    Enabled = true,
                    IsSecret = false
                }
            },
            Models = new List<ModelConfig>
            {
                new()
                {
                    Id = "gpt-4o",
                    DisplayName = "GPT-4o (Chat Completions)",
                    Protocol = LlmProtocolType.Chat,
                    MaxContextTokens = 128000,
                    MaxOutputTokens = 4096,
                    Temperature = 0.2,
                    Enabled = true
                },
                new()
                {
                    Id = "o3-mini",
                    DisplayName = "o3-mini (Responses API)",
                    Protocol = LlmProtocolType.Resp,
                    MaxContextTokens = 200000,
                    MaxOutputTokens = 16384,
                    Temperature = null,
                    Enabled = true
                }
            }
        };

        var deepSeekProvider = new ProviderConfig
        {
            Id = "provider-deepseek",
            Name = "DeepSeek Official",
            Enabled = true,
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKeyEncrypted = string.Empty,
            ProxyMode = ProviderProxyMode.Inherit,
            EnableCustomHeaders = false,
            CustomHeaders = new List<CustomHeaderEntry>(),
            Models = new List<ModelConfig>
            {
                new()
                {
                    Id = "deepseek-chat",
                    DisplayName = "DeepSeek-V3 (Chat)",
                    Protocol = LlmProtocolType.Chat,
                    MaxContextTokens = 64000,
                    MaxOutputTokens = 8192,
                    Temperature = 0.1,
                    Enabled = true
                }
            }
        };

        return new ProvidersConfigDocument
        {
            ActiveProviderId = openAiProvider.Id,
            ActiveModelId = "gpt-4o",
            Providers = new List<ProviderConfig> { openAiProvider, deepSeekProvider }
        };
    }

    private static ProviderConfig NormalizeProviderForStorage(ProviderConfig incoming, ProviderConfig? existing)
    {
        var clone = incoming.Clone();
        clone.Id = string.IsNullOrWhiteSpace(clone.Id) ? Guid.NewGuid().ToString("N") : clone.Id.Trim();
        clone.Name = string.IsNullOrWhiteSpace(clone.Name) ? "Unnamed Provider" : clone.Name.Trim();
        clone.BaseUrl = string.IsNullOrWhiteSpace(clone.BaseUrl) ? "https://api.openai.com/v1" : clone.BaseUrl.Trim();

        // 处理 API Key 加密逻辑：绝不保存明文 ApiKey
        if (!string.IsNullOrWhiteSpace(clone.ApiKey) && !clone.ApiKey.Contains("****", StringComparison.Ordinal))
        {
            clone.ApiKeyEncrypted = SecretProtector.Protect(clone.ApiKey.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(clone.ApiKeyEncrypted))
        {
            // 若传入的 ApiKeyEncrypted 尚未是合法 DPAPI 密文（例如调用方误将明文赋给了 ApiKeyEncrypted），自动加密
            if (!SecretProtector.IsProtectedCipherText(clone.ApiKeyEncrypted))
            {
                clone.ApiKeyEncrypted = SecretProtector.Protect(clone.ApiKeyEncrypted.Trim());
            }
        }
        else if (existing != null && !string.IsNullOrWhiteSpace(existing.ApiKeyEncrypted) && clone.ApiKey == null)
        {
            clone.ApiKeyEncrypted = existing.ApiKeyEncrypted;
        }

        clone.ApiKey = null;
        clone.ApiKeyMasked = null;
        clone.HasApiKey = null;

        if (clone.CustomProxy != null)
        {
            clone.CustomProxy.Scheme = EffectiveProxyDescriptor.NormalizeScheme(clone.CustomProxy.Scheme);
            clone.CustomProxy.Host = string.IsNullOrWhiteSpace(clone.CustomProxy.Host) ? "127.0.0.1" : clone.CustomProxy.Host.Trim();
            clone.CustomProxy.Port = clone.CustomProxy.Port is > 0 and <= 65535 ? clone.CustomProxy.Port : 7890;
            NormalizeProxySecretForStorage(clone.CustomProxy, existing?.CustomProxy);
        }

        clone.CustomHeaders = (clone.CustomHeaders ?? new List<CustomHeaderEntry>())
            .Where(h => h != null && !string.IsNullOrWhiteSpace(h.Key))
            .Select(h => new CustomHeaderEntry
            {
                Key = h.Key.Trim(),
                ValueTemplate = h.ValueTemplate ?? string.Empty,
                Enabled = h.Enabled,
                IsSecret = h.IsSecret
            })
            .ToList();

        clone.Models = (clone.Models ?? new List<ModelConfig>())
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new ModelConfig
            {
                Id = m.Id.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id.Trim() : m.DisplayName.Trim(),
                Protocol = m.Protocol,
                MaxContextTokens = m.MaxContextTokens > 0 ? m.MaxContextTokens : 128000,
                MaxOutputTokens = m.MaxOutputTokens > 0 ? m.MaxOutputTokens : 4096,
                Temperature = m.Temperature,
                Enabled = m.Enabled
            })
            .ToList();

        return clone;
    }

    private static void NormalizeProxySecretForStorage(ManualProxyConfig target, ManualProxyConfig? existing)
    {
        if (!string.IsNullOrWhiteSpace(target.Password) && !target.Password.Contains("****", StringComparison.Ordinal))
        {
            target.PasswordEncrypted = SecretProtector.Protect(target.Password);
        }
        else if (!string.IsNullOrWhiteSpace(target.PasswordEncrypted))
        {
            if (!SecretProtector.IsProtectedCipherText(target.PasswordEncrypted))
            {
                target.PasswordEncrypted = SecretProtector.Protect(target.PasswordEncrypted);
            }
        }
        else if (existing != null && !string.IsNullOrWhiteSpace(existing.PasswordEncrypted) && target.Password == null)
        {
            target.PasswordEncrypted = existing.PasswordEncrypted;
        }

        target.Password = null;
        target.PasswordMasked = null;
    }

    private static GlobalNetworkSettings SanitizeNetworkSettingsForClient(GlobalNetworkSettings source)
    {
        var copy = source.Clone();
        copy.CustomProxy ??= new ManualProxyConfig();
        string decryptedPwd = SecretProtector.Unprotect(copy.CustomProxy.PasswordEncrypted);
        copy.CustomProxy.Password = null;
        copy.CustomProxy.PasswordEncrypted = null;
        copy.CustomProxy.PasswordMasked = SecretProtector.MaskSecret(decryptedPwd);
        return copy;
    }

    private static ProvidersConfigDocument SanitizeProvidersDocumentForClient(ProvidersConfigDocument source)
    {
        return new ProvidersConfigDocument
        {
            ActiveProviderId = source.ActiveProviderId,
            ActiveModelId = source.ActiveModelId,
            Providers = source.Providers.Select(SanitizeProviderForClient).ToList()
        };
    }

    private static ProviderConfig SanitizeProviderForClient(ProviderConfig source)
    {
        var copy = source.Clone();
        string decryptedKey = SecretProtector.Unprotect(copy.ApiKeyEncrypted);
        copy.ApiKey = null;
        copy.ApiKeyEncrypted = string.Empty;
        copy.ApiKeyMasked = SecretProtector.MaskSecret(decryptedKey);
        copy.HasApiKey = !string.IsNullOrEmpty(decryptedKey);

        if (copy.CustomProxy != null)
        {
            string decryptedPwd = SecretProtector.Unprotect(copy.CustomProxy.PasswordEncrypted);
            copy.CustomProxy.Password = null;
            copy.CustomProxy.PasswordEncrypted = null;
            copy.CustomProxy.PasswordMasked = SecretProtector.MaskSecret(decryptedPwd);
        }

        return copy;
    }
}
