using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Services;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ListBox = System.Windows.Controls.ListBox;
using Clipboard = System.Windows.Clipboard;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ClassBulletinApp;

/// <summary>
/// 权限变更记录查看（只读）。
///
/// <para>发布机每次在「角色权限」里保存改动，都会往 <c>policy.log</c> 追加一行
/// （时间 / 操作人 / 改了什么）。权限配错的后果往往几周后才显形，这个窗口就是给管理员
/// 倒查"是谁、在哪次、把哪项权限改了"用的。</para>
///
/// <para>★ 日志只写在<b>本机</b> AppData，不会随数据包下发——它记录的是"发布机上的决策"，
/// 本来就该留在发布机。教室机打开本窗口看到的是各自本机的记录（通常为空）。</para>
/// </summary>
public class PolicyLogWindow : Window
{
    public PolicyLogWindow()
    {
        Title = "权限变更记录";
        Width = 760;
        Height = 460;
        MinWidth = 560;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

        // ---------- 顶部说明 ----------
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock
        {
            Text = "权限变更记录",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---------- 底部按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var btnCopy = new Button
        {
            Content = "复制全部",
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false
        };
        btnCopy.Click += (_, _) => CopyAll();
        var btnClose = new Button
        {
            Content = "关闭",
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 13
        };
        btnClose.Click += (_, _) => Close();
        bar.Children.Add(btnCopy);
        bar.Children.Add(btnClose);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 表格 ----------
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });   // 时间
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });   // 操作人
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 变更内容

        // 表头
        string[] header = { "时间", "操作人", "变更内容" };
        for (int c = 0; c < header.Length; c++)
        {
            var t = new TextBlock
            {
                Text = header[c],
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Gray,
                Margin = new Thickness(6, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(t, 0);
            Grid.SetColumn(t, c);
            grid.Children.Add(t);
        }

        var list = new ListBox
        {
            FontSize = 13,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(list, 1);
        Grid.SetRowSpan(list, 1);
        Grid.SetColumn(list, 0);
        Grid.SetColumnSpan(list, 3);

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(list);

        DockPanel.SetDock(grid, Dock.Top);
        root.Children.Add(grid);

        Content = root;

        // ---------- 加载日志 ----------
        var rows = LoadRows();
        if (rows.Count == 0)
        {
            list.Items.Add(new TextBlock
            {
                Text = "（暂无权限变更记录）",
                FontSize = 13,
                Foreground = Brushes.Gray,
                Margin = new Thickness(6, 6, 0, 0)
            });
            _allText = "";
        }
        else
        {
            foreach (var r in rows)   // 已倒序
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var time = new TextBlock { Text = r.Time, Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };
                var who = new TextBlock { Text = r.Who, Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };
                var detail = new TextBlock { Text = r.Detail, Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };

                Grid.SetColumn(time, 0); Grid.SetColumn(who, 1); Grid.SetColumn(detail, 2);
                row.Children.Add(time); row.Children.Add(who); row.Children.Add(detail);
                list.Items.Add(new ListBoxItem
                {
                    Content = row,
                    Padding = new Thickness(0, 2, 0, 2),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
                });
            }
            btnCopy.IsEnabled = true;
            _allText = string.Join(Environment.NewLine, rows.ConvertAll(r => $"{r.Time}\t{r.Who}\t{r.Detail}"));
        }
    
        GlassChrome.Prepare(this);}

    string _allText = "";

    sealed class Row { public string Time = ""; public string Who = ""; public string Detail = ""; }

    static List<Row> LoadRows()
    {
        var outp = new List<Row>();
        string path = Path.Combine(Store.AppDir, "policy.log");
        if (!File.Exists(path)) return outp;
        try
        {
            // 倒序：最新的改动在最上面
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t', 3);
                outp.Add(new Row
                {
                    Time = parts.Length > 0 ? parts[0] : "",
                    Who = parts.Length > 1 ? parts[1] : "",
                    Detail = parts.Length > 2 ? parts[2] : "(格式异常)"
                });
            }
        }
        catch { /* 读不出就当空 */ }
        return outp;
    }

    void CopyAll()
    {
        if (string.IsNullOrEmpty(_allText)) return;
        try
        {
            Clipboard.SetText(_allText);
            System.Windows.MessageBox.Show("已复制全部权限变更记录到剪贴板。", "权限变更记录",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("复制失败：" + ex.Message, "权限变更记录",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
