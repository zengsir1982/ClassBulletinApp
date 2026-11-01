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
/// 一个角色的能力集合（管理员可配的部分）。
///
/// <para>★ <b>只放"可以下放"的能力</b>。发布、管理用户、控制本机显示三项刻意不在其中：</para>
/// <list type="bullet">
/// <item><b>发布</b>：一次点击影响 30 台教室机，且没有回滚；</item>
/// <item><b>管理用户</b>：给了谁，谁就能把自己或别人改成管理员，整套权限当场失效；</item>
/// <item><b>控制本机显示</b>：教室机开机自动登录的是电教管理员，这项一关，
/// 那台机器连课表都显示不了，等于报废。</item>
/// </list>
///
/// <para>★ "改课表 / 作业 / 恢复停用"三项<b>只表示能力，不表示范围</b>：
/// 范围恒为"自己 <see cref="AppUser.ClassNames"/> 里的班"。所以一个没填班级的账号
/// 即使开了这三项，也什么班都改不了 —— 绝不能退化成"没有班级 = 没有限制"。</para>
/// </summary>
public class RolePolicy
{
    /// <summary>编辑本班课表（调课 / 代课 / 拖拽）。</summary>
    public bool EditSchedule { get; set; }

    /// <summary>编辑本班作业公示与缺交名单。</summary>
    public bool EditBoard { get; set; }

    /// <summary>恢复被主机策略停用的本班调课记录。</summary>
    public bool RestoreSuspended { get; set; }

    /// <summary>导入临时课表 / 教师课表（每周常规操作，影响面较小）。</summary>
    public bool ImportTemp { get; set; }

    /// <summary>导入总课表、清除调课、备份与恢复、数据包导入导出（影响面大）。</summary>
    public bool ManageData { get; set; }

    public RolePolicy Clone() => new()
    {
        EditSchedule = EditSchedule,
        EditBoard = EditBoard,
        RestoreSuspended = RestoreSuspended,
        ImportTemp = ImportTemp,
        ManageData = ManageData
    };

    /// <summary>管理员用的全权策略（实际判定会先判 <c>IsAdmin</c>，这里只作双保险）。</summary>
    public static RolePolicy All() => new()
    {
        EditSchedule = true,
        EditBoard = true,
        RestoreSuspended = true,
        ImportTemp = true,
        ManageData = true
    };

    /// <summary>
    /// 出厂默认。老数据没有 <c>Policies</c>、或表里缺某个角色时按这个回落，
    /// 保证"升级前后行为完全一致"——升级不该让任何人突然多出或少掉权限。
    /// </summary>
    public static RolePolicy DefaultFor(string? role) => (role ?? "").Trim().ToLowerInvariant() switch
    {
        // 班主任：改本班课表 + 本班作业 + 恢复本班被停用的调课；不能导数据、不能清全部
        UserRole.Teacher => new RolePolicy
        {
            EditSchedule = true,
            EditBoard = true,
            RestoreSuspended = true,
            ImportTemp = false,
            ManageData = false
        },
        // 电教管理员（学生，也是教室机开机身份）：只能控显示，其余全关
        _ => new RolePolicy()
    };

    /// <summary>人类可读的能力清单，用于变更日志与提示文案。</summary>
    public string Describe()
    {
        var on = new List<string>();
        if (EditSchedule) on.Add("改课表");
        if (EditBoard) on.Add("作业/缺交");
        if (RestoreSuspended) on.Add("恢复停用调课");
        if (ImportTemp) on.Add("导入临时课表");
        if (ManageData) on.Add("总课表/清除/备份");
        return on.Count == 0 ? "无（只能控显示）" : string.Join("、", on);
    }
}

/// <summary>
/// 用户表（users.json）。随数据包下发，管理员在任一机器上改完发布即全校生效。
/// </summary>
public class UserBook
{
    /// <summary>
    /// 1 = 首个多账号版本（无 <see cref="Policies"/>）；2 = 带角色权限配置。
    /// <para>★ 这个号决定"Policies 缺失"怎么解释，见 <see cref="PolicyFor"/>。</para>
    /// </summary>
    public int Schema { get; set; } = 1;

    public List<AppUser> Users { get; set; } = new();

    private Dictionary<string, RolePolicy> _policies = new();

    /// <summary>
    /// 各角色的能力配置，键为 <see cref="UserRole"/> 常量（<c>teacher</c> / <c>student</c>）。
    /// <para>★ 管理员<b>不进这张表</b>：管理员恒全权，允许配置等于允许把自己锁死，
    /// 而且锁死之后没有任何人能救（谁都没权限改回去了）。</para>
    /// </summary>
    public Dictionary<string, RolePolicy> Policies
    {
        get => _policies;
        // JSON 里写 "Policies": null 时不能把访问器打成 null，否则后面每次查都炸
        set => _policies = value ?? new Dictionary<string, RolePolicy>();
    }

    /// <summary>
    /// 取某角色当前生效的策略。
    /// <para>★ 回落规则分两档，这是最容易写错的地方：</para>
    /// <list type="bullet">
    /// <item><c>Schema &lt; 2</c>（老文件 / 老包）：缺 = 出厂默认。等价迁移，升级前后行为不变。</item>
    /// <item><c>Schema &gt;= 2</c>：缺 = <b>全关</b>。因为管理员显式删掉某个键时，
    /// 意思应该是"这角色什么都不给"，而不是"回落到默认又打开了"。</item>
    /// </list>
    /// </summary>
    public RolePolicy PolicyFor(string? role)
    {
        string key = (role ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return new RolePolicy();

        // 管理员恒全权，永远不查表
        if (key == UserRole.Admin) return RolePolicy.All();

        var found = FindPolicy(key);
        if (found != null) return found;

        return Schema >= 2 ? new RolePolicy() : RolePolicy.DefaultFor(key);
    }

    /// <summary>按键查找（不区分大小写，容忍手写 json 时大小写不一致）。</summary>
    public RolePolicy? FindPolicy(string? role)
    {
        string key = (role ?? "").Trim();
        if (key.Length == 0) return null;
        foreach (var kv in _policies)
            if (string.Equals((kv.Key ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>
    /// 把缺失的角色配置补齐为出厂默认（<b>只补缺，不覆盖已有</b>），并把 Schema 提到 2。
    /// 供"保存用户表 / 从包应用"时调用，让文件里能直接看到并手工编辑这几项。
    /// </summary>
    /// <returns>是否发生了改动。</returns>
    public bool EnsurePolicyDefaults()
    {
        bool changed = false;
        foreach (var role in new[] { UserRole.Teacher, UserRole.Student })
        {
            if (FindPolicy(role) == null)
            {
                _policies[role] = RolePolicy.DefaultFor(role);
                changed = true;
            }
        }
        if (Schema < 2) { Schema = 2; changed = true; }
        return changed;
    }

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
