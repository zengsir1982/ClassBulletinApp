using System;
using System.Globalization;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

public static class WeekHelper
{
    // 1=单周, 2=双周。优先按学校实际教学周（开学第一周周一）计算，否则回退 ISO 周。

    public static int GetParity(DateTime date, string? semesterStartMonday = null)
    {
        var start = ParseStart(semesterStartMonday);
        if (start.HasValue)
        {
            int weekNumber = TeachingWeekNumber(date, start.Value);
            if (weekNumber >= 1)
                return (weekNumber % 2 == 1) ? 1 : 2;
        }
        return (ISOWeek.GetWeekOfYear(date) % 2 == 1) ? 1 : 2;
    }

    public static string ParityText(DateTime date, string? semesterStartMonday = null)
        => GetParity(date, semesterStartMonday) == 1 ? "单周" : "双周";

    /// <summary>用于标题展示：返回「第 N 周」，若早于开学第一周则返回「未开学」。</summary>
    public static string WeekLabel(DateTime date, string? semesterStartMonday = null)
    {
        int n = TeachingWeek(date, semesterStartMonday);
        return n >= 1 ? $"第{n}周" : "未开学";
    }

    /// <summary>从开学第一周起算的教学周序号（第 1 周 = 1）。</summary>
    public static int TeachingWeek(DateTime date, string? semesterStartMonday = null)
    {
        var start = ParseStart(semesterStartMonday);
        if (start.HasValue)
            return TeachingWeekNumber(date, start.Value);
        return ISOWeek.GetWeekOfYear(date);
    }

    static DateTime? ParseStart(string? semesterStartMonday)
    {
        if (string.IsNullOrWhiteSpace(semesterStartMonday)) return null;
        if (!DateTime.TryParse(semesterStartMonday, out var s)) return null;
        // 规整到所在周的周一
        return s.Date.AddDays(-((int)s.DayOfWeek + 6) % 7);
    }

    static int TeachingWeekNumber(DateTime date, DateTime startMonday)
    {
        long days = (date.Date - startMonday).Ticks / TimeSpan.TicksPerDay;
        return (int)Math.Floor(days / 7.0) + 1;
    }

    /// <summary>
    /// 当前是否处于“上课时间”：取该班当天所有有安排的课程节次时间区间，
    /// 若当前时刻落在任一节次区间内即视为上课中。周末或当日无课表则返回 false。
    /// 用于“上课时间自动隐藏”悬浮窗。
    /// </summary>
    public static bool IsClassTimeNow(ScheduleFile? schedule, string? className)
    {
        if (schedule == null || string.IsNullOrEmpty(className)) return false;
        var cs = schedule.Classes.FirstOrDefault(c => c.ClassName == className);
        if (cs == null) return false;
        int dayIdx = ((int)DateTime.Now.DayOfWeek + 6) % 7;
        if (dayIdx > 4) return false;
        if (!cs.Data.TryGetValue(dayIdx, out var dayMap)) return false;

        var now = DateTime.Now.TimeOfDay;
        foreach (var period in dayMap.Keys)
        {
            var first = dayMap[period].FirstOrDefault();
            if (first != null && IsWithin(first.TimeRange, now)) return true;
        }
        return false;
    }

    static bool IsWithin(string? timeRange, TimeSpan now)
    {
        if (string.IsNullOrWhiteSpace(timeRange)) return false;
        var parts = timeRange.Split('-');
        if (parts.Length != 2) return false;
        if (!TimeSpan.TryParse(parts[0].Trim(), out var s) || !TimeSpan.TryParse(parts[1].Trim(), out var en))
            return false;
        return now >= s && now <= en;
    }
}
