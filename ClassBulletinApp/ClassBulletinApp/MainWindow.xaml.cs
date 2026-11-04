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
    /// <summary>诊断用：打开时把「提醒设置」页滚到最底部（<c>--shot mainremindend</c>）。</summary>
    internal bool ReminderScrollEnd;

    private bool _isFull;
    private bool _locked;
    private bool _exiting;

    private ScheduleWindow? _schWin;
    private NotificationWindow? _notifWin;
    private HomeworkWindow? _hwWin;
    private MissingWindow? _msWin;
    private CountdownWindow? _cdWin;
    private DutyWindow? _dutyWin;
    private bool _schOn, _notOn, _hwOn, _msOn, _cdOn, _dutyOn;
    private System.Windows.Forms.NotifyIcon? _tray;

    // 教师个人课表下拉（支持输入姓名查询）
    private List<string> _allTeachers = new();
    private bool _teacherGuard;
    private bool _classGuard;   // 从设置反向同步班级下拉时防止 SelectionChanged 递归
    private bool _titleLoading; // 加载标题栏设置时防止事件递归

    // 桌面最大化窗口自动隐藏
    private readonly DispatcherTimer _winTimer;
    private bool _maxActive;

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

        // 悬浮课表底部的快捷按钮在这里落地。
        // 用事件总线而不是让悬浮窗持有本窗口引用：诊断模式（--shot schedulewin）会单独 new
        // 一个 ScheduleWindow，没有主窗口可调，直接调用会空引用。
        AppEvents.QuickOpenHomework += OnQuickOpenHomework;
        AppEvents.QuickOpenMissing += OnQuickOpenMissing;
        AppEvents.QuickOpenCustomNote += OnQuickOpenCustomNote;
        AppEvents.QuickOpenCountdown += OnQuickOpenCountdown;
        AppEvents.QuickOpenDuty += OnQuickOpenDuty;
        AppEvents.QuickCheckUpdate += OnQuickCheckUpdate;
    }

    // ---------- 悬浮课表快捷按钮（作业 / 缺交 / 便签 / 更新课表） ----------

    private void OnQuickOpenHomework()
    {
        if (!UserSession.CanEditBoard)
        {
            TxtStatus.Text = "当前账号（" + UserSession.Caption + "）没有编辑作业公示的权限，"
                + "需要管理员在「用户管理 → 角色权限」里打开。";
            return;
        }
        ShowHomeworkWin();
    }

    private void OnQuickOpenMissing()
    {
        if (!UserSession.CanEditBoard)
        {
            TxtStatus.Text = "当前账号（" + UserSession.Caption + "）没有编辑缺交名单的权限。";
            return;
        }
        ShowMissingWin();
    }

    /// <summary>
    /// 悬浮课表「便签」快捷入口。
    /// <para>一张便签都没有 → 走「＋ 新增」同一条流程，直接弹出编辑窗口让人马上能输入；</para>
    /// <para>已有便签 → 把该显示的便签摆出来并置前，不弹对话框（改内容在便签上双击或点「编辑」；
    /// 新增 / 删除其它便签也在那个编辑窗口里，避免这里再多一层选择界面）。</para>
    /// </summary>
    private void OnQuickOpenCustomNote()
    {
        if (!UserSession.CanEditBoard)
        {
            TxtStatus.Text = "当前账号（" + UserSession.Caption + "）没有编辑便签的权限，"
                + "需要管理员在「用户管理 → 角色权限」里打开。";
            return;
        }

        try
        {
            var s = Store.LoadSettings();
            var list = s.CustomNotes ??= new System.Collections.Generic.List<CustomNote>();

            if (list.Count == 0)
            {
                BtnCustomNote_Click(this, new RoutedEventArgs());
                return;
            }

            // 只放出「标记为显示」的便签，不把老师刻意隐藏的全部翻出来；
            // 全都隐藏过时至少放出第一张，否则点快捷等于没反应。
            var visible = list.Where(n => n.Visible).ToList();
            if (visible.Count == 0)
            {
                list[0].Visible = true;
                Store.SaveSettings(s);
                visible.Add(list[0]);
            }

            foreach (var n in visible) ShowCustomNote(n.Id);

            TxtStatus.Text = visible.Count == 1
                ? "已在桌面显示自定义便签（双击便签或点「编辑」可修改内容）。"
                : $"已在桌面显示 {visible.Count} 张自定义便签（双击便签或点「编辑」可修改内容）。";
        }
        catch (Exception ex) { TxtStatus.Text = "打开自定义便签失败：" + ex.Message; }
    }

    /// <summary>悬浮课表「倒计时」快捷入口：显示并置前倒计时便签（一张都没有时直接打开编辑窗口）。</summary>
    private void OnQuickOpenCountdown()
    {
        if (!UserSession.CanEditBoard)
        {
            TxtStatus.Text = "当前账号（" + UserSession.Caption + "）没有编辑便签的权限，"
                + "需要管理员在「用户管理 → 角色权限」里打开。";
            return;
        }
        try
        {
            bool empty = Store.LoadCountdown().Items.Count == 0;
            ShowCountdownWin();
            if (empty) OpenCountdownEditor();
            TxtStatus.Text = "倒计时便签已显示（双击便签可编辑事件）。";
        }
        catch (Exception ex) { TxtStatus.Text = "打开倒计时失败：" + ex.Message; }
    }

    /// <summary>悬浮课表「值日」快捷入口：显示并置前值日生名单便签。</summary>
    private void OnQuickOpenDuty()
    {
        if (!UserSession.CanEditBoard)
        {
            TxtStatus.Text = "当前账号（" + UserSession.Caption + "）没有编辑便签的权限，"
                + "需要管理员在「用户管理 → 角色权限」里打开。";
            return;
        }
        try
        {
            bool empty = Store.LoadDuty().Items.Count == 0;
            ShowDutyWin();
            if (empty) OpenDutyEditor();
            TxtStatus.Text = "值日生名单已显示（双击便签可编辑）。";
        }
        catch (Exception ex) { TxtStatus.Text = "打开值日生名单失败：" + ex.Message; }
    }

    private async void OnQuickCheckUpdate()
    {
        if (!UserSession.CanManageUpdate)
        {
            AppEvents.RaiseQuickCheckUpdateDone(false, "当前账号没有「自动更新」权限。");
            return;
        }

        // 后台检查正在进行时立刻回话，别让悬浮窗的按钮空转到 8 秒兜底才恢复
        if (_updBusy)
        {
            AppEvents.RaiseQuickCheckUpdateDone(false, "上一次检查还没结束，请稍等几秒再点。");
            return;
        }

        try
        {
            await ManualCheckUpdateAsync();
        }
        catch (Exception ex)
        {
            AppEvents.RaiseQuickCheckUpdateDone(false, "更新失败：" + ex.Message);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = Store.LoadSettings();
        try
        {
            // ---------- 数据源分支（P2：二一排课主通道） ----------
            // ScheduleSource=="ErYi" 时优先从抓包缓存构建全校课表 + 今日调代课；
            // 失败 / 空缓存时回退到 Store.LoadSchedule()（此前 Excel 导入持久化的结果）。
            bool fromErYi = false;
            int eryiOverrideCount = 0;
            ScheduleFile? schedule = TryLoadErYi(settings, out fromErYi, out eryiOverrideCount);
            if (schedule == null)
                schedule = Store.LoadSchedule();

            if (schedule != null)
            {
                foreach (var c in schedule.Classes) ClassNames.Add(c.ClassName);
                if (!string.IsNullOrEmpty(settings.SelectedClass) && ClassNames.Contains(settings.SelectedClass))
                    CbClass.SelectedItem = settings.SelectedClass;
                else if (ClassNames.Count > 0)
                    CbClass.SelectedIndex = 0;
                TxtStatus.Text = fromErYi
                    ? $"已从二一排课缓存加载 {ClassNames.Count} 个班级课表，今日调代课 {eryiOverrideCount} 条。"
                    : $"已加载 {ClassNames.Count} 个班级课表。";
            }
            else
            {
                TxtStatus.Text = "尚未导入课表，请到「数据管理」导入全校总课程表 Excel 文件。";
            }

            // 登录账号所属班级优先：电教管理员 / 班主任 / 科任老师默认显示自己班（需求三）
            bool ownApplied = ApplyOwnClassDefault();

            // F5：用户名为班级名时，默认显示该班课表（在已导入班级中匹配）
            if (!ownApplied && !string.IsNullOrEmpty(settings.Username) && ClassNames.Contains(settings.Username))
            {
                settings.SelectedClass = settings.Username;
                settings.ScheduleKind = "Class";
                CbClass.SelectedItem = settings.Username;
                if (Store.EnablePersistence) Store.SaveSettings(settings);
            }

            // 用户中心：显示当前用户名 + 邮箱 / 手机 / 密保
            // ★ 必须取登录身份（UserSession），不能取 settings.Username：
            //   升级后 settings.Username 是迁移前的管理员名，而实际登录的可能是 local（电教管理员），
            //   照 settings 显示会让坐在教室机前的人以为自己在编辑管理员账号。
            if (TxtCurUser != null) TxtCurUser.Text = UserSession.Current?.Username ?? "（游客）";
            // 当前密码框永不在界面上回显任何内容（有 PasswordBox 也不预填）
            if (TxtCurPwd != null) TxtCurPwd.Password = "";
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
            if (ChkRememberUser != null) ChkRememberUser.IsChecked = settings.RememberUsername;
            if (ChkRememberPwd != null) ChkRememberPwd.IsChecked = settings.RememberPassword;
            if (ChkAutoLogin != null) ChkAutoLogin.IsChecked = settings.AutoLogin;

            ChkMaxHide.IsChecked = settings.AutoHideOnMaxWindow;
            ReloadNotes();
            ReloadHomework();
            ReloadMissing();
            ReloadNotifStyle();
            PopulateThemeCombo(settings.Theme);
            PopulateClockCombo(settings.ScheduleClockMode);
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

        // 诊断用：截图时把「提醒设置」页滚到最底部，便于核对新增区块
        if (ReminderScrollEnd)
            Dispatcher.BeginInvoke(new Action(() => { try { ReminderScroll?.ScrollToEnd(); } catch { } }));

        // 上下课提醒后台服务
        try { ReminderService.Start(); }
        catch (Exception ex) { TxtStatus.Text += " [提醒服务启动失败：" + ex.Message + "]"; }

        // 课表内容变化监视（手动调课 / 导入临时表 / 更新课表后主动提示）
        try { ScheduleChangeWatcher.Start(); }
        catch (Exception ex) { TxtStatus.Text += " [变化提醒启动失败：" + ex.Message + "]"; }

        // 桌面背景：课表 / 作业变化、跨天时自动重绘（仅在已应用时）
        try
        {
            BackdropService.Start();
            AppEvents.BackdropChanged += UpdateTrayMenuState;
            AppEvents.BackdropChanged += UpdateMsWpButton;
            AppEvents.HomeworkChanged += ReloadHomework;
            AppEvents.MissingChanged += ReloadMissing;
            AppEvents.CustomNoteChanged += OnCustomNoteChanged;
            AppEvents.RestoreFloating += OnRestoreFloating;
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
            // 倒计时 / 值日生名单：同样按记录恢复（默认 Visible=true，第一次启动就浮出来）
            if (settings.PlacementOf(CountdownWindow.PlacementKey).Visible) ShowCountdownWin();
            if (settings.PlacementOf(DutyWindow.PlacementKey).Visible) ShowDutyWin();
        }
        catch (Exception ex) { TxtStatus.Text += " [悬浮窗启动失败：" + ex.Message + "]"; }

        // 自动更新：先回显状态，再按设置挂上定时器（启动延迟见 UpdateStartupDelaySeconds）
        try
        {
            RefreshUpdateStatus();
            SetupUpdateTimer();
        }
        catch (Exception ex) { TxtStatus.Text += " [自动更新初始化失败：" + ex.Message + "]"; }

        UpdateToggleLabels();
        ApplyPermissions();

        try
        {
            if (settings.LaunchFullscreen || settings.LaunchLocked) SetFullscreen(true);
            if (settings.LaunchLocked) ToggleLock();
        }
        catch { }

        // 升级后一次性说明：等窗口真正显示出来再弹，否则 Owner 还没就位
        Loaded += (_, _) => ShowUpgradeNoticeOnce();
    }

    /// <summary>
    /// 同一版本只提示一次（<see cref="ScheduleCore.Models.AppSettings.UpgradeNoticeShownFor"/>）。
    /// <para>诊断 / 截图模式（<c>--ui-check</c>、<c>--shot</c>）直接跳过 —— 自动化流程卡在模态框上等于挂死。</para>
    /// </summary>
    private void ShowUpgradeNoticeOnce()
    {
        if (App.IsDiagnostic) return;

        try
        {
            var st = Store.LoadSettings();
            string cur = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            if (string.Equals(st.UpgradeNoticeShownFor, cur, StringComparison.Ordinal)) return;

            // 先碰一次 overrides：旧记录若还没归周，这一步会触发迁移并写下 MigratedAt，
            // 提示里才能说清"你原来的记录被归到哪了"。失败不影响提示本身。
            try { _ = Store.LoadOverrides().Count; } catch { }
            st = Store.LoadSettings();

            var items = UpgradeNoticeWindow.BuildItems(
                new AppSettingsSnapshot(st.WeekStartDay, st.OverridesMigratedAt),
                st.UpgradeNoticeShownFor);

            new UpgradeNoticeWindow(cur, items) { Owner = this }.ShowDialog();

            st.UpgradeNoticeShownFor = cur;
            Store.SaveSettings(st);
        }
        catch (Exception ex)
        {
            TxtStatus.Text += " [更新说明显示失败：" + ex.Message + "]";
        }
    }

    // ---------- 二一排课数据源（P2） ----------

    /// <summary>
    /// 当 ScheduleSource=="ErYi" 时，从二一排课抓包缓存目录构建全校课表 + 今日生效的调代课覆盖。
    /// 成功：返回构建出的 ScheduleFile，并持久化 schedule.json / overrides.json（保留 manual 覆盖）；
    /// 缓存为空、目录不存在或异常：返回 null，由调用方回退到 Excel 持久化的课表。
    /// </summary>
    private ScheduleFile? TryLoadErYi(AppSettings settings, out bool fromErYi, out int overrideCount)
    {
        fromErYi = false;
        overrideCount = 0;
        if (settings.ScheduleSource != "ErYi")
            return null;

        var dir = ResolveErYiDataDir(settings);
        try
        {
            var result = ErYiClient.BuildFromCache(dir);
            if (result.Schedule.Classes.Count == 0)
            {
                TxtStatus.Text = "二一排课缓存为空或不存在，回退到本地课表。";
                return null;
            }
            // 合并调代覆盖：用新的 temp 覆盖替换旧的 temp，保留用户手填的 manual（与 ImportTempSchedule 语义一致）
            var manual = Store.LoadOverrides().Where(o => o.Source != "temp").ToList();
            Store.SaveSchedule(result.Schedule);
            Store.SaveOverrides(result.Overrides.Concat(manual).ToList());
            fromErYi = true;
            // 缓存里有全量历史调代课（含已过期）：状态栏只报"今天实际生效"的条数
            overrideCount = result.Overrides.Count(o => o.IsActiveOn(DateTime.Today));
            return result.Schedule;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "二一排课缓存加载失败，回退本地：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 解析二一排课缓存目录：优先用设置项 ErYiDataDir；否则默认 %APPDATA%/ClassBulletinApp/eryi；
    /// DEBUG 构建下若默认目录不存在，回退到开发机原始数据目录，便于免复制直接验证。
    /// </summary>
    private static string ResolveErYiDataDir(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ErYiDataDir))
            return settings.ErYiDataDir;
        var dir = Path.Combine(Store.AppDir, "eryi");
#if DEBUG
        if (!Directory.Exists(dir))
        {
            var dev = @"F:\study\班级公布栏\数据对接方案\data";
            if (Directory.Exists(dev)) return dev;
        }
#endif
        return dir;
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
            // ★ 便签右上角的 ✕ 是 Hide（不是 Close），不会触发 Closed 事件。
            //   不同步这里，控制面板会一直显示「隐藏作业便签」，用户点一次等于再隐藏一次、
            //   要点两次才能重新显示（2026-11-01 现场反馈）。
            _hwWin.IsVisibleChanged += (_, _) =>
            {
                bool vis = _hwWin?.IsVisible == true;
                if (_hwOn != vis) { _hwOn = vis; UpdateToggleLabels(); }
            };
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
            // 同上：✕ 只是 Hide，靠可见性变化把按钮文案拉回来
            _msWin.IsVisibleChanged += (_, _) =>
            {
                bool vis = _msWin?.IsVisible == true;
                if (_msOn != vis) { _msOn = vis; UpdateToggleLabels(); }
            };
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

    // ---------- 倒计时 / 值日生名单（与作业便签同一套：悬浮 + 设为背景 + 位置记忆） ----------

    private void ShowCountdownWin()
    {
        if (_cdWin == null)
        {
            _cdWin = new CountdownWindow();
            _cdWin.Closed += (_, _) => { _cdWin = null; _cdOn = false; UpdateToggleLabels(); };
            // ✕ 只是 Hide，靠可见性变化把按钮文案拉回来
            _cdWin.IsVisibleChanged += (_, _) =>
            {
                bool vis = _cdWin?.IsVisible == true;
                if (_cdOn != vis) { _cdOn = vis; UpdateToggleLabels(); }
            };
        }
        _cdWin.Show();
        FloatingHelper.ClampToScreen(_cdWin);
        FloatingHelper.BringToFront(_cdWin);
        SetNoteVisible(CountdownWindow.PlacementKey, true);
        _cdOn = true;
        UpdateToggleLabels();
    }

    private void HideCountdownWin()
    {
        _cdWin?.Hide();
        SetNoteVisible(CountdownWindow.PlacementKey, false);
        _cdOn = false;
        UpdateToggleLabels();
    }

    private void ShowDutyWin()
    {
        if (_dutyWin == null)
        {
            _dutyWin = new DutyWindow();
            _dutyWin.Closed += (_, _) => { _dutyWin = null; _dutyOn = false; UpdateToggleLabels(); };
            _dutyWin.IsVisibleChanged += (_, _) =>
            {
                bool vis = _dutyWin?.IsVisible == true;
                if (_dutyOn != vis) { _dutyOn = vis; UpdateToggleLabels(); }
            };
        }
        _dutyWin.Show();
        FloatingHelper.ClampToScreen(_dutyWin);
        FloatingHelper.BringToFront(_dutyWin);
        SetNoteVisible(DutyWindow.PlacementKey, true);
        _dutyOn = true;
        UpdateToggleLabels();
    }

    private void HideDutyWin()
    {
        _dutyWin?.Hide();
        SetNoteVisible(DutyWindow.PlacementKey, false);
        _dutyOn = false;
        UpdateToggleLabels();
    }

    /// <summary>记住这张便签"下次启动要不要自己浮出来"。</summary>
    private static void SetNoteVisible(string key, bool visible)
    {
        try
        {
            var s = Store.LoadSettings();
            var p = s.PlacementOf(key);
            if (p.Visible == visible) return;
            p.Visible = visible;
            Store.SaveSettings(s);
        }
        catch { }
    }

    private void OpenCountdownEditor()
    {
        try
        {
            bool wasTop = _cdWin?.Topmost == true;
            if (_cdWin != null) _cdWin.Topmost = false;
            new CountdownEditWindow { Owner = _cdWin }.ShowDialog();
            if (_cdWin != null) _cdWin.Topmost = wasTop;
            AppEvents.RaiseCountdownChanged();
        }
        catch (Exception ex) { TxtStatus.Text = "编辑倒计时失败：" + ex.Message; }
    }

    private void OpenDutyEditor()
    {
        try
        {
            bool wasTop = _dutyWin?.Topmost == true;
            if (_dutyWin != null) _dutyWin.Topmost = false;
            new DutyEditWindow { Owner = _dutyWin }.ShowDialog();
            if (_dutyWin != null) _dutyWin.Topmost = wasTop;
            AppEvents.RaiseDutyChanged();
        }
        catch (Exception ex) { TxtStatus.Text = "编辑值日生名单失败：" + ex.Message; }
    }

    // ---------- 取消桌面背景（「设为背景」的反向操作） ----------

    /// <summary>
    /// 取消桌面背景：恢复系统原壁纸，并把当初「设为背景」时藏掉的悬浮窗放回来。
    /// <para>入口有三处：显示设置页的按钮、布局设置 → 桌面背景页、托盘菜单。</para>
    /// </summary>
    private void CancelBackdrop(bool confirm)
    {
        try
        {
            if (confirm)
            {
                var r = MessageBox.Show(this,
                    "取消桌面背景后：\n\n· 桌面壁纸恢复为系统原壁纸\n· 当初用「设为背景」隐藏的课表 / 便签会重新浮在桌面上\n\n要继续吗？",
                    "取消桌面背景", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK) return;
            }

            BackdropPin.Unpin(out string msg);
            TxtStatus.Text = msg;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "取消桌面背景失败：" + ex.Message;
        }
    }

    private void BtnCancelBackdrop_Click(object sender, RoutedEventArgs e) => CancelBackdrop(confirm: true);

    /// <summary>
    /// 「取消桌面背景」之后把窗口放回来。清单由 <see cref="BackdropPin.Unpin"/> 给出，
    /// 只包含当初确实被藏掉的窗口（用户本来就关着的便签不会被硬弹出来）。
    /// </summary>
    private void OnRestoreFloating(string[] tokens)
    {
        if (tokens == null || tokens.Length == 0) return;

        bool layoutChanged = false;
        foreach (var token in tokens)
        {
            try
            {
                if (string.Equals(token, "Schedule", StringComparison.OrdinalIgnoreCase))
                {
                    // 「设为背景」时把悬浮课表改成了靠边自动隐藏，这里改回常驻显示
                    var s = Store.LoadSettings();
                    var l = s.ScheduleLayout ??= new PanelLayout();
                    if (l.AutoHide)
                    {
                        l.AutoHide = false;
                        Store.SaveSettings(s);
                        layoutChanged = true;
                    }
                    ShowScheduleWin();
                }
                else if (string.Equals(token, "Homework", StringComparison.OrdinalIgnoreCase))
                {
                    ShowHomeworkWin();
                }
                else if (string.Equals(token, "Missing", StringComparison.OrdinalIgnoreCase))
                {
                    ShowMissingWin();
                }
                else if (string.Equals(token, "Countdown", StringComparison.OrdinalIgnoreCase))
                {
                    ShowCountdownWin();
                }
                else if (string.Equals(token, "Duty", StringComparison.OrdinalIgnoreCase))
                {
                    ShowDutyWin();
                }
                else if (token.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
                {
                    string id = token.Substring(5);
                    var s = Store.LoadSettings();
                    var note = s.CustomNotes?.FirstOrDefault(x => x.Id == id);
                    if (note == null) continue;      // 便签已被删除
                    if (!note.Visible)
                    {
                        note.Visible = true;         // 自定义便签的可见性是持久化的，不放回会「消失」
                        Store.SaveSettings(s);
                    }
                    ShowCustomNote(id);
                }
            }
            catch { }
        }

        if (layoutChanged) AppEvents.RaiseLayoutChanged();   // 让悬浮课表按新状态重建自动隐藏矩形
    }

    private void UpdateToggleLabels()
    {
        BtnToggleSch.Content = _schOn ? "课表悬浮：开" : "课表悬浮：关";
        BtnToggleNot.Content = _notOn ? "通知悬浮：开" : "通知悬浮：关";
        // 作业 / 缺交便签的开关只在「作业公示」标签页里（显示设置页已不再重复放一套）
        if (BtnToggleHw2 != null) BtnToggleHw2.Content = _hwOn ? "隐藏作业便签" : "显示作业便签";
        if (BtnToggleMs2 != null) BtnToggleMs2.Content = _msOn ? "隐藏缺交便签" : "显示缺交便签";
        if (BtnToggleCd != null) BtnToggleCd.Content = _cdOn ? "隐藏倒计时便签" : "显示倒计时便签";
        if (BtnToggleDuty != null) BtnToggleDuty.Content = _dutyOn ? "隐藏值日便签" : "显示值日便签";
        UpdateModeIndicator();
    }

    /// <summary>
    /// 按当前登录角色统一设置界面入口的可用性。
    /// ★ 权限判定只走 <see cref="UserSession"/>，不在各处散写 <c>if (isAdmin)</c> ——
    /// 全校有十几处能改数据的入口，漏一处就是一个后门。
    /// </summary>
    private void ApplyPermissions()
    {
        bool canData = UserSession.CanManageData;
        bool canPack = UserSession.CanManagePackage;
        bool canTemp = UserSession.CanImportTemp;
        bool canPublish = UserSession.CanPublish;

        void Set(Button? b, bool enabled, string deny)
        {
            if (b == null) return;
            b.IsEnabled = enabled;
            b.ToolTip = enabled ? null : deny;
        }

        // 整份数据（影响全部班级 / 全部教室机）
        Set(BtnImport, canData, UserSession.DenyManageData);
        Set(BtnClearOverrides, canData, UserSession.DenyManageData);
        Set(BtnClearImported, canData, UserSession.DenyManageData);

        // 数据包收发与备份恢复：不动别的机器，班主任默认有（需求二）
        Set(BtnExportPackage, canPack, UserSession.DenyManagePackage);
        Set(BtnImportPackage, canPack, UserSession.DenyManagePackage);
        Set(BtnBackup, canPack, UserSession.DenyManagePackage);

        // ★ 单班导入与教师课表刻意留在「总课表」权限下，不跟临时课表一起放开：
        //   这两个入口都能选任意班级 / 任意教师，一旦给班主任开了，
        //   他就能把别的班整份导入覆盖掉 —— 这不是"改本班"的范畴。
        Set(BtnImportSingle, canData, UserSession.DenyManageData);
        Set(BtnImportTeacher, canData, UserSession.DenyManageData);

        // 每周的临时课表：影响面较小，可单独授权
        Set(BtnImportTemp, canTemp, UserSession.DenyImportTemp);

        // 发布 / 同步策略（一次点击影响 30 台机器）；名单管理能给自己升管理员 —— 这两项恒只给管理员
        // ★ 自动更新设置（BtnUpdateSettings）走 CanManageUpdate：所有角色默认都能改本机更新来源，
        //   否则教室机改了共享文件夹就再也收不到更新（需求三）。
        Set(BtnUpdateSettings, UserSession.CanManageUpdate, UserSession.DenyManageUpdate);
        Set(BtnPublishNow, canPublish, UserSession.DenyPublish);
        Set(BtnManageUsers, UserSession.CanManageUsers, "只有管理员可以管理用户。");

        // 发全校通知的勾选框：无权限时禁用并强制本班（需求一）。
        // 防止电教管理员误发全校通知；无权限只能发本班，授权后可发全校。
        if (ChkNewNoticeSchool != null)
        {
            bool canSchool = UserSession.CanSendSchoolNotice;
            ChkNewNoticeSchool.IsEnabled = canSchool;
            if (!canSchool) ChkNewNoticeSchool.IsChecked = false;
            ChkNewNoticeSchool.ToolTip = canSchool ? null : UserSession.DenySendSchoolNotice;
        }

        // 恢复本机被停用的调课（班主任默认有：那是他自己班的记录）
        Set(BtnRestoreSuspended, UserSession.CanRestoreSuspended, UserSession.DenyRestoreSuspended);

        // 作业公示 / 缺交名单：范围受账号所属班级限制（科任老师只能改自己任教的班）
        ApplyBoardPermissions();

        // 改自己的账号信息：游客没有账号可改（点了也只会报"未登录"）
        Set(BtnSaveUser, UserSession.Current != null, "游客模式没有账号可修改，请先切换/登录一个账号。");

        // 找回密码信息（邮箱 / 手机 / 密保）存在 settings.json 里，全机只有一份，不是每个账号各自一份。
        // 多账号下让非管理员改，等于悄悄覆盖了管理员的找回方式；而找回入口本身也只对管理员开放
        // （其他角色忘密码由管理员重置），所以这一块只给管理员编辑。
        if (PanelRecovery != null)
        {
            PanelRecovery.IsEnabled = UserSession.IsAdmin;
            // 只靠 IsEnabled 看不出来：里面的 TextBox 显式设了白底，禁用后只是字变灰一点，
            // 用户会以为还能改。压暗整块，一眼就能看出"这块不归我管"。
            PanelRecovery.Opacity = UserSession.IsAdmin ? 1.0 : 0.5;
            PanelRecovery.ToolTip = UserSession.IsAdmin
                ? null
                : "找回密码信息是这台机器共用的一份（存在设置里），只有管理员可以修改。忘记密码请让管理员重置。";
        }

        // 身份行带上"这个身份到底能干什么"：权限可配之后，"我是班主任但改不了课"
        // 会让用户以为是程序坏了，写清楚省一轮排查。
        if (TxtRoleLine != null)
            TxtRoleLine.Text = $"当前身份：{UserSession.Caption}　｜　可用权限：{UserSession.PolicySummary}";
    }

    /// <summary>
    /// 当前真正在显示的班级。★ 以界面下拉为准、设置文件为兜底：
    /// 诊断 / 游客模式下 SaveSettings 不落盘，只读文件会拿到上一次的旧班级。
    /// </summary>
    private string CurrentDisplayClass()
        => (CbClass?.SelectedItem as string) ?? Store.LoadSettings().SelectedClass ?? "";

    /// <summary>
    /// 作业公示 / 缺交名单的入口开关。
    /// <para>★ 这两块板报是"本机当前显示的那个班"的，所以除了看能力，还要看这个班在不在
    /// 账号的 <see cref="AppUser.ClassNames"/> 里 —— 科任老师默认只能改自己任教的班。</para>
    /// </summary>
    private void ApplyBoardPermissions()
    {
        string cur = CurrentDisplayClass();
        bool ok = UserSession.CanEditBoardFor(cur);
        string deny = UserSession.DenyEditBoard(cur);

        if (BtnEditHw != null)
        {
            BtnEditHw.IsEnabled = ok;
            BtnEditHw.ToolTip = ok ? null : deny;
            BtnEditHw.Opacity = ok ? 1.0 : 0.55;
        }
        if (BtnEditMs != null)
        {
            BtnEditMs.IsEnabled = ok;
            BtnEditMs.ToolTip = ok ? null : deny;
            BtnEditMs.Opacity = ok ? 1.0 : 0.55;
        }
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

        // 课表变化提醒
        ChkChangeOn.IsChecked = r.ChangeEnabled;
        ChkChangeFull.IsChecked = r.ChangeFullScreen;
        ChkChangeVoice.IsChecked = r.ChangeVoice;
        TxtChangeSeconds.Text = Math.Clamp(r.ChangeSeconds, 1, 60).ToString();
        TxtChangeStatusMin.Text = Math.Clamp(r.ChangeStatusMinutes, 1, 120).ToString();
        TxtChangeDelay.Text = Math.Clamp(r.ChangeDelayMinutes, 0, 60).ToString();
        UpdateChangeDelayHint(r.ChangeDelayMinutes);

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
        UpdatePopupLayoutText(r);

        _remindReady = true;
    }

    /// <summary>「调整小窗位置与大小」：开一个和真实提醒一模一样的小窗，拖完保存。</summary>
    private void BtnPopupLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            var r = s.Reminder ??= new ReminderSettings();

            var w = new ReminderPopup(interactive: true);
            w.BeginLayoutEdit(r, saved =>
            {
                try
                {
                    var s2 = Store.LoadSettings();
                    s2.Reminder ??= new ReminderSettings();
                    s2.Reminder.PopupScale = saved.PopupScale;
                    s2.Reminder.PopupLeft = saved.PopupLeft;
                    s2.Reminder.PopupTop = saved.PopupTop;
                    Store.SaveSettings(s2);
                    UpdatePopupLayoutText(s2.Reminder);
                    TxtStatus.Text = "已保存提醒小窗的位置与大小。";
                }
                catch (Exception ex) { TxtStatus.Text = "保存小窗位置失败：" + ex.Message; }
            });
        }
        catch (Exception ex) { TxtStatus.Text = "打开小窗调整失败：" + ex.Message; }
    }

    /// <summary>把当前小窗位置与大小写成一句人话（顶部居中 / 已自定义 · 缩放百分比）。</summary>
    private void UpdatePopupLayoutText(ReminderSettings? r)
    {
        if (TxtPopupLayout == null) return;
        double s = r != null && r.PopupScale > 0 ? r.PopupScale : 1;
        string where = r?.PopupLeft is double && r?.PopupTop is double ? "已自定义位置" : "顶部居中";
        TxtPopupLayout.Text = $"当前：{where} · {s * 100:0}%";
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

            r.ChangeEnabled = ChkChangeOn.IsChecked == true;
            r.ChangeFullScreen = ChkChangeFull.IsChecked == true;
            r.ChangeVoice = ChkChangeVoice.IsChecked == true;
            r.ChangeSeconds = ParseRange(TxtChangeSeconds.Text, 1, 60, 15);
            r.ChangeStatusMinutes = ParseRange(TxtChangeStatusMin.Text, 1, 120, 10);
            r.ChangeDelayMinutes = ParseRange(TxtChangeDelay.Text, 0, 60, 2);
            UpdateChangeDelayHint(r.ChangeDelayMinutes);
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

    /// <summary>解析有范围的整数设置；空或越界时返回默认值（避免用户删空输入框就把设置归零）。</summary>
    private static int ParseRange(string? text, int min, int max, int fallback)
        => int.TryParse(text?.Trim(), out var v) && v >= min && v <= max ? v : fallback;

    /// <summary>
    /// 把「上课中发生变化，延后到下课 N 分钟」的当前生效值直接显示在输入框下方。
    /// <para>用户反馈"设了 2 分钟，上课时却立刻弹窗"——实际是输入框被填成了 0（0 = 立即弹）。
    /// 光看数字 0 不直观，这里给一句人话，让设置是否生效一眼可见。</para>
    /// </summary>
    private void UpdateChangeDelayHint(int minutes)
    {
        if (TxtChangeDelayHint == null) return;
        if (minutes <= 0)
        {
            TxtChangeDelayHint.Text = "当前：0 分钟 = 上课时间也会立即弹窗（课堂上会打断教学，建议填 2）";
            TxtChangeDelayHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));   // 红：提醒这是"不延迟"
        }
        else
        {
            TxtChangeDelayHint.Text = $"当前：上课中发生变化时，延后到该节下课 {minutes} 分钟后再弹窗";
            TxtChangeDelayHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));   // 绿：已按设置生效
        }
    }

    /// <summary>用户中心的三个登录偏好勾选联动：自动登录必须有密码；取消记住密码则自动登录一并取消。</summary>
    private void UserLoginPref_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkAutoLogin == null || ChkRememberPwd == null) return;

        if (ChkAutoLogin.IsChecked == true && ChkRememberPwd.IsChecked != true)
            ChkRememberPwd.IsChecked = true;

        if (ChkRememberPwd.IsChecked != true && ChkAutoLogin.IsChecked == true)
            ChkAutoLogin.IsChecked = false;
    }

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

    private void BtnToggleCd_Click(object sender, RoutedEventArgs e)
    {
        if (_cdOn) HideCountdownWin(); else ShowCountdownWin();
    }

    private void BtnToggleDuty_Click(object sender, RoutedEventArgs e)
    {
        if (_dutyOn) HideDutyWin(); else ShowDutyWin();
    }

    private void BtnEditCountdown_Click(object sender, RoutedEventArgs e) => OpenCountdownEditor();

    private void BtnEditDuty_Click(object sender, RoutedEventArgs e) => OpenDutyEditor();

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
        string showClass = CurrentDisplayClass();
        if (!UserSession.CanEditBoardFor(showClass))
        {
            TxtStatus.Text = UserSession.DenyEditBoard(showClass);
            return;
        }
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
            UpdateTrayMenuState();
            AppEvents.RaiseBackdropChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换缺交名单桌面背景失败：" + ex.Message;
        }
    }

    // ---------- 自定义便签（用户自建自由内容） ----------
    private readonly System.Collections.Generic.Dictionary<string, CustomNoteWindow> _customWins = new();

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
        // 浮动窗已各自刷新；托盘菜单里不再有便签子菜单，无需再同步。
    }

    private void BtnLayout_Click(object sender, RoutedEventArgs e)
    {
        var w = new LayoutWindow { Owner = this };
        w.ShowDialog();
    }

    // ---------- 悬浮课表日期旁的实时时钟 ----------
    /// <summary>时钟已初始化（防止 SelectionChanged 在灌数据时回写设置）。</summary>
    private bool _clockModeLoading;

    /// <summary>填充「日期旁时钟」下拉（不显示 / 时:分 / 时:分:秒）。</summary>
    private void PopulateClockCombo(int mode)
    {
        if (CbClockMode == null) return;
        _clockModeLoading = true;
        try
        {
            CbClockMode.Items.Clear();
            CbClockMode.Items.Add(new ComboBoxItem { Content = "不显示", Tag = 0 });
            CbClockMode.Items.Add(new ComboBoxItem { Content = "显示 时:分", Tag = 1 });
            CbClockMode.Items.Add(new ComboBoxItem { Content = "显示 时:分:秒", Tag = 2 });
            CbClockMode.SelectedIndex = Math.Clamp(mode, 0, 2);
        }
        finally { _clockModeLoading = false; }
    }

    private void CbClockMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clockModeLoading || CbClockMode == null) return;
        try
        {
            var s = Store.LoadSettings();
            s.ScheduleClockMode = Math.Clamp(CbClockMode.SelectedIndex, 0, 2);
            Store.SaveSettings(s);
            // 走课表变化事件：悬浮课表 Refresh 时会重读设置，立刻显示 / 收起时钟
            AppEvents.RaiseScheduleChanged();
            TxtStatus.Text = s.ScheduleClockMode == 0
                ? "已关闭悬浮课表的日期时钟。"
                : $"悬浮课表日期旁已显示实时时钟（{CbClockMode.Text}）。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换时钟显示失败：" + ex.Message;
        }
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
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "班级公布栏",
            Visible = true
        };
        RebuildTrayMenu();
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>
    /// 按设置重建托盘右键菜单。
    /// <para>★ 菜单项来自 <see cref="AppSettings.TrayMenuItems"/>（用户在「任务栏菜单…」里配），
    /// 默认只留「打开控制面板 / 用户中心 / 退出」—— 以前那排开关和自定义便签子菜单
    /// 在控制面板里都有，塞进右键只会让菜单又长又容易点错。</para>
    /// </summary>
    private void RebuildTrayMenu()
    {
        if (_tray == null) return;

        var s = Store.LoadSettings();
        var items = s.TrayMenuItems != null && s.TrayMenuItems.Count > 0
            ? s.TrayMenuItems
            : TrayCommands.Defaults();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        foreach (var it in items)
        {
            string cmd = (it.Command ?? "").Trim();
            if (!TrayCommands.IsValid(cmd)) continue;   // 手改 json 写错的不让它炸掉整个菜单

            if (cmd == TrayCommands.Separator)
            {
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                continue;
            }

            string text = string.IsNullOrWhiteSpace(it.Text) ? TrayCommands.LabelOf(cmd) : it.Text.Trim();
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(text, null, (_, _) => RunTrayCommand(cmd))
            {
                Tag = cmd,
                ToolTipText = TrayCommands.HintOf(cmd)
            });
        }

        // 兜底：配置被删光 / 写坏时至少留一条「退出」，否则托盘图标点不出任何东西，程序关不掉
        bool hasExit = false;
        foreach (System.Windows.Forms.ToolStripItem i in menu.Items)
            if (i is System.Windows.Forms.ToolStripMenuItem m && (m.Tag as string) == TrayCommands.Exit) hasExit = true;
        if (!hasExit)
        {
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitApp())
            {
                Tag = TrayCommands.Exit
            });
        }

        menu.Opening += (_, _) => UpdateTrayMenuState();

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
        _trayMenu = menu;
        UpdateTrayMenuState();
    }

    /// <summary>执行托盘菜单里的一条命令（标识见 <see cref="TrayCommands"/>）。</summary>
    private void RunTrayCommand(string cmd)
    {
        switch (cmd)
        {
            case TrayCommands.Panel: ShowMainWindow(); break;
            case TrayCommands.UserCenter: ShowMainWindow(); MainTabs.SelectedIndex = 6; break;
            case TrayCommands.ToggleSchedule: BtnToggleSch_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.ToggleNotice: BtnToggleNot_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.ToggleHomework: BtnToggleHw_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.ToggleMissing: BtnToggleMs_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.BackdropSchedule: ToggleBackdropSchedule(); break;
            case TrayCommands.BackdropHomework: ToggleBackdropHomework(); break;
            case TrayCommands.BackdropMissing: ToggleBackdropMissing(); break;
            case TrayCommands.BackdropOff: CancelBackdrop(confirm: false); break;
            case TrayCommands.Exit: ExitApp(); break;
        }
    }

    /// <summary>打开「任务栏菜单」编辑器；保存后立刻重建（不必重启程序）。</summary>
    private void BtnTrayMenu_Click(object sender, RoutedEventArgs e)
    {
        var w = new TrayMenuWindow { Owner = this };
        if (w.ShowDialog() == true) RebuildTrayMenu();
    }

    /// <summary>同步托盘菜单里各开关项的勾选状态（当前开着的打勾）。</summary>
    private void UpdateTrayMenuState()
    {
        try
        {
            if (_trayMenu == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            foreach (System.Windows.Forms.ToolStripItem item in _trayMenu.Items)
            {
                if (item is not System.Windows.Forms.ToolStripMenuItem mi) continue;
                switch (mi.Tag as string)
                {
                    case TrayCommands.ToggleSchedule: mi.Checked = _schOn; break;
                    case TrayCommands.ToggleNotice: mi.Checked = _notOn; break;
                    case TrayCommands.ToggleHomework: mi.Checked = _hwOn; break;
                    case TrayCommands.ToggleMissing: mi.Checked = _msOn; break;
                    case TrayCommands.BackdropSchedule: mi.Checked = b.ShowSchedule; break;
                    case TrayCommands.BackdropHomework: mi.Checked = b.ShowHomework; break;
                    case TrayCommands.BackdropMissing: mi.Checked = b.ShowMissing; break;
                }
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
            UpdateTrayMenuState();
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
            UpdateTrayMenuState();
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
            UpdateTrayMenuState();
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
            var me = UserSession.Current;
            if (me == null)
            {
                ShowUserMsg("当前是游客模式，不能修改账号。请点「切换账号…」用账号登录后再改。", false);
                return;
            }

            var s = Store.LoadSettings();
            var book = Store.LoadUsers();
            var acc = book.Find(me.Username);
            if (acc == null)
            {
                ShowUserMsg("当前账号已不在用户表里（可能已被管理员删除），请重新登录。", false);
                return;
            }

            string newUser = (TxtNewUser.Text ?? "").Trim();
            string curPwd = TxtCurPwd?.Password ?? "";
            string p1 = TxtNewPwd.Password ?? "";
            string p2 = TxtNewPwd2.Password ?? "";

            // ★ 留空 = 不改用户名 / 不改密码：只想换密码时不必连用户名一起重填
            bool changeUser = newUser.Length > 0 && !string.Equals(newUser, acc.Username, StringComparison.Ordinal);
            bool changePwd = p1.Length > 0 || p2.Length > 0;

            if (changePwd && p1 != p2)
            {
                ShowUserMsg("两次输入的新密码不一致。", false);
                return;
            }
            if (newUser.Length == 0 && (TxtNewUser.Text ?? "").Length > 0)
            {
                ShowUserMsg("新用户名不能只有空格。", false);
                return;
            }
            if (changeUser && book.Contains(newUser))
            {
                ShowUserMsg($"用户名「{newUser}」已经被占用，请换一个。", false);
                return;
            }

            // ★ 改用户名 / 改密码必须先验当前密码：教室里是公共电脑，
            //   否则任何能碰到这台机器的人都能直接把管理员账号换掉。
            if (changeUser || changePwd)
            {
                if (curPwd.Length == 0)
                {
                    ShowUserMsg("请先在「当前密码」里输入现在的密码，再修改用户名或密码。", false);
                    TxtCurPwd?.Focus();
                    return;
                }
                if (!PasswordHasher.Verify(curPwd, acc.PasswordHash, acc.Salt))
                {
                    ShowUserMsg("当前密码不正确，修改未生效。", false);
                    TxtCurPwd?.SelectAll();
                    TxtCurPwd?.Focus();
                    return;
                }
                if (changePwd && p1.Length == 0)
                {
                    ShowUserMsg("新密码不能为空（不想改密码就留空所有新密码框）。", false);
                    return;
                }
            }

            string oldName = acc.Username;
            if (changeUser)
            {
                acc.Username = newUser;
                // 兼容旧字段：程序其他地方仍读 settings.Username（如默认为班级名时的展示）
                s.Username = newUser;
            }
            if (p1.Length > 0)
            {
                PasswordHasher.SetPassword(acc, p1);
                s.Password = p1;   // 仅作兼容保留；登录已改为校验 users.json 的哈希
            }

            s.Email = (TxtEmail?.Text ?? "").Trim();
            s.Phone = (TxtPhone?.Text ?? "").Trim();

            // 密保：选中占位项（或不设）则清空；否则保存所选问题与答案
            string secQ = "";
            if (CbSecQuestion?.SelectedItem is ComboBoxItem ci && !string.IsNullOrEmpty(ci.Content as string)
                && (ci.Content as string) != "（不设密保问题）")
                secQ = (ci.Content as string) ?? "";
            s.SecurityQuestion = secQ;
            s.SecurityAnswer = secQ == "" ? "" : (TxtSecAnswer?.Text ?? "").Trim();

            // ---- 登录偏好：记住账号 / 记住密码 / 开机自动登录 ----
            bool rememberUser = ChkRememberUser?.IsChecked == true;
            bool rememberPwd = ChkRememberPwd?.IsChecked == true;
            bool wantAuto = ChkAutoLogin?.IsChecked == true;

            s.RememberUsername = rememberUser;
            s.RememberPassword = rememberPwd;

            // 管理员永不参与自动登录；自动登录必须有密码
            s.AutoLogin = wantAuto && rememberPwd && acc.Role != UserRole.Admin;

            if (rememberPwd)
            {
                s.AutoLoginUser = acc.Username;
                s.AutoLoginPassword = p1.Length > 0 ? p1 : s.AutoLoginPassword;
            }
            else
            {
                s.AutoLoginUser = "";
                s.AutoLoginPassword = "";
            }

            // 改名后自动登录里记的旧名字要跟着改，否则下次开机自动登录失败
            if (changeUser && string.Equals(s.AutoLoginUser, oldName, StringComparison.OrdinalIgnoreCase))
                s.AutoLoginUser = acc.Username;

            s.LastUsername = rememberUser ? acc.Username : "";

            // 游客模式不会落盘（EnablePersistence=false），符合「游客不保存」
            Store.SaveUsers(book);
            Store.SaveSettings(s);

            // 同一实例：改完立即生效，界面按新角色重算权限。
            // ★ 必须把 book 传进去：游客模式下 SaveUsers 不落盘，如果这里让 SignIn 自己去读盘，
            //   读到的是改之前的旧文件，刚改的角色 / 权限就不生效了。
            UserSession.SignIn(acc, book);
            ApplyPermissions();
            // 账号填了班级就顺手切到该班（用户中心里改了班也要立刻体现）
            ApplyOwnClassDefault();

            if (TxtCurUser != null) TxtCurUser.Text = acc.Username;
            TxtNewUser.Text = "";
            TxtCurPwd!.Password = "";
            TxtNewPwd.Password = "";
            TxtNewPwd2.Password = "";

            string tail = (acc.Role != UserRole.Admin && acc.ClassNames.Count > 0)
                ? "该账号填了所属班级：登录后默认显示该班课表。"
                : (ClassNames.Contains(acc.Username) ? "用户名为班级名，下次启动将默认显示该班课表。" : "");
            string what = changeUser && changePwd ? "用户名和密码都已更新。"
                       : changeUser ? "用户名已更新（密码未变）。"
                       : changePwd ? "密码已更新（用户名未变）。"
                       : "已保存。";

            string autoMsg = acc.Role == UserRole.Admin
                ? "管理员账号不参与开机自动登录：本机仍自动以受限账号进入，要改数据时在这里切换即可。"
                : (s.AutoLogin
                    ? "下次开机将直接用这个账号自动登录，不再询问。"
                    : "下次启动会回到登录界面（可以换账号或改用游客体验）。");

            ShowUserMsg(what + autoMsg + tail, true);
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

    // ---------- 用户管理 / 切换账号 ----------

    private void BtnManageUsers_Click(object sender, RoutedEventArgs e)
    {
        if (!UserSession.CanManageUsers)
        {
            ShowUserMsg("只有管理员可以管理用户。", false);
            return;
        }

        var dlg = new UserManagerWindow { Owner = this };
        bool saved = dlg.ShowDialog() == true;

        // 不管"保存并关闭"还是"取消"，都要重绑一次：用户在窗口里可能改过角色权限
        // （那是即时落盘的），此时界面按钮必须跟着变，否则要重启才生效。
        var r = UserSession.Rebind();
        ApplyPermissions();

        if (r is RebindResult.Removed or RebindResult.Disabled)
        {
            MessageBox.Show(this,
                r == RebindResult.Disabled
                    ? "你的账号已被停用，已退出登录。请用其他管理员账号登录后再操作。"
                    : "你的账号已从用户表里删除，已退出登录。",
                "需要重新登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            BtnSwitchUser_Click(this, new RoutedEventArgs());
            return;
        }

        if (saved)
            ShowUserMsg("用户名单已保存。发布数据包后，各教室机会一并收到。", true);
    }

    private void BtnSwitchUser_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LoginWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.IsGuest || dlg.User == null)
        {
            UserSession.SignOut();
            ShowUserMsg("已切换为游客：只能查看课表和控制显示，任何改动都不会保存。", true);
        }
        else
        {
            UserSession.SignIn(dlg.User);
            // 权限可配之后，不能再按"是不是管理员"猜他能干什么 —— 直接把他实际的权限念出来
            ShowUserMsg($"已切换为「{UserSession.Caption}」。可用权限：{UserSession.PolicySummary}。"
                + (UserSession.CanManageData ? "" : "本机数据的修改入口已按该身份限制。"), true);
        }

        ApplyPermissions();
        // 换了账号就按新账号的所属班级切一次（教室机上换人值班的常见场景）
        ApplyOwnClassDefault();
        if (TxtCurUser != null) TxtCurUser.Text = UserSession.Current?.Username ?? "（游客）";
        TxtNewUser.Text = "";
        TxtNewPwd.Password = "";
        TxtNewPwd2.Password = "";
        if (TxtCurPwd != null) TxtCurPwd.Password = "";

        AppEvents.RaiseScheduleChanged();
        TxtStatus.Text = $"当前身份：{UserSession.Caption}";
    }

    // ---------- 关于（独立标签页：简介 + 版本 + 软件更新 + 运行信息） ----------

    /// <summary>上次检查到的 Gitee 发布版本（「下载最新版」按钮要用它的直链）。</summary>
    private UpdateChecker.ReleaseInfo? _aboutRelease;

    /// <summary>切到「关于」标签时才刷新：里面的数据源 / 身份 / 课表规模会随操作变化。</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (TabAbout != null && ReferenceEquals(MainTabs.SelectedItem, TabAbout)) RefreshAboutTab();
    }

    /// <summary>把运行信息填进「关于」页（只读，不涉及权限）。</summary>
    private void RefreshAboutTab()
    {
        try
        {
            var info = AppSummary.Build();
            if (TxtAboutVer != null) TxtAboutVer.Text = "v" + info.Version;
            if (TxtAboutInfoVer != null) TxtAboutInfoVer.Text = $"{info.Version}　（64 位）";
            if (TxtAboutInfoRole != null) TxtAboutInfoRole.Text = $"{info.Role}　·　{info.User}";
            if (TxtAboutInfoClasses != null) TxtAboutInfoClasses.Text = info.ClassText;
            if (TxtAboutInfoSource != null) TxtAboutInfoSource.Text = info.SourceText;
            if (TxtAboutInfoSchema != null) TxtAboutInfoSchema.Text = info.SchemaText;
            if (TxtAboutInfoDir != null) TxtAboutInfoDir.Text = info.DataDir;
            if (TxtAboutInfoLastCheck != null) TxtAboutInfoLastCheck.Text = info.LastCheck;
            _aboutCopyText = info.CopyText;
        }
        catch { }
    }

    private string _aboutCopyText = "";

    /// <summary>检查更新：读 Gitee 上的最新发布版本（异步，不卡界面）。</summary>
    private async void BtnAboutCheck_Click(object sender, RoutedEventArgs e)
    {
        if (BtnAboutCheck == null) return;
        BtnAboutCheck.IsEnabled = false;
        string oldText = BtnAboutCheck.Content as string ?? "检查更新";
        BtnAboutCheck.Content = "检查中…";
        if (TxtAboutUpdStatus != null) TxtAboutUpdStatus.Text = "正在连接 Gitee…";

        try
        {
            var (ok, message, info) = await UpdateChecker.CheckAsync();

            if (TxtAboutUpdStatus != null) TxtAboutUpdStatus.Text = message;

            if (!ok || info == null)
            {
                _aboutRelease = null;
                if (BtnAboutDownload != null) BtnAboutDownload.Visibility = Visibility.Collapsed;
                if (PanelAboutNotes != null) PanelAboutNotes.Visibility = Visibility.Collapsed;
                return;
            }

            _aboutRelease = info;

            // 有新版才给「下载最新版」；没有附件时退回发布页
            if (BtnAboutDownload != null)
            {
                bool canDownload = info.IsNewer && !string.IsNullOrWhiteSpace(info.AssetUrl);
                BtnAboutDownload.Visibility = canDownload ? Visibility.Visible : Visibility.Collapsed;
                BtnAboutDownload.Content = string.IsNullOrWhiteSpace(info.AssetName)
                    ? "下载最新版"
                    : "下载 " + Shorten(info.AssetName, 26);
                BtnAboutDownload.ToolTip = canDownload ? info.AssetUrl : null;
            }

            // 发布说明（只在有新版本时展示，避免每台教室机都铺一屏历史说明）
            if (PanelAboutNotes != null && TxtAboutReleaseNotes != null)
            {
                bool showNotes = info.IsNewer && !string.IsNullOrWhiteSpace(info.Body);
                PanelAboutNotes.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
                if (showNotes) TxtAboutReleaseNotes.Text = info.Body.Trim();
            }
        }
        catch (Exception ex)
        {
            if (TxtAboutUpdStatus != null) TxtAboutUpdStatus.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            BtnAboutCheck.IsEnabled = true;
            BtnAboutCheck.Content = oldText;
        }
    }

    private static string Shorten(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max - 1) + "…";

    private void BtnAboutReleasePage_Click(object sender, RoutedEventArgs e)
        => OpenUrl(UpdateChecker.ReleasePageUrl, "打开 Gitee 发布页");

    private void BtnAboutDownload_Click(object sender, RoutedEventArgs e)
    {
        string url = _aboutRelease?.AssetUrl ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(UpdateChecker.ReleasePageUrl, "打开 Gitee 发布页");
            return;
        }
        OpenUrl(url, "下载最新版");
    }

    private void BtnAboutCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_aboutCopyText)) RefreshAboutTab();
            System.Windows.Clipboard.SetText(_aboutCopyText);
            if (BtnAboutCopy != null) BtnAboutCopy.Content = "已复制 ✓";
        }
        catch
        {
            if (BtnAboutCopy != null) BtnAboutCopy.Content = "复制失败";
        }
    }

    private void BtnAboutOpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Store.AppDir + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "打不开数据目录：" + ex.Message;
        }
    }

    /// <summary>用系统默认浏览器打开一个网址（失败只写状态栏，不弹框打断）。</summary>
    private void OpenUrl(string url, string what)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = what + "失败：" + ex.Message;
        }
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
        _cdWin?.Close();
        _dutyWin?.Close();
        foreach (var kv in _customWins) try { kv.Value.Close(); } catch { }
        _customWins.Clear();
        base.OnClosing(e);
    }

    // ---------- 班级选择（切换到班级课表） ----------

    /// <summary>
    /// 登录账号的所属班级优先：电教管理员 / 班主任 / 科任老师登录后，
    /// 若账号填了班级（<see cref="AppUser.ClassNames"/>），默认把课表切到自己班。
    ///
    /// <para>★ 只在「启动加载 / 切换账号 / 保存账号」这几个时机调用，
    /// <b>不要</b>放进任何刷新流程 —— 否则用户手动切到别的班看课表，一刷新就被拉回去。
    /// 管理员不参与（管理员看全校，切走才是常态）。</para>
    /// </summary>
    /// <returns>是否发生了切换（调用方据此决定要不要再走旧的回退规则）。</returns>
    private bool ApplyOwnClassDefault()
    {
        var u = UserSession.Current;
        if (u == null || UserSession.IsAdmin) return false;
        if (ClassNames.Count == 0) return false;

        // 账号可能填了多个班（年级组长），取第一个在已导入课表里存在的
        string? own = null;
        foreach (var c in u.ClassNames)
        {
            string t = (c ?? "").Trim();
            if (t.Length > 0 && ClassNames.Contains(t)) { own = t; break; }
        }
        if (own == null) return false;

        var s = Store.LoadSettings();
        bool already = string.Equals(s.SelectedClass, own, StringComparison.Ordinal)
                       && string.Equals(s.ScheduleKind, "Class", StringComparison.OrdinalIgnoreCase)
                       && (CbClass.SelectedItem as string) == own;
        if (already) return true;   // 已经是这个班，算作"已处理"，别再走用户名回退

        s.SelectedClass = own;
        s.ScheduleKind = "Class";
        if (Store.EnablePersistence) Store.SaveSettings(s);

        // 自己改 + 自己发通知，走 _classGuard 抑制 SelectionChanged 的二次保存
        _classGuard = true;
        try { CbClass.SelectedItem = own; }
        finally { _classGuard = false; }

        UpdateModeIndicator();
        ApplyBoardPermissions();
        AppEvents.RaiseScheduleChanged();
        return true;
    }

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
            // 换班之后"能不能改这个班的作业"可能就变了（科任老师只管自己教的班）
            ApplyBoardPermissions();
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
        // 用户主动用 Excel 定义/覆盖基础课表后，把主数据源切到 Excel，避免下次启动又被 ErYi 缓存覆盖
        st.ScheduleSource = "Excel";
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
            Title = "选择全校总课程表（可一次多选七 / 八 / 九年级三份）",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // ★ 按班合并，绝不整份替换：七/八/九年级是三个独立文件，
            //   整份替换会导致"最后导入的那个年级把前两个年级全抹掉"（只剩 30 个班）。
            //   与 r8 的「单班导入」是同一套合并语义。
            var cur = Store.LoadSchedule() ?? new ScheduleFile();
            int before = cur.Classes.Count;

            int parsed = 0;
            var skipped = new System.Collections.Generic.List<string>();
            var overwritten = new System.Collections.Generic.List<string>();
            var sources = new System.Collections.Generic.List<string>();

            foreach (var path in dlg.FileNames)
            {
                ScheduleFile sf;
                try
                {
                    sf = ScheduleImporter.Import(path);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{System.IO.Path.GetFileName(path)}（{ex.Message}）");
                    continue;
                }
                if (sf.Classes.Count == 0)
                {
                    skipped.Add($"{System.IO.Path.GetFileName(path)}（未识别到班级，可能不是全校总课程表）");
                    continue;
                }

                parsed++;
                sources.Add(System.IO.Path.GetFileName(path));
                foreach (var c in sf.Classes)
                {
                    if (cur.Classes.Any(x => x.ClassName == c.ClassName)) overwritten.Add(c.ClassName);
                    cur.Classes.RemoveAll(x => x.ClassName == c.ClassName);
                    cur.Classes.Add(c);
                }
            }

            if (parsed == 0)
            {
                TxtStatus.Text = "没有导入任何课表：" + string.Join("；", skipped);
                return;
            }

            // 统一按「年级 → 班号」排序：否则多选合并后下拉列表会变成「八、九、七」这种导入顺序
            ScheduleImporter.SortClasses(cur);

            cur.SourceFiles = sources;
            cur.SourceFile = dlg.FileNames.Length == 1
                ? dlg.FileName
                : $"合并自 {parsed} 份：{string.Join("、", sources)}";
            cur.ImportedAt = DateTime.Now;

            ApplyImportedSchedule(cur, dlg.FileNames[0], out int tc);

            int total = cur.Classes.Count;
            int net = total - before;
            var ow = overwritten.Distinct().ToList();
            string mergeInfo = $"共 {total} 个班（原有 {before}、净增 {Math.Max(0, net)}" +
                (ow.Count == 0 ? "、无同名覆盖" :
                 ow.Count <= 5 ? $"、覆盖同名班 {string.Join("、", ow)}" : $"、覆盖同名班 {ow.Count} 个") + "）";

            string head = parsed == 1
                ? $"已导入总课表：{mergeInfo}"
                : $"已导入 {parsed} 份总课表：{mergeInfo}";
            TxtStatus.Text = $"{head}；已整理 {tc} 位教师的个人课表（顶栏「教师」下拉可输入姓名查询）。"
                + (skipped.Count > 0 ? $"　未处理：{string.Join("；", skipped)}" : "");

            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入失败：" + ex.Message;
        }
    }

    // ---------- 导入临时课表（含 调/代 标记）：只生成覆盖记录，绝不改动基础课表 ----------
    private void BtnImportTemp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            Title = "选择全校班级临时总课表"
        };
        if (dlg.ShowDialog() != true) return;

        // 临时课表是“补丁”，必须先有底表；否则打上去也无处可打
        var baseSf = Store.LoadSchedule();
        if (baseSf == null || baseSf.Classes.Count == 0)
        {
            TxtStatus.Text = "请先导入全校总课程表（或在设置中改用二一缓存），再导入临时课表——临时课表只产生调代覆盖，不提供基础课表。";
            return;
        }

        // ★ 问生效区间：临时课表是快照、文件里没有日期，不问就会变成"永久生效"（设计 §4.5.5）
        var picker = new EffectiveRangeWindow(BoardBuilder.ResolveDisplayDate(Store.LoadSettings()),
            "这份临时课表（带 调/代 标记的部分）从哪天到哪天生效？")
        { Owner = this };
        if (picker.ShowDialog() != true) return;

        try
        {
            var (tempOverrides, tempClassNames) =
                ScheduleImporter.ImportTempOverrides(dlg.FileName, picker.From, picker.To);

            // 班级名对不上就挂不上去：先拦掉“选错年级的文件”，再提示个别不相符的班
            var existing = new HashSet<string>(baseSf.Classes.Select(c => c.ClassName));
            if (tempClassNames.Count > 0 && !tempClassNames.Any(n => existing.Contains(n)))
            {
                TxtStatus.Text = $"这份临时课表的班级（如「{tempClassNames[0]}」）与当前基础课表一个都对不上，可能选错了年级的文件。已取消导入，基础课表与调代课均未改动。";
                return;
            }

            var unknown = tempOverrides.Select(o => o.ClassName).Distinct()
                                       .Where(n => !existing.Contains(n))
                                       .OrderBy(n => n).ToList();

            // 临时课表是"整周快照"：同一周同一班，没再标记 调/代 的格子必须还原成底表原课程。
            // 因此先清掉「本区间 + 本份文件涉及的班级」下所有导入来源（temp）的记录，再加入新标记。
            // ★ 只清这份文件里出现的班级：教务有时分年级发（八年级一份、七年级一份），
            //   若不加班级过滤，后导的那份会把先导的那份整周清空。
            // ★ 手工记录（manual）与长期改课（basePatch）一律不动。
            var affected = new HashSet<string>(tempClassNames, StringComparer.Ordinal);
            var list = Store.LoadOverrides();

            bool InScope(LessonOverride x)
                => x.OverlapsRange(picker.From, picker.To)
                   && (affected.Count == 0 || affected.Contains(x.ClassName));

            // ★ 同步策略：本机设置为「覆盖」时，手工调课也一起停用（不删除，可恢复）。
            //   本机导入是人工操作，必须先弹确认 —— 静默清掉老师的手改最容易出事。
            int suspended = 0;
            if (Store.LoadSettings().OverwriteManualOverrides)
            {
                var manuals = list.Where(x => x.Source == "manual" && !x.Suspended && InScope(x)).ToList();
                if (manuals.Count > 0)
                {
                    var ans = MessageBox.Show(
                        $"当前同步策略为「以数据源为准」，这份临时课表会同时停用本机的 {manuals.Count} 条手工调课。\n\n"
                        + "记录不会被删除，可在「恢复被主机停用的本机调课」里复活。\n\n继续导入吗？",
                        "导入临时课表", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ans != MessageBoxResult.OK) return;

                    foreach (var m in manuals)
                    {
                        m.Suspended = true;
                        m.SuspendedAt = DateTime.Now;
                        m.SuspendedBy = "本机导入临时课表";
                    }
                    suspended = manuals.Count;
                }
            }

            int restored = list.RemoveAll(x => x.Source == "temp" && InScope(x));
            list.AddRange(tempOverrides);
            Store.SaveOverrides(list);

            int tiao = tempOverrides.Count(o => o.Type == "调");
            int dai = tempOverrides.Count(o => o.Type == "代");

            var st = Store.LoadSettings();
            st.LastImportPath = dlg.FileName;
            Store.SaveSettings(st);

            string range = $"{picker.From:M月d日} – {picker.To:M月d日}";
            string revertInfo = restored > 0
                ? $"　另有 {restored} 条此前的调代课在这份临时课表里已不再标记，已还原为底表原课程。"
                : "";
            if (suspended > 0)
                revertInfo += $"　按「以数据源为准」策略，另有 {suspended} 条本机手工调课已停用（可恢复）。";
            if (unknown.Count > 0)
            {
                TxtStatus.Text = $"已导入临时课表：调课 {tiao} 条、代课 {dai} 条，生效期 {range}；基础课表未改动。" + revertInfo +
                                 $"另有 {unknown.Count} 个班级名在基础课表中不存在，这些记录不会生效：" +
                                 string.Join("、", unknown.Take(6)) + (unknown.Count > 6 ? " 等" : "");
            }
            else
            {
                TxtStatus.Text = $"已导入临时课表：调课 {tiao} 条、代课 {dai} 条，生效期 {range}（过期自动失效）。基础课表保持不变。" + revertInfo;
            }
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
            var imported = ScheduleImporter.ImportSingleClass(dlg.FileName);

            // ★ 单班课表只替换「该班」这一份，不能把整份底表换掉（否则一台机器上导了单班课表，
            //   其余几十个班就全没了）。已有底表时按班名合并，没有底表时就是新建。
            var cur = Store.LoadSchedule() ?? new ScheduleFile();
            int before = cur.Classes.Count;
            foreach (var c in imported.Classes)
            {
                cur.Classes.RemoveAll(x => x.ClassName == c.ClassName);
                cur.Classes.Add(c);
            }
            cur.SourceFile = imported.SourceFile;
            cur.ImportedAt = DateTime.Now;

            ApplyImportedSchedule(cur, dlg.FileName, out int tcSingle);

            if (ClassNames.Count > 0)
            {
                var st = Store.LoadSettings();
                st.SelectedClass = imported.Classes.Count > 0 ? imported.Classes[0].ClassName : ClassNames[0];
                st.ScheduleKind = "Class";
                Store.SaveSettings(st);
                UpdateModeIndicator();
            }

            string merged = before > 0
                ? $"已合并到现有课表（原有 {before} 个班，现共 {ClassNames.Count} 个班）"
                : "已建立基础课表";
            TxtStatus.Text = $"已导入单个班级课表：{string.Join("、", imported.Classes.Select(c => c.ClassName))}；{merged}；已整理 {tcSingle} 位教师的个人课表。";
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
        // 三选一（本周 / 指定日期范围 / 全部）。保护 basePatch（长期改课），由 OverrideCleaner 统一负责。
        var dlg = new ClearOverridesWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        int removed = OverrideCleaner.Apply(dlg.Result);
        AppEvents.RaiseScheduleChanged();
        TxtStatus.Text = OverrideCleaner.Receipt(dlg.Result, removed);
    }

    /// <summary>恢复被「以数据源为准」策略停用的本机手工调课。</summary>
    private void BtnRestoreSuspended_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SuspendedOverridesWindow { Owner = this };
        dlg.ShowDialog();
        TxtStatus.Text = "已打开「恢复被主机停用的本机调课」。恢复后如需长期生效，请让管理员把同步策略改回「保留本机手工调课」。";
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

    // ---------- 数据包导出 / 导入（离线版多教室机同步） ----------

    private void BtnExportPackage_Click(object sender, RoutedEventArgs e)
    {
        var sf = Store.LoadSchedule();
        if (sf == null || sf.Classes.Count == 0)
        {
            TxtStatus.Text = "当前没有基础课表，无法导出数据包。请先导入全校总课程表（七 / 八 / 九年级三份）。";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "班级公布栏数据包 (*.cbpack)|*.cbpack",
            DefaultExt = ".cbpack",
            AddExtension = true,
            FileName = $"课表数据_{DateTime.Now:yyyyMMdd_HHmm}.cbpack",
            Title = "导出数据包"
        };
        if (dlg.ShowDialog() != true) return;

        var r = SchedulePackage.Export(
            dlg.FileName, sf, Store.LoadOverrides(), Store.LoadNotifications(),
            Store.LoadSettings(), Environment.MachineName,
            userBook: Store.LoadUsers());

        if (!r.Ok)
        {
            TxtStatus.Text = "导出失败：" + r.Error;
            return;
        }

        TxtStatus.Text = $"已导出数据包：{r.ClassCount} 个班、调代 {r.OverrideCount} 条" +
            (string.IsNullOrEmpty(r.OverrideRange) ? "" : $"（{r.OverrideRange}）") +
            $"、全校通知 {r.NoticeCount} 条；文件 {r.Size / 1024.0:0.#} KB。" +
            "本机的手工调课、作业、缺交、布局、密码都不会被打包。";
    }

    private void BtnImportPackage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "班级公布栏数据包 (*.cbpack)|*.cbpack|所有文件 (*.*)|*.*",
            Title = "导入数据包"
        };
        if (dlg.ShowDialog() != true) return;

        var pkg = SchedulePackage.Inspect(dlg.FileName);
        if (!pkg.Ok)
        {
            TxtStatus.Text = "数据包无效：" + pkg.Error;
            System.Windows.MessageBox.Show(pkg.Error, "导入数据包",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var m = pkg.Manifest!;
        string msg =
            "数据包内容：\n" +
            $"　打包时间：{m.BuiltAt:yyyy-MM-dd HH:mm}　来源：{(string.IsNullOrWhiteSpace(m.BuiltBy) ? "未标注" : m.BuiltBy)}\n" +
            $"　基础课表：{m.ClassCount} 个班\n" +
            $"　调代课：{m.OverrideCount} 条" +
            (string.IsNullOrEmpty(m.OverrideRange) ? "" : $"（{m.OverrideRange}）") + "\n" +
            $"　全校通知：{m.NoticeCount} 条\n\n" +
            "将覆盖：基础课表、" +
            (m.OverrideAuthoritative
                ? "全部导入来源的调代课（包里没覆盖到的周也会取消、还原成底表原课程）"
                : "该时间段内的调代课") +
            "、全校通知、开学周 / 主题。\n" +
            "会保留：本机手工调课、长期改课、本班通知、显示班级、窗口布局、密码、作业、缺交、自定义便签。\n\n" +
            "导入前会自动备份当前数据。确定导入吗？";

        if (System.Windows.MessageBox.Show(msg, "导入数据包",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var r = SchedulePackage.Apply(pkg);
        if (!r.Ok)
        {
            TxtStatus.Text = "导入失败：" + r.Error;
            System.Windows.MessageBox.Show(r.Error, "导入数据包",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 复用统一导入后处理：刷新班级下拉、重建教师表、把主数据源标记为 Excel
        ApplyImportedSchedule(pkg.Schedule!, dlg.FileName, out int tc);
        AppEvents.RaiseScheduleChanged();

        var sb = new System.Text.StringBuilder();
        sb.Append($"已导入数据包：{r.ClassCount} 个班；调代新增 {r.OverrideAdded} 条、清理 {r.OverrideRemoved} 条"
                  + (r.AuthoritativeCleared ? "（含未覆盖周已还原的）" : ""));
        if (r.NoticeAdded > 0) sb.Append($"；全校通知 {r.NoticeAdded} 条（本班通知保留 {r.NoticeKept} 条）");
        sb.Append($"；教师表 {tc} 位。");
        if (!string.IsNullOrEmpty(r.BackupDir)) sb.Append("　原数据已备份。");
        if (r.UserCount > 0) sb.Append($"　用户表 {r.UserCount} 个账号已同步。");
        foreach (var w in r.Warnings) sb.Append("　⚠ " + w);
        TxtStatus.Text = sb.ToString();

        // 包内可能带了用户表与角色权限：Apply 内部已经重绑过身份，这里把界面刷新一遍。
        // 必须处理"自己被打包踢掉"的情况，否则会带着一个已失效的身份继续跑。
        bool kicked = r.Identity is RebindResult.Removed or RebindResult.Disabled;
        ApplyPermissions();

        if (kicked)
        {
            System.Windows.MessageBox.Show(this,
                "这个数据包把你当前登录的账号"
                + (r.Identity == RebindResult.Disabled ? "停用了" : "删除了")
                + "，因此已退出登录。请用仍然有效的账号重新登录。",
                "身份已失效", MessageBoxButton.OK, MessageBoxImage.Warning);
            BtnSwitchUser_Click(this, new RoutedEventArgs());
        }
    }

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        var w = new BackupWindow { Owner = this };
        w.ShowDialog();

        if (w.Restored)
        {
            var sf = Store.LoadSchedule();
            ClassNames.Clear();
            if (sf != null) foreach (var c in sf.Classes) ClassNames.Add(c.ClassName);
            RefreshTeacherCombo();
            UpdateModeIndicator();
            AppEvents.RaiseScheduleChanged();
            TxtStatus.Text = "已从备份恢复，界面已刷新。";
        }
    }

    // ---------- 周视图：周起始日 ----------
    // ★ 控件已移到「周课程表」标签的工具栏（「清除本周调课」右边，见 WeekView.xaml），
    //   读写逻辑也在 WeekView 里，这里不再保留副本 —— 两处各有一份会出现
    //   "这页改了、那页还显示旧值"的错觉。


    // ---------- 自动更新（O3：共享文件夹 / U 盘 / FTP 三通道统一） ----------
    /// <summary>周期检查定时器（30 秒一跳，到点才真正去访问数据源）。</summary>
    private DispatcherTimer? _updTimer;

    /// <summary>下一次允许检查的时刻。</summary>
    private DateTime _updNextAt = DateTime.MinValue;

    /// <summary>检查/下载进行中，避免定时器与手动点击撞车。</summary>
    private bool _updBusy;

    /// <summary>source 目录里"已确认处理过"的指纹（发布成功或确认无变化）。</summary>
    private string _srcFp = "";

    /// <summary>上一次扫描到的指纹，用于「连续两次一致」的防抖。</summary>
    private string _srcFpPending = "";

    /// <summary>自动发布进行中。</summary>
    private bool _pubBusy;

    /// <summary>正在后台探测 source 目录（避免上一轮没回来又发下一轮）。</summary>
    private bool _srcProbeBusy;

    /// <summary>
    /// 上一次因「源目录不可达 / 未填写」写进状态栏的提示。
    /// <para>用来做两件事：① 同一句话每 30 秒重复刷一次会盖掉别的状态；② 恢复时只清自己写的那句，
    /// 不把别人刚写上去的消息（比如"已自动发布…"）误删。</para>
    /// </summary>
    private string _pubSrcWarn = "";

    /// <summary>按当前设置（重新）配置定时器；关闭自动更新或没配数据源则不启动。</summary>
    private void SetupUpdateTimer()
    {
        _updTimer?.Stop();
        _updTimer = null;

        var st = Store.LoadSettings();
        _updNextAt = DateTime.Now.AddSeconds(Math.Max(5, st.UpdateStartupDelaySeconds));

        if (!st.AutoUpdateEnabled) return;
        if (FeedSourceFactory.Create(st) == null) return;

        _updTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        // 源目录扫描每跳都做（不受 _updNextAt 限制）：Excel 放进去最多 60 秒就该自动发布，
        // 不必等「检查远端」那个间隔。检查远端仍按 UpdateIntervalMinutes 走。
        _updTimer.Tick += (_, _) => { TickSourcePublish(); TickUpdate(); };
        _updTimer.Start();

        // 启动后立刻探一次源目录：用户一打开程序就该知道"读不到源目录 / 没填目录"这类问题，
        // 而不是等满 30 秒，甚至一直不知道自动发布其实根本没在跑。
        TickSourcePublish();
    }

    /// <summary>
    /// 发布机专用：监视 source 目录，Excel 一变就自动重新打包上传，不必每次手点「发布到数据源」。
    /// <para>必须「连续两次扫描指纹一致」才动手 —— Excel 保存过程中文件大小/时间会变，
    /// 立刻读会读到半截内容，解析出一堆错的课。</para>
    /// <para>★ 扫描必须在后台线程做：共享目录不可达时 <c>Directory.Exists</c> 会阻塞 10–30 秒，
    /// 跑在 UI 线程上就是整个控制面板假死（用户会以为程序崩了）。</para>
    /// </summary>
    private void TickSourcePublish()
    {
        var st = Store.LoadSettings();
        if (!st.PublishEnabled || !st.AutoPublishOnSourceChange) return;

        if (string.IsNullOrWhiteSpace(st.PublishSourcePath))
        {
            // ★ 以前这里直接 return：开了发布机却没填目录，自动发布永远不动、界面上也一个字没有，
            //   用户只能靠"教室机怎么一直不更新"去反推（2026-11-02 现场）。
            WarnPublishSource("已勾选「本机兼任发布机」，但「发布源目录」是空的 —— 自动发布不会发生。"
                + "请到「数据管理 → 自动更新设置」填好共享目录（或点「共享访问向导…」自动填）。");
            return;
        }

        if (_pubBusy || _srcProbeBusy) return;

        var dir = st.PublishSourcePath;
        _ = ProbeSourceAsync(dir);
    }

    /// <summary>后台探一次源目录（存在性 + 指纹），回来后按 UI 线程继续判断。</summary>
    private async Task ProbeSourceAsync(string dir)
    {
        _srcProbeBusy = true;
        bool exists;
        string fp = "";
        try
        {
            (exists, fp) = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(dir)) return (false, "");
                    return (true, SourcePublisher.SourceFingerprint(dir));
                }
                catch
                {
                    // 目录能进但读不了文件（权限掉了）——按"不可用"处理，别当成"没有变化"
                    return (false, "");
                }
            });
        }
        finally
        {
            _srcProbeBusy = false;
        }

        if (!exists)
        {
            WarnPublishSource($"读不到发布源目录：{dir}\n"
                + ExplainDirUnreachable(dir)
                + "自动发布已停摆（Excel 改了也不会传出去）。");
            return;
        }

        ClearPublishSourceWarn();

        if (string.IsNullOrEmpty(fp)) return;

        if (fp == _srcFp) { _srcFpPending = ""; return; }   // 已经处理过的版本

        if (fp != _srcFpPending)
        {
            // 第一次看到：先记下，等下一跳再确认是不是稳定了
            _srcFpPending = fp;
            if (TxtUpdateStatus != null)
                TxtUpdateStatus.Text = $"　⏳ 检测到 source 目录有变化（{DateTime.Now:HH:mm:ss}），等文件写完后自动发布…";
            return;
        }

        _ = AutoPublishAsync(fp);
    }

    /// <summary>
    /// 目录读不到时给出<b>具体原因</b>。
    /// <para>★ <c>Directory.Exists</c> 会把"共享被删了 / 网络不通 / 没权限"全部吞成 false，
    /// 只报"读不到"等于让人去猜。这里再列一次目录，用真实异常码把原因分开 ——
    /// 现场最常见的三种（共享被删、凭据失效、445 被拦）处理办法完全不同。</para>
    /// </summary>
    private static string ExplainDirUnreachable(string dir)
    {
        try
        {
            Directory.GetFileSystemEntries(dir);
            return "（目录列举居然成功了，可能是瞬时故障，下一轮会自动重试。）";
        }
        catch (UnauthorizedAccessException)
        {
            return "原因是【没有访问权限】：凭据失效、账号密码改了，或者共享权限 / NTFS 权限没给到。\n";
        }
        catch (DirectoryNotFoundException)
        {
            return "原因是【路径不存在】：共享名写错了，或者办公电脑上那个共享 / 子目录被删掉了。\n"
                 + "在办公电脑上执行 net share 可以看当前真实共享名。\n";
        }
        catch (Exception ex)
        {
            int code = ex.HResult & 0xFFFF;
            return "原因是" + (code switch
            {
                53 => "【找不到网络路径】：办公电脑关机 / IP 变了 / 445 端口被防火墙拦，或者共享被停掉了。\n",
                67 => "【共享名不存在】：办公电脑上已经没有这个共享，执行 net share 看真实共享名。\n",
                5 => "【拒绝访问】：账号没有该共享的读取权限（共享权限和 NTFS 权限都要给）。\n",
                86 or 1326 => "【账号或密码不对】：到「自动更新设置 → 共享访问向导」第 2 步重新存一次凭据。\n",
                1219 => "【凭据冲突】：这台机器已用别的账号连过同一台电脑，先执行 net use \\\\主机 /delete 断开。\n",
                _ => $"【系统返回错误码 {code}】{ex.Message}\n"
            });
        }
    }

    /// <summary>源目录有问题时把话说清楚 —— 自动发布静默停摆是最难查的一类故障。</summary>
    private void WarnPublishSource(string text)
    {
        if (_pubSrcWarn == text) return;     // 同一句话不重复刷
        _pubSrcWarn = text;
        if (TxtStatus != null) TxtStatus.Text = "⚠ " + text.Replace("\n", "　");
        if (TxtUpdateStatus != null) TxtUpdateStatus.Text = "⚠ " + text;
    }

    /// <summary>源目录恢复正常：只清掉自己写的那句，不误删别的状态消息。</summary>
    private void ClearPublishSourceWarn()
    {
        if (_pubSrcWarn.Length == 0) return;
        if (TxtStatus != null && TxtStatus.Text == "⚠ " + _pubSrcWarn.Replace("\n", "　"))
            TxtStatus.Text = "";
        _pubSrcWarn = "";
    }

    /// <summary>把 source 目录重新打包上传，并让本机同步成这一版。</summary>
    private async Task AutoPublishAsync(string fp)
    {
        var st = Store.LoadSettings();
        var src = FeedSourceFactory.Create(st);
        if (src == null) return;

        _pubBusy = true;
        try
        {
            if (TxtUpdateStatus != null)
                TxtUpdateStatus.Text = $"　⏳ 正在自动发布（{DateTime.Now:HH:mm:ss}）…";

            var r = await Task.Run(() => SourcePublisher.Publish(src, st.PublishSourcePath, Environment.MachineName));

            if (!r.Ok)
            {
                // 失败**不记指纹**，下一跳会再试；但必须让人看见，不能静默
                if (TxtUpdateStatus != null)
                    TxtUpdateStatus.Text =
                        $"　⚠ {DateTime.Now:HH:mm} 自动发布失败：{r.Message}\n　下一轮会重试；也可点「发布到数据源」手动排查。";
                return;
            }

            _srcFp = fp;
            _srcFpPending = "";

            if (r.LocalApplied)
            {
                var sch = Store.LoadSchedule();
                if (sch != null) ApplyImportedSchedule(sch, "自动发布同步", out _);
                AppEvents.RaiseScheduleChanged();
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm} 已自动发布：{r.ClassCount} 个班、调代 {r.OverrideCount} 条");
            if (r.Skipped) sb.Append("（内容没变，未重复上传）");
            if (r.LocalApplied) sb.Append("；本机已同步");
            if (r.Warnings.Count > 0) sb.Append("　⚠ " + r.Warnings[0]);

            TxtStatus.Text = sb.ToString();
            RefreshUpdateStatus();
            if (TxtUpdateStatus != null) TxtUpdateStatus.Text += "\n上次自动发布：" + sb;
        }
        catch (Exception ex)
        {
            if (TxtUpdateStatus != null)
                TxtUpdateStatus.Text = $"　⚠ {DateTime.Now:HH:mm} 自动发布出错：{ex.Message}";
        }
        finally
        {
            _pubBusy = false;
        }
    }

    private void TickUpdate()
    {
        if (_updBusy || DateTime.Now < _updNextAt) return;

        var st = Store.LoadSettings();
        if (!st.AutoUpdateEnabled) { _updTimer?.Stop(); return; }

        var src = FeedSourceFactory.Create(st);
        if (src == null) { _updTimer?.Stop(); return; }

        // 先把下次时间推后，避免检查耗时较长时连续触发
        _updNextAt = DateTime.Now.AddMinutes(Math.Clamp(st.UpdateIntervalMinutes, AppSettings.MinUpdateIntervalMinutes, AppSettings.MaxUpdateIntervalMinutes));

        // 后台检查一律走 silent：不弹窗，只在状态行提示
        _ = CheckAndApplyAsync(src, st, silent: true);
    }

    /// <summary>把数据源与上次检查结果回显到「数据管理」页。</summary>
    private void RefreshUpdateStatus()
    {
        if (TxtUpdateStatus == null) return;

        var st = Store.LoadSettings();
        int mins = Math.Clamp(st.UpdateIntervalMinutes, AppSettings.MinUpdateIntervalMinutes, AppSettings.MaxUpdateIntervalMinutes);

        if (!st.AutoUpdateEnabled)
        {
            TxtUpdateStatus.Text = $"自动更新：未开启。开启后本机每 {mins} 分钟到数据源查一次，" +
                "只有内容变了才整包替换（不看文件时间戳）。";
            return;
        }

        var src = FeedSourceFactory.Create(st);
        if (src == null)
        {
            TxtUpdateStatus.Text = "自动更新：已开启，但数据源地址还不完整，请点「自动更新设置…」补全。";
            return;
        }

        string last = string.IsNullOrWhiteSpace(st.LastUpdateCheckInfo) ? "尚未检查过" : st.LastUpdateCheckInfo;
        string applied = string.IsNullOrWhiteSpace(st.LastAppliedAt)
            ? ""
            : $"（本机最后应用到 {st.LastAppliedAt}）";

        string pub = "";
        if (st.PublishEnabled)
        {
            pub = "\n发布机：" + (st.AutoPublishOnSourceChange
                    ? "监视 source 目录，Excel 变化后约 1 分钟自动发布"
                    : "未开启自动发布，需手动点「发布到数据源」")
                + (string.IsNullOrWhiteSpace(st.PublishSourcePath) ? "（未指定源目录）" : "　" + st.PublishSourcePath);
        }

        TxtUpdateStatus.Text =
            $"自动更新：每 {mins} 分钟检查　数据源：{UpdateService.DescribeSource(st)}\n" +
            $"上次：{last}{applied}" + pub;
    }

    private void BtnUpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        var w = new UpdateSettingsWindow { Owner = this };
        w.ShowDialog();
        RefreshUpdateStatus();
        SetupUpdateTimer();
    }

    private async void BtnUpdateCheck_Click(object sender, RoutedEventArgs e)
        => await ManualCheckUpdateAsync();

    /// <summary>
    /// 手动检查并应用一次更新。
    /// <para>主界面的「立即检查更新」与悬浮课表的「更新课表」共用这一份逻辑，
    /// 免得两边行为不一致（尤其是"没配数据源时弹设置窗口"这一步）。</para>
    /// </summary>
    private async Task ManualCheckUpdateAsync()
    {
        var st = Store.LoadSettings();
        var src = FeedSourceFactory.Create(st);
        if (src == null)
        {
            TxtStatus.Text = "还没配置更新数据源。请先填写共享文件夹或 FTP 地址。";
            // 从悬浮窗触发时主窗口可能在托盘里，弹窗会看不见 —— 先把它叫出来
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = WindowState.Normal;
            }
            new UpdateSettingsWindow { Owner = this }.ShowDialog();
            RefreshUpdateStatus();
            SetupUpdateTimer();
            return;
        }

        if (_updBusy) { TxtStatus.Text = "上一次检查还没结束，请稍等。"; return; }

        BtnUpdateCheck.IsEnabled = false;
        try
        {
            TxtStatus.Text = $"正在检查（{UpdateService.DescribeSource(st)}）…";
            await CheckAndApplyAsync(src, st, silent: false);
        }
        finally
        {
            BtnUpdateCheck.IsEnabled = true;
        }
    }

    /// <summary>
    /// 检查 →（按设置决定是否确认）→ 下载 → 校验 → 应用 → 刷新界面。
    /// <para><paramref name="silent"/>=true 用于定时后台检查：<b>绝不弹窗</b>；
    /// 无人值守的教室屏发现新版本必须直接自动应用，否则会"发现却不更新"（需求四修复点）。
    /// 手动点「立即检查更新」时若不勾「应用前确认」也直接应用，勾了才弹确认框。</para>
    /// </summary>
    private async Task CheckAndApplyAsync(IFeedSource src, AppSettings st, bool silent)
    {
        if (_updBusy) return;
        _updBusy = true;

        // 结果要回传给悬浮课的「更新课表」按钮：它点了之后必须知道成功还是失败，
        // 否则用户看到的只是按钮弹回去，不知道到底更新没更新。
        bool ok = true;
        string report = "";
        try
        {
            var chk = await Task.Run(() => UpdateService.Check(src, st));

            // 把"上次检查结果"落盘，重启后状态行仍能看到
            var st2 = Store.LoadSettings();
            st2.LastUpdateCheckInfo = chk.Message;
            Store.SaveSettings(st2);
            RefreshUpdateStatus();

            if (!silent) TxtStatus.Text = chk.Message;
            if (!chk.Ok || !chk.HasUpdate)
            {
                ok = chk.Ok;
                report = chk.Message;
                return;
            }

            var man = chk.Manifest!;
            bool apply;

            if (!st.UpdateConfirmBeforeApply)
            {
                apply = true;              // 全自动：查到就换（"应用前确认"关着）
            }
            else if (silent)
            {
                // 后台无人值守检查：绝不能弹窗等人工，发现新版本直接自动应用。
                // 这是需求四的关键修复——之前这里只是挂个提示 return，教室屏永远等不到人点。
                apply = true;
            }
            else
            {
                string msg =
                    "数据源上有新版本：\n" +
                    $"　打包时间：{man.BuiltAt:yyyy-MM-dd HH:mm}　来源：{(string.IsNullOrWhiteSpace(man.BuiltBy) ? "未标注" : man.BuiltBy)}\n" +
                    $"　基础课表：{man.ClassCount} 个班\n" +
                    $"　调代课：{man.OverrideCount} 条" +
                    (string.IsNullOrEmpty(man.OverrideRange) ? "" : $"（{man.OverrideRange}）") + "\n" +
                    $"　全校通知：{man.NoticeCount} 条\n\n" +
                    "将覆盖：基础课表、该时间段内的调代课、全校通知、开学周 / 主题。\n" +
                    "会保留：本机手工调课、本班通知、显示班级、窗口布局、密码、作业、缺交、自定义便签。\n\n" +
                    "导入前会自动备份。现在更新吗？";
                apply = MessageBox.Show(msg, "发现课表更新",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
            }
            if (!apply)
            {
                report = "已取消更新（数据源上的新版本没有下载）。";
                return;
            }

            var outp = await Task.Run(() => UpdateService.DownloadAndApply(src));
            if (!outp.Ok)
            {
                ok = false;
                report = outp.Message;
                TxtStatus.Text = outp.Message;
                return;
            }

            var ap = outp.Applied!;
            var sf = outp.Package!.Schedule;
            if (sf != null)
            {
                ApplyImportedSchedule(sf, "自动更新", out _);   // 刷新班级下拉 + 重建教师表
                AppEvents.RaiseScheduleChanged();
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"已更新：{ap.ClassCount} 个班；调代新增 {ap.OverrideAdded} 条、按周重整 {ap.OverrideRemoved} 条")
              .Append(ap.OverrideRemoved > 0
                    ? (ap.AuthoritativeCleared
                        ? "（未再标记 调/代 的格子、以及数据源里没有临时课表的周，都已还原为底表原课程）"
                        : "（其中未再标记 调/代 的格子已还原为底表原课程）")
                    : "");
            if (ap.NoticeAdded > 0) sb.Append($"，全校通知 {ap.NoticeAdded} 条（本班通知保留 {ap.NoticeKept} 条）");
            // ★ 停用 ≠ 删除：必须告诉用户"记录还在、能恢复"，否则会以为老师的工作被抹掉了
            if (ap.ManualSuspended > 0)
                sb.Append($"，另有 {ap.ManualSuspended} 条本机手工调课按主机策略已停用（可在「恢复被主机停用的本机调课」里复活）");
            sb.Append("。");
            if (ap.UserCount > 0) sb.Append($"　用户表 {ap.UserCount} 个账号已同步。");

            // ★ 自动更新是无人值守跑的，不能弹模态框（会在上课时突然挡住屏幕）。
            //   身份被包踢掉时只写状态栏 + 立刻把入口全灰，用户一操作就会发现并看到原因。
            bool kicked = ap.Identity is RebindResult.Removed or RebindResult.Disabled;
            if (kicked)
                sb.Append(ap.Identity == RebindResult.Disabled
                    ? "　⚠ 你的账号已在本版中被停用，已退出登录 —— 请到「用户中心 → 切换账号」用有效账号登录。"
                    : "　⚠ 你的账号已不在本版用户表里，已退出登录 —— 请到「用户中心 → 切换账号」用有效账号登录。");

            foreach (var w in ap.Warnings) sb.Append("　⚠ " + w);
            TxtStatus.Text = sb.ToString();
            report = sb.ToString();

            ApplyPermissions();   // 角色 / 角色权限可能随包变了，按钮可用性要跟着刷新
            RefreshUpdateStatus();
        }
        catch (Exception ex)
        {
            ok = false;
            report = "检查更新出错：" + ex.Message;
            TxtStatus.Text = report;
        }
        finally
        {
            _updBusy = false;
            // 后台静默检查不发通知：没人看着，弹提示只会打扰上课
            if (!silent && report.Length > 0) AppEvents.RaiseQuickCheckUpdateDone(ok, report);
        }
    }

    /// <summary>
    /// 发布：本机是"发布机"时，从 source 目录的 Excel 重新打包上传；
    /// 否则直接把本机当前数据打包上传（适合教师在自己电脑上临时发一次修正）。
    /// </summary>
    private async void BtnPublishNow_Click(object sender, RoutedEventArgs e)
    {
        var st = Store.LoadSettings();
        var src = FeedSourceFactory.Create(st);
        if (src == null)
        {
            TxtStatus.Text = "还没配置数据源，无法发布。请先填写共享文件夹或 FTP 地址。";
            new UpdateSettingsWindow { Owner = this }.ShowDialog();
            RefreshUpdateStatus();
            SetupUpdateTimer();
            return;
        }

        bool fromSource = st.PublishEnabled && !string.IsNullOrWhiteSpace(st.PublishSourcePath);

        if (fromSource)
        {
            // 发布前预检：底表缺年级、临时表没带日期这类问题，必须在"传上去"之前摆到人面前
            var pre = SourcePublisher.Scan(st.PublishSourcePath);
            if (!pre.Ok)
            {
                TxtStatus.Text = pre.Message;
                MessageBox.Show(pre.Message, "发布数据包", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string what =
                "将从 Excel 源目录重新打包：\n" + st.PublishSourcePath + "\n\n" +
                pre.Describe() + "\n\n" +
                "发布到：" + UpdateService.DescribeSource(st) + "\n\n" +
                "文件名含「临时」的，按 yyyy-MM-dd 前缀对齐到「所在周」（写周三也算本周）。\n" +
                "临时课表按整周快照处理：这些周内未标记 调/代 的格子会还原为底表原课程。\n" +
                "发布成功后，本机数据也会同步成这一版。\n\n" +
                "确定发布吗？";

            if (MessageBox.Show(what, "发布数据包", MessageBoxButton.OKCancel,
                    pre.HasRisk ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }
        else
        {
            string what =
                "将把本机当前的课表数据打包上传到：\n" + UpdateService.DescribeSource(st) +
                "\n\n（未开启「发布机模式」，所以不读 source 目录，直接用本机数据）\n\n确定发布吗？";

            if (MessageBox.Show(what, "发布数据包",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }

        if (fromSource && !Directory.Exists(st.PublishSourcePath))
        {
            TxtStatus.Text = "发布源目录不存在：" + st.PublishSourcePath;
            MessageBox.Show("发布源目录不存在：\n" + st.PublishSourcePath +
                "\n\n请在「自动更新设置…」里指定存放 Excel 的目录。", "发布数据包",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnPublishNow.IsEnabled = false;
        TxtStatus.Text = "正在打包并发布…";
        try
        {
            if (fromSource)
            {
                var r = await Task.Run(() => SourcePublisher.Publish(src, st.PublishSourcePath, Environment.MachineName));

                TxtStatus.Text = (r.Skipped ? "数据源上已是最新，未重复上传。" : "") + r.Message;

                // 本机同步成功后，界面上的课表 / 调代 / 教师表都得跟着变
                if (r.LocalApplied)
                {
                    var sch = Store.LoadSchedule();
                    if (sch != null) ApplyImportedSchedule(sch, "发布数据包同步", out _);
                    AppEvents.RaiseScheduleChanged();
                }

                // 摘要与"已应用"标记由 SourcePublisher 自己记（RecordReleased），
                // 这里只负责刷新界面和把问题摆出来。

                // 有坑就明确弹出来，别只塞进状态栏
                if (!r.Ok || r.Warnings.Count > 0 || !string.IsNullOrEmpty(r.LocalError))
                {
                    var box = new System.Text.StringBuilder();
                    box.AppendLine(r.Ok ? "发布完成，但有以下问题需要注意：" : "发布失败：");
                    box.AppendLine();
                    foreach (var w in r.Warnings) box.AppendLine("⚠ " + w + "\n");
                    if (!string.IsNullOrEmpty(r.LocalError)) box.AppendLine("⚠ " + r.LocalError);
                    MessageBox.Show(box.ToString().TrimEnd(), "发布数据包",
                        MessageBoxButton.OK, r.Ok ? MessageBoxImage.Warning : MessageBoxImage.Error);
                }
            }
            else
            {
                var (ok, msg) = await Task.Run(() => UpdateService.PublishCurrent(src, Environment.MachineName));
                TxtStatus.Text = msg;
                if (!ok) MessageBox.Show(msg, "发布数据包", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            BtnPublishNow.IsEnabled = true;
            var st3 = Store.LoadSettings();
            st3.LastUpdateCheckInfo = TxtStatus.Text;
            Store.SaveSettings(st3);
            RefreshUpdateStatus();
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
        "Microsoft YaHei UI", "微软雅黑", "黑体", "宋体", "楷体", "仿宋", "等线", "幼圆",
        "隶书", "华文行楷", "华文新魏", "Arial", "Segoe UI", "Times New Roman", "Consolas"
    };

    /// <summary>
    /// 字号档位：从 12（很小）到 96（特别大）。数字本身就是字号，选完一眼能对上。
    /// <para>原来的 20~64 九档只是"够用"，现在补细：通知条要刚好填满屏幕时，20 与 24 的差别很明显。</para>
    /// </summary>
    private static readonly string[] NotifSizes =
    {
        "12", "14", "16", "18", "20", "22", "24", "26", "28", "30", "32", "36",
        "40", "44", "48", "54", "60", "66", "72", "80", "88", "96"
    };

    private static NotifColorOption ColorOption(string name, string hex) => new()
    {
        Name = name,
        Hex = hex,
        Swatch = ThemeHelper.Brush(hex, System.Windows.Media.Colors.Gray)
    };

    private static List<NotifColorOption> NotifTextColorOptions() => new()
    {
        ColorOption("白色", "#FFFFFF"),
        ColorOption("米白", "#FFF8E7"),
        ColorOption("亮黄", "#FFE9A8"),
        ColorOption("金黄", "#FFD54F"),
        ColorOption("橙色", "#FFD8A8"),
        ColorOption("西瓜红", "#FF8A80"),
        ColorOption("天蓝", "#CFE6FF"),
        ColorOption("湖蓝", "#8ED6FF"),
        ColorOption("薄荷", "#C9F7E8"),
        ColorOption("浅绿", "#D5F5D8"),
        ColorOption("草绿", "#A5E6A0"),
        ColorOption("粉色", "#FFD9E6"),
        ColorOption("淡紫", "#E4D9FF"),
        ColorOption("浅灰", "#D8DEE9"),
        ColorOption("深灰", "#2B2F36"),
        ColorOption("纯黑", "#000000")
    };

    private static List<NotifColorOption> NotifBarColorOptions() => new()
    {
        new() { Name = "跟随主题", Hex = "", Swatch = ThemeHelper.Brush("#8A94A6", System.Windows.Media.Colors.Gray) },
        ColorOption("深蓝", "#1B3A66"),
        ColorOption("藏青", "#12294A"),
        ColorOption("钢蓝", "#2C4A6E"),
        ColorOption("墨绿", "#10403B"),
        ColorOption("深灰", "#2B2F36"),
        ColorOption("酒红", "#5B1F2A"),
        ColorOption("咖啡", "#4B3427"),
        ColorOption("紫黑", "#2E2233"),
        ColorOption("浅色", "#F2F5FA"),
        ColorOption("淡蓝", "#DCEBFF"),
        ColorOption("纯白", "#FFFFFF")
    };

    /// <summary>
    /// 把（可能来自系统取色器的）自定义色补进下拉。
    /// 不补的话：重开界面时找不到这个颜色 → 下拉落回第一项 → 用户一保存就被改掉了。
    /// </summary>
    private static NotifColorOption EnsureNotifColor(List<NotifColorOption> list, string? hex)
    {
        var v = (hex ?? "").Trim();
        var hit = list.FirstOrDefault(x => string.Equals(x.Hex, v, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;

        var opt = ColorOption(string.IsNullOrEmpty(v) ? "跟随主题" : "自定义 " + v.ToUpperInvariant(), v);
        list.Add(opt);
        return opt;
    }

    /// <summary>把设置里的字体 / 字号 / 字色 / 背景色同步到控件与预览条（不触发保存）。</summary>
    private void ReloadNotifStyle()
    {
        var nl = Store.LoadSettings().NotificationLayout ?? new PanelLayout();
        _notifStyleReady = false;
        try
        {
            // 字体：设置里存的可能是旧版本 / 系统里自带的字体（不在预设表里），补一项而不是悄悄换回默认
            string fam = string.IsNullOrWhiteSpace(nl.FontFamily) ? NotifFonts[0] : nl.FontFamily;
            var fonts = NotifFonts.ToList();
            int fi = fonts.IndexOf(fam);
            if (fi < 0) { fonts.Insert(0, fam); fi = 0; }
            CbNotifFont.ItemsSource = fonts;
            CbNotifFont.SelectedIndex = fi;

            // 字号：老数据存的可能是 25 这种"不在档位表里"的值，补进去而不是抹成最近的档
            var sizes = NotifSizes.ToList();
            string want = ((int)Math.Round(nl.FontSize <= 0 ? 28 : nl.FontSize)).ToString();
            if (!sizes.Contains(want))
            {
                sizes.Add(want);
                sizes.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));
            }
            CbNotifSize.ItemsSource = sizes;
            CbNotifSize.SelectedItem = want;

            var texts = NotifTextColorOptions();
            CbNotifText.ItemsSource = texts;
            CbNotifText.SelectedItem = EnsureNotifColor(texts, string.IsNullOrWhiteSpace(nl.TextColor) ? "#FFFFFF" : nl.TextColor);

            var bars = NotifBarColorOptions();
            CbNotifBg.ItemsSource = bars;
            CbNotifBg.SelectedItem = EnsureNotifColor(bars, nl.BarColor ?? "");
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
        return Math.Clamp(v, 12, 120);
    }

    private void NotifStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_notifStyleReady) return;
        SaveNotifStyle();
    }

    /// <summary>把当前下拉里的字体 / 字号 / 字色 / 背景写进设置，并让顶部字幕条立即按新外观重排。</summary>
    private void SaveNotifStyle()
    {
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

    private void BtnNotifTextPick_Click(object sender, RoutedEventArgs e) => PickNotifColor(bar: false);

    private void BtnNotifBgPick_Click(object sender, RoutedEventArgs e) => PickNotifColor(bar: true);

    /// <summary>
    /// 用系统取色器给字色 / 背景色取任意颜色：取到的色补进下拉（下次打开还在），并立即生效。
    /// 取消取色时保持原样 —— 不要把手滑的"取消"当成选了黑色。
    /// </summary>
    private void PickNotifColor(bool bar)
    {
        var cb = bar ? CbNotifBg : CbNotifText;
        if (cb == null) return;

        var cur = cb.SelectedItem as NotifColorOption;
        if (!EditorStyleOptions.TryPickColor(cur?.Hex, out string hex))
        {
            TxtStatus.Text = "没有取到颜色，保持原样。";
            return;
        }

        var list = cb.ItemsSource as List<NotifColorOption>
                   ?? (bar ? NotifBarColorOptions() : NotifTextColorOptions());
        var opt = EnsureNotifColor(list, hex);

        // 换数据源会先触发一次"没有选中项"的 SelectionChanged，用标志挡住，避免把设置写成空值
        _notifStyleReady = false;
        try
        {
            cb.ItemsSource = null;
            cb.ItemsSource = list;
            cb.SelectedItem = opt;
        }
        finally { _notifStyleReady = true; }

        SaveNotifStyle();
        TxtStatus.Text = $"通知条{(bar ? "背景" : "字色")}已设为自定义颜色 {hex.ToUpperInvariant()}。";
    }

    private void BtnAddNotice_Click(object sender, RoutedEventArgs e)
    {
        var title = TxtNewTitle.Text.Trim();
        var content = TxtNewContent.Text.Trim();
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return;
        bool school = ChkNewNoticeSchool?.IsChecked == true;
        // 无发全校通知权限时强制降级为本班，并提示（需求一）
        if (school && !UserSession.CanSendSchoolNotice)
        {
            school = false;
            TxtStatus.Text = "当前身份不能发布全校通知，已改为本班通知（只在本机显示）。";
        }
        else
        {
            TxtStatus.Text = school
                ? "已添加全校通知：会随数据包同步到所有教室机（发布 / 自动更新后生效）。"
                : "已添加本班通知：只在本机显示。";
        }
        Notifications.Insert(0, new Notification
        {
            Title = title,
            Content = content,
            CreatedAt = DateTime.Now,
            Scope = school ? "school" : "local"
        });
        SaveNotes();
        TxtNewTitle.Text = "";
        TxtNewContent.Text = "";
        // 范围勾选保持用户上一次的选择（连续发多条全校通知时不用每次重勾）
        AppEvents.RaiseNotificationChanged();
    }

    /// <summary>切换一条通知的范围（全校 ⇄ 本班）。</summary>
    private void BtnNoticeScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not string id) return;
        var item = Notifications.FirstOrDefault(n => n.Id == id);
        if (item == null) return;

        // 想从「本班」升到「全校」但无权限：拦截（需求一）。降级（全校→本班）始终允许。
        if (item.Scope != "school" && !UserSession.CanSendSchoolNotice)
        {
            TxtStatus.Text = UserSession.DenySendSchoolNotice;
            return;
        }

        item.Scope = item.Scope == "school" ? "local" : "school";
        SaveNotes();
        ReloadNotes();   // 重绑一次，按钮文案 / 徽标才会跟着变
        AppEvents.RaiseNotificationChanged();
        TxtStatus.Text = item.Scope == "school"
            ? "已设为全校通知：下次「发布到数据源」时会同步到所有教室机。"
            : "已设为本班通知：只在本机显示，不再随数据包发出。";
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
        // 按钮虽然会被禁灰，但快捷键 / 自动化仍可能触发，这里再兜一次
        string showClass = CurrentDisplayClass();
        if (!UserSession.CanEditBoardFor(showClass))
        {
            TxtStatus.Text = UserSession.DenyEditBoard(showClass);
            return;
        }
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
            UpdateTrayMenuState();
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
