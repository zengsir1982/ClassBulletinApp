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
    private AutoHider? _hider;
    private readonly DispatcherTimer _timer;

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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCurrentLayout();

        AppEvents.ScheduleChanged += Refresh;
        AppEvents.LayoutChanged += OnLayoutChanged;
        Refresh();
        _timer.Start();
    }

    private void ApplyCurrentLayout()
    {
        var l = Store.LoadSettings().ScheduleLayout;
        _layout = l;
        // 「置顶显示」= 半透明常驻且可交互；只有在布局里显式勾选「鼠标穿透」时才穿透，
        // 且穿透时隐藏按钮、标题区、课表区仍保持可点击。
        bool topShow = !l.AutoHide;
        _clickThrough = l.ClickThrough && !l.AutoHide;
        FloatingHelper.ApplyLayout(this, l, _clickThrough, topShow ? l.OverlayOpacity : l.Opacity);
        if (_clickThrough)
            FloatingHelper.SetClickThrough(this, true, BtnHide, ClassHeader, DateHeader, TodayPanel);
        else
            FloatingHelper.SetClickThrough(this, false);

        BtnHide.Content = l.AutoHide ? "置顶显示" : "自动隐藏";

        _hider?.Dispose();
        _hider = (l.AutoHide || l.AutoHideDuringClass)
            ? new AutoHider(this, l, () => l.AutoHide || (l.AutoHideDuringClass && IsClassTimeNow()))
            : null;
    }

    private void OnLayoutChanged()
    {
        ApplyCurrentLayout();
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

        TxtHint.Visibility = (!_isTeacherMode && res.Rows.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        TxtTeacherHint.Visibility = (_isTeacherMode && res.Rows.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
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

    // ---------- 长按 / 右键 弹出调课编辑（仅班级课表可编辑） ----------
    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isTeacherMode) return;
        if (_clickThrough) return;
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

        if (_isTeacherMode || _clickThrough)
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
            if (ReferenceEquals(dep, BtnHide)) return true;
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
        _timer.Stop();
        base.OnClosed(e);
    }
}
