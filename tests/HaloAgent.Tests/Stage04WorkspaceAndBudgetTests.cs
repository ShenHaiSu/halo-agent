#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

public sealed class Stage04WorkspaceAndBudgetTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly TokenBudgetService _budgetService;

    public Stage04WorkspaceAndBudgetTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage04Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage04Bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);

        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _budgetService = new TokenBudgetService();
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
            // Ignore cleanup failures in test temp folders
        }
    }

    [Fact(DisplayName = "S04-AC01: 多工作路径隔离、规范化与会话自动初始化")]
    public async Task AC01_Workspace_Normalization_And_Session_Isolation()
    {
        // 1. 创建两个不同的测试项目物理目录
        string projA = Path.Combine(_tempRoot, "Projects", "ProjectAlpha");
        string projB = Path.Combine(_tempRoot, "Projects", "ProjectBeta");
        Directory.CreateDirectory(projA);
        Directory.CreateDirectory(projB);

        // 带尾部反斜杠与大小写路径
        string pathWithTrailingSlash = projA + "\\";

        // 2. 添加 Workspace A
        WorkspaceInfo wsA = await _service.AddOrUpdateWorkspaceAsync(pathWithTrailingSlash, "项目 Alpha");
        Assert.NotNull(wsA);
        Assert.Equal(Path.GetFullPath(projA).TrimEnd('\\', '/'), wsA.RootPath);
        Assert.False(wsA.RootPath.EndsWith("\\"));
        Assert.Equal(16, wsA.WorkspaceHash.Length);
        Assert.True(wsA.DirectoryExists);

        // 3. 再次添加相同路径（不同大小写/格式），应更新/返回同一个
        WorkspaceInfo wsA2 = await _service.AddOrUpdateWorkspaceAsync(projA.ToLowerInvariant(), "项目 Alpha 别名");
        Assert.Equal(wsA.Id, wsA2.Id);
        Assert.Equal(wsA.WorkspaceHash, wsA2.WorkspaceHash);

        // 4. 添加 Workspace B
        WorkspaceInfo wsB = await _service.AddOrUpdateWorkspaceAsync(projB, "项目 Beta");
        Assert.NotNull(wsB);
        Assert.NotEqual(wsA.Id, wsB.Id);
        Assert.NotEqual(wsA.WorkspaceHash, wsB.WorkspaceHash);

        // 5. 验证自动初始化的默认会话分别独立存储在各 workspaceId 目录下
        var sessionsA = await _service.GetSessionsByWorkspaceAsync(wsA.Id);
        var sessionsB = await _service.GetSessionsByWorkspaceAsync(wsB.Id);

        Assert.Single(sessionsA);
        Assert.Single(sessionsB);
        Assert.NotEqual(sessionsA[0].Id, sessionsB[0].Id);
        Assert.NotEqual(sessionsA[0].SessionHash, sessionsB[0].SessionHash);

        // 6. 验证物理存储结构隔离
        string wsDirA = Path.Combine(_tempRoot, "sessions", wsA.Id);
        string wsDirB = Path.Combine(_tempRoot, "sessions", wsB.Id);
        Assert.True(Directory.Exists(wsDirA));
        Assert.True(Directory.Exists(wsDirB));

        string indexFileA = Path.Combine(wsDirA, "sessions-index.json");
        string indexFileB = Path.Combine(wsDirB, "sessions-index.json");
        Assert.True(File.Exists(indexFileA));
        Assert.True(File.Exists(indexFileB));

        // 7. 测试删除 Workspace
        bool removed = await _service.RemoveWorkspaceAsync(wsB.Id);
        Assert.True(removed);
        var remainingWorkspaces = await _service.GetAllWorkspacesAsync();
        Assert.Single(remainingWorkspaces);
        Assert.Equal(wsA.Id, remainingWorkspaces[0].Id);
        Assert.False(Directory.Exists(wsDirB));
    }

    [Fact(DisplayName = "S04-AC02: 会话元数据持久化、并发 Append 与 SessionHash 确定性校验")]
    public async Task AC02_Session_Metadata_And_Concurrent_Append()
    {
        string projPath = Path.Combine(_tempRoot, "Projects", "ProjectSessionTest");
        Directory.CreateDirectory(projPath);

        var ws = await _service.AddOrUpdateWorkspaceAsync(projPath, "会话测试项目");
        Assert.NotNull(ws);

        // 1. 创建新会话
        var session = await _service.CreateSessionAsync(ws.Id, "初始会话", "provider-openai", "gpt-4o");
        Assert.NotNull(session);
        Assert.Equal(ws.Id, session.WorkspaceId);
        Assert.Equal(16, session.SessionHash.Length);

        // 验证 SessionHash 的确定性生成 (workspaceId + ":" + sessionId 的 16 位小写十六进制)
        string expectedHash = HeaderVariableResolver.ComputeDeterministicHash16(ws.Id + ":" + session.Id);
        Assert.Equal(expectedHash, session.SessionHash);

         // 2. 追加第一条 User 消息，应自动将默认标题更新为 T1 五字标题（need03-03：第一句话去空格后前 5 字）
         var userMsg1 = UnifiedChatMessage.User("请帮我实现一个高效的二分查找算法代码");
         await _service.AppendSessionMessageAsync(ws.Id, session.Id, userMsg1);
 
         var updatedSession = await _service.GetSessionAsync(ws.Id, session.Id);
         Assert.NotNull(updatedSession);
         Assert.Equal(1, updatedSession!.MessageCount);
         Assert.Equal("请帮我实现", updatedSession.Title);

        // 3. 追加 Assistant 消息
        var assistantMsg = UnifiedChatMessage.Assistant("以下是 C# 实现的二分查找代码：...");
        assistantMsg.OutputTokens = 120;
        await _service.AppendSessionMessageAsync(ws.Id, session.Id, assistantMsg);

        // 4. 并发追加测试 (验证 SemaphoreSlim per-session 锁并发安全性与 messages.jsonl 完整性)
        int concurrentCount = 15;
        var tasks = new List<Task>();
        for (int i = 0; i < concurrentCount; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                var msg = UnifiedChatMessage.User($"并发测试消息 #{index}");
                await _service.AppendSessionMessageAsync(ws.Id, session.Id, msg);
            }));
        }
        await Task.WhenAll(tasks);

        // 5. 校验消息流式读取与物理 messages.jsonl 行数
        var allMessages = await _service.GetSessionMessagesAsync(ws.Id, session.Id);
        Assert.Equal(2 + concurrentCount, allMessages.Count);

        string sessionDir = Path.Combine(_tempRoot, "sessions", ws.Id, session.Id);
        string jsonlFile = Path.Combine(sessionDir, "messages.jsonl");
        Assert.True(File.Exists(jsonlFile));

        string[] lines = await File.ReadAllLinesAsync(jsonlFile);
        Assert.Equal(2 + concurrentCount, lines.Length);

        // 验证 meta.json 也正确保持同步
        string metaFile = Path.Combine(sessionDir, "meta.json");
        Assert.True(File.Exists(metaFile));
        var metaFromDisk = JsonSerializer.Deserialize<SessionMetadata>(await File.ReadAllTextAsync(metaFile), HaloJsonOptions.CompactCamelCase);
        Assert.NotNull(metaFromDisk);
        Assert.Equal(2 + concurrentCount, metaFromDisk!.MessageCount);

        // 6. 验证会话 Markdown 导出功能
        string markdown = await _service.ExportSessionMarkdownAsync(ws.Id, session.Id);
        Assert.Contains("二分查找", markdown);
        Assert.Contains("Session Hash", markdown);
    }

    [Fact(DisplayName = "S04-AC03: 离线 Token 估算器与成对原子截断（Pair-Preserving Truncation）")]
    public void AC03_TokenEstimation_And_PairPreserving_Truncation()
    {
        // 1. 离线 Token 启发式估算校验
        string cjkText = "你好世界，这是一段中文测试内容。"; // 16 CJK chars
        int cjkTokens = TokenBudgetService.EstimateTokens(cjkText);
        // 16 * 1.55 = 24.8 -> ceil 25
        Assert.Equal(25, cjkTokens);

        string asciiText = "Hello world, this is a clean English prompt test string."; // 56 chars
        int asciiTokens = TokenBudgetService.EstimateTokens(asciiText);
        // 56 / 3.6 = 15.55 -> ceil 16
        Assert.Equal(16, asciiTokens);

        // 2. 构造具有复杂 ToolCall/ToolResult 链的多轮对话
        var messages = new List<UnifiedChatMessage>();

        // [Group 0]: 首个用户任务提示（锚点，必须保留！）
        var anchorUser = UnifiedChatMessage.User("用户原始任务：分析项目代码并修复所有编译错误");
        anchorUser.InputTokens = 100;
        messages.Add(anchorUser);

        // [Group 1]: 早期中间轮次 1 - 应当在超限时作为整体被淘汰
        var earlyUser1 = UnifiedChatMessage.User("第1次辅助询问");
        earlyUser1.InputTokens = 200;
        var earlyAssistant1 = UnifiedChatMessage.Assistant("第1次辅助回复");
        earlyAssistant1.OutputTokens = 300;
        messages.Add(earlyUser1);
        messages.Add(earlyAssistant1);

        // [Group 2]: 早期工具调用链 - Assistant 发起 2 个工具调用，紧跟 2 个 Tool 结果
        var toolCalls = new List<UnifiedToolCall>
        {
            new UnifiedToolCall("call_abc1", null, "glob", "*.cs"),
            new UnifiedToolCall("call_abc2", null, "read", "Program.cs")
        };
        var toolAssistant = UnifiedChatMessage.Assistant("正在扫描文件...", toolCalls);
        toolAssistant.OutputTokens = 250;

        var toolResult1 = UnifiedChatMessage.Tool("call_abc1", "['Program.cs', 'App.cs']", "glob");
        toolResult1.InputTokens = 150;

        var toolResult2 = UnifiedChatMessage.Tool("call_abc2", "namespace MyApp { class Program {} }", "read");
        toolResult2.InputTokens = 350;

        messages.Add(toolAssistant);
        messages.Add(toolResult1);
        messages.Add(toolResult2);

        // [Group 3]: 活跃最新轮次 - 用户提问与待继续执行轮次（最新轮次必须保留！）
        var latestUser = UnifiedChatMessage.User("最后结论：请给出修改补丁");
        latestUser.InputTokens = 150;
        messages.Add(latestUser);

         // 3. 执行原子成对截断构建：设置总预算无法容纳全部历史，仅允许容纳 首锚点 + 最新轮次 + 提示
         // need04-05 起为纯启发式估算（不消费 InputTokens 快照），本组消息启发式总量约 200 tokens，
         // 故预算设为 150 tokens 以触发截断（旧 450 按快照口径已不够触发）
         int limitedBudget = 150;
         var truncated = _budgetService.BuildWithinBudget(messages, limitedBudget);

        // 4. 断言验证
        // (a) 必须保留首个用户锚点
        Assert.Equal("用户原始任务：分析项目代码并修复所有编译错误", truncated[0].Content);

        // (b) 必须包含折叠系统提示
        var noticeMsg = truncated.FirstOrDefault(m => m.Content != null && m.Content.Contains("较早的历史对话已自动折叠省略"));
        Assert.NotNull(noticeMsg);
        Assert.Equal("system", noticeMsg!.Role);

        // (c) 必须保留最后的最新轮次
        Assert.Equal("最后结论：请给出修改补丁", truncated.Last().Content);

        // (d) 验证原子性：工具调用组绝不能出现"只保留 tool_calls 没有 tool 结果"或"只有 tool 结果没有 tool_calls"的残缺现象
        var remainingToolCalls = truncated.Where(m => m.ToolCalls != null && m.ToolCalls.Count > 0).ToList();
        var remainingToolResults = truncated.Where(m => m.Role == "tool").ToList();
        if (remainingToolCalls.Count == 0)
        {
            Assert.Empty(remainingToolResults);
        }
        else
        {
            // 每一个 tool_call 都应在 tool 角色中找到对应 tool_call_id
            var callIds = remainingToolCalls.SelectMany(m => m.ToolCalls!).Select(c => c.CallId).ToHashSet();
            var resCallIds = remainingToolResults.Select(r => r.ToolCallId!).ToHashSet();
            Assert.Subset(callIds, resCallIds);
        }

        // 5. 预算状态 DTO 计算校验
        var model = new ModelConfig { Id = "test-model", MaxContextTokens = 8192, MaxOutputTokens = 2048 };
        var status = _budgetService.CalculateBudgetStatus(
            model: model,
            messages: messages,
            sessionHash: "sess_hash_123456");

        Assert.Equal(8192, status.TotalContextTokens);
        Assert.Equal(2048, status.MaxOutputTokens);
        Assert.Equal(1024, status.SafetyBuffer);
         Assert.Equal(8192 - 2048 - 1024 - 150, status.AvailableInputTokens); // need05-01：可用 = 总量 - 输出 - 安全 - 占用快照（末条 InputTokens=150）
         Assert.Equal(150, status.UsedInputTokens); // 占用快照 = 末条 InputTokens（need05-01 覆盖语义）
        Assert.False(status.IsNearLimit);
    }

    [Fact(DisplayName = "S04-AC04: 跨路径数据迁移与会话完整性校验")]
    public async Task AC04_DataRoot_Migration_Preserves_Workspaces_And_Sessions()
    {
        // 1. 初始化原始数据目录并填充 Workspace 与 Session
        string originProj = Path.Combine(_tempRoot, "Projects", "MigrationProject");
        Directory.CreateDirectory(originProj);

        var ws = await _service.AddOrUpdateWorkspaceAsync(originProj, "迁移测试项目");
        var session = await _service.CreateSessionAsync(ws.Id, "迁移前会话");
        var msg = UnifiedChatMessage.User("测试迁移持久化数据完整性");
        await _service.AppendSessionMessageAsync(ws.Id, session.Id, msg);

        // 2. 准备迁移目标目录
        string targetDataRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentMigratedTarget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDataRoot);

        try
        {
            // 3. 调用 StoragePathManager 进行迁移
            var migrationResult = await _storageManager.ChangeDataRootAsync(
                newTargetPath: targetDataRoot,
                migrateExistingData: true,
                onProgress: null,
                ct: CancellationToken.None);
            Assert.True(migrationResult.Success);
            Assert.Equal(targetDataRoot, _storageManager.CurrentDataRoot);

            // 4. 重建仓储与服务，验证所有 Workspace 和 Session 数据无缝加载
            var newRepo = new SessionRepository(_storageManager);
            var newService = new WorkspaceSessionService(newRepo);

            var workspaces = await newService.GetAllWorkspacesAsync();
            Assert.Single(workspaces);
            Assert.Equal(ws.Id, workspaces[0].Id);
            Assert.Equal(ws.WorkspaceHash, workspaces[0].WorkspaceHash);

            var sessions = await newService.GetSessionsByWorkspaceAsync(ws.Id);
            var reloadedSession = sessions.FirstOrDefault(s => s.Id == session.Id);
            Assert.NotNull(reloadedSession);
            Assert.Equal(session.SessionHash, reloadedSession!.SessionHash);

            var reloadedMessages = await newService.GetSessionMessagesAsync(ws.Id, session.Id);
            Assert.True(reloadedMessages.Count >= 1);
            Assert.Contains(reloadedMessages, m => m.Content == "测试迁移持久化数据完整性");
        }
        finally
        {
            if (Directory.Exists(targetDataRoot))
            {
                try { Directory.Delete(targetDataRoot, recursive: true); } catch { }
            }
        }
    }
}

