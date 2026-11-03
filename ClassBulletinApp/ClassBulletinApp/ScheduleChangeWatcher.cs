using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 「课表有变化」监视器：定时比对当前显示对象（班级 / 教师 + 显示日）的今日课表，
/// 一旦内容变了（手动调课、导入临时表、同步更新包、更新课表）就提醒。
///
/// <para><b>为什么用比对而不是在各处更新后调用：</b>课表的来源太多（手改 / 导入临时表 /
/// 远端包 / 更新课表 / 单班导入），任何一处漏挂就少一次提醒；比对只看"结果变了没有"，
/// 天然覆盖全部来源，也不会因为"导入了但内容一样"而误报。</para>
///
/// <para><b>三种节奏：</b>状态栏红字立即挂上（保留 N 分钟）；弹窗默认立即弹；
/// 变化发生在<b>上课时间内</b>则延后到该节下课 + N 分钟，免得打断课堂教学。</para>
/// </summary>
public static class ScheduleChangeWatcher
{
    private sealed record Slot(int Period, TimeSpan Start, TimeSpan End);

    private static DispatcherTimer? _timer;
    private static DispatcherTimer? _debounce;

    /// <summary>是否已拿到第一份基线（首次运行 / 刚切换对象时只记录，不提醒）。</summary>
    private static bool _primed;

    /// <summary>当前基线对应的「显示对象 + 显示日」。</summary>
    private static string _key = "";

    /// <summary>当前基线：节次 → 「科目|教师」。</summary>
    private static Dictionary<int, string> _slots = new();

    /// <summary>待弹时间（上课中的变化会推后到这里）；MinValue = 没有待弹。</summary>
    private static DateTime _pendingAt = DateTime.MinValue;

    /// <summary>待弹的变化描述（多次变化会累加）。</summary>
    private static readonly List<string> Pending = new();

    private static ReminderPopup? _popup;
    private static FullScreenReminderWindow? _full;

    public static void Start()
    {
        if (_timer != null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // 课表保存后立刻再比一次，不用干等下一个 12 秒；去抖避免批量调课时连比十几次
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce?.Stop(); Tick(); };
        AppEvents.ScheduleChanged += OnScheduleChanged;
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _debounce?.Stop();
        _debounce = null;
        AppEvents.ScheduleChanged -= OnScheduleChanged;
    }

    private static void OnScheduleChanged()
    {
        if (_debounce == null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private static void Tick()
    {
        try
        {
            var s = Store.LoadSettings();
            var r = s.Reminder ?? new ReminderSettings();


            // ① 关掉开关时：丢弃还没弹的变化，只维护基线
            //    （否则用户刚关掉提醒，之前攒下的那条还会弹出来）
            if (!r.ChangeEnabled)
            {
                _pendingAt = DateTime.MinValue;
                Pending.Clear();

                Sample(s, out string k0, out var v0, out _);
                _key = k0;
                _slots = v0;
                _primed = true;
                return;
            }

            // ② 到点就把攒下的变化弹出来（上课中检测到的会推到下课后）
            if (_pendingAt != DateTime.MinValue && DateTime.Now >= _pendingAt) Flush(r);

            if (!Sample(s, out string key, out var slots, out var times)) return;

            // ③ 首次 / 切班 / 跨天：只建立基线
            if (!_primed || key != _key)
            {
                _key = key;
                _slots = slots;
                _primed = true;
                return;
            }

            // ④ 比对：只有同一「班级 + 日期」下内容变了才算变化
            var diffs = Diff(_slots, slots);
            _slots = slots;
            if (diffs.Count == 0) return;

            foreach (var d in diffs)
                if (!Pending.Contains(d)) Pending.Add(d);

            // 状态栏红字：立刻挂上，让不在电脑前也能回来看到
            AppEvents.RaiseScheduleFlash(Summary(Pending), Math.Max(1, r.ChangeStatusMinutes));

            // 弹窗时间：上课中 → 该节下课 + N 分钟；N=0 或不在上课时间 → 立刻
            var now = DateTime.Now;
            var at = now;
            if (r.ChangeDelayMinutes > 0)
            {
                var lessonEnd = CurrentLessonEnd(times, now);
                if (lessonEnd.HasValue) at = lessonEnd.Value.AddMinutes(r.ChangeDelayMinutes);
            }

            if (_pendingAt == DateTime.MinValue || at < _pendingAt) _pendingAt = at;
            if (_pendingAt <= DateTime.Now) Flush(r);
        }
        catch
        {
            // 监视器出问题绝不能影响主程序
        }
    }

    /// <summary>把待弹的变化弹出来（小窗或全屏），并清空队列。</summary>
    private static void Flush(ReminderSettings r)
    {
        _pendingAt = DateTime.MinValue;
        if (Pending.Count == 0) return;

        string summary = Summary(Pending);
        string detail = string.Join("；", Pending.Take(8));
        if (Pending.Count > 8) detail += $"　等 {Pending.Count} 处";
        Pending.Clear();

        int sec = Math.Clamp(r.ChangeSeconds, 1, 60);

        try
        {
            if (r.PlaySound) System.Media.SystemSounds.Exclamation.Play();
            if (r.ChangeVoice)
                VoiceSpeaker.Speak("课表有变化，" + summary, r.VoiceName, r.VoiceRate, r.VoiceVolume);

            if (r.ChangeFullScreen)
            {
                _full ??= new FullScreenReminderWindow();
                _full.ShowReminder("课表已更新", detail, sec);
            }
            else
            {
                _popup ??= new ReminderPopup();
                _popup.ShowReminder("课表有变化", detail, sec, r);
            }
        }
        catch
        {
            // 弹窗失败静默忽略
        }
    }

    /// <summary>把变化清单压成一句话（状态栏 / 语音用），最多列三处。</summary>
    private static string Summary(List<string> changes)
    {
        if (changes.Count == 0) return "";
        string head = string.Join("　", changes.Take(3));
        return changes.Count > 3 ? $"{head} 等 {changes.Count} 处" : head;
    }

    /// <summary>
    /// 取当前显示对象的今日课表：键 =「班级/教师 + 显示日」，值 = 各节「科目|教师」。
    /// 顺带带出可提醒的节次时间，用于判断"现在是不是在上课"。
    /// </summary>
    private static bool Sample(AppSettings s, out string key, out Dictionary<int, string> slots, out List<Slot> times)
    {
        key = "";
        slots = new Dictionary<int, string>();
        times = new List<Slot>();

        bool teacher = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
        var schedule = teacher ? Store.LoadTeacherSchedule() : Store.LoadSchedule();
        string name = teacher ? s.SelectedTeacher : s.SelectedClass;
        if (schedule == null || string.IsNullOrEmpty(name)) return false;

        var displayDate = BoardBuilder.ResolveDisplayDate(s);
        List<LessonOverride>? viewOverrides = null;
        if (teacher)
            viewOverrides = TeacherOverrideApplier.BuildTeacherOverrides(schedule, name, Store.LoadOverrides());

        var res = BoardBuilder.Build(name, s.SemesterStartMonday, schedule, displayDate, viewOverrides);
        key = (teacher ? "T:" : "C:") + name + "|" + displayDate.ToString("yyyy-MM-dd");

        foreach (var row in res.Rows)
        {
            if (row.Period <= 0) continue;

            var lesson = row.Lessons.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Subject));
            slots[row.Period] = lesson == null
                ? ""
                : lesson.Subject.Trim() + "|" + (lesson.Teacher ?? "").Trim();

            // 节次时间（用于"现在是否在上课"），格式与上下课提醒一致：HH:mm-HH:mm
            var parts = (row.TimeRange ?? "").Split('-');
            if (parts.Length == 2
                && TimeSpan.TryParse(parts[0].Trim(), out var st)
                && TimeSpan.TryParse(parts[1].Trim(), out var en) && en > st)
                times.Add(new Slot(row.Period, st, en));
        }
        return true;
    }

    /// <summary>找出当前时间所在节的结束时刻；不在上课时间返回 null。</summary>
    private static DateTime? CurrentLessonEnd(List<Slot> times, DateTime now)
    {
        var t = now.TimeOfDay;
        foreach (var sl in times)
            if (t >= sl.Start && t < sl.End)
                return now.Date + sl.End;
        return null;
    }

    /// <summary>比对两份课表，产出可读的变化描述（如「第3节 数学 → 英语」）。</summary>
    private static List<string> Diff(Dictionary<int, string> before, Dictionary<int, string> after)
    {
        var list = new List<string>();
        var keys = new SortedSet<int>(before.Keys);
        keys.UnionWith(after.Keys);

        foreach (var p in keys)
        {
            before.TryGetValue(p, out var o);
            after.TryGetValue(p, out var n);
            o ??= "";
            n ??= "";
            if (o == n) continue;

            var os = o.Split('|');
            var ns = n.Split('|');
            string oSub = os.Length > 0 ? os[0] : "";
            string oTch = os.Length > 1 ? os[1] : "";
            string nSub = ns.Length > 0 ? ns[0] : "";
            string nTch = ns.Length > 1 ? ns[1] : "";

            if (oSub == nSub)
            {
                // 同一科换老师（代课换人）
                if (string.IsNullOrEmpty(oSub)) continue;
                list.Add($"第{p}节 {oSub} {(oTch.Length == 0 ? "（空）" : oTch)} → {(nTch.Length == 0 ? "（空）" : nTch)}");
            }
            else if (string.IsNullOrEmpty(oSub))
            {
                list.Add($"第{p}节 新增 {nSub}");
            }
            else if (string.IsNullOrEmpty(nSub))
            {
                list.Add($"第{p}节 取消 {oSub}");
            }
            else
            {
                list.Add($"第{p}节 {oSub} → {nSub}");
            }
        }
        return list;
    }
}
