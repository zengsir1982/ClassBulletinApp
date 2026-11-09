using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮课表底部「内置快捷」胶囊的目录：有哪几个、按角色的出厂默认、按权限的硬过滤、以及最终显示不显示。
///
/// <para>★ 三件事必须分开，别混成一条判断：</para>
/// <list type="number">
/// <item><b>能力</b>（<see cref="IsAllowed"/>）：没权限的入口无论怎么勾都不显示 —— 例如电教管理员默认没有
/// 「发布」权限，勾上也发不出去，显示出来只会让人点一次报一次错；</item>
/// <item><b>出厂默认</b>（<see cref="DefaultVisible"/>）：电教管理员是「作业 / 缺交 / 便签 / 倒数 / 值日 / 信息」，
/// 老师（班主任 / 科任）与管理员是「发布 / 便签 / 周课 / 倒数 / 设置 / 信息」；</item>
/// <item><b>用户手工开关</b>（<see cref="VisibleMap"/> / <see cref="SaveVisible"/>）：按角色分别存，
/// 某角色没存过就用出厂默认。</item>
/// </list>
///
/// <para>★ 为什么按角色分别存：一台教室机上电教管理员和班主任都会登录。
/// 老师把「作业」关掉，不该让第二天的电教管理员也找不到它。</para>
/// </summary>
public static class QuickActionCatalog
{
    public const string Homework = "hw";
    public const string Missing = "missing";
    public const string Note = "note";
    public const string Countdown = "countdown";
    public const string Duty = "duty";
    public const string Settings = "settings";
    public const string Info = "info";
    public const string Publish = "publish";
    public const string Week = "week";

    /// <summary>一个胶囊：动作键 + 文字 + 提示 + 胶囊配色（悬浮课表与「应用快捷方式」预览共用）。</summary>
    public sealed class Entry
    {
        public string Key { get; init; } = "";
        public string Text { get; init; } = "";
        public string Tip { get; init; } = "";
        public string Bg { get; init; } = "#EEF1F5";
        public string Border { get; init; } = "#B9C2CE";
    }

    /// <summary>全部内置快捷（顺序 = 设置窗口里的勾选顺序）。</summary>
    public static readonly Entry[] All =
    {
        new()
        {
            Key = Homework, Text = "📋 作业", Bg = "#E9EFF9", Border = "#C2D2E9",
            Tip = "打开「作业公示」便签，可以直接编辑各科作业内容"
        },
        new()
        {
            Key = Missing, Text = "📝 缺交", Bg = "#FFF4E5", Border = "#F0C48A",
            Tip = "打开「缺交作业名单」便签，可以直接登记谁没交"
        },
        new()
        {
            Key = Note, Text = "📌 便签", Bg = "#F2EDFD", Border = "#BFAFE8",
            Tip = "打开自定义便签（教师寄语 / 温馨提示等）；还没有便签时会新建一张"
        },
        new()
        {
            Key = Countdown, Text = "⏳ 倒数", Bg = "#FFF8E1", Border = "#E8CE8A",
            Tip = "打开倒计时便签（期末考试 / 运动会还有几天）；还没有事件时会直接弹出编辑窗口"
        },
        new()
        {
            Key = Duty, Text = "🧹 值日", Bg = "#E8F5E9", Border = "#A5CFA8",
            Tip = "打开值日生名单便签；还没有安排时会直接弹出编辑窗口"
        },
        new()
        {
            Key = Week, Text = "🗓 周课", Bg = "#E8F0FE", Border = "#A8C0E8",
            Tip = "打开控制面板的「周课程表」页（整周课表，可拖动课程卡调课）"
        },
        new()
        {
            Key = Publish, Text = "📤 发布", Bg = "#EAF7F0", Border = "#9FD3B4",
            Tip = "新建班级内容变更单：把作业 / 缺交 / 通知发到教室机（发布到的班级仍以「信息管理」页勾选为准）"
        },
        new()
        {
            Key = Info, Text = "📨 信息", Bg = "#F0F4FF", Border = "#B8C6EC",
            Tip = "打开「信息管理」的消息台账 / 回执窗口（教室里是收件箱，老师这边是发送箱）"
        },
        new()
        {
            Key = Settings, Text = "⚙ 设置", Bg = "#EEF1F5", Border = "#B9C2CE",
            Tip = "打开控制面板的「显示设置」页（字号、颜色、悬浮窗、手写球等都在这里）"
        }
    };

    /// <summary>电教管理员（student）的出厂默认。</summary>
    static readonly string[] StudentDefault = { Homework, Missing, Note, Countdown, Duty, Info };

    /// <summary>老师（班主任 / 科任）与管理员共用的出厂默认。</summary>
    static readonly string[] TeacherDefault = { Publish, Note, Week, Countdown, Settings, Info };

    public static Entry? Find(string key) => All.FirstOrDefault(e => e.Key == key);

    /// <summary>当前登录角色（admin / teacher / subject / student；游客为空串）。</summary>
    public static string RoleKey() => (UserSession.Role ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 该角色没手工改过时的出厂默认。
    /// <para>管理员与老师同一套（管理员平时也在办公室发信息、看周课）；游客 / 未知角色同样按老师这套，
    /// 剩下的由 <see cref="IsAllowed"/> 按权限裁掉，不会冒出改不了东西的入口。</para>
    /// </summary>
    public static string[] DefaultVisible(string? role)
        => string.Equals((role ?? "").Trim(), UserRole.Student, StringComparison.OrdinalIgnoreCase)
            ? StudentDefault : TeacherDefault;

    /// <summary>
    /// 能力硬过滤：没权限的入口一律不显示（不是置灰）。
    /// <para>「设置 / 周课 / 信息」不按编辑权限收：它们只是入口，控制面板本来就对所有角色开放。</para>
    /// </summary>
    public static bool IsAllowed(string key) => key switch
    {
        Homework or Missing or Note or Countdown or Duty => UserSession.CanEditBoard,
        Publish => UserSession.CanPublishInfo,
        _ => true
    };

    /// <summary>勾选框被禁用时给出的原因（没有权限的那几项）。</summary>
    public static string DenyReason(string key) => key switch
    {
        Publish => UserSession.DenyPublishInfo,
        Homework or Missing or Note or Countdown or Duty
            => "当前账号（" + UserSession.Caption + "）没有编辑作业 / 便签的权限，需要管理员在「用户管理 → 角色权限」里打开。",
        _ => ""
    };

    /// <summary>当前角色每个胶囊「该不该显示」（已合并出厂默认与手工开关，但<b>未</b>做权限过滤）。</summary>
    public static Dictionary<string, bool> VisibleMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        var def = DefaultVisible(RoleKey());

        Dictionary<string, bool>? saved = null;
        try
        {
            var s = Store.LoadSettings();
            s.QaVisibleByRole?.TryGetValue(RoleKey(), out saved);
        }
        catch { }

        foreach (var e in All)
        {
            map[e.Key] = saved != null && saved.TryGetValue(e.Key, out bool v)
                ? v
                : def.Contains(e.Key, StringComparer.Ordinal);
        }
        return map;
    }

    /// <summary>把当前角色的勾选结果落盘（九个键全写，今后新增的入口不会再被老配置误关）。</summary>
    public static void SaveVisible(IDictionary<string, bool> map)
    {
        var s = Store.LoadSettings();
        WriteInto(s, map);
        Store.SaveSettings(s);
    }

    /// <summary>把勾选结果写进 settings 对象（调用方统一 SaveSettings，避免一次确定写两遍盘）。</summary>
    public static void WriteInto(AppSettings s, IDictionary<string, bool> map)
    {
        s.QaVisibleByRole ??= new Dictionary<string, Dictionary<string, bool>>();
        var target = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var e in All) target[e.Key] = map.TryGetValue(e.Key, out bool v) && v;
        s.QaVisibleByRole[RoleKey()] = target;
    }

    /// <summary>
    /// 胶囊在底部那一排里的顺序：先按该角色的出厂默认顺序排（电教管理员 = 作业…信息；
    /// 老师 = 发布 / 便签 / 周课…），再补上用户额外打开的那些 —— 与用户口头描述的顺序一致。
    /// </summary>
    public static string[] OrderFor(string? role)
    {
        var def = DefaultVisible(role);
        return def.Concat(All.Select(e => e.Key).Where(k => !def.Contains(k, StringComparer.Ordinal)))
                  .ToArray();
    }

    /// <summary>当前角色最终该显示的动作键（权限过滤 + 手工开关都算上）。</summary>
    public static HashSet<string> VisibleNow()
    {
        var map = VisibleMap();
        return new HashSet<string>(
            All.Select(e => e.Key).Where(k => map.TryGetValue(k, out bool v) && v && IsAllowed(k)),
            StringComparer.Ordinal);
    }
}
