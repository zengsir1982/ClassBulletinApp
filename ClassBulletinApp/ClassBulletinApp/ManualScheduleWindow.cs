using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleCore.Models;
using ScheduleCore.Services;
using ClassBulletinApp.Controls;

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
    System.Windows.Controls.TextBlock _txtHint = null!;
    // r142：任课安排表标题右侧的「N 门」
    System.Windows.Controls.TextBlock _lblPairCount = null!;
    System.Windows.Controls.ScrollViewer _gridHost = null!;
    System.Windows.Controls.Grid _grid = null!;
    Button _btnDeleteClass = null!;
    // r139：整份课表方案的删除入口挪到这里（原「时间表与科目设置」窗口的方案页签已删除）
    Button _btnDeletePlan = null!;

    // r142：左上角的学期 / 课表版本名称（直接可编辑），以及作息 / 科目库两行的编辑入口
    System.Windows.Controls.TextBox _tbTermName = null!;
    Button _btnEditPeriod = null!, _btnEditSubjectLib = null!;

    // ---- 科目优先批量录入 ----
    readonly List<SubjectPair> _pairs = new();
    System.Windows.Controls.ListBox _lbPairs = null!;
    Button _btnPairEdit = null!, _btnPairDel = null!;
    // r137 诊断入口：清空按钮 / 下载模板文字链 + 紧随的问号
    internal Button? DiagClearPairsButton { get; private set; }
    internal Button? DiagDownloadTemplateLink { get; private set; }
    internal HelpBubble? DiagDownloadTemplateHelp { get; private set; }
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

        // r142 重排：只有三行 —— 顶栏 / 主体 / 底栏。
        // 原来那两行长提示（说明手录与导入分开存）占了一整行、还压着网格，现在收进底栏左侧当状态条，
        // 只在有事件时写一行结果，上方不再常年挂两大段灰字。
        var root = new System.Windows.Controls.Grid { Margin = new Thickness(16, 14, 16, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 顶栏
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 主体
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底栏

        var toolbar = BuildToolbar();
        System.Windows.Controls.Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // 底栏左侧的状态文本。字段名沿用 _txtHint：窗口里有 40 多处写它，改名只是无谓噪声。
        _txtHint = new System.Windows.Controls.TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        // 状态条只有一行，长句会被省略号截掉；悬停时把完整内容显示出来（绑定到自身 Text，文本变了自动跟着变）
        _txtHint.SetBinding(System.Windows.FrameworkElement.ToolTipProperty,
            new System.Windows.Data.Binding("Text") { Source = _txtHint });

        _gridHost = new System.Windows.Controls.ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(2)
        };

        // 主体：左栏（设置区 + 任课安排表）+ 右侧课表网格
        var work = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 0) };
        work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCol = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 14, 0) };
        leftCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var setPanel = BuildSettingsPanel();
        System.Windows.Controls.Grid.SetRow(setPanel, 0);
        leftCol.Children.Add(setPanel);

        var pairPanel = BuildPairPanel();
        System.Windows.Controls.Grid.SetRow(pairPanel, 1);
        leftCol.Children.Add(pairPanel);

        System.Windows.Controls.Grid.SetColumn(leftCol, 0);
        work.Children.Add(leftCol);
        System.Windows.Controls.Grid.SetColumn(_gridHost, 1);
        work.Children.Add(_gridHost);
        System.Windows.Controls.Grid.SetRow(work, 1);
        root.Children.Add(work);

        var bottomBar = BuildBottomBar();
        System.Windows.Controls.Grid.SetRow(bottomBar, 2);
        root.Children.Add(bottomBar);

        // 拖动浮起卡片的覆盖层：放在最后（第 4 个子元素，前面三个仍是 0/1/2 行），
        // Canvas 默认不吃鼠标，坐标与根 Grid 完全一致，比 AdornerLayer 更可控（截图/诊断里也看得见）。
        _dragLayer = new System.Windows.Controls.Canvas { IsHitTestVisible = false, ClipToBounds = false };
        System.Windows.Controls.Panel.SetZIndex(_dragLayer, 100);
        System.Windows.Controls.Grid.SetRow(_dragLayer, 0);
        System.Windows.Controls.Grid.SetRowSpan(_dragLayer, 3);
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
        _cbPlan.SelectionChanged += (_, _) =>
        {
            if (_btnDeletePlan != null) _btnDeletePlan.IsEnabled = _cbPlan.SelectedItem is SavedSchedulePlan;
            if (_ready) ApplyPlan();
        };

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
        // r142：顶栏只留「学期名称 + 班级 + 两个次要操作」。
        // 原来四个「标签+下拉」横排（12 列），1024 宽的屏上会把最右边的「删除该班」挤出界；
        // 三个下拉全部搬到左栏设置区竖排后，每条都有完整宽度，窄屏也不会溢出。
        var bar = new System.Windows.Controls.Grid { Height = 40, Margin = new Thickness(0, 0, 0, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 0 学期名称
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 1 班级标签
        bar.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 132, MaxWidth = 260   // 2 班级下拉
        });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3 弹性
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 4 清空网格
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 5 删除该班

        void Put(System.Windows.UIElement el, int col)
        {
            System.Windows.Controls.Grid.SetColumn(el, col);
            bar.Children.Add(el);
        }

        // ---- 学期 / 课表版本名称：最左上角，点进去直接改 ----
        var settings = Store.LoadSettings();
        string shownTerm = !string.IsNullOrWhiteSpace(_source.TermName)
            ? _source.TermName
            : (settings.TermNameConfirmed ?? "");
        _tbTermName = new System.Windows.Controls.TextBox
        {
            Text = shownTerm,
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = ResBrush("PrimaryBrush", System.Windows.Media.Brushes.SteelBlue),
            Padding = new Thickness(0),
            MinWidth = 88, MaxWidth = 176,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "学期 / 课表版本名称（如「2026学年第一学期」），随数据包下发。"
                    + "点这里可直接改，按回车或点别处生效；清空后大屏不再显示学期名。"
        };
        _tbTermName.LostFocus += (_, _) => CommitTermName();
        _tbTermName.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { CommitTermName(); e.Handled = true; }
        };
        var capsule = new Border
        {
            Background = ResBrush("PrimarySoftBrush",
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xEE, 0xF9))),
            BorderBrush = ResBrush("PrimarySoftBorderBrush",
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xD8, 0xEE))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
            Child = _tbTermName
        };
        Put(capsule, 0);

        Put(new System.Windows.Controls.TextBlock
        {
            Text = "班级", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ResBrush("MutedBrush", System.Windows.Media.Brushes.Gray)
        }, 1);

        _cbClass = new System.Windows.Controls.ComboBox
        {
            FontSize = 13.5, IsEditable = true, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ToolTip = "选一个已有班级，或直接输入新班名（保存后成为独立的手录课表）"
        };
        Put(_cbClass, 2);

        // 两个危险操作留在顶栏右侧但改成次要样式：和底栏的「保存到该班」在视觉上分开，不会手滑点错
        var btnClear = SmallGhost("清空网格");
        btnClear.Margin = new Thickness(8, 0, 0, 0);
        btnClear.ToolTip = "清空当前班级网格里的全部课（其他班不受影响）";
        btnClear.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show("清空当前班级网格里的全部课？（其他班不受影响）", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _data = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Lesson>>>();
            _dirty = true;
            RebuildGrid();
            SetStatus("已清空网格。要撤回请点「取消」直接关窗。", System.Windows.Media.Brushes.DimGray);
        };
        Put(btnClear, 4);

        // 删除班级（只对手动录入的班开放，导入 / 自动更新的班删了也会被下一次更新加回来）
        _btnDeleteClass = SmallGhost("删除该班");
        _btnDeleteClass.Margin = new Thickness(8, 0, 0, 0);
        _btnDeleteClass.Click += (_, _) => DeleteClass();
        Put(_btnDeleteClass, 5);
        return bar;
    }

    /// <summary>
    /// r142：左栏顶部「设置」区 —— 作息时间 / 科目库 / 课表方案三行，各带一个动作图标。
    /// <para>放在任课安排表上方：先把「按哪份作息排节次、从哪个库带科目、套不套方案」定下来，
    /// 再往下填任课安排，顺序和实际操作一致。</para>
    /// </summary>
    System.Windows.UIElement BuildSettingsPanel()
    {
        var g = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void Cell(System.Windows.UIElement el, int row, int col)
        {
            System.Windows.Controls.Grid.SetRow(el, row);
            System.Windows.Controls.Grid.SetColumn(el, col);
            g.Children.Add(el);
        }
        void Label(string text, int row) => Cell(new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = ResBrush("MutedBrush", System.Windows.Media.Brushes.Gray)
        }, row, 0);
        System.Windows.Controls.ComboBox Field(string tip, int row)
        {
            var cb = new System.Windows.Controls.ComboBox
            {
                FontSize = 12.5, Height = 26, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 6), ToolTip = tip
            };
            Cell(cb, row, 1);
            return cb;
        }

        // r139：原「节次模板」改叫「作息时间」—— 它选的是一份作息时间表，叫模板反而让人以为是排版模板
        Label("作息时间", 0);
        _cbPeriod = Field("这节课表按哪份作息排节次（决定行数与节次时间）。右边图标可改这份作息", 0);
        _btnEditPeriod = IconButton("编辑这份作息时间表（含「设为全校作息」）", PencilPath, EditPeriodTemplate);
        _btnEditPeriod.Margin = new Thickness(6, 0, 0, 6);
        Cell(_btnEditPeriod, 0, 2);

        Label("科目库", 1);
        _cbSubjectLib = Field("「从科目库导入」时按这份清单带出任课安排。右边图标可改这份清单", 1);
        _btnEditSubjectLib = IconButton("编辑这份常用科目库", PencilPath, EditSubjectLibrary);
        _btnEditSubjectLib.Margin = new Thickness(6, 0, 0, 6);
        Cell(_btnEditSubjectLib, 1, 2);

        Label("课表方案", 2);
        _cbPlan = Field("选一个已保存的方案，直接在上面改（会覆盖当前网格）", 2);
        // r139：方案的删除挪到下拉右边，选一个就能删（原来要去「时间表与科目设置」窗口里删）
        _btnDeletePlan = IconButton("删除当前选中的课表方案（不影响已保存到班级的课表）", TrashPath, DeletePlan);
        _btnDeletePlan.Margin = new Thickness(6, 0, 0, 0);
        _btnDeletePlan.IsEnabled = false;
        Cell(_btnDeletePlan, 2, 2);

        return g;
    }

    // 图标用实心多边形而不是描边轮廓：14px 见方时描边会挤成一团，看着像一道斜线
    const string PencilPath = "M2.6 12.4 L2.6 14 L4.2 14 L12.2 6 L10.6 4.4 Z";
    const string TrashPath = "M4.6 5 H11.4 L10.9 13.6 H5.1 Z M5.2 3.9 V2.4 H10.8 V3.9 H12.4 V4.9 H3.6 V3.9 Z";

    /// <summary>下拉右边的动作图标按钮（铅笔 = 编辑，垃圾桶 = 删除）。</summary>
    static Button IconButton(string tip, string pathData, Action onClick)
    {
        var shape = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse(pathData),
            Fill = ResBrush("PrimaryBrush", System.Windows.Media.Brushes.SteelBlue),
            Width = 13, Height = 13,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var b = new Button
        {
            Content = shape, Width = 30, Height = 26, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = tip
        };
        b.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "AppGhostButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    Button SmallGhost(string text)
    {
        var b = new Button
        {
            Content = text, FontSize = 12.5, Padding = new Thickness(12, 5, 12, 5),
            MinHeight = 28, VerticalAlignment = VerticalAlignment.Center
        };
        b.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "AppGhostButton");
        return b;
    }

    static System.Windows.Media.Brush ResBrush(string key, System.Windows.Media.Brush fallback)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    /// <summary>底栏左侧的一行状态文本（替代原来压在网格上的两行长提示）。</summary>
    void SetStatus(string text, System.Windows.Media.Brush? color = null)
    {
        if (_txtHint == null) return;
        _txtHint.Text = text ?? "";
        _txtHint.Foreground = color ?? System.Windows.Media.Brushes.DimGray;
    }

    /// <summary>
    /// 学期 / 课表版本名称写回。它是随数据包下发的元数据，不属于某一个班，
    /// 所以直接改底表并落盘，不走「保存到该班」那条 Result 通道。
    /// </summary>
    void CommitTermName()
    {
        if (_tbTermName == null || _source == null) return;
        string v = (_tbTermName.Text ?? "").Trim();
        if (string.Equals(v, (_source.TermName ?? "").Trim(), StringComparison.Ordinal)) return;
        try
        {
            _source.TermName = v;
            var disk = Store.LoadSchedule() ?? new ScheduleFile();
            disk.TermName = v;
            Store.SaveSchedule(disk);
            AppEvents.RaiseScheduleChanged();
            SetStatus(string.IsNullOrEmpty(v)
                ? "已清空学期名称，大屏不再显示。"
                : "学期名称已改为「" + v + "」，随数据包下发到教室机。",
                System.Windows.Media.Brushes.DarkGreen);
        }
        catch (Exception ex)
        {
            SetStatus("学期名没保存成功：" + ex.Message, System.Windows.Media.Brushes.Firebrick);
        }
    }

    /// <summary>编辑当前选中的作息时间表（可顺带设为全校作息）。</summary>
    void EditPeriodTemplate()
    {
        if (_cbPeriod.SelectedItem is not PeriodTemplate p)
        {
            SetStatus("还没有可编辑的作息时间表。", System.Windows.Media.Brushes.Firebrick);
            return;
        }
        var dlg = new PeriodTemplateDialog(p, UserSession.CanManageData) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.MakeDefault)
            foreach (var x in _templates.PeriodTemplates) x.IsDefault = ReferenceEquals(x, p);
        if (Store.EnablePersistence) Store.SaveScheduleTemplates(_templates);

        // 改了名称 / 节次数，下拉里的显示与网格行数都要跟着变
        _cbPeriod.Items.Refresh();
        if (_ready) RebuildGrid();

        if (dlg.AppliedToSchool)
        {
            var settings = Store.LoadSettings();
            bool hasFeed = settings.BoardSyncEnabled || !string.IsNullOrWhiteSpace(settings.ErYiDataDir)
                           || settings.UpdateSourceKind != "Off";
            SetStatus(hasFeed
                    ? "作息已保存并设为全校作息：本机立即生效，教室机等下次发布 / 同步。"
                    : "作息已保存并设为全校作息（本机立即生效）。",
                System.Windows.Media.Brushes.DarkGreen);
        }
        else
        {
            SetStatus("作息时间表「" + p.Name + "」已保存（只影响手动录入，不改大屏时间）。",
                System.Windows.Media.Brushes.DarkGreen);
        }
        PersistSetup();
    }

    /// <summary>编辑当前选中的常用科目库。</summary>
    void EditSubjectLibrary()
    {
        if (_cbSubjectLib.SelectedItem is not SubjectLibrary lib)
        {
            SetStatus("还没有可编辑的科目库。", System.Windows.Media.Brushes.Firebrick);
            return;
        }
        var dlg = new SubjectLibraryDialog(lib) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.MakeDefault)
            foreach (var x in _templates.SubjectLibraries) x.IsDefault = ReferenceEquals(x, lib);
        if (Store.EnablePersistence) Store.SaveScheduleTemplates(_templates);
        _cbSubjectLib.Items.Refresh();
        SetStatus("科目库「" + lib.Name + "」已保存（" + lib.Items.Count + " 科），「从科目库导入」会按它带出。",
            System.Windows.Media.Brushes.DarkGreen);
        PersistSetup();
    }

    System.Windows.Controls.Grid BuildBottomBar()
    {
        var bar = new System.Windows.Controls.Grid { Margin = new Thickness(0, 8, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // r142：状态文本挪到最左边 —— 操作结果就近显示，不再占着网格上方一整行
        if (_txtHint != null)
        {
            System.Windows.Controls.Grid.SetColumn(_txtHint, 0);
            bar.Children.Add(_txtHint);
        }

        var right = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        System.Windows.Controls.Grid.SetColumn(right, 1);

        var btnCancel = new Button { Content = "取消", FontSize = 13, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        right.Children.Add(btnCancel);

        var btnSave = new Button { Content = "保存到该班", FontSize = 13, Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        btnSave.Click += (_, _) => SaveToClass();
        right.Children.Add(btnSave);

        // r139：去掉「方案名」输入框 —— 常驻一个空输入框既占地方又要用户先想名字。
        // 改成点按钮时弹框问名字，按钮紧跟在主操作右边，问号说明它到底是干什么的。
        var btnPlan = new Button
        {
            Content = "另存为手录课表方案", FontSize = 13, Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(10, 0, 0, 0)
        };
        btnPlan.Click += (_, _) => SaveAsPlan();
        right.Children.Add(btnPlan);

        right.Children.Add(new HelpBubble
        {
            PopupTitle = "「另存为手录课表方案」有什么用",
            BubbleWidth = 360,
            Text = "把当前网格里的整份课表存成一个「方案」（会问你方案名），之后在顶部「课表方案」下拉里就能选它，"
                 + "在它基础上改，不用每次从空白开始排。\n"
                 + "方案只存这台机器（不随数据包下发、不影响已保存的班级）；要删方案，选中后点下拉右边的「删除方案」。\n"
                 + "★ 它不会保存到班级 —— 要让这个班真的用上这份课，还是要点「保存到该班」。",
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

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
                ? "删除「" + _shownClass + "」这份手动录入的课表（任课安排也一起删除）"
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

        // 先说清「这个班是哪来的」——手动录入与导入/自动更新是两套课表，不说清会以为改的是同一份。
        // r142：底栏只有一行，文案压到最短（原来那几句在 1024 宽下必被省略号吃掉）；
        //       完整说明仍在 ToolTip 里兜底（见构造函数里绑到自身 Text 的那句）。
        string lead = string.IsNullOrWhiteSpace(className) ? ""
            : cls == null
                ? "「" + className + "」是新班，保存后成为独立的手录课表。"
            : cls.IsManual
                ? "「" + className + "」是手录课表，与导入的课表互不覆盖。"
                : "「" + className + "」是导入的课表，保存会另存为「"
                  + ClassSchedule.WithManualSuffix(className) + "」，不改动它。";

        string setupInfo = setup != null && _pairs.Count > 0
            ? "已调出任课安排 " + _pairs.Count + " 门。"
            : string.IsNullOrWhiteSpace(className) ? ""
            : "还没有任课安排，可先导入或从现有课表提取。";

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
            _txtHint.Text = "请先在左侧「任课安排表」里选一门课，再点格子填充。";
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
        // r142：改成 Grid 而不是 StackPanel —— 列表占 Star 行，随窗口高度自己伸缩。
        // 原来是写死 206px，左栏上方多出三行设置后，固定高度会把底部的增删按钮顶出可视区。
        var g = new System.Windows.Controls.Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 导入入口
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 增删改

        // ---- 标题：任课安排表 + 门数 ----
        var head = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _lblPairCount = new System.Windows.Controls.TextBlock
        {
            FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResBrush("MutedBrush", System.Windows.Media.Brushes.Gray)
        };
        System.Windows.Controls.DockPanel.SetDock(_lblPairCount, Dock.Right);
        head.Children.Add(_lblPairCount);
        head.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "任课安排表", FontSize = 13.5, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        System.Windows.Controls.Grid.SetRow(head, 0);
        g.Children.Add(head);

        // ---- 导入入口（r139：按真实流程排在列表上方 —— 先导入，再看列表，最后增删改）----
        var imports = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var row1 = new System.Windows.Controls.WrapPanel();
        row1.Children.Add(SmallButton("从科目库导入", ImportFromLibrary));
        row1.Children.Add(SmallButton("从现有课表提取", ExtractFromSchedule));
        imports.Children.Add(row1);

        var row2 = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        row2.Children.Add(SmallButton("导入任课安排(Excel)", ImportSubjectTable));
        // 链接只是「导入」的前置小工具，不再做成独立按钮抢注意力；问号说明它下载什么、填完回到哪里用
        var linkRow = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center
        };
        var lnkTemplate = new Button
        {
            Content = "下载模板", Padding = new Thickness(2, 4, 2, 4), Margin = new Thickness(4, 0, 2, 0)
        };
        lnkTemplate.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "AppLinkButton");
        lnkTemplate.ToolTip = "下载空白的「任课安排模板」Excel（科目 / 教师 / 地点 / 周课时）";
        lnkTemplate.Click += (_, _) => DownloadSubjectTemplate();
        DiagDownloadTemplateLink = lnkTemplate;
        linkRow.Children.Add(lnkTemplate);
        DiagDownloadTemplateHelp = new HelpBubble
        {
            PopupTitle = "「下载模板」有什么用",
            BubbleWidth = 340,
            Text = "下载的是一张空的 Excel 任课安排表（列：科目 / 教师 / 地点 / 周课时）。\n"
                 + "按本校实际情况填好后，回到这里点「导入任课安排(Excel)」，一次把各科教师与课时填进下面的「任课安排表」。\n"
                 + "模板只是统一填写格式，不会改动课表格子，也不影响已保存的课表方案。",
            VerticalAlignment = VerticalAlignment.Center
        };
        linkRow.Children.Add(DiagDownloadTemplateHelp);
        row2.Children.Add(linkRow);
        imports.Children.Add(row2);
        System.Windows.Controls.Grid.SetRow(imports, 1);
        g.Children.Add(imports);

        // ---- 列表：三列对齐（科目 / 教师 / 周课时），比原来一行拼接的文字好扫 ----
        _lbPairs = new System.Windows.Controls.ListBox
        {
            FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = ResBrush("LineBrush", System.Windows.Media.Brushes.LightGray),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            ItemTemplate = BuildPairItemTemplate()
        };
        _lbPairs.SelectionChanged += (_, _) => UpdatePairButtons();
        System.Windows.Controls.Grid.SetRow(_lbPairs, 2);
        g.Children.Add(_lbPairs);

        var row3 = new System.Windows.Controls.WrapPanel();
        row3.Children.Add(SmallButton("新增", AddPair));
        _btnPairEdit = SmallButton("编辑", EditPair);
        row3.Children.Add(_btnPairEdit);
        _btnPairDel = SmallButton("删除", DeletePair);
        row3.Children.Add(_btnPairDel);
        DiagClearPairsButton = SmallButton("清空", ClearPairs);
        row3.Children.Add(DiagClearPairsButton);
        System.Windows.Controls.Grid.SetRow(row3, 3);
        g.Children.Add(row3);

        UpdatePairButtons();
        return g;
    }

    /// <summary>任课安排的列表项模板：科目（左，定宽）· 教师（中，填充，灰）· 周课时（右）。</summary>
    static System.Windows.DataTemplate BuildPairItemTemplate()
    {
        var dock = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.DockPanel));
        dock.SetValue(System.Windows.FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));

        var periods = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        periods.SetValue(System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding("WeeklyPeriods") { StringFormat = "{0} 节" });
        periods.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.0);
        periods.SetValue(System.Windows.Controls.TextBlock.MarginProperty, new Thickness(4, 0, 2, 0));
        periods.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        periods.SetValue(System.Windows.Documents.TextElement.ForegroundProperty,
            ResBrushStatic("MutedBrush", System.Windows.Media.Brushes.Gray));
        periods.SetValue(System.Windows.Controls.DockPanel.DockProperty, Dock.Right);
        dock.AppendChild(periods);

        var subject = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        subject.SetValue(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Subject"));
        subject.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 12.5);
        subject.SetValue(System.Windows.Controls.TextBlock.WidthProperty, 62.0);
        subject.SetValue(System.Windows.Documents.TextElement.ForegroundProperty,
            ResBrushStatic("InkBrush", System.Windows.Media.Brushes.Black));
        subject.SetValue(System.Windows.Controls.DockPanel.DockProperty, Dock.Left);
        dock.AppendChild(subject);

        var teacher = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        teacher.SetValue(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Teacher"));
        teacher.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.5);
        teacher.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        teacher.SetValue(System.Windows.Documents.TextElement.ForegroundProperty,
            ResBrushStatic("MutedBrush", System.Windows.Media.Brushes.Gray));
        teacher.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dock.AppendChild(teacher);

        var dt = new System.Windows.DataTemplate();
        dt.VisualTree = dock;
        return dt;
    }

    static System.Windows.Media.Brush ResBrushStatic(string key, System.Windows.Media.Brush fallback)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

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
        if (_lblPairCount != null) _lblPairCount.Text = _pairs.Count > 0 ? _pairs.Count + " 门" : "";
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

    /// <summary>一键清空当前班的任课安排表（逐条「删除」的快捷方式，仍会按班级持久化）。</summary>
    void ClearPairs()
    {
        if (_pairs.Count == 0) { _txtHint.Text = "任课安排表已经是空的。"; return; }
        if (System.Windows.MessageBox.Show($"确定清空这 {_pairs.Count} 条任课安排吗？\n只清「任课安排表」清单，已经填进课表的课不受影响。",
                "清空任课安排表", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _pairs.Clear();
        RefreshPairs();
        PersistSetup();
        _txtHint.Text = "任课安排表已清空（课表格子里的课没动）。";
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
            System.Windows.MessageBox.Show("模板已保存：\n" + dlg.FileName + "\n\n填好后回到本窗口点「导入任课安排(Excel)」。",
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
        // r139：方案名改成弹框输入（原来底栏常驻一个空输入框，既占地方又要用户先想名字）
        string suggested = (_cbPlan.SelectedItem as SavedSchedulePlan)?.Name
                           ?? "方案 " + DateTime.Now.ToString("MM-dd HH:mm");
        var dlg = new SimpleNameDialog("另存为手录课表方案", suggested, "") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string name = dlg.EnteredName;
        if (string.IsNullOrWhiteSpace(name)) return;

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

    /// <summary>删除当前选中的课表方案（r139：从「时间表与科目设置」窗口搬到这里）。</summary>
    void DeletePlan()
    {
        if (_cbPlan.SelectedItem is not SavedSchedulePlan p) return;
        if (System.Windows.MessageBox.Show(
                "删除课表方案「" + p.Name + "」？\n\n只删这份可复用的草稿，已经保存到班级的课表不受影响。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _templates.Plans.Remove(p);
        Store.SaveScheduleTemplates(_templates);
        RefreshPlanCombo(null);
        PersistSetup();
        _txtHint.Text = "已删除方案「" + p.Name + "」。";
        _txtHint.Foreground = System.Windows.Media.Brushes.DimGray;
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
    /// 同名且对方是导入的课表时，提示另存为「X（手录）」，绝不覆盖导入的那份。
    /// 返回 "" 表示用户放弃保存（去改名或取消）。
    /// <para>r140：弹窗从 MessageBox 的是 / 否 / 取消 改成三个写明结果的按钮
    /// （<see cref="SaveClassConflictDialog"/>），语义完全没变。</para>
    /// </summary>
    /// <param name="ask">确认框（诊断可注入，避免真弹模态框把 --ui-check 卡住）。</param>
    internal string ResolveSaveName(string raw, Func<string, MessageBoxResult>? ask = null)
    {
        var hit = _source.Classes.FirstOrDefault(c => string.Equals(c.ClassName, raw, StringComparison.OrdinalIgnoreCase));
        if (hit == null || hit.IsManual) return raw;    // 新班 或 自己这份手动课表 → 原样保存

        string suggestion = ClassSchedule.WithManualSuffix(raw);
        string question = "「" + raw + "」已有电子表格 / 自动更新导入的课表。\n\n"
            + "手动录入的课表会和它分开保存，两边互不覆盖：\n"
            + "    分开存（推荐） = 另存为「" + suggestion + "」\n"
            + "    改个班名 = 回去改个班名再存\n"
            + "    取消 = 不保存";
        MessageBoxResult r;
        if (ask != null)
        {
            r = ask(question);
        }
        else
        {
            var dlg = new SaveClassConflictDialog(raw, suggestion) { Owner = this };
            dlg.ShowDialog();
            r = dlg.Result switch
            {
                SaveClassConflictDialog.Choice.Separate => MessageBoxResult.Yes,
                SaveClassConflictDialog.Choice.Rename => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel
            };
        }
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
                ? "删除「" + name + "」这份手动录入的课表？\n\n其他班不受影响；它的任课安排表也一起删除。"
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

