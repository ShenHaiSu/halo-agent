using System;
using System.IO;
using System.Threading.Tasks;
#nullable enable
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

// need17-04 验收：执行搜索分册上限同源 + 枚举 + 截断教学。
public sealed class Stage17ExecSearchTests
{
    [Fact] public void Exec_Timeout_SameSource() { var j = new ExecCommandTool().GetDefinition().ParametersJson; Assert.Contains("60000", j); Assert.Contains("300000", j); Assert.Equal(60000, ExecCommandTool.DefaultTimeoutMs); }
    [Fact] public void Exec_Runner_Enum() { Assert.Contains("\"cmd\"", new ExecCommandTool().GetDefinition().ParametersJson); Assert.Contains("\"python\"", new ExecCommandTool().GetDefinition().ParametersJson); }
    [Fact] public void Grep_Required_Pattern() { Assert.Contains("pattern", new GrepSearchTool().GetDefinition().ParametersJson); Assert.Contains("100", new GrepSearchTool().GetDefinition().ParametersJson); }
    [Fact] public void ListDir_MaxDepth_SameSource() { var j = new ListDirTool().GetDefinition().ParametersJson; Assert.Contains("\"max_depth\"", j); Assert.Contains("10", j); }
    [Fact] public void ViewImage_Required_FilePath() { Assert.Contains("file_path", new ViewImageTool().GetDefinition().ParametersJson); }
    [Fact] public void Truncation_Teaching_Present() { Assert.Contains("never re-run a broad", new ExecCommandTool().Description); Assert.Contains("never re-run a broad", new GrepSearchTool().Description); }
    [Fact] public void Exec_Schema_Closed() { Assert.Contains("additionalProperties", new ExecCommandTool().GetDefinition().ParametersJson); }
    [Fact] public void Grep_LongLine_Hint() { Assert.Contains("read_file", new GrepSearchTool().GetDefinition().ParametersJson + new GrepSearchTool().Description); }
}
