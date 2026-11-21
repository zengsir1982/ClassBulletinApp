using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 构建「整周课表」数据（一周 7 列 × 各节次），override 优先于基础课。
///
/// <para><b>关键：列位置与 <c>DayIdx</c> 不是一回事。</b>
/// <c>DayIdx</c> 在数据层永远是 0=周一 … 6=周日（课表 Excel 就是这么导入的）；
/// 而"这一周从哪天开始"由设置决定（默认周日）。因此：
/// 第 <c>col</c> 列的日期 = <c>weekStart.AddDays(col)</c>，
/// 该列的 <c>DayIdx</c> = <c>((int)日期.DayOfWeek + 6) % 7</c>。
/// 两者混为一谈的话，把周起始日改成周日就会整列错位一天。</para>
/// </summary>
public static class WeekBuilder
{
    public static WeekGridData BuildWeek(string selectedClass, string semesterStart, ScheduleFile schedule,
        List<LessonOverride> overrides, DateTime weekStart)
    {
        var data = new WeekGridData { Monday = weekStart };

        int maxPeriod = 1;
        foreach (var c in schedule.Classes)
            foreach (var dm in c.Data.Values)
                foreach (var p in dm.Keys) maxPeriod = Math.Max(maxPeriod, p);
        data.MaxPeriod = maxPeriod;
        // 公共时间：r135 课表作息 Bell 优先 → 二一 FixedByGrade 兜底（不再回退本机默认模板）
        data.FixedSlots = BoardBuilder.ResolveFixedSlots(schedule, selectedClass) ?? new List<FixedSlot>();

        var selClass = schedule.Classes.FirstOrDefault(c => c.ClassName == selectedClass);

        for (int col = 0; col < 7; col++)
        {
            var thatDate = weekStart.AddDays(col);
            // r133 校历：这一列可能"按别的星期上课"（周六补周三的课）或干脆放假。
            // ★ 补课列的 DayIdx 与被映射的那列相同，所以外面取格子必须按「列号」而不是 DayIdx。
            var plan = SchoolCalendar.Resolve(schedule, thatDate, semesterStart);
            int dayIdx = plan.DayIdx;      // 0=周一 .. 6=周日
            data.ColumnDayIdx.Add(dayIdx);

            int parity = plan.Parity;
            // ★ 每一列各取「那一天」生效的覆盖：一条只在本周三生效的调课，不该出现在周四那列
            var ovDict = plan.UsesOverrides
                ? OverrideExpander.MapForDate(overrides, selectedClass, plan.OverrideDate)
                : new Dictionary<string, LessonOverride>();
            data.DayHeaders.Add(HeaderText(thatDate, plan));

            for (int period = 1; period <= maxPeriod; period++)
            {
                var cell = new WeekCell
                {
                    Col = col,
                    DayIdx = dayIdx,
                    Period = period,
                    DayName = DayName(thatDate.DayOfWeek),
                    PeriodText = $"第{period}节",
                    IsMakeupColumn = plan.IsMakeup,
                    IsOffColumn = plan.IsOff
                };

                // 放假日：整列留空（连节次时间都不显示，避免看着像有课）
                if (plan.IsOff)
                {
                    data.Cells.Add(cell);
                    continue;
                }

                // 时间（取基础课第一条）
                if (selClass != null && selClass.Data.TryGetValue(dayIdx, out var dayMap)
                    && dayMap.TryGetValue(period, out var lessons) && lessons.Count > 0)
                {
                    // r135：与悬浮课表同一套逐节解析，Excel 导入班也能吃到全校作息
                    string timeRange = BellResolver.ResolveTimeRange(schedule, selClass, lessons[0]);
                    cell.TimeRange = timeRange;
                    if (!string.IsNullOrEmpty(timeRange))
                        cell.PeriodText = $"第{period}节\n{timeRange}";
                }

                string key = OverrideExpander.CellKey(selectedClass, dayIdx, period);
                if (ovDict.TryGetValue(key, out var ov))
                {
                    if (ov.IsEmpty)
                    {
                        cell.IsEmpty = true;
                        cell.IsOverridden = true;
                        cell.Type = "调";
                        cell.BadgeText = "已调空";
                        cell.BadgeColor = "#9E9E9E";
                    }
                    else
                    {
                        cell.Subject = ov.Subject;
                        cell.Teacher = ov.Teacher;
                        cell.Location = ov.Location;
                        cell.LocationText = string.IsNullOrEmpty(ov.Location) ? "" : " · " + ov.Location;
                        cell.IsOverridden = true;
                        cell.Type = string.IsNullOrEmpty(ov.Type) ? "调" : ov.Type;
                        cell.BadgeText = cell.Type == "代" ? "代课" : "已调课";
                        cell.BadgeColor = OverrideBadge.Color(cell.Type, ov.Source);
                    }
                    data.Cells.Add(cell);
                    continue;
                }

                // 基础课（按单双周取当周生效的那节）
                if (selClass != null && selClass.Data.TryGetValue(dayIdx, out var dm2)
                    && dm2.TryGetValue(period, out var ls) && ls.Count > 0)
                {
                    var active = ls.FirstOrDefault(l => l.Week == 0 || l.Week == parity) ?? ls[0];
                    cell.Subject = active.Subject;
                    cell.Teacher = active.Teacher;
                    cell.Location = active.Location;
                    cell.LocationText = string.IsNullOrEmpty(active.Location) ? "" : " · " + active.Location;
                    cell.WeekTag = active.Week == 1 ? "单周" : active.Week == 2 ? "双周" : "";
                }
                data.Cells.Add(cell);
            }
        }

        for (int period = 1; period <= maxPeriod; period++)
            data.PeriodHeaders.Add($"第{period}节");

        return data;
    }

    /// <summary>表头：常规「周X／MM/dd」；补课列加一行「按周三」，放假日加一行「放假」。</summary>
    private static string HeaderText(DateTime date, DayPlan plan)
    {
        string head = $"{DayName(date.DayOfWeek)}\n{date:MM/dd}";
        if (plan.IsOff) return head + "\n放假";
        if (plan.IsMakeup) return head + "\n按" + DayName(plan.DayIdx) + "上课";
        return head;
    }

    private static string DayName(int dayIdx) => dayIdx switch
    {
        0 => "周一",
        1 => "周二",
        2 => "周三",
        3 => "周四",
        4 => "周五",
        5 => "周六",
        _ => "周日"
    };

    private static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}
