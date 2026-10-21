#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need14 验收：edit_file 宽容匹配（R1）/ edit_lines 行号编辑（R2）/ 结构化诊断（R3）/
/// 成功回吐截断（R4）/ 高危锚点拦截（R5）。对应需求稿 §5 的 A1–A7。
/// </summary>
public sealed class Stage14EditFileToolTests : IDisposable
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

    public Stage14EditFileToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentStage14Tests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentStage14Bootstrap_{Guid.NewGuid():N}"
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
        _registry = new ToolRegistry();

        _projectDir = Path.Combine(_tempRoot, "Projects", "Need14");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "need14 验收项目")
            .GetAwaiter()
            .GetResult();
        _session = _service
            .CreateSessionAsync(_workspace.Id, "need14 验收会话")
            .GetAwaiter()
            .GetResult();

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

    // ============ A1：hostBridge.ts 式大文件（CRLF + Tab + 超长单行）6 处改动一次成功 ============

    [Fact(DisplayName = "need14-A1: CRLF+Tab+超长单行的 mock 区 6 处改动 normalized 下一次成功")]
    public async Task A1_HostBridgeLike_MockRegion_SixEdits_OneShot()
    {
        const string fileName = "hostBridge.mock.ts";
        File.WriteAllText(Absolute(fileName), BuildHostBridgeLikeSource(), new UTF8Encoding(false));

        // (1) 单行行尾锚点（无缩进）→ 追加 7 个子代理字段
        ToolResult edit1 = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "maxIterations: 25,",
                new_string = "maxIterations: 25,\n  subAgents: { enabled: true, maxParallel: 4, timeoutMs: 60000, retry: 2, logLevel: 'info', model: 'gpt-4o', allowWrite: false },",
            }
        );
        Assert.True(edit1.Success, edit1.Error);

        // (2) 老 exact 行为必须仍然失败（证明兼容 + normalized 才是解药）
        ToolResult exactMiss = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "    async getUsageDbStatus() {\n        return { ok: true, rows: 0 };\n    }",
                new_string = "    async getUsageDbStatus() {\n        return { ok: true, rows: 9 };\n    }",
                match_mode = "exact",
            }
        );
        Assert.False(exactMiss.Success);
        Assert.Contains("OLD_STRING_NOT_FOUND", exactMiss.Error ?? string.Empty);

        // (3) 同一 old_string 在默认 normalized 下一次成功
        ToolResult edit2 = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "    async getUsageDbStatus() {\n        return { ok: true, rows: 0 };\n    }",
                new_string = "    async getUsageDbStatus() {\n        return { ok: true, rows: 9 };\n    }",
            }
        );
        Assert.True(edit2.Success, edit2.Error);

        // (4) 文件行尾带空格，old_string 不带 → 忽略行尾空格差异
        // (4) 文件行尾带空格，old_string 不带 → 忽略行尾空格差异
        ToolResult edit3 = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "version: 1,",
                new_string = "version: 2,",
            }
        );
        Assert.True(edit3.Success, edit3.Error);

        // (5) 单行行尾锚点 → 追加 mock 方法
        ToolResult edit4 = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "lastRejection: null } }; }",
                new_string = "lastRejection: null } }; }\n  mockGetUsage: () => ({ ok: true }),",
            }
        );
        Assert.True(edit4.Success, edit4.Error);

        // (6) 4 空格缩进锚点 vs Tab 缩进落盘
        ToolResult edit5 = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "    execDefault: { runner: 'cmd', timeoutMs: 30000 },",
                new_string = "    execDefault: { runner: 'cmd', timeoutMs: 60000 },",
            }
        );
        Assert.True(edit5.Success, edit5.Error);

        // (7) 超长单行（> 400 字符）行内锚点
        ToolResult edit6 = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "item59-payload,item60-payload",
                new_string = "item59-payload,item60-payload,PATCHED",
            }
        );
        Assert.True(edit6.Success, edit6.Error);

        string after = File.ReadAllText(Absolute(fileName), Encoding.UTF8);
        Assert.Contains("subAgents", after);
        Assert.Contains("rows: 9", after);
        Assert.Contains("version: 2,", after);
        Assert.Contains("mockGetUsage", after);
        Assert.Contains("timeoutMs: 60000", after);
        Assert.Contains("PATCHED", after);

        // 只换目标区：全文件仍是纯 CRLF，未被编辑的 Tab 缩进行逐字节保持
        Assert.Equal(CountOf(after, "\n"), CountOf(after, "\r\n"));
        Assert.Contains("\tconst longLine = '", after);
        Assert.Contains("rows: 9", after);
        Assert.DoesNotContain("version: 2, ", after); // 行尾空格被目标区一并替换
    }

    // ============ A2：Tab / CRLF / 超长单行 各 1 例，一次成功 ============

    [Fact(DisplayName = "need14-A2: Tab文件+CRLF文件+超长单行各1例一次成功")]
    public async Task A2_TabFile_CrlfFile_LongLine()
    {
        // (1) Tab 落盘 + 空格锚点
        const string tabFile = "tab_indented.cs";
        File.WriteAllText(
            Absolute(tabFile),
            "\tif (x) {\r\n\t\treturn;\r\n\t}\r\n",
            new UTF8Encoding(false)
        );
        ToolResult tabEdit = await Call(
            "edit_file",
            new
            {
                file_path = tabFile,
                old_string = "    if (x) {\n        return;\n    }",
                new_string = "        if (x) {\n            return 42;\n        }",
            }
        );
        Assert.True(tabEdit.Success, tabEdit.Error);
        Assert.Contains("return 42;", File.ReadAllText(Absolute(tabFile), Encoding.UTF8));

        // (2) CRLF 落盘 + LF 锚点
        const string crlfFile = "crlf_sample.txt";
        File.WriteAllText(
            Absolute(crlfFile),
            "alpha\r\nbeta\r\ngamma\r\n",
            new UTF8Encoding(false)
        );
        ToolResult crlfEdit = await Call(
            "edit_file",
            new
            {
                file_path = crlfFile,
                old_string = "alpha\nbeta",
                new_string = "alpha\nBETA",
            }
        );
        Assert.True(crlfEdit.Success, crlfEdit.Error);
        string crlfAfter = File.ReadAllText(Absolute(crlfFile), Encoding.UTF8);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", crlfAfter);

        // (3) 超长单行（> 400 字符）行内替换
        const string longFile = "long_line.ts";
        string longLine =
            "const registry = '"
            + string.Join(",", Enumerable.Range(0, 60).Select(i => $"node{i:D2}"))
            + "';";
        Assert.True(longLine.Length > 400, $"样本长度不足：{longLine.Length}");
        File.WriteAllText(Absolute(longFile), longLine + "\r\n", new UTF8Encoding(false));
        ToolResult longEdit = await Call(
            "edit_file",
            new
            {
                file_path = longFile,
                old_string = "node30,node31",
                new_string = "node30,node31,node31b",
            }
        );
        Assert.True(longEdit.Success, longEdit.Error);
        Assert.Contains(
            "node30,node31,node31b",
            File.ReadAllText(Absolute(longFile), Encoding.UTF8)
        );
    }

    // ============ A3：错 1 个字符 → Top-3 行号 + 空白符可视化 + 可执行建议 ============

    [Fact(DisplayName = "need14-A3: old_string 错1字符时返回结构化诊断（Top-3行号+空白可视化+建议）")]
    public async Task A3_NotFound_ReturnsStructuredDiagnostic()
    {
        const string fileName = "diagnostic.ts";
        var sb = new StringBuilder();
        sb.Append("// 诊断样本\r\n");
        for (int i = 1; i <= 10; i++)
        {
            sb.Append("\tconst value")
                .Append(i.ToString("D2"))
                .Append(" = compute(")
                .Append(i)
                .Append(");\r\n");
        }

        for (int i = 1; i <= 5; i++)
        {
            sb.Append("\t\treturn { ok: true, rows: ").Append(i).Append(" };\r\n");
        }

        sb.Append("\tasync getUsageDbStatus() {\r\n");
        sb.Append("\t\treturn { ok: true, rows: 0 };\r\n");
        sb.Append("\t}\r\n");
        File.WriteAllText(Absolute(fileName), sb.ToString(), new UTF8Encoding(false));

        // 与第 18 行仅差 1 个字符（true → ture），且另有 5 行同形状候选
        ToolResult miss = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "\t\treturn { ok: ture, rows: 0 };",
                new_string = "\t\treturn { ok: true, rows: 1 };",
            }
        );

        Assert.False(miss.Success);
        Assert.StartsWith("OLD_STRING_NOT_FOUND", miss.Error ?? string.Empty);
        string payload = ExtractJson(miss.ErrorPayloadJson ?? miss.Error ?? string.Empty);
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("OLD_STRING_NOT_FOUND", root.GetProperty("code").GetString());
        Assert.Equal("CRLF", root.GetProperty("file_eol").GetString());
        Assert.Equal(fileName.Replace('\\', '/'), root.GetProperty("file").GetString());
        Assert.True(root.GetProperty("file_lines").GetInt32() >= 18);

        JsonElement nearest = root.GetProperty("nearest");
        Assert.Equal(3, nearest.GetArrayLength());
        Assert.Equal(18, nearest[0].GetProperty("line").GetInt32());
        Assert.True(nearest[0].GetProperty("score").GetDouble() > 0.8);

        string preview = nearest[0].GetProperty("preview").GetString() ?? string.Empty;
        Assert.Contains("↵", preview); // 行尾可视化
        Assert.Contains("→", preview); // Tab 可视化
        Assert.Contains("rows: 0", preview);

        string hint = root.GetProperty("hint").GetString() ?? string.Empty;
        Assert.Contains("edit_lines", hint);
        Assert.Contains("offset=18", hint);
        Assert.Contains("Tab", hint); // 命中行行首为 Tab 时必须点明
    }

    // ============ A4：`},` + replace_all 被 ANCHOR_TOO_SHORT 拒绝 ============

    [Fact(DisplayName = "need14-A4: 短锚点+replace_all 被拒绝且错误码为 ANCHOR_TOO_SHORT")]
    public async Task A4_ShortAnchor_WithReplaceAll_Rejected()
    {
        const string fileName = "anchor_guard.ts";
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++)
        {
            sb.Append("\tobj").Append(i).Append(": { a: ").Append(i).Append(" },\r\n");
        }

        File.WriteAllText(Absolute(fileName), sb.ToString(), new UTF8Encoding(false));
        string before = File.ReadAllText(Absolute(fileName), Encoding.UTF8);

        ToolResult blocked = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "},",
                new_string = "};",
                replace_all = true,
            }
        );

        Assert.False(blocked.Success);
        Assert.Contains("ANCHOR_TOO_SHORT", blocked.Error ?? string.Empty);
        Assert.Contains("edit_lines", blocked.Error ?? string.Empty);
        Assert.Equal(before, File.ReadAllText(Absolute(fileName), Encoding.UTF8));

        // 长锚点 + replace_all 仍然放行
        ToolResult allowed = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "obj7: { a: 7 }",
                new_string = "obj7: { a: 70 }",
                replace_all = true,
            }
        );
        Assert.True(allowed.Success, allowed.Error);
    }

    // ============ A5：成功回吐 ≤ 2000 字符（含截断标记） ============

    [Fact(DisplayName = "need14-A5: 成功回吐严格≤2000字符并带 truncated 标记")]
    public async Task A5_SuccessOutput_IsClamped()
    {
        const string fileName = "big_output.txt";
        File.WriteAllText(
            Absolute(fileName),
            "BEGIN\r\nreplace-me\r\nEND\r\n",
            new UTF8Encoding(false)
        );

        string huge =
            "replace-me\r\n"
            + string.Join(
                "\r\n",
                Enumerable.Range(0, 400).Select(i => $"line-{i:D3}-" + new string('x', 60))
            );
        ToolResult big = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "replace-me",
                new_string = huge,
            }
        );

        Assert.True(big.Success, big.Error);
        Assert.True(big.Output.Length <= 2000, $"成功回吐 {big.Output.Length} 字符，超过 2000 上限");
        Assert.Contains("truncated", big.Output);

        // 小改动同样是短回吐
        ToolResult small = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "BEGIN",
                new_string = "BEGIN!",
            }
        );
        Assert.True(small.Success, small.Error);
        Assert.True(small.Output.Length <= 2000);
        Assert.Contains("```diff", small.Output);
    }

    // ============ A6：edit_lines 在 CRLF/Tab 文件上逐字节正确 ============

    [Fact(DisplayName = "need14-A6: edit_lines 在 CRLF+Tab 文件上逐字节正确（仅目标行变化）")]
    public async Task A6_EditLines_ByteExact_On_CrlfTabFile()
    {
        const string fileName = "crlf_tab.cs";
        const string original =
            "class Sample {\r\n\tvoid Foo() {\r\n\t\tint a = 1;\r\n\t\tint b = 2;\r\n\t}\r\n}\r\n";
        File.WriteAllText(Absolute(fileName), original, new UTF8Encoding(false));

        // 行号口径与 read_file 一致
        ToolResult readBack = await Call(
            "read_file",
            new
            {
                file_path = fileName,
                offset = 3,
                limit = 2
            }
        );
        Assert.True(readBack.Success, readBack.Error);
        Assert.Contains("int a = 1;", readBack.Output);
        Assert.Contains("int b = 2;", readBack.Output);

        ToolResult edit = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 3,
                limit = 2,
                new_text = "\t\tint a = 10;\n\t\tint b = 20;\n\t\tint c = 30;",
            }
        );
        Assert.True(edit.Success, edit.Error);

        const string expected =
            "class Sample {\r\n\tvoid Foo() {\r\n\t\tint a = 10;\r\n\t\tint b = 20;\r\n\t\tint c = 30;\r\n\t}\r\n}\r\n";
        Assert.Equal(expected, File.ReadAllText(Absolute(fileName), Encoding.UTF8));
        Assert.Equal(
            expected,
            File.ReadAllText(Absolute(fileName), Encoding.UTF8).Replace("\r\n", "\r\n")
        );
        Assert.DoesNotContain(
            "\n\n",
            File.ReadAllText(Absolute(fileName), Encoding.UTF8).Replace("\r\n", "\n")
        );

        // 纯插入（limit = 0）
        ToolResult insert = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 2,
                limit = 0,
                new_text = "\t// inserted marker",
            }
        );
        Assert.True(insert.Success, insert.Error);
        string afterInsert = File.ReadAllText(Absolute(fileName), Encoding.UTF8);
        Assert.Equal(
            "class Sample {\r\n\t// inserted marker\r\n\tvoid Foo() {\r\n\t\tint a = 10;\r\n\t\tint b = 20;\r\n\t\tint c = 30;\r\n\t}\r\n}\r\n",
            afterInsert
        );
        Assert.Equal(CountOf(afterInsert, "\n"), CountOf(afterInsert, "\r\n"));
    }

    [Fact(DisplayName = "need14-A6b: edit_lines 的 hash 校验/空文本/越界保护")]
    public async Task A6b_EditLines_HashAndGuards()
    {
        const string fileName = "guarded.cs";
        const string original = "one\r\ntwo\r\nthree\r\n";
        File.WriteAllText(Absolute(fileName), original, new UTF8Encoding(false));

        // 空 new_text 未显式 allow_empty → 拒绝
        ToolResult emptyBlocked = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 2,
                limit = 1,
                new_text = string.Empty,
            }
        );
        Assert.False(emptyBlocked.Success);
        Assert.Contains("EMPTY_NEW_TEXT", emptyBlocked.Error ?? string.Empty);
        Assert.Equal(original, File.ReadAllText(Absolute(fileName), Encoding.UTF8));

        // expected_hash 不符 → 拒绝
        ToolResult hashBlocked = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 2,
                limit = 1,
                new_text = "TWO",
                expected_hash = new string('0', 64),
            }
        );
        Assert.False(hashBlocked.Success);
        Assert.Contains("EXPECTED_HASH_MISMATCH", hashBlocked.Error ?? string.Empty);
        Assert.Equal(original, File.ReadAllText(Absolute(fileName), Encoding.UTF8));

        // expected_hash 相符 → 放行
        string fileHash = Sha256Hex(File.ReadAllBytes(Absolute(fileName)));
        ToolResult hashOk = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 2,
                limit = 1,
                new_text = "TWO",
                expected_hash = "sha256:" + fileHash,
            }
        );
        Assert.True(hashOk.Success, hashOk.Error);
        Assert.Equal(
            "one\r\nTWO\r\nthree\r\n",
            File.ReadAllText(Absolute(fileName), Encoding.UTF8)
        );

        // 越界 → OUT_OF_RANGE
        ToolResult outOfRange = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 99,
                limit = 1,
                new_text = "x",
            }
        );
        Assert.False(outOfRange.Success);
        Assert.Contains("OUT_OF_RANGE", outOfRange.Error ?? string.Empty);

        // 显式 allow_empty → 删除整行
        ToolResult deletion = await Call(
            "edit_lines",
            new
            {
                file_path = fileName,
                offset = 2,
                limit = 1,
                new_text = string.Empty,
                allow_empty = true,
            }
        );
        Assert.True(deletion.Success, deletion.Error);
        Assert.Equal("one\r\nthree\r\n", File.ReadAllText(Absolute(fileName), Encoding.UTF8));
    }

    // ============ A7：老调用（仅 4 个旧参数）行为不变 ============

    [Fact(DisplayName = "need14-A7: 老4参数调用行为不变（唯一性/替换/replace_all/自愈诊断）")]
    public async Task A7_LegacyFourParams_BehaviourUnchanged()
    {
        const string fileName = "legacy.txt";
        File.WriteAllText(Absolute(fileName), "line1\nline2\nline3\n", new UTF8Encoding(false));

        // 1) 单处替换：成功且落盘正确
        ToolResult once = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "line2",
                new_string = "line2-edited",
            }
        );
        Assert.True(once.Success, once.Error);
        Assert.Contains("--- a/", once.Output);
        Assert.Contains("+++ b/", once.Output);
        Assert.Contains("@@", once.Output);
        Assert.Equal(
            "line1\nline2-edited\nline3\n",
            File.ReadAllText(Absolute(fileName), Encoding.UTF8)
        );

        // 2) 多处命中且未开 replace_all：need15 后为 ANCHOR_AMBIGUOUS（含 matched 兼容词）
        ToolResult ambiguous = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "line",
                new_string = "LINE",
            }
        );
        Assert.False(ambiguous.Success);
        Assert.True(
            (ambiguous.Error ?? string.Empty).Contains("ANCHOR_AMBIGUOUS")
                || (ambiguous.Error ?? string.Empty).Contains(
                    "matched",
                    StringComparison.OrdinalIgnoreCase
                ),
            ambiguous.Error
        );

        // 3) 长锚点 + replace_all：处处替换
        File.WriteAllText(
            Absolute(fileName),
            "aaa-keep-1 bbb\r\naaa-keep-1 bbb\r\n",
            new UTF8Encoding(false)
        );
        ToolResult all = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "aaa-keep-1 bbb",
                new_string = "aaa-keep-1 ccc",
                replace_all = true,
            }
        );
        Assert.True(all.Success, all.Error);
        Assert.Equal(
            "aaa-keep-1 ccc\r\naaa-keep-1 ccc\r\n",
            File.ReadAllText(Absolute(fileName), Encoding.UTF8)
        );

        // 4) 完全不存在的锚点：仍失败，但改由结构化诊断承载（R3）
        ToolResult missing = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "zzz-not-here-at-all",
                new_string = "x",
            }
        );
        Assert.False(missing.Success);
        Assert.Contains("OLD_STRING_NOT_FOUND", missing.Error ?? string.Empty);

        // 5) 缺失必需参数仍旧报错
        ToolResult noOld = await Call("edit_file", new { file_path = fileName, new_string = "x" });
        Assert.False(noOld.Success);
        Assert.Contains("old_string", noOld.Error ?? string.Empty);
    }

    [Fact(DisplayName = "need14-A7b: regex 模式与 ignore_leading_whitespace 兜底可用")]
    public async Task A7b_RegexAndIgnoreLeadingWhitespace()
    {
        const string fileName = "regex_src.cs";
        File.WriteAllText(
            Absolute(fileName),
            "\tint a = 1;\r\n\tint b = 2;\r\n\tint c = 3;\r\n",
            new UTF8Encoding(false)
        );

        // regex：按模式批量改写
        // regex：按模式批量改写（need15 P0-1 后 $ 默认字面，显式 opt-in 才展开）
        ToolResult regex = await Call(
            "edit_file",
            new
            {
                file_path = fileName,
                old_string = "int (\\w) = (\\d);",
                new_string = "int $1 = $2$2;",
                match_mode = "regex",
                replace_all = true,
                regex_expand_groups = true,
            }
        );
        Assert.True(regex.Success, regex.Error);
        string afterRegex = File.ReadAllText(Absolute(fileName), Encoding.UTF8);
        Assert.Contains("int a = 11;", afterRegex);
        Assert.Contains("int b = 22;", afterRegex);
        Assert.Contains("int c = 33;", afterRegex);

        // ignore_leading_whitespace：极端缩进漂移也能命中
        const string driftFile = "drift.cs";
        File.WriteAllText(
            Absolute(driftFile),
            "\t\t\tif (flag) {\r\n\t\t\t\tflag = false;\r\n\t\t\t}\r\n",
            new UTF8Encoding(false)
        );
        ToolResult drift = await Call(
            "edit_file",
            new
            {
                file_path = driftFile,
                old_string = "if (flag) {\nflag = false;\n}",
                new_string = "if (flag) {\nflag = true;\n}",
                ignore_leading_whitespace = true,
            }
        );
        Assert.True(drift.Success, drift.Error);
        Assert.Contains("flag = true;", File.ReadAllText(Absolute(driftFile), Encoding.UTF8));
    }

    // ============ 非功能需求：性能预算 ============

    [Fact(DisplayName = "need14-perf: 1MB文件normalized匹配<200ms、3000行nearest搜索<500ms")]
    public async Task Perf_NormalizedMatch_And_NearestSearch()
    {
        // (1) 1MB / CRLF / Tab 文件：单点 normalized 命中
        var big = new StringBuilder(1024 * 1024 + 4096);
        for (int i = 0; big.Length < 1024 * 1024; i++)
        {
            big.Append("\t\tconfigValue")
                .Append(i)
                .Append(" = compute(runtime, flags); // filler\r\n");
        }

        const string bigFile = "big_perf.ts";
        File.WriteAllText(Absolute(bigFile), big.ToString(), new UTF8Encoding(false));

        ToolResult hit = await Call(
            "edit_file",
            new
            {
                file_path = bigFile,
                old_string = "configValue7777 = compute(runtime, flags); // filler",
                new_string = "configValue7777 = compute(runtime, flags, extra); // filler",
            }
        );
        Assert.True(hit.Success, hit.Error);
        Assert.True(hit.DurationMs < 200, $"1MB normalized 匹配耗时 {hit.DurationMs}ms，超出 200ms 预算");

        // (2) 3000 行文件未命中：nearest 搜索耗时
        var many = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            many.Append("\tconst item").Append(i).Append(" = factory(").Append(i).Append(");\r\n");
        }

        const string manyFile = "many_lines.ts";
        File.WriteAllText(Absolute(manyFile), many.ToString(), new UTF8Encoding(false));

        ToolResult miss = await Call(
            "edit_file",
            new
            {
                file_path = manyFile,
                old_string = "\tconst item1500 = factry(1500);",
                new_string = "x",
            }
        );
        Assert.False(miss.Success);
        Assert.Contains("OLD_STRING_NOT_FOUND", miss.Error ?? string.Empty);
        Assert.True(miss.DurationMs < 500, $"3000 行 nearest 搜索耗时 {miss.DurationMs}ms，超出 500ms 预算");
    }

    // ============ 工具与样本 ============

    private string Absolute(string name) => Path.Combine(_projectDir, name);

    private Task<ToolResult> Call(string toolName, object args) =>
        _registry.DispatchAsync(
            new ToolCallDto("call_" + Guid.NewGuid().ToString("N"), toolName, Args(args)),
            _context
        );

    private static string Args(object o) =>
        JsonSerializer.Serialize(
            o,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

    private static int CountOf(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string Sha256Hex(byte[] payload)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(payload);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>need13 实战样本复刻：CRLF + Tab 缩进 + 单行 &gt; 400 字符 + 行尾空格。</summary>
    private static string BuildHostBridgeLikeSource()
    {
        var sb = new StringBuilder();
        sb.Append("// mock bridge fixture（need14 A1：CRLF + Tab + 超长单行）\r\n");
        sb.Append("export interface MockBridgeConfig {\r\n");
        sb.Append("\tmaxIterations: 25,\r\n");
        sb.Append("\tversion: 1,   \r\n");
        sb.Append("\tlastRejection: null } }; }\r\n");
        sb.Append("\r\n");
        sb.Append("export const mockAgentDefaults = {\r\n");
        sb.Append("\texecDefault: { runner: 'cmd', timeoutMs: 30000 },\r\n");
        sb.Append("\tasync getUsageDbStatus() {\r\n");
        sb.Append("\t\treturn { ok: true, rows: 0 };\r\n");
        sb.Append("\t}\r\n");
        sb.Append("};\r\n");
        sb.Append("\r\n");
        sb.Append("\tconst longLine = '");
        for (int i = 0; i < 70; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("item").Append(i.ToString("D2")).Append("-payload");
        }

        sb.Append("';\r\n");
        return sb.ToString();
    }

    private static string ExtractJson(string s)
    {
        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : s;
    }
}
