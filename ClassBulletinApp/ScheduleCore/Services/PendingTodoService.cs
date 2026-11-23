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

    public static Counts Compute(BoardSyncState? state = null, bool? classroomSide = null)
    {
        var c = new Counts();
        if (!Store.EnablePersistence) return c;

        bool classSide = classroomSide ?? BoardSyncService.IsClassroomMachine;
        List<BoardMessageRecord> recs;
        try { recs = (state ?? BoardSyncService.LoadState()).Records; }
        catch { return c; }   // 台账坏了就当没有待办；绝不让角标把主窗启动挡掉

        foreach (var r in recs)
        {
            if (r.Order.IsRevoke) continue;
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
