using System.Security.Cryptography;
using System.Text;

namespace CampusNetLogin.Services;

/// <summary>
/// 密码加解密服务。
/// 使用 Windows DPAPI（绑定当前用户）加密，兼容旧版 Python 程序的
/// "dpapi:" / "xor:" 存储格式，实现无缝迁移。
/// </summary>
public static class PasswordProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string XorPrefix = "xor:";
    private const byte XorKey = 0x5A;

    /// <summary>加密明文密码，返回可存储字符串。</summary>
    public static string Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return string.Empty;

        try
        {
            var raw = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(raw);
        }
        catch
        {
            // 退回简单 XOR 混淆（仅防随手可见）
            var bytes = Encoding.UTF8.GetBytes(plain);
            for (int i = 0; i < bytes.Length; i++) bytes[i] ^= XorKey;
            return XorPrefix + Convert.ToBase64String(bytes);
        }
    }

    /// <summary>解密存储的密码；无法解密时返回空串。</summary>
    public static string Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                var raw = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                var dec = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }

            if (stored.StartsWith(XorPrefix, StringComparison.Ordinal))
            {
                var raw = Convert.FromBase64String(stored[XorPrefix.Length..]);
                for (int i = 0; i < raw.Length; i++) raw[i] ^= XorKey;
                return Encoding.UTF8.GetString(raw);
            }
        }
        catch
        {
            return string.Empty;
        }

        // 兼容旧版明文存储
        return stored;
    }
}
