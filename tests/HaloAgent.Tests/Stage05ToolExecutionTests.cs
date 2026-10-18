#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Process;
using Host.Desktop.Tools.Safety;
using Xunit;

namespace HaloAgent.Tests;

// 并行禁用由 Stage01FoundationTests.cs 的 [assembly: CollectionBehavior] 全局控制。

public sealed class Stage05ToolExecutionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _bootstrapRoot;
    private readonly string _projectDir;
    private readonly StoragePathManager _storageManager;
    private readonly SessionRepository _repository;
    private readonly WorkspaceSessionService _service;
    private readonly ToolRegistry _registry;
    private readonly WorkspaceInfo _workspace;
    private readonly SessionMetadata _session;
    private readonly ToolContext _context;

    public Stage05ToolExecutionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage05Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage05Bootstrap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        string bootstrapFile = Path.Combine(_bootstrapRoot, "bootstrap.json");
        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: bootstrapFile,
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot);

        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _registry = new ToolRegistry();

        _projectDir = Path.Combine(_tempRoot, "Projects", "ToolTest");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service.AddOrUpdateWorkspaceAsync(_projectDir, "工具测试项目").GetAwaiter().GetResult();
        _session = _service.CreateSessionAsync(_workspace.Id, "Stage05 验收会话").GetAwaiter().GetResult();

        _context = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = _session.Id,
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

    private static string Args(object o) =>
        JsonSerializer.Serialize(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ============ S05-AC01: exec_command CMD 优先与中文无乱码 ============

    [Fact(DisplayName = "S05-AC01: SmartConsoleDecoder 严格UTF8优先、GBK回退")]
    public void AC01_SmartDecoder_Utf8First_GbkFallback()
    {
        string original = "中文测试目录 Mixed ABC 123";
        SmartConsoleDecoder.EnsureRegistered();
        byte[] gbkBytes = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            .GetBytes(original);
        // GBK 中文字符必然含非合法 UTF-8 序列 → 回退解码必须还原
        string decoded = SmartConsoleDecoder.DecodeBytes(gbkBytes);
        Assert.Equal(original, decoded);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes("纯UTF8内容 hello 😀");
        Assert.Equal("纯UTF8内容 hello 😀", SmartConsoleDecoder.DecodeBytes(utf8Bytes));
        Assert.Equal(string.Empty, SmartConsoleDecoder.DecodeBytes(Array.Empty<byte>()));
    }

    [Fact(DisplayName = "S05-AC01: exec_command 默认cmd执行echo中文无乱码")]
    public async Task AC01_ExecCommand_CmdEchoChinese_NoGarbled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // cmd runner 仅 Windows
        }

        var result = await _registry.DispatchAsync(
            new ToolCallDto("call_ac01", "exec_command", Args(new { command = "echo 中文测试" })),
            _context);

        Assert.True(result.Success, $"exec 失败：{result.Error} / {result.Output}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("中文测试", result.Output);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact(DisplayName = "S05-AC01: CommandRunnerProbe 生成cmd /d /s /c 启动参数与标准环境变量")]
    public void AC01_CommandRunnerProbe_CmdStartInfo()
    {
        var psi = CommandRunnerProbe.BuildCmdStartInfo("dir /b", _projectDir);
        Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/d /s /c", psi.Arguments, StringComparison.Ordinal);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal("utf-8", psi.Environment["PYTHONIOENCODING"]);
        Assert.Equal("1", psi.Environment["PYTHONUTF8"]);
        Assert.Equal("true", psi.Environment["CI"]);
    }

    // ============ S05-AC02: python 次选与缺失降级 ============

    [Fact(DisplayName = "S05-AC02: Python缺失时返回降级提示")]
    public async Task AC02_PythonMissing_FallbackMessage()
    {
        var missingCtx = new ToolContext
        {
            WorkspaceRoot = _projectDir,
            WorkspaceId = _workspace.Id,
            SessionId = _session.Id,
            DataRoot = _tempRoot,
            PythonProbeOverride = () => CommandRunnerProbe.ProbeResult.Missing(),
        };

        var result = await _registry.DispatchAsync(
            new ToolCallDto("call_ac02", "exec_command", Args(new { command = "print('hi')", runner = "python" })),
            missingCtx);

        Assert.False(result.Success);
        Assert.Contains("Python runtime is not detected", result.Error ?? result.Output);
        Assert.Contains("cmd", result.Error ?? result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "S05-AC02: Python探测优先级=设置路径>PATH(排除WindowsApps)>py-3")]
    public void AC02_ProbePriority_SkipsWindowsApps()
    {
        string emptyDir = Path.Combine(_tempRoot, "empty_path_dir");
        Directory.CreateDirectory(emptyDir);

        var result = CommandRunnerProbe.ProbePython(
            configuredPath: Path.Combine(emptyDir, "nope.exe"),
            pathEnvOverride: emptyDir + ";C:\\WindowsApps",
            checkLauncher: false);

        Assert.False(result.Found);

        // 本机真实探测不应返回 WindowsApps 下的解释器
        var real = CommandRunnerProbe.ProbePython();
        if (real.Found)
        {
            Assert.DoesNotContain("WindowsApps", real.ExePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(DisplayName = "S05-AC02: 本机有Python时可执行内联脚本并自动清理临时脚本")]
    public async Task AC02_PythonInline_ExecutesWhenAvailable()
    {
        var real = CommandRunnerProbe.ProbePython();
        if (!real.Found)
        {
            return; // 本机无 Python 则跳过执行部分（缺失降级已由上一用例覆盖）
        }

        var result = await _registry.DispatchAsync(
            new ToolCallDto("call_ac02b", "exec_command",
                Args(new { command = "print('hello-stage05')\nprint(40 + 2)", runner = "python" })),
            _context);

        Assert.True(result.Success, $"python 执行失败：{result.Error} / {result.Output}");
        Assert.Contains("hello-stage05", result.Output);
        Assert.Contains("42", result.Output);

        // 临时脚本执行后应可被清理（至少不残留当次 call 的脚本内容目录膨胀）
        string tmpDir = _context.SessionTempDir;
        if (Directory.Exists(tmpDir))
        {
            foreach (string f in Directory.GetFiles(tmpDir, "exec_call_ac02b*.py"))
            {
                Assert.True(false, $"临时脚本未清理：{f}");
            }
        }
    }

    // ============ S05-AC03: Job Object 超时强杀 ============

    [Fact(DisplayName = "S05-AC03: JobObject创建与进程纳管")]
    public void AC03_JobObject_CreateAndAssign()
    {
        using var job = JobObjectManager.Create();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.True(JobObjectManager.IsSupported);
            Assert.True(job.IsActive);
            Assert.Equal(0x2000u, JobObjectManager.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE);
        }
        else
        {
            Assert.False(JobObjectManager.IsSupported);
            Assert.False(job.IsActive);
        }
    }

    [Fact(DisplayName = "S05-AC03: 超时2秒强杀进程树")]
    public async Task AC03_ExecCommand_TimeoutKillsTree()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var result = await _registry.DispatchAsync(
            new ToolCallDto("call_ac03", "exec_command",
                Args(new { command = "ping 127.0.0.1 -n 60", timeout_ms = 2000 })),
            _context);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("超时", result.Error ?? string.Empty);
        // 2 秒超时 + 容限：必须远早于 60 个 ping 完成
        Assert.True(result.DurationMs < 30000, $"超时回收过慢：{result.DurationMs}ms");
        Assert.True(result.DurationMs >= 1500, $"返回过快疑似未真实执行：{result.DurationMs}ms");
    }

    // ============ S05-AC04: read/write/edit + GBK 兼容 ============

    [Fact(DisplayName = "S05-AC04: GBK+CRLF文件的read/edit全流程与Diff")]
    public async Task AC04_GbkCrlf_ReadEdit_Diff()
    {
        Encoding gbk = Encoding.GetEncoding(936);
        string fileName = "legacy_gbk.cs";
        string fullPath = Path.Combine(_projectDir, fileName);
        string originalText = "// 中文注释：旧逻辑\r\nint a = 1;\r\n// 中文注释：待修改行\r\nint b = 2;\r\n";
        File.WriteAllBytes(fullPath, gbk.GetBytes(originalText));

        // 1. read_file 准确识别 GBK + CRLF + 行号
        var readResult = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04r", "read_file", Args(new { file_path = fileName })),
            _context);
        Assert.True(readResult.Success, readResult.Error);
        Assert.Contains("[Encoding: GBK", readResult.Output);
        Assert.Contains("CRLF", readResult.Output);
        Assert.Contains("中文注释", readResult.Output);
        Assert.Contains("1:", readResult.Output);

        // 2. edit_file 用 LF 传参仍可匹配 CRLF 文件
        var editResult = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04e", "edit_file", Args(new
            {
                file_path = fileName,
                old_string = "// 中文注释：待修改行\nint b = 2;",
                new_string = "// 中文注释：已修复\nint b = 42;",
            })),
            _context);
        Assert.True(editResult.Success, editResult.Error);
        Assert.Contains("@@", editResult.Output);
        Assert.Contains("--- a/", editResult.Output);
        Assert.Contains("+++ b/", editResult.Output);
        Assert.Contains("-int b = 2;", editResult.Output);
        Assert.Contains("+int b = 42;", editResult.Output);

        // 3. 写回后仍为 GBK + CRLF
        byte[] raw = await File.ReadAllBytesAsync(fullPath);
        string roundTrip = gbk.GetString(raw);
        Assert.Contains("int b = 42;", roundTrip);
        Assert.Contains("\r\n", roundTrip);
        Assert.DoesNotContain("�", SmartConsoleDecoder.DecodeBytes(raw));

        // 4. 唯一性校验：重复 old_string 非 replace_all 应拒绝
        var dupResult = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04d", "edit_file", Args(new
            {
                file_path = fileName,
                old_string = "// 中文注释",
                new_string = "X",
            })),
            _context);
        Assert.False(dupResult.Success);
        Assert.Contains("matched", dupResult.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "S05-AC04: write_file自动建父目录+原子写+分页read")]
    public async Task AC04_WriteAndPagedRead()
    {
        var writeResult = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04w", "write_file",
                Args(new { file_path = "sub/deep/notes.txt", content = "line1\nline2\nline3" })),
            _context);
        Assert.True(writeResult.Success, writeResult.Error);
        Assert.Contains("已创建新文件", writeResult.Output);
        Assert.True(File.Exists(Path.Combine(_projectDir, "sub", "deep", "notes.txt")));

        var page = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04p", "read_file",
                Args(new { file_path = "sub/deep/notes.txt", offset = 2, limit = 1 })),
            _context);
        Assert.True(page.Success, page.Error);
        Assert.Contains("line2", page.Output);
        Assert.DoesNotContain("line1", page.Output);

        var binary = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04b", "read_file",
                Args(new { file_path = "sub/deep/notes.txt" })),
            _context);
        Assert.True(binary.Success);

        // 二进制拒绝
        string binPath = Path.Combine(_projectDir, "sub", "blob.bin");
        File.WriteAllBytes(binPath, new byte[] { 0x41, 0x00, 0x42, 0xFF });
        var binResult = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04bin", "read_file", Args(new { file_path = "sub/blob.bin" })),
            _context);
        Assert.False(binResult.Success);
        Assert.Contains("二进制", binResult.Error ?? string.Empty);
    }

    [Fact(DisplayName = "S05-AC04: list_dir递归/排除与grep_search正则检索")]
    public async Task AC04_ListDirAndGrep()
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, "src"));
        Directory.CreateDirectory(Path.Combine(_projectDir, "node_modules", "pkg"));
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "src", "app.cs"), "class App { // TODO: fix me }\n", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "src", "readme.md"), "hello world\n", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "node_modules", "pkg", "x.js"), "TODO should be excluded\n", Encoding.UTF8);

        var list = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04l", "list_dir", Args(new { path = ".", recursive = true, max_depth = 3 })),
            _context);
        Assert.True(list.Success, list.Error);
        Assert.Contains("app.cs", list.Output);
        Assert.DoesNotContain("node_modules", list.Output);

        var grep = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04g", "grep_search",
                Args(new { pattern = "TODO", path = ".", include_glob = "*.cs" })),
            _context);
        Assert.True(grep.Success, grep.Error);
        Assert.Contains("app.cs", grep.Output);
        Assert.Contains("1:", grep.Output);
        Assert.DoesNotContain("x.js", grep.Output);

        var badRegex = await _registry.DispatchAsync(
            new ToolCallDto("call_ac04bad", "grep_search", Args(new { pattern = "([unclosed" })),
            _context);
        Assert.False(badRegex.Success);
        Assert.Contains("正则", badRegex.Error ?? string.Empty);
    }

    // ============ S05-AC05: 超长输出截断落盘 ============

    [Fact(DisplayName = "S05-AC05: 超长输出截断落盘(首4000+尾8000,上限16000)")]
    public void AC05_ToolOutputGuard_Spillover()
    {
        string big = new string('A', 4000) + new string('中', 46000) + new string('Z', 10000);
        Assert.True(big.Length > 50000);

        var (display, spilled) = ToolOutputGuard.ApplyGuard(big, _tempRoot, _workspace.Id, _session.Id, "call_ac05");

        Assert.NotNull(spilled);
        Assert.True(File.Exists(spilled));
        Assert.Equal(big, File.ReadAllText(spilled, Encoding.UTF8));
        Assert.True(display.Length <= ToolOutputGuard.MaxInlineChars, $"截断后长度 {display.Length} 超出上限");
        Assert.Contains("中间省略", display);
        Assert.Contains("read_file", display);
        Assert.StartsWith(new string('A', 100), display);
        Assert.EndsWith(new string('Z', 100), display);

        var (small, smallSpill) = ToolOutputGuard.ApplyGuard("short", _tempRoot, _workspace.Id, _session.Id, "x");
        Assert.Equal("short", small);
        Assert.Null(smallSpill);
    }

    [Fact(DisplayName = "S05-AC05: exec超长输出经Registry自动截断落盘")]
    public async Task AC05_ExecLongOutput_SpilledViaRegistry()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // 输出约 60,000+ 字符
        var result = await _registry.DispatchAsync(
            new ToolCallDto("call_ac05exec", "exec_command",
                Args(new { command = "for /L %i in (1,1,1500) do echo ABCDEFGHIJ0123456789_abcdefghijklmnopqrstuvwxyz_行%i", timeout_ms = 60000 })),
            _context);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.SpilledFile);
        Assert.True(File.Exists(result.SpilledFile));
        Assert.True(result.Output.Length <= ToolOutputGuard.MaxInlineChars, $"返回长度 {result.Output.Length} 超出上限");
        Assert.Contains("中间省略", result.Output);
        Assert.Contains("tool-outputs", result.SpilledFile);
    }

    // ============ 系统路径写保护 ============

    [Fact(DisplayName = "S05-安全: 系统路径写保护(write/edit拦截C盘系统目录)")]
    public async Task AC_Safety_SystemPathWriteBlocked()
    {
        Assert.True(PathSafetyValidator.IsProtectedSystemPath(@"C:\Windows\System32\evil.txt"));
        Assert.True(PathSafetyValidator.IsProtectedSystemPath(@"C:\Program Files\App\a.txt"));
        Assert.True(PathSafetyValidator.IsProtectedSystemPath(@"C:\Program Files (x86)\App\a.txt"));
        Assert.False(PathSafetyValidator.IsProtectedSystemPath(Path.Combine(_projectDir, "ok.txt")));

        var writeBlocked = await _registry.DispatchAsync(
            new ToolCallDto("call_safe1", "write_file",
                Args(new { file_path = @"C:\Windows\Temp\halo_agent_probe_test.txt", content = "evil" })),
            _context);
        Assert.False(writeBlocked.Success);
        Assert.Contains("系统", writeBlocked.Error ?? string.Empty);

        var editBlocked = await _registry.DispatchAsync(
            new ToolCallDto("call_safe2", "edit_file",
                Args(new
                {
                    file_path = @"C:\Program Files\halo_probe.txt",
                    old_string = "a",
                    new_string = "b",
                })),
            _context);
        Assert.False(editBlocked.Success);
        Assert.Contains("严禁", editBlocked.Error ?? string.Empty);
    }

    // ============ 双协议 Schema 与自愈 ============

    [Fact(DisplayName = "S05-注册表: 11工具注册(6标准+4 need09+need14 edit_lines)+chat/resp双协议Schema+未知工具自愈")]
    public async Task AC_Registry_SixTools_DualSchema_SelfHeal()
    {
        var defs = _registry.GetDefinitions();
        Assert.Equal(11, defs.Count);
        foreach (string expected in new[] { "exec_command", "read_file", "write_file", "edit_file", "edit_lines", "list_dir", "grep_search", "view_image", "ask_user", "skill_tool", "memory_tool" })
        {
            Assert.Contains(defs, d => d.Name == expected);
        }

        string chatJson = _registry.GetChatToolsJson();
        using (JsonDocument chat = JsonDocument.Parse(chatJson))
        {
            Assert.Equal(11, chat.RootElement.GetArrayLength());
            Assert.Equal("function", chat.RootElement[0].GetProperty("type").GetString());
            Assert.True(chat.RootElement[0].GetProperty("function").TryGetProperty("name", out _));
        }

        string respJson = _registry.GetResponsesToolsJson();
        using (JsonDocument resp = JsonDocument.Parse(respJson))
        {
            Assert.Equal(11, resp.RootElement.GetArrayLength());
            Assert.Equal("function", resp.RootElement[0].GetProperty("type").GetString());
            Assert.True(resp.RootElement[0].TryGetProperty("name", out _));
        }

        var unknown = await _registry.DispatchAsync(
            new ToolCallDto("call_x", "no_such_tool", "{}"), _context);
        Assert.False(unknown.Success);
        Assert.Contains("未知工具", unknown.Error ?? string.Empty);
        Assert.Contains("exec_command", unknown.Error ?? string.Empty);
    }
}
