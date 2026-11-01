using System;
using System.Collections.Generic;

namespace ScheduleCore.Models;

/// <summary>
/// 角色常量。三类写死，不做可配置权限矩阵——学校场景里配错的成本远高于灵活性收益。
/// </summary>
public static class UserRole
{
    /// <summary>管理员：可改别人的数据、可发布、可改同步策略、可管理用户。</summary>
    public const string Admin = "admin";

    /// <summary>班主任：只能改自己班（<see cref="AppUser.ClassNames"/>）的课表。</summary>
    public const string Teacher = "teacher";

    /// <summary>电教管理员（学生）：只能控制本机显示，不能碰任何数据。</summary>
    public const string Student = "student";

    public static string Display(string? role) => (role ?? "").ToLowerInvariant() switch
    {
        Admin => "管理员",
        Teacher => "班主任",
        Student => "电教管理员",
        _ => "未知"
    };

    public static bool IsValid(string? role)
        => role is Admin or Teacher or Student;
}

/// <summary>
/// 一个登录账号。
/// <para>★ 密码只存 PBKDF2 哈希 + 盐，明文不落盘、不进数据包。
/// 用户表要随包下发到 30 台教室机，明文密码等于把全校账号贴在每台机器上。</para>
/// </summary>
public class AppUser
{
    /// <summary>登录名（唯一，区分大小写不敏感由调用方统一处理）。</summary>
    public string Username { get; set; } = "";

    /// <summary>显示名（如「八(22)班 班主任 张三」），留空则显示用户名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>PBKDF2 派生出的密码哈希（Base64）。</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>随机盐（Base64，每条独立）。</summary>
    public string Salt { get; set; } = "";

    /// <summary>角色，取值见 <see cref="UserRole"/>。</summary>
    public string Role { get; set; } = UserRole.Student;

    /// <summary>
    /// 所属班级（可多个）。
    /// <para>★ 一期就用列表：班主任带两个班、年级组长管全年级都能表达，
    /// 不必等到二期再改数据结构（界面先只让填一个）。</para>
    /// </summary>
    public List<string> ClassNames { get; set; } = new();

    /// <summary>是否启用（停用后不能登录，但保留记录）。</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>界面显示名（DisplayName 为空时退回 Username）。</summary>
    /// <remarks>★ 必须 JsonIgnore：计算属性写进 users.json 毫无意义，而且这份文件会随包下发到 30 台机器。</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    /// <summary>该账号是否可改指定班级的课表（不看角色，只看班级归属）。</summary>
    public bool OwnsClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return false;
        foreach (var c in ClassNames)
            if (string.Equals((c ?? "").Trim(), className.Trim(), StringComparison.Ordinal))
                return true;
        return false;
    }
}

/// <summary>
/// 用户表（users.json）。随数据包下发，管理员在任一机器上改完发布即全校生效。
/// </summary>
public class UserBook
{
    public int Schema { get; set; } = 1;

    public List<AppUser> Users { get; set; } = new();

    /// <summary>
    /// 最后修改时间（仅供界面显示）。
    /// <para>★ 可空且<b>打包时不填</b>：包内任何 <c>DateTime.Now</c> 都会让摘要每次都变，
    /// 30 台机器就会天天重新下载（2026-10-31 踩过一次）。</para>
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>按用户名查找（不区分大小写）。</summary>
    public AppUser? Find(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        foreach (var u in Users)
            if (string.Equals(u.Username?.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
                return u;
        return null;
    }

    /// <summary>是否已存在同名的启用账号。</summary>
    public bool Contains(string? username) => Find(username) != null;

    /// <summary>管理员账号数量（用来防止"把最后一个管理员删掉"）。</summary>
    /// <remarks>计算属性，不落盘 —— 写进文件会变成"看起来能改、其实没用"的字段。</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public int AdminCount => Users.Count(u => u.Enabled && u.Role == UserRole.Admin);
}
