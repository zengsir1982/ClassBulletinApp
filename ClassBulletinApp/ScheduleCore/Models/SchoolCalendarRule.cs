using System;
using System.Text.Json.Serialization;

namespace ScheduleCore.Models;

/// <summary>
/// 校历规则：某一天「怎么上课」。
///
/// <para><b>为什么需要它</b>：课表是按星期几（<c>DayIdx</c> 0=周一…6=周日）存的，
/// 而调休会打破"日历星期 = 上课星期"这个默认假设 —— 周六要上周三的课，或者周一放假。
/// 以前只能靠「固定显示日期」把整台机器骗到某一天（日期、倒计时、提醒全跟着错位），
/// 这里把「真实日期」和「按哪天的课表上课」彻底分开。</para>
///
/// <para>存进 <see cref="ScheduleFile.Calendar"/>，跟着课表随数据包下发 —— 全校口径一致，
/// 不用每台教室机手动设。</para>
/// </summary>
public class SchoolCalendarRule
{
    /// <summary>这一天（yyyy-MM-dd）。</summary>
    public string Date { get; set; } = "";

    /// <summary><see cref="CalendarKind.Makeup"/> = 补课（按某天的课表上课）；<see cref="CalendarKind.Off"/> = 放假。</summary>
    public string Kind { get; set; } = CalendarKind.Makeup;

    /// <summary>
    /// 补课时「按哪一天的课表上课」（yyyy-MM-dd）。周几、单双周、调代课全部按这一天取，
    /// 所以跨周补课（周六补上周三）也不会把单双周算错。
    /// </summary>
    public string AsDate { get; set; } = "";

    /// <summary>
    /// 补课日是否沿用那一天生效的临时调课/代课。默认 true ——「上的就是那天的课」。
    /// 若那天调课本身是因为"那天有活动所以把语文调走"，补课时可以取消勾选，只看干净的基础课表。
    /// </summary>
    public bool InheritOverrides { get; set; } = true;

    /// <summary>备注，如「上周三活动停课」。大屏标签和设置列表里会显示。</summary>
    public string Note { get; set; } = "";

    [JsonIgnore] public bool IsOff => Kind == CalendarKind.Off;

    [JsonIgnore]
    public DateTime? DateValue
        => DateTime.TryParse(Date, out var d) ? d.Date : null;

    [JsonIgnore]
    public DateTime? AsDateValue
        => DateTime.TryParse(AsDate, out var d) ? d.Date : null;

    /// <summary>列表里显示用：「2026-11-21（周六）补课 按 11-18 周三」。</summary>
    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            if (DateValue == null) return "（日期无效）" + (Note ?? "");
            var d = DateValue.Value;
            string head = $"{d:MM-dd}（{WeekDayShort(d.DayOfWeek)}）";
            if (IsOff) return head + "　放假" + Suffix();
            var asD = AsDateValue;
            string tail = asD == null ? "　补课（未指定按哪天）" : $"　补课 · 按 {asD.Value:MM-dd} {WeekDayShort(asD.Value.DayOfWeek)}";
            return head + tail + (InheritOverrides ? "" : "（不沿用调课）") + Suffix();
        }
    }

    string Suffix() => string.IsNullOrWhiteSpace(Note) ? "" : "　" + Note;

    static string WeekDayShort(DayOfWeek d) => d switch
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

/// <summary>校历规则类型常量（存字符串，方便以后加「半天」之类不破坏旧数据）。</summary>
public static class CalendarKind
{
    /// <summary>补课：按 <see cref="SchoolCalendarRule.AsDate"/> 那天的课表上课。</summary>
    public const string Makeup = "Makeup";

    /// <summary>放假：不上课，大屏显示「今日放假」。</summary>
    public const string Off = "Off";
}
