#nullable enable
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 换行画像纯函数守卫：三类换行统计、混合风格判定、标签与写回变化告警。
/// 全部脱离文件系统，直接验证统计口径与告警文本。
/// </summary>
public sealed class EolProfileTests
{
    [Fact]
    public void Of_PureCrlf_CountsCrlfOnly()
    {
        var p = FileToolShared.EolProfile.Of("alpha\r\nbeta\r\n");
        Assert.Equal(new FileToolShared.EolProfile(2, 0, 0), p);
        Assert.False(p.IsMixed);
        Assert.Equal("CRLF", p.Label);
    }

    [Fact]
    public void Of_PureLf_CountsLfOnly()
    {
        var p = FileToolShared.EolProfile.Of("alpha\nbeta\n");
        Assert.Equal(new FileToolShared.EolProfile(0, 2, 0), p);
        Assert.False(p.IsMixed);
        Assert.Equal("LF", p.Label);
    }

    [Fact]
    public void Of_PureCr_CountsCrOnly()
    {
        var p = FileToolShared.EolProfile.Of("alpha\rbeta\r");
        Assert.Equal(new FileToolShared.EolProfile(0, 0, 2), p);
        Assert.False(p.IsMixed);
        Assert.Equal("CR", p.Label);
    }

    [Fact]
    public void Of_EmptyText_ReturnsZeroProfile()
    {
        var p = FileToolShared.EolProfile.Of(string.Empty);
        Assert.Equal(new FileToolShared.EolProfile(0, 0, 0), p);
        Assert.False(p.IsMixed);
        Assert.Equal("LF", p.Label);
    }

    [Fact]
    public void Of_NoNewLine_ReturnsZeroProfile()
    {
        var p = FileToolShared.EolProfile.Of("single line without break");
        Assert.Equal(new FileToolShared.EolProfile(0, 0, 0), p);
        Assert.False(p.IsMixed);
    }

    [Fact]
    public void Of_CrlfIsSingleUnit_NotDoubleCounted()
    {
        // \r\n 只记一次 CRLF，不得同时记入裸 CR 与裸 LF。
        // a\r\n + b\r\n = 2 CRLF；c\r = 1 裸 CR；d\n + e\n = 2 裸 LF。
        var p = FileToolShared.EolProfile.Of("a\r\nb\r\nc\rd\ne\n");
        Assert.Equal(new FileToolShared.EolProfile(2, 2, 1), p);
    }

    [Theory]
    [InlineData("a\r\nb\n")]
    [InlineData("a\r\nb\r")]
    [InlineData("a\nb\r")]
    [InlineData("a\r\nb\nc\rd")]
    public void Of_TwoOrMoreStyles_IsMixed(string text)
    {
        var p = FileToolShared.EolProfile.Of(text);
        Assert.True(p.IsMixed);
        Assert.StartsWith("Mixed(", p.Label);
    }

    [Fact]
    public void Label_Mixed_IncludesAllThreeCounts()
    {
        var p = FileToolShared.EolProfile.Of("a\r\nb\nc\rd");
        Assert.Equal("Mixed(CRLF 1/LF 1/CR 1)", p.Label);
    }

    [Fact]
    public void DescribeChange_StyleChanged_ReturnsWarningWithBothLabels()
    {
        string? warn = FileToolShared.EolProfile.DescribeChange("a\r\nb", "a\nb", "CRLF");
        Assert.NotNull(warn);
        Assert.Contains("WARN_EOL_CHANGED", warn!);
        Assert.Contains("CRLF -> LF", warn!);
        Assert.Contains("目标 CRLF", warn!);
    }

    [Fact]
    public void DescribeChange_SameStyle_ReturnsNull()
    {
        Assert.Null(FileToolShared.EolProfile.DescribeChange("a\r\nb", "c\r\nd", "CRLF"));
    }

    [Fact]
    public void DescribeChange_CountChanged_WarnsEvenIfLabelSame()
    {
        // 画像计数变化即告警（对齐写回对照口径），即使前后标签同为 CRLF。
        string? warn = FileToolShared.EolProfile.DescribeChange("a\r\nb", "a\r\nb\r\nc", "CRLF");
        Assert.NotNull(warn);
        Assert.Contains("CRLF -> CRLF", warn!);
    }
}
