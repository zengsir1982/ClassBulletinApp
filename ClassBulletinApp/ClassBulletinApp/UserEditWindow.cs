using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 与 WinForms 同名的控件统一指向 WPF 版本
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = System.Windows.Controls.TextBox;

namespace ClassBulletinApp;

/// <summary>
/// 新增 / 修改一个账号。
/// 只负责收集表单，不做任何落盘；由 <see cref="UserManagerWindow"/> 统一保存。
/// </summary>
public class UserEditWindow : Window
{
    readonly AppUser? _src;
    readonly UserBook _book;

    readonly TextBox _user = new();
    readonly TextBox _display = new();
    readonly ComboBox _role = new();
    readonly TextBox _classes = new();
    readonly TextBox _heads = new();
    readonly TextBlock _headHint = new();
    readonly TextBox _subject = new();
    readonly TextBlock _subjectHint = new();
    readonly PasswordBox _pwd = new();
    readonly TextBlock _msg = new();

    /// <summary>确定后的账号（新建或原对象的副本）。取消时为 null。</summary>
    public AppUser? Result { get; private set; }

    public UserEditWindow(AppUser? existing, UserBook book, IEnumerable<string> allClasses)
    {
        _src = existing;
        _book = book;

        Title = existing == null ? "新增账号" : "修改账号";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(18) };

        TextBlock Label(string t) => new()
        {
            Text = t,
            FontSize = 13,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 8, 0, 4)
        };

        root.Children.Add(new TextBlock
        {
            Text = existing == null ? "新增账号" : "修改账号",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        root.Children.Add(Label("用户名（登录名，不能重复）"));
        _user.FontSize = 14;
        _user.Padding = new Thickness(8, 6, 8, 6);
        _user.Text = existing?.Username ?? "";
        _user.IsEnabled = existing == null;   // 改名会让"谁改的"这类历史记录对不上，一期先不允许
        root.Children.Add(_user);

        root.Children.Add(Label("显示名（如「八(22)班 班主任 张三」，可留空）"));
        _display.FontSize = 14;
        _display.Padding = new Thickness(8, 6, 8, 6);
        _display.Text = existing?.DisplayName ?? "";
        root.Children.Add(_display);

        root.Children.Add(Label("角色"));
        _role.FontSize = 14;
        _role.Items.Add(new ComboBoxItem { Content = "管理员（可改全部数据、可发布、可管用户）", Tag = UserRole.Admin });
        _role.Items.Add(new ComboBoxItem { Content = "班主任（改本班课表 / 作业，可收发数据包）", Tag = UserRole.Teacher });
        _role.Items.Add(new ComboBoxItem { Content = "科任老师（只能改所教班级的作业 / 缺交）", Tag = UserRole.SubjectTeacher });
        _role.Items.Add(new ComboBoxItem { Content = "电教管理员（管本机显示，可改本班课表）", Tag = UserRole.Student });
        _role.SelectedIndex = existing == null ? 3
            : existing.Role == UserRole.Admin ? 0
            : existing.Role == UserRole.Teacher ? 1
            : existing.Role == UserRole.SubjectTeacher ? 2 : 3;
        root.Children.Add(_role);

        root.Children.Add(Label("所属班级（班主任 / 科任老师必填；多个班用逗号分隔）"));
        _classes.FontSize = 14;
        _classes.Padding = new Thickness(8, 6, 8, 6);
        _classes.Text = string.Join("，", existing?.ClassNames ?? new List<string>());
        root.Children.Add(_classes);

        var cls = allClasses.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Take(12).ToList();
        if (cls.Count > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "可选：" + string.Join("、", cls) + (allClasses.Count() > 12 ? " …" : ""),
                FontSize = 11.5,
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        root.Children.Add(Label("班主任班级（决定能改哪个班的课表；留空 = 与所属班级相同）"));
        _heads.FontSize = 14;
        _heads.Padding = new Thickness(8, 6, 8, 6);
        _heads.Text = string.Join("，", existing?.HeadClassNames ?? new List<string>());
        _heads.ToolTip = "只有这里填的班才能改课表。\n"
                       + "班主任通常还兼教别的班：那些班填在「所属班级」里，能发作业 / 缺交，但改不了课表。\n"
                       + "留空 = 按「所属班级」处理（老账号升级前后行为不变）。";
        root.Children.Add(_heads);

        _headHint.FontSize = 11.5;
        _headHint.Foreground = System.Windows.Media.Brushes.Gray;
        _headHint.TextWrapping = TextWrapping.Wrap;
        _headHint.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_headHint);

        root.Children.Add(Label("任课科目（只填一个，如「语文」；留空 = 不限科目）"));
        _subject.FontSize = 14;
        _subject.Padding = new Thickness(8, 6, 8, 6);
        // 老账号为空时标灰提示，且绝不擅自填一个科目：升级不该让任何人突然少权限
        _subject.Text = existing?.Subject ?? "";
        _subject.ToolTip = "留空表示不限科目（老账号保持为空即可）；只对科任老师生效。";
        root.Children.Add(_subject);

        _subjectHint.FontSize = 11.5;
        _subjectHint.Foreground = System.Windows.Media.Brushes.Gray;
        _subjectHint.TextWrapping = TextWrapping.Wrap;
        _subjectHint.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_subjectHint);
        _role.SelectionChanged += (_, _) => { UpdateSubjectHint(); UpdateHeadState(); };
        _subject.TextChanged += (_, _) => UpdateSubjectHint();
        _heads.TextChanged += (_, _) => UpdateHeadHint();
        UpdateSubjectHint();
        UpdateHeadState();

        root.Children.Add(Label(existing == null ? "密码" : "新密码（留空表示不修改）"));
        _pwd.FontSize = 14;
        _pwd.Padding = new Thickness(8, 6, 8, 6);
        _pwd.PasswordChar = '*';
        root.Children.Add(_pwd);

        _msg.FontSize = 12.5;
        _msg.Foreground = System.Windows.Media.Brushes.Firebrick;
        _msg.TextWrapping = TextWrapping.Wrap;
        _msg.Margin = new Thickness(0, 10, 0, 0);
        _msg.Visibility = Visibility.Collapsed;
        root.Children.Add(_msg);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "确定", Padding = new Thickness(18, 8, 18, 8), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 8, 18, 8), FontSize = 14 };
        cancel.Click += (_, _) => Close();
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);

        Content = root;
        Loaded += (_, _) => { if (existing == null) _user.Focus(); else _pwd.Focus(); };
    
        GlassChrome.Prepare(this);}

    /// <summary>按当前角色提示任课科目的作用，避免管理员以为填了就能限制班主任。</summary>
    void UpdateSubjectHint()
    {
        string role = (_role.SelectedItem as ComboBoxItem)?.Tag as string ?? UserRole.Student;
        if (role == UserRole.SubjectTeacher)
        {
            _subjectHint.Foreground = System.Windows.Media.Brushes.DarkOrange;
            _subjectHint.Text = string.IsNullOrWhiteSpace(_subject.Text)
                ? "★ 当前是科任老师且未填科目：暂按「不限科目」处理（老账号兼容）。建议填上，将来教师机按科限权才生效。"
                : "★ 科任老师：将来教师机发变更单时，只能修改本科目条目；班主任 / 管理员不受此限制。";
        }
        else
        {
            _subjectHint.Foreground = System.Windows.Media.Brushes.Gray;
            _subjectHint.Text = "只对「科任老师」生效；班主任 / 管理员 / 电教管理员不按科目限权。";
        }
    }

    /// <summary>按当前角色启用 / 清空「班主任班级」，并刷新提示。</summary>
    void UpdateHeadState()
    {
        string role = (_role.SelectedItem as ComboBoxItem)?.Tag as string ?? UserRole.Student;
        bool isTeacher = role == UserRole.Teacher;

        _heads.IsEnabled = isTeacher;
        // 非班主任留着这一项没有意义，还会在下次切回班主任时冒出个意外值
        if (!isTeacher) _heads.Text = "";

        UpdateHeadHint();
    }

    void UpdateHeadHint()
    {
        string role = (_role.SelectedItem as ComboBoxItem)?.Tag as string ?? UserRole.Student;
        if (role != UserRole.Teacher)
        {
            _headHint.Foreground = System.Windows.Media.Brushes.Gray;
            _headHint.Text = "只有「班主任」看这一项；其他角色能不能改课表由角色权限决定，与这里无关。";
            return;
        }

        bool empty = string.IsNullOrWhiteSpace(_heads.Text);
        _headHint.Foreground = empty
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Gray;
        _headHint.Text = empty
            ? "★ 留空时改课范围 = 所属班级（老账号兼容）。建议填上，避免兼教的班也能改课。"
            : "只能改这里列的班的课表；任教的其他班可以发作业 / 缺交，但改不了课表。";
    }

    void Confirm()
    {
        string user = (_user.Text ?? "").Trim();
        if (user.Length == 0) { Err("用户名不能为空。"); return; }

        if (_src == null && _book.Contains(user)) { Err("这个用户名已经存在。"); return; }

        string role = (_role.SelectedItem as ComboBoxItem)?.Tag as string ?? UserRole.Student;

        var classes = (_classes.Text ?? "")
            .Split(new[] { ',', '，', '、', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        var heads = (_heads.Text ?? "")
            .Split(new[] { ',', '，', '、', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        if (role != UserRole.Teacher) heads.Clear();

        // 班主任班级必须是所属班级的子集：否则"改课范围"会跑出"作业范围"之外，
        // 出现"能改某个班课表、却连那个班的作业都发不了"的怪状态。
        if (heads.Count > 0)
        {
            var outside = heads.Where(h => !classes.Contains(h, StringComparer.Ordinal)).ToList();
            if (outside.Count > 0)
            {
                Err($"班主任班级必须在「所属班级」里：{string.Join("、", outside)} 不在所属班级中。");
                return;
            }
        }

        // 班主任 / 科任老师的权限范围恒为"所属班级"，没填班级等于什么都不能改
        if ((role == UserRole.Teacher || role == UserRole.SubjectTeacher) && classes.Count == 0)
        {
            Err(role == UserRole.Teacher
                ? "班主任必须填所属班级，否则他一个班也改不了。"
                : "科任老师必须填任教班级，否则他一个班的作业也改不了。");
            return;
        }

        string pwd = _pwd.Password ?? "";
        if (_src == null && pwd.Length == 0) { Err("新增账号必须设置密码。"); return; }

        // 不允许把最后一个管理员降级或停用（否则再也进不去管理界面）
        if (_src != null && _src.Role == UserRole.Admin && role != UserRole.Admin)
        {
            int others = _book.Users.Count(u => u.Enabled && u.Role == UserRole.Admin
                                                && !string.Equals(u.Username, _src.Username, StringComparison.OrdinalIgnoreCase));
            if (others == 0) { Err("这是最后一个管理员，不能改成其他角色。"); return; }
        }

        var target = _src ?? new AppUser { CreatedAt = DateTime.Now };
        target.Username = _src?.Username ?? user;
        target.DisplayName = (_display.Text ?? "").Trim();
        target.Role = role;
        target.ClassNames = classes;
        target.HeadClassNames = heads;
        // 单选、自由文本、归一化（去全角/半角空白）；留空 = 不限科目
        target.Subject = BoardIds.NormalizeSubject(_subject.Text);
        target.Enabled = true;
        if (pwd.Length > 0) PasswordHasher.SetPassword(target, pwd);

        Result = target;
        DialogResult = true;
        Close();
    }

    void Err(string msg)
    {
        _msg.Text = msg;
        _msg.Visibility = Visibility.Visible;
    }
}
