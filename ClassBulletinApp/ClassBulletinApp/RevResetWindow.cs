using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 托盘用的 WinForms 也注入了同名类型，按项目既有规矩用别名消歧义
using MediaColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfBrush = System.Windows.Media.Brush;
using HAlign = System.Windows.HorizontalAlignment;
using CheckBox = System.Windows.Controls.CheckBox;

namespace ClassBulletinApp;

/// <summary>
/// 代次维护（r144）：查看并清零本机的各个"高水位 / 计数器"。
///
/// <para><b>为什么要有这个窗。</b>课表与设置靠代次决定谁覆盖谁，而代次是每台机器各自累加的数字。
/// 恢复过备份、换过机器、或手工改过 settings.json 的教室机可能把 <c>AppliedCalendarRev</c>
/// 顶到 999 —— 发布端才 3，永远追不上，于是"下发了却毫无反应"，现场还看不出任何报错。
/// 清零是唯一出路。</para>
///
/// <para><b>为什么分两组。</b>教室机侧（已应用代次 / 推送凭据 / 包摘要）与发布端侧（三个计数器）
/// 语义完全不同：前者清了会"变开放"（更愿意接受下发），后者清了只是让自己重新从 1 数起。
/// 混在一起勾，很容易把不该动的也清了。</para>
/// </summary>
public class RevResetWindow : Window
{
    readonly List<Row> _rows = new();

    /// <summary>一行 = 一个计数器 + 一个勾选框。</summary>
    sealed class Row
    {
        public RevResetService.RevKind Kind { get; init; }
        public CheckBox Box { get; init; } = null!;
    }

    /// <summary>本次是否真的执行过重置（主窗据此刷新状态栏）。</summary>
    public bool DidReset { get; private set; }

    /// <summary>执行后的逐项说明。</summary>
    public List<string> Messages { get; private set; } = new();

    public RevResetWindow()
    {
        Title = "代次维护 · 重置高水位";
        Width = 640;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = ResolveBrush("SurfaceBrush", WpfBrushes.White);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "代次维护（重置高水位）",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = ResolveBrush("InkBrush", WpfBrushes.Black)
        });
        root.Children.Add(new TextBlock
        {
            Text = "课表与设置靠「代次」决定谁覆盖谁：教室机记一个已应用的值，新内容比它大才生效。"
                 + "这个值一旦虚高（恢复过备份、换过机器、手改过配置文件），发布端再怎么发都追不上，"
                 + "现象就是「明明下发了，教室机毫无反应」。清零后重新按正常顺序累加即可恢复。",
            FontSize = 12.5,
            Foreground = ResolveBrush("BodyBrush", WpfBrushes.DimGray),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 6, 0, 14)
        });

        var items = RevResetService.Snapshot();

        root.Children.Add(Section("A. 本机已应用（教室机侧）",
            "清零这些 = 让本机重新愿意接受下发。发错方向时是最常用的救火手段。",
            items.Where(x => !x.IsPublisher).ToList()));
        root.Children.Add(Section("B. 发布计数器（发布端侧）",
            "只在「重建发布机 / 换主控机 / 代次被搞乱」时用。清零只影响之后发出去的包从 1 数起。",
            items.Where(x => x.IsPublisher).ToList()));

        // 快捷选择：两组各一颗，省得逐个点
        var quick = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };
        quick.Children.Add(Ghost("只选教室机侧", () => SelectOnly(RevResetService.ClassroomKinds())));
        quick.Children.Add(Ghost("只选发布端", () => SelectOnly(RevResetService.PublisherKinds())));
        quick.Children.Add(Ghost("全不选", () => SelectOnly(Array.Empty<RevResetService.RevKind>())));
        root.Children.Add(quick);

        // 警示
        var warn = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0xFD, 0xF3, 0xF2)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xC0, 0x39, 0x2B)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new TextBlock
            {
                Text = "⚠ 清零不可撤销，也没有历史留痕。清「已应用设置代次 / 教学日历代次」之后，"
                     + "下一次收到的旧包或旧下发也会被当成新的应用一次；清「课表包摘要」会让本机重新下载一次整包。"
                     + "请在确认发布端内容无误后再操作。",
                FontSize = 12,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x2B, 0x20)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            }
        };
        root.Children.Add(warn);

        // 按钮固定在底栏：内容长了也只滚中间那块，不会把「重置选中项」挤到看不见的地方
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HAlign.Right
        };
        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(20, 9, 20, 9),
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true,
            Background = new SolidColorBrush(MediaColor.FromRgb(0xF3, 0xF4, 0xF6)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xC7, 0xCF, 0xDA))
        };
        cancel.Click += (_, _) => Close();

        var reset = new Button
        {
            Content = "重置选中项",
            Padding = new Thickness(22, 9, 22, 9),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Background = new SolidColorBrush(MediaColor.FromRgb(0xFD, 0xEC, 0xEA)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xE4, 0xA9, 0xA1)),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xC0, 0x39, 0x2B))
        };
        reset.Click += (_, _) => DoReset();

        buttons.Children.Add(cancel);
        buttons.Children.Add(reset);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        Grid.SetRow(scroll, 0);
        grid.Children.Add(scroll);

        var footer = new Border
        {
            BorderBrush = ResolveBrush("LineBrush", WpfBrushes.LightGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12, 24, 12),
            Child = buttons
        };
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        Content = grid;

        GlassChrome.Prepare(this);
    }

    WpfBrush ResolveBrush(string key, WpfBrush fallback)
    {
        try
        {
            if (TryFindResource(key) is WpfBrush b) return b;
        }
        catch { }
        return fallback;
    }

    /// <summary>一块分组：标题 + 一句说明 + 若干行。</summary>
    FrameworkElement Section(string title, string subtitle, List<RevResetService.RevItem> items)
    {
        var card = new StackPanel();

        card.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = ResolveBrush("InkBrush", WpfBrushes.Black)
        });
        card.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = ResolveBrush("MutedBrush", WpfBrushes.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 9)
        });

        foreach (var it in items)
        {
            var box = new CheckBox
            {
                Content = $"{it.Name}：{it.Value}",
                FontSize = 13.5,
                Foreground = ResolveBrush("BodyBrush", WpfBrushes.DimGray),
                ToolTip = it.Hint
            };
            _rows.Add(new Row { Kind = it.Kind, Box = box });

            var line = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            line.Children.Add(box);
            line.Children.Add(new TextBlock
            {
                Text = it.Hint,
                FontSize = 11.5,
                Foreground = ResolveBrush("HintBrush", WpfBrushes.Gray),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 2, 0, 0)
            });
            card.Children.Add(line);
        }

        return new Border
        {
            Background = ResolveBrush("SurfaceBrush", WpfBrushes.White),
            BorderBrush = ResolveBrush("LineBrush", WpfBrushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 6),
            Margin = new Thickness(0, 0, 0, 14),
            Child = card
        };
    }

    Button Ghost(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 12.5,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Background = WpfBrushes.White,
            BorderBrush = ResolveBrush("InputBorderBrush", WpfBrushes.LightGray),
            Foreground = ResolveBrush("BodyBrush", WpfBrushes.DimGray)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    void SelectOnly(IEnumerable<RevResetService.RevKind> kinds)
    {
        var set = new HashSet<RevResetService.RevKind>(kinds);
        foreach (var r in _rows) r.Box.IsChecked = set.Contains(r.Kind);
    }

    void DoReset()
    {
        var kinds = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Kind).ToList();
        if (kinds.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要重置的项。", "没有选中",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string names = string.Join("\n", _rows.Where(r => kinds.Contains(r.Kind))
            .Select(r => "· " + r.Box.Content));
        if (MessageBox.Show(this,
                "即将清零以下几项代次，此操作不可撤销：\n\n" + names +
                "\n\n确定继续吗？", "确认重置代次",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        try
        {
            Messages = RevResetService.Reset(kinds);
            DidReset = Messages.Count > 0;

            // 立刻把勾选框上的数值换成新值（不用关窗重开）
            var fresh = RevResetService.Snapshot().ToDictionary(x => x.Kind, x => x.Value);
            foreach (var r in _rows)
            {
                string label = r.Box.Content?.ToString() ?? "";
                int cut = label.IndexOf('：');
                if (cut > 0 && fresh.TryGetValue(r.Kind, out var v))
                    r.Box.Content = label[..(cut + 1)] + v;
                r.Box.IsChecked = false;
            }

            MessageBox.Show(this,
                (Messages.Count == 0 ? "没有改动任何项。" : "已重置：\n\n· " + string.Join("\n· ", Messages))
                + (Store.EnablePersistence ? "" : "\n\n（当前是游客模式，改动不会保存，重启后失效）"),
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重置失败：" + ex.Message, "出错了",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 诊断钩子（--ui-check 用，同程序集可见） ----------
    internal int DiagRowCount => _rows.Count;
    internal int DiagClassroomRows => _rows.Count(r => RevResetService.ClassroomKinds().Contains(r.Kind));
    internal int DiagPublisherRows => _rows.Count(r => RevResetService.PublisherKinds().Contains(r.Kind));

    /// <summary>按「教室机侧 / 发布端侧」快捷勾选，走真实的按钮逻辑。</summary>
    internal void DiagSelectOnly(bool classroom)
        => SelectOnly(classroom ? RevResetService.ClassroomKinds() : RevResetService.PublisherKinds());

    internal List<RevResetService.RevKind> DiagChecked
        => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Kind).ToList();
}
