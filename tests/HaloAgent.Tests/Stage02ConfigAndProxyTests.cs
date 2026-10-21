using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

public sealed class Stage02ConfigAndProxyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;

    public Stage02ConfigAndProxyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage02Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage02Bootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot))
                Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup races
        }
    }

    [Fact]
    public void SecretProtector_DpapiRoundtrip_And_Mask_ShouldProtectSecrets()
    {
        string rawSecret = "sk-proj-halo-stage02-secret-key-9876543210";

        string encrypted = SecretProtector.Protect(rawSecret);
        Assert.NotEqual(rawSecret, encrypted);
        Assert.False(string.IsNullOrWhiteSpace(encrypted));

        string decrypted = SecretProtector.Unprotect(encrypted);
        Assert.Equal(rawSecret, decrypted);

        string masked = SecretProtector.MaskSecret(rawSecret);
        Assert.StartsWith("sk-", masked);
        Assert.DoesNotContain("3210", masked);
        Assert.Contains("****", masked);
        Assert.Contains($"len={rawSecret.Length}", masked);
    }

    [Fact]
    public void GlobalNetworkSettings_SaveAndReload_ShouldEncryptPasswordAndHotReloadHttpClientFactory()
    {
        string dataRoot = Path.Combine(_tempRoot, "HaloAgentData");
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap-net.json");
        var storage = StoragePathManager.Initialize(bootstrapFile, dataRoot, dataRoot);
        var proxyHub = new HaloHttpClientFactory();
        var configService = new ProviderConfigService(storage, proxyHub);

        var initial = configService.GetNetworkSettingsForClient();
        Assert.Equal(GlobalProxyMode.System, initial.Mode);

        var customSettings = new GlobalNetworkSettings
        {
            Mode = GlobalProxyMode.Custom,
            CustomProxy = new ManualProxyConfig
            {
                Scheme = "socks5",
                Host = "127.0.0.1",
                Port = 10808,
                RequiresAuth = true,
                Username = "halo_user",
                Password = "my_ proxy_password_123",
                BypassList = "localhost;127.*;192.168.*",
                BypassOnLocal = true
            },
            DefaultConnectTimeoutSeconds = 18,
            DefaultRequestTimeoutSeconds = 240
        };

        var sanitized = configService.SaveNetworkSettings(customSettings);
        Assert.Equal(GlobalProxyMode.Custom, sanitized.Mode);
        Assert.Null(sanitized.CustomProxy.Password);
        Assert.Null(sanitized.CustomProxy.PasswordEncrypted);
        Assert.False(string.IsNullOrWhiteSpace(sanitized.CustomProxy.PasswordMasked));

        // Verify raw disk JSON does NOT store plaintext password
        string networkJsonPath = Path.Combine(storage.ConfigDirectory, "network.json");
        Assert.True(File.Exists(networkJsonPath));
        string rawJson = File.ReadAllText(networkJsonPath, Encoding.UTF8);
        Assert.DoesNotContain("my_ proxy_password_123", rawJson);
        Assert.Contains("passwordEncrypted", rawJson);

        // Verify hot-reloaded proxyHub resolves Custom SOCKS5 proxy
        var effectiveGlobal = proxyHub.ResolveEffectiveProxy();
        Assert.Equal(EffectiveProxyKind.Custom, effectiveGlobal.Kind);
        Assert.Equal("socks5", effectiveGlobal.Scheme);
        Assert.Equal("127.0.0.1", effectiveGlobal.Host);
        Assert.Equal(10808, effectiveGlobal.Port);
        Assert.True(effectiveGlobal.RequiresAuth);
        Assert.Equal("halo_user", effectiveGlobal.Username);
        Assert.Equal("my_ proxy_password_123", effectiveGlobal.DecryptedPassword);

        // Switch to None (Forced Direct)
        var noneSettings = new GlobalNetworkSettings
        {
            Mode = GlobalProxyMode.None,
            DefaultConnectTimeoutSeconds = 12,
            DefaultRequestTimeoutSeconds = 120
        };
        configService.SaveNetworkSettings(noneSettings);

        var effectiveDirect = proxyHub.ResolveEffectiveProxy();
        Assert.Equal(EffectiveProxyKind.None, effectiveDirect.Kind);
        Assert.Contains("DIRECT", effectiveDirect.ToDisplaySummary());
    }

    [Fact]
    public void CascadingProxyMatrix_ShouldResolveProviderProxyModesCorrectly()
    {
        var globalCustom = new GlobalNetworkSettings
        {
            Mode = GlobalProxyMode.Custom,
            CustomProxy = new ManualProxyConfig
            {
                Scheme = "http",
                Host = "127.0.0.1",
                Port = 7890
            }
        };

        var proxyHub = new HaloHttpClientFactory(globalCustom);

        // Case 1: Provider = Inherit -> inherits Global Custom (http://127.0.0.1:7890)
        var inheritProvider = new ProviderConfig
        {
            Id = "prov-openai",
            Name = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            ProxyMode = ProviderProxyMode.Inherit
        };
        var desc1 = proxyHub.ResolveEffectiveProxy(inheritProvider);
        Assert.Equal(EffectiveProxyKind.Custom, desc1.Kind);
        Assert.Equal("http", desc1.Scheme);
        Assert.Equal("127.0.0.1", desc1.Host);
        Assert.Equal(7890, desc1.Port);

        // Case 2: Provider = None (e.g. local Ollama) -> Forced Direct bypassing Global Custom
        var directProvider = new ProviderConfig
        {
            Id = "prov-ollama",
            Name = "Local Ollama",
            BaseUrl = "http://127.0.0.1:11434/v1",
            ProxyMode = ProviderProxyMode.None
        };
        var desc2 = proxyHub.ResolveEffectiveProxy(directProvider);
        Assert.Equal(EffectiveProxyKind.None, desc2.Kind);
        Assert.Contains("DIRECT", desc2.ToDisplaySummary());

        // Case 3: Provider = Custom -> Uses provider's dedicated proxy (socks5://10.0.0.2:1080)
        var customProvider = new ProviderConfig
        {
            Id = "prov-enterprise",
            Name = "Enterprise Gateway",
            BaseUrl = "https://llm.corp.internal/v1",
            ProxyMode = ProviderProxyMode.Custom,
            CustomProxy = new ManualProxyConfig
            {
                Scheme = "socks5",
                Host = "10.0.0.2",
                Port = 1080
            }
        };
        var desc3 = proxyHub.ResolveEffectiveProxy(customProvider);
        Assert.Equal(EffectiveProxyKind.Custom, desc3.Kind);
        Assert.Equal("socks5", desc3.Scheme);
        Assert.Equal("10.0.0.2", desc3.Host);
        Assert.Equal(1080, desc3.Port);
    }

    [Fact]
    public async Task ProxyConnectivityTest_AgainstLocalHttpListener_ShouldReturnLatencyAndDiagnostic()
    {
        // Bind a local ephemeral TCP port and run a minimal HTTP response server
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            byte[] buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, 0, buffer.Length);

            string response =
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 15\r\nConnection: close\r\n\r\n{\"data\":[]}\r\n\r\n";
            byte[] respBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(respBytes, 0, respBytes.Length);
            await stream.FlushAsync();
        });

        try
        {
            var directGlobal = new GlobalNetworkSettings
            {
                Mode = GlobalProxyMode.None,
                DefaultConnectTimeoutSeconds = 5,
                DefaultRequestTimeoutSeconds = 10
            };
            var proxyHub = new HaloHttpClientFactory(directGlobal);

            var result = await proxyHub.TestProxyConnectivityAsync(
                targetUrl: $"http://127.0.0.1:{port}/v1/models",
                overrideGlobalSettings: directGlobal
            );

            Assert.True(result.Success, $"Connectivity test failed: {result.ErrorMessage}");
            Assert.Equal(200, result.StatusCode);
            Assert.True(result.ElapsedMs >= 0);
            Assert.Equal("OK", result.DiagnosticCategory);
            Assert.Contains("DIRECT", result.EffectiveProxyUsed);
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(500));
        }
    }

    [Fact]
    public void ProviderConfigService_SaveAndUpsert_ShouldEncryptApiKeysAndSupportChatAndRespModels()
    {
        string dataRoot = Path.Combine(_tempRoot, "ProviderData");
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap-prov.json");
        var storage = StoragePathManager.Initialize(bootstrapFile, dataRoot, dataRoot);
        var proxyHub = new HaloHttpClientFactory();
        var configService = new ProviderConfigService(storage, proxyHub);

        var newProvider = new ProviderConfig
        {
            Id = "provider-deepseek-custom",
            Name = "DeepSeek Custom Gateway",
            Enabled = true,
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-deepseek-plain-secret-key-abc123456",
            ProxyMode = ProviderProxyMode.Inherit,
            EnableCustomHeaders = true,
            CustomHeaders = new List<CustomHeaderEntry>
            {
                new()
                {
                    Key = "X-Session-Hash",
                    ValueTemplate = "sess_${session-hash}",
                    Enabled = true
                },
                new()
                {
                    Key = "X-Trace-Workspace",
                    ValueTemplate = "${workspace-id}:${model-id}",
                    Enabled = true
                }
            },
            Models = new List<ModelConfig>
            {
                new()
                {
                    Id = "deepseek-chat",
                    DisplayName = "DeepSeek V3 Chat",
                    Protocol = LlmProtocolType.Chat,
                    MaxContextTokens = 64000,
                    MaxOutputTokens = 8192,
                    Temperature = 0.2,
                    Enabled = true
                },
                new()
                {
                    Id = "o3-mini-resp",
                    DisplayName = "OpenAI o3-mini (Responses API)",
                    Protocol = LlmProtocolType.Resp,
                    MaxContextTokens = 200000,
                    MaxOutputTokens = 16384,
                    Temperature = 0.1,
                    Enabled = true
                }
            }
        };

        var updatedDoc = configService.UpsertProvider(newProvider);
        var savedClientView = updatedDoc.Providers.FirstOrDefault(p =>
            p.Id == "provider-deepseek-custom"
        );
        Assert.NotNull(savedClientView);
        Assert.True(savedClientView!.HasApiKey);
        Assert.Null(savedClientView.ApiKey);
        Assert.Empty(savedClientView.ApiKeyEncrypted);
        Assert.Contains("****", savedClientView.ApiKeyMasked);
        Assert.Equal(2, savedClientView.Models.Count);
        Assert.Equal(LlmProtocolType.Chat, savedClientView.Models[0].Protocol);
        Assert.Equal(LlmProtocolType.Resp, savedClientView.Models[1].Protocol);

        // Verify disk file does NOT contain plaintext API key
        string providersJsonPath = Path.Combine(storage.ConfigDirectory, "providers.json");
        Assert.True(File.Exists(providersJsonPath));
        string rawDiskJson = File.ReadAllText(providersJsonPath, Encoding.UTF8);
        Assert.DoesNotContain("sk-deepseek-plain-secret-key-abc123456", rawDiskJson);
        Assert.Contains("\"protocol\": \"chat\"", rawDiskJson);
        Assert.Contains("\"protocol\": \"resp\"", rawDiskJson);

        // Verify internal decryption for C# LLM client usage
        var internalProvider = configService.GetProviderById("provider-deepseek-custom");
        Assert.NotNull(internalProvider);
        string decryptedKey = configService.GetDecryptedApiKey(internalProvider!);
        Assert.Equal("sk-deepseek-plain-secret-key-abc123456", decryptedKey);
    }

    [Fact]
    public void HeaderVariableResolver_And_EndpointResolution_ShouldComputeDeterministicHashesAndUrls()
    {
        string workspacePath = @"F:\MyProgram\CSharp\halo-agent";
        string sessionId = "session-2026-stage02";

        var ctx = HeaderVariableResolver.CreateContext(
            workspacePath: workspacePath,
            sessionId: sessionId,
            providerId: "prov-openai",
            providerName: "OpenAI Gateway",
            modelId: "gpt-4o"
        );

        var ctxTrailingSlash = HeaderVariableResolver.CreateContext(
            workspacePath: workspacePath + "\\",
            sessionId: sessionId,
            providerId: "prov-openai",
            providerName: "OpenAI Gateway",
            modelId: "gpt-4o"
        );

        Assert.Equal(16, ctx.SessionHash.Length);
        Assert.Equal(ctx.SessionHash, ctxTrailingSlash.SessionHash); // Trailing slash normalized

        var provider = new ProviderConfig
        {
            Id = "prov-openai",
            Name = "OpenAI Gateway",
            BaseUrl = "https://api.openai.com/v1",
            EnableCustomHeaders = true,
            CustomHeaders = new List<CustomHeaderEntry>
            {
                new()
                {
                    Key = "X-Session-Affinity",
                    ValueTemplate = "affinity-${session-hash}",
                    Enabled = true
                },
                new()
                {
                    Key = "X-Halo-Context",
                    ValueTemplate = "${workspace-name}/${model-id}/${provider-id}",
                    Enabled = true
                },
                new()
                {
                    Key = "X-Disabled-Header",
                    ValueTemplate = "should-not-appear",
                    Enabled = false
                }
            }
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions"
        );
        var applied = HeaderVariableResolver.ApplyCustomHeaders(req, provider, ctx);

        Assert.Equal(2, applied.Count);
        Assert.Equal($"affinity-{ctx.SessionHash}", applied["X-Session-Affinity"]);
        Assert.Equal("halo-agent/gpt-4o/prov-openai", applied["X-Halo-Context"]);
        Assert.False(applied.ContainsKey("X-Disabled-Header"));

        // Verify URL Normalization & Protocol Endpoint Resolution (Chat vs Resp vs '#' raw prefix)
        Assert.Equal(
            "https://api.openai.com/v1/chat/completions",
            HeaderVariableResolver.ResolveEndpointUrl(
                "https://api.openai.com",
                LlmProtocolType.Chat
            )
        );
        Assert.Equal(
            "https://api.openai.com/v1/responses",
            HeaderVariableResolver.ResolveEndpointUrl(
                "https://api.openai.com/v1/",
                LlmProtocolType.Resp
            )
        );
        Assert.Equal(
            "https://custom.gateway.internal/v2/openai/chat/completions",
            HeaderVariableResolver.ResolveEndpointUrl(
                "https://custom.gateway.internal/v2/openai#",
                LlmProtocolType.Chat
            )
        );
        Assert.Equal(
            "https://custom.gateway.internal/v2/openai/responses",
            HeaderVariableResolver.ResolveEndpointUrl(
                "https://custom.gateway.internal/v2/openai#",
                LlmProtocolType.Resp
            )
        );
    }

    [Fact]
    public async Task HostBridgeFacade_Stage02IpcMethods_ShouldReturnCamelCaseBridgeResponses()
    {
        string dataRoot = Path.Combine(_tempRoot, "BridgeData");
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap-bridge.json");
        var storage = StoragePathManager.Initialize(bootstrapFile, dataRoot, dataRoot);
        var proxyHub = new HaloHttpClientFactory();
        var configService = new ProviderConfigService(storage, proxyHub);
        var bridge = new HostBridgeFacade(
            null,
            storage,
            gpuDisabled: false,
            proxyHub,
            configService
        );

        // 1. GetProvidersConfig
        var providersResp = await bridge.GetProvidersConfig();
        Assert.True(providersResp.Ok);
        Assert.NotNull(providersResp.Data);
        Assert.NotEmpty(providersResp.Data!.Providers);

        // 2. SetActiveModel
        var firstProvider = providersResp.Data.Providers[0];
        var firstModel = firstProvider.Models[0];
        var setActiveResp = await bridge.SetActiveModel(firstProvider.Id, firstModel.Id);
        Assert.True(setActiveResp.Ok);
        Assert.Equal(firstProvider.Id, setActiveResp.Data!.ActiveProviderId);
        Assert.Equal(firstModel.Id, setActiveResp.Data.ActiveModelId);

        // 3. PreviewProviderResolution
        string providerJson = JsonSerializer.Serialize(
            firstProvider,
            HaloJsonOptions.IndentedCamelCase
        );
        var previewResp = await bridge.PreviewProviderResolution(
            providerJson,
            firstModel.Id,
            "sess-bridge-01",
            @"F:\MyProgram\CSharp\halo-agent"
        );
        Assert.True(previewResp.Ok);
        Assert.NotNull(previewResp.Data);
        Assert.EndsWith("/v1/chat/completions", previewResp.Data!.ChatEndpointUrl);
        Assert.EndsWith("/v1/responses", previewResp.Data.RespEndpointUrl);
        Assert.Equal(16, previewResp.Data.SessionHash.Length);

        // 4. InspectSystemProxy
        var sysProxyResp = await bridge.InspectSystemProxy("https://api.openai.com");
        Assert.True(sysProxyResp.Ok);
        Assert.NotNull(sysProxyResp.Data);
    }
}
