#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 工具级集成守卫：真实临时文件走 edit_batch，验证结构化回执的真相层 == 实际写入，
/// 并对上游反馈文档的 6 个真实案例做语义回放。
/// </summary>
public sealed class EditBatchReceiptTests : IDisposable
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
    private readonly EditBatchTool _tool = new();

    public EditBatchReceiptTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ReceiptTests_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(Path.GetTempPath(), $"ReceiptBoot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_bootstrapRoot);

        _storageManager = StoragePathManager.Initialize(
            customBootstrapFilePath: Path.Combine(_bootstrapRoot, "bootstrap.json"),
            customDefaultDataRoot: _tempRoot,
            customFallbackDataRoot: _tempRoot
        );
        _repository = new SessionRepository(_storageManager);
        _service = new WorkspaceSessionService(_repository);
        _registry = new ToolRegistry();
        _projectDir = Path.Combine(_tempRoot, "Projects", "Receipt");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "receipt-project")
            .GetAwaiter()
            .GetResult();
        _session = _service
            .CreateSessionAsync(_workspace.Id, "receipt-session")
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
            Directory.Delete(_tempRoot, true);
        }
        catch { }
        try
        {
            Directory.Delete(_bootstrapRoot, true);
        }
        catch { }
    }

    private string Abs(string name) => Path.Combine(_projectDir, name);

    private static string Args(object o) => JsonSerializer.Serialize(o);

    private Task<ToolResult> CallText(string file, object edits, object? extra = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["file_path"] = file,
            ["mode"] = "text",
            ["edits"] = edits,
        };
        if (extra != null)
        {
            foreach (var p in JsonSerializer.Deserialize<Dictionary<string, object?>>(Args(extra))!)
            {
                args[p.Key] = p.Value;
            }
        }

        return _registry.DispatchAsync(
            new ToolCallDto("c-" + Guid.NewGuid().ToString("N"), "edit_batch", Args(args)),
            _context
        );
    }

    /// <summary>直接调用工具，保留隐藏调试参数（validator 会剥离未登记的 include_diff）。</summary>
    private Task<ToolResult> CallDirect(string argsJson) =>
        _tool.ExecuteAsync(new ToolCallDto("c-direct", "edit_batch", argsJson), _context);

    private static EditReceipt ReceiptOf(ToolResult r)
    {
        Assert.True(r.Success, r.Error);
        Assert.NotNull(r.SuccessPayloadJson);
        var receipt = EditReceipt.FromJson(r.SuccessPayloadJson!);
        Assert.NotNull(receipt);
        return receipt!;
    }

    private static string[] LfLines(string text)
    {
        string lf = text.Replace("\r\n", "\n");
        if (lf.EndsWith("\n", StringComparison.Ordinal))
        {
            lf = lf.Substring(0, lf.Length - 1);
        }

        return lf.Split('\n');
    }

    /// <summary>核心断言：每个 edit 的真相层与读回文件的 at_lines 区间逐字一致。</summary>
    private static void AssertTruthMatchesDisk(string absPath, EditReceipt receipt)
    {
        var detected = FileToolShared.ReadAllTextWithDetection(absPath);
        Assert.Equal(
            FileToolShared.ComputeTextHash(detected.Content, detected),
            receipt.Verify.HashAfter,
            ignoreCase: true
        );

        string[] fileLines = LfLines(detected.Content);
        Assert.Equal(fileLines.Length, receipt.Verify.TotalLines);

        foreach (var edit in receipt.Edits.OrderBy(e => e.Index))
        {
            Assert.True(edit.AtLines.Length == 2);
            Assert.Equal(edit.WrittenLines, LfLines(edit.ResultingText).Length);
            var slice = fileLines.Skip(edit.AtLines[0] - 1).Take(Math.Max(0, edit.WrittenLines));
            Assert.Equal(LfLines(edit.ResultingText), slice.ToArray());
        }
    }

    // ============ 判定层 / 真相层 / 摘要层 ============

    [Fact]
    public async Task TextBatch_ReturnsSuccessPayloadJson_AndTruthMatchesDisk()
    {
        const string file = "truth.txt";
        File.WriteAllText(
            Abs(file),
            "alpha\n<Summary :items=\"a\" />\nomega\n",
            new UTF8Encoding(false)
        );

        var r = await CallText(
            file,
            new[]
            {
                new
                {
                    old_string = "<Summary :items=\"a\" />",
                    new_string = "<Summary\n  :items=\"a\"\n  @pick=\"onPick\"\n/>",
                },
            }
        );

        var receipt = ReceiptOf(r);
        Assert.Single(receipt.Edits);
        Assert.Equal("text", receipt.Mode);
        Assert.False(receipt.DryRun);
        Assert.NotNull(receipt.Backup);
        Assert.True(receipt.Verify.AllEditsMutuallyDisjoint);
        Assert.Equal(4, receipt.Edits[0].WrittenLines);
        Assert.Equal(1, receipt.Edits[0].ReplacedLines);
        AssertTruthMatchesDisk(Abs(file), receipt);
        Assert.Contains("edit[0] ", r.Output);
    }

    [Fact]
    public async Task TextBatch_MultipleEdits_BlocksDisjoint_NoCrossTalk()
    {
        const string file = "multi.txt";
        File.WriteAllText(Abs(file), "AAA_FIRST\nbbb\nmid\nCCC_LAST\n", new UTF8Encoding(false));

        var r = await CallText(
            file,
            new[]
            {
                new { old_string = "AAA_FIRST", new_string = "AAA_FIRST\nEXTRA_1\nEXTRA_2" },
                new { old_string = "CCC_LAST", new_string = "CCC_LAST_PLUS" },
            }
        );

        var receipt = ReceiptOf(r);
        Assert.Equal(2, receipt.Edits.Count);
        Assert.Equal(new[] { 0, 1 }, receipt.Edits.Select(e => e.Index).ToArray());
        // 第一个 edit +2 行，第二个 edit 的落点必须按落盘后文本换算（原文 L4 → 落盘 L6）。
        Assert.Equal(6, receipt.Edits[1].AtLines[0]);
        AssertTruthMatchesDisk(Abs(file), receipt);
    }

    [Fact]
    public async Task LinesBatch_ReceiptCorrect()
    {
        const string file = "lines.txt";
        File.WriteAllText(Abs(file), "L1\nL2\nL3\nL4\n", new UTF8Encoding(false));

        var r = await _registry.DispatchAsync(
            new ToolCallDto(
                "c-lines",
                "edit_batch",
                Args(
                    new
                    {
                        file_path = file,
                        mode = "lines",
                        edits = new[]
                        {
                            new
                            {
                                offset = 2,
                                limit = 1,
                                new_text = "L2_NEW_A\nL2_NEW_B"
                            },
                            new
                            {
                                offset = 4,
                                limit = 1,
                                new_text = "L4_NEW"
                            },
                        },
                    }
                )
            ),
            _context
        );

        var receipt = ReceiptOf(r);
        Assert.Equal("lines", receipt.Mode);
        Assert.Equal(2, receipt.Edits.Count);
        Assert.Equal(1, receipt.Edits[0].ReplacedLines);
        Assert.Equal(2, receipt.Edits[0].WrittenLines);
        AssertTruthMatchesDisk(Abs(file), receipt);
    }

    [Fact]
    public async Task DryRun_ReceiptMarked_NoWrite()
    {
        const string file = "dry.txt";
        const string original = "keep_alpha\nkeep_beta\n";
        File.WriteAllText(Abs(file), original, new UTF8Encoding(false));
        long beforeTicks = File.GetLastWriteTimeUtc(Abs(file)).Ticks;

        var r = await CallText(
            file,
            new[] { new { old_string = "keep_alpha", new_string = "dry_alpha" } },
            new { dry_run = true }
        );

        var receipt = ReceiptOf(r);
        Assert.True(receipt.DryRun);
        Assert.Null(receipt.Backup);
        Assert.Equal(original, File.ReadAllText(Abs(file)));
        Assert.Equal(beforeTicks, File.GetLastWriteTimeUtc(Abs(file)).Ticks);
        Assert.Contains("DRY_RUN", r.Output);
    }

    [Fact]
    public async Task ExpectedHash_Chain_Success()
    {
        const string file = "chain.txt";
        File.WriteAllText(Abs(file), "step_one\nstep_two\n", new UTF8Encoding(false));

        var first = await CallText(
            file,
            new[] { new { old_string = "step_one", new_string = "STEP_ONE" } }
        );
        var firstReceipt = ReceiptOf(first);
        Assert.False(firstReceipt.Verify.ExpectedHashChecked);

        var second = await CallText(
            file,
            new[] { new { old_string = "step_two", new_string = "STEP_TWO" } },
            new { expected_hash = firstReceipt.Verify.HashAfter }
        );
        var secondReceipt = ReceiptOf(second);
        Assert.True(secondReceipt.Verify.ExpectedHashChecked);
        AssertTruthMatchesDisk(Abs(file), secondReceipt);
    }

    [Fact]
    public async Task ExpectedHash_Mismatch_PayloadHasExpectedAndActual()
    {
        const string file = "mismatch.txt";
        File.WriteAllText(Abs(file), "aaa_bbb\n", new UTF8Encoding(false));

        var r = await CallText(
            file,
            new[] { new { old_string = "aaa_bbb", new_string = "AAA_BBB" } },
            new
            {
                expected_hash = "0000000000000000000000000000000000000000000000000000000000000000"
            }
        );

        Assert.False(r.Success);
        Assert.Equal("EXPECTED_HASH_MISMATCH", r.ErrorCode);
        Assert.Null(r.SuccessPayloadJson);
        Assert.NotNull(r.ErrorPayloadJson);
        using var doc = JsonDocument.Parse(r.ErrorPayloadJson!);
        Assert.Equal(64, doc.RootElement.GetProperty("expected").GetString()!.Length);
        Assert.Equal(64, doc.RootElement.GetProperty("actual").GetString()!.Length);
    }

    [Fact]
    public async Task Ambiguous_ReportsOccurrences()
    {
        const string file = "ambiguous.txt";
        File.WriteAllText(
            Abs(file),
            "dup_line_here\nsomething\ndup_line_here\n",
            new UTF8Encoding(false)
        );

        var r = await CallText(
            file,
            new[] { new { old_string = "dup_line_here", new_string = "replacement_line" } }
        );

        Assert.False(r.Success);
        Assert.Equal("ANCHOR_AMBIGUOUS", r.ErrorCode);
        Assert.Null(r.SuccessPayloadJson);
        using var doc = JsonDocument.Parse(r.ErrorPayloadJson!);
        Assert.True(doc.RootElement.GetProperty("occurrences").GetInt32() > 1);
        Assert.Equal(0, doc.RootElement.GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task NoChange_NoReceipt()
    {
        const string file = "nochange.txt";
        File.WriteAllText(Abs(file), "same_content\n", new UTF8Encoding(false));

        var r = await CallText(
            file,
            new[] { new { old_string = "same_content", new_string = "same_content" } }
        );

        Assert.False(r.Success);
        Assert.Contains("NO_CHANGE", r.Error ?? string.Empty);
        Assert.Null(r.SuccessPayloadJson);
    }

    [Fact]
    public async Task IncludeDiff_WhenRequested_AppendsDebugView()
    {
        const string file = "diffview.txt";
        File.WriteAllText(Abs(file), "before_line\nkeep_line\n", new UTF8Encoding(false));

        string argsJson = Args(
            new
            {
                file_path = file,
                mode = "text",
                include_diff = true,
                edits = new[] { new { old_string = "before_line", new_string = "AFTER_LINE" } },
            }
        );
        var r = await CallDirect(argsJson);

        var receipt = ReceiptOf(r);
        Assert.Contains("```diff", r.Output);
        Assert.Contains("@@", r.Output);
        AssertTruthMatchesDisk(Abs(file), receipt);
    }

    // ============ 6 个真实案例语义回放 ============

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task RealCase_Replay_TruthEqualsDisk(int caseId)
    {
        var (source, anchor, replacement, minWritten, forbidden) = BuildCase(caseId);
        string file = $"case{caseId}.txt";
        File.WriteAllText(Abs(file), source, new UTF8Encoding(false));

        var r = await CallText(
            file,
            new[] { new { old_string = anchor, new_string = replacement } }
        );
        var receipt = ReceiptOf(r);

        var edit = Assert.Single(receipt.Edits);
        Assert.True(edit.WrittenLines >= minWritten, $"case {caseId}: written too small");
        // replaced_lines 必须等于锚点在原文中的真实行数（多数案例 1 行，末尾整块追加案例为多行）。
        int anchorLines = LfLines(anchor).Length;
        Assert.Equal(anchorLines, edit.ReplacedLines);
        Assert.Contains(replacement.Split('\n')[0], edit.ResultingText);
        foreach (string bad in forbidden)
        {
            Assert.DoesNotContain(bad, r.Output);
        }

        AssertTruthMatchesDisk(Abs(file), receipt);
    }

    private static (
        string Source,
        string Anchor,
        string Replacement,
        int MinWritten,
        string[] Forbidden
    ) BuildCase(int id) =>
        id switch
        {
            // 1. link_open rule 单行 → 后插整段 renderCodeWithCopy + rule 注册。
            1
                => (
                    "import { renderLink } from './link';\nif (linkOpen) return renderLink(state);\nfunction tail() { return 1; }\n}\n}\n",
                    "if (linkOpen) return renderLink(state);",
                    "if (linkOpen) return renderLink(state);\nfunction renderCodeWithCopy(s) { return s; }\nmd.inline.ruler.after('link', 'code_with_copy', renderCodeWithCopy);\n}",
                    3,
                    new[] { "没闭合" }
                ),
            // 2. onReady 单行 → 插入复制委托绑定/解绑。
            2
                => (
                    "export function useTimeline() {\nonReady(() => start());\nreturn api;\n}\n",
                    "onReady(() => start());",
                    "onReady(() => start());\nbindCopyDelegate();\nonUnmount(unbindCopyDelegate);",
                    3,
                    new[] { "重复行" }
                ),
            // 3. 用户气泡 <div → 新增 @contextmenu 一行（差值 1）。
            3
                => (
                    "<template>\n<div class=\"bubble-user\" :data-id=\"id\">\n{{ text }}\n</div>\n</template>\n",
                    "<div class=\"bubble-user\" :data-id=\"id\">",
                    "<div class=\"bubble-user\" :data-id=\"id\" @contextmenu=\"openMenu\">",
                    1,
                    Array.Empty<string>()
                ),
            // 4. 单行组件标签 → 展开为多行 + 两个事件（1→6 行头号守卫）。
            4
                => (
                    "<div>\n<TimelineSummarySection v-if=\"summaryList.length > 0\" :summaries=\"summaryList\" />\n<TimelineEmptyState v-if=\"isEmpty\" />\n</div>\n",
                    "<TimelineSummarySection v-if=\"summaryList.length > 0\" :summaries=\"summaryList\" />",
                    "<TimelineSummarySection\n  v-if=\"summaryList.length > 0\"\n  :summaries=\"summaryList\"\n  @user-menu=\"onSummaryUserMenu\"\n  @assistant-menu=\"onSummaryAssistantMenu\"\n/>",
                    6,
                    new[] { "没闭合", "标签没闭合" }
                ),
            // 5. 文件末尾锚点 → 追加整个 describe 块。
            5
                => (
                    "describe('summary section', () => {\nit('renders', () => {});\n});\n",
                    "describe('summary section', () => {\nit('renders', () => {});\n});",
                    "describe('summary section', () => {\nit('renders', () => {});\n});\ndescribe('summary menu bridge', () => {\nit('emits on pick', async () => {});\nit('emits on dismiss', async () => {});\n});",
                    5,
                    new[] { "被换成" }
                ),
            // 6. expect(emitted) 引用断言 → 字段级断言。
            6
                => (
                    "it('emits', async () => {\nawait wrapper.trigger('click');\nexpect(emitted).toBe(EXPECTED_REF);\n});\n",
                    "expect(emitted).toBe(EXPECTED_REF);",
                    "expect(emitted[0]).toBe('user-menu');\nexpect(emitted[0][0].id).toBe('target');",
                    2,
                    new[] { "原样显示" }
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
}
