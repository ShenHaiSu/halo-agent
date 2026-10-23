using HaloAgent.Tools;
using Host.Desktop.Tools;
using Xunit;

namespace HaloAgent.Tools.Tests;

/// <summary>
/// need21-B04/B05: [Tool] 扫描注册证据（新增工具只加 1 文件即注册成功；删 [Tool] 即不可用）。
/// 镜像归属：Tools.Tests/ToolRegistrationTests（旧 Stage17Declaration 映射）。
/// </summary>
public sealed class ToolRegistrationTests
{
    private static ToolRegistry Scanned() => new(registerBuiltIns: true);

    [Fact]
    public void Scan_RegistersAtLeastFifteenTools()
    {
        var reg = Scanned();
        Assert.True(reg.ToolNames.Count >= 15, $"扫描注册工具数={reg.ToolNames.Count}，期望≥15。");
    }

    [Fact]
    public void Scan_RegistersCoreBuiltIns()
    {
        var reg = Scanned();
        string[] required =
        {
            "read_file",
            "list_dir",
            "grep_search",
            "write_file",
            "edit_file",
            "edit_lines",
            "exec_command",
            "view_image",
            "ask_user",
            "skill_tool",
            "memory_tool",
            "goal_tool",
            "spawn_subagent",
            "undo_edit",
            "edit",
        };
        foreach (string name in required)
            Assert.Contains(name, reg.ToolNames);
    }

    [Fact]
    public void Scan_CoversEveryAttributedHandler()
    {
        var asm = typeof(ToolRegistry).Assembly;
        var attributed = asm.GetTypes()
            .Where(t =>
                !t.IsAbstract
                && !t.IsInterface
                && typeof(IToolHandler).IsAssignableFrom(t)
                && t.GetCustomAttributes(typeof(ToolAttribute), inherit: false).Length > 0
            )
            .ToList();
        Assert.NotEmpty(attributed);
        var reg = Scanned();
        foreach (var type in attributed)
        {
            var inst = Activator.CreateInstance(type) as IToolHandler;
            Assert.NotNull(inst);
            Assert.Contains(inst!.Name, reg.ToolNames);
        }
    }

    [Fact]
    public void Registry_HasNoDuplicateNames()
    {
        var reg = Scanned();
        var dupes = reg
            .ToolNames.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Definitions_MatchRegisteredNames()
    {
        var reg = Scanned();
        var defs = reg.GetDefinitions();
        Assert.Equal(reg.ToolNames.Count, defs.Count);
        var names = new HashSet<string>(reg.ToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs)
            Assert.Contains(d.Name, names);
    }
}
