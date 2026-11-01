using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Services;

// 同时引用 System.Windows.Forms（托盘）时同名类型会歧义，统一别名
using ListBox = System.Windows.Controls.ListBox;

namespace ClassBulletinApp;

/// <summary>
/// 备份与恢复：列出 <c>%APPDATA%\ClassBulletinApp\backup</c> 下的自动存档，
/// 可手动立即备份、回滚到某一份、打开备份文件夹。
/// 每次导入数据包 / 导入总课表 / 恢复前都会自动存档一份。
/// </summary>
public class BackupWindow : Window
{
    readonly ListBox _list = new();
    readonly TextBlock _status = new();

    /// <summary>本次会话是否执行过恢复（供调用方决定要不要刷新界面）。</summary>
    public bool Restored { get; private set; }

    public BackupWindow()
    {
        Title = "备份与恢复";
        Width = 780;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        // 用 DockPanel 而不是 Grid：按钮永远贴底，列表自适应剩余高度，
        // 不会因为内容高度估算不准把最后一行按钮挤出窗口。
        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

        var title = new TextBlock
        {
            Text = "数据备份",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var sub = new TextBlock
        {
            Text = "每次导入数据包、导入总课表或恢复备份前都会自动存档；最多保留 20 份，超出后自动删最旧的。",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var header = new StackPanel();
        header.Children.Add(title);
        header.Children.Add(sub);
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        _list.DisplayMemberPath = "Display";
        _list.FontSize = 14;
        _list.Margin = new Thickness(0, 0, 0, 10);

        _status.Text = "";
        _status.FontSize = 12;
        _status.Foreground = System.Windows.Media.Brushes.Gray;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 10);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 12)
        };

        var btnNow = MakeButton("立即备份一份", "#EAF2FD", "#9EC1F0");
        btnNow.Click += (_, _) =>
        {
            string dir = Store.BackupNow("手动备份");
            _status.Text = string.IsNullOrEmpty(dir)
                ? "没有任何可备份的数据（当前还没有课表 / 调代 / 通知）。"
                : "已备份到：" + dir;
            Reload();
        };

        var btnRestore = MakeButton("恢复到选中备份", "#FDF3E0", "#E8C48A");
        btnRestore.Click += (_, _) => RestoreSelected();

        var btnOpen = MakeButton("打开备份文件夹", "#F3F4F6", "#D1D5DB");
        btnOpen.Click += (_, _) =>
        {
            try
            {
                if (!System.IO.Directory.Exists(Store.BackupRoot))
                    System.IO.Directory.CreateDirectory(Store.BackupRoot);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Store.BackupRoot,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _status.Text = "打开文件夹失败：" + ex.Message;
            }
        };

        var btnClose = MakeButton("关闭", "#F3F4F6", "#D1D5DB");
        btnClose.Click += (_, _) => Close();

        bar.Children.Add(btnNow);
        bar.Children.Add(btnRestore);
        bar.Children.Add(btnOpen);
        bar.Children.Add(btnClose);

        // Dock 顺序：说明(Top) → 按钮(Top) → 状态(Bottom) → 列表(填满)
        // 按钮放在列表上方而不是窗口底部：底部在任何缩放/字体设置下都可能被裁掉一截，
        // 而按钮被裁 = 功能点不到，代价远大于"按钮不在底部"这点美观。
        System.Windows.Controls.DockPanel.SetDock(bar, System.Windows.Controls.Dock.Top);
        System.Windows.Controls.DockPanel.SetDock(_status, System.Windows.Controls.Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_status);
        root.Children.Add(_list);   // 最后一个填满剩余高度

        Content = root;
        Reload();
    }

    static Button MakeButton(string text, string bg, string border)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 14,
            Background = HexBrush(bg),
            BorderBrush = HexBrush(border)
        };
    }

    static System.Windows.Media.Brush HexBrush(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(hex)!;
            return new System.Windows.Media.SolidColorBrush(c);
        }
        catch
        {
            return System.Windows.Media.Brushes.WhiteSmoke;
        }
    }

    void Reload()
    {
        var items = Store.ListBackups();
        _list.ItemsSource = items;
        if (items.Count == 0)
        {
            _status.Text = "还没有任何备份。导入一次数据包或点「立即备份一份」就会产生。";
        }
        else if (string.IsNullOrWhiteSpace(_status.Text))
        {
            _status.Text = $"共 {items.Count} 份备份，最新一份：{items[0].Time:yyyy-MM-dd HH:mm}。";
        }
        if (items.Count > 0) _list.SelectedIndex = 0;
    }

    void RestoreSelected()
    {
        if (_list.SelectedItem is not Store.BackupInfo info)
        {
            _status.Text = "请先在列表里选中一份备份。";
            return;
        }

        var ok = MessageBox.Show(
            $"确定把当前数据恢复成这份备份吗？\n\n" +
            $"备份时间：{info.Time:yyyy-MM-dd HH:mm}\n" +
            $"内容：{info.ClassCount} 个班 · 调代 {info.OverrideCount} 条\n" +
            (string.IsNullOrWhiteSpace(info.Reason) ? "" : $"说明：{info.Reason}\n") +
            "\n将覆盖：基础课表、教师表、调代课、通知、全部设置（含显示班级、布局、密码）。\n" +
            "恢复前会先把当前状态另存一份，操作可逆。",
            "恢复备份", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (ok != MessageBoxResult.OK) return;

        var (success, message) = Store.RestoreBackup(info.Dir);
        _status.Text = message;
        if (success) Restored = true;
        Reload();
    }
}
