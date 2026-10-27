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
    private bool _classGuard;   // 从设置反向同步班级下拉时防止 SelectionChanged 递归

    // 桌面最大化窗口自动隐藏
    private readonly DispatcherTimer _winTimer;
    private bool _maxActive;
    private DispatcherTimer? _hwApplyTimer;
    private DispatcherTimer? _wpSaveTimer;
    private System.Windows.Forms.ToolStripMenuItem? _miWp;

    /// <summary>界面控件是否已就绪。为 false 时忽略作业公示参数变更事件（避免 XAML 加载期空引用）。</summary>
    private bool _hwReady;

    /// <summary>提醒设置控件是否已就绪（避免 XAML 加载期 TextChanged 触发保存）。</summary>
    private bool _remindReady;

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
            ReloadBackdropUi();
            RefreshTeacherCombo();
            UpdateModeIndicator();
            ReloadReminderUi();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "加载数据出错：" + ex.Message;
        }

        // 课表数据 / 显示对象变化时（含悬浮窗与周视图的双击切换），同步顶栏下拉与模式提示
        AppEvents.ScheduleChanged += SyncTargetUi;

        // 上下课提醒后台服务
        try { ReminderService.Start(); }
        catch (Exception ex) { TxtStatus.Text += " [提醒服务启动失败：" + ex.Message + "]"; }

        // 桌面背面：课表 / 作业变化、跨天时自动重绘（仅在已应用时）
        try
        {
            BackdropService.Start();
            AppEvents.BackdropChanged += UpdateTrayBackdropText;
        }
        catch { }

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

    /// <summary>
    /// 课表 / 显示对象变化后（含悬浮课表、周课程表里双击姓名互切），
    /// 把顶栏「教师 / 班级」下拉同步为设置中的当前值。
    /// </summary>
    private void SyncTargetUi()
    {
        if (!IsLoaded) return;
        try
        {
            var s = Store.LoadSettings();
            bool teacher = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
            _teacherGuard = true;
            _classGuard = true;
            try
            {
                if (teacher)
                {
                    if (_allTeachers.Contains(s.SelectedTeacher)) CbTeacher.Text = s.SelectedTeacher;
                }
                else
                {
                    if (ClassNames.Contains(s.SelectedClass)) CbClass.SelectedItem = s.SelectedClass;
                }
            }
            finally { _teacherGuard = false; _classGuard = false; }
            UpdateModeIndicator();
        }
        catch { }
    }

    // ---------- 上下课提醒设置 ----------
    private void ReloadReminderUi()
    {
        var r = Store.LoadSettings().Reminder ?? new ScheduleCore.Models.ReminderSettings();
        _remindReady = false;
        ChkRemindEnabled.IsChecked = r.Enabled;
        TxtRemindBeforeClass.Text = Math.Max(0, r.BeforeClassMinutes).ToString();
        TxtRemindBeforeEnd.Text = Math.Max(0, r.BeforeEndMinutes).ToString();
        TxtRemindSeconds.Text = Math.Clamp(r.DisplaySeconds, 3, 60).ToString();
        ChkRemindAfterSchool.IsChecked = r.AfterSchoolRemind;
        ChkRemindSound.IsChecked = r.PlaySound;

        // 语音：列出系统已安装语音，首项「系统默认」会自动优先选择中文语音
        ChkRemindVoice.IsChecked = r.VoiceEnabled;
        var voices = new List<string> { "（系统默认 · 自动选中文语音）" };
        voices.AddRange(VoiceSpeaker.GetInstalledVoices().Where(v => !voices.Contains(v)));
        CbRemindVoice.ItemsSource = voices;
        int idx = string.IsNullOrWhiteSpace(r.VoiceName) ? 0 : voices.IndexOf(r.VoiceName);
        CbRemindVoice.SelectedIndex = idx < 0 ? 0 : idx;
        SlVoiceRate.Value = Math.Clamp(r.VoiceRate, -10, 10);
        SlVoiceVolume.Value = Math.Clamp(r.VoiceVolume, 0, 100);
        UpdateVoiceLabels();
        UpdateVoiceEnabledUi();

        _remindReady = true;
    }

    private void UpdateVoiceLabels()
    {
        if (TxtVoiceRate != null) TxtVoiceRate.Text = SlVoiceRate.Value switch
        {
            0 => "正常",
            > 0 => $"偏快 +{SlVoiceRate.Value:0}",
            _ => $"偏慢 {SlVoiceRate.Value:0}"
        };
        if (TxtVoiceVolume != null) TxtVoiceVolume.Text = $"{SlVoiceVolume.Value:0}%";
    }

    private void UpdateVoiceEnabledUi()
    {
        bool on = ChkRemindVoice.IsChecked == true;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        if (CbRemindVoice != null) CbRemindVoice.IsEnabled = on;
        if (BtnVoiceTest != null) BtnVoiceTest.IsEnabled = on;
        if (SlVoiceRate != null) SlVoiceRate.IsEnabled = on;
        if (SlVoiceVolume != null) SlVoiceVolume.IsEnabled = on;
        if (TxtVoiceRate != null) TxtVoiceRate.Visibility = vis;
        if (TxtVoiceVolume != null) TxtVoiceVolume.Visibility = vis;
    }

    private void RemindSetting_Changed(object sender, RoutedEventArgs e)
    {
        UpdateVoiceEnabledUi();
        SaveReminderUi();
    }

    private void RemindSetting_TextChanged(object sender, TextChangedEventArgs e) => SaveReminderUi();

    private void RemindVoice_Selected(object sender, SelectionChangedEventArgs e) => SaveReminderUi();

    private void RemindVoice_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtVoiceRate == null || TxtVoiceVolume == null) return;
        UpdateVoiceLabels();
        SaveReminderUi();
    }

    private void BtnVoiceTest_Click(object sender, RoutedEventArgs e)
    {
        VoiceSpeaker.Speak("第3节 语文 即将上课，请回座位准备", SelectedVoiceName(), (int)SlVoiceRate.Value, (int)SlVoiceVolume.Value);
        TxtStatus.Text = "已试听语音播报（若没听到声音，请检查系统音量或安装中文语音包）。";
    }

    private string SelectedVoiceName()
    {
        string v = CbRemindVoice.SelectedItem as string ?? "";
        return v.StartsWith("（") ? "" : v;
    }

    private void SaveReminderUi()
    {
        if (!_remindReady) return;
        try
        {
            var s = Store.LoadSettings();
            s.Reminder ??= new ScheduleCore.Models.ReminderSettings();
            s.Reminder.Enabled = ChkRemindEnabled.IsChecked == true;
            s.Reminder.BeforeClassMinutes = ParseRemindMinutes(TxtRemindBeforeClass.Text);
            s.Reminder.BeforeEndMinutes = ParseRemindMinutes(TxtRemindBeforeEnd.Text);
            s.Reminder.DisplaySeconds = ParseRemindSeconds(TxtRemindSeconds.Text);
            s.Reminder.AfterSchoolRemind = ChkRemindAfterSchool.IsChecked == true;
            s.Reminder.PlaySound = ChkRemindSound.IsChecked == true;
            s.Reminder.VoiceEnabled = ChkRemindVoice.IsChecked == true;
            s.Reminder.VoiceName = SelectedVoiceName();
            s.Reminder.VoiceRate = (int)SlVoiceRate.Value;
            s.Reminder.VoiceVolume = (int)SlVoiceVolume.Value;
            Store.SaveSettings(s);
        }
        catch { }
    }

    private static int ParseRemindMinutes(string? text)
        => int.TryParse(text?.Trim(), out var v) && v >= 0 && v <= 30 ? v : 0;

    private static int ParseRemindSeconds(string? text)
        => int.TryParse(text?.Trim(), out var v) && v >= 3 && v <= 60 ? v : 8;

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
        _miWp = new System.Windows.Forms.ToolStripMenuItem("当天课表 → 桌面背面", null, (_, _) => ToggleBackdropSchedule());
        var miExit = new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitApp());
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
        {
            miPanel, miSch, miNot,
            new System.Windows.Forms.ToolStripSeparator(),
            _miWp,
            new System.Windows.Forms.ToolStripSeparator(),
            miExit
        });
        menu.Opening += (_, _) => UpdateTrayBackdropText();
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        UpdateTrayBackdropText();
    }

    /// <summary>同步托盘菜单中「当天课表 → 桌面背面」的勾选状态。</summary>
    private void UpdateTrayBackdropText()
    {
        try
        {
            if (_miWp == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            _miWp.Checked = b.ShowSchedule;
            _miWp.Text = b.ShowSchedule ? "当天课表 → 桌面背面（开）" : "当天课表 → 桌面背面（关）";
            _miWp.ToolTipText = b.ShowSchedule
                ? "点击关闭：从桌面背面移除当天课表"
                : "点击开启：把当天课表画到桌面壁纸上（位于所有窗口之下）";
        }
        catch { }
    }

    private void ToggleBackdropSchedule()
    {
        try
        {
            BackdropService.ToggleSchedule(out string msg);
            TxtStatus.Text = msg;
            ReloadBackdropUi();
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换桌面背面失败：" + ex.Message;
        }
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
        if (_classGuard) return;   // 从设置反向同步时跳过，避免递归保存
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
        }
        else
        {
            TxtHwTitle.Text = "各科作业公示";
        }
        if (HomeworkItems.Count == 0)
            HomeworkItems.Add(new HomeworkItem());
    }

    // ---------- 桌面背面（当天课表 + 作业公示 合成壁纸） ----------
    /// <summary>把设置中的桌面背面参数同步到「桌面背面」标签页控件。</summary>
    private void ReloadBackdropUi()
    {
        var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
        bool ready = _hwReady;
        _hwReady = false;
        try
        {
            ChkWpSchedule.IsChecked = b.ShowSchedule;
            ChkWpHomework.IsChecked = b.ShowHomework;
            ChkWpOrigin.IsChecked = b.UseOriginWallpaper;

            var sl = b.ScheduleLayout ?? new HomeworkWallpaperLayout();
            FillAnchorCombo(CbWpSchAnchor, sl.Anchor, 3);
            SlWpSchWidth.Value = Math.Clamp(sl.WidthRatio <= 0 ? 0.38 : sl.WidthRatio, SlWpSchWidth.Minimum, SlWpSchWidth.Maximum);
            SlWpSchX.Value = Math.Clamp(sl.OffsetX, SlWpSchX.Minimum, SlWpSchX.Maximum);
            SlWpSchY.Value = Math.Clamp(sl.OffsetY, SlWpSchY.Minimum, SlWpSchY.Maximum);

            ApplyHwLayoutToUi(b.HomeworkLayout ?? Store.LoadHomework()?.Layout);
        }
        finally { _hwReady = ready; }
        UpdateWpLabels();
    }

    private static void FillAnchorCombo(System.Windows.Controls.ComboBox cb, string? anchor, int fallback)
    {
        int idx = Array.IndexOf(HwAnchors, (anchor ?? "").Trim());
        // 兼容旧值
        if (idx < 0)
        {
            idx = (anchor ?? "").Trim() switch
            {
                "Left" => 3, "Right" => 5, "Top" => 1, "Bottom" => 7, "Center" => 4, _ => fallback
            };
        }
        cb.SelectedIndex = Math.Clamp(idx, 0, HwAnchors.Length - 1);
    }

    private static string ComboAnchor(System.Windows.Controls.ComboBox cb, string fallback)
        => cb.SelectedIndex >= 0 && cb.SelectedIndex < HwAnchors.Length ? HwAnchors[cb.SelectedIndex] : fallback;

    private void ApplyHwLayoutToUi(HomeworkWallpaperLayout? lay)
    {
        bool ready = _hwReady;
        _hwReady = false;
        try
        {
            FillAnchorCombo(CbHwAnchor, lay?.Anchor, 5);
            SlHwX.Value = Math.Clamp(lay?.OffsetX ?? 0, SlHwX.Minimum, SlHwX.Maximum);
            SlHwY.Value = Math.Clamp(lay?.OffsetY ?? 0, SlHwY.Minimum, SlHwY.Maximum);
            double ratio = (lay?.WidthRatio ?? 0) <= 0 ? 0.4 : lay!.WidthRatio;
            SlHwWidth.Value = Math.Clamp(ratio, SlHwWidth.Minimum, SlHwWidth.Maximum);
        }
        finally { _hwReady = ready; }
        UpdateHwLabels();
    }

    private HomeworkWallpaperLayout CurrentHwLayout() => new()
    {
        Anchor = ComboAnchor(CbHwAnchor, "MiddleRight"),
        OffsetX = SlHwX.Value,
        OffsetY = SlHwY.Value,
        WidthRatio = SlHwWidth.Value
    };

    private HomeworkWallpaperLayout CurrentWpScheduleLayout() => new()
    {
        Anchor = ComboAnchor(CbWpSchAnchor, "MiddleLeft"),
        OffsetX = SlWpSchX.Value,
        OffsetY = SlWpSchY.Value,
        WidthRatio = SlWpSchWidth.Value
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
        ScheduleWpSave();
        ScheduleWpRepaint();
    }

    private void WpLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hwReady) return;
        UpdateWpLabels();
        ScheduleWpSave();
        ScheduleWpRepaint();
    }

    private void WpSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hwReady) return;
        SaveWpSettings();
        ScheduleWpRepaint();
        TxtStatus.Text = "桌面背面内容已更新" + (BackdropService.IsApplied() ? "，正在重绘壁纸…" : "，点「应用到桌面背面」即可生效。");
    }

    private void UpdateHwLabels()
    {
        if (SlHwX == null || SlHwY == null || SlHwWidth == null) return;
        if (TxtHwX != null) TxtHwX.Text = $"{SlHwX.Value:0} 像素";
        if (TxtHwY != null) TxtHwY.Text = $"{SlHwY.Value:0} 像素";
        if (TxtHwWidth != null) TxtHwWidth.Text = $"{SlHwWidth.Value:0%}";
    }

    private void UpdateWpLabels()
    {
        if (SlWpSchX == null || SlWpSchY == null || SlWpSchWidth == null) return;
        if (TxtWpSchX != null) TxtWpSchX.Text = $"{SlWpSchX.Value:0} 像素";
        if (TxtWpSchY != null) TxtWpSchY.Text = $"{SlWpSchY.Value:0} 像素";
        if (TxtWpSchWidth != null) TxtWpSchWidth.Text = $"{SlWpSchWidth.Value:0%}";
    }

    /// <summary>把桌面背面参数写入设置（拖动滑块时防抖 400ms，避免频繁写文件）。</summary>
    private void ScheduleWpSave()
    {
        _wpSaveTimer?.Stop();
        _wpSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _wpSaveTimer.Tick += (_, _) =>
        {
            _wpSaveTimer?.Stop();
            SaveWpSettings();
        };
        _wpSaveTimer.Start();
    }

    private void SaveWpSettings()
    {
        try
        {
            var s = Store.LoadSettings();
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowSchedule = ChkWpSchedule.IsChecked == true;
            s.Backdrop.ShowHomework = ChkWpHomework.IsChecked == true;
            s.Backdrop.UseOriginWallpaper = ChkWpOrigin.IsChecked == true;
            s.Backdrop.ScheduleLayout = CurrentWpScheduleLayout();
            s.Backdrop.HomeworkLayout = CurrentHwLayout();
            Store.SaveSettings(s);

            // 作业内容文件里也写一份布局，保证老入口 / 其他窗口读到的位置一致
            var board = Store.LoadHomework();
            if (board != null)
            {
                board.Layout = s.Backdrop.HomeworkLayout;
                Store.SaveHomework(board);
            }
        }
        catch { }
    }

    /// <summary>已经应用到桌面时，调整参数后自动重绘（防抖 0.6 秒），方便直观调整位置。</summary>
    private void ScheduleWpRepaint()
    {
        if (!BackdropService.IsApplied()) return;

        _hwApplyTimer?.Stop();
        _hwApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hwApplyTimer.Tick += (_, _) =>
        {
            _hwApplyTimer?.Stop();
            BackdropService.Apply(silent: true);
        };
        _hwApplyTimer.Start();
    }

    private void BtnApplyWp_Click(object sender, RoutedEventArgs e)
    {
        SaveWpSettings();
        if (ChkWpSchedule.IsChecked != true && ChkWpHomework.IsChecked != true)
        {
            TxtStatus.Text = "请至少勾选「当天课表」或「作业公示」其中一项。";
            return;
        }
        BackdropService.Apply(silent: false, out string msg);
        TxtStatus.Text = msg;
        UpdateTrayBackdropText();
        AppEvents.RaiseBackdropChanged();
    }

    private void BtnClearWp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先落盘关闭开关，再恢复壁纸：避免「清除了壁纸但开关还亮着」的不一致
            var s = Store.LoadSettings();
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowSchedule = false;
            s.Backdrop.ShowHomework = false;
            Store.SaveSettings(s);

            BackdropService.Clear();
            ReloadBackdropUi();
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
            TxtStatus.Text = "已清除桌面背面，恢复系统原壁纸。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "清除桌面背面失败：" + ex.Message;
        }
    }

    private void ApplyHomeworkWallpaper(bool silent)
    {
        try
        {
            var board = BuildHomeworkBoard();
            Store.SaveHomework(board);

            var s = Store.LoadSettings();
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowHomework = true;      // 「保存并应用」隐含要显示作业
            s.Backdrop.HomeworkLayout = board.Layout;
            Store.SaveSettings(s);

            bool ok = BackdropService.Apply(silent, out string msg);
            if (!silent)
                TxtStatus.Text = ok ? $"{msg}（共 {board.Items.Count} 科作业）" : msg;
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            if (!silent) TxtStatus.Text = "应用作业公示失败：" + ex.Message;
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

    private void BtnClearHw_Click(object sender, RoutedEventArgs e) => BtnClearWp_Click(sender, e);

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
