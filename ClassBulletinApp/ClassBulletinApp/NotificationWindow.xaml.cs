using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace ClassBulletinApp;

public partial class NotificationWindow : Window
{
    private AutoHider? _hider;
    private PanelLayout? _layout;
    private ScheduleFile? _schedule;
    private string? _className;
    private bool _clickThrough;
    private DispatcherTimer? _danmakuTimer;

    /// <summary>程序化应用布局期间置 true，避免把自动定位 / 自动隐藏的位移当作拖动保存。</summary>
    private bool _applyingLayout;

    public NotificationWindow()
    {
        InitializeComponent();

        // 所见即所得：按住通知条拖动窗口（双击仍可编辑通知）；鼠标移到边沿 / 四角出现双向箭头时拖动改大小。
        // 只在用户松手时落盘，避免把自动布局 / 自动隐藏的坐标当成用户位置。
        void OnStart() { if (_hider != null) _hider.Suspended = true; }
        void OnEnd()
        {
            if (_hider != null) _hider.Suspended = false;
            SavePlacement();
            ApplyCurrentLayout();
            AppEvents.RaiseLayoutChanged();   // 自动隐藏开关变化后同步隐藏按钮标签等
        }

        FloatChrome.Enable(this, canInteract: () => !_clickThrough,
            onGestureStart: OnStart, onGestureEnd: OnEnd);
        FloatChrome.EnableDrag(Bar, this,
            onDragStart: OnStart, onDragEnd: OnEnd,
            canDrag: () => !_clickThrough);
    }

    /// <summary>记住拖动 / 缩放后的位置与大小，并把锚点更新为最近的屏幕边（自动隐藏往那边滑出）。</summary>
    private void SavePlacement()
    {
        if (_applyingLayout) return;
        try
        {
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            // 自动隐藏态窗口在屏幕外：此刻的坐标不代表用户意图，不保存
            if (!FloatingHelper.IsMostlyOnScreen(this, 0.3)) return;

            var s = Store.LoadSettings();
            var l = s.NotificationLayout ??= new PanelLayout();
            var clamped = FloatingHelper.ClampRectDip(this, new Rect(Left, Top, Width, Height));

            // 仅当窗口位置确实发生移动时才据「是否贴边」自动切换自动隐藏，避免单纯改大小误触发。
            bool moved = !l.Left.HasValue || !l.Top.HasValue
                || Math.Abs(l.Left.Value - clamped.X) > 8
                || Math.Abs(l.Top.Value - clamped.Y) > 8;
            if (moved)
            {
                bool near = FloatingHelper.IsNearEdge(this);
                l.AutoHide = near;
                if (near) l.ClickThrough = false;   // 自动隐藏与鼠标穿透互斥
            }

            l.Left = clamped.X;
            l.Top = clamped.Y;
            l.Width = clamped.Width;
            l.Height = clamped.Height;
            l.Anchor = FloatingHelper.NearestAnchor(this);
            Store.SaveSettings(s);

            if (_layout != null)
            {
                _layout.Left = l.Left; _layout.Top = l.Top;
                _layout.Width = l.Width; _layout.Height = l.Height;
                _layout.Anchor = l.Anchor;
                _layout.AutoHide = l.AutoHide;
                _layout.ClickThrough = l.ClickThrough;
            }
        }
        catch { }
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

    private void ApplyCurrentLayout(PanelLayout? live = null)
    {
        var s = Store.LoadSettings();
        _layout = live ?? s.NotificationLayout;
        // 「置顶显示」= 半透明常驻且完全可交互（双击可编辑、隐藏按钮可单击）；
        // 只有在布局里显式勾选「鼠标穿透」时才打开穿透（此时通知条与隐藏按钮仍保持可点击）。
        bool topShow = !_layout.AutoHide;
        _clickThrough = _layout.ClickThrough && !_layout.AutoHide;
        _schedule = Store.LoadSchedule();
        _className = s.SelectedClass;
        _applyingLayout = true;
        try
        {
            FloatingHelper.ApplyLayout(this, _layout, _clickThrough, topShow ? _layout.OverlayOpacity : _layout.Opacity);
        }
        finally { _applyingLayout = false; }
        if (_clickThrough)
            FloatingHelper.SetClickThrough(this, true, BtnHide, Bar);
        else
            FloatingHelper.SetClickThrough(this, false);
        TxtMarquee.FontSize = Math.Max(12, _layout.FontSize);
        ApplyBarStyle(_layout);
        BtnHide.Content = _layout.AutoHide ? "置顶显示" : "自动隐藏";
        _hider?.Dispose();
        _hider = (_layout.AutoHide || _layout.AutoHideDuringClass)
            ? new AutoHider(this, _layout, () => _layout.AutoHide || (_layout.AutoHideDuringClass && WeekHelper.IsClassTimeNow(_schedule, _className)))
            : null;
    }

    /// <summary>应用通知条的外观：字体、文字颜色、条背景色（留空则用界面主题默认色）。</summary>
    private void ApplyBarStyle(PanelLayout l)
    {
        try
        {
            TxtMarquee.FontFamily = string.IsNullOrWhiteSpace(l.FontFamily)
                ? new FontFamily("Microsoft YaHei UI")
                : new FontFamily(l.FontFamily);
            TxtMarquee.Foreground = ThemeHelper.Brush(l.TextColor, Color.FromRgb(0xFF, 0xFF, 0xFF));

            if (string.IsNullOrWhiteSpace(l.BarColor))
            {
                Bar.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");
            }
            else
            {
                Bar.Background = ThemeHelper.Brush(l.BarColor, Color.FromRgb(0x1E, 0x3A, 0x6B));
            }
        }
        catch { }
    }

    private void OnLayoutChanged(PanelLayout? schedule, PanelLayout? notification)
    {
        ApplyCurrentLayout(notification);
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
        if (_hider == null) SavePlacement();   // 自动隐藏态下窗口在屏外，别把屏外坐标存进去
        _hider?.Dispose();
        _danmakuTimer?.Stop();
        AppEvents.NotificationChanged -= StartMarquee;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        AppEvents.ReminderDanmaku -= OnReminderDanmaku;
        base.OnClosed(e);
    }
}
