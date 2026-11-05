using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 「课表有变化」监视器：定时比对当前显示对象（班级 / 教师 + 显示日）的今日课表，
/// 一旦内容变了（导入临时表、同步更新包、更新课表）就提醒。
///
/// <para><b>为什么用比对而不是在各处更新后调用：</b>课表的来源太多（手改 / 导入临时表 /
/// 远端包 / 更新课表 / 单班导入），任何一处漏挂就少一次提醒；比对只看"结果变了没有"，
/// 天然覆盖全部来源，也不会因为"导入了但内容一样"而误报。</para>
///
/// <para><b>手动调课不提醒：</b>老师自己拖课、编辑窗保存、撤销，都会由
/// <see cref="Store.LocalManualEdit"/> 把那几格标成"自己改的"，本监视器只更新基线，
/// 不弹窗、不播报、不挂状态栏红字 —— 自己刚动完手再被告知"课表有变化"只会让人以为出错。
/// 同一格随后又被外部来源覆盖时，<see cref="Store.ExternalEdit"/> 会清掉标记，照旧提醒。</para>
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

    /// <summary>
    /// 本机手动调课刚碰过的格子（<c>班级|星期|节次</c> → 失效时刻）。
    /// <para>这类变化是老师自己动手造成的，不该再弹窗/播报/挂红字告诉他"课表有变化"；
    /// 但基线必须照常更新，否则下一次比对会把同一个变化再算一遍。</para>
    /// </summary>
    private static readonly Dictionary<string, DateTime> _manualCells = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>手动格标记的有效期：覆盖"保存 → 去抖 250ms → 比对"这段窗口即可，不必太长。</summary>
    private const int ManualCellTtlSeconds = 30;

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
        Store.LocalManualEdit += OnLocalManualEdit;
        Store.ExternalEdit += OnExternalEdit;
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _debounce?.Stop();
        _debounce = null;
        AppEvents.ScheduleChanged -= OnScheduleChanged;
        Store.LocalManualEdit -= OnLocalManualEdit;
        Store.ExternalEdit -= OnExternalEdit;
    }

    /// <summary>手动调课写盘了：把那几格记下来，下次比对时跳过它们（见 <see cref="IsManualCell"/>）。</summary>
    private static void OnLocalManualEdit(IReadOnlyCollection<string> cells)
    {
        var until = DateTime.Now.AddSeconds(ManualCellTtlSeconds);
        foreach (var c in cells)
            if (!string.IsNullOrEmpty(c)) _manualCells[c] = until;
    }

    /// <summary>
    /// 外部来源（导入临时表 / 同步包）改了课：把这几格的"手动"标记清掉。
    /// 不清的话，老师 30 秒内先手动调、紧接着又被同步包覆盖时，后面这次真变化会被当成
    /// "自己刚调的"而不再提醒 —— 那才是真正的漏报。
    /// </summary>
    private static void OnExternalEdit(IReadOnlyCollection<string> cells)
    {
        foreach (var c in cells) _manualCells.Remove(c);
    }

    /// <summary>取出仍然有效的手动格并清理过期项。</summary>
    private static HashSet<string> TakeManualCells()
    {
        var now = DateTime.Now;
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _manualCells.Keys.ToList())
        {
            if (_manualCells[k] <= now) _manualCells.Remove(k);
            else alive.Add(k);
        }
        return alive;
    }

    /// <summary>这一节的变化是老师自己刚调出来的吗？（是就只更新基线，不提醒）</summary>
    private static bool IsManualCell(int period, string cellPrefix, HashSet<string> manual)
        => manual.Count > 0 && manual.Contains(cellPrefix + "|" + period);

    /// <summary>诊断用（<c>--notify-check</c>）：把内部状态摊平成一行，看抑制为什么没生效。</summary>
    internal static string DiagState()
        => $"primed={_primed} key={_key} manual=[{string.Join(";", _manualCells.Keys)}] "
         + $"pending=[{string.Join(";", Pending)}] at={(_pendingAt == DateTime.MinValue ? "-" : _pendingAt.ToString("HH:mm:ss"))} "
         + $"slots={_slots.Count}";

    /// <summary>
    /// 诊断用（<c>--change-check</c>）：注入一批"手动格"，再问某节会不会被跳过。
    /// 这里最容易错的是键的拼法（<c>班级|星期|节次</c>），拼错就整条抑制失效，所以要单独验。
    /// </summary>
    internal static bool DiagManualSkip(string cellPrefix, int period, IEnumerable<string> cells)
    {
        OnLocalManualEdit(new List<string>(cells));
        return IsManualCell(period, cellPrefix, TakeManualCells());
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

                Sample(s, out string k0, out var v0, out _, out _);
                _key = k0;
                _slots = v0;
                _primed = true;
                return;
            }

            // ② 到点就把攒下的变化弹出来（上课中检测到的会推到下课后）
            if (_pendingAt != DateTime.MinValue && DateTime.Now >= _pendingAt) Flush(r);

            if (!Sample(s, out string key, out var slots, out var times, out string cellPrefix)) return;

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

            // ④' 去掉「老师自己刚手动调的那几格」：自己动手调完再弹窗告知一遍毫无意义。
            //     只跳过手动格，同一批里混进来的外部变化（导入临时表 / 同步包）照旧提醒。
            var manual = TakeManualCells();
            if (manual.Count > 0)
                diffs = diffs.Where(d => !IsManualCell(d.Period, cellPrefix, manual)).ToList();
            if (diffs.Count == 0) return;

            foreach (var d in diffs)
                if (!Pending.Contains(d.Text)) Pending.Add(d.Text);

            // 状态栏红字：立刻挂上，让不在电脑前也能回来看到
            AppEvents.RaiseScheduleFlash(Summary(Pending), Math.Max(1, r.ChangeStatusMinutes));

            // 弹窗时间：上课中 → 该节下课 + N 分钟；N=0 或不在上课时间 → 立刻
            var now = DateTime.Now;
            var at = now;
            if (r.ChangeDelayMinutes > 0)
            {
                var lessonEnd = CurrentLessonEnd(times, now);
                if (lessonEnd.HasValue)
                {
                    // 明确在上课中：延后到该节下课 + N 分钟
                    at = lessonEnd.Value.AddMinutes(r.ChangeDelayMinutes);
                }
                else if (times.Count == 0)
                {
                    // 课表拿不到节次时间（Excel 未带时间 / 数据暂缺）：无法判断是否上课，
                    // 保守延后 N 分钟 —— 宁可课间晚一点提示，也不要在课堂上突然弹窗。
                    at = now.AddMinutes(r.ChangeDelayMinutes);
                }
                // times 非空但当前不在任何节内 = 课间 / 放学后，立即弹（不会打断任何课）
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
                _full.ShowReminder("课表已更新", detail, sec, r);
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

    /// <summary>
    /// 设置页「预览效果」：按当前设置立刻弹一次示例提示，用的是与真实提醒同一批窗口实例，
    /// 所以看到的配色、字号、位置与实际弹出来的一模一样。不写数据、不进待弹队列。
    /// </summary>
    public static void Preview(ReminderSettings r)
    {
        try
        {
            const string title = "课表已更新";
            const string detail = "第3节 数学 → 英语；第5节 体育 → 数学";
            int sec = Math.Clamp(r.ChangeSeconds, 3, 15);

            if (r.ChangeFullScreen)
            {
                _full ??= new FullScreenReminderWindow();
                _full.ShowReminder(title, detail, sec, r);
            }
            else
            {
                _popup ??= new ReminderPopup();
                _popup.ShowReminder("课表有变化", detail, sec, r);
            }
        }
        catch
        {
            // 预览失败不影响设置保存
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
    private static bool Sample(AppSettings s, out string key, out Dictionary<int, string> slots,
        out List<Slot> times, out string cellPrefix)
    {
        key = "";
        cellPrefix = "";
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

        // 与 LessonOverride.CellKey 同构（班级|星期|节次），用来对手动调课标记做精确匹配
        cellPrefix = name + "|" + ((int)displayDate.DayOfWeek + 6) % 7;

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

    /// <summary>
    /// 比对两份课表，产出可读的变化描述（如「第3节 数学 → 英语」）。
    /// 带上节次号，供调用方把"老师自己手动调的那几节"过滤掉。
    /// </summary>
    private static List<(int Period, string Text)> Diff(Dictionary<int, string> before, Dictionary<int, string> after)
    {
        var list = new List<(int Period, string Text)>();
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
                list.Add((p, $"第{p}节 {oSub} {(oTch.Length == 0 ? "（空）" : oTch)} → {(nTch.Length == 0 ? "（空）" : nTch)}"));
            }
            else if (string.IsNullOrEmpty(oSub))
            {
                list.Add((p, $"第{p}节 新增 {nSub}"));
            }
            else if (string.IsNullOrEmpty(nSub))
            {
                list.Add((p, $"第{p}节 取消 {oSub}"));
            }
            else
            {
                list.Add((p, $"第{p}节 {oSub} → {nSub}"));
            }
        }
        return list;
    }
}
