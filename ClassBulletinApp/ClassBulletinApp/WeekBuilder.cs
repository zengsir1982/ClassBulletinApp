using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 构建「整周课表」数据（周一..周日 × 各节次），override 优先于基础课。
/// </summary>
public static class WeekBuilder
{
    public static WeekGridData BuildWeek(string selectedClass, string semesterStart, ScheduleFile schedule,
        List<LessonOverride> overrides, DateTime monday)
    {
        var data = new WeekGridData { Monday = monday };

        int maxPeriod = 1;
        foreach (var c in schedule.Classes)
            foreach (var dm in c.Data.Values)
                foreach (var p in dm.Keys) maxPeriod = Math.Max(maxPeriod, p);
        data.MaxPeriod = maxPeriod;

        // 用索引器而不是 ToDictionary：历史数据里可能出现重复键，重复时后者覆盖前者
        var ovDict = new Dictionary<string, LessonOverride>();
        foreach (var o in overrides) ovDict[o.Key] = o;
        var selClass = schedule.Classes.FirstOrDefault(c => c.ClassName == selectedClass);

        for (int dayIdx = 0; dayIdx < 7; dayIdx++)
        {
            var thatDate = monday.AddDays(dayIdx);
            int parity = WeekHelper.GetParity(thatDate, semesterStart);
            data.DayHeaders.Add($"{DayName((DayOfWeek)((dayIdx + 1) % 7))}\n{thatDate:MM/dd}");

            for (int period = 1; period <= maxPeriod; period++)
            {
                var cell = new WeekCell
                {
                    DayIdx = dayIdx,
                    Period = period,
                    DayName = DayName((DayOfWeek)((dayIdx + 1) % 7)),
                    PeriodText = $"第{period}节"
                };

                // 时间（取基础课第一条）
                if (selClass != null && selClass.Data.TryGetValue(dayIdx, out var dayMap)
                    && dayMap.TryGetValue(period, out var lessons) && lessons.Count > 0)
                {
                    cell.TimeRange = lessons[0].TimeRange;
                    if (!string.IsNullOrEmpty(lessons[0].TimeRange))
                        cell.PeriodText = $"第{period}节\n{lessons[0].TimeRange}";
                }

                string key = $"{selectedClass}|{dayIdx}|{period}";
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
                        cell.BadgeColor = cell.Type == "代" ? "#2D9CDB" : "#E67E22";
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
