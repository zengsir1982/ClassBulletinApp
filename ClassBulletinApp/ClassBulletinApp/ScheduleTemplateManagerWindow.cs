using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 时间表与科目设置（纯代码 Window，原名「模板预设管理」）：三页签 —— 作息时间表 / 常用科目库 / 整份课表方案。
/// <para>「作息时间表」把节次与公共时间合并成同一个多行编辑框：一行一条，
/// 「第1节 08:10-08:55」是节次行，「大课间 |09:50-10:10|」是公共时间行（不占节次序号，
/// 位置决定它插在第几节之后）。添加/删除按钮按光标所在行操作，比原来填「第几节后」直观。</para>
/// <para>三类模板都是发布端本机私有（schedule_templates.json），不进数据包、不下发。
/// 内置项可设默认但不可删除；自定义项可增删改。所有改动点「完成」时一次性落盘。</para>
/// </summary>
public class ScheduleTemplateManagerWindow : Window
{
    readonly ScheduleTemplates _templates;
    bool _loading;

    /// <summary>时间占位符：插入新行后选中它，用户直接打字就能覆盖（不用先删掉再敲）。</summary>
    const string TimePh = "HH:MM-HH:MM";

    System.Windows.Controls.ListBox _lbPeriod = null!;
    System.Windows.Controls.TextBox _tbPeriodName = null!, _tbSheet = null!;
    System.Windows.Controls.TextBlock _txtSheetHint = null!;
    Button _btnPeriodDel = null!, _btnPeriodDefault = null!, _btnPeriodBell = null!;

    System.Windows.Controls.ListBox _lbSubject = null!;
    System.Windows.Controls.TextBox _tbSubjectName = null!, _tbSubjects = null!;
    Button _btnSubjectDel = null!, _btnSubjectDefault = null!;

    System.Windows.Controls.ListBox _lbPlan = null!;
    System.Windows.Controls.TextBox _tbPlanName = null!, _tbPlanInfo = null!;
    Button _btnPlanDel = null!;

    public ScheduleTemplateManagerWindow(ScheduleTemplates templates)
    {
        _templates = templates ?? ScheduleTemplates.CreateWithBuiltIns();

        Title = "时间表与科目设置";
        Width = 780;
        Height = 640;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;

        var root = new System.Windows.Controls.Grid { Margin = new Thickness(14, 12, 14, 10) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var tabs = new System.Windows.Controls.TabControl();
        System.Windows.Controls.Grid.SetRow(tabs, 0);
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "作息时间表", Content = BuildPeriodPage() });
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "常用科目库", Content = BuildSubjectPage() });
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "整份课表方案", Content = BuildPlanPage() });
        root.Children.Add(tabs);

        var bar = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        System.Windows.Controls.Grid.SetRow(bar, 1);
        bar.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "模板存本机；点「应用到当前课表」后作息随课表下发",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0)
        });
        var btnDone = new Button { Content = "完成", FontSize = 13, Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        btnDone.Click += (_, _) => { Store.SaveScheduleTemplates(_templates); DialogResult = true; Close(); };
        bar.Children.Add(btnDone);
        root.Children.Add(bar);

        Content = root;
        RefreshPeriodList();
        RefreshSubjectList();
        RefreshPlanList();
        GlassChrome.Prepare(this);
    }

    // ---------- 作息时间表 ----------
    System.Windows.UIElement BuildPeriodPage()
    {
        var g = TwoCol(out var left, out var right);
        _lbPeriod = ListBox();
        _lbPeriod.SelectionChanged += (_, _) => LoadPeriod();
        left.Children.Add(new System.Windows.Controls.TextBlock { Text = "作息时间表", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        left.Children.Add(_lbPeriod);
        var add = new Button { Content = "+ 新建作息时间表", FontSize = 12, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        add.Click += (_, _) =>
        {
            var p = new PeriodTemplate { Name = "新作息时间表", Periods = new List<PeriodTime>() };
            for (int i = 1; i <= 7; i++) p.Periods.Add(new PeriodTime { Index = i, TimeRange = TimePh });
            _templates.PeriodTemplates.Add(p);
            RefreshPeriodList();
            _lbPeriod.SelectedItem = p;
        };
        left.Children.Add(add);
        // r129：删除从右侧按钮行挪到这里，紧跟「新建」——建和删在同一列，符合直觉
        _btnPeriodDel = new Button { Content = "删除作息时间表", FontSize = 12, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        _btnPeriodDel.Click += (_, _) => DeleteSelectedPeriodTemplate(confirm: true);
        left.Children.Add(_btnPeriodDel);

        right.Children.Add(FieldLabel("名称"));
        _tbPeriodName = TextBox();
        _tbPeriodName.TextChanged += (_, _) => { if (_loading) return; if (_lbPeriod.SelectedItem is PeriodTemplate p) { p.Name = _tbPeriodName.Text; UpdateListBoxLabel(_lbPeriod, p); } };
        right.Children.Add(_tbPeriodName);

        right.Children.Add(FieldLabel("作息时间表（一行一条：节次写「第1节 08:10-08:55」，公共时间写「大课间 |09:50-10:10|」。"
            + "公共时间不占节次序号，前后顺序就是它在课表里的位置）"));
        _tbSheet = TextBox(300);
        _tbSheet.VerticalContentAlignment = VerticalAlignment.Top;
        _tbSheet.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        _tbSheet.TextChanged += (_, _) => { if (_loading) return; if (_lbPeriod.SelectedItem is PeriodTemplate p) ParseScheduleSheet(_tbSheet.Text, p); };
        right.Children.Add(_tbSheet);

        // r129：四个「添加/删除」排一行，「设为默认」固定在同行最右边
        var actRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 8, 0, 0) };
        actRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var acts2 = new System.Windows.Controls.WrapPanel();
        acts2.Children.Add(SheetButton("添加节次", () => InsertSheetLine(period: true)));
        acts2.Children.Add(SheetButton("删除节次", () => DeleteSheetLine(period: true)));
        acts2.Children.Add(SheetButton("添加公共时间", () => InsertSheetLine(period: false)));
        acts2.Children.Add(SheetButton("删除公共时间", () => DeleteSheetLine(period: false)));
        System.Windows.Controls.Grid.SetColumn(acts2, 0);
        actRow.Children.Add(acts2);

        var bellActs = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        _btnPeriodDefault = new Button { Content = "设为默认", FontSize = 12, Padding = new Thickness(10, 5, 10, 5) };
        _btnPeriodDefault.Click += (_, _) => { if (_lbPeriod.SelectedItem is PeriodTemplate p) { foreach (var x in _templates.PeriodTemplates) x.IsDefault = x == p; RefreshPeriodListKeepSel(p); } };
        bellActs.Children.Add(_btnPeriodDefault);

        // r135：与「设为默认」分开 —— 这个按钮把模板内容拷贝进 ScheduleFile.Bell
        _btnPeriodBell = new Button
        {
            Content = "应用到当前课表", FontSize = 12, Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(6, 0, 0, 0), IsEnabled = UserSession.CanManageData,
            ToolTip = UserSession.CanManageData
                ? "把这份作息的节次时间与公共时间写进当前课表；个人用本机立即生效，学校用随数据包下发"
                : UserSession.DenyManageData
        };
        _btnPeriodBell.Click += (_, _) => ApplyBellToSchedule(confirm: true);
        bellActs.Children.Add(_btnPeriodBell);

        System.Windows.Controls.Grid.SetColumn(bellActs, 1);
        actRow.Children.Add(bellActs);
        right.Children.Add(actRow);

        _txtSheetHint = new System.Windows.Controls.TextBlock
        {
            Text = "把光标放在某行开头点「添加」= 插在这一行前面；放在行尾点「添加」= 插在这一行后面。",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        };
        right.Children.Add(_txtSheetHint);
        return g;
    }

    /// <summary>
    /// 删除选中的作息时间表。confirm=false 供诊断直接调用（不能弹模态框，会把 --ui-check 卡住）。
    /// </summary>
    internal void DeleteSelectedPeriodTemplate(bool confirm)
    {
        if (_lbPeriod.SelectedItem is not PeriodTemplate p || p.IsBuiltIn) return;
        if (confirm && System.Windows.MessageBox.Show(
                "删除作息时间表「" + p.Name + "」？\n\n已经保存好的课表不受影响（它们的节次时间不变），"
                + "只是这份模板不再出现在列表里。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _templates.PeriodTemplates.Remove(p);
        RefreshPeriodList();
        _txtSheetHint.Text = "已删除作息时间表「" + p.Name + "」。";
    }

    static Button SheetButton(string text, Action onClick)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    void RefreshPeriodList()
    {
        _loading = true;
        _lbPeriod.Items.Clear();
        foreach (var p in _templates.PeriodTemplates) _lbPeriod.Items.Add(p);
        if (_lbPeriod.Items.Count > 0) _lbPeriod.SelectedIndex = 0; else LoadPeriod();
        _loading = false;
    }
    void RefreshPeriodListKeepSel(PeriodTemplate sel)
    {
        int idx = _lbPeriod.SelectedIndex;
        _loading = true;
        _lbPeriod.Items.Clear();
        foreach (var p in _templates.PeriodTemplates) _lbPeriod.Items.Add(p);
        _lbPeriod.SelectedIndex = idx >= 0 && idx < _lbPeriod.Items.Count ? idx : 0;
        _loading = false;
    }
    void LoadPeriod()
    {
        _loading = true;
        if (_lbPeriod.SelectedItem is PeriodTemplate p)
        {
            _tbPeriodName.Text = p.Name;
            _tbSheet.Text = FormatScheduleSheet(p);
            bool builtIn = p.IsBuiltIn;
            _btnPeriodDel.IsEnabled = !builtIn;
            _btnPeriodDel.Content = builtIn ? "内置·不可删" : "删除作息时间表";
            _btnPeriodDefault.IsEnabled = true;
            _btnPeriodDefault.Content = p.IsDefault ? "✓ 当前默认" : "设为默认";
            _txtSheetHint.Text = "共 " + p.Periods.Count + " 个节次、" + p.FixedSlots.Count + " 条公共时间。"
                + "把光标放在某行开头点「添加」= 插在该行前；放在行尾点「添加」= 插在该行后。";
        }
        else
        {
            _tbPeriodName.Text = _tbSheet.Text = "";
            _btnPeriodDel.IsEnabled = _btnPeriodDefault.IsEnabled = false;
        }
        _loading = false;
        RefreshBellMarks();
    }

    // ---------- r135：课表作息 ----------

    internal Button DiagBellButton => _btnPeriodBell;
    internal string DiagHint => _txtSheetHint.Text;
    internal void DiagSelectPeriod(PeriodTemplate p) => _lbPeriod.SelectedItem = p;

    /// <summary>
    /// 列表里给「当前课表作息」那份打绿标、给内容被改过但没重新应用的打橙标。
    /// 只改 ListBoxItem 的前景与 ToolTip，不换数据结构 —— 名称框每敲一个字都会触发
    /// TextChanged，重建 ItemsSource 会把光标顶到开头（见 UpdateListBoxLabel 注释）。
    /// </summary>
    void RefreshBellMarks()
    {
        var bell = Store.LoadSchedule()?.Bell;
        _lbPeriod.UpdateLayout();
        foreach (var item in _lbPeriod.Items)
        {
            if (_lbPeriod.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ListBoxItem li) continue;
            li.Foreground = System.Windows.Media.Brushes.Black;
            li.FontWeight = FontWeights.Normal;
            li.ToolTip = null;
            if (item is not PeriodTemplate p || bell == null
                || !string.Equals(p.Name, bell.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (BellContentEquals(p, bell))
            {
                li.Foreground = System.Windows.Media.Brushes.SeaGreen;
                li.FontWeight = FontWeights.Bold;
                li.ToolTip = "当前课表作息";
            }
            else
            {
                li.Foreground = System.Windows.Media.Brushes.DarkOrange;
                li.ToolTip = "有未应用的改动：点「应用到当前课表」推给教室机";
            }
        }
    }

    static bool BellContentEquals(PeriodTemplate p, BellSchedule bell)
    {
        if (p.Periods.Count != bell.Periods.Count || p.FixedSlots.Count != bell.FixedSlots.Count) return false;
        foreach (var x in p.Periods)
            if (!bell.Periods.Any(y => y.Index == x.Index && string.Equals(y.TimeRange, x.TimeRange, StringComparison.Ordinal))) return false;
        foreach (var f in p.FixedSlots)
            if (!bell.FixedSlots.Any(g => g.AfterPeriod == f.AfterPeriod
                && string.Equals(g.Name, f.Name, StringComparison.Ordinal)
                && string.Equals(g.TimeRange, f.TimeRange, StringComparison.Ordinal))) return false;
        return true;
    }

    /// <summary>把选中模板拷贝进当前课表的 Bell。confirm=false 供诊断直接调用（不弹模态框）。</summary>
    internal void ApplyBellToSchedule(bool confirm)
    {
        if (!UserSession.CanManageData)
        {
            if (confirm) System.Windows.MessageBox.Show(UserSession.DenyManageData, "没有权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            _txtSheetHint.Text = UserSession.DenyManageData;
            return;
        }
        if (_lbPeriod.SelectedItem is not PeriodTemplate p) return;

        var filled = p.Periods.Where(x => !string.IsNullOrWhiteSpace(x.TimeRange) && !x.TimeRange.Contains("HH:MM")).ToList();
        if (filled.Count == 0)
        {
            string msg = "这份作息时间表还没有填节次时间（HH:MM-HH:MM 是占位符）。请先填好再应用。";
            if (confirm) System.Windows.MessageBox.Show(msg, "不能应用", MessageBoxButton.OK, MessageBoxImage.Warning);
            _txtSheetHint.Text = msg;
            return;
        }

        var bell = new BellSchedule
        {
            Name = p.Name,
            UpdatedAt = DateTime.Now,
            UpdatedFrom = UserSession.Current?.Username ?? Environment.MachineName,
            Periods = filled.Select(x => new PeriodTime { Index = x.Index, TimeRange = x.TimeRange }).ToList(),
            FixedSlots = p.FixedSlots.Select(x => new FixedSlot { Name = x.Name, AfterPeriod = x.AfterPeriod, TimeRange = x.TimeRange }).ToList()
        };

        var sf = Store.LoadSchedule() ?? new ScheduleFile();
        bool hasManual = sf.Classes.Any(c => c.IsManual);
        sf.Bell = bell;
        Store.SaveSchedule(sf);
        AppEvents.RaiseScheduleChanged();   // 只写磁盘界面不会刷新，大屏会停在旧时间上（r113 提醒踩过同样的坑）
        RefreshBellMarks();

        var settings = Store.LoadSettings();
        bool hasFeed = settings.BoardSyncEnabled || !string.IsNullOrWhiteSpace(settings.ErYiDataDir)
                       || settings.UpdateSourceKind != "Off";
        string text = hasFeed
            ? "已应用到当前课表。教室机要等下次发布 / 同步数据包后才生效。"
            : "已应用到当前课表（本机立即生效）。";
        if (hasManual) text += "\n手动录入的班仍用保存时的时间，不受本次影响。";
        if (!Store.EnablePersistence) text += "\n（游客模式：本次未写入磁盘）";
        _txtSheetHint.Text = text;
        if (confirm) System.Windows.MessageBox.Show(text, "已应用", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>模板 → 多行作息时间表：节次行 + 紧跟它的公共时间行。</summary>
    internal static string FormatScheduleSheet(PeriodTemplate p)
    {
        var lines = new List<string>();
        var slots = p.FixedSlots.ToList();
        var used = new HashSet<FixedSlot>();

        void EmitAfter(int after)
        {
            foreach (var f in slots)
            {
                if (used.Contains(f) || f.AfterPeriod != after) continue;
                lines.Add(f.Name + " |" + (string.IsNullOrWhiteSpace(f.TimeRange) ? TimePh : f.TimeRange) + "|");
                used.Add(f);
            }
        }

        EmitAfter(0);
        foreach (var pt in p.Periods.OrderBy(x => x.Index))
        {
            lines.Add("第" + pt.Index + "节 " + (string.IsNullOrWhiteSpace(pt.TimeRange) ? TimePh : pt.TimeRange));
            EmitAfter(pt.Index);
        }
        // 兜底：AfterPeriod 指向不存在的节次（外部数据），放到最后，别让这条公共时间凭空消失
        foreach (var f in slots)
            if (!used.Contains(f))
                lines.Add(f.Name + " |" + (string.IsNullOrWhiteSpace(f.TimeRange) ? TimePh : f.TimeRange) + "|");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 多行作息时间表 → 模板。含「|」的行是公共时间（AfterPeriod = 它前面最近的节次号，没有则为 0），
    /// 其余行是节次。顺序即位置，所以用户不需要再写「第几节后」。
    /// </summary>
    internal static void ParseScheduleSheet(string? text, PeriodTemplate p)
    {
        var periods = new List<PeriodTime>();
        var slots = new List<FixedSlot>();
        int lastIndex = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Contains('|'))
            {
                var name = line.Split('|')[0].Trim();
                if (name.Length == 0) continue;
                slots.Add(new FixedSlot { Name = name, TimeRange = ExtractTimeRange(line), AfterPeriod = lastIndex });
            }
            else
            {
                var pm = Regex.Match(line, @"第\s*(\d+)\s*节");
                int idx = pm.Success ? int.Parse(pm.Groups[1].Value) : periods.Count + 1;
                periods.Add(new PeriodTime { Index = idx, TimeRange = ExtractTimeRange(line) });
                lastIndex = idx;
            }
        }
        p.Periods = periods;
        p.FixedSlots = slots;
    }

    static string ExtractTimeRange(string line)
    {
        var m = Regex.Match(line, @"(\d{1,2}:\d{2}\s*[-~至]\s*\d{1,2}:\d{2})");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", "") : "";
    }

    static List<string> SplitSheetLines(string? text)
        => string.IsNullOrEmpty(text) ? new List<string>() : text.Split('\n').ToList();

    /// <summary>光标所在行：按字符偏移数 \n，不用 GetLineIndexFromCharacterIndex（它数的是渲染行，换行会错位）。</summary>
    int CurrentLineIndex(out bool atStart)
    {
        string text = _tbSheet.Text ?? "";
        int caret = Math.Min(Math.Max(0, _tbSheet.CaretIndex), text.Length);
        int line = 0, lineStart = 0;
        for (int i = 0; i < caret; i++) if (text[i] == '\n') { line++; lineStart = i + 1; }
        atStart = caret - lineStart <= 0;
        return line;
    }

    /// <summary>节次行按出现顺序重排 第1节…第N节（公共时间行原位置不动）。</summary>
    static void RenumberPeriodLines(List<string> lines)
    {
        int n = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.Contains('|')) continue;
            n++;
            var m = Regex.Match(line, @"^第\s*\d+\s*节\s*");
            string rest = m.Success ? line.Substring(m.Length) : line;
            lines[i] = "第" + n + "节 " + rest;
        }
    }

    void InsertSheetLine(bool period)
    {
        if (_lbPeriod.SelectedItem is not PeriodTemplate p) return;
        var lines = SplitSheetLines(_tbSheet.Text);
        int line = CurrentLineIndex(out bool atStart);
        int at = lines.Count == 0 ? 0 : Math.Min(lines.Count, Math.Max(0, atStart ? line : line + 1));
        lines.Insert(at, period ? "第1节 " + TimePh : "大课间 |" + TimePh + "|");
        if (period) RenumberPeriodLines(lines);
        ApplySheet(p, lines, at, selectTime: true);
        _txtSheetHint.Text = period
            ? "已插入一个节次（序号自动重排），时间已选中，直接输入即可。"
            : "已插入一条公共时间，时间已选中，直接输入即可；名称可改成 眼保健操 / 午休 / 升旗 等。";
    }

    void DeleteSheetLine(bool period)
    {
        if (_lbPeriod.SelectedItem is not PeriodTemplate p) return;
        var lines = SplitSheetLines(_tbSheet.Text);
        if (lines.Count == 0) return;
        int line = CurrentLineIndex(out _);
        if (line < 0 || line >= lines.Count) return;

        bool isPublic = lines[line].Contains('|');
        if (isPublic == period)
        {
            _txtSheetHint.Text = period
                ? "光标所在行是公共时间，要删它请点「删除公共时间」。"
                : "光标所在行是节次，要删它请点「删除节次」。";
            return;
        }
        if (period && lines.Count(l => l.Trim().Length > 0 && !l.Contains('|')) <= 1)
        {
            _txtSheetHint.Text = "至少要保留一个节次。";
            return;
        }

        string what = lines[line].Trim();
        lines.RemoveAt(line);
        if (period) RenumberPeriodLines(lines);
        ApplySheet(p, lines, Math.Min(line, Math.Max(0, lines.Count - 1)), selectTime: false);
        _txtSheetHint.Text = "已删除：" + what;
    }

    void ApplySheet(PeriodTemplate p, List<string> lines, int caretLine, bool selectTime)
    {
        string text = string.Join("\n", lines);
        _tbSheet.Text = text;
        ParseScheduleSheet(text, p);          // 显式再解析：_loading 期间 TextChanged 会被跳过
        int start = 0;
        for (int i = 0; i < caretLine && i < lines.Count; i++) start += lines[i].Length + 1;
        _tbSheet.Focus();
        int ti = caretLine >= 0 && caretLine < lines.Count ? lines[caretLine].IndexOf(TimePh, StringComparison.Ordinal) : -1;
        if (selectTime && ti >= 0)
        {
            _tbSheet.SelectionStart = start + ti;
            _tbSheet.SelectionLength = TimePh.Length;
        }
        else
        {
            _tbSheet.SelectionStart = Math.Min(_tbSheet.Text.Length, start);
            _tbSheet.SelectionLength = 0;
        }
    }

    // ---------- 常用科目库 ----------
    System.Windows.UIElement BuildSubjectPage()
    {
        var g = TwoCol(out var left, out var right);
        _lbSubject = ListBox();
        _lbSubject.SelectionChanged += (_, _) => LoadSubject();
        left.Children.Add(new System.Windows.Controls.TextBlock { Text = "科目库", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        left.Children.Add(_lbSubject);
        var add = new Button { Content = "+ 新建科目库", FontSize = 12, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        add.Click += (_, _) =>
        {
            var s = new SubjectLibrary { Name = "新科目库", Items = new List<SubjectItem>() };
            _templates.SubjectLibraries.Add(s);
            RefreshSubjectList();
            _lbSubject.SelectedItem = s;
        };
        left.Children.Add(add);

        right.Children.Add(FieldLabel("名称"));
        _tbSubjectName = TextBox();
        _tbSubjectName.TextChanged += (_, _) => { if (_loading) return; if (_lbSubject.SelectedItem is SubjectLibrary s) { s.Name = _tbSubjectName.Text; UpdateListBoxLabel(_lbSubject, s); } };
        right.Children.Add(_tbSubjectName);

        right.Children.Add(FieldLabel("科目清单（一行一科：科目 或 科目|教师|地点；教师可留空）"));
        _tbSubjects = TextBox(240);
        _tbSubjects.TextChanged += (_, _) => { if (_loading) return; if (_lbSubject.SelectedItem is SubjectLibrary s) s.Items = ParseSubjects(_tbSubjects.Text); };
        right.Children.Add(_tbSubjects);

        var acts = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _btnSubjectDefault = new Button { Content = "设为默认", FontSize = 12, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        _btnSubjectDefault.Click += (_, _) => { if (_lbSubject.SelectedItem is SubjectLibrary s) { foreach (var x in _templates.SubjectLibraries) x.IsDefault = x == s; RefreshSubjectListKeepSel(s); } };
        _btnSubjectDel = new Button { Content = "删除", FontSize = 12, Padding = new Thickness(10, 5, 10, 5) };
        _btnSubjectDel.Click += (_, _) => { if (_lbSubject.SelectedItem is SubjectLibrary s && !s.IsBuiltIn) { _templates.SubjectLibraries.Remove(s); RefreshSubjectList(); } };
        acts.Children.Add(_btnSubjectDefault);
        acts.Children.Add(_btnSubjectDel);
        right.Children.Add(acts);
        return g;
    }

    void RefreshSubjectList()
    {
        _loading = true;
        _lbSubject.Items.Clear();
        foreach (var s in _templates.SubjectLibraries) _lbSubject.Items.Add(s);
        if (_lbSubject.Items.Count > 0) _lbSubject.SelectedIndex = 0; else LoadSubject();
        _loading = false;
    }
    void RefreshSubjectListKeepSel(SubjectLibrary sel)
    {
        int idx = _lbSubject.SelectedIndex;
        _loading = true;
        _lbSubject.Items.Clear();
        foreach (var s in _templates.SubjectLibraries) _lbSubject.Items.Add(s);
        _lbSubject.SelectedIndex = idx >= 0 && idx < _lbSubject.Items.Count ? idx : 0;
        _loading = false;
    }
    void LoadSubject()
    {
        _loading = true;
        if (_lbSubject.SelectedItem is SubjectLibrary s)
        {
            _tbSubjectName.Text = s.Name;
            _tbSubjects.Text = string.Join("\n", s.Items.Select(i =>
                string.IsNullOrWhiteSpace(i.Teacher) && string.IsNullOrWhiteSpace(i.Location)
                    ? i.Subject
                    : i.Subject + "|" + i.Teacher + "|" + i.Location));
            bool builtIn = s.IsBuiltIn;
            _btnSubjectDel.IsEnabled = !builtIn;
            _btnSubjectDel.Content = builtIn ? "内置·不可删" : "删除";
            _btnSubjectDefault.IsEnabled = true;
            _btnSubjectDefault.Content = s.IsDefault ? "✓ 当前默认" : "设为默认";
        }
        else
        {
            _tbSubjectName.Text = _tbSubjects.Text = "";
            _btnSubjectDel.IsEnabled = _btnSubjectDefault.IsEnabled = false;
        }
        _loading = false;
    }

    static List<SubjectItem> ParseSubjects(string text)
    {
        var list = new List<SubjectItem>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            var item = new SubjectItem { Subject = parts[0].Trim() };
            if (parts.Length > 1) item.Teacher = parts[1].Trim();
            if (parts.Length > 2) item.Location = parts[2].Trim();
            list.Add(item);
        }
        return list;
    }

    // ---------- 整份课表方案 ----------
    System.Windows.UIElement BuildPlanPage()
    {
        var g = TwoCol(out var left, out var right);
        _lbPlan = ListBox();
        _lbPlan.SelectionChanged += (_, _) => LoadPlan();
        left.Children.Add(new System.Windows.Controls.TextBlock { Text = "已存方案", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        left.Children.Add(_lbPlan);
        left.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "方案在「手动录入课表」窗口里点「另存为课表方案」创建；在此可重命名或删除。",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        right.Children.Add(FieldLabel("方案名"));
        _tbPlanName = TextBox();
        _tbPlanName.TextChanged += (_, _) => { if (_loading) return; if (_lbPlan.SelectedItem is SavedSchedulePlan p) { p.Name = _tbPlanName.Text; UpdateListBoxLabel(_lbPlan, p); } };
        right.Children.Add(_tbPlanName);
        right.Children.Add(FieldLabel("信息"));
        _tbPlanInfo = TextBox(160);
        _tbPlanInfo.IsReadOnly = true;
        _tbPlanInfo.TextWrapping = TextWrapping.Wrap;
        right.Children.Add(_tbPlanInfo);

        var acts = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _btnPlanDel = new Button { Content = "删除方案", FontSize = 12, Padding = new Thickness(10, 5, 10, 5) };
        _btnPlanDel.Click += (_, _) => { if (_lbPlan.SelectedItem is SavedSchedulePlan p) { _templates.Plans.Remove(p); RefreshPlanList(); } };
        acts.Children.Add(_btnPlanDel);
        right.Children.Add(acts);
        return g;
    }

    void RefreshPlanList()
    {
        _loading = true;
        _lbPlan.Items.Clear();
        foreach (var p in _templates.Plans.OrderByDescending(x => x.SavedAt)) _lbPlan.Items.Add(p);
        if (_lbPlan.Items.Count > 0) _lbPlan.SelectedIndex = 0; else LoadPlan();
        _loading = false;
    }
    void RefreshPlanListKeepSel(SavedSchedulePlan sel)
    {
        int idx = _lbPlan.SelectedIndex;
        _loading = true;
        _lbPlan.Items.Clear();
        foreach (var p in _templates.Plans.OrderByDescending(x => x.SavedAt)) _lbPlan.Items.Add(p);
        _lbPlan.SelectedIndex = idx >= 0 && idx < _lbPlan.Items.Count ? idx : 0;
        _loading = false;
    }
    void LoadPlan()
    {
        _loading = true;
        if (_lbPlan.SelectedItem is SavedSchedulePlan p)
        {
            _tbPlanName.Text = p.Name;
            int classes = p.Data.Count;
            int lessons = p.Data.Values.Sum(d => d.Values.Sum(l => l.Count));
            _tbPlanInfo.Text = $"保存时间：{p.SavedAt:yyyy-MM-dd HH:mm}\n节次模板：{p.PeriodTemplateName}\n覆盖 {classes} 天 · 共 {lessons} 节课";
            _btnPlanDel.IsEnabled = true;
        }
        else
        {
            _tbPlanName.Text = _tbPlanInfo.Text = "";
            _btnPlanDel.IsEnabled = false;
        }
        _loading = false;
    }

    // ---------- 通用小控件 ----------
    /// <summary>
    /// 左右两栏。★ r129：两栏都套一层 ScrollViewer —— 原来窗口一缩小（或系统缩放 125% 时
    /// 可用高度变矮），右侧「添加节次 / 删除节次 / 设为默认」那一排会被裁掉，用户以为没有这些按钮。
    /// 现在内容超出就出滚动条，按钮永远滚得到。
    /// </summary>
    static System.Windows.Controls.Grid TwoCol(out System.Windows.Controls.StackPanel left, out System.Windows.Controls.StackPanel right)
    {
        var g = new System.Windows.Controls.Grid { Margin = new Thickness(10) };
        g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left = new System.Windows.Controls.StackPanel();
        right = new System.Windows.Controls.StackPanel();

        var lsv = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0),
            Content = left
        };
        var rsv = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0),
            Content = right
        };

        System.Windows.Controls.Grid.SetColumn(lsv, 0);
        System.Windows.Controls.Grid.SetColumn(rsv, 2);
        System.Windows.Controls.Grid.SetRow(lsv, 0);
        System.Windows.Controls.Grid.SetRow(rsv, 0);
        g.Children.Add(lsv);
        g.Children.Add(rsv);
        return g;
    }

    /// <summary>
    /// 只更新 ListBox 中某一项显示的文字，不重建 ItemsSource、更不回写输入框。
    /// 名称输入框每敲一个字都会触发 TextChanged，若此时清空重建列表 → SelectionChanged
    /// → LoadPeriod/LoadSubject/LoadPlan 重新给 TextBox.Text 赋值 → 光标被顶到开头。
    /// </summary>
    static void UpdateListBoxLabel(System.Windows.Controls.ListBox lb, object item)
    {
        if (lb.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
        if (lb.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ListBoxItem li) return;
        var tb = FindFirst<System.Windows.Controls.TextBlock>(li);
        if (tb != null) tb.Text = item.ToString() ?? "";
    }

    static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindFirst<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    static System.Windows.Controls.ListBox ListBox() => new()
    {
        Height = 340,
        FontSize = 13,
        DisplayMemberPath = "Name",
        SelectionMode = System.Windows.Controls.SelectionMode.Single
    };

    static System.Windows.Controls.TextBox TextBox(double height = 34) => new()
    {
        FontSize = 13,
        Height = height,
        VerticalContentAlignment = VerticalAlignment.Top,
        AcceptsReturn = height > 40,
        TextWrapping = height > 40 ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    static System.Windows.Controls.TextBlock FieldLabel(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = System.Windows.Media.Brushes.DimGray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 3)
    };
}
