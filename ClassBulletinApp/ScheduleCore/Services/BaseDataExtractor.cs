using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// r136：「课表基础数据」自动提取与三态合并。
/// <para>从当前课表 (ScheduleFile.Classes) 反向整理班级 / 科目 / 教师清单，
/// 从每条课自带的 TimeRange 提取作息节次时间，并从来源文件名猜学期名建议值。</para>
/// <para>★ 只产出内存结果 (<see cref="BaseDataExtractResult"/>)，不落盘、不改课表；
/// 冲突（同一节两种时间）要人工单选后由 <see cref="Apply"/> 写回。</para>
/// </summary>
public static class BaseDataExtractor
{
    /// <summary>从当前课表整理出一份候选清单与作息。</summary>
    public static BaseDataExtractResult Extract(ScheduleFile sf)
    {
        var r = new BaseDataExtractResult();
        if (sf == null) return r;

        r.SuggestedTermName = GuessTermName(sf);

        // ---- 班级 / 科目 / 教师：从 Classes 的每条课统计 ----
        var classCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subjectCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var teacherCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cs in sf.Classes)
        {
            string className = (cs.ClassName ?? "").Trim();
            if (className.Length > 0)
                Bump(classCount, className);

            foreach (var day in cs.Data)
                foreach (var slot in day.Value)
                    foreach (var lesson in slot.Value)
                    {
                        string subj = (lesson.Subject ?? "").Trim();
                        if (subj.Length > 0) Bump(subjectCount, subj);
                        string teacher = TeacherExtractor.NormalizeTeacherName(lesson.Teacher);
                        if (teacher.Length > 0) Bump(teacherCount, teacher);
                    }
        }

        // 已有清单里的人工项（手动 / 排除 / 改名墓碑）先取出来，自动项整批刷新
        r.Classes = MergeAuto(sf.ClassEntries, classCount);
        r.Subjects = MergeAuto(sf.SubjectEntries, subjectCount);
        r.Teachers = MergeAuto(sf.TeacherEntries, teacherCount);

        // ---- 作息节次时间：从每条课的 TimeRange 按节次归并，同节多值记冲突 ----
        var byPeriod = new Dictionary<int, List<string>>();
        foreach (var cs in sf.Classes)
            foreach (var day in cs.Data)
                foreach (var slot in day.Value)
                {
                    int period = slot.Key;
                    foreach (var lesson in slot.Value)
                    {
                        string t = NormalizeRange(lesson.TimeRange);
                        if (t.Length == 0) continue;
                        if (!byPeriod.TryGetValue(period, out var list))
                            byPeriod[period] = list = new List<string>();
                        if (!list.Contains(t)) list.Add(t);
                    }
                }

        foreach (var period in byPeriod.Keys.OrderBy(x => x))
        {
            var vals = byPeriod[period];
            if (vals.Count == 1)
            {
                r.BellPeriods.Add(new PeriodTime { Index = period, TimeRange = vals[0] });
            }
            else
            {
                // 同节两种时间：不自动取值，列为冲突让人选
                r.Conflicts.Add(new BaseDataConflict
                {
                    Key = $"第{period}节",
                    Candidates = vals.OrderBy(x => x).ToList()
                });
            }
        }

        // 公共时间：二一已写进 FixedByGrade，取出现次数最多的那个年级那份作为候选
        r.BellFixedSlots = sf.FixedByGrade.Values
            .Where(x => x is { Count: > 0 })
            .GroupBy(x => string.Join("|", x.Select(f => $"{f.AfterPeriod}:{f.Name}:{f.TimeRange}")))
            .OrderByDescending(g => g.Count())
            .Select(g => g.First().ToList())
            .FirstOrDefault() ?? new List<FixedSlot>();

        return r;
    }

    /// <summary>
    /// 「从课表节次自动填入」专用：按出现次数给每一节推荐一个时间。
    /// <para>★ 与 <see cref="Extract"/> 的区别：那边同节多值必须人工裁决才能 Apply（防止悄悄改掉全校作息）；
    /// 这边是用户明确点了「自动填入」的填表动作 —— 真实课表里 90 个班同一节常有两三种写法
    /// （如 08:00-08:40 / 08:00-08:45），逐节记冲突会让整张表一个节次都填不进去，
    /// 所以按出现次数取多数值，少数差异通过 <paramref name="contestedPeriods"/> 回报给界面提示人工核对。</para>
    /// </summary>
    /// <param name="contestedPeriods">出现两种及以上不同时间的节次数（仅用于提示，不阻断填入）。</param>
    public static List<PeriodTime> SuggestBellPeriods(ScheduleFile? sf, out int contestedPeriods)
    {
        contestedPeriods = 0;
        var result = new List<PeriodTime>();
        if (sf == null) return result;

        var byPeriod = new Dictionary<int, Dictionary<string, int>>();
        foreach (var cs in sf.Classes)
            foreach (var day in cs.Data)
                foreach (var slot in day.Value)
                {
                    foreach (var lesson in slot.Value)
                    {
                        string t = NormalizeRange(lesson.TimeRange);
                        if (t.Length == 0) continue;
                        if (!byPeriod.TryGetValue(slot.Key, out var counts))
                            byPeriod[slot.Key] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        counts.TryGetValue(t, out int n);
                        counts[t] = n + 1;
                    }
                }

        foreach (var period in byPeriod.Keys.OrderBy(x => x))
        {
            var counts = byPeriod[period];
            if (counts.Count > 1) contestedPeriods++;
            // 并列时取字典序较小者，保证同一份课表每次填出来的结果一样
            var best = counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
            result.Add(new PeriodTime { Index = period, TimeRange = best.Key });
        }
        return result;
    }

    /// <summary>把整理结果写回课表。冲突必须已裁决（Conflicts 里每项 Chosen 非空）。</summary>
    public static int Apply(ScheduleFile sf, BaseDataExtractResult r)
    {
        if (sf == null || r == null) return 0;
        if (r.Conflicts.Any(c => !c.Resolved))
            throw new InvalidOperationException("还有未确认的作息分歧，请先逐项选定。");

        int changed = 0;
        sf.ClassEntries = r.Classes;
        sf.SubjectEntries = r.Subjects;
        sf.TeacherEntries = r.Teachers;
        changed += r.Classes.Count + r.Subjects.Count + r.Teachers.Count;

        if (!string.IsNullOrWhiteSpace(r.SuggestedTermName))
            sf.TermName = r.SuggestedTermName.Trim();

        // 只有整理出了节次或公共时间才写 Bell，避免空结果把已设的作息抹掉
        if (r.BellPeriods.Count > 0 || r.BellFixedSlots.Count > 0)
        {
            sf.Bell ??= new BellSchedule();
            sf.Bell.Periods = r.BellPeriods.OrderBy(x => x.Index).ToList();
            sf.Bell.FixedSlots = r.BellFixedSlots.OrderBy(x => x.AfterPeriod).ToList();
            if (string.IsNullOrEmpty(sf.Bell.Name)) sf.Bell.Name = "从课表整理";
            sf.Bell.UpdatedAt = DateTime.Now;
            sf.Bell.UpdatedFrom = "基础数据整理";
        }
        return changed;
    }

    /// <summary>
    /// 三态合并：已有的人工项（手动 / 排除 / 改名墓碑）原样保留；
    /// 课表里出现的名字若已被改名 / 排除则不复活；其余按当前统计刷新为自动项。
    /// </summary>
    private static List<BaseDataEntry> MergeAuto(
        List<BaseDataEntry> existing, Dictionary<string, int> counts)
    {
        var result = new List<BaseDataEntry>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (existing != null)
        {
            foreach (var e in existing)
            {
                result.Add(e);
                handled.Add(e.Name);
                // 改名后的新名也不应被自动项重复添加（如果它是手动项的话）
                if (!string.IsNullOrEmpty(e.RenamedTo)) handled.Add(e.RenamedTo);
            }
        }

        foreach (var kv in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, TeacherExtractor.ZhComparer()))
        {
            if (handled.Contains(kv.Key)) continue;
            result.Add(new BaseDataEntry
            {
                Name = kv.Key,
                Count = kv.Value,
                Origin = BaseDataOrigin.Auto,
                UpdatedAt = DateTime.Now
            });
        }
        return result;
    }

    private static void Bump(Dictionary<string, int> map, string name)
    {
        map.TryGetValue(name, out int n);
        map[name] = n + 1;
    }

    private static string NormalizeRange(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s == "HH:MM" || s.Contains("HH:MM")) return "";   // 占位符不当作时间
        return Regex.IsMatch(s, @"\d{1,2}:\d{2}\s*[-~至]\s*\d{1,2}:\d{2}") ? s : "";
    }

    /// <summary>从来源文件名猜学期名建议值。猜不到返回 null，由人手工填。</summary>
    private static string? GuessTermName(ScheduleFile sf)
    {
        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(sf.SourceFile)) sources.Add(sf.SourceFile);
        if (sf.SourceFiles != null) sources.AddRange(sf.SourceFiles.Where(x => !string.IsNullOrWhiteSpace(x)));
        foreach (var s in sources)
        {
            // 2026学年第一学期 / 2026-2027学年第二学期 / 2026 年秋季学期
            var m = Regex.Match(s, @"(20\d{2}(?:\s*[-—~]\s*20\d{2})?\s*学年\s*第?\s*[一二12]\s*学期)");
            if (m.Success) return Regex.Replace(m.Groups[1].Value, @"\s+", "");
            m = Regex.Match(s, @"(20\d{2}\s*年\s*(?:春|秋)(?:季)?\s*学期)");
            if (m.Success) return Regex.Replace(m.Groups[1].Value, @"\s+", "");
        }
        return null;
    }
}
