using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class NotificationWindow : Window
{
    private AutoHider? _hider;
    private PanelLayout? _layout;
    private ScheduleFile? _schedule;
    private string? _className;

    public NotificationWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCurrentLayout();
        AppEvents.NotificationChanged += StartMarquee;
        AppEvents.LayoutChanged += OnLayoutChanged;
        Bar.SizeChanged += (_, _) => StartMarquee();
        StartMarquee();
    }

    private void ApplyCurrentLayout()
    {
        var s = Store.LoadSettings();
        _layout = s.NotificationLayout;
        _schedule = Store.LoadSchedule();
        _className = s.SelectedClass;
        FloatingHelper.ApplyLayout(this, _layout);
        TxtMarquee.FontSize = Math.Max(12, _layout.FontSize);
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

    private void StartMarquee()
    {
        if (!IsLoaded) return;
        var l = Store.LoadSettings().NotificationLayout;
        var notifs = Store.LoadNotifications()
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();
        string text = notifs.Count == 0
            ? "暂无通知"
            : string.Join("　　　◆　　　", notifs.Select(n =>
                (string.IsNullOrEmpty(n.Title) ? "" : n.Title + "：") + n.Content));

        TxtMarquee.Text = text;
        TxtMarquee.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = TxtMarquee.DesiredSize.Width;
        double hostW = Bar.ActualWidth;
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

    protected override void OnClosed(EventArgs e)
    {
        _hider?.Dispose();
        AppEvents.NotificationChanged -= StartMarquee;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        base.OnClosed(e);
    }
}
