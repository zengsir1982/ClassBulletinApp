using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ClassBulletinApp;

/// <summary>
/// 值日生名单悬浮窗：按「每天 / 周一 … 周日」列出值日安排，自动高亮今天那一行。
/// 风格与设置项完全参照作业 / 缺交便签（主题 + 标题样式 + 内容样式 + 置顶 + 锁定 + 设为背景）。
/// <para>摆放 / 置顶 / 锁定 / 是否显示存在 <see cref="AppSettings.NoteWindows"/> 的 <c>duty</c> 键下。</para>
/// </summary>
public partial class DutyWindow : Window, IGlassSurface
{
    /// <summary>在 <see cref="AppSettings.NoteWindows"/> 里的键名。</summary>
    internal const string PlacementKey = "duty";

    private DutyBoard? _board;
    private DispatcherTimer? _posTimer;
    private DispatcherTimer? _dayTimer;
    private DateTime _renderedDay = DateTime.Today;
    private string _themeKey = "green";
    private bool _loading;
    private bool _recentered;

    /// <summary>是否锁定（锁定后不能移动 / 缩放 / 编辑，防止误操作）。</summary>
    private bool _locked;

    // ---------- WM_NCHITTEST 原生拖动 / 缩放 ----------
    private const int WM_NCHITTEST = 0x84;
    private const int WM_NCLBUTTONDBLCLK = 0xA3;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const double RESIZE_MARGIN = 8;

    public DutyWindow()
    {
        InitializeComponent();
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); SavePlacement(); };

        // 高亮行按「今天」算：跨零点后要换行，30 秒查一次足够
        _dayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dayTimer.Tick += (_, _) =>
        {
            if (DateTime.Today == _renderedDay) return;
            _renderedDay = DateTime.Today;
            RebuildContent();
        };
    
        GlassChrome.Prepare(this);}

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        Reload();
        UpdateTopButton();
        UpdateBackdropButton();
        UpdateLockButton();
        UpdateHintVisibility();

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            FloatingHelper.ClampToScreen(this);
            RebuildContent();   // 重新显示时"今天"可能已经变了
        };

        AppEvents.DutyChanged += OnDutyChanged;
        AppEvents.NoteLockChanged += UpdateLockButton;   // 编辑窗改了锁定，立刻同步锁头图标
        _dayTimer?.Start();

        if (_recentered) SavePlacement();
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var src = PresentationSource.FromVisual(this) as HwndSource;
        src?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            if (_locked) { handled = true; return (IntPtr)HTCLIENT; }

            int lp = lParam.ToInt32();
            int sx = (short)(lp & 0xFFFF);
            int sy = (short)((lp >> 16) & 0xFFFF);
            var client = PointFromScreen(new System.Windows.Point(sx, sy));
            double w = ActualWidth, h = ActualHeight;

            bool left = client.X <= RESIZE_MARGIN;
            bool right = client.X >= w - RESIZE_MARGIN;
            bool top = client.Y <= RESIZE_MARGIN;
            bool bottom = client.Y >= h - RESIZE_MARGIN;

            if (top && left) { handled = true; return (IntPtr)HTTOPLEFT; }
            if (top && right) { handled = true; return (IntPtr)HTTOPRIGHT; }
            if (bottom && left) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
            if (bottom && right) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
            if (left) { handled = true; return (IntPtr)HTLEFT; }
            if (right) { handled = true; return (IntPtr)HTRIGHT; }
            if (top) { handled = true; return (IntPtr)HTTOP; }
            if (bottom) { handled = true; return (IntPtr)HTBOTTOM; }

            var hit = VisualTreeHelper.HitTest(this, client)?.VisualHit;
            while (hit != null)
            {
                if (hit is Button) { handled = true; return (IntPtr)HTCLIENT; }
                hit = VisualTreeHelper.GetParent(hit);
            }
            handled = true;
            return (IntPtr)HTCAPTION;
        }

        if (msg == WM_NCLBUTTONDBLCLK)
        {
            if (_locked) { handled = true; return IntPtr.Zero; }
            handled = true;
            Dispatcher.BeginInvoke(new Action(OpenEditor));
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ---------- 位置 / 大小持久化 ----------
    private void RestorePlacement()
    {
        try
        {
            _loading = true;
            var p = Store.LoadSettings().PlacementOf(PlacementKey);
            if (p.Width is double sw && sw >= MinWidth) Width = sw;
            if (p.Height is double sh && sh >= MinHeight) Height = sh;
            Topmost = p.TopMost;

            var area = FloatingHelper.WorkAreaDip(this);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);

            if (p.Left is double l && p.Top is double t && IsUsable(l, t, area))
            {
                Left = l;
                Top = t;
                FloatingHelper.ClampToScreen(this);
            }
            else
            {
                FloatingHelper.CenterOnScreen(this);
                _recentered = true;
            }
        }
        catch { }
        finally { _loading = false; }
    }

    private bool IsUsable(double left, double top, Rect area)
    {
        var r = new Rect(left, top, Width, Height);
        var inter = Rect.Intersect(r, area);
        if (inter.IsEmpty || inter.Width <= 0 || inter.Height <= 0) return false;
        double need = Math.Max(1, r.Width * r.Height) * 0.55;
        return inter.Width * inter.Height >= need;
    }

    private void SavePlacement()
    {
        if (_loading) return;
        // 未显示的窗口 Left/Top 是 NaN：写进设置会让整个 settings.json 序列化失败
        if (!FloatingHelper.IsFiniteRect(this)) return;
        try
        {
            var s = Store.LoadSettings();
            var p = s.PlacementOf(PlacementKey);
            p.Left = Left;
            p.Top = Top;
            p.Width = Width;
            p.Height = Height;
            Store.SaveSettings(s);
        }
        catch { }
    }

    private void Window_LocationChanged(object sender, EventArgs e) => DebounceSavePlacement();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DebounceSavePlacement();
        UpdateHintVisibility();
    }

    private void UpdateHintVisibility()
    {
        if (TxtHint == null) return;
        TxtHint.Visibility = ActualWidth >= 420 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DebounceSavePlacement()
    {
        if (_loading) return;
        _posTimer?.Stop();
        _posTimer?.Start();
    }

    // ---------- 内容展示 ----------
    private void Reload()
    {
        _board = Store.LoadDuty();
        _themeKey = string.IsNullOrWhiteSpace(_board.Theme) ? "green" : _board.Theme;
        var theme = HomeworkThemes.Find(_themeKey);
        ApplyTheme(theme);

        TxtTitle.Text = string.IsNullOrWhiteSpace(_board.Title) ? "值日生名单" : _board.Title;
        ApplyTitleStyle(_board.TitleStyle, theme);
        RebuildContent();
    }

    private void ApplyTitleStyle(TextStyle? style, HomeworkTheme t)
    {
        TxtTitle.FontSize = HwTextRender.TitleSize(style);
        TxtTitle.FontWeight = HwTextRender.BoldOr(style, true) ? FontWeights.Bold : FontWeights.Normal;
        TxtTitle.Foreground = ThemeHelper.Brush(
            HwTextRender.ColorOr(style, t.HeaderForeground), Color.FromRgb(0x1F, 0x2A, 0x37));
    }

    private void OnDutyChanged()
    {
        Reload();
        UpdateBackdropButton();
    }

    private void ApplyTheme(HomeworkTheme t)
    {
        NoteBorder.Background = ThemeHelper.Brush(t.Background, Color.FromRgb(0xFF, 0xFF, 0xFF));
        NoteBorder.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xD8, 0xDE, 0xE9));
        Header.Background = ThemeHelper.Brush(t.HeaderBackground, Color.FromRgb(0xEA, 0xF1, 0xFD));
        TxtTitle.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x1F, 0x2A, 0x37));
        BtnHide.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x1F, 0x2A, 0x37));
        TxtHint.Foreground = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x5B, 0x64, 0x72));
        GlassBrush.Apply(NoteBorder);
    }

    /// <summary>IGlassSurface：设置变更时由 GlassBrush.RefreshAll 统一刷新底色。</summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
        => GlassBrush.Apply(NoteBorder, settings);

    /// <summary>
    /// 重建条目区：每天一行 = 星期（小字）+ 值日生姓名（正文），下面一行可选写值日内容。
    /// 今天那一行加淡底 + 左侧竖条，跟桌面背景卡片（BackdropRenderer.BuildDutyCard）口径一致。
    /// </summary>
    private void RebuildContent()
    {
        if (_board == null) return;
        _renderedDay = DateTime.Today;

        var t = HomeworkThemes.Find(_themeKey);
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));
        var accent = ThemeHelper.Brush(t.Accent, Color.FromRgb(0x2E, 0x7D, 0x32));

        var cs = _board.ContentStyle;
        double bodySize = HwTextRender.BodySize(cs);
        var bodyBrush = ThemeHelper.Brush(HwTextRender.ColorOr(cs, t.Foreground), Color.FromRgb(0x33, 0x3B, 0x47));
        var bodyWeight = HwTextRender.BoldOr(cs, false) ? FontWeights.Bold : FontWeights.Normal;

        // 星期列 / 值日内容各有自己的字号颜色（默认与加设置项之前一致：12 号加粗弱色 / 11 号弱色）
        var ds = _board.DayStyle;
        double daySize = HwTextRender.ScaleSize(ds, 12);
        var dayWeight = HwTextRender.BoldOr(ds, true) ? FontWeights.Bold : FontWeights.Normal;
        var dayBrushNormal = ThemeHelper.Brush(HwTextRender.ColorOr(ds, t.MutedColor), Color.FromRgb(0xB0, 0xB6, 0xBF));
        var dayBrushToday = ThemeHelper.Brush(HwTextRender.ColorOr(ds, t.Accent), Color.FromRgb(0x2E, 0x7D, 0x32));

        var dutySt = _board.DutyStyle;
        double dutySize = HwTextRender.ScaleSize(dutySt, 11);
        var dutyWeight = HwTextRender.BoldOr(dutySt, false) ? FontWeights.Bold : FontWeights.Normal;
        var dutyBrush = ThemeHelper.Brush(HwTextRender.ColorOr(dutySt, t.MutedColor), Color.FromRgb(0xB0, 0xB6, 0xBF));

        string todayName = BackdropRenderer.WeekDayText(DateTime.Today.DayOfWeek);

        ContentPanel.Children.Clear();

        var items = _board.SortedItems();
        if (items.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "（暂无值日安排，点「编辑」添加）",
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var it in items)
            {
                string day = it.DayText();
                bool isToday = _board.HighlightToday && string.Equals(day, todayName, StringComparison.Ordinal);
                var brush = ThemeHelper.Brush(HwTextRender.ColorOr(cs, t.Foreground), Color.FromRgb(0x33, 0x3B, 0x47));

                var row = new Border
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(isToday ? 6 : 0, isToday ? 4 : 0, 0, isToday ? 4 : 0),
                    CornerRadius = new CornerRadius(6),
                    Background = isToday ? SoftBrush(ThemeHelper.Brush(t.Accent, Color.FromRgb(0x2E, 0x7D, 0x32))) : null,
                    BorderThickness = isToday ? new Thickness(2, 0, 0, 0) : new Thickness(0),
                    BorderBrush = isToday ? accent : null
                };

                var panel = new StackPanel();

                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = day,
                    FontSize = daySize,
                    FontWeight = dayWeight,
                    Foreground = isToday ? dayBrushToday : dayBrushNormal,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 30
                });
                head.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Names) ? "（未填写）" : it.Names,
                    FontSize = bodySize,
                    FontWeight = bodyWeight,
                    Foreground = isToday ? brush : bodyBrush,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(head);

                if (!string.IsNullOrWhiteSpace(it.Duty))
                    panel.Children.Add(new TextBlock
                    {
                        Text = it.Duty,
                        FontSize = dutySize,
                        FontWeight = dutyWeight,
                        Foreground = dutyBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 0)
                    });

                row.Child = panel;
                ContentPanel.Children.Add(row);
            }
        }

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "更新于 " + _board.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(0, 6, 0, 0)
        });
    }

    /// <summary>把强调色调成很淡的一层底色（今天那一行的高亮背板）。</summary>
    private static Brush SoftBrush(Brush source)
    {
        if (source is SolidColorBrush sc)
        {
            var c = sc.Color;
            return new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        }
        return Brushes.Transparent;
    }

    // ---------- 编辑对话框 ----------
    private void OpenEditor()
    {
        try
        {
            bool wasTop = Topmost;
            Topmost = false;

            var dlg = new DutyEditWindow { Owner = this };
            dlg.ShowDialog();

            Topmost = wasTop;
            Reload();
            AppEvents.RaiseDutyChanged();
        }
        catch { }
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        OpenEditor();
    }

    // ---------- 置顶 ----------
    private void UpdateTopButton()
    {
        if (BtnTop == null) return;
        bool top = Topmost;
        BtnTop.Content = top ? "已置顶" : "置顶";
        BtnTop.Background = top ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
        BtnTop.Foreground = top ? Brushes.White : Brushes.Black;
    }

    private void BtnTop_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        try
        {
            var s = Store.LoadSettings();
            s.PlacementOf(PlacementKey).TopMost = Topmost;
            Store.SaveSettings(s);
        }
        catch { }
        UpdateTopButton();
    }

    // ---------- 锁定 ----------
    private void UpdateLockButton()
    {
        if (BtnLock == null) return;
        _locked = Store.LoadSettings().PlacementOf(PlacementKey).Locked;
        RefreshLockVisual();
    }

    private void RefreshLockVisual()
    {
        BtnLock.Content = _locked ? "🔒" : "🔓";
        BtnLock.Background = _locked ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
        BtnLock.Foreground = _locked ? Brushes.White : Brushes.Black;
        if (TxtHint != null)
            TxtHint.Text = _locked ? "已锁定" : "拖动·双击编辑";
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e)
    {
        _locked = !_locked;
        try
        {
            var s = Store.LoadSettings();
            s.PlacementOf(PlacementKey).Locked = _locked;
            Store.SaveSettings(s);
        }
        catch { }
        RefreshLockVisual();
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        try
        {
            var s = Store.LoadSettings();
            s.PlacementOf(PlacementKey).Visible = false;
            Store.SaveSettings(s);
        }
        catch { }
    }

    // ---------- 桌面背景 ----------
    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        if (!BackdropPin.Pin(BackdropPin.Kind.Duty, this, out string msg))
        {
            System.Windows.MessageBox.Show(msg, "设为背景", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateBackdropButton();
        AppEvents.RaiseBackdropChanged();

        try
        {
            var s = Store.LoadSettings();
            s.PlacementOf(PlacementKey).Visible = false;
            Store.SaveSettings(s);
        }
        catch { }

        Hide();
    }

    private void BtnPos_Click(object sender, RoutedEventArgs e)
    {
        bool wasTop = Topmost;
        Topmost = false;
        try { new BackdropEditorWindow { Owner = this }.ShowDialog(); }
        catch { }
        finally { Topmost = wasTop; }
        AppEvents.RaiseBackdropChanged();
    }

    private void UpdateBackdropButton()
    {
        if (BtnWp == null) return;
        bool on = false;
        try { on = Store.LoadSettings().Backdrop?.ShowDuty == true; }
        catch { }

        BtnWp.Background = on ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
        BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
        BtnWp.ToolTip = on
            ? "值日生名单已在桌面背景显示；再点一次会按「现在的位置和大小」重新锁定。要取消：布局设置 → 桌面背景 取消勾选它"
            : "按「现在的位置和大小」一键设为桌面背景，并隐藏本便签";
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.DutyChanged -= OnDutyChanged;
        AppEvents.NoteLockChanged -= UpdateLockButton;
        _posTimer?.Stop();
        _dayTimer?.Stop();
        base.OnClosed(e);
    }
}
