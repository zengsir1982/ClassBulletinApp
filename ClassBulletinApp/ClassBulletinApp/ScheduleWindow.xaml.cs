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

namespace ClassBulletinApp;

public partial class ScheduleWindow : Window
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

    public ScheduleWindow()
    {
        InitializeComponent();
        TodayPanel.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<PeriodRow>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Refresh();

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

        // 快捷胶囊「自动」档要跟窗口宽度联动：拖动缩放后立刻重排，窄窗挤不下就自动收小
        SizeChanged += (_, _) => ApplyBuiltinQuickActionSize();

        Closed += (_, _) =>
        {
            AppEvents.QuickCheckUpdateDone -= OnQuickCheckUpdateDone;
            _qaWatchdog?.Stop();
            _qaWatchdog = null;
        };
    }

    // ---------- 内置快捷：作业 / 缺交 / 便签 / 更新课表 ----------

    /// <summary>
    /// 按当前账号权限决定各快捷是否显示。
    /// <para>★ 隐藏而不是置灰：教室机登学生 / 科任老师时，一排灰按钮既占地方，
    /// 又容易让人以为程序出了故障。全都无权限时整条收起，不留空白。</para>
    /// </summary>
    private void ApplyQuickActionPermissions()
    {
        if (BtnQaHomework == null) return;

        BtnQaHomework.Visibility = UserSession.CanEditBoard ? Visibility.Visible : Visibility.Collapsed;
        BtnQaMissing.Visibility = UserSession.CanEditBoard ? Visibility.Visible : Visibility.Collapsed;
        // 便签与作业 / 缺交同属「编辑板报」能力，同一权限口径
        BtnQaNote.Visibility = UserSession.CanEditBoard ? Visibility.Visible : Visibility.Collapsed;
        // 「更新课表」已移到标题栏，按「管理更新」权限独立显隐（与底部快捷栏无关）
        BtnQaUpdate.Visibility = UserSession.CanManageUpdate ? Visibility.Visible : Visibility.Collapsed;

        // 底部快捷栏（作业 / 缺交 / 便签）全无权限时整条收起
        bool any = BtnQaHomework.Visibility == Visibility.Visible
                || BtnQaMissing.Visibility == Visibility.Visible
                || BtnQaNote.Visibility == Visibility.Visible;
        BuiltinWrap.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnQaHomework_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenHomework();

    private void BtnQaMissing_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenMissing();

    private void BtnQaNote_Click(object sender, RoutedEventArgs e) => AppEvents.RaiseQuickOpenCustomNote();

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

        TxtQaUpdate.Text = "✔ 完成";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_qaUpdating) UpdateQaButtonVisual();
        };
        t.Start();
    }

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
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF))
                : new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFD));
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

            string sig = timeText + "|" + failText;
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
    /// 按设置调整底部三个内置快捷（作业 / 缺交 / 便签）胶囊的大小。
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
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xEF)),
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
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
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
        // 「常驻显示」= 半透明常驻且可交互（不靠边自动隐藏）；只有在布局里显式勾选「鼠标穿透」时才穿透，
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
    }

    private void OnLayoutChanged(PanelLayout? schedule, PanelLayout? notification)
    {
        ApplyCurrentLayout(schedule);
        Refresh();
    }

    private bool IsClassTimeNow() => WeekHelper.IsClassTimeNow(_schedule, _selectedName);

    private void Refresh()
    {
        var s = Store.LoadSettings();
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

        var res = BoardBuilder.Build(_selectedName, _semesterStart, _schedule, displayDate, viewOverrides);

        TxtClass.Text = string.IsNullOrEmpty(res.ClassName)
            ? (_isTeacherMode ? "未选择教师" : "未选择班级")
            : res.ClassName;
        TxtDate.Text = res.DateText;
        TxtWeek.Text = res.WeekLine;

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
    }

    /// <summary>确保有有效的显示对象；原选中项不存在时自动取第一个并回写设置。</summary>
    private static string PickName(string? current, ScheduleFile? sf, AppSettings s, bool teacher)
    {
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
        BtnLock.Background = _locked ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFD));
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
        BtnTop.Background = top ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : Brushes.White;
        BtnTop.Foreground = top ? Brushes.White : Brushes.Black;
        BtnTop.BorderBrush = top ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)) : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
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
            return;
        }

        // 注意：必须用局部变量保存——StartPressTimer() 内部的 CancelPress() 会把 _pressRow 置 null
        var row = FindPeriodRow(e.OriginalSource as DependencyObject);
        _pressRow = row;
        if (row != null)
        {
            _pressPoint = e.GetPosition(this);
            StartPressTimer();
        }

        // 记录拖拽候选（必须是有课的格子）；注意用局部变量 row，避免被 CancelPress 清空
        _dragCandidate = RowHasLesson(row) ? row : null;
        _dragStart = e.GetPosition(this);
        _dragging = false;
        if (_dragCandidate != null)
        {
            // 不在这里 Mouse.Capture：提前捕获会让后续事件的 OriginalSource 变成窗口本身，
            // 导致双击「教师 / 班级行」无法识别；真正开始拖动时再捕获（见 PreviewMouseMove）
            if (_hider != null) _hider.Suspended = true;
        }
    }

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pressTimer != null && _pressTimer.IsEnabled)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) > 8 || Math.Abs(p.Y - _pressPoint.Y) > 8)
                CancelPress();
        }

        if (_dragCandidate == null || _isTeacherMode || _clickThrough) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

        var cur = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(cur.X - _dragStart.X) > 6 || Math.Abs(cur.Y - _dragStart.Y) > 6)
            {
                CancelPress();
                _dragging = true;
                Mouse.Capture(this);   // 真正开始拖动才捕获，保证移出窗口仍能收到 move/up
                if (_hider != null) _hider.Suspended = true;
                Mouse.OverrideCursor = Cursors.Hand;
            }
            else return;
        }
        UpdateDropTarget(cur);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CancelPress();
        bool wasDragging = _dragging;
        var src = _dragCandidate;
        PeriodRow? tgt = null;
        if (wasDragging) tgt = HitTestRow(e.GetPosition(this));
        EndDrag();

        if (wasDragging && src != null && tgt != null && !ReferenceEquals(src, tgt))
            ApplyDayDrag(src, tgt);
    }

    private void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging) EndDrag();
    }

    private void EndDrag()
    {
        ClearHighlight();
        if (_hider != null) _hider.Suspended = false;
        _dragging = false;
        _dragCandidate = null;
        _dropRow = null;
        Mouse.OverrideCursor = null;
        if (Mouse.Captured == this) Mouse.Capture(null);
    }

    private void UpdateDropTarget(System.Windows.Point pt)
    {
        var row = HitTestRow(pt);
        if (ReferenceEquals(row, _dropRow)) return;

        ClearHighlight();
        _dropRow = row;
        if (_dropRow == null || ReferenceEquals(_dropRow, _dragCandidate)) return;

        var border = HitTestBorder(pt);
        if (border == null) return;
        _dropBorder = border;
        _savedBg = border.Background;
        _savedBorder = border.BorderBrush;
        border.Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xEE, 0xFD));
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
    }

    private void ClearHighlight()
    {
        if (_dropBorder != null)
        {
            _dropBorder.Background = _savedBg ?? Brushes.Transparent;
            _dropBorder.BorderBrush = _savedBorder ?? Brushes.Transparent;
        }
        _dropBorder = null;
        _savedBg = null;
        _savedBorder = null;
    }

    private static bool RowHasLesson(PeriodRow? row)
        => row != null && row.Period > 0 && row.Lessons.Any(l => !string.IsNullOrEmpty(l.Subject));

    private PeriodRow? HitTestRow(System.Windows.Point pt)
        => HitTestBorder(pt)?.DataContext as PeriodRow;

    private Border? HitTestBorder(System.Windows.Point pt)
    {
        Border? found = null;
        VisualTreeHelper.HitTest(this,
            null,
            result =>
            {
                DependencyObject? dep = result.VisualHit;
                while (dep != null)
                {
                    if (dep is Border b && b.DataContext is PeriodRow row && row.Period > 0)
                    {
                        found = b;
                        return HitTestResultBehavior.Stop;
                    }
                    dep = VisualTreeHelper.GetParent(dep);
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(pt));
        return found;
    }

    private void Window_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (_isTeacherMode) return;
        if (_clickThrough) return;
        _pressRow = FindPeriodRow(e.OriginalSource as DependencyObject);
        if (_pressRow != null)
        {
            _pressPoint = e.GetTouchPoint(this).Position;
            StartPressTimer();
        }
    }

    private void Window_PreviewTouchUp(object sender, TouchEventArgs e) => CancelPress();

    private void StartPressTimer()
    {
        CancelPress();
        if (_locked) return;   // 锁定后禁止长按编辑
        _pressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
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
        AppEvents.RaiseScheduleChanged();
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
