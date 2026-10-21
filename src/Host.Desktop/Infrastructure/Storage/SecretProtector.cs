using System.Security.Cryptography;
using System.Text;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 基于 Windows DPAPI (DataProtectionScope.CurrentUser) 的本机密钥加解密器。
/// 兼容 Windows 7 SP1 / 10 / 11，避免 API Key 与代理密码在配置文件中明文裸奔。
/// </summary>
public static class SecretProtector
{
    // 绑定应用级附加熵
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes(
        "HaloAgent.Win7Desktop.SecretSalt.v1"
    );

    /// <summary>
    /// 将明文加密为 DPAPI Base64 密文
    /// </summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = ProtectedData.Protect(
            plainBytes,
            AppEntropy,
            DataProtectionScope.CurrentUser
        );
        return Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// 将 DPAPI Base64 密文解密为明文；若解密失败（如跨机器拷贝配置），安全返回空字符串
    /// </summary>
    public static string Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherBase64);
            byte[] plainBytes = ProtectedData.Unprotect(
                cipherBytes,
                AppEntropy,
                DataProtectionScope.CurrentUser
            );
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // 若用户跨机器直接拷贝了配置文件导致当前用户解密失败，返回空串并提示重新输入 API Key
            return string.Empty;
        }
    }

    /// <summary>
    /// 判断给定字符串是否已经是当前用户可解密的有效 DPAPI Base64 密文
    /// </summary>
    public static bool IsProtectedCipherText(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(candidate);
            byte[] plainBytes = ProtectedData.Unprotect(
                cipherBytes,
                AppEntropy,
                DataProtectionScope.CurrentUser
            );
            return plainBytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// need22-P0 S5：供前端 UI 展示的脱敏掩码（全掩码，仅保留类型前缀与长度，如 sk-****(len=51)）。
    /// </summary>
    public static string MaskSecret(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        // 全掩码：不保留首尾原文，仅保留类型前缀（sk- 族）与长度
        if (plainText.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
        {
            return $"sk-****(len={plainText.Length})";
        }

        return $"****(len={plainText.Length})";
    }
}
