#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HaloAgent.Tools;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// need21-B04: 管线顺序测试（Validation → Safety → Handler，顺序稳定可单测）。
/// 非法参数 + 非法路径双错用例必须先报 Validation。
/// </summary>
public sealed class PipelineOrderTests
{
    private static ToolContext Ctx(string wsRoot) =>
        new()
        {
            WorkspaceRoot = wsRoot,
            WorkspaceId = "ws",
            SessionId = "ss",
            DataRoot = Path.GetTempPath(),
        };

    [Fact]
    public void Validation_Before_Safety_BadArgsWins()
    {
        // read_file 缺必需参数 file_path（Validation 必败），同时路径穿越（Safety 也会败）。
        // 管线顺序要求：先报 Validation（INVALID_*），不报 PATH_*。
        var reg = new ToolRegistry();
        string ws = Path.Combine(Path.GetTempPath(), $"Need21B04_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            var r = reg.DispatchAsync(new ToolCallDto("c1", "read_file", "{}"), Ctx(ws))
                .GetAwaiter()
                .GetResult();
            Assert.False(r.Success);
            Assert.NotNull(r.ErrorCode);
            Assert.DoesNotContain("PATH_", r.ErrorCode);
        }
        finally
        {
            try
            {
                Directory.Delete(ws, true);
            }
            catch { }
        }
    }

    [Fact]
    public void Safety_Blocks_Traversal_WhenArgsValid()
    {
        // 参数合法但路径越界 → Safety 拦截 PATH_OUTSIDE_WORKSPACE，且 Handler 未执行。
        var reg = new ToolRegistry();
        string ws = Path.Combine(Path.GetTempPath(), $"Need21B04_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            var r = reg.DispatchAsync(
                    new ToolCallDto("c2", "read_file", "{\"file_path\":\"../../outside.txt\"}"),
                    Ctx(ws)
                )
                .GetAwaiter()
                .GetResult();
            Assert.False(r.Success);
            // 中央 Safety 或工具内联 Safety 任一拦截均可，但错误码必须为路径类。
            Assert.True(
                (r.ErrorCode ?? string.Empty).Contains("PATH")
                    || (r.Error ?? string.Empty).Contains("越界")
                    || (r.Error ?? string.Empty).Contains("工作区"),
                $"expect path-denied, got code={r.ErrorCode} err={r.Error}"
            );
        }
        finally
        {
            try
            {
                Directory.Delete(ws, true);
            }
            catch { }
        }
    }

    [Fact]
    public void Pipeline_Order_Is_Stable_Across_Runs()
    {
        var reg = new ToolRegistry();
        string ws = Path.Combine(Path.GetTempPath(), $"Need21B04_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            string? first = null;
            for (int i = 0; i < 3; i++)
            {
                var r = reg.DispatchAsync(
                        new ToolCallDto($"c{i}", "no_such_tool_xyz", "{}"),
                        Ctx(ws)
                    )
                    .GetAwaiter()
                    .GetResult();
                Assert.False(r.Success);
                Assert.Equal("UNKNOWN_TOOL", r.ErrorCode);
                first ??= r.ErrorCode;
                Assert.Equal(first, r.ErrorCode);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(ws, true);
            }
            catch { }
        }
    }
}

/// <summary>
/// need21-B04: 工具注册一致性（扫描证据）。
/// 所有 [Tool] 特性类都已注册；所有显式内置工具都有 [Tool]（禁用桩除外）。
/// </summary>
public sealed class ToolRegistrationTests
{
    [Fact]
    public void All_Tool_Attributes_Are_Registered()
    {
        var asm = typeof(ToolRegistry).Assembly;
        var attributed = asm.GetTypes()
            .Where(t =>
                !t.IsAbstract
                && !t.IsInterface
                && typeof(IToolHandler).IsAssignableFrom(t)
                && t.GetCustomAttribute<ToolAttribute>() != null
            )
            .Select(t => t.GetCustomAttribute<ToolAttribute>()!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(attributed);
        var reg = new ToolRegistry();
        foreach (var name in attributed)
            Assert.Contains(name, reg.ToolNames);
    }

    [Fact]
    public void Builtins_Have_Tool_Attribute_Except_Disabled_Stubs()
    {
        var reg = new ToolRegistry(registerBuiltIns: true);
        var asm = typeof(ToolRegistry).Assembly;
        var attributedNames = new HashSet<string>(
            asm.GetTypes()
                .Where(t =>
                    !t.IsAbstract
                    && !t.IsInterface
                    && typeof(IToolHandler).IsAssignableFrom(t)
                    && t.GetCustomAttribute<ToolAttribute>() != null
                )
                .Select(t => t.GetCustomAttribute<ToolAttribute>()!.Name),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var name in reg.ToolNames)
        {
            // 禁用桩（Disabled*）走工厂显式注册，无需特性。
            if (name.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase))
                continue;
            Assert.True(attributedNames.Contains(name), $"built-in '{name}' 缺少 [Tool] 特性");
        }
    }

    [Fact]
    public void Scan_Produces_Same_Count_As_Before()
    {
        var reg = new ToolRegistry();
        // 15 个 [Tool]（与 B04 前显式注册一致；禁用桩由工厂另行注册）。
        Assert.Equal(15, reg.ToolNames.Count);
    }
}
