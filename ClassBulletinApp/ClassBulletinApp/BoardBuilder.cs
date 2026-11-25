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
    /// r162：把「已经过去」的固定显示日清回今天，并落盘。悬浮课表 / 壁纸 / 提醒 / 变化检测
    /// 都从同一份设置取显示日，所以在这里治一次，四处一起好。
    /// <para>启动调一次；软件不关跨天时由悬浮课表的 30 秒定时器再调一次。</para>
    /// <para>★ 只清过去的日期：用户为调休提前选的未来日期必须留着，否则预告就没了。</para>
    /// <returns>是否真的重置了（用于调用方决定是否抛 <see cref="AppEvents.ScheduleChanged"/>）。</returns>
    /// </summary>
    public static bool ResetExpiredDisplayDate()
    {
        var s = Store.LoadSettings();
        if (!string.Equals(s.DisplayDateMode, "Fixed", StringComparison.OrdinalIgnoreCase)
            || !DateTime.TryParse(s.FixedDisplayDate, out var d)
            || d.Date >= DateTime.Today) return false;

        s.DisplayDateMode = "Today";
        s.FixedDisplayDate = "";
        if (Store.EnablePersistence) Store.SaveSettings(s);
        return true;
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
    /// <param name="showAlternateWeek">
    /// 是否把单双周里「不是本周」的那节课也列出来（灰色弱化）。
    /// <para><b>默认 true = 保持旧行为</b>，因为上课提醒 / 课表变化检测需要看到完整数据；
    /// 只有「显示给人看」的两处（悬浮课表、桌面背景）才按设置传 false 只留本周的课。</para>
    /// </param>
    public static TodayResult Build(string? selectedClass, string semesterStart, ScheduleFile? schedule,
        DateTime displayDate, List<LessonOverride>? overrides = null, bool showAlternateWeek = true)
    {
        // r133 校历：先问「今天按哪天的课表上课」，再由 plan 决定 dayIdx / 单双周 / 是否补课放假。
        // ★ 顺序很重要：plan 必须在下面的提前 return 之前算出来，否则没选班时大屏就看不到调休标签。
        var plan = SchoolCalendar.Resolve(schedule, displayDate, semesterStart);
        // r137：调休日不展示「今天第几周 星期六」——课表关注的是什么课在上，
        // 直接显示「按第12周周三上课」。真实日期仍在 DateText 那一行，不会丢。
        string weekLine = plan.IsMakeup && plan.AsDate.HasValue
            ? plan.TagText
            : $"{WeekHelper.WeekLabel(displayDate, semesterStart)}　{DayName(displayDate.DayOfWeek)}";
        var res = new TodayResult
        {
            DisplayDate = displayDate,
            DateText = $"{displayDate:yyyy年M月d日}",
            WeekLine = weekLine,
            ClassName = selectedClass ?? ""
        };
        res.Plan = plan;
        res.DayTag = plan.TagText;
        res.IsMakeup = plan.IsMakeup;
        res.IsOff = plan.IsOff;
        res.IsWeekend = !plan.IsMakeup && plan.DayIdx > 4;

        if (schedule == null || string.IsNullOrEmpty(selectedClass)) return res;
        var cs = schedule.Classes.FirstOrDefault(c => c.ClassName == selectedClass);
        if (cs == null) return res;

        int dayIdx = plan.DayIdx;
        // ★ 只取「在 displayDate 这天生效」的覆盖：临时调整自带生效区间，
        //   上周的调代课不会再顶到本周课表上（见 OverrideExpander 注释）。
        //   用索引器而不是 ToDictionary：同一格可能残留多条，取 UpdatedAt 最新那条。
        // r133 调休补课：按「被指向的那天」取调课（周三的代课会跟着搬到周六）；
        //   规则里明确「不沿用调课」时整片不看，只留干净的基础课表。
        var ovDict = plan.UsesOverrides
            ? OverrideExpander.MapForDate(overrides ?? Store.LoadOverrides(), selectedClass, plan.OverrideDate)
            : new Dictionary<string, LessonOverride>();
        int parity = plan.Parity;

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
                    TimeRange = BellResolver.ResolveTimeRange(schedule, cs, new Lesson { Period = ov.Period }),
                    Lessons = { new LessonView
                    {
                        DayIdx = dayIdx, Period = ov.Period,
                        Subject = ov.Subject, Teacher = ov.Teacher,
                        LocationText = string.IsNullOrEmpty(ov.Location) ? "" : " · " + ov.Location,
                        WeekTag = "", SubjectColor = "#222222",
                        IsOverridden = true, OverrideType = type,
                        OverrideBadgeText = isSub ? "代课" : "已调课",
                        OverrideBadgeColor = OverrideBadge.Color(type, ov.Source),
                        OverrideNote = OverrideBadge.Note(ov.Note, type, ov.Source)
                    } }
                });
            }
            if (!anyOv)
                res.Rows.Add(new PeriodRow
                {
                    DayIdx = dayIdx,
                    PeriodText = plan.IsOff ? "放假" : "无课",
                    CardBg = plan.IsOff ? "#EEF3EC" : "#FFF7E6",
                    Lessons = { new LessonView { Subject = EmptyDayText(plan), SubjectColor = plan.IsOff ? "#3B6D11" : "#9A7B3F" } }
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

        // 固定时段（大课间/眼保健操/午休）单独收集，最后按「第 N 节之后」插进 body
        var body = new List<PeriodRow>();

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
                OverrideBadgeColor = OverrideBadge.Color(t, ov.Source),
                OverrideNote = OverrideBadge.Note(ov.Note, t, ov.Source)
            };
        }

        foreach (var period in periods.OrderBy(k => k))
        {
            dayMap.TryGetValue(period, out var lessons);
            string key = OverrideExpander.CellKey(selectedClass, dayIdx, period);

            // 原课表没有这一节：若临时课表新增了课，整行都由覆盖记录构成
            if (lessons == null || lessons.Count == 0)
            {
                if (!ovDict.TryGetValue(key, out var nov) || nov.IsEmpty) continue;
                body.Add(new PeriodRow
                {
                    DayIdx = dayIdx,
                    Period = period,
                    PeriodText = $"第{period}节",
                    TimeRange = BellResolver.ResolveTimeRange(schedule, cs, new Lesson { Period = period }),
                    Lessons = { MakeOverrideView(nov, period) }
                });
                continue;
            }

            var first = lessons.First();
            // r135：节次时间逐节解析 —— 手动班用自己的快照，导入班优先 Bell，缺哪节回退哪节
            string timeRange = BellResolver.ResolveTimeRange(schedule, cs, first);
            var row = new PeriodRow
            {
                DayIdx = dayIdx,
                Period = period,
                PeriodText = $"第{period}节",
                TimeRange = timeRange
            };
            if (!string.IsNullOrEmpty(timeRange) && IsCurrent(timeRange, displayDate))
                row.CardBg = "#FFF3CD";

            if (ovDict.TryGetValue(key, out var ov))
            {
                if (ov.IsEmpty)
                {
                    // 被调为空课：仍显示节次行，但内容空白
                    body.Add(row);
                    continue;
                }
                row.Lessons.Add(MakeOverrideView(ov, period));
            }
            else
            {
                foreach (var l in lessons)
                {
                    bool active = l.Week == 0 || l.Week == parity;
                    // 「只显示当前周」时，非本周那节直接不列；整格都没有本周的课时，
                    // 这一节就只剩节次行（空白卡），看起来就是"这周这节没课"，正是想要的简洁效果。
                    if (!showAlternateWeek && !active) continue;
                    row.Lessons.Add(new LessonView
                    {
                        DayIdx = dayIdx,
                        Period = period,
                        Subject = l.Subject,
                        Teacher = l.Teacher,
                        LocationText = string.IsNullOrEmpty(l.Location) ? "" : " · " + l.Location,
                        WeekTag = l.Week == 1 ? "（单周）" : l.Week == 2 ? "（双周）" : "",
                        SubjectColor = active ? "#222222" : "#BBBBBB"
                    });
                }
            }
            body.Add(row);
        }

        res.Rows.AddRange(InsertFixedSlots(body, ResolveFixedSlots(schedule, selectedClass), dayIdx));
        return res;
    }

    /// <summary>
    /// 取该班固定时段。优先级：
    /// ① 手动录入的班 —— 用它自己那份（r129：手动录入的作息与导入 / 自动更新完全独立）；
    /// ② r135 当前课表作息 Bell.FixedSlots（管理员显式设置，全校一致，优先）；
    /// ③ 二一导入的年级作息 FixedByGrade（没设 Bell 时的兜底）；
    /// ④ null —— 不插公共时间行。
    /// <para>★ r135 摘掉了原来的「回退到本机默认模板」：那会让从没设过作息的教室机
    /// 冒出内置初中的大课间 09:50-10:10（幽灵数据）。</para>
    /// </summary>
    internal static List<FixedSlot>? ResolveFixedSlots(ScheduleFile schedule, string? className)
    {
        var cls = string.IsNullOrWhiteSpace(className)
            ? null
            : schedule.Classes.FirstOrDefault(c => c.ClassName == className);
        if (cls != null && cls.IsManual)
        {
            // 发布端本机：模板还在就跟着模板走（用户改了作息立刻生效）
            if (!string.IsNullOrWhiteSpace(cls.PeriodTemplateName))
            {
                var own = Store.LoadScheduleTemplates().PeriodTemplates
                    .FirstOrDefault(p => string.Equals(p.Name, cls.PeriodTemplateName, StringComparison.OrdinalIgnoreCase));
                if (own != null) return own.FixedSlots;
            }
            // 教室机没有本地模板：用保存时写进课表的那份快照
            if (cls.FixedSlots is { Count: > 0 }) return cls.FixedSlots;
            return null;   // 手动班明确没有公共时间时不再回退默认模板（否则会冒出别人学校的大课间）
        }

        return BellResolver.ResolveFixedSlots(schedule, className);
    }

    /// <summary>
    /// 把固定时段行插到对应节次之后，形成「第1节 → 大课间 → 第2节 …」的顺序。
    /// 固定行 Period=0、Lessons 为空：悬浮窗渲染成灰色紧凑分隔条，
    /// 拖动调课的命中判断（Period &gt; 0）与上课提醒都会自动跳过它们。
    /// </summary>
    internal static List<PeriodRow> InsertFixedSlots(List<PeriodRow> body, List<FixedSlot>? fixedSlots, int dayIdx)
    {
        if (fixedSlots == null || fixedSlots.Count == 0) return body;

        var outp = new List<PeriodRow>(body.Count + fixedSlots.Count);

        // 第1节之前（after <= 0）的固定项
        foreach (var f in fixedSlots.Where(f => f.AfterPeriod <= 0))
            outp.Add(MakeFixedRow(f, dayIdx));

        foreach (var r in body)
        {
            outp.Add(r);
            foreach (var f in fixedSlots.Where(f => f.AfterPeriod == r.Period))
                outp.Add(MakeFixedRow(f, dayIdx));
        }
        return outp;
    }

    private static PeriodRow MakeFixedRow(FixedSlot f, int dayIdx) => new()
    {
        DayIdx = dayIdx,
        Period = 0,                 // 0 = 固定时段，不参与拖动/编辑/提醒
        PeriodText = f.Name,
        TimeRange = f.TimeRange,
        CardBg = "#F1F3F6",
        IsFixed = true
    };

    /// <summary>当天没有任何课可显示时的提示语（r133：区分「放假」「补课但缺数据」「普通休息」）。</summary>
    private static string EmptyDayText(DayPlan plan)
    {
        if (plan.IsOff)
            return string.IsNullOrWhiteSpace(plan.Note) ? "今日放假，不上课" : "今日放假：" + plan.Note;
        if (plan.IsMakeup)
        {
            string d = plan.AsDate.HasValue ? SchoolCalendar.DayNameShort(plan.AsDate.Value) : "指定日";
            return "按" + d + "课表上课，但课表里没有" + d + "的课程数据";
        }
        return "今日休息，无课程安排";
    }

    private static bool IsCurrent(string timeRange, DateTime now)    {
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
    public string WeekLine { get; set; } = "";        // 普通日「第12周　星期三」；调休日「按第12周周三上课」
    public string ClassName { get; set; } = "";
    public bool IsWeekend { get; set; }
    public DateTime DisplayDate { get; set; } = DateTime.Now;  // 实际显示依据的那一天

    // ===== r133 校历 / 调休 =====
    /// <summary>这一天的校历结论（补课 / 放假 / 普通日），渲染层用它判断标签与底色。</summary>
    public DayPlan? Plan { get; set; }
    /// <summary>大屏标签：「按第12周周三上课」「今日放假」，普通日为空。</summary>
    public string DayTag { get; set; } = "";
    public bool IsMakeup { get; set; }
    public bool IsOff { get; set; }
}
