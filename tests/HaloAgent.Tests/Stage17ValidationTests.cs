using System;
using System.IO;
using System.Threading.Tasks;
#nullable enable
using Host.Desktop.Tools;
using Host.Desktop.Tools.Validation;
using Xunit;

namespace HaloAgent.Tests;

// need17-02 验收：中央校验管线（联调版：old_string 不前置 minLength、edit_lines limit 留业务层）。
public sealed class Stage17ValidationTests
{
    private static ToolContext Ctx() => new() { WorkspaceRoot = Path.GetTempPath(), WorkspaceId = "w", SessionId = "s", DataRoot = Path.GetTempPath() };

    [Fact] public void Coerce_StringInt_Accepted()
    {
        var r = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\",\"offset\":\"10\"}");
        Assert.True(r.Success, r.Message);
        Assert.Contains("\"offset\":10", r.NormalizedJson);
    }

    [Fact] public void Coerce_StringBool_Accepted()
    {
        var r = ToolCallValidator.Validate("edit_file", "{\"file_path\":\"a.txt\",\"old_string\":\"0123456789abcdef\",\"new_string\":\"y\",\"replace_all\":\"true\"}");
        Assert.True(r.Success, r.Message);
    }

    [Fact] public void Reject_FloatOffset()
    {
        var r = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\",\"offset\":10.5}");
        Assert.False(r.Success);
        Assert.Equal("TYPE_MISMATCH", r.Code);
    }

    [Fact] public void Reject_BadEnum()
    {
        var r = ToolCallValidator.Validate("exec_command", "{\"command\":\"dir\",\"runner\":\"powershell\"}");
        Assert.False(r.Success);
        Assert.Equal("ENUM_VIOLATION", r.Code);
    }

    [Fact] public void Reject_UnknownField_NamesIt()
    {
        var r = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\",\"nope\":1}");
        Assert.False(r.Success);
        Assert.Equal("UNKNOWN_ARGUMENT", r.Code);
        Assert.Contains("nope", r.Message);
    }

    [Fact] public void Reject_MissingRequired()
    {
        var r = ToolCallValidator.Validate("read_file", "{}");
        Assert.False(r.Success);
        Assert.Equal("MISSING_REQUIRED_PARAM", r.Code);
    }

    [Fact] public void Reject_BadJson()
    {
        var r = ToolCallValidator.Validate("read_file", "{bad");
        Assert.False(r.Success);
        Assert.Equal("INVALID_JSON_ARGUMENTS", r.Code);
    }

    [Fact] public void Reject_EmptyContent_MinLength()
    {
        // 联调：空串按缺失处理（MISSING_REQUIRED_PARAM），与业务层一致。
        var r = ToolCallValidator.Validate("write_file", "{\"file_path\":\"a.txt\",\"content\":\"\"}");
        Assert.False(r.Success);
        Assert.True(r.Code == "CONSTRAINT_VIOLATION" || r.Code == "MISSING_REQUIRED_PARAM", r.Code);
    }
    [Fact] public void Alias_Path_Moved_WithWarning()
    {
        var r2 = ToolCallValidator.Validate("read_file", "{\"path\":\"a.txt\"}");
        Assert.True(r2.Success, r2.Message);
        Assert.Contains("\"file_path\"", r2.NormalizedJson);
        Assert.Contains(r2.Warnings, w => w.Contains("ALIASED_PARAM"));
    }

    [Fact] public void EmptyArgs_Treated_AsEmptyObject()
    {
        var r = ToolCallValidator.Validate("list_dir", "");
        Assert.True(r.Success, r.Message);
    }

    [Fact] public async Task Validator_Blocks_Before_Execute()
    {
        var reg = new ToolRegistry();
        var r = await reg.DispatchAsync(new ToolCallDto("c1", "read_file", "{}"), Ctx());
        Assert.False(r.Success);
        Assert.StartsWith("MISSING_REQUIRED_PARAM", r.Error);
    }
    [Fact] public void ErrorCodes_Frozen_Subset()
    {
        var r1 = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\",\"nope\":1}");
        Assert.Equal("UNKNOWN_ARGUMENT", r1.Code);
        var r2 = ToolCallValidator.Validate("read_file", "{}");
        Assert.Equal("MISSING_REQUIRED_PARAM", r2.Code);
        var r3 = ToolCallValidator.Validate("read_file", "{bad");
        Assert.Equal("INVALID_JSON_ARGUMENTS", r3.Code);
        var t = ToolCallValidator.Validate("exec_command", "{\"command\":\"x\",\"runner\":\"z\"}");
        Assert.Equal("ENUM_VIOLATION", t.Code);
    }
    [Fact] public void Off_UnknownField_Downgraded_ToWarning()
    {
        var r = ToolCallValidator.Validate("read_file", "{\"file_path\":\"a.txt\",\"nope\":1}", closedWorld: false);
        Assert.True(r.Success, r.Message);
        Assert.Contains(r.Warnings, w => w.Contains("UNKNOWN_ARGUMENT"));
    }
    [Fact] public void Skill_BadAction_EnumViolation()
    {
        // need22-P0 S3：skill action 已对齐实现（list|get|create|update|delete|enable|disable），非法动作改用 explode 断言。
        var r = ToolCallValidator.Validate("skill_tool", "{\"action\":\"explode\"}");
        Assert.False(r.Success);
        Assert.Equal("ENUM_VIOLATION", r.Code);
    }
    [Fact] public void Memory_Set_MissingBody_Rejected()
    {
        var bad = ToolCallValidator.Validate("memory_tool", "{\"action\":\"set\",\"name\":\"m\"}");
        Assert.False(bad.Success);
        Assert.Equal("MISSING_REQUIRED_PARAM", bad.Code);
        var ok = ToolCallValidator.Validate("memory_tool", "{\"action\":\"set\",\"name\":\"m\",\"content\":\"x\"}");
        Assert.True(ok.Success, ok.Message);
    }
    [Fact] public async Task Dispatch_Off_UnknownField_Passes_WithWarning()
    {
        var reg = new ToolRegistry();
        var r = await reg.DispatchAsync(new ToolCallDto("c1", "list_dir", "{\"path\":\".\",\"nope\":1}"), Ctx(), null, closedWorld: false);
        Assert.True(r.Success, r.Error);
        Assert.NotNull(r.ValidatedWarnings);
    }
}
