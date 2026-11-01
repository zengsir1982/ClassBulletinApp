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

    /// <summary>
    /// 当前身份生效的角色策略（登录 / 切换账号 / 数据包更新时重新绑定）。
    /// <para>★ 不缓存成静态常量的原因：管理员可能在发布机上改了权限再发一次包，
    /// 那 30 台教室机下次同步后必须立刻按新规则走，不能等重启。</para>
    /// </summary>
    static RolePolicy _policy = new();

    /// <summary>当前登录用户；<c>null</c> = 游客（只能看和控显示，不能改任何数据）。</summary>
    public static AppUser? Current => _current;

    public static bool IsLoggedIn => _current != null;

    /// <summary>游客身份（未登录 / 主动以游客进入）。</summary>
    public static bool IsGuest => _current == null;

    public static string Role => _current?.Role ?? "";

    public static bool IsAdmin => string.Equals(Role, UserRole.Admin, StringComparison.Ordinal);

    public static bool IsTeacher => string.Equals(Role, UserRole.Teacher, StringComparison.Ordinal);

    public static bool IsStudent => string.Equals(Role, UserRole.Student, StringComparison.Ordinal);

    /// <summary>当前身份的策略快照（只读用；改配置请走 <c>Store.SaveUsers</c>）。</summary>
    public static RolePolicy Policy => _policy;

    /// <summary>界面显示用的身份名。</summary>
    public static string Caption => _current == null
        ? "游客"
        : $"{_current.Caption}（{UserRole.Display(_current.Role)}）";

    public static string RoleDisplay => _current == null ? "游客" : UserRole.Display(_current.Role);

    /// <summary>当前身份能力的可读摘要，如「改课表、作业/缺交」。界面提示与日志用。</summary>
    public static string PolicySummary => IsAdmin ? "全部权限" : _policy.Describe();

    /// <summary>
    /// 登录。会顺带从用户表取一次角色策略。
    /// <para>★ 这里<b>刻意兜底读一次</b>用户表，而不是要求每个调用点都传 book：
    /// 只要有一个人忘了传，那台机器上的权限配置就静默不生效，而且查不出来。
    /// 登录不是高频操作，多读一次盘无所谓。</para>
    /// </summary>
    public static void SignIn(AppUser user, UserBook? book = null)
    {
        _current = user;
        _policy = (book ?? Store.LoadUsers()).PolicyFor(user.Role);
    }

    public static void SignOut()
    {
        _current = null;
        _policy = new RolePolicy();
    }

    /// <summary>
    /// 用户表变化后重新绑定当前身份与策略（数据包应用、保存用户表、切换账号后调用）。
    /// <para>★ 这是"权限改了但界面没变、要重启才生效"的唯一解法；
    /// 同时负责发现"我自己被停用 / 删除了"这种必须立刻踢出登录的情况。</para>
    /// </summary>
    public static RebindResult Rebind(UserBook? book = null)
    {
        if (_current == null) return RebindResult.Guest;

        var b = book ?? Store.LoadUsers();
        var fresh = b.Find(_current.Username);
        if (fresh == null)
        {
            SignOut();
            return RebindResult.Removed;
        }
        if (!fresh.Enabled)
        {
            SignOut();
            return RebindResult.Disabled;
        }

        _current = fresh;
        _policy = b.PolicyFor(fresh.Role);
        return RebindResult.Ok;
    }

    // ================== 权限 ==================

    /// <summary>
    /// 能否编辑指定班级的课表。
    /// <para>★ 能力与范围是两件事：先看角色有没有开"改课表"这项能力，
    /// 再看这个班在不在 <see cref="AppUser.ClassNames"/> 里。
    /// 没填班级的账号即使开了能力也什么都改不了 —— 绝不能退化成"没班级 = 没限制"。</para>
    /// </summary>
    public static bool CanEditClass(string? className)
    {
        if (_current == null) return false;
        if (IsAdmin) return true;
        if (!_policy.EditSchedule) return false;
        return _current.OwnsClass(className);
    }

    /// <summary>能否导入总课表 / 清除调课 / 备份恢复 / 导入导出数据包（影响整份数据）。</summary>
    public static bool CanManageData => IsAdmin || _policy.ManageData;

    /// <summary>能否导入临时课表 / 教师课表（每周常规操作，影响面比总课表小）。</summary>
    public static bool CanImportTemp => IsAdmin || _policy.ImportTemp;

    /// <summary>能否恢复被主机策略停用的本机调课。</summary>
    public static bool CanRestoreSuspended => IsAdmin || _policy.RestoreSuspended;

    /// <summary>
    /// 能否发布到数据源、修改同步策略（一次点击影响全部教室机）。
    /// <para>★ <b>刻意写死只给管理员</b>，不进角色配置：没有回滚手段，
    /// 一旦某个角色被放开，一次误操作就是全校课表被覆盖。</para>
    /// </summary>
    public static bool CanPublish => IsAdmin;

    /// <summary>
    /// 能否管理用户名单。
    /// <para>★ <b>刻意写死只给管理员</b>：能管用户 = 能把自己改成管理员，
    /// 那整套权限就成了一道装饰。</para>
    /// </summary>
    public static bool CanManageUsers => IsAdmin;

    /// <summary>能否编辑作业公示 / 缺交名单。</summary>
    public static bool CanEditBoard => IsAdmin || _policy.EditBoard;

    /// <summary>能否操作本机显示（开关便签、切显示日、全屏、锁定）—— 所有人，含游客。</summary>
    /// <remarks>
    /// ★ 恒为 <c>true</c> 且<b>不可配置</b>：教室机开机自动登录的是电教管理员，
    /// 这项一关，那台机器连课表都显示不了，等于报废。
    /// </remarks>
    public static bool CanControlDisplay => true;

    /// <summary>
    /// 统一的拒绝提示文案。集中一处，避免每个入口各写一句不一样的话。
    /// </summary>
    public static string DenyEditClass(string? className)
    {
        if (IsGuest) return "当前是游客模式，不能修改课表。请用账号登录后再改。";

        // 连"改课表"这项能力都没给：说"只能改本班"会让人以为是自己班弄错了
        if (!_policy.EditSchedule)
            return $"当前身份（{RoleDisplay}）没有修改课表的权限，请让管理员在「用户管理 → 角色权限」里开通。";

        if (IsTeacher)
        {
            // 这个分支专门对付最常见的配置错误：班主任账号忘填班级
            if (_current!.ClassNames.Count == 0)
                return $"你登录的班主任账号（{_current.Caption}）还没有指定班级，所以改不了任何班。"
                     + "请让管理员在「用户管理」里给这个账号填上班级。";
            return $"你登录的是班主任账号（{_current.Caption}），只能修改本班课表，不能改「{className}」。";
        }

        return $"当前身份（{RoleDisplay}）没有修改「{className}」的权限，请让管理员操作。";
    }

    public static string DenyManageData => IsGuest
        ? "当前是游客模式，不能修改数据。请用管理员账号登录。"
        : $"当前身份（{RoleDisplay}）没有「导入总课表 / 清除 / 备份」的权限，请让管理员操作。";

    public static string DenyImportTemp => IsGuest
        ? "当前是游客模式，不能导入数据。请用账号登录。"
        : $"当前身份（{RoleDisplay}）没有导入课表的权限，请让管理员在「用户管理 → 角色权限」里开通。";

    public static string DenyRestoreSuspended => $"当前身份（{RoleDisplay}）不能恢复调课记录。";

    public static string DenyPublish => $"当前身份（{RoleDisplay}）不能发布数据或修改同步策略，请用管理员账号登录。";
}

/// <summary>重新绑定身份的结果。</summary>
public enum RebindResult
{
    /// <summary>身份与权限已按最新用户表刷新。</summary>
    Ok,

    /// <summary>本来就是游客，无事可做。</summary>
    Guest,

    /// <summary>自己的账号已从用户表里删除，已强制登出。</summary>
    Removed,

    /// <summary>自己的账号已被停用，已强制登出。</summary>
    Disabled
}
