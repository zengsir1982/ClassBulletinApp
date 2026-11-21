using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用 WPF 与 WinForms，控件名有同名类型 —— 统一消歧义
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HAlign = System.Windows.HorizontalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 「校历 / 调休」设置窗（r133，纯代码窗口）。
///
/// <para>解决的是"日期 ≠ 上哪天的课"：周六补周三的课、周一放假，都由这里的一条规则描述。
/// 规则存在课表里（<see cref="ScheduleFile.Calendar"/>），跟着数据包下发到教室机。</para>
///
/// <para>★ 界面刻意只有一个表单：日期 + 类型（补课/放假）+ 按哪天 + 备注。
/// 不加"按周几"这种第二种口径 —— 两种写法混用必然出现"单双周对不上"的怪事。</para>
/// </summary>
internal class SchoolCalendarWindow : Window
{
    private readonly ListBox _list = new();
    private readonly DatePicker _dpDate = new();
    private readonly ComboBox _cbKind = new();
    private readonly DatePicker _dpAs = new();
    private readonly CheckBox _chkInherit = new();
    private readonly TextBox _tbNote = new();
    private readonly TextBlock _txtStatus = new();
    private readonly TextBlock _txtEmpty = new();
    private readonly TextBox _tbBulk = new();
    private readonly StackPanel _bulkPanel = new();
    private ScheduleFile? _sf;

    /// <summary>诊断用：最近一次操作的结果说明。</summary>
    internal string DiagStatus => _txtStatus.Text;

    /// <summary>诊断用：当前规则条数。</summary>
    internal int DiagRuleCount => _list.Items.Count;

    public SchoolCalendarWindow(DateTime? prefillDate = null)
    {
        Title = "校历 / 调休";
        Width = 880;
        Height = 700;
        MinWidth = 780;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ThemeHelper.Brush("#F5F7FA", Color.FromRgb(0xF5, 0xF7, 0xFA));

        BuildUi();
        LoadRules();
        if (prefillDate.HasValue) { _dpDate.SelectedDate = prefillDate.Value.Date; SyncKindEnabled(); }
        GlassChrome.Prepare(this);
    }

    // ==================== 界面 ====================

    void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

        var tip = new TextBlock
        {
            Text = "调休上课在这里设：例如「11-21（周六）· 按 11-18（周三）上课」，周六那天的日期仍然是 11-21，"
                 + "只是课表按周三排；单双周、调课也按 11-18 那天算。放假日大屏显示「今日放假」。",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var tip2 = new TextBlock
        {
            Text = "★ 校历随课表保存在文件里，改完要「发布/同步」到教室机才生效（本机立即生效）。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Foreground = ThemeHelper.Brush("#BA7517", Color.FromRgb(0xBA, 0x75, 0x17))
        };
        var head = new StackPanel();
        head.Children.Add(tip);
        head.Children.Add(tip2);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // 左：规则列表；右：表单
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        Grid.SetRow(main, 1);
        root.Children.Add(main);

        // ---- 左栏列表 ----
        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        var leftTitle = new TextBlock
        {
            Text = "已设置的日期", FontSize = 13, FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);

        var listBox = _list;
        listBox.FontSize = 13;
        listBox.BorderThickness = new Thickness(0);
        listBox.SelectionChanged += (_, _) => LoadSelectedIntoForm();
        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listBox
        };
        var listHost = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0.5), Padding = new Thickness(6),
            Child = listScroll
        };
        listHost.SetResourceReference(Border.BorderBrushProperty, "PrimarySoftBorderBrush");
        left.Children.Add(listHost);

        _txtEmpty.Text = "还没有任何调休规则。右边的表单加一条试试。";
        _txtEmpty.FontSize = 12;
        _txtEmpty.Margin = new Thickness(8, 6, 8, 0);
        _txtEmpty.TextWrapping = TextWrapping.Wrap;
        _txtEmpty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(_txtEmpty, Dock.Bottom);
        left.Children.Add(_txtEmpty);

        Grid.SetColumn(left, 0);
        main.Children.Add(left);

        // ---- 右栏表单 ----
        // 表单套一层滚动条：窗口压矮 / 系统字号放大时，底部按钮与说明仍然能滚到（r129 同款教训）
        var form = new StackPanel { Margin = new Thickness(4, 0, 6, 0) };
        var formScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = form
        };
        Grid.SetColumn(formScroll, 1);
        main.Children.Add(formScroll);

        form.Children.Add(FieldLabel("这一天"));
        _dpDate.FontSize = 14;
        _dpDate.SelectedDateFormat = DatePickerFormat.Long;
        _dpDate.Margin = new Thickness(0, 0, 0, 10);
        _dpDate.SelectedDateChanged += (_, _) => SyncKindEnabled();
        form.Children.Add(_dpDate);

        form.Children.Add(FieldLabel("怎么安排"));
        _cbKind.FontSize = 14;
        _cbKind.Margin = new Thickness(0, 0, 0, 10);
        _cbKind.Items.Add("补课 —— 按某一天的课表上课（调休）");
        _cbKind.Items.Add("放假 —— 这天不上课");
        _cbKind.SelectedIndex = 0;
        _cbKind.SelectionChanged += (_, _) => SyncKindEnabled();
        form.Children.Add(_cbKind);

        form.Children.Add(FieldLabel("按哪一天的课表上课（只补课用）"));
        _dpAs.FontSize = 14;
        _dpAs.SelectedDateFormat = DatePickerFormat.Long;
        _dpAs.Margin = new Thickness(0, 0, 0, 6);
        form.Children.Add(_dpAs);

        _chkInherit.Content = new TextBlock
        {
            Text = "沿用这一天的临时调课 / 代课", FontSize = 13, TextWrapping = TextWrapping.Wrap
        };
        _chkInherit.IsChecked = true;
        _chkInherit.Margin = new Thickness(0, 0, 0, 10);
        _chkInherit.ToolTip = "默认勾选：那天第 3 节有代课，补课搬到这天时代课也一起过来。\n"
                            + "若那天的调课是因为「那天有活动」才调的，补课时取消勾选，只看干净的基础课表。";
        form.Children.Add(_chkInherit);

        form.Children.Add(FieldLabel("备注（可选）"));
        _tbNote.FontSize = 14;
        _tbNote.Padding = new Thickness(8, 6, 8, 6);
        _tbNote.Margin = new Thickness(0, 0, 0, 12);
        _tbNote.ToolTip = "例如「上周三活动停课」。会显示在大屏标签后面，方便同事看懂为什么这么调。";
        form.Children.Add(_tbNote);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var bs = MakePrimaryButton("保存这一天");
        bs.Click += (_, _) => SaveCurrent();
        btns.Children.Add(bs);

        var bd = MakeButton("删除这一天");
        bd.Margin = new Thickness(8, 0, 0, 0);
        bd.Click += (_, _) => DeleteSelected();
        btns.Children.Add(bd);

        var bc = MakeButton("清空表单");
        bc.Margin = new Thickness(8, 0, 0, 0);
        bc.Click += (_, _) => { _list.SelectedItem = null; _dpDate.SelectedDate = DateTime.Today; _dpAs.SelectedDate = null; _tbNote.Text = ""; _cbKind.SelectedIndex = 0; };
        btns.Children.Add(bc);
        form.Children.Add(btns);

        // 批量粘贴（折叠）
        var bBulk = MakeButton("批量粘贴…");
        bBulk.HorizontalAlignment = HAlign.Left;
        bBulk.Click += (_, _) => _bulkPanel.Visibility = _bulkPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        form.Children.Add(bBulk);

        _bulkPanel.Visibility = Visibility.Collapsed;
        _bulkPanel.Margin = new Thickness(0, 8, 0, 0);
        var bulkTip = new TextBlock
        {
            Text = "一行一条，例如：\n2026-11-21 补课 2026-11-18 上周三停课\n2026-11-23 放假 校运会",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        };
        bulkTip.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _bulkPanel.Children.Add(bulkTip);

        _tbBulk.Height = 88;
        _tbBulk.FontSize = 13;
        _tbBulk.AcceptsReturn = true;
        _tbBulk.TextWrapping = TextWrapping.NoWrap;
        _tbBulk.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _tbBulk.Padding = new Thickness(6);
        _bulkPanel.Children.Add(_tbBulk);

        var bParse = MakeButton("解析并添加");
        bParse.HorizontalAlignment = HAlign.Left;
        bParse.Margin = new Thickness(0, 6, 0, 0);
        bParse.Click += (_, _) => BulkAdd();
        _bulkPanel.Children.Add(bParse);
        form.Children.Add(_bulkPanel);

        // 常见用法：右下角空着也是空着，把两种典型场景写清楚（新用户最容易搞混"日期"和"按哪天"）
        var help = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 16, 0, 0)
        };
        help.SetResourceReference(Border.BorderBrushProperty, "PrimarySoftBorderBrush");
        var helpText = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Text = "常见用法\n"
                 + "① 周六补周三的课 → 这一天=周六，类型=补课，按哪天=那周三。\n"
                 + "② 某天不上课（校运会 / 台风）→ 类型=放假。"
        };
        helpText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        help.Child = helpText;
        form.Children.Add(help);

        // 状态行
        _txtStatus.FontSize = 12.5;
        _txtStatus.TextWrapping = TextWrapping.Wrap;
        _txtStatus.Margin = new Thickness(0, 6, 0, 8);
        _txtStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(_txtStatus, 2);
        root.Children.Add(_txtStatus);

        // 底部按钮
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right };
        var close = MakeButton("关闭");
        close.MinWidth = 88;
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
    }

    static TextBlock FieldLabel(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return tb;
    }

    static Button MakeButton(string text) => new()
    {
        Content = text,
        FontSize = 13,
        Padding = new Thickness(12, 7, 12, 7),
        MinHeight = 34
    };

    static Button MakePrimaryButton(string text)
    {
        var b = MakeButton(text);
        b.SetResourceReference(BackgroundProperty, "PrimaryBrush");
        b.Foreground = Brushes.White;
        b.BorderThickness = new Thickness(0);
        return b;
    }

    // ==================== 数据 ====================

    void LoadRules(int selectDay = 0)
    {
        _sf = Store.LoadSchedule();
        _list.Items.Clear();
        var rules = (_sf?.Calendar ?? new List<SchoolCalendarRule>())
            .OrderBy(r => r.DateValue ?? DateTime.MaxValue).ToList();
        foreach (var r in rules)
            _list.Items.Add(new ListBoxItem
            {
                Content = r.DisplayText,
                Tag = r,
                FontSize = 13,
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 2)
            });
        _txtEmpty.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selectDay > 0)
        {
            foreach (ListBoxItem it in _list.Items)
                if (it.Tag is SchoolCalendarRule rr && rr.DateValue?.Day == selectDay) { _list.SelectedItem = it; break; }
        }
        SyncKindEnabled();
    }

    void LoadSelectedIntoForm()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: SchoolCalendarRule r }) return;
        _dpDate.SelectedDate = r.DateValue;
        _cbKind.SelectedIndex = r.IsOff ? 1 : 0;
        _dpAs.SelectedDate = r.AsDateValue;
        _chkInherit.IsChecked = r.InheritOverrides;
        _tbNote.Text = r.Note ?? "";
        SyncKindEnabled();
        _txtStatus.Text = "正在编辑：" + r.DisplayText;
    }

    void SyncKindEnabled()
    {
        bool makeup = _cbKind.SelectedIndex != 1;
        _dpAs.IsEnabled = makeup;
        _chkInherit.IsEnabled = makeup;
    }

    void SaveCurrent()
    {
        if (_dpDate.SelectedDate == null) { _txtStatus.Text = "请先选「这一天」。"; return; }
        bool off = _cbKind.SelectedIndex == 1;
        if (!off && _dpAs.SelectedDate == null)
        {
            _txtStatus.Text = "补课要指定「按哪一天的课表上课」（例如上周三）。只想放假请把「怎么安排」改成放假。";
            return;
        }

        string? warn = CheckPersistence();
        if (warn != null) { _txtStatus.Text = warn; return; }

        var sf = _sf ?? Store.LoadSchedule() ?? new ScheduleFile();
        var rule = new SchoolCalendarRule
        {
            Date = _dpDate.SelectedDate.Value.ToString("yyyy-MM-dd"),
            Kind = off ? CalendarKind.Off : CalendarKind.Makeup,
            AsDate = off ? "" : _dpAs.SelectedDate!.Value.ToString("yyyy-MM-dd"),
            InheritOverrides = _chkInherit.IsChecked == true,
            Note = (_tbNote.Text ?? "").Trim()
        };

        if (rule.DateValue == rule.AsDateValue && rule.AsDateValue != null)
        {
            _txtStatus.Text = "「这一天」和「按哪天」是同一天，不需要设调休。";
            return;
        }

        bool added = SchoolCalendar.SetRule(sf.Calendar, rule);
        Store.SaveSchedule(sf);
        _sf = sf;
        LoadRules(_dpDate.SelectedDate.Value.Day);
        AppEvents.RaiseScheduleChanged();   // 大屏 / 周视图 / 提醒立即按新校历重算
        _txtStatus.Text = (added ? "已新增：" : "已更新：") + rule.DisplayText
            + "　本机已生效；要让教室机也用上，请发布 / 同步课表。";
    }

    void DeleteSelected()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: SchoolCalendarRule r } || r.DateValue == null)
        {
            _txtStatus.Text = "先在左边选一条要删的规则。";
            return;
        }
        string? warn = CheckPersistence();
        if (warn != null) { _txtStatus.Text = warn; return; }
        var sf = _sf ?? Store.LoadSchedule() ?? new ScheduleFile();
        var d = r.DateValue.Value;
        SchoolCalendar.RemoveRule(sf.Calendar, d);
        Store.SaveSchedule(sf);
        _sf = sf;
        LoadRules();
        AppEvents.RaiseScheduleChanged();
        _txtStatus.Text = "已删除 " + d.ToString("MM-dd") + " 的校历规则。";
    }

    void BulkAdd()
    {
        string? warn = CheckPersistence();
        if (warn != null) { _txtStatus.Text = warn; return; }
        var lines = (_tbBulk.Text ?? "").Split('\n')
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (lines.Count == 0) { _txtStatus.Text = "粘贴框是空的。"; return; }

        var sf = _sf ?? Store.LoadSchedule() ?? new ScheduleFile();
        int ok = 0;
        var bad = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            var rule = SchoolCalendar.ParseLine(lines[i], DateTime.Today.Year);
            if (rule == null) { bad.Add("第 " + (i + 1) + " 行看不懂：" + lines[i]); continue; }
            SchoolCalendar.SetRule(sf.Calendar, rule);
            ok++;
        }
        if (ok > 0)
        {
            Store.SaveSchedule(sf);
            _sf = sf;
            LoadRules();
            AppEvents.RaiseScheduleChanged();
        }
        _txtStatus.Text = ok == 0
            ? "没有一条能认出来。格式：2026-11-21 补课 2026-11-18 备注 / 2026-11-23 放假 备注"
            : $"已添加 / 更新 {ok} 条。" + (bad.Count == 0 ? "" : "　" + string.Join("；", bad));
        if (ok > 0) _tbBulk.Text = "";
    }

    static string? CheckPersistence()
        => Store.EnablePersistence ? null : "游客模式不能改校历：请先用管理员 / 教师账号登录。";
}
