using System.Security.Cryptography;
using System.Text;

namespace YanhuLink.Droid;

/// <summary>
/// 密码加解密。
///
/// 与 Windows 版（DPAPI）的区别：
///   Windows 有系统级的 DPAPI（按用户账户加密），Android 没有等价物，
///   因此这里用「Android Keystore 派生的密钥 + AES-GCM」的方案。
///
/// 实现策略（分层，兼顾安全与可用）：
///   1. 密钥来源：Android ID + 应用包名，经 PBKDF2 派生
///      —— 换设备后无法解密（符合预期，密码不跟着走）
///   2. 加密算法：AES-256-GCM（带认证标签，防篡改）
///   3. 存储格式：base64(nonce ‖ tag ‖ ciphertext)
///
/// 安全等级说明：
///   这**不是**硬件级密钥保护（那需要 Android Keystore 的 KeyStore API，
///   需引入平台特定代码）。对于「校园网上网密码」这个敏感度，
///   结合应用私有目录的访问隔离，已经是合理的防护。
///   README 中会明确说明这一点，不夸大安全性。
/// </summary>
public static class PasswordProtector
{
    private const int NonceSize = 12;   // AES-GCM 标准 nonce 长度
    private const int TagSize = 16;     // 认证标签长度
    private const int KeySize = 32;     // AES-256

    /// <summary>
    /// 派生本机密钥。
    ///
    /// 用 Android ID 作为盐 —— 它按应用签名与用户生成，
    /// 同一设备同一应用稳定，但不同设备/重装后不同。
    /// </summary>
    private static byte[] DeriveKey()
    {
        var seed = new StringBuilder();
        try { seed.Append(global::Android.Provider.Settings.Secure.GetString(
            global::Android.App.Application.Context.ContentResolver,
            global::Android.Provider.Settings.Secure.AndroidId)); } catch { }
        seed.Append('|');
        seed.Append(global::Android.App.Application.Context.PackageName);
        seed.Append("|YanhuLink-v1");

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(seed.ToString()),
            salt: Encoding.UTF8.GetBytes("YanhuLink.Salt.v1"),
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: KeySize);
    }

    /// <summary>加密明文密码。空串返回空串。</summary>
    public static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;

        try
        {
            var key = DeriveKey();
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var plainBytes = Encoding.UTF8.GetBytes(plain);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag);

            var result = new byte[NonceSize + TagSize + cipher.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
            Buffer.BlockCopy(cipher, 0, result, NonceSize + TagSize, cipher.Length);

            return Convert.ToBase64String(result);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>解密。失败（换设备、数据损坏、密钥变更）返回空串。</summary>
    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return string.Empty;

        try
        {
            var data = Convert.FromBase64String(encrypted);
            if (data.Length < NonceSize + TagSize) return string.Empty;

            var key = DeriveKey();
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipher = new byte[data.Length - NonceSize - TagSize];

            Buffer.BlockCopy(data, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(data, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(data, NonceSize + TagSize, cipher, 0, cipher.Length);

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // 解密失败最常见的原因是「换了设备」或「重装了应用」，
            // 此时返回空串，界面会提示用户重新输入密码。
            return string.Empty;
        }
    }
}
