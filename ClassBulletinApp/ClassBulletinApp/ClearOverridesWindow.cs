using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Services;

// System.Windows.Forms（托盘）也注入全局 using，Color / HorizontalAlignment / VerticalAlignment
// 都有同名类型；属性名还会遮蔽类型名（HorizontalAlignment = HAlign.Right 编译不过）。
using MediaColor = System.Windows.Media.Color;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 清除临时调课 / 代课：先选范围（本周 / 指定日期范围 / 全部），再让人看清"删几条、留几条"。
///
/// <para>设计动机：原来只有一个「全部清除」按钮，想清掉本周的调课就得连历史一起清，
/// 而确认框只说"共 N 条"，人根本不知道这 N 条是哪段时间的。</para>
///
/// <para>★ 长期改课（<c>Source=basePatch</c>）永不参与清除，窗口里会明确显示保留了多少条。</para>
/// </summary>
public class ClearOverridesWindow : Window
{
    readonly RadioButton _rWeek = new();
    readonly RadioButton _rRange = new();
    readonly RadioButton _rAll = new();

    readonly DatePicker _from = new();
    readonly DatePicker _to = new();

    readonly TextBlock _weekHint = new();
    readonly TextBlock _preview = new();
    readonly TextBlock _protect = new();
    readonly Button _ok;

    /// <summary>确认后的清除计划；取消为 null。</summary>
    public ClearPlan? Result { get; private set; }

    public ClearOverridesWindow()
    {
        Title = "清除临时调课 / 代课";
        Width = 620;
        // 高度交给内容决定：选了「指定日期范围」会多出一行，写死 Height 在不同 DPI 下
        // 会把底部按钮挤出窗口（UpdateSettingsWindow 踩过）。
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var st = Store.LoadSettings();
        int startDay = WeekHelper.NormalizeStartDay(st.WeekStartDay);
        var (ws, we) = WeekHelper.WeekWindowOf(WeekHelper.WeekStart(DateTime.Today, startDay));

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "清除哪一段的临时调课？",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x2A, 0x37)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        root.Children.Add(new TextBlock
        {
            Text = "这里清的是「临时」层：手动改课、从临时课表导入的调代、悬浮窗改的记录。"
                 + "长期改课（改在原始课表上的）属于底表本身，不受影响。",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x6B, 0x72, 0x80)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // ---------- ① 本周 ----------
        _rWeek.Content = "清除本周";
        _rWeek.GroupName = "clear";
        _rWeek.FontSize = 14;
        _rWeek.Margin = new Thickness(0, 0, 0, 2);

        _weekHint.Text = $"本周是 {ws:M月d日}（{WeekHelper.StartDayName(startDay)}）~ {we:M月d日}，"
                       + "只清这 7 天里生效的记录，往后的周不受影响。";
        _weekHint.FontSize = 12;
        _weekHint.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x6B, 0x72, 0x80));
        _weekHint.TextWrapping = TextWrapping.Wrap;
        _weekHint.Margin = new Thickness(26, 0, 0, 12);

        root.Children.Add(_rWeek);
        root.Children.Add(_weekHint);

        // ---------- ② 指定日期范围 ----------
        _rRange.Content = "清除指定日期范围";
        _rRange.GroupName = "clear";
        _rRange.FontSize = 14;
        _rRange.Margin = new Thickness(0, 0, 0, 6);

        foreach (var dp in new[] { _from, _to })
        {
            dp.Width = 150;
            dp.FontSize = 13.5;
        }
        _from.SelectedDate = DateTime.Today;
        _to.SelectedDate = DateTime.Today.AddDays(6);

        var rangeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(26, 0, 0, 12)
        };
        rangeRow.Children.Add(new TextBlock
        {
            Text = "从",
            FontSize = 13.5,
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        rangeRow.Children.Add(_from);
        rangeRow.Children.Add(new TextBlock
        {
            Text = "到",
            FontSize = 13.5,
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(10, 0, 6, 0)
        });
        rangeRow.Children.Add(_to);

        root.Children.Add(_rRange);
        root.Children.Add(rangeRow);

        // ---------- ③ 全部 ----------
        _rAll.Content = "清除全部临时记录";
        _rAll.GroupName = "clear";
        _rAll.FontSize = 14;
        _rAll.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(_rAll);

        // ---------- 预览 ----------
        _preview.FontSize = 13.5;
        _preview.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x2A, 0x37));
        _preview.TextWrapping = TextWrapping.Wrap;

        _protect.FontSize = 12.5;
        _protect.Foreground = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x63, 0x1F));
        _protect.TextWrapping = TextWrapping.Wrap;
        _protect.Margin = new Thickness(0, 6, 0, 0);
        _protect.Visibility = Visibility.Collapsed;

        var previewStack = new StackPanel();
        previewStack.Children.Add(_preview);
        previewStack.Children.Add(_protect);

        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0xF7, 0xF9, 0xFC)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE6, 0xF0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = previewStack
        });

        root.Children.Add(new TextBlock
        {
            Text = "此操作不可撤销。不确定的话可以先在「备份与恢复」里点一次「立即备份一份」。",
            FontSize = 12,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x9C, 0x3B, 0x3B)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        // ---------- 按钮 ----------
        var cancel = new Button
        {
            Content = "取消",
            FontSize = 14,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xF3, 0xF4, 0xF6)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xD1, 0xD5, 0xDB))
        };
        cancel.Click += (_, _) => { Result = null; DialogResult = false; };

        _ok = new Button
        {
            Content = "清除",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(26, 8, 26, 8),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xFD, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xA0, 0xA0)),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x9C, 0x3B, 0x3B))
        };
        _ok.Click += (_, _) => Confirm();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HAlign.Right
        };
        bar.Children.Add(cancel);
        bar.Children.Add(_ok);
        root.Children.Add(bar);

        Content = root;

        // ★ 控件全部建好、_ok 已赋值之后才挂事件：
        //   构造函数前半段设 IsChecked / SelectedDate 时若已挂事件，会提前触发 UpdatePreview 摸到 null。
        _rWeek.Checked += (_, _) => OnScopeChanged();
        _rRange.Checked += (_, _) => OnScopeChanged();
        _rAll.Checked += (_, _) => OnScopeChanged();
        _from.SelectedDateChanged += (_, _) => UpdatePreview();
        _to.SelectedDateChanged += (_, _) => UpdatePreview();

        _rWeek.IsChecked = true;   // 默认「本周」
        SyncDateEnabled();
        UpdatePreview();
    }

    void OnScopeChanged()
    {
        SyncDateEnabled();
        UpdatePreview();
    }

    /// <summary>日期选择器只在「指定日期范围」模式下可用（灰掉比隐藏更稳，窗口高度不会跳）。</summary>
    void SyncDateEnabled()
    {
        bool on = _rRange.IsChecked == true;
        _from.IsEnabled = on;
        _to.IsEnabled = on;
    }

    /// <summary>按当前选择算一遍计划，把"删几条、留几条"直接摆出来。</summary>
    void UpdatePreview()
    {
        ClearPlan plan;
        try { plan = OverrideCleaner.Plan(BuildScope(), RangeFrom, RangeTo); }
        catch (Exception ex)
        {
            _preview.Text = "读取调课记录失败：" + ex.Message;
            _ok.IsEnabled = false;
            return;
        }

        if (plan.Targets == 0)
        {
            _preview.Text = plan.Scope == ClearScope.All
                ? "当前没有任何临时调课记录。"
                : $"{plan.RangeText} 范围内没有临时调课记录。";
            _ok.IsEnabled = false;
        }
        else
        {
            _preview.Text = plan.Describe() + $"\n清除后还剩 {plan.Remaining} 条。";
            _ok.IsEnabled = true;
        }

        if (plan.Protected > 0)
        {
            _protect.Text = $"另有 {plan.Protected} 条「长期改课」会保留"
                          + " —— 它们改的是原始课表本身，不属于临时调整。";
            _protect.Visibility = Visibility.Visible;
        }
        else
        {
            _protect.Visibility = Visibility.Collapsed;
        }
    }

    ClearScope BuildScope()
        => _rRange.IsChecked == true ? ClearScope.Range
         : _rAll.IsChecked == true ? ClearScope.All
         : ClearScope.ThisWeek;

    DateTime RangeFrom => _from.SelectedDate ?? DateTime.Today;
    DateTime RangeTo => _to.SelectedDate ?? DateTime.Today;

    void Confirm()
    {
        var plan = OverrideCleaner.Plan(BuildScope(), RangeFrom, RangeTo);
        if (plan.Targets == 0) { DialogResult = false; return; }

        // 「全部」且量大时再问一遍：这是一步就能清掉整个学期临时安排的按钮
        if (plan.Scope == ClearScope.All && plan.Targets >= 20)
        {
            var r = MessageBox.Show(
                $"确定要清除全部 {plan.Targets} 条临时记录吗？涉及 {plan.ClassCount} 个班，将全部失效。\n\n此操作不可撤销。",
                "确认清除全部", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
        }

        Result = plan;
        DialogResult = true;
    }
}
