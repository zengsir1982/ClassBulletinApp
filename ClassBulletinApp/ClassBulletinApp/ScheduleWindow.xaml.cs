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
    }

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

        AppEvents.ScheduleChanged += Refresh;
        AppEvents.LayoutChanged += OnLayoutChanged;
        AppEvents.BackdropChanged += UpdateBackdropButton;
        Refresh();
        UpdateBackdropButton();
        LoadQuickLaunchers();
        _timer.Start();
    }

    /// <summary>同步底部「桌面背景」按钮状态。</summary>
    private void UpdateBackdropButton()
    {
        try
        {
            if (BtnWp == null) return;
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            bool on = b.ShowSchedule;
            BtnWp.Content = on ? "桌面背景：开" : "桌面背景：关";
            BtnWp.Background = on
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF))
                : new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFD));
            BtnWp.Foreground = on ? Brushes.White : Brushes.Black;
            TxtWpState.Text = on
                ? "当天课表已在桌面背景显示（不遮挡操作）" + (b.ShowHomework ? "，含作业公示" : "")
                : "点右侧按钮可把当天课表画到桌面壁纸上";
        }
        catch { }
    }

    private void BtnWp_Click(object sender, RoutedEventArgs e)
    {
        BackdropService.ToggleSchedule(out string msg);
        UpdateBackdropButton();
        AppEvents.RaiseBackdropChanged();
        TxtWpState.Text = msg;
    }

    // ---------- 常用快捷（悬浮课表底部应用栏：图标按钮，支持仅图标 + 可调大小） ----------
    private void LoadQuickLaunchers()
    {
        var s = Store.LoadSettings();
        _ql = new System.Collections.ObjectModel.ObservableCollection<QuickLaunchItem>(
            s.QuickLaunchers ?? new System.Collections.Generic.List<QuickLaunchItem>());
        _qlIconOnly = s.QlIconOnly;
        _qlIconSize = s.QlIconSize > 0 ? s.QlIconSize : 28;
        RenderQuickLaunchers();
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
        // 「置顶显示」= 半透明常驻且可交互；只有在布局里显式勾选「鼠标穿透」时才穿透，
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
            FloatingHelper.SetClickThrough(this, true, BtnHide, BtnWp, BtnLock, BtnPos, ClassHeader, DateHeader, TodayPanel);
        else
            FloatingHelper.SetClickThrough(this, false);

        BtnHide.Content = l.AutoHide ? "置顶显示" : "自动隐藏";

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

    // ---------- 隐藏按钮：快速切换自动靠边隐藏 / 置顶显示 ----------
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

    private void OpenEditor(PeriodRow row)
    {
        if (string.IsNullOrEmpty(_selectedName)) return;
        var current = row.Lessons.Count > 0 ? row.Lessons[0] : new LessonView();
        var w = new LessonOverrideWindow(_selectedName, row.DayIdx, row.Period, current, _schedule)
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
        string key = $"{_selectedName}|{dayIdx}|{period}";
        list.RemoveAll(o => o.Key == key);
        if (isEmpty)
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedName, DayIdx = dayIdx, Period = period,
                IsEmpty = true, Type = "调", Source = "manual", UpdatedAt = DateTime.Now
            });
        }
        else if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedName, DayIdx = dayIdx, Period = period,
                Subject = subject, Teacher = teacher, Location = location,
                Type = type, Source = "manual", UpdatedAt = DateTime.Now
            });
        }
    }

    private bool IsWithinButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (ReferenceEquals(dep, BtnHide) || ReferenceEquals(dep, BtnWp)
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
