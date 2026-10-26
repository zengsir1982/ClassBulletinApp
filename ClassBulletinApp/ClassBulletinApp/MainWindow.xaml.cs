using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class MainWindow : Window
{
    private ScheduleFile? _schedule;
    private string? _selectedClass;
    private bool _isFull;
    private bool _locked;
    private bool _exiting;
    private string _semesterStart = "";
    private readonly DispatcherTimer _timer;

    // 日视图拖拽调课
    private PeriodRow? _dayDragSrc;
    private System.Windows.Point _dayDragStart;

    private ScheduleWindow? _schWin;
    private NotificationWindow? _notifWin;
    private bool _schOn, _notOn;
    private System.Windows.Forms.NotifyIcon? _tray;

    public ObservableCollection<string> ClassNames { get; } = new();
    public ObservableCollection<PeriodRow> TodayRows { get; } = new();
    public ObservableCollection<Notification> Notifications { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        CbClass.ItemsSource = ClassNames;
        TodayPanel.ItemsSource = TodayRows;
        NotificationsPanel.ItemsSource = Notifications;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => RefreshToday();
        _timer.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = Store.LoadSettings();
        try
        {
            _semesterStart = settings.SemesterStartMonday;
            _schedule = Store.LoadSchedule();
            if (_schedule != null)
            {
                foreach (var c in _schedule.Classes) ClassNames.Add(c.ClassName);
                if (!string.IsNullOrEmpty(settings.SelectedClass) && ClassNames.Contains(settings.SelectedClass))
                    CbClass.SelectedItem = settings.SelectedClass;
                else if (ClassNames.Count > 0)
                    CbClass.SelectedIndex = 0;
                TxtStatus.Text = $"已加载 {ClassNames.Count} 个班级课表。";
            }
            else
            {
                TxtStatus.Text = "尚未导入课表，请点击右上角「导入课表」选择全校总课程表 Excel 文件。";
            }

            // 初始化显示日选择器（Fixed 模式回显日期，否则清空表示“今天”）
            var dispSettings = Store.LoadSettings();
            DpDisplayDate.SelectedDate = dispSettings.DisplayDateMode == "Fixed"
                && DateTime.TryParse(dispSettings.FixedDisplayDate, out var fd) ? fd : (DateTime?)null;

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

        try { RefreshToday(); }
        catch (Exception ex) { TxtStatus.Text = "刷新课表出错：" + ex.Message; }
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
        _selectedClass = CbClass.SelectedItem as string;
        if (_selectedClass != null)
        {
            var s = Store.LoadSettings();
            s.SelectedClass = _selectedClass;
            Store.SaveSettings(s);
        }
        AppEvents.RaiseScheduleChanged();
        RefreshToday();
    }

    // ---------- 生成当日课表（编辑区） ----------
    private void RefreshToday()
    {
        var s = Store.LoadSettings();
        _selectedClass = s.SelectedClass;
        _semesterStart = s.SemesterStartMonday;
        var displayDate = BoardBuilder.ResolveDisplayDate(s);
        var res = BoardBuilder.Build(_selectedClass, _semesterStart, _schedule, displayDate);

        TxtClassName.Text = string.IsNullOrEmpty(res.ClassName) ? "未选择班级" : res.ClassName;
        TxtDate.Text = res.DateText;
        TxtWeek.Text = res.WeekLine;

        TodayRows.Clear();
        foreach (var r in res.Rows) TodayRows.Add(r);
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
            _schedule = sf;
            ClassNames.Clear();
            foreach (var c in sf.Classes) ClassNames.Add(c.ClassName);
            if (ClassNames.Count > 0) CbClass.SelectedIndex = 0;

            var st = Store.LoadSettings();
            st.LastImportPath = dlg.FileName;
            Store.SaveSettings(st);

            TxtStatus.Text = $"导入成功，共 {ClassNames.Count} 个班级。";
            AppEvents.RaiseScheduleChanged();
            RefreshToday();
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
            _schedule = sf;
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
            RefreshToday();
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
        {
            _semesterStart = Store.LoadSettings().SemesterStartMonday;
            RefreshToday();
        }
    }

    // ---------- 双击课卡编辑调课（含确认 / 取消） ----------
    private void TodayPanel_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null)
        {
            if (dep is System.Windows.Controls.Border b && b.DataContext is PeriodRow row && row.Period > 0)
            {
                OpenOverrideEditor(row.DayIdx, row.Period);
                break;
            }
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
    }

    private void OpenOverrideEditor(int dayIdx, int period)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var row = TodayRows.FirstOrDefault(r => r.DayIdx == dayIdx && r.Period == period);
        var current = (row != null && row.Lessons.Count > 0) ? row.Lessons[0] : new LessonView();
        var w = new LessonOverrideWindow(_selectedClass, dayIdx, period, current, _schedule) { Owner = this, Topmost = true };
        if (w.ShowDialog() == true)
        {
            AppEvents.RaiseScheduleChanged();
            RefreshToday();
        }
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
        Store.SaveOverrides(new List<LessonOverride>());
        AppEvents.RaiseScheduleChanged();
        RefreshToday();
        TxtStatus.Text = "已清除全部临时调课记录。";
    }

    // ---------- 显示日（调休上课） ----------
    private void DpDisplayDate_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DpDisplayDate.SelectedDate == null) return; // “今天”按钮负责切回今天，这里不处理清空
        var s = Store.LoadSettings();
        s.DisplayDateMode = "Fixed";
        s.FixedDisplayDate = DpDisplayDate.SelectedDate.Value.ToString("yyyy-MM-dd");
        Store.SaveSettings(s);
        AppEvents.RaiseScheduleChanged();
        RefreshToday();
    }

    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        s.DisplayDateMode = "Today";
        s.FixedDisplayDate = "";
        Store.SaveSettings(s);
        DpDisplayDate.SelectedDate = null;
        AppEvents.RaiseScheduleChanged();
        RefreshToday();
    }

    private void BtnWeek_Click(object sender, RoutedEventArgs e)
    {
        var w = new WeekWindow { Owner = this };
        w.Show();
    }

    // ---------- 日视图：拖动调课 ----------
    private void TodayCard_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.DataContext is PeriodRow row && row.Period > 0)
        {
            var lesson = row.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
            if (lesson == null) { _dayDragSrc = null; return; }
            _dayDragSrc = row;
            _dayDragStart = e.GetPosition((System.Windows.IInputElement)sender);
        }
    }

    private void TodayCard_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dayDragSrc != null && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            var p = e.GetPosition((System.Windows.IInputElement)sender);
            if (Math.Abs(p.X - _dayDragStart.X) > 4 || Math.Abs(p.Y - _dayDragStart.Y) > 4)
            {
                var src = _dayDragSrc;
                _dayDragSrc = null;
                System.Windows.DragDrop.DoDragDrop((System.Windows.DependencyObject)sender,
                    new System.Windows.DataObject("DayPeriod", src), System.Windows.DragDropEffects.Move);
            }
        }
    }

    private void TodayCard_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _dayDragSrc = null;

    private void TodayCard_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DayPeriod")) { e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true; }
    }

    private void TodayCard_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData("DayPeriod") is PeriodRow src
            && sender is System.Windows.FrameworkElement fe
            && fe.DataContext is PeriodRow tgt
            && src != tgt)
        {
            ApplyDayDrag(src, tgt);
            e.Handled = true;
        }
    }

    private void TodayCard_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.DataContext is PeriodRow row && row.Period > 0)
        {
            OpenOverrideEditor(row.DayIdx, row.Period);
            e.Handled = true;
        }
    }

    private void ApplyDayDrag(PeriodRow src, PeriodRow tgt)
    {
        if (src.Period == tgt.Period || string.IsNullOrEmpty(_selectedClass)) return;
        var srcLesson = src.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
        if (srcLesson == null) return;
        var tgtLesson = tgt.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));

        string RawLoc(string? lt)
        {
            if (string.IsNullOrEmpty(lt)) return "";
            return lt!.StartsWith(" · ") ? lt.Substring(3).Trim() : lt.Trim();
        }

        var list = Store.LoadOverrides();
        string TypeOf(LessonView lv) => lv.IsOverridden && !string.IsNullOrEmpty(lv.OverrideType) ? lv.OverrideType : "调";

        if (tgtLesson == null)
        {
            // 移动：目标 = 源内容；源 = 空（调空）
            UpsertOverride(list, src.DayIdx, tgt.Period, srcLesson.Subject, srcLesson.Teacher, RawLoc(srcLesson.LocationText), TypeOf(srcLesson));
            UpsertOverride(list, src.DayIdx, src.Period, "", "", "", "调", isEmpty: true);
        }
        else
        {
            // 互换
            UpsertOverride(list, src.DayIdx, tgt.Period, srcLesson.Subject, srcLesson.Teacher, RawLoc(srcLesson.LocationText), TypeOf(srcLesson));
            UpsertOverride(list, src.DayIdx, src.Period, tgtLesson.Subject, tgtLesson.Teacher, RawLoc(tgtLesson.LocationText), TypeOf(tgtLesson));
        }
        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        RefreshToday();
        TxtStatus.Text = $"已调课：第{src.Period}节 ⇄ 第{tgt.Period}节";
    }

    private void UpsertOverride(List<LessonOverride> list, int dayIdx, int period,
        string subject, string teacher, string location, string type, bool isEmpty = false)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
        string key = $"{_selectedClass}|{dayIdx}|{period}";
        list.RemoveAll(o => o.Key == key);
        if (isEmpty)
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedClass,
                DayIdx = dayIdx,
                Period = period,
                IsEmpty = true,
                Type = "调",
                Source = "manual",
                UpdatedAt = DateTime.Now
            });
        }
        else if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedClass,
                DayIdx = dayIdx,
                Period = period,
                Subject = subject,
                Teacher = teacher,
                Location = location,
                Type = type,
                Source = "manual",
                UpdatedAt = DateTime.Now
            });
        }
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
