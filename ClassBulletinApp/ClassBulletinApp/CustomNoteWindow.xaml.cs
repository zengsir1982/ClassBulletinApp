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
/// 自定义便签悬浮窗：用户自建的自由内容便签（教师寄语、温馨提示等），
/// 整块可拖动 / 拉边改大小（WM_NCHITTEST 原生处理），支持主题、置顶、锁定，双击编辑。
/// 摆放、置顶、锁定、是否显示都随便签一起持久化。
/// </summary>
public partial class CustomNoteWindow : Window, IGlassSurface
{
    private readonly string _noteId;
    private CustomNote? _note;
    private DispatcherTimer? _posTimer;
    private string _themeKey = "plain";
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

    public CustomNoteWindow(string noteId)
    {
        _noteId = noteId;
        InitializeComponent();
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _posTimer.Tick += (_, _) => { _posTimer.Stop(); SavePlacement(); };
    
        GlassChrome.Prepare(this);}

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _note = Store.LoadSettings().CustomNotes.FirstOrDefault(n => n.Id == _noteId);
        if (_note == null) { Close(); return; }

        RestorePlacement();
        Reload();
        UpdateTopButton();
        UpdateBackdropButton();
        _locked = _note.Locked;
        UpdateLockButton();
        UpdateHintVisibility();

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            FloatingHelper.ClampToScreen(this);
        };

        AppEvents.CustomNoteChanged += OnCustomNoteChanged;

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
        if (_note == null) return;
        try
        {
            _loading = true;
            if (_note.Width is double sw && sw >= MinWidth) Width = sw;
            if (_note.Height is double sh && sh >= MinHeight) Height = sh;
            Topmost = _note.TopMost;

            var area = FloatingHelper.WorkAreaDip(this);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);

            if (_note.Left is double l && _note.Top is double t && IsUsable(l, t, area))
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
        if (_loading || _note == null) return;
        // 未显示的窗口 Left/Top 是 NaN：写进设置会让整个 settings.json 序列化失败
        if (!FloatingHelper.IsFiniteRect(this)) return;
        try
        {
            var s = Store.LoadSettings();
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == _noteId);
            if (n == null) return;
            n.Left = Left;
            n.Top = Top;
            n.Width = Width;
            n.Height = Height;
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

    /// <summary>
    /// 底栏现在有「编辑 / 置顶 / 锁定 / 位置 / 设为背景」五个按钮，窗口窄时提示文字会被挤掉半个字，
    /// 不如干脆收起：够宽（≥420）才显示，把空间留给按钮。
    /// </summary>
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
        if (_note == null) return;
        _themeKey = string.IsNullOrWhiteSpace(_note.Theme) ? "plain" : _note.Theme;
        var theme = HomeworkThemes.Find(_themeKey);
        ApplyTheme(theme);
        TxtTitle.Text = string.IsNullOrWhiteSpace(_note.Title) ? "自定义便签" : _note.Title;
        ApplyTitleStyle(_note.TitleStyle, theme);
        RebuildContent();
    }

    /// <summary>应用标题的字号 / 颜色 / 粗细（未设置时保持主题默认：加粗 + 主题标题色 + 17 号）。</summary>
    private void ApplyTitleStyle(TextStyle? style, HomeworkTheme t)
    {
        TxtTitle.FontSize = HwTextRender.TitleSize(style);
        TxtTitle.FontWeight = HwTextRender.BoldOr(style, true) ? FontWeights.Bold : FontWeights.Normal;
        TxtTitle.Foreground = ThemeHelper.Brush(
            HwTextRender.ColorOr(style, t.HeaderForeground), Color.FromRgb(0x1B, 0x28, 0x36));
    }

    private void OnCustomNoteChanged(string? id)
    {
        if (id != null && id != _noteId) return;
        _note = Store.LoadSettings().CustomNotes.FirstOrDefault(n => n.Id == _noteId);
        if (_note == null) { Dispatcher.BeginInvoke(new Action(Close)); return; }
        Reload();
        UpdateBackdropButton();
        _locked = _note.Locked;
        UpdateLockButton();
    }

    private void ApplyTheme(HomeworkTheme t)
    {
        NoteBorder.Background = ThemeHelper.Brush(t.Background, Color.FromRgb(0xFF, 0xFF, 0xFF));
        NoteBorder.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xD8, 0xDE, 0xE9));
        Header.Background = ThemeHelper.Brush(t.HeaderBackground, Color.FromRgb(0xE9, 0xEF, 0xF9));
        TxtTitle.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x1B, 0x28, 0x36));
        BtnHide.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x1B, 0x28, 0x36));
        TxtHint.Foreground = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x5B, 0x64, 0x72));
        GlassBrush.Apply(NoteBorder);
    }

    /// <summary>IGlassSurface：设置变更时由 GlassBrush.RefreshAll 统一刷新底色。</summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
        => GlassBrush.Apply(NoteBorder, settings);

    private void RebuildContent()
    {
        if (_note == null) return;
        var t = HomeworkThemes.Find(_themeKey);
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x98, 0xA2, 0xB3));

        ContentPanel.Children.Clear();

        string body = string.IsNullOrWhiteSpace(_note.Content) ? "（暂无内容，双击添加）" : _note.Content;
        // 格式设置：加粗 / 字色（空=随主题正文色）/ 字号档位（与作业公示共用 HwTextStyle）
        var bodyBrush = string.IsNullOrWhiteSpace(_note.Color)
            ? fg
            : ThemeHelper.Brush(_note.Color, Color.FromRgb(0x33, 0x3B, 0x47));
        ContentPanel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = HwTextStyle.SizeOf(_note.SizeLevel) - 4,
            FontWeight = _note.Bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = bodyBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0)
        });

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "更新于 " + _note.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(0, 10, 0, 0)
        });
    }

    // ---------- 编辑对话框 ----------
    private void OpenEditor()
    {
        if (_note == null) return;
        try
        {
            // 编辑期间临时取消置顶，避免便签盖住编辑对话框
            bool wasTop = Topmost;
            Topmost = false;

            var dlg = new CustomNoteEditWindow(_note);
            bool ok = dlg.ShowDialog() == true;

            Topmost = ok ? (_note.TopMost) : wasTop;

            _note = Store.LoadSettings().CustomNotes.FirstOrDefault(x => x.Id == _noteId);
            if (_note == null) { Close(); return; }

            Reload();
            UpdateTopButton();
            _locked = _note.Locked;
            UpdateLockButton();
            AppEvents.RaiseCustomNoteChanged(_noteId);
        }
        catch { }
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;   // 锁定后禁止编辑
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
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == _noteId);
            if (n != null) { n.TopMost = Topmost; Store.SaveSettings(s); }
        }
        catch { }
        UpdateTopButton();
    }

    // ---------- 锁定 ----------
    private void UpdateLockButton()
    {
        if (BtnLock == null) return;
        bool locked = _locked;
        BtnLock.Content = locked ? "🔒" : "🔓";
        BtnLock.Background = locked ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
        BtnLock.Foreground = locked ? Brushes.White : Brushes.Black;
        if (TxtHint != null)
            TxtHint.Text = locked ? "已锁定" : "拖动·双击编辑";
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e)
    {
        _locked = !_locked;
        try
        {
            var s = Store.LoadSettings();
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == _noteId);
            if (n != null) { n.Locked = _locked; Store.SaveSettings(s); }
        }
        catch { }
        UpdateLockButton();
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) => Hide();

    // ---------- 桌面背景（与作业 / 缺交便签同一套：位置 + 设为背景） ----------

    /// <summary>
    /// 一步「设为背景」：把当前的位置与大小锁进桌面背景排版 → 打开显示并重绘壁纸 → 隐藏本便签。
    /// 失败时（如便签在副屏）弹框说明并保留窗口，避免"藏起来了但壁纸上没有"。
    /// </summary>
    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        if (!BackdropPin.Pin(BackdropPin.Kind.CustomNote, _noteId, this, out string msg))
        {
            System.Windows.MessageBox.Show(msg, "设为背景", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateBackdropButton();
        AppEvents.RaiseBackdropChanged();

        // 内容已经在壁纸上了：把"是否显示"也置为否，否则下次启动会照旧把它浮出来，等于同一份内容显示两遍
        try
        {
            var s = Store.LoadSettings();
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == _noteId);
            if (n != null) { n.Visible = false; Store.SaveSettings(s); }
        }
        catch { }

        Hide();   // 已经画到壁纸上了，便签不必再占着屏幕（可在控制面板 / 悬浮课表「便签」再次打开）
    }

    private void BtnPos_Click(object sender, RoutedEventArgs e)
    {
        // 「位置」直接进入所见即所得排版：在模拟屏幕上拖动便签定位、拉四边改大小
        bool wasTop = Topmost;
        Topmost = false;   // 排版窗口是模态的，先让便签别压住它
        try { new BackdropEditorWindow { Owner = this }.ShowDialog(); }
        catch { }
        finally { Topmost = wasTop; }
        AppEvents.RaiseBackdropChanged();
    }

    /// <summary>「设为背景」按钮的视觉状态：蓝底 = 这张便签当前已画在桌面背景上。</summary>
    private void UpdateBackdropButton()
    {
        if (BtnWp == null) return;
        bool on = false;
        try
        {
            var b = Store.LoadSettings().Backdrop;
            on = b?.CustomNotes != null
                 && b.CustomNotes.TryGetValue(_noteId, out var entry)
                 && entry != null && entry.Visible;
        }
        catch { }

        BtnWp.Background = on ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
        BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
        BtnWp.ToolTip = on
            ? "这张便签已在桌面背景显示；再点一次会按「现在的位置和大小」重新锁定。要取消：布局设置 → 桌面背景 取消勾选它"
            : "按「现在的位置和大小」一键设为桌面背景，并隐藏本便签";
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.CustomNoteChanged -= OnCustomNoteChanged;
        _posTimer?.Stop();
        base.OnClosed(e);
    }
}
