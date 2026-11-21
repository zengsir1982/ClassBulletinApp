using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;

namespace ClassBulletinApp;

public partial class NotificationWindow : Window, IGlassSurface
{
    private AutoHider? _hider;

    /// <summary>鼠标移过来时让通知条自己让开（变透明），离开再恢复；判定靠全局光标轮询。</summary>
    private HoverFader? _fader;
    private PanelLayout? _layout;
    private ScheduleFile? _schedule;
    private string? _className;
    private bool _clickThrough;
    private DispatcherTimer? _danmakuTimer;

    // ===== r131 徽章（换课 / 倒计时 / 隐藏 三态）=====
    /// <summary>徽章详情浮层的自动收起计时器。</summary>
    private DispatcherTimer? _badgePopupTimer;
    /// <summary>跨零点检测：徽章里的"今天"和实际倒计时天数都要在过零点后重算。</summary>
    private DispatcherTimer? _dayRolloverTimer;
    private DateTime _badgeDay = DateTime.Today;
    /// <summary>诊断注入：当天换课列表（null = 从 Store 读）。</summary>
    internal List<LessonOverride>? DiagTodayOverrides;
    /// <summary>诊断注入：倒计时卡（null = 从 Store 读）。</summary>
    internal CountdownBoard? DiagCountdown;
    /// <summary>诊断注入：倒计时徽章开关（null = 从 Store 读）。</summary>
    internal bool? DiagShowCountdownBadge;

    /// <summary>诊断（--ui-check）用：确认「移上去变透明」这条链真的接上了（穿透态下肉眼很难验证）。</summary>
    internal HoverFader? Fader => _fader;

    /// <summary>程序化应用布局期间置 true，避免把自动定位 / 自动隐藏的位移当作拖动保存。</summary>
    private bool _applyingLayout;

    public NotificationWindow()
    {
        InitializeComponent();

        // 所见即所得：按住通知条拖动窗口（双击仍可编辑通知）；鼠标移到边沿 / 四角出现双向箭头时拖动改大小。
        // 只在用户松手时落盘，避免把自动布局 / 自动隐藏的坐标当成用户位置。
        void OnStart()
        {
            if (_hider != null) _hider.Suspended = true;
            if (_fader != null) _fader.Suspended = true;
        }
        void OnEnd()
        {
            if (_hider != null) _hider.Suspended = false;
            if (_fader != null) _fader.Suspended = false;
            SavePlacement();
            ApplyCurrentLayout();
            AppEvents.RaiseLayoutChanged();   // 自动隐藏开关变化后同步隐藏按钮标签等
        }

        FloatChrome.Enable(this, canInteract: () => !_clickThrough,
            onGestureStart: OnStart, onGestureEnd: OnEnd);
        FloatChrome.EnableDrag(Bar, this,
            onDragStart: OnStart, onDragEnd: OnEnd,
            canDrag: () => !_clickThrough);
    
            GlassChrome.Prepare(this);}

    /// <summary>记住拖动 / 缩放后的位置与大小，并把锚点更新为最近的屏幕边（自动隐藏往那边滑出）。</summary>
    private void SavePlacement()
    {
        if (_applyingLayout) return;
        try
        {
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            // 自动隐藏态窗口在屏幕外：此刻的坐标不代表用户意图，不保存
            if (!FloatingHelper.IsMostlyOnScreen(this, 0.3)) return;

            var s = Store.LoadSettings();
            var l = s.NotificationLayout ??= new PanelLayout();
            var clamped = FloatingHelper.ClampRectDip(this, new Rect(Left, Top, Width, Height));

            // 仅当窗口位置确实发生移动时才据「是否贴边」自动切换自动隐藏，避免单纯改大小误触发。
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
        AppEvents.NotificationChanged += StartMarquee;
        AppEvents.LayoutChanged += OnLayoutChanged;
        AppEvents.ReminderDanmaku += OnReminderDanmaku;
        // r131：徽章三条刷新链 —— 调代课变动、倒计时便签变动、 crosses midnight。
        AppEvents.ScheduleChanged += OnBadgeDataChanged;
        AppEvents.CountdownChanged += OnBadgeDataChanged;
        _dayRolloverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dayRolloverTimer.Tick += (_, _) =>
        {
            if (_badgeDay != DateTime.Today) { _badgeDay = DateTime.Today; RefreshBadge(); }
        };
        _dayRolloverTimer.Start();
        MarqueeHost.SizeChanged += (_, _) => StartMarquee();
        StartMarquee();
        RefreshBadge();
    }

    /// <summary>
    /// 换课数据变化时：若徽章从「非换课」翻成「换课」，自动弹一次详情（相当于方案二的弹幕）；
    /// 倒计时变化只静默改徽章，不弹（倒计时不是刚发生的动作，弹了反而打扰）。
    /// </summary>
    private void OnBadgeDataChanged()
    {
        var before = _badgeKind;
        RefreshBadge();
        if (before != BadgeKind.Changes && _badgeKind == BadgeKind.Changes && BadgePopup != null && !BadgePopup.IsOpen)
            Badge_Click(this, new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left));
    }

    private void ApplyCurrentLayout(PanelLayout? live = null)
    {
        var s = Store.LoadSettings();
        _layout = live ?? s.NotificationLayout;
        // 「常驻显示」= 默认不透明常驻且完全可交互（双击可编辑、隐藏按钮可单击）；
        // 只有在布局里显式勾选「鼠标穿透」时才打开穿透（此时通知条与隐藏按钮仍保持可点击）。
        bool topShow = !_layout.AutoHide;
        _clickThrough = _layout.ClickThrough && !_layout.AutoHide;
        _schedule = Store.LoadSchedule();
        _className = s.SelectedClass;
        _applyingLayout = true;
        try
        {
            FloatingHelper.ApplyLayout(this, _layout, _clickThrough, topShow ? _layout.OverlayOpacity : _layout.Opacity);
        }
        finally { _applyingLayout = false; }
        if (_clickThrough)
            FloatingHelper.SetClickThrough(this, true);
        else
            FloatingHelper.SetClickThrough(this, false);

        // 穿透态下条上的「自动隐藏」按钮点不到（整窗都不接收鼠标），留着只会误导 —— 直接收起来。
        BtnHide.Visibility = _clickThrough ? Visibility.Collapsed : Visibility.Visible;
        Bar.ToolTip = _clickThrough
            ? "鼠标移过来会自动隐藏；要拖动或切换自动隐藏，请先到「布局设置 → 通知悬浮窗」取消鼠标穿透"
            : "按住拖动可移动位置；拉窗口边沿可改变大小；双击可编辑通知 / 公告";

        TxtMarquee.FontSize = Math.Max(12, _layout.FontSize);
        ApplyBarStyle(_layout);
        BtnHide.Content = _layout.AutoHide ? "常驻显示" : "自动隐藏";
        _hider?.Dispose();
        _hider = (_layout.AutoHide || _layout.AutoHideDuringClass)
            ? new AutoHider(this, _layout, () => _layout.AutoHide || (_layout.AutoHideDuringClass && WeekHelper.IsClassTimeNow(_schedule, _className)))
            : null;

        // 鼠标移过来就自己让开（默认开）。判定走全局光标轮询 —— 穿透态下窗口收不到鼠标消息。
        // 基准透明度跟着「常驻 / 自动隐藏」走，与 ApplyLayout 上面设的那个值保持一致。
        // ★ 只在「常驻 + 穿透」时启用：取消穿透说明用户要交互，那时把条子淡到看不见等于点空气。
        _fader ??= new HoverFader(this,
            () => _layout != null && _layout.HoverFade && _layout.ClickThrough && !_layout.AutoHide,
            () => _layout == null ? 1.0
                : _layout.AutoHide ? _layout.Opacity : _layout.OverlayOpacity);
        _fader.Resync();   // ApplyLayout 刚直接写过 Opacity，把淡出状态重新对齐（否则会闪回来）
    }

    /// <summary>应用通知条的外观：字体、文字颜色、条背景色（留空则用界面主题默认色）。</summary>
    private void ApplyBarStyle(PanelLayout l)
    {
        try
        {
            TxtMarquee.FontFamily = string.IsNullOrWhiteSpace(l.FontFamily)
                ? new FontFamily("Microsoft YaHei UI")
                : new FontFamily(l.FontFamily);
            TxtMarquee.Foreground = ThemeHelper.Brush(l.TextColor, Color.FromRgb(0xFF, 0xFF, 0xFF));

            if (string.IsNullOrWhiteSpace(l.BarColor))
            {
                Bar.SetResourceReference(Border.BackgroundProperty, "PrimaryDarkBrush");
            }
            else
            {
                Bar.Background = ThemeHelper.Brush(l.BarColor, Color.FromRgb(0x1E, 0x3A, 0x6B));
            }
            ApplyGlass();
        }
        catch { }
    }

    /// <summary>
    /// IGlassSurface：玻璃底色只改 Bar.Background，绝不碰 Window.Opacity —— HoverFader
    /// 用 HoldEnd 动画占着整窗透明度，Resync() 还会清掉任何挂上去的动画。
    /// </summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
    {
        // 玻璃会把 Background 写成本地值、切断动态资源绑定；每次刷新先重新绑定一次，
        // 保证切主题后取到的是最新的 PrimaryDarkBrush，而不是旧主题的快照。
        if (_layout != null && string.IsNullOrWhiteSpace(_layout.BarColor))
            Bar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PrimaryDarkBrush");
        GlassBrush.Apply(Bar, settings);
    }

    private void OnLayoutChanged(PanelLayout? schedule, PanelLayout? notification)
    {
        ApplyCurrentLayout(notification);
        StartMarquee();
        RefreshBadge();   // r131：倒计时徽章开关可能在布局窗口里刚被改过
    }

    // ---------- 提醒弹幕：临时把通知条内容替换为提醒文案，数秒后恢复 ----------
    private void OnReminderDanmaku(string text, int seconds)
    {
        if (!IsLoaded || string.IsNullOrWhiteSpace(text)) return;
        _danmakuTimer?.Stop();
        TxtMarquee.Text = text;
        TxtMarquee.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = TxtMarquee.DesiredSize.Width;
        double hostW = MarqueeHost.ActualWidth;
        var tt = new TranslateTransform();
        TxtMarquee.RenderTransform = tt;
        tt.X = hostW > 0 ? (hostW - tw) / 2 : 0;   // 弹幕居中显示，不滚动

        _danmakuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(3, seconds)) };
        _danmakuTimer.Tick += (_, _) =>
        {
            _danmakuTimer?.Stop();
            _danmakuTimer = null;
            StartMarquee();   // 恢复为正常通知滚动
        };
        _danmakuTimer.Start();
    }

    // ---------- 双击编辑通知（穿透态下通知条本身仍保持可交互） ----------
    private void Bar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            var w = new NotificationEditWindow { Owner = this, Topmost = true };
            w.ShowDialog();
            e.Handled = true;
        }
    }

    // ---------- 隐藏按钮：快速切换自动隐藏 ----------
    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        bool turnOn = !s.NotificationLayout.AutoHide;
        s.NotificationLayout.AutoHide = turnOn;
        if (turnOn) s.NotificationLayout.ClickThrough = false;   // 自动隐藏与鼠标穿透互斥
        Store.SaveSettings(s);
        ApplyCurrentLayout();
        AppEvents.RaiseLayoutChanged();
    }

    /// <summary>
    /// 跑马灯只滚「置顶优先 + 最新」的最多<i>两条</i>。
    /// <para>★ 为什么限两条：这原本是把全部通知拼成一段文本无限滚动，条数一多整圈被拉长，
    /// 今天的通知被压在一堆旧通知后面。完整清单交给并行的「事项列表」便签，
    /// 跑马灯退回它最擅长的事 —— 提醒「刚来的这两件事」。</para>
    /// </summary>
    internal const int MarqueeMax = 2;

    private void StartMarquee()
    {
        if (!IsLoaded) return;
        var l = Store.LoadSettings().NotificationLayout;
        var live = Store.LoadNotifications().Where(x => x.IsLive).ToList();
        var notifs = live.OrderByDescending(n => n.Pinned)
                         .ThenByDescending(n => n.CreatedAt)
                         .Take(MarqueeMax)
                         .ToList();
        int more = live.Count - notifs.Count;

        // r72：跑马灯用 Inlines 逐条渲染，标题 / 正文各自带自己的格式。
        // 仍是一个 TextBlock，滚动用的 Measure / TranslateTransform 口径完全不变。
        TxtMarquee.Inlines.Clear();
        double baseSize = TxtMarquee.FontSize > 0 ? TxtMarquee.FontSize : Math.Max(12, l.FontSize);
        if (notifs.Count == 0)
        {
            TxtMarquee.Inlines.Add(new Run("暂无通知（双击通知条可编辑）"));
        }
        else
        {
            for (int i = 0; i < notifs.Count; i++)
            {
                var n = notifs[i];
                if (i > 0) TxtMarquee.Inlines.Add(new Run("　　　◆　　　"));
                if (!string.IsNullOrEmpty(n.Title))
                {
                    var r = new Run(n.Title + "：")
                    {
                        FontSize = HwTextRender.ScaleSize(n.TitleStyle, baseSize),
                        FontWeight = HwTextRender.BoldOr(n.TitleStyle, true) ? FontWeights.Bold : FontWeights.Normal
                    };
                    if (!string.IsNullOrWhiteSpace(n.TitleStyle?.Color))
                        r.Foreground = ThemeHelper.Brush(n.TitleStyle!.Color, Colors.White);
                    TxtMarquee.Inlines.Add(r);
                }
                var rc = new Run(n.Content ?? "")
                {
                    FontSize = HwTextRender.ScaleSize(n.ContentStyle, baseSize),
                    FontWeight = HwTextRender.BoldOr(n.ContentStyle, false) ? FontWeights.Bold : FontWeights.Normal
                };
                if (!string.IsNullOrWhiteSpace(n.ContentStyle?.Color))
                    rc.Foreground = ThemeHelper.Brush(n.ContentStyle!.Color, Colors.White);
                TxtMarquee.Inlines.Add(rc);
            }
            if (more > 0) TxtMarquee.Inlines.Add(new Run("　　　◆　　　另有 " + more + " 条"));
        }

        TxtMarquee.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = TxtMarquee.DesiredSize.Width;
        double hostW = MarqueeHost.ActualWidth;
        if (hostW <= 0) { Dispatcher.BeginInvoke(new Action(StartMarquee)); return; }

        var tt = new TranslateTransform();
        TxtMarquee.RenderTransform = tt;

        if (textW <= hostW)
        {
            tt.X = (hostW - textW) / 2; // 短文本居中，不滚动
            return;
        }

        double speed = Math.Max(20, l.MarqueeSpeed);
        double dur = Math.Max(6, (textW + hostW) / speed);
        var anim = new DoubleAnimation(hostW, -textW - 30, TimeSpan.FromSeconds(dur))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ==================== r131 徽章：换课 > 倒计时 > 隐藏 ====================

    /// <summary>
    /// 徽章三态的唯一裁决入口。当天（今天这一列）有生效的调代课 = 红色换课徽章；
    /// 没有且开关开且有未过期倒计时 = 琥珀色徽章；都没有 = 隐藏。
    /// 整个过程与跑马灯 completely 无关，不碰 TxtMarquee / StartMarquee，所以不占那两条名额。
    /// </summary>
    internal void RefreshBadge()
    {
        if (Badge == null) return;
        _badgeDay = DateTime.Today;
        try
        {
            var settings = Store.LoadSettings();
            string? cls = settings.SelectedClass;
            var today = DateTime.Today;

            // ① 当天生效的换课
            // ★ r132 只统计「今天」这一列：DayIdx 必须等于今天的日历星期（0=周一..6=周日）。
            //   旧写法直接拿 ForDate（只按生效区间筛、不看星期），一条周一到周五有效的调课
            //   在周一那天就会被算成"今天"的换课，于是徽章显示的是整周总数。
            // ★ 同时按格子归并（MapForDate）：同一格既有导入临时表又有手动改课时只算一处，
            //   这样徽章数字与课表上真正变了样的格子数一致。
            // r133：星期几改由校历裁决 —— 周六补周三的课时，今天这一列是"周三"，
            //   调课也按被映射的那天取。
            var overrides = DiagTodayOverrides ?? Store.LoadOverrides();
            var schedule = Store.LoadSchedule();
            var plan = SchoolCalendar.Resolve(schedule, today, settings.SemesterStartMonday);
            int todayIdx = plan.DayIdx;   // 0=周一 .. 6=周日
            var todayList = plan.UsesOverrides
                ? OverrideExpander.MapForDate(overrides, cls, plan.OverrideDate).Values
                    .Where(o => o.DayIdx == todayIdx)
                    .OrderBy(o => o.Period).ToList()
                : new List<LessonOverride>();

            if (todayList.Count > 0)
            {
                ShowBadge("今日换课", todayList.Count.ToString(), "#C0392B", "#FFD166", "#7A2B12",
                    tooltip: "点开看今天的调课 / 代课明细（只统计当前班「" + (cls ?? "") + "」）");
                _badgeKind = BadgeKind.Changes;
                _badgeChanges = todayList;
                return;
            }

            // ② 没有换课：开关开 + 有未过期倒计时 → 琥珀徽章（最近一条）
            bool showCd = DiagShowCountdownBadge ?? settings.NotificationLayout?.ShowCountdownBadge == true;
            if (showCd)
            {
                var board = DiagCountdown ?? Store.LoadCountdown();
                var next = board.SortedItems().FirstOrDefault(x => x.DaysLeft() >= 0);
                if (next != null)
                {
                    int days = next.DaysLeft();
                    string val = days == 0 ? "就是今天" : "还有 " + days + " 天";
                    ShowBadge(ShortName(next.Name), val, "#E08A1E", "#FFE0A3", "#7A4A00",
                        tooltip: "点开看倒计时（共 " + board.SortedItems().Count + " 条）；关闭此徽章请到 布局设置 → 通知悬浮窗");
                    _badgeKind = BadgeKind.Countdown;
                    _badgeChanges = null;
                    return;
                }
            }

            // ③ 都没有：隐藏
            Badge.Visibility = Visibility.Collapsed;
            BadgePopup.IsOpen = false;
            _badgeKind = BadgeKind.None;
            _badgeChanges = null;
        }
        catch
        {
            // 读设置 / 底表失败一律隐藏徽章，绝不让通知条本身崩掉
            Badge.Visibility = Visibility.Collapsed;
            _badgeKind = BadgeKind.None;
            _badgeChanges = null;
        }
    }

    /// <summary>徽章最多显示 6 个字，超长截断 + 省略号。</summary>
    internal static string ShortName(string name)
    {
        string s = (name ?? "").Trim();
        return s.Length <= 6 ? s : s.Substring(0, 6) + "…";
    }

    private void ShowBadge(string label, string value, string bg, string valueBg, string valueFg, string tooltip)
    {
        BadgeLabel.Text = label;
        BadgeValue.Text = value;
        // 徽章字号跟着通知条字幕字号走（至少 16、至多与字幕齐平），大屏远距离才看得清
        double baseFs = TxtMarquee.FontSize > 0 ? TxtMarquee.FontSize : 28;
        double badgeFs = Math.Max(16, Math.Min(baseFs, 34));
        BadgeLabel.FontSize = badgeFs;
        BadgeValue.FontSize = Math.Max(14, badgeFs - 2);
        Badge.Background = ThemeHelper.Brush(bg, System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        BadgeValue.Background = ThemeHelper.Brush(valueBg, System.Windows.Media.Color.FromRgb(0xFF, 0xD1, 0x66));
        BadgeValue.Foreground = ThemeHelper.Brush(valueFg, System.Windows.Media.Color.FromRgb(0x7A, 0x2B, 0x12));
        Badge.ToolTip = tooltip;
        Badge.Visibility = Visibility.Visible;
    }

    private enum BadgeKind { None, Changes, Countdown }
    private BadgeKind _badgeKind = BadgeKind.None;
    private List<LessonOverride>? _badgeChanges;

    /// <summary>点徽章弹出详情；再点收起。</summary>
    private void Badge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_clickThrough) return;   // 穿透态下整窗不接鼠标，这里只是保险
        e.Handled = true;
        if (BadgePopup.IsOpen) { CloseBadgePopup(); return; }
        if (_badgeKind == BadgeKind.None) return;

        BadgePopupBody.Children.Clear();
        if (_badgeKind == BadgeKind.Changes)
        {
            AddPopupTitle("今日 " + (_badgeChanges?.Count ?? 0) + " 处换课", "#C0392B");
            foreach (var o in (_badgeChanges ?? new List<LessonOverride>()).Take(6))
            {
                string type = string.IsNullOrEmpty(o.Type) ? (o.Source == "manual" ? "调" : "调") : o.Type;
                string subj = o.IsEmpty ? "空课" : (string.IsNullOrEmpty(o.Subject) ? "—" : o.Subject);
                string detail = "第" + o.Period + "节　" + subj
                    + (string.IsNullOrEmpty(o.Teacher) ? "" : "　" + o.Teacher)
                    + (string.IsNullOrEmpty(o.Note) ? "" : "（" + o.Note + "）");
                AddPopupRow(detail, type == "代" ? "#C0392B" : "#BA7517");
            }
            int more = (_badgeChanges?.Count ?? 0) - 6;
            if (more > 0) AddPopupRow("…另有 " + more + " 处，详见课表", "#888780");
        }
        else if (_badgeKind == BadgeKind.Countdown)
        {
            var board = DiagCountdown ?? Store.LoadCountdown();
            AddPopupTitle("倒计时", "#B87333");
            foreach (var it in board.SortedItems().Where(x => x.DaysLeft() >= 0).Take(6))
                AddPopupRow(it.Name + "　" + it.DaysText(),
                    it.DaysLeft() == 0 ? "#C0392B" : "#B87333");
        }

        BadgePopup.IsOpen = true;
        _badgePopupTimer?.Stop();
        _badgePopupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _badgePopupTimer.Tick += (_, _) => CloseBadgePopup();
        _badgePopupTimer.Start();
    }

    private void CloseBadgePopup()
    {
        _badgePopupTimer?.Stop();
        _badgePopupTimer = null;
        BadgePopup.IsOpen = false;
    }

    private void AddPopupTitle(string text, string color)
    {
        BadgePopupBody.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(color, System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B)),
            Margin = new System.Windows.Thickness(0, 0, 0, 6)
        });
    }

    private void AddPopupRow(string text, string color)
    {
        var row = new DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, Foreground = System.Windows.Media.Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        BadgePopupBody.Children.Add(row);
    }

    private bool IsWithinButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (ReferenceEquals(dep, BtnHide) || ReferenceEquals(dep, Badge)) return true;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hider == null) SavePlacement();   // 自动隐藏态下窗口在屏外，别把屏外坐标存进去
        _hider?.Dispose();
        _fader?.Dispose();
        _danmakuTimer?.Stop();
        _badgePopupTimer?.Stop();
        _dayRolloverTimer?.Stop();
        AppEvents.NotificationChanged -= StartMarquee;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        AppEvents.ReminderDanmaku -= OnReminderDanmaku;
        AppEvents.ScheduleChanged -= OnBadgeDataChanged;
        AppEvents.CountdownChanged -= OnBadgeDataChanged;
        base.OnClosed(e);
    }
}
