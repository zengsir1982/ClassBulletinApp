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
        _role.Items.Add(new ComboBoxItem { Content = "班主任（只能改自己班的课表）", Tag = UserRole.Teacher });
        _role.Items.Add(new ComboBoxItem { Content = "电教管理员 / 学生（只能控制本机显示）", Tag = UserRole.Student });
        _role.SelectedIndex = existing == null ? 2
            : existing.Role == UserRole.Admin ? 0
            : existing.Role == UserRole.Teacher ? 1 : 2;
        root.Children.Add(_role);

        root.Children.Add(Label("所属班级（班主任必填；多个班用逗号分隔）"));
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

        if (role == UserRole.Teacher && classes.Count == 0)
        {
            Err("班主任必须填所属班级，否则他一个班也改不了。");
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
