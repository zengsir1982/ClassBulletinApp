using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>清除临时调课 / 代课的作用范围。</summary>
public enum ClearScope
{
    /// <summary>只清「今天所在的那一周」（周起始日跟设置走）。</summary>
    ThisWeek,

    /// <summary>清与指定日期区间有交集的所有记录。</summary>
    Range,

    /// <summary>清全部临时记录。</summary>
    All,
}

/// <summary>
/// 一次清除的「计划」：先把要删什么、删几条、哪些被保护算清楚，再让人确认。
/// 好处是确认框里的数字与实际执行的数字必然一致（同一个 <see cref="Plan"/> 驱动）。
/// </summary>
public sealed class ClearPlan
{
    public ClearScope Scope { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    /// <summary>将被删除的条数。</summary>
    public int Targets { get; init; }

    /// <summary>属于「长期改课」（basePatch）而被保护、不参与清除的条数。</summary>
    public int Protected { get; init; }

    /// <summary>清除后还剩多少条。</summary>
    public int Remaining { get; init; }

    /// <summary>涉及的班级数（去重）。</summary>
    public int ClassCount { get; init; }

    public string RangeText => $"{From:yyyy-MM-dd} ~ {To:yyyy-MM-dd}";

    public string ScopeText => Scope switch
    {
        ClearScope.ThisWeek => "本周",
        ClearScope.Range => "指定日期范围",
        _ => "全部",
    };

    public string Describe()
    {
        if (Scope == ClearScope.All)
            return $"将清除全部 {Targets} 条临时调课 / 代课记录（涉及 {ClassCount} 个班）。";

        return $"将清除 {RangeText} 范围内生效的 {Targets} 条记录（涉及 {ClassCount} 个班）。\n"
             + "区间与这段时间有交集的记录都会删除，其他时段不受影响。";
    }
}

/// <summary>
/// 调课 / 代课记录的清除逻辑。
///
/// <para>★ 唯一的保护规则：<c>Source == "basePatch"</c> 的记录（长期改课，改的是底表本身）
/// <b>永不参与清除</b>。它们不是"临时调整"，误删等于把整个学期改回原样，
/// 且没有任何地方能找回来。要清除长期改课，将来由 O6 单独的入口负责。</para>
/// </summary>
public static class OverrideCleaner
{
    /// <summary>长期改课的来源标记（与 <c>SourcePublisher</c> / <c>SchedulePackage</c> 保持一致）。</summary>
    public const string BasePatchSource = "basePatch";

    /// <summary>该记录是否受保护、不参与任何"清除临时调课"操作。</summary>
    public static bool IsProtected(LessonOverride o)
        => o != null && string.Equals(o.Source, BasePatchSource, StringComparison.Ordinal);

    /// <summary>计算清除计划。不读写磁盘，便于预览与测试。</summary>
    public static ClearPlan Plan(ClearScope scope, DateTime? from = null, DateTime? to = null,
        List<LessonOverride>? current = null)
    {
        var all = current ?? Store.LoadOverrides();

        DateTime f, t;
        if (scope == ClearScope.ThisWeek)
        {
            // 「本周」= 今天所在周，周起始日跟设置走（周日开头时周日起算），
            // 与悬浮窗 / 周视图写入时用的区间口径完全一致。
            var st = Store.LoadSettings();
            int startDay = WeekHelper.NormalizeStartDay(st.WeekStartDay);
            var weekStart = WeekHelper.WeekStart(DateTime.Today, startDay);
            (f, t) = WeekHelper.WeekWindowOf(weekStart);
        }
        else if (scope == ClearScope.Range)
        {
            f = (from ?? DateTime.Today).Date;
            t = (to ?? DateTime.Today).Date;
            if (t < f) (f, t) = (t, f);          // 起止填反了就换过来，不报错
        }
        else
        {
            // 全量清除不需要日期，From/To 只为界面显示取今天
            f = t = DateTime.Today;
        }

        var targets = new List<LessonOverride>();
        int protectedCount = 0;

        foreach (var o in all)
        {
            if (o == null) continue;
            if (IsProtected(o)) { protectedCount++; continue; }

            // All 模式无条件命中；其余按生效区间求交
            bool hit = scope == ClearScope.All || o.OverlapsRange(f, t);
            if (hit) targets.Add(o);
        }

        return new ClearPlan
        {
            Scope = scope,
            From = f,
            To = t,
            Targets = targets.Count,
            Protected = protectedCount,
            Remaining = all.Count - targets.Count,
            ClassCount = targets.Select(x => x.ClassName).Distinct(StringComparer.Ordinal).Count(),
        };
    }

    /// <summary>按计划执行清除，返回实际删除条数。</summary>
    public static int Apply(ClearPlan plan)
    {
        var all = Store.LoadOverrides();
        int removed = plan.Scope == ClearScope.All
            ? all.RemoveAll(o => o != null && !IsProtected(o))
            : all.RemoveAll(o => o != null && !IsProtected(o) && o.OverlapsRange(plan.From, plan.To));

        if (removed > 0) Store.SaveOverrides(all);
        return removed;
    }

    /// <summary>一句话回执（状态栏用）。</summary>
    public static string Receipt(ClearPlan plan, int removed)
    {
        if (removed == 0) return $"没有符合条件的记录（{plan.ScopeText}），未做任何改动。";

        string tail = plan.Protected > 0
            ? $"　另有 {plan.Protected} 条长期改课已保留（不参与临时清除）。"
            : "";
        return plan.Scope == ClearScope.All
            ? $"已清除全部 {removed} 条临时调课记录。{tail}"
            : $"已清除 {plan.RangeText} 范围内 {removed} 条记录，其他时段不受影响。{tail}";
    }
}
