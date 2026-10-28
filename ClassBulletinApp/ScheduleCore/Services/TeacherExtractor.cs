using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 从已解析的课表文件（全校总课表 / 单班课表 / 临时总课表均可）反向提取「教师个人课表」：
/// 每位任课教师生成一条 ClassSchedule（ClassName = 教师姓名），
/// 每节课的 Lesson.Teacher 存「所教班级」，Lesson.Location 保留上课地点，
/// 便于悬浮窗按「班级：八年级X班」的形式显示（与导入的教师个人课表文件显示逻辑一致）。
/// </summary>
public static class TeacherExtractor
{
    // 明显不是人名的情况：教室/场地名
    static readonly string[] LocationKeywords =
        { "室", "场", "馆", "楼", "区", "操场", "跑道", "主席台", "平台", "天台", "架空" };

    /// <summary>由课表文件生成教师个人课表文件。</summary>
    public static ScheduleFile Build(ScheduleFile source)
    {
        var result = new ScheduleFile
        {
            SourceFile = source?.SourceFile ?? "",
            ImportedAt = DateTime.Now
        };
        if (source?.Classes == null || source.Classes.Count == 0) return result;

        var map = new Dictionary<string, ClassSchedule>(StringComparer.Ordinal);

        foreach (var cs in source.Classes)
        {
            string className = cs.ClassName ?? "";
            if (string.IsNullOrWhiteSpace(className)) continue;

            foreach (var day in cs.Data)
            {
                int dayIdx = day.Key;
                foreach (var slot in day.Value)
                {
                    int period = slot.Key;
                    foreach (var lesson in slot.Value)
                    {
                        var teacher = NormalizeTeacherName(lesson.Teacher);
                        if (string.IsNullOrEmpty(teacher)) continue;

                        if (!map.TryGetValue(teacher, out var ts))
                        {
                            ts = new ClassSchedule { ClassName = teacher };
                            map[teacher] = ts;
                        }
                        if (!ts.Data.ContainsKey(dayIdx))
                            ts.Data[dayIdx] = new Dictionary<int, List<Lesson>>();
                        if (!ts.Data[dayIdx].ContainsKey(period))
                            ts.Data[dayIdx][period] = new List<Lesson>();

                        var list = ts.Data[dayIdx][period];
                        // 同一节同一科目的重复记录去重（如同一节被拆成两行导入）
                        if (list.Any(x => x.Subject == lesson.Subject && x.Teacher == className)) continue;

                        list.Add(new Lesson
                        {
                            Period = period,
                            Subject = lesson.Subject,
                            Teacher = className,          // 教师视角：这一-field 显示所教班级
                            Location = lesson.Location,
                            Week = lesson.Week,
                            TimeRange = lesson.TimeRange
                        });
                    }
                }
            }
        }

        foreach (var name in map.Keys.OrderBy(x => x, ZhComparer()))
            result.Classes.Add(map[name]);

        return result;
    }

    /// <summary>中文名称排序器（无中文排序支持时自动退回序号排序，避免异常）。</summary>
    public static IComparer<string> ZhComparer()
    {
        try { return StringComparer.Create(new CultureInfo("zh-CN"), true); }
        catch { return StringComparer.OrdinalIgnoreCase; }
    }

    /// <summary>
    /// 归一化教师名：去掉括号内容、（调）/（代）标记与空白；
    /// 命中明显非人名（含数字/字母、过长、场地关键字）时返回空串以跳过。
    /// </summary>
    static string NormalizeTeacherName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = Regex.Replace(raw, @"[\(（][^）)]*[\)）]", "").Trim();
        s = s.Replace("（调）", "").Replace("(调)", "").Replace("（代）", "").Replace("(代)", "").Trim();
        if (s.Length == 0 || s.Length > 6) return "";
        if (Regex.IsMatch(s, @"[0-9A-Za-z]")) return "";
        if (LocationKeywords.Any(k => s.Contains(k))) return "";
        return s;
    }
}
