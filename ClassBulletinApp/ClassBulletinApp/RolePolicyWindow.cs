using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

using CheckBox = System.Windows.Controls.CheckBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ClassBulletinApp;

/// <summary>
/// 角色权限配置（仅管理员可打开）。
///
/// <para>三行角色 × 五项能力。管理员行<b>恒全权且不可改</b>；「发布」与「管理用户」
/// 两列<b>刻意不出现</b>，因为它们一旦下放，整套权限会自己失效（详见 <see cref="RolePolicy"/> 注释）。</para>
///
/// <para>★ 配置存在 <c>users.json</c> 里，<b>随数据包下发</b>，所以只有"发布机"上的修改才有意义。
/// 其他机器打开只读，否则会出现"在这台改松了、下次同步又被覆盖，而且没人查得出来"。</para>
/// </summary>
public class RolePolicyWindow : Window
{
    /// <summary>一项可配置能力：界面文字 + 说明 + 读写 <see cref="RolePolicy"/> 的存取器。</summary>
    sealed class Capability
    {
        public string Name = "";
        public string Hint = "";
        public Func<RolePolicy, bool> Get = _ => false;
        public Action<RolePolicy, bool> Set = (_, _) => { };
    }

    static readonly Capability[] Caps =
    {
        new()
        {
            Name = "编辑课表",
            Hint = "调课 / 代课 / 拖拽，范围限本班",
            Get = p => p.EditSchedule,
            Set = (p, v) => p.EditSchedule = v
        },
        new()
        {
            Name = "作业与缺交名单",
            Hint = "编辑本班作业公示、缺交登记",
            Get = p => p.EditBoard,
            Set = (p, v) => p.EditBoard = v
        },
        new()
        {
            Name = "恢复被停用的调课",
            Hint = "主机策略覆盖掉的本班记录可自己恢复",
            Get = p => p.RestoreSuspended,
            Set = (p, v) => p.RestoreSuspended = v
        },
        new()
        {
            Name = "导入临时课表",
            Hint = "每周调代课的常规操作，影响面小",
            Get = p => p.ImportTemp,
            Set = (p, v) => p.ImportTemp = v
        },
        new()
        {
            Name = "总课表 / 清除 / 备份",
            Hint = "导入全校总课表、清除调课、备份恢复、导入导出数据包",
            Get = p => p.ManageData,
            Set = (p, v) => p.ManageData = v
        }
    };

    /// <summary>「总课表 / 清除 / 备份」在数组里的下标 —— 高危项判断与保存前提醒都用它。</summary>
    const int CapManageData = 4;

    /// <summary>可配置的角色（管理员不在其中）。</summary>
    static readonly string[] Roles = { UserRole.Teacher, UserRole.Student };

    static readonly double[] ColWidths = { 330, 90, 100, 120 };

    readonly UserBook _book;
    readonly bool _canEdit;
    readonly CheckBox[,] _boxes = new CheckBox[Roles.Length, Caps.Length];

    /// <summary>保存后为 true，调用方据此决定是否重刷界面。</summary>
    public bool Changed { get; private set; }

    public RolePolicyWindow(UserBook book)
    {
        _book = book;

        // 只有发布机的修改能被带出去：权限存在 users.json 里、随数据包下发，
        // 在教室机上改了，下一次同步就被发布机的版本覆盖回去。
        var set = Store.LoadSettings();
        _canEdit = set.PublishEnabled;

        Title = "角色权限";
        Width = 800;
        // 高度随内容自适应：标题+说明 / 可选提示条 / 表头 / 五行 / 按钮条，互不挤压。
        // 只读时多一条黄色提示条，窗口自动加高，不会截断底部；非只读时也不会空一大片。
        SizeToContent = SizeToContent.Height;
        MinWidth = 720;
        MinHeight = 420;
        MaxHeight = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true };

        // ---------- 顶部说明 ----------
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "角色权限",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var intro = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Text = "勾选各角色能做的事。权限存在 users.json 里，随数据包下发到各教室机；"
                 + "管理员恒为全部权限，不在此配置。\n"
                 + "★ 「发布」与「管理用户」两项刻意不开放：前者一次点击影响 30 台机器且无法回滚，"
                 + "后者等于允许对方把自己改成管理员，整套权限会当场失效。"
        };
        head.Children.Add(intro);

        // 非发布机：明确告诉他为什么不能改，以及要去哪里切换
        if (!_canEdit)
        {
            var warn = new Border
            {
                Background = HexBrush("#FFF7E6"),
                BorderBrush = HexBrush("#F0C36D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    FontSize = 12.5,
                    Foreground = HexBrush("#8A5A00"),
                    TextWrapping = TextWrapping.Wrap,
                    Text = "这台机器不是发布机，权限配置在这里是只读的。"
                         + "要修改，请到「数据管理 → 自动更新设置」勾上「这台是发布机」后再打开本窗口（"
                         + "权限会随数据包下发，教室机上改了会被下次同步覆盖回去）。"
                }
            };
            head.Children.Add(warn);
        }

        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---------- 底部按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };

        Button Mk(string text, string? bg = null)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(13, 7, 13, 7),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0)
            };
            if (bg != null) b.Background = HexBrush(bg);
            return b;
        }

        var btnReset = Mk("恢复出厂默认", "#F1F1F1");
        btnReset.IsEnabled = _canEdit;
        btnReset.Click += (_, _) => ResetDefaults();

        var btnSave = Mk("保存", "#E8F6EE");
        btnSave.IsEnabled = _canEdit;
        btnSave.Click += (_, _) => { if (SaveAll()) { DialogResult = true; Close(); } };

        var btnClose = Mk("关闭");
        btnClose.Click += (_, _) => Close();

        foreach (var b in new[] { btnReset, btnSave, btnClose })
            bar.Children.Add(b);

        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 表格 ----------
        root.Children.Add(BuildTable());

        Content = root;
    }

    Grid BuildTable()
    {
        var g = new Grid();
        for (int i = 0; i < ColWidths.Length; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidths[i]) });

        var rows = Caps.Length + 1;
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });   // 表头
        for (int i = 0; i < Caps.Length; i++)
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

        // 表头
        string[] header = { "能力", "管理员", "班主任", "电教管理员" };
        for (int c = 0; c < header.Length; c++)
        {
            var t = new TextBlock
            {
                Text = header[c],
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(c == 0 ? 2 : 0, 0, 0, 6)
            };
            if (c > 0) t.TextAlignment = TextAlignment.Center;
            else t.Margin = new Thickness(2, 0, 0, 6);
            Grid.SetRow(t, 0);
            Grid.SetColumn(t, c);
            g.Children.Add(t);
        }

        for (int r = 0; r < Caps.Length; r++)
        {
            int row = r + 1;

            // 能力名 + 说明
            var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            cell.Children.Add(new TextBlock { Text = Caps[r].Name, FontSize = 13.5 });
            cell.Children.Add(new TextBlock
            {
                Text = Caps[r].Hint,
                FontSize = 11.5,
                Foreground = Brushes.Gray,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, 0);
            g.Children.Add(cell);

            // 管理员列：恒勾、恒灰，表示"这一格不归你配"
            var adminBox = new CheckBox
            {
                IsChecked = true,
                IsEnabled = false,
                Opacity = 0.55,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "管理员恒为全部权限，不可配置（否则可以把自己锁死，且没人能救）。"
            };
            Grid.SetRow(adminBox, row);
            Grid.SetColumn(adminBox, 1);
            g.Children.Add(adminBox);

            // 两个可配角色
            for (int i = 0; i < Roles.Length; i++)
            {
                var box = new CheckBox
                {
                    IsChecked = Caps[r].Get(_book.PolicyFor(Roles[i])),
                    IsEnabled = _canEdit,
                    Opacity = _canEdit ? 1.0 : 0.55,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (!_canEdit) box.ToolTip = "本机不是发布机，只读。";
                _boxes[i, r] = box;
                Grid.SetRow(box, row);
                Grid.SetColumn(box, i + 2);
                g.Children.Add(box);
            }

            // 分隔线
            var line = new Border
            {
                BorderBrush = HexBrush("#EAEAEA"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetRow(line, row);
            Grid.SetColumn(line, 0);
            Grid.SetColumnSpan(line, ColWidths.Length);
            g.Children.Add(line);
        }

        return g;
    }

    void ResetDefaults()
    {
        if (MessageBox.Show(this,
                "把「班主任」「电教管理员」两个角色恢复为出厂默认？\n\n"
                + "班主任：改课表、作业/缺交、恢复停用调课。\n"
                + "电教管理员：只能控制本机显示。",
                "恢复默认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        for (int i = 0; i < Roles.Length; i++)
        {
            var def = RolePolicy.DefaultFor(Roles[i]);
            for (int r = 0; r < Caps.Length; r++)
                _boxes[i, r].IsChecked = Caps[r].Get(def);
        }
    }

    bool SaveAll()
    {
        // 先留一份旧值，用于写变更日志（权限配错要能倒查是谁在哪次改的）
        var before = new Dictionary<string, RolePolicy>();
        foreach (var role in Roles) before[role] = _book.PolicyFor(role).Clone();

        var changes = new List<string>();

        for (int i = 0; i < Roles.Length; i++)
        {
            var p = _book.PolicyFor(Roles[i]).Clone();
            bool roleChanged = false;
            for (int r = 0; r < Caps.Length; r++)
            {
                bool now = _boxes[i, r].IsChecked == true;
                bool old = Caps[r].Get(before[Roles[i]]);
                if (now != old)
                {
                    Caps[r].Set(p, now);
                    changes.Add($"{UserRole.Display(Roles[i])}/{Caps[r].Name} {(old ? "开" : "关")}→{(now ? "开" : "关")}");
                    roleChanged = true;
                }
            }
            if (roleChanged) _book.Policies[Roles[i]] = p;
        }

        if (changes.Count == 0) return true;   // 没改动，不算错

        // 高危项提醒：这两个角色是教室机的开机身份，开了等于"一开机就有这个权力"
        bool risky = false;
        for (int i = 0; i < Roles.Length; i++)
            if (_boxes[i, CapManageData].IsChecked == true) risky = true;

        if (risky)
        {
            if (MessageBox.Show(this,
                    "你给「总课表 / 清除 / 备份」开了权限。\n\n"
                    + "注意：电教管理员是教室机开机自动登录的身份。开了这一项，"
                    + "那台机器开机就具备导入全校课表、清除调课的能力，学生乱点也可能清掉数据。\n\n"
                    + "确定保存吗？",
                    "高危权限", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return false;
        }

        try
        {
            _book.EnsurePolicyDefaults();
            Store.SaveUsers(_book);
            AppendChangeLog(UserSession.Current?.Username ?? "未知", string.Join("；", changes));

            // 当前登录者如果是被改的那个角色，权限要立刻生效，不能等重启。
            // 诊断/截图模式下不重绑：那里的假身份不在 user 表里，重绑会把身份直接清掉。
            if (!Store.SuppressWrites) UserSession.Rebind(_book);
            Changed = true;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "角色权限",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// 把这次改动追加到 <c>policy.log</c>。
    /// <para>权限配错的后果往往几周后才显形，届时没人记得是哪次同步改的 —— 留一行就够倒查。</para>
    /// </summary>
    static void AppendChangeLog(string who, string detail)
    {
        if (Store.SuppressWrites) return;
        try
        {
            string path = Path.Combine(Store.AppDir, "policy.log");
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{who}\t{detail}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { /* 日志写不进去不能挡住保存 */ }
    }

    static Brush HexBrush(string hex)
    {
        try
        {
            // 全限定：System.Drawing 也有 Color / ColorConverter，全局 using 下会歧义
            return new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        }
        catch { return Brushes.WhiteSmoke; }
    }
}
