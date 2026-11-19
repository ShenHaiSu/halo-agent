#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 会话标题两阶段门闩回归：即时标题（T1）不置锁、改名置锁、
/// 冻结分支 Fail+专用码且短路 LLM、未冻结走真实 LLM 并以 source=title 落用量。
/// </summary>
public sealed class SessionTitleLifecycleTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storage;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly ProviderConfigService _providers;
    private readonly HaloHttpClientFactory _httpFactory;

    public SessionTitleLifecycleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"SessionTitleLife_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(_tempRoot, "Bootstrap");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        _storage = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storage);
        _service = new WorkspaceSessionService(_repository);
        _httpFactory = new HaloHttpClientFactory();
        _providers = new ProviderConfigService(_storage, _httpFactory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // ignore cleanup failures in temp folders
        }
    }

    private async Task<(string WorkspaceId, string SessionId)> NewSessionAsync()
    {
        string projRoot = Path.Combine(_tempRoot, "Projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projRoot);
        var ws = await _service.AddOrUpdateWorkspaceAsync(projRoot, "标题测试");
        var sess = await _service.CreateSessionAsync(ws.Id);
        return (ws.Id, sess.Id);
    }

    private HostBridgeFacade NewFacade(ILlmGatewayClient gateway) =>
        new(
            mainForm: null,
            storageManager: _storage,
            gpuDisabled: false,
            httpClientFactory: _httpFactory,
            providerConfigService: _providers,
            llmGatewayClient: gateway,
            sessionRepository: _repository,
            workspaceSessionService: _service
        );

    private void SeedApiKey()
    {
        // 明文 ApiKey 优先于 DPAPI 密文，测试环境免加密依赖
        var doc = _providers.GetProvidersConfigInternal();
        foreach (var p in doc.Providers)
            p.ApiKey = "sk-test-key";
        _providers.SaveProvidersConfig(doc);
    }

    [Fact(DisplayName = "AppendSessionMessageAsync：首条 user 生成 T1 后 TitleLocked 仍为 false")]
    public async Task Append_FirstUser_GeneratesT1_WithoutLocking()
    {
        var (ws, sid) = await NewSessionAsync();
        var ok = await _service.AppendSessionMessageAsync(
            ws,
            sid,
            UnifiedChatMessage.User("帮我写一个排序算法。后面忽略")
        );

        Assert.True(ok);
        var meta = await _service.GetSessionAsync(ws, sid);
        Assert.NotNull(meta);
        Assert.Equal("帮我写一个", meta!.Title);
        Assert.False(meta.TitleLocked);
    }

    [Fact(DisplayName = "UpdateSessionTitleAsync：任意改名后 TitleLocked=true（约定不变）")]
    public async Task UpdateTitle_AlwaysLocks()
    {
        var (ws, sid) = await NewSessionAsync();
        var ok = await _service.UpdateSessionTitleAsync(ws, sid, "用户手改标题");
        Assert.True(ok);
        var meta = await _service.GetSessionAsync(ws, sid);
        Assert.True(meta!.TitleLocked);
        Assert.Equal("用户手改标题", meta.Title);
    }

    [Fact(DisplayName = "GenerateSessionTitle：已冻结返回 Fail + TITLE_ALREADY_LOCKED，且 LLM 未被调用")]
    public async Task GenerateTitle_WhenLocked_FailsFast_WithoutLlmCall()
    {
        var (ws, sid) = await NewSessionAsync();
        // 经由改名通道置锁（手动改名约定）
        Assert.True(await _service.UpdateSessionTitleAsync(ws, sid, "用户手改标题"));

        var gateway = new SpyGateway("不该被调用的标题");
        var facade = NewFacade(gateway);
        var resp = await facade.GenerateSessionTitle(ws, sid, "provider-openai", "gpt-4o");

        Assert.False(resp.Ok);
        Assert.Equal("TITLE_ALREADY_LOCKED", resp.Code);
        Assert.Equal(0, gateway.CallCount);
    }

    [Fact(DisplayName = "GenerateSessionTitle：未冻结走 LLM 分支并回写标题，落一条 source=title 用量")]
    public async Task GenerateTitle_WhenUnlocked_CallsLlm_And_RecordsTitleUsage()
    {
        var (ws, sid) = await NewSessionAsync();
        Assert.True(
            await _service.AppendSessionMessageAsync(ws, sid, UnifiedChatMessage.User("帮我写一个排序算法"))
        );
        Assert.True(
            await _service.AppendSessionMessageAsync(
                ws,
                sid,
                UnifiedChatMessage.Assistant("好的，这是快速排序的实现")
            )
        );

        SeedApiKey();
        var gateway = new SpyGateway("快速排序实现");
        var facade = NewFacade(gateway);
        var resp = await facade.GenerateSessionTitle(ws, sid, "provider-openai", "gpt-4o");

        Assert.True(resp.Ok);
        Assert.Equal("快速排序实现", resp.Data);
        Assert.True(gateway.CallCount >= 1);

        var meta = await _service.GetSessionAsync(ws, sid);
        Assert.Equal("快速排序实现", meta!.Title);
        Assert.True(meta.TitleLocked);

        // 用量异步落袋，轮询等待
        using var usage = new UsageMetricsService(_storage);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        long titleCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            var (_, rows) = usage.QueryPage(
                new UsageFilter
                {
                    Preset = "all",
                    Sources = new List<string> { "title" }
                },
                1,
                50
            );
            titleCount = rows.Count(r => r.Source == "title");
            if (titleCount > 0)
                break;
            Thread.Sleep(30);
        }
        Assert.True(titleCount > 0, "应有一条 source=title 的用量记录，证明 LLM 小请求真的发出");
    }

    [Fact(DisplayName = "ParseSessionTitle：去噪、钳 12 字、surrogate 安全，行为不变")]
    public void ParseSessionTitle_Pure_Behavior()
    {
        var method = typeof(HostBridgeFacade).GetMethod(
            "ParseSessionTitle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);
        string Invoke(string raw) => (string)method!.Invoke(null, new object[] { raw })!;

        Assert.Equal("我的测试标题", Invoke("标题：我的测试标题"));
        Assert.Equal("", Invoke("！。？"));
        // 钳 12 个码点
        Assert.Equal(12, Invoke("一二三四五六七八九十甲乙丙丁").Length);
        // surrogate pair（emoji）按码点计，不被截断成半个代理项
        string longWithEmoji = "标题😀😀😀😀😀😀😀😀😀😀😀😀😀😀";
        string parsed = Invoke(longWithEmoji);
        Assert.False(string.IsNullOrEmpty(parsed));
        Assert.DoesNotContain('\uFFFD', parsed);
    }

    /// <summary>记录 StreamAsync 调用次数并可返回指定标题事件的网关替身。</summary>
    private sealed class SpyGateway : ILlmGatewayClient
    {
        private readonly string _title;
        private int _callCount;

        public SpyGateway(string title) => _title = title;

        public int CallCount => Volatile.Read(ref _callCount);

        public async IAsyncEnumerable<UnifiedStreamEvent> StreamAsync(
            UnifiedLlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _callCount);
            await Task.Yield();
            yield return UnifiedStreamEvent.Text(_title);
            yield return UnifiedStreamEvent.Tokens(120, 8, 0, 128);
            yield return UnifiedStreamEvent.Completed();
        }
    }
}
