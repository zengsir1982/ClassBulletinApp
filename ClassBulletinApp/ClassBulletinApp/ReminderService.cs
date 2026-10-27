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

            void Fire(string kind, int period, string title, string detail)
            {
                string key = $"{dateKey}|{kind}|{period}";
                lock (Fired)
                {
                    if (!Fired.Add(key)) return;
                }
                Show(title, detail, shown, r.PlaySound);
            }

            foreach (var sl in slots)
            {
                if (r.BeforeClassMinutes > 0 &&
                    t >= sl.Start - TimeSpan.FromMinutes(r.BeforeClassMinutes) && t < sl.Start)
                    Fire("prep", sl.Period, $"即将上课 · 第{sl.Period}节",
                        $"{sl.Subject}　{sl.Start:hh\\:mm} 开始");

                if (t >= sl.Start && t < sl.Start + TimeSpan.FromMinutes(1))
                    Fire("on", sl.Period, $"上课 · 第{sl.Period}节", sl.Subject);

                if (r.BeforeEndMinutes > 0 &&
                    t >= sl.End - TimeSpan.FromMinutes(r.BeforeEndMinutes) && t < sl.End)
                    Fire("offpre", sl.Period, $"即将下课 · 第{sl.Period}节",
                        $"{sl.Subject}　{sl.End:hh\\:mm} 下课");

                if (t >= sl.End && t < sl.End + TimeSpan.FromMinutes(1))
                    Fire("off", sl.Period, "课间休息", $"第{sl.Period}节 {sl.Subject} 已下课");
            }

            if (r.AfterSchoolRemind)
            {
                var last = slots.Last();
                if (t >= last.End && t < last.End + TimeSpan.FromMinutes(10))
                    Fire("after", last.Period, "放学了", "今天课程全部结束");
            }
        }
        catch
        {
            // 提醒失败不影响主程序
        }
    }

    private static void Show(string title, string detail, int seconds, bool sound)
    {
        try
        {
            if (sound) System.Media.SystemSounds.Exclamation.Play();
            if (_popup == null) _popup = new ReminderPopup();
            _popup.ShowReminder(title, detail, seconds);
        }
        catch
        {
            // 弹窗失败静默忽略
        }
    }
}
