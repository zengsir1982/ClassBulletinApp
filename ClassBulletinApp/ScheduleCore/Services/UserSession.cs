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

    /// <summary>
    /// 「本次登录会话里临时增加的任教班级」：<b>只存在内存里，绝不写回 users.json</b>。
    /// <para>★ 发布窗里老师临时给某个班发一条信息时，用 <see cref="GrantSessionClass"/> 放开班级范围；
    /// 换账号 / 退出登录即清空，下次登录不会凭空多出一个班的权限。</para>
    /// <para>★ 为什么不直接往 <see cref="AppUser.ClassNames"/> 里塞：那个对象来自用户表，
    /// 一旦被别处保存就会把「临时班级」写进随包下发的 users.json，等于永久扩权。</para>
    /// </summary>
    static readonly System.Collections.Generic.HashSet<string> _sessionClasses =
        new(System.StringComparer.Ordinal);

    /// <summary>本次会话可用的班级清单 = 账号所属班级 + 会话内临时增加的班级（去重，只读）。</summary>
    public static System.Collections.Generic.IReadOnlyList<string> SessionClassNames
    {
        get
        {
            var list = new System.Collections.Generic.List<string>();
            if (_current != null)
                foreach (var c in _current.ClassNames ?? new System.Collections.Generic.List<string>())
                {
                    string v = (c ?? "").Trim();
                    if (v.Length > 0 && !list.Contains(v, System.StringComparer.Ordinal)) list.Add(v);
                }
            foreach (var c in _sessionClasses)
                if (!list.Contains(c, System.StringComparer.Ordinal)) list.Add(c);
            return list;
        }
    }

    /// <summary>会话内临时放开一个班级（不写盘）。返回是否真的新增了。</summary>
    public static bool GrantSessionClass(string? className)
    {
        string v = (className ?? "").Trim();
        if (v.Length == 0) return false;
        return _sessionClasses.Add(v);
    }

    /// <summary>班级范围判定：账号所属班级或本次会话临时增加的班级都算。</summary>
    static bool OwnsClassInSession(string? className)
    {
        if (_current == null) return false;
        if (_current.OwnsClass(className)) return true;
        string v = (className ?? "").Trim();
        return v.Length > 0 && _sessionClasses.Contains(v);
    }

    /// <summary>当前登录用户；<c>null</c> = 游客（只能看和控显示，不能改任何数据）。</summary>
    public static AppUser? Current => _current;

    public static bool IsLoggedIn => _current != null;

    /// <summary>游客身份（未登录 / 主动以游客进入）。</summary>
    public static bool IsGuest => _current == null;

    public static string Role => _current?.Role ?? "";

    public static bool IsAdmin => string.Equals(Role, UserRole.Admin, StringComparison.Ordinal);

    public static bool IsTeacher => string.Equals(Role, UserRole.Teacher, StringComparison.Ordinal);

    public static bool IsSubjectTeacher => string.Equals(Role, UserRole.SubjectTeacher, StringComparison.Ordinal);

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
        _sessionClasses.Clear();      // 换账号 = 新会话，临时班级一律作废
        _current = user;
        _policy = (book ?? Store.LoadUsers()).PolicyFor(user.Role);
    }

    public static void SignOut()
    {
        _sessionClasses.Clear();
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
        return OwnsClassInSession(className);
    }

    /// <summary>能否导入总课表 / 单班 / 教师课表 / 清除调课（会整份覆盖全年级数据）。</summary>
    public static bool CanManageData => IsAdmin || _policy.ManageData;

    /// <summary>
    /// 能否导入 / 导出数据包、备份与恢复本机数据。
    /// <para>★ 与 <see cref="CanManageData"/> 拆开：班主任要能备份和收发数据包，
    /// 但"导入总课表 / 清除调课"会整份覆盖全校数据，必须留在管理员手里。</para>
    /// </summary>
    public static bool CanManagePackage => IsAdmin || _policy.ManagePackage;

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

    /// <summary>
    /// 能否编辑指定班级的作业公示 / 缺交名单。
    /// <para>★ 作业公示 / 缺交名单是"本机显示的那一个班"的板报，
    /// 所以范围同样受 <see cref="AppUser.ClassNames"/> 限制：科任老师只能改自己任教的班。
    /// 没填班级的账号即使开了能力，也什么都改不了。</para>
    /// </summary>
    public static bool CanEditBoardFor(string? className)
    {
        if (_current == null) return false;
        if (IsAdmin) return true;
        if (!_policy.EditBoard) return false;
        return OwnsClassInSession(className);
    }

    /// <summary>当前登录账号的任课科目（未填 / 非科任老师返回空串）。</summary>
    public static string Subject => _current?.NormalizedSubject ?? "";

    /// <summary>
    /// 能否改「指定班级 + 指定科目」的一条作业 / 缺交条目。
    /// <para>★ 教师机生成结构化变更单时逐条走这里：班级范围与科目范围必须同时满足。
    /// 按科限权只对科任老师生效；老账号没填科目 = 不限（见 <see cref="AppUser.OwnsSubject"/>）。</para>
    /// </summary>
    public static bool CanEditBoardItem(string? className, string? subject)
        => CanEditBoardFor(className) && (_current?.OwnsSubject(subject) ?? false);

    /// <summary>科目越权时的提示（供后续变更单发送界面使用）。</summary>
    public static string DenyEditBoardItem(string? subject)
        => $"当前身份（{RoleDisplay}）的任课科目是「{_current?.SubjectCaption ?? "未填"}」，"
         + $"不能修改「{(string.IsNullOrWhiteSpace(subject) ? "通用" : subject)}」科目的作业条目。"
         + "如需改别的科目，请让管理员在「用户管理」里调整该账号的任课科目。";

    /// <summary>
    /// 能否发布全校通知（随包下发到所有教室机）。
    /// <para>★ 默认只对管理员开放；班主任 / 电教管理员需显式授权。
    /// 关闭时仍可发"本班通知"（只留本机，不影响其他机器）。</para>
    /// </summary>
    public static bool CanSendSchoolNotice => IsAdmin || _policy.SendSchoolNotice;

    /// <summary>
    /// 能否修改本机自动更新设置（共享文件夹 / U 盘 / FTP 来源、是否启用自动更新）。
    /// <para>★ <b>默认所有角色都开</b>（见 <see cref="RolePolicy.ManageUpdate"/>）：
    /// 教室机开机自动登录的是电教管理员，这项一旦被关，改了共享文件夹路径就再也收不到更新。</para>
    /// </summary>
    public static bool CanManageUpdate => IsAdmin || _policy.ManageUpdate;

    /// <summary>
    /// 能否「发布信息」＝从教师机发作业 / 缺交 / 本班通知变更单给教室机。
    /// <para>★ 电教管理员（教室机确认人）<b>默认没有</b>这项权限：自己发自己批会让双人核对失去意义。
    /// 需要时由管理员在「用户管理 → 角色权限」里显式开通；管理员恒开、游客恒关。</para>
    /// <para>★ 与 <see cref="CanPublish"/> 区分：那个是「发布到数据源」，一次影响全校课表，
    /// 写死只给管理员，不进角色配置。</para>
    /// </summary>
    public static bool CanPublishInfo => IsAdmin || _policy.PublishInfo;

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
        : $"当前身份（{RoleDisplay}）没有「导入总课表 / 清除调课」的权限，请让管理员操作。";

    public static string DenyManagePackage => IsGuest
        ? "当前是游客模式，不能收发数据包。请用账号登录。"
        : $"当前身份（{RoleDisplay}）没有「导入导出数据包 / 备份恢复」的权限，请让管理员在「用户管理 → 角色权限」里开通。";

    public static string DenyImportTemp => IsGuest
        ? "当前是游客模式，不能导入数据。请用账号登录。"
        : $"当前身份（{RoleDisplay}）没有导入课表的权限，请让管理员在「用户管理 → 角色权限」里开通。";

    public static string DenyRestoreSuspended => $"当前身份（{RoleDisplay}）不能恢复调课记录。";

    /// <summary>不能编辑作业 / 缺交时的提示（区分"没这项能力"与"不是这个班"）。</summary>
    public static string DenyEditBoard(string? className)
    {
        if (IsGuest) return "当前是游客模式，不能编辑作业 / 缺交名单。请用账号登录后再改。";

        if (!_policy.EditBoard)
            return $"当前身份（{RoleDisplay}）没有编辑作业 / 缺交名单的权限，请让管理员在「用户管理 → 角色权限」里开通。";

        if (_current != null && _current.ClassNames.Count == 0)
            return $"你登录的账号（{_current.Caption}）还没有指定班级，所以改不了任何班的作业。"
                 + "请让管理员在「用户管理」里给这个账号填上班级。";

        return $"你登录的账号（{_current?.Caption ?? RoleDisplay}）只能编辑自己任教班级的作业 / 缺交名单，"
             + $"当前显示的是「{className}」。";
    }

    public static string DenyPublish => $"当前身份（{RoleDisplay}）不能发布数据或修改同步策略，请用管理员账号登录。";

    public static string DenySendSchoolNotice => IsGuest
        ? "当前是游客模式，不能发布通知。请用账号登录。"
        : $"当前身份（{RoleDisplay}）没有发布全校通知的权限，只能发本班通知；如需全校通知请让管理员在「用户管理 → 角色权限」里开通。";

    public static string DenyManageUpdate => IsGuest
        ? "当前是游客模式，不能修改更新设置。请用账号登录。"
        : $"当前身份（{RoleDisplay}）不能修改自动更新设置，请让管理员操作。";

    /// <summary>不能发布信息时的统一提示（说清楚去哪里开通，避免以为是程序坏了）。</summary>
    public static string DenyPublishInfo => IsGuest
        ? "当前是游客模式，不能发布信息。请用教师账号登录后再发。"
        : $"当前身份（{RoleDisplay}）默认没有「发布信息」的权限：电教管理员只负责在教室机确认并应用变更单，"
          + "不能自己发自己批。请改用班主任 / 科任老师账号，或让管理员在「用户管理 → 角色权限」里开通。";
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
