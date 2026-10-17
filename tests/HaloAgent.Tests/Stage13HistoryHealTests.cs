#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 历史孤儿愈合验收：中断/杀进程残留的 assistant tool_calls 补 synthetic cancelled tool，
/// 无主 tool 消息清理；单条删除先备份再删行并顺带愈合。
/// </summary>
public sealed class Stage13HistoryHealTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;

    public Stage13HistoryHealTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage13Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage13Bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);

        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);

        string projectDir = Path.Combine(_tempRoot, "Projects", "HealTest");
        Directory.CreateDirectory(projectDir);

        _workspace = _service.AddOrUpdateWorkspaceAsync(projectDir, "愈合测试项目").GetAwaiter().GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "Stage13 验收会话").GetAwaiter().GetResult();
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

    private static UnifiedChatMessage AssistantWithCall(string callId, string name = "read_file") =>
        UnifiedChatMessage.Assistant(
            "正文",
            new List<UnifiedToolCall> { new UnifiedToolCall(callId, null, name, "{}") });

    [Fact]
    public async Task Heal_Appends_Synthetic_For_Orphan_Tail_And_Is_Idempotent()
    {
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.User("查文件"));
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, AssistantWithCall("call_orphan_1"));

        var r = await _service.HealSessionHistoryAsync(_workspace.Id, _session.Id);
        Assert.True(r.Success);
        Assert.Equal(1, r.SyntheticAppended);
        Assert.Equal(0, r.OrphanToolsRemoved);
        Assert.NotNull(r.BackupFileName);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(3, messages.Count);
        var synthetic = messages[2];
        Assert.Equal("tool", synthetic.Role);
        Assert.Equal("call_orphan_1", synthetic.ToolCallId);
        Assert.StartsWith(WorkspaceSessionService.SyntheticCancelledToolPrefix, synthetic.Content);

        // 二次愈合必须是干净 no-op（不写盘）
        var r2 = await _service.HealSessionHistoryAsync(_workspace.Id, _session.Id);
        Assert.True(r2.Success);
        Assert.Equal(0, r2.SyntheticAppended);
        Assert.Equal(0, r2.OrphanToolsRemoved);
        Assert.Null(r2.BackupFileName);
    }

    [Fact]
    public async Task Heal_Removes_Orphan_Tool_And_Fills_Missing_Call()
    {
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.User("开始"));
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, AssistantWithCall("call_a"));
        // c2 无归属 assistant，必须被清理；call_a 缺回包，必须补 synthetic
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.Tool("call_ghost", "输出", "list_dir"));

        var r = await _service.HealSessionHistoryAsync(_workspace.Id, _session.Id);
        Assert.True(r.Success);
        Assert.Equal(1, r.SyntheticAppended);
        Assert.Equal(1, r.OrphanToolsRemoved);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(3, messages.Count);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("tool", messages[2].Role);
        Assert.Equal("call_a", messages[2].ToolCallId);
        Assert.DoesNotContain(messages, m => string.Equals(m.ToolCallId, "call_ghost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Heal_Clean_History_Does_Not_Rewrite()
    {
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.User("读文件"));
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, AssistantWithCall("call_ok"));
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.Tool("call_ok", "内容", "read_file"));

        var r = await _service.HealSessionHistoryAsync(_workspace.Id, _session.Id);
        Assert.True(r.Success);
        Assert.Equal(0, r.SyntheticAppended);
        Assert.Equal(0, r.OrphanToolsRemoved);
        Assert.Equal(3, r.KeptMessages);
        Assert.Null(r.BackupFileName);
    }

    [Fact]
    public async Task Delete_User_Message_Removes_One_Line_With_Backup()
    {
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.User("第一句"));
        var second = UnifiedChatMessage.User("第二句");
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, second);

        var r = await _service.DeleteSessionMessageAsync(_workspace.Id, _session.Id, second.Id);
        Assert.True(r.Success);
        Assert.Equal(1, r.KeptMessages);
        Assert.NotNull(r.BackupFileName);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Single(messages);
        Assert.Equal("第一句", messages[0].Content);
    }

    [Fact]
    public async Task Delete_Tool_Message_Heals_Dangling_Assistant()
    {
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.User("读文件"));
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, AssistantWithCall("call_del", "read_file"));
        var toolMsg = UnifiedChatMessage.Tool("call_del", "内容", "read_file");
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, toolMsg);

        var r = await _service.DeleteSessionMessageAsync(_workspace.Id, _session.Id, toolMsg.Id);
        Assert.True(r.Success);
        Assert.Equal(1, r.SyntheticAppended);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Equal(3, messages.Count);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("call_del", messages[2].ToolCallId);
        Assert.StartsWith(WorkspaceSessionService.SyntheticCancelledToolPrefix, messages[2].Content);
    }

    [Fact]
    public async Task Delete_Missing_Message_Fails_Without_Touching_Disk()
    {
        await _service.AppendSessionMessageAsync(_workspace.Id, _session.Id, UnifiedChatMessage.User("留存"));

        var r = await _service.DeleteSessionMessageAsync(_workspace.Id, _session.Id, "msg_not_exist");
        Assert.False(r.Success);
        Assert.Equal("MESSAGE_NOT_FOUND", r.ErrorCode);

        var messages = await _service.GetSessionMessagesAsync(_workspace.Id, _session.Id);
        Assert.Single(messages);
    }
}
