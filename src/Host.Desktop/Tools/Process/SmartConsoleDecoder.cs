using System.Text;

namespace Host.Desktop.Tools.Process;

/// <summary>
/// Win7 中文乱码克星：GBK (CP936) / UTF-8 智能字节流解码器。
/// 同一条命令管道中同时兼容 Win7 原生 GBK 中文输出与现代工具 UTF-8 输出，
/// 无需修改系统代码页。net6.0-windows 兼容，仅使用受支持的托管 API。
/// </summary>
public static class SmartConsoleDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private static readonly object ProviderLock = new();
    private static bool _providerRegistered;

    static SmartConsoleDecoder()
    {
        EnsureRegistered();
    }

    /// <summary>
    /// 注册 CodePagesEncodingProvider（支持 Encoding.GetEncoding(936)）。
    /// 多次调用安全，内部一次性锁保护。
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (ProviderLock)
        {
            if (_providerRegistered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _providerRegistered = true;
        }
    }

    /// <summary>
    /// 获取 GBK (CP936) 编码实例。
    /// </summary>
    public static Encoding GbkEncoding
    {
        get
        {
            EnsureRegistered();
            return Encoding.GetEncoding(936);
        }
    }

    /// <summary>
    /// 按“严格 UTF-8 优先、失败回退 GBK(936)”策略解码原始字节。
    /// </summary>
    public static string DecodeBytes(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            // 1. 优先使用 throwOnInvalidBytes: true 的严格 UTF-8 校验解码
            return StrictUtf8.GetString(rawBytes);
        }
        catch (DecoderFallbackException)
        {
            // 2. 非合法 UTF-8 字节序列（如 Win7 CMD 原生命令输出的 GBK 中文）→ 无损回退 CP936
            return GbkEncoding.GetString(rawBytes);
        }
    }

    /// <summary>
    /// byte[] 重载。
    /// </summary>
    public static string DecodeBytes(byte[]? rawBytes)
    {
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return string.Empty;
        }

        return DecodeBytes(new ReadOnlySpan<byte>(rawBytes));
    }

    /// <summary>
    /// 尝试严格 UTF-8 解码，返回是否成功（供 read_file 等三级探测复用）。
    /// </summary>
    public static bool TryDecodeStrictUtf8(byte[] rawBytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(rawBytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
