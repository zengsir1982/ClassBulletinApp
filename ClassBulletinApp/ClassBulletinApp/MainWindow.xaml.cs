using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

    // 教师个人课表下拉（支持输入姓名查询）
    private List<string> _allTeachers = new();
    private bool _teacherGuard;

    // 桌面最大化窗口自动隐藏
    private readonly DispatcherTimer _winTimer;
    private bool _maxActive;
    private DispatcherTimer? _hwApplyTimer;

    /// <summary>界面控件是否已就绪。为 false 时忽略作业公示参数变更事件（避免 XAML 加载期空引用）。</summary>
    private bool _hwReady;

    public ObservableCollection<string> ClassNames { get; } = new();
    public ObservableCollection<Notification> Notifications { get; } = new();
    public ObservableCollection<HomeworkItem> HomeworkItems { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        CbClass.ItemsSource = ClassNames;
        NotificationsPanel.ItemsSource = Notifications;
        HwItems.ItemsSource = HomeworkItems;

        // 桌面最大化窗口监视（2 秒轮询，开销极小）
        _winTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _winTimer.Tick += (_, _) => CheckMaxWindow();
        _winTimer.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _hwReady = true;   // 控件已就绪，此后才响应作业公示参数变更
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

            ChkMaxHide.IsChecked = settings.AutoHideOnMaxWindow;
            ReloadNotes();
            ReloadHomework();
            RefreshTeacherCombo();
            UpdateModeIndicator();
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
        UpdateModeIndicator();
    }

    /// <summary>顶栏提示当前悬浮课表显示的是班级课表还是教师个人课表。</summary>
    private void UpdateModeIndicator()
    {
        var s = Store.LoadSettings();
        bool teacher = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
        TxtCurrentMode.Text = teacher
            ? $"当前显示：教师 {s.SelectedTeacher}"
            : $"当前显示：{s.SelectedClass}";
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

    // ---------- 班级选择（切换到班级课表） ----------
    private void CbClass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cls = CbClass.SelectedItem as string;
        if (cls != null)
        {
            var s = Store.LoadSettings();
            s.SelectedClass = cls;
            s.ScheduleKind = "Class";
            Store.SaveSettings(s);
            UpdateModeIndicator();
        }
        AppEvents.RaiseScheduleChanged();
    }

    // ---------- 教师选择（切换到教师个人课表；可输入姓名查询） ----------
    private void RefreshTeacherCombo()
    {
        var sf = Store.LoadTeacherSchedule();
        _allTeachers = sf?.Classes.Select(c => c.ClassName).Where(n => !string.IsNullOrEmpty(n)).ToList()
                       ?? new List<string>();
        var s = Store.LoadSettings();
        bool teacherMode = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
        string cur = (teacherMode && _allTeachers.Contains(s.SelectedTeacher)) ? s.SelectedTeacher : "";
        SetTeacherItems(_allTeachers.ToList(), cur, false);
    }

    private void SetTeacherItems(List<string> items, string text, bool openDrop)
    {
        _teacherGuard = true;
        try
        {
            CbTeacher.ItemsSource = items;
            CbTeacher.Text = text;
        }
        finally { _teacherGuard = false; }
        if (openDrop && items.Count > 0 && CbTeacher.IsFocused) CbTeacher.IsDropDownOpen = true;
        RestoreTeacherCaret(text);
    }

    private void RestoreTeacherCaret(string q)
    {
        var tb = CbTeacher.Template?.FindName("PART_EditableTextBox", CbTeacher) as System.Windows.Controls.TextBox;
        if (tb != null)
        {
            tb.SelectionStart = q?.Length ?? 0;
            tb.SelectionLength = 0;
        }
    }

    private void CbTeacher_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_teacherGuard) return;
        FilterTeachers(true);
    }

    private void FilterTeachers(bool openDrop)
    {
        string q = (CbTeacher.Text ?? "").Trim();
        var list = string.IsNullOrEmpty(q)
            ? _allTeachers.ToList()
            : _allTeachers.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        SetTeacherItems(list, q, openDrop);
    }

    private void CbTeacher_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_teacherGuard) return;
        if (CbTeacher.SelectedItem is string name && !string.IsNullOrEmpty(name))
            SelectTeacher(name);
    }

    private void SelectTeacher(string name)
    {
        var s = Store.LoadSettings();
        s.ScheduleKind = "Teacher";
        s.SelectedTeacher = name;
        Store.SaveSettings(s);
        _teacherGuard = true;
        CbTeacher.Text = name;
        _teacherGuard = false;
        UpdateModeIndicator();
        TxtStatus.Text = $"悬浮课表已切换为教师「{name}」的个人课表（双击悬浮课表标题的姓名处可再切换）。";
        AppEvents.RaiseScheduleChanged();
    }

    private void CbTeacher_LostFocus(object sender, RoutedEventArgs e)
    {
        string input = (CbTeacher.Text ?? "").Trim();
        if (input.Length > 0)
        {
            var exact = _allTeachers.FirstOrDefault(n => n.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (exact != null) { SelectTeacher(exact); return; }
        }
        var s = Store.LoadSettings();
        bool teacherMode = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
        string cur = (teacherMode && _allTeachers.Contains(s.SelectedTeacher)) ? s.SelectedTeacher : "";
        SetTeacherItems(_allTeachers.ToList(), cur, false);
    }

    // ---------- 课表导入后的统一处理（含教师个人课表整理） ----------
    private void ApplyImportedSchedule(ScheduleFile sf, string importPath, out int teacherCount)
    {
        Store.SaveSchedule(sf);
        ClassNames.Clear();
        foreach (var c in sf.Classes) ClassNames.Add(c.ClassName);

        var st = Store.LoadSettings();
        st.LastImportPath = importPath;
        if (ClassNames.Count > 0 &&
            (string.IsNullOrEmpty(st.SelectedClass) || !ClassNames.Contains(st.SelectedClass)))
            st.SelectedClass = ClassNames[0];
        Store.SaveSettings(st);

        if (!string.IsNullOrEmpty(st.SelectedClass) && ClassNames.Contains(st.SelectedClass))
            CbClass.SelectedItem = st.SelectedClass;
        else if (ClassNames.Count > 0)
            CbClass.SelectedIndex = 0;

        teacherCount = ExtractTeachers(sf);
        RefreshTeacherCombo();
        UpdateModeIndicator();
    }

    /// <summary>由已导入课表反向整理教师个人课表并保存，返回教师人数。</summary>
    private int ExtractTeachers(ScheduleFile sf)
    {
        try
        {
            var ts = TeacherExtractor.Build(sf);
            if (ts.Classes.Count == 0) return 0;
            Store.SaveTeacherSchedule(ts);
            var s = Store.LoadSettings();
            if (string.IsNullOrEmpty(s.SelectedTeacher) || !ts.Classes.Any(c => c.ClassName == s.SelectedTeacher))
            {
                s.SelectedTeacher = ts.Classes[0].ClassName;
                Store.SaveSettings(s);
            }
            return ts.Classes.Count;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "整理教师个人课表时出错：" + ex.Message;
            return 0;
        }
    }

    /// <summary>把单独导入的教师个人课表合并进教师库（保留已整理的其他教师）。</summary>
    private void MergeTeacherSchedule(ScheduleFile imported)
    {
        var existing = Store.LoadTeacherSchedule() ?? new ScheduleFile();
        foreach (var cs in imported.Classes)
        {
            existing.Classes.RemoveAll(c => c.ClassName == cs.ClassName);
            existing.Classes.Add(cs);
        }
        existing.SourceFile = imported.SourceFile;
        existing.ImportedAt = DateTime.Now;
        Store.SaveTeacherSchedule(existing);
        RefreshTeacherCombo();
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
            ApplyImportedSchedule(sf, dlg.FileName, out int tc);

            TxtStatus.Text = $"导入成功，共 {ClassNames.Count} 个班级；已整理 {tc} 位教师的个人课表（顶栏「教师」下拉可输入姓名查询）。";
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
            ApplyImportedSchedule(sf, dlg.FileName, out int tcTemp);

            // 合并调/代覆盖：移除旧的导入来源，保留手动记录，再写入本次导入结果
            var list = Store.LoadOverrides();
            list.RemoveAll(x => x.Source == "temp");
            list.AddRange(tempOverrides);
            Store.SaveOverrides(list);

            int tiao = tempOverrides.Count(o => o.Type == "调");
            int dai = tempOverrides.Count(o => o.Type == "代");
            TxtStatus.Text = $"已导入临时课表，共 {ClassNames.Count} 个班级；调课 {tiao} 条、代课 {dai} 条；已整理 {tcTemp} 位教师的个人课表。";
            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入临时课表失败：" + ex.Message;
        }
    }

    // ---------- 导入单个班级课表（单班适用） ----------
    private void BtnImportSingle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            Title = "选择单个班级课表"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sf = ScheduleImporter.ImportSingleClass(dlg.FileName);
            ApplyImportedSchedule(sf, dlg.FileName, out int tcSingle);

            if (ClassNames.Count > 0)
            {
                var st = Store.LoadSettings();
                st.SelectedClass = ClassNames[0];
                st.ScheduleKind = "Class";
                Store.SaveSettings(st);
                UpdateModeIndicator();
            }

            TxtStatus.Text = $"已导入单个班级课表：{sf.Classes.Count} 个班级（{string.Join("、", sf.Classes.Select(c => c.ClassName))}）；已整理 {tcSingle} 位教师的个人课表。";
            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入单个班级课表失败：" + ex.Message;
        }
    }

    // ---------- 导入单个教师个人课表 ----------
    private void BtnImportTeacher_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            Title = "选择单个教师课表"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sf = ScheduleImporter.ImportTeacherSchedule(dlg.FileName);
            MergeTeacherSchedule(sf);
            if (sf.Classes.Count > 0)
            {
                var st = Store.LoadSettings();
                st.SelectedTeacher = sf.Classes[0].ClassName;
                st.ScheduleKind = "Teacher";
                st.LastImportPath = dlg.FileName;
                Store.SaveSettings(st);
                RefreshTeacherCombo();
                UpdateModeIndicator();
            }

            TxtStatus.Text = $"已导入并切换到教师课表：{string.Join("、", sf.Classes.Select(c => c.ClassName))}。悬浮课表已显示该教师的个人课表（双击标题姓名可切换）。";
            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入教师课表失败：" + ex.Message;
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

    // ---------- 桌面最大化窗口自动隐藏 ----------
    private void ChkMaxHide_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        s.AutoHideOnMaxWindow = ChkMaxHide.IsChecked == true;
        Store.SaveSettings(s);
        CheckMaxWindow(force: true);
        TxtStatus.Text = s.AutoHideOnMaxWindow
            ? "已开启：桌面有窗口最大化时自动隐藏悬浮窗（恢复/关闭后自动重新显示）。"
            : "已关闭最大化窗口自动隐藏。";
    }

    private void CheckMaxWindow(bool force = false)
    {
        try
        {
            bool enabled = Store.LoadSettings().AutoHideOnMaxWindow;
            bool active = enabled && WinDetect.IsMaximizedWindowPresent();
            if (!force && active == _maxActive) return;
            _maxActive = active;

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

    // ---------- 作业公示 ----------
    private static readonly string[] HwAnchors =
    {
        "TopLeft", "TopCenter", "TopRight",
        "MiddleLeft", "MiddleCenter", "MiddleRight",
        "BottomLeft", "BottomCenter", "BottomRight"
    };

    private void ReloadHomework()
    {
        var board = Store.LoadHomework();
        HomeworkItems.Clear();
        if (board != null)
        {
            TxtHwTitle.Text = board.Title;
            foreach (var it in board.Items) HomeworkItems.Add(it);
            ApplyHwLayoutToUi(board.Layout);
        }
        else
        {
            TxtHwTitle.Text = "各科作业公示";
            ApplyHwLayoutToUi(null);
        }
        if (HomeworkItems.Count == 0)
            HomeworkItems.Add(new HomeworkItem());
    }

    private void ApplyHwLayoutToUi(HomeworkWallpaperLayout? lay)
    {
        string anchor = string.IsNullOrEmpty(lay?.Anchor) ? "MiddleRight" : lay!.Anchor;
        int idx = Array.IndexOf(HwAnchors, anchor);
        if (idx < 0) idx = 5;
        CbHwAnchor.SelectedIndex = idx;
        SlHwX.Value = Math.Clamp(lay?.OffsetX ?? 0, SlHwX.Minimum, SlHwX.Maximum);
        SlHwY.Value = Math.Clamp(lay?.OffsetY ?? 0, SlHwY.Minimum, SlHwY.Maximum);
        double ratio = (lay?.WidthRatio ?? 0) <= 0 ? 0.4 : lay!.WidthRatio;
        SlHwWidth.Value = Math.Clamp(ratio, SlHwWidth.Minimum, SlHwWidth.Maximum);
        UpdateHwLabels();
    }

    private HomeworkWallpaperLayout CurrentHwLayout() => new()
    {
        Anchor = CbHwAnchor.SelectedIndex >= 0 && CbHwAnchor.SelectedIndex < HwAnchors.Length
            ? HwAnchors[CbHwAnchor.SelectedIndex]
            : "MiddleRight",
        OffsetX = SlHwX.Value,
        OffsetY = SlHwY.Value,
        WidthRatio = SlHwWidth.Value
    };

    private HomeworkBoard BuildHomeworkBoard() => new()
    {
        Title = (TxtHwTitle.Text ?? "").Trim(),
        Items = HomeworkItems
            .Where(x => !string.IsNullOrWhiteSpace(x.Subject) || !string.IsNullOrWhiteSpace(x.Content))
            .Select(x => new HomeworkItem { Subject = (x.Subject ?? "").Trim(), Content = (x.Content ?? "").Trim() })
            .ToList(),
        UpdatedAt = DateTime.Now,
        Layout = CurrentHwLayout()
    };

    private void HwLayout_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 加载期间（ComboBox 的 SelectedIndex、Slider 因 Minimum 而 coerce 初值）就会触发本事件，
        // 此时 SlHwX / TxtHwX 等字段尚未赋值，必须等 Loaded 之后再响应。
        if (!_hwReady) return;
        UpdateHwLabels();
        ScheduleHwRepaint();
    }

    private void UpdateHwLabels()
    {
        if (SlHwX == null || SlHwY == null || SlHwWidth == null) return;
        if (TxtHwX != null) TxtHwX.Text = $"{SlHwX.Value:0} 像素";
        if (TxtHwY != null) TxtHwY.Text = $"{SlHwY.Value:0} 像素";
        if (TxtHwWidth != null) TxtHwWidth.Text = $"{SlHwWidth.Value:0%}";
    }

    /// <summary>已经应用为桌面背景时，调整参数后自动重绘（防抖 0.5 秒），方便直观调整位置。</summary>
    private void ScheduleHwRepaint()
    {
        string outPath = Path.Combine(Store.AppDir, "homework_wallpaper.png");
        var cur = WallpaperHelper.GetCurrentWallpaper();
        if (!string.Equals(cur, outPath, StringComparison.OrdinalIgnoreCase)) return;

        _hwApplyTimer?.Stop();
        _hwApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hwApplyTimer.Tick += (_, _) =>
        {
            _hwApplyTimer?.Stop();
            ApplyHomeworkWallpaper(silent: true);
        };
        _hwApplyTimer.Start();
    }

    private void ApplyHomeworkWallpaper(bool silent)
    {
        try
        {
            var board = BuildHomeworkBoard();
            Store.SaveHomework(board);

            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null)
            {
                if (!silent) TxtStatus.Text = "无法获取主屏幕分辨率，应用壁纸失败。";
                return;
            }
            string outPath = Path.Combine(Store.AppDir, "homework_wallpaper.png");
            HomeworkRenderer.RenderToPng(board, screen.Bounds.Width, screen.Bounds.Height, outPath);

            // 首次应用前记录系统原壁纸，便于「清除」恢复
            var cur = WallpaperHelper.GetCurrentWallpaper();
            if (!string.Equals(cur, outPath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(cur))
            {
                var st = Store.LoadSettings();
                if (string.IsNullOrEmpty(st.SavedWallpaper)) { st.SavedWallpaper = cur; Store.SaveSettings(st); }
            }
            WallpaperHelper.SetWallpaper(outPath);
            if (!silent)
                TxtStatus.Text = $"作业公示已保存并应用为桌面背景（共 {board.Items.Count} 科）。调整位置/偏移/宽度会自动更新。";
        }
        catch (Exception ex)
        {
            if (!silent) TxtStatus.Text = "应用作业公示壁纸失败：" + ex.Message;
        }
    }

    private void BtnHwPreview_Click(object sender, RoutedEventArgs e)
    {
        ApplyHomeworkWallpaper(silent: false);
        WindowState = WindowState.Minimized;   // 最小化控制面板，便于看到壁纸效果
    }

    private void BtnAddHw_Click(object sender, RoutedEventArgs e)
        => HomeworkItems.Add(new HomeworkItem());

    private void BtnDelHw_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is HomeworkItem it)
        {
            HomeworkItems.Remove(it);
            if (HomeworkItems.Count == 0) HomeworkItems.Add(new HomeworkItem());
        }
    }

    private void BtnSaveHw_Click(object sender, RoutedEventArgs e) => ApplyHomeworkWallpaper(silent: false);

    private void BtnClearHw_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var st = Store.LoadSettings();
            WallpaperHelper.SetWallpaper(string.IsNullOrEmpty(st.SavedWallpaper) ? "" : st.SavedWallpaper);
            TxtStatus.Text = "已清除作业公示桌面背景，恢复原壁纸。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "清除桌面背景失败：" + ex.Message;
        }
    }

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
