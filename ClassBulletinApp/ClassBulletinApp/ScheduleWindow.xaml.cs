using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class ScheduleWindow : Window
{
    /// <summary>显示类别：Class=班级课表，Teacher=教师个人课表。</summary>
    public string Kind { get; set; } = "Class";

    private string _semesterStart = "";
    private string? _selectedClass;
    private PanelLayout? _layout;
    private ScheduleFile? _schedule;
    private bool _clickThrough;
    private AutoHider? _hider;
    private readonly DispatcherTimer _timer;

    // 长按（触摸/鼠标左键按住）弹出编辑
    private DispatcherTimer? _pressTimer;
    private PeriodRow? _pressRow;
    private System.Windows.Point _pressPoint;

    // 拖动调课
    private PeriodRow? _dragRow;
    private System.Windows.Point _dragStart;

    public ScheduleWindow()
    {
        InitializeComponent();
        TodayPanel.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<PeriodRow>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Refresh();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        _semesterStart = s.SemesterStartMonday;
        _selectedClass = Kind == "Teacher" ? s.SelectedTeacher : s.SelectedClass;
        Title = Kind == "Teacher" ? "教师课表悬浮窗" : "课表悬浮窗";
        ClassHeader.ToolTip = Kind == "Teacher"
            ? "双击此处可切换显示其他教师课表"
            : "双击此处可切换显示其他班级课表";
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
        // 置顶显示(AutoHide=false) 即开启鼠标穿透，并按预设透明度显示；自动隐藏态则为普通透明度
        bool topShow = !l.AutoHide;
        _clickThrough = topShow;
        FloatingHelper.ApplyLayout(this, l, topShow, topShow ? l.OverlayOpacity : l.Opacity);
        if (topShow)
            FloatingHelper.SetClickThrough(this, true, BtnHide, ClassHeader, DateHeader);
        else
            FloatingHelper.SetClickThrough(this, false);

        TxtHint.Visibility = (!topShow && Kind == "Class") ? Visibility.Visible : Visibility.Collapsed;
        TxtTeacherHint.Visibility = (!topShow && Kind == "Teacher") ? Visibility.Visible : Visibility.Collapsed;
        BtnHide.Content = l.AutoHide ? "置顶显示" : "自动隐藏";

        _hider?.Dispose();
        _hider = (l.AutoHide || l.AutoHideDuringClass)
            ? new AutoHider(this, l, () => l.AutoHide || (l.AutoHideDuringClass && IsClassTimeNow()))
            : null;
    }

    private void OnLayoutChanged()
    {
        _semesterStart = Store.LoadSettings().SemesterStartMonday;
        _selectedClass = Kind == "Teacher" ? Store.LoadSettings().SelectedTeacher : Store.LoadSettings().SelectedClass;
        ApplyCurrentLayout();
        Refresh();
    }

    private bool IsClassTimeNow() => WeekHelper.IsClassTimeNow(_schedule, _selectedClass);

    private void Refresh()
    {
        var s = Store.LoadSettings();
        _selectedClass = Kind == "Teacher" ? s.SelectedTeacher : s.SelectedClass;
        _semesterStart = s.SemesterStartMonday;
        _schedule = Kind == "Teacher" ? Store.LoadTeacherSchedule() : Store.LoadSchedule();
        var displayDate = BoardBuilder.ResolveDisplayDate(s);
        var res = BoardBuilder.Build(_selectedClass, _semesterStart, _schedule, displayDate);

        TxtClass.Text = string.IsNullOrEmpty(res.ClassName) ? (Kind == "Teacher" ? "未选择教师" : "未选择班级") : res.ClassName;
        TxtDate.Text = res.DateText;
        TxtWeek.Text = res.WeekLine;

        string prefix = Kind == "Teacher" ? "班级：" : "教师：";
        var rows = (System.Collections.ObjectModel.ObservableCollection<PeriodRow>)TodayPanel.ItemsSource;
        rows.Clear();
        foreach (var r in res.Rows)
        {
            foreach (var lv in r.Lessons) lv.TeacherPrefix = prefix;
            rows.Add(r);
        }
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

    // ---------- 班级/教师区双击：切换显示 ----------
    private void ClassHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            OpenClassSwitch();
            e.Handled = true;
        }
    }

    private void OpenClassSwitch()
    {
        if (Kind == "Teacher")
        {
            var s = Store.LoadSettings();
            var w = new TeacherSwitchWindow(s.SelectedTeacher) { Owner = this, Topmost = true };
            if (w.ShowDialog() == true) AppEvents.RaiseScheduleChanged();
        }
        else
        {
            var s = Store.LoadSettings();
            var w = new ClassSwitchWindow(s.SelectedClass) { Owner = this, Topmost = true };
            if (w.ShowDialog() == true) AppEvents.RaiseScheduleChanged();
        }
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
        // 置顶显示 ⇔ 鼠标穿透 + 预设透明度；自动隐藏 ⇔ 普通显示、可交互
        Store.SaveSettings(s);
        ApplyCurrentLayout();
        AppEvents.RaiseLayoutChanged();
    }

    // ---------- 长按 / 右键 弹出调课编辑（仅班级课表可编辑） ----------
    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_kind_NonEditable) return;
        if (_clickThrough) return;
        var row = FindPeriodRow(e.OriginalSource as DependencyObject);
        if (row != null) { OpenEditor(row); e.Handled = true; }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_kind_NonEditable) return;
        if (_clickThrough) return;
        _pressRow = FindPeriodRow(e.OriginalSource as DependencyObject);
        if (_pressRow != null)
        {
            _pressPoint = e.GetPosition(this);
            StartPressTimer();
        }
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CancelPress();

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pressTimer != null && _pressTimer.IsEnabled)
        {
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) > 8 || Math.Abs(p.Y - _pressPoint.Y) > 8)
                CancelPress();
        }
    }

    private void Window_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (_kind_NonEditable) return;
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
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var current = row.Lessons.Count > 0 ? row.Lessons[0] : new LessonView();
        var w = new LessonOverrideWindow(_selectedClass, row.DayIdx, row.Period, current, _schedule)
        {
            Owner = this,
            Topmost = true
        };
        if (w.ShowDialog() == true)
            AppEvents.RaiseScheduleChanged();
    }

    // ---------- 拖动调课（仅班级课表） ----------
    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_kind_NonEditable) return;
        if (_clickThrough) return;
        if (sender is FrameworkElement fe && fe.DataContext is PeriodRow row && row.Period > 0)
        {
            var lesson = row.Lessons.FirstOrDefault(l => !string.IsNullOrEmpty(l.Subject));
            if (lesson == null) { _dragRow = null; return; }
            _dragRow = row;
            _dragStart = e.GetPosition(fe);
        }
    }

    private void Card_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_kind_NonEditable) return;
        if (_clickThrough || _dragRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement fe) return;
        var p = e.GetPosition(fe);
        if (Math.Abs(p.X - _dragStart.X) > 6 || Math.Abs(p.Y - _dragStart.Y) > 6)
        {
            CancelPress();
            var src = _dragRow;
            _dragRow = null;
            System.Windows.DragDrop.DoDragDrop(fe, new System.Windows.DataObject("DayPeriod", src),
                System.Windows.DragDropEffects.Move);
        }
    }

    private void Card_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragRow = null;

    private void Card_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DayPeriod")) { e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true; }
    }

    private void Card_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData("DayPeriod") is PeriodRow src
            && sender is FrameworkElement fe
            && fe.DataContext is PeriodRow tgt
            && src != tgt)
        {
            ApplyDayDrag(src, tgt);
            e.Handled = true;
        }
    }

    private void ApplyDayDrag(PeriodRow src, PeriodRow tgt)
    {
        if (src.Period == tgt.Period || string.IsNullOrEmpty(_selectedClass)) return;
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

    private void UpsertOverride(System.Collections.Generic.List<LessonOverride> list, int dayIdx, int period,
        string subject, string teacher, string location, string type, bool isEmpty = false)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
        string key = $"{_selectedClass}|{dayIdx}|{period}";
        list.RemoveAll(o => o.Key == key);
        if (isEmpty)
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedClass, DayIdx = dayIdx, Period = period,
                IsEmpty = true, Type = "调", Source = "manual", UpdatedAt = DateTime.Now
            });
        }
        else if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedClass, DayIdx = dayIdx, Period = period,
                Subject = subject, Teacher = teacher, Location = location,
                Type = type, Source = "manual", UpdatedAt = DateTime.Now
            });
        }
    }

    private bool _kind_NonEditable => Kind != "Class";

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
        _hider?.Dispose();
        AppEvents.ScheduleChanged -= Refresh;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        _timer.Stop();
        base.OnClosed(e);
    }
}
