using System.Text;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.BuiltIn;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools;

public sealed partial class ToolRegistry
{
    /// <summary>
    /// 导出 Chat Completions (chat) 协议 tools JSON 数组字符串。
    /// </summary>
    public string GetChatToolsJson()
    {
        var defs = GetDefinitions();
        var sb = new StringBuilder("[");
        for (int i = 0; i < defs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(defs[i].ToChatToolJson());
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// 导出 Responses API (resp) 协议 tools JSON 数组字符串。
    /// </summary>
    public string GetResponsesToolsJson()
    {
        var defs = GetDefinitions();
        var sb = new StringBuilder("[");
        for (int i = 0; i < defs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(defs[i].ToResponsesToolJson());
        }

        sb.Append(']');
        return sb.ToString();
    }
}
