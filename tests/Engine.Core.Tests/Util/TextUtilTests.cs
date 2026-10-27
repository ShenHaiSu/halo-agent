using HaloAgent.Engine.Core.Util;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Util;

public class TextUtilTests
{
    [Fact]
    public void TruncateChars_does_not_split_chinese()
    {
        var result = TextUtil.TruncateChars("你好世界和平", 2, "…");

        Assert.Equal("你好…", result);
    }

    [Fact]
    public void TruncateChars_does_not_split_emoji()
    {
        var result = TextUtil.TruncateChars("a\U0001F600bc", 2, "~");

        Assert.Equal("a\U0001F600~", result);
    }

    [Fact]
    public void TruncateChars_short_string_has_no_suffix()
    {
        Assert.Equal("hi", TextUtil.TruncateChars("hi", 5, "…"));
    }

    [Fact]
    public void TruncateMiddle_keeps_head_and_tail()
    {
        var result = TextUtil.TruncateMiddle("0123456789", 4);

        Assert.StartsWith("01", result, StringComparison.Ordinal);
        Assert.EndsWith("89", result, StringComparison.Ordinal);
        Assert.Contains("省略", result, StringComparison.Ordinal);
    }
}
