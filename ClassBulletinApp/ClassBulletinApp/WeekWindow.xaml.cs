using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 因项目同时引用了 System.Windows.Forms（托盘），以下 WPF 类型存在歧义，统一用别名
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfDataObject = System.Windows.DataObject;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfMessageBox = System.Windows.MessageBox;
using WpfCursors = System.Windows.Input.Cursors;

namespace ClassBulletinApp;

public partial class WeekWindow : Window
{
    private string? _selectedClass;
    private string _semesterStart = "";
    private DateTime _monday;
    private WeekCell? _dragSrc;
    private System.Windows.Point _dragStart;
    private string _editType = "调";   // 调课 / 代课（由顶部下拉选择）

    public WeekWindow()
    {
        InitializeComponent();
        _monday = StartOfWeek(DateTime.Now);
        Loaded += (_, _) => { AppEvents.ScheduleChanged += Rebuild; Rebuild(); };
    }

    protected override void OnClosed(EventArgs e)
    {
        AppEvents.ScheduleChanged -= Rebuild;
        base.OnClosed(e);
    }

    // ---------- 周导航 ----------
    private void BtnPrevWeek_Click(object sender, RoutedEventArgs e) { _monday = _monday.AddDays(-7); Rebuild(); }
    private void BtnThisWeek_Click(object sender, RoutedEventArgs e) { _monday = StartOfWeek(DateTime.Now); Rebuild(); }
    private void BtnNextWeek_Click(object sender, RoutedEventArgs e) { _monday = _monday.AddDays(7); Rebuild(); }

    // ---------- 调课 / 代课 模式 ----------
    private void CbEditType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _editType = CbEditType.SelectedIndex == 1 ? "代" : "调";
    }

    private static DateTime StartOfWeek(DateTime date)
        => date.AddDays(-((int)date.DayOfWeek + 6) % 7).Date;

    // ---------- 构建 ----------
    private void Rebuild()
    {
        var s = Store.LoadSettings();
        _selectedClass = s.SelectedClass;
        _semesterStart = s.SemesterStartMonday;
        var schedule = Store.LoadSchedule();
        var overrides = Store.LoadOverrides();
        if (schedule == null)
        {
            TxtStatus.Text = "尚未导入课表，请先在控制面板「导入课表」。";
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

        WeekGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); // 星期表头行
        for (int p = 0; p < data.MaxPeriod; p++)
            WeekGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(92) }); // 每节课格高度

        WeekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); // 节次表头列
        for (int d = 0; d < 7; d++)
            WeekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 星期表头
        for (int d = 0; d < 7; d++)
        {
            var h = new Border
            {
                Background = Brush("#2D6CDF"),
                Margin = new Thickness(3),
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
            // 节次表头
            var ph = new Border
            {
                Background = Brush("#EAF1FD"),
                Margin = new Thickness(3),
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
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(3),
            Padding = new Thickness(6),
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
                sp.Children.Add(new TextBlock
                {
                    Text = cell.Teacher,
                    FontSize = 12,
                    Foreground = Brush("#4B5563")
                });
            if (!string.IsNullOrEmpty(cell.LocationText))
                sp.Children.Add(new TextBlock
                {
                    Text = cell.LocationText.Trim(),
                    FontSize = 11,
                    Foreground = Brush("#2E8B57")
                });
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

        // 拖拽：源（仅从有课的格子开始）
        border.PreviewMouseLeftButtonDown += (s, e) =>
        {
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
                    var src = _dragSrc;
                    _dragSrc = null;
                    DragDrop.DoDragDrop(border, new WpfDataObject("WeekCell", src), WpfDragDropEffects.Move);
                }
            }
        };
        border.PreviewMouseLeftButtonUp += (s, e) => { _dragSrc = null; };

        // 拖拽：目标
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

        // 右键清除该格调课
        border.MouseRightButtonUp += (s, e) =>
        {
            ClearCell(cell);
            e.Handled = true;
        };

        return border;
    }

    // ---------- 调课逻辑 ----------
    private void ApplyDrag(WeekCell src, WeekCell tgt)
    {
        if (src.DayIdx == tgt.DayIdx && src.Period == tgt.Period) return;
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var list = Store.LoadOverrides();

        if (!tgt.HasLesson)
        {
            // 移动：目标 = 源内容（按当前选择的调/代类型）；源 = 空（清空）
            Upsert(list, tgt.DayIdx, tgt.Period, src.Subject, src.Teacher, src.Location, _editType, isEmpty: !src.HasLesson);
            Upsert(list, src.DayIdx, src.Period, "", "", "", "", isEmpty: true);
        }
        else
        {
            // 互换（两格均按当前选择的调/代类型写入）
            Upsert(list, tgt.DayIdx, tgt.Period, src.Subject, src.Teacher, src.Location, _editType, isEmpty: !src.HasLesson);
            Upsert(list, src.DayIdx, src.Period, tgt.Subject, tgt.Teacher, tgt.Location, _editType, isEmpty: !tgt.HasLesson);
        }

        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = $"已调课：{src.DayName}第{src.Period}节 ⇄ {tgt.DayName}第{tgt.Period}节";
    }

    private void ClearCell(WeekCell cell)
    {
        if (string.IsNullOrEmpty(_selectedClass)) return;
        var list = Store.LoadOverrides();
        list.RemoveAll(o => o.ClassName == _selectedClass && o.DayIdx == cell.DayIdx && o.Period == cell.Period);
        Store.SaveOverrides(list);
        AppEvents.RaiseScheduleChanged();
        Rebuild();
        TxtStatus.Text = $"已清除 {cell.DayName}第{cell.Period}节 的调课";
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
                ClassName = _selectedClass,
                DayIdx = dayIdx,
                Period = period,
                IsEmpty = true,
                Type = "调",
                Source = "manual",
                UpdatedAt = DateTime.Now
            });
        }
        else if (!string.IsNullOrWhiteSpace(subject))
        {
            list.Add(new LessonOverride
            {
                ClassName = _selectedClass,
                DayIdx = dayIdx,
                Period = period,
                Subject = subject,
                Teacher = teacher,
                Location = location,
                Type = string.IsNullOrEmpty(type) ? "调" : type,
                Source = "manual",
                UpdatedAt = DateTime.Now
            });
        }
        // 否则（非空 subject 且非 isEmpty）不添加 → 恢复为基础课
    }

    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex)!); }
        catch { return new SolidColorBrush(Colors.Black); }
    }
}
