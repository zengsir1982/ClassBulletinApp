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
public partial class CustomNoteWindow : Window
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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _note = Store.LoadSettings().CustomNotes.FirstOrDefault(n => n.Id == _noteId);
        if (_note == null) { Close(); return; }

        RestorePlacement();
        Reload();
        UpdateTopButton();
        _locked = _note.Locked;
        UpdateLockButton();

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
        if (_note == null) return;
        _themeKey = string.IsNullOrWhiteSpace(_note.Theme) ? "plain" : _note.Theme;
        ApplyTheme(HomeworkThemes.Find(_themeKey));
        TxtTitle.Text = string.IsNullOrWhiteSpace(_note.Title) ? "自定义便签" : _note.Title;
        RebuildContent();
    }

    private void OnCustomNoteChanged(string? id)
    {
        if (id != null && id != _noteId) return;
        _note = Store.LoadSettings().CustomNotes.FirstOrDefault(n => n.Id == _noteId);
        if (_note == null) { Dispatcher.BeginInvoke(new Action(Close)); return; }
        Reload();
        _locked = _note.Locked;
        UpdateLockButton();
    }

    private void ApplyTheme(HomeworkTheme t)
    {
        NoteBorder.Background = ThemeHelper.Brush(t.Background, Color.FromRgb(0xFF, 0xFF, 0xFF));
        NoteBorder.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xD8, 0xDE, 0xE9));
        Header.Background = ThemeHelper.Brush(t.HeaderBackground, Color.FromRgb(0xEA, 0xF1, 0xFD));
        TxtTitle.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x1F, 0x2A, 0x37));
        BtnHide.Foreground = ThemeHelper.Brush(t.HeaderForeground, Color.FromRgb(0x1F, 0x2A, 0x37));
        TxtHint.Foreground = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x5B, 0x64, 0x72));
    }

    private void RebuildContent()
    {
        if (_note == null) return;
        var t = HomeworkThemes.Find(_themeKey);
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x9C, 0xA3, 0xAF));

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
        BtnTop.Background = top ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
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
        BtnLock.Background = locked ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
        BtnLock.Foreground = locked ? Brushes.White : Brushes.Black;
        if (TxtHint != null)
            TxtHint.Text = locked ? "已锁定：不能移动 / 缩放 / 编辑" : "拖动摆放 · 拉边沿改大小 · 双击编辑";
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

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.CustomNoteChanged -= OnCustomNoteChanged;
        _posTimer?.Stop();
        base.OnClosed(e);
    }
}
