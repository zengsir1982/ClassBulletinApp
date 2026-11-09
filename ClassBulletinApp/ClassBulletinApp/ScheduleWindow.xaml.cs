using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace ClassBulletinApp;

public partial class ScheduleWindow : Window, IGlassSurface
{
    private string _semesterStart = "";

    /// <summary>当前显示的班级名或教师名。</summary>
    private string _selectedName = "";

    /// <summary>是否正在显示教师个人课表（由 AppSettings.ScheduleKind 决定，可随时切换）。</summary>
    private bool _isTeacherMode;

    private PanelLayout? _layout;
    private ScheduleFile? _schedule;
    private bool _clickThrough;
    private bool _locked;
    private AutoHider? _hider;
    private readonly DispatcherTimer _timer;

    /// <summary>日期旁实时时钟的刷新定时器（每秒一次）。</summary>
    private DispatcherTimer? _clockTimer;

    /// <summary>时钟显示方式：0 = 不显示，1 = 时:分，2 = 时:分:秒。</summary>
    private int _clockMode;

    /// <summary>「已是最新版本」提示的出现时刻（用于状态栏文案）。</summary>
    private DateTime _upToDateAt;

    /// <summary>「已是最新版本」红字提示的截止时刻；过了这个点就恢复常规字幕。</summary>
    private DateTime _upToDateUntil;

    /// <summary>「课表有变化」红字内容（空 = 当前没有变化提示）。</summary>
    private string _flashText = "";

    /// <summary>「课表有变化」红字保留到什么时候（默认 10 分钟，可在提醒设置里改）。</summary>
    private DateTime _flashUntil;

    /// <summary>程序化应用布局（ApplyLayout）期间置 true，避免把自动定位 / 自动隐藏的位移当成用户拖动保存。</summary>
    private bool _applyingLayout;

    // 常用快捷（悬浮课表下方自定义软件 / 文件夹入口）
    private System.Collections.ObjectModel.ObservableCollection<QuickLaunchItem> _ql = new();

    /// <summary>快捷方式是否仅显示图标（不显示名称）。</summary>
    private bool _qlIconOnly;

    /// <summary>快捷方式图标大小（逻辑像素）。</summary>
    private double _qlIconSize = 28;

    // ---------- 内置快捷（作业 / 缺交 / 便签 / 更新课表） ----------

    /// <summary>「更新课表」正在执行，防重复点击。</summary>
    private bool _qaUpdating;

    /// <summary>
    /// 上一次「更新课表」是否失败。
    /// <para>失败<b>不改按钮</b>（按钮始终是「更新课表」，随时可以再点一次重试），
    /// 只在下方状态栏用红字提示原因，直到下一次更新成功才消失。</para>
    /// </summary>
    private bool _qaFailed;

    /// <summary>上一次失败的原因（单行、已截断），状态栏红字用。</summary>
    private string _qaFailMessage = "";

    /// <summary>状态栏字幕的动画；重新排版前先取掉，避免旧动画继续推着文字跑。</summary>
    private TranslateTransform? _marqueeShift;

    /// <summary>「更新课表」的兜底定时器：主窗口没回消息时别让按钮永远卡在"更新中…"。</summary>
    private DispatcherTimer? _qaWatchdog;

    /// <summary>内置快捷胶囊的字号设置（0 = 随悬浮课表宽度自动）。缓存在字段里，拖动缩放时不必反复读盘。</summary>
    private double _qaFontSetting;

    /// <summary>当前已应用的胶囊字号，用于避免每次 SizeChanged 都重排一遍。</summary>
    private double _qaAppliedFont = -1;

    // 长按（触摸/鼠标左键按住）弹出编辑
    private DispatcherTimer? _pressTimer;
    private PeriodRow? _pressRow;
    private System.Windows.Point _pressPoint;

    // 拖动调课（手动命中式，不依赖系统拖放，避免“禁止拖放”光标）
    private PeriodRow? _dragCandidate;
    private System.Windows.Point _dragStart;
    private bool _dragging;
    private PeriodRow? _dropRow;
    private Border? _dropBorder;
    private Brush? _savedBg, _savedBorder;
    private Thickness _savedBorderThickness;
    private bool _dropValid;

    // ---- 触摸手势：静止才计时长按，一动就优先滚动（判定逻辑见 DrivePointer）----
    //
    //   手指按下 ──0.5 秒内位移 >10px──→ 滚动课表（本次按下永久失能：不右键、不选中、不拖拽）
    //        │
    //        └─ 静止满 0.5 秒 → 浮起（长按态，幽灵卡跟手）
    //                 ├ 原地松手（位移 <10px） → 打开调课窗（＝ 鼠标右键）
    //                 └ 移动 ≥10px            → 拖动，松手落到哪节就交换哪节
    //
    //   特例：按在【左列抓手】上 → 0 秒起拖（不判滚动、不计长按），原地松手同样＝右键。
    //        抓手是给"手指按下去头 10px 容易抖"留的保险入口，见 _gripPress。
    //
    // ★ 铁律：一动就滚，静止才计时。位移一过 10px 立刻清掉长按计时器并作废本次按下的
    //   拖动 / 右键资格 —— 否则每一次想滚课表都要先僵住 0.5 秒，手感立刻崩。
    // ★ 只有一个时间点（500ms）。曾经是「0.3 秒解禁拖动 + 1 秒弹编辑窗」两个点，
    //   中间那 0.7 秒松手既没弹窗也不交换，用户只会觉得"卡住了"。
    // ★ 单击 / 双击不自己做：关掉系统 Press-and-Hold 后，触摸仍会被 WPF 提升成鼠标事件，
    //   点击次数、双击间隔全部沿用系统阈值（与鼠标完全一致），这里一概不拦。
    private const int TouchHoldMs = 500;          // 静止按满 → 浮起（长按态）
    private const double GestureSlop = 10;        // 一动就滚的临界；也是浮起后起拖的位移
    private const double EdgeScrollZone = 46;     // 拖动时离上下边缘多近触发自动滚动

    private bool _pressFromTouch;                 // 本次按下来自触摸，而非鼠标
    private bool _touchHoldArmed;                 // 已浮起（长按完成 / 抓手按下即浮起）
    private bool _gripPress;                      // 本次按下落在左列抓手上（0 秒即可拖）
    private bool _inPointerSetup;                 // 正在设置拖动状态（挡 Mouse.OverrideCursor 引发的重入）
    private DispatcherTimer? _holdArmTimer;
    private Border? _armBorder;                   // 被"拿起来"的那张卡
    private Brush? _armSavedBorderBrush;
    private Thickness _armSavedBorderThickness;
    private double _armSavedOpacity = 1;
    private System.Windows.Point _lastDragPoint;  // 最近一次拖动点（窗口坐标，边缘自动滚动用）

    /// <summary>本次触摸按下的起点（窗口坐标）。手势位移一律以它为原点 —— 触摸被提升出来的
    /// 鼠标坐标偶尔会差一两像素，混用会让"到底动没动够 10px"时准时不准。</summary>
    private System.Windows.Point _touchStartPt;

    /// <summary>本次按下已经判定为"滚动"，后续不再有任何长按 / 拖动 / 右键收尾。</summary>
    private bool _gestureScrolled;

    /// <summary>
    /// 最近一次"由触摸驱动拖动"的时刻（<see cref="Environment.TickCount64"/>，毫秒）。
    /// <para>触摸屏上手指会被系统<b>模拟成鼠标</b>再交给 WPF，但这套模拟的移动事件很不可靠：
    /// 按下与抬起一定有，中间的 move 稀疏甚至直接停掉（程序一捕获鼠标就更明显）。
    /// 所以拖动的位置必须来自触摸事件；这个时间戳用来判断"触摸还在驱动吗"，
    /// 只有在触摸已经停了 150ms 时才回退到模拟鼠标，避免两个数据源互相拉扯、卡片发抖。</para>
    /// </summary>
    private long _lastTouchDriveTick = -100000;

    // 拖动覆盖层：跟手的幽灵卡 + 原位虚框（DragLayer 在 XAML 里，命中测试已关）
    private Border? _ghost;
    private System.Windows.Shapes.Rectangle? _slotOutline;
    private System.Windows.Shapes.Rectangle? _dropMarkerL, _dropMarkerR;
    private double _ghostGrabDX, _ghostGrabDY;
    private DispatcherTimer? _edgeScrollTimer;
    private readonly List<(PeriodRow Row, Border Border)> _rowBorders = new();

    // 交换后的撤销快照（5 秒内有效）
    private string? _undoSnapshot;
    private DispatcherTimer? _actionTimer;

    /// <summary>最近一次触摸按下的时刻（<see cref="Environment.TickCount"/>），用来判断鼠标事件是否由触摸提升而来。</summary>
    private int _lastTouchDownTick;

    // 单指滑动滚动（未唤醒拖动时，手指划页面就是翻页）
    private bool _singleScroll;
    private double _singleScrollOrigin, _singleScrollStartY;

    public ScheduleWindow()
    {
        InitializeComponent();
        TodayPanel.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<PeriodRow>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Refresh();

        // 日期旁的实时时钟：每秒只更新那一个 TextBlock，不触发整窗刷新（30 秒的 _timer 只管数据）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { UpdateClockText(); CheckFlashExpiry(); };
        _clockTimer.Start();

        // 所见即所得：按住标题区（班级 / 日期那块及空白处）拖动移动窗口；
        // 鼠标移到窗口边沿 / 四角出现双向箭头时拖动改大小。都只在用户松手时落盘。
        void OnStart() { if (_hider != null) _hider.Suspended = true; }
        void OnEnd()
        {
            if (_hider != null) _hider.Suspended = false;
            SavePlacement();
            ApplyCurrentLayout();      // 按新位置重建自动隐藏的展开 / 缩回矩形
            AppEvents.RaiseLayoutChanged();   // 自动隐藏开关变化后同步隐藏按钮标签等
        }

        FloatChrome.Enable(this, canInteract: () => !_clickThrough && !_locked,
            onGestureStart: OnStart, onGestureEnd: OnEnd);
        FloatChrome.EnableDrag(HeaderBar, this,
            onDragStart: OnStart, onDragEnd: OnEnd,
            canDrag: () => !_clickThrough && !_locked);

        // 内置快捷：点击只发事件，真正打开窗口 / 检查更新由主窗口做（这里不持有它的引用）。
        AppEvents.QuickCheckUpdateDone += OnQuickCheckUpdateDone;
        AppEvents.ScheduleFlash += OnScheduleFlash;

        // 快捷胶囊「自动」档要跟窗口宽度联动：拖动缩放后立刻重排，窄窗挤不下就自动收小
        // 课表行高同理：窗口变矮（或屏幕缩放变大）后压紧字号与内边距，尽量一屏显示完当天的课
        SizeChanged += (_, _) =>
        {
            ApplyBuiltinQuickActionSize();
            QueueRowMetrics();
        };

        Closed += (_, _) =>
        {
            AppEvents.QuickCheckUpdateDone -= OnQuickCheckUpdateDone;
            AppEvents.ScheduleFlash -= OnScheduleFlash;
            AppEvents.QuickActionsChanged -= ApplyQuickActionPermissions;
            _qaWatchdog?.Stop();
            _qaWatchdog = null;
            _clockTimer?.Stop();
            _clockTimer = null;
            StopTouchScroll();          // 停掉触摸惯性计时器，别让它拽着一个已关闭的窗口
        };
    
        GlassChrome.Prepare(this);}

    // ---------- 内置快捷：作业 / 缺交 / 便签 / 倒数 / 值日 / 周课 / 发布 / 信息 / 设置 ----------

    /// <summary>动作键 → 胶囊按钮（顺序无关，只用来查）。</summary>
    private Dictionary<string, Button> QuickActionButtons() => new(StringComparer.Ordinal)
    {
        [QuickActionCatalog.Homework] = BtnQaHomework,
        [QuickActionCatalog.Missing] = BtnQaMissing,
        [QuickActionCatalog.Note] = BtnQaNote,
        [QuickActionCatalog.Countdown] = BtnQaCountdown,
        [QuickActionCatalog.Duty] = BtnQaDuty,
        [QuickActionCatalog.Week] = BtnQaWeek,
        [QuickActionCatalog.Publish] = BtnQaPublish,
        [QuickActionCatalog.Info] = BtnQaInfo,
        [QuickActionCatalog.Settings] = BtnQaSettings
    };

    /// <summary>
    /// 按「权限 + 节点角色的显示配置」决定每个胶囊显示不显示，并按角色顺序重排整排。
    /// <para>★ 隐藏而不是置灰：教室机登学生 / 科任老师时，一排灰按钮既占地方，
    /// 又容易让人以为程序出了故障。全都隐藏时整条收起，不留空白。</para>
    /// <para>★ 出厂默认与手工开关都在 <see cref="QuickActionCatalog"/> 里：电教管理员默认
    /// 作业 / 缺交 / 便签 / 倒数 / 值日 / 信息，老师与管理员默认 发布 / 便签 / 周课 / 倒数 / 设置 / 信息。</para>
    /// </summary>
    private void ApplyQuickActionPermissions()
    {
        if (BtnQaHomework == null || BtnQaInfo == null || BuiltinWrap == null) return;

        var vis = QuickActionCatalog.VisibleNow();
        var buttons = QuickActionButtons();

        // 先清空再按角色顺序加回去：WrapPanel 的 Children 顺序就是实际顺序，
        // 这样老师看到的是「发布 / 便签 / 周课 / 倒数 / 设置 / 信息」，与口头描述一致
        BuiltinWrap.Children.Clear();
        foreach (var key in QuickActionCatalog.OrderFor(UserSession.Role))
        {
            if (!buttons.TryGetValue(key, out var b) || b == null) continue;
            BuiltinWrap.Children.Add(b);

            bool show = vis.Contains(key);
            b.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            var e = QuickActionCatalog.Find(key);
            if (e != null)
                b.ToolTip = show ? e.Tip : e.Tip + "\n（已在「管理 → 应用快捷方式 → 内置快捷」里隐藏）";
        }

        // 「更新课表」在标题栏，按「管理更新」权限独立显隐（与底部快捷栏无关）
        BtnQaUpdate.Visibility = UserSession.CanManageUpdate ? Visibility.Visible : Visibility.Collapsed;

        BuiltinWrap.Visibility = vis.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _qaAppliedFont = -1;         // 新加的胶囊也要吃到当前字号档（否则会被「同档不重排」挡掉）
        ApplyBuiltinQuickActionSize();
    }

    private void BtnQaHomework_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenHomework();

    private void BtnQaMissing_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenMissing();

    private void BtnQaNote_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenCustomNote();

    private void BtnQaCountdown_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenCountdown();

    private void BtnQaDuty_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenDuty();

    private void BtnQaSettings_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenSettings();

    private void BtnQaWeek_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenWeek();

    private void BtnQaPublish_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenPublish();

    private void BtnQaInfo_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenInfoLedger();

    private void BtnQaUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_qaUpdating) return;
        SetQaUpdating(true);
        AppEvents.RaiseQuickCheckUpdate();

        // 兜底：主窗口若因故没回消息（被关掉 / 事件没接上），8 秒后自己恢复，
        // 否则按钮会永远卡在"更新中…"，看着像死了。
        _qaWatchdog?.Stop();
        _qaWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _qaWatchdog.Tick += (_, _) =>
        {
            _qaWatchdog?.Stop();
            if (_qaUpdating) SetQaUpdating(false);
        };
        _qaWatchdog.Start();
    }

    private void SetQaUpdating(bool on)
    {
        _qaUpdating = on;
        UpdateQaButtonVisual();
    }

    /// <summary>「更新课表」按钮的提示（成功 / 失败后按状态替换）。</summary>
    private const string QaUpdateTip = "立即从数据源检查并应用最新的课表 / 调代课（可反复点，不影响正在进行的数据）";

    /// <summary>
    /// 刷新「更新课表」按钮：只有「更新中」与「更新课表」两种样子。
    /// <para>失败不再把按钮染红 —— 按钮永远可点、可反复重试，失败原因放到下方状态栏滚动字幕里
    /// （见 <see cref="UpdateStatusText"/>）。</para>
    /// </summary>
    private void UpdateQaButtonVisual()
    {
        if (BtnQaUpdate == null || TxtQaUpdate == null) return;

        BtnQaUpdate.IsEnabled = !_qaUpdating;
        TxtQaUpdate.Text = _qaUpdating ? "⏳ 更新中…" : "更新课表";
        BtnQaUpdate.ToolTip = _qaFailed && !string.IsNullOrWhiteSpace(_qaFailMessage)
            ? $"上次更新失败：{_qaFailMessage}\n点一下可立刻重试（状态栏也有红色提示）"
            : QaUpdateTip;
    }

    /// <summary>
    /// 「更新课表」执行完毕：成功时按钮上短暂显示「✔ 完成」，4 秒后复原；
    /// 失败则按钮**立刻回到「更新课表」**（可马上再试），失败原因写进状态栏红字字幕。
    /// <para>不弹框 —— 悬浮窗通常就贴在教室屏幕边上，弹框会挡住课表。</para>
    /// </summary>
    private void OnQuickCheckUpdateDone(bool ok, string message)
    {
        _qaWatchdog?.Stop();
        _qaUpdating = false;
        _qaFailed = !ok;
        _qaFailMessage = ok ? "" : OneLine(message);
        SaveQaFailureState(ok, _qaFailMessage);

        UpdateQaButtonVisual();
        UpdateStatusText();     // 成功 → 红字消失；失败 → 红字开始滚动

        if (!ok) return;        // 失败：按钮已是「更新课表」，不需要任何延时复原

        // 「已是最新版本」：状态栏用红字提示 10 秒，让他知道"点了、查过了、没新东西"，
        // 而不是看着按钮闪一下就弹回去以为没反应。
        if (!string.IsNullOrEmpty(message) && message.Contains("已是最新"))
            ShowUpToDateHint();

        TxtQaUpdate.Text = "✔ 完成";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_qaUpdating) UpdateQaButtonVisual();
        };
        t.Start();
    }

    /// <summary>状态栏红字提示「已是最新版本（检查时刻）」，10 秒后自动消失。</summary>
    private void ShowUpToDateHint()
    {
        _upToDateAt = DateTime.Now;
        _upToDateUntil = _upToDateAt.AddSeconds(10);
        UpdateStatusText();

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _upToDateUntil = default;
            UpdateStatusText();
        };
        t.Start();
    }

    /// <summary>诊断用：直接触发一次「已是最新版本」提示（截图核对红字与排版）。</summary>
    internal void SimulateUpToDate() => ShowUpToDateHint();

    /// <summary>把多行 / 超长的失败信息压成一行（状态栏只有一行高），过长就截断。</summary>
    private static string OneLine(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string one = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                             .Select(x => x.Trim())
                                             .Where(x => x.Length > 0));
        if (one.Length > max) one = one[..max] + "…";
        return one;
    }

    /// <summary>
    /// 诊断专用：把状态栏切到"上次更新失败"的样子，供 <c>--shot schedulewinfail</c> 截图核对红字。
    /// 正常运行时由 <see cref="OnQuickCheckUpdateDone"/> 在真失败时调用同一段逻辑。
    /// </summary>
    internal void SimulateQaFailure(string message) => OnQuickCheckUpdateDone(false, message);

    /// <summary>把「更新失败」状态（含原因）记进设置：关掉再打开，状态栏红字还在，直到下一次成功。</summary>
    private static void SaveQaFailureState(bool ok, string message)
    {
        try
        {
            var s = Store.LoadSettings();
            s.QaUpdateFailed = !ok;
            s.QaUpdateFailedAt = ok ? "" : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            s.QaUpdateFailedMessage = ok ? "" : message;
            Store.SaveSettings(s);
        }
        catch { }
    }

    /// <summary>启动时读回「上次更新失败」状态与原因。</summary>
    private void LoadQaFailureState()
    {
        try
        {
            var s = Store.LoadSettings();
            _qaFailed = s.QaUpdateFailed && !AppliedAfterFailure(s);
            _qaFailMessage = _qaFailed ? OneLine(s.QaUpdateFailedMessage) : "";
            if (_qaFailed && string.IsNullOrWhiteSpace(_qaFailMessage)) _qaFailMessage = "未返回具体原因";
        }
        catch { _qaFailed = false; _qaFailMessage = ""; }
    }

    /// <summary>
    /// 失败之后若已经成功应用过新数据（例如后台自动更新跑成功了），把红字清掉 ——
    /// 否则会出现"课表早就更新好了、状态栏还红着"的错觉。返回是否清掉了。
    /// </summary>
    private bool SyncQaFailure(AppSettings s)
    {
        if (!_qaFailed) return false;
        if (!AppliedAfterFailure(s)) return false;

        s.QaUpdateFailed = false;
        s.QaUpdateFailedAt = "";
        s.QaUpdateFailedMessage = "";
        try { Store.SaveSettings(s); } catch { }
        _qaFailed = false;
        _qaFailMessage = "";
        return true;
    }

    /// <summary>「最后一次成功应用数据」是否晚于「上次更新失败」的时刻。</summary>
    private static bool AppliedAfterFailure(AppSettings s)
        => DateTime.TryParse(s.QaUpdateFailedAt, out var failed)
           && DateTime.TryParse(s.LastAppliedAt, out var applied)
           && applied > failed;

    /// <summary>
    /// 记住用户拖动 / 缩放后的位置与大小：写入布局的自由坐标，并把锚点更新为最近的屏幕边
    /// （自动靠边隐藏就往那条边滑出）。之后 ApplyLayout 会直接用这组坐标，不再贴边重排。
    /// </summary>
    private void SavePlacement()
    {
        if (_applyingLayout) return;
        try
        {
            var s = Store.LoadSettings();
            var l = s.ScheduleLayout ??= new PanelLayout();
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            // 自动隐藏态窗口在屏幕外：此刻的坐标不代表用户意图，不保存
            if (!FloatingHelper.IsMostlyOnScreen(this, 0.3)) return;

            var clamped = FloatingHelper.ClampRectDip(this, new Rect(Left, Top, Width, Height));

            // 仅当窗口位置确实发生移动时才据「是否贴边」自动切换自动隐藏，
            // 避免单纯改大小（位置不变）时误触发。
            bool moved = !l.Left.HasValue || !l.Top.HasValue
                || Math.Abs(l.Left.Value - clamped.X) > 8
                || Math.Abs(l.Top.Value - clamped.Y) > 8;
            if (moved)
            {
                bool near = FloatingHelper.IsNearEdge(this);
                l.AutoHide = near;
                if (near) l.ClickThrough = false;   // 自动隐藏与鼠标穿透互斥
            }

            l.Left = clamped.X;
            l.Top = clamped.Y;
            l.Width = clamped.Width;
            l.Height = clamped.Height;
            l.Anchor = FloatingHelper.NearestAnchor(this);
            Store.SaveSettings(s);

            if (_layout != null)
            {
                _layout.Left = l.Left; _layout.Top = l.Top;
                _layout.Width = l.Width; _layout.Height = l.Height;
                _layout.Anchor = l.Anchor;
                _layout.AutoHide = l.AutoHide;
                _layout.ClickThrough = l.ClickThrough;
            }
        }
        catch { }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCurrentLayout();
        _locked = Store.LoadSettings().ScheduleLayout?.Locked ?? false;
        UpdateLockButton();
        UpdateTopButton();
        LoadQaFailureState();       // 上次更新失败的话，状态栏继续显示红字提示（按钮不受影响）

        AppEvents.ScheduleChanged += Refresh;
        AppEvents.QuickActionsChanged += ApplyQuickActionPermissions;
        AppEvents.LayoutChanged += OnLayoutChanged;
        AppEvents.BackdropChanged += UpdateBackdropButton;
        Refresh();
        UpdateBackdropButton();
        UpdateQaButtonVisual();
        LoadQuickLaunchers();
        ApplyQuickActionPermissions();
        _timer.Start();
    }

    /// <summary>
    /// 同步「设为背景 / 还原背景」按钮与状态栏：
    /// 已在壁纸上时按钮变成「还原背景」（蓝底），点一下撤掉壁纸并把悬浮窗放回来；
    /// 否则是「设为背景」，按当前大小位置一键锁到壁纸。
    /// </summary>
    private void UpdateBackdropButton()
    {
        try
        {
            if (BtnWp == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            bool on = b.ShowSchedule;
            BtnWp.Content = on ? "还原背景" : "设为背景";
            BtnWp.Background = on
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3))
                : new SolidColorBrush(Color.FromRgb(0xE9, 0xEF, 0xF9));
            BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
            BtnWp.ToolTip = on
                ? "当天课表已画在桌面背景上；点「还原背景」可撤掉壁纸内容、恢复系统壁纸，并把悬浮课表放回屏幕"
                : "按「现在的位置和大小」一键设为桌面背景，并把本悬浮课表改为靠边自动隐藏";
            if (TxtWpState != null)
            {
                TxtWpState.Text = on ? "背景：已设" : "背景：未设";
                TxtWpState.ToolTip = on
                    ? "当天课表已在桌面背景显示（壁纸层，不遮挡操作）；点右下「还原背景」可取消"
                    : "点右下「设为背景」可按当前大小位置一键画到桌面壁纸";
            }
        }
        catch { }
    }

    /// <summary>
    /// 「设为背景 / 还原背景」同一个按钮来回切换：
    /// 设置时把当前的位置与大小锁进桌面背景排版 → 重绘壁纸 → 本悬浮课表改为靠边自动隐藏；
    /// 还原时撤掉壁纸内容、恢复系统壁纸，并把当初被藏掉的悬浮窗（含本窗口）放回屏幕。
    /// </summary>
    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        bool onWallpaper = Store.LoadSettings().Backdrop?.ShowSchedule == true;

        if (onWallpaper)
        {
            if (TxtWpState != null) TxtWpState.Text = "背景：还原中…";
            bool done = BackdropPin.Unpin(out string offMsg);
            UpdateBackdropButton();
            if (TxtWpState != null) TxtWpState.ToolTip = offMsg;
            if (!done) return;   // 没设过背景 / 失败：状态栏已说明原因，悬浮窗保持原样
            return;
        }

        if (!BackdropPin.Pin(BackdropPin.Kind.Schedule, this, out string msg))
        {
            // 失败（如悬浮窗在副屏）时不改动任何状态，只在状态栏说明原因（鼠标悬停看全文）
            if (TxtWpState != null) { TxtWpState.Text = "背景：设置失败"; TxtWpState.ToolTip = msg; }
            return;
        }

        // 已经画到壁纸上了，悬浮课表不必再常驻：改为靠边自动隐藏（鼠标移到屏幕边沿滑出）
        var s = Store.LoadSettings();
        var l = s.ScheduleLayout ??= new PanelLayout();
        l.AutoHide = true;
        l.ClickThrough = false;        // 自动隐藏与鼠标穿透互斥
        Store.SaveSettings(s);

        ApplyCurrentLayout();          // 按新状态重建自动隐藏的展开 / 缩回矩形
        AppEvents.RaiseLayoutChanged();
        UpdateBackdropButton();
        if (TxtWpState != null) TxtWpState.ToolTip = msg + "　悬浮课表已改为靠边自动隐藏（需要还原时点右下「还原背景」）";
    }

    // ---------- 状态栏滚动字幕 ----------

    /// <summary>字幕常态色（与原来状态栏一致的低调灰）。</summary>
    private static readonly Brush MarqueeNormalBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));

    /// <summary>更新失败时的红字（#C62828 与告警文字同一档，白底上足够醒目）。</summary>
    private static readonly Brush MarqueeFailBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

    /// <summary>上一次排进字幕的内容。内容没变就不重排，避免 30 秒一次的定时刷新把滚动打断。</summary>
    private string _marqueeSig = "";

    /// <summary>收到「课表有变化」：挂上红字并立即重排字幕。</summary>
    private void OnScheduleFlash(string summary, int minutes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(summary)) return;
            _flashText = summary.Trim();
            _flashUntil = DateTime.Now.AddMinutes(Math.Max(1, minutes));
            UpdateStatusText();
        }
        catch { }
    }

    /// <summary>诊断用：模拟一次「课表有变化」，验证状态栏红字排版。</summary>
    internal void SimulateChangeFlash(string summary, int minutes) => OnScheduleFlash(summary, minutes);

    /// <summary>变化红字到期后自动撤掉（挂在每秒的时钟定时器上，几乎不花代价）。</summary>
    private void CheckFlashExpiry()
    {
        if (_flashText.Length == 0 || DateTime.Now < _flashUntil) return;
        _flashText = "";
        UpdateStatusText();
    }

    /// <summary>
    /// 刷新状态栏字幕：把「课表更新时间」和「上次更新失败的原因」拼成一条，超宽就滚动显示。
    /// <para>失败时红字排在<b>最前面</b> —— 状态栏一般只露出开头十来个字，把要紧的放前面才看得见。</para>
    /// </summary>
    private void UpdateStatusText()
    {
        if (TxtMarquee == null) return;
        try
        {
            var at = Store.LoadSettings().LastAppliedAt;
            string timeText = string.IsNullOrWhiteSpace(at) ? "课表尚未同步更新" : "课表更新于：" + at;
            string failText = _qaFailed ? "更新失败：" + _qaFailMessage : "";

            // 「已是最新版本」只在点完更新后的 10 秒内显示（红字，与失败同色系）
            bool upToDate = _upToDateUntil > DateTime.Now;
            string upText = upToDate ? $"已是最新版本（{_upToDateAt:HH:mm:ss}）" : "";

            // 「课表有变化」：由变化监视器触发，默认挂 10 分钟（提醒设置里可改）
            string flashText = _flashUntil > DateTime.Now && _flashText.Length > 0
                ? "课表有变化：" + _flashText
                : "";

            string sig = timeText + "|" + failText + "|" + flashText + "|" + upText;
            if (sig == _marqueeSig) return;      // 内容没变：保留正在滚动的动画，不打断
            _marqueeSig = sig;

            TxtMarquee.Inlines.Clear();
            if (failText.Length > 0)
            {
                TxtMarquee.Inlines.Add(new Run("⚠ " + failText)
                {
                    Foreground = MarqueeFailBrush,
                    FontWeight = FontWeights.SemiBold
                });
                TxtMarquee.Inlines.Add(new Run("（点右上「更新课表」可重试）") { Foreground = MarqueeFailBrush });
                TxtMarquee.Inlines.Add(new Run("　·　") { Foreground = MarqueeNormalBrush });
            }
            if (flashText.Length > 0)
            {
                TxtMarquee.Inlines.Add(new Run("● " + flashText)
                {
                    Foreground = MarqueeFailBrush,
                    FontWeight = FontWeights.SemiBold
                });
                TxtMarquee.Inlines.Add(new Run("（详情见提醒弹窗）") { Foreground = MarqueeFailBrush });
                TxtMarquee.Inlines.Add(new Run("　·　") { Foreground = MarqueeNormalBrush });
            }
            if (upText.Length > 0)
            {
                TxtMarquee.Inlines.Add(new Run("✓ " + upText)
                {
                    Foreground = MarqueeFailBrush,
                    FontWeight = FontWeights.SemiBold
                });
                TxtMarquee.Inlines.Add(new Run("　·　") { Foreground = MarqueeNormalBrush });
            }
            TxtMarquee.Inlines.Add(new Run(timeText) { Foreground = MarqueeNormalBrush });

            StartMarquee();
        }
        catch { }
    }

    /// <summary>
    /// 排出字幕动画：文字比状态栏窄就静静左对齐显示（短句来回滚反而难读）；
    /// 放不下就以每秒约 46 像素匀速滚过，开头先停 3 秒让人看清最前面几个字。
    /// </summary>
    private void StartMarquee()
    {
        if (TxtMarquee == null || MarqueeView == null) return;

        var shift = TxtMarquee.RenderTransform as TranslateTransform;
        if (shift == null)
        {
            shift = new TranslateTransform();
            TxtMarquee.RenderTransform = shift;
        }
        _marqueeShift = shift;

        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = 0;

        // Canvas 里的 TextBlock 不受可用宽度限制，量出来就是整句的真实宽度
        TxtMarquee.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = TxtMarquee.DesiredSize.Width;
        double viewW = MarqueeView.ActualWidth;

        if (viewW < 8 || textW <= viewW - 2) return;   // 放得下：静态显示

        const double speed = 46;    // 每秒滚动像素（约 46 时中文一行看得舒服）
        const double lead = 3;      // 开头停顿
        const double tail = 0.6;    // 滚完停顿再回到开头
        const double extend = 16;   // 末尾多滚一点，让最后一个字也彻底移出

        double scroll = (textW + extend) / speed;

        var kf = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(lead))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(-(textW + extend), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(lead + scroll))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(-(textW + extend), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(lead + scroll + tail))));
        shift.BeginAnimation(TranslateTransform.XProperty, kf);
    }

    /// <summary>状态栏宽度变了：重新判断"要不要滚"（宽度没变就不管，免得 Height 变化也重排字幕）。</summary>
    private void MarqueeView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;
        // 布局过程中量文字容易和排版互相踩，推到本轮布局之后再做
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartMarquee));
    }

    // ---------- 常用快捷（悬浮课表底部应用栏：图标按钮，支持仅图标 + 可调大小） ----------
    private void LoadQuickLaunchers()
    {
        var s = Store.LoadSettings();
        _ql = new System.Collections.ObjectModel.ObservableCollection<QuickLaunchItem>(
            s.QuickLaunchers ?? new System.Collections.Generic.List<QuickLaunchItem>());
        _qlIconOnly = s.QlIconOnly;
        _qlIconSize = s.QlIconSize > 0 ? s.QlIconSize : 28;
        _qaFontSetting = s.QaBuiltinFontSize;
        RenderQuickLaunchers();
        ApplyBuiltinQuickActionSize();
    }

    /// <summary>
    /// 按设置调整底部内置快捷（作业 / 缺交 / 便签 / 倒计时 / 值日 / 设置）胶囊的大小。
    /// <para>六个共用同一档字号：它们是一整排，大小不一致会一眼看出「后加的三个没跟上」。</para>
    /// <para>设为「自动」时跟着悬浮课表宽度走：窄窗不挤成一团、宽窗也不显得太小。
    /// 设置值缓存在 <see cref="_qaFontSetting"/>，拖动缩放期间不会反复读设置文件。</para>
    /// </summary>
    private void ApplyBuiltinQuickActionSize()
    {
        if (BtnQaHomework == null || BtnQaMissing == null || BtnQaNote == null) return;

        double w = ActualWidth > 0 ? ActualWidth : 420;
        double fs = QuickActionStyle.Resolve(_qaFontSetting, w);
        if (Math.Abs(fs - _qaAppliedFont) < 0.05) return;   // 同一档，避免每次 SizeChanged 都重排
        _qaAppliedFont = fs;

        QuickActionStyle.Apply(BtnQaHomework, fs);
        QuickActionStyle.Apply(BtnQaMissing, fs);
        QuickActionStyle.Apply(BtnQaNote, fs);
        QuickActionStyle.Apply(BtnQaCountdown, fs);
        QuickActionStyle.Apply(BtnQaDuty, fs);
        QuickActionStyle.Apply(BtnQaWeek, fs);
        QuickActionStyle.Apply(BtnQaPublish, fs);
        QuickActionStyle.Apply(BtnQaInfo, fs);
        QuickActionStyle.Apply(BtnQaSettings, fs);
    }

    // ---------- 课表行尺寸自适应（窗口高度变化时压紧 / 放大，尽量一屏显示完当天的课） ----------

    /// <summary>上一次应用的卡片高度 / 科目字号，用来避免每次 SizeChanged 都重排整屏。</summary>
    private double _rowCardH = -1, _rowSubjectSize = -1;

    /// <summary>已经排了一次「延后计算」，避免连续 SizeChanged 排上一堆。</summary>
    private bool _rowMetricsPending;

    /// <summary>窗口尺寸变了：等本轮布局完成再量可用高度，否则拿到的是上一轮的 ViewportHeight。</summary>
    private void QueueRowMetrics()
    {
        if (_rowMetricsPending) return;
        _rowMetricsPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _rowMetricsPending = false;
            ApplyRowMetrics();
        }));
    }

    /// <summary>
    /// 按窗口可用高度给「每节课」重新分高度，并让字号跟着卡片高度走。
    /// <para><b>为什么要有这个：</b>原来每行固定约 76 DIP（内边距 10 + 行间距 8 + 19 号科目）。
    /// 100% 缩放下窗口能拉到 1000 DIP 高，8 节加固定行刚够；屏幕设成 125% 后逻辑画布只剩
    /// 约 830 DIP，同一份内容就装不进去，用户只能拉着滚动条一节一节往下看。</para>
    /// <para><b>做法：</b>把可用高度按「课节数」均分，高度够就放大行高与字号（最大 20 号），
    /// 不够就整体压紧（最小 12 号）。目标是"一屏尽量显示完当天的课"，
    /// 而不是"字号死不变、剩下的靠滚"。</para>
    /// <para>只改视觉尺寸，不碰任何数据：调代课、拖动换节、点行切换教师都不受影响。</para>
    /// </summary>
    private void ApplyRowMetrics()
    {
        if (ScheduleScroll == null || TodayPanel == null) return;

        double avail = ScheduleScroll.ViewportHeight;
        if (avail <= 1) avail = ScheduleScroll.ActualHeight - 20;   // 还没量出 Viewport 时的近似
        if (avail <= 1) return;

        int lessons = 0, fixedRows = 0;
        if (TodayPanel.ItemsSource is System.Collections.IEnumerable src)
        {
            foreach (var o in src)
            {
                if (o is not PeriodRow r) continue;
                if (r.IsFixed) fixedRows++; else lessons++;
            }
        }
        if (lessons <= 0) return;

        // 固定横条（大课间 / 眼保健操 / 午休）不参与均分：13 号单行 + 上下内边距 + 下间距 ≈ 31 DIP
        double per = Math.Max(30, (avail - fixedRows * 32) / lessons);

        double gap     = Math.Clamp(per * 0.10, 4, 8);          // 卡片之间的缝
        double cardH   = Math.Clamp(per - gap, 42, 116);        // 卡片本体高度（不含缝）
        double padding = Math.Clamp(cardH * 0.13, 4, 10);
        // 卡内两行文字合计 ≈ 内边距*2 + 科目行(字号*1.33) + 教师行(字号*0.68*1.33) + 上下留白 4
        double subject = Math.Clamp((cardH - padding * 2 - 4) / 2.234, 12, 20);
        double teacher = Math.Clamp(subject * 0.68, 9.5, 14);
        double period  = Math.Clamp(subject * 0.95, 11.5, 19);
        double weekTag = Math.Clamp(subject * 0.63, 9, 13);

        // 变化太小就别动：改 DynamicResource 会重排整屏，拖窗口时容易抖
        if (Math.Abs(cardH - _rowCardH) < 0.5 && Math.Abs(subject - _rowSubjectSize) < 0.05) return;
        _rowCardH = cardH;
        _rowSubjectSize = subject;

        Resources["LessonRowMinHeight"] = cardH;
        Resources["LessonCardPadding"]   = new Thickness(padding);
        Resources["LessonCardMargin"]    = new Thickness(0, 0, 0, gap);
        Resources["LessonSubjectSize"]   = subject;
        Resources["LessonTeacherSize"]   = teacher;
        Resources["LessonPeriodSize"]    = period;
        Resources["LessonWeekTagSize"]   = weekTag;
    }

    /// <summary>按当前「仅图标 / 图标大小」把快捷方式渲染成底部应用栏（图标按钮 + 移除按钮）。</summary>
    private void RenderQuickLaunchers()
    {
        if (QlWrap == null) return;
        QlWrap.Children.Clear();
        if (TxtQlEmpty != null)
            TxtQlEmpty.Visibility = _ql.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var icon = (QlIconConverter)Resources["QlIcon"];
        double size = _qlIconSize;
        foreach (var item in _ql)
        {
            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE6, 0xEE)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var panel = new DockPanel { LastChildFill = false };

            var open = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3, 6, 3),
                Tag = item,
                ToolTip = item.Path
            };
            open.Click += QlItem_Click;

            var img = new Image
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, _qlIconOnly ? 0 : 5, 0),
                Source = icon.Convert(item.Path, typeof(System.Windows.Media.ImageSource), null, null) as System.Windows.Media.ImageSource
            };
            if (_qlIconOnly)
            {
                open.Content = img;
            }
            else
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(img);
                sp.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                open.Content = sp;
            }
            DockPanel.SetDock(open, Dock.Left);
            panel.Children.Add(open);

            var rm = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = "×",
                FontSize = 13,
                Width = 18,
                Height = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)),
                Tag = item,
                ToolTip = "移除"
            };
            rm.Click += QlRemove_Click;
            DockPanel.SetDock(rm, Dock.Right);
            panel.Children.Add(rm);

            border.Child = panel;
            QlWrap.Children.Add(border);
        }
    }

    private void UpdateQlEmpty()
    {
        if (TxtQlEmpty != null)
            TxtQlEmpty.Visibility = _ql.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnAddQl_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "程序与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            Title = "选择要添加的软件"
        };
        if (dlg.ShowDialog() == true) AddLauncher(dlg.FileName, false);
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加的文件夹" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddLauncher(dlg.SelectedPath, true);
    }

    private void AddLauncher(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var name = isFolder
            ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))
            : System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) name = path;
        _ql.Add(new QuickLaunchItem { Name = name, Path = path, IsFolder = isFolder });
        SaveLaunchers();
        RenderQuickLaunchers();
    }

    private void QlItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is QuickLaunchItem item && !string.IsNullOrWhiteSpace(item.Path))
        {
            try
            {
                if (item.IsFolder)
                    System.Diagnostics.Process.Start("explorer.exe", item.Path);
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("无法打开：" + item.Path + "\n" + ex.Message,
                    "常用快捷", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void QlRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is QuickLaunchItem item)
        {
            _ql.Remove(item);
            SaveLaunchers();
            RenderQuickLaunchers();
        }
    }

    private void BtnQlManage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new QuickLaunchWindow { Owner = this, Topmost = true };
            if (dlg.ShowDialog() == true) LoadQuickLaunchers();
        }
        catch { }
    }

    private void SaveLaunchers()
    {
        try
        {
            var s = Store.LoadSettings();
            s.QuickLaunchers = new System.Collections.Generic.List<QuickLaunchItem>(_ql);
            Store.SaveSettings(s);
        }
        catch { }
    }

    private void ApplyCurrentLayout(PanelLayout? live = null)
    {
        var l = live ?? Store.LoadSettings().ScheduleLayout;
        _layout = l;
        // 「常驻显示」= 默认不透明常驻且可交互（不靠边自动隐藏）；只有在布局里显式勾选「鼠标穿透」时才穿透，
        // 且穿透时隐藏按钮、标题区、课表区仍保持可点击。
        bool topShow = !l.AutoHide;
        _clickThrough = l.ClickThrough && !l.AutoHide;
        _applyingLayout = true;
        try
        {
            FloatingHelper.ApplyLayout(this, l, _clickThrough, topShow ? l.OverlayOpacity : l.Opacity);
        }
        finally { _applyingLayout = false; }
        if (_clickThrough)
            FloatingHelper.SetClickThrough(this, true, BtnHide, BtnQaUpdate, BtnWp, BtnTop, BtnLock, BtnPos, ClassHeader, DateHeader, TodayPanel);
        else
            FloatingHelper.SetClickThrough(this, false);

        BtnHide.Content = l.AutoHide ? "常驻显示" : "自动隐藏";

        _hider?.Dispose();
        _hider = (l.AutoHide || l.AutoHideDuringClass)
            ? new AutoHider(this, l, () => l.AutoHide || (l.AutoHideDuringClass && IsClassTimeNow()))
            : null;
        ApplyGlass();
    }

    /// <summary>IGlassSurface：只改根 Border 底色，不动 Window.Opacity / DragLayer / 触屏结构。</summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
        => GlassBrush.Apply(this.Content as System.Windows.Controls.Border, settings);

    private void OnLayoutChanged(PanelLayout? schedule, PanelLayout? notification)
    {
        ApplyCurrentLayout(schedule);
        Refresh();
    }

    private bool IsClassTimeNow() => WeekHelper.IsClassTimeNow(_schedule, _selectedName);

    private void Refresh()
    {
        var s = Store.LoadSettings();
        // 触屏滚动方式可能刚在控制面板里改过：每次刷新都对齐一次（属性本身会判断同值不重设）
        ApplyTouchScrollMode(s.TwoFingerScroll);
        // 状态栏的红色「更新失败」只该留到下一次成功：若别的途径（自动更新）已经刷新过数据，这里顺手清掉
        SyncQaFailure(s);
        _semesterStart = s.SemesterStartMonday;
        _isTeacherMode = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);

        if (_isTeacherMode)
        {
            _schedule = Store.LoadTeacherSchedule();
            // 尚未整理出教师个人课表时自动退回班级课表，避免悬浮窗空白
            if (_schedule == null || _schedule.Classes.Count == 0)
            {
                _isTeacherMode = false;
                _schedule = Store.LoadSchedule();
                _selectedName = PickName(s.SelectedClass, _schedule, s, teacher: false);
            }
            else
            {
                _selectedName = PickName(s.SelectedTeacher, _schedule, s, teacher: true);
            }
        }
        else
        {
            _schedule = Store.LoadSchedule();
            _selectedName = PickName(s.SelectedClass, _schedule, s, teacher: false);
        }
        s.SemesterStartMonday = _semesterStart;

        var displayDate = BoardBuilder.ResolveDisplayDate(s);

        // 教师个人课表：把班级层面的调课/代课记录（含临时课表导入的）换算到这位教师身上，
        // 使悬浮课表优先显示临时课表的最新安排
        List<LessonOverride>? viewOverrides = null;
        if (_isTeacherMode)
            viewOverrides = TeacherOverrideApplier.BuildTeacherOverrides(_schedule, _selectedName, Store.LoadOverrides());

        // 单双周：默认只显示本周要上的那节（更简洁），可在显示设置里改回"两周都列出来（灰色）"
        var res = BoardBuilder.Build(_selectedName, _semesterStart, _schedule, displayDate, viewOverrides,
                                     showAlternateWeek: s.ShowAlternateWeekLessons);

        TxtClass.Text = string.IsNullOrEmpty(res.ClassName)
            ? (_isTeacherMode ? "未选择教师" : "未选择班级")
            : res.ClassName;
        TxtDate.Text = res.DateText;
        TxtWeek.Text = res.WeekLine;
        ApplyClockMode(s.ScheduleClockMode);

        ClassHeader.ToolTip = _isTeacherMode
            ? "双击此处可切换到其他教师，或改看某个班的课表"
            : "双击此处可切换到其他班级，或改看某位教师的课表";

        string prefix = _isTeacherMode ? "班级：" : "教师：";
        var rows = (System.Collections.ObjectModel.ObservableCollection<PeriodRow>)TodayPanel.ItemsSource;
        rows.Clear();
        foreach (var r in res.Rows)
        {
            foreach (var lv in r.Lessons) lv.TeacherPrefix = prefix;
            rows.Add(r);
        }

        UpdateQlEmpty();
        UpdateStatusText();

        // 行数变了（换了日期 / 班级 / 单双周显示方式），每节可用的高度也跟着变。
        // 这里同步算一次而不是排队：--shot 截图是 Refresh 后立刻取渲染结果，排队会来不及。
        ApplyRowMetrics();
    }

    /// <summary>
    /// 应用日期旁的时钟显示方式：0 = 不显示，1 = 时:分，2 = 时:分:秒。
    /// 关闭时整个 TextBlock 折叠，日期行不会多出空白宽度。
    /// </summary>
    private void ApplyClockMode(int mode)
    {
        _clockMode = Math.Clamp(mode, 0, 2);
        if (TxtClock == null) return;
        TxtClock.Visibility = _clockMode == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateClockText();
    }

    /// <summary>刷新时钟文字（每秒一次）。窗口不可见时直接跳过，不白算。</summary>
    private void UpdateClockText()
    {
        if (TxtClock == null || _clockMode == 0) return;
        if (!IsVisible) return;
        TxtClock.Text = DateTime.Now.ToString(_clockMode == 1 ? "HH:mm" : "HH:mm:ss");
    }

    /// <summary>确保有有效的显示对象；原选中项不存在时自动取第一个并回写设置。</summary>
    private static string PickName(string? current, ScheduleFile? sf, AppSettings s, bool teacher)    {
        var names = sf?.Classes.Select(c => c.ClassName).Where(n => !string.IsNullOrEmpty(n)).ToList()
                    ?? new List<string>();
        if (!string.IsNullOrEmpty(current) && names.Contains(current)) return current!;
        if (names.Count > 0)
        {
            string first = names[0];
            if (teacher) s.SelectedTeacher = first; else s.SelectedClass = first;
            Store.SaveSettings(s);
            return first;
        }
        return "";
    }

    // ---------- 日期区双击：切换显示日期 / 回到今天 ----------
    private void DateHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            OpenDateSwitch();
            e.Handled = true;
        }
    }

    // ---------- 班级 / 教师区双击：统一切换窗口 ----------
    private void ClassHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            OpenTargetSwitch();
            e.Handled = true;
        }
    }

    private void OpenTargetSwitch()
    {
        var w = new ScheduleTargetWindow { Owner = this, Topmost = true };
        if (w.ShowDialog() == true) AppEvents.RaiseScheduleChanged();
    }

    // ---------- 双击课卡「教师：XX」/「班级：XX」行：互切班级课表 / 教师个人课表 ----------
    private void SwitchTargetFromCard(string name)
    {
        if (string.IsNullOrEmpty(name)) return;

        // 班级课表 → 教师：确认该教师有个人课表数据
        if (!_isTeacherMode)
        {
            var ts = Store.LoadTeacherSchedule();
            if (ts == null || !ts.Classes.Any(c => c.ClassName == name)) return;
        }

        var s = Store.LoadSettings();
        if (_isTeacherMode) { s.ScheduleKind = "Class"; s.SelectedClass = name; }
        else { s.ScheduleKind = "Teacher"; s.SelectedTeacher = name; }
        Store.SaveSettings(s);
        AppEvents.RaiseScheduleChanged();   // 本窗与周视图、控制面板下拉都会同步刷新
    }

    /// <summary>从命中元素向上查找课卡「教师 / 班级行」（Tag=TeacherTag）。</summary>
    private static FrameworkElement? FindTeacherTagElement(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is FrameworkElement fe && "TeacherTag".Equals(fe.Tag as string, StringComparison.Ordinal))
                return fe;
            var next = LogicalTreeHelper.GetParent(dep);
            if (next == null && dep is System.Windows.Media.Visual v)
                next = VisualTreeHelper.GetParent(v);
            dep = next;
        }
        return null;
    }

    private void OpenDateSwitch()
    {
        var s = Store.LoadSettings();
        var cur = BoardBuilder.ResolveDisplayDate(s);
        var w = new DateSwitchWindow(cur) { Owner = this, Topmost = true };
        w.ShowDialog();
    }

    // ---------- 隐藏按钮：快速切换自动靠边隐藏 / 常驻显示 ----------
    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        bool turnOn = !s.ScheduleLayout.AutoHide;
        s.ScheduleLayout.AutoHide = turnOn;
        if (turnOn) s.ScheduleLayout.ClickThrough = false;   // 自动隐藏与鼠标穿透互斥
        Store.SaveSettings(s);
        ApplyCurrentLayout();
        AppEvents.RaiseLayoutChanged();
    }

    // ---------- 锁定 ----------
    private void UpdateLockButton()
    {
        if (BtnLock == null) return;
        BtnLock.Content = _locked ? "🔒" : "🔓";
        BtnLock.Background = _locked ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : new SolidColorBrush(Color.FromRgb(0xE9, 0xEF, 0xF9));
        BtnLock.Foreground = _locked ? Brushes.White : Brushes.Black;
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e)
    {
        _locked = !_locked;
        try
        {
            var s = Store.LoadSettings();
            var l = s.ScheduleLayout ??= new PanelLayout();
            l.Locked = _locked;
            Store.SaveSettings(s);
        }
        catch { }
        UpdateLockButton();
    }

    // ---------- 置顶（始终置顶 / 普通层级，与便签「置顶」同风格同功能） ----------
    private void UpdateTopButton()
    {
        if (BtnTop == null) return;
        bool top = Topmost;
        BtnTop.Content = top ? "已置顶" : "置顶";
        BtnTop.Background = top ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : Brushes.White;
        BtnTop.Foreground = top ? Brushes.White : Brushes.Black;
        BtnTop.BorderBrush = top ? new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)) : new SolidColorBrush(Color.FromRgb(0xC7, 0xCF, 0xDA));
    }

    private void BtnTop_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        var l = s.ScheduleLayout ??= new PanelLayout();
        l.TopMost = !l.TopMost;
        Store.SaveSettings(s);
        Topmost = l.TopMost;   // 直接切换 Z 序，无需重排布局（避免已拖动的位置被重新贴边）
        UpdateTopButton();
    }

    // ---------- 位置（桌面背景所见即所得排版） ----------
    private void BtnPos_Click(object sender, RoutedEventArgs e)
    {
        try { new BackdropEditorWindow { Owner = this }.ShowDialog(); }
        catch { }
        AppEvents.RaiseBackdropChanged();
    }

    // ---------- 长按 / 右键 弹出调课编辑（仅班级课表可编辑） ----------
    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isTeacherMode) return;
        if (_clickThrough) return;
        if (_locked) return;   // 锁定后禁止编辑
        var row = FindPeriodRow(e.OriginalSource as DependencyObject);
        if (row != null) { OpenEditor(row); e.Handled = true; }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;

        // 抓手上的触摸按下已经在 TouchDown 里就地浮起了（为了赶在手指移动之前，见那里注释）。
        // 这个"提升出来的" MouseDown 会晚到几十毫秒，绝不能再走一遍初始化 —— 那会把刚浮起的
        // 幽灵卡 StopHoldArm 掉，表现为"按下去一闪就没了"。
        if (_gripPress && _touchHoldArmed && _pressFromTouch) return;

        // 双击课卡里的「教师：XX」/「班级：XX」行 → 在班级课表与教师个人课表间互切。
        // 必须在按下处理最前面判断：该区域不做拖动候选、也不启动长按编辑。
        if (e.ClickCount == 2)
        {
            var el = FindTeacherTagElement(e.OriginalSource as DependencyObject);
            if (el != null)
            {
                CancelPress();
                if (el.DataContext is LessonView lv && !string.IsNullOrEmpty(lv.Teacher))
                    SwitchTargetFromCard(lv.Teacher);
                e.Handled = true;
                return;
            }
        }

        if (_isTeacherMode || _clickThrough || _locked)
        {
            _pressRow = null;
            _dragCandidate = null;
            _gripPress = false;
            StopHoldArm();
            return;
        }

        // 注意：必须用局部变量保存——StartPressTimer() 内部的 CancelPress() 会把 _pressRow 置 null
        var row = FindPeriodRow(e.OriginalSource as DependencyObject);
        _pressRow = row;
        _gripPress = IsGripElement(e.OriginalSource as DependencyObject);

        // 记录拖拽候选（必须是有课的格子）；注意用局部变量 row，避免被 CancelPress 清空
        _dragCandidate = RowHasLesson(row) ? row : null;
        _dragging = false;
        _gestureScrolled = false;

        // 区分「手指按的」和「鼠标按的」：触摸被 WPF 提升成鼠标事件时带 StylusDevice，
        // 并且刚刚一定有过一次 TouchDown（兜底判断，避免个别设备 StylusDevice 为空）。
        // 窗口取 250ms 而不是更长：提升几乎总在 50ms 内到达，窗口太大时"刚用鼠标点完、
        // 马上拖另一张卡"会被误判成触摸，白白要按住 0.5 秒才动。
        _pressFromTouch = e.StylusDevice != null
                          || (Environment.TickCount - _lastTouchDownTick) < 250;

        // 拖动原点：触摸一律用 TouchDown 记下的真实起点，别用提升坐标 —— 两者差一两像素，
        // 会让"到底动没动够 10px"时准时不准；幽灵卡的抓取偏移也吃这个点，混用会让卡片
        // 一浮起来就歪一下。
        bool haveTouchOrigin = _touchStartPt.X > 0 || _touchStartPt.Y > 0;
        _dragStart = (_pressFromTouch && haveTouchOrigin) ? _touchStartPt : e.GetPosition(this);
        if (row != null) _pressPoint = _dragStart;
        _lastDragPoint = _dragStart;   // 本次拖动的真实位置从这里开始记

        // ★ 触摸不再用"按住 1 秒弹编辑窗"这条老路：触摸改由「长按浮起 → 原地松手 = 编辑」
        //   （见 Window_PreviewMouseLeftButtonUp），两个计时器并存会互相打架。
        //   鼠标保持原样：按住 1 秒弹编辑窗，同时也随时可以直接拖。
        //   ★ 抓手上不启动它：抓手本来就是"随时可拖、松手即编辑"，再叠一个 1 秒计时器，
        //     原地按住一秒会变成弹两次（计时器一次 + 松手一次）。
        if (!_pressFromTouch && row != null && !_gripPress) StartPressTimer();

        StopHoldArm();   // 会把 _touchHoldArmed 清成 false

        if (_dragCandidate != null)
        {
            // 不在这里 Mouse.Capture：提前捕获会让后续事件的 OriginalSource 变成窗口本身，
            // 导致双击「教师 / 班级行」无法识别；真正开始拖动时再捕获（见 PreviewMouseMove）
            if (_hider != null) _hider.Suspended = true;

            if (_gripPress) ArmAt(_dragStart);               // 抓手：按下即浮起，0 秒起拖
            else if (_pressFromTouch) StartHoldArm(_dragStart);   // 卡片正文：等 0.5 秒长按浮起
            else _touchHoldArmed = true;                     // 鼠标：立即可拖
        }
        else
        {
            _touchHoldArmed = !_pressFromTouch;
        }
    }

    /// <summary>
    /// 触摸按下后静止计时 <see cref="TouchHoldMs"/>；到点把这次触摸标记为"浮起（长按态）"，
    /// 并给按住的卡片加抬起视觉。期间划走（<see cref="DrivePointer"/>）会立刻清掉计时器，
    /// 本次按下永久失去长按 / 拖动资格 —— 这就是"一动就优先滚动"。
    /// </summary>
    private void StartHoldArm(System.Windows.Point pt)
    {
        StopHoldArm();
        _armBorder = HitTestBorder(pt);
        if (_armBorder == null)
        {
            // 视觉命中偶尔会失手（刷新重建、覆盖层、缝隙）。别让这次长按直接作废 ——
            // 用户看到的就是「按住了却什么都没浮起来，松手倒弹出修改窗口」。
            // 退化成「纵向最近的一行」，与 FindDropRow 的兜底同一套判据。
            CollectRowBorders();
            double best = double.MaxValue;
            foreach (var (_, rb) in _rowBorders)
            {
                if (rb.ActualHeight <= 0) continue;
                try
                {
                    var tl = rb.TransformToVisual(this).Transform(new System.Windows.Point(0, 0));
                    double d = Math.Abs(tl.Y + rb.ActualHeight / 2 - pt.Y);
                    if (d < best && d <= rb.ActualHeight / 2 + 22) { best = d; _armBorder = rb; }
                }
                catch { }
            }
        }
        if (_armBorder == null) return;
        _armSavedBorderBrush = _armBorder.BorderBrush;
        _armSavedBorderThickness = _armBorder.BorderThickness;
        _armSavedOpacity = _armBorder.Opacity;

        _holdArmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TouchHoldMs) };
        _holdArmTimer.Tick += (_, _) =>
        {
            _holdArmTimer?.Stop();
            _holdArmTimer = null;
            // 已经划走（判定为滚动）/ 松手了 —— 计时器必须彻底作废，绝不能在这里复活一次"浮起"
            if (_gestureScrolled || _dragCandidate == null || _armBorder == null) return;
            ArmAt(_dragStart);
        };
        _holdArmTimer.Start();
    }

    /// <summary>
    /// 真正进入"浮起（可以搬了）"状态。两条路共用：静止满 0.5 秒（<see cref="StartHoldArm"/>），
    /// 以及左列抓手按下（0 秒，见 <see cref="_gripPress"/>）。
    /// <para>浮起来就得让人看见"现在可以搬了"：原卡变淡腾出位置、原位留虚线框、卡片跟手、
    /// 底部说明两种收尾方式。少了这一段，用户永远不知道自己处在哪个状态。</para>
    /// </summary>
    private void ArmAt(System.Windows.Point pt)
    {
        if (_dragCandidate == null || _isTeacherMode || _clickThrough || _locked) return;

        // 抓手路径没有先经过 StartHoldArm，_armBorder 还是空的 —— 就地按坐标补一次命中。
        // 命中的必须是"课程行的根 Border"（抓手是 Grid，不会顶替它，见 XAML 里的说明）。
        if (_armBorder == null)
        {
            _armBorder = HitTestBorder(pt);
            if (_armBorder == null) return;
            _armSavedBorderBrush = _armBorder.BorderBrush;
            _armSavedBorderThickness = _armBorder.BorderThickness;
            _armSavedOpacity = _armBorder.Opacity;
        }

        _holdArmTimer?.Stop();
        _holdArmTimer = null;
        _touchHoldArmed = true;
        ShowArmVisual();

        CollectRowBorders();
        // 命中的 Border 偶尔只是卡片里的一小块（w<40 / h<16），CreateGhost 会直接早退，
        // 于是「状态已经浮起、画面上却什么都没有」。换成本行根 Border 再建一次卡面。
        if (_armBorder != null && (_armBorder.ActualWidth < 40 || _armBorder.ActualHeight < 16))
        {
            var rootBorder = _rowBorders.FirstOrDefault(x => ReferenceEquals(x.Row, _dragCandidate)).Border;
            if (rootBorder != null && !ReferenceEquals(rootBorder, _armBorder)
                && rootBorder.ActualWidth >= 40 && rootBorder.ActualHeight >= 16)
            {
                _armBorder.Opacity = _armSavedOpacity;   // 先把刚压淡的小 Border 还原
                _armBorder = rootBorder;
                _armSavedOpacity = rootBorder.Opacity;
                ShowArmVisual();
            }
        }
        if (_armBorder != null) CreateGhost(_dragCandidate, _armBorder);
        _lastDragPoint = pt;
        UpdateDragVisuals(pt);
        UpdateDropTarget(pt);
        ShowAction("松手 → 打开调课（同右键）　·　拖到别的节 → 交换");
    }

    /// <summary>取消"浮起"状态并还原卡片外观（松手、划走、取消拖动时都要调）。</summary>
    private void StopHoldArm()
    {
        _holdArmTimer?.Stop();
        _holdArmTimer = null;
        _touchHoldArmed = false;
        ClearArmVisual();
        ClearGhost();
        if (ActionBar != null && TxtAction != null && BtnActionUndo.Visibility != Visibility.Visible)
            ActionBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 卡片"浮起来"：原卡变淡、腾出位置，让下方那张虚线框说明"这里空了"。
    /// ★ 原卡不再描蓝边也不加阴影 —— 它已经"被搬走"，不该再抢注意力；
    ///   真正的焦点是跟手的那张幽灵卡。
    /// </summary>
    private void ShowArmVisual()
    {
        if (_armBorder == null) return;
        _armBorder.Opacity = 0.25;
        _armBorder.Effect = null;
    }

    private void ClearArmVisual()
    {
        if (_armBorder == null) return;
        _armBorder.BorderBrush = _armSavedBorderBrush;
        _armBorder.BorderThickness = _armSavedBorderThickness;
        _armBorder.Opacity = _armSavedOpacity;
        _armBorder.Effect = null;
        _armBorder = null;
        _armSavedBorderBrush = null;
        _armSavedOpacity = 1;
    }

    // ---------- 拖动覆盖层：跟手的幽灵卡 + 原位留白虚框 ----------

    /// <summary>抓一遍当前所有课程行的 Border，拖动时用它做"最近一行"落点判断（落在行间缝隙也不会失手）。</summary>
    private void CollectRowBorders()
    {
        _rowBorders.Clear();
        if (TodayPanel == null) return;

        void Walk(DependencyObject d)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                // 按"行"去重，不按 Border 去重：一张卡里可能有多层 Border 都继承了 PeriodRow 的
                // DataContext，取最外层那个（递归是父→子，先遇到的即最外层）才是整张卡片的框。
                if (c is Border b && b.DataContext is PeriodRow r && r.Period > 0
                    && !_rowBorders.Any(x => ReferenceEquals(x.Row, r)))
                    _rowBorders.Add((r, b));
                Walk(c);
            }
        }
        Walk(TodayPanel);
    }

    private void CreateGhost(PeriodRow row, Border source)
    {
        ClearGhost();
        double w = source.ActualWidth, h = source.ActualHeight;
        if (w < 40 || h < 16) return;

        // 抓取偏移必须在同一个坐标系里算：两头都用 DragLayer（画布）坐标。
        // ★ 这里以前是「左边用窗口坐标（TransformToVisual(this)）、右边用画布坐标（ToDragLayer）」，
        //   混用的后果是幽灵卡相对手指恒定偏一段（根边框 1px + 覆盖层容器位移）。
        //   更糟的是 DragLayer 一旦不在窗口原点，偏移会大到"卡片跑到窗口中间去"。
        var cardInLayer = source.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        var startInLayer = ToDragLayer(_dragStart);
        _ghostGrabDX = startInLayer.X - cardInLayer.X;
        _ghostGrabDY = startInLayer.Y - cardInLayer.Y;

        var slot = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = 10,
            RadiusY = 10,
            Stroke = new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = Brushes.Transparent
        };
        DragLayer.Children.Add(slot);
        Canvas.SetLeft(slot, cardInLayer.X);
        Canvas.SetTop(slot, cardInLayer.Y);
        _slotOutline = slot;

        var lv = row.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
        var ghost = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            // 必须完全不透明：半透明会让底下那行的「已调课」徽章之类透上来，看着像卡片上沾了脏东西。
            // 落点靠整行蓝框 + 底部文字提示表达，不靠"透过卡片看"。
            Opacity = 1.0,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.03, 1.03),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.28,
                Color = Colors.Black
            }
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(10, 8, 0, 0) };
        left.Children.Add(new TextBlock
        {
            Text = row.PeriodText,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(row.TimeRange))
            left.Children.Add(new TextBlock
            {
                Text = row.TimeRange,
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3))
            });
        Grid.SetColumn(left, 0);
        g.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(0, 8, 10, 0) };
        right.Children.Add(new TextBlock
        {
            Text = lv?.DisplaySubject ?? "",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x28, 0x36)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(lv?.Teacher))
            right.Children.Add(new TextBlock
            {
                Text = "教师：" + lv!.Teacher,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x54, 0x67)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        Grid.SetColumn(right, 1);
        g.Children.Add(right);

        ghost.Child = g;
        DragLayer.Children.Add(ghost);
        // 先摆到卡片原位再等 UpdateDragVisuals 跟手：少了这一句，万一这一帧位置没算出来，
        // 幽灵卡会以 Canvas 默认坐标（左上角，或者容器中间）出现 —— 看着像"卡片自己跑掉了"。
        Canvas.SetLeft(ghost, cardInLayer.X);
        Canvas.SetTop(ghost, cardInLayer.Y);
        _ghost = ghost;
    }

    /// <summary>
    /// 幽灵卡跟着手指、虚框钉在原位。滚动时行会移动，所以滚动 tick 里也要重新调用。
    /// <para>参数 <paramref name="pt"/> 是<b>窗口坐标</b>（手指位置、命中测试都用这套），
    /// Canvas 里定位要用 <see cref="DragLayer"/> 坐标，这里统一换算 —— 窗口根 Border 有 1px 边框，
    /// 两者并不是完全重合的。</para>
    /// </summary>
    private void UpdateDragVisuals(System.Windows.Point pt)
    {
        var p = ToDragLayer(pt);
        if (_slotOutline != null && _armBorder != null)
        {
            // 窗口正在关闭 / 卡片已脱离可视树时，TransformToVisual 会抛
            // "不共享共同的上级"。这一帧画不出来无所谓，绝不能让它打断拖动。
            try
            {
                var origin = _armBorder.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
                Canvas.SetLeft(_slotOutline, origin.X);
                Canvas.SetTop(_slotOutline, origin.Y);
            }
            catch { }
        }
        if (_ghost != null)
        {
            double gx = p.X - _ghostGrabDX;
            double gy = p.Y - _ghostGrabDY;

            // 兜底：抓取偏移是"按下那一刻"算的，卡片被 30 秒刷新重建 / 换行之后可能对不上，
            // 幽灵卡就会飞到一个凭空的坐标（用户看到的是"卡片跑到窗口中间"）。
            // 正常偏移绝不会超过整张卡片，所以一旦算出的位置离手指超过一张半卡片，就直接
            // 居中贴到手指上 —— 宁可抓取点差一点，也不能让卡片脱离手指。
            if (Math.Abs(gx - p.X) > _ghost.Width * 1.5 || Math.Abs(gy - p.Y) > _ghost.Height * 1.5)
            {
                gx = p.X - _ghost.Width / 2;
                gy = p.Y - _ghost.Height / 2;
            }

            Canvas.SetLeft(_ghost, gx);
            Canvas.SetTop(_ghost, gy);
        }
    }

    /// <summary>窗口坐标 → 覆盖层画布坐标。</summary>
    private System.Windows.Point ToDragLayer(System.Windows.Point pt)
    {
        try { return DragLayer == null ? pt : TranslatePoint(pt, DragLayer); }
        catch { return pt; }
    }

    /// <summary>覆盖层画布坐标 → 窗口坐标（诊断代码里构造的点是画布坐标）。</summary>
    private System.Windows.Point FromDragLayer(System.Windows.Point pt)
    {
        try { return DragLayer == null ? pt : DragLayer.TranslatePoint(pt, this); }
        catch { return pt; }
    }

    private void ClearGhost()
    {
        if (_ghost != null) { DragLayer.Children.Remove(_ghost); _ghost = null; }
        if (_slotOutline != null) { DragLayer.Children.Remove(_slotOutline); _slotOutline = null; }
        HideDropMarkers();
        StopEdgeScroll();
    }

    /// <summary>
    /// 落点指示：目标行左右各一条竖蓝条，画在卡片外侧。
    /// 为什么不用"整行高亮"就完事 —— 幽灵卡正压在目标行上，整行高亮的边被盖得看不见；
    /// 往卡片外面挪几像素，无论手指把卡片拖到哪都不会被遮住。
    /// </summary>
    private void ShowDropMarkers(Rect r)
    {
        if (DragLayer == null) return;
        if (_dropMarkerL == null || _dropMarkerR == null)
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3));
            _dropMarkerL = new System.Windows.Shapes.Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = brush };
            _dropMarkerR = new System.Windows.Shapes.Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = brush };
            DragLayer.Children.Add(_dropMarkerL);
            DragLayer.Children.Add(_dropMarkerR);
        }
        _dropMarkerL.Height = _dropMarkerR.Height = Math.Max(8, r.Height);
        Canvas.SetLeft(_dropMarkerL, r.X - 7);
        Canvas.SetTop(_dropMarkerL, r.Y);
        Canvas.SetLeft(_dropMarkerR, r.X + r.Width + 4);
        Canvas.SetTop(_dropMarkerR, r.Y);
        _dropMarkerL.Visibility = _dropMarkerR.Visibility = Visibility.Visible;
    }

    private void HideDropMarkers()
    {
        if (_dropMarkerL != null) _dropMarkerL.Visibility = Visibility.Collapsed;
        if (_dropMarkerR != null) _dropMarkerR.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 诊断用（--drag-check）：不模拟手指，只验证拖动链路的几何计算。
    /// 覆盖两件最容易算错的事：① 落在行与行之间的缝隙里仍然有落点；② 幽灵卡 / 虚框的尺寸与位置合理。
    /// 纯只读，不改任何数据。
    /// </summary>
    internal string DiagDragProbe()
    {
        var sb = new System.Text.StringBuilder();
        if (ScheduleScroll == null || TodayPanel == null)
        {
            sb.AppendLine("FAIL 课表未初始化");
            return sb.ToString();
        }
        if (DragLayer == null) { sb.AppendLine("FAIL DragLayer 不存在"); return sb.ToString(); }

        CollectRowBorders();
        sb.AppendLine($"rows={_rowBorders.Count}");
        if (_rowBorders.Count < 2) { sb.AppendLine("FAIL 课程行不足 2 行"); return sb.ToString(); }

        int hitRow = 0, missRow = 0, hitGap = 0, gapTotal = 0;
        foreach (var (r, b) in _rowBorders)
        {
            var tl = b.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
            var center = new System.Windows.Point(tl.X + b.ActualWidth / 2, tl.Y + b.ActualHeight / 2);
            if (FindDropRow(FromDragLayer(center), out var found, out _) && ReferenceEquals(found, r)) hitRow++;
            else { missRow++; sb.AppendLine($"  miss row p={r.Period} y={center.Y:F1}"); }
        }
        for (int i = 0; i + 1 < _rowBorders.Count; i++)
        {
            var a = _rowBorders[i].Border;
            var c = _rowBorders[i + 1].Border;
            var at = a.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
            var ct = c.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
            double aBottom = at.Y + a.ActualHeight;
            if (ct.Y - aBottom < 3) continue;      // 两行之间没有缝隙，不用测
            gapTotal++;
            if (FindDropRow(FromDragLayer(new System.Windows.Point(at.X + 20, (aBottom + ct.Y) / 2)), out _, out _)) hitGap++;
        }
        sb.AppendLine($"hitRow={hitRow}/{hitRow + missRow} hitGap={hitGap}/{gapTotal}");

        var (row0, br0) = _rowBorders[0];
        _armBorder = br0;
        _armSavedOpacity = br0.Opacity;
        var cardTl = br0.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        _dragStart = FromDragLayer(new System.Windows.Point(cardTl.X + 20, cardTl.Y + 10));
        CreateGhost(row0, br0);
        UpdateDragVisuals(_dragStart);

        if (_ghost != null)
            sb.AppendLine($"ghost w={_ghost.Width:F1} h={_ghost.Height:F1} left={Canvas.GetLeft(_ghost):F1} top={Canvas.GetTop(_ghost):F1} card={cardTl.X:F1},{cardTl.Y:F1}");
        else
            sb.AppendLine("FAIL ghost 未创建");

        if (_slotOutline != null)
            sb.AppendLine($"slot left={Canvas.GetLeft(_slotOutline):F1} top={Canvas.GetTop(_slotOutline):F1}");
        else
            sb.AppendLine("FAIL 虚框未创建");

        // 跟手：手指平移 (30,120)，幽灵卡必须一模一样地平移同样的距离。
        // 这一条专门盯"窗口坐标 / 画布坐标混用"——混了就会差一截，卡片看着不跟手。
        if (_ghost != null)
        {
            double x0 = Canvas.GetLeft(_ghost), y0 = Canvas.GetTop(_ghost);
            UpdateDragVisuals(new System.Windows.Point(_dragStart.X + 30, _dragStart.Y + 120));
            double dx2 = Canvas.GetLeft(_ghost) - x0, dy2 = Canvas.GetTop(_ghost) - y0;
            double err = Math.Abs(dx2 - 30) + Math.Abs(dy2 - 120);
            sb.AppendLine($"ghost 跟手 dx={dx2:F1} dy={dy2:F1} 期望 30/120 误差={err:F1}{(err < 1.0 ? " ok" : " ★ 偏差过大")}");
            UpdateDragVisuals(_dragStart);
        }

        // 手势分界线现在是"位移 10px"（见 GestureSlop），不再是"离没离开原格"。
        // 这三个值一起打印，回归时一眼能看出阈值或系统手势有没有被改回去。
        sb.AppendLine($"gesture slop={GestureSlop} hold={TouchHoldMs}ms panning={ScheduleScroll.PanningMode}");

        // 命中一致性：同一行换几个位置按下，浮起来的必须都是「整行根 Border」，
        // 幽灵卡左上角也必须每次都贴住卡片原位。
        // ★ 这一条专盯用户报的两件事：① 按科目/姓名浮起的是小格子、按边沿是整大格；
        //   ② 幽灵卡偶尔出现在别处（坐标混用 / 抓取偏移算错）。
        try
        {
            var (rP, bP) = _rowBorders[0];
            var tlP = bP.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
            var probes = new (string Name, double Fx, double Fy)[]
            {
                ("左列抓手", 0.05, 0.50), ("科目", 0.55, 0.22), ("姓名", 0.55, 0.78),
                ("右内沿", 0.95, 0.50), ("上内沿", 0.50, 0.08), ("下内沿", 0.50, 0.92),
            };
            int sizeOk = 0, posOk = 0;
            foreach (var (nm, fx, fy) in probes)
            {
                var q = FromDragLayer(new System.Windows.Point(tlP.X + bP.ActualWidth * fx, tlP.Y + bP.ActualHeight * fy));
                var hb = HitTestBorder(q);
                bool isRoot = ReferenceEquals(hb, bP);
                if (isRoot) sizeOk++;
                else sb.AppendLine($"  probe[{nm}] 命中 {hb?.ActualWidth:F0}x{hb?.ActualHeight:F0}" +
                                   $"（行根是 {bP.ActualWidth:F0}x{bP.ActualHeight:F0}）");

                _armBorder = hb;
                _armSavedOpacity = bP.Opacity;
                _dragStart = q;
                _dragCandidate = rP;
                if (hb != null) CreateGhost(rP, hb);
                UpdateDragVisuals(q);
                if (_ghost != null)
                {
                    double gx = Canvas.GetLeft(_ghost), gy = Canvas.GetTop(_ghost);
                    if (Math.Abs(gx - tlP.X) <= 1.5 && Math.Abs(gy - tlP.Y) <= 1.5) posOk++;
                    else sb.AppendLine($"  probe[{nm}] 幽灵卡在 {gx:F0},{gy:F0}，应贴卡片 {tlP.X:F0},{tlP.Y:F0}");
                }
                ClearGhost();
                _armBorder = null;
            }
            sb.AppendLine($"命中 整行根={sizeOk}/{probes.Length} 幽灵卡贴合={posOk}/{probes.Length} " +
                          $"{(sizeOk == probes.Length && posOk == probes.Length ? "ok" : "★ 尺寸或位置不对")}");

            // 把「科目」位置父链上的所有 Border 打出来：只要中间还有 DataContext=PeriodRow 的
            // 小 Border，就说明"取最近的那个"一定会浮出小格子 —— 这条断言是那次 bug 的根因凭证。
            DependencyObject? vis = null;
            VisualTreeHelper.HitTest(this, null,
                r => { vis = r.VisualHit; return HitTestResultBehavior.Stop; },
                new PointHitTestParameters(FromDragLayer(new System.Windows.Point(
                    tlP.X + bP.ActualWidth * 0.55, tlP.Y + bP.ActualHeight * 0.22))));
            // 把「科目」位置父链上的 Border 尺寸打出来：链上第一个是 ItemsControl 默认模板
            // 自带的 Border（≈ 内容区 149x47），第二个才是行根（243x70）。旧代码取"最近的那个"，
            // 于是按科目浮起的是小格子、按边沿是整大格 —— 这条断言就是那个 bug 的凭证。
            var chain = new System.Text.StringBuilder();
            DependencyObject? d = vis;
            int depth = 0;
            while (d != null && depth++ < 24)
            {
                if (d is Border bb) chain.Append($"{bb.ActualWidth:F0}x{bb.ActualHeight:F0} ");
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            sb.AppendLine("科目处 Border 链: " + (chain.Length == 0 ? "(无)" : chain + "← 取最后一个（行根）"));
        }
        catch (Exception ex) { sb.AppendLine("EX probe " + ex.Message); }
        finally { ClearGhost(); ClearArmVisual(); }

        // 左列抓手：既要真的存在，也不能把"行根 Border"顶掉 ——
        // 抓手一旦写成 Border，HitTestBorder 就会返回它，幽灵卡会缩成 74px 宽的一条。
        var grip = FindGripElement(TodayPanel);
        if (grip == null) sb.AppendLine("grip=FAIL 左列抓手不存在");
        else
        {
            var gtl = grip.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
            var gc = FromDragLayer(new System.Windows.Point(gtl.X + grip.ActualWidth / 2, gtl.Y + grip.ActualHeight / 2));
            var hb = HitTestBorder(gc);
            var hrow = hb?.DataContext as PeriodRow;
            double gw = grip.ActualWidth, bw = hb?.ActualWidth ?? 0;
            bool ok = hrow != null && bw > gw * 2;
            sb.AppendLine($"grip w={gw:F0} hitPeriod={hrow?.Period.ToString() ?? "-"} hitBorderW={bw:F0} " +
                          $"{(ok ? "ok（点在抓手内仍命中的是整行）" : "★ 抓手把行根 Border 顶掉了")}");
            // 抓手内部的文字也必须被认成抓手（用户很少正好按在空白上）
            var inner = VisualTreeHelper.GetChildrenCount(grip) > 0 ? VisualTreeHelper.GetChild(grip, 0) : null;
            sb.AppendLine($"grip 识别={(IsGripElement(grip) ? "self-ok" : "self-FAIL")}/{(inner == null || IsGripElement(inner) ? "inner-ok" : "inner-FAIL")}");

            // 抓手手势链（纯内存演练，不写数据）：按下 → 0 秒就浮起；3px 不算拖；过 10px 起拖。
            // 这一段专盯"抓手到底有没有跳过 0.5 秒"——只验证到"能动"不够，必须没有等待。
            // ★ 课表被用户锁定时抓手本来就拖不动（ArmAt 第一行就早退），那是正确行为不是回归；
            //   诊断用的是真实数据副本，会把锁定状态一起带过来，所以这里临时解锁演练、结束后还原。
            bool lockedWas = _locked;
            try
            {
                _locked = false;
                ClearGhost();
                _armBorder = null;
                _dragCandidate = grip.DataContext as PeriodRow;
                _dragging = false;
                _touchHoldArmed = false;
                _gripPress = true;
                var gp = FromDragLayer(new System.Windows.Point(gtl.X + 20, gtl.Y + grip.ActualHeight / 2));
                _dragStart = gp;
                _touchStartPt = gp;
                var candidateWas = _dragCandidate;
                var armBorderWas = _armBorder;

                ArmAt(gp);                                     // 按下那一刻
                bool armedNow = _touchHoldArmed && _ghost != null;
                DrivePointer(new System.Windows.Point(gp.X + 3, gp.Y + 2), null);
                bool stillNotDragging = !_dragging;
                DrivePointer(new System.Windows.Point(gp.X + 30, gp.Y + 40), null);
                bool draggingNow = _dragging;
                sb.AppendLine($"grip 手势 按下即浮起={armedNow} 3px不拖={stillNotDragging} 30px起拖={draggingNow} " +
                              $"{(armedNow && stillNotDragging && draggingNow ? "ok" : "★ 手势链断了")}");
                // 手势链没起来时，把 ArmAt 的四个早退条件逐个报出来 —— 否则只能看到 False，
                // 分不清是「抓手指针没拿到行」还是「课表被锁定 / 教师机只读」这类环境问题。
                if (!armedNow)
                    sb.AppendLine($"  grip 早退诊断 candidate={(candidateWas != null ? "PeriodRow#" + candidateWas.Period : "null")} "
                                  + $"teacher={_isTeacherMode} clickThrough={_clickThrough} locked={_locked} "
                                  + $"armBorder={(armBorderWas != null ? armBorderWas.ActualWidth.ToString("F0") + "x" + armBorderWas.ActualHeight.ToString("F0") : "null")}");
            }
            catch (Exception ex) { sb.AppendLine("EX grip " + ex.Message); }
            finally { _locked = lockedWas; EndDrag(); }
        }

        // 撤销靠"整份覆盖表的 JSON 往返"，这里确认它能可靠复原（DateOnly / 可空字段都不能丢）
        try
        {
            var list = Store.LoadOverrides();
            string s1 = System.Text.Json.JsonSerializer.Serialize(list);
            var back = System.Text.Json.JsonSerializer.Deserialize<List<LessonOverride>>(s1);
            bool same = back != null && System.Text.Json.JsonSerializer.Serialize(back) == s1;
            sb.AppendLine($"json roundtrip={(same ? "ok" : "FAIL")} n={list.Count}");
        }
        catch (Exception ex) { sb.AppendLine("EX json " + ex.Message); }

        ClearGhost();
        _armBorder = null;
        return sb.ToString();
    }

    // ---------- 底部提示条 ----------

    /// <summary>诊断用（--shot dragarmed）：把界面摆成"卡片已浮起、正拖到第三行"的样子，供核对视觉。</summary>
    internal void DiagArmDragVisual()
    {
        CollectRowBorders();
        if (_rowBorders.Count < 3 || DragLayer == null) return;

        var (row0, br0) = _rowBorders[0];
        _dragCandidate = row0;
        _armBorder = br0;
        _armSavedBorderBrush = br0.BorderBrush;
        _armSavedBorderThickness = br0.BorderThickness;
        _armSavedOpacity = br0.Opacity;
        ShowArmVisual();

        var cardTl = br0.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        _dragStart = FromDragLayer(new System.Windows.Point(cardTl.X + 24, cardTl.Y + 14));
        CreateGhost(row0, br0);
        _dragging = true;

        var t2 = _rowBorders[2].Border.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        _lastDragPoint = FromDragLayer(new System.Windows.Point(t2.X + 30, t2.Y + 18));
        UpdateDragVisuals(_lastDragPoint);
        UpdateDropTarget(_lastDragPoint);
    }

    /// <summary>显示底部提示。<paramref name="seconds"/> &gt; 0 时到点自动收起（撤销条用 5 秒）。</summary>
    private void ShowAction(string text, bool undo = false, int seconds = 0)
    {
        if (ActionBar == null || TxtAction == null) return;
        if (TxtAction.Text != text) TxtAction.Text = text;
        BtnActionUndo.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        // 只有"能撤销"的时候才需要这条挡住点击；拖动中的实时提示一律让点击穿过去，
        // 免得它浮在课表底部把最后几节的点击吃掉。
        ActionBar.IsHitTestVisible = undo;

        // 用户开始下一步操作（拖动中的实时提示）→ 上一步的反悔窗口就此关闭，避免"撤销"按钮
        // 挂在一条讲别的事情的提示上。
        if (!undo) _undoSnapshot = null;

        _actionTimer?.Stop();
        _actionTimer = null;
        if (seconds > 0)
        {
            _actionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _actionTimer.Tick += (_, _) =>
            {
                _actionTimer?.Stop();
                _actionTimer = null;
                _undoSnapshot = null;
                HideAction();
            };
            _actionTimer.Start();
        }
    }

    private void HideAction()
    {
        if (ActionBar != null) ActionBar.Visibility = Visibility.Collapsed;
        if (BtnActionUndo != null) BtnActionUndo.Visibility = Visibility.Collapsed;
    }

    private void BtnActionUndo_Click(object sender, RoutedEventArgs e)
    {
        var snap = _undoSnapshot;
        _undoSnapshot = null;
        if (string.IsNullOrEmpty(snap)) { HideAction(); return; }
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<LessonOverride>>(snap);
            if (list != null)
            {
                Store.SaveOverrides(list);
                AppEvents.RaiseScheduleChanged();
                ShowAction("已撤销", seconds: 2);
                return;
            }
        }
        catch { }
        ShowAction("撤销失败", seconds: 2);
    }

    // ---------- 拖动到滚动区上下边缘时自动滚动（跨屏调课不用先滚再拖） ----------

    private void StartEdgeScroll()
    {
        if (_edgeScrollTimer != null) return;
        _edgeScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeScrollTimer.Tick += (_, _) => EdgeScrollTick();
        _edgeScrollTimer.Start();
    }

    private void StopEdgeScroll()
    {
        _edgeScrollTimer?.Stop();
        _edgeScrollTimer = null;
    }

    private void EdgeScrollTick()
    {
        if (!_dragging || ScheduleScroll == null) { StopEdgeScroll(); return; }
        if (ScheduleScroll.ScrollableHeight <= 0) return;

        var tl = ScheduleScroll.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        double top = tl.Y, bottom = tl.Y + ScheduleScroll.ActualHeight;
        double y = ToDragLayer(_lastDragPoint).Y;   // 触发区是画布坐标，手指位置是窗口坐标

        double speed = 0;
        if (y < top + EdgeScrollZone) speed = -(1 - Math.Max(0, y - top) / EdgeScrollZone);
        else if (y > bottom - EdgeScrollZone) speed = 1 - Math.Max(0, bottom - y) / EdgeScrollZone;
        if (speed == 0) return;

        double before = ScheduleScroll.VerticalOffset;
        ScheduleScroll.ScrollToVerticalOffset(before + Math.Clamp(speed, -1, 1) * 14);
        if (Math.Abs(ScheduleScroll.VerticalOffset - before) > 0.01)
        {
            UpdateDragVisuals(_lastDragPoint);   // 原位虚框要跟着行一起走
            UpdateDropTarget(_lastDragPoint);
        }
    }

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 起拖时改 Mouse.OverrideCursor 会让 WPF 同步回调一次本事件（重入）。这一次必须无视：
        // 那时拖动状态正设到一半，照常走下面的"没按住左键就收尾"会把刚起手的拖动掐断。
        if (_inPointerSetup) return;

        // 单指滚动中：只跟手滚，不再参与任何拖动判定
        if (_singleScroll)
        {
            // 同上：触摸态下 LeftButton 不可信（手指按着，模拟鼠标键却是松的）
            if (e.LeftButton != MouseButtonState.Pressed) { if (_pressFromTouch) return; EndSingleScroll(); return; }
            var sy = e.GetPosition(this).Y;
            ScheduleScroll?.ScrollToVerticalOffset(_singleScrollOrigin - (sy - _singleScrollStartY));
            TrackFlingVelocity(sy);
            return;
        }

        if (_pressTimer != null && _pressTimer.IsEnabled)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) > GestureSlop || Math.Abs(p.Y - _pressPoint.Y) > GestureSlop)
                CancelPress();
        }

        if (_dragCandidate == null || _isTeacherMode || _clickThrough) return;

        // ★ 触摸被系统"模拟成鼠标"时，这里的 LeftButton 往往已经是 Released —— 真正按着的是
        //   手指，不是鼠标键。若照常 EndDrag，浮起之后手指一停、系统发来一个模拟 move，
        //   拖动就被当场收尾（表现正是"卡片浮起来了，动一下反而没了"）。
        //   触摸的收尾只认 PreviewTouchUp / 提升出来的 MouseUp。
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_pressFromTouch && (_touchHoldArmed || _dragging)) return;
            EndDrag();
            return;
        }

        // ★ 触摸按住期间由触摸事件驱动（更准、更及时）。只有触摸已经停了 150ms（个别一体机
        //   根本不发 TouchMove）才回退到"提升出来的鼠标"，否则两个数据源会把幽灵卡扯得发抖。
        if (_pressFromTouch && Environment.TickCount64 - _lastTouchDriveTick < 150) return;

        DrivePointer(e.GetPosition(this), null);
    }

    /// <summary>
    /// 手势判定的唯一实现，鼠标与触摸共用：规则与视觉完全相同，只有位置来源不同。
    /// <para>★ 判定手指位置只能用它自己的 Touch 坐标。触摸屏上凡是"鼠标移动"都是系统模拟的，
    /// 中间会稀疏甚至直接停掉 —— 拿它驱动幽灵卡，就会出现"卡片浮起来了但不跟手"。</para>
    /// <para>外面这层只负责防重入：起拖时会改 Mouse.OverrideCursor，WPF 会同步回调
    /// PreviewMouseMove，那一次再进来就会把状态搅乱。</para>
    /// </summary>
    private void DrivePointer(System.Windows.Point cur, TouchDevice? touch)
    {
        if (_inPointerSetup) return;
        _inPointerSetup = true;
        try { DrivePointerCore(cur, touch); }
        finally { _inPointerSetup = false; }
    }

    private void DrivePointerCore(System.Windows.Point cur, TouchDevice? touch)
    {
        if (_isTeacherMode || _clickThrough) return;

        // 位移一律以"真实按下点"为原点：触摸用 TouchDown 记的点，鼠标用 MouseDown 的点。
        var origin = touch != null ? _touchStartPt : _dragStart;
        double dx = Math.Abs(cur.X - origin.X);
        double dy = Math.Abs(cur.Y - origin.Y);

        // 空格子 / 固定行（大课间、午休）上没有可搬的课，但"一动就滚"必须照样成立 ——
        // 否则从这些地方起手就划不动课表（以前这里直接 return，等于课表中间那些空白
        // 都是"滚不动的死区"）。鼠标不走这条路，鼠标用滚轮。
        if (_dragCandidate == null)
        {
            if (touch == null || _singleScroll) return;
            if (!_gestureScrolled && (dx > GestureSlop || dy > GestureSlop)
                && ScheduleScroll != null && ScheduleScroll.ScrollableHeight > 0)
            {
                _gestureScrolled = true;
                if (_hider != null) _hider.Suspended = false;
                BeginSingleScroll(cur.Y, touch);
            }
            return;
        }

        // ① 还没浮起：只在 10px 以内等长按。一过 10px 立刻判为滚动，并让本次按下永久失能
        //    （不再右键、不选中、不拖拽）—— 这就是"一动就优先滚动，静止才计时长按"。
        //    ★ 必须是"一超阈值就滚"，不能"等满 500 毫秒再判断有没有划走" ——
        //      后者会让每一次想滚动课表都先僵住 0.5 秒，手感立刻变差。
        //    ★ 抓手是唯一的例外：压在左列上就等于声明"我要搬这一节"，任何位移都不判滚动。
        if (!_touchHoldArmed)
        {
            if (_gripPress)
            {
                ArmAt(origin);      // 正常情况下按下时已经浮起，这里只是兜底补一次
                if (!_touchHoldArmed) return;
            }
            else
            {
                if (dx <= GestureSlop && dy <= GestureSlop) return;

                CancelPress();
                StopHoldArm();          // 清长按计时器 + 幽灵卡（计时器里还会再查一次 _gestureScrolled）
                _dragCandidate = null;
                _gestureScrolled = true;
                if (_hider != null) _hider.Suspended = false;
                if (ScheduleScroll != null && ScheduleScroll.ScrollableHeight > 0) BeginSingleScroll(cur.Y, touch);
                return;
            }
        }

        // ② 已浮起（长按态）：位移 <10px 仍算"原地没动"，松手就是右键（打开调课窗）；
        //    ≥10px 才开始搬运 —— 这条 10px 是"右键"与"拖动换节"的唯一分界线。
        if (!_dragging)
        {
            if (dx <= GestureSlop && dy <= GestureSlop) return;

            CancelPress();
            _dragging = true;
            CollectRowBorders();   // 落点判断和落点高亮都靠它

            // 鼠标路径没有"浮起"这一步，_armBorder 与幽灵卡都还是空的 —— 这里补上。
            // 少了它，鼠标拖动时只有目标行那两根竖条，手上什么也没有；而且下一次触摸浮起
            // 如果复用了残留的 _armBorder，抓取偏移就是按旧卡片算的，卡片会整体跑偏。
            if (_armBorder == null)
            {
                _armBorder = HitTestBorder(_dragStart);
                if (_armBorder != null)
                {
                    _armSavedBorderBrush = _armBorder.BorderBrush;
                    _armSavedBorderThickness = _armBorder.BorderThickness;
                    _armSavedOpacity = _armBorder.Opacity;
                    ShowArmVisual();
                }
            }
            if (_armBorder != null && _ghost == null) CreateGhost(_dragCandidate, _armBorder);

            // 触摸只捕获手指（划出窗口也能继续收到 move）；鼠标才捕获鼠标。
            // ★ 触摸拖动千万别 Mouse.Capture：一捕获，系统模拟的鼠标事件就断了。
            if (touch != null) CaptureDragTouch(touch);
            else { try { Mouse.Capture(this); } catch { } }
            if (_hider != null) _hider.Suspended = true;
            Mouse.OverrideCursor = Cursors.Hand;
            StartEdgeScroll();
        }
        _lastDragPoint = cur;
        UpdateDragVisuals(cur);
        UpdateDropTarget(cur);
    }

    /// <summary>把正在拖动的那根手指（以及同时按着的其它手指）捕获到窗口，避免划出边界后丢事件。</summary>
    private void CaptureDragTouch(TouchDevice dev)
    {
        try { CaptureTouch(dev); } catch { }
        foreach (var d in _touchPoints.Keys.ToList())
        {
            if (ReferenceEquals(d, dev)) continue;
            try { CaptureTouch(d); } catch { }
        }
    }

    // ---------- 单指滚动（按下后 10px 内没有静止满 500ms、却先划走了 → 走这里） ----------

    private void BeginSingleScroll(double startY, TouchDevice? dev = null)
    {
        if (ScheduleScroll == null) return;
        StopFling();
        _singleScroll = true;
        _singleScrollOrigin = ScheduleScroll.VerticalOffset;
        _singleScrollStartY = startY;
        _lastMid = startY;
        _lastMidAt = DateTime.UtcNow;
        _flingVelocity = 0;
        _dragCandidate = null;
        _dragging = false;
        if (_hider != null) _hider.Suspended = false;

        // 捕获那根手指（不是鼠标）：划出窗口 / 课表边界后仍能继续收到 move，
        // 否则滚到一半就"粘住"。
        // ★ 绝不能 Mouse.Capture —— 一捕获，系统模拟出来的鼠标链就断了。
        if (dev != null) { try { CaptureTouch(dev); } catch { } }
    }

    private void EndSingleScroll()
    {
        _singleScroll = false;
        try { if (Mouse.Captured == this) Mouse.Capture(null); } catch { }
        try { ReleaseAllTouchCaptures(); } catch { }
    }

    /// <summary>估算抬手速度（沿用双指滚动那套平滑，松手后由 <see cref="StartFling"/> 滑一小段）。</summary>
    private void TrackFlingVelocity(double y)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastMidAt).TotalSeconds;
        if (dt <= 0.001) return;
        double v = (y - _lastMid) / dt;
        _flingVelocity = _flingVelocity * 0.7 + v * 0.3;
        _lastMid = y;
        _lastMidAt = now;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 单指翻页松手：滑一小段再停，不参与换课
        if (_singleScroll)
        {
            EndSingleScroll();
            StartFling();
            return;
        }

        FinishDragAt(e.GetPosition(this));
    }

    /// <summary>
    /// 结束一次拖动 / 长按，并按「卡片离没离开原格」决定收尾动作。
    /// 抽成独立方法是为了让抬起既能从提升出来的鼠标事件走，也能从触摸事件兜底进来
    /// （老一体机偶尔只报触摸、不报提升出来的鼠标）。幂等：状态已清时再调一次什么都不做。
    /// </summary>
    private void FinishDragAt(System.Windows.Point pt)
    {
        bool wasDragging = _dragging;
        bool wasArmed = _touchHoldArmed;
        bool fromTouch = _pressFromTouch;
        bool wasGrip = _gripPress;          // 抓手按下：鼠标 / 触摸都算"浮起过"，原地松手即右键
        bool canOpen = wasArmed && (fromTouch || wasGrip);
        var src = _dragCandidate;
        // 轻点 / 滚动收尾：本次按下没有任何拖动意图。必须把长按计时器、拖拽候选、自动隐藏
        // 暂停状态一并收干净 —— 这里曾经只 CancelPress() 就返回，500ms 后计时器照样触发，
        // 凭空浮起一张幽灵卡；之后再划一下，就会把"上一次点过的课"拖走。
        if (!wasDragging && !canOpen)
        {
            CancelPress();
            EndDrag();
            _gestureScrolled = false;
            return;
        }

        CancelPress();

        PeriodRow? tgt = null;
        bool overFixed = false;
        if (wasDragging)
        {
            // 触摸提升出来的 MouseUp 可能还带着「按下那一刻」的坐标（中间 move 没提升），
            // 拿它算落点会命中源行 → 误报「已取消调课」。以 TouchMove 记下的最后位置为准。
            var dropPt = pt;
            if ((_lastDragPoint.X > 0 || _lastDragPoint.Y > 0)
                && Math.Abs(_lastDragPoint.X - pt.X) + Math.Abs(_lastDragPoint.Y - pt.Y) > 2)
                dropPt = _lastDragPoint;
            FindDropRow(dropPt, out tgt, out overFixed);
        }

        // 先把视觉全部还原（幽灵卡 / 虚框 / 原卡透明度 / 边缘滚动），再决定业务动作
        EndDrag();

        if (wasDragging && src != null)
        {
            if (tgt != null && !ReferenceEquals(src, tgt))
                ApplyDayDrag(src, tgt);                                  // 提示与撤销都在它里面给
            else if (overFixed)
                ShowAction("这一节不能放课程，已放回原位", seconds: 3);   // 绝不静默失败
            else
                ShowAction("已取消调课", seconds: 2);
            return;
        }

        // 浮起过、但卡片自始至终没离开原格 —— 这就是"按住不动再松手"，按约定弹编辑窗。
        // 抓手同理：压在左列上不动就松手 = 想调这一节（等同鼠标右键）。
        // （真正拖动过的，上面那支已经处理掉了：落到别格就交换，落回原处就取消。）
        if (canOpen && src != null)
            OpenEditor(src);
    }

    private void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_singleScroll) EndSingleScroll();
        // ★ 只在"真的没有捕获了"时收尾。拖动起手那一句 Mouse.Capture(this) 也会走进本事件
        //   （捕获从"无"转到"窗口自己"），无条件 EndDrag 会把刚起手的拖动当场掐断 ——
        //   表现就是"按下去浮起了，一移动卡片就消失"。
        if (_dragging && Mouse.Captured != this) EndDrag();
    }

    private void EndDrag()
    {
        ClearHighlight();
        StopHoldArm();
        StopEdgeScroll();
        if (_hider != null) _hider.Suspended = false;
        _dragging = false;
        _dragCandidate = null;
        _gripPress = false;
        _dropRow = null;
        _dropValid = false;
        Mouse.OverrideCursor = null;
        try { if (Mouse.Captured == this) Mouse.Capture(null); } catch { }
        // 触摸拖动时捕获的是手指（见 CaptureDragTouch），收尾必须放掉，否则下一根手指按下来
        // 事件还会被路由到这个已经结束的拖动状态上。
        try { ReleaseAllTouchCaptures(); } catch { }
    }

    private void UpdateDropTarget(System.Windows.Point pt)
    {
        PeriodRow? row;
        bool overFixed;
        FindDropRow(pt, out row, out overFixed);
        bool valid = row != null && !ReferenceEquals(row, _dragCandidate);

        if (ReferenceEquals(row, _dropRow) && _dropValid == valid)
        {
            UpdateDragHint(row, overFixed);
            return;
        }

        ClearHighlight();
        _dropRow = row;
        _dropValid = valid;

        if (valid)
        {
            var border = FindBorderOf(row!, pt);
            if (border != null)
            {
                _dropBorder = border;
                _savedBg = border.Background;
                _savedBorder = border.BorderBrush;
                _savedBorderThickness = border.BorderThickness;
                border.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xEB, 0xFD));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3));
                border.BorderThickness = new Thickness(2);

                var tl = border.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
                ShowDropMarkers(new Rect(tl.X, tl.Y, border.ActualWidth, border.ActualHeight));
            }
        }
        UpdateDragHint(row, overFixed);
    }

    /// <summary>拖动过程中底部那条提示：告诉用户"现在松手会发生什么"，比任何图标都直接。</summary>
    private void UpdateDragHint(PeriodRow? row, bool overFixed)
    {
        if (_dragCandidate == null) return;
        var a = _dragCandidate.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
        string an = a?.DisplaySubject ?? "本课";

        if (row == null)
        {
            if (overFixed) ShowAction("这一节不能放课程");
            else ShowAction("拖到要交换的那一节");
            return;
        }
        if (ReferenceEquals(row, _dragCandidate))
        {
            ShowAction("放回原处 · 松手取消");
            return;
        }
        var b = row.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
        string bn = b == null ? "空节" : b.DisplaySubject;
        ShowAction($"松手交换　第{_dragCandidate.Period}节 {an} ⇄ 第{row.Period}节 {bn}");
    }

    private void ClearHighlight()
    {
        if (_dropBorder != null)
        {
            _dropBorder.Background = _savedBg ?? Brushes.Transparent;
            _dropBorder.BorderBrush = _savedBorder ?? Brushes.Transparent;
            _dropBorder.BorderThickness = _savedBorderThickness;
        }
        _dropBorder = null;
        _savedBg = null;
        _savedBorder = null;
        HideDropMarkers();
    }

    /// <summary>
    /// 找落点。先按视觉命中（精确到卡片）；压在固定行上时判为"不能放"；
    /// 落在行间缝隙（每行之间有 Margin）时退化成"纵向最近的一行" ——
    /// 保证拖到哪都有明确落点，不会再出现"拖了半天、松手什么都没发生"。
    /// </summary>
    private bool FindDropRow(System.Windows.Point pt, out PeriodRow? row, out bool overFixed)
    {
        row = null;
        overFixed = false;

        var direct = HitTestBorderAny(pt);
        if (direct != null && direct.DataContext is PeriodRow dr)
        {
            if (dr.Period > 0) { row = dr; return true; }
            overFixed = true;
            return false;
        }

        double best = double.MaxValue;
        foreach (var (r, b) in _rowBorders)
        {
            if (b.ActualHeight <= 0) continue;
            System.Windows.Point tl;
            // 卡片已脱离可视树（刷新 / 关闭途中）时换算会抛异常，跳过这一行就好。
            try { tl = b.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0)); }
            catch { continue; }
            double cy = tl.Y + b.ActualHeight / 2;
            double d = Math.Abs(cy - pt.Y);
            if (d < best && d <= b.ActualHeight / 2 + 22)
            {
                best = d;
                row = r;
            }
        }
        return row != null;
    }

    private Border? FindBorderOf(PeriodRow row, System.Windows.Point pt)
    {
        foreach (var (r, b) in _rowBorders)
            if (ReferenceEquals(r, row)) return b;
        return HitTestBorder(pt);
    }

    private static bool RowHasLesson(PeriodRow? row)
        => row != null && row.Period > 0 && row.Lessons.Any(l => !string.IsNullOrEmpty(l.Subject));

    private PeriodRow? HitTestRow(System.Windows.Point pt)
        => HitTestBorder(pt)?.DataContext as PeriodRow;

    private Border? HitTestBorder(System.Windows.Point pt) => HitTestBorder(pt, false);

    private Border? HitTestBorderAny(System.Windows.Point pt) => HitTestBorder(pt, true);

    private Border? HitTestBorder(System.Windows.Point pt, bool includeFixed)
    {
        Border? found = null;
        VisualTreeHelper.HitTest(this,
            null,
            result =>
            {
                // ★ 必须取「父链上最外层的那个」Border，不能取最近的那个。
                //   课程卡的模板里会嵌着好几个 Border（调/代课徽章、将来可能加的底板），它们
                //   的 DataContext 一路继承下来，最近的 Border 可能只是"科目 + 姓名"那一小块。
                //   按最近的那个浮起，幽灵卡就只有内容那么大 —— 表现为"按科目是小格子、
                //   按边沿是整大格"。这里把整条父链走完，取最外层 = 这一行的根 Border。
                DependencyObject? dep = result.VisualHit;
                Border? outer = null;
                while (dep != null)
                {
                    if (dep is Border b && b.DataContext is PeriodRow row && (includeFixed || row.Period > 0))
                        outer = b;                    // 继续往上找，最后留下的就是最外层
                    dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
                }
                if (outer != null)
                {
                    found = outer;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(pt));
        return found;
    }

    // ---------- 触屏手势：静止计时长按 / 一动优先滚动 ----------
    //
    //   单击、双击 → 系统默认（触摸被提升成鼠标事件，点击次数与间隔全用系统阈值）
    //   静止 0.5s → 浮起；原地松手 = 右键（打开调课窗）；移 ≥10px = 拖动换节
    //   0.5s 内划走 / 双指划动 → 滚动课表（本次按下永久失能）
    //
    // ★ 为什么滚动不能交给 ScrollViewer：
    //   只要 ScrollViewer.PanningMode != None，它在触摸按下时就会捕获触摸并标记「已处理」，
    //   WPF 因此不再把这次触摸提升成鼠标事件 —— 单击 / 双击 / 拖动换节全部失效。
    //   所以 PanningMode 恒为 None，滚动一律由下面这套位移计算自己做（单指 / 双指同一套）。

    /// <summary>当前还按在屏幕上的触摸点（TouchDevice → 窗口内 DIP 坐标）。</summary>
    private readonly Dictionary<TouchDevice, Point> _touchPoints = new();

    /// <summary>是否识别双指划动（false = 只用单指快速划动滚动）。两种方式都不会把触摸交给系统。</summary>
    private bool _twoFingerScroll = true;

    /// <summary>正在双指滚动。</summary>
    private bool _touchScrolling;

    /// <summary>双指按下瞬间的课表偏移与两指中点 Y。全程以它为基准算，不做增量累加（避免误差越滚越偏）。</summary>
    private double _scrollOrigin, _scrollOriginMid;

    /// <summary>松手惯性：上一帧中点与时间，用来估算抬手速度。</summary>
    private double _lastMid;
    private DateTime _lastMidAt;
    private double _flingVelocity;          // DIP/秒，正 = 手指往下划
    private DispatcherTimer? _flingTimer;

    /// <summary>
    /// 按设置决定要不要识别双指划动。
    /// <para>★ <c>PanningMode</c> 恒为 <c>None</c>，不再随设置切换：只要交给系统托管，
    /// ScrollViewer 就会抢走触摸，拖动换节必然失效。老一体机没有双指也没关系 ——
    /// 单指快速划动同样能滚（见 <see cref="DrivePointer"/>），只是不识别第二根手指。</para>
    /// </summary>
    private void ApplyTouchScrollMode(bool twoFinger)
    {
        // 只在真的换了模式时清理：Refresh 每 30 秒跑一次，若无条件清理会把正在进行的双指滚动掐断
        bool changed = _twoFingerScroll != twoFinger;
        _twoFingerScroll = twoFinger;
        if (changed) StopTouchScroll();

        if (ScheduleScroll == null) return;
        if (ScheduleScroll.PanningMode != PanningMode.None) ScheduleScroll.PanningMode = PanningMode.None;
    }

    private void Window_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        var pt = e.GetTouchPoint(this).Position;
        _touchPoints[e.TouchDevice] = pt;
        _lastTouchDownTick = Environment.TickCount;    // 供 MouseDown 判断"这次按下是手指还是鼠标"
        _lastTouchDriveTick = Environment.TickCount64; // 从现在起由触摸驱动（见 DrivePointer）

        // 第二根手指按下 → 一律退出单指手势（不允许它覆盖第一指的起点），
        // 需要双指滚动时再进入双指模式。先把第一指可能已经开始的拖动 / 长按清掉，
        // 否则一边滚一边把卡片拖走一半。
        if (_touchPoints.Count >= 2)
        {
            if (_twoFingerScroll && !_touchScrolling) BeginTouchScroll();
            else if (!_twoFingerScroll) { CancelPress(); StopHoldArm(); _dragCandidate = null; _gripPress = false; }
            e.Handled = true;      // 第二指不再提升成鼠标，避免多出一个"单击"
            return;
        }

        if (_isTeacherMode) return;
        if (_clickThrough) return;

        // 本次按下的起点与状态。★ 这里绝不能再起"按住 1 秒弹编辑窗"的计时器：
        // 触摸的长按只有一条路 —— 静止满 500ms 浮起（见 StartHoldArm），松手即右键。
        // 两个计时器并存的结果是：浮起后愣神一秒，编辑窗会自己弹出来。
        _touchStartPt = pt;
        _gestureScrolled = false;
        _gripPress = IsGripElement(e.OriginalSource as DependencyObject) && !_locked;

        // ★ 左列抓手：按下就地浮起，0 秒起拖。
        //   为什么不在等 "提升出来的 MouseDown" 里做：那一步会晚到几十毫秒，而手指可能已经先
        //   动了 —— 那时 _dragCandidate 还是空的，DrivePointer 直接 return，表现为"抓手拖不动"。
        //   这里把状态一次备齐，MouseDown 到达后按 _gripPress 短路（见那里注释），不重复初始化。
        if (_gripPress)
        {
            var grow = FindPeriodRow(e.OriginalSource as DependencyObject);
            if (RowHasLesson(grow))
            {
                _pressRow = grow;
                _pressPoint = pt;
                _dragStart = pt;
                _dragCandidate = grow;
                _dragging = false;
                _pressFromTouch = true;
                if (_hider != null) _hider.Suspended = true;
                ArmAt(pt);
            }
            else
            {
                _gripPress = false;   // 空格子（没课）不抓 —— 抓手只对课程行有意义
            }
        }
        else
        {
            // ★ 长按计时器提前起在 TouchDown，不再只等「提升出来的 MouseDown」。
            //   老一体机 / 触摸捕获状态下提升链会迟到甚至不到：那时卡片按 0.5 秒纹丝
            //   不动，可系统提升的鼠标 Up 还在，于是「没浮起、松手却弹出修改窗口」。
            //   这里就地按真实触摸点找到课程行并起表；MouseDown 正常到达时会重启一次表
            //   （延迟几十毫秒，无感），不到也没关系 —— 这条表照样能把卡片浮起来。
            var brow = HitTestBorder(pt);
            if (brow?.DataContext is PeriodRow prow && RowHasLesson(prow))
            {
                _pressRow = prow;
                _pressPoint = pt;
                _dragStart = pt;
                _lastDragPoint = pt;
                _dragCandidate = prow;
                _dragging = false;
                _pressFromTouch = true;
                if (_hider != null) _hider.Suspended = true;
                StartHoldArm(pt);
            }
        }
    }

    private void Window_PreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (!_touchPoints.ContainsKey(e.TouchDevice)) return;
        var pos = e.GetTouchPoint(this).Position;
        _touchPoints[e.TouchDevice] = pos;
        // 触摸还在动 → 后续一律由触摸坐标驱动，别再用系统模拟出来的鼠标事件
        _lastTouchDriveTick = Environment.TickCount64;

        // ★ 单指：唯一的手势驱动点。滚动判定、卡片跟手、落点高亮全在这条路径上做。
        //   这里曾经什么都不干（只处理双指滚动），于是"浮起时卡片画对了一次，之后手指动、
        //   卡片不动"—— 因为位置只由 PreviewMouseMove 更新，而触摸时那一套基本收不到。
        if (_touchPoints.Count == 1)
        {
            if (_singleScroll)
            {
                // 已经进入单指滚动：继续用触摸坐标滚（比模拟鼠标准）
                ScheduleScroll?.ScrollToVerticalOffset(_singleScrollOrigin - (pos.Y - _singleScrollStartY));
                TrackFlingVelocity(pos.Y);
                return;
            }
            DrivePointer(pos, e.TouchDevice);
            return;
        }

        if (!_touchScrolling || _touchPoints.Count < 2) return;

        var pts = _touchPoints.Values.Take(2).ToList();
        double mid = (pts[0].Y + pts[1].Y) / 2;

        // 手指往上划（mid 变小）→ 内容往上走 → 偏移变大
        ScheduleScroll.ScrollToVerticalOffset(_scrollOrigin - (mid - _scrollOriginMid));

        var now = DateTime.UtcNow;
        double dt = (now - _lastMidAt).TotalSeconds;
        if (dt > 0.001)
        {
            double v = (mid - _lastMid) / dt;
            _flingVelocity = _flingVelocity * 0.7 + v * 0.3;   // 平滑，别被手指抖动带偏
            _lastMid = mid;
            _lastMidAt = now;
        }

        e.Handled = true;
    }

    private void Window_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        _touchPoints.Remove(e.TouchDevice);
        CancelPress();

        // 单指翻页松手（触摸提升出来的鼠标 Up 有时会先到、有时后到，两边都判一次，幂等）
        if (_singleScroll && _touchPoints.Count == 0)
        {
            EndSingleScroll();
            StartFling();
            e.Handled = true;
            return;
        }

        if (_touchScrolling && _touchPoints.Count < 2)
        {
            EndTouchScroll();
            e.Handled = true;
            return;
        }

        // 兜底：个别一体机只报触摸、不报"提升出来的鼠标事件"，那样拖动会一直悬着不结算。
        // FinishDragAt 幂等 —— 正常设备上鼠标 Up 已经处理过了，这里什么都不会做。
        if (_touchPoints.Count == 0 && (_dragging || (_touchHoldArmed && _pressFromTouch)))
        {
            FinishDragAt(e.GetTouchPoint(this).Position);
        }

        if (_touchPoints.Count == 0)
        {
            // 轻点结束（或上面刚收尾完）：长按计时器、拖拽候选、自动隐藏暂停一律收干净。
            // 少这一句，500ms 后计时器仍会触发，把一张早就松手的卡片"浮"起来 ——
            // 之后随便划一下，就会把上一次点过的那节课拖走。
            StopHoldArm();
            _dragCandidate = null;
            _dragging = false;
            _gripPress = false;
            _gestureScrolled = false;
            if (_hider != null) _hider.Suspended = false;
        }
    }

    /// <summary>进入双指滚动：清掉拖动 / 长按状态，记下基准点，并捕获所有触摸。</summary>
    private void BeginTouchScroll()
    {
        CancelPress();
        EndDrag();
        if (_singleScroll) EndSingleScroll();   // 单指翻页途中落下第二根手指 → 交给双指接管
        StopFling();

        var pts = _touchPoints.Values.Take(2).ToList();
        _scrollOriginMid = (pts[0].Y + pts[1].Y) / 2;
        _lastMid = _scrollOriginMid;
        _lastMidAt = DateTime.UtcNow;
        _flingVelocity = 0;
        _scrollOrigin = ScheduleScroll.VerticalOffset;
        _touchScrolling = true;

        // 捕获全部触摸：手指划出 ScrollViewer 边界后仍能继续收到 move，否则滚到一半就"粘住"
        foreach (var dev in _touchPoints.Keys.ToList())
        {
            try { ScheduleScroll.CaptureTouch(dev); } catch { }
        }
    }

    private void EndTouchScroll()
    {
        _touchScrolling = false;
        foreach (var dev in _touchPoints.Keys.ToList())
        {
            try { ScheduleScroll.ReleaseTouchCapture(dev); } catch { }
        }
        StartFling();
    }

    /// <summary>彻底退出滚动（切设置、窗口关闭时用），不触发惯性。</summary>
    private void StopTouchScroll()
    {
        _touchScrolling = false;
        _touchPoints.Clear();
        if (_singleScroll) EndSingleScroll();
        StopFling();
    }

    /// <summary>
    /// 抬手后的惯性滑行。衰减取 0.92/帧（约 0.7 秒滑停），比系统滚动短促一些 ——
    /// 悬浮窗只有几百像素高，滑太久反而要再往回找。
    /// </summary>
    private void StartFling()
    {
        StopFling();
        if (Math.Abs(_flingVelocity) < 120) return;      // 太慢不值得滑

        double v = Math.Clamp(_flingVelocity, -3000, 3000);
        _flingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flingTimer.Tick += (_, _) =>
        {
            v *= 0.92;
            if (Math.Abs(v) < 60) { StopFling(); return; }

            double before = ScheduleScroll.VerticalOffset;
            ScheduleScroll.ScrollToVerticalOffset(before - v * 0.016);
            if (Math.Abs(ScheduleScroll.VerticalOffset - before) < 0.1) StopFling();   // 已经到顶 / 到底
        };
        _flingTimer.Start();
    }

    private void StopFling()
    {
        _flingTimer?.Stop();
        _flingTimer = null;
        _flingVelocity = 0;
    }

    /// <summary>
    /// 诊断（<c>--touch-mode</c>）：输出当前滚动托管方式与手势阈值。
    /// <para>三条硬前提，任何一条不成立手势就会失灵，且截图看不出来：</para>
    /// <para>① <c>PanningMode=None</c> —— 只要不是 None，ScrollViewer 就会抢走触摸，
    /// 触摸不再提升成鼠标事件，单击 / 双击 / 拖动换节全部失效。</para>
    /// <para>② 系统 Press-and-Hold 必须关 —— 否则 500ms 时系统会画圈并自己发一个右键，
    /// 与"静止 500ms 浮起"打架，长按中途突然弹出调课窗。</para>
    /// <para>③ 阈值 10px / 500ms —— "一动就滚、静止才计时"全靠这两个数。</para>
    /// </summary>
    internal string DiagTouchMode()
    {
        string ph = "?", fl = "?";
        try { ph = Stylus.GetIsPressAndHoldEnabled(this).ToString(); } catch { }
        try { fl = Stylus.GetIsFlicksEnabled(this).ToString(); } catch { }
        return $"TwoFinger={_twoFingerScroll} PanningMode={ScheduleScroll?.PanningMode} "
             + $"ScrollableHeight={ScheduleScroll?.ScrollableHeight:0.#} Viewport={ScheduleScroll?.ViewportHeight:0.#} "
             + $"systemPressHold={ph} systemFlicks={fl} slop={GestureSlop} hold={TouchHoldMs}ms";
    }

    private void StartPressTimer()
    {
        CancelPress();
        if (_locked) return;   // 锁定后禁止长按编辑
        _pressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _pressTimer.Tick += (_, _) =>
        {
            var r = _pressRow;
            CancelPress();
            EndDrag();
            if (r != null) OpenEditor(r);
        };
        _pressTimer.Start();
    }

    private void CancelPress()
    {
        if (_pressTimer != null) { _pressTimer.Stop(); _pressTimer = null; }
        _pressRow = null;
    }

    private PeriodRow? FindPeriodRow(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is Border b && b.DataContext is PeriodRow row && row.Period > 0)
                return row;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return null;
    }

    /// <summary>
    /// 命中的元素是不是左列那个"拖动抓手"（<c>Tag="Grip"</c>，见 <c>LessonRowTpl</c>）。
    /// <para>抓手压在左列上 = 用户明确表示"我要搬这一节"：按下即浮起、0 秒起拖，
    /// 而且不参与"一动就滚"的判定 —— 这是给"手指按下去头 10px 容易抖"留的保险。</para>
    /// <para>抓手在 XAML 里必须是 <see cref="Grid"/> 而不是 <see cref="Border"/>，
    /// 否则它会被 <see cref="FindPeriodRow"/> / <see cref="HitTestBorder(System.Windows.Point)"/>
    /// 当成课程行的根 Border，幽灵卡会缩成左列那么宽的一条。</para>
    /// </summary>
    private static bool IsGripElement(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is FrameworkElement fe && fe.Tag is string s && s == "Grip") return true;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return false;
    }

    /// <summary>在可视化树里找第一个左列抓手（诊断用）。</summary>
    private static FrameworkElement? FindGripElement(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is FrameworkElement fe && fe.Tag is string s && s == "Grip") return fe;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var hit = FindGripElement(VisualTreeHelper.GetChild(root, i));
            if (hit != null) return hit;
        }
        return null;
    }

    static DateTime _lastDenyTipAt = DateTime.MinValue;

    /// <summary>
    /// 权限不足提示。3 秒内只弹一次 —— 悬浮窗上连续拖拽 / 长按很容易连点，
    /// 弹一屏"无法修改"既没用又挡视线。
    /// </summary>
    void TipDeny(string msg)
    {
        if ((DateTime.Now - _lastDenyTipAt).TotalSeconds < 3) return;
        _lastDenyTipAt = DateTime.Now;
        MessageBox.Show(msg, "无法修改", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenEditor(PeriodRow row)
    {
        if (string.IsNullOrEmpty(_selectedName)) return;

        if (!UserSession.CanEditClass(_selectedName))
        {
            TipDeny(UserSession.DenyEditClass(_selectedName));
            return;
        }

        var current = row.Lessons.Count > 0 ? row.Lessons[0] : new LessonView();
        var w = new LessonOverrideWindow(_selectedName, row.DayIdx, row.Period, current, _schedule,
            BoardBuilder.ResolveDisplayDate(Store.LoadSettings()))
        {
            Owner = this,
            Topmost = true
        };
        if (w.ShowDialog() == true)
            AppEvents.RaiseScheduleChanged();
    }

    private void ApplyDayDrag(PeriodRow src, PeriodRow tgt)
    {
        if (src.Period == tgt.Period || string.IsNullOrEmpty(_selectedName)) return;

        // 班主任只能改自己班；电教管理员（学生）/ 游客不能改任何班
        if (!UserSession.CanEditClass(_selectedName))
        {
            TipDeny(UserSession.DenyEditClass(_selectedName));
            return;
        }

        var srcLesson = src.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
        if (srcLesson == null) return;
        var tgtLesson = tgt.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));

        string RawLoc(string? lt)
        {
            if (string.IsNullOrEmpty(lt)) return "";
            return lt!.StartsWith(" · ") ? lt.Substring(3).Trim() : lt.Trim();
        }

        var list = Store.LoadOverrides();

        // 撤销快照：整份覆盖表的原始状态。交换只动两条记录，但直接存整份最省事也最安全
        // （不用去猜哪两条是这次写的，5 秒后自动作废）。
        string snapshot = System.Text.Json.JsonSerializer.Serialize(list);

        string TypeOf(LessonView lv) => lv.IsOverridden && !string.IsNullOrEmpty(lv.OverrideType) ? lv.OverrideType : "调";

        if (tgtLesson == null)
        {
            UpsertOverride(list, src.DayIdx, tgt.Period, srcLesson.Subject, srcLesson.Teacher, RawLoc(srcLesson.LocationText), TypeOf(srcLesson));
            UpsertOverride(list, src.DayIdx, src.Period, "", "", "", "调", isEmpty: true);
        }
        else
        {
            UpsertOverride(list, src.DayIdx, tgt.Period, srcLesson.Subject, srcLesson.Teacher, RawLoc(srcLesson.LocationText), TypeOf(srcLesson));
            UpsertOverride(list, src.DayIdx, src.Period, tgtLesson.Subject, tgtLesson.Teacher, RawLoc(tgtLesson.LocationText), TypeOf(tgtLesson));
        }
        Store.SaveOverrides(list);

        // ★ 反馈先行：先让「已交换」立刻出现，再把「通知所有窗口刷新」推到下一帧。
        //   原来 RaiseScheduleChanged 是一次性同步多播，桌面壁纸全量重绘正好横在
        //   「松手 → 看到结果」中间；配合 BackdropService 的 1.2 秒防抖，抬手即出结果。
        // 松手即生效，但给 5 秒反悔窗口 —— 触屏拖错一次不想再拖回去。
        _undoSnapshot = snapshot;
        string a = srcLesson.DisplaySubject;
        string b = tgtLesson?.DisplaySubject ?? "空节";
        ShowAction($"已交换　第{src.Period}节 {a} ⇄ 第{tgt.Period}节 {b}", undo: true, seconds: 5);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { AppEvents.RaiseScheduleChanged(); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpsertOverride(List<LessonOverride> list, int dayIdx, int period,
        string subject, string teacher, string location, string type, bool isEmpty = false)
    {
        if (string.IsNullOrEmpty(_selectedName)) return;

        // ★ 悬浮窗的改动一律只作用于「当前显示日所在的那一周」（设计 D6/D13）：
        //   以前不带生效区间 = 永久生效，下周还顶在课表上。
        //   周起始日跟着设置走，保证与周视图里"本周"是同一段日期。
        var _st = Store.LoadSettings();
        var (from, to) = WeekHelper.WeekWindow(BoardBuilder.ResolveDisplayDate(_st), _st.WeekStartDay);

        // 只清本周该格的手工记录：其他周、以及远程/导入来源的记录不动
        list.RemoveAll(o => o.ClassName == _selectedName && o.DayIdx == dayIdx && o.Period == period
                            && o.Source == "manual" && o.OverlapsRange(from, to));

        // ★ 拖回原位 = 取消调代：与底表该格完全一致时不留记录（否则格子上一直挂着「已调课」）
        if (LessonComparer.IsSameAsBase(_schedule, _selectedName, dayIdx, period,
                                        subject, teacher, location, isEmpty))
            return;

        if (isEmpty)
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedName, DayIdx = dayIdx, Period = period,
                IsEmpty = true, Type = "调", Source = "manual",
                ValidFrom = DateOnly.FromDateTime(from), ValidTo = DateOnly.FromDateTime(to),
                UpdatedAt = DateTime.Now
            });
        }
        else if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedName, DayIdx = dayIdx, Period = period,
                Subject = subject, Teacher = teacher, Location = location,
                Type = type, Source = "manual",
                ValidFrom = DateOnly.FromDateTime(from), ValidTo = DateOnly.FromDateTime(to),
                UpdatedAt = DateTime.Now
            });
        }
    }

    private bool IsWithinButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (ReferenceEquals(dep, BtnHide) || ReferenceEquals(dep, BtnQaUpdate)
                || ReferenceEquals(dep, BtnWp) || ReferenceEquals(dep, BtnTop)
                || ReferenceEquals(dep, BtnLock) || ReferenceEquals(dep, BtnPos))
                return true;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        EndDrag();
        _hider?.Dispose();
        AppEvents.ScheduleChanged -= Refresh;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        AppEvents.BackdropChanged -= UpdateBackdropButton;
        _timer.Stop();
        base.OnClosed(e);
    }
}
