using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Infrastructure.Config;

public sealed partial class ProviderConfigService
{
    /// <summary>
    /// 预览指定 Provider 的完整端点 URL、生效代理与自定义请求头变量替换结果
    /// </summary>
    public ProviderEndpointPreviewDto PreviewProviderEndpointsAndHeaders(
        ProviderConfig provider,
        string? modelId = null,
        string? sessionId = null,
        string? workspacePath = null
    )
    {
        string effectiveModelId = !string.IsNullOrWhiteSpace(modelId)
            ? modelId.Trim()
            : provider.Models.FirstOrDefault()?.Id ?? "gpt-4o";

        var ctx = HeaderVariableResolver.CreateContext(
            workspacePath: workspacePath ?? Environment.CurrentDirectory,
            sessionId: sessionId ?? "sess_stage02_preview",
            providerId: provider.Id,
            providerName: provider.Name,
            modelId: effectiveModelId
        );

        using var dummyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            HeaderVariableResolver.ResolveEndpointUrl(provider.BaseUrl, LlmProtocolType.Chat)
        );
        var resolvedHeaders = HeaderVariableResolver.ApplyCustomHeaders(
            dummyRequest,
            provider,
            ctx
        );

        var effectiveProxy = _httpClientFactory.ResolveEffectiveProxy(provider);

        return new ProviderEndpointPreviewDto
        {
            BaseUrl = provider.BaseUrl,
            NormalizedVersionPrefix = HeaderVariableResolver.NormalizeVersionedPrefix(
                provider.BaseUrl
            ),
            ChatEndpointUrl = HeaderVariableResolver.ResolveEndpointUrl(
                provider.BaseUrl,
                LlmProtocolType.Chat
            ),
            RespEndpointUrl = HeaderVariableResolver.ResolveEndpointUrl(
                provider.BaseUrl,
                LlmProtocolType.Resp
            ),
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
        var loaded = AtomicFileWriter.ReadJsonOrDefault<GlobalNetworkSettings>(
            NetworkConfigFilePath
        );
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
            DefaultRequestTimeoutSeconds = 120 // need22-P1 T3：新装默认 120s（老文件沿用旧值，HttpClient 侧统一钳制 120s 上限）
        };

        AtomicFileWriter.WriteJsonAtomic(NetworkConfigFilePath, defaults);
        return defaults;
    }

    private ProvidersConfigDocument LoadProvidersDocumentInternal()
    {
        var loaded = AtomicFileWriter.ReadJsonOrDefault<ProvidersConfigDocument>(
            ProvidersConfigFilePath
        );
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
                    Key = "x-opencode-session",
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

    private static ProviderConfig NormalizeProviderForStorage(
        ProviderConfig incoming,
        ProviderConfig? existing
    )
    {
        var clone = incoming.Clone();
        clone.Id = string.IsNullOrWhiteSpace(clone.Id)
            ? Guid.NewGuid().ToString("N")
            : clone.Id.Trim();
        clone.Name = string.IsNullOrWhiteSpace(clone.Name) ? "Unnamed Provider" : clone.Name.Trim();
        clone.BaseUrl = string.IsNullOrWhiteSpace(clone.BaseUrl)
            ? "https://api.openai.com/v1"
            : clone.BaseUrl.Trim();

        // 处理 API Key 加密逻辑：绝不保存明文 ApiKey
        if (
            !string.IsNullOrWhiteSpace(clone.ApiKey)
            && !clone.ApiKey.Contains("****", StringComparison.Ordinal)
        )
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
        else if (
            existing != null
            && !string.IsNullOrWhiteSpace(existing.ApiKeyEncrypted)
            && clone.ApiKey == null
        )
        {
            clone.ApiKeyEncrypted = existing.ApiKeyEncrypted;
        }

        clone.ApiKey = null;
        clone.ApiKeyMasked = null;
        clone.HasApiKey = null;

        if (clone.CustomProxy != null)
        {
            clone.CustomProxy.Scheme = EffectiveProxyDescriptor.NormalizeScheme(
                clone.CustomProxy.Scheme
            );
            clone.CustomProxy.Host = string.IsNullOrWhiteSpace(clone.CustomProxy.Host)
                ? "127.0.0.1"
                : clone.CustomProxy.Host.Trim();
            clone.CustomProxy.Port = clone.CustomProxy.Port is > 0 and <= 65535
                ? clone.CustomProxy.Port
                : 7890;
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
            .Select(m => NormalizeModelForStorage(m))
            .ToList();
        return clone;
    }

    /// <summary>need09-03：模型 vision 能力位校验（maxImages 0..16 else PROVIDER_VALIDATION；detail 缺失/非法回退 high；老 json 缺字段即默认值）。</summary>
    private static ModelConfig NormalizeModelForStorage(ModelConfig m)
    {
        if (m.MaxImagesPerTurn < 0 || m.MaxImagesPerTurn > VisionDefaults.MaxAllowedImages)
        {
            throw new ProviderConfigValidationException("maxImagesPerTurn", "允许 0 或 1..16（0=默认4）");
        }
        return new ModelConfig
        {
            Id = m.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(m.DisplayName)
                ? m.Id.Trim()
                : m.DisplayName.Trim(),
            Protocol = m.Protocol,
            MaxContextTokens = m.MaxContextTokens > 0 ? m.MaxContextTokens : 128000,
            MaxOutputTokens = m.MaxOutputTokens > 0 ? m.MaxOutputTokens : 4096,
            Temperature = m.Temperature,
            Enabled = m.Enabled,
            SupportsVision = m.SupportsVision,
            MaxImagesPerTurn = m.MaxImagesPerTurn,
            VisionDetail = VisionDefaults.NormalizeDetail(m.VisionDetail),
            IncludeReasoningInHistory = m.IncludeReasoningInHistory
        };
    }

    private static void NormalizeProxySecretForStorage(
        ManualProxyConfig target,
        ManualProxyConfig? existing
    )
    {
        if (
            !string.IsNullOrWhiteSpace(target.Password)
            && !target.Password.Contains("****", StringComparison.Ordinal)
        )
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
        else if (
            existing != null
            && !string.IsNullOrWhiteSpace(existing.PasswordEncrypted)
            && target.Password == null
        )
        {
            target.PasswordEncrypted = existing.PasswordEncrypted;
        }

        target.Password = null;
        target.PasswordMasked = null;
    }

    private static GlobalNetworkSettings SanitizeNetworkSettingsForClient(
        GlobalNetworkSettings source
    )
    {
        var copy = source.Clone();
        copy.CustomProxy ??= new ManualProxyConfig();
        string decryptedPwd = SecretProtector.Unprotect(copy.CustomProxy.PasswordEncrypted);
        copy.CustomProxy.Password = null;
        copy.CustomProxy.PasswordEncrypted = null;
        copy.CustomProxy.PasswordMasked = SecretProtector.MaskSecret(decryptedPwd);
        return copy;
    }

    private static ProvidersConfigDocument SanitizeProvidersDocumentForClient(
        ProvidersConfigDocument source
    )
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
