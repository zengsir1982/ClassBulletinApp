using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Services;

using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;

namespace ClassBulletinApp;

/// <summary>
/// 「关于」窗口：软件简介 + 版本号 + 本机运行信息。
///
/// <para>★ 版本号一律取 <see cref="AppInfo.VersionText"/>（源 = 程序集版本 = csproj 的 Version），
/// 绝不在这里写死字符串 —— 以前"窗口标题显示 1.0.3、安装包是 1.0.4"这种尾巴就是硬编码造成的。</para>
///
/// <para>本窗口<b>只读</b>：不写设置、不碰数据，游客身份也能打开（看版本不涉及权限）。</para>
/// </summary>
public class AboutWindow : Window
{
    /// <summary>点「复制版本信息」时写进剪贴板的文本（反馈问题时直接粘贴）。</summary>
    readonly string _copyText = "";

    public AboutWindow()
    {
        Title = "关于　班级公布栏";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        // ---------- 取值 ----------
        // 统一走 AppSummary：「关于」标签页与本窗口显示同一份数据，改了不会只改一半
        var summ = AppSummary.Build();
        string ver = summ.Version;
        string user = summ.User;
        string role = summ.Role;
        string dataDir = summ.DataDir;
        string lastCheck = summ.LastCheck;
        string classText = summ.ClassText;
        string srcText = summ.SourceText;
        _copyText = summ.CopyText;

        // ---------- 根容器 ----------
        var root = new StackPanel();

        // 标题行：软件名 + 右侧版本徽标
        var titleGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "班级公布栏",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = HexBrush("#1F2A37"),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleGrid.Children.Add(title);

        var badge = new Border
        {
            Background = HexBrush("#EAF1FD"),
            BorderBrush = HexBrush("#9DBDF0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "v" + ver,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = HexBrush("#1E4FA8")
            }
        };
        Grid.SetColumn(badge, 1);
        titleGrid.Children.Add(badge);
        root.Children.Add(titleGrid);

        root.Children.Add(Muted("面向教室多媒体屏的课表与班级信息公布工具", 13, 0, 0, 0, 14));

        // 简介
        root.Children.Add(new TextBlock
        {
            Text = "导入全校总课程表后选定班级，教室屏上就会显示当天的课程、通知公告、作业公示与缺交名单；"
                 + "支持调代课（含按周生效区间）、多账号与角色权限、以及多台教室机之间的数据包自动同步。",
            FontSize = 13.5,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            Foreground = HexBrush("#374151")
        });

        root.Children.Add(Divider(16));

        // ---------- 主要功能 ----------
        root.Children.Add(SectionTitle("主要功能"));
        root.Children.Add(Feature("当日课表", "按班显示当天课表，自动高亮当前这一节，含节次时间与课间"));
        root.Children.Add(Feature("调代课", "拖拽 / 右键手工修改，导入临时课表；可按周设定生效区间，到期自动失效"));
        root.Children.Add(Feature("通知公告", "全校通知随数据包下发到各教室机，本班通知只在本机显示"));
        root.Children.Add(Feature("作业与缺交", "作业公示、缺交名单、自定义便签，均可独立悬浮或画到桌面"));
        root.Children.Add(Feature("桌面背面", "把课表 / 作业 / 缺交名单直接画在桌面壁纸上，位于所有窗口之下"));
        root.Children.Add(Feature("多机同步", "共享文件夹 / U 盘 / FTP 作为数据源，定时检查并自动应用新数据包"));
        root.Children.Add(Feature("多账号", "管理员 / 班主任 / 科任老师 / 电教管理员，各角色能做什么由管理员配置"));

        root.Children.Add(Divider(16));

        // ---------- 运行信息 ----------
        root.Children.Add(SectionTitle("运行信息"));

        var info = new Border
        {
            Background = HexBrush("#F7F9FC"),
            BorderBrush = HexBrush("#E3E8EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var infoGrid = new Grid();
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        AddInfoRow(infoGrid, ref row, "版本", $"{ver}　（64 位）");
        AddInfoRow(infoGrid, ref row, "当前身份", $"{role}　·　{user}");
        AddInfoRow(infoGrid, ref row, "课表规模", classText);
        AddInfoRow(infoGrid, ref row, "数据源", srcText);
        AddInfoRow(infoGrid, ref row, "数据格式", summ.SchemaText);
        AddInfoRow(infoGrid, ref row, "数据目录", dataDir);
        AddInfoRow(infoGrid, ref row, "上次检查更新", lastCheck, isLast: true);

        info.Child = infoGrid;
        root.Children.Add(info);

        // ---------- 按钮行 ----------
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0)
        };

        var btnCopy = new Button
        {
            Content = "复制版本信息",
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13.5,
            Background = HexBrush("#EAF1FD"),
            BorderBrush = HexBrush("#9DBDF0"),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "把版本、身份、数据目录等一起复制到剪贴板，反馈问题时直接粘贴"
        };
        btnCopy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_copyText);
                btnCopy.Content = "已复制 ✓";
            }
            catch { btnCopy.Content = "复制失败"; }
        };
        buttons.Children.Add(btnCopy);

        var btnDir = new Button
        {
            Content = "打开数据目录",
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13.5,
            Background = HexBrush("#EAF1FD"),
            BorderBrush = HexBrush("#9DBDF0"),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "备份 / 排查时在这个文件夹里找 settings、schedule、users.json"
        };
        btnDir.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dataDir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "打不开目录：" + ex.Message, "打开数据目录", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        buttons.Children.Add(btnDir);

        var btnClose = new Button
        {
            Content = "关闭",
            Padding = new Thickness(22, 8, 22, 8),
            FontSize = 13.5,
            Background = HexBrush("#2D6CDF"),
            Foreground = Brushes.White,
            BorderBrush = HexBrush("#2D6CDF"),
            IsDefault = true
        };
        btnClose.Click += (_, _) => Close();
        buttons.Children.Add(btnClose);

        root.Children.Add(buttons);

        // 页脚
        root.Children.Add(new TextBlock
        {
            Text = "© 2026　逆流的鱼　·　仅供校内使用",
            FontSize = 12,
            Foreground = HexBrush("#9CA3AF"),
            Margin = new Thickness(0, 16, 0, 0)
        });

        Content = new Border { Padding = new Thickness(26, 22, 26, 20), Child = root };
    }

    // ---------- 小工具 ----------

    // System.Drawing 也定义了 Color / ColorConverter，必须全限定（本项目同时启用 WinForms）
    static SolidColorBrush HexBrush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>数据源类型的中文名由 <see cref="AppSummary.KindText"/> 统一提供（此处不再重复实现）。</summary>

    static TextBlock Muted(string text, double size, double top, double right, double bottom, double left)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = HexBrush("#6B7280"),
            Margin = new Thickness(left, top, right, bottom)
        };

    static TextBlock SectionTitle(string text)
        => new()
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = HexBrush("#1F2A37"),
            Margin = new Thickness(0, 0, 0, 8)
        };

    static Border Divider(double top)
        => new()
        {
            Height = 1,
            Background = HexBrush("#E3E8EF"),
            Margin = new Thickness(0, top, 0, top - 2)
        };

    /// <summary>一条「标题 —— 说明」，标题列定宽，说明自动换行。</summary>
    static Grid Feature(string name, string desc)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        g.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = HexBrush("#2D6CDF"),
            VerticalAlignment = VerticalAlignment.Top
        });

        var d = new TextBlock
        {
            Text = desc,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Foreground = HexBrush("#4B5563")
        };
        Grid.SetColumn(d, 1);
        g.Children.Add(d);
        return g;
    }

    /// <summary>运行信息里的一行：左侧灰标签、右侧值（长值自动换行）。</summary>
    void AddInfoRow(Grid g, ref int row, string label, string value, bool isLast = false)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lb = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = HexBrush("#6B7280"),
            Margin = new Thickness(0, 3, 0, isLast ? 3 : 9)
        };
        Grid.SetRow(lb, row);
        Grid.SetColumn(lb, 0);
        g.Children.Add(lb);

        var vl = new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            Foreground = HexBrush("#1F2A37"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, isLast ? 3 : 9)
        };
        Grid.SetRow(vl, row);
        Grid.SetColumn(vl, 1);
        g.Children.Add(vl);

        row++;
    }
}
