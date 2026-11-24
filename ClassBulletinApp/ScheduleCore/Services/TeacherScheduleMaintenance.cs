using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 教师个人课表（<c>teacher_schedule.json</c>）的「保鲜」与「认人」。
///
/// <para>教师表是由总课表反向整理出来的派生数据，故意不打进数据包（能省一半体积），
/// 各机导入后自己重建。但重建只挂在「导入课表 / 手动导入数据包」这几条链路上：
/// 用二一排课缓存启动、或者别人直接替换 data 目录里的底表，教师表就会停在旧版本 ——
/// 老师登录后看到上周的课，上下课提醒也跟着错。</para>
///
/// <para>这里只做判定，读写盘留给调用方：诊断模式（不落盘）也需要跑同一套判定。</para>
/// </summary>
public static class TeacherScheduleMaintenance
{
    /// <summary>
    /// 教师表是不是该重建了。
    /// <para>判定只看时间戳，不做内容比对（重建本身才 10 毫秒级，但每次启动都重建会让
    /// 手工单独导入过教师表的机器被总课表覆盖掉 —— 那是数据丢失，比过期严重）。</para>
    /// </summary>
    public static bool IsStale(ScheduleFile? source, ScheduleFile? teachers)
    {
        if (source?.Classes == null || source.Classes.Count == 0) return false;
        if (teachers?.Classes == null || teachers.Classes.Count == 0) return true;

        // 底表比教师表新 = 底表更新过但没重建。留 1 秒容差：同一次导入里底表先写、教师表后写。
        return teachers.ImportedAt < source.ImportedAt.AddSeconds(-1);
    }

    /// <summary>
    /// 在教师表里找「这个账号是谁」。三级匹配：全等 → 去空格 / 去括号后相等 → 唯一包含。
    /// <para>返回教师表里的原始姓名（即 <c>ClassSchedule.ClassName</c>），匹配不到返回 null。</para>
    /// <para>★ 账号用户名与教师表姓名用的是同一个归一化函数
    /// （<see cref="TeacherAccountGenerator.NormalizeTeacherName"/> 与 <see cref="TeacherExtractor.NormalizeTeacherName"/>），
    /// 所以绝大多数情况一级就命中，后两级只为对付「张三老师」「张三(班主任)」这类手工账号。</para>
    /// </summary>
    public static string? MatchTeacher(AppUser? user, ScheduleFile? teachers)
    {
        if (user == null || teachers?.Classes == null || teachers.Classes.Count == 0) return null;

        var names = teachers.Classes
            .Select(c => c.ClassName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        if (names.Count == 0) return null;

        // 账号上可能写的候选：登录名、显示名（「八(22)班 班主任 张三」）、以及显示名里的最后一段
        var candidates = new List<string>();
        void Add(string? s)
        {
            string v = (s ?? "").Trim();
            if (v.Length == 0) return;
            if (!candidates.Contains(v, StringComparer.Ordinal)) candidates.Add(v);
        }
        Add(user.Username);
        Add(user.Caption);
        foreach (var part in (user.Caption ?? "").Split(new[] { ' ', '　', '·' }, StringSplitOptions.RemoveEmptyEntries))
            Add(part);

        // 一级：原样相等（忽略大小写）
        foreach (var c in candidates)
        {
            var hit = names.FirstOrDefault(n => n.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        // 二级：去空格、去括号内容后相等
        foreach (var c in candidates)
        {
            string k = LooseKey(c);
            if (k.Length == 0) continue;
            var hit = names.FirstOrDefault(n => LooseKey(n) == k);
            if (hit != null) return hit;
        }

        // 三级：唯一包含（「张三」↔「张三丰」不行，必须只有一个命中）
        foreach (var c in candidates)
        {
            string k = LooseKey(c);
            if (k.Length < 2) continue;
            var hits = names.Where(n => LooseKey(n).Contains(k, StringComparison.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0];
        }
        return null;
    }

    /// <summary>宽松键：去括号内容、去空白、去「老师 / 班主任」这类后缀。</summary>
    internal static string LooseKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(raw, @"[\(（][^）)]*[\)）]", "");
        s = s.Replace(" ", "").Replace("　", "").Replace("老师", "").Replace("班主任", "");
        return s.Trim();
    }
}
