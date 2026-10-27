using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class NotificationWindow : Window
{
    private AutoHider? _hider;
    private PanelLayout? _layout;
    private ScheduleFile? _schedule;
    private string? _className;
    private bool _clickThrough;
    private DispatcherTimer? _danmakuTimer;

    public NotificationWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCurrentLayout();
        AppEvents.NotificationChanged += StartMarquee;
        AppEvents.LayoutChanged += OnLayoutChanged;
        AppEvents.ReminderDanmaku += OnReminderDanmaku;
        MarqueeHost.SizeChanged += (_, _) => StartMarquee();
        StartMarquee();
    }

    private void ApplyCurrentLayout()
    {
        var s = Store.LoadSettings();
        _layout = s.NotificationLayout;
        // 「置顶显示」= 半透明常驻且完全可交互（双击可编辑、隐藏按钮可单击）；
        // 只有在布局里显式勾选「鼠标穿透」时才打开穿透（此时通知条与隐藏按钮仍保持可点击）。
        bool topShow = !_layout.AutoHide;
        _clickThrough = _layout.ClickThrough && !_layout.AutoHide;
        _schedule = Store.LoadSchedule();
        _className = s.SelectedClass;
        FloatingHelper.ApplyLayout(this, _layout, _clickThrough, topShow ? _layout.OverlayOpacity : _layout.Opacity);
        if (_clickThrough)
            FloatingHelper.SetClickThrough(this, true, BtnHide, Bar);
        else
            FloatingHelper.SetClickThrough(this, false);
        TxtMarquee.FontSize = Math.Max(12, _layout.FontSize);
        BtnHide.Content = _layout.AutoHide ? "置顶显示" : "自动隐藏";
        _hider?.Dispose();
        _hider = (_layout.AutoHide || _layout.AutoHideDuringClass)
            ? new AutoHider(this, _layout, () => _layout.AutoHide || (_layout.AutoHideDuringClass && WeekHelper.IsClassTimeNow(_schedule, _className)))
            : null;
    }

    private void OnLayoutChanged()
    {
        ApplyCurrentLayout();
        StartMarquee();
    }

    // ---------- 提醒弹幕：临时把通知条内容替换为提醒文案，数秒后恢复 ----------
    private void OnReminderDanmaku(string text, int seconds)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(text)) return;
        _danmakuTimer?.Stop();
        TxtMarquee.Text = text;
        TxtMarquee.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = TxtMarquee.DesiredSize.Width;
        double hostW = MarqueeHost.ActualWidth;
        var tt = new TranslateTransform();
        TxtMarquee.RenderTransform = tt;
        tt.X = hostW > 0 ? (hostW - tw) / 2 : 0;   // 弹幕居中显示，不滚动

        _danmakuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(3, seconds)) };
        _danmakuTimer.Tick += (_, _) =>
        {
            _danmakuTimer?.Stop();
            _danmakuTimer = null;
            StartMarquee();   // 恢复为正常通知滚动
        };
        _danmakuTimer.Start();
    }

    // ---------- 双击编辑通知（穿透态下通知条本身仍保持可交互） ----------
    private void Bar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            var w = new NotificationEditWindow { Owner = this, Topmost = true };
            w.ShowDialog();
            e.Handled = true;
        }
    }

    // ---------- 隐藏按钮：快速切换自动隐藏 ----------
    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        bool turnOn = !s.NotificationLayout.AutoHide;
        s.NotificationLayout.AutoHide = turnOn;
        if (turnOn) s.NotificationLayout.ClickThrough = false;   // 自动隐藏与鼠标穿透互斥
        Store.SaveSettings(s);
        ApplyCurrentLayout();
        AppEvents.RaiseLayoutChanged();
    }

    private void StartMarquee()
    {
        if (!IsLoaded) return;
        var l = Store.LoadSettings().NotificationLayout;
        var notifs = Store.LoadNotifications()
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();
        string text = notifs.Count == 0
            ? "暂无通知（双击通知条可编辑）"
            : string.Join("　　　◆　　　", notifs.Select(n =>
                (string.IsNullOrEmpty(n.Title) ? "" : n.Title + "：") + n.Content));

        TxtMarquee.Text = text;
        TxtMarquee.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = TxtMarquee.DesiredSize.Width;
        double hostW = MarqueeHost.ActualWidth;
        if (hostW <= 0) { Dispatcher.BeginInvoke(new Action(StartMarquee)); return; }

        var tt = new TranslateTransform();
        TxtMarquee.RenderTransform = tt;

        if (textW <= hostW)
        {
            tt.X = (hostW - textW) / 2; // 短文本居中，不滚动
            return;
        }

        double speed = Math.Max(20, l.MarqueeSpeed);
        double dur = Math.Max(6, (textW + hostW) / speed);
        var anim = new DoubleAnimation(hostW, -textW - 30, TimeSpan.FromSeconds(dur))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private bool IsWithinButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (ReferenceEquals(dep, BtnHide)) return true;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _hider?.Dispose();
        _danmakuTimer?.Stop();
        AppEvents.NotificationChanged -= StartMarquee;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        AppEvents.ReminderDanmaku -= OnReminderDanmaku;
        base.OnClosed(e);
    }
}
