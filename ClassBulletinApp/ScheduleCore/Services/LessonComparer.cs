using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 「这条调代课是不是把课改回了原位」的判定。
///
/// <para><b>为什么需要它</b>：调课的操作是可逆的（拖走再拖回、右键改回原科目），
/// 但如果改回原位后仍留一条覆盖记录，格子上会一直挂着「已调课 / 代课」角标，
/// 数据包也会带着一堆"内容是原课"的无效记录发到 30 台机器上。
/// 因此只要写入的内容与底表该格<b>完全相同</b>，就应当把这条记录删掉，而不是保存。</para>
///
/// <para><b>保守原则</b>：单双周同一格是两门不同课时，"原位"不唯一，此时一律返回
/// <c>false</c>（判断不了 → 照常保留记录），宁可多留一条也不要误删用户的手工安排。</para>
/// </summary>
public static class LessonComparer
{
    /// <summary>
    /// 取底表某格（班 + 星期 + 节次）的原始课程。
    /// 返回 false 表示"判断不了"：该班在底表里不存在，或同格是单双周两门不同课。
    /// </summary>
    public static bool TryBaseCell(ScheduleFile? schedule, string className, int dayIdx, int period,
                                   out string subject, out string teacher, out string location)
    {
        subject = teacher = location = "";
        var cs = schedule?.Classes.FirstOrDefault(c => c.ClassName == className);
        if (cs == null) return false;

        if (!cs.Data.TryGetValue(dayIdx, out var dayMap)) return true;                 // 该日无课 → 空白
        if (!dayMap.TryGetValue(period, out var lessons) || lessons.Count == 0) return true;

        var first = lessons[0];
        if (lessons.Any(l => !SameText(l.Subject, first.Subject) || !SameText(l.Teacher, first.Teacher)))
            return false;   // 单双周不同课：没有唯一的"原位"

        subject = first.Subject;
        teacher = first.Teacher;
        location = first.Location;
        return true;
    }

    /// <summary>
    /// 覆盖内容是否就是底表原课程。调空（isEmpty）只有在底表本来也没课时才算"原位"。
    /// </summary>
    public static bool IsSameAsBase(ScheduleFile? schedule, string className, int dayIdx, int period,
                                    string? subject, string? teacher, string? location, bool isEmpty)
    {
        if (!TryBaseCell(schedule, className, dayIdx, period, out var bs, out var bt, out var bl))
            return false;

        if (isEmpty) return string.IsNullOrWhiteSpace(bs);
        if (string.IsNullOrWhiteSpace(subject)) return false;

        return SameText(subject, bs) && SameText(teacher, bt) && SameText(location, bl);
    }

    /// <summary>课表文本比较：忽略首尾空格与大小写（Excel 里常见多打的空格）。</summary>
    public static bool SameText(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
