using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class MainWindow : Window
{
    private bool _isFull;
    private bool _locked;
    private bool _exiting;

    private ScheduleWindow? _schWin;
    private NotificationWindow? _notifWin;
    private bool _schOn, _notOn;
    private System.Windows.Forms.NotifyIcon? _tray;

    // 文档前台自动隐藏
    private readonly DispatcherTimer _docTimer;
    private bool _docActive;

    public ObservableCollection<string> ClassNames { get; } = new();
    public ObservableCollection<Notification> Notifications { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        CbClass.ItemsSource = ClassNames;
        NotificationsPanel.ItemsSource = Notifications;

        // 前台文档/演示程序监视（2 秒轮询，开销极小）
        _docTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _docTimer.Tick += (_, _) => CheckDocForeground();
        _docTimer.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = Store.LoadSettings();
        try
        {
            var schedule = Store.LoadSchedule();
            if (schedule != null)
            {
                foreach (var c in schedule.Classes) ClassNames.Add(c.ClassName);
                if (!string.IsNullOrEmpty(settings.SelectedClass) && ClassNames.Contains(settings.SelectedClass))
                    CbClass.SelectedItem = settings.SelectedClass;
                else if (ClassNames.Count > 0)
                    CbClass.SelectedIndex = 0;
                TxtStatus.Text = $"已加载 {ClassNames.Count} 个班级课表。";
            }
            else
            {
                TxtStatus.Text = "尚未导入课表，请到「数据管理」导入全校总课程表 Excel 文件。";
            }

            ChkDocHide.IsChecked = settings.AutoHideOnDocOpen;
            ReloadNotes();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "加载数据出错：" + ex.Message;
        }

        // 托盘初始化失败不应拖垮主窗口
        try { SetupTray(); }
        catch (Exception ex) { TxtStatus.Text += " [托盘初始化失败：" + ex.Message + "]"; }

        // 悬浮窗创建失败也不影响控制面板
        try
        {
            if (settings.AutoShowSchedule) ShowScheduleWin();
            if (settings.AutoShowNotification) ShowNotificationWin();
        }
        catch (Exception ex) { TxtStatus.Text += " [悬浮窗启动失败：" + ex.Message + "]"; }

        UpdateToggleLabels();

        try
        {
            if (settings.LaunchFullscreen || settings.LaunchLocked) SetFullscreen(true);
            if (settings.LaunchLocked) ToggleLock();
        }
        catch { }
    }

    // ---------- 悬浮窗管理 ----------
    private void ShowScheduleWin()
    {
        if (_schWin == null)
        {
            _schWin = new ScheduleWindow();
            _schWin.Closed += (_, _) => { _schWin = null; _schOn = false; UpdateToggleLabels(); };
        }
        _schWin.Show();
        _schOn = true;
        UpdateToggleLabels();
    }

    private void HideScheduleWin()
    {
        _schWin?.Hide();
        _schOn = false;
        UpdateToggleLabels();
    }

    private void ShowNotificationWin()
    {
        if (_notifWin == null)
        {
            _notifWin = new NotificationWindow();
            _notifWin.Closed += (_, _) => { _notifWin = null; _notOn = false; UpdateToggleLabels(); };
        }
        _notifWin.Show();
        _notOn = true;
        UpdateToggleLabels();
    }

    private void HideNotificationWin()
    {
        _notifWin?.Hide();
        _notOn = false;
        UpdateToggleLabels();
    }

    private void UpdateToggleLabels()
    {
        BtnToggleSch.Content = _schOn ? "课表悬浮：开" : "课表悬浮：关";
        BtnToggleNot.Content = _notOn ? "通知悬浮：开" : "通知悬浮：关";
    }

    private void BtnToggleSch_Click(object sender, RoutedEventArgs e)
    {
        if (_schOn) HideScheduleWin(); else ShowScheduleWin();
    }

    private void BtnToggleNot_Click(object sender, RoutedEventArgs e)
    {
        if (_notOn) HideNotificationWin(); else ShowNotificationWin();
    }

    private void BtnLayout_Click(object sender, RoutedEventArgs e)
    {
        var w = new LayoutWindow { Owner = this };
        w.ShowDialog();
    }

    // ---------- 托盘 ----------
    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "班级公布栏",
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var miPanel = new System.Windows.Forms.ToolStripMenuItem("打开控制面板", null, (_, _) => ShowMainWindow());
        var miSch = new System.Windows.Forms.ToolStripMenuItem("课表悬浮：开/关", null, (_, _) => BtnToggleSch_Click(this, new RoutedEventArgs()));
        var miNot = new System.Windows.Forms.ToolStripMenuItem("通知悬浮：开/关", null, (_, _) => BtnToggleNot_Click(this, new RoutedEventArgs()));
        var miExit = new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitApp());
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { miPanel, miSch, miNot, miExit });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void BtnHideTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void ExitApp()
    {
        _exiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _tray?.Dispose();
        _schWin?.Close();
        _notifWin?.Close();
        base.OnClosing(e);
    }

    // ---------- 班级选择 ----------
    private void CbClass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cls = CbClass.SelectedItem as string;
        if (cls != null)
        {
            var s = Store.LoadSettings();
            s.SelectedClass = cls;
            Store.SaveSettings(s);
        }
        AppEvents.RaiseScheduleChanged();
    }

    // ---------- 导入课表 ----------
    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            Title = "选择全校总课程表"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sf = ScheduleImporter.Import(dlg.FileName);
            Store.SaveSchedule(sf);
            ClassNames.Clear();
            foreach (var c in sf.Classes) ClassNames.Add(c.ClassName);
            if (ClassNames.Count > 0) CbClass.SelectedIndex = 0;

            var st = Store.LoadSettings();
            st.LastImportPath = dlg.FileName;
            Store.SaveSettings(st);

            TxtStatus.Text = $"导入成功，共 {ClassNames.Count} 个班级。";
            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入失败：" + ex.Message;
        }
    }

    // ---------- 导入临时课表（含 调/代 标记） ----------
    private void BtnImportTemp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            Title = "选择全校班级临时总课表"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (sf, tempOverrides) = ScheduleImporter.ImportTempSchedule(dlg.FileName);
            Store.SaveSchedule(sf);
            ClassNames.Clear();
            foreach (var c in sf.Classes) ClassNames.Add(c.ClassName);
            if (ClassNames.Count > 0) CbClass.SelectedIndex = 0;

            // 合并调/代覆盖：移除旧的导入来源，保留手动记录，再写入本次导入结果
            var list = Store.LoadOverrides();
            list.RemoveAll(x => x.Source == "temp");
            list.AddRange(tempOverrides);
            Store.SaveOverrides(list);

            var st = Store.LoadSettings();
            st.LastImportPath = dlg.FileName;
            Store.SaveSettings(st);

            int tiao = tempOverrides.Count(o => o.Type == "调");
            int dai = tempOverrides.Count(o => o.Type == "代");
            TxtStatus.Text = $"已导入临时课表，共 {ClassNames.Count} 个班级；调课 {tiao} 条、代课 {dai} 条。";
            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入临时课表失败：" + ex.Message;
        }
    }

    // ---------- 全屏 / 锁定 ----------
    private void BtnFull_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_isFull);

    private void SetFullscreen(bool on)
    {
        _isFull = on;
        if (on)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            BtnFull.Content = "退出全屏";
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            BtnFull.Content = "全屏";
        }
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e) => ToggleLock();

    private void ToggleLock()
    {
        _locked = !_locked;
        LockOverlay.Visibility = _locked ? Visibility.Visible : Visibility.Collapsed;
        Cursor = _locked ? System.Windows.Input.Cursors.None : System.Windows.Input.Cursors.Arrow;
        BtnLock.Content = _locked ? "解锁" : "锁定";
    }

    // ---------- 设置 ----------
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow { Owner = this };
        if (w.ShowDialog() == true)
            AppEvents.RaiseScheduleChanged();   // 开学周等变化后刷新周视图
    }

    // ---------- 清除全部临时调课 ----------
    private void BtnClearOverrides_Click(object sender, RoutedEventArgs e)
    {
        var list = Store.LoadOverrides();
        if (list.Count == 0)
        {
            TxtStatus.Text = "当前没有临时调课记录。";
            return;
        }
        var result = System.Windows.MessageBox.Show(
            $"确定要清除全部 {list.Count} 条临时调课 / 代课记录吗？此操作不可撤销。",
            "清除全部临时调课", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        Store.SaveOverrides(new System.Collections.Generic.List<LessonOverride>());
        AppEvents.RaiseScheduleChanged();
        TxtStatus.Text = "已清除全部临时调课记录。";
    }

    private void BtnWeek_Click(object sender, RoutedEventArgs e)
    {
        var w = new WeekWindow { Owner = this };
        w.Show();
    }

    // ---------- 文档 / 演示程序前台自动隐藏 ----------
    private void ChkDocHide_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        s.AutoHideOnDocOpen = ChkDocHide.IsChecked == true;
        Store.SaveSettings(s);
        CheckDocForeground(force: true);
        TxtStatus.Text = s.AutoHideOnDocOpen
            ? "已开启：有文档/演示程序在前台时自动隐藏悬浮窗。"
            : "已关闭文档前台自动隐藏。";
    }

    private void CheckDocForeground(bool force = false)
    {
        try
        {
            bool enabled = Store.LoadSettings().AutoHideOnDocOpen;
            bool active = enabled && DocDetect.IsDocumentForeground();
            if (!force && active == _docActive) return;
            _docActive = active;

            if (active)
            {
                // 临时隐藏，但不改变用户的“开关意图”
                _schWin?.Hide();
                _notifWin?.Hide();
            }
            else
            {
                if (_schOn) _schWin?.Show();
                if (_notOn) _notifWin?.Show();
            }
        }
        catch { }
    }

    // ---------- 通知 / 公告 ----------
    private void BtnAddNotice_Click(object sender, RoutedEventArgs e)
    {
        var title = TxtNewTitle.Text.Trim();
        var content = TxtNewContent.Text.Trim();
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return;
        Notifications.Insert(0, new Notification { Title = title, Content = content, CreatedAt = DateTime.Now });
        SaveNotes();
        TxtNewTitle.Text = "";
        TxtNewContent.Text = "";
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnDeleteNotice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string id)
        {
            var item = Notifications.FirstOrDefault(n => n.Id == id);
            if (item != null)
            {
                Notifications.Remove(item);
                SaveNotes();
                AppEvents.RaiseNotificationChanged();
            }
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string id)
        {
            var item = Notifications.FirstOrDefault(n => n.Id == id);
            if (item != null)
            {
                item.Pinned = !item.Pinned;
                SaveNotes();
                ReloadNotes();
                AppEvents.RaiseNotificationChanged();
            }
        }
    }

    private void ReloadNotes()
    {
        var list = Store.LoadNotifications()
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();
        Notifications.Clear();
        foreach (var n in list) Notifications.Add(n);
    }

    private void SaveNotes() => Store.SaveNotifications(Notifications.ToList());

    // ---------- 快捷键 ----------
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.L)
        {
            ToggleLock();
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            SetFullscreen(!_isFull);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_locked)
            {
                ToggleLock();
                e.Handled = true;
            }
            else if (_isFull)
            {
                SetFullscreen(false);
                e.Handled = true;
            }
        }
    }
}
