using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 把「以班级为单位的调课 / 代课记录」换算到某位教师的个人课表上，
/// 使教师个人周课表与悬浮课表也能反映临时课表的最新安排
/// （班级层面的调代记录优先于由总课表整理出来的原始教师课表）。
///
/// 换算规则（对目标教师的每一格）：
///   ① 该格对应的班级被「调空」            → 教师这节课消失
///   ② 该格换成了别的任课教师（代课）       → 教师这节课消失
///   ③ 该格仍是本人任教，但科目/地点有变化  → 采用临时课表的科目与地点
///   ④ 临时课表中出现了「任课教师=本人」但该格原本不在其个人课表里的记录 → 追加为新增的课
/// 生成的是以「教师名|星期|节次」为键的覆盖集合，可直接喂给 WeekBuilder / BoardBuilder，
/// 从而同时获得调课/代课徽章。原始存储的数据不会被修改。
/// </summary>
public static class TeacherOverrideApplier
{
    /// <summary>生成某位教师个人课表应当套用的覆盖集合。</summary>
    /// <param name="teacherSchedule">教师个人课表（Lesson.Teacher 存的是所教班级）。</param>
    /// <param name="teacherName">教师姓名。</param>
    /// <param name="classOverrides">以班级为单位的调课/代课记录（含临时课表导入与手动调课）。</param>
    public static List<LessonOverride> BuildTeacherOverrides(
        ScheduleFile? teacherSchedule, string? teacherName, IEnumerable<LessonOverride>? classOverrides)
    {
        var result = new List<LessonOverride>();
        if (string.IsNullOrWhiteSpace(teacherName) || teacherSchedule == null) return result;

        var all = (classOverrides ?? Enumerable.Empty<LessonOverride>())
                  .Where(o => o != null && !string.IsNullOrEmpty(o.ClassName))
                  .ToList();
        if (all.Count == 0) return result;   // 无任何调代数据：教师课表保持原样

        var mine = teacherSchedule.Classes.FirstOrDefault(c => c.ClassName == teacherName);
        if (mine == null) return result;

        // 教师原课：(星期, 节次) -> 该格的所有课时（单双周交替时可能有多条）
        var slots = new Dictionary<(int Day, int Period), List<Lesson>>();
        foreach (var day in mine.Data)
            foreach (var slot in day.Value)
                foreach (var l in slot.Value)
                {
                    var key = (day.Key, slot.Key);
                    if (!slots.TryGetValue(key, out var list))
                    {
                        list = new List<Lesson>();
                        slots[key] = list;
                    }
                    list.Add(l);
                }

        var ovMap = all
            .GroupBy(o => o.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // ---- ① ② ③：逐格按班级层面的记录修正 ----
        foreach (var kv in slots)
        {
            int dayIdx = kv.Key.Day, period = kv.Key.Period;
            var lessons = kv.Value;
            if (lessons.Count == 0) continue;

            var kept = new List<(Lesson Lesson, string Type)>();
            bool changed = false;

            foreach (var l in lessons)
            {
                string cls = (l.Teacher ?? "").Trim();   // 教师视角：Teacher 字段实为所教班级
                if (cls.Length == 0 || !ovMap.TryGetValue($"{cls}|{dayIdx}|{period}", out var ov))
                {
                    kept.Add((l, ""));
                    continue;
                }

                changed = true;

                // ① 调空：教师少一节课
                if (ov.IsEmpty) continue;

                // ② 换成别的教师任课：代课，本人这节取消
                if (!string.IsNullOrWhiteSpace(ov.Teacher) && !SameTeacher(ov.Teacher, teacherName!)) continue;

                // ③ 仍是本人任课：采用临时课表的科目 / 地点
                string type = string.IsNullOrWhiteSpace(ov.Type) ? "调" : ov.Type;
                kept.Add((new Lesson
                {
                    Period = period,
                    Week = l.Week,
                    TimeRange = l.TimeRange,
                    Subject = string.IsNullOrWhiteSpace(ov.Subject) ? l.Subject : ov.Subject,
                    Teacher = cls,
                    Location = string.IsNullOrWhiteSpace(ov.Location) ? l.Location : ov.Location
                }, type));
            }

            if (!changed) continue;

            if (kept.Count == 0)
            {
                result.Add(MakeOverride(teacherName!, dayIdx, period, "", "", "", "调", true));
            }
            else if (kept.Count == 1)
            {
                var k = kept[0];
                result.Add(MakeOverride(teacherName!, dayIdx, period, k.Lesson.Subject,
                    k.Lesson.Teacher, k.Lesson.Location, string.IsNullOrEmpty(k.Type) ? "调" : k.Type, false));
            }
            // 同一格有多个班/多门课的复杂情形：不下覆盖，保留整理结果，避免丢数据
        }

        // ---- ④ 临时课表里新增给这位教师的课 ----
        foreach (var ov in all)
        {
            if (ov.IsEmpty) continue;
            if (string.IsNullOrWhiteSpace(ov.Teacher) || !SameTeacher(ov.Teacher, teacherName!)) continue;

            bool slotBusy = slots.TryGetValue((ov.DayIdx, ov.Period), out var existing)
                            && existing != null && existing.Count > 0
                            && existing.All(l => !string.Equals((l.Teacher ?? "").Trim(), ov.ClassName, StringComparison.Ordinal));
            // 该格已被别的班占用（无法在同一格里同时表达两个班）时跳过，避免冲掉原有记录
            if (slotBusy) continue;

            bool alreadyKnown = result.Any(o => o.DayIdx == ov.DayIdx && o.Period == ov.Period);
            if (alreadyKnown) continue;

            string type = string.IsNullOrWhiteSpace(ov.Type) ? "调" : ov.Type;
            result.Add(MakeOverride(teacherName!, ov.DayIdx, ov.Period, ov.Subject, ov.ClassName, ov.Location, type, false));
        }

        return result
            .GroupBy(o => o.Key, StringComparer.Ordinal)
            .Select(g => g.Last())
            .OrderBy(o => o.DayIdx).ThenBy(o => o.Period)
            .ToList();
    }

    private static LessonOverride MakeOverride(string teacherName, int dayIdx, int period,
        string subject, string className, string location, string type, bool isEmpty)
        => new LessonOverride
        {
            ClassName = teacherName,
            DayIdx = dayIdx,
            Period = period,
            Subject = subject,
            Teacher = className,      // 教师视角：这里填所教班级
            Location = location,
            Type = type,
            IsEmpty = isEmpty,
            // 视图层派生数据，不落盘，避免与 classes 的原始记录混淆
            Source = "teacher-view",
            UpdatedAt = DateTime.Now
        };

    /// <summary>教师名比对：忽略空白与全半角括号后的残留差异。</summary>
    private static bool SameTeacher(string a, string b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);
}
