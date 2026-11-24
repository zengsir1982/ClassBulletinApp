using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 「还有几件事等你办」的统一口径：托盘 / 悬浮课表角标、落地页分组、今日页都读这一份，避免各处各算一套数字对不上。
/// <para>★ 数的是<b>待办</b>不是「未读」：看过了但没裁决，事还在 → 仍要算进来。已阅只用一个灰点表示。</para>
/// <para>★ 游客模式（不写盘）无从判断真实状态，一律返回 0，角标隐藏，别显示假数字。</para>
/// </summary>
public static class PendingTodoService
{
    public sealed class Counts
    {
        /// <summary>角标主数字：真正要人动手的条数。</summary>
        public int Todo;

        /// <summary>待裁决（教室机收件）：收件、未终结、还没做裁决。</summary>
        public int NeedDecision;

        /// <summary>待核对（教师机发件）：发件、未终结、还没核对完。</summary>
        public int NeedVerify;

        /// <summary>冲突：IsTerminal 把「冲突」算终结，但它恰恰要人处理，单独捞回来。</summary>
        public int Conflict;

        /// <summary>看过但没办（Seen 且未终结）：只点亮灰点，不进主数字。</summary>
        public int SeenButPending;

        public bool Any => Todo > 0;
    }

    /// <summary>
    /// 一条记录算不算待办。方向按当前身份决定：电教管理员数收件，教师 / 发布端数发件。
    /// <para>撤回单不算；已核对 / 已忽略 / 已过期 / 已撤回不算；「冲突」无论终结与否都捞回来。</para>
    /// </summary>
    static bool IsTodo(BoardMessageRecord r, bool classroomSide)
    {
        if (r.Order.IsRevoke) return false;
        if (r.Status == BoardSyncConsts.StConflict) return true;
        if (r.IsTerminal) return false;
        return classroomSide
            ? r.Direction == BoardSyncConsts.DirIn && string.IsNullOrWhiteSpace(r.Decision)
            : r.Direction == BoardSyncConsts.DirOut;
    }

    /// <summary>
    /// 这条变更单算不算「我发的」。
    /// <para>★ <b>今日卡、托盘角标、台账筛选都走这一个判定</b>：以前各处各写一遍，
    /// 迟早出现「今日卡说 2 条、台账列表 5 条」，老师会以为丢了单。</para>
    /// <para>★ <b>Sender 为空的老记录一律算</b>：升级到 r157 之前的单没记发件人，
    /// 按发件人筛掉等于把老单藏起来，宁可多条也不漏单。</para>
    /// </summary>
    public static bool IsMine(BoardChangeOrder order, string? me = null)
    {
        string sender = (order.Sender ?? "").Trim();
        if (sender.Length == 0) return true;
        string who = (me ?? UserSession.Current?.Username ?? "").Trim();
        if (who.Length == 0) return true;    // 没登录（游客 / 教室机自动登录未识别）时不过滤
        return sender.Equals(who, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 默认要不要「只算我发的」。
    /// <para>教师机 + 非管理员 + 已登录 → 是：办公室机器上多人共用，A 老师不该看到 B 老师的单。
    /// 教室机恒否（那台机器要确认的是本班<b>所有</b>到件，不看是谁发的）。</para>
    /// </summary>
    public static bool MineOnlyDefault
        => !BoardSyncService.IsClassroomMachine
           && UserSession.Current != null
           && !UserSession.IsAdmin
           && !string.IsNullOrWhiteSpace(UserSession.Current.Username);

    public static Counts Compute(BoardSyncState? state = null, bool? classroomSide = null, bool? mineOnly = null)
    {
        var c = new Counts();
        if (!Store.EnablePersistence) return c;

        bool classSide = classroomSide ?? BoardSyncService.IsClassroomMachine;
        // ★ 只对「教师机发件」这一侧生效：教室机的收件是全班共用的，按发件人筛会漏单。
        bool mine = (mineOnly ?? MineOnlyDefault) && !classSide;
        string me = mine ? (UserSession.Current?.Username ?? "").Trim() : "";
        mine = mine && me.Length > 0;

        List<BoardMessageRecord> recs;
        try { recs = (state ?? BoardSyncService.LoadState()).Records; }
        catch { return c; }   // 台账坏了就当没有待办；绝不让角标把主窗启动挡掉

        foreach (var r in recs)
        {
            if (r.Order.IsRevoke) continue;
            // 别人的单不算我的待办；★ Sender 为空的老记录仍算 —— 升级前的单没人想漏掉。
            if (mine && !IsMine(r.Order, me)) continue;
            bool conflict = r.Status == BoardSyncConsts.StConflict;
            bool live = conflict || !r.IsTerminal;

            if (live && r.Seen && !conflict) c.SeenButPending++;   // 看过但没办；只做诊断展示，不参与主数字

            if (!IsTodo(r, classSide)) continue;
            c.Todo++;
            if (conflict) c.Conflict++;
            else if (classSide && r.Direction == BoardSyncConsts.DirIn) c.NeedDecision++;
            else if (!classSide && r.Direction == BoardSyncConsts.DirOut) c.NeedVerify++;
        }
        return c;
    }

    /// <summary>角标显示文字：超过 99 显示 99+，避免图标被三位数撑爆。</summary>
    public static string BadgeText(int n) => n <= 0 ? "" : n > 99 ? "99+" : n.ToString();
}
