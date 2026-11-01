using System;
using System.Security.Cryptography;
using System.Text;

namespace ScheduleCore.Services;

/// <summary>
/// 密码哈希（PBKDF2-SHA256 + 每条独立随机盐）。
///
/// <para>为什么不存明文：用户表要随数据包下发到 30 台教室机，
/// 明文密码等于把全校账号写在每台机器的文件里，谁打开都能看到别人的密码。</para>
///
/// <para>★ 算法一旦上线就不要再改：改了等于全校重设密码。
/// 迭代次数写死在这里，升级次数只需在 <see cref="Verify"/> 里兼容旧次数即可。</para>
/// </summary>
public static class PasswordHasher
{
    /// <summary>PBKDF2 迭代次数。教室机 CPU 较弱，10 万次约几十毫秒，够用且不卡登录。</summary>
    const int Iterations = 100_000;

    const int SaltBytes = 16;
    const int HashBytes = 32;

    /// <summary>生成随机盐（Base64）。</summary>
    public static string NewSalt()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));

    /// <summary>按「密码 + 盐」算出哈希（Base64）。</summary>
    public static string Hash(string password, string saltBase64)
    {
        var salt = string.IsNullOrEmpty(saltBase64)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(saltBase64);

        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return Convert.ToBase64String(bytes);
    }

    /// <summary>校验密码；盐或哈希缺失时一律返回 false（宁可登不进去，也不放行空密码）。</summary>
    public static bool Verify(string? password, string? hash, string? salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
        try
        {
            var actual = Hash(password ?? "", salt);
            // 定长比较，避免按字节提前返回泄露信息
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actual),
                Encoding.UTF8.GetBytes(hash));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>给用户设置新密码（自动生成新盐）。</summary>
    public static void SetPassword(Models.AppUser user, string password)
    {
        user.Salt = NewSalt();
        user.PasswordHash = Hash(password ?? "", user.Salt);
    }
}
