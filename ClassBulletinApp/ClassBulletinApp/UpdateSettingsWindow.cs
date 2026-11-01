using System;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 与 WinForms 同名的控件统一指向 WPF 版本
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ClassBulletinApp;

/// <summary>
/// 自动更新设置：数据源（关闭 / 共享文件夹 / FTP）、检查频率、是否发布机。
/// 只读写 <see cref="AppSettings"/>，不做网络动作；"测试连接"才真的去探一次。
/// </summary>
public class UpdateSettingsWindow : Window
{
    readonly ComboBox _kind = new();
    readonly TextBox _folder = new();
    readonly TextBox _ftpUrl = new();
    readonly TextBox _ftpUser = new();
    readonly PasswordBox _ftpPwd = new();
    readonly TextBox _interval = new();
    readonly TextBox _delay = new();
    readonly CheckBox _confirm = new();
    readonly CheckBox _publish = new();
    readonly CheckBox _autoPub = new();
    readonly TextBox _sourceDir = new();
    readonly RadioButton _rbKeep = new();
    readonly RadioButton _rbOverwrite = new();
    readonly StackPanel _policyPanel = new();
    readonly TextBlock _status = new();

    TextBlock _folderLabel = new();
    TextBlock _ftpLabel = new();
    TextBlock _ftpUserLabel = new();
    TextBlock _ftpPwdLabel = new();
    TextBlock _sourceLabel = new();
    TextBlock _autoPubLabel = new();
    TextBlock _policyLabel = new();

    AppSettings _s = new();

    /// <summary>打开窗口时的策略值。判断"是不是刚改成覆盖"用它，而不是用已经会被改写的 _s。</summary>
    bool _openedOverwrite;

    public UpdateSettingsWindow()
    {
        Title = "自动更新设置";
        Width = 760;
        MinHeight = 460;
        // 高度跟着内容走：教室机 / 笔记本的 DPI 与字号不同，写死 Height 容易把底部按钮挤出窗口
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        _s = Store.LoadSettings();

        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true };

        // ---------- 顶部：说明 ----------
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "自动更新（共享文件夹 / FTP）",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        head.Children.Add(new TextBlock
        {
            Text = "程序会定时读取数据源里的 manifest.json，发现整包摘要变化才下载 data.cbpack，"
                 + "校验通过后自动应用。连不上就继续用本地数据，不会影响正常使用。",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        System.Windows.Controls.DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
        root.Children.Add(head);

        // ---------- 底部：按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var btnTest = MakeButton("测试连接", "#EAF2FD", "#9EC1F0");
        var btnSave = MakeButton("保存", "#E8F6EE", "#7FC99A");
        var btnClose = MakeButton("取消", "#F3F4F6", "#D1D5DB");
        btnTest.Click += (_, _) => TestConnection();
        btnSave.Click += (_, _) => SaveAndClose();
        btnClose.Click += (_, _) => Close();
        bar.Children.Add(btnTest);
        bar.Children.Add(btnSave);
        bar.Children.Add(btnClose);
        System.Windows.Controls.DockPanel.SetDock(bar, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 中间：表单 ----------
        var form = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        for (int i = 0; i < 13; i++)
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        void AddRow(string label, UIElement ctrl, TextBlock? reuse = null)
        {
            TextBlock lb;
            if (reuse != null)
            {
                lb = reuse;      // 复用字段引用，后面才能按数据源类型显示/隐藏整行
                lb.Text = label;
            }
            else
            {
                lb = new TextBlock { Text = label };
            }
            lb.FontSize = 13;
            lb.Foreground = System.Windows.Media.Brushes.DimGray;
            lb.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            lb.Margin = new Thickness(0, 5, 8, 5);

            Grid.SetRow(lb, row);
            Grid.SetColumn(lb, 0);
            form.Children.Add(lb);

            Grid.SetRow(ctrl, row);
            Grid.SetColumn(ctrl, 1);
            ctrl.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
            form.Children.Add(ctrl);
            row++;
        }

        _kind.Items.Add(new ComboBoxItem { Content = "关闭（不自动更新）", Tag = "Off" });
        _kind.Items.Add(new ComboBoxItem { Content = "共享文件夹 / U 盘", Tag = "Folder" });
        _kind.Items.Add(new ComboBoxItem { Content = "FTP 服务器", Tag = "Ftp" });
        _kind.SelectionChanged += (_, _) => SyncVisibility();
        AddRow("数据源", _kind);

        _folder.FontSize = 14;
        _folder.Padding = new Thickness(8, 6, 8, 6);
        _folder.ToolTip = @"例如 \\server\课表 或 F:\课表（U 盘）";
        AddRow("共享路径", _folder, _folderLabel);

        _ftpUrl.FontSize = 14;
        _ftpUrl.Padding = new Thickness(8, 6, 8, 6);
        _ftpUrl.ToolTip = "例如 ftp://10.0.0.5/schedule/ （末尾带斜杠）";
        AddRow("FTP 地址", _ftpUrl, _ftpLabel);

        _ftpUser.FontSize = 14;
        _ftpUser.Padding = new Thickness(8, 6, 8, 6);
        AddRow("FTP 账号", _ftpUser, _ftpUserLabel);

        _ftpPwd.FontSize = 14;
        _ftpPwd.Padding = new Thickness(8, 6, 8, 6);
        AddRow("FTP 密码", _ftpPwd, _ftpPwdLabel);

        _interval.FontSize = 14;
        _interval.Padding = new Thickness(8, 6, 8, 6);
        _interval.ToolTip = $"检查数据源有没有新包的间隔，{AppSettings.MinUpdateIntervalMinutes}~{AppSettings.MaxUpdateIntervalMinutes} 分钟。\n"
                          + "默认 30。调小只影响\"本机多久去拉一次\"；30 台机器同时设很小会加重共享盘 / FTP 的登录负担。\n"
                          + "发布机改了 Excel 不需要动这里——source 目录变化会自动打包上传（30 秒级）。";
        AddRow("检查间隔（分钟）", _interval);

        _delay.FontSize = 14;
        _delay.Padding = new Thickness(8, 6, 8, 6);
        _delay.ToolTip = "启动后等多少秒再开始第一次检查。30 台机器同开时可错开。";
        AddRow("启动延迟（秒）", _delay);

        _confirm.Content = "发现新版本时先弹窗问我（不勾选则自动应用）";
        _confirm.FontSize = 13;
        AddRow("应用方式", _confirm);

        _publish.Content = "本机兼任「发布机」（把 source 里的 Excel 打包上传）";
        _publish.FontSize = 13;
        _publish.Checked += (_, _) => SyncVisibility();
        _publish.Unchecked += (_, _) => SyncVisibility();
        AddRow("发布机", _publish);

        _sourceDir.FontSize = 14;
        _sourceDir.Padding = new Thickness(8, 6, 8, 6);
        _sourceDir.ToolTip = "存 Excel 的目录。临时课表文件名要以日期开头，如 2026-10-26_临时课表_八年级.xlsx（写周内任意一天都可，会按所在周对齐）";
        AddRow("发布源目录", _sourceDir, _sourceLabel);

        _autoPub.Content = "source 里的 Excel 一变就自动发布（不用每次手点「发布到数据源」）";
        _autoPub.FontSize = 13;
        _autoPub.ToolTip = "每 30 秒扫一次源目录的文件名 / 大小 / 修改时间；连续两次一致才发布，\n避免 Excel 正在保存时读到半截文件。";
        AddRow("自动发布", _autoPub, _autoPubLabel);

        // ---------- 同步策略：是否连本机手工调课一起覆盖 ----------
        _rbKeep.GroupName = "syncPolicy";
        _rbOverwrite.GroupName = "syncPolicy";
        _rbKeep.Content = "保留本机手工调课（默认）";
        _rbKeep.FontSize = 13;
        _rbOverwrite.Content = "以数据源为准：连本机手工调课一起覆盖";
        _rbOverwrite.FontSize = 13;

        var policyPanel = _policyPanel;
        policyPanel.Children.Add(_rbKeep);
        policyPanel.Children.Add(_rbOverwrite);
        policyPanel.Children.Add(new TextBlock
        {
            Text = "选「覆盖」后，各教室机老师自己拖的调课会在下一次同步时被停用"
                 + "（不删除，可在「数据管理 → 恢复被主机停用的本机调课」里复活）。"
                 + "长期改课（basePatch）任何时候都不受影响。\n"
                 + "只在勾了「发布机」的机器上可改；教室机以数据包里的设置为准。",
            FontSize = 11.5,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 6, 0, 0)
        });
        AddRow("同步策略", policyPanel, _policyLabel);

        _status.Text = "";
        _status.FontSize = 12;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = System.Windows.Media.Brushes.Gray;
        _status.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_status, row);
        Grid.SetColumn(_status, 0);
        Grid.SetColumnSpan(_status, 2);
        form.Children.Add(_status);

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = form
        });

        Content = root;
        Load();
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
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        }
        catch { return System.Windows.Media.Brushes.WhiteSmoke; }
    }

    void Load()
    {
        var kind = (_s.UpdateSourceKind ?? "Off").ToLowerInvariant();
        foreach (ComboBoxItem it in _kind.Items)
        {
            if (string.Equals((string?)it.Tag, kind == "folder" ? "Folder" : kind == "ftp" ? "Ftp" : "Off",
                    StringComparison.OrdinalIgnoreCase))
            {
                _kind.SelectedItem = it;
                break;
            }
        }
        if (_kind.SelectedItem == null) _kind.SelectedIndex = 0;

        _folder.Text = _s.UpdateFolderPath ?? "";
        _ftpUrl.Text = _s.UpdateFtpUrl ?? "";
        _ftpUser.Text = _s.UpdateFtpUser ?? "";
        _ftpPwd.Password = _s.UpdateFtpPassword ?? "";
        _interval.Text = _s.UpdateIntervalMinutes.ToString();
        _delay.Text = _s.UpdateStartupDelaySeconds.ToString();
        _confirm.IsChecked = _s.UpdateConfirmBeforeApply;
        _publish.IsChecked = _s.PublishEnabled;
        _autoPub.IsChecked = _s.AutoPublishOnSourceChange;
        _sourceDir.Text = _s.PublishSourcePath ?? "";

        _openedOverwrite = _s.OverwriteManualOverrides;
        _rbOverwrite.IsChecked = _openedOverwrite;
        _rbKeep.IsChecked = !_openedOverwrite;

        SyncVisibility();
    }

    string SelectedKind()
    {
        return _kind.SelectedItem is ComboBoxItem it ? (string?)it.Tag ?? "Off" : "Off";
    }

    void SyncVisibility()
    {
        bool folder = SelectedKind() == "Folder";
        bool ftp = SelectedKind() == "Ftp";

        // 标签也要一起隐藏，否则会留下两行空标签（行高是 Auto，控件隐藏了标签还占位）
        foreach (var c in new FrameworkElement[] { _folder, _folderLabel }) c.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in new FrameworkElement[] { _ftpUrl, _ftpLabel, _ftpUser, _ftpUserLabel, _ftpPwd, _ftpPwdLabel })
            c.Visibility = ftp ? Visibility.Visible : Visibility.Collapsed;

        bool pub = _publish.IsChecked == true;
        foreach (var c in new FrameworkElement[] { _sourceDir, _sourceLabel, _autoPub, _autoPubLabel, _policyPanel, _policyLabel })
            c.Visibility = pub ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 落盘。返回 false = 用户在策略确认框里点了取消，调用方应中止。
    /// </summary>
    bool Save(bool confirm = true)
    {
        // ★ 首次把策略切到「覆盖」必须二次确认：这一步会让各教室机老师的手工调课失效，
        //   属于"一次点击影响 30 台机器"的操作，不确认太容易误开。
        bool isPublisher = _publish.IsChecked == true;
        bool wantOverwrite = _rbOverwrite.IsChecked == true;

        if (confirm && isPublisher && wantOverwrite && !_openedOverwrite)
        {
            var r = MessageBox.Show(
                "开启后，各教室机上老师自己拖的调课，会在它们下一次同步时被停用。\n\n"
                + "记录不会被删除，仍可在「数据管理 → 恢复被主机停用的本机调课」里复活；"
                + "长期改课（basePatch）不受影响。\n\n"
                + "确定要开启吗？",
                "确认同步策略", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return false;
        }

        _s.UpdateSourceKind = SelectedKind();
        _s.UpdateFolderPath = _folder.Text.Trim();
        _s.UpdateFtpUrl = _ftpUrl.Text.Trim();
        _s.UpdateFtpUser = _ftpUser.Text.Trim();
        _s.UpdateFtpPassword = _ftpPwd.Password;
        // 与运行期定时器共用同一份区间常量：存下去的值就是真实生效的值，不再"设 3 实际按 5 跑"
        _s.UpdateIntervalMinutes = int.TryParse(_interval.Text.Trim(), out var m)
            ? Math.Clamp(m, AppSettings.MinUpdateIntervalMinutes, AppSettings.MaxUpdateIntervalMinutes)
            : 30;
        _s.UpdateStartupDelaySeconds = int.TryParse(_delay.Text.Trim(), out var d) ? Math.Max(0, Math.Min(600, d)) : 30;
        _s.UpdateConfirmBeforeApply = _confirm.IsChecked == true;
        _s.PublishEnabled = isPublisher;
        _s.AutoPublishOnSourceChange = _autoPub.IsChecked == true;
        _s.PublishSourcePath = _sourceDir.Text.Trim();

        // ★ 只有发布机才写策略。教室机这份值仅作显示，实际生效的是包里下发的策略，
        //   若这里也写，就会出现"本机设置"和"包策略"两个真相互相覆盖。
        if (isPublisher) _s.OverwriteManualOverrides = wantOverwrite;

        _s.AutoUpdateEnabled = SelectedKind() != "Off";

        // 发布源目录留空时，自动推出一个默认：数据源目录同级的 source
        if (_s.PublishEnabled && string.IsNullOrWhiteSpace(_s.PublishSourcePath))
        {
            if (SelectedKind() == "Folder" && !string.IsNullOrWhiteSpace(_s.UpdateFolderPath))
                _s.PublishSourcePath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_s.UpdateFolderPath.TrimEnd('\\', '/')) ?? "",
                    "source");
        }

        Store.SaveSettings(_s);
        _openedOverwrite = _s.OverwriteManualOverrides;
        return true;
    }

    void SaveAndClose()
    {
        if (!Save()) return;   // 用户在确认框里点了取消
        DialogResult = true;
        Close();
    }

    void TestConnection()
    {
        Save(confirm: false);     // 先落盘，保证测试用的是当前填写值（测试连接不必弹策略确认）
        var src = FeedSourceFactory.Create(_s);
        if (src == null)
        {
            _status.Text = "还没选数据源，或地址为空。";
            return;
        }

        _status.Text = "正在测试…";
        try
        {
            var (ok, msg) = src.Test();
            _status.Text = (ok ? "✔ " : "✘ ") + msg;

            if (ok)
            {
                var chk = UpdateService.Check(src, _s);
                _status.Text += "\n" + (chk.Ok ? "　" + chk.Message : "　" + chk.Message);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "✘ 测试失败：" + ex.Message;
        }
    }
}
