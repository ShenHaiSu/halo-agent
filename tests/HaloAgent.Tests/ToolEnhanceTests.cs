#nullable enable
using System;
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
using Host.Desktop.Tools.Validation;
using Xunit;

namespace HaloAgent.Tests;

public sealed class ToolEnhanceTests : IDisposable
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

    public ToolEnhanceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"HaloAgentEnhance_{Guid.NewGuid():N}");
        _bootstrapRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloAgentEnhanceBoot_{Guid.NewGuid():N}"
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

        _projectDir = Path.Combine(_tempRoot, "Projects", "Enhance");
        Directory.CreateDirectory(_projectDir);

        _workspace = _service
            .AddOrUpdateWorkspaceAsync(_projectDir, "enhance")
            .GetAwaiter()
            .GetResult();
        _session = _service
            .CreateSessionAsync(_workspace.Id, "enhance-session")
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

    private Task<ToolResult> Call(string tool, string argsJson) =>
        _registry.DispatchAsync(
            new ToolCallDto("c-" + Guid.NewGuid().ToString("N"), tool, argsJson),
            _context
        );

    private static string Sha256OfFile(string path)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static string Args(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void ReadOnly_ExtraField_Stripped_WithWarning()
    {
        var r = ToolCallValidator.Validate(
            "read_file",
            "{\"file_path\":\"a.txt\",\"explanation\":\"hi\"}"
        );
        Assert.True(r.Success, r.Message);
        Assert.DoesNotContain("explanation", r.NormalizedJson ?? string.Empty);
        Assert.Contains(r.Warnings, w => w.Contains("STRIPPED_UNKNOWN_PARAM"));
    }

    [Fact]
    public void Write_MisspelledSafetyField_Rejected_WithGuess()
    {
        var r = ToolCallValidator.Validate(
            "write_file",
            "{\"file_path\":\"a.txt\",\"content\":\"x\",\"dryrun\":true}"
        );
        Assert.False(r.Success);
        Assert.Equal("UNKNOWN_ARGUMENT", r.Code);
        Assert.Contains("dry_run", r.Message);
    }

    [Fact]
    public void Write_SubstringGuard_Rejected()
    {
        var r = ToolCallValidator.Validate(
            "write_file",
            "{\"file_path\":\"a.txt\",\"content\":\"x\",\"dry\":true}"
        );
        Assert.False(r.Success);
        Assert.Equal("UNKNOWN_ARGUMENT", r.Code);
    }

    [Fact]
    public void Write_UnrelatedField_Stripped_WithWarning()
    {
        var r = ToolCallValidator.Validate(
            "write_file",
            "{\"file_path\":\"a.txt\",\"content\":\"x\",\"explanation\":\"hi\"}"
        );
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Warnings, w => w.Contains("STRIPPED_UNKNOWN_PARAM"));
    }

    [Fact]
    public void Write_ModeField_Rejected_WithGuess()
    {
        var r = ToolCallValidator.Validate(
            "edit_file",
            "{\"file_path\":\"a.txt\",\"old_string\":\"12345678\",\"new_string\":\"y\",\"mode\":\"lines\"}"
        );
        Assert.False(r.Success);
        Assert.Equal("UNKNOWN_ARGUMENT", r.Code);
        Assert.Contains("是不是想传", r.Message);
    }

    [Fact]
    public void ExecCommand_UnknownField_StillRejected()
    {
        var r = ToolCallValidator.Validate(
            "exec_command",
            "{\"command\":\"dir\",\"explanation\":\"hi\"}"
        );
        Assert.False(r.Success);
        Assert.Equal("UNKNOWN_ARGUMENT", r.Code);
    }

    [Fact]
    public void Alias_NewText_FilePath_Moved_WithWarning()
    {
        var r = ToolCallValidator.Validate(
            "edit_lines",
            "{\"filePath\":\"a.txt\",\"offset\":1,\"limit\":1,\"newText\":\"x\"}"
        );
        Assert.True(r.Success, r.Message);
        Assert.Contains("\"file_path\"", r.NormalizedJson);
        Assert.Contains("\"new_text\"", r.NormalizedJson);
        Assert.Contains(r.Warnings, w => w.Contains("ALIASED_PARAM"));
    }

    [Fact]
    public void WireSchema_HasNoNumericOrStringConstraints()
    {
        foreach (var d in _registry.GetDefinitions())
        {
            string schema = d.ParametersJson;
            Assert.DoesNotContain("\"minimum\"", schema);
            Assert.DoesNotContain("\"maximum\"", schema);
            Assert.DoesNotContain("\"minLength\"", schema);
            Assert.DoesNotContain("\"maxLength\"", schema);
            Assert.DoesNotContain("\"default\"", schema);
        }
    }

    [Fact]
    public void WireSchema_KeepsRequiredAndEnum()
    {
        var edit = _registry.GetDefinitions().First(d => d.Name == "edit_batch");
        Assert.Contains("\"required\"", edit.ParametersJson);
        Assert.Contains("\"enum\"", edit.ParametersJson);
    }

    [Fact]
    public async Task ShortAnchor_SevenChars_Rejected()
    {
        File.WriteAllText(Abs("short.txt"), "abcdefghij\nklmnopqrst\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_file",
            Args(
                new
                {
                    file_path = "short.txt",
                    old_string = "abcdefg",
                    new_string = "X"
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("ANCHOR_TOO_SHORT", r.Error);
        Assert.Contains("8", r.Error);
    }

    [Fact]
    public async Task Anchor_EightChars_Succeeds()
    {
        File.WriteAllText(Abs("eight.txt"), "abcdefghij\nklmnopqrst\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_file",
            Args(
                new
                {
                    file_path = "eight.txt",
                    old_string = "abcdefgh",
                    new_string = "ABCDEFGH"
                }
            )
        );
        Assert.True(r.Success, r.Error);
    }

    [Fact]
    public async Task EditLines_MissingLimit_BusinessCode()
    {
        File.WriteAllText(Abs("lim.txt"), "a\nb\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_lines",
            Args(
                new
                {
                    file_path = "lim.txt",
                    offset = 1,
                    new_text = "x"
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("MISSING_LIMIT", r.Error);
    }

    [Fact]
    public void EditFile_HiddenKnob_PassesCentralCheck()
    {
        var r = ToolCallValidator.Validate(
            "edit_file",
            "{\"file_path\":\"a.txt\",\"old_string\":\"12345678\",\"new_string\":\"y\",\"ignore_leading_whitespace\":true}"
        );
        Assert.True(r.Success, r.Message);
    }

    [Fact]
    public async Task Overwrite_HashConfirms_WithoutFlag()
    {
        File.WriteAllText(Abs("hash.txt"), "hello\n", new UTF8Encoding(false));
        string hash = Sha256OfFile(Abs("hash.txt"));
        var r = await Call(
            "write_file",
            Args(
                new
                {
                    file_path = "hash.txt",
                    content = "world\n",
                    expected_hash = hash
                }
            )
        );
        Assert.True(r.Success, r.Error);
        Assert.Equal("world\n", File.ReadAllText(Abs("hash.txt")));
    }

    [Fact]
    public async Task Overwrite_WrongHash_Rejected()
    {
        File.WriteAllText(Abs("hashbad.txt"), "hello\n", new UTF8Encoding(false));
        var r = await Call(
            "write_file",
            Args(
                new
                {
                    file_path = "hashbad.txt",
                    content = "world\n",
                    expected_hash = new string('0', 64)
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("EXPECTED_HASH_MISMATCH", r.Error);
    }

    [Fact]
    public async Task Overwrite_FlagPlusWrongHash_Rejected()
    {
        File.WriteAllText(Abs("hashflag.txt"), "hello\n", new UTF8Encoding(false));
        var r = await Call(
            "write_file",
            Args(
                new
                {
                    file_path = "hashflag.txt",
                    content = "world\n",
                    allow_overwrite = true,
                    expected_hash = new string('0', 64)
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("EXPECTED_HASH_MISMATCH", r.Error);
    }

    [Fact]
    public async Task Overwrite_WithoutEither_NeedsConfirm()
    {
        File.WriteAllText(Abs("needconfirm.txt"), "hello\n", new UTF8Encoding(false));
        var r = await Call(
            "write_file",
            Args(new { file_path = "needconfirm.txt", content = "world\n" })
        );
        Assert.False(r.Success);
        Assert.StartsWith("NEED_CONFIRM_OVERWRITE", r.Error);
    }

    [Fact]
    public async Task ClearFile_WithFlag_Succeeds()
    {
        File.WriteAllText(Abs("clear.txt"), "hello\n", new UTF8Encoding(false));
        var r = await Call(
            "write_file",
            Args(
                new
                {
                    file_path = "clear.txt",
                    content = "",
                    allow_overwrite = true
                }
            )
        );
        Assert.True(r.Success, r.Error);
        Assert.Equal(string.Empty, File.ReadAllText(Abs("clear.txt")));
    }

    [Fact]
    public async Task Batch_Lines_Undo_RestoresWholeBatch()
    {
        File.WriteAllText(Abs("undobatch.txt"), "u1\nu2\nu3\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "undobatch.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 1,
                            limit = 1,
                            new_text = "U1"
                        },
                        new
                        {
                            offset = 3,
                            limit = 1,
                            new_text = "U3"
                        },
                    },
                }
            )
        );
        Assert.True(r.Success, r.Error);
        string output = r.Output ?? string.Empty;
        int bi = output.IndexOf("backup:", StringComparison.Ordinal);
        Assert.True(bi >= 0, output);
        string backupRel = output
            .Substring(bi + "backup:".Length)
            .Trim()
            .Split(
                new[] { ' ', '\r', '\n', ')', '）', '，', ',' },
                StringSplitOptions.RemoveEmptyEntries
            )
            .First();
        Assert.Equal("U1\nu2\nU3\n", File.ReadAllText(Abs("undobatch.txt")));
        var undo = await Call("undo_edit", Args(new { backup_path = backupRel }));
        Assert.True(undo.Success, undo.Error);
        Assert.Equal("u1\nu2\nu3\n", File.ReadAllText(Abs("undobatch.txt")));
    }

    [Fact]
    public async Task Batch_SafetyTypo_Rejected_FileUntouched()
    {
        File.WriteAllText(Abs("guard.txt"), "g1\ng2\n", new UTF8Encoding(false));
        string before = File.ReadAllText(Abs("guard.txt"));
        var r = await Call(
            "edit_batch",
            "{\"file_path\":\"guard.txt\",\"mode\":\"lines\",\"dryrun\":true,\"edits\":[{\"offset\":1,\"limit\":1,\"new_text\":\"X\"}]}"
        );
        Assert.False(r.Success);
        Assert.Equal("UNKNOWN_ARGUMENT", r.ErrorCode ?? ToolResult.TryExtractCode(r.Error));
        Assert.Equal(before, File.ReadAllText(Abs("guard.txt")));
    }

    [Fact]
    public async Task ClearFile_WithoutFlag_NeedsConfirm()
    {
        File.WriteAllText(Abs("clear2.txt"), "hello\n", new UTF8Encoding(false));
        var r = await Call("write_file", Args(new { file_path = "clear2.txt", content = "" }));
        Assert.False(r.Success);
        Assert.StartsWith("NEED_CONFIRM_OVERWRITE", r.Error);
    }

    [Fact]
    public async Task NewEmptyFile_Succeeds()
    {
        var r = await Call("write_file", Args(new { file_path = "brandnew.txt", content = "" }));
        Assert.True(r.Success, r.Error);
        Assert.Equal(string.Empty, File.ReadAllText(Abs("brandnew.txt")));
    }

    [Fact]
    public async Task LargeFile_HashIsFull_AndReusable()
    {
        string big = new string('a', 1_100_000);
        File.WriteAllText(Abs("big.bin.txt"), big, new UTF8Encoding(false));
        var read = await Call(
            "read_file",
            Args(
                new
                {
                    file_path = "big.bin.txt",
                    offset = 1,
                    limit = 1
                }
            )
        );
        Assert.True(read.Success, read.Error);
        string output = read.Output ?? string.Empty;
        int hi = output.IndexOf("Hash:", StringComparison.Ordinal);
        Assert.True(hi >= 0, output);
        string tail = output.Substring(hi + "Hash:".Length).Trim();
        string hash = tail.Split(
                new[] { ' ', '\r', '\n', ',', ']' },
                StringSplitOptions.RemoveEmptyEntries
            )
            .First();
        Assert.Equal(64, hash.Length);
        var edit = await Call(
            "edit_lines",
            Args(
                new
                {
                    file_path = "big.bin.txt",
                    offset = 1,
                    limit = 1,
                    new_text = "z",
                    expected_hash = hash
                }
            )
        );
        Assert.True(edit.Success, edit.Error);
    }

    [Fact]
    public void Repair_BareNewline_Succeeds_WithMark()
    {
        string raw =
            "{\"file_path\":\"a.txt\",\"offset\":1,\"limit\":1,\"new_text\":\"line1\nline2\"}";
        var r = ToolCallValidator.Validate("edit_lines", raw);
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Warnings, w => w.Contains("REPAIRED_JSON"));
        Assert.Contains("line1\\nline2", r.NormalizedJson);
    }

    [Fact]
    public void Repair_TrailingComma_Succeeds_WithMark()
    {
        var r = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\",}");
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Warnings, w => w.Contains("REPAIRED_JSON"));
    }

    [Fact]
    public void Repair_Truncation_NotRepaired()
    {
        var r = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\"");
        Assert.False(r.Success);
        Assert.Equal("INVALID_JSON_ARGUMENTS", r.Code);
    }

    [Fact]
    public void Repair_Disabled_RestoresOldBehavior()
    {
        string raw =
            "{\"file_path\":\"a.txt\",\"offset\":1,\"limit\":1,\"new_text\":\"line1\nline2\"}";
        var r = ToolCallValidator.Validate(
            "edit_lines",
            raw,
            closedWorld: true,
            enableRepair: false
        );
        Assert.False(r.Success);
        Assert.Equal("INVALID_JSON_ARGUMENTS", r.Code);
    }

    [Fact]
    public async Task Batch_Lines_TwoRanges_Atomic()
    {
        File.WriteAllText(Abs("batch.txt"), "l1\nl2\nl3\nl4\nl5\nl6\n", new UTF8Encoding(false));
        string beforeMtime = File.GetLastWriteTimeUtc(Abs("batch.txt")).Ticks.ToString();
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "batch.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 2,
                            limit = 1,
                            new_text = "L2"
                        },
                        new
                        {
                            offset = 5,
                            limit = 2,
                            new_text = "L5\nL6"
                        },
                    },
                }
            )
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("2 处", r.Output);
        Assert.Contains("backup:", r.Output);
        Assert.Equal("l1\nL2\nl3\nl4\nL5\nL6\n", File.ReadAllText(Abs("batch.txt")));
        Assert.NotEqual(beforeMtime, File.GetLastWriteTimeUtc(Abs("batch.txt")).Ticks.ToString());
    }

    [Fact]
    public async Task Batch_Lines_Overlap_WholeCallRejected_FileUntouched()
    {
        File.WriteAllText(Abs("overlap.txt"), "l1\nl2\nl3\nl4\n", new UTF8Encoding(false));
        string before = File.ReadAllText(Abs("overlap.txt"));
        DateTime mtime = File.GetLastWriteTimeUtc(Abs("overlap.txt"));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "overlap.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 2,
                            limit = 2,
                            new_text = "X"
                        },
                        new
                        {
                            offset = 3,
                            limit = 2,
                            new_text = "Y"
                        },
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("EDITS_OVERLAP", r.Error);
        Assert.Equal(before, File.ReadAllText(Abs("overlap.txt")));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(Abs("overlap.txt")));
    }

    [Fact]
    public async Task Batch_Lines_OutOfRange_WholeCallRejected()
    {
        File.WriteAllText(Abs("range.txt"), "l1\nl2\n", new UTF8Encoding(false));
        string before = File.ReadAllText(Abs("range.txt"));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "range.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 1,
                            limit = 1,
                            new_text = "OK"
                        },
                        new
                        {
                            offset = 99,
                            limit = 1,
                            new_text = "BAD"
                        },
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("OUT_OF_RANGE", r.Error);
        Assert.Equal(before, File.ReadAllText(Abs("range.txt")));
    }

    [Fact]
    public async Task Batch_Lines_DryRun_PreviewsWithoutWrite()
    {
        File.WriteAllText(Abs("dry.txt"), "l1\nl2\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "dry.txt",
                    mode = "lines",
                    dry_run = true,
                    edits = new[]
                    {
                        new
                        {
                            offset = 1,
                            limit = 1,
                            new_text = "DRY"
                        }
                    },
                }
            )
        );
        Assert.True(r.Success, r.Error);
        Assert.Contains("DRY_RUN", r.Output);
        Assert.Equal("l1\nl2\n", File.ReadAllText(Abs("dry.txt")));
    }

    [Fact]
    public async Task Batch_Lines_TooMany_Rejected()
    {
        File.WriteAllText(Abs("many.txt"), "l1\nl2\n", new UTF8Encoding(false));
        var edits = Enumerable
            .Range(1, 9)
            .Select(i => new
            {
                offset = 1,
                limit = 0,
                new_text = "x"
            })
            .ToArray();
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "many.txt",
                    mode = "lines",
                    edits
                }
            )
        );
        Assert.False(r.Success);
        Assert.Contains("TOO_MANY_EDITS", r.Error);
    }

    [Fact]
    public async Task Batch_Lines_EmptyEdits_Rejected()
    {
        File.WriteAllText(Abs("empty.txt"), "l1\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            "{\"file_path\":\"empty.txt\",\"mode\":\"lines\",\"edits\":[]}"
        );
        Assert.False(r.Success);
        Assert.StartsWith("EMPTY_EDITS", r.Error);
    }

    [Fact]
    public async Task Batch_Lines_OversizedNewText_Rejected()
    {
        File.WriteAllText(Abs("huge.txt"), "l1\n", new UTF8Encoding(false));
        string big = new string('z', 300_000);
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "huge.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 1,
                            limit = 0,
                            new_text = big
                        }
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("CONTENT_TOO_LARGE", r.Error);
        Assert.Contains("拆单", r.Error);
    }

    [Fact]
    public async Task Batch_Lines_EmptyText_NeedsAllowEmpty()
    {
        File.WriteAllText(Abs("del.txt"), "l1\nl2\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "del.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 1,
                            limit = 1,
                            new_text = ""
                        }
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.Contains("EMPTY_NEW_TEXT", r.Error);
    }

    [Fact]
    public async Task Batch_Lines_SamePointDoubleInsert_Overlap()
    {
        File.WriteAllText(Abs("ins.txt"), "l1\nl2\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "ins.txt",
                    mode = "lines",
                    edits = new[]
                    {
                        new
                        {
                            offset = 2,
                            limit = 0,
                            new_text = "A"
                        },
                        new
                        {
                            offset = 2,
                            limit = 0,
                            new_text = "B"
                        },
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("EDITS_OVERLAP", r.Error);
    }

    [Fact]
    public async Task Batch_Lines_MissingMode_DefaultsToLines()
    {
        File.WriteAllText(Abs("defmode.txt"), "l1\nl2\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "defmode.txt",
                    edits = new[]
                    {
                        new
                        {
                            offset = 1,
                            limit = 1,
                            new_text = "D"
                        }
                    },
                }
            )
        );
        Assert.True(r.Success, r.Error);
        Assert.Equal("D\nl2\n", File.ReadAllText(Abs("defmode.txt")));
    }

    [Fact]
    public async Task Batch_Text_TwoAnchors_Succeed()
    {
        File.WriteAllText(
            Abs("tbatch.txt"),
            "alpha-111 start\nmiddle line here\nbeta-222 end\n",
            new UTF8Encoding(false)
        );
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "tbatch.txt",
                    mode = "text",
                    edits = new[]
                    {
                        new { old_string = "alpha-111 start", new_string = "ALPHA" },
                        new { old_string = "beta-222 end", new_string = "BETA" },
                    },
                }
            )
        );
        Assert.True(r.Success, r.Error);
    }

    [Fact]
    public async Task Batch_Text_SecondMiss_WholeCallRejected()
    {
        File.WriteAllText(Abs("tmiss.txt"), "alpha-111 start\nmiddle\n", new UTF8Encoding(false));
        string before = File.ReadAllText(Abs("tmiss.txt"));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "tmiss.txt",
                    mode = "text",
                    edits = new[]
                    {
                        new { old_string = "alpha-111 start", new_string = "ALPHA" },
                        new { old_string = "missing-anchor-xyz-123", new_string = "B" },
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("OLD_STRING_NOT_FOUND", r.Error);
        Assert.Contains("edits[1]", r.Error);
        Assert.Equal(before, File.ReadAllText(Abs("tmiss.txt")));
    }

    [Fact]
    public async Task Batch_Text_Overlap_Rejected()
    {
        File.WriteAllText(Abs("tover.txt"), "aaa-111 bbb-222 ccc\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "tover.txt",
                    mode = "text",
                    edits = new[]
                    {
                        new { old_string = "aaa-111 bbb-222", new_string = "X" },
                        new { old_string = "bbb-222 ccc", new_string = "Y" },
                    },
                }
            )
        );
        Assert.False(r.Success);
        Assert.StartsWith("EDITS_OVERLAP", r.Error);
    }

    [Fact]
    public async Task Batch_Text_NoChainSubstitution()
    {
        File.WriteAllText(
            Abs("chain.txt"),
            "first-anchor-111\nsecond-anchor-222\n",
            new UTF8Encoding(false)
        );
        var r = await Call(
            "edit_batch",
            Args(
                new
                {
                    file_path = "chain.txt",
                    mode = "text",
                    edits = new[]
                    {
                        new { old_string = "first-anchor-111", new_string = "second-anchor-222" },
                        new { old_string = "second-anchor-222", new_string = "DONE" },
                    },
                }
            )
        );
        Assert.True(r.Success, r.Error);
        Assert.Equal("second-anchor-222\nDONE\n", File.ReadAllText(Abs("chain.txt")));
    }

    [Fact]
    public async Task Batch_Text_MissingOldString_IndexedError()
    {
        File.WriteAllText(Abs("idx.txt"), "hello-anchor-111\n", new UTF8Encoding(false));
        var r = await Call(
            "edit_batch",
            "{\"file_path\":\"idx.txt\",\"mode\":\"text\",\"edits\":[{\"new_string\":\"x\"}]}"
        );
        Assert.False(r.Success);
        Assert.StartsWith("MISSING_REQUIRED_PARAM", r.Error);
        Assert.Contains("edits[0]", r.Error);
        Assert.Contains("old_string", r.Error);
    }
}
