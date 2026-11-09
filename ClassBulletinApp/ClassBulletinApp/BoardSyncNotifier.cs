using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using System.Windows.Threading;
using Wpf = System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 教室机轮询摆渡目录 + 课间提示。
/// <para>★ 上课期间只静默入档，绝不打断课堂；课间 / 放学后合并成一张提示卡。</para>
/// <para>★ 教师机不弹窗（它收到的是回执，不是新变更单）；应用仍须电教管理员在信息管理器里点确认。</para>
/// <para>★ <b>所有共享盘 / FTP 操作都在后台线程</b>：轮询与快照写各占一个线程池线程，
/// 共享盘卡住时只有那一轮变慢，界面照旧能点、能拖、能切页。早先这些都跑在
/// DispatcherTimer 的 UI 线程上 —— 那正是「操作有时候卡顿」的根因。</para>
/// </summary>
public static class BoardSyncNotifier
{
    static DispatcherTimer? _timer;
    static DispatcherTimer? _snapshotTimer;
    static BoardSyncPopup? _popup;
    static FullScreenReminderWindow? _full;

    /// <summary>轮询防重入：上一轮还没回来就不再发起新的（定时器到点也不排队）。</summary>
    static int _ticking;

    /// <summary>快照发布防重入：内容连续改几次只写一次。</summary>
    static int _snapping;

    /// <summary>
    /// 上课时间收到、但发送方要求「课间播」的语音提醒文本。
    /// <para>先排队，下一轮轮询发现已下课再一起念 —— 绝不为了念一条作业把课堂打断。</para>
    /// </summary>
    static readonly List<string> _pendingVoice = new();

    public static void Start()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = Interval() };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }
        HookContentChanged();
    }

    /// <summary>
    /// 教室机本地改了作业 / 缺交 / 通知后，1.2s 防抖刷新内容快照，
    /// 让教师机尽快看到「那边现在有什么」。绝不在这里重绘壁纸、绝不碰课表事件。
    /// </summary>
    static void HookContentChanged()
    {
        if (_snapshotTimer != null) return;
        _snapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _snapshotTimer.Tick += (_, _) =>
        {
            _snapshotTimer!.Stop();
            if (!BoardSyncService.IsEnabled || !BoardSyncService.IsClassroomMachine) return;
            if (Interlocked.CompareExchange(ref _snapping, 1, 0) != 0) return;
            // 写共享盘放到后台：保存完作业那一下绝不能卡在 UI 线程等网络
            Task.Run(() =>
            {
                try { BoardSyncService.PublishSnapshot(force: true); }
                catch (Exception ex) { Debug.WriteLine("[BoardSync snapshot] " + ex.Message); }
                finally { Interlocked.Exchange(ref _snapping, 0); }
            });
        };
        void Bump()
        {
            if (_snapshotTimer == null) return;
            _snapshotTimer.Stop();
            _snapshotTimer.Start();
        }
        AppEvents.HomeworkChanged += Bump;
        AppEvents.MissingChanged += Bump;
        AppEvents.NotificationChanged += Bump;
    }

    static TimeSpan Interval()
        => TimeSpan.FromSeconds(Math.Max(15, Math.Min(600, Store.LoadSettings().BoardSyncPollSeconds)));

    /// <summary>设置里改了轮询秒数后立刻生效，不用重启。</summary>
    public static void RefreshInterval()
    {
        if (_timer == null) { Start(); return; }
        _timer.Interval = Interval();
    }

    public static void CheckNow() => Tick();

    /// <summary>发起一轮轮询：立刻返回，真正的网络 I/O 在后台线程跑。</summary>
    static void Tick()
    {
        try
        {
            if (!BoardSyncService.IsEnabled) return;    // 未启用 / 未配数据源：完全不碰共享盘
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try
                {
                    var (added, _, _) = BoardSyncService.Pull(BoardSyncService.SubscribedClasses());
                    var forces = BoardSyncService.LastPullForce.ToList();   // 后台线程上取快照
                    var voices = BoardSyncService.LastPullVoice.ToList();
                    int autoApplied = BoardSyncService.LastPullAutoApplied;
                    // ★ 即使没有新消息也要回到 UI 线程：上课时间排队的那几句语音，
                    //   要靠「下课后的第一轮轮询」念出来（added=0 时同样要跑 OnPulled）。
                    Dispatch(() => OnPulled(added, forces, voices, autoApplied));
                }
                catch (Exception ex)
                {
                    // 轮询失败绝不能让托盘程序崩掉；下个周期自然重试
                    Debug.WriteLine("[BoardSync] " + ex.Message);
                }
                finally { Interlocked.Exchange(ref _ticking, 0); }
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _ticking, 0);
            Debug.WriteLine("[BoardSync] " + ex.Message);
        }
    }

    static void Dispatch(Action a)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        try { app.Dispatcher.BeginInvoke(a); } catch { /* 正在退出 */ }
    }

    /// <summary>
    /// 回到 UI 线程后再决定弹什么 / 念什么：上课时间不弹窗、不念「课间播」的语音，只入档。
    /// <para>语音与弹窗是两条独立的路：教室机可能关掉了弹提示卡，但仍希望把通知念出来。</para>
    /// </summary>
    static void OnPulled(int added, List<string> forces,
                         List<(string Text, bool Immediate)> voices, int autoApplied = 0)
    {
        try
        {
            var s = Store.LoadSettings();

            // 自动增加是在后台轮询里直接写盘的（没走台账窗口），这里回 UI 线程补发内容 Changed：
            // 通知条 / 桌面背景 / 教室机快照才会立刻跟上，而不是等到下次手动刷新。
            if (autoApplied > 0 && UserSession.IsStudent)
            {
                AppEvents.RaiseNotificationChanged();
                AppEvents.RaiseBackdropChanged();
            }

            // 上课时间静默入档；周末 / 无课表时 IsClassTimeNow 返回 false，按课间处理
            bool inClass = WeekHelper.IsClassTimeNow(Store.LoadSchedule(), s.SelectedClass);

            // ① 语音播报（只有教室机念；教师机收到的是回执，不吵）
            if (UserSession.IsStudent && s.BoardSyncVoiceReminder)
            {
                var speak = new List<string>();
                foreach (var (text, immediate) in voices)
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (immediate || !inClass) speak.Add(text);
                    else
                    {
                        // 上课时间先排队，下课再念；最多留 6 条，免得连上一天攒成一长串
                        _pendingVoice.Add(text);
                        if (_pendingVoice.Count > 6) _pendingVoice.RemoveAt(0);
                    }
                }
                if (!inClass && _pendingVoice.Count > 0)
                {
                    speak.AddRange(_pendingVoice);
                    _pendingVoice.Clear();
                }
                if (speak.Count > 0) SpeakNow(string.Join("；", speak), s);
            }
            else if (!s.BoardSyncVoiceReminder)
            {
                _pendingVoice.Clear();      // 关了语音就把排队的丢掉，别等再打开时念一堆陈年旧事
            }

            // ② 弹窗（可整体关闭；教师机不弹）
            if (!s.BoardSyncPopupDuringBreak) return;
            if (!UserSession.IsStudent) return;
            if (inClass) return;

            // 发送方要求「强提醒」→ 先整屏大字提示一次（比右下角小卡片显眼得多）
            if (forces.Count > 0 && s.BoardSyncFullScreenReminder) ShowFullScreen(forces, s);

            // 右下角合并卡片照旧：告诉管理员「有几条待确认」并给入口
            if (_popup != null && _popup.IsVisible) { _popup.Bump(added, autoApplied); return; }
            if (added <= 0) return;
            _popup = new BoardSyncPopup();
            _popup.Closed += (_, _) => _popup = null;
            _popup.Bump(added, autoApplied);
            _popup.Show();          // 非模态，不会像 ShowDialog 那样禁用其它顶层窗口
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[BoardSync popup] " + ex.Message);
        }
    }

    /// <summary>用系统语音念一句。音色 / 语速 / 音量沿用「提醒设置」；任何失败都静默（绝不挡住弹窗）。</summary>
    static void SpeakNow(string text, AppSettings s)
    {
        try
        {
            var r = s.Reminder ?? new ReminderSettings();
            VoiceSpeaker.Speak(text, r.VoiceName, r.VoiceRate, r.VoiceVolume);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[BoardSync voice] " + ex.Message);
        }
    }

    /// <summary>课间全屏提醒：复用「课表变化提醒」的整屏窗与外观，只是换个标签与文案。</summary>
    static void ShowFullScreen(List<string> forces, AppSettings s)
    {
        try
        {
            if (_full == null)
            {
                _full = new FullScreenReminderWindow();
                _full.Closed += (_, _) => _full = null;
            }

            string title = forces.Count == 1
                ? forces[0]
                : $"有 {forces.Count} 条信息需要全班注意";
            string detail = forces.Count == 1
                ? "教师机发来一条需要当场提醒的信息，请电教管理员在信息管理器里确认后应用。"
                : "分别是：" + string.Join("；", forces.Take(3))
                  + (forces.Count > 3 ? $"……（共 {forces.Count} 条）" : "")
                  + "\n请电教管理员在信息管理器里逐条确认后应用。";

            int sec = Math.Clamp(s.Reminder?.ChangeSeconds ?? 15, 5, 60);
            _full.ShowReminder(title, detail, sec, s.Reminder, "班级信息");
        }
        catch (Exception ex)
        {
            _full = null;      // 窗被关掉 / 出错就重造一个，别让强提醒从此失效
            Debug.WriteLine("[BoardSync fullscreen] " + ex.Message);
        }
    }
}

/// <summary>课间提示卡：右下角、置顶、多条合并成一个数字，点开才进信息管理器。</summary>
public class BoardSyncPopup : Window
{
    readonly Wpf.TextBlock _text = new()
    {
        FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
    };
    int _count;
    int _autoCount;

    public BoardSyncPopup()
    {
        Title = "班级内容变更单";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB));

        var work = SystemParameters.WorkArea;
        Left = Math.Max(0, work.Right - Width - 18);
        Top = Math.Max(0, work.Bottom - 210);

        var root = new Wpf.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "📨 收到班级内容变更单", FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x55)),
            Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(_text);

        var bar = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var open = new Wpf.Button { Content = "打开信息管理器", Padding = new Thickness(12, 6, 12, 6) };
        open.Click += (_, _) =>
        {
            var w = new BoardSyncWindow();
            w.Show();
            Close();
        };
        var later = new Wpf.Button { Content = "稍后处理", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        later.Click += (_, _) => Close();
        bar.Children.Add(open);
        bar.Children.Add(later);
        root.Children.Add(bar);
        Content = root;
    
        GlassChrome.Prepare(this);}

    public void Bump(int n, int autoApplied = 0)
    {
        _count += Math.Max(0, n);
        _autoCount += Math.Max(0, autoApplied);
        int pending = Math.Max(0, _count - _autoCount);
        _text.Text = pending > 0
            ? $"共有 {pending} 条来自教师机的变更单待确认。\n" +
              "上课期间已静默入档；请由电教管理员在课间逐条选择「增加 / 替换 / 忽略」。"
            : $"收到 {_autoCount} 条通知，教室机已按发送方指定自动增加（无需确认）。\n" +
              "内容已落地，可在「消息台账 / 回执」里核对或撤回。";
        if (pending > 0 && _autoCount > 0)
            _text.Text += $"\n另有 {_autoCount} 条通知已自动增加。";
    }
}
