using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class MainWindow : Window
{
    /// <summary>诊断用：打开时把「提醒设置」页滚到最底部（<c>--shot mainremindend</c>）。</summary>
    internal bool ReminderScrollEnd;

    /// <summary>诊断用：打开时展开「信息管理 → ③ 同步设置」（<c>--shot maininfoopen</c>）。</summary>
    internal bool InfoSettingsExpanded;

    /// <summary>诊断用：打开便签管理页后滚到底（<c>--shot mainnotes</c>），否则「其他便签」那几张卡在折叠区外。</summary>
    internal bool NotesScrollEnd;

    private bool _isFull;
    private bool _locked;
    private bool _exiting;

    /// <summary>P1 新导航：诊断 / 托盘是否显式指定过起始页。没指定才默认落到「今日」。</summary>
    private bool _initialTabSet;

    private ScheduleWindow? _schWin;
    private NotificationWindow? _notifWin;
    private HomeworkWindow? _hwWin;
    private MissingWindow? _msWin;
    private CountdownWindow? _cdWin;
    private DutyWindow? _dutyWin;
    private NoticeWindow? _noticeWin;
    private bool _schOn, _notOn, _hwOn, _msOn, _cdOn, _dutyOn, _noticeOn;
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

    /// <summary>打开时默认选中的标签页下标（0=周课程表,1=数据管理,2=显示设置,3=提醒设置,4=通知公告,
    /// 5=便签管理,6=信息管理,7=用户中心,8=关于,9=今日首页）。
    /// 与 LayoutWindow 一样必须走属性 setter：对象初始化器的赋值发生在构造函数之后。
    /// 0~8 的顺序一个都不能动：悬浮窗快捷入口、托盘命令、截图目标都按这些下标跳转。</summary>
    public int InitialTabIndex
    {
        set
        {
            _initialTabSet = true;
            if (MainTabs != null && MainTabs.Items.Count > 0)
                MainTabs.SelectedIndex = Math.Clamp(value, 0, MainTabs.Items.Count - 1);
            ApplyNavGroup(GroupOfTab(value), false);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
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
        AppEvents.QuickOpenSettings += OnQuickOpenSettings;
        AppEvents.QuickOpenWeek += OnQuickOpenWeek;
        AppEvents.QuickOpenPublish += OnQuickOpenPublish;
        AppEvents.QuickOpenInfoLedger += OnQuickOpenInfoLedger;
        AppEvents.QuickCheckUpdate += OnQuickCheckUpdate;

        // 球上右键「隐藏」时，这里的勾选要跟着取消（球是常驻窗口，两边状态必须一致）
        AppEvents.HandwritingBallChanged += OnHandwritingBallChanged;

        // 页面级滚动已经打开触摸拖动（XAML 里 PanningMode="VerticalOnly"），
        // 但 ScrollViewer 在触摸按下的一瞬间就会捕获触摸并吃掉事件，落到子控件上的触摸
        // 也就不再下发 —— 语速 / 音量两个滑块会变成「一拖就滚页面、滑块纹丝不动」，
        // 嵌套的说明框同理。窗口的 PreviewTouchDown（隧道最外层，比 ScrollViewer 的类处理更早）
        // 里先看落点，命中滑块或「自己能滚的嵌套框」时把外层 ScrollViewer 的接管临时关掉。
        // 处理函数挂在 XAML 的 PreviewTouchDown 上，不在代码里 new 委托（省掉类型名依赖）。
    
        GlassChrome.Prepare(this);}

    /// <summary>
    /// 触摸落点保护：页面滚动开起来之后，滑块与嵌套滚动框仍然要能被手指直接操作。
    /// <para>做法是「临时摘掉外层 ScrollViewer 的接管」而不是标记事件已处理 —— 标记已处理会连
    /// 滑块自己也收不到触摸。摘掉只影响这一次按下（ScrollViewer 只在 TouchDown 时读 PanningMode），
    /// 所以按着不放拖动也不会中途被抢，延时恢复即可。</para>
    /// </summary>
    private void OnPreviewTouchDownKeepInnerTouch(object sender, TouchEventArgs e)
    {
        try
        {
            var dep = e.OriginalSource as DependencyObject;
            var host = FindTouchHostScrollViewer(dep);
            if (host == null || host.PanningMode == PanningMode.None) return;

            var old = host.PanningMode;
            host.PanningMode = PanningMode.None;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                host.PanningMode = old;
            };
            timer.Start();
        }
        catch
        {
            // 触摸自适应失败最多是「滑块拖不动」，绝不打断正常使用
        }
    }

    /// <summary>落点是否属于「必须自己处理触摸」的控件；是的话返回应当让位的那层 ScrollViewer。</summary>
    private static ScrollViewer? FindTouchHostScrollViewer(DependencyObject? dep)
    {
        // ① 滑块：滑块自己会滚（例外：滑块在哪个 ScrollViewer 里就摘哪个）
        var slider = FindAncestor<Slider>(dep);
        if (slider != null) return FindAncestor<ScrollViewer>(dep);

        // ② 嵌套滚动框（例如「关于」页里 150 高的发行说明）：它自己能滚就禁掉它的外层
        var inner = FindAncestor<ScrollViewer>(dep);
        if (inner == null || inner.ScrollableHeight <= 0.5) return null;

        if (inner.PanningMode == PanningMode.None) inner.PanningMode = PanningMode.VerticalOnly;
        return FindAncestor<ScrollViewer>(VisualTreeHelper.GetParent(inner));
    }

    private static T? FindAncestor<T>(DependencyObject? dep) where T : DependencyObject
    {
        while (dep != null)
        {
            if (dep is T t) return t;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    /// <summary>
    /// 把控制面板的尺寸收进当前屏幕的工作区（只缩不放）。
    /// <para><b>为什么需要：</b>XAML 里 <c>Height="900"</c> 是按 100% 缩放（1080p 工作区约 1040 DIP）
    /// 调出来的。屏幕改成 125% 缩放后，系统只给程序 1536×830 左右的逻辑画布，900 大于 830，
    /// 而 <c>CenterScreen</c> 会忠实居中，于是顶部工具栏和底部按钮各被切掉一截 —— 而且窗口本身
    /// 没有滚动条，用户既看不到也拖不动，只能改系统缩放。</para>
    /// <para>预留 24 DIP 余量，避免窗口边框贴住任务栏；大屏上不触发任何改动。</para>
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var wa = SystemParameters.WorkArea;          // 已是 DIP，跟随当前 DPI
            double maxH = Math.Max(MinHeight, wa.Height - 24);
            double maxW = Math.Max(MinWidth, wa.Width - 24);
            if (Height > maxH) Height = maxH;
            if (Width > maxW) Width = maxW;
        }
        catch
        {
            // 取不到工作区（极老的系统 / 远程会话）就保持 XAML 原尺寸，至少不会崩
        }
    }

    /// <summary>球被隐藏后，把控制面板里的勾选和托盘菜单的勾选一起同步。</summary>
    private void OnHandwritingBallChanged()
    {
        try
        {
            bool on = Store.LoadSettings().ShowHandwritingBall;
            if (ChkHandBall != null) ChkHandBall.IsChecked = on;
            UpdateTrayMenuState();
        }
        catch { }
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
            // 用 LiveCount：只有墓碑（全删过）也算空，否则会显示一张什么都没有的便签
            bool empty = Store.LoadDuty().LiveCount == 0;
            ShowDutyWin();
            if (empty) OpenDutyEditor();
            TxtStatus.Text = "值日生名单已显示（双击便签可编辑）。";
        }
        catch (Exception ex) { TxtStatus.Text = "打开值日生名单失败：" + ex.Message; }
    }

    /// <summary>
    /// 悬浮课表「设置」快捷入口：打开控制面板并切到「显示设置」页。
    /// <para>为什么是这一页：字号、颜色、悬浮窗、手写球、触摸键盘这些"看得见"的开关都在这，
    /// 老师从悬浮窗点进来通常就是奔着"字太小 / 颜色不好看 / 某个窗口不见了"。</para>
    /// </summary>
    private void OnQuickOpenSettings()
    {
        ShowMainWindow();
        if (MainTabs != null) MainTabs.SelectedIndex = 2;   // 0 周课程表 / 1 数据管理 / 2 显示设置
        TxtStatus.Text = "已打开「显示设置」：字号、颜色、悬浮窗、手写悬浮球都在这一页。";
    }

    /// <summary>悬浮课表「周课」快捷入口：打开控制面板并切到「周课程表」页。</summary>
    private void OnQuickOpenWeek()
    {
        ShowMainWindow();
        if (MainTabs != null) MainTabs.SelectedIndex = 0;   // 0 周课程表
        TxtStatus.Text = "已打开「周课程表」：拖动课程卡可交换两节课，双击卡片可编辑。";
    }

    /// <summary>
    /// 悬浮课表「信息」快捷入口：直接打开消息台账 / 回执窗口，不绕控制面板。
    /// <para>非模态（Show 而不是 ShowDialog）：它是常驻的查看窗，不该把其它窗口一起禁用。</para>
    /// </summary>
    private void OnQuickOpenInfoLedger()
    {
        try
        {
            new BoardSyncWindow().Show();
            TxtStatus.Text = UserSession.IsStudent
                ? "已打开消息台账：这里看「收件箱」，有教师机发来的变更单时逐条选「增加 / 替换 / 忽略」。"
                : "已打开消息台账：这里看「发送箱」，逐条核对教室机回执；改错了还能撤回还原。";
        }
        catch (Exception ex) { TxtStatus.Text = "打不开消息台账：" + ex.Message; }
    }

    /// <summary>
    /// 悬浮课表「发布」快捷入口：直接弹「新建班级内容变更单」。
    /// <para>目标班级仍以「信息管理」页勾选的为准；一个都没勾过时退到当前显示班级 /
    /// 唯一的任教班级 —— 绝不擅自把信息发到名单外的班级。</para>
    /// </summary>
    private void OnQuickOpenPublish()
    {
        if (!UserSession.CanPublishInfo) { TxtStatus.Text = UserSession.DenyPublishInfo; return; }

        var s = Store.LoadSettings();
        if (!s.BoardSyncEnabled)
        {
            TxtStatus.Text = "同步还没启用：请到「信息管理 → ③ 同步设置」勾选「启用同步」并保存后再发布。";
            return;
        }

        var mine = TeachingClasses();
        var picked = mine.Where(c => _pickedInfoClasses.Contains(c)).ToList();
        if (picked.Count == 0)
        {
            string own = (s.SelectedClass ?? "").Trim();
            if (own.Length > 0 && mine.Contains(own, StringComparer.Ordinal)) picked.Add(own);
            else if (mine.Count == 1) picked.Add(mine[0]);
        }
        if (picked.Count == 0)
        {
            TxtStatus.Text = "还没有任教班级：请到「信息管理」页从下拉里增加班级并勾选后再发布。";
            return;
        }
        PublishFor(picked, null);
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
            if (ChkTouchKb != null) ChkTouchKb.IsChecked = settings.TouchKeyboardAutoShow;
            if (ChkHandBall != null) ChkHandBall.IsChecked = settings.ShowHandwritingBall;
            // 黑板启动选项：勾选状态如实反映设置，两个勾选有从属关系（不自动打开就谈不上先收起）
            SyncBoardStartOptions(settings);
            if (ChkBothWeeks != null) ChkBothWeeks.IsChecked = settings.ShowAlternateWeekLessons;
            if (ChkTwoFinger != null) ChkTwoFinger.IsChecked = settings.TwoFingerScroll;
            TouchInputBinder.RefreshSetting();
            ReloadNotes();
            ReloadHomework();
            ReloadMissing();
            ReloadNotifStyle();
            PopulateThemeCombo(settings.Theme);
            PopulateClockCombo(settings.ScheduleClockMode);
            InitGlassControls();
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

        // 诊断用：截图时展开「信息管理 → ③ 同步设置」（否则新增开关在折叠区里看不到）
        if (InfoSettingsExpanded)
            Dispatcher.BeginInvoke(new Action(() => { try { ExpInfoSettings.IsExpanded = true; } catch { } }));

        // 诊断用：便签管理页滚到底，才拍得到「其他便签」那几张卡（含事项列表）
        if (NotesScrollEnd)
            Dispatcher.BeginInvoke(new Action(() => { try { NotesScroll?.ScrollToEnd(); } catch { } }));

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
            // 事项便签默认显示：它才是通知的完整清单，跑马灯只滚最新两条
            // ★ 事项便签（通知公告）按「上次实际是显示还是收起」恢复：
            //   以前只看 AutoShowNotice（默认 true，而且没人写回），所以每次开机都自己弹出来，
            //   用户关掉也没用。现在读 Placement.Visible —— Show/Hide 时都会记这个值；
            //   只有「从没记录过」的老配置才退回旧的默认开关。
            bool noticeEverSet = settings.NoteWindows?.ContainsKey(NoticeWindow.PlacementKey) == true;
            bool noticeVisible = noticeEverSet
                ? settings.PlacementOf(NoticeWindow.PlacementKey).Visible
                : settings.AutoShowNotice;
            if (noticeVisible) ShowNoticeWin();
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
            RefreshSaveDirsUi();     // 「保存位置」两项路径回显（r79）
        }
        catch (Exception ex) { TxtStatus.Text += " [自动更新初始化失败：" + ex.Message + "]"; }

        UpdateToggleLabels();
        ApplyPermissions();
        InitInfoTab();   // 班级下拉要在 ClassNames 填充完后才有内容

        try
        {
            if (settings.LaunchFullscreen || settings.LaunchLocked) SetFullscreen(true);
            if (settings.LaunchLocked) ToggleLock();
        }
        catch { }

        // 升级后一次性说明：等窗口真正显示出来再弹，否则 Owner 还没就位
        Loaded += (_, _) => ShowUpgradeNoticeOnce();

        // 空闲预热：把"第一次调课为什么要等几秒"摊到启动后的空闲时间上。
        // 首次拖动要现读全校课表 / 覆盖表 + 现 JIT 建表与刷新路径，冷启动那一下能明显卡住；
        // 这些 read 有签名缓存，预热一次之后拖动就只走内存。
        // P1 新导航：默认落到「今日」；诊断 / 托盘显式指定 InitialTabIndex 时不覆盖
        InitNavShell();

        Dispatcher.BeginInvoke(new Action(WarmUpForFirstDrag),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // 上次留着的涂鸦板：等主窗立起来再恢复，否则它们会先于主窗抢到焦点。
        // 诊断/截图模式（SuppressWrites）不恢复，免得一堆板盖住被测窗口。
        try
        {
            // 「显示设置 → 黑板」里的两个启动选项：不勾「启动时自动打开」就保持关闭；
            // 勾了「先收成板名条」则这次启动只露出板头那一条。
            var boardStart = Store.LoadSettings();
            if (!Store.SuppressWrites && boardStart.DoodleRestoreOnStart)
                DoodleBoardService.RestoreAll(boardStart.DoodleStartCollapsed);
        }
        catch (Exception ex) { TxtStatus.Text += " [涂鸦板恢复失败：" + ex.Message + "]"; }
    }

    /// <summary>空闲时把课表 / 覆盖表 / 今日看板读一遍，消除首次调课时的冷启动卡顿。</summary>
    private void WarmUpForFirstDrag()
    {
        try
        {
            var s = Store.LoadSettings();
            _ = Store.LoadOverrides();
            var sf = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase)
                ? (Store.LoadTeacherSchedule() ?? Store.LoadSchedule())
                : Store.LoadSchedule();
            if (sf == null) return;
            string name = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase)
                ? s.SelectedTeacher : s.SelectedClass;
            if (string.IsNullOrEmpty(name)) return;
            // 走一遍建表（含覆盖表展开）：这条链正是调课后刷新要跑的
            _ = BoardBuilder.Build(name, s.SemesterStartMonday, sf, BoardBuilder.ResolveDisplayDate(s),
                                   null, showAlternateWeek: s.ShowAlternateWeekLessons);
        }
        catch { /* 预热失败无所谓，真用到时会再算 */ }
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
        SetNotificationVisible(true);
        UpdateToggleLabels();
    }

    private void HideNotificationWin()
    {
        _notifWin?.Hide();
        _notOn = false;
        SetNotificationVisible(false);
        UpdateToggleLabels();
    }

    /// <summary>
    /// 记住通知条「上次是显示还是收起」。
    /// <para>★ 只写 <c>AutoShowNotification</c> 还不够：老配置里的
    /// <c>NotificationShowDefaultsRev</c> 若还是 0，下次启动 <c>UpgradeNotificationShowDefaults</c>
    /// 会把它强行改回 true，用户关掉的条子又自己回来了。所以顺手把代次压成 1（= 升级已跑过），
    /// 之后这个开关就完全由用户决定。</para>
    /// </summary>
    private static void SetNotificationVisible(bool visible)
    {
        try
        {
            var s = Store.LoadSettings();
            bool changed = s.AutoShowNotification != visible || s.NotificationShowDefaultsRev < 1;
            if (s.NotificationShowDefaultsRev < 1) s.NotificationShowDefaultsRev = 1;
            s.AutoShowNotification = visible;
            if (changed) Store.SaveSettings(s);
        }
        catch { }
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

    // ---------- 事项列表便签（通知 / 公告的便签形态） ----------
    private void ShowNoticeWin()
    {
        if (_noticeWin == null)
        {
            _noticeWin = new NoticeWindow();
            _noticeWin.Closed += (_, _) => { _noticeWin = null; _noticeOn = false; UpdateToggleLabels(); };
            // ✕ 是 Hide 不是 Close，不会触发 Closed：不同步这里，按钮文案会一直停在「显示事项便签」
            _noticeWin.IsVisibleChanged += (_, _) =>
            {
                bool vis = _noticeWin?.IsVisible == true;
                if (_noticeOn != vis) { _noticeOn = vis; UpdateToggleLabels(); }
            };
        }
        _noticeWin.Show();
        FloatingHelper.ClampToScreen(_noticeWin);
        FloatingHelper.BringToFront(_noticeWin);
        SetNoteVisible(NoticeWindow.PlacementKey, true);
        _noticeOn = true;
        UpdateToggleLabels();
    }

    private void HideNoticeWin()
    {
        _noticeWin?.Hide();
        SetNoteVisible(NoticeWindow.PlacementKey, false);
        _noticeOn = false;
        UpdateToggleLabels();
    }

    private void BtnToggleNotice_Click(object sender, RoutedEventArgs e)
    {
        if (_noticeOn) HideNoticeWin();
        else ShowNoticeWin();
    }

    private void BtnEditNotice_Click(object sender, RoutedEventArgs e) => OpenNotificationEditor();

    /// <summary>
    /// 通知公告便签的「风格」：配色预设 + 字号，只写本机 settings.json（不进数据包）。
    /// 保存后立即让已打开的便签重绘，不用重启也不用等 30 秒轮询。
    /// </summary>
    private void BtnStyleNotice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new NoticeStyleWindow { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.ResultTheme)) return;

            var s = Store.LoadSettings();
            s.NoticeNoteTheme = dlg.ResultTheme!;
            s.NoticeNoteScale = dlg.ResultScale;
            if (Store.EnablePersistence) Store.SaveSettings(s);

            _noticeWin?.Reload();
            string name = ScheduleCore.Models.HomeworkThemes.Find(s.NoticeNoteTheme).Name;
            TxtStatus.Text = $"已更新通知公告便签风格：{name} · {ScaleName(s.NoticeNoteScale)}（仅本机）。";
        }
        catch (Exception ex) { TxtStatus.Text = "设置便签风格失败：" + ex.Message; }
    }

    static string ScaleName(double scale)
        => scale < 0.999 ? "小字号" : scale > 1.001 ? "大字号" : "标准字号";

    /// <summary>打开通知编辑窗口（便签管理页与事项便签共用同一入口）。</summary>
    private void OpenNotificationEditor()
    {
        try
        {
            var dlg = new NotificationEditWindow { Owner = this };
            dlg.ShowDialog();
            AppEvents.RaiseNotificationChanged();
        }
        catch { }
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
        if (BtnToggleBoard != null)
            BtnToggleBoard.Content = DoodleBoardService.AnyOpen ? "黑板：开" : "黑板：关";
        // 作业 / 缺交便签的开关只在「作业公示」标签页里（显示设置页已不再重复放一套）
        if (BtnToggleHw2 != null) BtnToggleHw2.Content = _hwOn ? "隐藏作业便签" : "显示作业便签";
        if (BtnToggleMs2 != null) BtnToggleMs2.Content = _msOn ? "隐藏缺交便签" : "显示缺交便签";
        if (BtnToggleCd != null) BtnToggleCd.Content = _cdOn ? "隐藏倒计时便签" : "显示倒计时便签";
        if (BtnToggleDuty != null) BtnToggleDuty.Content = _dutyOn ? "隐藏值日便签" : "显示值日便签";
        if (BtnToggleNotice != null) BtnToggleNotice.Content = _noticeOn ? "隐藏通知公告便签" : "显示通知公告便签";
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

        // 「信息管理」页的发布按钮（含三类快发）也按同一套权限刷新：
        // 电教管理员默认没有发布权限，切换账号后必须立刻变灰，不能等重启。
        ApplyInfoPermissions();
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

        // P4：上课 / 下课 / 放学三组完全同构 —— 一套读写 + 三次调用，只编辑差异
        LoadReminderProfile(r.ClassStart, ChkCsOn, TxtCsLead, TxtCsTitle, TxtCsDetail, TxtCsVoice, TxtCsDanmaku);
        LoadReminderProfile(r.ClassEnd, ChkCeOn, TxtCeLead, TxtCeTitle, TxtCeDetail, TxtCeVoice, TxtCeDanmaku);
        LoadReminderProfile(r.AfterSchool, ChkAsOn, TxtAsLead, TxtAsTitle, TxtAsDetail, TxtAsVoice, TxtAsDanmaku);

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
        ReloadPopupStyleUi(r);

        _remindReady = true;
    }

    /// <summary>P4：三组提醒字段同构，收敛成一套读写 + 三次调用。
    /// 控件名、事件、就绪标志、解析规则、默认值全部不变，纯等价重构（不动持久化模型，不需要新 Rev）。</summary>
    private static void LoadReminderProfile(ScheduleCore.Models.ReminderProfile p,
        System.Windows.Controls.CheckBox on, System.Windows.Controls.TextBox lead,
        System.Windows.Controls.TextBox title, System.Windows.Controls.TextBox detail,
        System.Windows.Controls.TextBox voice, System.Windows.Controls.TextBox danmaku)
    {
        on.IsChecked = p.Enabled;
        lead.Text = Math.Max(0, p.LeadSeconds).ToString();
        title.Text = p.Title;
        detail.Text = p.Detail;
        voice.Text = p.VoiceText;
        danmaku.Text = p.Danmaku;
    }

    private static void SaveReminderProfile(ScheduleCore.Models.ReminderProfile p,
        System.Windows.Controls.CheckBox on, System.Windows.Controls.TextBox lead,
        System.Windows.Controls.TextBox title, System.Windows.Controls.TextBox detail,
        System.Windows.Controls.TextBox voice, System.Windows.Controls.TextBox danmaku)
    {
        p.Enabled = on.IsChecked == true;
        p.LeadSeconds = ParseNonNeg(lead.Text);
        p.Title = title.Text ?? "";
        p.Detail = detail.Text ?? "";
        p.VoiceText = voice.Text ?? "";
        p.Danmaku = danmaku.Text ?? "";
    }

    // ---------- 提醒提示外观（小窗 + 全屏共用） ----------

    /// <summary>外观控件是否已就绪（避免加载期 SelectionChanged / ValueChanged 触发保存）。</summary>
    private bool _popupStyleReady;

    /// <summary>提醒提示可选的文字 / 底色。深浅两类都放进来：白底要深字，深底要浅字。</summary>
    private static List<NotifColorOption> PopupColorOptions() => new()
    {
        ColorOption("白色", "#FFFFFF"),
        ColorOption("米白", "#FFF8E7"),
        ColorOption("纯黑", "#111111"),
        ColorOption("深灰", "#1B2836"),
        ColorOption("中灰", "#475467"),
        ColorOption("深蓝", "#123A6B"),
        ColorOption("海军蓝", "#0B2545"),
        ColorOption("深绿", "#14532D"),
        ColorOption("墨绿", "#10403B"),
        ColorOption("酒红", "#5B1F2A"),
        ColorOption("亮黄", "#FFD54F"),
        ColorOption("金黄", "#E6A700"),
        ColorOption("正红", "#D32F2F"),
        ColorOption("草绿", "#2E7D32"),
        ColorOption("天蓝", "#1565C0"),
        ColorOption("淡蓝", "#DCEBFF"),
        ColorOption("浅灰", "#D8DEE9"),
        ColorOption("护眼绿", "#E7F4E9")
    };

    /// <summary>把设置里的风格 / 不透明度 / 字号 / 自定义颜色同步到控件（不触发保存）。</summary>
    private void ReloadPopupStyleUi(ScheduleCore.Models.ReminderSettings r)
    {
        _popupStyleReady = false;
        try
        {
            CbPopupStyle.ItemsSource = ReminderVisualStyle.Presets.Select(p => p.Name).ToList();
            string key = (r.PopupStyle ?? "classic").Trim().ToLowerInvariant();
            int idx = Array.FindIndex(ReminderVisualStyle.Presets, p => p.Key == key);
            CbPopupStyle.SelectedIndex = idx < 0 ? 0 : idx;

            SlPopupOpacity.Value = Math.Round(Math.Clamp(r.PopupOpacity <= 0 ? 1 : r.PopupOpacity, 0.55, 1.0) * 100);
            SlPopupFont.Value = Math.Round(Math.Clamp(r.PopupFontScale <= 0 ? 1 : r.PopupFontScale, 0.8, 1.8) * 100);

            var texts = PopupColorOptions();
            CbPopupText.ItemsSource = texts;
            CbPopupText.SelectedItem = EnsureNotifColor(texts, string.IsNullOrWhiteSpace(r.PopupTextColor) ? "#1B2836" : r.PopupTextColor);

            var cards = PopupColorOptions();
            CbPopupCard.ItemsSource = cards;
            CbPopupCard.SelectedItem = EnsureNotifColor(cards, string.IsNullOrWhiteSpace(r.PopupCardColor) ? "#FFFFFF" : r.PopupCardColor);
        }
        finally
        {
            _popupStyleReady = true;
            UpdatePopupStyleLabels();
        }
    }

    private string SelectedPopupStyleKey()
    {
        int i = CbPopupStyle?.SelectedIndex ?? 0;
        return i >= 0 && i < ReminderVisualStyle.Presets.Length ? ReminderVisualStyle.Presets[i].Key : "classic";
    }

    private void UpdatePopupStyleLabels()
    {
        if (TxtPopupOpacity != null) TxtPopupOpacity.Text = $"{SlPopupOpacity.Value:0}%";
        if (TxtPopupFont != null) TxtPopupFont.Text = $"{SlPopupFont.Value:0}%";

        // 自定义颜色只在「自定义」风格下有意义：变灰但不隐藏，用户能看到还有这两个选项
        bool custom = SelectedPopupStyleKey() == "custom";
        if (WrapPopupCustom != null)
        {
            WrapPopupCustom.IsEnabled = custom;
            WrapPopupCustom.Opacity = custom ? 1.0 : 0.45;
        }
    }

    private void PopupStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_popupStyleReady) return;
        UpdatePopupStyleLabels();
        SaveReminderUi();
    }

    private void PopupStyle_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_popupStyleReady) return;
        UpdatePopupStyleLabels();
        SaveReminderUi();
    }

    /// <summary>预览：先把当前界面值落盘，再用真实提醒窗口弹一次（看到的就是实际效果）。</summary>
    private void BtnPopupPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveReminderUi();
            var r = Store.LoadSettings().Reminder ?? new ScheduleCore.Models.ReminderSettings();
            ScheduleChangeWatcher.Preview(r);
            TxtStatus.Text = r.ChangeFullScreen
                ? "已按当前外观弹一次全屏预览（点任意处或几秒后自动关闭）。"
                : "已按当前外观弹一次小窗预览（几秒后自动消失）。";
        }
        catch (Exception ex) { TxtStatus.Text = "预览失败：" + ex.Message; }
    }

    private void BtnPopupTextPick_Click(object sender, RoutedEventArgs e) => PickPopupColor(text: true);

    private void BtnPopupCardPick_Click(object sender, RoutedEventArgs e) => PickPopupColor(text: false);

    /// <summary>系统取色器：取到的色补进下拉（下次打开还在），取消则保持原样。</summary>
    private void PickPopupColor(bool text)
    {
        var cb = text ? CbPopupText : CbPopupCard;
        if (cb == null) return;

        var cur = cb.SelectedItem as NotifColorOption;
        if (!EditorStyleOptions.TryPickColor(cur?.Hex, out string hex))
        {
            TxtStatus.Text = "没有取到颜色，保持原样。";
            return;
        }

        var list = cb.ItemsSource as List<NotifColorOption> ?? PopupColorOptions();
        var opt = EnsureNotifColor(list, hex);

        _popupStyleReady = false;
        try
        {
            cb.ItemsSource = null;
            cb.ItemsSource = list;
            cb.SelectedItem = opt;
        }
        finally { _popupStyleReady = true; }

        SaveReminderUi();
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

            SaveReminderProfile(r.ClassStart, ChkCsOn, TxtCsLead, TxtCsTitle, TxtCsDetail, TxtCsVoice, TxtCsDanmaku);
            SaveReminderProfile(r.ClassEnd, ChkCeOn, TxtCeLead, TxtCeTitle, TxtCeDetail, TxtCeVoice, TxtCeDanmaku);
            SaveReminderProfile(r.AfterSchool, ChkAsOn, TxtAsLead, TxtAsTitle, TxtAsDetail, TxtAsVoice, TxtAsDanmaku);

            r.DisplaySeconds = ParseSeconds(TxtRemindSeconds.Text);
            r.PlaySound = ChkRemindSound.IsChecked == true;

            r.ChangeEnabled = ChkChangeOn.IsChecked == true;
            r.ChangeFullScreen = ChkChangeFull.IsChecked == true;
            r.ChangeVoice = ChkChangeVoice.IsChecked == true;
            r.ChangeSeconds = ParseRange(TxtChangeSeconds.Text, 1, 60, 15);
            r.ChangeStatusMinutes = ParseRange(TxtChangeStatusMin.Text, 1, 120, 10);
            r.ChangeDelayMinutes = ParseRange(TxtChangeDelay.Text, 0, 60, 2);
            UpdateChangeDelayHint(r.ChangeDelayMinutes);

            // 提示外观（小窗 + 全屏共用）
            r.PopupStyle = SelectedPopupStyleKey();
            r.PopupOpacity = Math.Round(Math.Clamp(SlPopupOpacity.Value / 100.0, 0.55, 1.0), 2);
            r.PopupFontScale = Math.Round(Math.Clamp(SlPopupFont.Value / 100.0, 0.8, 1.8), 2);
            r.PopupCardColor = (CbPopupCard.SelectedItem as NotifColorOption)?.Hex ?? "";
            r.PopupTextColor = (CbPopupText.SelectedItem as NotifColorOption)?.Hex ?? "";

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

    /// <summary>
    /// 「显示设置 → 黑板」的开关：一键把全部黑板打开 / 收起。
    /// <para>★ 收起只是关窗口，配置与笔迹都留着 —— 这是黑板一贯的语义（同窗口上的 ✕），
    /// 真正的「删掉这块板」在「黑板管理…」里。</para>
    /// </summary>
    private void BtnToggleBoard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DoodleBoardService.AnyOpen)
            {
                DoodleBoardService.HideAllOpen();
                TxtStatus.Text = "已收起黑板（内容与笔迹都留着，下次点「黑板：开」就回来）。";
            }
            else
            {
                DoodleBoardService.ShowAll();
                TxtStatus.Text = "已打开黑板，共 " + DoodleBoardService.Open.Count + " 块。";
            }
            UpdateToggleLabels();
            RefreshDoodleTrayMenu();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换黑板失败：" + ex.Message;
        }
    }

    private void BtnBoardManager_Click(object sender, RoutedEventArgs e)
    {
        OpenDoodleManager();
        UpdateToggleLabels();
    }

    /// <summary>
    /// 「显示设置 → 黑板」这几个控件的回显（Window_Loaded 与诊断共用一条）。
    /// <para>★ 两个勾选有从属关系：不「自动打开」就谈不上「先收成板名条」，后者置灰。</para>
    /// </summary>
    private void SyncBoardStartOptions(AppSettings settings)
    {
        if (ChkBoardRestore != null) ChkBoardRestore.IsChecked = settings.DoodleRestoreOnStart;
        if (ChkBoardStartMin != null)
        {
            ChkBoardStartMin.IsChecked = settings.DoodleStartCollapsed;
            ChkBoardStartMin.IsEnabled = settings.DoodleRestoreOnStart;
        }
        if (BtnToggleBoard != null)
            BtnToggleBoard.Content = DoodleBoardService.AnyOpen ? "黑板：开" : "黑板：关";
    }

    /// <summary>诊断用：窗口没 Show 时 Loaded 不会触发，这里手动回显一次（与真机同一份代码）。</summary>
    internal void DiagSyncBoardStartOptions() => SyncBoardStartOptions(Store.LoadSettings());

    /// <summary>启动时是否自动打开黑板（写进 DoodleRestoreOnStart）。</summary>
    private void ChkBoardRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.DoodleRestoreOnStart = ChkBoardRestore.IsChecked == true;
            Store.SaveSettings(s);

            if (ChkBoardStartMin != null) ChkBoardStartMin.IsEnabled = s.DoodleRestoreOnStart;
            TxtStatus.Text = s.DoodleRestoreOnStart
                ? "下次启动会自动打开上次没关的黑板。"
                : "下次启动黑板保持关闭，要用时在这里点「黑板：开」。";
        }
        catch { /* 存不上就下次再设 */ }
    }

    /// <summary>启动时是否先收成板名条（写进 DoodleStartCollapsed）。</summary>
    private void ChkBoardStartMin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.DoodleStartCollapsed = ChkBoardStartMin.IsChecked == true;
            Store.SaveSettings(s);
            TxtStatus.Text = s.DoodleStartCollapsed
                ? "下次启动黑板只露出板名条，点「展开」才铺满。"
                : "下次启动黑板按上次的大小铺开。";
        }
        catch { }
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
            string? writeErr = Store.LastWriteError;
            if (writeErr != null)
            {
                TxtStatus.Text = "⚠ 缺交名单没有保存：" + writeErr + "（改动只在内存里，请检查账号与磁盘权限后重试）";
                return;
            }

            // ★ 同作业公示：壁纸由 MissingChanged 的 1.2s 防抖订阅负责，不在确认链里同步渲染
            ReloadMissing();
            AppEvents.RaiseMissingChanged();
            AppEvents.RaiseBackdropChanged();
            TxtStatus.Text = "缺交作业名单已更新（共 " + saved.LiveCount + " 条记录）";
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
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x4F, 0xA3))
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

    // ---------- 全局玻璃底色（r77：从「布局设置」迁到界面主题） ----------
    private bool _glassWired;

    /// <summary>从设置读出玻璃控件并挂事件（只在窗口构造时调一次）。</summary>
    private void InitGlassControls()
    {
        if (_glassWired || ChkGlassOn == null) return;
        _glassWired = true;

        var s = Store.LoadSettings();
        ChkGlassOn.IsChecked = s.GlassEnabled;
        ChkGlassFloat.IsChecked = s.GlassFloatEnabled;
        SlGlass.Value = Math.Clamp(s.GlassOpacity, SlGlass.Minimum, SlGlass.Maximum);
        ChkGlassAll.IsChecked = s.GlassAllWindows;
        SlGlassChrome.Value = Math.Clamp(s.GlassChromeOpacity, SlGlassChrome.Minimum, SlGlassChrome.Maximum);
        SelectGlassMode(s.GlassBackdropMode);
        RefreshGlassUi();

        ChkGlassOn.Click += (_, _) => ApplyGlassChange();
        ChkGlassFloat.Click += (_, _) => ApplyGlassChange();
        ChkGlassAll.Click += (_, _) => ApplyGlassChange();
        SlGlass.ValueChanged += (_, _) => ApplyGlassChange();
        SlGlassChrome.ValueChanged += (_, _) => ApplyGlassChange();
        CbGlassMode.SelectionChanged += (_, _) => ApplyGlassChange();
    }

    /// <summary>玻璃开关 / 通透度 / 档位：即时写盘，刷新文字并让已打开的窗口立刻跟上。</summary>
    private void ApplyGlassChange()
    {
        if (!_glassWired) return;
        try
        {
            var s = Store.LoadSettings();
            s.GlassEnabled = ChkGlassOn.IsChecked == true;
            s.GlassFloatEnabled = ChkGlassFloat.IsChecked == true;
            s.GlassOpacity = SlGlass.Value;
            s.GlassAllWindows = ChkGlassAll.IsChecked == true;
            s.GlassChromeOpacity = SlGlassChrome.Value;
            s.GlassBackdropMode = SelectedGlassMode();
            if (Store.EnablePersistence) Store.SaveSettings(s);
            RefreshGlassUi();
            GlassBrush.RefreshAll(s);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换玻璃效果失败：" + ex.Message;
        }
    }

    private string SelectedGlassMode()
        => (CbGlassMode.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
           ?? GlassBackdrop.ModeAuto;

    private void SelectGlassMode(string? mode)
    {
        string m = string.IsNullOrWhiteSpace(mode) ? GlassBackdrop.ModeAuto : mode!;
        foreach (var o in CbGlassMode.Items)
            if (o is System.Windows.Controls.ComboBoxItem it
                && string.Equals(it.Tag as string, m, StringComparison.OrdinalIgnoreCase))
            { CbGlassMode.SelectedItem = it; return; }
        CbGlassMode.SelectedIndex = 0;
    }

    private void RefreshGlassUi()
    {
        if (ChkGlassOn == null) return;
        bool on = ChkGlassOn.IsChecked == true;
        bool floatOn = on && ChkGlassFloat.IsChecked == true;
        bool chromeOn = on && ChkGlassAll.IsChecked == true;

        ChkGlassFloat.IsEnabled = on;
        SlGlass.IsEnabled = floatOn;
        ChkGlassAll.IsEnabled = on;
        SlGlassChrome.IsEnabled = chromeOn;
        CbGlassMode.IsEnabled = chromeOn;

        TxtGlass.Text = floatOn ? $"{SlGlass.Value:0}% 通透" : "未启用";
        TxtGlassChrome.Text = chromeOn ? $"{SlGlassChrome.Value:0}% 通透" : "未启用";
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
    /// <summary>托盘里的「涂鸦板 ▸」子菜单；内容每次菜单弹出时重建（板可能刚被关掉 / 新建）。</summary>
    private System.Windows.Forms.ToolStripMenuItem? _doodleMenu;

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

        // 涂鸦板：固定子菜单，不进 AppSettings.TrayMenuItems ——
        // 那条配置是用户自己在「任务栏菜单…」里排的，程序往里塞东西会打乱他的顺序。
        // ★ 旧版这里只有一个「新建」，板一关就没入口找回；现在列出全部已创建的板。
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _doodleMenu = new System.Windows.Forms.ToolStripMenuItem("涂鸦板")
        {
            Tag = "doodle_root",
            ToolTipText = "已创建的黑板都在这里，点一下重新打开或切到前面"
        };
        menu.Items.Add(_doodleMenu);

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
            case TrayCommands.UserCenter: ShowMainWindow(); MainTabs.SelectedIndex = 7; break;
            case TrayCommands.ToggleSchedule: BtnToggleSch_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.ToggleNotice: BtnToggleNot_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.ToggleHomework: BtnToggleHw_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.ToggleMissing: BtnToggleMs_Click(this, new RoutedEventArgs()); break;
            case TrayCommands.BackdropSchedule: ToggleBackdropSchedule(); break;
            case TrayCommands.BackdropHomework: ToggleBackdropHomework(); break;
            case TrayCommands.BackdropMissing: ToggleBackdropMissing(); break;
            case TrayCommands.BackdropOff: CancelBackdrop(confirm: false); break;
            case TrayCommands.ToggleHandBall: SetHandwritingBall(!Store.LoadSettings().ShowHandwritingBall); break;
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
    /// <summary>托盘「新建涂鸦板」。失败必须说出来 —— 点了没反应比报错更难查。</summary>
    private void NewDoodleBoard()
    {
        try
        {
            DoodleBoardService.CreateNew();
            RefreshDoodleTrayMenu();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "新建涂鸦板失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 重建托盘「涂鸦板 ▸」的子菜单：每块已创建的板一行，开着的标 ●、收起的标 ○。
    /// <para>★ 每次菜单弹出都重画：板可能在别处被新建、收起或删除，缓存一份就会点空。</para>
    /// </summary>
    private void RefreshDoodleTrayMenu()
    {
        if (_doodleMenu == null) return;

        var items = _doodleMenu.DropDownItems;
        var olds = new List<System.Windows.Forms.ToolStripItem>();
        foreach (System.Windows.Forms.ToolStripItem item in items) olds.Add(item);
        items.Clear();
        foreach (var old in olds) old.Dispose();

        var cfgs = DoodleBoardService.AllConfigs;
        if (cfgs.Count == 0)
        {
            items.Add(new System.Windows.Forms.ToolStripMenuItem("（还没有黑板，点下面新建）") { Enabled = false });
        }
        else
        {
            foreach (var cfg in cfgs)
            {
                bool open = DoodleBoardService.FindOpen(cfg.Id) != null;
                string id = cfg.Id;
                string name = string.IsNullOrWhiteSpace(cfg.Name) ? "涂鸦板" : cfg.Name;
                items.Add(new System.Windows.Forms.ToolStripMenuItem((open ? "● " : "○ ") + name, null, (_, _) =>
                {
                    try { DoodleBoardService.OpenOrActivate(id); }
                    catch (Exception ex) { TxtStatus.Text = "打开黑板失败：" + ex.Message; }
                })
                {
                    ToolTipText = open ? "已打开，点一下切到前面" : "已收起，点一下重新打开"
                });
            }
        }

        items.Add(new System.Windows.Forms.ToolStripSeparator());
        items.Add(new System.Windows.Forms.ToolStripMenuItem("新建黑板…", null, (_, _) => NewDoodleBoard()));
        items.Add(new System.Windows.Forms.ToolStripMenuItem("管理所有黑板…", null, (_, _) => OpenDoodleManager()));
    }

    /// <summary>打开独立的黑板管理窗（打开 / 收起 / 改名 / 删除）。</summary>
    private void OpenDoodleManager()
    {
        try
        {
            var w = new DoodleBoardManagerWindow { Owner = this };
            w.ShowDialog();
            RefreshDoodleTrayMenu();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "打开黑板管理失败：" + ex.Message;
        }
    }

    private void UpdateTrayMenuState()
    {
        try
        {
            if (_trayMenu == null) return;
            RefreshDoodleTrayMenu();
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
                    case TrayCommands.ToggleHandBall: mi.Checked = Store.LoadSettings().ShowHandwritingBall; break;
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
        SyncNavFromTab(MainTabs.SelectedIndex);
    }

    // ---------------- P1 新导航（侧栏 5 组 + 二级分段条） ----------------
    // 设计要点：TabControl 的 9 个 TabItem 顺序、索引、x:Name 全部不变，
    // HeadlessTabs 样式只隐藏页头；「今日」追加在末尾 index 9。
    // 因此 SelectedIndex = 0/2/7/8、托盘命令、--shot main* 截图目标零回归。

    private static int GroupOfTab(int index) => index switch
    {
        9 => 0,        // 今日
        0 => 1,        // 课程表
        4 or 5 => 2,   // 内容：通知公告 / 便签管理
        6 => 3,        // 消息：信息管理（同步设置 + 发布入口）
        _ => 4,        // 设置：1 数据 / 2 显示 / 3 提醒 / 7 用户 / 8 关于
    };

    private void SyncNavFromTab(int index)
    {
        try { ApplyNavGroup(GroupOfTab(index), false); }
        catch { /* 导航外观失败不能影响内容加载 */ }
    }

    private void InitNavShell()
    {
        try
        {
            RefreshTodayPage();
            if (!_initialTabSet && MainTabs != null && MainTabs.Items.Count > 9)
                MainTabs.SelectedIndex = 9;
            ApplyNavGroup(GroupOfTab(MainTabs?.SelectedIndex ?? 0), false);
        }
        catch { }
    }

    private void RefreshTodayPage()
    {
        if (TxtTodaySubtitle == null) return;
        string[] weeks = { "日", "一", "二", "三", "四", "五", "六" };
        var now = DateTime.Now;
        string cls = (CbClass?.SelectedItem as string) ?? "";
        TxtTodaySubtitle.Text = $"{now:yyyy 年 M 月 d 日} 周{weeks[(int)now.DayOfWeek]}"
            + (string.IsNullOrWhiteSpace(cls) ? "" : " · " + cls)
            + " · 课表与便签按当前显示日期刷新";
    }

    /// <summary>切换一级导航组：更新高亮与二级分段条，可选切到该组默认页。</summary>
    private void ApplyNavGroup(int group, bool selectDefaultTab)
    {
        group = Math.Clamp(group, 0, 4);
        var buttons = new[] { NavBtnToday, NavBtnWeek, NavBtnContent, NavBtnMessage, NavBtnSetting };
        var subs = new[] { SubNavToday, SubNavWeek, SubNavContent, SubNavMessage, SubNavSetting };
        var activeBg = TryFindResource("PrimaryLightBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightBlue;
        var activeFg = TryFindResource("PrimaryDarkBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DarkBlue;
        var normalFg = TryFindResource("InkBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null) continue;
            bool on = i == group;
            buttons[i]!.Background = on ? activeBg : System.Windows.Media.Brushes.Transparent;
            buttons[i]!.Foreground = on ? activeFg : normalFg;
            buttons[i]!.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
            if (subs[i] != null) subs[i]!.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        if (selectDefaultTab && MainTabs != null)
        {
            int[] def = { 9, 0, 4, 6, 2 };
            MainTabs.SelectedIndex = Math.Clamp(def[group], 0, MainTabs.Items.Count - 1);
        }
    }

    private void NavGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        int group = tag switch
        {
            "today" => 0, "week" => 1, "content" => 2, "message" => 3, "setting" => 4, _ => 0
        };
        ApplyNavGroup(group, true);
        if (group == 0) RefreshTodayPage();
    }

    private void NavSub_Click(object sender, RoutedEventArgs e) => RunNavAction(sender);
    private void NavTodayAction_Click(object sender, RoutedEventArgs e) => RunNavAction(sender);

    /// <summary>二级分段条与今日页快捷动作共用：数字 = 切 Tab，字符串 = 复用已有事件处理器。</summary>
    private void RunNavAction(object sender)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (int.TryParse(tag, out int idx))
        {
            if (MainTabs != null) MainTabs.SelectedIndex = Math.Clamp(idx, 0, MainTabs.Items.Count - 1);
            return;
        }
        var args = new RoutedEventArgs();
        switch (tag)
        {
            case "week": ApplyNavGroup(1, true); break;
            case "publish": OnQuickOpenPublish(); break;
            case "ledger": OnQuickOpenInfoLedger(); break;
            case "pull": BtnInfoPull_Click(this, args); break;
            case "layout": BtnLayout_Click(this, args); break;
            case "backdrop": BtnWpSettings_Click(this, args); break;
        }
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
    // 展示控制里的「全屏 / 锁定 / 隐藏到托盘」按钮已移除；全屏与锁定改由启动项与快捷键驱动
    // （Ctrl+L 锁定、F11/Esc 切换全屏），这里只保留状态切换逻辑，不再回写按钮文字。
    private void SetFullscreen(bool on)
    {
        _isFull = on;
        if (on)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
    }

    private void ToggleLock()
    {
        _locked = !_locked;
        LockOverlay.Visibility = _locked ? Visibility.Visible : Visibility.Collapsed;
        Cursor = _locked ? System.Windows.Input.Cursors.None : System.Windows.Input.Cursors.Arrow;
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

    // ---------- 保存位置（数据包默认目录 / 备份文件夹，r79） ----------

    /// <summary>把两项路径回显到界面；空值显示默认位置的说明文字。</summary>
    private void RefreshSaveDirsUi()
    {
        try
        {
            var s = Store.LoadSettings();
            if (TxtPackDir != null)
                TxtPackDir.Text = string.IsNullOrWhiteSpace(s.PackageSaveDir)
                    ? "（默认：系统默认位置 / 上次用过的文件夹）"
                    : s.PackageSaveDir;
            if (TxtBackupDir != null)
                TxtBackupDir.Text = string.IsNullOrWhiteSpace(s.BackupRootDir)
                    ? Store.DefaultBackupRoot + "（默认）"
                    : s.BackupRootDir;
        }
        catch { }
    }

    /// <summary>统一的选择文件夹对话框（.NET 8 自带的 OpenFolderDialog，比 WinForms 版好看且高 DPI 友好）。</summary>
    private static string? PickFolder(string title, string? initial)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
                dlg.InitialDirectory = initial!;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }
        catch { return null; }
    }

    private void BtnPickPackDir_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        string? dir = PickFolder("选择数据包的默认保存文件夹", s.PackageSaveDir);
        if (string.IsNullOrWhiteSpace(dir)) return;

        s.PackageSaveDir = dir!.Trim();
        Store.SaveSettings(s);
        RefreshSaveDirsUi();
        TxtStatus.Text = "导出数据包时将默认打开：" + s.PackageSaveDir;
    }

    private void BtnResetPackDir_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        s.PackageSaveDir = "";
        Store.SaveSettings(s);
        RefreshSaveDirsUi();
        TxtStatus.Text = "数据包保存位置已改回默认（跟随系统默认 / 上次用过的文件夹）。";
    }

    private void BtnPickBackupDir_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        string current = Store.BackupRoot;

        string? dir = PickFolder("选择备份文件夹（自动备份都会写到这里）", current);
        if (string.IsNullOrWhiteSpace(dir)) return;

        string target;
        try { target = System.IO.Path.GetFullPath(dir!.Trim()); }
        catch { TxtStatus.Text = "这个路径不对劲：" + dir; return; }

        if (string.Equals(target.TrimEnd('\\'), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            TxtStatus.Text = "备份文件夹没有变化。";
            return;
        }

        int moved = MoveBackups(current, target, out string err);
        if (err.Length > 0)
        {
            // 搬不动就整件事不做：宁可保持原状，也不能让「设置指向新目录、旧备份留在原地」
            TxtStatus.Text = "备份文件夹没改成：" + err + "（原备份与设置都没有改动）";
            return;
        }

        s.BackupRootDir = target;
        Store.SaveSettings(s);
        RefreshSaveDirsUi();
        TxtStatus.Text = moved > 0
            ? $"备份文件夹已改到 {target}，顺手搬过去 {moved} 份旧备份。"
            : $"备份文件夹已改到 {target}。";
    }

    private void BtnResetBackupDir_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        if (string.IsNullOrWhiteSpace(s.BackupRootDir))
        {
            TxtStatus.Text = "备份文件夹已经是默认位置：" + Store.DefaultBackupRoot;
            return;
        }

        string current = Store.BackupRoot;
        string target = Store.DefaultBackupRoot;
        int moved = MoveBackups(current, target, out string err);
        if (err.Length > 0)
        {
            TxtStatus.Text = "没切回默认位置：" + err + "（设置未改动）";
            return;
        }

        s.BackupRootDir = "";
        Store.SaveSettings(s);
        RefreshSaveDirsUi();
        TxtStatus.Text = moved > 0
            ? $"备份文件夹已恢复默认，{moved} 份备份已搬回 {target}。"
            : "备份文件夹已恢复默认：" + target;
    }

    /// <summary>
    /// 把旧备份根目录下的每一份备份搬到新目录，返回搬动份数。
    /// <para>同名的（同一时间戳）跳过不覆盖；跨盘 / 跨共享时 Directory.Move 会失败，
    /// 自动退化成「复制 + 删源」。任何一步出错都连源目录一起保留，并让调用方放弃改设置。</para>
    /// </summary>
    private static int MoveBackups(string from, string to, out string err)
    {
        err = "";
        try
        {
            if (!System.IO.Directory.Exists(from)) return 0;
            if (string.Equals(from.TrimEnd('\\'), to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 0;

            System.IO.Directory.CreateDirectory(to);
            int n = 0;
            foreach (var sub in System.IO.Directory.GetDirectories(from))
            {
                string dest = System.IO.Path.Combine(to, System.IO.Path.GetFileName(sub));
                if (System.IO.Directory.Exists(dest)) continue;

                try
                {
                    System.IO.Directory.Move(sub, dest);
                }
                catch
                {
                    CopyFolder(sub, dest);
                    try { System.IO.Directory.Delete(sub, true); } catch { }
                }
                n++;
            }
            return n;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return 0;
        }
    }

    private static void CopyFolder(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var f in System.IO.Directory.GetFiles(from))
            System.IO.File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(from))
            CopyFolder(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
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
        // r79：默认落在用户指定的目录（「数据管理 → 保存位置」）
        string packDir = Store.LoadSettings().PackageSaveDir;
        if (!string.IsNullOrWhiteSpace(packDir) && System.IO.Directory.Exists(packDir))
            dlg.InitialDirectory = packDir;
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

        // 导出成功就把这次用的目录记下来，下次对话框直接落在这儿（用户手动换了地方也不用再选一遍）
        try
        {
            string usedDir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            if (usedDir.Length > 0 && !string.Equals(usedDir, packDir, StringComparison.OrdinalIgnoreCase))
            {
                var st = Store.LoadSettings();
                st.PackageSaveDir = usedDir;
                Store.SaveSettings(st);
                RefreshSaveDirsUi();
            }
        }
        catch { }

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
        // r79：接收方一般从同一个目录拿包，直接用「保存位置」里设的目录
        string inDir = Store.LoadSettings().PackageSaveDir;
        if (!string.IsNullOrWhiteSpace(inDir) && System.IO.Directory.Exists(inDir))
            dlg.InitialDirectory = inDir;
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

    // ---------- 单双周课程显示 ----------

    /// <summary>
    /// 「同时显示单双周」开关。默认关 = 只显示本周要上的那节。
    /// <para>改完立刻重画悬浮课表与桌面背景（走课表变化事件，两边共用一次刷新），
    /// 不用重启就能看出"少了一半灰字"。</para>
    /// </summary>
    private void ChkBothWeeks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.ShowAlternateWeekLessons = ChkBothWeeks.IsChecked == true;
            Store.SaveSettings(s);

            AppEvents.RaiseScheduleChanged();

            TxtStatus.Text = s.ShowAlternateWeekLessons
                ? "已改为同时显示单双周：同一格两门课都会列出来，不是本周的那节显示为灰色。"
                : "已改为只显示本周课程：单双周交替的格子只列本周要上的那节，看起来更简洁。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换单双周显示失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 触屏滚动方式：勾选 = 双指上下划滚动（默认，单指留给拖动换节）；
    /// 取消 = 交回系统做单指滚动（老一体机只报单点触摸时用，代价是手指拖不动课程卡）。
    /// <para>改完立刻生效：存盘 + 通知悬浮课表刷新，<c>ScheduleWindow.Refresh</c> 里会重设
    /// <c>ScrollViewer.PanningMode</c>，不用重启。</para>
    /// </summary>
    private void ChkTwoFinger_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.TwoFingerScroll = ChkTwoFinger.IsChecked == true;
            Store.SaveSettings(s);

            AppEvents.RaiseScheduleChanged();

            TxtStatus.Text = s.TwoFingerScroll
                ? "已启用双指滚动：按住卡片左列「第N节」可直接拖动换课（或按住 0.5 秒浮起再拖），快速划动或双指划动＝滚动课表。"
                : "已改为只认单指滚动：单指快速划动＝滚动课表，按住左列抓手或按住 0.5 秒再拖＝换课（不会响应第二根手指）。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "切换触屏滚动方式失败：" + ex.Message;
        }
    }

    // ---------- 触摸键盘自动弹出 ----------
    private void ChkTouchKb_Click(object sender, RoutedEventArgs e){
        var s = Store.LoadSettings();
        s.TouchKeyboardAutoShow = ChkTouchKb.IsChecked == true;
        Store.SaveSettings(s);
        TouchInputBinder.RefreshSetting();

        if (s.TouchKeyboardAutoShow)
        {
            TxtStatus.Text = TouchKeyboard.IsAvailable
                ? "已开启：手指点输入框时会自动弹出触摸键盘。"
                : "已开启，但本机没有找到触摸键盘程序（TabTip / osk），可能需要在系统里启用触摸键盘。";
        }
        else
        {
            TouchKeyboard.Hide();
            TxtStatus.Text = "已关闭触摸键盘自动弹出（仍可从系统任务栏手动调出）。";
        }
    }

    /// <summary>手写悬浮球开关。球是常驻窗口，改完要立即生效，不能让用户重启才知道。</summary>
    private void ChkHandBall_Click(object sender, RoutedEventArgs e)
        => SetHandwritingBall(ChkHandBall.IsChecked == true);

    /// <summary>
    /// 设置手写悬浮球的显示状态（「显示设置」勾选与任务栏右键菜单共用一个入口）。
    /// <para>两边必须走同一段代码：勾选框、托盘勾选、球的可见性三者状态一旦分叉，
    /// 就会出现"菜单里打勾但屏幕上没球"这种谁也说不清的毛病。</para>
    /// </summary>
    private void SetHandwritingBall(bool on)
    {
        var s = Store.LoadSettings();
        s.ShowHandwritingBall = on;
        Store.SaveSettings(s);

        App.EnsureHandwritingBall();

        if (ChkHandBall != null) ChkHandBall.IsChecked = on;
        UpdateTrayMenuState();

        TxtStatus.Text = on
            ? (BackdropService.IsApplied()
                ? "手写悬浮球已开启，但当前是「桌面背景」模式，取消背景后才会显示在桌面上。"
                : "已显示手写悬浮球：点一下就能手写，按住可以拖到屏幕任意位置。")
            : "已隐藏手写悬浮球（编辑窗里的「✎ 手写输入」按钮仍然可用）。";
    }

    private void CheckMaxWindow(bool force = false)    {
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
        ColorOption("浅色", "#F1F4F9"),
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

    // r75：通知条格式条的状态（字号磅值 + 文字颜色）。
    // 原来靠两个 ComboBox 的 SelectedItem 记状态，现在字号是 A± 步进、颜色是色板弹层，得自己记账。
    private int _notifSize = 28;
    private string _notifTextHex = "";
    private List<NotifColorOption> _notifTextOptions = new();

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

            // 字号：老数据可能存着 25 这种「不在档位表里」的值 —— 照原值显示，A± 时才吸附到最近档
            _notifSize = Math.Clamp((int)Math.Round(nl.FontSize <= 0 ? 28 : nl.FontSize), 12, 96);
            UpdateNotifSizeUi();

            // 文字颜色：空 = 跟随通知条底色的默认白字
            _notifTextOptions = NotifTextColorOptions();
            _notifTextHex = string.IsNullOrWhiteSpace(nl.TextColor) ? "" : nl.TextColor!.Trim();
            BuildNotifSwatches();

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

    /// <summary>字号那一格 + 右侧摘要 + 色条颜色（三个地方读的是同一份状态）。</summary>
    private void UpdateNotifSizeUi()
    {
        TxtNotifSize.Text = _notifSize + " 号";
        NotifColorBar.Fill = NotifTextBrush();
        TxtNotifStyleMeta.Text = "字号 " + _notifSize
                                 + (string.IsNullOrEmpty(_notifTextHex) ? " · 默认白字" : " · " + _notifTextHex!.ToUpperInvariant());
    }

    private System.Windows.Media.Brush NotifTextBrush()
        => string.IsNullOrEmpty(_notifTextHex)
            ? System.Windows.Media.Brushes.White
            : ThemeHelper.Brush(_notifTextHex!, System.Windows.Media.Colors.White);

    /// <summary>按档位表顺序步进（12,14,16…96），不是 ±1 磅 —— ±1 会把「18→19」这种半档写成歪值。</summary>
    private void StepNotifSize(int dir)
    {
        var arr = NotifSizes.Select(int.Parse).ToArray();
        int idx = Array.FindIndex(arr, v => v == _notifSize);
        if (idx < 0)
        {
            idx = 0;
            while (idx < arr.Length - 1 && arr[idx] < _notifSize) idx++;
        }
        idx = Math.Clamp(idx + dir, 0, arr.Length - 1);
        if (arr[idx] == _notifSize) return;
        _notifSize = arr[idx];
        UpdateNotifSizeUi();
        SaveNotifStyle();
    }

    private void BtnNotifSizeDown_Click(object sender, RoutedEventArgs e) => StepNotifSize(-1);
    private void BtnNotifSizeUp_Click(object sender, RoutedEventArgs e) => StepNotifSize(1);

    /// <summary>重建文字颜色色板（与格式条同一个色格样式；自定义色会被补齐到末尾，不会丢）。</summary>
    private void BuildNotifSwatches()
    {
        if (_notifTextOptions.Count == 0) _notifTextOptions = NotifTextColorOptions();
        var list = _notifTextOptions;
        var cur = EnsureNotifColor(list, string.IsNullOrEmpty(_notifTextHex) ? "#FFFFFF" : _notifTextHex);
        NotifSwatchGrid.Children.Clear();
        foreach (var opt in list)
        {
            var sw = new System.Windows.Controls.Border
            {
                Style = (System.Windows.Style)FindResource("BarSwatchStyle"),
                Background = opt.Swatch,
                ToolTip = opt.Name,
                Tag = opt
            };
            if (string.Equals(opt.Hex ?? "", cur.Hex ?? "", StringComparison.OrdinalIgnoreCase))
            {
                sw.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1E, 0x4F, 0xA3));
                sw.BorderThickness = new Thickness(2);
            }
            var picked = opt;
            sw.MouseLeftButtonUp += (_, _) =>
            {
                _notifTextHex = picked.Hex ?? "";
                NotifColorPopup.IsOpen = false;
                BuildNotifSwatches();
                SaveNotifStyle();
            };
            NotifSwatchGrid.Children.Add(sw);
        }
        NotifColorBar.Fill = NotifTextBrush();
        TxtNotifStyleMeta.Text = "字号 " + _notifSize
                                 + (string.IsNullOrEmpty(_notifTextHex) ? " · 默认白字" : " · " + _notifTextHex!.ToUpperInvariant());
    }

    private void BtnNotifColor_Click(object sender, RoutedEventArgs e)
    {
        BuildNotifSwatches();
        NotifColorPopup.IsOpen = true;
    }

    private void BtnNotifMoreColor_Click(object sender, RoutedEventArgs e)
    {
        if (!EditorStyleOptions.TryPickColor(string.IsNullOrEmpty(_notifTextHex) ? "#FFFFFF" : _notifTextHex, out string hex))
        {
            TxtStatus.Text = "没有取到颜色，保持原样。";
            return;
        }
        _notifTextHex = hex;
        NotifColorPopup.IsOpen = false;
        BuildNotifSwatches();
        SaveNotifStyle();
        TxtStatus.Text = "通知条字色已设为自定义颜色 " + hex.ToUpperInvariant() + "。";
    }

    /// <summary>恢复默认：微软雅黑 / 28 号 / 白字 / 跟随主题的深蓝底。</summary>
    private void BtnNotifReset_Click(object sender, RoutedEventArgs e)
    {
        _notifStyleReady = false;
        try
        {
            _notifSize = 28;
            _notifTextHex = "";
            if (CbNotifFont.ItemsSource is List<string> fs)
            {
                int i = fs.IndexOf(NotifFonts[0]);
                CbNotifFont.SelectedIndex = i >= 0 ? i : 0;
            }
            if (CbNotifBg.ItemsSource is List<NotifColorOption> bars)
                CbNotifBg.SelectedItem = EnsureNotifColor(bars, "");
        }
        finally { _notifStyleReady = true; }
        UpdateNotifSizeUi();
        BuildNotifSwatches();
        SaveNotifStyle();
        TxtStatus.Text = "通知条外观已恢复默认。";
    }

    /// <summary>预览条与顶部通知字幕条用同一套字体 / 字号 / 颜色，改完立刻能看到效果。</summary>
    private void ApplyNotifPreview()
    {
        try
        {
            var font = CbNotifFont.SelectedItem as string ?? NotifFonts[0];
            NotifPreviewText.FontFamily = new System.Windows.Media.FontFamily(font);
            NotifPreviewText.FontSize = Math.Clamp(_notifSize, 12, 120);
            NotifPreviewText.Foreground = NotifTextBrush();

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

    /// <summary>把当前格式条 / 下拉里的字体、字号、字色、背景写进设置，并让顶部字幕条立即按新外观重排。</summary>
    private void SaveNotifStyle()
    {
        try
        {
            var s = Store.LoadSettings();
            s.NotificationLayout ??= new PanelLayout();
            s.NotificationLayout.FontFamily = CbNotifFont.SelectedItem as string ?? "";
            s.NotificationLayout.FontSize = _notifSize;
            s.NotificationLayout.TextColor = _notifTextHex;
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

    private void BtnNotifBgPick_Click(object sender, RoutedEventArgs e)
    {
        var cur = CbNotifBg.SelectedItem as NotifColorOption;
        if (!EditorStyleOptions.TryPickColor(cur?.Hex, out string hex))
        {
            TxtStatus.Text = "没有取到颜色，保持原样。";
            return;
        }

        var list = CbNotifBg.ItemsSource as List<NotifColorOption> ?? NotifBarColorOptions();
        var opt = EnsureNotifColor(list, hex);

        // 换数据源会先触发一次"没有选中项"的 SelectionChanged，用标志挡住，避免把设置写成空值
        _notifStyleReady = false;
        try
        {
            CbNotifBg.ItemsSource = null;
            CbNotifBg.ItemsSource = list;
            CbNotifBg.SelectedItem = opt;
        }
        finally { _notifStyleReady = true; }

        SaveNotifStyle();
        TxtStatus.Text = "通知条背景已设为自定义颜色 " + hex.ToUpperInvariant() + "。";
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
                // 打墓碑而不是移除：物理删除会让晚到的旧变更单把它重新加回来
                var all = Store.LoadNotificationsAll();
                var target = all.FirstOrDefault(x => x.Id == item.Id);
                if (target != null)
                {
                    target.Deleted = true;
                    target.DeletedAt = DateTime.Now;
                    Store.SaveNotifications(all);
                }
                Notifications.Remove(item);
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

    private void SaveNotes()
    {
        // ★ 不能直接 SaveNotifications(Notifications)：这个集合只装「未撤下」的项，
        //   直接保存会把墓碑整批冲掉，等于又退回物理删除（晚到的旧指令会复活已撤下的通知）。
        //   这里按 Id 把界面上的改动 patch 回全量列表，墓碑原样保留。
        var all = Store.LoadNotificationsAll();
        var byId = new Dictionary<string, Notification>();
        foreach (var x in all) byId[x.Id] = x;
        foreach (var n in Notifications)
        {
            if (byId.TryGetValue(n.Id, out var target))
            {
                target.Title = n.Title;
                target.Content = n.Content;
                target.Pinned = n.Pinned;
                target.Scope = n.Scope;
                target.DueAt = n.DueAt;
                // r72：文字格式跟着 patch，别让主页的置顶 / 编辑把编辑窗设好的样式冲掉
                target.TitleStyle = n.TitleStyle?.Clone();
                target.ContentStyle = n.ContentStyle?.Clone();
            }
            else all.Add(n);
        }
        Store.SaveNotifications(all);
    }

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

    // ==================== 信息管理页 ====================
    // 发布作业 / 缺交 / 通知的入口、任教班级、同步设置都集中在这一个标签页；
    // 消息台账（BoardSyncWindow）只负责查看与裁决，不再堆设置输入框。

    /// <summary>本次发布信息的目标班级（勾选状态只存内存，避免下次启动悄悄发到多个班）。</summary>
    private readonly HashSet<string> _pickedInfoClasses = new(StringComparer.Ordinal);
    private bool _teachingGuard;

    private void InitInfoTab()
    {
        try
        {
            CbTeachingClass.ItemsSource = ClassNames;   // 与顶栏班级下拉共用同一份课表班级
            LoadInfoSettings();
        }
        catch (Exception ex)
        {
            TxtInfoStatus.Text = "信息管理页初始化失败：" + ex.Message;
        }
    }

    /// <summary>本机自己的任教班级列表（老版本「额外订阅班级」字段，逗号 / 顿号 / 分号分隔）。</summary>
    private static List<string> TeachingClasses()
    {
        string raw = Store.LoadSettings().BoardSyncExtraClasses ?? "";
        var list = new List<string>();
        foreach (var p in raw.Split(new[] { ',', '，', '、', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string c = p.Trim();
            if (c.Length > 0 && !list.Contains(c, StringComparer.Ordinal)) list.Add(c);
        }
        return list;
    }

    private static void SaveTeachingClasses(IEnumerable<string> list)
    {
        var s = Store.LoadSettings();
        s.BoardSyncExtraClasses = string.Join("、", list);
        Store.SaveSettings(s);
    }

    /// <summary>回显同步设置 + 重建任教班级 chips + 刷新发布权限。</summary>
    private void LoadInfoSettings()
    {
        var s = Store.LoadSettings();
        ChkInfoSyncEnabled.IsChecked = s.BoardSyncEnabled;
        ChkInfoIsClassroom.IsChecked = s.BoardSyncIsClassroom;
        ChkInfoPopup.IsChecked = s.BoardSyncPopupDuringBreak;
        ChkInfoFullScreen.IsChecked = s.BoardSyncFullScreenReminder;
        ChkInfoVoice.IsChecked = s.BoardSyncVoiceReminder;
        ChkInfoAutoApply.IsChecked = s.BoardSyncAutoApply;
        TxtInfoRoot.Text = s.BoardSyncRoot ?? "";
        TxtInfoPoll.Text = Math.Max(15, s.BoardSyncPollSeconds).ToString();
        TxtInfoKeepDays.Text = Math.Max(0, s.BoardSyncLedgerKeepDays).ToString();

        var list = TeachingClasses();
        foreach (var dead in _pickedInfoClasses.Where(x => !list.Contains(x, StringComparer.Ordinal)).ToList())
            _pickedInfoClasses.Remove(dead);
        // 首次进入：优先勾上本机当前显示班级；只有一个任教班时直接勾上，
        // 省掉「点了发布才提示先勾选」这一步；多班时不自动全勾，避免一次误发到多个班。
        if (_pickedInfoClasses.Count == 0)
        {
            string own = (s.SelectedClass ?? "").Trim();
            if (own.Length > 0 && list.Contains(own, StringComparer.Ordinal)) _pickedInfoClasses.Add(own);
            else if (list.Count == 1) _pickedInfoClasses.Add(list[0]);
        }
        RebuildTeachingClassChips();
        TxtTeachingClassHint.Text = ClassNames.Count == 0 ? "（未导入课表，暂无可选班级）" : "选中即加入列表";
        ApplyInfoPermissions();
    }

    /// <summary>任教班级 → 一排可勾选、可移除的小标签。</summary>
    private void RebuildTeachingClassChips()
    {
        TeachingClassChips.Children.Clear();
        var list = TeachingClasses();
        foreach (var c in list)
        {
            string cls = c;
            var chip = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xF4, 0xFD)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC7, 0xDC, 0xF7)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 6, 4), Margin = new Thickness(0, 0, 8, 8)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = cls, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _pickedInfoClasses.Contains(cls),
                ToolTip = "勾上 = 本次发布信息的目标班级（可单选、多选）"
            };
            cb.Checked += (_, _) => _pickedInfoClasses.Add(cls);
            cb.Unchecked += (_, _) => _pickedInfoClasses.Remove(cls);
            var rm = new Button
            {
                Content = "×", Tag = cls, FontSize = 13, Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(8, 0, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = null, ToolTip = "从任教班级列表里移除"
            };
            rm.Click += RemoveTeachingClass_Click;
            row.Children.Add(cb);
            row.Children.Add(rm);
            chip.Child = row;
            TeachingClassChips.Children.Add(chip);
        }
        TxtTeachingClassEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>下拉选中一个班立即加入任教班级列表（不用再点「添加」）。</summary>
    private void CbTeachingClass_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_teachingGuard) return;
        if (CbTeachingClass.SelectedItem is not string cls || string.IsNullOrWhiteSpace(cls)) return;
        _teachingGuard = true;
        try
        {
            var list = TeachingClasses();
            if (!list.Contains(cls, StringComparer.Ordinal))
            {
                list.Add(cls);
                SaveTeachingClasses(list);
                _pickedInfoClasses.Add(cls);   // 新加的班默认勾上，省一次点击
                RebuildTeachingClassChips();
                TxtInfoStatus.Text = "已增加任教班级「" + cls + "」，并勾为发布目标。";
            }
            CbTeachingClass.SelectedIndex = -1;
        }
        finally { _teachingGuard = false; }
    }

    private void RemoveTeachingClass_Click(object sender, RoutedEventArgs e)
    {
        string cls = (sender as Button)?.Tag as string ?? "";
        if (cls.Length == 0) return;
        var list = TeachingClasses();
        list.RemoveAll(c => string.Equals(c, cls, StringComparison.Ordinal));
        SaveTeachingClasses(list);
        _pickedInfoClasses.Remove(cls);
        RebuildTeachingClassChips();
        TxtInfoStatus.Text = "已从任教班级移除「" + cls + "」。";
    }

    /// <summary>发布权限只问 UserSession；电教管理员默认发不了，按钮直接禁灰并说明原因。</summary>
    private void ApplyInfoPermissions()
    {
        bool can = UserSession.CanPublishInfo;
        foreach (var b in new[] { BtnInfoPublish, BtnInfoPublishHw, BtnInfoPublishMs, BtnInfoPublishNotice })
        {
            if (b == null) continue;
            b.IsEnabled = can;
            b.ToolTip = can ? "把信息发给勾选的任教班级" : UserSession.DenyPublishInfo;
        }

        var s = Store.LoadSettings();
        bool configured = !(s.UpdateSourceKind ?? "Off").Equals("Off", StringComparison.OrdinalIgnoreCase)
                          || !string.IsNullOrWhiteSpace(s.BoardSyncRoot);
        if (!can)
        {
            TxtInfoHint.Text = "🔒 " + UserSession.DenyPublishInfo;
            TxtInfoHint.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x6A, 0x20));
        }
        else if (!s.BoardSyncEnabled)
        {
            TxtInfoHint.Text = "提示：同步还没启用。展开下面「③ 同步设置」勾选「启用同步」并保存后才能发布信息。";
            TxtInfoHint.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x70, 0x85));
        }
        else if (!configured)
        {
            TxtInfoHint.Text = "⚠ 还没配置数据源：请在「同步设置」里填摆渡根目录，或到「数据管理 → 自动更新设置」配置共享目录 / FTP。";
            TxtInfoHint.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        }
        else
        {
            TxtInfoHint.Text = "已就绪：勾选任教班级 → 点「发布信息」→ 教室机电教管理员确认后应用；回执可在「消息台账 / 回执」里核对。";
            TxtInfoHint.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x66, 0x3C));
        }
    }

    private void BtnInfoPublish_Click(object sender, RoutedEventArgs e) => OpenInfoPublish(null);

    private void BtnInfoPublishType_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as Button)?.Tag as string ?? "";
        string entity = tag switch
        {
            "ms" => BoardSyncConsts.EntityMissing,
            "duty" => BoardSyncConsts.EntityDuty,
            "notice" => BoardSyncConsts.EntityNotice,
            _ => BoardSyncConsts.EntityHomework
        };
        OpenInfoPublish(entity);
    }

    /// <summary>先选班级、再弹信息发布页面；多班时每个班各发一条。</summary>
    private void OpenInfoPublish(string? entity)
    {
        if (!UserSession.CanPublishInfo) { TxtInfoStatus.Text = UserSession.DenyPublishInfo; return; }

        var picked = TeachingClasses().Where(c => _pickedInfoClasses.Contains(c)).ToList();
        if (picked.Count == 0)
        {
            TxtInfoStatus.Text = "请先在「我的任教班级」里勾选要发布的班级（可单选、多选）。";
            return;
        }
        if (!Store.LoadSettings().BoardSyncEnabled)
        {
            TxtInfoStatus.Text = "同步还没启用：请展开「③ 同步设置」勾选「启用同步」并保存后重试。";
            return;
        }
        PublishFor(picked, entity);
    }

    /// <summary>
    /// 弹「新建班级内容变更单」并逐条发出，结果写状态栏。
    /// <para>信息管理页与悬浮课表「📤 发布」快捷按钮共用这一条链，避免两处发送逻辑各写一遍。</para>
    /// </summary>
    private void PublishFor(List<string> picked, string? entity)
    {
        try
        {
            var w = new BoardSendWindow(picked, entity);
            // 主窗口最小化到托盘时不要挂 Owner：CenterOwner 会把弹窗排到屏幕外 / 被别的窗口盖住
            if (IsVisible) w.Owner = this;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (w.ShowDialog() != true || w.Results.Count == 0) return;

            int ok = 0;
            var errs = new List<string>();
            foreach (var o in w.Results)
            {
                var r = BoardSyncService.Send(o);
                if (r.Ok) ok++;
                else errs.Add(r.Message);
            }
            string msg = ok > 0
                ? $"✔ 已发布 {ok} 条信息，等待教室机确认。"
                  + (errs.Count > 0 ? "失败 " + errs.Count + " 条：" + string.Join("；", errs) : "")
                : "❌ " + string.Join("；", errs);
            TxtStatus.Text = msg;
            if (TxtInfoStatus != null) TxtInfoStatus.Text = msg;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "发布失败：" + ex.Message;
            if (TxtInfoStatus != null) TxtInfoStatus.Text = "发布失败：" + ex.Message;
        }
    }

    private async void BtnInfoPull_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnInfoPull.IsEnabled = false;
            TxtInfoStatus.Text = "正在检查共享目录…（界面不卡，检查完自动更新）";
            // 列目录 + 读文件是网络 I/O：放后台，绝不让界面跟着等共享盘
            var (report, err) = await Task.Run(() =>
            {
                var r = BoardSyncService.Pull(BoardSyncService.SubscribedClasses());
                return (r.Report, BoardSyncService.LastError);
            });
            TxtInfoStatus.Text = report + (err == null ? "" : "；" + err);
        }
        catch (Exception ex) { TxtInfoStatus.Text = "检查失败：" + ex.Message; }
        finally { BtnInfoPull.IsEnabled = true; }
    }

    private async void BtnInfoSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string cls = CurrentDisplayClass();
            BtnInfoSnapshot.IsEnabled = false;
            TxtInfoStatus.Text = "正在读取教室机内容…";
            var (snap, msg) = await Task.Run(() => BoardSyncService.LoadRemoteSnapshot(cls));
            TxtInfoStatus.Text = msg;
            if (snap == null) return;
            new BoardSnapshotViewWindow(snap) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { TxtInfoStatus.Text = "读取教室机内容失败：" + ex.Message; }
        finally { BtnInfoSnapshot.IsEnabled = true; }
    }

    /// <summary>消息台账 / 回执：非模态，不禁用其它顶层窗。</summary>
    private void BtnInfoLedger_Click(object sender, RoutedEventArgs e)
    {
        try { new BoardSyncWindow { Owner = this }.Show(); }
        catch (Exception ex) { TxtInfoStatus.Text = "打不开消息台账：" + ex.Message; }
    }

    private async void BtnInfoSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.BoardSyncEnabled = ChkInfoSyncEnabled.IsChecked == true;
            s.BoardSyncIsClassroom = ChkInfoIsClassroom.IsChecked == true;
            s.BoardSyncPopupDuringBreak = ChkInfoPopup.IsChecked == true;
            s.BoardSyncFullScreenReminder = ChkInfoFullScreen.IsChecked == true;
            s.BoardSyncVoiceReminder = ChkInfoVoice.IsChecked == true;
            s.BoardSyncAutoApply = ChkInfoAutoApply.IsChecked == true;
            s.BoardSyncRoot = (TxtInfoRoot.Text ?? "").Trim();
            s.BoardSyncPollSeconds = int.TryParse((TxtInfoPoll.Text ?? "").Trim(), out var n)
                ? Math.Max(15, Math.Min(600, n)) : 60;
            TxtInfoPoll.Text = s.BoardSyncPollSeconds.ToString();

            // 台账保留天数：0 = 全部显示；上限一年，避免手滑填成 99999
            s.BoardSyncLedgerKeepDays = int.TryParse((TxtInfoKeepDays.Text ?? "").Trim(), out var keepDays)
                ? Math.Clamp(keepDays, 0, 365) : 7;
            TxtInfoKeepDays.Text = s.BoardSyncLedgerKeepDays.ToString();
            Store.SaveSettings(s);
            BoardSyncNotifier.RefreshInterval();

            // 发布快照 / 连通性测试都要碰共享盘：后台做，按钮置灰防连点，界面照旧能操作
            BtnInfoSaveSettings.IsEnabled = false;
            TxtInfoStatus.Text = "设置已保存，正在检查数据源…";
            bool classroom = s.BoardSyncIsClassroom && s.BoardSyncEnabled;
            var (ok, msg) = await Task.Run(() => classroom
                ? BoardSyncService.PublishSnapshot(force: true)
                : BoardSyncService.Test());
            TxtInfoStatus.Text = ok ? "设置已保存。" + msg : "设置已保存，但" + msg;
            ApplyInfoPermissions();
        }
        catch (Exception ex) { TxtInfoStatus.Text = "保存设置失败：" + ex.Message; }
        finally { BtnInfoSaveSettings.IsEnabled = true; }
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
            // ★ 只数可见条目：全是墓碑时仍应给一行空行，别把「已删光」误判成「有内容」
            if (cur.LiveCount == 0) cur.Items.Add(new HomeworkItem());   // 首次打开先给一行空行，填完直接确定

            var dlg = new HomeworkEditWindow(cur);
            if (dlg.ShowDialog() != true) return;

            var saved = dlg.BuildBoard();
            Store.SaveHomework(saved);
            string? writeErr = Store.LastWriteError;
            if (writeErr != null)
            {
                TxtStatus.Text = "⚠ 作业公示没有保存：" + writeErr + "（改动只在内存里，请检查账号与磁盘权限后重试）";
                return;
            }

            // ★ 不在保存链里同步重绘壁纸：BackdropService 已订阅 HomeworkChanged 走 1.2s 防抖，
            //   否则每次点确定都要等整图渲染 + PNG 编码 + 设壁纸（一体机上约 1~2 秒）。
            ReloadHomework();
            AppEvents.RaiseHomeworkChanged();
            AppEvents.RaiseBackdropChanged();
            TxtStatus.Text = "作业公示已保存（共 " + saved.LiveCount + " 科）";
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
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x4F, 0xA3))
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
