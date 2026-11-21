using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleCore.Models;
using ScheduleCore.Services;

// Button / Image / Orientation / RadioButton / MessageBox / ComboBox 等在 GlobalUsings 里按项目惯例处理；
// 这里所有可能歧义的类型一律写全名，避免 WinForms + WPF 双用导致 CS0104。

namespace ClassBulletinApp;

/// <summary>
/// 手动录入基础课表（纯代码 Window）。
/// <para>整班「星期 × 节次」网格，行数由所选节次模板决定；单击格用左侧「课程设置」里选中的课填充，
/// 双击弹 <see cref="CellEditorWindow"/> 微调（全周 / 单周 / 双周 / 单双周两门），右键清空，
/// 按住格子拖到另一格 = 移动（目标有课则两格交换）。</para>
/// <para>本窗口只产出 <see cref="Result"/>（一个 ClassSchedule），实际落盘由 MainWindow 复用
/// ApplyImportedSchedule 完成——保证"只替换目标班、其他班不动"与导入链路语义一致。</para>
/// <para>落盘动作有两个：①「另存为方案」写本机 schedule_templates.json 的 Plans；
/// ② r125 起课程设置（任课配对）按班级自动静默存进 ClassSetups，下次进来自动调出。</para>
/// </summary>
public class ManualScheduleWindow : Window
{
    /// <summary>点「保存到该班」后回传的班级课表（DialogResult=true 且 <see cref="DeleteClassName"/> 为空时有效）。</summary>
    public ClassSchedule? Result { get; private set; }

    /// <summary>
    /// 点「删除该班」后回传要删除的班名（DialogResult=true 时有效）。
    /// 由 MainWindow 落盘 —— 本窗口不直接写 schedule.json，避免和导入链路抢同一份文件。
    /// </summary>
    public string? DeleteClassName { get; private set; }

    readonly ScheduleFile _source;
    readonly ScheduleTemplates _templates;

    System.Windows.Controls.ComboBox _cbClass = null!;
    System.Windows.Controls.ComboBox _cbPeriod = null!;
    System.Windows.Controls.ComboBox _cbSubjectLib = null!;
    System.Windows.Controls.ComboBox _cbPlan = null!;
    System.Windows.Controls.TextBox _txtPlanName = null!;
    System.Windows.Controls.TextBlock _txtHint = null!;
    System.Windows.Controls.ScrollViewer _gridHost = null!;
    System.Windows.Controls.Grid _grid = null!;
    Button _btnDeleteClass = null!;

    // ---- 科目优先批量录入 ----
    readonly List<SubjectPair> _pairs = new();
    System.Windows.Controls.ListBox _lbPairs = null!;
    Button _btnPairEdit = null!, _btnPairDel = null!;
    int _totalPeriods = 7;

    /// <summary>构造期间为 false：下拉框初始化会触发 SelectionChanged，避免提前重建/套用。</summary>
    bool _ready;
    /// <summary>拖动结束后 Button 还会补发一次 Click，用它吞掉那次「填充」。</summary>
    bool _suppressClick;

    // 单击 = 填充，双击 = 编辑：靠延时区分（Button 双击会连发两次 Click）
    System.Windows.Threading.DispatcherTimer? _clickTimer;
    (int day, int period) _pendingKey;
    Button? _pendingBtn;

    // 拖动移动课程
    readonly Dictionary<(int day, int period), Button> _cellButtons = new();
    (int day, int period)? _dragFrom;
    System.Windows.Point _dragOrigin;
    bool _dragging;
    Button? _dragHighlight;

    // 拖动时「浮起的课程卡」：根 Grid 上一层 Canvas 覆盖层（不吃鼠标、不参与布局）
    System.Windows.Controls.Canvas _dragLayer = null!;
    System.Windows.Controls.Border? _dragGhost;
    Button? _dragSourceBtn;

    Dictionary<int, Dictionary<int, List<Lesson>>> _data = new();
    bool _dirty;

    static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五" };
    const int Days = 5;
    const string NoPlan = "（不使用方案）";

    public ManualScheduleWindow(ScheduleFile? source, ScheduleTemplates templates)
    {
        _source = source ?? new ScheduleFile();
        _templates = templates ?? ScheduleTemplates.CreateWithBuiltIns();

        Title = "手动录入基础课表";
        Width = 1040;
        Height = 660;
        MinWidth = 880;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;

        var root = new System.Windows.Controls.Grid { Margin = new Thickness(16, 14, 16, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 顶部工具
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 网格
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部按钮

        var toolbar = BuildToolbar();
        System.Windows.Controls.Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _txtHint = new System.Windows.Controls.TextBlock
        {
            Text = "提示：手动录入的课表与电子表格 / 自动更新导入的课表分开存放、互不覆盖（同名班保存时会自动改名）。"
                 + "空格单击即填；已有课的格子拖动可移动/交换、双击可改、右键清空。",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8)
        };
        System.Windows.Controls.Grid.SetRow(_txtHint, 1);
        root.Children.Add(_txtHint);

        _gridHost = new System.Windows.Controls.ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(2)
        };

        // 主体：左侧「任课清单」+ 右侧课表网格（取代原来只有网格的单一区）
        var work = new System.Windows.Controls.Grid { Margin = new Thickness(0, 2, 0, 0) };
        work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(252) });
        work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pairPanel = BuildPairPanel();
        System.Windows.Controls.Grid.SetColumn(pairPanel, 0);
        work.Children.Add(pairPanel);
        System.Windows.Controls.Grid.SetColumn(_gridHost, 1);
        work.Children.Add(_gridHost);
        System.Windows.Controls.Grid.SetRow(work, 2);
        root.Children.Add(work);

        var bottomBar = BuildBottomBar();
        System.Windows.Controls.Grid.SetRow(bottomBar, 3);
        root.Children.Add(bottomBar);

        // 拖动浮起卡片的覆盖层：放在最后（第 5 个子元素，前面四个仍是 0/1/2/3 行），
        // Canvas 默认不吃鼠标，坐标与根 Grid 完全一致，比 AdornerLayer 更可控（截图/诊断里也看得见）。
        _dragLayer = new System.Windows.Controls.Canvas { IsHitTestVisible = false, ClipToBounds = false };
        System.Windows.Controls.Panel.SetZIndex(_dragLayer, 100);
        System.Windows.Controls.Grid.SetRow(_dragLayer, 0);
        System.Windows.Controls.Grid.SetRowSpan(_dragLayer, 4);
        root.Children.Add(_dragLayer);

        Content = root;

        // 初始化：班级列表 / 模板下拉 / 方案下拉 / 载入第一个班
        foreach (var c in _source.Classes) _cbClass.Items.Add(c.ClassName);
        // 只在课程设置里出现过的班（上次输了名字但还没保存到班）也要能选到，否则下次调不回来
        foreach (var s in _templates.ClassSetups)
            if (!string.IsNullOrWhiteSpace(s.ClassName)
                && !_source.Classes.Any(c => string.Equals(c.ClassName, s.ClassName, StringComparison.OrdinalIgnoreCase)))
                _cbClass.Items.Add(s.ClassName);
        _cbClass.Items.Add(NewClassItem);
        if (_cbClass.Items.Count > 0) _cbClass.SelectedIndex = 0;
        foreach (var p in _templates.PeriodTemplates) _cbPeriod.Items.Add(p);
        if (_cbPeriod.Items.Count > 0)
            _cbPeriod.SelectedItem = _templates.DefaultPeriodTemplate ?? _templates.PeriodTemplates[0];
        foreach (var s in _templates.SubjectLibraries) _cbSubjectLib.Items.Add(s);
        if (_cbSubjectLib.Items.Count > 0)
            _cbSubjectLib.SelectedItem = _templates.DefaultSubjectLibrary ?? _templates.SubjectLibraries[0];
        RefreshPlanCombo(null);

        _cbClass.SelectionChanged += (_, _) => OnClassSelectionChanged();
        // 切节次模板要立刻按新行数重建（原来没订阅，换模板要等下次重建才生效）
        _cbPeriod.SelectionChanged += (_, _) => { if (_ready) RebuildGrid(); };
        _cbPlan.SelectionChanged += (_, _) => { if (_ready) ApplyPlan(); };

        // 拖动落子统一在窗口层处理（格子按钮的 MouseMove 在真机上收不全）
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragUp;
        LostMouseCapture += (_, _) => { if (_dragging) CancelDrag(); };

        _ready = true;
        LoadClassIntoGrid();

        GlassChrome.Prepare(this);
    }

    /// <summary>重填方案下拉；selectName 为 null 或找不到时选「不使用方案」。</summary>
    void RefreshPlanCombo(string? selectName)
    {
        _ready = false;   // 重填会触发 SelectionChanged，别让它连锁套用
        try
        {
            _cbPlan.Items.Clear();
            _cbPlan.Items.Add(NoPlan);
            foreach (var p in _templates.Plans) _cbPlan.Items.Add(p);
            var wanted = string.IsNullOrWhiteSpace(selectName)
                ? null
                : _templates.Plans.FirstOrDefault(p => string.Equals(p.Name, selectName!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (wanted != null) _cbPlan.SelectedItem = wanted;
            else _cbPlan.SelectedIndex = 0;
        }
        finally { _ready = true; }
    }

    /// <summary>选中课表方案 → 把方案内容套到当前网格（基于现有方案改，省得从空白排）。</summary>
    void ApplyPlan()
    {
        if (_cbPlan.SelectedItem is not SavedSchedulePlan plan || plan.Data.Count == 0) return;
        if (_dirty && System.Windows.MessageBox.Show(
                "当前网格有未保存的改动，套用方案「" + plan.Name + "」会覆盖它。继续？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            RefreshPlanCombo(null);
            return;
        }
        // 方案里记了节次模板，套用时一并切过去（模板不存在就保持当前）
        if (!string.IsNullOrWhiteSpace(plan.PeriodTemplateName))
        {
            var pt = _templates.PeriodTemplates.FirstOrDefault(p =>
                string.Equals(p.Name, plan.PeriodTemplateName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pt != null && !ReferenceEquals(_cbPeriod.SelectedItem, pt)) _cbPeriod.SelectedItem = pt;
        }
        _data = CloneData(plan.Data);
        _dirty = true;
        RebuildGrid();
        _txtHint.Text = "已套用课表方案「" + plan.Name + "」，可直接在上面改，改完点「保存到该班」。";
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
        PersistSetup();
    }

    System.Windows.Controls.Grid BuildToolbar()
    {
        // 用 Grid 严格分列对齐：原来 WrapPanel 里每个 Labelled 自带 Margin.Top=14，
        // 而 WrapPanel 不认子项的 VerticalAlignment，下拉框 / 标签 / 按钮高低错位。
        var bar = new System.Windows.Controls.Grid { Height = 36, Margin = new Thickness(0, 0, 0, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 0 班级标签
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); // 1 班级下拉
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 2 节次标签
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); // 3 节次下拉
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 4 科目库标签
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) }); // 5 科目库下拉
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 6 方案标签
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) }); // 7 方案下拉
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 8 弹性
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 9 清空按钮
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 10 删除班级按钮

        void Put(System.Windows.UIElement el, int col)
        {
            Grid.SetColumn(el, col);
            bar.Children.Add(el);
        }
        void Lbl(string text, int col) => Put(new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        }, col);

        Lbl("班级", 0);
        _cbClass = new System.Windows.Controls.ComboBox { Width = 160, FontSize = 13, IsEditable = true, VerticalAlignment = VerticalAlignment.Center };
        Put(_cbClass, 1);

        Lbl("节次模板", 2);
        _cbPeriod = new System.Windows.Controls.ComboBox { Width = 130, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Put(_cbPeriod, 3);

        Lbl("科目库", 4);
        _cbSubjectLib = new System.Windows.Controls.ComboBox { Width = 104, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Put(_cbSubjectLib, 5);

        Lbl("课表方案", 6);
        _cbPlan = new System.Windows.Controls.ComboBox
        {
            Width = 158, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "选一个已保存的方案，直接在上面改（会覆盖当前网格）"
        };
        Put(_cbPlan, 7);

        var btnClear = new Button { Content = "清空网格", FontSize = 13, Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center };
        btnClear.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show("清空当前班级网格里的全部课？（其他班不受影响）", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _data = new Dictionary<int, Dictionary<int, List<Lesson>>>();
            _dirty = true;
            RebuildGrid();
        };
        Put(btnClear, 9);

        // r129：删除班级（只对手动录入的班开放，导入 / 自动更新的班删了也会被下一次更新加回来）
        _btnDeleteClass = new Button
        {
            Content = "删除该班",
            FontSize = 13,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _btnDeleteClass.Click += (_, _) => DeleteClass();
        Put(_btnDeleteClass, 10);
        return bar;
    }

    System.Windows.Controls.Grid BuildBottomBar()
    {
        var bar = new System.Windows.Controls.Grid { Margin = new Thickness(0, 8, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumn(left, 0);
        left.Children.Add(new System.Windows.Controls.TextBlock { Text = "方案名：", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        _txtPlanName = new System.Windows.Controls.TextBox { Width = 160, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_txtPlanName);
        var btnPlan = new Button { Content = "另存为课表方案", FontSize = 13, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        btnPlan.Click += (_, _) => SaveAsPlan();
        left.Children.Add(btnPlan);
        bar.Children.Add(left);

        var right = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        System.Windows.Controls.Grid.SetColumn(right, 1);
        var btnCancel = new Button { Content = "取消", FontSize = 13, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnSave = new Button { Content = "保存到该班", FontSize = 13, Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        btnSave.Click += (_, _) => SaveToClass();
        right.Children.Add(btnCancel);
        right.Children.Add(btnSave);
        bar.Children.Add(right);
        return bar;
    }

    /// <summary>「新建班级」这个占位项：选中它表示网格不对应任何现有班。</summary>
    const string NewClassItem = "（新建班级）";

    /// <summary>网格里当前实际显示的班名（用于切班被取消时把下拉退回去）。</summary>
    string _shownClass = "";

    /// <summary>正在按程序改下拉选中项（含初始化 / 回滚）：此时 SelectionChanged 不算用户切班。</summary>
    bool _inClassSwitch;

    /// <summary>
    /// 当前要编辑的班级名。
    /// <para>★ <b>IsEditable 的 ComboBox 在 SelectionChanged 回调里 <c>Text</c> 仍是上一个选中项</b>
    /// （r125 在单格编辑器的科目下拉踩过同一个坑；r128 这里同样中招：选「七年级(2)班」网格却仍是
    /// 「七年级(1)班」的课，用户看到的就是"进去的课表和刚更新的课表不一致"）。
    /// 所以：切班回调期间只信 <see cref="ComboBox.SelectedItem"/>；回调之外用户手改了输入框
    /// （Text 与选中项不同）才以 Text 为准，这样"直接输入新班名"依旧可用。</para>
    /// </summary>
    string CurrentClassName
    {
        get
        {
            string sel = (_cbClass.SelectedItem as string ?? "").Trim();
            string txt = (_cbClass.Text ?? "").Trim();
            if (!_inClassSwitch && txt.Length > 0 && !string.Equals(txt, sel, StringComparison.Ordinal))
                return txt == NewClassItem ? "" : txt;      // 用户手输了新班名
            if (sel.Length > 0) return sel == NewClassItem ? "" : sel;
            return txt == NewClassItem ? "" : txt;
        }
    }

    void OnClassSelectionChanged()
    {
        if (_inClassSwitch) return;      // 程序在改选中项（初始化 / 回滚），不是用户切班
        _inClassSwitch = true;
        try { LoadClassIntoGrid(); }
        finally { _inClassSwitch = false; }
    }

    /// <summary>把下拉退回「网格里正在显示的那个班」——用户取消切班时不能让选中项和内容错开。</summary>
    void RevertClassSelection()
    {
        _inClassSwitch = true;
        try
        {
            string want = string.IsNullOrEmpty(_shownClass) ? NewClassItem : _shownClass;
            if (!_cbClass.Items.Contains(want)) _cbClass.Items.Add(want);
            _cbClass.SelectedItem = want;
            _cbClass.Text = want;        // IsEditable 下 Text 不一定跟着 SelectedItem 走，显式同步
        }
        finally { _inClassSwitch = false; }
    }

    void LoadClassIntoGrid(string? className = null)
    {
        string name = className ?? CurrentClassName;
        if (_dirty && System.Windows.MessageBox.Show("当前班级有未保存的改动，切换班级会丢失。仍要切换？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            RevertClassSelection();
            return;
        }
        var cs = _source.Classes.FirstOrDefault(c => c.ClassName == name);
        _data = cs == null ? new Dictionary<int, Dictionary<int, List<Lesson>>>() : CloneData(cs.Data);
        _dirty = false;
        _shownClass = name;
        ApplySetupForClass(name, cs);
        UpdateClassButtons();
        RebuildGrid();
    }

    /// <summary>「删除该班」：手动班永久删；导入的班也能删（会提示"下次更新会回来"）。</summary>
    void UpdateClassButtons()
    {
        if (_btnDeleteClass == null) return;
        var cs = _source.Classes.FirstOrDefault(c => c.ClassName == _shownClass);
        _btnDeleteClass.IsEnabled = cs != null;
        _btnDeleteClass.ToolTip = cs == null
            ? "这个班还没保存过（只是输入了班名），没有可删的课表"
            : cs.IsManual
                ? "删除「" + _shownClass + "」这份手动录入的课表（课程设置也一起删除）"
                : "从本机课表里移除「" + _shownClass + "」。注意：它是导入的课表，"
                  + "下次自动更新 / 重新导入 Excel 时还会回来。";
    }

    /// <summary>
    /// 切到某班时自动调出上次保存的课程设置（r125）。没有存档就留空并提示可以导入 / 提取；
    /// 有存档则同时恢复该班上次用的节次模板、科目库、课表方案下拉。
    /// <para>r129：手动录入的班会把自己的作息时间表名记在 <see cref="ClassSchedule.PeriodTemplateName"/>，
    /// 优先按它恢复 —— 即使模板改名了、课程设置丢了，重新打开也能对上。</para>
    /// </summary>
    void ApplySetupForClass(string className, ClassSchedule? cls)
    {
        var setup = _templates.FindSetup(className);
        _pairs.Clear();
        if (setup != null)
        {
            foreach (var p in setup.Pairs) _pairs.Add(p.Clone());
            SelectByName(_cbPeriod, setup.PeriodTemplateName, _templates.PeriodTemplates);
            SelectByName(_cbSubjectLib, setup.SubjectLibraryName, _templates.SubjectLibraries);
            RefreshPlanCombo(setup.PlanName);
        }
        else
        {
            RefreshPlanCombo(null);
        }
        if (!string.IsNullOrWhiteSpace(cls?.PeriodTemplateName))
            SelectByName(_cbPeriod, cls!.PeriodTemplateName, _templates.PeriodTemplates);
        RefreshPairs();

        // 先说清「这个班是哪来的」——手动录入与导入/自动更新是两套课表，不说清会以为改的是同一份
        string lead = string.IsNullOrWhiteSpace(className) ? ""
            : cls == null
                ? "「" + className + "」是新班级，保存后成为独立的手动录入课表（不影响导入的课表）。"
            : cls.IsManual
                ? "「" + className + "」是手动录入的课表，与导入 / 自动更新的课表分开存放、互不覆盖。"
                : "「" + className + "」来自电子表格 / 自动更新导入。在这里保存会另存为「"
                  + ClassSchedule.WithManualSuffix(className) + "」，不会改动它。";

        string setupInfo = setup != null && _pairs.Count > 0
            ? "已自动调出课程设置（" + _pairs.Count + " 门，" + setup.SavedAt.ToString("MM-dd HH:mm") + " 保存），改了会自动记住。"
            : string.IsNullOrWhiteSpace(className) ? ""
            : "还没有课程设置：可点「导入任课表(Excel)」或「从现有课表提取」，之后进来自动调出。";

        _txtHint.Text = lead + (lead.Length > 0 && setupInfo.Length > 0 ? "  " : "") + setupInfo;
        _txtHint.Foreground = setup != null && _pairs.Count > 0
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.DimGray;

        // ★ 大屏 / 周视图显示的是「基础课表 + 临时调课 / 代课」，本窗口只显示基础课表。
        //   不说明的话，自动更新后再进来会觉得"课表不一致"（其实差异就是这几条调代课）。
        int ov = 0;
        try
        {
            if (!string.IsNullOrWhiteSpace(className))
                ov = Store.LoadOverrides().Count(o => !o.Suspended
                        && string.Equals(o.ClassName, className, StringComparison.Ordinal));
        }
        catch { }
        if (ov > 0 && _txtHint.Text.Length > 0)
        {
            _txtHint.Text = _txtHint.Text.TrimEnd('。')
                + "。本窗口编辑的是基础课表；此班另有 " + ov + " 条临时调课 / 代课只在大屏生效。";
        }
    }

    /// <summary>把当前班的课程设置 + 下拉选择静默写回 schedule_templates.json（按班级各存一份）。</summary>
    void PersistSetup(string? nameOverride = null)
    {
        if (!_ready) return;
        string name = nameOverride ?? CurrentClassName;
        if (string.IsNullOrWhiteSpace(name)) return;   // 还没选/输入班级时不存
        _templates.UpsertSetup(new ClassSetup
        {
            ClassName = name,
            PeriodTemplateName = (_cbPeriod.SelectedItem as PeriodTemplate)?.Name ?? "",
            SubjectLibraryName = (_cbSubjectLib.SelectedItem as SubjectLibrary)?.Name ?? "",
            PlanName = (_cbPlan.SelectedItem as SavedSchedulePlan)?.Name ?? "",
            SavedAt = DateTime.Now,
            Pairs = _pairs.Select(p => p.Clone()).ToList()
        });
        Store.SaveScheduleTemplates(_templates);
    }

    static void SelectByName<T>(System.Windows.Controls.ComboBox cb, string? name, IReadOnlyList<T> items) where T : class
    {
        if (cb == null || string.IsNullOrWhiteSpace(name)) return;
        string key = name!.Trim();
        var hit = items.FirstOrDefault(i => string.Equals(i.ToString(), key, StringComparison.OrdinalIgnoreCase));
        if (hit != null) cb.SelectedItem = hit;
    }

    static Dictionary<int, Dictionary<int, List<Lesson>>> CloneData(Dictionary<int, Dictionary<int, List<Lesson>>> src)
    {
        var d = new Dictionary<int, Dictionary<int, List<Lesson>>>();
        foreach (var day in src)
        {
            var inner = new Dictionary<int, List<Lesson>>();
            foreach (var per in day.Value)
                inner[per.Key] = per.Value.Select(l => new Lesson
                {
                    Period = l.Period, Subject = l.Subject, Teacher = l.Teacher,
                    Location = l.Location, Week = l.Week, TimeRange = l.TimeRange
                }).ToList();
            d[day.Key] = inner;
        }
        return d;
    }

    void RebuildGrid()
    {
        var pt = _cbPeriod.SelectedItem as PeriodTemplate;
        int periods = pt?.Periods.Count ?? 7;
        // 老数据可能有超出模板节数的节（如导入的 8 节），一并显示，别把它们藏起来
        foreach (var day in _data.Values)
            foreach (var per in day.Keys)
                if (per > periods) periods = per;
        if (periods < 1) periods = 1;
        _totalPeriods = periods; // 供「自动铺到空位」使用

        // 行计划：正课节 + 公共时间（大课间/眼保健操/午休/升旗，不占节次序号）
        var fixedSlots = (pt?.FixedSlots ?? new List<FixedSlot>()).OrderBy(f => f.AfterPeriod).ToList();
        var plan = new List<(int period, FixedSlot? fx)>();
        foreach (var f in fixedSlots.Where(f => f.AfterPeriod <= 0)) plan.Add((0, f));
        for (int p = 1; p <= periods; p++)
        {
            plan.Add((p, null));
            foreach (var f in fixedSlots.Where(f => f.AfterPeriod == p)) plan.Add((0, f));
        }

        var g = new System.Windows.Controls.Grid { Margin = new Thickness(4) };
        _cellButtons.Clear();   // 拖动命中测试靠它反查 (day, period)
        for (int c = 0; c < Days + 1; c++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(58) : new GridLength(1, GridUnitType.Star), MinWidth = c == 0 ? 58 : 96 });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (_, fx) in plan)
            g.RowDefinitions.Add(new RowDefinition { Height = fx != null ? GridLength.Auto : new GridLength(56) });

        void Put(System.Windows.UIElement el, int row, int col)
        {
            System.Windows.Controls.Grid.SetRow(el, row);
            System.Windows.Controls.Grid.SetColumn(el, col);
            g.Children.Add(el);
        }

        Put(new System.Windows.Controls.TextBlock { Text = "节次", FontSize = 12, Foreground = System.Windows.Media.Brushes.DimGray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        for (int d = 0; d < Days; d++)
            Put(new System.Windows.Controls.TextBlock { Text = DayNames[d], FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 0, d + 1);

        int rowIdx = 1;
        foreach (var (period, fx) in plan)
        {
            if (fx != null)
            {
                // 公共时间：灰底横条跨整行，不可点、不参与排课
                var bar = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF3, 0xF6)),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(2, 2, 2, 2),
                    Padding = new Thickness(8, 3, 8, 3),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = fx.Name + (string.IsNullOrWhiteSpace(fx.TimeRange) ? "" : "　" + fx.TimeRange),
                        FontSize = 11,
                        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0x5E, 0x5A)),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                System.Windows.Controls.Grid.SetRow(bar, rowIdx);
                System.Windows.Controls.Grid.SetColumn(bar, 0);
                System.Windows.Controls.Grid.SetColumnSpan(bar, Days + 1);
                g.Children.Add(bar);
                rowIdx++;
                continue;
            }

            string time = pt?.Periods.FirstOrDefault(x => x.Index == period)?.TimeRange ?? "";
            var head = new System.Windows.Controls.StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(new System.Windows.Controls.TextBlock { Text = "第" + period + "节", FontSize = 12, HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            head.Children.Add(new System.Windows.Controls.TextBlock { Text = time, FontSize = 10, Foreground = System.Windows.Media.Brushes.DimGray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            Put(head, rowIdx, 0);

            for (int d = 0; d < Days; d++)
            {
                int day = d, per = period;
                var cell = MakeCell(day, per, time);
                Put(cell, rowIdx, d + 1);
                _cellButtons[(day, per)] = cell;
            }
            rowIdx++;
        }
        _grid = g;
        _gridHost.Content = g;
    }

    System.Windows.Controls.Button MakeCell(int day, int period, string time)
    {
        var (text, brush) = CellDisplay(day, period);
        var btn = new Button
        {
            Content = text,
            FontSize = 12,
            Background = brush,
            Margin = new Thickness(2),
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            ToolTip = CellTip(day, period)
        };
        btn.Click += (_, _) => OnCellClick(day, period, btn);
        btn.MouseDoubleClick += (_, _) =>
        {
            if (_clickTimer != null) { _clickTimer.Stop(); _clickTimer = null; } // 双击不填充，直接编辑
            OpenCellEditor(day, period);
        };
        btn.MouseRightButtonUp += (_, e) =>
        {
            if (_data.TryGetValue(day, out var dm2) && dm2.ContainsKey(period))
            {
                RemoveCell(day, period);
                _dirty = true;
                UpdateCellVisual(btn, day, period);
                _txtHint.Text = "已清空 " + DayNames[day] + " 第" + period + "节。";
                _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
            }
            e.Handled = true;
        };

        // ---- r125 拖动移动：按住已排课的格子拖到另一格 = 移动；目标有课则两格交换 ----
        // ★ 坐标要在这里记（只在格子按钮上挂事件不够：真机拖动时 Button 会被 WPF 的
        //   点击逻辑吞掉后续 MouseMove，实测 12 次移动只有 1 次到达格子），
        //   真正的 move / up 由窗口级 Preview 事件统一处理（见 OnDragMove / OnDragUp）。
        btn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _suppressClick = false;                 // 新的一下按下：把上次拖动的抑制标记清掉
            if (!HasCell(day, period)) { _dragFrom = null; return; }   // 空格不参与拖动
            _dragFrom = (day, period);
            _dragOrigin = e.GetPosition(this);
            _dragging = false;
            ClearDragHighlight();
        };
        return btn;
    }

    /// <summary>窗口级：按住格子移动超过阈值 → 进入拖动，并高亮鼠标下的目标格。</summary>
    void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragFrom == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragOrigin.X) < 8 && Math.Abs(pos.Y - _dragOrigin.Y) < 8) return;
            _dragging = true;
            if (_clickTimer != null) { _clickTimer.Stop(); _clickTimer = null; }   // 别让待触发的填充插进来
            // 浮起：源格留个虚影，光标带一张卡走
            if (_cellButtons.TryGetValue(_dragFrom.Value, out var src)) ShowDragGhost(src);
            _txtHint.Text = "拖到目标格松手：空格=移动，已有课=两格交换。";
            _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
        }
        MoveDragGhost(e.GetPosition(_dragLayer));
        HighlightCell(HitCell(e.GetPosition(_grid)));
    }

    /// <summary>
    /// 生成「浮起的课程卡」：圆角蓝色卡片 + 投影 + 轻微放大旋转，像从网格里被拎起来。
    /// 尺寸取源格实际大小，文字取源格内容。
    /// </summary>
    void ShowDragGhost(Button src)
    {
        HideDragGhost();
        _dragSourceBtn = src;
        src.Opacity = 0.35;        // 原位留个虚影，像卡片被拎起来了
        double w = Math.Max(88, src.ActualWidth - 4);
        double h = Math.Max(40, src.ActualHeight - 4);
        var card = new System.Windows.Controls.Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(9),
            Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0x3C, 0x6F, 0xD1),
                System.Windows.Media.Color.FromRgb(0x1E, 0x4F, 0xA3), 90),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Opacity = 0.95,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16, ShadowDepth = 5, Direction = 270, Opacity = 0.38,
                Color = System.Windows.Media.Colors.Black
            },
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new System.Windows.Media.TransformGroup
            {
                Children =
                {
                    new System.Windows.Media.ScaleTransform(1.06, 1.06),
                    new System.Windows.Media.RotateTransform(2.5)
                }
            },
            Child = new System.Windows.Controls.TextBlock
            {
                Text = (src.Content as string ?? "").Replace("\n", " "),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _dragGhost = card;
        _dragLayer.Children.Add(card);
    }

    void MoveDragGhost(System.Windows.Point pos)
    {
        if (_dragGhost == null) return;
        // 光标落在卡片右下偏中一点，像"捏着卡片一角"，不会把目标格完全挡死
        System.Windows.Controls.Canvas.SetLeft(_dragGhost, pos.X - _dragGhost.Width / 2 + 8);
        System.Windows.Controls.Canvas.SetTop(_dragGhost, pos.Y - _dragGhost.Height / 2 - 10);
    }

    void HideDragGhost()
    {
        if (_dragSourceBtn != null) { _dragSourceBtn.Opacity = 1.0; _dragSourceBtn = null; }
        if (_dragGhost != null) { _dragLayer.Children.Remove(_dragGhost); _dragGhost = null; }
    }

    /// <summary>截图诊断：把浮起卡片停在网格中部（真实拖动过程中抓不到稳定画面）。</summary>
    internal void DiagShowDragCard()
    {
        Button? src = null;
        foreach (var kv in _cellButtons)
            if (HasCell(kv.Key.day, kv.Key.period)) { src = kv.Value; break; }
        if (src == null) return;
        ShowDragGhost(src);
        // ★ 只能用 TransformToAncestor(根元素)：浮卡 Canvas 是网格的兄弟节点，不是它的祖先，
        //   直接 TransformToAncestor(_dragLayer) 会抛异常（卡片就停在 0,0）。
        var rootEl = (System.Windows.FrameworkElement)Content;
        var c = src.TransformToAncestor(rootEl).Transform(
            new System.Windows.Point(src.ActualWidth / 2, src.ActualHeight / 2));
        MoveDragGhost(new System.Windows.Point(c.X + 14, c.Y - 8));
    }

    /// <summary>窗口级：松手时落子。没真拖动过就交给原来的单击填充逻辑。</summary>
    void OnDragUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragFrom == null) return;
        EndDrag(e.GetPosition(_grid));
    }

    bool HasCell(int day, int period)
        => _data.TryGetValue(day, out var dm) && dm.TryGetValue(period, out var lm) && lm.Count > 0;

    /// <summary>把窗口坐标里的元素往上找，反查是哪个课程格。</summary>
    (int day, int period)? HitCell(System.Windows.Point gridPoint)
    {
        if (_grid == null) return null;
        var el = _grid.InputHitTest(gridPoint) as System.Windows.DependencyObject;
        while (el != null && !ReferenceEquals(el, _grid))
        {
            if (el is Button b)
            {
                foreach (var kv in _cellButtons)
                    if (ReferenceEquals(kv.Value, b)) return kv.Key;
                return null;
            }
            el = System.Windows.Media.VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    void HighlightCell((int day, int period)? key)
    {
        Button? btn = null;
        if (key != null) _cellButtons.TryGetValue(key.Value, out btn);
        if (ReferenceEquals(btn, _dragHighlight)) return;
        ClearDragHighlight();
        if (btn == null) return;
        _dragHighlight = btn;
        btn.BorderThickness = new Thickness(2);
        btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x4F, 0xA3));
    }

    void ClearDragHighlight()
    {
        if (_dragHighlight == null) return;
        _dragHighlight.BorderThickness = new Thickness(1);
        _dragHighlight.BorderBrush = System.Windows.Media.Brushes.Transparent;
        _dragHighlight = null;
    }

    void CancelDrag()
    {
        _dragFrom = null;
        _dragging = false;
        HideDragGhost();
        ClearDragHighlight();
    }

    void EndDrag(System.Windows.Point gridPoint)
    {
        var from = _dragFrom;
        bool dragged = _dragging;
        _dragFrom = null;
        _dragging = false;
        HideDragGhost();
        ClearDragHighlight();
        if (from == null || !dragged) return;
        _suppressClick = true;   // 松手后 Button 还会补发一次 Click，别让它去填充

        var target = HitCell(gridPoint);
        if (target == null || target.Value == from.Value)
        {
            _txtHint.Text = "已取消拖动。";
            _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }
        bool swapped = MoveOrSwap(_data, from.Value, target.Value);
        _dirty = true;
        RebuildGrid();
        _txtHint.Text = (swapped ? "已把 " : "已把 ")
            + DayNames[from.Value.day] + "第" + from.Value.period + "节"
            + (swapped ? " 与 " : " 移到 ")
            + DayNames[target.Value.day] + "第" + target.Value.period + "节"
            + (swapped ? " 两格交换。" : "。");
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    /// <summary>
    /// 把 from 格的课搬到 to 格；to 原本有课则两格内容互换（不丢课）。返回 true 表示发生了交换。
    /// </summary>
    internal static bool MoveOrSwap(
        Dictionary<int, Dictionary<int, List<Lesson>>> data, (int day, int period) from, (int day, int period) to)
    {
        if (!data.TryGetValue(from.day, out var fd) || !fd.TryGetValue(from.period, out var fromLessons) || fromLessons.Count == 0)
            return false;
        if (!data.TryGetValue(to.day, out var td) || !td.TryGetValue(to.period, out var toLessons) || toLessons.Count == 0)
            toLessons = null;
        bool hasTarget = toLessons != null;

        if (!data.TryGetValue(to.day, out var dest)) data[to.day] = dest = new Dictionary<int, List<Lesson>>();
        dest[to.period] = fromLessons;

        if (hasTarget)
        {
            if (!data.TryGetValue(from.day, out var src)) data[from.day] = src = new Dictionary<int, List<Lesson>>();
            src[from.period] = toLessons!;   // 必须用交换前抓到的引用，dest 刚被覆盖
        }
        else if (data.TryGetValue(from.day, out var src2))
        {
            src2.Remove(from.period);
        }
        return hasTarget;
    }

    (string text, System.Windows.Media.Brush brush) CellDisplay(int day, int period)
    {
        var lessons = _data.TryGetValue(day, out var dm) && dm.TryGetValue(period, out var lm) ? lm : new List<Lesson>();
        if (lessons.Count == 0) return ("+", System.Windows.Media.Brushes.White);
        if (lessons.Count == 1)
        {
            var l = lessons[0];
            return (string.IsNullOrWhiteSpace(l.Subject) ? "+"
                : l.Subject + (string.IsNullOrWhiteSpace(l.Teacher) ? "" : "\n" + l.Teacher) + WeekTag(l.Week),
                System.Windows.Media.Brushes.White);
        }
        return (string.Join("\n", lessons.Select(l => (l.Week == 1 ? "单 " : l.Week == 2 ? "双 " : "") + l.Subject)),
            System.Windows.Media.Brushes.AliceBlue);
    }

    string CellTip(int day, int period)
    {
        var lessons = _data.TryGetValue(day, out var dm) && dm.TryGetValue(period, out var lm) ? lm : new List<Lesson>();
        return BuildToolTip(lessons) + (lessons.Count == 0
            ? "\n空格：单击用左侧选中课程填充"
            : "\n已有课：拖动可移动/交换，双击可改，右键清空（单击不会覆盖）");
    }

    void UpdateCellVisual(System.Windows.Controls.Button btn, int day, int period)
    {
        var (text, brush) = CellDisplay(day, period);
        btn.Content = text;
        btn.Background = brush;
        btn.ToolTip = CellTip(day, period);
    }

    /// <summary>单击/双击区分：单击延时 220ms 后填充；同一格第二下视为双击，取消填充交给编辑器。</summary>
    void OnCellClick(int day, int period, System.Windows.Controls.Button btn)
    {
        if (_suppressClick) { _suppressClick = false; return; }   // 刚拖完，别顺手把课又填一遍
        // r126：已有课的格子单击不填充（免得手一抖把排好的课冲掉）——它只负责被拖走，
        // 要改内容请双击；空格才是"点一下就填"。
        if (HasCell(day, period))
        {
            if (_clickTimer != null) { _clickTimer.Stop(); _clickTimer = null; }
            _txtHint.Text = DayNames[day] + "第" + period + "节已有课：拖动可移动/交换，双击可改，右键清空。";
            _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }
        if (_clickTimer != null)
        {
            _clickTimer.Stop();
            _clickTimer = null;
            if (_pendingKey == (day, period)) return;      // 同一格第二下 → 双击
            FillCell(_pendingKey.day, _pendingKey.period, _pendingBtn); // 换了格子：先把上一格补上
        }
        _pendingKey = (day, period);
        _pendingBtn = btn;
        _clickTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer!.Stop();
            _clickTimer = null;
            FillCell(day, period, btn);
        };
        _clickTimer.Start();
    }

    void FillCell(int day, int period, System.Windows.Controls.Button? btn)
    {
        if (_lbPairs.SelectedItem is not SubjectPair pair)
        {
            _txtHint.Text = "请先在左侧「课程设置」里选一门课，再点格子填充。";
            _txtHint.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        if (HasCell(day, period))   // 兜底：已有课的格子绝不被填充覆盖
        {
            _txtHint.Text = DayNames[day] + "第" + period + "节已有课，未改动；要换课请双击或先右键清空。";
            _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }
        SetCell(day, period, pair.ToLessons(period));
        _dirty = true;
        if (btn != null) UpdateCellVisual(btn, day, period);
        _txtHint.Text = "已填 " + DayNames[day] + " 第" + period + "节：" + pair;
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    // ---------- 科目优先：任课清单 + 批量安排 ----------
    System.Windows.UIElement BuildPairPanel()
    {
        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "课程设置", FontSize = 14, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        _lbPairs = new System.Windows.Controls.ListBox { Height = 214, FontSize = 12 };
        _lbPairs.SelectionChanged += (_, _) => UpdatePairButtons();
        sp.Children.Add(_lbPairs);

        var row1 = SmallRow();
        row1.Children.Add(SmallButton("新增", AddPair));
        _btnPairEdit = SmallButton("编辑", EditPair);
        row1.Children.Add(_btnPairEdit);
        _btnPairDel = SmallButton("删除", DeletePair);
        row1.Children.Add(_btnPairDel);
        sp.Children.Add(row1);

        var row2 = SmallRow();
        row2.Children.Add(SmallButton("从科目库带入", ImportFromLibrary));
        row2.Children.Add(SmallButton("从现有课表提取", ExtractFromSchedule));
        sp.Children.Add(row2);

        var row2b = SmallRow();
        row2b.Children.Add(SmallButton("导入任课表(Excel)", ImportSubjectTable));
        row2b.Children.Add(SmallButton("下载任课表模板", DownloadSubjectTemplate));
        sp.Children.Add(row2b);

        // r126：入口从「数据管理」页挪到这里 —— 录入时想到要改时间表/科目库，就地能进
        var row2c = SmallRow();
        row2c.Children.Add(SmallButton("时间表与科目设置…", OpenTemplateManager));
        sp.Children.Add(row2c);

        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "空格单击即填；已有课的格子拖动可移动/交换、双击可改（单击不会覆盖）。",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 3)
        });

        UpdatePairButtons();
        return sp;
    }

    static System.Windows.Controls.WrapPanel SmallRow() => new() { Margin = new Thickness(0, 6, 0, 0) };

    static Button SmallButton(string text, Action action)
    {
        var b = new Button
        {
            Content = text, FontSize = 12, Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 5, 0)
        };
        b.Click += (_, _) => action();
        return b;
    }

    void UpdatePairButtons()
    {
        var p = _lbPairs.SelectedItem as SubjectPair;
        _btnPairEdit.IsEnabled = _btnPairDel.IsEnabled = p != null;
    }

    void RefreshPairs()
    {
        _lbPairs.Items.Clear();
        foreach (var p in _pairs) _lbPairs.Items.Add(p);
        if (_lbPairs.Items.Count > 0) _lbPairs.SelectedIndex = 0;
        UpdatePairButtons();
    }

    void AddPair()
    {
        var ed = new PairEditorWindow(null, _cbSubjectLib.SelectedItem as SubjectLibrary) { Owner = this };
        if (ed.ShowDialog() == true && ed.Result != null && !ed.Result.IsEmpty)
        {
            _pairs.Add(ed.Result);
            RefreshPairs();
            _lbPairs.SelectedItem = ed.Result;
            PersistSetup();
        }
    }

    void EditPair()
    {
        if (_lbPairs.SelectedItem is not SubjectPair old) return;
        int idx = _pairs.IndexOf(old);
        var ed = new PairEditorWindow(old, _cbSubjectLib.SelectedItem as SubjectLibrary) { Owner = this };
        if (ed.ShowDialog() == true && ed.Result != null && !ed.Result.IsEmpty)
        {
            _pairs[idx] = ed.Result;
            RefreshPairs();
            _lbPairs.SelectedItem = ed.Result;
            PersistSetup();
        }
    }

    void DeletePair()
    {
        if (_lbPairs.SelectedItem is not SubjectPair p) return;
        _pairs.Remove(p);
        RefreshPairs();
        PersistSetup();
    }

    void ImportFromLibrary()
    {
        if (_cbSubjectLib.SelectedItem is not SubjectLibrary lib)
        {
            System.Windows.MessageBox.Show("请先在顶部选一个科目库。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int added = 0;
        foreach (var it in lib.Items)
        {
            if (string.IsNullOrWhiteSpace(it.Subject)) continue;
            if (_pairs.Any(x => !x.TwoLessons && x.Subject == it.Subject && x.Teacher == it.Teacher)) continue;
            _pairs.Add(new SubjectPair { Subject = it.Subject, Teacher = it.Teacher, Location = it.Location });
            added++;
        }
        RefreshPairs();
        PersistSetup();
        _txtHint.Text = "已从科目库带入 " + added + " 条配对（教师栏可在「编辑」里补）。这批设置已自动保存。";
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    void ExtractFromSchedule()
    {
        _pairs.AddRange(ExtractPairs(_data, _pairs));
        RefreshPairs();
        PersistSetup();
        _txtHint.Text = _pairs.Count == 0
            ? "当前班还没有排课，没有可提取的配对。"
            : "已从现有课表提取配对，合计 " + _pairs.Count + " 条（同格单双周已合并成一条双门），已自动保存。";
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    /// <summary>
    /// 扫描现有课表提炼「科目+教师」配对：同一格里的 W1/W2 两门合并成一条双门配对，
    /// 其余按科目+教师+周次去重。existing 用于跳过清单里已有的配对。
    /// </summary>
    internal static List<SubjectPair> ExtractPairs(
        Dictionary<int, Dictionary<int, List<Lesson>>> data, IEnumerable<SubjectPair>? existing = null)
    {
        var result = new List<SubjectPair>();
        var seen = new System.Collections.Generic.HashSet<string>();
        if (existing != null) foreach (var p in existing) seen.Add(PairKey(p));

        void AddIfNew(SubjectPair p)
        {
            if (p.IsEmpty) return;
            if (seen.Add(PairKey(p))) result.Add(p);
        }
        foreach (var day in data.Values)
        {
            foreach (var cell in day.Values)
            {
                var ls = cell.Where(l => !string.IsNullOrWhiteSpace(l.Subject) || !string.IsNullOrWhiteSpace(l.Teacher)).ToList();
                if (ls.Count == 0) continue;
                var w1 = ls.FirstOrDefault(l => l.Week == 1);
                var w2 = ls.FirstOrDefault(l => l.Week == 2);
                if (w1 != null && w2 != null)
                {
                    AddIfNew(new SubjectPair
                    {
                        TwoLessons = true, Subject = w1.Subject, Teacher = w1.Teacher, Location = w1.Location,
                        Subject2 = w2.Subject, Teacher2 = w2.Teacher, Location2 = w2.Location
                    });
                    foreach (var other in ls.Where(l => l != w1 && l != w2))
                        AddIfNew(new SubjectPair { Subject = other.Subject, Teacher = other.Teacher, Location = other.Location, Week = other.Week });
                }
                else
                {
                    foreach (var l in ls)
                        AddIfNew(new SubjectPair { Subject = l.Subject, Teacher = l.Teacher, Location = l.Location, Week = l.Week });
                }
            }
        }
        return result;
    }

    /// <summary>
    /// r126：就地打开「时间表与科目设置」。管理器改的就是本窗口这份 <see cref="_templates"/> 实例，
    /// 关掉后按新模板刷新下拉（尽量保住当前选择）并重建网格，不用回数据管理页。
    /// </summary>
    void OpenTemplateManager()
    {
        string? keepPeriod = (_cbPeriod.SelectedItem as PeriodTemplate)?.Name;
        string? keepLib = (_cbSubjectLib.SelectedItem as SubjectLibrary)?.Name;
        string? keepPlan = (_cbPlan.SelectedItem as SavedSchedulePlan)?.Name;

        var wnd = new ScheduleTemplateManagerWindow(_templates) { Owner = this };
        wnd.ShowDialog();

        _cbPeriod.Items.Clear();
        foreach (var p in _templates.PeriodTemplates) _cbPeriod.Items.Add(p);
        SelectByName(_cbPeriod, keepPeriod, _templates.PeriodTemplates);
        if (_cbPeriod.SelectedItem == null && _cbPeriod.Items.Count > 0) _cbPeriod.SelectedIndex = 0;

        _cbSubjectLib.Items.Clear();
        foreach (var s in _templates.SubjectLibraries) _cbSubjectLib.Items.Add(s);
        SelectByName(_cbSubjectLib, keepLib, _templates.SubjectLibraries);
        if (_cbSubjectLib.SelectedItem == null && _cbSubjectLib.Items.Count > 0) _cbSubjectLib.SelectedIndex = 0;

        RefreshPlanCombo(keepPlan);
        RebuildGrid();
        PersistSetup();
        _txtHint.Text = "时间表与科目设置已更新（作息时间表 / 科目库 / 课表方案都按最新内容刷新）。";
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    void ImportSubjectTable()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            Title = "选择任课安排表（科目 | 教师 | 地点 | 周课时）"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var imported = SubjectTableImporter.Import(dlg.FileName);
            if (imported.Count == 0)
            {
                System.Windows.MessageBox.Show("表里没有读到任何科目。请确认有列标题行：科目 | 教师 | 地点 | 周课时。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int added = 0;
            foreach (var p in imported)
            {
                if (_pairs.Any(x => !x.TwoLessons && x.Subject == p.Subject && x.Teacher == p.Teacher)) continue;
                _pairs.Add(p);
                added++;
            }
            RefreshPairs();
            PersistSetup();
            _txtHint.Text = "已导入 " + added + " 门课"
                + (imported.Count > added ? "（跳过重复 " + (imported.Count - added) + " 条）" : "") + "，已自动保存。";
            _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导入失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void DownloadSubjectTemplate()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = "任课安排表模板.xlsx",
            Title = "保存任课安排表模板"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            SubjectTableImporter.WriteTemplate(dlg.FileName);
            System.Windows.MessageBox.Show("模板已保存：\n" + dlg.FileName + "\n\n填好后回到本窗口点「导入任课表(Excel)」。",
                "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("保存失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static string PairKey(SubjectPair p) => p.TwoLessons
        ? "2|" + p.Subject + "|" + p.Teacher + "|" + p.Subject2 + "|" + p.Teacher2
        : "1|" + p.Subject + "|" + p.Teacher + "|" + p.Week;

    /// <summary>按天轮转取空位：先给周一到周五各取一节，再进入下一轮，避免全堆在同一天。</summary>
    internal static List<(int day, int period)> PickSpread(List<(int day, int period)> empties, int count)
    {
        var byDay = empties.GroupBy(x => x.day).OrderBy(g => g.Key)
            .Select(g => g.OrderBy(x => x.period).ToList()).ToList();
        var result = new List<(int, int)>();
        for (int round = 0; result.Count < count; round++)
        {
            bool any = false;
            foreach (var q in byDay)
            {
                if (round < q.Count && result.Count < count) { result.Add(q[round]); any = true; }
            }
            if (!any) break;
        }
        return result;
    }

    void OpenCellEditor(int day, int period)
    {
        var lessons = _data.TryGetValue(day, out var dm) && dm.TryGetValue(period, out var lm) ? lm : new List<Lesson>();
        var ed = new CellEditorWindow(day, period, lessons, _cbSubjectLib.SelectedItem as SubjectLibrary, _pairs);
        ed.Owner = this;
        if (ed.ShowDialog() == true)
        {
            if (ed.Cleared) RemoveCell(day, period);
            else SetCell(day, period, ed.EditedLessons);
            _dirty = true;
            RebuildGrid();
        }
    }

    static string WeekTag(int week) => week == 1 ? "（单）" : week == 2 ? "（双）" : "";
    static string BuildToolTip(List<Lesson> ls)
    {
        if (ls.Count == 0) return "点击填写（全周 / 单周 / 双周）";
        return string.Join("  |  ", ls.Select(l => $"{l.Subject} {l.Teacher} {l.Location} {WeekTag(l.Week)}").Where(x => x.Trim().Length > 0));
    }

    void SetCell(int day, int period, List<Lesson> lessons)
    {
        if (!_data.TryGetValue(day, out var dm)) _data[day] = dm = new Dictionary<int, List<Lesson>>();
        dm[period] = lessons;
    }
    void RemoveCell(int day, int period)
    {
        if (_data.TryGetValue(day, out var dm)) dm.Remove(period);
    }

    void SaveAsPlan()
    {
        string name = _txtPlanName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show("请先填写方案名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            _txtPlanName.Focus();
            return;
        }
        if (_templates.Plans.Any(p => p.Name == name) &&
            System.Windows.MessageBox.Show("已有同名方案「" + name + "」，覆盖它？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var plan = new SavedSchedulePlan
        {
            Name = name,
            SavedAt = DateTime.Now,
            PeriodTemplateName = (_cbPeriod.SelectedItem as PeriodTemplate)?.Name ?? "",
            Data = CloneData(_data)
        };
        _templates.Plans.RemoveAll(p => p.Name == name);
        _templates.Plans.Add(plan);
        Store.SaveScheduleTemplates(_templates);
        RefreshPlanCombo(name);
        PersistSetup();
        _txtHint.Text = "已保存方案「" + name + "」，顶部「课表方案」里可直接选它继续改。方案只存本机，不随数据包下发。";
        _txtHint.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }

    void SaveToClass()
    {
        string raw = CurrentClassName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            System.Windows.MessageBox.Show("请在「班级」里选一个现有班，或直接输入新班级名后保存。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            _cbClass.Focus();
            return;
        }
        string name = ResolveSaveName(raw);
        if (name.Length == 0) return;      // 用户选择回去改名 / 取消保存

        var tpl = _cbPeriod.SelectedItem as PeriodTemplate;
        var data = CloneData(_data);
        if (tpl != null) StampPeriodTimes(data, tpl);

        Result = new ClassSchedule
        {
            ClassName = name,
            Origin = "Manual",
            PeriodTemplateName = tpl?.Name ?? "",
            FixedSlots = tpl == null
                ? new List<FixedSlot>()
                : tpl.FixedSlots.Select(f => new FixedSlot { Name = f.Name, AfterPeriod = f.AfterPeriod, TimeRange = f.TimeRange }).ToList(),
            Data = data
        };
        PersistSetup(name);   // 顺手记住这个班用的课程设置 / 节次模板 / 方案
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 决定这次保存用哪个班名（r129）。手动录入的课表与「电子表格导入 / 自动更新」的课表各存一份：
    /// 同名且对方是导入的课表时，提示另存为「X（手动录入）」，绝不覆盖导入的那份。
    /// 返回 "" 表示用户放弃保存（去改名或取消）。
    /// </summary>
    /// <param name="ask">确认框（诊断可注入，避免真弹模态框把 --ui-check 卡住）。</param>
    internal string ResolveSaveName(string raw, Func<string, MessageBoxResult>? ask = null)
    {
        var hit = _source.Classes.FirstOrDefault(c => string.Equals(c.ClassName, raw, StringComparison.OrdinalIgnoreCase));
        if (hit == null || hit.IsManual) return raw;    // 新班 或 自己这份手动课表 → 原样保存

        string suggestion = ClassSchedule.WithManualSuffix(raw);
        string question = "「" + raw + "」已有电子表格 / 自动更新导入的课表。\n\n"
            + "手动录入的课表会和它分开保存，两边互不覆盖：\n"
            + "    是 = 另存为「" + suggestion + "」\n"
            + "    否 = 回去改个班名\n"
            + "    取消 = 不保存";
        var r = ask != null
            ? ask(question)
            : System.Windows.MessageBox.Show(question, "班级重名", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) return suggestion;
        if (r == MessageBoxResult.No)
        {
            _cbClass.Focus();
            // IsEditable 的 ComboBox 里真正的输入框是模板部件 PART_EditableTextBox
            if (_cbClass.Template?.FindName("PART_EditableTextBox", _cbClass) is System.Windows.Controls.TextBox tb)
            {
                tb.Focus();
                tb.SelectAll();      // 把旧班名整段选中，敲字直接替换
            }
        }
        return "";
    }

    /// <summary>
    /// 把作息时间表里的节次时间写进每一节课（r129）。
    /// <para>大屏 / 同班课表按 <see cref="Lesson.TimeRange"/> 显示第几节几点到几点；
    /// 手动录入原来完全不写这个字段，所以手动排的班在大屏上只有「第3节」没有时间。
    /// 写进课表本身（而不是渲染时去查模板）才能随数据包下发到教室机。</para>
    /// </summary>
    internal static void StampPeriodTimes(Dictionary<int, Dictionary<int, List<Lesson>>> data, PeriodTemplate tpl)
    {
        foreach (var day in data.Values)
        {
            foreach (var cell in day)
            {
                string time = tpl.Periods.FirstOrDefault(x => x.Index == cell.Key)?.TimeRange ?? "";
                if (time.Length == 0 || time.Contains("HH:MM")) continue;   // 占位符没填就不写
                foreach (var l in cell.Value)
                {
                    l.Period = cell.Key;
                    l.TimeRange = time;
                }
            }
        }
    }

    /// <summary>
    /// 删除当前这个班的课表（r129）。真正落盘交给 MainWindow，本窗口只回传班名。
    /// <para>手动录入的班：永久删除（连同课程设置）；导入的班：可以删，但要说明"下次更新会重新出现"。</para>
    /// </summary>
    /// <param name="confirm">false 供诊断直接调用（不弹确认框）。</param>
    internal void DeleteClass(bool confirm = true)
    {
        string name = _shownClass;
        if (name.Length == 0)
        {
            _txtHint.Text = "还没选班级，没有可删的课表。";
            _txtHint.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        var cs = _source.Classes.FirstOrDefault(c => c.ClassName == name);
        if (cs == null)
        {
            _txtHint.Text = "「" + name + "」还没保存过（只输入了班名），不用删除。";
            _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        bool manual = cs.IsManual;
        if (confirm)
        {
            string q = manual
                ? "删除「" + name + "」这份手动录入的课表？\n\n其他班不受影响；它的课程设置（任课清单）也一起删除。"
                : "「" + name + "」是电子表格 / 自动更新导入的课表。\n\n现在删掉只是从本机课表里移除；"
                  + "下一次自动更新或重新导入 Excel 时它会重新出现。仍要删除吗？";
            if (System.Windows.MessageBox.Show(q, manual ? "确认删除" : "删除导入的课表",
                    MessageBoxButton.YesNo, manual ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        _templates.ClassSetups.RemoveAll(c => string.Equals((c.ClassName ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
        Store.SaveScheduleTemplates(_templates);
        DeleteClassName = name;
        try { DialogResult = true; } catch (InvalidOperationException) { /* 非模态（诊断里 Show 出来的） */ }
        Close();
    }
}

