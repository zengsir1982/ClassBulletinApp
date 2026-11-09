using System;
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
/// 缺交作业名单便签（独立于作业公示）：整块可拖到屏幕任意位置固定摆放（位置 / 大小持久化），
/// 支持多种便签主题、拖边沿改大小、置顶或隐藏、双击弹出编辑对话框；
/// 底部「桌面背景」一键投射，旁边「位置」直接进入桌面便签定位设置。
/// 拖动与缩放交由系统原生处理（WM_NCHITTEST），不拦截任何交互。
/// </summary>
public partial class MissingWindow : Window, IGlassSurface
{
    private DispatcherTimer? _posTimer;
    private string _themeKey = "pink";
    private bool _loading;

    /// <summary>是否锁定（锁定后不能移动 / 缩放 / 编辑，防止误操作）。</summary>
    private bool _locked;

    /// <summary>本次是否因存档坐标失效而重新居中（是则立刻把修正后的位置写回，避免每次都重算）。</summary>
    private bool _recentered;

    // ---------- WM_NCHITTEST 原生拖动 / 缩放 ----------
    private const int WM_NCHITTEST = 0x84;
    private const int WM_NCLBUTTONDBLCLK = 0xA3;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const double RESIZE_MARGIN = 8;

    public MissingWindow()
    {
        InitializeComponent();
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); SavePlacement(); };
    
        GlassChrome.Prepare(this);}

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        Reload();
        UpdateTopButton();
        UpdateBackdropButton();
        _locked = Store.LoadSettings().MsWinLocked;
        UpdateLockButton();

        // 每次显示都再收一次屏（运行中改分辨率 / 拔显示器 / 改缩放后仍然看得见）
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            FloatingHelper.ClampToScreen(this);
        };

        AppEvents.MissingChanged += OnMissingChanged;
        AppEvents.BackdropChanged += UpdateBackdropButton;
        AppEvents.NoteLockChanged += OnNoteLockChanged;

        // 存档坐标已失效时，把重新居中后的位置立刻写回，避免每次启动都重算
        if (_recentered) SavePlacement();
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var src = PresentationSource.FromVisual(this) as HwndSource;
        src?.AddHook(WndProc);
    }

    /// <summary>
    /// 整块便签都是拖动区（HTCAPTION，系统平滑拖动，可摆到任意位置），按钮除外；
    /// 四边 / 四角保留原生缩放；双击（WM_NCLBUTTONDBLCLK）打开编辑对话框。
    /// </summary>
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

            // 按钮保持可点击，其余整块作为拖动区
            var hit = VisualTreeHelper.HitTest(this, client)?.VisualHit;
            while (hit != null)
            {
                if (hit is System.Windows.Controls.Button) { handled = true; return (IntPtr)HTCLIENT; }
                hit = VisualTreeHelper.GetParent(hit);
            }
            handled = true;
            return (IntPtr)HTCAPTION;
        }

        // 双击便签 → 打开编辑对话框；同时吞掉系统默认的「最大化」
        if (msg == WM_NCLBUTTONDBLCLK)
        {
            if (_locked) { handled = true; return IntPtr.Zero; }   // 锁定后禁止编辑
            handled = true;
            Dispatcher.BeginInvoke(new Action(OpenEditor));
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ---------- 位置 / 大小 / 置顶持久化 ----------
    private void RestorePlacement()
    {
        try
        {
            _loading = true;
            var s = Store.LoadSettings();
            if (s.MsWinWidth is double sw && sw >= MinWidth) Width = sw;
            if (s.MsWinHeight is double sh && sh >= MinHeight) Height = sh;
            Topmost = s.MsWinTopMost;

            // DPI 感知收屏（逻辑像素 vs 物理像素必须换算），否则 125% 缩放下会算到屏幕外
            var area = FloatingHelper.WorkAreaDip(this);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);

            if (s.MsWinLeft is double l && s.MsWinTop is double t && IsUsable(l, t, area))
            {
                Left = l;
                Top = t;
                FloatingHelper.ClampToScreen(this);
            }
            else
            {
                // 首次打开，或存档坐标已失效（换显示器 / 改分辨率 / 改缩放）：默认摆在屏幕正中央，
                // 再往右下错开一点，避免和作业便签完全重叠（两张便签同时打开时都能看到标题）
                FloatingHelper.CenterOnScreen(this);
                Left += 36;
                Top += 36;
                FloatingHelper.ClampToScreen(this);
                _recentered = true;
            }
        }
        catch { }
        finally { _loading = false; }
    }

    /// <summary>存档坐标是否还可用：至少 55% 的面积落在工作区内，否则视为失效（避免窗口「打开后看不见」）。</summary>
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
            s.MsWinLeft = Left;
            s.MsWinTop = Top;
            s.MsWinWidth = Width;
            s.MsWinHeight = Height;
            Store.SaveSettings(s);
        }
        catch { }
    }

    private void Window_LocationChanged(object sender, EventArgs e) => DebounceSavePlacement();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => DebounceSavePlacement();

    private void DebounceSavePlacement()
    {
        if (_loading) return;
        _posTimer?.Stop();
        _posTimer?.Start();
    }

    // ---------- 内容展示 ----------
    private void Reload()
    {
        var board = Store.LoadMissingBoard();
        _themeKey = string.IsNullOrWhiteSpace(board.Theme) ? "pink" : board.Theme;
        var theme = HomeworkThemes.Find(_themeKey);
        ApplyTheme(theme);
        TxtTitle.Text = string.IsNullOrWhiteSpace(board.Title) ? "缺交作业名单" : board.Title;
        ApplyTitleStyle(board.TitleStyle, theme);
        RebuildContent(board);
    }

    /// <summary>应用总标题的字号 / 颜色 / 粗细（未设置时保持主题默认：加粗 + 主题标题色 + 17 号）。</summary>
    private void ApplyTitleStyle(TextStyle? style, HomeworkTheme t)
    {
        TxtTitle.FontSize = HwTextRender.TitleSize(style);
        TxtTitle.FontWeight = HwTextRender.BoldOr(style, true) ? FontWeights.Bold : FontWeights.Normal;
        TxtTitle.Foreground = ThemeHelper.Brush(
            HwTextRender.ColorOr(style, t.HeaderForeground), Color.FromRgb(0xAD, 0x14, 0x57));
    }

    private void OnMissingChanged() => Reload();

    private void ApplyTheme(HomeworkTheme t)
    {
        NoteBorder.Background = ThemeHelper.Brush(t.Background, Color.FromRgb(0xFC, 0xE4, 0xEC));
        NoteBorder.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xF8, 0xBB, 0xD0));
        Header.Background = ThemeHelper.Brush(t.HeaderBackground, Color.FromRgb(0xF8, 0xBB, 0xD0));
        TxtTitle.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0xAD, 0x14, 0x57));
        BtnHide.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0xAD, 0x14, 0x57));
        TxtHint.Foreground = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x8E, 0x5A, 0x66));
        GlassBrush.Apply(NoteBorder);
    }

    /// <summary>IGlassSurface：设置变更时由 GlassBrush.RefreshAll 统一刷新底色。</summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
        => GlassBrush.Apply(NoteBorder, settings);

    private void RebuildContent(MissingBoard board)
    {
        var t = HomeworkThemes.Find(_themeKey);
        var missing = ThemeHelper.Brush(t.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x98, 0xA2, 0xB3));
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));

        ContentPanel.Children.Clear();

        var items = board.LiveItems.Where(m => !string.IsNullOrWhiteSpace(m.Students)).ToList();
        if (items.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "（暂无缺交记录，双击添加）",
                FontSize = 14, Foreground = muted, Margin = new Thickness(0, 4, 0, 0)
            });
        }
        else
        {
            foreach (var m in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                // 科目行默认加粗 + 主题缺交色；字号 / 颜色可在编辑窗里单独调
                var ss = m.SubjectStyle;
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(m.Subject) ? "缺交：" : "【" + m.Subject + "】缺交：",
                    FontSize = HwTextRender.MissingSubjectSize(ss),
                    FontWeight = HwTextRender.BoldOr(ss, true) ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(HwTextRender.ColorOr(ss, t.MissingColor),
                                                   Color.FromRgb(0xC0, 0x39, 0x2B)),
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(new TextBlock
                {
                    Text = m.Students,
                    FontSize = Math.Max(10, HwTextStyle.SizeOf(m.SizeLevel) - 4),
                    FontWeight = m.Bold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = string.IsNullOrWhiteSpace(m.Color) ? fg : ThemeHelper.Brush(m.Color, Color.FromRgb(0x33, 0x3B, 0x47)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                ContentPanel.Children.Add(row);
            }
        }

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = 11, Foreground = muted, Margin = new Thickness(0, 8, 0, 0)
        });
    }

    // ---------- 编辑对话框 ----------
    private void OpenEditor()
    {
        try
        {
            var board = Store.LoadMissingBoard();
            var dlg = new MissingEditWindow(board);
            if (dlg.ShowDialog() != true) return;

            var saved = dlg.BuildBoard();
            Store.SaveMissingBoard(saved);
            if (Store.LastWriteError is string err)
            {
                System.Windows.MessageBox.Show("缺交名单没有保存：" + err, "保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 同作业公示：壁纸交给 MissingChanged 的 1.2s 防抖订阅
            AppEvents.RaiseMissingChanged();
            AppEvents.RaiseBackdropChanged();
        }
        catch { }
    }

    // ---------- 设为背景 ----------
    /// <summary>
    /// 同步「设为背景」按钮状态：文案恒为「设为背景」（再点一次 = 按当前大小位置重新锁一次），
    /// 蓝底表示缺交名单当前已在桌面背景显示。
    /// </summary>
    private void UpdateBackdropButton()
    {
        try
        {
            if (BtnWp == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            bool on = b.ShowMissing;
            BtnWp.Background = on ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
            BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
            BtnWp.ToolTip = on
                ? "缺交名单已在桌面背景显示；再点一次会按「现在的位置和大小」重新锁定。要取消：布局设置 → 桌面背景 取消勾选"
                : "按「现在的位置和大小」一键设为桌面背景，并隐藏本便签";
        }
        catch { }
    }

    /// <summary>
    /// 一步「设为背景」：把当前的位置与大小锁进桌面背景排版 → 打开显示并重绘壁纸 → 隐藏本便签。
    /// 失败（如便签在副屏）时不隐藏，弹框说明原因。
    /// </summary>
    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        if (!BackdropPin.Pin(BackdropPin.Kind.Missing, this, out string msg))
        {
            System.Windows.MessageBox.Show(msg, "设为背景", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateBackdropButton();
        AppEvents.RaiseBackdropChanged();
        Hide();   // 已画到桌面背景上，便签不必再占着屏幕（控制面板可再次显示）
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
            s.MsWinTopMost = Topmost;
            Store.SaveSettings(s);
        }
        catch { }
        UpdateTopButton();
    }

    private void BtnPos_Click(object sender, RoutedEventArgs e) => OpenLayoutWindow();

    private void OpenLayoutWindow()
    {
        // 「位置」直接进入所见即所得排版：在模拟屏幕上拖动便签定位、拉四边改大小
        try { new BackdropEditorWindow { Owner = this }.ShowDialog(); }
        catch { }
        AppEvents.RaiseBackdropChanged();
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) => Hide();

    // ---------- 锁定 ----------
    private void UpdateLockButton()
    {
        if (BtnLock == null) return;
        bool locked = _locked;
        BtnLock.Content = locked ? "🔒" : "🔓";
        BtnLock.Background = locked ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
        BtnLock.Foreground = locked ? Brushes.White : Brushes.Black;
        if (TxtHint != null)
            TxtHint.Text = locked ? "已锁定：不能移动 / 缩放 / 编辑" : "拖动摆放 · 拉边沿改大小 · 双击编辑";
    }

    // 编辑窗里改了「锁定便签」：重读设置并刷新锁头图标，不等重启
    private void OnNoteLockChanged()
    {
        _locked = Store.LoadSettings().MsWinLocked;
        UpdateLockButton();
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e)
    {
        _locked = !_locked;
        try
        {
            var s = Store.LoadSettings();
            s.MsWinLocked = _locked;
            Store.SaveSettings(s);
        }
        catch { }
        UpdateLockButton();
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.MissingChanged -= OnMissingChanged;
        AppEvents.BackdropChanged -= UpdateBackdropButton;
        AppEvents.NoteLockChanged -= OnNoteLockChanged;
        _posTimer?.Stop();
        base.OnClosed(e);
    }
}
