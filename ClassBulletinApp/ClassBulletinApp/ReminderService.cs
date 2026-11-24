using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 上下课提醒服务（参照 ClassIsland 的课前 / 上课 / 下课 / 放学提醒）：
/// 每 15 秒检查一次当前显示对象的今日课表（含临时课表的调 / 代安排与调休显示日），
/// 到点弹出置顶提醒窗。同一提醒每天只触发一次。
/// </summary>
public static class ReminderService
{
    private static DispatcherTimer? _timer;
    private static ReminderPopup? _popup;
    private static readonly HashSet<string> Fired = new();

    public static void Start()
    {
        if (_timer != null) return;
        AppEvents.ScheduleChanged += Reset;   // 课表 / 显示对象变化后允许重新判定
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private static void Reset()
    {
        lock (Fired) Fired.Clear();
    }

    /// <summary>
    /// 提醒对象的一句话描述（「提醒设置」页顶部那行用）。
    /// <para>与 <see cref="Check"/> 同一套判定：教师模式读教师个人课表，否则读当前显示班级。
    /// 老师说「为什么提醒的是这个班」时，看一眼这行就清楚了。</para>
    /// </summary>
    public static string DescribeTarget()
    {
        try
        {
            var s = Store.LoadSettings();
            if (string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(s.SelectedTeacher)
                    ? "我的课表（还没选教师）"
                    : $"我的课表 · {s.SelectedTeacher}";
            return string.IsNullOrWhiteSpace(s.SelectedClass)
                ? "未选择班级（不会提醒）"
                : $"班级课表 · {s.SelectedClass}";
        }
        catch { return ""; }
    }

    private sealed record Slot(int Period, TimeSpan Start, TimeSpan End, string Subject, string ClassName);

    /// <summary>
    /// 从今天的课表行里挑出「真的要提醒的节次」。
    /// <para>★ 教师个人课表里「这节我没课」的行有节次时间但没有课节，必须跳过 ——
    /// 以前照收，老师会收到一串「第 2 节 ·」的空提醒。班级课表每节都有课，所以这只在教师模式暴露。</para>
    /// </summary>
    private static List<Slot> CollectSlots(TodayResult res, bool teacher)
    {
        var slots = new List<Slot>();
        foreach (var row in res.Rows)
        {
            // 固定时段（大课间 / 午休）不是课；Period=0 同理
            if (row.IsFixed || row.Period <= 0 || string.IsNullOrWhiteSpace(row.TimeRange)) continue;
            var parts = row.TimeRange.Split('-');
            if (parts.Length != 2) continue;
            if (!TimeSpan.TryParse(parts[0].Trim(), out var st)) continue;
            if (!TimeSpan.TryParse(parts[1].Trim(), out var en)) continue;

            var lesson = row.Lessons.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.DisplaySubject));
            if (lesson == null) continue;

            // 教师视角 Lesson.Teacher 存的是所教班级（TeacherExtractor），正好给 {c} 用
            string cls = teacher ? (lesson.Teacher ?? "") : "";
            slots.Add(new Slot(row.Period, st, en, lesson.DisplaySubject, cls));
        }
        return slots;
    }

    /// <summary>
    /// 诊断用：按给定课表算出「会提醒的节次」，每项形如 <c>科目|班级</c>（不弹窗、不发通知）。
    /// <para>★ 必须由调用方显式传课表与姓名：诊断模式下设置不落盘，读 Store 会读到旧值，
    /// 那验的就不是教师模式了。</para>
    /// </summary>
    internal static List<string> DiagSlots(ScheduleFile? schedule, string? name, bool teacher, DateTime when)
    {
        if (schedule == null || string.IsNullOrEmpty(name)) return new List<string>();
        try
        {
            List<LessonOverride>? ov = teacher
                ? TeacherOverrideApplier.BuildTeacherOverrides(schedule, name, Store.LoadOverrides())
                : null;
            var res = BoardBuilder.Build(name, Store.LoadSettings().SemesterStartMonday, schedule, when.Date, ov,
                                         showAlternateWeek: false);
            return CollectSlots(res, teacher).Select(x => x.Subject + "|" + x.ClassName).ToList();
        }
        catch { return new List<string>(); }
    }

    private static void Check()
    {
        try
        {
            var s = Store.LoadSettings();
            var r = s.Reminder ?? new ReminderSettings();
            if (!r.Enabled) return;

            bool teacher = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
            var schedule = teacher ? Store.LoadTeacherSchedule() : Store.LoadSchedule();
            string name = teacher ? s.SelectedTeacher : s.SelectedClass;
            if (schedule == null || string.IsNullOrEmpty(name)) return;

            var displayDate = BoardBuilder.ResolveDisplayDate(s);
            List<LessonOverride>? viewOverrides = null;
            if (teacher)
                viewOverrides = TeacherOverrideApplier.BuildTeacherOverrides(schedule, name, Store.LoadOverrides());

            var res = BoardBuilder.Build(name, s.SemesterStartMonday, schedule, displayDate, viewOverrides,
                                         showAlternateWeek: false);
            // r133 校历：放假不提醒；调休补课（周六上 Wednesday 的课）照常提醒
            var plan = res.Plan ?? SchoolCalendar.Resolve(schedule, displayDate, s.SemesterStartMonday);
            if (plan.IsOff) return;
            if (plan.DayIdx > 4) return;   // 周末不提醒

            var slots = CollectSlots(res, teacher);
            if (slots.Count == 0) return;
            slots.Sort((a, b) => a.Period.CompareTo(b.Period));

            var now = DateTime.Now;
            string dateKey = now.Date.ToString("yyyyMMdd");
            var t = now.TimeOfDay;
            int shown = Math.Max(3, r.DisplaySeconds);
            var oneMin = TimeSpan.FromMinutes(1);
            var tenMin = TimeSpan.FromMinutes(10);

            string Fill(string tpl, int p, string subj, string time, string cls)
            {
                if (string.IsNullOrEmpty(tpl)) return "";
                return tpl.Replace("{p}", p.ToString())
                         .Replace("{s}", subj)
                         .Replace("{t}", time)
                         .Replace("{c}", cls);
            }

            // 触发某一类提醒；lead>0 在「事件时间 - lead」提醒，lead=0 在事件发生的时刻提醒
            void Fire(string kind, int period, ReminderProfile p, TimeSpan eventTime, string subj, string cls, TimeSpan windowEnd)
            {
                if (p == null || !p.Enabled) return;
                string key = $"{dateKey}|{kind}|{period}";
                lock (Fired)
                {
                    if (!Fired.Add(key)) return;
                }
                string title = Fill(p.Title, period, subj, $"{eventTime:hh\\:mm}", cls);
                string detail = Fill(p.Detail, period, subj, $"{eventTime:hh\\:mm}", cls);
                string spoken = Fill(p.VoiceText, period, subj, $"{eventTime:hh\\:mm}", cls);
                string danmaku = Fill(p.Danmaku, period, subj, $"{eventTime:hh\\:mm}", cls);
                Show(title, detail, shown, r, spoken);
                if (!string.IsNullOrWhiteSpace(danmaku))
                    AppEvents.RaiseReminderDanmaku(danmaku, shown);
            }

            foreach (var sl in slots)
            {
                // 上课提醒
                var cs = r.ClassStart;
                if (cs.Enabled)
                {
                    int lead = Math.Max(0, cs.LeadSeconds);
                    var fireAt = lead > 0 ? sl.Start - TimeSpan.FromSeconds(lead) : sl.Start;
                    var winEnd = lead > 0 ? sl.Start : sl.Start + oneMin;
                    if (t >= fireAt && t < winEnd)
                        Fire("cs", sl.Period, cs, sl.Start, sl.Subject, sl.ClassName, winEnd);
                }

                // 下课提醒
                var ce = r.ClassEnd;
                if (ce.Enabled)
                {
                    int lead = Math.Max(0, ce.LeadSeconds);
                    var fireAt = lead > 0 ? sl.End - TimeSpan.FromSeconds(lead) : sl.End;
                    var winEnd = lead > 0 ? sl.End : sl.End + oneMin;
                    if (t >= fireAt && t < winEnd)
                        Fire("ce", sl.Period, ce, sl.End, sl.Subject, sl.ClassName, winEnd);
                }
            }

            // 放学提醒（以当天最后一节结束时间为事件时间）
            var asP = r.AfterSchool;
            if (asP.Enabled)
            {
                var last = slots.Last();
                int lead = Math.Max(0, asP.LeadSeconds);
                var fireAt = lead > 0 ? last.End - TimeSpan.FromSeconds(lead) : last.End;
                var winEnd = lead > 0 ? last.End : last.End + tenMin;
                if (t >= fireAt && t < winEnd)
                    Fire("as", last.Period, asP, last.End, last.Subject, last.ClassName, winEnd);
            }
        }
        catch
        {
            // 提醒失败不影响主程序
        }
    }

    private static void Show(string title, string detail, int seconds, ReminderSettings r, string spoken)
    {
        try
        {
            if (r.PlaySound) System.Media.SystemSounds.Exclamation.Play();
            if (r.VoiceEnabled)
                VoiceSpeaker.Speak(spoken, r.VoiceName, r.VoiceRate, r.VoiceVolume);
            if (_popup == null) _popup = new ReminderPopup();
            _popup.ShowReminder(title, detail, seconds, r);
        }
        catch
        {
            // 弹窗失败静默忽略
        }
    }
}
