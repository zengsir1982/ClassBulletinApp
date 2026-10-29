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
    private HomeworkWindow? _hwWin;
    private MissingWindow? _msWin;
    private bool _schOn, _notOn, _hwOn, _msOn;
    private System.Windows.Forms.NotifyIcon? _tray;

    // 教师个人课表下拉（支持输入姓名查询）
    private List<string> _allTeachers = new();
    private bool _teacherGuard;
    private bool _classGuard;   // 从设置反向同步班级下拉时防止 SelectionChanged 递归
    private bool _titleLoading; // 加载标题栏设置时防止事件递归

    // 桌面最大化窗口自动隐藏
    private readonly DispatcherTimer _winTimer;
    private bool _maxActive;
    private System.Windows.Forms.ToolStripMenuItem? _miWp;
    private System.Windows.Forms.ToolStripMenuItem? _miWpHw;
    private System.Windows.Forms.ToolStripMenuItem? _miWpMs;
    private System.Windows.Forms.ToolStripMenuItem? _miCustom;

    /// <summary>提醒设置控件是否已就绪（避免 XAML 加载期 TextChanged 触发保存）。</summary>
    private bool _remindReady;

    public ObservableCollection<string> ClassNames { get; } = new();
    public ObservableCollection<Notification> Notifications { get; } = new();

    /// <summary>打开时默认选中的标签页下标（0=周课程表,1=数据管理,2=显示设置,3=提醒设置,4=通知公告,5=作业公示,6=用户中心）。
    /// 与 LayoutWindow 一样必须走属性 setter：对象初始化器的赋值发生在构造函数之后。</summary>
    public int InitialTabIndex
    {
        set
        {
            if (MainTabs != null && MainTabs.Items.Count > 0)
                MainTabs.SelectedIndex = Math.Clamp(value, 0, MainTabs.Items.Count - 1);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        CbClass.ItemsSource = ClassNames;
        NotificationsPanel.ItemsSource = Notifications;

        // 桌面最大化窗口监视（2 秒轮询，开销极小）
        _winTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _winTimer.Tick += (_, _) => CheckMaxWindow();
        _winTimer.Start();
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

            // F5：用户名为班级名时，默认显示该班课表（在已导入班级中匹配）
            if (!string.IsNullOrEmpty(settings.Username) && ClassNames.Contains(settings.Username))
            {
                settings.SelectedClass = settings.Username;
                settings.ScheduleKind = "Class";
                CbClass.SelectedItem = settings.Username;
                if (Store.EnablePersistence) Store.SaveSettings(settings);
            }

            // 用户中心：显示当前用户名 + 邮箱 / 手机 / 密保
            if (TxtCurUser != null) TxtCurUser.Text = settings.Username;
            if (TxtEmail != null) TxtEmail.Text = settings.Email ?? "";
            if (TxtPhone != null) TxtPhone.Text = settings.Phone ?? "";
            if (CbSecQuestion != null)
            {
                // 选中与已保存密保问题一致的预设项；未设置则回到占位项
                bool matched = false;
                for (int i = 0; i < CbSecQuestion.Items.Count; i++)
                {
                    if (CbSecQuestion.Items[i] is ComboBoxItem ci && (ci.Content as string) == settings.SecurityQuestion)
                    { CbSecQuestion.SelectedIndex = i; matched = true; break; }
                }
                if (!matched) CbSecQuestion.SelectedIndex = 0;
            }
            if (TxtSecAnswer != null) TxtSecAnswer.Text = settings.SecurityAnswer ?? "";

            ChkMaxHide.IsChecked = settings.AutoHideOnMaxWindow;
            ReloadNotes();
            ReloadHomework();
            ReloadMissing();
            ReloadCustomNotes();
            ReloadNotifStyle();
            PopulateThemeCombo(settings.Theme);
            RefreshTeacherCombo();
            UpdateModeIndicator();
            ReloadReminderUi();
            LoadTitleBarSettings();
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

        // 桌面背景：课表 / 作业变化、跨天时自动重绘（仅在已应用时）
        try
        {
            BackdropService.Start();
            AppEvents.BackdropChanged += UpdateTrayBackdropText;
            AppEvents.BackdropChanged += UpdateMsWpButton;
            AppEvents.HomeworkChanged += ReloadHomework;
            AppEvents.MissingChanged += ReloadMissing;
            AppEvents.CustomNoteChanged += OnCustomNoteChanged;
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
            if (settings.AutoShowHomework) ShowHomeworkWin();
            if (settings.AutoShowMissing) ShowMissingWin();
            // 自定义便签：每张各自记住 Visible 开关，启动即恢复显示中的便签
            foreach (var cn in settings.CustomNotes)
                if (cn.Visible) ShowCustomNote(cn.Id);
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

    private void ShowHomeworkWin()
    {
        if (_hwWin == null)
        {
            _hwWin = new HomeworkWindow();
            _hwWin.Closed += (_, _) => { _hwWin = null; _hwOn = false; UpdateToggleLabels(); };
        }
        _hwWin.Show();
        // 先收屏再提到最前，确保便签一定出现在桌面可见区域内（不被控制面板 / 全屏展示盖住）
        FloatingHelper.ClampToScreen(_hwWin);
        FloatingHelper.BringToFront(_hwWin);
        _hwOn = true;
        UpdateToggleLabels();
    }

    private void HideHomeworkWin()
    {
        _hwWin?.Hide();
        _hwOn = false;
        UpdateToggleLabels();
    }

    private void ShowMissingWin()
    {
        if (_msWin == null)
        {
            _msWin = new MissingWindow();
            _msWin.Closed += (_, _) => { _msWin = null; _msOn = false; UpdateToggleLabels(); };
        }
        _msWin.Show();
        FloatingHelper.ClampToScreen(_msWin);
        FloatingHelper.BringToFront(_msWin);
        _msOn = true;
        UpdateToggleLabels();
    }

    private void HideMissingWin()
    {
        _msWin?.Hide();
        _msOn = false;
        UpdateToggleLabels();
    }

    private void UpdateToggleLabels()
    {
        BtnToggleSch.Content = _schOn ? "课表悬浮：开" : "课表悬浮：关";
        BtnToggleNot.Content = _notOn ? "通知悬浮：开" : "通知悬浮：关";
        if (BtnToggleHw != null) BtnToggleHw.Content = _hwOn ? "作业悬浮：开" : "作业悬浮：关";
        if (BtnToggleHw2 != null) BtnToggleHw2.Content = _hwOn ? "隐藏作业便签" : "显示作业便签";
        if (BtnToggleMs != null) BtnToggleMs.Content = _msOn ? "缺交悬浮：开" : "缺交悬浮：关";
        if (BtnToggleMs2 != null) BtnToggleMs2.Content = _msOn ? "隐藏缺交便签" : "显示缺交便签";
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

    /// <summary>把开学周 / 开机自启同步到标题栏控件。</summary>
    private void LoadTitleBarSettings()
    {
        var s = Store.LoadSettings();
        _titleLoading = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(s.SemesterStartMonday) && DateTime.TryParse(s.SemesterStartMonday, out var d))
                DpSemester.SelectedDate = d;
            else
                DpSemester.SelectedDate = null;
            ChkAutoStartTitle.IsChecked = s.AutoStart;
        }
        finally { _titleLoading = false; }
    }

    private void DpSemester_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_titleLoading) return;
        try
        {
            var s = Store.LoadSettings();
            s.SemesterStartMonday = DpSemester.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
            Store.SaveSettings(s);
            AppEvents.RaiseScheduleChanged();   // 开学周变化后刷新周视图 / 悬浮课表的「第几周」
            TxtStatus.Text = string.IsNullOrEmpty(s.SemesterStartMonday)
                ? "已清除开学周，按系统日历周自动估算。"
                : $"开学周已设为 {DpSemester.SelectedDate:yyyy-MM-dd}。";
        }
        catch { }
    }

    private void ChkAutoStartTitle_Changed(object sender, RoutedEventArgs e)
    {
        if (_titleLoading) return;
        try
        {
            bool on = ChkAutoStartTitle.IsChecked == true;
            var s = Store.LoadSettings();
            s.AutoStart = on;
            Store.SaveSettings(s);
            SettingsWindow.ApplyAutoStart(on);
            TxtStatus.Text = on ? "已开启开机自启（写入当前用户启动项）。" : "已关闭开机自启。";
        }
        catch { }
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

        // 上课
        var cs = r.ClassStart;
        ChkCsOn.IsChecked = cs.Enabled;
        TxtCsLead.Text = Math.Max(0, cs.LeadSeconds).ToString();
        TxtCsTitle.Text = cs.Title;
        TxtCsDetail.Text = cs.Detail;
        TxtCsVoice.Text = cs.VoiceText;
        TxtCsDanmaku.Text = cs.Danmaku;

        // 下课
        var ce = r.ClassEnd;
        ChkCeOn.IsChecked = ce.Enabled;
        TxtCeLead.Text = Math.Max(0, ce.LeadSeconds).ToString();
        TxtCeTitle.Text = ce.Title;
        TxtCeDetail.Text = ce.Detail;
        TxtCeVoice.Text = ce.VoiceText;
        TxtCeDanmaku.Text = ce.Danmaku;

        // 放学
        var ae = r.AfterSchool;
        ChkAsOn.IsChecked = ae.Enabled;
        TxtAsLead.Text = Math.Max(0, ae.LeadSeconds).ToString();
        TxtAsTitle.Text = ae.Title;
        TxtAsDetail.Text = ae.Detail;
        TxtAsVoice.Text = ae.VoiceText;
        TxtAsDanmaku.Text = ae.Danmaku;

        TxtRemindSeconds.Text = Math.Clamp(r.DisplaySeconds, 3, 60).ToString();
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
            var r = s.Reminder;
            r.Enabled = ChkRemindEnabled.IsChecked == true;

            r.ClassStart.Enabled = ChkCsOn.IsChecked == true;
            r.ClassStart.LeadSeconds = ParseNonNeg(TxtCsLead.Text);
            r.ClassStart.Title = TxtCsTitle.Text ?? "";
            r.ClassStart.Detail = TxtCsDetail.Text ?? "";
            r.ClassStart.VoiceText = TxtCsVoice.Text ?? "";
            r.ClassStart.Danmaku = TxtCsDanmaku.Text ?? "";

            r.ClassEnd.Enabled = ChkCeOn.IsChecked == true;
            r.ClassEnd.LeadSeconds = ParseNonNeg(TxtCeLead.Text);
            r.ClassEnd.Title = TxtCeTitle.Text ?? "";
            r.ClassEnd.Detail = TxtCeDetail.Text ?? "";
            r.ClassEnd.VoiceText = TxtCeVoice.Text ?? "";
            r.ClassEnd.Danmaku = TxtCeDanmaku.Text ?? "";

            r.AfterSchool.Enabled = ChkAsOn.IsChecked == true;
            r.AfterSchool.LeadSeconds = ParseNonNeg(TxtAsLead.Text);
            r.AfterSchool.Title = TxtAsTitle.Text ?? "";
            r.AfterSchool.Detail = TxtAsDetail.Text ?? "";
            r.AfterSchool.VoiceText = TxtAsVoice.Text ?? "";
            r.AfterSchool.Danmaku = TxtAsDanmaku.Text ?? "";

            r.DisplaySeconds = ParseSeconds(TxtRemindSeconds.Text);
            r.PlaySound = ChkRemindSound.IsChecked == true;
            r.VoiceEnabled = ChkRemindVoice.IsChecked == true;
            r.VoiceName = SelectedVoiceName();
            r.VoiceRate = (int)SlVoiceRate.Value;
            r.VoiceVolume = (int)SlVoiceVolume.Value;
            Store.SaveSettings(s);
        }
        catch { }
    }

    private static int ParseNonNeg(string? text)
        => int.TryParse(text?.Trim(), out var v) && v >= 0 && v <= 3600 ? v : 0;

    private static int ParseSeconds(string? text)
        => int.TryParse(text?.Trim(), out var v) && v >= 3 && v <= 60 ? v : 8;

    private void BtnToggleSch_Click(object sender, RoutedEventArgs e)
    {
        if (_schOn) HideScheduleWin(); else ShowScheduleWin();
    }

    private void BtnToggleNot_Click(object sender, RoutedEventArgs e)
    {
        if (_notOn) HideNotificationWin(); else ShowNotificationWin();
    }

    private void BtnToggleHw_Click(object sender, RoutedEventArgs e)
    {
        if (_hwOn) HideHomeworkWin(); else ShowHomeworkWin();
    }

    private void BtnToggleMs_Click(object sender, RoutedEventArgs e)
    {
        if (_msOn) HideMissingWin(); else ShowMissingWin();
    }

    // ---------- 缺交作业名单（独立便签，内容 / 格式 / 主题都在弹窗里改） ----------
    private void ReloadMissing()
    {
        try
        {
            var mb = Store.LoadMissingBoard();   // 首次调用会从 homework.json 迁移旧缺交数据
            ImgMsPreview.Source = BackdropRenderer.RenderMissingPreview(mb, 320, 380);
        }
        catch { }
        UpdateMsWpButton();
    }

    private void BtnEditMs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new MissingEditWindow(Store.LoadMissingBoard());
            if (dlg.ShowDialog() != true) return;

            var saved = dlg.BuildBoard();
            Store.SaveMissingBoard(saved);
            if (Store.LoadSettings().Backdrop?.ShowMissing == true)
                BackdropService.Apply(silent: true, out _);

            ReloadMissing();
            AppEvents.RaiseMissingChanged();
            AppEvents.RaiseBackdropChanged();
            TxtStatus.Text = "缺交作业名单已更新（共 " + saved.Items.Count + " 条记录）";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "打开缺交名单编辑失败：" + ex.Message;
        }
    }

    /// <summary>同步「缺交名单 → 桌面背景」按钮状态。</summary>
    private void UpdateMsWpButton()
    {
        if (BtnMsWp == null) return;
        bool on = (Store.LoadSettings().Backdrop ?? new BackdropSettings()).ShowMissing;
        BtnMsWp.Content = on ? "缺交→桌面背景：开" : "缺交→桌面背景：关";
        BtnMsWp.Background = on
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x6C, 0xDF))
            : System.Windows.Media.Brushes.White;
        BtnMsWp.Foreground = on ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
    }

    private void BtnMsWp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BackdropService.ToggleMissing(out string msg);
            TxtStatus.Text = msg;
            UpdateMsWpButton();
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换缺交名单桌面背景失败：" + ex.Message;
        }
    }

    // ---------- 自定义便签（用户自建自由内容） ----------
    private readonly System.Collections.Generic.Dictionary<string, CustomNoteWindow> _customWins = new();

    /// <summary>
    /// 同步自定义便签状态。入口只有一个「＋ 新增 / 编辑自定义便签」按钮，
    /// 新建 / 选择 / 删除都在编辑窗口内完成，所以这里只负责刷新托盘菜单与可见窗口。
    /// </summary>
    private void ReloadCustomNotes()
    {
        try { UpdateTrayCustomMenu(); } catch { }
    }

    private void BtnToggleCustom_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        try
        {
            var s = Store.LoadSettings();
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == id);
            if (n == null) return;
            n.Visible = !n.Visible;
            Store.SaveSettings(s);
            if (n.Visible) ShowCustomNote(id); else HideCustomNote(id);
            ReloadCustomNotes();
            UpdateTrayCustomMenu();
        }
        catch (Exception ex) { TxtStatus.Text = "切换自定义便签失败：" + ex.Message; }
    }

    /// <summary>
    /// 「＋ 新增 / 编辑自定义便签」：单一入口。
    /// 还没有便签时先新建一张并立即打开编辑窗口（此时便签窗口临时取消置顶，避免挡住对话框）；
    /// 已有便签时直接打开编辑窗口，可在窗口内切换 / 新建 / 删除。
    /// </summary>
    private void BtnCustomNote_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.CustomNotes ??= new System.Collections.Generic.List<CustomNote>();

            CustomNote? target = null;
            bool isNew = s.CustomNotes.Count == 0;
            if (isNew)
            {
                target = new CustomNote
                {
                    Title = "自定义便签",
                    Content = "",
                    Theme = "plain",
                    Visible = true,
                    UpdatedAt = DateTime.Now
                };
                s.CustomNotes.Add(target);
                Store.SaveSettings(s);
            }
            else
            {
                target = s.CustomNotes.FirstOrDefault();
            }
            if (target == null) return;

            // 先把便签窗口显示出来，再临时取消置顶，保证编辑对话框在最前面
            var win = ShowCustomNote(target.Id);
            if (win != null) win.Topmost = false;

            var dlg = new CustomNoteEditWindow(target.Id) { Owner = this };
            bool ok = dlg.ShowDialog() == true;

            // 恢复置顶状态（按便签自身设置）
            if (win != null)
            {
                var after = Store.LoadSettings().CustomNotes.FirstOrDefault(x => x.Id == target.Id);
                win.Topmost = after?.TopMost ?? true;
            }

            var s2 = Store.LoadSettings();
            // 取消新建且内容仍为空：回收这张空便签，避免留下空白窗口
            if (!ok && isNew)
            {
                var n = s2.CustomNotes.FirstOrDefault(x => x.Id == target.Id);
                if (n != null && string.IsNullOrWhiteSpace(n.Content) && string.IsNullOrWhiteSpace(n.Title))
                {
                    s2.CustomNotes.RemoveAll(x => x.Id == target.Id);
                    Store.SaveSettings(s2);
                    HideCustomNote(target.Id, forceClose: true);
                }
            }

            // 编辑窗口里可能新建了其它便签：把需要显示的都显示出来
            foreach (var n in Store.LoadSettings().CustomNotes)
                if (n.Visible && !_customWins.ContainsKey(n.Id)) ShowCustomNote(n.Id);

            ReloadCustomNotes();
            TxtStatus.Text = ok ? "自定义便签已保存。" : "";
        }
        catch (Exception ex) { TxtStatus.Text = "自定义便签操作失败：" + ex.Message; }
    }

    /// <summary>显示指定自定义便签的悬浮窗，返回该窗口（便于临时调整置顶）。</summary>
    private CustomNoteWindow? ShowCustomNote(string id)
    {
        if (_customWins.TryGetValue(id, out var existing))
        {
            if (!existing.IsVisible) existing.Show();
            FloatingHelper.ClampToScreen(existing);
            FloatingHelper.BringToFront(existing);
            return existing;
        }
        var win = new CustomNoteWindow(id);
        win.Closed += (_, _) =>
        {
            _customWins.Remove(id);
            var s = Store.LoadSettings();
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == id);
            if (n != null) { n.Visible = false; Store.SaveSettings(s); }
            ReloadCustomNotes();
            UpdateTrayCustomMenu();
        };
        _customWins[id] = win;
        win.Show();
        FloatingHelper.ClampToScreen(win);
        FloatingHelper.BringToFront(win);
        return win;
    }

    private void HideCustomNote(string id, bool forceClose = false)
    {
        if (_customWins.TryGetValue(id, out var win))
        {
            if (forceClose) win.Close(); else win.Hide();
            _customWins.Remove(id);
        }
    }

    private void OnCustomNoteChanged(string? id)
    {
        // 浮动窗已各自刷新；此处仅同步控制面板列表与托盘菜单
        try { ReloadCustomNotes(); } catch { }
        UpdateTrayCustomMenu();
    }

    private void UpdateTrayCustomMenu()
    {
        try
        {
            if (_miCustom == null) return;
            _miCustom.DropDownItems.Clear();
            var notes = Store.LoadSettings().CustomNotes ?? new System.Collections.Generic.List<CustomNote>();
            if (notes.Count == 0)
            {
                var none = new System.Windows.Forms.ToolStripMenuItem("（暂无自定义便签）") { Enabled = false };
                _miCustom.DropDownItems.Add(none);
                return;
            }
            foreach (var n in notes)
            {
                var label = (string.IsNullOrWhiteSpace(n.Title) ? "（未命名）" : n.Title) + (n.Visible ? " · 显示中" : " · 已隐藏");
                var mi = new System.Windows.Forms.ToolStripMenuItem(label, null, (_, _) => ToggleCustomFromTray(n.Id));
                mi.Checked = n.Visible;
                _miCustom.DropDownItems.Add(mi);
            }
        }
        catch { }
    }

    private void ToggleCustomFromTray(string id)
    {
        try
        {
            var s = Store.LoadSettings();
            var n = s.CustomNotes.FirstOrDefault(x => x.Id == id);
            if (n == null) return;
            n.Visible = !n.Visible;
            Store.SaveSettings(s);
            if (n.Visible) ShowCustomNote(id); else HideCustomNote(id);
            ReloadCustomNotes();
            UpdateTrayCustomMenu();
        }
        catch { }
    }

    private void BtnLayout_Click(object sender, RoutedEventArgs e)
    {
        var w = new LayoutWindow { Owner = this };
        w.ShowDialog();
    }

    // ---------- 界面主题（配色方案） ----------
    private void PopulateThemeCombo(string currentTheme)
    {
        if (CbTheme == null) return;
        CbTheme.ItemsSource = ThemeManager.Themes
            .Select(t => new { Key = t.Key, Name = t.Name })
            .ToList();
        CbTheme.DisplayMemberPath = "Name";
        CbTheme.SelectedValuePath = "Key";
        CbTheme.SelectedValue = ThemeManager.Themes.Any(t => t.Key == currentTheme)
            ? currentTheme
            : "ClassicBlue";
    }

    private void CbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbTheme?.SelectedValue is not string key || string.IsNullOrEmpty(key)) return;
        try
        {
            ThemeManager.Apply(key);
            var s = Store.LoadSettings();
            s.Theme = key;
            Store.SaveSettings(s);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换主题失败：" + ex.Message;
        }
    }

    private void BtnThemeReset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ThemeManager.Apply("ClassicBlue");
            var s = Store.LoadSettings();
            s.Theme = "ClassicBlue";
            Store.SaveSettings(s);
            if (CbTheme != null) CbTheme.SelectedValue = "ClassicBlue";
            TxtStatus.Text = "已恢复默认配色（经典蓝）。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "恢复默认主题失败：" + ex.Message;
        }
    }

    // ---------- 作业公示 → 桌面背景位置调整 ----------
    private void BtnWpSettings_Click(object sender, RoutedEventArgs e)
    {
        // 直接打开「布局设置」对话框并定位到「桌面背景」标签页
        var w = new LayoutWindow { Owner = this, InitialTabIndex = 2 };
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
        var miUser = new System.Windows.Forms.ToolStripMenuItem("用户中心", null, (_, _) => { ShowMainWindow(); MainTabs.SelectedIndex = 6; });
        var miSch = new System.Windows.Forms.ToolStripMenuItem("课表悬浮：开/关", null, (_, _) => BtnToggleSch_Click(this, new RoutedEventArgs()));
        var miNot = new System.Windows.Forms.ToolStripMenuItem("通知悬浮：开/关", null, (_, _) => BtnToggleNot_Click(this, new RoutedEventArgs()));
        var miHw = new System.Windows.Forms.ToolStripMenuItem("作业悬浮：开/关", null, (_, _) => BtnToggleHw_Click(this, new RoutedEventArgs()));
        var miMs = new System.Windows.Forms.ToolStripMenuItem("缺交悬浮：开/关", null, (_, _) => BtnToggleMs_Click(this, new RoutedEventArgs()));
        _miWp = new System.Windows.Forms.ToolStripMenuItem("当天课表 → 桌面背景", null, (_, _) => ToggleBackdropSchedule());
        _miWpHw = new System.Windows.Forms.ToolStripMenuItem("作业公示 → 桌面背景", null, (_, _) => ToggleBackdropHomework());
        _miWpMs = new System.Windows.Forms.ToolStripMenuItem("缺交名单 → 桌面背景", null, (_, _) => ToggleBackdropMissing());
        _miCustom = new System.Windows.Forms.ToolStripMenuItem("自定义便签");
        var miExit = new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitApp());
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
        {
            miPanel, miUser, miSch, miNot, miHw, miMs,
            new System.Windows.Forms.ToolStripSeparator(),
            _miWp, _miWpHw, _miWpMs,
            new System.Windows.Forms.ToolStripSeparator(),
            _miCustom,
            new System.Windows.Forms.ToolStripSeparator(),
            miExit
        });
        menu.Opening += (_, _) => { UpdateTrayBackdropText(); UpdateTrayCustomMenu(); };
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        UpdateTrayBackdropText();
        UpdateTrayCustomMenu();
    }

    /// <summary>同步托盘菜单中「当天课表 / 作业公示 → 桌面背景」的勾选状态。</summary>
    private void UpdateTrayBackdropText()
    {
        try
        {
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            if (_miWp != null)
            {
                _miWp.Checked = b.ShowSchedule;
                _miWp.Text = b.ShowSchedule ? "当天课表 → 桌面背景（开）" : "当天课表 → 桌面背景（关）";
                _miWp.ToolTipText = b.ShowSchedule
                    ? "点击关闭：从桌面背景移除当天课表"
                    : "点击开启：把当天课表画到桌面壁纸上（位于所有窗口之下）";
            }
            if (_miWpHw != null)
            {
                _miWpHw.Checked = b.ShowHomework;
                _miWpHw.Text = b.ShowHomework ? "作业公示 → 桌面背景（开）" : "作业公示 → 桌面背景（关）";
                _miWpHw.ToolTipText = b.ShowHomework
                    ? "点击关闭：从桌面背景移除作业公示"
                    : "点击开启：把作业公示画到桌面壁纸上";
            }
            if (_miWpMs != null)
            {
                _miWpMs.Checked = b.ShowMissing;
                _miWpMs.Text = b.ShowMissing ? "缺交名单 → 桌面背景（开）" : "缺交名单 → 桌面背景（关）";
                _miWpMs.ToolTipText = b.ShowMissing
                    ? "点击关闭：从桌面背景移除缺交作业名单"
                    : "点击开启：把缺交作业名单便签画到桌面壁纸上";
            }
        }
        catch { }
    }

    private void ToggleBackdropMissing()
    {
        try
        {
            BackdropService.ToggleMissing(out string msg);
            TxtStatus.Text = msg;
            UpdateTrayBackdropText();
            UpdateMsWpButton();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换桌面背景失败：" + ex.Message;
        }
    }

    private void ToggleBackdropHomework()
    {
        try
        {
            BackdropService.ToggleHomework(out string msg);
            TxtStatus.Text = msg;
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换桌面背景失败：" + ex.Message;
        }
    }

    private void ToggleBackdropSchedule()
    {
        try
        {
            BackdropService.ToggleSchedule(out string msg);
            TxtStatus.Text = msg;
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换桌面背景失败：" + ex.Message;
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

    // ---------- 用户中心 ----------
    private void BtnSaveUser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            string newUser = (TxtNewUser.Text ?? "").Trim();
            string p1 = TxtNewPwd.Password ?? "";
            string p2 = TxtNewPwd2.Password ?? "";
            if (string.IsNullOrEmpty(newUser))
            {
                ShowUserMsg("用户名不能为空。", false);
                return;
            }
            if (p1 != p2)
            {
                ShowUserMsg("两次输入的密码不一致。", false);
                return;
            }
            s.Username = newUser;
            if (!string.IsNullOrEmpty(p1)) s.Password = p1;
            s.Email = (TxtEmail?.Text ?? "").Trim();
            s.Phone = (TxtPhone?.Text ?? "").Trim();
            // 密保：选中占位项（或不设）则清空；否则保存所选问题与答案
            string secQ = "";
            if (CbSecQuestion?.SelectedItem is ComboBoxItem ci && !string.IsNullOrEmpty(ci.Content as string)
                && (ci.Content as string) != "（不设密保问题）")
                secQ = (ci.Content as string) ?? "";
            s.SecurityQuestion = secQ;
            s.SecurityAnswer = secQ == "" ? "" : (TxtSecAnswer?.Text ?? "").Trim();
            Store.SaveSettings(s);   // 游客模式不会落盘（EnablePersistence=false），符合「游客不保存」
            if (TxtCurUser != null) TxtCurUser.Text = s.Username;
            TxtNewUser.Text = "";
            TxtNewPwd.Password = "";
            TxtNewPwd2.Password = "";
            ShowUserMsg("已保存。下次启动若用户名为班级名，将默认显示该班课表。", true);
        }
        catch (Exception ex)
        {
            ShowUserMsg("保存失败：" + ex.Message, false);
        }
    }

    private void ShowUserMsg(string text, bool ok)
    {
        if (TxtUserMsg == null) return;
        TxtUserMsg.Text = text;
        TxtUserMsg.Foreground = ok
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        TxtUserMsg.Visibility = Visibility.Visible;
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
        _hwWin?.Close();
        _msWin?.Close();
        foreach (var kv in _customWins) try { kv.Value.Close(); } catch { }
        _customWins.Clear();
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

    // ---------- 一键清除全部导入数据（课表 / 教师个人课表 / 调代覆盖） ----------
    private void BtnClearImported_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定要清除全部已导入的数据吗？\n\n将删除：基础课表、教师个人课表、全部调课 / 代课记录（共 3 类导入数据）。\n此操作不可撤销，清除后需重新导入。\n（通知公告、作业公示、布局与提醒设置不会受影响。）",
            "一键清除导入数据", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        try
        {
            var dir = ScheduleCore.Services.Store.AppDir;
            int removed = 0;
            void TryDelete(string name)
            {
                var p = System.IO.Path.Combine(dir, name);
                if (System.IO.File.Exists(p)) { System.IO.File.Delete(p); removed++; }
            }
            TryDelete("schedule.json");
            TryDelete("teacher_schedule.json");
            TryDelete("overrides.json");

            // 复位与课表相关的选择，并刷新下拉与悬浮窗
            var st = ScheduleCore.Services.Store.LoadSettings();
            st.SelectedClass = "";
            st.SelectedTeacher = "";
            st.ScheduleKind = "Class";
            ScheduleCore.Services.Store.SaveSettings(st);

            ClassNames.Clear();
            RefreshTeacherCombo();
            UpdateModeIndicator();
            if (CbClass != null) CbClass.SelectedIndex = -1;

            AppEvents.RaiseScheduleChanged();
            TxtStatus.Text = $"已清除导入数据（{removed} 个文件）。请重新导入课表。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "清除导入数据失败：" + ex.Message;
        }
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
    // ---------- 通知条字体 / 背景（顶部字幕条统一外观，选中即生效） ----------
    private sealed class NotifColorOption
    {
        public string Name { get; init; } = "";
        public string Hex { get; init; } = "";
        public System.Windows.Media.Brush Swatch { get; init; } = System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>通知条外观控件是否已就绪（避免加载期 SelectionChanged 触发保存）。</summary>
    private bool _notifStyleReady;

    private static readonly string[] NotifFonts =
    {
        "Microsoft YaHei UI", "微软雅黑", "黑体", "宋体", "楷体", "仿宋", "等线", "Arial"
    };

    private static readonly string[] NotifSizes = { "20", "24", "28", "32", "36", "40", "48", "56", "64" };

    private static NotifColorOption ColorOption(string name, string hex) => new()
    {
        Name = name,
        Hex = hex,
        Swatch = ThemeHelper.Brush(hex, System.Windows.Media.Colors.Gray)
    };

    private static List<NotifColorOption> NotifTextColorOptions() => new()
    {
        ColorOption("白色", "#FFFFFF"),
        ColorOption("亮黄", "#FFE9A8"),
        ColorOption("天蓝", "#CFE6FF"),
        ColorOption("浅绿", "#D5F5D8"),
        ColorOption("粉色", "#FFD9E6"),
        ColorOption("橙色", "#FFD8A8"),
        ColorOption("深灰", "#2B2F36")
    };

    private static List<NotifColorOption> NotifBarColorOptions() => new()
    {
        new() { Name = "跟随主题", Hex = "", Swatch = ThemeHelper.Brush("#8A94A6", System.Windows.Media.Colors.Gray) },
        ColorOption("深蓝", "#1B3A66"),
        ColorOption("墨绿", "#10403B"),
        ColorOption("深灰", "#2B2F36"),
        ColorOption("酒红", "#5B1F2A"),
        ColorOption("紫黑", "#2E2233"),
        ColorOption("浅色", "#F2F5FA")
    };

    /// <summary>把设置里的字体 / 字号 / 字色 / 背景色同步到控件与预览条（不触发保存）。</summary>
    private void ReloadNotifStyle()
    {
        var nl = Store.LoadSettings().NotificationLayout ?? new PanelLayout();
        _notifStyleReady = false;
        try
        {
            CbNotifFont.ItemsSource = NotifFonts;
            string fam = string.IsNullOrWhiteSpace(nl.FontFamily) ? NotifFonts[0] : nl.FontFamily;
            int fi = Array.IndexOf(NotifFonts, fam);
            CbNotifFont.SelectedIndex = fi < 0 ? 0 : fi;

            CbNotifSize.ItemsSource = NotifSizes;
            int size = (int)Math.Round(nl.FontSize <= 0 ? 28 : nl.FontSize);
            int si = Array.IndexOf(NotifSizes, size.ToString());
            CbNotifSize.SelectedIndex = si < 0 ? 2 : si;   // 找不到就回到 28

            var texts = NotifTextColorOptions();
            CbNotifText.ItemsSource = texts;
            int ti = texts.FindIndex(x => string.Equals(x.Hex, string.IsNullOrWhiteSpace(nl.TextColor) ? "#FFFFFF" : nl.TextColor, StringComparison.OrdinalIgnoreCase));
            CbNotifText.SelectedIndex = ti < 0 ? 0 : ti;

            var bars = NotifBarColorOptions();
            CbNotifBg.ItemsSource = bars;
            int bi = bars.FindIndex(x => string.Equals(x.Hex, nl.BarColor ?? "", StringComparison.OrdinalIgnoreCase));
            CbNotifBg.SelectedIndex = bi < 0 ? 0 : bi;
        }
        finally
        {
            _notifStyleReady = true;
            ApplyNotifPreview();
        }
    }

    /// <summary>预览条与顶部通知字幕条用同一套字体 / 字号 / 颜色，改完立刻能看到效果。</summary>
    private void ApplyNotifPreview()
    {
        try
        {
            var font = CbNotifFont.SelectedItem as string ?? NotifFonts[0];
            NotifPreviewText.FontFamily = new System.Windows.Media.FontFamily(font);
            NotifPreviewText.FontSize = ParseNotifSize(CbNotifSize.SelectedItem as string);
            NotifPreviewText.Foreground = (CbNotifText.SelectedItem as NotifColorOption)?.Swatch
                                          ?? System.Windows.Media.Brushes.White;

            var bg = CbNotifBg.SelectedItem as NotifColorOption;
            if (bg == null || string.IsNullOrEmpty(bg.Hex))
                NotifPreview.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");
            else
                NotifPreview.Background = ThemeHelper.Brush(bg.Hex, System.Windows.Media.Color.FromRgb(0x1B, 0x3A, 0x66));
        }
        catch { }
    }

    private static double ParseNotifSize(string? text)
    {
        double v = 28;
        if (double.TryParse(text, out double d)) v = d;
        return Math.Clamp(v, 14, 72);
    }

    private void NotifStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_notifStyleReady) return;
        try
        {
            var s = Store.LoadSettings();
            s.NotificationLayout ??= new PanelLayout();
            s.NotificationLayout.FontFamily = CbNotifFont.SelectedItem as string ?? "";
            s.NotificationLayout.FontSize = ParseNotifSize(CbNotifSize.SelectedItem as string);
            s.NotificationLayout.TextColor = (CbNotifText.SelectedItem as NotifColorOption)?.Hex ?? "";
            s.NotificationLayout.BarColor = (CbNotifBg.SelectedItem as NotifColorOption)?.Hex ?? "";
            Store.SaveSettings(s);

            ApplyNotifPreview();
            AppEvents.RaiseLayoutChanged();   // 顶部字幕条立即换字体 / 颜色
            TxtStatus.Text = "通知条字体与背景已更新。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "保存通知条外观失败：" + ex.Message;
        }
    }

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

    // ---------- 作业公示（预览 + 弹窗编辑） ----------
    /// <summary>刷新两张预览图：直接用桌面便签同一套渲染，所见即所得。</summary>
    private void ReloadHomework()
    {
        try
        {
            var board = Store.LoadHomework() ?? new HomeworkBoard();
            ImgHwPreview.Source = BackdropRenderer.RenderHomeworkPreview(board, 320, 380);
        }
        catch { }
        UpdateHwWpButton();
    }

    /// <summary>「＋ 添加 / 编辑作业公示」：内容、文字格式、主题风格都在弹窗里完成。</summary>
    private void BtnEditHw_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cur = Store.LoadHomework() ?? new HomeworkBoard();
            if (cur.Items.Count == 0) cur.Items.Add(new HomeworkItem());   // 首次打开先给一行空行，填完直接确定

            var dlg = new HomeworkEditWindow(cur);
            if (dlg.ShowDialog() != true) return;

            var saved = dlg.BuildBoard();
            Store.SaveHomework(saved);
            if (Store.LoadSettings().Backdrop?.ShowHomework == true)
                BackdropService.Apply(silent: true, out _);

            ReloadHomework();
            AppEvents.RaiseHomeworkChanged();
            AppEvents.RaiseBackdropChanged();
            TxtStatus.Text = "作业公示已保存（共 " + saved.Items.Count + " 科）";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "打开作业编辑失败：" + ex.Message;
        }
    }

    /// <summary>同步「作业公示 → 桌面背景」按钮状态。</summary>
    private void UpdateHwWpButton()
    {
        if (BtnHwWp == null) return;
        bool on = (Store.LoadSettings().Backdrop ?? new BackdropSettings()).ShowHomework;
        BtnHwWp.Content = on ? "作业→桌面背景：开" : "作业→桌面背景：关";
        BtnHwWp.Background = on
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x6C, 0xDF))
            : System.Windows.Media.Brushes.White;
        BtnHwWp.Foreground = on ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
    }

    private void BtnHwWp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BackdropService.ToggleHomework(out string msg);
            TxtStatus.Text = msg;
            UpdateHwWpButton();
            UpdateTrayBackdropText();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换作业桌面背景失败：" + ex.Message;
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
