using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 项目同时引用 System.Windows.Forms（托盘），以下 WPF 类型存在歧义，统一别名
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfDataObject = System.Windows.DataObject;
using WpfDragDropEffects = System.Windows.DragDropEffects;
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

    private string? _selectedClass;
    private string _semesterStart = "";
    private DateTime _monday;
    private WeekCell? _dragSrc;
    private WeekCell? _pressCell;
    private System.Windows.Point _dragStart;
    private bool _cellDragged;
    private string _editType = "调";   // 拖动产生的覆盖类型

    public WeekView()
    {
        InitializeComponent();
        _monday = StartOfWeek(DateTime.Now);
        Loaded += (_, _) => { AppEvents.ScheduleChanged += Rebuild; Rebuild(); };
        Unloaded += (_, _) => { AppEvents.ScheduleChanged -= Rebuild; };
    }

    private static DateTime StartOfWeek(DateTime date)
        => date.AddDays(-((int)date.DayOfWeek + 6) % 7).Date;

    // ---------- 周导航 ----------
    private void BtnPrevWeek_Click(object sender, RoutedEventArgs e) { _monday = _monday.AddDays(-7); Rebuild(); }
    private void BtnThisWeek_Click(object sender, RoutedEventArgs e) { _monday = StartOfWeek(DateTime.Now); Rebuild(); }
    private void BtnNextWeek_Click(object sender, RoutedEventArgs e) { _monday = _monday.AddDays(7); Rebuild(); }

    private void ApplyModeHint()
    {
        if (!IsLoaded || TxtHint == null) return;
        TxtHint.Text = Mode == WeekViewMode.Manage
            ? "操作：拖动课卡可调课（目标有课则互换，为空则移动并清空原格）；右键某格 = 安排调课/代课；单击已安排的格子 = 二次确认后清除该节调课/代课。橙色=调课，蓝色=代课。"
            : "操作：拖动某节课到另一格（目标有课则互换，为空则移动并清空原格）；右键某格可清除该格调课。";
    }

    // ---------- 构建 ----------
    private void Rebuild()
    {
        ApplyModeHint();

        var s = Store.LoadSettings();
        _selectedClass = s.SelectedClass;
        _semesterStart = s.SemesterStartMonday;
        var schedule = Store.LoadSchedule();
        var overrides = Store.LoadOverrides();
        if (schedule == null)
        {
            TxtStatus.Text = "尚未导入课表，请先在控制面板「数据管理」导入课表。";
            WeekGrid.Children.Clear();
            TxtWeekRange.Text = "";
            return;
        }
        if (string.IsNullOrEmpty(_selectedClass))
        {
            TxtStatus.Text = "请先在控制面板选择班级。";
            WeekGrid.Children.Clear();
            TxtWeekRange.Text = "";
            return;
        }
        var data = WeekBuilder.BuildWeek(_selectedClass, _semesterStart, schedule, overrides, _monday);
        BuildGrid(data);
        TxtWeekRange.Text = $"第{WeekHelper.TeachingWeek(_monday, _semesterStart)}周　{_monday:MM月dd日} - {_monday.AddDays(6):MM月dd日}　{_selectedClass}";
    }

    private void BuildGrid(WeekGridData data)
    {
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
                Background = Brush("#2D6CDF"),
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
                Background = Brush("#EAF1FD"),
                Margin = new System.Windows.Thickness(3),
                CornerRadius = new CornerRadius(6)
            };
            ph.Child = new TextBlock
            {
                Text = data.PeriodHeaders[p],
                Foreground = Brush("#2D6CDF"),
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
                var cell = cellDict[(d, p + 1)];
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
            BorderBrush = Brush("#E3E8EF"),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new System.Windows.Thickness(3),
            Padding = new System.Windows.Thickness(6),
            AllowDrop = true,
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
                Text = cell.Subject,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Foreground = Brush(cell.SubjectColor),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(cell.Teacher))
                sp.Children.Add(new TextBlock { Text = cell.Teacher, FontSize = 12, Foreground = Brush("#4B5563") });
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

        // 拖拽（源）：仅从有课格子开始
        border.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _pressCell = cell;
            _cellDragged = false;
            if (!cell.HasLesson) { _dragSrc = null; return; }
            _dragSrc = cell;
            _dragStart = e.GetPosition(border);
        };
        border.PreviewMouseMove += (s, e) =>
        {
            if (_dragSrc != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(border);
                if (Math.Abs(p.X - _dragStart.X) > 4 || Math.Abs(p.Y - _dragStart.Y) > 4)
                {
                    _cellDragged = true;
                    var src = _dragSrc;
                    _dragSrc = null;
                    DragDrop.DoDragDrop(border, new WpfDataObject("WeekCell", src), WpfDragDropEffects.Move);
                }
            }
        };
        border.PreviewMouseLeftButtonUp += (s, e) =>
        {
            var press = _pressCell;
            _pressCell = null;
            _dragSrc = null;
            if (_cellDragged) { _cellDragged = false; return; }
            // 单击（未拖动）：Manage 模式下单击「已安排」的格子 → 二次确认清除
            if (Mode == WeekViewMode.Manage && press == cell && cell.IsOverridden)
                ConfirmClearCell(cell);
        };

        // 拖拽（目标）
        border.DragOver += (s, e) =>
        {
            if (e.Data.GetDataPresent("WeekCell")) { e.Effects = WpfDragDropEffects.Move; e.Handled = true; }
        };
        border.Drop += (s, e) =>
        {
            if (e.Data.GetData("WeekCell") is WeekCell src && src != cell)
            {
                ApplyDrag(src, cell);
                e.Handled = true;
            }
        };

        // 右键：Manage=安排调代课；Float=清除该格
        border.MouseRightButtonUp += (s, e) =>
        {
            if (Mode == WeekViewMode.Manage) OpenArrangeEditor(cell);
            else ClearCell(cell);
            e.Handled = true;
        };

        return border;
    }

    // ---------- 安排调课/代课（右键，Manage 模式） ----------
    private void OpenArrangeEditor(WeekCell cell)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
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
        var w = new LessonOverrideWindow(_selectedClass, cell.DayIdx, cell.Period, current, schedule)
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

    // ---------- 清除单格（Float 右键 / Manage 单击确认） ----------
    private void ConfirmClearCell(WeekCell cell)
    {
        var r = WpfMessageBox.Show(
            $"确定清除 {cell.DayName}第{cell.Period}节 的调课/代课安排吗？（恢复为原课表）",
            "清除调代课", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.OK) ClearCell(cell);
    }

    private void ClearCell(WeekCell cell)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var list = Store.LoadOverrides();
        int n = list.RemoveAll(o => o.ClassName == _selectedClass && o.DayIdx == cell.DayIdx && o.Period == cell.Period);
        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = n > 0 ? $"已清除 {cell.DayName}第{cell.Period}节 的调课" : $"{cell.DayName}第{cell.Period}节 无调课记录";
    }

    // ---------- 拖动调课 ----------
    private void ApplyDrag(WeekCell src, WeekCell tgt)
    {
        if (src.DayIdx == tgt.DayIdx && src.Period == tgt.Period) return;
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var list = Store.LoadOverrides();

        if (!tgt.HasLesson)
        {
            Upsert(list, tgt.DayIdx, tgt.Period, src.Subject, src.Teacher, src.Location, _editType, isEmpty: !src.HasLesson);
            Upsert(list, src.DayIdx, src.Period, "", "", "", "", isEmpty: true);
        }
        else
        {
            Upsert(list, tgt.DayIdx, tgt.Period, src.Subject, src.Teacher, src.Location, _editType, isEmpty: !src.HasLesson);
            Upsert(list, src.DayIdx, src.Period, tgt.Subject, tgt.Teacher, tgt.Location, _editType, isEmpty: !tgt.HasLesson);
        }

        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = $"已{(_editType == "代" ? "安排代课" : "调课")}：{src.DayName}第{src.Period}节 ⇄ {tgt.DayName}第{tgt.Period}节";
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var r = WpfMessageBox.Show($"确定清除「{_selectedClass}」全部调课 / 代课 / 调空记录吗？此操作不可撤销。",
            "清除全部调课", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        var list = Store.LoadOverrides();
        list.RemoveAll(o => o.ClassName == _selectedClass);
        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = "已清除全部调课记录。";
    }

    private void Upsert(List<LessonOverride> list, int dayIdx, int period, string subject, string teacher, string location, string type, bool isEmpty)
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
                Type = string.IsNullOrEmpty(type) ? "调" : type, Source = "manual", UpdatedAt = DateTime.Now
            });
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex)!); }
        catch { return new SolidColorBrush(Colors.Black); }
    }
}
