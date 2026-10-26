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
    private bool _clickThrough;

    public NotificationWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCurrentLayout();
        AppEvents.NotificationChanged += StartMarquee;
        AppEvents.LayoutChanged += OnLayoutChanged;
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
            ? new AutoHider(this, _layout,
                () => _layout.AutoHide || (_layout.AutoHideDuringClass && WeekHelper.IsClassTimeNow(_schedule, _className)),
                OnCompactChanged)
            : null;
        if (_hider == null) SetCompactUi(false);
    }

    // ---------- 自动隐藏：完整通知条 ⇄ 通知悬浮条 ----------
    private void OnCompactChanged(bool compact) => SetCompactUi(compact);

    private void SetCompactUi(bool compact)
    {
        if (Bar == null || DockBar == null) return;
        Bar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DockBar.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        // 展开后重新测量并启动跑马灯（折叠时 MarqueeHost 宽度为 0，无需滚动）
        if (!compact) Dispatcher.BeginInvoke(new Action(StartMarquee));
    }

    private bool InDockMode => _hider != null && _hider.IsCompact && DockBar != null &&
                               DockBar.Visibility == Visibility.Visible;

    private bool _dockDragging;
    private System.Windows.Point _dockStartScreen, _dockStartWin;

    private void DockBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!InDockMode) return;
        _dockDragging = true;
        _dockStartScreen = PointToScreen(e.GetPosition(this));
        _dockStartWin = new System.Windows.Point(Left, Top);
        System.Windows.Input.Mouse.Capture(DockBar);
        if (_hider != null) { _hider.CancelHover(); _hider.Suspended = true; }
        e.Handled = true;
    }

    private void DockBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dockDragging || _hider == null) return;
        var cur = PointToScreen(e.GetPosition(this));
        _hider.SetDockPosition(
            _dockStartWin.X + (cur.X - _dockStartScreen.X),
            _dockStartWin.Y + (cur.Y - _dockStartScreen.Y));
        e.Handled = true;
    }

    private void DockBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dockDragging) return;
        _dockDragging = false;
        if (System.Windows.Input.Mouse.Captured == DockBar) System.Windows.Input.Mouse.Capture(null);
        SaveDockPosition();
        _hider?.SuppressExpand();
        if (_hider != null) _hider.Suspended = false;
        e.Handled = true;
    }

    private void SaveDockPosition()
    {
        if (_hider == null) return;
        var r = _hider.DockRect;
        var s = Store.LoadSettings();
        s.NotificationLayout.DockLeft = r.X;
        s.NotificationLayout.DockTop = r.Y;
        Store.SaveSettings(s);
    }

    private void OnLayoutChanged()
    {
        ApplyCurrentLayout();
        StartMarquee();
    }

    // ---------- 双击编辑通知（穿透态下通知条本身仍保持可交互） ----------
    private void Bar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (InDockMode) return;   // 小条状态的点击用于拖动位置
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
        // 缩小成悬浮条时通知区不可见（宽度为 0），不要反复重排入协程
        if (Bar != null && Bar.Visibility != Visibility.Visible) return;
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
        AppEvents.NotificationChanged -= StartMarquee;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        base.OnClosed(e);
    }
}
