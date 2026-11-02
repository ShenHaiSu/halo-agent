using System.Text;
using HaloAgent.Engine.Core.Tools.Builtin;
using HaloAgent.Engine.Core.Util;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

/// <summary>P21-F4：shell 输出字节拼接乱码回归。Decoder 累积解码 + 字符边界丢弃，任何切分下均无 U+FFFD。</summary>
public class RunShellCommandBufferTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Utf8SplitAcrossChunks(int firstLen)
    {
        var bytes = Utf8("中"); // E4 B8 AD
        var buf = new RunShellCommandTool.RollingBuffer(128_000);
        var dec = Encoding.UTF8.GetDecoder();
        buf.AppendDecoded(bytes[..firstLen], firstLen, dec);
        buf.AppendDecoded(bytes[firstLen..], bytes.Length - firstLen, dec);

        Assert.Equal("中", buf.ToText());
        Assert.DoesNotContain("�", buf.ToText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EmojiSplit(int firstLen)
    {
        var bytes = Utf8("😀"); // F0 9F 98 80
        var buf = new RunShellCommandTool.RollingBuffer(128_000);
        var dec = Encoding.UTF8.GetDecoder();
        buf.AppendDecoded(bytes[..firstLen], firstLen, dec);
        buf.AppendDecoded(bytes[firstLen..], bytes.Length - firstLen, dec);

        Assert.Equal("😀", buf.ToText());
        Assert.DoesNotContain("�", buf.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void RollingDropKeepsCharBoundary()
    {
        var buf = new RunShellCommandTool.RollingBuffer(1000);
        var raw = Utf8(new string('中', 5000) + "😀混合output");
        // 用 7 字节奇数 chunk 强制切在多字节字符中间
        var dec = Encoding.UTF8.GetDecoder();
        for (int i = 0; i < raw.Length; i += 7)
        {
            int n = Math.Min(7, raw.Length - i);
            var slice = new byte[n];
            Array.Copy(raw, i, slice, 0, n);
            buf.AppendDecoded(slice, n, dec);
        }

        var text = buf.ToText();
        Assert.DoesNotContain("�", text, StringComparison.Ordinal);
        Assert.EndsWith("😀混合output", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeathTailNoReplacement()
    {
        var buf = new RunShellCommandTool.RollingBuffer(128_000);
        buf.Append("前言" + new string('文', 3000) + "尾部中文😀混合输出");
        var tail = buf.TailText(1500);

        Assert.DoesNotContain("�", tail, StringComparison.Ordinal);
        Assert.EndsWith("尾部中文😀混合输出", tail, StringComparison.Ordinal);
        // 按 Rune 截断：取最后 1 个字符应为完整字符而非半个代理项
        var last = TextUtil.TakeLast("AB😀", 1);
        Assert.Equal("😀", last);
    }
}
