#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 工具级字节画像回归：验证编辑与新建落盘后换行风格不退化、混合收敛到主风格、边界样本不插入裸换行。
/// 全部基于真实工具调度与临时文件，断言落盘字节而非仅文本内容。
/// </summary>
public sealed class EolRoundTripTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly string _projectDir;
    private readonly ToolRegistry _registry;
    private readonly ToolContext _context;

    public EolRoundTripTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentEolRoundTrip_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentEolRoundTripBootstrap_{Guid.NewGuid():N}"
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
        _projectDir = Path.Combine(_tempRoot, "Projects", "EolRoundTrip");
        Directory.CreateDirectory(_projectDir);
        var workspace = service
            .AddOrUpdateWorkspaceAsync(_projectDir, "eol round trip 项目")
            .GetAwaiter()
            .GetResult();
        var session = service
            .CreateSessionAsync(workspace.Id, "eol round trip 会话")
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

    private void WriteRaw(string name, string content) =>
        File.WriteAllText(Abs(name), content, new UTF8Encoding(false));

    private static FileToolShared.EolProfile ProfileOfFile(string path) =>
        FileToolShared.EolProfile.Of(File.ReadAllText(path));

    [Fact]
    public void DetectSpanEol_CrlfAtSpanStart_IsRecognized()
    {
        // 命中区间从 \n 开始，\r 落在 start-1；修复后仍应判为 CRLF，避免插入裸 LF。
        string raw = "alpha\r\nbeta";
        int start = raw.IndexOf('\n'); // 指向 \n，前一字符为 \r
        Assert.Equal("\r\n", EditFileHelpers.DetectSpanEol(raw, start, start + 1));
    }

    [Fact]
    public async Task MixedFile_Edit_ConvergesToMainStyle()
    {
        // CRLF 为主 + 一处裸 LF；编辑触及该行后应统一收敛为主风格 CRLF。
        WriteRaw("mixed.txt", "alpha\r\nbeta\r\ngamma\ndelta\r\n");
        Assert.True(ProfileOfFile(Abs("mixed.txt")).IsMixed);

        var r = await Call(
            "edit_file",
            new
            {
                file_path = "mixed.txt",
                old_string = "gamma\ndelta",
                new_string = "GAMMA\ndelta",
            }
        );
        Assert.True(r.Success, r.Error);

        var profile = ProfileOfFile(Abs("mixed.txt"));
        Assert.False(profile.IsMixed);
        Assert.Equal(0, profile.BareLf);
        Assert.Equal(0, profile.BareCr);
        Assert.Equal(4, profile.Crlf);
    }

    [Fact]
    public async Task PureCrlfFile_Edit_StaysPureCrlf()
    {
        WriteRaw("pure.txt", "alpha\r\nbeta\r\ngamma\r\n");
        var r = await Call(
            "edit_file",
            new
            {
                file_path = "pure.txt",
                old_string = "alpha\nbeta\ngamma",
                new_string = "alpha\nBETA\ngamma",
            }
        );
        Assert.True(r.Success, r.Error);

        var profile = ProfileOfFile(Abs("pure.txt"));
        Assert.False(profile.IsMixed);
        Assert.Equal(3, profile.Crlf);
        Assert.Equal(0, profile.BareLf);
        Assert.Equal(0, profile.BareCr);
    }

    [Fact]
    public async Task WriteFile_NewFile_AutoFallsBackToCrlf()
    {
        var r = await Call(
            "write_file",
            new { file_path = "fresh.txt", content = "line1\nline2\nline3\n", }
        );
        Assert.True(r.Success, r.Error);

        var profile = ProfileOfFile(Abs("fresh.txt"));
        Assert.False(profile.IsMixed);
        Assert.Equal(3, profile.Crlf);
        Assert.Equal(0, profile.BareLf);
        Assert.Contains("CRLF", r.Output);
    }

    [Fact]
    public async Task WriteFile_NewFile_ExplicitLf_IsPureLf()
    {
        var r = await Call(
            "write_file",
            new
            {
                file_path = "lf.txt",
                content = "line1\r\nline2\r\nline3\r\n",
                eol = "lf",
            }
        );
        Assert.True(r.Success, r.Error);

        var profile = ProfileOfFile(Abs("lf.txt"));
        Assert.False(profile.IsMixed);
        Assert.Equal(0, profile.Crlf);
        Assert.Equal(3, profile.BareLf);
    }

    [Fact]
    public async Task WriteFile_NewFile_ExplicitCrlf_IsPureCrlf()
    {
        var r = await Call(
            "write_file",
            new
            {
                file_path = "crlf.txt",
                content = "line1\nline2\n",
                eol = "crlf",
            }
        );
        Assert.True(r.Success, r.Error);

        var profile = ProfileOfFile(Abs("crlf.txt"));
        Assert.False(profile.IsMixed);
        Assert.Equal(2, profile.Crlf);
        Assert.Equal(0, profile.BareLf);
    }

    [Fact]
    public async Task WriteFile_Overwrite_InheritsOriginalStyle()
    {
        WriteRaw("inherit.txt", "old\r\ncontent\r\n");
        var r = await Call(
            "write_file",
            new
            {
                file_path = "inherit.txt",
                content = "new\ncontent\n",
                allow_overwrite = true,
            }
        );
        Assert.True(r.Success, r.Error);

        var profile = ProfileOfFile(Abs("inherit.txt"));
        Assert.False(profile.IsMixed);
        Assert.Equal(2, profile.Crlf);
        Assert.Equal(0, profile.BareLf);
    }

    [Fact]
    public void Detect_BareCrFile_ReportsCrStyle()
    {
        string path = Abs("bare-cr.txt");
        File.WriteAllText(path, "alpha\rbeta\r", new UTF8Encoding(false));
        var detected = FileToolShared.ReadAllTextWithDetection(path);
        Assert.False(detected.IsMixedEol);
        Assert.Equal("\r", detected.NewLine);
        Assert.Equal("CR", detected.NewLineLabel);
    }
}
