using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ClassBulletinApp;

/// <summary>
/// 用户管理（仅管理员可用）。
///
/// <para>改完点「保存并关闭」写进本机 <c>users.json</c>；发布数据包时它会一起下发到各教室机
/// （只带用户名 / 哈希 / 盐 / 角色 / 班级 / 任课科目，<b>绝不含明文密码</b>）。</para>
///
/// <para>★ 界面上能做的限制，抵不过直接改 json 文件 —— 这是离线单机程序的固有边界，
/// 权限在这里的作用是"不给入口、减少误操作"，不是安全防护。</para>
///
/// <para><b>版面（r155 重做）</b>：原先十个按钮挤在底部一行换行堆两排，和列表抢空间。
/// 现在按"建 / 管"分开 —— 建账号的三个动作（新增 / 导入名单 / 从课表生成）放顶部工具条，
/// 对选中账号的动作（修改 / 启用停用 / 删除）与窗口级动作（角色权限 / 变更记录 / 保存）放底部；
/// 常显的一大段权限说明收进标题右边的问号。列表支持点表头排序、Ctrl / Shift 多选，
/// 「按科目」下拉可一键筛出并全选某一科的人，批量停用 / 删除就不用一个个勾。</para>
/// </summary>
public class UserManagerWindow : Window
{
    readonly ListBox _list = new();
    readonly TextBox _search = new();
    readonly ComboBox _subjectCombo = new();
    readonly TextBlock _count = new();
    readonly TextBlock _selInfo = new();
    readonly Controls.HelpBubble _bubble;
    readonly Grid _header;
    readonly List<Button> _headerButtons = new();
    readonly UserBook _book;

    /// <summary>导出按钮（导出期间置灰，避免连点写同一个文件）。</summary>
    Button? _btnExport;

    enum SortField { Role, Username, Display, ClassCount, Subject, Enabled }

    SortField _sort = SortField.Role;
    bool _sortDesc;
    string _keyword = "";
    string? _subjectFilter;          // null = 不筛；"" = 未填（不限）；其他 = 科目名
    bool _suppressSel;

    /// <summary>列表列宽（表头与数据行共用，改一处两边一起变）。合计留出余量，避免出现横向滚动条。</summary>
    static readonly double[] ColWidths = { 130, 150, 90, 300, 120, 66 };

    static readonly (string Title, double Width, SortField Sort)[] Columns =
    {
        ("用户名", 130, SortField.Username),
        ("显示名", 150, SortField.Display),
        ("角色", 90, SortField.Role),
        ("班级", 300, SortField.ClassCount),
        ("任课科目", 120, SortField.Subject),
        ("状态", 66, SortField.Enabled)
    };

    public UserManagerWindow()
    {
        Title = "用户管理";
        Width = 1000;
        Height = 660;
        MinWidth = 860;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ChromePalette.BrushOf(ChromePalette.Surface, "#F4F6FA");

        _book = Store.LoadUsers();

        var root = new DockPanel { Margin = new Thickness(16, 14, 16, 14), LastChildFill = true };
        Content = root;

        // ---------- ① 标题行（长说明收进问号）----------
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "用户管理",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _bubble = new Controls.HelpBubble
        {
            PopupTitle = "账号与权限说明",
            BubbleWidth = 470,
            Margin = new Thickness(8, 0, 0, 0)
        };
        titleRow.Children.Add(_bubble);
        head.Children.Add(titleRow);

        _count.FontSize = 12.5;
        _count.Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
        _count.TextWrapping = TextWrapping.Wrap;
        _count.Margin = new Thickness(0, 2, 0, 0);
        head.Children.Add(_count);

        // ---------- ② 建账号工具条（从底部搬上来，与"管理"分开）----------
        var buildCard = Card();
        DockPanel.SetDock(buildCard, Dock.Top);
        root.Children.Add(buildCard);

        var buildRow = new StackPanel { Orientation = Orientation.Horizontal };
        buildCard.Child = buildRow;

        buildRow.Children.Add(new TextBlock
        {
            Text = "建账号",
            FontSize = 12.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var btnAdd = Mk("新增账号", ChromePalette.HexOf(ChromePalette.PrimaryLight, "#E9EFF9"));
        btnAdd.Click += (_, _) => AddUser();
        buildRow.Children.Add(btnAdd);

        var btnImport = Mk("从 Excel / CSV 导入名单", "#E8F6EE");
        btnImport.ToolTip = "第一行写表头，按列名认列（插入新列、调换列顺序都不影响）：\n"
                          + "用户名 | 显示名 | 角色 | 任教班级 | 新密码 | 任课科目 | 班主任班级 | 状态。\n"
                          + "同名账号＝更新，不同名＝新增；密码留空保留原密码，单元格留空＝这一项不动（写「-」表示清空）。\n"
                          + "写完盘之前会先列出「新增 / 更新 / 停用 / 跳过」的明细，确认后才生效。";
        btnImport.Click += (_, _) => ImportUsers();
        buildRow.Children.Add(btnImport);

        // 导出：与导入配成一对（拿到表 → 改 → 导回）
        _btnExport = Mk("导出账号表…", "#F3EEFB");
        _btnExport.ToolTip = "把账号资料导成 Excel：第 1 页账号表（可改完再导回）、第 2 页填写说明、第 3 页角色权限矩阵。\n"
                          + "出于安全，密码不出现在表格里 ——「新密码」列恒空，留空导回时保留原密码。\n"
                          + "这不是完整备份：要连密码一起备份，请用「设置 → 数据管理 → 备份与恢复」。\n"
                          + "列表里有筛选时，会先问是导全部还是只导当前显示的这些。";
        _btnExport.Click += (_, _) => ExportUsers();
        buildRow.Children.Add(_btnExport);

        // 从课表底表批量生成全校教师账号（预览确认后才写盘）
        var btnGen = Mk("从课表生成账号", "#EAF6F4");
        btnGen.ToolTip = "按课表底表的任课教师建账号：上班主任课的那位是本班班主任，其余为科任老师。\n"
                       + "初始密码 123，只存哈希；已有账号不覆盖。";
        btnGen.Click += (_, _) =>
        {
            var dlg = new TeacherAccountWindow(_book) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Saved) Reload();
        };
        buildRow.Children.Add(btnGen);

        buildRow.Children.Add(new TextBlock
        {
            Text = "改动确认后才写盘",
            FontSize = 11.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // ---------- ③ 筛选条 ----------
        var filterCard = Card();
        DockPanel.SetDock(filterCard, Dock.Top);
        root.Children.Add(filterCard);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal };
        filterCard.Child = filterRow;

        filterRow.Children.Add(new TextBlock
        {
            Text = "搜索",
            FontSize = 12.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });

        _search.Width = 200;
        _search.FontSize = 13;
        _search.Padding = new Thickness(7, 5, 7, 5);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = "按用户名、显示名、角色、班级、科目过滤。按 Esc 清空。";
        _search.TextChanged += (_, _) =>
        {
            string kw = (_search.Text ?? "").Trim();
            if (string.Equals(kw, _keyword, StringComparison.Ordinal)) return;
            _keyword = kw;
            Reload();
        };
        filterRow.Children.Add(_search);

        filterRow.Children.Add(new TextBlock
        {
            Text = "按科目",
            FontSize = 12.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 6, 0)
        });

        _subjectCombo.Width = 190;
        _subjectCombo.FontSize = 13;
        _subjectCombo.Padding = new Thickness(6, 4, 6, 4);
        _subjectCombo.VerticalContentAlignment = VerticalAlignment.Center;
        _subjectCombo.ToolTip = "选一门课 → 列表只留这门课的老师并全部选中，接着用下方按钮批量停用 / 删除。\n选「全部科目」回到完整名单。";
        _subjectCombo.SelectionChanged += (_, _) => OnSubjectFilterChanged();
        filterRow.Children.Add(_subjectCombo);

        _selInfo.FontSize = 12.5;
        _selInfo.Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
        _selInfo.VerticalAlignment = VerticalAlignment.Center;
        _selInfo.Margin = new Thickness(16, 0, 0, 0);
        _selInfo.Text = "已选 0 人";
        filterRow.Children.Add(_selInfo);

        filterRow.Children.Add(new TextBlock
        {
            Text = "点表头排序 · Ctrl / Shift 多选",
            FontSize = 11.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // ---------- ④ 表头（可点击排序）----------
        _header = MakeHeader();
        DockPanel.SetDock(_header, Dock.Top);
        root.Children.Add(_header);

        // ---------- ⑤ 底部：对选中账号的动作 + 窗口级动作 ----------
        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(left, 0);
        bar.Children.Add(left);

        var btnEdit = Mk("修改 / 重置密码");
        btnEdit.Click += (_, _) => EditUser();
        left.Children.Add(btnEdit);

        var btnToggle = Mk("启用 / 停用");
        btnToggle.ToolTip = "选中多个时整批切换：有停用的就全部启用，否则全部停用。";
        btnToggle.Click += (_, _) => ToggleEnabled();
        left.Children.Add(btnToggle);

        var btnDel = Mk("删除", "#FDE8E8");
        btnDel.ToolTip = "选中多个时整批删除（会再确认一次）。";
        btnDel.Click += (_, _) => DeleteUser();
        left.Children.Add(btnDel);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);

        // 角色权限：改完立即落盘（与其他操作一致），回来后刷新本窗口提示
        var btnPolicy = Mk("角色权限…", "#F3EEFB");
        btnPolicy.Click += (_, _) =>
        {
            var dlg = new RolePolicyWindow(_book) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Changed) Reload();
        };
        right.Children.Add(btnPolicy);

        // 权限变更记录：倒查"谁在哪次改了哪项权限"
        var btnLog = Mk("权限变更记录…");
        btnLog.Click += (_, _) => new PolicyLogWindow { Owner = this }.ShowDialog();
        right.Children.Add(btnLog);

        var btnSave = Mk("保存并关闭", "#E8F6EE");
        btnSave.Click += (_, _) => { SaveAll(); DialogResult = true; Close(); };
        right.Children.Add(btnSave);

        var btnClose = Mk("取消");
        btnClose.Click += (_, _) => Close();
        btnClose.Margin = new Thickness(0);
        right.Children.Add(btnClose);

        // ---------- ⑥ 列表 ----------
        _list.FontSize = 13;
        _list.Margin = new Thickness(0, 0, 0, 0);
        _list.BorderThickness = new Thickness(0, 1, 0, 0);
        _list.BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE");
        _list.Background = System.Windows.Media.Brushes.Transparent;
        _list.Padding = new Thickness(0, 4, 0, 0);
        _list.SelectionMode = SelectionMode.Extended;
        _list.SelectionChanged += (_, _) => { if (!_suppressSel) UpdateSelInfo(); };
        _list.MouseDoubleClick += (_, _) => EditUser();
        root.Children.Add(_list);

        Reload();

        GlassChrome.Prepare(this);
    }

    // ================== 构建小工具 ==================

    static Border Card() => new()
    {
        Background = ChromePalette.BrushOf("CardBrush", "#FFFFFF"),
        BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 9, 12, 9),
        Margin = new Thickness(0, 0, 0, 8)
    };

    static Button Mk(string text, string? bg = null)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (bg != null) b.Background = HexBrush(bg);
        return b;
    }

    /// <summary>表头：每列一个扁平按钮，点一下按该列排序、再点反转方向。</summary>
    Grid MakeHeader()
    {
        var g = new Grid { Margin = new Thickness(6, 4, 6, 4) };
        foreach (var c in Columns)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Width) });

        _headerButtons.Clear();
        for (int i = 0; i < Columns.Length; i++)
        {
            var col = Columns[i];
            var b = new Button
            {
                Content = col.Title,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Height = 20,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.Gray,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "按「" + col.Title + "」排序"
            };
            b.Click += (_, _) =>
            {
                if (_sort == col.Sort) _sortDesc = !_sortDesc;
                else { _sort = col.Sort; _sortDesc = false; }
                UpdateHeaderText();
                Reload(SelectedNames());
            };
            Grid.SetColumn(b, i);
            g.Children.Add(b);
            _headerButtons.Add(b);
        }

        UpdateHeaderText();
        return g;
    }

    void UpdateHeaderText()
    {
        for (int i = 0; i < Columns.Length && i < _headerButtons.Count; i++)
        {
            var b = _headerButtons[i];
            bool active = _sort == Columns[i].Sort;
            b.Content = active ? Columns[i].Title + (_sortDesc ? " ▼" : " ▲") : Columns[i].Title;
            b.Foreground = active
                ? HexBrush(ChromePalette.HexOf(ChromePalette.Primary, "#1E4FA3"))
                : System.Windows.Media.Brushes.Gray;
        }
    }

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
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = bold ? 12.5 : 13
            };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (dim) t.Foreground = System.Windows.Media.Brushes.Gray;
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    static System.Windows.Media.Brush HexBrush(string hex) => ListRowStyle.HexBrush(hex);

    // ================== 列表 ==================

    /// <summary>重建列表（可保留选中：批量操作后不想丢掉刚挑好的那批人）。</summary>
    void Reload(IEnumerable<string>? keepSelected = null)
    {
        var keep = new HashSet<string>(keepSelected ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        var rows = _book.Users.Where(Pass).ToList();
        rows.Sort(Compare);

        _suppressSel = true;
        try
        {
            _list.Items.Clear();
            foreach (var u in rows)
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

                var row = MakeRow(
                    new[] { u.Username, shown, roleText, cls, u.SubjectCaption, u.Enabled ? "启用" : "已停用" },
                    ColWidths);
                if (!u.Enabled) row.Opacity = 0.55;

                _list.Items.Add(new ListBoxItem
                {
                    Content = row,
                    Tag = u,
                    Style = ListRowStyle.Get(),
                    ToolTip = BuildTip(u),
                    IsSelected = keep.Contains(u.Username)
                });
            }
        }
        finally { _suppressSel = false; }

        RefreshSubjects();
        UpdateCount();
        UpdateSelInfo();
    }

    /// <summary>关键字 + 科目筛选。</summary>
    bool Pass(AppUser u)
    {
        if (_subjectFilter != null)
        {
            string mine = u.NormalizedSubject;
            if (!string.Equals(mine, _subjectFilter, StringComparison.Ordinal)) return false;
        }

        if (_keyword.Length == 0) return true;
        string role = UserRole.Display(u.Role);
        return Hit(u.Username) || Hit(u.DisplayName) || Hit(role)
               || u.ClassNames.Any(Hit) || u.HeadClassNames.Any(Hit) || Hit(u.SubjectCaption);

        bool Hit(string? s) => !string.IsNullOrEmpty(s) && s.Contains(_keyword, StringComparison.OrdinalIgnoreCase);
    }

    int Compare(AppUser a, AppUser b)
    {
        int r = _sort switch
        {
            SortField.Username => Zh.Compare(a.Username, b.Username),
            SortField.Display => Zh.Compare(a.DisplayName, b.DisplayName),
            SortField.Role => RoleRank(a.Role).CompareTo(RoleRank(b.Role)),
            SortField.ClassCount => a.ClassNames.Count.CompareTo(b.ClassNames.Count),
            SortField.Subject => Zh.Compare(a.NormalizedSubject, b.NormalizedSubject),
            _ => (a.Enabled ? 1 : 0).CompareTo(b.Enabled ? 1 : 0)
        };

        if (_sortDesc) r = -r;
        // 主键相同按中文姓名序：课表生成后一次会有两三百个中文名，
        // 按码位排出来是乱的（张、李、陈 看着随机），拼音序才像一份名单。
        if (r == 0) r = Zh.Compare(a.Username, b.Username);
        return r;
    }

    static readonly IComparer<string> Zh = TeacherExtractor.ZhComparer();

    static int RoleRank(string? role) => (role ?? "").Trim().ToLowerInvariant() switch
    {
        UserRole.Admin => 0,
        UserRole.Teacher => 1,
        UserRole.SubjectTeacher => 2,
        _ => 3
    };

    /// <summary>科目下拉里的一条。</summary>
    sealed class SubjectEntry
    {
        public string? Key;
        public string Caption = "";
        public override string ToString() => Caption;
    }

    /// <summary>按现有账号的任课科目重建下拉（「全部」永远在第一项，未填科目排最后）。</summary>
    void RefreshSubjects()
    {
        var groups = _book.Users
            .GroupBy(u => u.NormalizedSubject, StringComparer.Ordinal)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key.Length == 0 ? "\uffff" : g.Key, Zh)
            .ToList();

        if (_subjectFilter != null && !groups.Any(g => string.Equals(g.Key, _subjectFilter, StringComparison.Ordinal)))
            _subjectFilter = null;

        var entries = new List<SubjectEntry>
        {
            new() { Key = null, Caption = $"全部科目（{_book.Users.Count} 人）" }
        };
        foreach (var g in groups)
            entries.Add(new SubjectEntry
            {
                Key = g.Key,
                Caption = (g.Key.Length == 0 ? "未填（不限）" : g.Key) + $"（{g.Count} 人）"
            });

        _suppressSubject = true;
        try
        {
            _subjectCombo.ItemsSource = entries;
            int idx = 0;
            if (_subjectFilter != null)
            {
                // ★ 不能用 `e.Key ?? ""` 合并空科目：空科目组的 Key 本来就是 ""，
                //   合并后会和「全部科目」（Key=null）撞上 —— 下拉自己跳回「全部」，
                //   而列表还筛着「未填」那 95 人，看上去像「选了全部却没恢复」。
                int found = entries.FindIndex(e =>
                    e.Key != null && string.Equals(e.Key, _subjectFilter, StringComparison.Ordinal));
                if (found >= 0) idx = found;
            }
            _subjectCombo.SelectedIndex = idx;
        }
        finally { _suppressSubject = false; }

        _subjectCombo.IsEnabled = _book.Users.Count > 0;
    }

    bool _suppressSubject;

    void OnSubjectFilterChanged()
    {
        if (_suppressSubject) return;

        var entry = _subjectCombo.SelectedItem as SubjectEntry;
        string? key = entry?.Key;
        if (string.Equals(_subjectFilter, key, StringComparison.Ordinal)) return;

        _subjectFilter = key;
        Reload(SelectedNames());

        // 选了某一科 → 整批选中，紧接着就能批量停用 / 删除
        if (key != null) SelectAllShown();
    }

    void SelectAllShown()
    {
        _suppressSel = true;
        try { foreach (ListBoxItem it in _list.Items) it.IsSelected = true; }
        finally { _suppressSel = false; }
        UpdateSelInfo();
    }

    void UpdateCount()
    {
        int admins = _book.AdminCount;
        int generated = _book.Users.Count(u => !string.IsNullOrWhiteSpace(u.GeneratedFromSchedule));
        _count.Text = $"共 {_book.Users.Count} 个账号（启用中的管理员 {admins} 个"
            + (generated > 0 ? $"，课表自动生成 {generated} 个" : "")
            + "）。启用 / 停用与删除都支持多选后整批执行。";

        // 权限说明随「角色权限…」变化，每次刷新重算气泡正文
        _bubble.Text = "管理员可改全部数据并发布（恒全权，不可配置）；班主任 / 科任老师 / 电教管理员能做什么，"
            + "由下方「角色权限…」决定，改完随数据包下发到各教室机。\n\n"
            + $"当前：班主任 = {_book.PolicyFor(UserRole.Teacher).Describe()}；\n"
            + $"科任老师 = {_book.PolicyFor(UserRole.SubjectTeacher).Describe()}；\n"
            + $"电教管理员 = {_book.PolicyFor(UserRole.Student).Describe()}。\n\n"
            + "★ 班主任只能改「班主任班级」里的课表，任教的其他班可以发作业 / 缺交；"
            + "开机自动登录只允许「班主任 / 科任老师 / 电教管理员」，管理员账号永不参与自动登录 —— "
            + "否则谁坐到这台电脑前谁就有管理员权限。\n\n"
            + "★ 修改 / 重置密码只对单个账号生效；启用停用与删除支持多选整批执行。"
            + "课程表生成出来的账号，手工改过之后就不会再被「从课表生成账号」覆盖。";
    }

    void UpdateSelInfo()
    {
        int n = _list.SelectedItems.Count;
        _selInfo.Text = $"已选 {n} 人｜显示 {_list.Items.Count}/{_book.Users.Count}";
        _selInfo.Foreground = n > 0
            ? HexBrush(ChromePalette.HexOf(ChromePalette.Primary, "#1E4FA3"))
            : ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
    }

    List<AppUser> SelectedUsers()
        => _list.SelectedItems.OfType<ListBoxItem>()
                .Select(i => i.Tag as AppUser)
                .Where(u => u != null)
                .Select(u => u!)
                .ToList();

    List<string> SelectedNames() => SelectedUsers().Select(u => u.Username).ToList();

    AppUser? Selected() => SelectedUsers().FirstOrDefault();

    /// <summary>行悬停提示：班级列窄、班主任班不单列，完整信息放这里。</summary>
    static string BuildTip(AppUser u)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(u.Username).Append("（").Append(UserRole.Display(u.Role)).Append('）');
        if (!u.Enabled) sb.Append(" · 已停用");
        sb.AppendLine();
        sb.AppendLine("任教班级：" + (u.ClassNames.Count == 0 ? "—" : string.Join("、", u.ClassNames)));
        sb.AppendLine("班主任班级：" + (u.HeadClassNames.Count == 0 ? "—（按任教班级算）" : string.Join("、", u.HeadClassNames)));
        sb.Append("任课科目：").Append(u.SubjectCaption);
        if (!string.IsNullOrWhiteSpace(u.GeneratedFromSchedule))
            sb.AppendLine().Append("来源：从课表生成（手工改动后不再自动刷新）");
        return sb.ToString();
    }

    IEnumerable<string> AllClasses()
    {
        try { return (Store.LoadSchedule()?.Classes.Select(c => c.ClassName) ?? Enumerable.Empty<string>()).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    // ================== 单个 / 批量操作 ==================

    void AddUser()
    {
        var dlg = new UserEditWindow(null, _book, AllClasses()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        _book.Users.Add(dlg.Result);
        SaveAll();
        string[] keep = string.IsNullOrWhiteSpace(dlg.Result.Username) ? Array.Empty<string>() : new[] { dlg.Result.Username };
        Reload(keep);
    }

    void EditUser()
    {
        var picked = SelectedUsers();
        if (picked.Count == 0) { Tip("请先在列表里选一个账号。"); return; }
        if (picked.Count > 1) { Tip($"「修改 / 重置密码」一次只改一个账号（现在选了 {picked.Count} 个）。\n\n"
                                  + "批量要改的如果是科目，可以用上方「按科目」先筛出一批，再到「从课表生成账号」里整批设科目；"
                                  + "如果是启用 / 停用或删除，底部那两个按钮支持多选。"); return; }

        var u = picked[0];
        var dlg = new UserEditWindow(u, _book, AllClasses()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        SaveAll();
        Reload(new[] { u.Username });

        // 改的是当前登录的人 → 立即刷新界面上的权限
        if (UserSession.Current != null &&
            string.Equals(UserSession.Current.Username, u.Username, StringComparison.OrdinalIgnoreCase))
            Tip("已保存。你修改的是当前登录账号，权限已立即生效。");
    }

    void ToggleEnabled()
    {
        var picked = SelectedUsers();
        if (picked.Count == 0) { Tip("请先在列表里选账号（可 Ctrl / Shift 多选）。"); return; }

        // 目标状态：有停用的就把这批全部启用；否则全部停用
        bool enable = picked.Any(u => !u.Enabled);

        if (!enable)
        {
            int admins = picked.Count(u => u.Enabled && u.Role == UserRole.Admin);
            if (_book.AdminCount - admins <= 0)
            {
                Tip("这批里包含现在唯一启用中的管理员，停用后就没人能管理用户了。");
                return;
            }
        }

        foreach (var u in picked) u.Enabled = enable;
        SaveAll();
        Reload(picked.Select(u => u.Username));
    }

    void DeleteUser()
    {
        var picked = SelectedUsers();
        if (picked.Count == 0) { Tip("请先在列表里选账号（可 Ctrl / Shift 多选）。"); return; }

        int adminsToDelete = picked.Count(u => u.Enabled && u.Role == UserRole.Admin);
        if (_book.AdminCount - adminsToDelete <= 0)
        {
            Tip("删掉就没有启用中的管理员了，已取消。请先另设一个管理员。");
            return;
        }

        string msg = picked.Count == 1
            ? $"确定删除账号「{picked[0].Username}（{picked[0].Caption}）」吗？\n\n删除后该账号不能再登录，已记录的课表数据不受影响。"
            : $"确定删除选中的 {picked.Count} 个账号吗？\n\n"
              + string.Join("、", picked.Take(8).Select(u => u.Username))
              + (picked.Count > 8 ? $" 等 {picked.Count} 个" : "")
              + "\n\n删除后这些账号不能再登录，已记录的课表数据不受影响。";

        if (System.Windows.MessageBox.Show(msg, "删除账号",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        foreach (var u in picked) _book.Users.Remove(u);
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

    // ================== 导出 / 导入名单 ==================

    /// <summary>单元格写「-」表示"清空这一项"（空单元格一律理解为"不动"）。</summary>
    static bool IsClearCell(string cell)
    {
        var t = (cell ?? "").Trim();
        return t == "-" || t == "—" || t == "－" || t == "无" || t == "/";
    }

    /// <summary>
    /// 导出账号资料表（.xlsx）。列表里有筛选时先问范围 —— 默认导全部，
    /// 因为"筛着筛着把半个名单当备份导走"是最容易犯的错。
    /// </summary>
    async void ExportUsers()
    {
        int total = _book.Users.Count;
        if (total == 0) { Tip("现在还没有任何账号，没什么可导出的。"); return; }

        var shown = _list.Items.OfType<ListBoxItem>()
                        .Select(i => i.Tag as AppUser)
                        .Where(u => u != null).Select(u => u!).ToList();

        List<AppUser> scope = _book.Users.ToList();

        if (shown.Count > 0 && shown.Count < total)
        {
            var pick = System.Windows.MessageBox.Show(
                $"列表里有筛选：现在显示 {shown.Count} 个，账号一共 {total} 个。\n\n"
                + "「是」＝ 导出全部（适合备份、核对）\n"
                + "「否」＝ 只导出当前显示的这些（适合只发某个年级）",
                "导出范围", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (pick == MessageBoxResult.Cancel) return;
            if (pick == MessageBoxResult.No) scope = shown;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出账号资料表",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"用户账号_{DateTime.Now:yyyyMMdd}.xlsx"
        };
        try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        catch { /* 没有桌面目录就用系统默认 */ }
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".xlsx");

        string scopeNote = scope.Count == total
            ? $"本次导出全部 {total} 个账号。"
            : $"本次只导出当前筛选出的 {scope.Count} 个账号（账号一共 {total} 个）。";

        // 目标可能是网络盘 / OneDrive：写盘一律后台，别让窗口假死
        if (_btnExport != null) _btnExport.IsEnabled = false;
        try
        {
            await System.Threading.Tasks.Task.Run(
                () => UserExportService.Export(_book, scope, path, scopeNote));
        }
        catch (UnauthorizedAccessException)
        {
            Tip($"没有写入权限，导出失败：\n{path}\n\n换一个文件夹（比如桌面）再试，或看看这个文件是不是只读。");
            return;
        }
        catch (IOException ex)
        {
            Tip($"写不进去：{ex.Message}\n\n最常见的原因是同名文件正被 Excel / WPS 打开——先关掉它再导出。");
            return;
        }
        catch (Exception ex)
        {
            Tip("导出失败：" + ex.Message);
            return;
        }
        finally
        {
            if (_btnExport != null) _btnExport.IsEnabled = true;
        }

        var ask = System.Windows.MessageBox.Show(
            $"已导出 {scope.Count} 个账号：\n{path}\n\n"
            + "提醒：文件里有全校账号名单，别发到班级群 / 家长群。\n\n要打开所在文件夹吗？",
            "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (ask == MessageBoxResult.Yes)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch { /* 打开失败不影响导出结果 */ }
        }
    }

    // ---------------- 导入：读表 → 建计划 → 预览确认 → 应用 ----------------

    /// <summary>
    /// 从表格导入账号。列按第一行表头认（认不出表头就按老的位置顺序读，第 1 行仍跳过）；
    /// 空单元格＝这一项不动，写「-」＝清空。写盘前统一弹预览确认。
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
        try { rows = UserImportService.ReadTable(dlg.FileName); }
        catch (Exception ex) { Tip("读取失败：" + ex.Message); return; }

        if (rows.Count == 0) { Tip("这个文件里没有内容。"); return; }
        if (rows.Count == 1) { Tip("只有表头，没有数据行。"); return; }

        // 认不出表头 → 退回老的位置模式（第 1 行照样当表头跳过），升级前后老表格都能导
        var map = UserImportService.MapColumns(rows[0]) ?? UserImportService.Positional();
        var result = UserImportService.Build(_book, rows.Skip(1).ToList(), map);

        if (!result.AnyChange)
        {
            Tip(result.Same > 0
                ? $"表里 {result.Same} 个账号都跟现有资料一样，没有需要改的。"
                : "没读到有效的数据行，或者每一行都被跳过了。");
            return;
        }

        var preview = new UserImportPreviewWindow(result, Path.GetFileName(dlg.FileName)) { Owner = this };
        if (preview.ShowDialog() != true) return;

        UserImportService.Apply(_book, result.Plans);
        SaveAll();
        Reload();

        string msg = $"已应用：新增 {result.Add} 个、更新 {result.Update} 个";
        if (result.Enable > 0) msg += $"、启用 {result.Enable} 个";
        if (result.Disable > 0) msg += $"、停用 {result.Disable} 个";
        if (result.Skip > 0) msg += $"、跳过 {result.Skip} 个";
        if (result.Same > 0) msg += $"（{result.Same} 个无需改动）";
        Tip(msg + "。\n\n要下发到教室机，还需要「发布数据包」。");
    }
}
