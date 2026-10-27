using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// </summary>
public partial class HomeworkWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<HomeworkItem> _items = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<MissingHomeworkItem> _miss = new();
    private DispatcherTimer? _saveTimer;

    /// <summary>正在执行内部保存（由本窗口触发）时为真，避免自我触发的 HomeworkChanged 把正在编辑的内容重载掉。</summary>
    private bool _suppressReload;

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

    private HomeworkBoard BuildBoard()
    {
        return new HomeworkBoard
        {
            Title = (TxtTitle.Text ?? "").Trim(),
            Items = _items
                .Where(x => !string.IsNullOrWhiteSpace(x.Subject) || !string.IsNullOrWhiteSpace(x.Content))
                .Select(x => new HomeworkItem { Subject = (x.Subject ?? "").Trim(), Content = (x.Content ?? "").Trim() })
                .ToList(),
            Missing = _miss
                .Where(m => !string.IsNullOrWhiteSpace(m.Subject) || !string.IsNullOrWhiteSpace(m.Students))
                .Select(m => new MissingHomeworkItem { Subject = (m.Subject ?? "").Trim(), Students = (m.Students ?? "").Trim() })
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

    private void BtnHide_Click(object sender, RoutedEventArgs e) => Hide();

    // ---------- 拖拽移动窗口（标题区 / 头部可拖，标题文本框与按钮除外） ----------
    private bool _dragging;
    private System.Windows.Point _dragStart;
    private double _dragLeft, _dragTop;

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (ReferenceEquals(e.OriginalSource, TxtTitle)) return;   // 点标题编辑时不拖拽
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragLeft = Left;
        _dragTop = Top;
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Left = _dragLeft + (p.X - _dragStart.X);
        Top = _dragTop + (p.Y - _dragStart.Y);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            if (Mouse.Captured == this) Mouse.Capture(null);
        }
    }

    private static bool IsWithinButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is System.Windows.Controls.Button) return true;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveNow();   // 关闭前确保落盘
        AppEvents.HomeworkChanged -= OnHomeworkChanged;
        AppEvents.BackdropChanged -= UpdateBackdropButton;
        _saveTimer?.Stop();
        base.OnClosed(e);
    }
}
