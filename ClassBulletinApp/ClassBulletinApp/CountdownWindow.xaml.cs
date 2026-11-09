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
/// 倒计时悬浮窗：一张卡里放多条事件（期末考试还有 30 天、运动会还有 12 天…），
/// 整块可拖动 / 拉边改大小（WM_NCHITTEST 原生处理），支持主题、置顶、锁定、双击编辑，
/// 也能一键「设为背景」画到桌面上。
/// <para>摆放 / 置顶 / 锁定 / 是否显示存在 <see cref="AppSettings.NoteWindows"/> 的 <c>countdown</c> 键下，
/// 与值日生名单共用同一套存储结构。</para>
/// </summary>
public partial class CountdownWindow : Window, IGlassSurface
{
    /// <summary>在 <see cref="AppSettings.NoteWindows"/> 里的键名。</summary>
    internal const string PlacementKey = "countdown";

    private CountdownBoard? _board;
    private DispatcherTimer? _posTimer;
    private DispatcherTimer? _dayTimer;
    private DateTime _renderedDay = DateTime.Today;
    private string _themeKey = "classic";
    private bool _loading;
    private bool _recentered;

    /// <summary>是否锁定（锁定后不能移动 / 缩放 / 编辑，防止误操作）。</summary>
    private bool _locked;

    /// <summary>倒计时是否已经画在桌面背景上（底栏按钮状态用，避免每次都读盘）。</summary>
    private bool _backdropOn;

    // ---------- WM_NCHITTEST 原生拖动 / 缩放 ----------
    private const int WM_NCHITTEST = 0x84;
    private const int WM_NCLBUTTONDBLCLK = 0xA3;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const double RESIZE_MARGIN = 8;

    public CountdownWindow()
    {
        InitializeComponent();
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); SavePlacement(); };

        // 天数按「今天」算：跨零点后必须重算，否则"还有 1 天"会一直挂到下次重启
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
            RebuildContent();   // 重新显示时天数可能已经变了
        };

        AppEvents.CountdownChanged += OnCountdownChanged;
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
            // 锁定：整窗返回 HTCLIENT，禁止移动与拖拽缩放（按钮仍可点击）
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
            if (_locked) { handled = true; return IntPtr.Zero; }   // 锁定后禁止编辑
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
        RefreshBottomBar();     // 卡片变窄时底栏只留图标，避免折行把内容挤扁
    }

    /// <summary>底栏按钮多，窗口窄时提示文字会被挤掉半个字，不如收起：够宽（≥420）才显示。</summary>
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
        _board = Store.LoadCountdown();
        _themeKey = string.IsNullOrWhiteSpace(_board.Theme) ? "classic" : _board.Theme;
        var theme = HomeworkThemes.Find(_themeKey);
        ApplyTheme(theme);

        TxtTitle.Text = string.IsNullOrWhiteSpace(_board.Title) ? "倒计时" : _board.Title;
        ApplyTitleStyle(_board.TitleStyle, theme);
        RebuildContent();
    }

    /// <summary>应用标题的字号 / 颜色 / 粗细（未设置时保持主题默认：加粗 + 主题标题色 + 17 号）。</summary>
    private void ApplyTitleStyle(TextStyle? style, HomeworkTheme t)
    {
        TxtTitle.FontSize = HwTextRender.TitleSize(style);
        TxtTitle.FontWeight = HwTextRender.BoldOr(style, true) ? FontWeights.Bold : FontWeights.Normal;
        TxtTitle.Foreground = ThemeHelper.Brush(
            HwTextRender.ColorOr(style, t.HeaderForeground), Color.FromRgb(0x1F, 0x2A, 0x37));
    }

    private void OnCountdownChanged()
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
    /// 重建条目区：左侧事件名（+ 备注），右侧「还有 N 天」。
    /// 视觉口径与桌面背景卡片（BackdropRenderer.BuildCountdownCard）保持一致 —— 同一份数据在
    /// 悬浮窗和壁纸上长得一样，用户才不会觉得"设为背景后像换了个东西"。
    /// </summary>
    private void RebuildContent()
    {
        if (_board == null) return;
        _renderedDay = DateTime.Today;

        var t = HomeworkThemes.Find(_themeKey);
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var cs = _board.ContentStyle;
        double bodySize = HwTextRender.BodySize(cs);
        var bodyBrush = ThemeHelper.Brush(HwTextRender.ColorOr(cs, t.Foreground), Color.FromRgb(0x33, 0x3B, 0x47));
        var bodyWeight = HwTextRender.BoldOr(cs, false) ? FontWeights.Bold : FontWeights.Normal;

        // 天数单独一套样式：r73c 起默认「红色 + 加粗 + 特大号」，
        // 样式为空 / 颜色为空时也回退红色（不再回退主题强调色，免得同一个便签在不同主题下颜色乱跳）
        var ds = _board.DaysStyle;
        double daysSize = HwTextRender.SizeOf(ds, 0);
        double unitSize = Math.Max(9, daysSize - 7);         // 「天」字 / 「已过」等小字
        var daysWeight = HwTextRender.BoldOr(ds, true) ? FontWeights.Bold : FontWeights.Normal;
        var daysBrush = ThemeHelper.Brush(HwTextRender.ColorOr(ds, CountdownBoard.DaysDefaultColor),
                                          Color.FromRgb(0xC0, 0x39, 0x2B));
        var daysUnitBrush = ThemeHelper.Brush(HwTextRender.ColorOr(ds, t.MutedColor), Color.FromRgb(0xB0, 0xB6, 0xBF));

        // 名字与天数紧挨着排、整张卡像一块整体（而不是名字贴左、天数贴右、中间空一大截）；
        // 卡片宽度不再自动跟随内容收窄，改由用户拖动边框决定（拖完自动记住）
        ContentPanel.Children.Clear();

        var items = _board.SortedItems();
        if (items.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "（暂无倒计时，点「编辑」添加）",
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var it in items)
            {
                bool hasNote = !string.IsNullOrWhiteSpace(it.Note);

                // 一条 = 一个整体：左列放「事件名 + 备注」（* 列，宽度随窗口收窄、超长截断），
                // 右列放「天数」（Auto，始终完整可见并紧贴事件名右侧）。
                // 之所以用 Grid 两列而不是横排 StackPanel：之前横排里 name 的 MaxWidth 取半屏上限、
                // 不受窗口约束，窗口被拖窄时 name 占满整行、天数被挤出右侧裁掉，看不到了；
                // Grid 的 * 列会随窗口把 name 收窄，天数永远留在右边、与名字紧挨。
                // 备注只放在左列、不跨列，也不会反向撑宽整张卡。
                var block = new Grid
                {
                    Margin = new Thickness(0, 0, 0, hasNote ? 10 : 7),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    }
                };

                // 左列：事件名（超长截断）+ 备注（可选）
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Name) ? "（未命名）" : it.Name,
                    FontSize = bodySize,
                    FontWeight = bodyWeight,
                    Foreground = bodyBrush,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = hasNote ? it.Note : null,
                    VerticalAlignment = VerticalAlignment.Center
                };
                left.Children.Add(name);

                if (hasNote)
                {
                    left.Children.Add(new TextBlock
                    {
                        Text = it.Note,
                        FontSize = unitSize,
                        Foreground = muted,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                }

                Grid.SetColumn(left, 0);
                block.Children.Add(left);

                // 天数：还有 30 天 / 就是今天 / 已过去 3 天；紧贴事件名右侧，与它连成一句
                var right = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                bool hasDate = it.Target() != null;
                int days = it.DaysLeft();
                bool isToday = hasDate && days == 0 && _board.HighlightToday;

                if (!hasDate)
                {
                    right.Children.Add(new TextBlock { Text = "未设日期", FontSize = unitSize, Foreground = daysUnitBrush });
                }
                else if (days < 0)
                {
                    right.Children.Add(new TextBlock { Text = $"已过去{-days}天", FontSize = unitSize, Foreground = daysUnitBrush });
                }
                else if (days == 0)
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = "就是今天", FontSize = daysSize, FontWeight = daysWeight,
                        Foreground = isToday
                            ? ThemeHelper.Brush(t.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B))
                            : daysBrush
                    });
                }
                else
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = days.ToString(), FontSize = daysSize, FontWeight = daysWeight,
                        Foreground = daysBrush
                    });
                    // 「天」不写前导空格：「7 天」中间那道空隙就是它造成的，只留 1px 呼吸位
                    right.Children.Add(new TextBlock
                    {
                        Text = "天", FontSize = unitSize, Foreground = daysUnitBrush,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(1, 0, 0, Math.Max(0, (daysSize - unitSize) / 4))
                    });
                }

                Grid.SetColumn(right, 1);
                block.Children.Add(right);

                ContentPanel.Children.Add(block);
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


    // ---------- 编辑对话框 ----------
    private void OpenEditor()
    {
        try
        {
            // 编辑期间临时取消置顶，避免便签盖住编辑对话框
            bool wasTop = Topmost;
            Topmost = false;

            var dlg = new CountdownEditWindow { Owner = this };
            dlg.ShowDialog();

            Topmost = wasTop;
            Reload();
            AppEvents.RaiseCountdownChanged();
        }
        catch { }
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;   // 锁定后禁止编辑
        OpenEditor();
    }

    // ---------- 置顶 ----------
    private void UpdateTopButton() => RefreshBottomBar();

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));

    /// <summary>
    /// 底栏按钮的统一刷新。卡片按内容收窄后可能只剩 220 宽，五个文字按钮放不下会折行、
    /// 把内容区挤扁 —— 所以窄的时候只显示图标（含义写在 ToolTip 里），宽的时候照常显示文字。
    /// </summary>
    private void RefreshBottomBar()
    {
        if (BtnEdit == null) return;
        bool narrow = ActualWidth > 0 && ActualWidth < 280;
        bool top = Topmost;

        BtnEdit.Content = narrow ? "✏" : "编辑";
        BtnTop.Content = narrow ? (top ? "📌" : "⬆") : (top ? "已置顶" : "置顶");
        BtnLock.Content = _locked ? "🔒" : "🔓";
        BtnPos.Content = narrow ? "📍" : "位置";
        BtnWp.Content = narrow ? "🖼" : "设为背景";

        BtnTop.Background = top ? AccentBrush : Brushes.White;
        BtnTop.Foreground = top ? Brushes.White : Brushes.Black;
        BtnLock.Background = _locked ? AccentBrush : Brushes.White;
        BtnLock.Foreground = _locked ? Brushes.White : Brushes.Black;
        BtnWp.Background = _backdropOn ? AccentBrush : Brushes.White;
        BtnWp.Foreground = _backdropOn ? Brushes.White : Brushes.Black;
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
        RefreshBottomBar();
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
        BtnLock.Content = _locked ? "🔒" : "🔓";
        BtnLock.Background = _locked ? AccentBrush : Brushes.White;
        BtnLock.Foreground = _locked ? Brushes.White : Brushes.Black;
        if (TxtHint != null)
            TxtHint.Text = _locked ? "已锁定" : "拖动·双击编辑";
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        // 记住"不显示"：否则下次启动又会被自动浮出来（控制面板 / 托盘可再次打开）
        try
        {
            var s = Store.LoadSettings();
            s.PlacementOf(PlacementKey).Visible = false;
            Store.SaveSettings(s);
        }
        catch { }
    }

    // ---------- 桌面背景（与作业 / 缺交 / 自定义便签同一套：位置 + 设为背景） ----------

    /// <summary>
    /// 一步「设为背景」：把当前的位置与大小锁进桌面背景排版 → 打开显示并重绘壁纸 → 隐藏本便签。
    /// 失败时（如便签在副屏）弹框说明并保留窗口，避免"藏起来了但壁纸上没有"。
    /// </summary>
    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        if (!BackdropPin.Pin(BackdropPin.Kind.Countdown, this, out string msg))
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

        Hide();   // 已经画到壁纸上了，便签不必再占着屏幕
    }

    private void BtnPos_Click(object sender, RoutedEventArgs e)
    {
        // 「位置」直接进入所见即所得排版：在模拟屏幕上拖动便签定位、拉四边改大小
        bool wasTop = Topmost;
        Topmost = false;
        try { new BackdropEditorWindow { Owner = this }.ShowDialog(); }
        catch { }
        finally { Topmost = wasTop; }
        AppEvents.RaiseBackdropChanged();
    }

    /// <summary>「设为背景」按钮的视觉状态：蓝底 = 这张便签当前已画在桌面背景上。</summary>
    private void UpdateBackdropButton()
    {
        if (BtnWp == null) return;
        try { _backdropOn = Store.LoadSettings().Backdrop?.ShowCountdown == true; }
        catch { _backdropOn = false; }

        bool on = _backdropOn;
        RefreshBottomBar();
        BtnWp.ToolTip = on
            ? "倒计时已在桌面背景显示；再点一次会按「现在的位置和大小」重新锁定。要取消：布局设置 → 桌面背景 取消勾选它"
            : "按「现在的位置和大小」一键设为桌面背景，并隐藏本便签";
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.CountdownChanged -= OnCountdownChanged;
        AppEvents.NoteLockChanged -= UpdateLockButton;
        _posTimer?.Stop();
        _dayTimer?.Stop();
        base.OnClosed(e);
    }
}
