using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 某一天「到底怎么上课」的结论。所有显示路径都从这里拿 <see cref="DayIdx"/> / <see cref="Parity"/>，
/// 不许再各自用 <c>((int)date.DayOfWeek + 6) % 7</c> —— 否则调休日在大屏、周视图、提醒、
/// 换课徽章上显示的会是不同的东西。
/// </summary>
public sealed class DayPlan
{
    /// <summary>真实日期（永远是这一天本身，不会被调休改写）。</summary>
    public DateTime Date { get; init; }

    /// <summary>按哪一列的课表上课（0=周一 … 6=周日）。默认 = 日历星期；补课日 = 被指向那天的星期。</summary>
    public int DayIdx { get; init; }

    /// <summary>1=单周 / 2=双周。默认按 <see cref="Date"/> 所在教学周；补课日按 <see cref="AsDate"/> 那周。</summary>
    public int Parity { get; init; }

    /// <summary>这天放假（不上课）。</summary>
    public bool IsOff { get; init; }

    /// <summary>这天是调休补课日（周末/放假日也要按某天的课表上课）。</summary>
    public bool IsMakeup { get; init; }

    /// <summary>补课按哪一天（null = 不是补课日或规则没指定）。</summary>
    public DateTime? AsDate { get; init; }

    /// <summary>补课日是否沿用被指向那天的临时调课/代课。</summary>
    public bool InheritOverrides { get; init; } = true;

    /// <summary>规则备注（如「上周三活动停课」）。</summary>
    public string Note { get; init; } = "";

    /// <summary>查调代课用的日期：补课日指向被映射的那天（这样"周三第 3 节的代课"会一起搬过来）。</summary>
    public DateTime OverrideDate => IsMakeup && InheritOverrides && AsDate.HasValue ? AsDate.Value : Date;

    /// <summary>这天是否还要看调代课（补课且明确"不沿用"时整片忽略）。</summary>
    public bool UsesOverrides => !(IsMakeup && !InheritOverrides);

    /// <summary>大屏上的小标签文案：补课「按周三课表上课」；放假「今日放假」；普通日空串。</summary>
    public string TagText
    {
        get
        {
            if (IsOff) return "今日放假";
            if (!IsMakeup) return "";
            string d = AsDate.HasValue ? SchoolCalendar.DayNameShort(AsDate.Value) : "指定日";
            return "按" + d + "课表上课";
        }
    }

    /// <summary>把标签 + 备注拼成一句人话，给周视图表头 / 提示文字用。</summary>
    public string Describe()
    {
        string t = TagText;
        if (string.IsNullOrEmpty(t)) return "";
        return string.IsNullOrWhiteSpace(Note) ? t : t + "（" + Note + "）";
    }

    /// <summary>这一天是否算"正常上课日"（补课算，放假日与普通周末不算）。</summary>
    public bool IsSchoolDay => !IsOff && DayIdx <= 4;
}

/// <summary>
/// 校历换算的唯一入口（r133）。
///
/// <para>默认假设是"日历星期 = 上哪天的课"，调休打破它。这里提供两层：
/// ① <see cref="Resolve(ScheduleFile?, DateTime, string?)"/> 给业务代码用；
/// ② <see cref="SetRule"/> / <see cref="RemoveRule"/> 给设置界面用（同一天只留一条，后写的覆盖先前的）。</para>
///
/// <para>★ 一条坏规则绝不能把课表弄没：日期解析失败时退回"普通一天"，而不是当天无课。</para>
/// </summary>
public static class SchoolCalendar
{
    /// <summary>按课表里的校历解析某一天。</summary>
    public static DayPlan Resolve(ScheduleFile? schedule, DateTime date, string? semesterStartMonday = null)
        => Resolve(schedule?.Calendar, date, semesterStartMonday);

    /// <summary>按校历规则解析某一天（这是唯一实现）。</summary>
    public static DayPlan Resolve(IEnumerable<SchoolCalendarRule>? rules, DateTime date,
        string? semesterStartMonday = null)
    {
        date = date.Date;
        var plan = new DayPlan
        {
            Date = date,
            DayIdx = DayIdxOf(date),
            Parity = WeekHelper.GetParity(date, semesterStartMonday),
            InheritOverrides = true
        };

        var rule = FindRule(rules, date);
        if (rule == null) return plan;

        if (rule.IsOff)
            return new DayPlan
            {
                Date = date, DayIdx = plan.DayIdx, Parity = plan.Parity,
                IsOff = true, Note = rule.Note ?? ""
            };

        var asDate = rule.AsDateValue;
        // 规则写坏了（没填「按哪天」或日期解析不了）就当普通一天，绝不因此让当天无课
        if (asDate == null) return plan;

        return new DayPlan
        {
            Date = date,
            DayIdx = DayIdxOf(asDate.Value),
            Parity = WeekHelper.GetParity(asDate.Value, semesterStartMonday),
            IsMakeup = true,
            AsDate = asDate.Value,
            InheritOverrides = rule.InheritOverrides,
            Note = rule.Note ?? ""
        };
    }

    /// <summary>这一天的校历规则（同一天多条时取最后一条 —— 后加的覆盖先前的）。</summary>
    public static SchoolCalendarRule? FindRule(IEnumerable<SchoolCalendarRule>? rules, DateTime date)
    {
        if (rules == null) return null;
        var d = date.Date;
        SchoolCalendarRule? hit = null;
        foreach (var r in rules)
        {
            if (r == null) continue;
            var rv = r.DateValue;
            if (rv.HasValue && rv.Value == d) hit = r;
        }
        return hit;
    }

    /// <summary>0=周一 … 6=周日（数据层约定，与 <c>LessonOverride.DayIdx</c> 一致）。</summary>
    public static int DayIdxOf(DateTime date) => ((int)date.DayOfWeek + 6) % 7;

    /// <summary>0=周一 →「周一」。</summary>
    public static string DayNameShort(int dayIdx) => dayIdx switch
    {
        0 => "周一",
        1 => "周二",
        2 => "周三",
        3 => "周四",
        4 => "周五",
        5 => "周六",
        _ => "周日"
    };

    /// <summary>「周三」。</summary>
    public static string DayNameShort(DateTime date) => DayNameShort(DayIdxOf(date));

    /// <summary>
    /// 写入一条规则（同一天已有则整条替换）。返回 true 表示是新增、false 表示覆盖。
    /// 只改内存列表，落盘由调用方 <c>Store.SaveSchedule</c> 负责。
    /// </summary>
    public static bool SetRule(List<SchoolCalendarRule> list, SchoolCalendarRule rule)
    {
        if (list == null || rule?.DateValue == null) return false;
        var d = rule.DateValue.Value;
        list.RemoveAll(r => r?.DateValue == d);
        list.Add(rule);
        list.Sort((a, b) => (a.DateValue ?? DateTime.MaxValue).CompareTo(b.DateValue ?? DateTime.MaxValue));
        return true;
    }

    /// <summary>删掉某一天的规则，返回删掉的条数。</summary>
    public static int RemoveRule(List<SchoolCalendarRule> list, DateTime date)
    {
        if (list == null) return 0;
        var d = date.Date;
        return list.RemoveAll(r => r?.DateValue == d);
    }

    /// <summary>
    /// 解析一条文本规则，供批量粘贴用。支持这些写法（日期可用 - 或 / 分隔）：
    /// <c>2026-11-21 补课 2026-11-18 上周三活动停课</c>、
    /// <c>2026-11-21 按 11-18 上课</c>、<c>2026-11-23 放假 校运会</c>。
    /// 解析不出来返回 null（调用方给出行号提示）。
    /// </summary>
    public static SchoolCalendarRule? ParseLine(string line, int defaultYear)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Replace('／', '/').Replace('－', '-')
                        .Split(new[] { ' ', '\t', ',', '，', ';', '；' },
                               StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // ① 第一个日期 = 这一天
        if (!TryDate(parts[0], defaultYear, out var date)) return null;

        string rest = line;
        bool off = rest.Contains("放假") || rest.Contains("休息") || rest.Contains("不上课");
        var rule = new SchoolCalendarRule
        {
            Date = date.ToString("yyyy-MM-dd"),
            Kind = off ? CalendarKind.Off : CalendarKind.Makeup
        };
        if (off) { rule.Note = GuessNote(); return rule; }

        // ② 第二个日期 = 按哪天上（"按 11-18 上课" 里的 11-18）
        for (int i = 1; i < parts.Length; i++)
        {
            if (!TryDate(parts[i], date.Year, out var asDate)) continue;
            rule.AsDate = asDate.ToString("yyyy-MM-dd");
            break;
        }
        rule.Note = GuessNote();
        rule.InheritOverrides = !line.Contains("不沿用") && !line.Contains("不看调课");
        if (rule.AsDate == "") return null;   // 补课必须有"按哪天"，否则写了等于没写
        return rule;

        // 备注 = 去掉日期 / 关键词之后剩下的那段（没有就空）
        string GuessNote()
        {
            var words = parts.Where(p =>
            {
                if (TryDate(p, defaultYear, out _)) return false;
                if (p is "补课" or "调休" or "上课" or "放假" or "休息" or "按" or "不沿用") return false;
                return true;
            }).ToList();
            return string.Join(" ", words).Trim();
        }
    }

    static bool TryDate(string s, int defaultYear, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (DateTime.TryParse(s, out date)) { date = date.Date; return true; }

        // 支持 11-18 / 11/18 这种省略年份的写法
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d{1,2})[-/.](\d{1,2})$");
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var mo)
            && int.TryParse(m.Groups[2].Value, out var dd)
            && mo >= 1 && mo <= 12 && dd >= 1 && dd <= 31)
        {
            try { date = new DateTime(defaultYear, mo, dd); return true; } catch { }
        }
        return false;
    }
}
