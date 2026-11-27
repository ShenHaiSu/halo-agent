#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 子会话状态回归：索引读-改-写的原子性，以及子状态的显式落盘。
/// 并发用例用仓储代理构造确定性交错，不依赖 Task.Run 碰运气。
/// </summary>
public sealed class SubAgentConcurrentStateTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly WorkspaceInfo _workspace;

    public SubAgentConcurrentStateTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentConcurrentState_{Guid.NewGuid():N}"
        );
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentConcurrentStateBoot_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        string projectDir = Path.Combine(_tempRoot, "Projects", "ConcurrentState");
        Directory.CreateDirectory(projectDir);
        _workspace = _service
            .AddOrUpdateWorkspaceAsync(projectDir, "并发状态测试")
            .GetAwaiter()
            .GetResult();
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
        catch { }
    }

    /// <summary>
    /// 拦截索引加载：让两次加载拿到同一份旧快照后再放行，构造
    /// Load(a) → Load(b) → Save(a) → Save(b) 的确定性 lost-update 交错。
    /// 修复后索引写入改走锁内原子操作，不再经过该拦截点，用例自然通过。
    /// </summary>
    public class GatingRepositoryProxy : DispatchProxy
    {
        private ISessionRepository _target = null!;
        private int _loadCount;
        private readonly ManualResetEventSlim _bothLoaded = new(false);

        public static ISessionRepository Wrap(ISessionRepository target)
        {
            var proxy = DispatchProxy.Create<ISessionRepository, GatingRepositoryProxy>();
            var gating = (GatingRepositoryProxy)proxy;
            gating._target = target;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result = targetMethod!.Invoke(_target, args);
            if (targetMethod.Name == nameof(ISessionRepository.LoadSessionsIndexAsync))
            {
                if (Interlocked.Increment(ref _loadCount) == 2)
                    _bothLoaded.Set();
                return HoldUntilBothLoadedAsync((Task<List<SessionMetadata>>)result!);
            }
            return result;
        }

        private async Task<List<SessionMetadata>> HoldUntilBothLoadedAsync(
            Task<List<SessionMetadata>> loading
        )
        {
            var snapshot = await loading.ConfigureAwait(false);
            // 带超时：修复后拦截点不再触发时也不会挂死。
            _bothLoaded.Wait(TimeSpan.FromSeconds(5));
            return snapshot;
        }
    }

    /// <summary>
    /// 经反射取显式状态写回入口：入口缺失时以断言失败暴露，而不是让整个测试项目编译失败，
    /// 便于 revert 时同一批用例都能跑出失败结果。
    /// </summary>
    private async Task<bool> WriteSubStatusAsync(string sessionId, string status)
    {
        var method = typeof(WorkspaceSessionService).GetMethod("UpdateSubStatusAsync");
        Assert.NotNull(method);
        var pending =
            (Task<bool>)
                method!.Invoke(
                    _service,
                    new object[] { _workspace.Id, sessionId, status, CancellationToken.None }
                )!;
        return await pending.ConfigureAwait(false);
    }

    [Fact(DisplayName = "并发 Append 保留两个会话各自的计数")]
    public async Task ConcurrentAppend_PreservesBothSessionCounts()
    {
        // 只追加一条：meta.json 是独立权威源，后续 Append 会用正确计数自愈 index，
        // 反而掩盖本次要抓的索引丢失。单条 + 确定性交错才能稳定断言丢失结果。
        const int perSession = 1;

        var first = await _service.CreateSessionAsync(_workspace.Id, "sess-a");
        var second = await _service.CreateSessionAsync(_workspace.Id, "sess-b");

        // 经代理注入确定性交错：两次索引加载拿到同一份旧快照后才放行。
        var gated = new WorkspaceSessionService(GatingRepositoryProxy.Wrap(_repository));
        using var start = new ManualResetEventSlim(false);
        var appendFirst = Task.Run(async () =>
        {
            start.Wait();
            for (int i = 0; i < perSession; i++)
                await gated.AppendSessionMessageAsync(
                    _workspace.Id,
                    first.Id,
                    UnifiedChatMessage.User("msg-" + i)
                );
        });
        var appendSecond = Task.Run(async () =>
        {
            start.Wait();
            for (int i = 0; i < perSession; i++)
                await gated.AppendSessionMessageAsync(
                    _workspace.Id,
                    second.Id,
                    UnifiedChatMessage.User("msg-" + i)
                );
        });

        start.Set();
        await Task.WhenAll(appendFirst, appendSecond).WaitAsync(TimeSpan.FromSeconds(30));

        var index = await _repository.LoadSessionsIndexAsync(_workspace.Id);
        Assert.Equal(perSession, index.Single(s => s.Id == first.Id).MessageCount);
        Assert.Equal(perSession, index.Single(s => s.Id == second.Id).MessageCount);
    }

    [Fact(DisplayName = "状态写入后紧跟 Append 仍可读回")]
    public async Task SubStatus_WrittenBeforeAppend_SurvivesAppend()
    {
        var session = await _service.CreateSessionAsync(_workspace.Id, "sess-status");
        Assert.True(await WriteSubStatusAsync(session.Id, "running"));

        await _service.AppendSessionMessageAsync(
            _workspace.Id,
            session.Id,
            UnifiedChatMessage.User("hello")
        );

        var meta = await _service.GetSessionAsync(_workspace.Id, session.Id);
        Assert.Equal("running", meta!.SubStatus);
        var index = await _repository.LoadSessionsIndexAsync(_workspace.Id);
        Assert.Equal("running", index.Single(s => s.Id == session.Id).SubStatus);
    }

    [Fact(DisplayName = "三种终态写入后 meta 与索引都一致")]
    public async Task SubStatus_TerminalStatesAllPersist()
    {
        foreach (var status in new[] { "succeeded", "failed", "cancelled" })
        {
            var session = await _service.CreateSessionAsync(
                _workspace.Id,
                "sess-terminal-" + status
            );
            Assert.True(await WriteSubStatusAsync(session.Id, "running"));
            Assert.True(await WriteSubStatusAsync(session.Id, status));

            var meta = await _service.GetSessionAsync(_workspace.Id, session.Id);
            Assert.Equal(status, meta!.SubStatus);
            var index = await _repository.LoadSessionsIndexAsync(_workspace.Id);
            Assert.Equal(status, index.Single(s => s.Id == session.Id).SubStatus);
        }
    }
}
