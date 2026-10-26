using System;
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
        _selectedClass = s.SelectedClass;
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
        _clickThrough = l.ClickThrough;
        FloatingHelper.ApplyLayout(this, l);
        TxtHint.Visibility = l.ClickThrough ? Visibility.Collapsed : Visibility.Visible;
        _hider?.Dispose();
        _hider = (l.AutoHide || l.AutoHideDuringClass)
            ? new AutoHider(this, l, () => l.AutoHide || (l.AutoHideDuringClass && IsClassTimeNow()))
            : null;
    }

    private void OnLayoutChanged()
    {
        _semesterStart = Store.LoadSettings().SemesterStartMonday;
        _selectedClass = Store.LoadSettings().SelectedClass;
        ApplyCurrentLayout();
        Refresh();
    }

    private bool IsClassTimeNow() => WeekHelper.IsClassTimeNow(_schedule, _selectedClass);

    private void Refresh()
    {
        var s = Store.LoadSettings();
        _selectedClass = s.SelectedClass;
        _semesterStart = s.SemesterStartMonday;
        _schedule = Store.LoadSchedule();
        var displayDate = BoardBuilder.ResolveDisplayDate(s);
        var res = BoardBuilder.Build(_selectedClass, _semesterStart, _schedule, displayDate);

        TxtClass.Text = string.IsNullOrEmpty(res.ClassName) ? "未选择班级" : res.ClassName;
        TxtDate.Text = res.DateText;
        TxtWeek.Text = res.WeekLine;

        var rows = (System.Collections.ObjectModel.ObservableCollection<PeriodRow>)TodayPanel.ItemsSource;
        rows.Clear();
        foreach (var r in res.Rows) rows.Add(r);
    }

    // ---------- 长按 / 右键 弹出调课编辑（替代双击，便于触屏） ----------
    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        var row = FindPeriodRow(e.OriginalSource as DependencyObject);
        if (row != null) { OpenEditor(row); e.Handled = true; }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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

    protected override void OnClosed(EventArgs e)
    {
        _hider?.Dispose();
        AppEvents.ScheduleChanged -= Refresh;
        AppEvents.LayoutChanged -= OnLayoutChanged;
        _timer.Stop();
        base.OnClosed(e);
    }
}
