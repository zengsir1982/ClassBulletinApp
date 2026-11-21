using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// r135：课表作息（<see cref="BellSchedule"/>）的统一读取入口。
/// <para>设计要点：节次时间是「逐节回退」，不是「整体切换」—— Bell 只配了 4 节时，
/// 第 5-8 节各自回退到 Lesson.TimeRange；Bell 为空时全部走旧路径，升级零变化。</para>
/// <para>手动录入班保留 r129 语义：永远用自己写进课表的 TimeRange 快照，不受 Bell 影响。</para>
/// </summary>
public static class BellResolver
{
    /// <summary>
    /// 某节课该显示什么时间。优先级：
    /// ① 手动录入班 → Lesson.TimeRange（StampPeriodTimes 写死的快照）；
    /// ② 当前课表作息里这一节有值 → Bell.Periods[Index]；
    /// ③ 否则 → Lesson.TimeRange（导入时的副本，Excel 表头没写时间时为空串）。
    /// </summary>
    public static string ResolveTimeRange(ScheduleFile? schedule, ClassSchedule? cls, Lesson? lesson)
    {
        if (lesson == null) return "";
        string fallback = lesson.TimeRange ?? "";
        if (cls != null && cls.IsManual) return fallback;

        var periods = schedule?.Bell?.Periods;
        if (periods != null && periods.Count > 0)
        {
            var pt = periods.FirstOrDefault(p => p.Index == lesson.Period && !string.IsNullOrWhiteSpace(p.TimeRange));
            if (pt != null && !pt.TimeRange.Contains("HH:MM")) return pt.TimeRange;
        }
        return fallback;
    }

    /// <summary>
    /// 非手动班的公共时间（大课间 / 眼保健操 / 午休）取自哪里：
    /// ① 当前课表作息 Bell.FixedSlots（管理员显式设置，优先）；
    /// ② 二一导入的年级作息 FixedByGrade（没设 Bell 时的兜底）；
    /// ③ null —— 不插公共时间行。
    /// <para>手动录入班不走这里：App 层先处理它自己的模板/快照（见 BoardBuilder.ResolveFixedSlots）。</para>
    /// </summary>
    public static List<FixedSlot>? ResolveFixedSlots(ScheduleFile? schedule, string? className)
    {
        if (schedule == null) return null;

        var bell = schedule.Bell;
        if (bell != null && bell.FixedSlots is { Count: > 0 })
            return bell.FixedSlots;

        var grade = ErYiClient.ParseGrade(className ?? "");
        if (!string.IsNullOrEmpty(grade) && schedule.FixedByGrade != null
            && schedule.FixedByGrade.TryGetValue(grade, out var list) && list.Count > 0)
            return list;

        return null;
    }
}
