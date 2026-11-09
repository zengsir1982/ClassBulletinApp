using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 项目同时引用 System.Windows.Forms（托盘），以下 WPF 类型存在歧义，统一别名
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfMessageBox = System.Windows.MessageBox;
using WpfCursors = System.Windows.Input.Cursors;

namespace ClassBulletinApp;

/// <summary>周视图交互模式。</summary>
public enum WeekViewMode
{
    /// <summary>控制面板：右键某格 = 安排调课/代课；单击已安排格 = 二次确认后清除；可拖拽。</summary>
    Manage,
    /// <summary>独立周窗口：右键某格 = 清除；可拖拽。</summary>
    Float
}

/// <summary>
/// 整周课表视图（周一..周日 × 各节次），供控制面板与独立周窗口共用。
/// </summary>
public partial class WeekView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(WeekViewMode), typeof(WeekView),
        new PropertyMetadata(WeekViewMode.Float, OnModeChanged));

    public WeekViewMode Mode
    {
        get => (WeekViewMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WeekView v) v.ApplyModeHint();
    }

    private string? _selectedName;     // 当前显示的班级名或教师名
    private bool _isTeacher;           // true = 显示教师个人课表（只读）
    private string _semesterStart = "";
    private DateTime _monday;          // 这一周的「第一天」（跟着周起始日设置，默认周日）
    private int _weekStartDay = WeekHelper.DefaultWeekStartDay;
    private WeekCell? _pressCell;
    private System.Windows.Point _dragStart;
    private string _editType = "调";   // 拖动产生的覆盖类型
    private bool _weekStartGuard;      // 回填周起始日下拉时抑制 SelectionChanged

    // ---------- 手势（与悬浮课表 ScheduleWindow 完全同一套规则，r46） ----------
    //
    //   单击 / 双击          → 系统默认（触摸提升成鼠标，不拦）
    //   位移 >10px 且未浮起  → 滚动整周（本次按下永久失能：不拖、不弹编辑）
    //   静止满 0.5 秒        → 浮起（幽灵卡跟手）
    //        ├ 原地松手       → 等同右键：Manage=安排调课/代课，Float=清除本格
    //        └ 移 ≥10px      → 拖动，松手落到哪格就换哪格
    //
    // ★ 这里以前用 DragDrop.DoDragDrop：它在触摸屏上根本走不通 —— 触摸被系统模拟成鼠标后
    //   DoDragDrop 会 pump 消息并吃掉触摸，手指一松就被当成"取消"，所以"周课表拖不动"。
    //   现在改成和悬浮课表一样：自己画幽灵卡、自己算落点。
    private const int HoldMs = 500;       // 静止按满 → 浮起
    private const double Slop = 10;       // 一动就滚的临界；也是浮起后起拖的位移
    private const double EdgeZone = 40;   // 拖动时离上下边缘多近自动滚动

    /// <summary>
    /// 拖动时要不要"浮起"一张跟手的幽灵卡。
    /// <para>r47 曾按用户要求关掉，只留原位虚框；实测后用户反馈「有张卡跟着手确实感觉好一点」，
    /// r48 重新打开。现在坐标已经全部统一到 <see cref="DragLayer"/> 画布，卡片不会再跑到手指下方两格多。
    /// 留成 static readonly 开关（不能用 const，否则关闭分支会触发 CS0162 不可达代码警告），方便再切回去。</para>
    /// </summary>
    private static readonly bool ShowFloatingCard = true;

    private readonly Dictionary<TouchDevice, System.Windows.Point> _touchPoints = new();
    private readonly List<(WeekCell Cell, Border Border)> _cells = new();
    private System.Windows.Point _touchStartPt, _lastDragPt;
    private long _lastTouchDownTick = -100000, _lastTouchDriveTick = -100000;
    private bool _pressFromTouch, _armed, _dragging, _gestureScrolled, _inSetup, _singleScroll, _touchScrolling;
    // TouchDown 已经就地完成初始化（含长按计时器）——提升出来的 MouseDown 不要再清一遍状态
    private bool _touchInitDone;
    private DispatcherTimer? _holdTimer, _edgeTimer;
    private Border? _srcBorder;            // 被拿起来的源格
    private Border? _ghost;                // 跟手的幽灵卡（VisualBrush 复刻源格）
    private System.Windows.Shapes.Rectangle? _slot;   // 原位虚线框
    private double _grabDX, _grabDY, _armSavedOpacity = 1;
    private Border? _dropBorder;           // 当前落点格
    private System.Windows.Media.Brush? _dropSavedBrush;
    private System.Windows.Thickness _dropSavedThick;

    // 滚动 / 惯性
    private double _scrollOrigin, _scrollStartY, _lastScrollY, _flingV;
    private DateTime _lastScrollAt;
    private DispatcherTimer? _flingTimer;

    public WeekView()
    {
        InitializeComponent();
        _monday = WeekHelper.WeekStart(DateTime.Now, _weekStartDay);
        Loaded += (_, _) => { AppEvents.ScheduleChanged += OnScheduleChanged; PopulateWeekStartCombo(); Rebuild(); };
        Unloaded += (_, _) => { AppEvents.ScheduleChanged -= OnScheduleChanged; EndDrag(true); };

        // 控制面板停在别的页 / 主窗口最小化时攒下的重建，切回本页再补。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _rebuildPending) { _rebuildPending = false; Rebuild(); }
        };

        // 手势统一挂在这里（而不是每个格子一份 lambda）：格子在 Rebuild 时整批重建，
        // 事件挂在控件级才不会漏、也不会残留旧格子的闭包。
        PreviewMouseLeftButtonDown += OnPointerDown;
        PreviewMouseMove += OnPointerMove;
        PreviewMouseLeftButtonUp += OnPointerUp;
        PreviewTouchDown += OnTouchDown;
        PreviewTouchMove += OnTouchMove;
        PreviewTouchUp += OnTouchUp;
        LostMouseCapture += (_, _) => { if (_dragging && Mouse.Captured != this) EndDrag(); };
    }

    // ---------- 周起始日（放在「清除本周调课」右边） ----------
    private void PopulateWeekStartCombo()
    {
        if (CbWeekStartDay == null) return;
        int cur = WeekHelper.NormalizeStartDay(Store.LoadSettings().WeekStartDay);
        _weekStartGuard = true;
        try
        {
            CbWeekStartDay.Items.Clear();
            for (int d = 0; d < 7; d++)
                CbWeekStartDay.Items.Add(new ComboBoxItem { Content = WeekHelper.StartDayName(d), Tag = d });
            CbWeekStartDay.SelectedIndex = cur;
        }
        finally { _weekStartGuard = false; }
    }

    private void CbWeekStartDay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_weekStartGuard) return;
        if (CbWeekStartDay.SelectedItem is not ComboBoxItem ci || ci.Tag is not int d) return;

        var s = Store.LoadSettings();
        if (s.WeekStartDay == d) return;
        s.WeekStartDay = d;
        Store.SaveSettings(s);

        // 先把自己这一天对齐到新的周首日（Rebuild 里也会幂等地再对齐一次），
        // 再广播：周视图 / 悬浮窗 / 桌面背景一起换周。
        _weekStartDay = WeekHelper.NormalizeStartDay(d);
        _monday = WeekHelper.WeekStart(_monday, _weekStartDay);
        AppEvents.RaiseScheduleChanged();   // 自己也订阅了，会同步重建
        TxtStatus.Text = $"周视图已改为「一周从{WeekHelper.StartDayName(d)}开始」。";
    }

    // ---------- 周导航 ----------
    private void BtnPrevWeek_Click(object sender, RoutedEventArgs e) { _monday = _monday.AddDays(-7); Rebuild(); }
    private void BtnThisWeek_Click(object sender, RoutedEventArgs e) { _monday = WeekHelper.WeekStart(DateTime.Now, _weekStartDay); Rebuild(); }
    private void BtnNextWeek_Click(object sender, RoutedEventArgs e) { _monday = _monday.AddDays(7); Rebuild(); }

    private void ApplyModeHint()
    {
        if (!IsLoaded || TxtHint == null) return;
        TxtHint.Text = Mode == WeekViewMode.Manage
            ? (_isTeacher
                ? "当前显示的是教师个人周课表（只读）：格子中的第二行为所教班级，双击班级名称可切换到该班级的周课表。要安排调课 / 代课，请先到顶栏「班级」下拉切换到对应班级。"
                : "操作：鼠标按住课卡直接拖；手指按住课卡 0.5 秒（浮起）再拖 —— 目标有课则互换，为空则移动并清空原格。手指原地按住松手 = 右键（打开调课安排）；快速划动 / 双指划动 = 滚动。双击教师姓名 = 切换到该教师的个人周课表。橙色=调课，蓝色=代课。")
            : "操作：鼠标按住直接拖；手指按住 0.5 秒浮起再拖 —— 目标有课则互换，为空则移动并清空原格。手指原地按住松手 = 右键（清除本格调课）；快速划动 / 双指划动 = 滚动。";
    }

    // ---------- 构建 ----------
    private bool _rebuildPending;

    /// <summary>
    /// 课表变化 → 重建周视图。
    /// <para><b>不可见时不重建：</b>这个页签在控制面板里，调课多在悬浮课表做，用户大多
    /// 根本没看着它。原先不管看不看得见都重建 56 个格子，把悬浮课表拖动后的反应拖慢。
    /// 这里只记脏标记，切回本页时由 <c>IsVisibleChanged</c> 补上。</para>
    /// </summary>
    private void OnScheduleChanged()
    {
        if (!IsVisible) { _rebuildPending = true; return; }
        Rebuild();
    }

    private void Rebuild()
    {
        var s = Store.LoadSettings();
        _isTeacher = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
        _selectedName = _isTeacher ? s.SelectedTeacher : s.SelectedClass;
        _semesterStart = s.SemesterStartMonday;

        // 周起始日可能在「显示设置」里被改过：重新对齐当前显示周的第一天
        // （对齐是幂等的：本来就是该周首日时不会移动）
        _weekStartDay = WeekHelper.NormalizeStartDay(s.WeekStartDay);
        _monday = WeekHelper.WeekStart(_monday, _weekStartDay);

        ApplyModeHint();   // 依赖 _isTeacher 提供正确的操作提示

        // 教师个人课表与班级课表分别取自各自的存储
        var rawOverrides = Store.LoadOverrides();
        var schedule = _isTeacher ? Store.LoadTeacherSchedule() : Store.LoadSchedule();
        // 教师模式：把班级层面的调课/代课记录（含临时课表导入的）换算到这位教师身上，
        // 使周视图优先反映临时课表的最新安排
        var overrides = _isTeacher
            ? TeacherOverrideApplier.BuildTeacherOverrides(schedule, _selectedName, rawOverrides)
            : rawOverrides;

        string targetLabel = _isTeacher ? "教师" : "班级";
        if (schedule == null || schedule.Classes.Count == 0)
        {
            TxtStatus.Text = _isTeacher
                ? "尚无教师个人课表：请先在「数据管理」导入全校课表（会自动整理出教师课表）或导入单个教师课表。"
                : "尚未导入课表，请先在控制面板「数据管理」导入课表。";
            WeekGrid.Children.Clear();
            TxtWeekRange.Text = "";
            return;
        }
        if (string.IsNullOrEmpty(_selectedName))
        {
            TxtStatus.Text = _isTeacher
                ? "请先在控制面板顶栏的「教师」下拉中选择一位教师。"
                : "请先在控制面板选择班级。";
            WeekGrid.Children.Clear();
            TxtWeekRange.Text = "";
            return;
        }

        var data = WeekBuilder.BuildWeek(_selectedName, _semesterStart, schedule, overrides, _monday);
        BuildGrid(data);
        // 教学周序号按"这一周里占多数的那一周"算：取中间那天（周起始日不同时，
        // 直接用第一天会落到上一教学周，例如周日开头时周日属于上一个教学周）
        int weekNo = WeekHelper.TeachingWeek(_monday.AddDays(3), _semesterStart);
        TxtWeekRange.Text = $"第{weekNo}周　{_monday:MM月dd日} - {_monday.AddDays(6):MM月dd日}　{_selectedName}（{targetLabel}）";
        TxtStatus.Text = _isTeacher
            ? $"当前显示教师「{_selectedName}」的周课表（只读；如需查看某班请到顶栏「班级」下拉切换）。"
            : "";

    }

    private void BuildGrid(WeekGridData data)
    {
        EndDrag(force: true);        // 重建前先把幽灵卡 / 高亮收干净，否则会留在旧格子上
        _cells.Clear();
        WeekGrid.Children.Clear();
        WeekGrid.RowDefinitions.Clear();
        WeekGrid.ColumnDefinitions.Clear();

        WeekGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int p = 0; p < data.MaxPeriod; p++)
            WeekGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        WeekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        for (int d = 0; d < 7; d++)
            WeekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 星期表头
        for (int d = 0; d < 7; d++)
        {
            var h = new Border
            {
                Background = Brush("#1E4FA3"),
                Margin = new System.Windows.Thickness(3),
                CornerRadius = new CornerRadius(6)
            };
            h.Child = new TextBlock
            {
                Text = data.DayHeaders[d],
                Foreground = WpfBrushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center
            };
            Grid.SetRow(h, 0);
            Grid.SetColumn(h, d + 1);
            WeekGrid.Children.Add(h);
        }

        var cellDict = data.Cells.ToDictionary(c => (c.DayIdx, c.Period), c => c);
        for (int p = 0; p < data.MaxPeriod; p++)
        {
            var ph = new Border
            {
                Background = Brush("#E9EFF9"),
                Margin = new System.Windows.Thickness(3),
                CornerRadius = new CornerRadius(6)
            };
            ph.Child = new TextBlock
            {
                Text = data.PeriodHeaders[p],
                Foreground = Brush("#1E4FA3"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center
            };
            Grid.SetRow(ph, p + 1);
            Grid.SetColumn(ph, 0);
            WeekGrid.Children.Add(ph);

            for (int d = 0; d < 7; d++)
            {
                int dayIdx = data.ColumnDayIdx.Count > d ? data.ColumnDayIdx[d] : d;   // 列号 ≠ DayIdx
                var cell = cellDict[(dayIdx, p + 1)];
                var border = MakeCellBorder(cell);
                Grid.SetRow(border, p + 1);
                Grid.SetColumn(border, d + 1);
                WeekGrid.Children.Add(border);
            }
        }
    }

    private Border MakeCellBorder(WeekCell cell)
    {
        var border = new Border
        {
            Background = cell.IsEmpty ? Brush("#F2F2F2") : WpfBrushes.White,
            BorderBrush = Brush("#E1E6EE"),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new System.Windows.Thickness(3),
            Padding = new System.Windows.Thickness(6),
            Cursor = WpfCursors.Hand
        };

        var sp = new StackPanel();
        if (cell.IsOverridden)
        {
            var badge = new Border
            {
                Background = Brush(cell.BadgeColor),
                CornerRadius = new CornerRadius(4),
                Padding = new System.Windows.Thickness(4, 1, 4, 1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            badge.Child = new TextBlock { Text = cell.BadgeText, Foreground = WpfBrushes.White, FontSize = 11 };
            sp.Children.Add(badge);
        }

        if (cell.HasLesson)
        {
            sp.Children.Add(new TextBlock
            {
                Text = cell.DisplaySubject,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Foreground = Brush(cell.SubjectColor),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(cell.Teacher))
            {
                // 第二行：班级课表显示任课教师，教师课表显示所教班级。
                // 双击可互切：看某位教师的个人周课表 / 看某个班的周课表。
                var tbName = new TextBlock
                {
                    Text = cell.Teacher,
                    FontSize = 12,
                    Foreground = Brush("#1E4FA3"),
                    Background = WpfBrushes.Transparent,
                    Cursor = WpfCursors.Hand,
                    ToolTip = "双击切换到该" + (_isTeacher ? "班级" : "教师") + "的周课表"
                };
                tbName.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        SwitchFromCell(cell);
                        e.Handled = true;
                    }
                };
                sp.Children.Add(tbName);
            }
            if (!string.IsNullOrEmpty(cell.LocationText))
                sp.Children.Add(new TextBlock { Text = cell.LocationText.Trim(), FontSize = 11, Foreground = Brush("#2E8B57") });
        }
        else
        {
            sp.Children.Add(new TextBlock
            {
                Text = cell.IsEmpty ? "（空）" : "",
                FontSize = 12,
                Foreground = Brush("#BBBBBB")
            });
        }
        border.Child = sp;
        border.Tag = cell;                     // 手势要靠它从命中的可视元素反查是哪个格子
        _cells.Add((cell, border));

        // 右键：Manage=安排调代课；Float=清除该格
        border.MouseRightButtonUp += (s, e) =>
        {
            if (_isTeacher)
            {
                TxtStatus.Text = "教师个人周课表为只读：如需安排调课 / 代课，请先到顶栏「班级」下拉切换到对应班级。";
            }
            else if (Mode == WeekViewMode.Manage) OpenArrangeEditor(cell);
            else ClearCell(cell);
            e.Handled = true;
        };

        return border;
    }

    // ---------- 双击教师姓名 / 班级名称：在班级课表与教师个人课表间互切 ----------
    private void SwitchFromCell(WeekCell cell)
    {
        if (string.IsNullOrEmpty(cell.Teacher)) return;
        string fromLabel = _isTeacher ? "班级" : "教师";

        // 切到教师前先确认该教师有个人课表数据
        if (!_isTeacher)
        {
            var ts = Store.LoadTeacherSchedule();
            if (ts == null || !ts.Classes.Any(c => c.ClassName == cell.Teacher))
            {
                TxtStatus.Text = $"教师「{cell.Teacher}」暂无个人课表数据（可在「数据管理」导入全校课表自动整理）。";
                return;
            }
        }

        var s = Store.LoadSettings();
        if (_isTeacher) { s.ScheduleKind = "Class"; s.SelectedClass = cell.Teacher; }
        else { s.ScheduleKind = "Teacher"; s.SelectedTeacher = cell.Teacher; }
        Store.SaveSettings(s);
        AppEvents.RaiseScheduleChanged();   // Rebuild 同步执行，_isTeacher 已是新状态

        TxtStatus.Text = _isTeacher
            ? $"已从{fromLabel}课表切换到教师「{cell.Teacher}」的个人周课表。"
            : $"已从{fromLabel}课表切换到班级「{cell.Teacher}」的周课表。";
    }

    // ---------- 安排调课/代课（右键，Manage 模式） ----------
    private void OpenArrangeEditor(WeekCell cell)
    {
        if (string.IsNullOrEmpty(_selectedName)) return;

        // 班主任只能改自己班；电教管理员 / 游客不能改任何班
        if (!_isTeacher && !UserSession.CanEditClass(_selectedName))
        {
            TxtStatus.Text = UserSession.DenyEditClass(_selectedName);
            return;
        }

        var schedule = Store.LoadSchedule();
        var current = new LessonView
        {
            DayIdx = cell.DayIdx,
            Period = cell.Period,
            Subject = cell.Subject,
            Teacher = cell.Teacher,
            LocationText = cell.LocationText,
            IsOverridden = cell.IsOverridden,
            OverrideType = cell.Type
        };
        var w = new LessonOverrideWindow(_selectedName, cell.DayIdx, cell.Period, current, schedule, _monday)
        {
            Owner = Window.GetWindow(this),
            Topmost = true
        };
        if (w.ShowDialog() == true)
        {
            AppEvents.RaiseScheduleChanged();
            Rebuild();
            TxtStatus.Text = $"已更新 {cell.DayName}第{cell.Period}节 的调课安排。";
        }
    }

    // ---------- 清除单格（Float 右键） ----------
    private void ClearCell(WeekCell cell)
    {
        if (string.IsNullOrEmpty(_selectedName)) return;

        if (!UserSession.CanEditClass(_selectedName))
        {
            TxtStatus.Text = UserSession.DenyEditClass(_selectedName);
            return;
        }

        var list = Store.LoadOverrides();
        // ★ 只清「当前显示周」这一格：以前按班级+节次无条件删，会把其他周同一格的记录一起删掉
        var (ws, we) = WeekHelper.WeekWindowOf(_monday);
        int n = list.RemoveAll(o => o.ClassName == _selectedName && o.DayIdx == cell.DayIdx
                                    && o.Period == cell.Period && o.OverlapsRange(ws, we));
        if (n > 0) Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = n > 0
            ? $"已清除 {ws:M/d}–{we:M/d} 这一周 {cell.DayName}第{cell.Period}节 的调课"
            : $"{cell.DayName}第{cell.Period}节 本周无调课记录";
    }

    // ---------- 手势：与悬浮课表完全同一套（规则见字段区注释） ----------

    /// <summary>从命中的可视元素往上找"格子 Border"（<c>Tag</c> 里存着 WeekCell）。</summary>
    private static Border? CellBorderOf(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is Border b && b.Tag is WeekCell) return b;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return null;
    }

    /// <summary>能当拖动源的格子：教师课表只读，空格子没东西可搬。</summary>
    private bool CanDrag(WeekCell? c) => !_isTeacher && c != null && c.HasLesson;

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_inSetup) return;

        // ★ TouchDown 已经把「按下点 / 源格 / 长按计时器」都备好了。这个 MouseDown 是触摸
        //   提升出来的，晚到几十毫秒；若再走一遍下面的 EndWrap()，会先把计时器和源格清掉，
        //   再重起一次表 —— 老一体机上提升链迟到甚至不到，表现就是「按住不浮起，但松手
        //   照样弹编辑窗」。这里直接沿用 TouchDown 的结果。
        if (_touchInitDone && _pressFromTouch && !_dragging) return;

        // 上一轮没收干净（极少数一体机会漏 Up）：正在拖就先完整收尾，否则只清按下痕迹。
        if (_dragging) EndDrag(force: true);
        else EndWrap();

        var b = CellBorderOf(e.OriginalSource as DependencyObject);
        _pressCell = b?.Tag as WeekCell;
        _pressFromTouch = e.StylusDevice != null
                          || (Environment.TickCount - _lastTouchDownTick) < 250;
        bool haveTouchOrigin = _touchStartPt.X > 0 || _touchStartPt.Y > 0;
        _dragStart = (_pressFromTouch && haveTouchOrigin) ? _touchStartPt : e.GetPosition(this);
        _lastDragPt = _dragStart;          // 本次拖动的真实位置从这里开始记
        _gestureScrolled = false;

        if (!CanDrag(_pressCell)) { _srcBorder = null; _armed = false; return; }

        _srcBorder = b;
        if (_pressFromTouch) StartHold();   // 触摸：静止满 0.5 秒浮起
        else _armed = true;                 // 鼠标：按住即可拖（与原来手感一致）
    }

    private void OnPointerMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_inSetup) return;

        // 单指 / 双指滚动中：只跟手滚
        if (_singleScroll)
        {
            // 触摸被模拟成鼠标时 LeftButton 往往已是 Released —— 不能据此结束滚动
            if (e.LeftButton != MouseButtonState.Pressed) { if (_pressFromTouch) return; EndSingleScroll(); return; }
            double sy = e.GetPosition(this).Y;
            ScrollTo(_scrollOrigin - (sy - _scrollStartY));
            TrackFling(sy);
            return;
        }

        if (_pressCell == null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 触摸按住时这条事件是系统模拟的，收尾只认 TouchUp / 提升出来的 Up
            if (_pressFromTouch && (_armed || _dragging)) return;
            EndDrag();
            return;
        }

        // 触摸按住期间一律由触摸坐标驱动（模拟鼠标的 move 稀疏甚至直接停掉）
        if (_pressFromTouch && Environment.TickCount64 - _lastTouchDriveTick < 150) return;

        Drive(e.GetPosition(this), null);
    }

    private void OnPointerUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_singleScroll) { EndSingleScroll(); StartFling(); return; }
        FinishAt(e.GetPosition(this));
    }

    private void OnTouchDown(object? sender, TouchEventArgs e)
    {
        var pt = e.GetTouchPoint(this).Position;
        _touchPoints[e.TouchDevice] = pt;
        _lastTouchDownTick = Environment.TickCount;
        _lastTouchDriveTick = Environment.TickCount64;
        _touchInitDone = false;     // 每一次按下都重新判定，别把上一次的标记带过来

        if (_touchPoints.Count >= 2)
        {
            // 第二根手指：放弃单指的长按 / 拖动，交给双指滚动
            CancelHold();
            EndDrag();
            if (!_touchScrolling)
            {
                var pts = _touchPoints.Values.Take(2).ToList();
                _touchScrolling = true;
                _scrollOrigin = WeekScroll.VerticalOffset;
                _scrollStartY = (pts[0].Y + pts[1].Y) / 2;
                _lastScrollY = _scrollStartY;
                _lastScrollAt = DateTime.UtcNow;
                _flingV = 0;
            }
            e.Handled = true;      // 第二指不再提升成鼠标，避免多出一个"单击"
            return;
        }

        _touchStartPt = pt;
        _gestureScrolled = false;
        _lastDragPt = pt;

        // ★ 长按计时器不再只挂在「提升出来的 MouseDown」上。老一体机 / 触摸捕获状态下
        //   提升链会迟到甚至不到，那时卡片按 0.5 秒纹丝不动，可鼠标右键语义却还在，
        //   于是「长按没浮起、松手照样弹修改窗口」。这里就地按真实触摸点命中格子并起表；
        //   MouseDown 若正常到达，会走 OnPointerDown 的短路直接沿用，不再重起计时器。
        var tb = CellBorderOf(HitTestAt(pt));
        var tc = tb?.Tag as WeekCell;
        if (CanDrag(tc))
        {
            _pressCell = tc;
            _srcBorder = tb;
            _pressFromTouch = true;
            _dragStart = pt;
            _touchInitDone = true;
            StartHold();
        }
    }

    private void OnTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_touchPoints.ContainsKey(e.TouchDevice)) return;
        var pt = e.GetTouchPoint(this).Position;
        _touchPoints[e.TouchDevice] = pt;
        _lastTouchDriveTick = Environment.TickCount64;

        if (_touchPoints.Count == 1)
        {
            if (_singleScroll)
            {
                ScrollTo(_scrollOrigin - (pt.Y - _scrollStartY));
                TrackFling(pt.Y);
                return;
            }
            Drive(pt, e.TouchDevice);
            return;
        }

        if (_touchPoints.Count < 2 || !_touchScrolling) return;
        var two = _touchPoints.Values.Take(2).ToList();
        double mid = (two[0].Y + two[1].Y) / 2;
        ScrollTo(_scrollOrigin - (mid - _scrollStartY));
        TrackFling(mid);
        e.Handled = true;
    }

    private void OnTouchUp(object? sender, TouchEventArgs e)
    {
        _touchPoints.Remove(e.TouchDevice);

        if (_singleScroll && _touchPoints.Count == 0) { EndSingleScroll(); StartFling(); e.Handled = true; return; }
        if (_touchScrolling && _touchPoints.Count < 2) { _touchScrolling = false; StartFling(); e.Handled = true; return; }

        // 兜底：个别一体机只报触摸、不报提升出来的鼠标事件
        if (_touchPoints.Count == 0 && (_dragging || (_armed && _pressFromTouch)))
            FinishAt(e.GetTouchPoint(this).Position);

        if (_touchPoints.Count == 0) EndWrap();
    }

    /// <summary>手势判定的唯一实现（鼠标 / 触摸共用），与悬浮课表 <c>DrivePointer</c> 同构。</summary>
    private void Drive(System.Windows.Point cur, TouchDevice? touch)
    {
        if (_inSetup) return;

        var origin = touch != null ? _touchStartPt : _dragStart;
        double dx = Math.Abs(cur.X - origin.X);
        double dy = Math.Abs(cur.Y - origin.Y);

        // 空格子 / 只读：没东西可搬，但"一动就滚"必须照样成立（否则这些格子成了滚不动的死区）
        if (!CanDrag(_pressCell) || _srcBorder == null)
        {
            if (touch == null || _singleScroll) return;
            if (!_gestureScrolled && (dx > Slop || dy > Slop) && WeekScroll.ScrollableHeight > 0)
            {
                _gestureScrolled = true;
                BeginSingleScroll(cur.Y, touch);
            }
            return;
        }

        // ① 还没浮起：10px 内等长按；一过 10px 立刻判滚动，本次按下永久失能
        if (!_armed)
        {
            if (dx <= Slop && dy <= Slop) return;
            CancelHold();
            _gestureScrolled = true;
            _pressCell = null;
            _srcBorder = null;
            if (WeekScroll.ScrollableHeight > 0) BeginSingleScroll(cur.Y, touch);
            return;
        }

        // ② 已浮起：<10px 仍算原地（松手=右键），≥10px 才开始搬
        if (!_dragging)
        {
            if (dx <= Slop && dy <= Slop) return;
            StartDrag(touch);
        }
        _lastDragPt = cur;
        UpdateVisuals(cur);
        UpdateDrop(cur);
    }

    private void StartHold()
    {
        CancelHold();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            _holdTimer = null;
            if (_gestureScrolled || _srcBorder == null || !CanDrag(_pressCell)) return;
            Arm();
        };
        _holdTimer.Start();
    }

    private void CancelHold()
    {
        _holdTimer?.Stop();
        _holdTimer = null;
    }

    /// <summary>"浮起来"：源格变淡、原位留虚线框、幽灵卡跟手。抓手 / 长按到点都走这里。</summary>
    private void Arm()
    {
        if (_srcBorder == null) return;
        _armed = true;
        _armSavedOpacity = _srcBorder.Opacity;
        _srcBorder.Opacity = 0.25;
        ShowGhost();
        TxtStatus.Text = Mode == WeekViewMode.Manage
            ? "松手 → 安排调课/代课（同右键）　·　拖到别的格 → 交换"
            : "松手 → 清除本格调课（同右键）　·　拖到别的格 → 交换";
    }

    private void StartDrag(TouchDevice? touch)
    {
        CancelHold();
        // 鼠标路径没有经过 Arm（按下即可拖），这里补上源格变淡 + 幽灵卡
        if (_srcBorder != null && _ghost == null)
        {
            _armSavedOpacity = _srcBorder.Opacity;
            _srcBorder.Opacity = 0.25;
            ShowGhost();
        }
        _dragging = true;

        // ★ 起拖这一段会触发同步回调（改 OverrideCursor 会回调 PreviewMouseMove，
        //   设捕获也可能回调 LostMouseCapture）。_inSetup 全程挡住，否则这些回调看到
        //   "左键没按着"就把刚起手的拖动当场收尾。
        _inSetup = true;
        try
        {
            if (touch != null) { try { CaptureTouch(touch); } catch { } }
            else { try { Mouse.Capture(this); } catch { } }
            Mouse.OverrideCursor = WpfCursors.Hand;
            StartEdgeScroll();
        }
        finally { _inSetup = false; }
    }

    private void ShowGhost()
    {
        ClearGhost();
        if (DragLayer == null || _srcBorder == null) return;
        double w = _srcBorder.ActualWidth, h = _srcBorder.ActualHeight;
        if (w < 8 || h < 8) return;

        // ★ 坐标一律换算到 DragLayer（覆盖层画布）。
        //   以前这里取的是 UserControl 坐标（TransformToVisual(this)），而 Canvas.SetLeft 要的是
        //   画布坐标 —— 两者差着顶部工具栏 + 周范围 + 提示那一整块高度，正好就是用户看到的
        //   "手指按下后格子跑到手指下方两格多"。DragLayer 是 ScrollViewer 的兄弟节点，
        //   它的原点在工具栏下面，不是 (0,0)，所以必须换算。
        var tl = _srcBorder.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        var start = ToLayer(_dragStart);
        _grabDX = start.X - tl.X;
        _grabDY = start.Y - tl.Y;

        // 原位虚框：钉在源格原来的位置，跟手不跟着走（源格变淡后由它占位）
        _slot = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = 8,
            RadiusY = 8,
            Stroke = new SolidColorBrush(WpfColor.FromRgb(0x37, 0x8A, 0xDD)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = WpfBrushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_slot, tl.X);
        Canvas.SetTop(_slot, tl.Y);
        DragLayer.Children.Add(_slot);

        if (!ShowFloatingCard) return;   // 关掉时只留原位虚框（目标格高亮在 UpdateDrop 里）

        // 用 VisualBrush 直接"复印"源格内容：不用复制一遍，调课徽章 / 科目 / 教师名 / 地点都在。
        // ★ 千万不能把 VisualBrush 指向 _srcBorder 本身：Arm() 刚把源格 Opacity 压到 0.25，
        //   以它为根渲染时幽灵卡会跟着一起变淡，看着像半张卡。以它的 Child（StackPanel）为根
        //   就不带父级的 Opacity；外面补一层同款 Padding，让文字位置与源格对齐。
        var content = _srcBorder.Child as System.Windows.Media.Visual;
        var ghost = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new System.Windows.Thickness(2),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x37, 0x8A, 0xDD)),
            Background = WpfBrushes.White,
            Opacity = 1.0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.03, 1.03),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.28 },
            Child = new Border
            {
                Padding = _srcBorder.Padding,
                Child = new System.Windows.Shapes.Rectangle
                {
                    Fill = content == null ? null : new VisualBrush(content) { Stretch = Stretch.Fill },
                    RadiusX = 7,
                    RadiusY = 7
                }
            }
        };
        // 先摆到源格原位再等 UpdateVisuals 跟手：少了这一句，万一这一帧位置没算出来，
        // 卡片会以 Canvas 默认坐标出现 —— 看着就是"卡片自己跑掉了"。
        Canvas.SetLeft(ghost, tl.X);
        Canvas.SetTop(ghost, tl.Y);
        DragLayer.Children.Add(ghost);
        _ghost = ghost;
    }

    /// <summary>视图（UserControl）坐标 → 覆盖层画布坐标。</summary>
    private System.Windows.Point ToLayer(System.Windows.Point pt)
    {
        try { return DragLayer == null ? pt : TranslatePoint(pt, DragLayer); }
        catch { return pt; }
    }

    private void ClearGhost()
    {
        if (_ghost != null) { DragLayer?.Children.Remove(_ghost); _ghost = null; }
        if (_slot != null) { DragLayer?.Children.Remove(_slot); _slot = null; }
    }

    private void UpdateVisuals(System.Windows.Point pt)
    {
        // pt 是视图坐标，Canvas 要用画布坐标 —— 不换算就会整体差一个工具栏的高度
        var p = ToLayer(pt);
        if (_ghost != null)
        {
            double gx = p.X - _grabDX;
            double gy = p.Y - _grabDY;
            // 抓取偏移是"按下那一刻"算的，格子被 30 秒刷新重建 / 滚动后可能对不上，
            // 卡片就会飞到一个凭空坐标（用户看到的是"跑到窗口中间"）。正常偏移绝不超过
            // 一整张卡片，一旦离手指超过一张半，就直接居中贴手指 —— 宁可抓取点差一点。
            if (Math.Abs(gx - p.X) > _ghost.Width * 1.5 || Math.Abs(gy - p.Y) > _ghost.Height * 1.5)
            {
                gx = p.X - _ghost.Width / 2;
                gy = p.Y - _ghost.Height / 2;
            }
            Canvas.SetLeft(_ghost, gx);
            Canvas.SetTop(_ghost, gy);
        }
        if (_slot != null && _srcBorder != null)
        {
            try
            {
                var tl = _srcBorder.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
                Canvas.SetLeft(_slot, tl.X);
                Canvas.SetTop(_slot, tl.Y);
            }
            catch { }
        }
    }

    private void UpdateDrop(System.Windows.Point pt)
    {
        var b = CellBorderOf(HitTestAt(pt));
        if (ReferenceEquals(b, _dropBorder)) return;
        ClearDropHighlight();
        if (b == null || ReferenceEquals(b, _srcBorder)) return;

        _dropBorder = b;
        _dropSavedBrush = b.BorderBrush;
        _dropSavedThick = b.BorderThickness;
        b.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x4F, 0xA3));
        b.BorderThickness = new System.Windows.Thickness(3);
    }

    private void ClearDropHighlight()
    {
        if (_dropBorder == null) return;
        try
        {
            _dropBorder.BorderBrush = _dropSavedBrush;
            _dropBorder.BorderThickness = _dropSavedThick;
        }
        catch { }
        _dropBorder = null;
        _dropSavedBrush = null;
    }

    private DependencyObject? HitTestAt(System.Windows.Point pt)
    {
        DependencyObject? hit = null;
        try
        {
            VisualTreeHelper.HitTest(this,
                dep =>
                {
                    // 覆盖层（原位虚框 / 跟手卡）只是画给人看的。虽然 DragLayer 已经设了
                    // IsHitTestVisible=False，某些 WPF 版本仍会让子元素参与命中；
                    // 一旦它挡在手指下面，落点就会算成 null → "已取消调课"。这里整棵跳过。
                    if (dep is Canvas cv && ReferenceEquals(cv, DragLayer))
                        return HitTestFilterBehavior.ContinueSkipSelfAndChildren;
                    return HitTestFilterBehavior.Continue;
                },
                r => { hit = r.VisualHit; return HitTestResultBehavior.Stop; },
                new PointHitTestParameters(pt));
        }
        catch { }
        return hit;
    }

    /// <summary>
    /// 结算落点时该用哪个坐标。触摸拖动期间系统提升出来的 MouseUp 常常还带着
    /// <b>按下那一刻</b>的坐标（中间的 move 没有提升成鼠标），拿它算落点必然命中源格，
    /// 于是「明明拖到别的格，松手却提示已取消调课」。拖动过程中由 TouchMove 记下的
    /// <see cref="_lastDragPt"/> 才是手指真正最后停的地方。
    /// </summary>
    private System.Windows.Point DropPointOf(System.Windows.Point pt, bool wasDrag)
    {
        if (!wasDrag || (_lastDragPt.X <= 0 && _lastDragPt.Y <= 0)) return pt;
        return Math.Abs(_lastDragPt.X - pt.X) + Math.Abs(_lastDragPt.Y - pt.Y) > 2
            ? _lastDragPt : pt;
    }

    /// <summary>结束一次拖动 / 长按，并按"有没有真的拖开"决定收尾动作。</summary>
    private void FinishAt(System.Windows.Point pt)
    {
        bool wasDrag = _dragging, wasArmed = _armed, fromTouch = _pressFromTouch;
        var srcCell = _pressCell;
        if (!fromTouch) wasArmed = false;      // 鼠标单击不算"浮起过"，不弹编辑

        var dropPt = DropPointOf(pt, wasDrag);
        var tgtBorder = wasDrag ? CellBorderOf(HitTestAt(dropPt)) : null;
        if (ReferenceEquals(tgtBorder, _srcBorder)) tgtBorder = null;

        EndDrag();

        if (wasDrag && srcCell != null)
        {
            if (tgtBorder?.Tag is WeekCell tc && !ReferenceEquals(tc, srcCell)) ApplyDrag(srcCell, tc);
            else TxtStatus.Text = "已取消调课";
        }
        else if (wasArmed && srcCell != null && !_isTeacher)
        {
            // 浮起后原地松手 = 右键：Manage 打开安排窗口，Float 清除本格。
            // ★ Float 的"右键"是破坏性操作（清调课），手指长按松手比鼠标右键更容易误触，
            //   所以这里补一次确认；鼠标右键路径保持原样（点得很准，不需要二次确认）。
            if (Mode == WeekViewMode.Manage)
            {
                OpenArrangeEditor(srcCell);
            }
            else if (!srcCell.IsOverridden)
            {
                TxtStatus.Text = $"{srcCell.DayName}第{srcCell.Period}节 本周没有调课记录。";
            }
            else
            {
                var r = WpfMessageBox.Show(
                    $"要清除 {srcCell.DayName}第{srcCell.Period}节 的调课吗？",
                    "清除调课",
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Question);
                if (r == System.Windows.MessageBoxResult.OK) ClearCell(srcCell);
            }
        }

        EndWrap();
    }

    /// <summary>把拖动 / 长按的视觉与状态全部收干净（<paramref name="force"/> 连按下状态一起清）。</summary>
    private void EndDrag(bool force = false)
    {
        CancelHold();
        if (_srcBorder != null)
        {
            try { _srcBorder.Opacity = _armSavedOpacity; } catch { }
            _armSavedOpacity = 1;
        }
        ClearGhost();
        ClearDropHighlight();
        StopEdgeScroll();
        _armed = false;
        _dragging = false;
        _srcBorder = null;
        _touchInitDone = false;
        Mouse.OverrideCursor = null;
        try { if (Mouse.Captured == this) Mouse.Capture(null); } catch { }
        // ★ 触摸拖动 / 单指滚动捕获的是手指（CaptureTouch），不是鼠标。WPF 不保证 TouchUp
        //   之后自动解除这层捕获；残留下来，之后所有触摸事件都会继续路由到这个已经收尾的
        //   WeekView —— 表现就是「控制面板拖过一次后，悬浮课表长按再也浮不起来，但提升
        //   出来的鼠标事件还能弹编辑窗」。悬浮课表的 EndDrag 早就有这一句，这里漏了。
        try { ReleaseAllTouchCaptures(); } catch { }
        if (force) EndWrap();
    }

    /// <summary>轻点 / 收尾后清掉"本次按下"的痕迹（绝不动 _armed 以外的拖动视觉，视觉交给 EndDrag）。</summary>
    private void EndWrap()
    {
        CancelHold();
        _pressCell = null;
        _srcBorder = null;
        _armed = false;
        _gestureScrolled = false;
        _touchInitDone = false;
    }

    // ---------- 滚动：单指快速划动 / 双指划动（与悬浮课表同一套） ----------

    private void BeginSingleScroll(double startY, TouchDevice? dev = null)
    {
        if (WeekScroll == null) return;
        StopFling();
        _singleScroll = true;
        _scrollOrigin = WeekScroll.VerticalOffset;
        _scrollStartY = startY;
        _lastScrollY = startY;
        _lastScrollAt = DateTime.UtcNow;
        _flingV = 0;
        _pressCell = null;
        _srcBorder = null;
        // 捕获手指（不是鼠标）：划出边界后仍能继续收到 move
        if (dev != null) { try { CaptureTouch(dev); } catch { } }
    }

    private void EndSingleScroll()
    {
        _singleScroll = false;
        try { if (Mouse.Captured == this) Mouse.Capture(null); } catch { }
        try { ReleaseAllTouchCaptures(); } catch { }
    }

    private void ScrollTo(double offset)
    {
        if (WeekScroll == null) return;
        double max = Math.Max(0, WeekScroll.ScrollableHeight);
        WeekScroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(max, offset)));
    }

    private void TrackFling(double y)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastScrollAt).TotalSeconds;
        if (dt <= 0.001) return;
        double v = (y - _lastScrollY) / dt;
        _flingV = _flingV * 0.7 + v * 0.3;
        _lastScrollY = y;
        _lastScrollAt = now;
    }

    private void StartFling()
    {
        double v = _flingV;
        _flingV = 0;
        if (Math.Abs(v) < 120 || WeekScroll == null) return;   // 慢速松手不滑
        StopFling();
        _flingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _flingTimer.Tick += (_, _) =>
        {
            v *= 0.92;
            if (Math.Abs(v) < 40) { StopFling(); return; }
            double before = WeekScroll.VerticalOffset;
            ScrollTo(before - v * 0.016);
            if (Math.Abs(WeekScroll.VerticalOffset - before) < 0.01) StopFling();
        };
        _flingTimer.Start();
    }

    private void StopFling()
    {
        _flingTimer?.Stop();
        _flingTimer = null;
    }

    // ---------- 拖动时靠上下边缘自动滚动（跨屏调课不用先滚再拖） ----------

    private void StartEdgeScroll()
    {
        if (_edgeTimer != null) return;
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeTimer.Tick += (_, _) =>
        {
            if (!_dragging || WeekScroll == null) { StopEdgeScroll(); return; }
            if (WeekScroll.ScrollableHeight <= 0) return;

            var tl = WeekScroll.TransformToVisual(this).Transform(new System.Windows.Point(0, 0));
            double top = tl.Y, bottom = tl.Y + WeekScroll.ActualHeight, y = _lastDragPt.Y;
            double speed = 0;
            if (y < top + EdgeZone) speed = -(1 - Math.Max(0, y - top) / EdgeZone);
            else if (y > bottom - EdgeZone) speed = 1 - Math.Max(0, bottom - y) / EdgeZone;
            if (speed == 0) return;

            double before = WeekScroll.VerticalOffset;
            ScrollTo(before + Math.Clamp(speed, -1, 1) * 12);
            if (Math.Abs(WeekScroll.VerticalOffset - before) > 0.01)
            {
                UpdateVisuals(_lastDragPt);
                UpdateDrop(_lastDragPt);
            }
        };
        _edgeTimer.Start();
    }

    private void StopEdgeScroll()
    {
        _edgeTimer?.Stop();
        _edgeTimer = null;
    }

    /// <summary>
    /// 诊断截图用（<c>--shot weekdrag</c>）：把一个有课的格子浮起并拖开一段，
    /// <b>不收尾</b>，保持「源格变淡 + 原位虚框 + 跟手卡」的画面供截图核对观感。
    /// 几何正确性由 <see cref="DiagWeekProbe"/> 验，这里只管看得见的部分。
    /// </summary>
    internal void DiagArmDragVisual()
    {
        var one = _cells.FirstOrDefault(c => c.Cell.HasLesson);
        var b = one.Border;
        if (b == null || DragLayer == null) return;

        var tl = b.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
        var centerInLayer = new System.Windows.Point(tl.X + b.ActualWidth / 2, tl.Y + b.ActualHeight / 2);
        var center = DragLayer.TranslatePoint(centerInLayer, this);

        _pressCell = one.Cell;
        _srcBorder = b;
        _dragStart = center;
        _armed = false;
        _dragging = false;
        _gestureScrolled = false;

        Arm();
        Drive(new System.Windows.Point(center.X + 70, center.Y + 55), null);
    }

    /// <summary>
    /// 诊断用（<c>--week-check</c>）：验证周课表的手势改造是否成立。
    /// 纯内存演练，不写任何调代课数据。重点看五件事：
    /// ① 触摸全部交给自己（PanningMode=None）、系统长按 / Flicks 已关；
    /// ② 格子都注册了 Tag（手势靠它反查 WeekCell）；
    /// ③ 浮起后原位虚框必须严丝合缝钉在源格原位（用户报的"格子跑到手指下方两格多"就是这里）；
    /// ④ 跟手卡必须存在、不透明、初始贴源格，并且拖动后与手指同位移（r48 恢复）；
    /// ⑤ 3px 不算拖、40px 起拖。
    /// </summary>
    internal string DiagWeekProbe()
    {
        var sb = new System.Text.StringBuilder();
        string ph = "?", fl = "?";
        try { ph = Stylus.GetIsPressAndHoldEnabled(this).ToString(); } catch { }
        try { fl = Stylus.GetIsFlicksEnabled(this).ToString(); } catch { }
        sb.AppendLine($"cells={_cells.Count} scroll={WeekScroll?.ScrollableHeight:0.#} viewport={WeekScroll?.ViewportHeight:0.#}");
        sb.AppendLine($"panning={WeekScroll?.PanningMode} pressHold={ph} flicks={fl} slop={Slop} hold={HoldMs}ms");

        if (_cells.Count == 0) { sb.AppendLine("RESULT=FAIL 没有格子"); return sb.ToString(); }
        int tagged = _cells.Count(c => c.Border.Tag == c.Cell);
        sb.AppendLine($"tagged={tagged}/{_cells.Count} {(tagged == _cells.Count ? "ok" : "★ 有格子没注册 Tag")}");

        var one = _cells.FirstOrDefault(c => c.Cell.HasLesson);
        var b = one.Border;
        if (b == null) { sb.AppendLine("RESULT=FAIL 没有有课的格子（无法演练拖动）"); return sb.ToString(); }

        try
        {
            // Canvas 定位全是画布坐标；Drive() 收的是视图坐标。两个都算出来，别混。
            var tl = b.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
            var centerInLayer = new System.Windows.Point(tl.X + b.ActualWidth / 2, tl.Y + b.ActualHeight / 2);
            var center = DragLayer.TranslatePoint(centerInLayer, this);

            _pressCell = one.Cell;
            _srcBorder = b;
            _dragStart = center;
            _armed = false;
            _dragging = false;

            Arm();                                   // 长按到点，走同一个入口
            bool armed = _armed;

            // ★ 原位虚框必须严丝合缝钉在源格原位。以前这里算的是 UserControl 坐标却直接塞给
            //   Canvas，差着整个工具栏的高度 —— 就是用户看到的"格子跑到手指下方两格多"。
            double slotX = _slot == null ? double.NaN : Canvas.GetLeft(_slot);
            double slotY = _slot == null ? double.NaN : Canvas.GetTop(_slot);
            bool slotOk = _slot != null && Math.Abs(slotX - tl.X) <= 1.5 && Math.Abs(slotY - tl.Y) <= 1.5;
            bool sizeOk = _slot != null
                          && Math.Abs(_slot.Width - b.ActualWidth) < 1.5
                          && Math.Abs(_slot.Height - b.ActualHeight) < 1.5;

            // 跟手卡：必须存在、完全不透明、尺寸=源格、一浮起就钉在源格原位。
            var gh0 = _ghost;
            double ghostX = gh0 == null ? double.NaN : Canvas.GetLeft(gh0);
            double ghostY = gh0 == null ? double.NaN : Canvas.GetTop(gh0);
            bool ghostBorn = gh0 != null
                             && Math.Abs(ghostX - tl.X) <= 1.5 && Math.Abs(ghostY - tl.Y) <= 1.5
                             && Math.Abs(gh0.Width - b.ActualWidth) < 1.5
                             && Math.Abs(gh0.Height - b.ActualHeight) < 1.5
                             && Math.Abs(gh0.Opacity - 1.0) < 0.01;

            Drive(new System.Windows.Point(center.X + 3, center.Y + 2), null);
            bool notYet = !_dragging;
            Drive(new System.Windows.Point(center.X + 40, center.Y + 30), null);
            bool dragging = _dragging;

            // 手指从中心移了 (40,30)，卡片在画布里也必须正好移 (40,30) —— 只要差一截，
            // 就是坐标系又混用了（r47 那个"跑到手指下方两格多"的根因）。
            var gh1 = _ghost;
            double gdx = gh1 == null ? double.NaN : Canvas.GetLeft(gh1) - ghostX;
            double gdy = gh1 == null ? double.NaN : Canvas.GetTop(gh1) - ghostY;
            double followErr = gh1 == null ? double.NaN : Math.Max(Math.Abs(gdx - 40), Math.Abs(gdy - 30));
            bool ghostOk = !ShowFloatingCard || (ghostBorn && !double.IsNaN(followErr) && followErr <= 1.5);

            // ★ 落点判定实证：把手指拖到另一个格子的正中心，看 HitTestAt 到底命中谁。
            //   以前只验「卡片跟手位移」，没验手指下方的命中结果 —— 用户报的
            //   「拖到别的格松手提示已取消调课」就藏在这里。
            var other = _cells.FirstOrDefault(c => c.Border != null && !ReferenceEquals(c.Border, b));
            bool dropHitOk = false;
            bool staleOk = true;
            var oc = center;
            string hitDesc = "(没有第二个格子可验)";
            if (other.Border != null)
            {
                var otl = other.Border.TransformToVisual(DragLayer).Transform(new System.Windows.Point(0, 0));
                var ocLayer = new System.Windows.Point(otl.X + other.Border.ActualWidth / 2,
                                                       otl.Y + other.Border.ActualHeight / 2);
                oc = DragLayer.TranslatePoint(ocLayer, this);
                Drive(oc, null);
                var hit = HitTestAt(oc);
                var hitBorder = CellBorderOf(hit);
                dropHitOk = ReferenceEquals(hitBorder, other.Border);

                var chain = new System.Text.StringBuilder();
                DependencyObject? d = hit;
                int depth = 0;
                while (d != null && depth++ < 8)
                {
                    chain.Append(d.GetType().Name).Append("→");
                    d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
                }
                hitDesc = $"{hit?.GetType().Name ?? "null"} 链={chain} " +
                          $"命中Border={(hitBorder == null ? "null" : $"{hitBorder.ActualWidth:F0}x{hitBorder.ActualHeight:F0}")} " +
                          $"目标={other.Border.ActualWidth:F0}x{other.Border.ActualHeight:F0} " +
                          $"目标高亮={ReferenceEquals(_dropBorder, other.Border)}";
            }

            sb.AppendLine($"浮起={armed} 跟手卡={ShowFloatingCard} " +
                          $"原位虚框={slotX:F0},{slotY:F0} 源格={tl.X:F0},{tl.Y:F0} {(slotOk ? "位置 ok" : "★ 位置错位")}");
            sb.AppendLine($"虚框尺寸={_slot?.Width:F0}x{_slot?.Height:F0} 源格={b.ActualWidth:F0}x{b.ActualHeight:F0} {(sizeOk ? "尺寸 ok" : "★ 尺寸不对")}");
            sb.AppendLine($"跟手卡初始={ghostX:F0},{ghostY:F0} 尺寸={gh0?.Width:F0}x{gh0?.Height:F0} 不透明度={gh0?.Opacity:0.##} {(ghostBorn ? "贴合 ok" : "★ 初始不对")}");
            sb.AppendLine($"跟手位移=({gdx:F0},{gdy:F0}) 期望(40,30) 误差={followErr:F1} {(ghostOk ? "ok" : "★ 卡片没跟手")}");
            if (other.Border != null)
            {
                // 模拟「提升出来的 MouseUp 还带着按下点坐标」：结算必须自动改用最后一次
                // 真实拖动位置，否则命中源格 → 误报「已取消调课」。
                var chosen = DropPointOf(center, true);
                var chosenBorder = CellBorderOf(HitTestAt(chosen));
                staleOk = Math.Abs(chosen.X - oc.X) < 1 && Math.Abs(chosen.Y - oc.Y) < 1
                          && ReferenceEquals(chosenBorder, other.Border);
                sb.AppendLine($"过期MouseUp兜底 入参={center.X:F0},{center.Y:F0} 采用={chosen.X:F0},{chosen.Y:F0} " +
                              $"命中目标={ReferenceEquals(chosenBorder, other.Border)} " +
                              $"{(staleOk ? "ok" : "★ 会误报已取消调课")}");
            }

            sb.AppendLine($"落点={hitDesc} {(dropHitOk ? "ok" : "★ 落点丢失")}");
            sb.AppendLine($"3px不拖={notYet} 40px起拖={dragging} {(armed && slotOk && sizeOk && notYet && dragging ? "ok" : "★ 手势链断了")}");
            sb.AppendLine("RESULT=" + (armed && slotOk && sizeOk && ghostOk && dropHitOk && staleOk && notYet && dragging && tagged == _cells.Count ? "OK" : "FAIL"));
        }
        catch (Exception ex) { sb.AppendLine("EX " + ex.Message); sb.AppendLine("RESULT=FAIL"); }
        finally { EndDrag(force: true); }
        return sb.ToString();
    }

    // ---------- 拖动调课 ----------
    private void ApplyDrag(WeekCell src, WeekCell tgt)    {
        if (_isTeacher) return;   // 教师课表只读
        if (src.DayIdx == tgt.DayIdx && src.Period == tgt.Period) return;
        if (string.IsNullOrEmpty(_selectedName)) return;

        if (!UserSession.CanEditClass(_selectedName))
        {
            TxtStatus.Text = UserSession.DenyEditClass(_selectedName);
            Rebuild();    // 把拖到一半的视觉状态复原
            return;
        }

        var list = Store.LoadOverrides();
        var schedule = Store.LoadSchedule();

        if (!tgt.HasLesson)
        {
            Upsert(list, schedule, tgt.DayIdx, tgt.Period, src.Subject, src.Teacher, src.Location, _editType, isEmpty: !src.HasLesson);
            Upsert(list, schedule, src.DayIdx, src.Period, "", "", "", "", isEmpty: true);
        }
        else
        {
            Upsert(list, schedule, tgt.DayIdx, tgt.Period, src.Subject, src.Teacher, src.Location, _editType, isEmpty: !src.HasLesson);
            Upsert(list, schedule, src.DayIdx, src.Period, tgt.Subject, tgt.Teacher, tgt.Location, _editType, isEmpty: !tgt.HasLesson);
        }

        Store.SaveOverrides(list);

        // ★ 反馈先行 + 只重建一次：原来 RaiseScheduleChanged() 会同步触发本控件订阅的
        //   Rebuild，紧接着又手动 Rebuild 一次，整张周课表重建两遍，松手那一下明显卡住。
        //   现在先把结果写进状态栏（立刻可见），重建推到下一帧、且只由事件触发一次。
        string done = $"已{(_editType == "代" ? "安排代课" : "调课")}：{src.DayName}第{src.Period}节 ⇄ {tgt.DayName}第{tgt.Period}节";
        TxtStatus.Text = done;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { AppEvents.RaiseScheduleChanged(); } catch { }
            // Rebuild 会重置状态栏，刷新完补写一次结果
            try { TxtStatus.Text = done; } catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isTeacher)
        {
            TxtStatus.Text = "教师个人周课表不含调课记录；请使用顶栏「班级」下拉切换到班级后再清除。";
            return;
        }
        if (string.IsNullOrEmpty(_selectedName)) return;

        if (!UserSession.CanEditClass(_selectedName))
        {
            TxtStatus.Text = UserSession.DenyEditClass(_selectedName);
            return;
        }

        var (ws, we) = WeekHelper.WeekWindowOf(_monday);
        var targets = Store.LoadOverrides()
                           .Where(o => o.ClassName == _selectedName && o.OverlapsRange(ws, we)).ToList();
        if (targets.Count == 0)
        {
            TxtStatus.Text = $"本周（{ws:M/d}–{we:M/d}）没有调课记录。";
            return;
        }
        var r = WpfMessageBox.Show(
            $"确定清除「{_selectedName}」在 {ws:M/d}–{we:M/d} 这一周的 {targets.Count} 条调课 / 代课 / 调空记录吗？\n" +
            "其他周的记录不受影响；此操作不可撤销。",
            "清除本周调课", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        var list = Store.LoadOverrides();
        int n = list.RemoveAll(o => o.ClassName == _selectedName && o.OverlapsRange(ws, we));
        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = $"已清除本周 {n} 条调课记录（其他周不受影响）。";
    }

    private void Upsert(List<LessonOverride> list, ScheduleFile? schedule,
                        int dayIdx, int period, string subject, string teacher, string location, string type, bool isEmpty)
    {
        if (string.IsNullOrEmpty(_selectedName)) return;

        // ★ 周视图里改的是「当前显示的这一周」（设计 D13：临时层带生效区间）
        var (ws, we) = WeekHelper.WeekWindowOf(_monday);

        // ★ 拖回原位 = 取消调代：与底表该格完全一致时，这条记录不该留下标记
        //   （以前会留一条"内容是原课"的覆盖，格子上一直挂着「已调课」角标）
        if (IsSameAsBase(schedule, _selectedName, dayIdx, period, subject, teacher, location, isEmpty))
        {
            list.RemoveAll(o => o.ClassName == _selectedName && o.DayIdx == dayIdx && o.Period == period
                                && o.Source == "manual" && o.OverlapsRange(ws, we));
            return;
        }

        // 只清本周该格的手工记录；其他周、以及远程/导入来源不动
        list.RemoveAll(o => o.ClassName == _selectedName && o.DayIdx == dayIdx && o.Period == period
                            && o.Source == "manual" && o.OverlapsRange(ws, we));

        if (isEmpty)
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedName, DayIdx = dayIdx, Period = period,
                IsEmpty = true, Type = "调", Source = "manual",
                ValidFrom = DateOnly.FromDateTime(ws), ValidTo = DateOnly.FromDateTime(we),
                UpdatedAt = DateTime.Now
            });
        }
        else if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedName, DayIdx = dayIdx, Period = period,
                Subject = subject, Teacher = teacher, Location = location,
                Type = string.IsNullOrEmpty(type) ? "调" : type, Source = "manual",
                ValidFrom = DateOnly.FromDateTime(ws), ValidTo = DateOnly.FromDateTime(we),
                UpdatedAt = DateTime.Now
            });
        }
    }

    // ---------- 「是否与底表原位一致」：判定在 ScheduleCore.LessonComparer（周视图与右键调课窗共用） ----------
    private static bool IsSameAsBase(ScheduleFile? schedule, string className, int dayIdx, int period,
                                     string? subject, string? teacher, string? location, bool isEmpty)
        => LessonComparer.IsSameAsBase(schedule, className, dayIdx, period, subject, teacher, location, isEmpty);

    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex)!); }
        catch { return new SolidColorBrush(Colors.Black); }
    }
}
