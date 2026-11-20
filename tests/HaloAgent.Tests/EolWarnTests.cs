#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 写回告警守卫：验证编辑工具在换行画像发生变化时，成功回执与 dry_run 预览均显式产出
/// WARN_EOL_CHANGED；画像保持稳定时保持静默。此处只验证可观测性链路接通，
/// 具体写回风格策略由内部风格测试覆盖。
/// </summary>
public sealed class EolWarnTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly string _projectDir;
    private readonly ToolRegistry _registry;
    private readonly ToolContext _context;

    public EolWarnTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentEolWarn_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentEolWarnBootstrap_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        var storage = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        var repository = new SessionRepository(storage);
        var service = new WorkspaceSessionService(repository);
        _registry = new ToolRegistry();
        _projectDir = Path.Combine(_tempRoot, "Projects", "EolWarn");
        Directory.CreateDirectory(_projectDir);
        var workspace = service
            .AddOrUpdateWorkspaceAsync(_projectDir, "eol warn 项目")
            .GetAwaiter()
            .GetResult();
        var session = service
            .CreateSessionAsync(workspace.Id, "eol warn 会话")
            .GetAwaiter()
            .GetResult();
        _context = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = workspace.Id,
            SessionId = session.Id,
            DataRoot = _tempRoot,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }

            if (Directory.Exists(_bootstrapRoot))
            {
                Directory.Delete(_bootstrapRoot, recursive: true);
            }
        }
        catch
        {
            // 忽略测试临时目录清理异常
        }
    }

    private string Abs(string name) => Path.Combine(_projectDir, name);

    private Task<ToolResult> Call(string tool, object args) =>
        _registry.DispatchAsync(
            new ToolCallDto(
                "c-" + Guid.NewGuid().ToString("N"),
                tool,
                JsonSerializer.Serialize(
                    args,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
                )
            ),
            _context
        );

    private void WriteCrlf(string name, string lfContent) =>
        File.WriteAllText(Abs(name), lfContent.Replace("\n", "\r\n"), new UTF8Encoding(false));

    [Fact]
    public async Task EditFile_Success_WarnsWhenEolBecomesMixed()
    {
        WriteCrlf("mix.txt", "alpha\nbeta\ngamma\n");
        var r = await Call(
            "edit_file",
            new
            {
                file_path = "mix.txt",
                old_string = "alpha\nbeta\ngamma",
                new_string = "alpha\nbeta\rinserted\ngamma", // 裸 CR 使画像从纯 CRLF 变为混合
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("WARN_EOL_CHANGED", r.Output);
        Assert.Contains("CRLF -> Mixed", r.Output);
    }

    [Fact]
    public async Task EditFile_DryRun_WarnsWhenEolBecomesMixed()
    {
        WriteCrlf("dry.txt", "alpha\nbeta\ngamma\n");
        var r = await Call(
            "edit_file",
            new
            {
                file_path = "dry.txt",
                old_string = "alpha\nbeta\ngamma",
                new_string = "alpha\nbeta\rinserted\ngamma",
                dry_run = true,
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("DRY_RUN", r.Output);
        Assert.Contains("WARN_EOL_CHANGED", r.Output);
        string disk = File.ReadAllText(Abs("dry.txt"));
        Assert.Contains("beta", disk);
        Assert.DoesNotContain("inserted", disk); // dry_run 未写盘
    }

    [Fact]
    public async Task EditFile_Success_SilentWhenEolStable()
    {
        WriteCrlf("stable.txt", "alpha\nbeta\ngamma\n");
        var r = await Call(
            "edit_file",
            new
            {
                file_path = "stable.txt",
                old_string = "alpha\nbeta\ngamma",
                new_string = "alpha\nBETA\ngamma", // 等行替换，换行画像不变
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.DoesNotContain("WARN_EOL_CHANGED", r.Output);
    }

    [Fact]
    public async Task EditLines_Success_WarnsWhenEolBecomesMixed()
    {
        WriteCrlf("lines.txt", "alpha\nbeta\ngamma\n");
        var r = await Call(
            "edit_lines",
            new
            {
                file_path = "lines.txt",
                offset = 2,
                limit = 1,
                new_text = "beta\rinserted",
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("WARN_EOL_CHANGED", r.Output);
    }

    [Fact]
    public async Task EditBatch_Success_WarnsWhenEolBecomesMixed()
    {
        WriteCrlf("batch.txt", "alpha\nbeta\ngamma\n");
        var r = await Call(
            "edit_batch",
            new
            {
                file_path = "batch.txt",
                mode = "text",
                edits = new[]
                {
                    new
                    {
                        old_string = "alpha\nbeta\ngamma",
                        new_string = "alpha\nbeta\rinserted\ngamma",
                    }
                },
            }
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("WARN_EOL_CHANGED", r.Output);
    }
}
