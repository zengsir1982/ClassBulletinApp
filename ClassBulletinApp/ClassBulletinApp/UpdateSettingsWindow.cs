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

    /// <summary>非管理员打开时的权限提示（默认折叠，按身份显示）。</summary>
    ClassBulletinApp.Controls.HelpBubble _roleNote = new();

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
        // r77：问号并入标题行末（非管理员时该行才多出一个「权限说明」）
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = "自动更新（共享文件夹 / FTP）",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        // 非管理员（但有自动更新设置权限）打开时的提示：能改本机数据源，但不能动"发布机"身份
        _roleNote.PopupTitle = "权限说明";
        _roleNote.Text = "当前身份不是管理员：可以修改本机的共享文件夹 / FTP / 检查频率等更新来源，"
                 + "但「发布机」身份及其专属项（源目录、自动发布、同步策略）为只读，只有管理员能开关。"
                 + "这样既能防止改了共享文件夹就收不到更新，又不会让教室机误变成发布机往数据源写数据。";
        _roleNote.Visibility = Visibility.Collapsed;
        titleRow.Children.Add(_roleNote);
        head.Children.Add(titleRow);

        System.Windows.Controls.DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
        root.Children.Add(head);

        // ---------- 底部：按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        // 向导入口放在最左：它的作用就是"把这张表里跟共享/FTP 有关的几行一次填对"，
        // 用户遇到「发布源目录不存在」时第一反应应该是点它，而不是在这里逐格猜。
        var btnWizard = MakeButton("共享访问向导…", ChromePalette.HexOf(ChromePalette.PrimaryLight, "#E9EFF9"), ChromePalette.HexOf(ChromePalette.PrimaryBorder, "#C2D2E9"));
        btnWizard.ToolTip = "三步走：① 办公电脑开共享并建只读账号　② 本机保存访问凭据并实测　③ 配置 FTP 数据通道。\n"
                          + "做完会自动回填下面的「发布源目录」，并把数据源设为 FTP。";
        var btnTest = MakeButton("测试连接", ChromePalette.HexOf(ChromePalette.PrimaryLight, "#E9EFF9"), ChromePalette.HexOf(ChromePalette.PrimaryBorder, "#C2D2E9"));
        var btnSave = MakeButton("保存", "#E8F6EE", "#7FC99A");
        var btnClose = MakeButton("取消", "#F3F4F6", "#C7CFDA");
        btnWizard.Click += (_, _) =>
        {
            new ShareAccessWizardWindow { Owner = this }.ShowDialog();
            // 向导会直接写 settings.json：这里必须重新读盘 + 重填，否则用户看到的是旧值，
            // 接着点「保存」就把向导刚写好的东西又盖回去了。
            _s = Store.LoadSettings();
            Load();
        };
        btnTest.Click += (_, _) => TestConnection();
        btnSave.Click += (_, _) => SaveAndClose();
        btnClose.Click += (_, _) => Close();
        bar.Children.Add(btnWizard);
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
        _sourceDir.ToolTip = "存 Excel 的目录。临时课表文件名要以日期开头，如 2026-10-26_临时课表_八年级.xlsx（写周内任意一天都可，会按所在周对齐）\n"
                           + "⚠ 这里要填【文件夹路径】（如 \\\\192.168.40.168\\dfzx\\source 或 D:\\dfzx\\source），\n"
                           + "不要填 FTP 地址 —— FTP 上没有 Excel，Excel 永远留在共享目录里。\n"
                           + "不确定怎么填就点下面「共享访问向导…」，它会自动算好并填进来。";
        // 加「浏览…」：这里填的是 UNC 路径，手敲错一个字符就报"发布源目录不存在"；
        // 而从资源管理器「复制路径」粘过来的串还带双引号（2026-11-02 现场排查过这类）。
        var srcPanel = new Grid();
        srcPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        srcPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_sourceDir, 0);
        srcPanel.Children.Add(_sourceDir);

        var btnBrowse = MakeButton("浏览…", "#F3F4F6", "#C7CFDA");
        btnBrowse.Padding = new Thickness(12, 6, 12, 6);
        btnBrowse.Margin = new Thickness(8, 0, 0, 0);
        btnBrowse.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        btnBrowse.ToolTip = "选一个文件夹（也可以是共享目录）。\n要选网络位置时，直接在对话框的地址栏输入 \\\\主机\\共享\\source 回车即可。";
        btnBrowse.Click += (_, _) => BrowseSourceDir();
        Grid.SetColumn(btnBrowse, 1);
        srcPanel.Children.Add(btnBrowse);

        AddRow("发布源目录", srcPanel, _sourceLabel);

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
        // r77：问号并入「保留本机手工调课」那一行的行末，不再独占一行
        var keepRow = new StackPanel { Orientation = Orientation.Horizontal };
        keepRow.Children.Add(_rbKeep);
        keepRow.Children.Add(new ClassBulletinApp.Controls.HelpBubble
        {
            PopupTitle = "注意事项",
            Text = "选「覆盖」后，各教室机老师自己拖的调课会在下一次同步时被停用"
                 + "（不删除，可在「数据管理 → 恢复被主机停用的本机调课」里复活）。"
                 + "长期改课（basePatch）任何时候都不受影响。\n"
                 + "只在勾了「发布机」的机器上可改；教室机以数据包里的设置为准。",
            Margin = new Thickness(4, 0, 0, 0)
        });
        policyPanel.Children.Add(keepRow);
        policyPanel.Children.Add(_rbOverwrite);
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
    
        GlassChrome.Prepare(this);}

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
        ApplyRoleRestrictions();
    }

    /// <summary>
    /// 非管理员（但拥有自动更新设置权限）打开时，禁用「发布机」身份及其专属项。
    /// <para>★ 需求三：所有角色都能改本机更新来源（共享文件夹 / FTP），否则教室机改了路径就收不到更新；
    /// 但"发布机"一旦在教室机上被打开，那台机器也会往数据源写数据、影响 30 台机器，所以恒只给管理员。</para>
    /// </summary>
    void ApplyRoleRestrictions()
    {
        if (UserSession.IsAdmin) return;

        _roleNote.Visibility = Visibility.Visible;

        _publish.IsEnabled = false;
        _publish.ToolTip = "只有管理员可以开关「发布机」身份。";

        // 发布机专属项一并禁用，防止切换发布机时误改这些
        _sourceDir.IsEnabled = false;
        _autoPub.IsEnabled = false;
        _rbKeep.IsEnabled = false;
        _rbOverwrite.IsEnabled = false;
    }

    /// <summary>选一个文件夹填进「发布源目录」。</summary>
    void BrowseSourceDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择存课表 Excel 的文件夹（可以是共享目录）",
            Multiselect = false
        };

        // 粘过来的路径常带双引号（资源管理器「复制路径」的默认行为），先剥掉再判存在
        var cur = _sourceDir.Text.Trim().Trim('"');
        if (cur.Length > 0 && System.IO.Directory.Exists(cur)) dlg.InitialDirectory = cur;

        if (dlg.ShowDialog(this) != true) return;
        _sourceDir.Text = dlg.FolderName;
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
        // 剥掉双引号：从资源管理器「复制路径」粘过来的串默认带引号，
        // 不剥的话 Directory.Exists 判 false，报的却是"发布源目录不存在"，很难联想到引号上。
        _s.PublishSourcePath = _sourceDir.Text.Trim().Trim('"');

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
