using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Wpf = System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 班级内容同步「信息管理器」：同一窗口按身份切换两种视图。
/// <para>教师机 = 发送箱 + 逐条核对（可撤回）；教室机 = 收件箱 + 电教管理员单人裁决。</para>
/// <para>★ 只做「人点确认」，机器按条目 Id 应用；绝不整表覆盖教室机本地改动。</para>
/// <para>★ 详情 / 回执按「变更单 → 教室机回执 → 技术信息」分区排版，
/// 字段用中文标签两列表格，不再把 subject/content/一堆哈希挤成一长串。</para>
/// </summary>
public class BoardSyncWindow : Window
{
    readonly Wpf.ListBox _list = new();
    readonly Wpf.StackPanel _detailPanel = new();
    readonly Wpf.TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Wpf.TextBlock _snapshotBar = new() { TextWrapping = TextWrapping.Wrap };
    // 操作条用 WrapPanel：多出「撤回还原」后按钮变四个，窄窗口下一行放不下会挤出右边缘，
    // 换行比缩小按钮更好读（这些按钮都是要人看清楚再点的）。
    readonly Wpf.WrapPanel _decisionBar = new()
    {
        Orientation = Wpf.Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center
    };
    readonly Wpf.ComboBox _view = new();
    readonly Wpf.TextBlock _configHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    // r147：待办概览条——双击托盘进来时一眼看到「哪几类、各几条」，点一下直接筛到那一类
    readonly Wpf.TextBlock _todoBar = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    Wpf.Button[]? _todoButtons;
    // 设置项全部集中到控制面板「信息管理」页，这里只保留发布入口，界面不再堆一排输入框。
    readonly Wpf.Button _send = new() { Content = "＋ 发布信息", Padding = new Thickness(12, 5, 12, 5) };

    // ---------------- r167：列表筛选改成两个下拉 ----------------
    // 原来是「只看我发的」+「显示全部」两个勾选框。问题在教室机上：「只看我发的」被整个藏掉，
    // 那一行只剩一个「显示全部」，看起来像根本筛不了东西。现在拆成两个维度、都用下拉：
    //   查什么（谁发的 / 状态）  ×  查多久以前（保留期 / 全部时段）
    // 两个维度独立，互不吞掉对方 —— 比如「只看未处理 + 全部时段」这种组合以前做不出来。

    /// <summary>筛选键：全部记录。</summary>
    const string FAll = "all";
    /// <summary>筛选键：只看我发的（仅教师机发送箱）。</summary>
    const string FMine = "mine";
    /// <summary>筛选键：待裁决 / 待核对 / 待重试。</summary>
    const string FPending = "pending";
    /// <summary>筛选键：已应用 / 已核对 / 已忽略 / 已过期。</summary>
    const string FDone = "done";
    /// <summary>筛选键：有冲突。</summary>
    const string FConflict = "conflict";
    /// <summary>筛选键：撤回记录。</summary>
    const string FRevoke = "revoke";

    readonly Wpf.ComboBox _filter = new()
    {
        Width = 200, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "按「谁发的 / 办到哪一步」筛；「全部时段」在右边那个下拉里"
    };
    /// <summary>时间范围（原「显示全部」勾选框）。</summary>
    readonly Wpf.ComboBox _range = new()
    {
        Width = 170, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        ToolTip = "默认只列最近 N 天的记录（N 在「信息管理 → ③ 同步设置」里改）；"
                + "更早的记录选「全部时段」再翻，未处理的不受保留期限制"
    };
    /// <summary>筛选结果提示：条数 + 被时间 / 条件各筛掉多少，免得以为丢单。</summary>
    readonly Wpf.TextBlock _filterHint = new()
    {
        FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 10, 0),
        // r169：筛选条改成「左下拉 + 右清除按钮」后，中间这段只剩几百像素 ——
        // 长了就省略号 + 悬停看全文，别硬裁半截字。
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = new SolidColorBrush(Muted)
    };
    /// <summary>代码在重建下拉项 / 同步选中项，别把 SelectionChanged 当成用户操作。</summary>
    bool _filterSync, _rangeSync;

    /// <summary>「清除台账…」：唯一会真的删记录的地方，只对教师 / 管理员开放。</summary>
    readonly Wpf.Button _clear = new()
    {
        Content = "🧹 清除台账…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 10, 0)
    };

    /// <summary>
    /// 「清除筛选结果」：只清列表里现在这些（视图 + 筛选条件 + 时间范围三者叠加后的结果）。
    /// <para>★ 为什么需要它：原来的「清除台账…」只有「全部清空 / 只清已终结」两个选项，
    /// 想只清某个班某段时间只能清全部再补救，实际没人敢点。</para>
    /// <para>★ 未处理的收件一律保留（<see cref="BoardSyncService.ClearLedgerRecords"/> 里兜底）：
    /// 那是教室机还没裁决的变更单，清了教师机仍显示「已发出」，本机却不再提醒 —— 等于静默丢消息。</para>
    /// </summary>
    readonly Wpf.Button _purge = new()
    {
        Content = "清除筛选结果", Padding = new Thickness(10, 4, 10, 4), FontSize = 12,
        ToolTip = "把列表里现在这些记录从本机台账删掉（清前自动备份）；未处理的变更单会保留，不会被误清"
    };

    /// <summary>是否「全部时段」（不受保留期限制）。由右边那个下拉驱动。</summary>
    bool _showAll;

    /// <summary>
    /// 当前筛选键（<see cref="FAll"/> / <see cref="FMine"/> / …）。
    /// <para>★ 这是「查什么」的唯一真源，Refresh 只认它；<see cref="_mineOnly"/> 是镜像。</para>
    /// <para>r169：教室机默认「只看未处理」—— 电教管理员打开台账就是来裁决的，
    /// 一排已核对 / 已过期的旧记录挡在前面，每天都要先手动切一次。</para>
    /// </summary>
    string _filterKey = DefaultFilterKey();

    static string DefaultFilterKey()
    {
        try
        {
            if (BoardSyncService.IsClassroomMachine) return FPending;
        }
        catch { }
        return PendingTodoService.MineOnlyDefault ? FMine : FAll;
    }

    /// <summary>
    /// 「只看我发的」镜像（r158 起就在，待办条与诊断读它）。默认值直接取今日卡 / 托盘角标同一口径，
    /// 老师打开台账看到的条数与角标一致；教室机上这个筛选无意义（收件箱看的是本班全部到件）。
    /// <para>★ 只由筛选下拉的 SelectionChanged 和 <see cref="RebuildFilterItems"/> 写，别处一律只读。</para>
    /// </summary>
    bool _mineOnly = PendingTodoService.MineOnlyDefault;

    /// <summary>上一次刷新时被「只看我发的」筛掉的条数，只用于状态提示。</summary>
    int _mineHidden;

    List<BoardMessageRecord> _records = new();

    static readonly Color Ink = Color.FromRgb(0x33, 0x41, 0x55);
    static readonly Color Muted = Color.FromRgb(0x77, 0x88, 0x99);
    static readonly Color Blue = Color.FromRgb(0x1F, 0x55, 0xA8);
    static readonly Color Green = Color.FromRgb(0x1F, 0x66, 0x3C);
    static readonly Color Amber = Color.FromRgb(0xB0, 0x6A, 0x20);

    public BoardSyncWindow()
    {
        Title = "📨 信息管理 · 消息台账与回执";
        Width = 1040;
        Height = 700;
        MinWidth = 860;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xF9));

        BuildUi();
        LoadHints();
        Refresh();
    
        // 台账窗口开着时所有通知都能看到，不再朗读：打开（真正显示）时进入抑制，
        // 关闭时退出。用 Loaded 而不是构造函数 —— 诊断里大量 new 窗口只为读字段，不该静音。
        Loaded += (_, _) =>
        {
            VoiceSpeaker.EnterSuppression();
            // 教师机定时轮询被下发关掉后，打开台账是看回执的主要入口，补拉一次。
            // 开关还开着时不补：定时轮询已在跑，别让每次开窗都多一次 FTP 登录。
            var s = Store.LoadSettings();
            if (s.BoardSyncEnabled && !BoardSyncService.IsClassroomMachine && !s.BoardSyncTeacherPolling)
                DoPull();
        };
        Closed += (_, _) => VoiceSpeaker.ExitSuppression();
        GlassChrome.Prepare(this);}

    void BuildUi()
    {
        var grid = new Wpf.Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 0 顶栏
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 1 设置条
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 2 r147 待办分组条
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 3 r158 列表筛选条
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 4 快照提示
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 5 主体
        grid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 6 状态栏
        Content = grid;

        // ---- 顶栏 ----
        var head = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Wpf.TextBlock
        {
            Text = "消息台账", FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x55)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0)
        });
        _view.Items.Add(new Wpf.ComboBoxItem { Content = "📥 收件箱（待处理）", Tag = "in" });
        _view.Items.Add(new Wpf.ComboBoxItem { Content = "📤 发送箱（逐条核对）", Tag = "out" });
        _view.SelectedIndex = UserSession.IsStudent ? 0 : 1;
        _view.Width = 190;
        _view.Margin = new Thickness(0, 0, 10, 0);
        _view.SelectionChanged += (_, _) => { RebuildFilterItems(); Refresh(); };
        head.Children.Add(_view);

        var btnRefresh = new Wpf.Button { Content = "↻ 立即检查", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 0) };
        btnRefresh.Click += (_, _) => DoPull();
        head.Children.Add(btnRefresh);
        var btnSnap = new Wpf.Button { Content = "👀 教室机当前内容", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 0) };
        btnSnap.Click += (_, _) => ShowSnapshot();
        head.Children.Add(btnSnap);
        _send.Click += (_, _) => DoSend();
        head.Children.Add(_send);

        // r167：筛选条的两个下拉在下面 filters 那一段接事件（这里是旧的两个勾选框，已删）。
        _clear.Click += (_, _) => ClearLedgerNow();
        head.Children.Add(_clear);

        var btnClose = new Wpf.Button { Content = "关闭", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0) };
        btnClose.Click += (_, _) => Close();
        head.Children.Add(btnClose);
        Wpf.Grid.SetRow(head, 0);
        grid.Children.Add(head);

        // ---- 状态条（设置项统一在控制面板「信息管理」页，这里只显示当前配置摘要） ----
        var cfg = new Wpf.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xE4, 0xF7)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 8),
            Child = _configHint
        };
        _configHint.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x55, 0x88));
        Wpf.Grid.SetRow(cfg, 1);
        grid.Children.Add(cfg);

        // r147：待办分组条。三个按钮按「身份相关」只显示有用的那类：
        // 教室机 → 待裁决 + 冲突；教师机 → 待核对 + 冲突。点了切到对应视图并置顶未办项。
        var todoWrap = new Wpf.WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        todoWrap.Children.Add(_todoBar);
        var btnJudge = new Wpf.Button
        {
            Content = "待裁决 →", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "切到收件箱并选中第一条待裁决的变更单"
        };
        btnJudge.Click += (_, _) => JumpToTodo("in", r =>
            !r.Order.IsRevoke && string.IsNullOrWhiteSpace(r.Decision) && r.Status != BoardSyncConsts.StConflict);
        var btnVerify = new Wpf.Button
        {
            Content = "待核对 →", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "切到发送箱并选中第一条待核对的信息"
        };
        btnVerify.Click += (_, _) => JumpToTodo("out", r =>
            !r.Order.IsRevoke && !r.IsTerminal && r.Status != BoardSyncConsts.StConflict);
        var btnConflict = new Wpf.Button
        {
            Content = "冲突 →", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "切到冲突项：核对不一致 / 缺 dataVersion，需要人工看一眼"
        };
        btnConflict.Click += (_, _) => JumpToTodo(null, r => r.Status == BoardSyncConsts.StConflict);
        todoWrap.Children.Add(btnJudge);
        todoWrap.Children.Add(btnVerify);
        todoWrap.Children.Add(btnConflict);
        Wpf.Grid.SetRow(todoWrap, 2);
        grid.Children.Add(todoWrap);
        _todoButtons = new[] { btnJudge, btnVerify, btnConflict };

        // ---- r167 列表筛选条：查什么（下拉） × 查多久以前（下拉）；r169 右端加「清除筛选结果」 ----
        var filters = new Wpf.DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var filterLab = new Wpf.TextBlock
        {
            Text = "列表筛选", FontSize = 12, Foreground = new SolidColorBrush(Muted),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        Wpf.DockPanel.SetDock(filterLab, Wpf.Dock.Left);
        filters.Children.Add(filterLab);
        _filter.SelectionChanged += (_, _) =>
        {
            if (_filterSync) return;
            _filterKey = (_filter.SelectedItem as Wpf.ComboBoxItem)?.Tag as string ?? FAll;
            // _mineOnly 是给待办条和诊断读的镜像：只在一个地方算出来，别两处各判一次
            _mineOnly = _filterKey == FMine;
            Refresh();
        };
        _range.SelectionChanged += (_, _) =>
        {
            if (_rangeSync) return;
            _showAll = _range.SelectedItem is Wpf.ComboBoxItem ri && ri.Tag is bool rb && rb;
            Refresh();
        };
        Wpf.DockPanel.SetDock(_filter, Wpf.Dock.Left);
        filters.Children.Add(_filter);
        Wpf.DockPanel.SetDock(_range, Wpf.Dock.Left);
        filters.Children.Add(_range);

        // r169：一键清掉「现在筛出来的这些」—— 原来只能清全部 / 清已终结，
        // 想只清某个班、某个时间段，得先清全部再补救，实际没人敢点。
        _purge.Margin = new Thickness(10, 0, 0, 0);
        _purge.VerticalAlignment = VerticalAlignment.Center;
        _purge.Click += (_, _) => PurgeFilteredNow();
        Wpf.DockPanel.SetDock(_purge, Wpf.Dock.Right);
        filters.Children.Add(_purge);

        _filterHint.VerticalAlignment = VerticalAlignment.Center;
        filters.Children.Add(_filterHint);
        Wpf.Grid.SetRow(filters, 3);
        grid.Children.Add(filters);

        _snapshotBar.FontSize = 12;
        _snapshotBar.Margin = new Thickness(0, 0, 0, 10);
        _snapshotBar.Foreground = new SolidColorBrush(Muted);
        Wpf.Grid.SetRow(_snapshotBar, 4);
        grid.Children.Add(_snapshotBar);

        // ---- 主体 ----
        var body = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 10) };
        body.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(370) });
        body.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Wpf.Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE8)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0)
        };
        _list.FontSize = 13;
        _list.Margin = new Thickness(4);
        _list.SelectionChanged += (_, _) => ShowDetail();
        left.Child = _list;
        Wpf.Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var right = new Wpf.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE8)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12)
        };
        var rightGrid = new Wpf.Grid();
        rightGrid.RowDefinitions.Add(new Wpf.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightGrid.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });
        var detailScroll = new Wpf.ScrollViewer
        {
            VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled
        };
        _detailPanel.Margin = new Thickness(0, 0, 8, 0);
        detailScroll.Content = _detailPanel;
        Wpf.Grid.SetRow(detailScroll, 0);
        rightGrid.Children.Add(detailScroll);
        _decisionBar.Margin = new Thickness(0, 12, 0, 0);
        Wpf.Grid.SetRow(_decisionBar, 1);
        rightGrid.Children.Add(_decisionBar);
        right.Child = rightGrid;
        Wpf.Grid.SetColumn(right, 1);
        body.Children.Add(right);

        Wpf.Grid.SetRow(body, 5);
        grid.Children.Add(body);

        // ---- 状态栏 ----
        _status.FontSize = 12;
        _status.Foreground = new SolidColorBrush(Ink);
        Wpf.Grid.SetRow(_status, 6);
        grid.Children.Add(_status);
    }

    static Wpf.TextBlock Lab(string text) => new()
    {
        Text = text, FontSize = 12, Margin = new Thickness(12, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Muted)
    };

    static Wpf.TextBlock Line(string text, double size = 13, Color? fg = null,
                              bool bold = false, bool mono = false)
        => new()
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = mono ? new System.Windows.Media.FontFamily("Consolas")
                              : new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(fg ?? Ink),
            Margin = new Thickness(0, 0, 0, 5)
        };

    /// <summary>字段字典 → 中文标签两列表格（回执易读的关键）。</summary>
    static UIElement FieldGrid(IDictionary<string, string>? fields, string emptyText = "（无内容）")
    {
        if (fields == null || fields.Count == 0) return Line(emptyText, 12, Muted);
        var order = new[] { "subject", "students", "title", "content", "bold", "color", "sizeLevel", "pinned" };
        var keys = fields.Keys
            .OrderBy(k => { int i = Array.IndexOf(order, k); return i < 0 ? 99 : i; })
            .ThenBy(k => k, StringComparer.Ordinal).ToList();

        var g = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(76) });
        g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var k in keys)
        {
            int row = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });
            string v = fields[k] ?? "";
            if (k is "bold" or "pinned") v = v == "true" ? "是" : "否";
            if (v.Length == 0) v = "（空）";

            var lab = Line(BoardSyncConsts.FieldLabel(k), 12, Muted);
            lab.Margin = new Thickness(0, 3, 8, 3);
            var val = Line(v, 13, Ink);
            val.Margin = new Thickness(0, 3, 0, 3);
            Wpf.Grid.SetRow(lab, row); Wpf.Grid.SetColumn(lab, 0);
            Wpf.Grid.SetRow(val, row); Wpf.Grid.SetColumn(val, 1);
            g.Children.Add(lab); g.Children.Add(val);
        }
        return g;
    }

    /// <summary>
    /// 只读显示当前配置摘要与发布权限。★ 设置项不再堆在台账窗口里：
    /// 启用同步 / 教室机 / 摆渡根目录 / 轮询 / 任教班级统一在控制面板「信息管理」页保存。
    /// </summary>
    void LoadHints()
    {
        var s = Store.LoadSettings();
        bool configured = !(s.UpdateSourceKind ?? "Off").Equals("Off", StringComparison.OrdinalIgnoreCase)
                          || !string.IsNullOrWhiteSpace(s.BoardSyncRoot);
        string sync = s.BoardSyncEnabled ? "同步已启用" : "同步未启用";
        string machine = BoardSyncService.IsClassroomMachine ? "本机＝教室机（发布内容快照）"
                                                             : "本机＝教师机（读教室机快照）";
        int keep = KeepDays();
        string keepText = keep <= 0 ? "全部" : "最近 " + keep + " 天";
        _configHint.Text = $"{sync}　│　{machine}　│　{(configured ? "数据源已配置" : "数据源未配置")}"
                         + $"　│　台账默认显示 {keepText}记录（未处理的永远显示；保留期在「信息管理 → ③ 同步设置」里改）";
        _snapshotBar.Text = BoardSyncService.IsClassroomMachine
            ? "本机是教室机：作业 / 缺交 / 本班通知变化后会自动发布内容快照，教师机才能选到目标条目。"
            : "教师机：点「👀 教室机当前内容」查看教室机发布的内容快照；没快照时发布信息只能选「新增」。";

        bool can = UserSession.CanPublishInfo;
        _send.IsEnabled = can;
        _send.ToolTip = can ? "发布作业 / 缺交 / 通知信息给所选班级"
                            : UserSession.DenyPublishInfo;

        // r167：两个下拉的候选项按「当前视图 + 这台机器」重建：
        // 「只看我发的」只在教师机的发送箱出现 —— 教室机收件箱要确认的是本班全部到件，
        // 列出来人反而会怀疑「是不是漏了别人发来的」。
        RebuildFilterItems();

        // 清除只给教师 / 管理员：电教管理员误清会把待处理消息一起清掉
        _clear.IsEnabled = !UserSession.IsStudent && Store.EnablePersistence;
        _clear.ToolTip = UserSession.IsStudent
            ? "电教管理员只能查看台账，不能清除（请用班主任 / 科任 / 管理员账号）"
            : "删除已终结（已核对 / 已忽略 / 已过期 / 已撤回）或全部记录；清前自动备份 board_sync.json.bak";
        _purge.IsEnabled = _clear.IsEnabled;
        _purge.ToolTip = UserSession.IsStudent
            ? "电教管理员只能查看台账，不能清除（请用班主任 / 科任 / 管理员账号）"
            : "只删「当前筛选出来的这些记录」（清前自动备份）；未处理的变更单会保留";

        if (!configured)
            _status.Text = "⚠ 未配置数据源：请到「信息管理 → 同步设置」配置共享目录或 FTP，同步功能才能收发消息。";
    }

    /// <summary>台账保留天数（0 = 不过滤）。</summary>
    static int KeepDays()
    {
        try { return Math.Max(0, Store.LoadSettings().BoardSyncLedgerKeepDays); }
        catch { return 7; }
    }

    bool IsOut => (_view.SelectedItem as Wpf.ComboBoxItem)?.Tag as string == "out";

    /// <summary>
    /// 重建两个筛选下拉的候选项（切视图 / 换机器 / 打开窗口时各来一次）。
    /// <para>★ 重建时必须挂 _filterSync / _rangeSync：Items.Clear() 会把 SelectedIndex 打成 -1，
    /// 不挡住就会触发 SelectionChanged → 把 _filterKey 覆成「全部记录」再 Refresh 一次，
    /// 结果就是「切个视图筛选自己就复位了」。</para>
    /// </summary>
    void RebuildFilterItems()
    {
        string me = (UserSession.Current?.Username ?? "").Trim();
        bool allowMine = !BoardSyncService.IsClassroomMachine && IsOut;

        _filterSync = true;
        try
        {
            _filter.Items.Clear();
            void Add(string label, string key)
                => _filter.Items.Add(new Wpf.ComboBoxItem { Content = label, Tag = key });
            Add("全部记录", FAll);
            if (allowMine)
                Add(me.Length > 0 ? "只看我发的（" + me + "）" : "只看我发的", FMine);
            Add("只看未处理", FPending);
            Add("只看已处理", FDone);
            Add("只看有冲突", FConflict);
            Add("只看撤回记录", FRevoke);

            // 找回原来那一项；这台机器 / 这个视图没有它（比如切到收件箱时的「只看我发的」）就落回全部
            int idx = 0;
            for (int i = 0; i < _filter.Items.Count; i++)
                if ((_filter.Items[i] as Wpf.ComboBoxItem)?.Tag as string == _filterKey) { idx = i; break; }
            _filter.SelectedIndex = idx;
            _filterKey = (_filter.Items[idx] as Wpf.ComboBoxItem)?.Tag as string ?? FAll;
            _mineOnly = _filterKey == FMine;
        }
        finally { _filterSync = false; }

        int keep = KeepDays();
        _rangeSync = true;
        try
        {
            _range.Items.Clear();
            if (keep <= 0)
            {
                // 保留期设成 0 = 本来就不限时间，别摆一个点了没反应的「最近 0 天」
                _showAll = true;
                _range.Items.Add(new Wpf.ComboBoxItem { Content = "全部时段", Tag = true });
                _range.SelectedIndex = 0;
            }
            else
            {
                _range.Items.Add(new Wpf.ComboBoxItem { Content = "最近 " + keep + " 天", Tag = false });
                _range.Items.Add(new Wpf.ComboBoxItem { Content = "全部时段（含更早）", Tag = true });
                _range.SelectedIndex = _showAll ? 1 : 0;
            }
            _range.IsEnabled = keep > 0;
        }
        finally { _rangeSync = false; }
    }

    /// <summary>
    /// 「查什么」的判定。
    /// <para>★「只看我发的」走 <see cref="PendingTodoService.IsMine"/> —— 与今日卡 / 托盘角标同一口径，
    /// 两处各写一份就会出现「今日卡说 2 条、这里列 5 条」。发件人为空的老记录算「我的」，宁可多列不漏单。</para>
    /// </summary>
    bool FilterPredicate(BoardMessageRecord r)
    {
        switch (_filterKey)
        {
            case FMine:
                return PendingTodoService.IsMine(r.Order);
            case FPending:
                // 待重试（StRetry）也在这里：它同样还没办完
                return !r.IsTerminal && !r.Order.IsRevoke && r.Status != BoardSyncConsts.StConflict;
            case FDone:
                // 已应用不是 IsTerminal（后面还要核对一次），但老师眼里它已经办完了
                return r.Status != BoardSyncConsts.StConflict
                       && (r.IsTerminal || r.Status == BoardSyncConsts.StApplied);
            case FConflict:
                return r.Status == BoardSyncConsts.StConflict;
            case FRevoke:
                return r.Order.IsRevoke;
            default:
                return true;
        }
    }

    async void DoPull()
    {
        try
        {
            _status.Text = "正在检查共享目录…";
            var (report, err) = await Task.Run(() =>
            {
                var r = BoardSyncService.Pull(BoardSyncService.SubscribedClasses());
                return (r.Report, BoardSyncService.LastError);
            });
            _status.Text = report + (err == null ? "" : "；" + err);
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = "检查失败：" + ex.Message;
        }
    }

    async void ShowSnapshot()
    {
        string cls = (Store.LoadSettings().SelectedClass ?? "").Trim();
        try
        {
            _status.Text = "正在读取教室机内容…";
            var (snap, msg) = await Task.Run(() => BoardSyncService.LoadRemoteSnapshot(cls));
            _snapshotBar.Text = "📡 " + msg;
            _snapshotBar.Foreground = new SolidColorBrush(snap == null ? Amber : Blue);
            _status.Text = msg;
            if (snap == null) return;
            new BoardSnapshotViewWindow(snap) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            _status.Text = "读取教室机内容失败：" + ex.Message;
        }
    }

    async void DoSend()
    {
        if (!UserSession.CanPublishInfo)
        {
            _status.Text = UserSession.DenyPublishInfo;
            return;
        }
        // 台账窗口里的发布是单班快捷入口（默认本机显示班级）；
        // 要多班批量发布，请到控制面板「信息管理」页勾选班级后点「发布信息」。
        var w = new BoardSendWindow { Owner = this };
        if (w.ShowDialog() != true || w.Results.Count == 0) return;

        // ★ 发送要写共享盘（r154）：放到后台，共享盘断了只让"发送"这一步慢，
        //   窗口照旧能点、能关。
        _status.Text = "正在发送…";
        var results = await Task.Run(() =>
        {
            var list = new List<(bool Ok, string Message)>();
            foreach (var o in w.Results)
            {
                var r = BoardSyncService.Send(o);
                list.Add((r.Ok, r.Message));
            }
            return list;
        });

        int ok = results.Count(r => r.Ok);
        var errs = results.Where(r => !r.Ok).Select(r => r.Message).ToList();
        _status.Text = ok > 0
            ? $"已发出 {ok} 条信息。" + (errs.Count > 0 ? "失败 " + errs.Count + " 条：" + string.Join("；", errs) : "")
            : "❌ " + string.Join("；", errs);
        _view.SelectedIndex = 1;
        Refresh();
        // 教师机定时轮询可能被下发关掉：发布成功当场补拉一次回执，
        // 否则老师要等到手动点「立即检查」才知道教室机收没收到。
        if (ok > 0) BoardSyncNotifier.CheckNow();
    }

    /// <summary>r147：刷新待办分组条。数字与托盘 / 悬浮课表同一口径（PendingTodoService）。</summary>
    void RefreshTodoBar()
    {
        try
        {
            // 待办条必须跟当前列表口径一致：勾了「只看我发的」就只数我的，
            // 取消勾选就数全部 —— 否则条上写 2 条、下面列出 5 条，会让人以为丢单。
            var c = PendingTodoService.Compute(mineOnly: _mineOnly && IsOut);
            // 与 PendingTodoService 默认口径一致：用 IsClassroomMachine，不能只看当前登录角色
            string role = BoardSyncService.IsClassroomMachine ? "教室机" : "教师机";
            string tail = IsOut && _mineOnly && _mineHidden > 0
                ? $"（只看我发的，另有 {_mineHidden} 条是别人发的）" : "";
            _todoBar.Text = (c.Any
                ? $"{role}待办 {c.Todo} 条：待裁决 {c.NeedDecision} · 待核对 {c.NeedVerify}"
                  + (c.Conflict > 0 ? $" · 冲突 {c.Conflict}" : "")
                : $"{role}当前没有待办（办结后数字会自动减）") + tail;
            _todoBar.Foreground = new SolidColorBrush(c.Any
                ? Color.FromRgb(0x8A, 0x5A, 0x00) : Color.FromRgb(0x77, 0x88, 0x99));
            if (_todoButtons != null)
            {
                _todoButtons[0].IsEnabled = c.NeedDecision > 0;
                _todoButtons[1].IsEnabled = c.NeedVerify > 0;
                _todoButtons[2].IsEnabled = c.Conflict > 0;
            }
        }
        catch { _todoBar.Text = "待办统计暂不可用"; }
    }

    /// <summary>点待办分组：切视图（传 null 保持当前），选中第一条符合条件的记录。</summary>
    void JumpToTodo(string? view, Func<BoardMessageRecord, bool> pred)
    {
        try
        {
            if (view != null)
                for (int i = 0; i < _view.Items.Count; i++)
                    if ((_view.Items[i] as Wpf.ComboBoxItem)?.Tag as string == view)
                    { _view.SelectedIndex = i; break; }

            Refresh();
            for (int i = 0; i < _records.Count; i++)
                if (pred(_records[i])) { _list.SelectedIndex = i; _list.ScrollIntoView(_list.SelectedItem); break; }
            ShowDetail();
        }
        catch { }
    }

    void Refresh()
    {
        try
        {
            var st = BoardSyncService.LoadState();
            var all = IsOut ? BoardSyncService.Outbox(st) : BoardSyncService.Inbox(st);
            int total = all.Count;

            // r167：先按「查什么」筛，再按「查多久以前」筛 —— 两个维度分开算，
            // 提示里各报各的数，才说得清「一条都没了」到底是筛掉了还是时间挡住了。
            var byFilter = all.Where(FilterPredicate).ToList();
            int hiddenByFilter = total - byFilter.Count;
            _mineHidden = _filterKey == FMine ? hiddenByFilter : 0;

            // ★ 未处理的收件箱不受保留期影响（见 FilterByKeepDays），避免消息被藏起来。
            int keepDays = KeepDays();
            var withinKeep = _showAll ? byFilter : BoardSyncService.FilterByKeepDays(byFilter, keepDays);
            int hiddenByTime = byFilter.Count - withinKeep.Count;

            // 撤回通知只是技术回执：原记录已经显示「已撤回」，默认不该再列一条
            // 「[已发出] … 撤回：xxx」让人误以为又发了新信息。
            // 选「全部时段」或明确选「只看撤回记录」时才列 —— 那是审计痕迹，不是新消息。
            bool hideRevoke = IsOut && !_showAll && _filterKey != FRevoke;
            var shown = hideRevoke ? withinKeep.Where(r => !r.Order.IsRevoke).ToList() : withinKeep;
            _records = shown;

            _filterHint.Text = "共 " + shown.Count + " 条"
                + (hiddenByTime > 0 ? "，另有 " + hiddenByTime + " 条更早（时间范围里可见）" : "")
                + (hiddenByFilter > 0 ? "；按当前条件筛掉 " + hiddenByFilter + " 条" : "");
            _filterHint.ToolTip = _filterHint.Text;

            // r169：按钮上带条数 —— 「清除筛选结果」到底会清掉几条，点之前就该看得见
            _purge.Content = shown.Count > 0 ? $"清除筛选结果（{shown.Count}）" : "清除筛选结果";
            _purge.IsEnabled = shown.Count > 0 && !UserSession.IsStudent && Store.EnablePersistence;

            int prev = _list.SelectedIndex;
            _list.Items.Clear();
            foreach (var r in _records)
            {
                string dec = string.IsNullOrWhiteSpace(r.Decision)
                    ? "" : " · " + BoardSyncConsts.DecisionName(r.Decision);
                _list.Items.Add($"[{r.Status}] {r.Order.CreatedAt:MM-dd HH:mm}  " +
                                $"{BoardSyncConsts.EntityName(r.Order.Entity)} · {r.Order.Summary}{dec}");
            }
            if (_records.Count > 0)
                _list.SelectedIndex = prev >= 0 && prev < _records.Count ? prev : 0;
            else
                ShowDetail();

            RefreshTodoBar();
        }
        catch (Exception ex)
        {
            _detailPanel.Children.Clear();
            _detailPanel.Children.Add(Line("台账读取失败：" + ex.Message, 14, Amber));
            _status.Text = "坏台账必须修复后才能继续，避免静默丢消息。";
        }
    }

    BoardMessageRecord? Current
        => _list.SelectedIndex >= 0 && _list.SelectedIndex < _records.Count
           ? _records[_list.SelectedIndex] : null;

    void ShowDetail()
    {
        _decisionBar.Children.Clear();
        _detailPanel.Children.Clear();
        var r = Current;
        if (r == null)
        {
            _detailPanel.Children.Add(Line("（暂无消息）", 16, Ink, bold: true));
            _detailPanel.Children.Add(Line(IsOut
                ? "教师机点「＋ 发布信息」把作业 / 缺交 / 通知发给教室机；多班批量发布请到「信息管理」页。"
                : "教室机点「↻ 立即检查」收取教师机发来的信息；收到后课间会弹提示。", 13, Muted));
            return;
        }

        if (r.Direction == BoardSyncConsts.DirIn && !r.Seen)
        {
            var seen = BoardSyncService.MarkSeen(r.MsgId);
            if (!seen.Ok) _status.Text = seen.Message;
        }

        var o = r.Order;
        bool done = !string.IsNullOrWhiteSpace(r.Decision) || r.IsTerminal;

        // ---- 详情（r77 重排版：去标题、只留「目标 → 改成」，元信息收成浅色小字，提醒折叠） ----
        _detailPanel.Children.Add(Line("目标条目", 12, Muted));
        _detailPanel.Children.Add(Line(
            string.IsNullOrWhiteSpace(o.ItemId)
                ? "（未指定 · 教室机会新增一条）"
                : (string.IsNullOrWhiteSpace(o.TargetText) ? o.ItemId : o.TargetText),
            16, Ink, bold: true));

        _detailPanel.Children.Add(Line("要求改成", 12, Muted));
        _detailPanel.Children.Add(FieldGrid(o.Fields));
        if (o.IsWeeklyDuty)
            _detailPanel.Children.Add(Line("一周安排（逐天）：\n" + BoardSyncConsts.WeekRowsText(o.WeekRows), 13, Blue));
        string reqStyles = BoardSyncConsts.FormatStyles(o.Styles);
        if (reqStyles.Length > 0)
            _detailPanel.Children.Add(Line("格式：\n" + reqStyles, 12, Muted));

        // 元信息：浅色小字一行（所属班级 · 发送人 · 发送时间 · 有效时间）
        var metaParts = new List<string>
        {
            o.ClassKey,
            "发送人 " + o.Sender + (string.IsNullOrWhiteSpace(o.SenderRole) ? "" : "（" + o.SenderRole + "）"),
            "发送 " + o.CreatedAt.ToString("MM-dd HH:mm"),
            "有效 " + o.ValidDay
        };
        _detailPanel.Children.Add(Line(string.Join("　·　", metaParts), 11, Muted));

        // 状态 / 裁决压成一行，不额外占版面
        if (done)
        {
            var stParts = new List<string> { r.Status };
            if (!string.IsNullOrWhiteSpace(r.Decision))
                stParts.Add((r.DecisionBy == "教室机自动增加" ? "自动处理 " : "裁决 ") +
                            BoardSyncConsts.DecisionName(r.Decision));
            if (!string.IsNullOrWhiteSpace(r.ReceiptTime))
                stParts.Add("处理 " + r.ReceiptTime);
            _detailPanel.Children.Add(Line(string.Join("　·　", stParts), 11, Muted));
        }
        if (o.AutoApply)
            _detailPanel.Children.Add(Line("⚡ 发送方指定「直接增加」，教室机收到后自动新增一条。", 11, Amber));

        // 全屏 / 语音：折叠进一个「🔊 提醒详情」，点开再看，避免一屏都是提醒说明
        bool hasFull = o.ForceBreakReminder;
        bool hasVoice = !string.IsNullOrWhiteSpace(o.VoiceText);
        if (hasFull || hasVoice)
        {
            var bell = new Wpf.StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            if (hasFull)
            {
                bell.Children.Add(Line("🔔 课间全屏提醒" + (string.IsNullOrWhiteSpace(o.ForceText) ? "（显示摘要）" : ""), 12, Amber, bold: true));
                if (!string.IsNullOrWhiteSpace(o.ForceText))
                    bell.Children.Add(Line(o.ForceText, 12, Muted));
            }
            if (hasVoice)
            {
                bell.Children.Add(Line("🔊 语音播报（" + (o.VoiceImmediate ? "收到就播，上课也播" : "课间播") + "）", 12, Blue, bold: true));
                bell.Children.Add(Line(o.VoiceText!, 12, Muted));
            }
            _detailPanel.Children.Add(new Wpf.Expander
            {
                Header = "🔊 提醒详情（全屏 / 语音）", FontSize = 12,
                Foreground = new SolidColorBrush(Muted), IsExpanded = false,
                Margin = new Thickness(0, 6, 0, 6), Content = bell
            });
        }

        // 技术信息保留为默认收起，方便排查但不占版面
        var tech = new Wpf.StackPanel();
        tech.Children.Add(Line("MsgId：" + o.MsgId, 11, Muted, mono: true));
        tech.Children.Add(Line("目标 Id：" + (string.IsNullOrWhiteSpace(o.ItemId) ? "（空）" : o.ItemId), 11, Muted, mono: true));
        tech.Children.Add(Line("base 签名：" + (string.IsNullOrWhiteSpace(o.BaseSignature) ? "（无）" : o.BaseSignature), 11, Muted, mono: true));
        if (!string.IsNullOrWhiteSpace(r.ArchivedSnapshot))
            tech.Children.Add(Line("被覆盖版本全文存档：" + r.ArchivedSnapshot, 11, Muted, mono: true));
        _detailPanel.Children.Add(new Wpf.Expander
        {
            Header = "技术信息（Id / 签名 / 存档）", FontSize = 12,
            Foreground = new SolidColorBrush(Muted), IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8), Content = tech
        });

        // ---- 撤回还原：只有教室机（本机数据的唯一写者）能按存档把原文放回去 ----
        // 放在窗口底部操作条而不是详情卡里：详情卡要滚动才看得到，这个按钮是「改错了要救」的，
        // 必须一眼能看见。已应用 / 已核对 / 冲突三种状态都能撤（冲突时更需要）。
        bool canRestore = r.Direction == BoardSyncConsts.DirIn
                          && !o.IsRevoke
                          && !string.IsNullOrWhiteSpace(r.ArchivedSnapshot)
                          && r.Status is BoardSyncConsts.StApplied
                              or BoardSyncConsts.StVerified or BoardSyncConsts.StConflict
                          && (BoardSyncService.IsClassroomMachine || UserSession.IsStudent);
        if (canRestore)
        {
            var undo = new Wpf.Button
            {
                Content = o.IsWeeklyDuty
                    ? "↩ 撤回这次整周替换（还原整张值日表）"
                    : "↩ 撤回这次替换（还原原文）",
                Padding = new Thickness(14, 8, 14, 8), FontSize = 14,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5)),
                ToolTip = "按存档整条还原教室机原来的内容与格式；这次变更新增的那条会被软删除，教师机会收到「已撤回」回执"
            };
            undo.Click += (_, _) => RestoreNow(r.MsgId);
            _decisionBar.Children.Add(undo);
        }

        // ---- 裁决按钮 ----
        if (r.Direction == BoardSyncConsts.DirIn && !r.IsTerminal && !o.IsRevoke)
        {
            if (!UserSession.IsStudent)
            {
                _decisionBar.Children.Add(new Wpf.TextBlock
                {
                    Text = "只有电教管理员（教室机）能应用变更单；本机是教师机，只能查看与核对。",
                    Foreground = new SolidColorBrush(Amber),
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
                });
                return;
            }
            foreach (var d in BoardSyncConsts.DecisionsFor(o.Op))
            {
                string label = (o.IsWeeklyDuty ? BoardSyncConsts.WeeklyDecisionName(d)
                                               : BoardSyncConsts.DecisionName(d)) +
                               (d == BoardSyncConsts.DecAppend ? "（推荐）" : "");
                // 整周「替换」的对象是整张表，没有单一目标条目 —— 不要去弹「现场指定目标」。
                bool needTarget = !o.IsWeeklyDuty &&
                                  d is BoardSyncConsts.DecReplace or BoardSyncConsts.DecDelete;
                var btn = new Wpf.Button
                {
                    Content = label + (needTarget ? "…" : ""), Tag = d,
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(0, 0, 10, 0), FontSize = 14,
                    Background = d == BoardSyncConsts.DecAppend
                        ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFF)) : Brushes.White
                };
                string decision = d;
                btn.Click += (_, _) =>
                {
                    string? targetId = null, targetSig = null;
                    if (needTarget && !BoardSyncService.LocalHasItem(o.Entity, o.ItemId))
                    {
                        var picked = PickTarget(o, decision);
                        if (picked == null) return;      // 用户取消 / 本机无同类条目
                        targetId = picked.Id;
                        targetSig = picked.Signature;    // 人工指定：以教室机现场看到的当前值做 base
                    }
                    ApplyDecision(r.MsgId, decision, targetId, targetSig);
                };
                _decisionBar.Children.Add(btn);
            }
        }
        else if (r.Direction == BoardSyncConsts.DirOut && !r.IsTerminal && !o.IsRevoke)
        {
            var btn = new Wpf.Button
            {
                Content = "撤回这条变更单", Padding = new Thickness(14, 8, 14, 8), FontSize = 14
            };
            btn.Click += async (_, _) =>
            {
                // 撤回 = 再发一条变更单（要写共享盘）：后台做，窗口不跟着等
                _status.Text = "正在撤回…";
                var res = await Task.Run(() => BoardSyncService.Revoke(r.MsgId));
                _status.Text = res.Ok ? res.Message : "❌ " + res.Message;
                Refresh();
            };
            _decisionBar.Children.Add(btn);
        }
    }

    /// <summary>
    /// 变更单没带目标 / 目标在教室机不存在时，让电教管理员现场指定一条本机条目。
    /// 这是「替换」不退化成「新增」的最后一道兜底；快照只是让它通常不需要走到这一步。
    /// </summary>
    BoardSnapshotItem? PickTarget(BoardChangeOrder o, string decision)
    {
        string cls = (Store.LoadSettings().SelectedClass ?? o.ClassKey ?? "").Trim();
        var local = BoardSyncService.LocalItemsForTarget(o.Entity, cls);
        string verb = decision == BoardSyncConsts.DecReplace ? "替换" : "删除";
        if (local.Count == 0)
        {
            MessageBox.Show(
                $"教室里「{BoardSyncConsts.EntityName(o.Entity)}」目前没有任何条目，没法{verb}。\n\n" +
                "如果教师机本来就是要新加一条，请改选「增加（两条都留）」。",
                "无法" + verb, MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var w = new BoardTargetPickWindow(o, decision, local) { Owner = this };
        return w.ShowDialog() == true ? w.Result : null;
    }

    async void ApplyDecision(string msgId, string decision,
                             string? targetItemId = null, string? targetBaseSignature = null)
    {
        // 应用要改本机作业 / 缺交文件 + 排回执：后台做（回执本身已经是异步补发的）
        _status.Text = "正在应用…";
        var res = await Task.Run(() => BoardSyncService.Apply(msgId, decision,
            targetItemId: targetItemId, targetBaseSignature: targetBaseSignature));
        _status.Text = res.Ok ? "✔ " + res.Message : "❌ " + res.Message;
        if (!res.Ok) return;

        if (decision is not (BoardSyncConsts.DecIgnore or BoardSyncConsts.DecKeep))
            RaiseContentChanged();
        Refresh();
    }

    /// <summary>
    /// 撤回这次替换 / 删除：按回执里的整条存档原文还原教室机数据。
    /// <para>★ 先确认再动手：还原会改本机作业 / 缺交文件，且把这次新增的那条软删除。</para>
    /// </summary>
    async void RestoreNow(string msgId)
    {
        var pick = MessageBox.Show(this,
            "要撤回这次替换、把原文还原回教室机吗？\n\n" +
            "· 这次变更「新增」出来的那一条会被软删除（不物理删除，仍可追溯）\n" +
            "· 原文按存档整条还原，包括字体 / 颜色 / 字号\n" +
            "· 会写一份「已撤回」回执，教师机下一轮检查就能看到\n\n" +
            "如果只是格式不满意，建议用「发布信息」再发一条修正，而不是撤回。",
            "撤回并还原", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (pick != MessageBoxResult.OK) return;

        _status.Text = "正在还原…";
        var res = await Task.Run(() => BoardSyncService.RestoreArchived(msgId));
        _status.Text = res.Ok ? "✔ " + res.Message : "❌ " + res.Message;
        if (!res.Ok) return;

        RaiseContentChanged();
        Refresh();
    }

    /// <summary>
    /// 清除「当前筛选出来的这些」（r169）：视图 + 筛选条件 + 时间范围叠加后的 <see cref="_records"/>。
    /// <para>两步确认：先看条数与「会保留几条未处理」，再落盘。</para>
    /// </summary>
    void PurgeFilteredNow()
    {
        try
        {
            var targets = _records.ToList();
            if (targets.Count == 0)
            {
                _status.Text = "当前筛选下没有记录，不用清除。";
                return;
            }
            int pendingKept = targets.Count(r => r.Direction == BoardSyncConsts.DirIn && !r.IsTerminal);
            string what = ((_filter.SelectedItem as Wpf.ComboBoxItem)?.Content as string ?? "全部记录")
                        + " · " + ((_range.SelectedItem as Wpf.ComboBoxItem)?.Content as string ?? "最近记录");
            string scope = (IsOut ? "发送箱" : "收件箱") + " · " + what;

            var dlg = new LedgerPurgeWindow(targets.Count, pendingKept, scope) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            var res = BoardSyncService.ClearLedgerRecords(targets.Select(r => r.MsgId));
            _status.Text = res.Ok ? "✔ " + res.Message : "❌ " + res.Message;
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = "清除筛选结果失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 清除本机台账记录（人工，永不自动）。清前由服务备份成 board_sync.json.bak。
    /// <para>★ 只给教师 / 管理员：电教管理员误清会把待处理变更单一起清掉。</para>
    /// </summary>
    void ClearLedgerNow()
    {
        try
        {
            var st = BoardSyncService.LoadState();
            var (total, terminal, pending) = BoardSyncService.LedgerCounts(st);
            if (total == 0)
            {
                _status.Text = "台账里没有记录，不用清除。";
                return;
            }

            var dlg = new LedgerCleanWindow(total, terminal, pending, KeepDays()) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.ClearAll == null) return;

            var res = BoardSyncService.ClearLedger(dlg.ClearAll.Value);
            _status.Text = res.Ok ? "✔ " + res.Message : "❌ " + res.Message;
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = "清除台账失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 数据已落盘：只发内容类 Changed，壁纸走 BackdropService 的 1.2s 防抖订阅。
    /// 绝不 RaiseScheduleChanged()，也绝不在确认链里同步重绘壁纸。
    /// </summary>
    static void RaiseContentChanged()
    {
        AppEvents.RaiseHomeworkChanged();
        AppEvents.RaiseMissingChanged();
        AppEvents.RaiseDutyChanged();       // 值日安排也是变更单的一类，应用 / 撤回后便签要立刻刷新
        AppEvents.RaiseNotificationChanged();
        AppEvents.RaiseBackdropChanged();
    }
}

/// <summary>
/// 「清除台账」对话框：只清已终结 / 全部清空二选一，先把后果说清楚再动手。
/// <para>为什么不用 MessageBox 的三按钮：是 / 否 / 取消 对「只清一部分」这种语义太含糊，
/// 误点一下就把未处理的变更单清掉，教师机那边还以为消息还等着。</para>
/// </summary>
public class LedgerCleanWindow : Window
{
    /// <summary>false = 只清已终结；true = 全部清空；null = 取消。</summary>
    public bool? ClearAll { get; private set; }

    public LedgerCleanWindow(int total, int terminal, int pending, int keepDays)
    {
        Title = "清除台账记录";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xF9));

        var root = new Wpf.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "清除本机台账记录", FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x55)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(new Wpf.TextBlock
        {
            Text = $"当前共 {total} 条：已终结（已核对 / 已忽略 / 已过期 / 已撤回）{terminal} 条，未处理 {pending} 条。\n"
                 + $"清除前会自动备份成 board_sync.json.bak；默认显示最近 {(keepDays <= 0 ? "全部" : keepDays + " 天")}的记录，"
                 + "更早的记录也可以先点「显示全部」翻看确认。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x66, 0x77)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var all = new Wpf.RadioButton
        {
            Content = $"全部清空（{total} 条，含 {pending} 条未处理）", FontSize = 14, IsChecked = true,
            IsEnabled = total > 0, Margin = new Thickness(0, 0, 0, 6)
        };
        var only = new Wpf.RadioButton
        {
            Content = $"只清已终结记录（{terminal} 条）", FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6)
        };
        root.Children.Add(all);
        root.Children.Add(only);
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "★ 清掉之后：这些回执再也查不到，也不能再按存档「撤回替换」；"
                 + "若把未处理的变更单一起清掉，教师机那边仍显示「已发出」，但本机不会再提醒你确认。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x20)),
            Margin = new Thickness(0, 8, 0, 0)
        });

        var bar = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var ok = new Wpf.Button
        {
            Content = "确定清除", Padding = new Thickness(16, 7, 16, 7), FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0xA0))
        };
        ok.Click += (_, _) =>
        {
            ClearAll = all.IsChecked == true;
            DialogResult = true;
            Close();
        };
        var cancel = new Wpf.Button
        {
            Content = "取消", Padding = new Thickness(16, 7, 16, 7), FontSize = 14,
            Margin = new Thickness(10, 0, 0, 0)
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);

        Content = root;
        // ★ 必须走分层玻璃窗：本窗是纯 C# 构造、没有原生标题栏以外的容器，
        //   如果不在这里 Prepare，GlassBackdrop 的 DWM 路径会把 Window.Background 设成透明，
        //   而「非分层窗口不支持逐像素 alpha」→ 整窗被合成为纯黑（用户报的「清除台账弹窗是黑的」）。
        GlassChrome.Prepare(this);
    }
}

/// <summary>
/// 「清除筛选结果」的二次确认（r169）。
/// <para>★ 只显示「会清几条 / 会保留几条」，不提供其它选项 —— 要清的范围由窗外那两个下拉决定，
/// 这里再放开一个「改成全部」等于让老师在一张纸上做两件事，容易清错。</para>
/// </summary>
public class LedgerPurgeWindow : Window
{
    public LedgerPurgeWindow(int count, int pendingKept, string scope)
    {
        Title = "清除筛选结果";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xF9));

        var root = new Wpf.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new Wpf.TextBlock
        {
            Text = $"将清除当前列表里的 {count} 条记录",
            FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x55)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        var body = new Wpf.TextBlock
        {
            Text = $"筛选范围：{scope}\n清除前会自动备份成 board_sync.json.bak。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x66, 0x77)),
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(body);
        if (pendingKept > 0)
        {
            root.Children.Add(new Wpf.TextBlock
            {
                Text = $"★ 其中有 {pendingKept} 条是未处理的变更单，会保留不清 —— "
                     + "它们是教室机还没裁决的事，清掉之后教师机仍显示「已发出」，本机却不再提醒。",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x20)),
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        var bar = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var ok = new Wpf.Button
        {
            Content = "确定清除", Padding = new Thickness(16, 7, 16, 7), FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0xA0))
        };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Wpf.Button
        {
            Content = "取消", Padding = new Thickness(16, 7, 16, 7), FontSize = 14,
            Margin = new Thickness(10, 0, 0, 0)
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);

        Content = root;
        // 与 LedgerCleanWindow 同样的原因：纯 C# 构造的窗必须走分层玻璃，否则整窗变纯黑
        GlassChrome.Prepare(this);
    }
}

/// <summary>教室机内容快照只读预览：让教师机先看清「那边现在有什么」。</summary>
public class BoardSnapshotViewWindow : Window
{
    public BoardSnapshotViewWindow(BoardSnapshot snap)
    {
        Title = "👀 教室机当前内容";
        Width = 620;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Wpf.StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new Wpf.TextBlock
        {
            Text = $"来源：{snap.DeviceName}　快照时间：{snap.CreatedAt:yyyy-MM-dd HH:mm:ss}　共 {snap.Items.Count} 条",
            FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x55, 0xA8))
        });

        var sb = new StringBuilder();
        foreach (var g in snap.Items.GroupBy(x => x.Entity))
        {
            sb.AppendLine("【" + BoardSyncConsts.EntityName(g.Key) + "】");
            foreach (var x in g)
            {
                sb.AppendLine("  · " + x.Brief);
                sb.AppendLine("    Id：" + x.Id);
                string fmt = BoardSyncConsts.FormatStyles(x.Styles, "    ");
                if (fmt.Length > 0) sb.AppendLine(fmt);
            }
            sb.AppendLine();
        }
        if (snap.Items.Count == 0) sb.AppendLine("（教室机这一类内容目前是空的）");

        var box = new Wpf.TextBox
        {
            Text = sb.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
            FontSize = 13, Padding = new Thickness(8), Height = 320
        };
        root.Children.Add(box);
        var close = new Wpf.Button
        {
            Content = "关闭", Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;
        GlassChrome.Prepare(this);   // 同 LedgerCleanWindow：不走分层窗就会被 DWM 路径合成成黑窗
    }
}

/// <summary>「替换 / 删除」时现场指定教室机目标条目。</summary>
public class BoardTargetPickWindow : Window
{
    readonly Wpf.ComboBox _box = new() { Width = 460, FontSize = 13 };
    public BoardSnapshotItem? Result { get; private set; }

    public BoardTargetPickWindow(BoardChangeOrder order, string decision,
                                 IReadOnlyList<BoardSnapshotItem> local)
    {
        string verb = decision == BoardSyncConsts.DecReplace ? "替换" : "删除";
        Title = "指定要" + verb + "的教室机条目";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Wpf.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "这条变更单在教室机找不到目标条目", FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x55)),
            Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "变更单目标：" + (string.IsNullOrWhiteSpace(order.ItemId)
                        ? "（新增一条，未指定 Id）" : order.ItemId) +
                   "\n请从教室机当前内容里选一条要" + verb + "的；选定后才会写入，不会悄悄变成新增。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x66, 0x77)),
            Margin = new Thickness(0, 0, 0, 14)
        });

        foreach (var x in local)
            _box.Items.Add(new Wpf.ComboBoxItem
            { Content = x.Brief + "　〔" + x.Id + "〕", Tag = x });
        _box.SelectedIndex = 0;
        root.Children.Add(_box);

        var bar = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var ok = new Wpf.Button { Content = "确定" + verb, Padding = new Thickness(14, 7, 14, 7) };
        ok.Click += (_, _) =>
        {
            Result = (_box.SelectedItem as Wpf.ComboBoxItem)?.Tag as BoardSnapshotItem;
            DialogResult = Result != null;
            Close();
        };
        var cancel = new Wpf.Button { Content = "取消", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bar.Children.Add(ok);
        bar.Children.Add(cancel);
        root.Children.Add(bar);
        Content = root;
        GlassChrome.Prepare(this);   // 同上：纯 C# 弹窗统一挂分层玻璃
    }
}
