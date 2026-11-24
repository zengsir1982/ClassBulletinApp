using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Services;

// 项目同时引用了 WPF 与 WinForms，同名类型用别名消歧义
using ListBox = System.Windows.Controls.ListBox;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 导入名单的确认窗：列出"新增多少、更新多少、谁会登不进来、哪些跳过"，确认后才写盘。
///
/// <para>★ 原来的导入是<b>直接写盘再报结果</b>，一张列错位的表能在两秒内把全校角色改掉，
/// 而界面上只留一句"更新 213 个"。这里把机器该做的事（比对、分类、说清后果）放在人眼前，
/// 人只负责点一次确认。计划本身在 <see cref="UserImportService.Build"/> 里算好，
/// 本窗口只负责显示 —— 所以"取消"时一个字节都没动。</para>
/// </summary>
public class UserImportPreviewWindow : Window
{
    public UserImportPreviewWindow(UserImportResult result, string sourceName)
    {
        Title = "导入预览";
        Width = 760;
        Height = 600;
        MinWidth = 620;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ChromePalette.BrushOf(ChromePalette.Surface, "#F4F6FA");

        var root = new DockPanel { Margin = new Thickness(16, 14, 16, 14), LastChildFill = true };
        Content = root;

        // ---------- 标题 ----------
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        head.Children.Add(new TextBlock
        {
            Text = "确认要按这张表改账号吗？",
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });

        head.Children.Add(new TextBlock
        {
            Text = "来源：" + sourceName,
            FontSize = 12,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        });

        // ---------- 统计 ----------
        var stats = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(stats, Dock.Top);
        root.Children.Add(stats);

        void Chip(string text, string fg, string bg)
        {
            stats.Children.Add(new Border
            {
                Background = ListRowStyle.HexBrush(bg),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 6),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    Foreground = ListRowStyle.HexBrush(fg)
                }
            });
        }

        if (result.Add > 0) Chip($"新增 {result.Add} 个", "#1E7B4F", "#E8F6EE");
        if (result.Update > 0) Chip($"更新 {result.Update} 个", "#1E4FA3", "#E9EFF9");
        if (result.Disable > 0) Chip($"将被停用 {result.Disable} 个", "#B45309", "#FEF3E2");
        if (result.Enable > 0) Chip($"将被启用 {result.Enable} 个", "#1E7B4F", "#E8F6EE");
        if (result.Same > 0) Chip($"无需改动 {result.Same} 个", "#667085", "#F0F2F6");
        if (result.Skip > 0) Chip($"跳过 {result.Skip} 个", "#B42318", "#FDE8E8");

        // ---------- 警示（有后果的事先说）----------
        if (result.Disable > 0 || result.LoseAllAdmins)
        {
            var warn = new Border
            {
                Background = ListRowStyle.HexBrush("#FEF3E2"),
                BorderBrush = ListRowStyle.HexBrush("#F0C48A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(warn, Dock.Top);
            root.Children.Add(warn);

            string msg = "";
            if (result.Disable > 0)
                msg += $"有 {result.Disable} 个账号会被停用（他们下次就登不进来了）。状态列写「启用」才会重新打开。\n";
            if (result.LoseAllAdmins)
                msg += "按这张表改完将没有任何启用的管理员 —— 确认时会自动把第一个账号设为管理员，否则没人能再管理用户。";

            warn.Child = new TextBlock
            {
                Text = msg.TrimEnd(),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ListRowStyle.HexBrush("#8A4B08")
            };
        }

        // ---------- 底部按钮 ----------
        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var hint = new TextBlock
        {
            Text = "确认后立即写入本机 users.json；要下发到教室机，还需「发布数据包」。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
        };
        Grid.SetColumn(hint, 0);
        bar.Children.Add(hint);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);

        var btnCancel = new Button { Content = "取消", Padding = new Thickness(14, 7, 14, 7), FontSize = 13 };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        right.Children.Add(btnCancel);

        var btnOk = new Button
        {
            Content = "确认导入",
            Padding = new Thickness(16, 7, 16, 7),
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            Background = ListRowStyle.HexBrush("#E8F6EE")
        };
        btnOk.IsEnabled = result.AnyChange;
        btnOk.Click += (_, _) => { DialogResult = true; Close(); };
        right.Children.Add(btnOk);

        // ---------- 明细列表 ----------
        var list = new ListBox
        {
            FontSize = 13,
            BorderThickness = new Thickness(1),
            BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
            Background = Brushes.White,
            Padding = new Thickness(0, 4, 0, 4)
        };

        foreach (var p in result.Plans)
        {
            var row = new StackPanel { Margin = new Thickness(2, 1, 2, 1) };

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new Border
            {
                Background = ListRowStyle.HexBrush(KindBg(p.Kind)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = KindText(p.Kind),
                    FontSize = 11,
                    Foreground = ListRowStyle.HexBrush(KindFg(p.Kind))
                }
            });
            line.Children.Add(new TextBlock
            {
                Text = p.Username,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            line.Children.Add(new TextBlock
            {
                Text = p.Note,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = p.Kind == UserImportKind.Skip
                    ? ListRowStyle.HexBrush("#B42318")
                    : ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
            });
            row.Children.Add(line);

            list.Items.Add(new ListBoxItem
            {
                Content = row,
                Style = ListRowStyle.Get(),
                ToolTip = p.Note
            });
        }

        root.Children.Add(list);

        GlassChrome.Prepare(this);
    }

    static string KindText(UserImportKind k) => k switch
    {
        UserImportKind.Add => "新增",
        UserImportKind.Update => "更新",
        _ => "跳过"
    };

    static string KindBg(UserImportKind k) => k switch
    {
        UserImportKind.Add => "#E8F6EE",
        UserImportKind.Update => "#E9EFF9",
        _ => "#FDE8E8"
    };

    static string KindFg(UserImportKind k) => k switch
    {
        UserImportKind.Add => "#1E7B4F",
        UserImportKind.Update => "#1E4FA3",
        _ => "#B42318"
    };
}
