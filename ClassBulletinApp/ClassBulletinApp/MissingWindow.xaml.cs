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
public partial class MissingWindow : Window
{
    private DispatcherTimer? _posTimer;
    private string _themeKey = "pink";
    private bool _loading;

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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        Reload();
        UpdateTopButton();
        UpdateBackdropButton();

        AppEvents.MissingChanged += OnMissingChanged;
        AppEvents.BackdropChanged += UpdateBackdropButton;
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
            if (s.MsWinWidth.HasValue && s.MsWinWidth.Value >= MinWidth) Width = s.MsWinWidth.Value;
            if (s.MsWinHeight.HasValue && s.MsWinHeight.Value >= MinHeight) Height = s.MsWinHeight.Value;

            if (s.MsWinLeft.HasValue && s.MsWinTop.HasValue)
            {
                Left = s.MsWinLeft.Value;
                Top = s.MsWinTop.Value;
                ClampIntoScreen();
            }
            else
            {
                var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
                if (area.HasValue)
                {
                    Left = Math.Max(0, area.Value.Left + area.Value.Width - Width - 32);
                    Top = Math.Max(0, area.Value.Top + area.Value.Height - Height - 60);
                }
            }

            Topmost = s.MsWinTopMost;
        }
        catch { }
        finally { _loading = false; }
    }

    /// <summary>若保存的位置已跑到屏幕外（换分辨率 / 拔显示器），拉回可见区域。</summary>
    private void ClampIntoScreen()
    {
        try
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            if (!area.HasValue) return;
            var a = area.Value;
            if (Left > a.Right - 60) Left = Math.Max(a.Left, a.Right - Math.Min(Width, a.Width) - 16);
            if (Left + Width < a.Left + 60) Left = a.Left + 16;
            if (Top > a.Bottom - 40) Top = Math.Max(a.Top, a.Bottom - Math.Min(Height, a.Height) - 16);
            if (Top < a.Top - 40) Top = a.Top + 16;
        }
        catch { }
    }

    private void SavePlacement()
    {
        if (_loading) return;
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
        ApplyTheme(HomeworkThemes.Find(_themeKey));
        TxtTitle.Text = string.IsNullOrWhiteSpace(board.Title) ? "缺交作业名单" : board.Title;
        RebuildContent(board);
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
    }

    private void RebuildContent(MissingBoard board)
    {
        var t = HomeworkThemes.Find(_themeKey);
        var missing = ThemeHelper.Brush(t.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        var muted = ThemeHelper.Brush(t.MutedColor, Color.FromRgb(0x9C, 0xA3, 0xAF));
        var fg = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));

        ContentPanel.Children.Clear();

        var items = (board.Items ?? new()).Where(m => !string.IsNullOrWhiteSpace(m.Students)).ToList();
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
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(m.Subject) ? "缺交：" : "【" + m.Subject + "】缺交：",
                    FontSize = 14, FontWeight = FontWeights.Bold,
                    Foreground = missing, TextWrapping = TextWrapping.Wrap
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

            var st = Store.LoadSettings();
            if (st.Backdrop?.ShowMissing == true)
                BackdropService.Apply(silent: true, out _);

            AppEvents.RaiseMissingChanged();
            AppEvents.RaiseBackdropChanged();
        }
        catch { }
    }

    // ---------- 桌面背景 ----------
    private void UpdateBackdropButton()
    {
        try
        {
            if (BtnWp == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            bool on = b.ShowMissing;
            BtnWp.Content = on ? "桌面背景：开" : "桌面背景：关";
            BtnWp.Background = on ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
            BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
        }
        catch { }
    }

    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        BackdropService.ToggleMissing(out _);
        UpdateBackdropButton();
        AppEvents.RaiseBackdropChanged();
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
            s.MsWinTopMost = Topmost;
            Store.SaveSettings(s);
        }
        catch { }
        UpdateTopButton();
    }

    private void BtnPos_Click(object sender, RoutedEventArgs e) => OpenLayoutWindow();

    private void OpenLayoutWindow()
    {
        try { new LayoutWindow { InitialTabIndex = 2 }.Show(); }
        catch { }
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        AppEvents.MissingChanged -= OnMissingChanged;
        AppEvents.BackdropChanged -= UpdateBackdropButton;
        _posTimer?.Stop();
        base.OnClosed(e);
    }
}
