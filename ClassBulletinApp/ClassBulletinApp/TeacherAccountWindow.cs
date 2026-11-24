using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleCore.Models;
using ScheduleCore.Services;

using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using SelectionMode = System.Windows.Controls.SelectionMode;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ClassBulletinApp;

/// <summary>
/// 「从课表生成教师账号」的预览确认窗。
///
/// <para>★ 这一步<b>只预览、不落盘账号</b>：先把"要建谁、是什么角色、管哪个班、教什么科目"摆出来，
/// 管理员确认后才由 <see cref="TeacherAccountGenerator.Apply"/> 写进用户表。
/// 生成 200 多个账号是不可撤销的操作，不给预览等于赌博。</para>
///
/// <para>「班主任科目」在这里配，而不是塞进课表导入流程 —— 它只决定"谁当班主任"，
/// 跟课表怎么解析无关；放进导入流程会逼着人为了改个规则重新导一遍课表。</para>
///
/// <para><b>科目口径</b>：默认取课时最多的那门课。283 位老师里绝大多数兼教两三门，
/// 逐个手工改不现实，所以这里给了「按科目下拉 → 整批选中 → 批量设置」三步，
/// 选定结果记在本机 <c>teacher_subjects.json</c>，下次打开自动沿用、也不会把账号冻成"手工改过"。</para>
///
/// <para><b>版面（r156）</b>：规则卡（班主任科目 / 重新预览 / 底表科目名）＋ 筛选卡（搜索 · 按科目 · 已选）
/// ＋ 表头 ＋ 列表 ＋ 底部一行（左边「把选中的 N 人设为 …」批量区，右下角「生成账号 / 取消」）。
/// 批量工具从工具卡移到底部，是为了让操作顺序与视觉顺序一致：先看列表选人，再改这批人的科目，最后生成。
/// 四十多个科目胶囊早已改成按人数排序的下拉；整段规则说明收进标题右边的问号气泡。</para>
/// </summary>
public class TeacherAccountWindow : Window
{
    readonly UserBook _book;

    /// <summary>
    /// 「班主任科目」：既可直接输入（允许「班会，团队活动」这种多写法），
    /// 也能从下拉里挑一个底表已有的科目名 —— 省的凭记忆敲错一个字，整批人都判不出班主任。
    /// </summary>
    readonly ComboBox _headCombo = new();

    readonly TextBox _search = new();
    readonly TextBlock _subjectHint = new();
    readonly TextBlock _stats = new();
    readonly TextBlock _warn = new();
    readonly TextBlock _selInfo = new();
    readonly TextBlock _bulkLabel = new();
    readonly ComboBox _groupCombo = new();
    readonly ComboBox _bulkSubject = new();
    readonly ListBox _list = new();
    readonly Grid _header;
    readonly List<Button> _headerButtons = new();

    /// <summary>批量设科目那一排（下拉 + 三个按钮）：没人选中时统一置灰，省得填了科目才发现没选人。</summary>
    readonly List<Control> _bulkTools = new();

    /// <summary>课表缓存：改科目只重算计划，不必反复读盘。</summary>
    ScheduleFile? _schedule;

    TeacherAccountPlan _plan = new();

    /// <summary>手工指定科目（用户名 → 科目，空串 = 不限），与 teacher_subjects.json 同步。</summary>
    Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    SortField _sort = SortField.Action;
    bool _sortDesc;
    string _keyword = "";

    /// <summary>
    /// 按科目筛选：<c>null</c> = 不筛；<c>""</c> = 只看「不限」；其他 = 该科目名。
    /// <para>★ 不跟搜索框共用：一是不想让选分组污染搜索词，二是「不限」的键是空串，
    /// 塞进搜索框等于"清空筛选"、语义正好反了。</para>
    /// </summary>
    string? _groupFilter;

    /// <summary>批量刷新下拉时抑制 SelectionChanged，避免计数乱跳。</summary>
    bool _suppressSel;

    /// <summary>重建科目下拉时抑制它自己的 SelectionChanged。</summary>
    bool _suppressGroup;

    /// <summary>上一次由程序自动填进批量框的科目名（空 = 用户自己敲的，不再覆盖）。</summary>
    string _bulkAuto = "";

    /// <summary>「班主任科目」下拉上一次灌进去的候选（拼成一串比对，没变就不重灌，免得动着光标）。</summary>
    string _headChoicesKey = "";

    /// <summary>程序在灌候选 / 回填文字时抑制下拉的 SelectionChanged（否则会自己触发一轮重算）。</summary>
    bool _suppressHead;

    static readonly IComparer<string> Zh = TeacherExtractor.ZhComparer();

    /// <summary>确定后是否写入了账号（供调用方决定要不要刷新列表）。</summary>
    public bool Saved { get; private set; }

    enum SortField { Username, Role, ClassCount, Head, Subject, Action }

    /// <summary>列表列定义（表头与数据行共用同一份宽度，改一处即可）。</summary>
    static readonly (string Title, double Width, SortField Sort)[] Columns =
    {
        ("用户名", 96, SortField.Username),
        ("角色", 76, SortField.Role),
        ("任教班级", 320, SortField.ClassCount),
        ("班主任班级", 168, SortField.Head),
        ("科目", 116, SortField.Subject),
        ("处理", 130, SortField.Action)
    };

    /// <summary>列宽数组（从 <see cref="Columns"/> 派生一次，避免每行重新分配）。</summary>
    static readonly double[] ColWidths = Columns.Select(c => c.Width).ToArray();

    /// <summary>标题旁问号里的长说明（整段收起来，界面只留一行副标题）。</summary>
    const string HelpText =
        "【这份账号怎么来的】\n" +
        "· 教师姓名取自课表底表的任课教师；上「班主任课」的那位就是这个班的班主任，其余为科任老师。\n" +
        "· 科目默认取课时最多的那门（班主任课不算，否则专职班主任会被填成「班会」）；课时并列时按科目名顺序取第一个。\n" +
        "· 初始密码统一 123，只存哈希不存明文；已有账号一律不覆盖。\n" +
        "· 本工具生成过的账号跟着课表刷新；你手工改过的会冻结，标「跳过（手工改过）」。\n\n" +
        "【怎么用】\n" +
        "1. 先确认「班主任科目」和底表写法一致 —— 点下拉可直接挑底表里的科目名，也可以自己输入；\n" +
        "   多个写法用逗号分隔（例如「班会，团队活动」）。改完点「重新预览」。\n" +
        "2. 用「按科目」下拉筛出某一科的人 —— 列表会自动全选，接着在底部点「设为该科目」一次改完。\n" +
        "3. 选中若干人后，科目下拉会把他们教的科目排到最前面，省得在四十多门课里翻。\n" +
        "4. 点表头排序，Ctrl / Shift 多选；右键可「选中当前筛选结果」「把选中的恢复成课表自动判定」。\n" +
        "5. 点右下「生成账号」确认后才写盘。改完记得发布数据包，教室机才拿得到这份用户表。\n\n" +
        "【科目怎么用】科目决定这位老师能看哪些班的作业 / 缺交；「不限」= 不按科目限制。";

    public TeacherAccountWindow(UserBook book)
    {
        _book = book ?? new UserBook();

        Title = "从课表生成教师账号";
        Width = 1040;
        Height = 760;
        MinWidth = 1000;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ChromePalette.BrushOf(ChromePalette.Surface, "#F4F6FA");

        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14), LastChildFill = true };
        Content = root;

        // ================== ① 标题行（说明收进问号）==================
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "从课表生成教师账号",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(new Controls.HelpBubble
        {
            PopupTitle = "这份账号是怎么算出来的",
            Text = HelpText,
            BubbleWidth = 470,
            Margin = new Thickness(8, 0, 0, 0)
        });
        head.Children.Add(titleRow);

        head.Children.Add(new TextBlock
        {
            Text = "上班主任课的是班主任，其余为科任；初始密码 123（只存哈希），已有账号不覆盖。",
            FontSize = 12.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        // ================== ② 规则 + 汇总 卡 ==================
        // 规则与统计放同一张卡：都是"当前这套口径的结果"，分开摆会多出两条分隔线。
        var ruleCard = Card();
        DockPanel.SetDock(ruleCard, Dock.Top);
        root.Children.Add(ruleCard);

        var ruleStack = new StackPanel();
        ruleCard.Child = ruleStack;

        var rule = new Grid();
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = "班主任科目",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(lbl, 0);
        rule.Children.Add(lbl);

        // 可输入 + 可选：右边自带下拉箭头（WPF 默认模板就有），
        // 下拉候选 = 底表实际科目名，见 BuildPlan()
        _headCombo.IsEditable = true;
        _headCombo.Width = 178;
        _headCombo.FontSize = 13.5;
        _headCombo.Padding = new Thickness(6, 4, 6, 4);
        _headCombo.VerticalContentAlignment = VerticalAlignment.Center;
        _headCombo.Text = TeacherAccountGenerator.DefaultHeadSubject;
        _headCombo.ToolTip = "学校用来认定班主任的那门课。点右侧小三角可直接选底表里的科目名，也可以自己输入。\n"
                           + "多个科目名用逗号分隔，例如：班会，团队活动\n"
                           + "必须与底表里的科目名一致（默认精确匹配；「xx-单 / xx-双」也能认）。";
        Grid.SetColumn(_headCombo, 1);
        rule.Children.Add(_headCombo);

        var btnPreview = new Button
        {
            Content = "重新预览",
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        btnPreview.Background = HexBrush(ChromePalette.HexOf(ChromePalette.PrimaryLight, "#E9EFF9"));
        btnPreview.Click += (_, _) => { LoadSchedule(); BuildPlan(); RefreshList(SelectedNames()); };
        Grid.SetColumn(btnPreview, 2);
        rule.Children.Add(btnPreview);

        // 底表科目名：数据不是说明，留在界面上，但压成一行省略号 + ToolTip 看全
        _subjectHint.FontSize = 11.5;
        _subjectHint.Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
        _subjectHint.TextTrimming = TextTrimming.CharacterEllipsis;
        _subjectHint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_subjectHint, 3);
        rule.Children.Add(_subjectHint);

        ruleStack.Children.Add(rule);

        ruleStack.Children.Add(new Border
        {
            Height = 1,
            Background = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
            Margin = new Thickness(0, 9, 0, 8)
        });

        _stats.FontSize = 13;
        _stats.FontWeight = FontWeights.SemiBold;
        _stats.TextWrapping = TextWrapping.Wrap;
        ruleStack.Children.Add(_stats);

        _warn.FontSize = 12.5;
        _warn.Foreground = HexBrush("#B45309");
        _warn.TextWrapping = TextWrapping.Wrap;
        _warn.Margin = new Thickness(0, 5, 0, 0);
        _warn.Visibility = Visibility.Collapsed;
        ruleStack.Children.Add(_warn);

        // ================== ③ 筛选 + 批量 卡 ==================
        var toolCard = Card();
        DockPanel.SetDock(toolCard, Dock.Top);
        root.Children.Add(toolCard);

        var toolStack = new StackPanel();
        toolCard.Child = toolStack;

        // --- 第一行：搜索 / 按科目 / 已选 ---
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        row1.Children.Add(Label("搜索"));
        _search.Width = 190;
        _search.FontSize = 13;
        _search.Padding = new Thickness(7, 5, 7, 5);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = "按姓名、班级、科目、角色过滤（输入「班」只看班主任，输入「八(3)」只看这个班）。\n按 Esc 清空。";
        _search.TextChanged += (_, _) =>
        {
            string kw = (_search.Text ?? "").Trim();
            if (string.Equals(kw, _keyword, StringComparison.Ordinal)) return;
            _keyword = kw;
            RefreshList(SelectedNames());
        };
        row1.Children.Add(_search);

        row1.Children.Add(Label("按科目", 16));
        _groupCombo.Width = 208;
        _groupCombo.FontSize = 13;
        _groupCombo.Padding = new Thickness(6, 4, 6, 4);
        _groupCombo.VerticalContentAlignment = VerticalAlignment.Center;
        _groupCombo.ToolTip = "选中某一科 → 列表只留这批人并自动全选，接着用下面的「设为该科目 / 设为不限」整批改。\n选「全部科目」回到完整名单。";
        _groupCombo.SelectionChanged += (_, _) => OnGroupChanged();
        row1.Children.Add(_groupCombo);

        _selInfo.FontSize = 12.5;
        _selInfo.Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
        _selInfo.VerticalAlignment = VerticalAlignment.Center;
        _selInfo.Margin = new Thickness(16, 0, 0, 0);
        _selInfo.Text = "已选 0 人";
        row1.Children.Add(_selInfo);

        toolStack.Children.Add(row1);

        // 原先这里还有一行「把选中的设为 …」的批量工具。r156 把它移到底部操作行，
        // 顺序改成「先选（列表）→ 再设置（批量）→ 最后生成」，跟人做事的次序一致。

        // ================== ④ 表头（可点击排序）==================
        _header = MakeHeader();
        DockPanel.SetDock(_header, Dock.Top);
        root.Children.Add(_header);

        // ================== ⑤ 底部：左边批量设置，右边生成 / 取消 ==================
        // 一行到底：读表 → 选人（上面列表）→ 改这批人的科目（左）→ 生成（右下）。
        // 生成与取消固定在右下角，跟"确认对话框"的肌肉记忆一致。
        var bar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 0 说明
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 1 科目框
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 2 三个按钮
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 3 提示
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 4 生成账号
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 5 取消

        _bulkLabel.FontSize = 12.5;
        _bulkLabel.Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
        _bulkLabel.VerticalAlignment = VerticalAlignment.Center;
        _bulkLabel.Margin = new Thickness(0, 0, 6, 0);
        _bulkLabel.Text = "把选中的设为";
        Grid.SetColumn(_bulkLabel, 0);
        bar.Children.Add(_bulkLabel);

        _bulkSubject.IsEditable = true;
        _bulkSubject.Width = 132;
        _bulkSubject.FontSize = 13;
        _bulkSubject.Padding = new Thickness(6, 4, 6, 4);
        _bulkSubject.VerticalContentAlignment = VerticalAlignment.Center;
        _bulkSubject.ToolTip = "要批量设成哪门课。可直接输入底表没有的科目名，也可点右侧小三角选。\n"
                             + "★ 选中若干人后，他们任教的科目会排在最前面。";
        Grid.SetColumn(_bulkSubject, 1);
        bar.Children.Add(_bulkSubject);
        _bulkTools.Add(_bulkSubject);

        var bulkButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bulkButtons, 2);
        bar.Children.Add(bulkButtons);

        var btnSetSubject = MakeTool("设为该科目", "#E9EFF9", () =>
        {
            string s = BoardIds.NormalizeSubject(_bulkSubject.Text);
            if (s.Length == 0)
            {
                Tip("先在左边「科目」框里填一个科目名（例如 语文），再点「设为该科目」。\n"
                    + "如果是想取消科目限制，请点「设为不限」。", MessageBoxImage.Information);
                return;
            }
            ApplyBulk(s);
        }, 8);
        bulkButtons.Children.Add(btnSetSubject);

        var btnNoLimit = MakeTool("设为不限", null, () => ApplyBulk(""));
        bulkButtons.Children.Add(btnNoLimit);

        var btnRestore = MakeTool("恢复自动", null, () => ApplyBulk(null));
        bulkButtons.Children.Add(btnRestore);

        _bulkTools.Add(btnSetSubject);
        _bulkTools.Add(btnNoLimit);
        _bulkTools.Add(btnRestore);

        var barHint = new TextBlock
        {
            Text = "确认后才写盘；改完记得发布数据包，教室机才会收到这份用户表。",
            FontSize = 11.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(barHint, 3);
        bar.Children.Add(barHint);

        var btnOk = new Button
        {
            Content = "生成账号",
            Padding = new Thickness(20, 8, 20, 8),
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnOk.Background = HexBrush("#E8F6EE");
        btnOk.Click += (_, _) => Commit();
        Grid.SetColumn(btnOk, 4);
        bar.Children.Add(btnOk);

        var btnCancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(20, 8, 20, 8),
            FontSize = 14
        };
        btnCancel.Click += (_, _) => Close();
        Grid.SetColumn(btnCancel, 5);
        bar.Children.Add(btnCancel);

        // ================== ⑥ 列表（填充剩余空间）==================
        _list.FontSize = 13;
        _list.BorderThickness = new Thickness(0, 1, 0, 0);
        _list.BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE");
        _list.Padding = new Thickness(0, 4, 0, 0);
        _list.Background = System.Windows.Media.Brushes.Transparent;
        _list.SelectionMode = SelectionMode.Extended;
        _list.SelectionChanged += (_, _) => { if (!_suppressSel) UpdateSelInfo(); };
        _list.ContextMenu = MakeContextMenu();
        root.Children.Add(_list);

        // ================== 事件 ==================
        // 回车直接重新预览；下拉展开时回车是"选中高亮项"，不能抢
        _headCombo.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !_headCombo.IsDropDownOpen)
            {
                BuildPlan(); RefreshList(SelectedNames());
                e.Handled = true;
            }
        };

        // 从下拉里挑了一门课 = 就是它了：当场重算，不必再点「重新预览」。
        // ★ 先把选中项写回 Text 再算 —— SelectionChanged 里 ComboBox.Text 还可能是旧值。
        _headCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressHead) return;
            if (_headCombo.SelectedItem is not string s || s.Length == 0) return;

            _suppressHead = true;
            try { _headCombo.Text = s; }
            finally { _suppressHead = false; }

            BuildPlan();
            RefreshList(SelectedNames());
        };

        // 搜索框里按 Esc 清空过滤，比全选删掉更顺手
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _search.Text = "";
                e.Handled = true;
            }
        };

        Loaded += (_, _) =>
        {
            try { _overrides = Store.LoadTeacherSubjects(); }
            catch { _overrides = new Dictionary<string, string>(StringComparer.Ordinal); }

            LoadSchedule();
            BuildPlan();
            RefreshList();
        };

        GlassChrome.Prepare(this);
    }

    // ================== 构建小工具 ==================

    /// <summary>白底圆角小卡：工具区两块共用同一份外观。</summary>
    static Border Card() => new()
    {
        Background = ChromePalette.BrushOf("CardBrush", "#FFFFFF"),
        BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, 8)
    };

    static TextBlock Label(string text, double left = 0) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 6, 0)
    };

    Button MakeTool(string text, string? bg, Action onClick, double left = 0)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 12.5,
            Margin = new Thickness(left, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (bg != null) b.Background = HexBrush(bg);
        b.Click += (_, _) => onClick();
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
                Cursor = Cursors.Hand,
                ToolTip = "按「" + col.Title + "」排序"
            };
            b.Click += (_, _) =>
            {
                if (_sort == col.Sort) _sortDesc = !_sortDesc;
                else { _sort = col.Sort; _sortDesc = false; }
                UpdateHeaderText();
                RefreshList(SelectedNames());
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

    ContextMenu MakeContextMenu()
    {
        var cm = new ContextMenu();

        var miAll = new MenuItem { Header = "选中当前筛选结果" };
        miAll.Click += (_, _) => { foreach (ListBoxItem it in _list.Items) it.IsSelected = true; };
        cm.Items.Add(miAll);

        var miNone = new MenuItem { Header = "清空选择" };
        miNone.Click += (_, _) => { _list.UnselectAll(); UpdateSelInfo(); };
        cm.Items.Add(miNone);

        var miShowAll = new MenuItem { Header = "显示全部科目" };
        miShowAll.Click += (_, _) => { if (_groupCombo.Items.Count > 0) _groupCombo.SelectedIndex = 0; };
        cm.Items.Add(new Separator());
        cm.Items.Add(miShowAll);

        var miAuto = new MenuItem { Header = "把选中的恢复成课表自动判定" };
        miAuto.Click += (_, _) => ApplyBulk(null);
        cm.Items.Add(new Separator());
        cm.Items.Add(miAuto);

        return cm;
    }

    /// <summary>读课表（只在打开窗口 / 点「重新预览」时读盘）。</summary>
    void LoadSchedule()
    {
        try { _schedule = Store.LoadSchedule(); }
        catch (Exception ex)
        {
            _schedule = null;
            _stats.Text = "读取课表失败：" + ex.Message;
        }
    }

    /// <summary>按当前规则 + 覆盖表算计划（不读盘、不落盘）。</summary>
    void BuildPlan()
    {
        string head = (_headCombo.Text ?? "").Trim();
        _plan = TeacherAccountGenerator.Build(_schedule, _book, head, _overrides);

        // 底表实际科目名：只读显示，省得人凭记忆写错"团队活动"
        // ★ 分隔符用「、」不用「 · 」—— 科目名本身可能带「·」（如「书法·单」），会看串行
        var names = _plan.SubjectsInSchedule;
        _subjectHint.Text = names.Count == 0
            ? "底表科目名：（课表里没读到科目）"
            : "底表科目名：" + string.Join("、", names);
        _subjectHint.ToolTip = _subjectHint.Text;

        // 「班主任科目」下拉候选 = 底表里真实出现的科目名（归一化去重、中文序）。
        // ★ 只在候选变了才重灌：一换 ItemsSource，编辑框里的光标和选区就没了。
        var options = names
            .Select(BoardIds.NormalizeSubject)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, Zh)
            .ToList();
        string key = string.Join("\u0001", options);
        if (!string.Equals(key, _headChoicesKey, StringComparison.Ordinal))
        {
            _headChoicesKey = key;
            _suppressHead = true;
            try
            {
                _headCombo.ItemsSource = options;
                // 重灌之后把当前填的值放回编辑框（它可能不在候选里，例如默认的「班会」）
                _headCombo.Text = head;
            }
            finally { _suppressHead = false; }
        }

        _stats.Text = _plan.Summary;

        if (_plan.Warnings.Count == 0)
        {
            _warn.Visibility = Visibility.Collapsed;
            _warn.Text = "";
        }
        else
        {
            _warn.Visibility = Visibility.Visible;
            _warn.Text = "注意：" + string.Join("\n　　　", _plan.Warnings.Take(6))
                         + (_plan.Warnings.Count > 6 ? $"\n　　　…另有 {_plan.Warnings.Count - 6} 条" : "");
        }

        RefreshGroups();
        RebuildBulkChoices();
    }

    /// <summary>下拉里的一条「科目分组」。</summary>
    sealed class GroupEntry
    {
        public string? Key;
        public string Caption = "";
        public override string ToString() => Caption;
    }

    /// <summary>
    /// 按「当前生效科目」把老师分组，灌进下拉。
    /// <para>为什么按生效科目而不是按课表算的科目：改完一批之后分组要跟着变 ——
    /// 语文 46 人改成历史，语文组就该消失、历史组变多，否则看不出刚才那批到底改到哪去了。</para>
    /// <para>★ 组数按人数降序，「不限」固定排最后；第一项恒为「全部科目」。</para>
    /// </summary>
    void RefreshGroups()
    {
        var groups = _plan.Items
            .GroupBy(d => (d.Subject ?? "").Trim(), StringComparer.Ordinal)
            .Select(g => new { Key = g.Key, Count = g.Count(), Manual = g.Any(d => d.SubjectManual) })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Key.Length == 0 ? "\uffff" : g.Key, Zh)
            .ToList();

        // 当前筛的这门课已经没人了（刚好被整批改走）→ 退回全部，别让人对着空列表发愣
        if (_groupFilter != null && !groups.Any(g => string.Equals(g.Key, _groupFilter, StringComparison.Ordinal)))
            _groupFilter = null;

        var entries = new List<GroupEntry> { new() { Key = null, Caption = $"全部科目（{_plan.Items.Count} 人）" } };
        foreach (var g in groups)
        {
            entries.Add(new GroupEntry
            {
                Key = g.Key,
                Caption = GroupCaption(g.Key) + $"（{g.Count} 人）" + (g.Manual ? " *" : "")
            });
        }

        _suppressGroup = true;
        try
        {
            _groupCombo.ItemsSource = entries;
            int idx = 0;
            if (_groupFilter != null)
            {
                int found = entries.FindIndex(e => string.Equals(e.Key ?? "", _groupFilter, StringComparison.Ordinal));
                if (found > 0) idx = found;
            }
            _groupCombo.SelectedIndex = idx;
        }
        finally { _suppressGroup = false; }

        _groupCombo.IsEnabled = _plan.Items.Count > 0;
    }

    static string GroupCaption(string key) => key.Length == 0 ? "不限" : key;

    /// <summary>
    /// 选组 = 筛选 + 全选（选回「全部科目」= 只取消筛选）。
    /// <para>不做"选一下就改到那门课"：283 个账号误操作代价太大，
    /// 先让人看见这批是谁、再点批量按钮，中间有一次核对的机会。</para>
    /// </summary>
    void OnGroupChanged()
    {
        if (_suppressGroup) return;

        var entry = _groupCombo.SelectedItem as GroupEntry;
        string? key = entry?.Key;
        if (string.Equals(_groupFilter, key, StringComparison.Ordinal)) return;

        _groupFilter = key;

        if (key == null)
        {
            RefreshList(SelectedNames());
            return;
        }

        RefreshList(null);
        SelectAllShown();
    }

    void SelectAllShown()
    {
        _suppressSel = true;
        try { foreach (ListBoxItem it in _list.Items) it.IsSelected = true; }
        finally { _suppressSel = false; }
        UpdateSelInfo();
    }

    // ================== 列表 ==================

    /// <summary>按搜索词过滤 + 按当前列排序，重建列表行（保留 <paramref name="keepSelected"/> 里的选中）。</summary>
    void RefreshList(IEnumerable<string>? keepSelected = null)
    {
        var keep = keepSelected == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(keepSelected, StringComparer.Ordinal);

        var rows = Filter(_plan.Items);
        rows.Sort(Compare);

        _suppressSel = true;
        try
        {
            _list.Items.Clear();
            foreach (var d in rows)
            {
                var row = MakeRow(
                    new[] { d.Username, d.RoleCaption, d.ClassCaption, d.HeadCaption, d.SubjectCaption, d.ActionCaption },
                    ColWidths);

                var tip = $"{d.Username}（{d.RoleCaption}）\n任教班：{d.ClassCaption}\n"
                        + $"班主任班：{d.HeadCaption}\n科目：{d.SubjectCaption} —— {d.SubjectSourceCaption}\n"
                        + $"处理：{d.ActionCaption}";

                var item = new ListBoxItem
                {
                    Content = row,
                    Tag = d,
                    Style = RowStyle(),
                    ToolTip = tip,
                    IsSelected = keep.Contains(d.Username)
                };

                // 跳过项标灰，一眼看出"这些不会被写"
                if (d.Action == TeacherAccountAction.SkipExisting || d.Action == TeacherAccountAction.SkipManual)
                    item.Opacity = 0.5;

                _list.Items.Add(item);
            }
        }
        finally { _suppressSel = false; }

        UpdateSelInfo();
    }

    /// <summary>
    /// 科目筛选 + 关键字搜索（可叠加：先选语文组、再搜「八(3)」= 语文组里带八(3)的）。
    /// </summary>
    List<TeacherAccountDraft> Filter(IEnumerable<TeacherAccountDraft> src)
    {
        IEnumerable<TeacherAccountDraft> q = src;

        if (_groupFilter != null)
            q = q.Where(d => string.Equals((d.Subject ?? "").Trim(), _groupFilter, StringComparison.Ordinal));

        if (_keyword.Length > 0)
            q = q.Where(d =>
                Contains(d.Username) || Contains(d.RoleCaption) || Contains(d.ClassCaption)
                || Contains(d.HeadCaption) || Contains(d.SubjectCaption) || Contains(d.ActionCaption));

        return q.ToList();

        bool Contains(string? s)
            => !string.IsNullOrEmpty(s) && s.Contains(_keyword, StringComparison.OrdinalIgnoreCase);
    }

    int Compare(TeacherAccountDraft a, TeacherAccountDraft b)
    {
        int r = _sort switch
        {
            SortField.Username => Zh.Compare(a.Username, b.Username),
            SortField.Role => RoleRank(a.Role).CompareTo(RoleRank(b.Role)),
            SortField.ClassCount => a.ClassNames.Count.CompareTo(b.ClassNames.Count),
            SortField.Head => Zh.Compare(a.HeadCaption, b.HeadCaption),
            SortField.Subject => Zh.Compare(a.SubjectCaption, b.SubjectCaption),
            _ => ActionRank(a.Action).CompareTo(ActionRank(b.Action))
        };

        if (_sortDesc) r = -r;
        if (r == 0) r = Zh.Compare(a.Username, b.Username);   // 主键相同按姓名，保证顺序稳定
        return r;
    }

    static int ActionRank(TeacherAccountAction a) => a switch
    {
        TeacherAccountAction.Create => 0,
        TeacherAccountAction.Update => 1,
        TeacherAccountAction.Unchanged => 2,
        TeacherAccountAction.SkipExisting => 3,
        _ => 4
    };

    static int RoleRank(string role) => role switch
    {
        UserRole.Teacher => 0,
        UserRole.SubjectTeacher => 1,
        _ => 2
    };

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
                Margin = new Thickness(0, 0, 8, 0),   // 列之间留点气，长班名不会贴到下一列
                FontSize = bold ? 12.5 : 13
            };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (dim) t.Foreground = System.Windows.Media.Brushes.Gray;
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    /// <summary>列表行样式（选中 / 悬停底色），与用户管理窗共用 <see cref="ListRowStyle"/>。</summary>
    static Style RowStyle() => ListRowStyle.Get();

    static System.Windows.Media.Brush HexBrush(string hex) => ListRowStyle.HexBrush(hex);

    // ================== 选中 / 批量 ==================

    List<TeacherAccountDraft> SelectedDrafts()
        => _list.SelectedItems.OfType<ListBoxItem>()
                .Select(i => i.Tag as TeacherAccountDraft)
                .Where(d => d != null)
                .Select(d => d!)
                .ToList();

    List<string> SelectedNames() => SelectedDrafts().Select(d => d.Username).ToList();

    void UpdateSelInfo()
    {
        int n = _list.SelectedItems.Count;
        _selInfo.Text = $"已选 {n} 人｜显示 {_list.Items.Count}/{_plan.Items.Count}";
        _selInfo.Foreground = n > 0
            ? HexBrush(ChromePalette.HexOf(ChromePalette.Primary, "#1E4FA3"))
            : ChromePalette.BrushOf(ChromePalette.Muted, "#667085");

        _bulkLabel.Text = n > 0 ? $"把选中的 {n} 人设为" : "把选中的设为";

        // 没人选中时批量那排置灰：填了科目才发现没选人，提示一次就够糟了
        foreach (var c in _bulkTools) c.IsEnabled = n > 0;

        RebuildBulkChoices();
    }

    /// <summary>
    /// 重排批量科目下拉：<b>选中的这批人教的科目排到最前</b>（按命中人数降序），其余按中文序跟在后面。
    /// <para>★ 只在框里还是"上次程序填的那个值"或空的时候才改写它 —— 用户自己敲进去的不覆盖。</para>
    /// </summary>
    void RebuildBulkChoices()
    {
        var picked = SelectedDrafts();

        var mine = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in picked)
        {
            foreach (var s in d.TaughtSubjects)
            {
                string key = (s ?? "").Trim();
                if (key.Length == 0) continue;
                mine.TryGetValue(key, out int c);
                mine[key] = c + 1;
            }
        }

        // 底表科目（归一化后去重），已经排在前面剔除
        var rest = _plan.SubjectsInSchedule
            .Select(BoardIds.NormalizeSubject)
            .Where(s => s.Length > 0 && !mine.ContainsKey(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, Zh)
            .ToList();

        var ordered = mine.OrderByDescending(kv => kv.Value)
                          .ThenBy(kv => kv.Key, Zh)
                          .Select(kv => kv.Key)
                          .Concat(rest)
                          .ToList();

        string cur = (_bulkSubject.Text ?? "").Trim();
        bool ours = cur.Length == 0 || string.Equals(cur, _bulkAuto, StringComparison.Ordinal);

        _bulkSubject.ItemsSource = ordered;

        if (picked.Count > 0 && ours && ordered.Count > 0)
        {
            _bulkSubject.Text = ordered[0];
            _bulkAuto = ordered[0];
        }
        else
        {
            _bulkSubject.Text = cur;
        }
    }

    /// <summary>
    /// 把选中的人设成同一门课。<paramref name="subject"/> 为 <c>null</c> → 删掉覆盖、恢复自动判定；
    /// 空串 → 显式"不限"。
    /// </summary>
    void ApplyBulk(string? subject)
    {
        var picked = SelectedNames();
        if (picked.Count == 0)
        {
            Tip("先在列表里选中要改的老师：\n"
                + "· 用「按科目」下拉选一科，会自动筛出并全选这批人；\n"
                + "· 或按住 Ctrl 一个个点、按住 Shift 连选一段；\n"
                + "· 也可以先在上方搜索框输入关键字，再右键「选中当前筛选结果」。\n\n"
                + "选好之后再来点批量按钮。", MessageBoxImage.Information);
            return;
        }

        foreach (var name in picked)
        {
            if (subject == null) _overrides.Remove(name);
            else _overrides[name] = subject;
        }

        try { Store.SaveTeacherSubjects(_overrides); }
        catch (Exception ex)
        {
            Tip("改动没能记住（下次打开会丢）：" + ex.Message, MessageBoxImage.Warning);
            return;
        }

        BuildPlan();
        RefreshList(picked);   // 保留选中，方便连续改下一批

        string what = subject == null ? "恢复为课表自动判定"
                    : subject.Length == 0 ? "设为「不限」"
                    : "设为「" + subject + "」";
        _selInfo.Text = $"{picked.Count} 人已{what}｜显示 {_list.Items.Count}/{_plan.Items.Count}";
    }

    // ================== 提交 ==================

    void Commit()
    {
        if (_plan.Items.Count == 0)
        {
            Tip("没有可生成的账号。请先导入全校总课表。", MessageBoxImage.Information);
            return;
        }

        if (_plan.HeadHitClasses == 0)
        {
            if (System.Windows.MessageBox.Show(
                    $"没有任何班级命中「{string.Join("、", _plan.HeadSubjects)}」，"
                    + "这样生成出来的账号全都是科任老师，没有班主任。\n\n仍然继续吗？",
                    "没有识别到班主任", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        if (_plan.ChangeCount == 0)
        {
            Tip("没有需要新增或更新的账号（已全部是最新，或同名账号都是手工建的）。", MessageBoxImage.Information);
            return;
        }

        string msg = $"将新增 {_plan.Count(TeacherAccountAction.Create)} 个、"
                   + $"更新 {_plan.Count(TeacherAccountAction.Update)} 个教师账号。\n\n"
                   + $"· 初始密码统一为 {TeacherAccountGenerator.DefaultPassword}（只存哈希，不存明文）\n"
                   + "· 已有账号不会被覆盖，手工改过的也不会被刷新\n"
                   + (_plan.OverrideApplied > 0
                        ? $"· 其中 {_plan.OverrideApplied} 个账号的科目是你手工指定的（已记在本机，下次沿用）\n"
                        : "")
                   + "· 改完记得发布数据包，教室机才拿得到这份用户表\n\n确定继续吗？";

        if (System.Windows.MessageBox.Show(msg, "生成教师账号",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        int n;
        try { n = TeacherAccountGenerator.Apply(_plan, _book); }
        catch (Exception ex) { Tip("生成失败：" + ex.Message, MessageBoxImage.Error); return; }

        try { Store.SaveUsers(_book); }
        catch (Exception ex) { Tip("账号已生成，但保存失败：" + ex.Message, MessageBoxImage.Error); return; }

        Saved = true;
        Tip($"已写入 {n} 个账号。\n\n初始密码 {TeacherAccountGenerator.DefaultPassword}；"
            + "班主任只能改自己当班主任的班（任教的其他班只能发作业）。\n\n"
            + "下一步：回到主界面发布数据包，教室机才会收到这份用户表。", MessageBoxImage.Information);

        DialogResult = true;
        Close();
    }

    void Tip(string msg, MessageBoxImage icon)
        => System.Windows.MessageBox.Show(msg, "从课表生成教师账号", MessageBoxButton.OK, icon);
}
