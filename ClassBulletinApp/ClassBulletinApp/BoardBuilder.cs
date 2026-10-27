using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 主窗与浮动课表窗共用的“生成课表”逻辑，避免重复实现。
/// </summary>
public static class BoardBuilder
{
    public static TodayResult BuildToday(string? selectedClass, string semesterStart, ScheduleFile? schedule)
        => Build(selectedClass, semesterStart, schedule, DateTime.Now);

    /// <summary>解析「显示日」：Fixed 模式返回固定日期（仅日期部分），否则返回今天。</summary>
    public static DateTime ResolveDisplayDate(AppSettings s)
    {
        if (s.DisplayDateMode == "Fixed" && DateTime.TryParse(s.FixedDisplayDate, out var d))
            return d.Date;
        return DateTime.Now.Date;
    }

    /// <summary>
    /// 按指定的显示日期构建课表。
    /// 支持调休：选某工作日即显示该天课；周末/无基础课数据的日子会显示该日已有的调课 override，否则提示休息。
    /// override 命中且 IsEmpty 时，该节课显示为空白（被调空）。
    /// </summary>
    /// <param name="overrides">
    /// 调课/代课记录。不传时读取 Store；
    /// 显示教师个人课表时由调用方传入「换算到该教师名下」的覆盖集合，以体现临时课表的最新安排。
    /// </param>
    public static TodayResult Build(string? selectedClass, string semesterStart, ScheduleFile? schedule,
        DateTime displayDate, List<LessonOverride>? overrides = null)
    {
        var res = new TodayResult
        {
            DisplayDate = displayDate,
            DateText = $"{displayDate:yyyy年M月d日}",
            WeekLine = $"{WeekHelper.WeekLabel(displayDate, semesterStart)}　{DayName(displayDate.DayOfWeek)}",
            ClassName = selectedClass ?? "",
            IsWeekend = ((int)displayDate.DayOfWeek + 6) % 7 > 4
        };
        if (schedule == null || string.IsNullOrEmpty(selectedClass)) return res;
        var cs = schedule.Classes.FirstOrDefault(c => c.ClassName == selectedClass);
        if (cs == null) return res;

        int dayIdx = ((int)displayDate.DayOfWeek + 6) % 7;
        // 用索引器而不是 ToDictionary：历史数据里可能出现重复键，重复时后者覆盖前者
        var ovDict = new Dictionary<string, LessonOverride>();
        foreach (var o in overrides ?? Store.LoadOverrides()) ovDict[o.Key] = o;
        int parity = WeekHelper.GetParity(displayDate, semesterStart);

        if (!cs.Data.TryGetValue(dayIdx, out var dayMap))
        {
            // 该日无基础课（周末/调休无数据）：若有 override 则逐节显示，否则提示休息
            bool anyOv = false;
            foreach (var ov in ovDict.Values
                         .Where(o => o.ClassName == selectedClass && o.DayIdx == dayIdx && !o.IsEmpty)
                         .OrderBy(o => o.Period))
            {
                anyOv = true;
                string type = string.IsNullOrEmpty(ov.Type) ? "调" : ov.Type;
                bool isSub = type == "代";
                res.Rows.Add(new PeriodRow
                {
                    DayIdx = dayIdx,
                    Period = ov.Period,
                    PeriodText = $"第{ov.Period}节",
                    CardBg = "#FFFFFF",
                    Lessons = { new LessonView
                    {
                        DayIdx = dayIdx, Period = ov.Period,
                        Subject = ov.Subject, Teacher = ov.Teacher,
                        LocationText = string.IsNullOrEmpty(ov.Location) ? "" : " · " + ov.Location,
                        WeekTag = "", SubjectColor = "#222222",
                        IsOverridden = true, OverrideType = type,
                        OverrideBadgeText = isSub ? "代课" : "已调课",
                        OverrideBadgeColor = isSub ? "#2D9CDB" : "#E67E22",
                        OverrideNote = ov.Note
                    } }
                });
            }
            if (!anyOv)
                res.Rows.Add(new PeriodRow
                {
                    DayIdx = dayIdx,
                    PeriodText = "无课",
                    CardBg = "#FFF7E6",
                    Lessons = { new LessonView { Subject = "今日休息，无课程安排", SubjectColor = "#9A7B3F" } }
                });
            return res;
        }

        // 节次集合 = 原课表已有的节次 ∪ 临时课表新增给该对象的节次
        // （教师个人课表尤其需要：别的班临时安排给他的课，原课表里本没有这一节）
        var periods = dayMap.Keys.ToList();
        foreach (var ov in ovDict.Values
                     .Where(o => o.ClassName == selectedClass && o.DayIdx == dayIdx && !o.IsEmpty))
        {
            if (!periods.Contains(ov.Period)) periods.Add(ov.Period);
        }

        LessonView MakeOverrideView(LessonOverride ov, int period)
        {
            string t = string.IsNullOrEmpty(ov.Type) ? "调" : ov.Type;
            bool sub = t == "代";
            return new LessonView
            {
                DayIdx = dayIdx,
                Period = period,
                Subject = ov.Subject,
                Teacher = ov.Teacher,
                LocationText = string.IsNullOrEmpty(ov.Location) ? "" : " · " + ov.Location,
                WeekTag = "",
                SubjectColor = "#222222",
                IsOverridden = true,
                OverrideType = t,
                OverrideBadgeText = sub ? "代课" : "已调课",
                OverrideBadgeColor = sub ? "#2D9CDB" : "#E67E22",
                OverrideNote = ov.Note
            };
        }

        foreach (var period in periods.OrderBy(k => k))
        {
            dayMap.TryGetValue(period, out var lessons);
            string key = $"{selectedClass}|{dayIdx}|{period}";

            // 原课表没有这一节：若临时课表新增了课，整行都由覆盖记录构成
            if (lessons == null || lessons.Count == 0)
            {
                if (!ovDict.TryGetValue(key, out var nov) || nov.IsEmpty) continue;
                res.Rows.Add(new PeriodRow
                {
                    DayIdx = dayIdx,
                    Period = period,
                    PeriodText = $"第{period}节",
                    Lessons = { MakeOverrideView(nov, period) }
                });
                continue;
            }

            var first = lessons.First();
            var row = new PeriodRow
            {
                DayIdx = dayIdx,
                Period = period,
                PeriodText = $"第{period}节",
                TimeRange = first.TimeRange
            };
            if (!string.IsNullOrEmpty(first.TimeRange) && IsCurrent(first.TimeRange, displayDate))
                row.CardBg = "#FFF3CD";

            if (ovDict.TryGetValue(key, out var ov))
            {
                if (ov.IsEmpty)
                {
                    // 被调为空课：仍显示节次行，但内容空白
                    res.Rows.Add(row);
                    continue;
                }
                row.Lessons.Add(MakeOverrideView(ov, period));
            }
            else
            {
                foreach (var l in lessons)
                {
                    bool active = l.Week == 0 || l.Week == parity;
                    row.Lessons.Add(new LessonView
                    {
                        DayIdx = dayIdx,
                        Period = period,
                        Subject = l.Subject,
                        Teacher = l.Teacher,
                        LocationText = string.IsNullOrEmpty(l.Location) ? "" : " · " + l.Location,
                        WeekTag = l.Week == 0 ? "" : $"（第{WeekHelper.TeachingWeek(displayDate, semesterStart)}周）",
                        SubjectColor = active ? "#222222" : "#BBBBBB"
                    });
                }
            }
            res.Rows.Add(row);
        }
        return res;
    }

    private static bool IsCurrent(string timeRange, DateTime now)
    {
        var parts = timeRange.Split('-');
        if (parts.Length != 2) return false;
        if (!TimeSpan.TryParse(parts[0], out var s) || !TimeSpan.TryParse(parts[1], out var en)) return false;
        var t = now.TimeOfDay;
        return t >= s && t <= en;
    }

    private static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日"
    };
}

public class TodayResult
{
    public List<PeriodRow> Rows { get; } = new();
    public string DateHeader { get; set; } = "";     // 兼容旧引用
    public string DateText { get; set; } = "";        // 日期，如 2026年10月26日
    public string WeekLine { get; set; } = "";        // 单双周 + 星期，如 双周 星期一
    public string ClassName { get; set; } = "";
    public bool IsWeekend { get; set; }
    public DateTime DisplayDate { get; set; } = DateTime.Now;  // 实际显示依据的那一天
}
