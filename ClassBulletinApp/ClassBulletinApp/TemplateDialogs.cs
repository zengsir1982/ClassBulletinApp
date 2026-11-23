using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;
// 全局 using 同时带进了 WinForms，TextBox / CheckBox 会二义（CS0104），这里显式指到 WPF
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace ClassBulletinApp;

/// <summary>
/// r142：作息时间表编辑弹窗（从已删除的「课表基础数据维护 → 作息时间」页搬来）。
/// <para>手动录入课表左栏「作息时间」行右边的编辑图标打开它，只编辑<b>当前选中的那一份</b>，
/// 不再有左边那列模板列表 —— 换模板在下拉里换，编辑只针对手上这份，操作路径更短。</para>
/// <para>★ 两层作息的边界在这里说清楚：<br/>
/// 「保存模板」只影响手动录入用的模子（本机 schedule_templates.json，不进数据包）；<br/>
/// 「保存并设为全校作息」则额外写进 <see cref="ScheduleFile.Bell"/> —— 大屏 / 周视图 / 上下课提醒
/// 读的都是那一份，并且随数据包下发到教室机。</para>
/// </summary>
internal class PeriodTemplateDialog : Window
{
    readonly PeriodTemplate _tpl;
    readonly PeriodTemplate _working;
    readonly TextBox _tbName = new();
    readonly TextBox _tbSheet = new();
    readonly TextBlock _hint = new();
    readonly CheckBox _chkDefault = new();
    readonly bool _canManageData;
    readonly Button _bSchool;

    /// <summary>诊断用：不弹模态框、直接走「保存并设为全校作息」的同一条路径。</summary>
    internal bool DiagCanApplyToSchool => _bSchool.IsEnabled;

    internal void DiagCommitToSchool() => Commit(toSchool: true);

    /// <summary>true = 用户点了「保存并设为全校作息」，调用方据此提示（Bell 已在弹窗内写好）。</summary>
    public bool AppliedToSchool { get; private set; }

    /// <summary>true = 勾了「设为新录课表的默认作息」，调用方负责把同列表里其它项的 IsDefault 清掉。</summary>
    public bool MakeDefault => _chkDefault.IsChecked == true;

    public PeriodTemplateDialog(PeriodTemplate tpl, bool canManageData)
    {
        _tpl = tpl ?? throw new ArgumentNullException(nameof(tpl));
        _canManageData = canManageData;

        // 在副本上改：用户点「取消」时原对象一个字段都不该被动过
        _working = new PeriodTemplate
        {
            Name = tpl.Name, IsBuiltIn = tpl.IsBuiltIn, IsDefault = tpl.IsDefault
        };
        ScheduleSheetText.ParseScheduleSheet(ScheduleSheetText.FormatScheduleSheet(tpl), _working);

        Title = "作息时间";
        Width = 620;
        Height = 640;
        MinWidth = 520;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(18, 16, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 名称 ----
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "名称", FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4),
            Foreground = Res("MutedBrush", System.Windows.Media.Brushes.Gray) });
        _tbName.Text = tpl.Name;
        _tbName.FontSize = 14;
        _tbName.Padding = new Thickness(6, 5, 6, 5);
        head.Children.Add(_tbName);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ---- 说明 ----
        var tip = new TextBlock
        {
            Text = "一行一条：节次写「第1节 08:10-08:55」，公共时间写「大课间 |09:50-10:10|」。"
                 + "公共时间不占节次序号，前后顺序就是它在课表里的位置。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 8)
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(tip, 1);
        root.Children.Add(tip);

        // ---- 正文编辑框 ----
        _tbSheet.Text = ScheduleSheetText.FormatScheduleSheet(tpl);
        _tbSheet.FontSize = 13.5;
        _tbSheet.AcceptsReturn = true;
        _tbSheet.TextWrapping = TextWrapping.NoWrap;
        _tbSheet.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _tbSheet.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _tbSheet.VerticalContentAlignment = VAlign.Top;
        _tbSheet.Padding = new Thickness(8);
        _tbSheet.FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI");
        var sheetBox = new Border
        {
            BorderBrush = Res("LineBrush", System.Windows.Media.Brushes.LightGray),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Child = _tbSheet, Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(sheetBox, 3);
        root.Children.Add(sheetBox);

        // ---- 行操作 ----
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        acts.Children.Add(Small("添加节次", () => InsertLine(period: true)));
        acts.Children.Add(Small("删除节次", () => DeleteLine(period: true)));
        acts.Children.Add(Small("添加公共时间", () => InsertLine(period: false)));
        acts.Children.Add(Small("删除公共时间", () => DeleteLine(period: false)));
        Grid.SetRow(acts, 2);
        root.Children.Add(acts);

        // ---- 提示 + 默认项 ----
        var foot = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        _hint.Text = "把光标放在某行开头点「添加」= 插在这一行前面；放在行尾点「添加」= 插在这一行后面。";
        _hint.FontSize = 11.5;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        foot.Children.Add(_hint);

        _chkDefault.Content = "设为新录课表的默认作息";
        _chkDefault.FontSize = 12.5;
        _chkDefault.IsChecked = tpl.IsDefault;
        _chkDefault.Margin = new Thickness(0, 8, 0, 0);
        foot.Children.Add(_chkDefault);
        Grid.SetRow(foot, 4);
        root.Children.Add(foot);

        // ---- 底栏 ----
        var bar = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var note = new TextBlock
        {
            Text = "「保存模板」只改这台机器的录入用作息；要让大屏 / 提醒也按它走，用右边那个按钮。",
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(note, 0);
        bar.Children.Add(note);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right };
        var bCancel = Ghost("取消");
        bCancel.IsCancel = true;
        bCancel.Click += (_, _) => { DialogResult = false; Close(); };

        var bSave = Ghost("保存模板");
        bSave.IsDefault = true;
        bSave.Click += (_, _) => Commit(toSchool: false);

        var bSchool = new Button
        {
            Content = "保存并设为全校作息", FontSize = 13, Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(8, 0, 0, 0), MinHeight = 32,
            IsEnabled = _canManageData,
            ToolTip = _canManageData
                ? "写进当前课表的作息：大屏、周视图、上下课提醒都按它显示；教室机要等下次发布 / 同步才生效"
                : UserSession.DenyManageData
        };
        _bSchool = bSchool;
        bSchool.SetResourceReference(StyleProperty, "AppPrimaryButton");
        bSchool.Click += (_, _) => Commit(toSchool: true);

        btns.Children.Add(bCancel);
        btns.Children.Add(bSave);
        btns.Children.Add(bSchool);
        Grid.SetColumn(btns, 1);
        bar.Children.Add(btns);
        Grid.SetRow(bar, 5);
        root.Children.Add(bar);

        Content = root;

        _tbSheet.Focus();
        _tbSheet.CaretIndex = 0;
    }

    // ---------- 保存 ----------

    void Commit(bool toSchool)
    {
        string name = (_tbName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            _hint.Text = "名称不能为空。";
            _tbName.Focus();
            return;
        }

        var parsed = new PeriodTemplate();
        ScheduleSheetText.ParseScheduleSheet(_tbSheet.Text, parsed);
        if (parsed.Periods.Count == 0)
        {
            _hint.Text = "至少要有一个节次（形如「第1节 08:10-08:55」）。";
            _tbSheet.Focus();
            return;
        }
        var filled = parsed.Periods.Where(x => !string.IsNullOrWhiteSpace(x.TimeRange)
                                               && !x.TimeRange.Contains("HH:MM")).ToList();
        if (toSchool && filled.Count == 0)
        {
            _hint.Text = "还没填任何节次时间（HH:MM-HH:MM 是占位符），不能设为全校作息。";
            _tbSheet.Focus();
            return;
        }

        // 回写原对象：手录窗口下拉里就是同一个实例，改完立刻看到新名字 / 新节次
        _tpl.Name = name;
        _tpl.Periods = parsed.Periods;
        _tpl.FixedSlots = parsed.FixedSlots;

        if (toSchool) WriteBell(_tpl, filled);
        AppliedToSchool = toSchool;
        // 诊断会在没 Show 过的实例上直接调，这时设 DialogResult 会抛；真实路径 IsLoaded 必为 true
        if (IsLoaded) DialogResult = true;
        Close();
    }

    /// <summary>
    /// 写进当前课表的 Bell。只改磁盘上的 Bell 这一块，不动课表本身；
    /// 写完要抛 <see cref="AppEvents.RaiseScheduleChanged"/>，否则大屏会停在旧时间上（r135 的坑）。
    /// </summary>
    void WriteBell(PeriodTemplate p, List<PeriodTime> filled)
    {
        var bell = new BellSchedule
        {
            Name = p.Name,
            UpdatedAt = DateTime.Now,
            UpdatedFrom = UserSession.Current?.Username ?? Environment.MachineName,
            Periods = filled.Select(x => new PeriodTime { Index = x.Index, TimeRange = x.TimeRange }).ToList(),
            FixedSlots = p.FixedSlots.Select(x => new FixedSlot
            {
                Name = x.Name, AfterPeriod = x.AfterPeriod, TimeRange = x.TimeRange
            }).ToList()
        };
        try
        {
            var disk = Store.LoadSchedule() ?? new ScheduleFile();
            disk.Bell = bell;
            Store.SaveSchedule(disk);
            AppEvents.RaiseScheduleChanged();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("作息没能写进课表：" + ex.Message, "写入失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 行编辑（光标位置规则沿用 r135） ----------

    int CaretLineIndex(out bool atStart)
    {
        string text = _tbSheet.Text ?? "";
        int caret = Math.Min(Math.Max(0, _tbSheet.CaretIndex), text.Length);
        int line = 0, lineStart = 0;
        for (int i = 0; i < caret; i++) if (text[i] == '\n') { line++; lineStart = i + 1; }
        atStart = caret - lineStart <= 0;
        return line;
    }

    void InsertLine(bool period)
    {
        var lines = ScheduleSheetText.SplitSheetLines(_tbSheet.Text);
        int line = CaretLineIndex(out bool atStart);
        int at = lines.Count == 0 ? 0 : Math.Min(lines.Count, Math.Max(0, atStart ? line : line + 1));
        lines.Insert(at, period
            ? "第1节 " + ScheduleSheetText.TimePh
            : "大课间 |" + ScheduleSheetText.TimePh + "|");
        if (period) ScheduleSheetText.RenumberPeriodLines(lines);
        ApplyLines(lines, at, selectTime: true);
        _hint.Text = period
            ? "已插入一个节次（序号自动重排），时间已选中，直接输入即可。"
            : "已插入一条公共时间，时间已选中，直接输入即可；名称可改成 眼保健操 / 午休 / 升旗 等。";
    }

    void DeleteLine(bool period)
    {
        var lines = ScheduleSheetText.SplitSheetLines(_tbSheet.Text);
        if (lines.Count == 0) return;
        int line = CaretLineIndex(out _);
        if (line < 0 || line >= lines.Count) return;

        bool isPublic = lines[line].Contains('|');
        if (isPublic == period)
        {
            _hint.Text = period
                ? "光标所在行是公共时间，要删它请点「删除公共时间」。"
                : "光标所在行是节次，要删它请点「删除节次」。";
            return;
        }
        if (period && lines.Count(l => l.Trim().Length > 0 && !l.Contains('|')) <= 1)
        {
            _hint.Text = "至少要保留一个节次。";
            return;
        }

        string what = lines[line].Trim();
        lines.RemoveAt(line);
        if (period) ScheduleSheetText.RenumberPeriodLines(lines);
        ApplyLines(lines, Math.Min(line, Math.Max(0, lines.Count - 1)), selectTime: false);
        _hint.Text = "已删除：" + what;
    }

    void ApplyLines(List<string> lines, int caretLine, bool selectTime)
    {
        string text = string.Join("\n", lines);
        _tbSheet.Text = text;
        int start = 0;
        for (int i = 0; i < caretLine && i < lines.Count; i++) start += lines[i].Length + 1;
        _tbSheet.Focus();
        int ti = caretLine >= 0 && caretLine < lines.Count
            ? lines[caretLine].IndexOf(ScheduleSheetText.TimePh, StringComparison.Ordinal) : -1;
        if (selectTime && ti >= 0)
        {
            _tbSheet.SelectionStart = start + ti;
            _tbSheet.SelectionLength = ScheduleSheetText.TimePh.Length;
        }
        else
        {
            _tbSheet.SelectionStart = Math.Min(_tbSheet.Text.Length, start);
            _tbSheet.SelectionLength = 0;
        }
    }

    static Button Small(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, FontSize = 12, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    static Button Ghost(string text)
    {
        var b = new Button
        {
            Content = text, FontSize = 13, Padding = new Thickness(14, 7, 14, 7),
            MinHeight = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        b.SetResourceReference(StyleProperty, "AppGhostButton");
        return b;
    }

    internal static System.Windows.Media.Brush Res(string key, System.Windows.Media.Brush fallback)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
}

/// <summary>
/// r142：常用科目库编辑弹窗（从已删除的基础数据维护「科目」页搬来）。
/// <para>一行一科：<c>科目</c> 或 <c>科目|教师|地点</c>，教师与地点可留空。
/// 改完只影响「从科目库导入」时带出来的候选，不动任何已保存的课表。</para>
/// </summary>
internal class SubjectLibraryDialog : Window
{
    readonly SubjectLibrary _lib;
    readonly TextBox _tbName = new();
    readonly TextBox _tbBody = new();
    readonly TextBlock _hint = new();
    readonly CheckBox _chkDefault = new();

    public bool MakeDefault => _chkDefault.IsChecked == true;

    public SubjectLibraryDialog(SubjectLibrary lib)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));

        Title = "常用科目库";
        Width = 520;
        Height = 580;
        MinWidth = 420;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(18, 16, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock
        {
            Text = "名称", FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4),
            Foreground = PeriodTemplateDialog.Res("MutedBrush", System.Windows.Media.Brushes.Gray)
        });
        _tbName.Text = lib.Name;
        _tbName.FontSize = 14;
        _tbName.Padding = new Thickness(6, 5, 6, 5);
        head.Children.Add(_tbName);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var tip = new TextBlock
        {
            Text = "一行一科：只写科目名即可；想连教师 / 地点一起带出来，写成「语文|张老师|601」。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 8)
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(tip, 1);
        root.Children.Add(tip);

        _tbBody.Text = string.Join("\n", lib.Items.Select(i =>
            string.IsNullOrWhiteSpace(i.Teacher) && string.IsNullOrWhiteSpace(i.Location)
                ? i.Subject
                : i.Subject + "|" + i.Teacher + "|" + i.Location));
        _tbBody.FontSize = 13.5;
        _tbBody.AcceptsReturn = true;
        _tbBody.TextWrapping = TextWrapping.Wrap;
        _tbBody.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _tbBody.VerticalContentAlignment = VAlign.Top;
        _tbBody.Padding = new Thickness(8);
        var box = new Border
        {
            BorderBrush = PeriodTemplateDialog.Res("LineBrush", System.Windows.Media.Brushes.LightGray),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Child = _tbBody, Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(box, 2);
        root.Children.Add(box);

        var foot = new StackPanel();
        _hint.Text = "改完点「保存」立即生效；「从科目库导入」会按这份清单带出任课安排。";
        _hint.FontSize = 11.5;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        foot.Children.Add(_hint);
        _chkDefault.Content = "设为默认科目库";
        _chkDefault.FontSize = 12.5;
        _chkDefault.IsChecked = lib.IsDefault;
        _chkDefault.Margin = new Thickness(0, 8, 0, 0);
        foot.Children.Add(_chkDefault);
        Grid.SetRow(foot, 3);
        root.Children.Add(foot);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var bCancel = new Button { Content = "取消", FontSize = 13, Padding = new Thickness(14, 7, 14, 7), MinHeight = 32, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        bCancel.SetResourceReference(StyleProperty, "AppGhostButton");
        bCancel.Click += (_, _) => { DialogResult = false; Close(); };

        var bSave = new Button { Content = "保存", FontSize = 13, Padding = new Thickness(16, 7, 16, 7), MinHeight = 32, IsDefault = true };
        bSave.SetResourceReference(StyleProperty, "AppPrimaryButton");
        bSave.Click += (_, _) => Commit();

        row.Children.Add(bCancel);
        row.Children.Add(bSave);
        Grid.SetRow(row, 4);
        root.Children.Add(row);

        Content = root;
        _tbBody.Focus();
    }

    void Commit()
    {
        string name = (_tbName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            _hint.Text = "名称不能为空。";
            _tbName.Focus();
            return;
        }
        var items = ParseBody(_tbBody.Text);
        if (items.Count == 0)
        {
            _hint.Text = "至少填一个科目。";
            _tbBody.Focus();
            return;
        }
        _lib.Name = name;
        _lib.Items = items;
        if (IsLoaded) DialogResult = true;
        Close();
    }

    /// <summary>一行一科：<c>科目</c> / <c>科目|教师</c> / <c>科目|教师|地点</c>。</summary>
    internal static List<SubjectItem> ParseBody(string? text)
    {
        var list = new List<SubjectItem>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            string subject = parts[0].Trim();
            if (subject.Length == 0) continue;
            list.Add(new SubjectItem
            {
                Subject = subject,
                Teacher = parts.Length > 1 ? parts[1].Trim() : "",
                Location = parts.Length > 2 ? parts[2].Trim() : ""
            });
        }
        return list;
    }
}
