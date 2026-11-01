using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

using ListBox = System.Windows.Controls.ListBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ClassBulletinApp;

/// <summary>
/// 用户管理（仅管理员可用）。
///
/// <para>改完点「保存并关闭」写进本机 <c>users.json</c>；发布数据包时它会一起下发到各教室机
/// （只带用户名 / 哈希 / 盐 / 角色 / 班级，<b>绝不含明文密码</b>）。</para>
///
/// <para>★ 界面上能做的限制，抵不过直接改 json 文件 —— 这是离线单机程序的固有边界，
/// 权限在这里的作用是"不给入口、减少误操作"，不是安全防护。</para>
/// </summary>
public class UserManagerWindow : Window
{
    readonly ListBox _list = new();
    readonly TextBlock _hint = new();
    readonly UserBook _book;

    public UserManagerWindow()
    {
        Title = "用户管理";
        Width = 820;
        Height = 560;
        MinWidth = 660;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _book = Store.LoadUsers();

        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

        // ---------- 顶部 ----------
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "用户管理",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _hint.FontSize = 12.5;
        _hint.Foreground = System.Windows.Media.Brushes.DimGray;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 10);
        head.Children.Add(_hint);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---------- 底部按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        Button Mk(string text, string? bg = null)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(12, 7, 12, 7),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0)
            };
            if (bg != null) b.Background = HexBrush(bg);
            return b;
        }

        var btnAdd = Mk("新增账号", "#EAF2FD");
        btnAdd.Click += (_, _) => AddUser();

        var btnEdit = Mk("修改 / 重置密码");
        btnEdit.Click += (_, _) => EditUser();

        var btnToggle = Mk("启用 / 停用");
        btnToggle.Click += (_, _) => ToggleEnabled();

        var btnDel = Mk("删除", "#FDE8E8");
        btnDel.Click += (_, _) => DeleteUser();

        var btnImport = Mk("从 Excel / CSV 导入名单", "#E8F6EE");
        btnImport.Click += (_, _) => ImportUsers();

        var btnSave = Mk("保存并关闭", "#E8F6EE");
        btnSave.Click += (_, _) => { SaveAll(); DialogResult = true; Close(); };

        var btnClose = Mk("取消");
        btnClose.Click += (_, _) => Close();

        foreach (var b in new[] { btnAdd, btnEdit, btnToggle, btnDel, btnImport, btnSave, btnClose })
            bar.Children.Add(b);

        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 列表 ----------
        // 表头 + 列对齐：单行字符串拼起来看着是"管理员 管理员"这种重复，也看不出哪列是什么，
        // 用 Grid 按固定列宽排（列表项宽度一致，所以和表头天然对齐）。
        var head2 = MakeRow(
            new[] { "用户名", "显示名", "角色", "班级", "状态" },
            ColWidths, bold: true, dim: true);
        head2.Margin = new Thickness(6, 6, 6, 4);
        DockPanel.SetDock(head2, Dock.Top);
        root.Children.Add(head2);

        _list.FontSize = 13;
        _list.Margin = new Thickness(0, 0, 0, 0);
        _list.BorderThickness = new Thickness(0, 1, 0, 0);
        _list.Padding = new Thickness(0, 4, 0, 0);
        _list.MouseDoubleClick += (_, _) => EditUser();
        root.Children.Add(_list);

        Content = root;
        Reload();
    }

    /// <summary>列表列宽（表头与数据行共用，改一处两边一起变）。合计留出余量，避免出现横向滚动条。</summary>
    static readonly double[] ColWidths = { 150, 160, 110, 210, 70 };

    static Grid MakeRow(IReadOnlyList<string> cells, double[] widths, bool bold = false, bool dim = false)
    {
        var g = new Grid();
        for (int i = 0; i < widths.Length; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });

        for (int i = 0; i < widths.Length && i < cells.Count; i++)
        {
            var t = new TextBlock
            {
                Text = cells[i] ?? "",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = bold ? 12.5 : 13
            };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (dim) t.Foreground = System.Windows.Media.Brushes.Gray;
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
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

    void Reload()
    {
        _list.Items.Clear();
        foreach (var u in _book.Users
                     .OrderBy(u => u.Role == UserRole.Admin ? 0 : u.Role == UserRole.Teacher ? 1 : 2)
                     .ThenBy(u => u.Username, StringComparer.Ordinal))
        {
            // 显示名留空、或者就是角色名 / 用户名本身的，一律显示"—"：
            // 迁移出来的老账号 DisplayName 被写成了"管理员"，和旁边的角色列重复，
            // 看着像"管理员　管理员"。数据不动，只在显示层去重。
            string shown = u.DisplayName?.Trim() ?? "";
            string roleText = UserRole.Display(u.Role);
            if (shown.Length == 0
                || string.Equals(shown, roleText, StringComparison.Ordinal)
                || string.Equals(shown, u.Username, StringComparison.OrdinalIgnoreCase))
                shown = "—";

            string cls = u.ClassNames.Count == 0 ? "—" : string.Join("、", u.ClassNames);
            string state = u.Enabled ? "启用" : "已停用";

            var row = MakeRow(new[] { u.Username, shown, roleText, cls, state }, ColWidths);
            if (!u.Enabled) row.Opacity = 0.55;

            _list.Items.Add(new ListBoxItem
            {
                Content = row,
                Tag = u,
                Padding = new Thickness(6, 5, 6, 5),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
            });
        }

        int admins = _book.AdminCount;
        _hint.Text = $"共 {_book.Users.Count} 个账号（管理员 {admins} 个）。"
            + "角色：管理员可改全部数据并发布；班主任只能改自己班的课表；电教管理员（学生）只能控制本机显示。\n"
            + "★ 开机自动登录只允许「班主任 / 电教管理员」，管理员账号永不参与自动登录 —— "
            + "否则谁坐到这台电脑前谁就有管理员权限。";
    }

    AppUser? Selected()
        => (_list.SelectedItem as ListBoxItem)?.Tag as AppUser;

    IEnumerable<string> AllClasses()
    {
        try { return (Store.LoadSchedule()?.Classes.Select(c => c.ClassName) ?? Enumerable.Empty<string>()).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    void AddUser()
    {
        var dlg = new UserEditWindow(null, _book, AllClasses()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        _book.Users.Add(dlg.Result);
        SaveAll();
        Reload();
    }

    void EditUser()
    {
        var u = Selected();
        if (u == null) { Tip("请先在列表里选一个账号。"); return; }

        var dlg = new UserEditWindow(u, _book, AllClasses()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        SaveAll();
        Reload();

        // 改的是当前登录的人 → 立即刷新界面上的权限
        if (UserSession.Current != null &&
            string.Equals(UserSession.Current.Username, u.Username, StringComparison.OrdinalIgnoreCase))
            Tip("已保存。你修改的是当前登录账号，权限已立即生效。");
    }

    void ToggleEnabled()
    {
        var u = Selected();
        if (u == null) { Tip("请先在列表里选一个账号。"); return; }

        if (u.Enabled && u.Role == UserRole.Admin && _book.AdminCount <= 1)
        {
            Tip("这是最后一个启用中的管理员，停用后就没人能管理用户了。");
            return;
        }

        u.Enabled = !u.Enabled;
        SaveAll();
        Reload();
    }

    void DeleteUser()
    {
        var u = Selected();
        if (u == null) { Tip("请先在列表里选一个账号。"); return; }

        if (u.Role == UserRole.Admin)
        {
            int others = _book.Users.Count(x => x.Enabled && x.Role == UserRole.Admin
                                                && !string.Equals(x.Username, u.Username, StringComparison.OrdinalIgnoreCase));
            if (others == 0) { Tip("这是最后一个管理员，删掉就没人能管理用户了。"); return; }
        }

        if (System.Windows.MessageBox.Show(
                $"确定删除账号「{u.Username}（{u.Caption}）」吗？\n\n删除后该账号不能再登录，已记录的课表数据不受影响。",
                "删除账号", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _book.Users.Remove(u);
        SaveAll();
        Reload();
    }

    void SaveAll()
    {
        try { Store.SaveUsers(_book); }
        catch (Exception ex) { Tip("保存失败：" + ex.Message); }
    }

    void Tip(string msg)
        => System.Windows.MessageBox.Show(msg, "用户管理", MessageBoxButton.OK, MessageBoxImage.Information);

    // ================== 导入名单 ==================

    /// <summary>
    /// 从表格导入：列顺序 用户名 | 显示名 | 角色 | 班级 | 密码。
    /// 第一行是表头（会被跳过）；角色可写 管理员/班主任/学生，也可写 admin/teacher/student。
    /// 用户名已存在的会被更新（同名不新增），密码留空则保留原密码。
    /// </summary>
    void ImportUsers()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "表格文件 (*.xlsx;*.csv)|*.xlsx;*.csv|所有文件 (*.*)|*.*",
            Title = "导入用户名单"
        };
        if (dlg.ShowDialog() != true) return;

        List<string[]> rows;
        try { rows = ReadTable(dlg.FileName); }
        catch (Exception ex) { Tip("读取失败：" + ex.Message); return; }

        if (rows.Count == 0) { Tip("没读到任何数据行。"); return; }

        int added = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Length == 0 || string.IsNullOrWhiteSpace(Cell(r, 0))) { skipped++; continue; }

            string user = Cell(r, 0);
            string display = Cell(r, 1);
            string roleText = Cell(r, 2);
            string clsText = Cell(r, 3);
            string pwd = Cell(r, 4);

            // 没写角色时按"有班级 = 班主任，无班级 = 电教管理员"推断，比默认成管理员安全
            string role = ParseRole(roleText)
                          ?? (string.IsNullOrWhiteSpace(clsText) ? UserRole.Student : UserRole.Teacher);

            var classes = clsText
                .Split(new[] { ',', '，', '、', ';', '；', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

            if (role == UserRole.Teacher && classes.Count == 0)
            {
                errors.Add($"第 {i + 2} 行「{user}」是班主任但没填班级，已跳过");
                skipped++;
                continue;
            }

            var exist = _book.Find(user);
            if (exist != null)
            {
                exist.DisplayName = string.IsNullOrWhiteSpace(display) ? exist.DisplayName : display;
                exist.Role = role;
                exist.ClassNames = classes;
                if (!string.IsNullOrEmpty(pwd)) PasswordHasher.SetPassword(exist, pwd);
                updated++;
            }
            else
            {
                if (string.IsNullOrEmpty(pwd))
                {
                    errors.Add($"第 {i + 2} 行「{user}」是新增账号但没填密码，已跳过");
                    skipped++;
                    continue;
                }
                var u = new AppUser
                {
                    Username = user,
                    DisplayName = display,
                    Role = role,
                    ClassNames = classes
                };
                PasswordHasher.SetPassword(u, pwd);
                _book.Users.Add(u);
                added++;
            }
        }

        // 至少得留一个管理员
        if (_book.AdminCount == 0)
        {
            errors.Add("导入后没有任何管理员，已自动把第一个账号设为管理员");
            var first = _book.Users.FirstOrDefault();
            if (first != null) first.Role = UserRole.Admin;
        }

        SaveAll();
        Reload();

        var msg = $"导入完成：新增 {added} 个、更新 {updated} 个、跳过 {skipped} 个。";
        if (errors.Count > 0) msg += "\n\n" + string.Join("\n", errors.Take(10))
                                    + (errors.Count > 10 ? $"\n…另有 {errors.Count - 10} 条" : "");
        Tip(msg);
    }

    static string Cell(string[] r, int i) => i < r.Length ? (r[i] ?? "").Trim() : "";

    static string? ParseRole(string text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        return t switch
        {
            "管理员" or "admin" or "administrator" => UserRole.Admin,
            "班主任" or "teacher" or "老师" => UserRole.Teacher,
            "学生" or "电教" or "电教管理员" or "student" or "local" => UserRole.Student,
            _ => null
        };
    }

    static List<string[]> ReadTable(string path)
    {
        var rows = new List<string[]>();

        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var lines = File.ReadAllLines(path);
            // 跳过表头
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                rows.Add(line.Split(','));
            }
            return rows;
        }

        using var wb = new ClosedXML.Excel.XLWorkbook(path);
        var ws = wb.Worksheet(1);
        bool first = true;
        foreach (var row in ws.RowsUsed())
        {
            if (first) { first = false; continue; }   // 表头
            var vals = new List<string>();
            int last = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (int c = 1; c <= Math.Max(last, 1); c++)
                vals.Add(row.Cell(c).GetString() ?? "");
            rows.Add(vals.ToArray());
        }
        return rows;
    }
}
