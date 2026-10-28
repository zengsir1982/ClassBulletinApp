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

    private sealed record Slot(int Period, TimeSpan Start, TimeSpan End, string Subject);

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

            var res = BoardBuilder.Build(name, s.SemesterStartMonday, schedule, displayDate, viewOverrides);
            int dayIdx = ((int)displayDate.DayOfWeek + 6) % 7;
            if (dayIdx > 4) return;   // 周末不提醒

            var slots = new List<Slot>();
            foreach (var row in res.Rows)
            {
                if (row.Period <= 0 || string.IsNullOrWhiteSpace(row.TimeRange)) continue;
                var parts = row.TimeRange.Split('-');
                if (parts.Length != 2) continue;
                if (!TimeSpan.TryParse(parts[0].Trim(), out var st)) continue;
                if (!TimeSpan.TryParse(parts[1].Trim(), out var en)) continue;
                var subj = row.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject))?.Subject ?? "";
                slots.Add(new Slot(row.Period, st, en, subj));
            }
            if (slots.Count == 0) return;
            slots.Sort((a, b) => a.Period.CompareTo(b.Period));

            var now = DateTime.Now;
            string dateKey = now.Date.ToString("yyyyMMdd");
            var t = now.TimeOfDay;
            int shown = Math.Max(3, r.DisplaySeconds);
            var oneMin = TimeSpan.FromMinutes(1);
            var tenMin = TimeSpan.FromMinutes(10);

            string Fill(string tpl, int p, string subj, string time)
            {
                if (string.IsNullOrEmpty(tpl)) return "";
                return tpl.Replace("{p}", p.ToString())
                         .Replace("{s}", subj)
                         .Replace("{t}", time);
            }

            // 触发某一类提醒；lead>0 在「事件时间 - lead」提醒，lead=0 在事件发生的时刻提醒
            void Fire(string kind, int period, ReminderProfile p, TimeSpan eventTime, string subj, TimeSpan windowEnd)
            {
                if (p == null || !p.Enabled) return;
                string key = $"{dateKey}|{kind}|{period}";
                lock (Fired)
                {
                    if (!Fired.Add(key)) return;
                }
                string title = Fill(p.Title, period, subj, $"{eventTime:hh\\:mm}");
                string detail = Fill(p.Detail, period, subj, $"{eventTime:hh\\:mm}");
                string spoken = Fill(p.VoiceText, period, subj, $"{eventTime:hh\\:mm}");
                string danmaku = Fill(p.Danmaku, period, subj, $"{eventTime:hh\\:mm}");
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
                        Fire("cs", sl.Period, cs, sl.Start, sl.Subject, winEnd);
                }

                // 下课提醒
                var ce = r.ClassEnd;
                if (ce.Enabled)
                {
                    int lead = Math.Max(0, ce.LeadSeconds);
                    var fireAt = lead > 0 ? sl.End - TimeSpan.FromSeconds(lead) : sl.End;
                    var winEnd = lead > 0 ? sl.End : sl.End + oneMin;
                    if (t >= fireAt && t < winEnd)
                        Fire("ce", sl.Period, ce, sl.End, sl.Subject, winEnd);
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
                    Fire("as", last.Period, asP, last.End, last.Subject, winEnd);
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
            _popup.ShowReminder(title, detail, seconds);
        }
        catch
        {
            // 弹窗失败静默忽略
        }
    }
}
