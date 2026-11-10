using System;
using System.Collections.Generic;
using System.Linq;
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
/// 事项列表便签：把「通知 / 公告」从一条无限滚动的跑马灯，升级成一块有版面的便签。
/// <para>为什么要有它：跑马灯把所有通知拼成一段文本，条数一多整圈就被拉长，
/// 今天的通知被压在一堆上周的通知后面；自动增加上线后条目涨得更快。
/// 便签能一眼扫完「还剩什么、什么时候到期」，跑马灯退化成只滚最新两条的摘要条。</para>
/// <para>层级与作业公示 / 缺交名单 / 值日安排并列：摆放、置顶、锁定、主题、玻璃底色、
/// 设为桌面背景全部走同一套（<see cref="AppSettings.NoteWindows"/> 的 <c>notice</c> 键）。</para>
/// </summary>
public partial class NoticeWindow : Window, IGlassSurface
{
    /// <summary>在 <see cref="AppSettings.NoteWindows"/> 里的键名。</summary>
    internal const string PlacementKey = "notice";

    /// <summary>
    /// 便签主题键与字号倍率（r79 起可配置）。以前写死 const "blue"，用户没得挑；
    /// 现在读 settings.json 的 <see cref="AppSettings.NoticeNoteTheme"/> / <c>NoticeNoteScale</c>，
    /// 改完由控制面板调 <see cref="Reload"/> 立刻生效。
    /// </summary>
    private string ThemeKey = "blue";
    private double _scale = 1.0;

    private DispatcherTimer? _posTimer;
    private DispatcherTimer? _dayTimer;
    private bool _loading;
    private bool _recentered;

    /// <summary>上次渲染的内容签名：条目集合或「今天/明天」变了才重建，避免每分钟无谓闪一下。</summary>
    private string _renderedSig = "";

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

    public NoticeWindow()
    {
        InitializeComponent();
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); SavePlacement(); };

        // 跨零点 / 有事项到期后列表要变，30 秒查一次「签名」即可（比每轮重建便宜）
        _dayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dayTimer.Tick += (_, _) =>
        {
            string sig = Signature();
            if (sig != _renderedSig) RebuildContent();
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
            RebuildContent();   // 重新显示时可能已经跨天 / 有事项到期
        };

        AppEvents.NotificationChanged += OnNotificationChanged;
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

    /// <summary>
    /// 便签上要显示的条目：<b>未撤下 且 未过期</b>，置顶优先，其余按截止日期从近到远
    /// （长期有效的排最后），同一天再按发布时间倒序。
    /// <para>★ 这里必须用 <see cref="Notification.IsLive"/>：到期只是「撤下显示」，
    /// 数据还在编辑窗里，可恢复。</para>
    /// </summary>
    internal static List<Notification> LiveForBoard()
        => Store.LoadNotifications()
                .Where(x => x.IsLive)
                .OrderByDescending(x => x.Pinned)
                .ThenBy(x => x.DueAt ?? DateTime.MaxValue)
                .ThenByDescending(x => x.CreatedAt)
                .ToList();

    /// <summary>内容签名：条目集合 / 文字 / 截止标签 / 置顶状态 / 文字格式变化时重绘。</summary>
    private string Signature()
    {
        var live = LiveForBoard();
        return string.Join("|", live.Select(x => x.Id + ":" + x.DueText + ":" + (x.Pinned ? "1" : "0")
                                                  + ":" + x.Title + ":" + x.Content + ":" + x.StyleSignature));
    }

    /// <summary>重读「便签风格」设置（配色预设 + 字号倍率）。供控制面板改完风格后立即刷新。</summary>
    private void LoadStyle()
    {
        try
        {
            var s = Store.LoadSettings();
            ThemeKey = string.IsNullOrWhiteSpace(s.NoticeNoteTheme) ? "blue" : s.NoticeNoteTheme.Trim();
            _scale = s.NoticeNoteScale <= 0.01 ? 1.0 : Math.Clamp(s.NoticeNoteScale, 0.80, 1.40);
        }
        catch
        {
            ThemeKey = "blue";
            _scale = 1.0;
        }
    }

    public void Reload()
    {
        LoadStyle();
        ApplyTheme(HomeworkThemes.Find(ThemeKey));
        RebuildContent();
        UpdateBackdropButton();
    }

    private void OnNotificationChanged()
    {
        RebuildContent();
        UpdateBackdropButton();
    }

    private void ApplyTheme(HomeworkTheme t)
    {
        NoteBorder.Background = ThemeHelper.Brush(t.Background, Color.FromRgb(0xE3, 0xF2, 0xFD));
        NoteBorder.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xBB, 0xDE, 0xFB));
        Header.Background = ThemeHelper.Brush(t.HeaderBackground, Color.FromRgb(0xBB, 0xDE, 0xFB));
        TxtTitle.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x0D, 0x47, 0xA1));
        BtnHide.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x0D, 0x47, 0xA1));
        TxtHint.Foreground = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x7A, 0x9A, 0xB5));

        GlassBrush.Apply(NoteBorder);
    }

    /// <summary>IGlassSurface：设置变更时由 GlassBrush.RefreshAll 统一刷新底色。</summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
        => GlassBrush.Apply(NoteBorder, settings);

    /// <summary>
    /// 重建条目区：一行一条事项 = 左侧圆点 + 标题（正文）/ 内容（次行）+ 右侧截止标签。
    /// 置顶的整行加淡底 + 左竖条，和值日便签「今天那一行」的处理方式一致。
    /// </summary>
    private void RebuildContent()
    {
        var t = HomeworkThemes.Find(ThemeKey);
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x1A, 0x3A, 0x5C));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x7A, 0x9A, 0xB5));
        var accent = ThemeHelper.Brush(t.Accent, Color.FromRgb(0x15, 0x65, 0xC0));
        var dueHot = new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C));

        var live = LiveForBoard();
        _renderedSig = Signature();

        ContentPanel.Children.Clear();

        if (live.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "（暂无通知，点「编辑」添加）",
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var n in live)
        {
            var row = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(n.Pinned ? 6 : 0, n.Pinned ? 4 : 0, 0, n.Pinned ? 4 : 0),
                CornerRadius = new CornerRadius(6),
                Background = n.Pinned ? SoftBrush(accent) : null,
                BorderThickness = n.Pinned ? new Thickness(2, 0, 0, 0) : new Thickness(0),
                BorderBrush = n.Pinned ? accent : null
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = n.Pinned ? accent : muted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 7, 0)
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            var body = new StackPanel();
            // r72：标题 / 正文各自读自己的文字格式。样式为 null（老数据 / 同步落地）时
            // ScaleSize(基准) 正好等于原来的 14 / 13 号，BoldOr 也回到原来的默认粗细 —— 外观一字不差。
            if (!string.IsNullOrWhiteSpace(n.Title))
                body.Children.Add(new TextBlock
                {
                    Text = n.Title,
                    FontSize = HwTextRender.ScaleSize(n.TitleStyle, 14) * _scale,
                    FontWeight = HwTextRender.BoldOr(n.TitleStyle, true) ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = string.IsNullOrWhiteSpace(n.TitleStyle?.Color)
                        ? fg
                        : ThemeHelper.Brush(n.TitleStyle!.Color, Color.FromRgb(0x1A, 0x3A, 0x5C)),
                    TextWrapping = TextWrapping.Wrap
                });
            if (!string.IsNullOrWhiteSpace(n.Content))
            {
                var bodyFg = string.IsNullOrWhiteSpace(n.Title) ? fg : muted;
                body.Children.Add(new TextBlock
                {
                    Text = n.Content,
                    FontSize = HwTextRender.ScaleSize(n.ContentStyle, 13) * _scale,
                    FontWeight = HwTextRender.BoldOr(n.ContentStyle, false) ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = string.IsNullOrWhiteSpace(n.ContentStyle?.Color)
                        ? bodyFg
                        : ThemeHelper.Brush(n.ContentStyle!.Color, Color.FromRgb(0x1A, 0x3A, 0x5C)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, string.IsNullOrWhiteSpace(n.Title) ? 0 : 2, 0, 0)
                });
            }
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);

            string due = n.DueText;
            var dueText = new TextBlock
            {
                Text = due,
                FontSize = 11,
                // 「今天 / 明天」是真正要动手的，给个暖色提醒；长期与其他日期保持弱色
                Foreground = due is "今天" or "明天" ? dueHot : muted,
                FontWeight = due is "今天" or "明天" ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 3, 0, 0)
            };
            Grid.SetColumn(dueText, 2);
            grid.Children.Add(dueText);

            row.Child = grid;
            ContentPanel.Children.Add(row);
        }

        int pinned = live.Count(x => x.Pinned);
        ContentPanel.Children.Add(new TextBlock
        {
            Text = pinned > 0 ? $"共 {live.Count} 条 · 置顶 {pinned} 条" : $"共 {live.Count} 条",
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(0, 2, 0, 0)
        });
    }

    /// <summary>把强调色调成很淡的一层底色（置顶那一行的背板）。</summary>
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

            var dlg = new NotificationEditWindow { Owner = this };
            dlg.ShowDialog();

            Topmost = wasTop;
            RebuildContent();
            AppEvents.RaiseNotificationChanged();
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
        BtnTop.Background = top ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
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
        BtnLock.Background = _locked ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
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
        if (!BackdropPin.Pin(BackdropPin.Kind.Notice, this, out string msg))
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
        try { on = Store.LoadSettings().Backdrop?.ShowNotice == true; }
        catch { }

        BtnWp.Background = on ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
        BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
        BtnWp.ToolTip = on
            ? "通知公告已在桌面背景显示；再点一次会按「现在的位置和大小」重新锁定。要取消：布局设置 → 桌面背景 取消勾选它"
            : "按「现在的位置和大小」一键设为桌面背景，并隐藏本便签";
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.NotificationChanged -= OnNotificationChanged;
        _posTimer?.Stop();
        _dayTimer?.Stop();
        base.OnClosed(e);
    }
}
