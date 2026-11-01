using System;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 当前登录身份与<b>唯一的权限判定入口</b>。
///
/// <para>★ 权限判定必须集中：全校有 10 处写调代课的地方（周视图拖拽、右键调课窗、
/// 悬浮窗拖拽、数据包导入、清除器等），散着写 <c>if (isAdmin)</c> 早晚漏一处，
/// 那漏掉的一处就是一个后门。所有 UI 入口统一问这里。</para>
///
/// <para><b>权限模型的边界</b>：这是"界面不给入口 + 减少误操作"，<b>不是安全防护</b>。
/// 离线单机程序的数据就在本机 JSON 文件里，能改文件的人照样绕过 ——
/// 真正防得住需要服务端，那就不是离线版了。</para>
/// </summary>
public static class UserSession
{
    static AppUser? _current;

    /// <summary>当前登录用户；<c>null</c> = 游客（只能看和控显示，不能改任何数据）。</summary>
    public static AppUser? Current => _current;

    public static bool IsLoggedIn => _current != null;

    /// <summary>游客身份（未登录 / 主动以游客进入）。</summary>
    public static bool IsGuest => _current == null;

    public static string Role => _current?.Role ?? "";

    public static bool IsAdmin => string.Equals(Role, UserRole.Admin, StringComparison.Ordinal);

    public static bool IsTeacher => string.Equals(Role, UserRole.Teacher, StringComparison.Ordinal);

    public static bool IsStudent => string.Equals(Role, UserRole.Student, StringComparison.Ordinal);

    /// <summary>界面显示用的身份名。</summary>
    public static string Caption => _current == null
        ? "游客"
        : $"{_current.Caption}（{UserRole.Display(_current.Role)}）";

    public static string RoleDisplay => _current == null ? "游客" : UserRole.Display(_current.Role);

    public static void SignIn(AppUser user) => _current = user;

    public static void SignOut() => _current = null;

    // ================== 权限 ==================

    /// <summary>
    /// 能否编辑指定班级的课表。
    /// 管理员可改全部；班主任只能改自己带的班；电教管理员（学生）与游客不能改。
    /// </summary>
    public static bool CanEditClass(string? className)
    {
        if (_current == null) return false;
        if (IsAdmin) return true;
        if (IsTeacher) return _current.OwnsClass(className);
        return false;
    }

    /// <summary>能否导入 / 清除 / 分发课表数据（会影响其他机器或整份数据）。</summary>
    public static bool CanManageData => IsAdmin;

    /// <summary>能否发布到数据源、修改同步策略（一次点击影响全部教室机）。</summary>
    public static bool CanPublish => IsAdmin;

    /// <summary>能否管理用户名单。</summary>
    public static bool CanManageUsers => IsAdmin;

    /// <summary>能否编辑作业公示 / 缺交名单（班主任及以上；学生只读）。</summary>
    public static bool CanEditBoard => IsAdmin || IsTeacher;

    /// <summary>能否操作本机显示（开关便签、切显示日、全屏、锁定）—— 所有人，含游客。</summary>
    public static bool CanControlDisplay => true;

    /// <summary>
    /// 统一的拒绝提示文案。集中一处，避免每个入口各写一句不一样的话。
    /// </summary>
    public static string DenyEditClass(string? className) => IsGuest
        ? "当前是游客模式，不能修改课表。请用账号登录后再改。"
        : IsTeacher
            ? $"你登录的是班主任账号（{_current?.Caption}），只能修改本班课表，不能改「{className}」。"
            : $"当前身份（{RoleDisplay}）没有修改课表的权限，请让管理员操作。";

    public static string DenyManageData => IsGuest
        ? "当前是游客模式，不能修改数据。请用管理员账号登录。"
        : $"当前身份（{RoleDisplay}）不能修改全校数据，请用管理员账号登录。";

    public static string DenyPublish => $"当前身份（{RoleDisplay}）不能发布数据或修改同步策略，请用管理员账号登录。";
}
