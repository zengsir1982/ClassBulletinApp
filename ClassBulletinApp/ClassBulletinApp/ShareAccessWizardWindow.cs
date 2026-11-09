using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using PasswordBox = System.Windows.Controls.PasswordBox;
using FontFamily = System.Windows.Media.FontFamily;
using TextBox = System.Windows.Controls.TextBox;

namespace ClassBulletinApp;

/// <summary>
/// 「共享访问设置向导」：把"办公电脑开共享 + 3 台发布机存凭据 + 数据通道怎么配"三件事
/// 收进一个三步向导，避免用户照着文档在不同机器之间来回试。
///
/// <para>★ 设计前提（本项目实际部署）：</para>
/// <list type="bullet">
/// <item>课表 Excel 放在<b>办公电脑</b>的共享名下（<c>dfzx</c>），三台发布机的源目录都指向
/// <c>\\办公电脑\dfzx\source</c> —— 谁点发布都扫同一份目录，合成的是完整包，不会互相覆盖。</item>
/// <item>办公电脑<b>不装 FTP</b>，数据通道继续用<b>学校现成的 FTP 服务器</b>。所以本向导
/// 第 3 步默认就是"用学校 FTP"，自建 FTP 只是给以后留的口子。</item>
/// <item>三台发布机用<b>不同的 Windows 账号</b>。凭据按 Windows 用户隔离，所以第 2 步
/// 必须每台各做一次 —— 这是本向导最想提醒、也最容易被忽略的一点。</item>
/// </list>
///
/// <para>★ 教室机不需要走这个向导：它们只读 FTP，不碰共享目录。第 1、2 步在教室机上做没有意义。</para>
///
/// <para>★ 密码不落 settings.json：第 2 步的密码只写进 Windows 凭据管理器（<see cref="SmbCredential"/>），
/// 写完就丢；第 3 步的 FTP 密码沿用原有 settings 字段（这是历史设计，量级不同：FTP 是只给程序用的通道账号）。</para>
/// </summary>
public class ShareAccessWizardWindow : Window
{
    readonly AppSettings _s;

    // ---------- 第 1 步：办公电脑 ----------
    readonly TextBox _hostBox = new();
    readonly TextBox _shareBox = new();
    readonly TextBox _subBox = new();

    /// <summary>命令块的实时刷新器：改了主机名/共享名，下面几条命令要跟着变。</summary>
    readonly List<Action> _cmdUpdaters = new();

    // ---------- 第 2 步：本机凭据 ----------
    readonly TextBox _smbUser = new();
    readonly PasswordBox _smbPwd = new();
    readonly TextBlock _uncText = new();
    readonly TextBlock _credState = new();
    readonly TextBlock _testText = new();
    readonly Border _testBox = new();
    readonly TextBlock _mapState = new();
    bool _credTested;

    // ---------- 第 3 步：数据通道 ----------
    readonly RadioButton _rbSchool = new();
    readonly RadioButton _rbOffice = new();
    readonly TextBox _ftpUrl = new();
    readonly TextBox _ftpUser = new();
    readonly PasswordBox _ftpPwd = new();
    readonly TextBlock _ftpNote = new();

    // ---------- 步骤框架 ----------
    readonly ContentControl _body = new();
    readonly List<FrameworkElement> _pages = new();
    readonly List<Border> _badges = new();
    readonly List<TextBlock> _badgeTexts = new();
    int _step;

    Button _btnPrev = new();
    Button _btnNext = new();
    Button _btnDone = new();

    /// <summary>打开时停在第几页（0/1/2）。诊断截图用；对象初始化器在构造之后才赋值，所以走 Loaded。</summary>
    public int InitialStep { get; set; }

    public ShareAccessWizardWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Width = 820;
        // 高度跟着屏幕走：办公电脑常见 1366x768，写死 680 会上下都紧；1080 屏则给足 720。
        // 高度跟着屏幕走：办公电脑常见 1366x768，写死 680 会上下都紧；1080 屏则给足 720。
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 40);
        MinWidth = 720;
        MinHeight = 520;

        _s = Store.LoadSettings();
        Title = "共享访问设置向导";

        // ---------- 顶部 ----------
        var head = new StackPanel();

        var titleGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.Children.Add(new TextBlock
        {
            Text = "共享访问设置向导",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#1F2A37"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var tag = new Border
        {
            Background = Hex(ChromePalette.HexOf(ChromePalette.PrimaryLight, "#EAF1FD")),
            BorderBrush = Hex(ChromePalette.HexOf(ChromePalette.PrimaryBorder, "#9DBDF0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "仅发布机需要",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = Hex(ChromePalette.HexOf(ChromePalette.PrimaryDark, "#1E4FA8"))
            }
        };
        Grid.SetColumn(tag, 1);
        titleGrid.Children.Add(tag);
        head.Children.Add(titleGrid);


        head.Children.Add(BuildStepBar());
        head.Children.Add(new Border
        {
            Height = 1,
            Background = Hex("#E3E8EF"),
            Margin = new Thickness(0, 12, 0, 0)
        });

        // ---------- 底部按钮 ----------
        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };

        _btnPrev = MakeButton("← 上一步", "#F3F4F6", "#D1D5DB");
        _btnNext = MakeButton("下一步 →", ChromePalette.HexOf(ChromePalette.Primary, "#2D6CDF"), ChromePalette.HexOf(ChromePalette.Primary, "#2D6CDF"), white: true);
        _btnDone = MakeButton("完成并保存", "#E8F6EE", "#7FC99A");
        var btnCancel = MakeButton("取消", "#F3F4F6", "#D1D5DB");

        _btnPrev.Click += (_, _) => GoTo(_step - 1);
        _btnNext.Click += (_, _) => GoTo(_step + 1);
        _btnDone.Click += (_, _) => Finish();
        btnCancel.Click += (_, _) => Close();

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        right.Children.Add(_btnPrev);
        right.Children.Add(_btnDone);
        right.Children.Add(_btnNext);
        right.Children.Add(btnCancel);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);


        // ---------- 组装 ----------
        _pages.Add(BuildStep1());
        _pages.Add(BuildStep2());
        _pages.Add(BuildStep3());
        _body.Content = _pages[0];

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 16), LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(bar);
        root.Children.Add(_body);
        Content = root;

        Load();
        GoTo(0);
        Loaded += (_, _) => GoTo(Math.Max(0, Math.Min(_pages.Count - 1, InitialStep)));
    
        GlassChrome.Prepare(this);}

    // ================= 框架 =================

    Border BuildStepBar()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };

        string[] names = { "办公电脑准备", "本机凭据", "数据通道" };
        for (int i = 0; i < names.Length; i++)
        {
            var badge = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var txt = new TextBlock
            {
                Text = names[i],
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0)
            };

            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 26, 0) };
            item.Children.Add(badge);
            item.Children.Add(txt);
            p.Children.Add(item);

            _badges.Add(badge);
            _badgeTexts.Add(txt);
        }
        return new Border { Child = p };
    }

    void GoTo(int step)
    {
        _step = Math.Max(0, Math.Min(_pages.Count - 1, step));
        _body.Content = _pages[_step];

        for (int i = 0; i < _badges.Count; i++)
        {
            bool on = i == _step;
            bool past = i < _step;
            _badges[i].Background = on ? Hex(ChromePalette.HexOf(ChromePalette.Primary, "#2D6CDF")) : past ? Hex("#E8F6EE") : Hex("#F3F4F6");
            _badges[i].BorderBrush = on ? Hex(ChromePalette.HexOf(ChromePalette.Primary, "#2D6CDF")) : past ? Hex("#7FC99A") : Hex("#D1D5DB");
            _badges[i].BorderThickness = new Thickness(1);
            if (_badges[i].Child is TextBlock n)
                n.Foreground = on ? Brushes.White : past ? Hex("#2F7A4E") : Hex("#9CA3AF");

            _badgeTexts[i].Foreground = on ? Hex("#1F2A37") : past ? Hex("#2F7A4E") : Hex("#9CA3AF");
            _badgeTexts[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        _btnPrev.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _btnNext.Visibility = _step == _pages.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        _btnDone.Visibility = _step == _pages.Count - 1 ? Visibility.Visible : Visibility.Collapsed;

        if (_step == 1) RefreshCredState();
        if (_step == 2) SyncFtpNote();
    }

    // ================= 第 1 步：办公电脑 =================

    FrameworkElement BuildStep1()
    {
        var p = new StackPanel();


        p.Children.Add(Field("办公电脑地址", _hostBox,
            "办公电脑的 IP 或计算机名，例如 192.168.40.168 或 ZLOVEK。不要带 \\\\，也不要带路径。"));
        p.Children.Add(Field("共享名", _shareBox, "默认 dfzx（就是办公电脑上那个共享文件夹的名字）。"));
        p.Children.Add(Field("课表子目录", _subBox,
            "默认 source。如果 Excel 就直接放在共享根目录里，这里留空。"));


        p.Children.Add(SectionTitle("① 建一个专用账号（不要用管理员账号，也不要用你自己的账号）"));
        p.Children.Add(CmdBlock(
            "在办公电脑上用【管理员】打开命令提示符，粘贴执行（把密码换成你自己的）：",
            () => "net user " + SmbUserText() + " \"换成你的密码\" /add /comment:\"课程表共享账号\"\r\n"
                + "wmic useraccount where name='" + SmbUserText() + "' set PasswordExpires=false"));

        p.Children.Add(SectionTitle("② 建共享，给这个账号「更改」权限"));
        p.Children.Add(CmdBlock(
            "第一行是新建共享；如果 dfzx 已经共享过，只执行带 rem 注释的那行：",
            () => "net share " + ShareText() + "=\"D:\\dfzx\" /grant:" + SmbUserText() + ",CHANGE\r\n"
                + "rem 已有共享时，只执行下面这行：\r\n"
                + "net share " + ShareText() + " /grant:" + SmbUserText() + ",CHANGE"));

        p.Children.Add(SectionTitle("③ NTFS 权限也要给「修改」（这步漏了就会出现「能连上但进不去」）"));
        p.Children.Add(CmdBlock(
            "共享权限和 NTFS 权限是两套，取交集，两边都要给：",
            () => "icacls \"D:\\dfzx\" /grant \"" + SmbUserText() + ":(OI)(CI)M\" /T"));


        p.Children.Add(SectionTitle("④ 自测一遍：读要通，写也要通"));
        p.Children.Add(CmdBlock(
            "在【发布机】上执行最准（那才是实际走网络的路径）。能列出文件、能写入并删掉测试文件，就说明这一侧好了：",
            () =>
            {
                var host = HostText().Length > 0 ? HostText() : "<办公电脑IP>";
                var share = ShareText();
                var unc = UncText();
                return "net use \\\\" + host + "\\" + share + " /user:" + SmbUserText() + " 你的密码\r\n"
                    + "dir " + unc + "\r\n"
                    + "echo ok > " + unc + "\\_写入测试.txt\r\n"
                    + "del " + unc + "\\_写入测试.txt\r\n"
                    + "net use \\\\" + host + "\\" + share + " /delete";
            }));



        return WrapScroll(p);
    }

    // ================= 第 2 步：本机凭据 =================

    FrameworkElement BuildStep2()
    {
        var p = new StackPanel();


        var uncRow = new Border
        {
            Background = Hex("#F7F9FC"),
            BorderBrush = Hex("#E3E8EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var uncStack = new StackPanel();
        uncStack.Children.Add(new TextBlock
        {
            Text = "发布源目录（会自动填进「发布源目录」设置）",
            FontSize = 11.5,
            Foreground = Hex("#6B7280")
        });
        _uncText.FontFamily = new FontFamily("Consolas");
        _uncText.FontSize = 13.5;
        _uncText.Foreground = Hex("#1F2A37");
        _uncText.TextWrapping = TextWrapping.Wrap;
        _uncText.Margin = new Thickness(0, 3, 0, 0);
        uncStack.Children.Add(_uncText);
        uncRow.Child = uncStack;
        p.Children.Add(uncRow);

        p.Children.Add(Field("共享账号", _smbUser, "第 1 步在办公电脑上建的那个账号，默认 cbpublish。"));
        p.Children.Add(Field("密码", _smbPwd, "只在点击下面按钮时用一次，不会保存到本程序的配置文件里。"));

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        var btnTest = MakeButton("测试并保存凭据", ChromePalette.HexOf(ChromePalette.PrimaryLight, "#EAF2FD"), ChromePalette.HexOf(ChromePalette.PrimaryBorder, "#9EC1F0"));
        btnTest.Click += async (_, _) => await TestAndSave();
        var btnDel = MakeButton("删除本机凭据", "#F3F4F6", "#D1D5DB");
        btnDel.Click += (_, _) => DeleteCredential();
        btnRow.Children.Add(btnTest);
        btnRow.Children.Add(btnDel);
        p.Children.Add(btnRow);

        // 凭据存好之后的两个常用动作：打开目录（放课表 / 核对文件）、映射盘符（拖文件方便）。
        var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var btnOpen = MakeButton("打开共享目录", "#E8F6EE", "#7FC99A");
        btnOpen.Click += (_, _) => OpenSharedFolder();
        var btnMap = MakeButton("映射为网络驱动器", "#E8F6EE", "#7FC99A");
        btnMap.Click += (_, _) => MapSharedFolder();
        btnRow2.Children.Add(btnOpen);
        btnRow2.Children.Add(btnMap);
        p.Children.Add(btnRow2);

        _mapState.FontSize = 12;
        _mapState.TextWrapping = TextWrapping.Wrap;
        _mapState.LineHeight = 18;
        _mapState.Margin = new Thickness(0, 0, 0, 10);
        p.Children.Add(_mapState);

        _credState.FontSize = 12;
        _credState.Foreground = Hex("#6B7280");
        _credState.TextWrapping = TextWrapping.Wrap;
        _credState.Margin = new Thickness(0, 0, 0, 10);
        p.Children.Add(_credState);

        _testBox.Background = Hex("#F7F9FC");
        _testBox.BorderBrush = Hex("#E3E8EF");
        _testBox.BorderThickness = new Thickness(1);
        _testBox.CornerRadius = new CornerRadius(6);
        _testBox.Padding = new Thickness(12, 9, 12, 9);
        _testBox.Visibility = Visibility.Collapsed;
        _testText.FontSize = 12.5;
        _testText.TextWrapping = TextWrapping.Wrap;
        _testText.LineHeight = 19;
        _testBox.Child = _testText;
        p.Children.Add(_testBox);


        return WrapScroll(p);
    }

    /// <summary>用资源管理器打开共享目录 —— 放课表、核对文件、看有没有副本，都在这里做。</summary>
    void OpenSharedFolder()
    {
        var unc = UncText();
        if (unc.Length == 0 || unc.Contains('<'))
        {
            ShowTest(false, "还没填办公电脑地址，回到第 1 步填好再打开。");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + unc + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowTest(false, "打不开目录：" + ex.Message);
        }
    }

    /// <summary>
    /// 把共享目录映射成盘符（自动挑一个空的）。
    /// <para>★ 映射出来的盘符只给"人拖文件"用。程序里的发布源目录必须写 UNC：
    /// 盘符只在交互登录会话里存在，开机早期 / 计划任务看不到，填成盘符就会变成"源目录不存在"。</para>
    /// </summary>
    void MapSharedFolder()
    {
        var host = HostText();
        var unc = UncText();
        if (host.Length == 0 || host.Contains('<')) { ShowTest(false, "还没填办公电脑地址。"); return; }

        if (SmbCredential.ReadUser(host) == null)
        {
            ShowTest(false, "本机还没有保存凭据。先点「测试并保存凭据」，成功之后再映射。");
            return;
        }

        var (ok, msg, drive) = SmbCredential.MapDrive(unc);
        _mapState.Foreground = Hex(ok ? "#2F7A4E" : "#9B1C1C");
        _mapState.Text = ok
            ? $"已映射为 {drive}（{unc}），在「此电脑」里就能看到，重启后自动恢复。\n"
              + $"★ 这个盘符只用于手动拖课表。程序里的「发布源目录」仍然写 {unc}，不要填成 {drive}"
            : "映射失败：" + msg + "\n（提示盘符被占用就往回看：先在资源管理器里断开那个旧映射再试。）";
    }

    async Task TestAndSave()
    {
        var host = HostText();
        var unc = UncText();
        var user = _smbUser.Text.Trim();
        var pwd = _smbPwd.Password;

        if (host.Length == 0 || host.StartsWith("<"))
        {
            ShowTest(false, "还没填办公电脑地址。请回到第 1 步填写 IP 或计算机名。");
            return;
        }
        if (user.Length == 0) { ShowTest(false, "共享账号不能为空。"); return; }
        if (pwd.Length == 0) { ShowTest(false, "请填写密码。"); return; }

        try
        {
            // ★ 先断开到这台电脑的旧连接，再写凭据。
            //   凭据管理器里的条目换了，**已建立的会话仍挂在旧账号上**，不断开的话下面 Probe
            //   测的是旧身份，会出现"新账号明明是错的，测试却一路通过"（2026-11-02 实测踩到）。
            SmbCredential.DisconnectHost(host);

            SmbCredential.Write(host, user, pwd);
        }
        catch (Win32Exception ex)
        {
            ShowTest(false, "保存凭据失败：" + SmbCredential.Explain(ex.NativeErrorCode));
            return;
        }
        catch (Exception ex)
        {
            ShowTest(false, "保存凭据失败：" + ex.Message);
            return;
        }

        _credTested = true;
        _mapState.Text = "";    // 换了账号，之前那次映射的结果不再作数
        ShowTest(true, "凭据已保存，并已断开该电脑的旧连接。正在测试能否读到共享目录…（网络不通时最多等 20 秒）");

        // 访问不可达的 UNC 路径时 Directory.Exists 会卡十几秒，必须放到后台线程 + 超时，
        // 否则整个窗口假死，用户以为程序挂了。
        var task = Task.Run(() => SmbCredential.Probe(unc));
        var finished = await Task.WhenAny(task, Task.Delay(20000));
        if (finished != task)
        {
            ShowTest(false,
                "20 秒内没有响应：网络不通，或办公电脑的 445 端口被防火墙拦了。\n"
                + "凭据已经存好，网络通了就能直接用，不用重做这一步。");
            return;
        }

        var (ok, msg, _, canWrite) = await task;
        if (!ok)
        {
            ShowTest(false, msg + "\n\n" + HintForProbeFailure(unc));
        }
        else if (!canWrite)
        {
            // 读通了、写不进：这是最容易漏的状态 —— 发布能正常跑，人却放不进课表。
            ShowTest(false, msg + "\n\n读这一侧没问题，但往这个目录里拖课表会被拒绝。"
                + "要放文件：共享权限给「更改」、NTFS 给「修改」（两层都要，缺一层都写不进去）。");
        }
        else
        {
            ShowTest(true, msg + "\n这台机器既能读课表，也能往共享目录里放课表。");
        }
        RefreshCredState();
    }

    /// <summary>探路失败时给一条"下一步该查什么"，比只报错误有用。</summary>
    string HintForProbeFailure(string unc)
    {
        var share = SmbCredential.ShareOf(unc);
        return "按顺序排查：\n"
             + $"1) 到办公电脑上执行 net share，确认共享名确实是「{share}」、后面的路径确实存在；\n"
             + "2) 确认共享权限和 NTFS 权限（属性 → 安全）都给了这个账号「读取」；\n"
             + "3) 如果之前用别的账号连过这台机器，先执行 net use \\\\" + HostText() + " /delete；\n"
             + "4) 换一台机器 ping 一下这个 IP，确认 445 端口没被防火墙拦。";
    }

    void DeleteCredential()
    {
        var host = HostText();
        if (host.Length == 0 || host.StartsWith("<")) return;

        SmbCredential.Delete(host);
        _credTested = false;
        _testBox.Visibility = Visibility.Collapsed;
        RefreshCredState();
    }

    void RefreshCredState()
    {
        var host = HostText();
        _credState.Text = "";
        if (host.Length == 0 || host.StartsWith("<"))
        {
            _credState.Text = "还没有填写办公电脑地址。";
            return;
        }

        var saved = SmbCredential.ReadUser(host);
        if (saved == null)
        {
            _credState.Text = $"本机（当前 Windows 账号）还没有为 {host} 保存过凭据。";
            _credState.Foreground = Hex("#6B7280");
        }
        else
        {
            _credState.Text = $"本机已为 {host} 保存凭据，账号：{saved}。密码在系统里，程序读不到也不需要读。";
            _credState.Foreground = Hex("#2F7A4E");
        }
        _uncText.Text = UncText();
    }

    // ================= 第 3 步：数据通道 =================

    FrameworkElement BuildStep3()
    {
        var p = new StackPanel();


        _rbSchool.GroupName = "ftpMode";
        _rbOffice.GroupName = "ftpMode";
        _rbSchool.Content = "用学校现成的 FTP（当前做法，推荐）";
        _rbOffice.Content = "以后改由办公电脑自己提供 FTP";
        _rbSchool.FontSize = 13.5;
        _rbOffice.FontSize = 13.5;
        _rbSchool.Margin = new Thickness(0, 4, 0, 2);
        _rbOffice.Margin = new Thickness(0, 8, 0, 2);
        _rbSchool.Checked += (_, _) => SyncFtpNote();
        _rbOffice.Checked += (_, _) => SyncFtpNote();
        p.Children.Add(_rbSchool);

        p.Children.Add(_rbOffice);

        // 说明块必须有自己的底色：它紧跟在第二个单选项后面，不区分的话会被读成"自建 FTP 的说明"，
        // 而它实际是随上面选择变化的那一段。
        var noteWrap = new Border
        {
            Background = Hex("#F7F9FC"),
            BorderBrush = Hex("#E3E8EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(20, 4, 0, 12),
            Child = _ftpNote
        };
        _ftpNote.Margin = new Thickness(0);
        _ftpNote.FontSize = 11.5;
        _ftpNote.Foreground = Hex("#4B5563");
        _ftpNote.TextWrapping = TextWrapping.Wrap;
        _ftpNote.LineHeight = 18;
        p.Children.Add(noteWrap);

        p.Children.Add(Divider(4));

        p.Children.Add(Field("FTP 地址", _ftpUrl,
            "末尾带斜杠。学校 FTP 是 Linux 的话照现状填，例如 ftp://192.168.40.168/home/dfzx/schedule/"));
        p.Children.Add(Field("FTP 账号", _ftpUser, "程序只需要在这两个文件上有读写权限：manifest.json、data.cbpack。"));
        p.Children.Add(Field("FTP 密码", _ftpPwd, "教室机也用这个账号读包，所以要填能用的公开通道账号。"));


        return WrapScroll(p);
    }

    void SyncFtpNote()
    {
        if (_rbOffice.IsChecked == true)
        {
            _ftpNote.Text =
                "自建 FTP 的步骤（办公电脑上做，管理员）：\n"
                + "1) 服务器管理器 → 添加角色和功能 → 勾「Web 服务器(IIS)」→ 在角色服务里勾上「FTP 服务器」；\n"
                + "2) IIS 管理器 → 右键网站 → 添加 FTP 站点 → 物理路径指到专门放数据包的目录（不要指到课表目录）；\n"
                + "3) 身份验证选「基本」，授权第 1 步那个账号「读取 + 写入」；\n"
                + "4) 防火墙放行 21 端口，以及「FTP 防火墙支持」里设的被动端口段（如 50000-50100）；\n"
                + "5) 地址填 ftp://办公电脑IP/ 即可。\n"
                + "注意：这条路要求办公电脑长期开机，否则教室机拉不到新包 —— 这也是目前继续用学校 FTP 的原因。";
            _ftpUrl.ToolTip = "例如 ftp://192.168.40.168/";
        }
        else
        {
            _ftpNote.Text =
                "学校 FTP 的目录分工：课表 Excel 不要放这里（那是共享目录的事），"
                + "FTP 上只放程序生成的两个文件。建议单独开一个目录，例如 /home/dfzx/schedule/。";
            _ftpUrl.ToolTip = "例如 ftp://192.168.40.168/home/dfzx/schedule/";
        }
    }

    // ================= 完成 =================

    void Finish()
    {
        var host = HostText();
        if (host.Length == 0 || host.StartsWith("<"))
        {
            MessageBox.Show(this, "还没填办公电脑地址，请回到第 1 步填写。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            GoTo(0);
            return;
        }

        var ftpUrl = _ftpUrl.Text.Trim();
        if (ftpUrl.Length == 0)
        {
            MessageBox.Show(this, "FTP 地址是空的。教室机靠它拉数据包，不能留空。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            GoTo(2);
            return;
        }
        ftpUrl = NormFtp(ftpUrl);

        if (!_credTested)
        {
            var r = MessageBox.Show(this,
                "第 2 步的「测试并保存凭据」还没有成功过。\n\n"
                + "如果直接保存，发布时很可能报「发布源目录不存在」—— 那通常就是这台机器还读不到共享目录。\n\n"
                + "仍然保存吗？",
                "共享连接还没验证", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) { GoTo(1); return; }
        }

        var unc = UncText();
        _s.UpdateSourceKind = "Ftp";
        _s.UpdateFtpUrl = ftpUrl;
        _s.UpdateFtpUser = _ftpUser.Text.Trim();
        _s.UpdateFtpPassword = _ftpPwd.Password;
        _s.AutoUpdateEnabled = true;
        _s.PublishSourcePath = unc;

        // 「发布机」身份只有管理员能开：教室机误开就会往数据源写数据，影响全部 30 台。
        bool canPublish = UserSession.IsAdmin;
        if (canPublish) _s.PublishEnabled = true;

        Store.SaveSettings(_s);

        MessageBox.Show(this,
            "已保存。\n\n"
            + "发布源目录：" + unc + "\n"
            + "数据通道：" + ftpUrl + "\n"
            + (canPublish
                ? "本机已标记为「发布机」。\n\n"
                : "当前身份不是管理员，「发布机」开关没有打开 —— 请让管理员打开它，否则这台机器不会发布。\n\n")
            + "接下来可以去「自动更新设置」点一次「测试连接」，再点「发布到数据源」验证整条链路。",
            "共享访问设置完成", MessageBoxButton.OK, MessageBoxImage.Information);

        DialogResult = true;
        Close();
    }

    // ================= 取值 =================

    /// <summary>清洗主机：去掉 \\ 前缀和路径尾巴。用户经常连路径一起粘进来。</summary>
    string HostText()
    {
        var s = _hostBox.Text.Trim();
        if (s.Length == 0) return "";
        if (s.StartsWith("<")) return s;      // 命令块里的占位符
        var t = SmbCredential.TargetOf(s);
        return t.Length > 0 ? t : s.Trim('\\', '/');
    }

    string ShareText()
    {
        var s = _shareBox.Text.Trim().Trim('\\', '/');
        return s.Length > 0 ? s : "dfzx";
    }

    string SubText() => _subBox.Text.Trim().Trim('\\', '/');

    /// <summary>
    /// FTP 地址规范化：补协议、补末尾斜杠。
    /// <para>程序拼 "地址 + 文件名" 是纯字符串拼接，少一个斜杠就会拼成 <c>.../dfzxmanifest.json</c> ——
    /// 这类错只能在连接测试时才暴露，所以入口处统一补好。</para>
    /// </summary>
    static string NormFtp(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (!s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
            s = "ftp://" + s.TrimStart('/');
        if (!s.EndsWith("/", StringComparison.Ordinal)) s += "/";
        return s;
    }

    string SmbUserText()
    {
        var s = _smbUser.Text.Trim();
        return s.Length > 0 ? s : "cbpublish";
    }

    /// <summary>完整的 UNC 路径：\\主机\共享[\子目录]。</summary>
    string UncText()
    {
        var host = HostText();
        if (host.Length == 0) host = "<办公电脑IP>";
        var unc = @"\\" + host + @"\" + ShareText();
        var sub = SubText();
        if (sub.Length > 0) unc += @"\" + sub;
        return unc;
    }

    /// <summary>
    /// 从现有配置里猜「办公电脑的共享路径」，用于预填。
    /// <para>★ 顺序：<b>发布源目录优先</b>。本项目的实际部署里，办公电脑就是 ZENGSIR，
    /// <c>PublishSourcePath</c>（<c>\\ZENGSIR\dfzx\source</c>）已经是发布机真实在用的源目录，
    /// 照它预填最准；<c>UpdateFolderPath</c> 反而可能是换机/改名前的旧值（曾出现过 ZLOVEK）。</para>
    /// <para>兜底：解析出的主机如果就是本机计算机名，说明这是"本机目录"而不是共享，跳过。
    /// （发布机自己开共享时二者会相同，但那时用户本来就该自己填，不宜替他猜。）</para>
    /// </summary>
    string ResolveExistingSource()
    {
        var me = Environment.MachineName ?? "";
        foreach (var cand in new[] { _s.PublishSourcePath, _s.UpdateFolderPath })
        {
            var v = (cand ?? "").Trim();
            if (!v.StartsWith(@"\\", StringComparison.Ordinal)) continue;   // ftp:// 或本地盘符都不算
            var h = SmbCredential.TargetOf(v);
            if (h.Length == 0) continue;
            if (string.Equals(h, me, StringComparison.OrdinalIgnoreCase)) continue;
            return v;
        }
        return "";
    }

    void Load()
    {
        // 已有配置能反解出主机/共享名就预填，省得用户再查一遍。
        var src = ResolveExistingSource();
        string host = "", share = "dfzx", sub = "source";
        if (src.StartsWith(@"\\", StringComparison.Ordinal))
        {
            host = SmbCredential.TargetOf(src);
            var sh = SmbCredential.ShareOf(src);
            if (sh.Length > 0) share = sh;

            var rest = src.Length > 2 + host.Length ? src.Substring(2 + host.Length).TrimStart('\\') : "";
            var afterShare = rest.Length > share.Length ? rest.Substring(share.Length).TrimStart('\\') : "";
            if (afterShare.Length > 0) sub = afterShare;
        }

        _hostBox.Text = host;
        _shareBox.Text = share;
        _subBox.Text = sub;
        _smbUser.Text = "cbpublish";
        _uncText.Text = UncText();

        _ftpUrl.Text = NormFtp(_s.UpdateFtpUrl);
        _ftpUser.Text = _s.UpdateFtpUser ?? "";
        _ftpPwd.Password = _s.UpdateFtpPassword ?? "";
        _rbSchool.IsChecked = true;
        SyncFtpNote();

        _hostBox.TextChanged += (_, _) => RefreshCommands();
        _shareBox.TextChanged += (_, _) => RefreshCommands();
        _subBox.TextChanged += (_, _) => RefreshCommands();
        RefreshCommands();
    }

    void RefreshCommands()
    {
        _uncText.Text = UncText();
        foreach (var f in _cmdUpdaters) f();
    }

    // ================= 小工具 =================

    static SolidColorBrush Hex(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    static Button MakeButton(string text, string bg, string border, bool white = false)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13.5,
            Background = Hex(bg),
            BorderBrush = Hex(border),
            Foreground = white ? Brushes.White : Hex("#1F2A37")
        };
    }

    static TextBlock SectionTitle(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Hex("#1F2A37"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 6)
        };

    static Border Divider(double top)
        => new()
        {
            Height = 1,
            Background = Hex("#E3E8EF"),
            Margin = new Thickness(0, top, 0, top + 8)
        };


    static Grid Field(string label, System.Windows.Controls.Control input, string tip)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lb = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Hex("#4B5563"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(lb, 0);
        g.Children.Add(lb);

        input.FontSize = 14;
        input.Padding = new Thickness(8, 6, 8, 6);
        input.ToolTip = tip;
        input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 1);
        g.Children.Add(input);

        return g;
    }

    /// <summary>命令块：等宽只读文本 + 一键复制。内容由 <paramref name="text"/> 提供，会随主机名变化刷新。</summary>
    Border CmdBlock(string caption, Func<string> text)
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            // ★ AcceptsReturn 必须为 true：不加的话 TextBox 会被当成单行控件，
            //   多行命令只能看到一行半（多出来的靠内部滚动），用户会以为命令被截断了。
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Background = Hex("#F7F9FC"),
            Foreground = Hex("#1F2A37"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = text()
        };
        _cmdUpdaters.Add(() =>
        {
            var t = text();
            if (box.Text != t) box.Text = t;
        });

        var copy = new Button
        {
            Content = "复制",
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 11.5,
            Background = Hex(ChromePalette.HexOf(ChromePalette.PrimaryLight, "#EAF1FD")),
            BorderBrush = Hex(ChromePalette.HexOf(ChromePalette.PrimaryBorder, "#9DBDF0")),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(box.Text);
                copy.Content = "已复制";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); copy.Content = "复制"; };
                timer.Start();
            }
            catch { copy.Content = "复制失败"; }
        };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5), LastChildFill = true };
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(copy);
        head.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 11.5,
            Foreground = Hex("#6B7280"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var stack = new StackPanel();
        stack.Children.Add(head);

        var codeWrap = new Border
        {
            Background = Hex("#F7F9FC"),
            BorderBrush = Hex("#E3E8EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = box
        };
        stack.Children.Add(codeWrap);

        return new Border { Margin = new Thickness(0, 0, 0, 14), Child = stack };
    }

    static FrameworkElement WrapScroll(UIElement content)
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 12, 6, 0),
            Content = content
        };
    }

    /// <summary>把测试结果写到结果框里（成功绿、失败红）。</summary>
    void ShowTest(bool ok, string text)
    {
        _testBox.Visibility = Visibility.Visible;
        _testBox.Background = Hex(ok ? "#F0FAF4" : "#FEF2F2");
        _testBox.BorderBrush = Hex(ok ? "#9FD9B4" : "#F5B5B5");
        _testText.Foreground = Hex(ok ? "#2F7A4E" : "#9B1C1C");
        _testText.Text = (ok ? "✔ " : "✘ ") + text;
    }
}
