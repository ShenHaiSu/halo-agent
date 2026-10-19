#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Storage;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need13-07 §1.1：子代理配置（agent.json 7 字段 + 校验 + 恢复 + 损坏回退）验收。
/// 对标 Stage08AgentConfigTests，零修改老文件。
/// </summary>
public sealed class Stage13SubAgentConfigTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly StoragePathManager _storageManager;
    private readonly AgentConfigService _agentService;

    public Stage13SubAgentConfigTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage13Cfg_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage13CfgBoot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);
        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);
        _agentService = new AgentConfigService(_storageManager);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            if (Directory.Exists(_bootstrapRoot)) Directory.Delete(_bootstrapRoot, recursive: true);
        }
        catch { }
    }

    [Fact(DisplayName = "S13: 老 agent.json 无 7 字段回默认无横幅")]
    public void SubDefaults_OldFileCompatible()
    {
        File.WriteAllText(_agentService.AgentConfigFilePath,
            "{\"version\":1,\"execDefaultTimeoutMs\":30000,\"execMaxTimeoutMs\":300000,\"maxIterations\":25,\"scope\":\"global\"}");
        var (doc, isDefault, fallback) = _agentService.LoadWithMeta();
        Assert.True(doc.SubAgentEnabled);
        Assert.Equal(3, doc.SubMaxParallel);
        Assert.Equal(15, doc.SubMaxIterations);
        Assert.True(doc.SubAllowExecCommand);
        Assert.True(doc.SubAllowFileWrite);
        Assert.False(doc.SubAllowSkillWrite);
        Assert.False(doc.SubAllowMemoryWrite);
        Assert.False(doc.SubAllowAskUser);
        Assert.False(isDefault);
        Assert.Null(fallback);
    }

    [Fact(DisplayName = "S13: subMaxParallel 越界拒绝且 field 正确")]
    public void SubValidation_ParallelRejected()
    {
        foreach (int bad in new[] { 0, 9 })
        {
            var ex = Assert.Throws<AgentConfigValidationException>(() =>
                _agentService.SaveAgentConfig(new AgentConfigDocument
                {
                    ExecDefaultTimeoutMs = 30000, ExecMaxTimeoutMs = 300000, MaxIterations = 25,
                    SubAgentEnabled = true, SubMaxParallel = bad, SubMaxIterations = 15,
                    SubAllowExecCommand = true, SubAllowFileWrite = true,
                }));
            Assert.Equal("subMaxParallel", ex.Field);
        }
    }

    [Fact(DisplayName = "S13: subMaxIterations 越界拒绝且 field 正确")]
    public void SubValidation_IterationsRejected()
    {
        foreach (int bad in new[] { 0, 101 })
        {
            var ex = Assert.Throws<AgentConfigValidationException>(() =>
                _agentService.SaveAgentConfig(new AgentConfigDocument
                {
                    ExecDefaultTimeoutMs = 30000, ExecMaxTimeoutMs = 300000, MaxIterations = 25,
                    SubAgentEnabled = true, SubMaxParallel = 3, SubMaxIterations = bad,
                    SubAllowExecCommand = true, SubAllowFileWrite = true,
                }));
            Assert.Equal("subMaxIterations", ex.Field);
        }
    }

    [Fact(DisplayName = "S13: subAllowAskUser=true 保存后读回 false")]
    public void SubAsk_LockedFalse()
    {
        _agentService.SaveAgentConfig(new AgentConfigDocument
        {
            ExecDefaultTimeoutMs = 30000, ExecMaxTimeoutMs = 300000, MaxIterations = 25,
            SubAllowAskUser = true,
        });
        var (doc, _, _) = _agentService.LoadWithMeta();
        Assert.False(doc.SubAllowAskUser);
    }

    [Fact(DisplayName = "S13: 按项恢复只回该组")]
    public void SubReset_Matrix()
    {
        _agentService.SaveAgentConfig(new AgentConfigDocument
        {
            ExecDefaultTimeoutMs = 30000, ExecMaxTimeoutMs = 300000, MaxIterations = 25,
            SubAgentEnabled = false, SubMaxParallel = 8, SubMaxIterations = 50,
            SubAllowExecCommand = false, SubAllowFileWrite = false,
            SubAllowSkillWrite = true, SubAllowMemoryWrite = true,
        });
        var r1 = _agentService.ResetAgentConfig("subEnabled");
        Assert.True(r1.SubAgentEnabled);
        Assert.Equal(8, r1.SubMaxParallel); // 另组保持
        var r2 = _agentService.ResetAgentConfig("subParallel");
        Assert.Equal(3, r2.SubMaxParallel);
        Assert.Equal(50, r2.SubMaxIterations);
        var r3 = _agentService.ResetAgentConfig("subIterations");
        Assert.Equal(15, r3.SubMaxIterations);
        var r4 = _agentService.ResetAgentConfig("subPerms");
        Assert.True(r4.SubAllowExecCommand);
        Assert.True(r4.SubAllowFileWrite);
        Assert.False(r4.SubAllowSkillWrite);
        Assert.False(r4.SubAllowMemoryWrite);
        Assert.False(r4.SubAllowAskUser);
    }

    [Fact(DisplayName = "S13: 全量恢复 + 未知项抛 field")]
    public void SubReset_AllAndUnknown()
    {
        _agentService.SaveAgentConfig(new AgentConfigDocument
        {
            ExecDefaultTimeoutMs = 60000, ExecMaxTimeoutMs = 600000, MaxIterations = 50,
            SubAgentEnabled = false, SubMaxParallel = 8, SubMaxIterations = 50,
        });
        var all = _agentService.ResetAgentConfig("all");
        Assert.Equal(30000, all.ExecDefaultTimeoutMs);
        Assert.True(all.SubAgentEnabled);
        Assert.Equal(3, all.SubMaxParallel);
        Assert.Equal(15, all.SubMaxIterations);
        var ex = Assert.Throws<AgentConfigValidationException>(() => _agentService.ResetAgentConfig("bogus"));
        Assert.Equal("field", ex.Field);
    }

    [Fact(DisplayName = "S13: 垃圾 agent.json 回默认 + fallback + corrupt 保留")]
    public void SubCorrupt_FallsBack()
    {
        File.WriteAllText(_agentService.AgentConfigFilePath, "{{{not-json");
        var (doc, isDefault, fallback) = _agentService.LoadWithMeta();
        Assert.True(isDefault);
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        Assert.True(doc.SubAgentEnabled);
        var dir = Path.GetDirectoryName(_agentService.AgentConfigFilePath)!;
        Assert.NotEmpty(Directory.GetFiles(dir, "agent.json.corrupt-*"));
    }
}
