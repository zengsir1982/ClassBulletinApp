using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// 作业公示悬浮窗：可随时点击修改各科作业与缺交作业名单（增 / 删 / 改），
/// 改动后自动保存到 homework.json；右下角「桌面背景」开关可一键把作业公示（含缺交名单）画到桌面壁纸。
/// 窗口拖动与边缘缩放交由系统原生处理（WM_NCHITTEST），避免手动拖拽抖动，也不拦截文本框交互。
/// </summary>
public partial class HomeworkWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<HomeworkItem> _items = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<MissingHomeworkItem> _miss = new();
    private DispatcherTimer? _saveTimer;

    /// <summary>正在执行内部保存（由本窗口触发）时为真，避免自我触发的 HomeworkChanged 把正在编辑的内容重载掉。</summary>
    private bool _suppressReload;

    // ---------- WM_NCHITTEST 原生拖动 / 缩放 ----------
    private const int WM_NCHITTEST = 0x84;
    private const int WM_NCLBUTTONDBLCLK = 0xA3;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const double RESIZE_MARGIN = 8;

    public HomeworkWindow()
    {
        InitializeComponent();
        HwItems.ItemsSource = _items;
        MissItems.ItemsSource = _miss;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            if (area.HasValue)
            {
                Left = Math.Max(0, area.Value.Left + 24);
                Top = Math.Max(0, area.Value.Top + 24);
            }
        }
        catch { }

        ReloadFromDisk();
        UpdateBackdropButton();

        AppEvents.HomeworkChanged += OnHomeworkChanged;
        AppEvents.BackdropChanged += UpdateBackdropButton;
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var src = PresentationSource.FromVisual(this) as HwndSource;
        src?.AddHook(WndProc);
    }

    /// <summary>让系统原生处理拖动（标题栏）与边缘缩放：头部空白处返回 HTCAPTION（平滑拖动，无抖动），
    /// 四边 / 四角返回缩放代码（原生缩放）；标题、隐藏按钮、标题编辑框视为普通客户区（可交互）。
    /// 同时吞掉标题栏双击最大化，避免误触。</summary>
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

            // 头部区域：标题（显示/编辑）与隐藏按钮作为普通客户区（可点击 / 双击编辑），
            // 其余头部空白作为拖动区（HTCAPTION），由系统平滑拖动。
            if (client.Y <= Header.ActualHeight && Header.IsVisible)
            {
                var hit = VisualTreeHelper.HitTest(this, client)?.VisualHit;
                bool interactive = false;
                while (hit != null)
                {
                    if (ReferenceEquals(hit, TxtTitle) || ReferenceEquals(hit, TxtTitleEdit) || ReferenceEquals(hit, BtnHide))
                    { interactive = true; break; }
                    hit = VisualTreeHelper.GetParent(hit);
                }
                if (!interactive) { handled = true; return (IntPtr)HTCAPTION; }
            }
            handled = true;
            return (IntPtr)HTCLIENT;
        }

        // 吞掉非客户区双击（避免在标题栏空白处双击意外最大化窗口）
        if (msg == WM_NCLBUTTONDBLCLK) { handled = true; return IntPtr.Zero; }

        return IntPtr.Zero;
    }

    private void ReloadFromDisk()
    {
        var board = Store.LoadHomework();
        _suppressReload = true;
        try
        {
            TxtTitle.Text = board?.Title ?? "各科作业公示";
            _items.Clear();
            if (board?.Items != null)
                foreach (var it in board.Items) _items.Add(it);
            if (_items.Count == 0) _items.Add(new HomeworkItem());

            _miss.Clear();
            if (board?.Missing != null)
                foreach (var m in board.Missing) _miss.Add(m);
        }
        finally { _suppressReload = false; }
        UpdateEmptyHints();
    }

    private void OnHomeworkChanged()
    {
        if (_suppressReload) return;
        ReloadFromDisk();
        UpdateBackdropButton();
    }

    // ---------- 标题编辑（双击进入 / 失焦或回车提交 / Esc 取消） ----------
    private void TxtTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;   // 仅双击进入编辑
        TxtTitle.Visibility = Visibility.Collapsed;
        TxtTitleEdit.Visibility = Visibility.Visible;
        TxtTitleEdit.Text = TxtTitle.Text;
        TxtTitleEdit.Focus();
        TxtTitleEdit.SelectAll();
    }

    private void TxtTitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitTitle();

    private void TxtTitleEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTitle(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelTitle(); e.Handled = true; }
    }

    private void CommitTitle()
    {
        if (TxtTitleEdit.Visibility != Visibility.Visible) return;
        TxtTitle.Text = string.IsNullOrWhiteSpace(TxtTitleEdit.Text) ? "各科作业公示" : TxtTitleEdit.Text.Trim();
        TxtTitleEdit.Visibility = Visibility.Collapsed;
        TxtTitle.Visibility = Visibility.Visible;
        DebounceSave();
    }

    private void CancelTitle()
    {
        TxtTitleEdit.Visibility = Visibility.Collapsed;
        TxtTitle.Visibility = Visibility.Visible;
    }

    // ---------- 编辑 ----------
    private void Editable_TextChanged(object sender, TextChangedEventArgs e) => DebounceSave();

    private void BtnAddHw_Click(object sender, RoutedEventArgs e)
    {
        _items.Add(new HomeworkItem());
        UpdateEmptyHints();
        DebounceSave();
    }

    private void BtnDelHw_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is HomeworkItem it)
        {
            _items.Remove(it);
            if (_items.Count == 0) _items.Add(new HomeworkItem());
            UpdateEmptyHints();
            DebounceSave();
        }
    }

    private void BtnAddMiss_Click(object sender, RoutedEventArgs e)
    {
        _miss.Add(new MissingHomeworkItem());
        UpdateEmptyHints();
        DebounceSave();
    }

    private void BtnDelMiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is MissingHomeworkItem m)
        {
            _miss.Remove(m);
            UpdateEmptyHints();
            DebounceSave();
        }
    }

    private void UpdateEmptyHints()
    {
        if (TxtHwEmpty != null)
        {
            bool empty = _items.Count <= 1
                && string.IsNullOrWhiteSpace(_items[0]?.Subject)
                && string.IsNullOrWhiteSpace(_items[0]?.Content);
            TxtHwEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtMissEmpty != null)
            TxtMissEmpty.Visibility = _miss.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DebounceSave()
    {
        if (_suppressReload) return;   // 重载过程中不触发保存，避免回写噪声
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    // ---------- 文本格式（加粗 / 颜色 / 字号档位） ----------
    private void Fmt_Changed(object sender, RoutedEventArgs e) => DebounceSave();

    private HomeworkBoard BuildBoard()
    {
        return new HomeworkBoard
        {
            Title = (TxtTitle.Text ?? "").Trim(),
            Items = _items
                .Where(x => !string.IsNullOrWhiteSpace(x.Subject) || !string.IsNullOrWhiteSpace(x.Content))
                .Select(x => new HomeworkItem
                {
                    Subject = (x.Subject ?? "").Trim(),
                    Content = (x.Content ?? "").Trim(),
                    Bold = x.Bold,
                    Color = x.Color ?? "",
                    SizeLevel = x.SizeLevel
                })
                .ToList(),
            Missing = _miss
                .Where(m => !string.IsNullOrWhiteSpace(m.Subject) || !string.IsNullOrWhiteSpace(m.Students))
                .Select(m => new MissingHomeworkItem
                {
                    Subject = (m.Subject ?? "").Trim(),
                    Students = (m.Students ?? "").Trim(),
                    Bold = m.Bold,
                    Color = m.Color ?? "",
                    SizeLevel = m.SizeLevel
                })
                .ToList(),
            UpdatedAt = DateTime.Now,
            Layout = Store.LoadSettings().Backdrop?.HomeworkLayout ?? new HomeworkWallpaperLayout()
        };
    }

    private void SaveNow()
    {
        try
        {
            var board = BuildBoard();
            Store.SaveHomework(board);

            // 若作业公示已在桌面背景显示，则同步重绘（含缺交名单）
            var s = Store.LoadSettings();
            if (s.Backdrop?.ShowHomework == true)
                BackdropService.Apply(silent: true, out _);

            _suppressReload = true;
            try
            {
                AppEvents.RaiseHomeworkChanged();
                AppEvents.RaiseBackdropChanged();
            }
            finally { _suppressReload = false; }
        }
        catch { }
    }

    // ---------- 桌面背景开关（窗口右下角） ----------
    private void UpdateBackdropButton()
    {
        try
        {
            if (BtnWp == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            bool on = b.ShowHomework;
            BtnWp.Content = on ? "桌面背景：开" : "桌面背景：关";
            BtnWp.Background = on
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF))
                : new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFD));
            BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
            TxtWpState.Text = on
                ? "作业公示已在桌面背景显示（不遮挡操作）"
                : "点右侧可把作业公示画到桌面壁纸";
        }
        catch { }
    }

    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        BackdropService.ToggleHomework(out string msg);
        UpdateBackdropButton();
        AppEvents.RaiseBackdropChanged();
        TxtWpState.Text = msg;
    }

    // ---------- 桌面背景位置设置（窗口左下角） ----------
    private void BtnPos_Click(object sender, RoutedEventArgs e)
    {
        try { new LayoutWindow { InitialTabIndex = 2 }.Show(); }
        catch { }
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosed(EventArgs e)
    {
        SaveNow();   // 关闭前确保落盘
        AppEvents.HomeworkChanged -= OnHomeworkChanged;
        AppEvents.BackdropChanged -= UpdateBackdropButton;
        _saveTimer?.Stop();
        base.OnClosed(e);
    }
}
