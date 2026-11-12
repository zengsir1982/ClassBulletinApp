using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 System.Drawing（同名类型极多），别名消歧义，避免 CS0104
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.IO.Path;
using PathShape = System.Windows.Shapes.Path;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 桌面涂鸦板：一块可自由摆放、可拉边改大小、可换材质的板子。
/// <para>两种用途共用同一块板 —— <b>便签容器</b>（上半自动拉通知/作业/缺交，下半手动便签）
/// 与 <b>手写黑板</b>（InkCanvas 落笔，笔迹按板 Id 存 ISF）。</para>
/// <para>★ 拖动与缩放全部交给 <c>WM_NCHITTEST</c>，不自己做 Thumb —— 系统级的拖拽在一体机
/// 手指操作下最跟手，也不会出现"拖到一半跟丢"的问题。边缘命中区 <see cref="DoodleBoardMetrics.ResizeMargin"/>。</para>
/// </summary>
public sealed class DoodleBoardWindow : Window
{
    // ---------- 桌面底层板：永远压在所有窗口之下 ----------
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // ---------- WM_NCHITTEST ----------
    private const int WM_NCHITTEST = 0x84;
    private const int WM_GETMINMAXINFO = 0x24;
    /// <summary>非客户区左键双击。板头整条是 HTCAPTION，WPF 收不到 MouseLeftButtonDown。</summary>
    private const int WM_NCLBUTTONDBLCLK = 0xA3;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private readonly DoodleBoardConfig _cfg;
    private DoodleBoardTheme _theme;

    private Border _frame = null!;
    private Border _surface = null!;
    private FrameworkElement _head = null!;
    private TextBlock _titleText = null!;
    private TextBlock _classText = null!;
    private TextBlock _clockText = null!;
    private TextBlock _pageText = null!;
    private readonly DispatcherTimer _clockTimer;
    private Button _modeBtn = null!;
    private Button _closeBtn = null!;
    private Button _settingsBtn = null!;
    private Button _noteManageBtn = null!;
    private Button _pageDropBtn = null!;
    private Button _lockBtn = null!;
    private PathShape _closeIcon = null!;
    private PathShape _settingsIcon = null!;

    /// <summary>底部工具带：手写参数、便签字号与颜色都在这条带子上，不占板头。</summary>
    private Border _toolBar = null!;
    private WrapPanel _toolItems = null!;
    /// <summary>手写工具带里当前显示的滑块 / 颜色胶囊；诊断据此确认只露该露的那一个。</summary>
    private FrameworkElement? _penSlider;
    private FrameworkElement? _eraserSlider;
    private Button? _colorCapsuleBtn;

    private Border _notesHost = null!;
    private Canvas _notesCanvas = null!;
    private int _notesLayer;
    private InkCanvas _ink = null!;
    /// <summary>
    /// 当前是否在手写层。★ 一个页面里便签和笔迹同时可见，只是命中权归不同层：
    /// 选中画笔/橡皮 → 手写层收事件、便签只读；点「选择」或取消工具 → 便签可拖可编辑。
    /// </summary>
    private bool _inkActive;
    /// <summary>板头那一行；字号调大时行高要跟着长，否则班级 / 时间会被裁掉。</summary>
    private RowDefinition _headRow = null!;
    /// <summary>板头容器：班级 / 时间与菜单按钮并排在同一行（诊断要读它的行定义）。</summary>
    private Grid? _headGrid;
    private TextBlock _sizeBadge = null!;

    // ---------- 最小化（收成板名条） ----------
    /// <summary>板面（便签 + 笔迹 + 右下角浮层）那一行。收起时整行隐藏，只留板头。</summary>
    private Grid _body = null!;
    private Button _minBtn = null!;
    private readonly List<FrameworkElement> _headHideWhenCollapsed = new();
    /// <summary>当前是否收起。★ 收起不卸载任何东西，切回来立刻是原样。</summary>
    private bool _collapsed;
    /// <summary>收起前的窗口高度。恢复用它，也让 PersistGeometry 只存展开高度。</summary>
    private double _expandedHeight;

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _badgeTimer;
    /// <summary>诊断用：第一张自动便签的标题 TextBlock（读它才能证明格式真的画到卡片上了）。</summary>
    private TextBlock? _firstAutoTitle;
    /// <summary>诊断用：第一张自动便签的正文 TextBlock（验自动换行 / 回车换行）。</summary>
    private TextBlock? _firstAutoBody;
    /// <summary>诊断用：第一张自动便签的卡片 Border（验「便签各自的风格」真的画到底色上）。</summary>
    private Border? _firstAutoCard;
    /// <summary>诊断用：第一张自动便签的格式键（宽卡排在最前，不能再用 auto[0] 猜）。</summary>
    private string? _firstAutoKey;

    private bool _applyingGeometry;
    private bool _loadingInk;
    private int _lastBucket;

    /// <summary>当前配置（服务层改完会替换成新对象）。</summary>
    public DoodleBoardConfig Config => _cfg;

    /// <summary>当前页（永远非空，老配置在构造时已迁移）。</summary>
    private DoodleBoardPage Page => _cfg.Pages[_cfg.ActivePage];

    /// <summary>
    /// 用户要求关掉这块板时触发。<paramref name="removeConfig"/>：
    /// <c>false</c> = 板头 ×「收起」，只关窗口，配置和笔迹都留着（下次启动自动恢复）；
    /// <c>true</c> = 菜单「删除这块板」，连配置和 ISF 笔迹一起删。
    /// </summary>
    public event EventHandler<bool>? RequestClose;

    public DoodleBoardWindow(DoodleBoardConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _theme = DoodleBoardThemes.Get(cfg.ThemeKey);

        Title = string.IsNullOrWhiteSpace(cfg.Name) ? "涂鸦板" : cfg.Name;
        WindowStyle = WindowStyle.None;
        // ★ 必须 CanResize：NoResize 会让系统丢弃 WM_NCHITTEST 返回的 HTBOTTOMRIGHT，
        // 表现为「拖角毫无反应」，而钩子和几何判断全都是对的 —— 极难查。
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;

        // ★ 涂鸦板是「桌面底层板」：压在所有窗口之下，不抢前台，不挡 PPT / 浏览器。
        // 两层保障：
        //   ① Topmost 恒 false（老配置里存着 true 也忽略）；
        //   ② ShowActivated = false → 窗口带 WS_EX_NOACTIVATE，点它也不会被激活，
        //      而「被顶到最前」只发生在激活时，所以它天然待在最底层。
        // 这比在消息钩子里反复 SetWindowPos(HWND_BOTTOM) 稳得多：那种做法会打断
        // 系统自己的拖动/缩放循环（拖角拖不动、松手卡死）。
        Topmost = false;
        ShowActivated = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        Width = DoodleBoardMetrics.ClampWidth(cfg.Width ?? DoodleBoardMetrics.DefaultWidth);
        Height = DoodleBoardMetrics.ClampHeight(cfg.Height ?? DoodleBoardMetrics.DefaultHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;

        _cfg.EnsurePage();   // 老配置把便签 / 格式迁到第一页，界面只读页对象

        BuildUi();
        ApplyTheme();
        // 老版笔迹按板一份，首次打开时搬到第一页；之后每页各写各的
        DoodleBoardService.MigrateLegacyInk(_cfg.Id, Page.Id);
        LoadInk();
        RebuildNotes();
        // 老手写板打开就落笔，老便签板打开就能拖便签；之后由工具带随时切换
        SetInkActive(string.Equals(_cfg.Mode, DoodleModes.Ink, StringComparison.OrdinalIgnoreCase));

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistGeometry(); };

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); _sizeBadge.Visibility = Visibility.Collapsed; };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => UpdateHeaderInfo();
        _clockTimer.Start();

        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) =>
        {
            if (_applyingGeometry) return;
            if (!_collapsed) UpdateSizeBadge();   // 收起/展开时弹「560 × 44」没有意义
            ScheduleSave();
            UpdateTitleMaxWidth();                // 板名的最宽值按板宽走，拉窄了要收一收

            // 板宽跨过分列阈值时重排一次，否则拉宽了还是老列数、拉窄了卡片被压扁。
            // ★ 比较的是"宽度档位"而不是实际列数：实际列数还被卡片数量约束，
            //   拿它比较会让每次 resize 都白重建一遍可视树。
            int bucket = ColumnsFor(Width);
            if (bucket != _lastBucket)
            {
                _lastBucket = bucket;
                RebuildNotes();   // 便签层与笔迹层同页常驻，手写时也要重排
            }
        };

        Loaded += OnLoaded;
        Closing += (_, _) => PersistGeometry();
        SubscribeContentChanges();
    }

    // ======================================================================
    //  界面
    // ======================================================================

    private void BuildUi()
    {
        _frame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1)
        };
        Content = _frame;

        _surface = new Border
        {
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true
        };
        _frame.Child = _surface;

        var root = new Grid();
        _headRow = new RowDefinition { Height = new GridLength(DoodleBoardMetrics.HeadHeight) };
        root.RowDefinitions.Add(_headRow);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 第三行：工具带。高度 Auto —— 窄板时按钮换行，带子自然变高，绝不裁掉按钮
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _surface.Child = root;

        _head = BuildHead();
        root.Children.Add(_head);

        var body = _body = new Grid { Margin = new Thickness(0) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // --- 便签容器 ---
        _notesHost = new Border { Padding = new Thickness(10, 4, 10, 4) };
        var scroll = new ScrollViewer
        {
            // 滚动条设 Hidden 而不是 Auto：这是块装饰板，教室远看时一条白滚动条比内容略满更难看。
            // 隐藏后滚轮依然有效，只是不画出那条杠。
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // ★ 这一块没有笔迹，可以放心开手指上下平移。
            // 手写区（InkCanvas）才是必须 PanningMode=None 的地方，两者不要混为一谈：
            // 便签内容超一屏时，一体机用户没有滚轮，不开平移就永远看不到下面的便签。
            PanningMode = PanningMode.VerticalOnly,
            PanningDeceleration = 0.0012,
            Focusable = false
        };
        _notesCanvas = new Canvas
        {
            // ★ 自由摆放：绝对定位，尺寸由每张便签自己记住。
            // PanningMode 交给外层 ScrollViewer，这里不要吞触屏事件。
            MinHeight = 420,
            MinWidth = 260
        };
        scroll.Content = _notesCanvas;
        _notesHost.Child = scroll;
        body.Children.Add(_notesHost);

        // --- 手写 ---
        // ★ 便签与笔迹同页叠加：两层都常驻可见，靠 IsHitTestVisible 切换「谁能收到手指」。
        // 笔迹必须画在便签上方（后加入 Grid → Z 序更高），否则学生写在便签上的字会被卡片盖住。
        _ink = new InkCanvas
        {
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.None,
            Margin = new Thickness(10, 0, 10, 4),
            Focusable = false,
            IsHitTestVisible = false
        };
        // 具体笔色 / 笔宽 / 橡皮大小由 ApplyInkStyle() 统一套用，这里只放不会变的属性
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            FitToCurve = true,
            StylusTip = StylusTip.Ellipse
        };
        _ink.StrokeCollected += (_, _) => ScheduleInkSave();
        _ink.StrokeErased += (_, _) => ScheduleInkSave();
        body.Children.Add(_ink);

        // --- 锁定按钮：只造一次，之后由 RebuildToolBar 挂在「手写 / 便签」切换按钮的左边 ---
        // ★ r108：原来自绘的右下角浮层同时放板名与锁定，锁上之后浮层被便签 / 正文压住、看不见，
        //   用户就卡在「拖不动、写不了」里出不来。挪进工具带后它永远在明面上，且一眼能看到状态。
        //   实例只造一份：工具带每次重建只是把它「重新挂回」最前面，不产生第二个锁定按钮。
        _lockBtn = MakeTextHeadButton("锁定", 76, "锁定后不能拖板、写笔迹或改便签");
        _lockBtn.Height = 34;
        _lockBtn.FontSize = 13.5;
        _lockBtn.Margin = new Thickness(0, 0, 6, 0);
        _lockBtn.Click += (_, _) => SetLocked(!_cfg.Locked);

        // --- 工具带（便签字号色 / 手写笔参数；板名钉在它最右边）---
        _toolBar = BuildToolBar();
        Grid.SetRow(_toolBar, 2);
        root.Children.Add(_toolBar);

        // --- 尺寸读数（拖动/缩放时短暂浮出） ---
        _sizeBadge = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1F, 0x2A, 0x3A)),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Grid.SetRow(_sizeBadge, 1);
        root.Children.Add(_sizeBadge);
    }

    private FrameworkElement BuildHead()
    {
        // ★ 板头一行：左边「班级 + 带年份时间」，右边整排按钮，两者垂直居中对齐。
        //   原来是上下两行 —— 菜单栏排在第二行，大屏上看着比班级低半格，用户要求并成一行。
        //   顶部留白算在整行的 Margin.Top 上（不是只给信息行），所以按钮跟着一起下移、仍然齐平；
        //   用 Margin 而不是外层留空，_head.ActualHeight 会把留白算进去，板头拖动区不会漏。
        var head = _headGrid = new Grid { Margin = new Thickness(0, HeadTopGap, 8, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 班级与时间放在一起，排在板头左边、距边沿约两个字符（不紧挨边沿）。
        var info = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(HeaderInset, 0, 12, 0)
        };
        _classText = new TextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = ParseBrush(HeadForeground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.Hand,
            ToolTip = "双击设置板头字体格式（班级 / 时间）"
        };
        _classText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            if (_collapsed) { SetCollapsed(false); return; }   // 收起时双击标题 = 展开
            EditHeaderStyle();
        };
        info.Children.Add(_classText);

        _clockText = new TextBlock
        {
            Text = "",
            FontSize = 14,
            Foreground = ParseBrush(HeadForeground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "双击设置板头字体格式（班级 / 时间）"
        };
        _clockText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            if (_collapsed) { SetCollapsed(false); return; }   // 收起时双击时间 = 展开
            EditHeaderStyle();
        };
        info.Children.Add(_clockText);

        Grid.SetColumn(info, 0);
        head.Children.Add(info);

        var controls = new DockPanel
        {
            LastChildFill = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        // 右侧动作：关闭 / 菜单 / 便签管理 / 页面下拉 / 翻页。
        // 手写切换已移到黑板左下角工具带前面；+板面改成「页面」下拉；锁定移到右下角。
        // Dock.Right 是逆序添加的，所以这里先加最右边的关闭按钮。
        _closeIcon = DoodleIcons.Close(ParseBrush(HeadForeground));
        _closeBtn = MakeHeadIconButton(_closeIcon, "收起这块板（保留内容，下次启动自动恢复）");
        _closeBtn.Click += (_, _) => RequestClose?.Invoke(this, false);
        DockPanel.SetDock(_closeBtn, Dock.Right);
        controls.Children.Add(_closeBtn);

        // ★ 最小化：只把窗口收成一条板名条，内容 / 笔迹 / 页全留着，点「展开」立刻回来。
        //   与右边「关闭」的区别要说清：关闭 = 这次不看了（下次启动也不自动开，配置留着），
        //   最小化 = 临时让开地方。
        //   ⚠ 不能走 WindowState.Minimized：这块板 ShowInTaskbar=false，最小化后就再没有
        //     任何入口能找回它（任务栏里没有它的按钮，Alt+Tab 也看不见）。
        _minBtn = MakeHeadIconButton(DoodleIcons.Minus(ParseBrush(HeadForeground)),
            "最小化：收成一条板名条，贴在原位；点「展开」或双击整条恢复");
        // ★ 必须按当前状态切：r105 写的是 SetCollapsed(true)，于是收起后按钮变成「展开」，
        //   点它还是 SetCollapsed(true) —— 自己把自己挡在折叠态里，永远展不开。
        _minBtn.Click += (_, _) => SetCollapsed(!_collapsed);
        DockPanel.SetDock(_minBtn, Dock.Right);
        controls.Children.Add(_minBtn);

        _settingsIcon = DoodleIcons.More(ParseBrush(HeadForeground));
        _settingsBtn = MakeHeadIconButton(_settingsIcon, "便签管理 / 主题 / 尺寸 / 板操作");
        _settingsBtn.Click += (_, _) => OpenBoardMenu(_settingsBtn, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        DockPanel.SetDock(_settingsBtn, Dock.Right);
        controls.Children.Add(_settingsBtn);

        _noteManageBtn = MakeTextHeadButton("便签管理", 82, "添加 / 管理这一页的内容模块与便签");
        _noteManageBtn.Click += (_, _) => OpenNoteManageMenu(_noteManageBtn);
        DockPanel.SetDock(_noteManageBtn, Dock.Right);
        controls.Children.Add(_noteManageBtn);

        // 页面下拉：从右上总菜单里的「页面」子菜单搬上来，一键切页 / 新建 / 重命名 / 删除。
        _pageDropBtn = MakeTextHeadButton("页面 ▾", 76, "切换 / 新建 / 重命名 / 删除页面");
        _pageDropBtn.Click += (_, _) => OpenPageList(_pageDropBtn);
        DockPanel.SetDock(_pageDropBtn, Dock.Right);
        controls.Children.Add(_pageDropBtn);

        // 翻页：大屏上直接点 ◀ ▶，与「页面」下拉两条路都能切页。
        var nextPage = MakeHeadIconButton(DoodleIcons.Next(ParseBrush(HeadForeground)), "下一页");
        nextPage.Width = 38;
        nextPage.Height = 38;
        nextPage.Click += (_, _) => SwitchPage(_cfg.ActivePage + 1);
        DockPanel.SetDock(nextPage, Dock.Right);
        controls.Children.Add(nextPage);

        _pageText = new TextBlock
        {
            Text = "页面 1/1",
            FontSize = 12,
            Foreground = ParseBrush(HeadForeground),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 2, 0),
            Padding = new Thickness(6, 6, 6, 6),
            IsHitTestVisible = false
        };
        DockPanel.SetDock(_pageText, Dock.Right);
        controls.Children.Add(_pageText);

        var prevPage = MakeHeadIconButton(DoodleIcons.Prev(ParseBrush(HeadForeground)), "上一页");
        prevPage.Width = 38;
        prevPage.Height = 38;
        prevPage.Click += (_, _) => SwitchPage(_cfg.ActivePage - 1);
        DockPanel.SetDock(prevPage, Dock.Right);
        controls.Children.Add(prevPage);

        // 收起后只留「展开」与「关闭」：一条板名条上排六个按钮，手指根本点不准，
        // 而且那时候点「便签管理」也没地方显示菜单。
        _headHideWhenCollapsed.AddRange(new FrameworkElement[]
        {
            _settingsBtn, _noteManageBtn, _pageDropBtn, nextPage, _pageText, prevPage
        });

        Grid.SetColumn(controls, 1);
        head.Children.Add(controls);

        UpdateHeaderInfo();

        ContextMenu = BuildBoardMenu();
        MouseRightButtonUp += (_, e) =>
        {
            OpenBoardMenu(this, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
            e.Handled = true;
        };

        // 收起时整条板名条都能双击展开（点空白处也算），不必非要点中「展开」两个字
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (!_collapsed || e.ClickCount != 2) return;
            e.Handled = true;
            SetCollapsed(false);
        };

        return head;
    }

    /// <summary>
    /// 最小化 / 展开：只改窗口高度与几处 Visibility，便签、笔迹、页、格式全都不动。
    /// <para>★ 不用 <c>WindowState.Minimized</c>：这块板 <c>ShowInTaskbar=false</c>，
    /// 最小化之后任务栏没有按钮、Alt+Tab 也看不见，等于把板弄丢了。</para>
    /// </summary>
    private void SetCollapsed(bool on, bool persist = true)
    {
        if (_collapsed == on) return;

        if (on)
        {
            // 先把展开高度记下来：等下 Height 会被改成一条板名条，之后就取不回来了
            if (Height > 8) _expandedHeight = Height;
            _collapsed = true;

            _body.Visibility = Visibility.Collapsed;
            _toolBar.Visibility = Visibility.Collapsed;
            foreach (var el in _headHideWhenCollapsed) el.Visibility = Visibility.Collapsed;

            // 同一个按钮切成「展开」文字：36 宽的图标条在板名条上不好点，也读不出是干什么的
            _minBtn.Content = new TextBlock
            {
                Text = "展开",
                FontSize = 12,
                Foreground = ParseBrush(HeadForeground),
                VerticalAlignment = VerticalAlignment.Center
            };
            _minBtn.Width = 64;
            _minBtn.Height = 32;
            _minBtn.ToolTip = "展开这块板（双击板名条也行）";

            // 板名条不需要拖大小，顺手把缩放边关掉，免得手指在条子上乱摸
            ResizeMode = ResizeMode.NoResize;
            UpdateHeaderInfo();
            Height = CollapsedWindowHeight();
        }
        else
        {
            _collapsed = false;

            _body.Visibility = Visibility.Visible;
            _toolBar.Visibility = Visibility.Visible;
            foreach (var el in _headHideWhenCollapsed) el.Visibility = Visibility.Visible;

            _minBtn.Content = DoodleIcons.Minus(ParseBrush(HeadForeground));
            _minBtn.Width = 36;
            _minBtn.Height = 36;
            _minBtn.ToolTip = "最小化：收成一条板名条，贴在原位；点「展开」或双击整条恢复";

            ResizeMode = ResizeMode.CanResize;
            double wa = SystemParameters.WorkArea.Height;
            Height = Math.Max(DoodleBoardMetrics.MinHeight,
                Math.Min(_expandedHeight > 8 ? _expandedHeight : DoodleBoardMetrics.DefaultHeight, wa));
            RebuildNotes();   // 收起期间宽度没变，但缩放排行/手柄要重新量一次，避免露出半截
        }

        if (persist)
        {
            _cfg.Collapsed = _collapsed;
            _cfg.Height = _expandedHeight;   // ★ 存的永远是展开高度
            DoodleBoardService.SaveConfig(_cfg);
        }
    }

    /// <summary>收起后窗口应有的高度：板头那一行 + 木框上下厚度。</summary>
    private double CollapsedWindowHeight()
    {
        double headH = _headRow?.Height.IsAbsolute == true && _headRow.Height.Value > 4
            ? _headRow.Height.Value
            : DoodleBoardMetrics.HeadHeight + HeadTopGap + 8;

        double frame = _frame.Padding.Top + _frame.Padding.Bottom
                     + _frame.BorderThickness.Top + _frame.BorderThickness.Bottom;

        return Math.Round(headH + frame);
    }

    /// <summary>诊断用：当前是否最小化。</summary>
    internal bool DiagCollapsed => _collapsed;

    /// <summary>诊断用：收起后是不是真的只剩一条（高度远小于展开态）。</summary>
    internal double DiagCollapsedHeight => CollapsedWindowHeight();

    /// <summary>诊断用：板头有没有最小化按钮，默认应是图标态而不是「展开」。</summary>
    internal bool DiagHasMinimizeButton =>
        _minBtn != null
        && _minBtn.Visibility == Visibility.Visible
        && (_minBtn.ToolTip?.ToString() ?? "").Contains("最小化", StringComparison.Ordinal);

    /// <summary>诊断用：收起高度落在「一条板名条」的范围里（不吞内容，也不留半截板面）。</summary>
    internal bool DiagCollapseIsCompact =>
        CollapsedWindowHeight() >= 40 && CollapsedWindowHeight() <= 110;

    /// <summary>诊断用：板面那一行是否整块藏起来了。</summary>
    internal bool DiagBodyHidden => _body.Visibility == Visibility.Collapsed;

    /// <summary>诊断用：收起 / 展开一步到位（不写配置，免得污染诊断副本）。</summary>
    internal void DiagCollapse(bool on) => SetCollapsed(on, persist: false);

    /// <summary>
    /// 诊断用：真的点一下板头上那个「展开 / 最小化」按钮，返回点击后是不是已经展开。
    /// <para>★ 断言的正是 r105 那个 bug：按钮点击写死 <c>SetCollapsed(true)</c>，
    /// 收起后文字变成「展开」，点下去还是「收起」，于是永远展不开。</para>
    /// </summary>
    internal bool DiagClickMinButtonAndReturns()
    {
        if (_minBtn == null) return false;
        _minBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        return !_collapsed && _body.Visibility == Visibility.Visible;
    }

    /// <summary>刷新板头的班级 / 时间 / 页码。班级空 = 没设当前班级，不硬写"未知班"占位置。</summary>
    private void UpdateHeaderInfo()
    {
        if (_classText == null || _clockText == null) return;
        try
        {
            string cls = (Store.LoadSettings().SelectedClass ?? "").Trim();
            _classText.Text = string.IsNullOrWhiteSpace(cls) ? "" : cls;
        }
        catch { _classText.Text = ""; }
        // 教室后排也要一眼看出日期，板头时间带年份（跨学期/跨年不会误读）
        _clockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        if (_pageText != null)
        {
            int n = Math.Max(1, _cfg.Pages?.Count ?? 1);
            int i = Math.Clamp(_cfg.ActivePage + 1, 1, n);
            _pageText.Text = $"页面 {i}/{n}";
        }
    }

    /// <summary>板头信息离左边沿两个字符（字号 × 2），大字号时同步变宽。</summary>
    private double HeaderInset => Math.Max(28, Math.Round(FontSize * 2.1));

    /// <summary>
    /// 板头信息与黑板上边沿之间的留白。
    /// <para>★ r103：原来空出整整一行（≈1.9 字号），用户反馈班级时间和菜单栏都偏下。
    /// 现在木色包边已经给出了上边界，这里只留几像素，文字贴着木框上沿走。</para>
    /// </summary>
    private double HeadTopGap => Math.Max(2, Math.Round(FontSize * 0.25));

    /// <summary>
    /// 「页面」下拉：从右上总菜单里的页面子菜单整段搬上来。
    /// 页面列表 + 新建 / 重命名 / 删除，一条路全给到大屏老师。
    /// </summary>
    private void OpenPageList(FrameworkElement anchor)
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 13 };
        for (int i = 0; i < _cfg.Pages.Count; i++)
        {
            int idx = i;
            var item = new MenuItem
            {
                Header = (i == _cfg.ActivePage ? "● " : "○ ") + _cfg.Pages[i].Name,
                IsCheckable = true,
                IsChecked = i == _cfg.ActivePage
            };
            item.Click += (_, _) => SwitchPage(idx);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "＋ 新建页面" };
        add.Click += (_, _) => AddPage();
        menu.Items.Add(add);
        var rename = new MenuItem { Header = "重命名当前页面…" };
        rename.Click += (_, _) => RenamePage();
        menu.Items.Add(rename);
        var del = new MenuItem { Header = "删除当前页面", IsEnabled = _cfg.Pages.Count > 1 };
        del.Click += (_, _) => DeletePage();
        menu.Items.Add(del);
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>板头「便签管理」下拉：把原来藏在右键菜单 / 便签区按钮里的入口集中到一处。</summary>
    private void OpenNoteManageMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 13 };
        FillNoteManageMenu(menu.Items);
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem BuildNoteManageMenuItem()
    {
        var item = new MenuItem
        {
            Header = "便签管理",
            ToolTip = "添加 / 管理这一页的内容模块与便签"
        };
        FillNoteManageMenu(item.Items);
        return item;
    }

    /// <summary>右键菜单与板头下拉共用同一份入口，避免两处菜单内容长歪。</summary>
    private void FillNoteManageMenu(ItemCollection items)
    {
        items.Clear();

        foreach (var info in DoodleModules.All)
        {
            var mi = new MenuItem
            {
                Header = info.Name,
                IsCheckable = true,
                IsChecked = Page.Modules.Any(x => string.Equals(x, info.Key, StringComparison.OrdinalIgnoreCase)),
                ToolTip = info.Hint
            };
            mi.Click += (_, _) => ToggleModule(info.Key, mi.IsChecked);
            items.Add(mi);
        }

        items.Add(new Separator());

        var addManual = new MenuItem
        {
            Header = "＋ 新增手动便签…",
            ToolTip = "在原「新增便签」按钮位置搬上来的入口"
        };
        addManual.Click += (_, _) => AddManualNote();
        items.Add(addManual);

        var addCustom = new MenuItem { Header = "＋ 新建自定义文字模块…" };
        addCustom.Click += (_, _) => AddCustomModule();
        items.Add(addCustom);

        var manageCustom = new MenuItem { Header = "管理自定义模块…", IsEnabled = Page.CustomModules.Count > 0 };
        manageCustom.Click += (_, _) => ManageCustomModules();
        items.Add(manageCustom);

        var import = new MenuItem { Header = "从另一块板导入模块设置…", IsEnabled = DoodleBoardService.Open.Count > 1 };
        import.Click += (_, _) => ImportModulesFromOtherBoard();
        items.Add(import);

        items.Add(new Separator());

        var clearPage = new MenuItem
        {
            Header = "本页不放任何便签",
            ToolTip = "清空这一页的展示模块和便签，留一块纯手写页面"
        };
        clearPage.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "清空这一页的所有便签和展示模块？手写笔迹会保留。",
                    "板面", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            ClearPageNotes();
        };
        items.Add(clearPage);
    }

    /// <summary>把当前页恢复成纯手写页。空模块列表是合法状态，绝不能再用默认三类填回来。</summary>
    private void ClearPageNotes()
    {
        Page.Modules.Clear();
        Page.Notes.Clear();
        Page.CustomModules.Clear();
        Page.Placements.Clear();
        Page.AutoStyles.Clear();
        DoodleBoardService.SaveConfig(_cfg);
        RebuildNotes();
    }

    /// <summary>双击板头班级 / 时间 → 打开板头专用格式窗。</summary>
    private void EditHeaderStyle()
    {
        if (_cfg.Locked) return;
        _cfg.HeaderStyle ??= new DoodleHeaderStyle();
        var dlg = new DoodleHeaderStyleWindow(_cfg.HeaderStyle) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _cfg.HeaderStyle = dlg.Draft;
        ApplyHeaderStyle();
        DoodleBoardService.SaveConfig(_cfg);
    }

    /// <summary>把板头样式套到文字上；行高跟着字号长，后排看得清也不会被裁掉。</summary>
    private void ApplyHeaderStyle()
    {
        if (_titleText == null || _classText == null || _clockText == null) return;
        var hs = _cfg.HeaderStyle ??= new DoodleHeaderStyle();

        _titleText.FontSize = hs.SafeTitleSize;
        _classText.FontSize = hs.SafeInfoSize;
        _clockText.FontSize = hs.SafeInfoSize;
        _titleText.FontWeight = hs.Bold ? FontWeights.Bold : FontWeights.Normal;

        Brush fg = string.IsNullOrWhiteSpace(hs.Color) ? ParseBrush(_theme.Ink) : ParseBrush(hs.Color);
        _titleText.Foreground = fg;
        _classText.Foreground = fg;
        _clockText.Foreground = fg;

        if (_headRow != null)
        {
            // 板头现在只有一行（信息 + 按钮并排）：行高 = 文字实际高度与 42 取大，再加顶部留白。
            // 原来按「信息行 + 36 固定操作行」两段算，并成一行后要减掉多出的那 28~34，否则板头空一大片。
            _headRow.Height = new GridLength(Math.Max(
                DoodleBoardMetrics.HeadHeight,
                Math.Max(hs.SafeTitleSize, hs.SafeInfoSize) * 1.6 + 12) + HeadTopGap + 8);
        }
    }

    // ======================================================================
    //  底部工具带
    // ======================================================================

    /// <summary>
    /// 工具带外壳。内容随模式重建（见 <see cref="RebuildToolBar"/>）。
    /// <para>★ 用 WrapPanel 而不是 DockPanel/Grid：板最窄 300 宽，一行放不下六七个 44px 按钮。
    /// WrapPanel 会自动折成两行，带子高度随内容长高 —— 按钮永远不会被裁成半个。</para>
    /// </summary>
    private Border BuildToolBar()
    {
        _toolItems = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 板名：钉在工具带最右边 —— 也就是整块板的最右下角（双击改名）。
        // ★ r108：原来它是 body 上的一个绝对定位浮层，会压在便签卡上；现在改成工具带这一行里
        //   右停靠的一块文字，永远与工具栏同高、不压任何内容，板被拉宽拉窄也不会跑位。
        _titleText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_cfg.Name) ? "涂鸦板" : _cfg.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = ParseBrush(HeadForeground),
            // 与按钮最后一行齐底：工具带折成两行时板名贴着右下角，不会飘到两行中间
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 4, 5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.Hand,
            ToolTip = "双击改板名"
        };
        _titleText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { e.Handled = true; RenameBoard(); }
        };
        UpdateTitleMaxWidth();

        DockPanel.SetDock(_titleText, Dock.Right);
        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(_titleText);   // 先右停靠，剩下的宽都给工具按钮
        row.Children.Add(_toolItems);

        return new Border
        {
            Padding = new Thickness(12, 4, 8, 6),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row
        };
    }

    /// <summary>
    /// 板名最多占工具带宽度的四成：板拉窄时自动收窄并省略号，绝不把工具按钮挤没；
    /// 板很宽时也不再让板名长得像标题（封顶 220）。
    /// </summary>
    private void UpdateTitleMaxWidth()
    {
        if (_titleText == null) return;
        double w = double.IsNaN(Width) || Width < 40 ? DoodleBoardMetrics.DefaultWidth : Width;
        _titleText.MaxWidth = Math.Max(90, Math.Min(220, w * 0.4));
    }

    /// <summary>当前笔色 / 字色等图标的描边笔。</summary>
    private Brush HeadBrush => ParseBrush(HeadForeground);

    private void RebuildToolBar()
    {
        _toolItems.Children.Clear();
        _penSlider = null;
        _eraserSlider = null;
        _colorCapsuleBtn = null;
        _toolBar.BorderBrush = ParseBrush(_theme.Divider);

        // ★ r108：锁定排在最前（「手写 / 便签」切换按钮的左边），锁上后它变成「已锁定」，
        //   在工具带上一眼就能看见、也能直接点回来解锁 —— 不再需要去右下角找那个浮层。
        if (_lockBtn != null)
        {
            _toolItems.Children.Add(_lockBtn);
            _toolItems.Children.Add(Divider());
            UpdateLockButton();
        }

        // ★ r99：切换按钮改回<b>文字</b>。上一版用 Pen 图标，紧跟着又是画笔图标，
        //   工具带最前面出现「两支笔」，老师分不清哪个是切层、哪个是落笔。
        //   文字「手写 / 便签」一眼说明「点了会去哪一层」，位置仍在工具带最前。
        bool erasing = _ink.EditingMode == InkCanvasEditingMode.EraseByPoint;
        _modeBtn = MakeToolTextButton(_inkActive ? "便签" : "手写",
            _inkActive ? "切回便签层（便签可拖、可编辑）" : "切到手写层（整页可写）",
            () => SetInkActive(!_inkActive));
        _toolItems.Children.Add(_modeBtn);
        _toolItems.Children.Add(Divider());

        // 画笔 / 橡皮仍在；再点一次当前工具退出手写（与手写切换按钮两条路并行）。
        _toolItems.Children.Add(MakeToolButton(DoodleIcons.Pen(HeadBrush),
            "画笔：整页可写（再点一次退出手写）", () =>
            {
                if (_inkActive && !erasing) SetInkActive(false);
                else SetInkActive(true, InkCanvasEditingMode.Ink);
            }, selected: _inkActive && !erasing));
        _toolItems.Children.Add(MakeToolButton(DoodleIcons.Eraser(HeadBrush),
            "橡皮：擦笔迹（再点一次退出手写）", () =>
            {
                if (_inkActive && erasing) SetInkActive(false);
                else SetInkActive(true, InkCanvasEditingMode.EraseByPoint);
            }, selected: _inkActive && erasing));
        _toolItems.Children.Add(Divider());

        if (_inkActive) BuildInkTools();
        else BuildNoteTools();
    }

    /// <summary>
    /// 切换当前页的输入层。笔迹与便签同页叠加、始终可见，只改命中权：
    /// 手写层收事件时便签只读（否则手指点在便签上写不出字），退回选择后便签恢复可拖可编辑。
    /// </summary>
    private void SetInkActive(bool active, InkCanvasEditingMode mode = InkCanvasEditingMode.Ink)
    {
        // 锁定时工具带还能看见，但绝不进手写层；否则「锁定」只挡拖动、挡不住落笔。
        active = active && !_cfg.Locked;
        // ★ r108：状态没变就不重排工具带。锁定按钮现在就在工具带里，点它的时候若整条重建，
        //   会把这个正在响应 Click 的按钮从可视树里摘掉（鼠标捕获悬空）。
        bool changed = active != _inkActive || (active && _ink.EditingMode != mode);
        _inkActive = active;
        _ink.IsHitTestVisible = active;
        _ink.EditingMode = active ? mode : InkCanvasEditingMode.None;
        _notesHost.IsHitTestVisible = !active;
        UpdateModeButton();
        ApplyInkStyle();
        if (changed) RebuildToolBar();
    }

    private void UpdateModeButton()
    {
        if (_modeBtn == null) return;
        // r99：文字按钮，标的是「点它要去哪一层」。
        _modeBtn.Content = _inkActive ? "便签" : "手写";
        _modeBtn.ToolTip = _inkActive ? "切回便签层（便签可拖、可编辑）" : "切到手写层（整页可写）";
    }

    // ---- 便签模式：字号 + 字色（颜色收成一个胶囊，点开再选）----

    private void BuildNoteTools()
    {
        _toolItems.Children.Add(ToolGroup(
            ToolLabel("字号"),
            MakeToolButton(DoodleIcons.Minus(HeadBrush), "字小一点", () => StepFont(-DoodleStyle.FontStep)),
            ValueChip(FontSize.ToString("0")),
            MakeToolButton(DoodleIcons.Plus(HeadBrush), "字大一点", () => StepFont(DoodleStyle.FontStep))));

        _toolItems.Children.Add(Divider());
        _toolItems.Children.Add(MakeTextColorCapsule());
    }

    /// <summary>字色胶囊：一个按钮 + 一个当前色点，点开再选色。与手写笔色胶囊同一套视觉。</summary>
    private Button MakeTextColorCapsule()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        content.Children.Add(DoodleIcons.Swatch(TextColor, false, Brushes.Transparent, 14,
            ParseBrush(_theme.Chalk ? "#66FFFFFF" : "#40000000")));
        content.Children.Add(new TextBlock
        {
            Text = "  颜色",
            FontSize = 13,
            Foreground = ParseBrush(HeadForeground),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = " ▾",
            FontSize = 12,
            Foreground = ParseBrush(_theme.Muted),
            VerticalAlignment = VerticalAlignment.Center
        });

        var accent = DoodleIcons.Parse(_theme.Accent);
        var btn = new Button
        {
            Content = content,
            Width = 88,
            Height = DoodleBoardMetrics.ToolButtonSize,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "便签文字颜色",
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle(),
            Background = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)),
            BorderBrush = ParseBrush(_theme.Divider),
            BorderThickness = new Thickness(1)
        };
        btn.Click += (_, _) => OpenTextPalette(btn);
        _colorCapsuleBtn = btn;
        return btn;
    }

    private void OpenTextPalette(Button anchor)
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 13 };
        foreach (var (name, hex) in DoodleStyle.NoteColors)
        {
            var mi = new MenuItem
            {
                Header = name,
                Icon = DoodleIcons.Swatch(hex, string.Equals(TextColor, hex, StringComparison.OrdinalIgnoreCase),
                    ParseBrush(_theme.Accent), 14, ParseBrush(_theme.Chalk ? "#66FFFFFF" : "#40000000"))
            };
            mi.Click += (_, _) => SetTextColor(hex);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- 手写模式：当前工具只露一套滑块；颜色收进一个胶囊 ----

    private void BuildInkTools()
    {
        // 笔 / 橡皮按钮在 RebuildToolBar 头部（同时是输入层开关）。
        // ★ 用户要求：点笔只露笔粗、点橡皮只露橡皮范围，两条带子不再同时占板面。
        bool erasing = _ink.EditingMode == InkCanvasEditingMode.EraseByPoint;
        if (erasing)
        {
            _eraserSlider = MakeToolSlider("橡皮范围", DoodleStyle.MinEraser, DoodleStyle.MaxEraser,
                EraserSize, "橡皮范围：拖滑块调大小", v =>
                {
                    _cfg.EraserSize = DoodleStyle.ClampEraser(v);
                    ApplyInkStyle();
                    DoodleBoardService.SaveConfig(_cfg);
                });
            _toolItems.Children.Add(ToolGroup(ToolLabel("橡皮范围"), _eraserSlider));
        }
        else
        {
            _penSlider = MakeToolSlider("笔粗", DoodleStyle.MinPenWidth, DoodleStyle.MaxPenWidth,
                PenWidth, "手写笔粗细：拖滑块调笔宽", v =>
                {
                    _cfg.PenWidth = DoodleStyle.ClampPen(v);
                    _cfg.ThickPen = _cfg.PenWidth > 4;
                    ApplyInkStyle();
                    DoodleBoardService.SaveConfig(_cfg);
                });
            _toolItems.Children.Add(ToolGroup(ToolLabel("笔粗"), _penSlider));
        }

        _toolItems.Children.Add(Divider());
        _toolItems.Children.Add(MakeToolButton(DoodleIcons.Trash(HeadBrush), "清空全部笔迹", ClearInk));
        _toolItems.Children.Add(MakePenColorCapsule());
    }

    /// <summary>
    /// 触屏可用的滑块：轨道给足 36 高，数值跟在右边实时变。
    /// ★ 不在 ValueChanged 里重建工具带 —— 重建会把正在拖的滑块换掉，手指一滑就断。
    /// </summary>
    private FrameworkElement MakeToolSlider(string tag, double min, double max, double value,
        string tip, Action<double> apply)
    {
        var valueText = new TextBlock
        {
            Text = value.ToString("0"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = ParseBrush(HeadForeground),
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Tag = tag,
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 118,
            Height = 36,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.None,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
            Focusable = false
        };
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = e.NewValue.ToString("0");
            apply(e.NewValue);
        };
        return ToolGroup(slider, valueText);
    }

    /// <summary>笔色胶囊：一个按钮 + 一个小色点，点开再选色。比横排六颗色点省一半宽度。</summary>
    private Button MakePenColorCapsule()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        content.Children.Add(DoodleIcons.Swatch(PenColor, false, Brushes.Transparent, 14,
            ParseBrush(_theme.Chalk ? "#66FFFFFF" : "#40000000")));
        content.Children.Add(new TextBlock
        {
            Text = "  颜色",
            FontSize = 13,
            Foreground = ParseBrush(HeadForeground),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = " ▾",
            FontSize = 12,
            Foreground = ParseBrush(_theme.Muted),
            VerticalAlignment = VerticalAlignment.Center
        });

        var accent = DoodleIcons.Parse(_theme.Accent);
        var btn = new Button
        {
            Content = content,
            Width = 88,
            Height = DoodleBoardMetrics.ToolButtonSize,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "手写笔颜色",
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle(),
            Background = new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)),
            BorderBrush = ParseBrush(_theme.Divider),
            BorderThickness = new Thickness(1)
        };
        btn.Click += (_, _) => OpenPenPalette(btn);
        _colorCapsuleBtn = btn;
        return btn;
    }

    private void OpenPenPalette(Button anchor)
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 13 };
        foreach (var (name, hex) in DoodleStyle.PenColors)
        {
            var mi = new MenuItem
            {
                Header = name,
                Icon = DoodleIcons.Swatch(hex, string.Equals(PenColor, hex, StringComparison.OrdinalIgnoreCase),
                    ParseBrush(_theme.Accent), 14, ParseBrush(_theme.Chalk ? "#66FFFFFF" : "#40000000"))
            };
            mi.Click += (_, _) => SetPenColor(hex);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>分组之间的横向留白。用它代替竖分隔线：竖线换行时会孤零零留在上一行末尾。</summary>
    private FrameworkElement LeftPad() => new Border { Width = 14 };

    private TextBlock ToolLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = ParseBrush(_theme.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
    }

    /// <summary>
    /// 把几个控件捆成不可分割的一组。
    /// <para>★ WrapPanel 是流式换行的：不分组就会把"颜色 + 六个色点"从中间劈开 ——
    /// 第一行末尾孤零零一个圆点、其余五个掉到第二行，看起来像排版坏了。</para>
    /// </summary>
    private StackPanel ToolGroup(params UIElement[] items)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var it in items) sp.Children.Add(it);
        return sp;
    }

    /// <summary>数值显示块（不可点，只是让用户看到当前值）。</summary>
    private FrameworkElement ValueChip(string text)
    {
        return new Border
        {
            MinWidth = 38,
            Height = DoodleBoardMetrics.ToolButtonSize,
            Margin = new Thickness(0, 0, 2, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = ParseBrush(HeadForeground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private FrameworkElement Divider()
    {
        return new Border
        {
            Width = 1,
            Height = 22,
            Margin = new Thickness(8, 0, 8, 0),
            Background = ParseBrush(_theme.Divider),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>图标按钮。<paramref name="selected"/> 用底色 + 描边表示，不靠"变亮变暗"。</summary>
    private Button MakeToolButton(FrameworkElement icon, string tip, Action onClick, bool selected = false)
    {
        var accentColor = DoodleIcons.Parse(_theme.Accent);

        var btn = new Button
        {
            Content = icon,
            Width = DoodleBoardMetrics.ToolButtonSize,
            Height = DoodleBoardMetrics.ToolButtonSize,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle()
        };

        if (selected)
        {
            btn.Background = new SolidColorBrush(Color.FromArgb(0x2E, accentColor.R, accentColor.G, accentColor.B));
            btn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, accentColor.R, accentColor.G, accentColor.B));
            btn.BorderThickness = new Thickness(1.5);
        }
        else
        {
            btn.Background = Brushes.Transparent;
            btn.BorderBrush = Brushes.Transparent;
            btn.BorderThickness = new Thickness(1.5);
        }

        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// 工具带里的文字按钮（r99：手写 / 便签切层）。带淡底色与描边，
    /// 在深色黑板上也看得出「这是一颗可点的按钮」，命中区仍是 44 触屏下限。
    /// </summary>
    private Button MakeToolTextButton(string text, string tip, Action onClick)
    {
        var accent = DoodleIcons.Parse(_theme.Accent);
        var btn = new Button
        {
            Content = text,
            MinWidth = 64,
            Height = DoodleBoardMetrics.ToolButtonSize,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = ParseBrush(HeadForeground),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle(),
            Background = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>色点按钮：色点本体 22~24，但命中区仍是 40×44，手指点得到。</summary>
    private Button MakeSwatchButton(string hex, bool selected, string tip, Action onClick, double dot = 20)
    {
        var accent = ParseBrush(_theme.Accent);
        var ring = selected ? accent : Brushes.Transparent;

        var btn = new Button
        {
            Content = DoodleIcons.Swatch(hex, selected, ring, dot,
                ParseBrush(_theme.Chalk ? "#66FFFFFF" : "#40000000")),
            Width = DoodleBoardMetrics.ToolButtonSize,
            Height = DoodleBoardMetrics.ToolButtonSize,
            Margin = new Thickness(0, 0, 2, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle(),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button MakeHeadIconButton(FrameworkElement icon, string tip)
    {
        return new Button
        {
            Content = icon,
            Width = 36,
            Height = 36,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle(),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
    }

    private Button MakeTextHeadButton(string text, double width, string tip)
    {
        return new Button
        {
            Content = text,
            Width = width,
            Height = 32,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Focusable = false,
            Template = BuildGhostTemplate(),
            Style = HoverStyle(),
            Background = Brushes.Transparent,
            // 纯文字在深色板上看不出可点，给一圈淡描边当"按钮边界"
            BorderBrush = ParseBrush(_theme.Divider),
            BorderThickness = new Thickness(1)
        };
    }

    /// <summary>悬停反馈只改透明度 —— 选中态用的是 Background，两者互不覆盖。</summary>
    private static Style HoverStyle()
    {
        var st = new Style(typeof(Button));
        var tr = new Trigger { Property = IsMouseOverProperty, Value = true };
        tr.Setters.Add(new Setter(OpacityProperty, 0.7));
        st.Triggers.Add(tr);
        return st;
    }

    /// <summary>重建板菜单。每次打开都重建，勾选态才不会跟实际状态脱节。</summary>
    private ContextMenu BuildBoardMenu()
    {
        var menu = new ContextMenu { FontFamily = this.FontFamily, FontSize = 12 };

        // ★ 排序原则（用户要求）：常用在上、不常用在下。
        //   ① 便签管理 / 黑板风格 / 锁定 —— 上课时最常点
        //   ② 新建 / 切换板、尺寸 —— 偶尔调整
        //   ③ 格式清除、改名 —— 布置时偶尔用
        //   ④ 删除 —— 危险操作，压在最底
        // 页面子菜单已按用户要求迁移到板头「页面 ▾」，这里不再重复。

        // ---- 便签管理（板头下拉与这里共用同一份菜单）----
        menu.Items.Add(BuildNoteManageMenuItem());

        // ---- 黑板风格 ----
        var themes = new MenuItem { Header = "黑板风格" };
        foreach (var t in DoodleBoardThemes.All)
        {
            var mi = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = t.Key == _cfg.ThemeKey };
            mi.Click += (_, _) => SetTheme(t.Key);
            themes.Items.Add(mi);
        }
        menu.Items.Add(themes);

        var lockItem = new MenuItem { Header = "锁定（不能拖、不能写）", IsCheckable = true, IsChecked = _cfg.Locked };
        lockItem.Click += (_, _) => SetLocked(lockItem.IsChecked);
        menu.Items.Add(lockItem);

        menu.Items.Add(new Separator());

        // ---- 板（多块切换）----
        var boards = new MenuItem { Header = "切换到板", IsEnabled = DoodleBoardService.Open.Count > 1 };
        foreach (var other in DoodleBoardService.Open)
        {
            if (ReferenceEquals(other, this)) continue;
            var mi = new MenuItem { Header = other.Config.Name };
            mi.Click += (_, _) => other.BringToFront();
            boards.Items.Add(mi);
        }
        menu.Items.Add(boards);
        var newBoard = new MenuItem { Header = "＋ 新建一块板" };
        newBoard.Click += (_, _) => DoodleBoardService.CreateNew();
        menu.Items.Add(newBoard);

        foreach (var p in DoodleBoardMetrics.Presets)
        {
            var mi = new MenuItem { Header = $"尺寸：{p.Name}（{(int)p.W} × {(int)p.H}）" };
            mi.Click += (_, _) => ApplyPreset(p.W, p.H);
            menu.Items.Add(mi);
        }
        var fit = new MenuItem { Header = "尺寸：铺满屏幕" };
        fit.Click += (_, _) => FillWorkArea();
        menu.Items.Add(fit);

        menu.Items.Add(new Separator());

        var headerStyleItem = new MenuItem
        {
            Header = "板头文字格式…",
            ToolTip = "单独放大板名 / 班级 / 时间，教室后排也看得清"
        };
        headerStyleItem.Click += (_, _) => EditHeaderStyle();
        menu.Items.Add(headerStyleItem);

        var clearAuto = new MenuItem
        {
            Header = "清除自动便签的字体格式",
            ToolTip = "把便签恢复成板默认字体",
            IsEnabled = Page.AutoStyles.Count > 0
        };
        clearAuto.Click += (_, _) => ClearAutoStyles();
        menu.Items.Add(clearAuto);

        var rename = new MenuItem { Header = "改板名…" };
        rename.Click += (_, _) => RenameBoard();
        menu.Items.Add(rename);

        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = "删除这块板…" };
        del.Click += (_, _) =>
        {
            string label = string.IsNullOrWhiteSpace(_cfg.Name) ? "这块板" : $"「{_cfg.Name}」";
            if (MessageBox.Show(this,
                    $"彻底删除{label}？板上的便签和手写笔迹都会删掉，且无法恢复。\n如果只是暂时不想看到它，请用板头的 × 收起。",
                    "删除黑板", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            RequestClose?.Invoke(this, true);
        };
        menu.Items.Add(del);

        return menu;
    }

    // ---- 板面 / 模块 操作 ----

    private void SwitchPage(int index)
    {
        if (index < 0 || index >= _cfg.Pages.Count || index == _cfg.ActivePage) return;
        SaveInk();   // ★ 必须在换页前落盘，否则当前页的笔迹会被写进新页文件
        _cfg.ActivePage = index;
        DoodleBoardService.MigrateLegacyInk(_cfg.Id, Page.Id);
        LoadInk();
        DoodleBoardService.SaveConfig(_cfg);
        UpdateHeaderInfo();
        RebuildNotes();
    }

    /// <summary>
    /// 一键新建板面：不再弹命名窗，也不再塞默认三类便签。
    /// 便签默认只放第一板面，新板面从一张空白页开始，由用户显式添加内容。
    /// </summary>
    private void AddPage()
    {
        SaveInk();   // 换页前落盘，否则当前页笔迹会被写到新页文件
        var page = new DoodleBoardPage
        {
            Name = "板面 " + (_cfg.Pages.Count + 1),
            Modules = new List<string>()
        };
        _cfg.Pages.Add(page);
        _cfg.ActivePage = _cfg.Pages.Count - 1;
        DoodleBoardService.MigrateLegacyInk(_cfg.Id, page.Id);
        LoadInk();
        DoodleBoardService.SaveConfig(_cfg);
        UpdateHeaderInfo();
        RebuildNotes();
    }

    private void RenamePage()
    {
        var dlg = new DoodleNoteEditWindow(Page.Name, "", showBody: false) { Owner = this };
        dlg.Title = "重命名板面";
        if (dlg.ShowDialog() != true) return;
        Page.Name = string.IsNullOrWhiteSpace(dlg.NoteTitle) ? Page.Name : dlg.NoteTitle;
        DoodleBoardService.SaveConfig(_cfg);
        UpdateHeaderInfo();
    }

    private void DeletePage()
    {
        if (_cfg.Pages.Count <= 1) return;
        if (MessageBox.Show(this, $"删除板面「{Page.Name}」？这张板面上的便签和手写笔迹会一起删掉。",
                "板面", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        string removedPageId = Page.Id;
        _cfg.Pages.RemoveAt(_cfg.ActivePage);
        _cfg.ActivePage = Math.Clamp(_cfg.ActivePage, 0, _cfg.Pages.Count - 1);
        DoodleBoardService.SaveConfig(_cfg);
        try
        {
            var p = DoodleBoardService.InkPath(_cfg.Id, removedPageId);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
        DoodleBoardService.MigrateLegacyInk(_cfg.Id, Page.Id);
        LoadInk();
        UpdateHeaderInfo();
        RebuildNotes();
    }

    private void ToggleModule(string key, bool on)
    {
        Page.Modules ??= new List<string>();
        bool has = Page.Modules.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        if (on && !has) Page.Modules.Add(key);
        if (!on && has) Page.Modules.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

        // ★ 「自定义」只是开关，真正的卡片来自 Page.CustomModules。
        //   一条都没建过时勾上它会一张卡都不出（看起来像没反应），这里自动补一张空白卡，
        //   勾上立刻有东西可双击编辑；要再加就在菜单里「＋ 新建自定义文字模块」。
        if (on && string.Equals(key, DoodleModules.Custom, StringComparison.OrdinalIgnoreCase))
            EnsureCustomModule(Page);

        DoodleBoardService.SaveConfig(_cfg);
        RebuildNotes();
    }

    /// <summary>
    /// 勾上「自定义」这条模块时，若 <c>CustomModules</c> 还是空的，补一张空白卡。
    /// <para>★ 板上的自定义卡片是按 <c>CustomModules</c> 逐条生成的，模块键只是「允许显示」的开关；
    /// 只有开关没有条目，就等于勾了没反应。这里保证勾上立刻有东西可看、可双击编辑。</para>
    /// </summary>
    /// <returns>true = 本次自动补了一张。</returns>
    internal static bool EnsureCustomModule(DoodleBoardPage page)
    {
        page.CustomModules ??= new List<DoodleCustomModule>();
        if (page.CustomModules.Count > 0) return false;
        page.CustomModules.Add(new DoodleCustomModule { Title = "自定义模块", Body = "" });
        return true;
    }

    /// <summary>原便签区「+ 新增便签」按钮搬进板头「便签管理」后的入口。</summary>
    private void AddManualNote()
    {
        if (_cfg.Locked) return;
        var note = new DoodleNote { Title = "新便签", Body = "" };
        Page.Notes.Add(note);
        RebuildNotes();
        DoodleBoardService.SaveConfig(_cfg);
        EditNote(note);
    }

    private void AddCustomModule()
    {
        var dlg = new DoodleNoteEditWindow("新模块", "") { Owner = this };
        dlg.Title = "新建自定义文字模块";
        if (dlg.ShowDialog() != true) return;
        Page.CustomModules ??= new List<DoodleCustomModule>();
        Page.CustomModules.Add(new DoodleCustomModule
        {
            Title = string.IsNullOrWhiteSpace(dlg.NoteTitle) ? "自定义模块" : dlg.NoteTitle,
            Body = dlg.NoteBody
        });
        if (!Page.Modules.Any(x => string.Equals(x, DoodleModules.Custom, StringComparison.OrdinalIgnoreCase)))
            Page.Modules.Add(DoodleModules.Custom);
        DoodleBoardService.SaveConfig(_cfg);
        RebuildNotes();
    }

    private void ManageCustomModules()
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 12 };
        foreach (var m in Page.CustomModules.ToList())
        {
            var item = new MenuItem { Header = m.Title };
            item.Click += (_, _) =>
            {
                var dlg = new DoodleNoteEditWindow(m.Title, m.Body) { Owner = this };
                dlg.Title = "编辑自定义模块";
                var sub = new ContextMenu { FontFamily = FontFamily, FontSize = 12 };
                var editOk = new MenuItem { Header = "编辑…" };
                editOk.Click += (_, _) =>
                {
                    if (dlg.ShowDialog() != true) return;
                    m.Title = dlg.NoteTitle; m.Body = dlg.NoteBody;
                    DoodleBoardService.SaveConfig(_cfg);
                    RebuildNotes();
                };
                var del = new MenuItem { Header = "删除" };
                del.Click += (_, _) =>
                {
                    Page.CustomModules.Remove(m);
                    DoodleBoardService.SaveConfig(_cfg);
                    RebuildNotes();
                };
                sub.Items.Add(editOk); sub.Items.Add(del);
                sub.PlacementTarget = this;
                sub.IsOpen = true;
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = _settingsBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ImportModulesFromOtherBoard()
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 12 };
        foreach (var other in DoodleBoardService.Open)
        {
            if (ReferenceEquals(other, this)) continue;
            var mi = new MenuItem { Header = other.Config.Name + " · " + other.Config.Pages[other.Config.ActivePage].Name };
            mi.Click += (_, _) =>
            {
                var src = other.Config.Pages[other.Config.ActivePage];
                Page.Modules = new List<string>(src.Modules);
                DoodleBoardService.SaveConfig(_cfg);
                RebuildNotes();
            };
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = _settingsBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>把这块板临时带到前面（多板切换用）。板永不抢激活，这里只改 z 序一次。</summary>
    public void BringToFront()
    {
        try
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            if (src == null) return;
            SetWindowPos(src.Handle, new IntPtr(-1), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    private void OpenBoardMenu(FrameworkElement anchor, System.Windows.Controls.Primitives.PlacementMode mode)
    {
        var menu = BuildBoardMenu();
        ContextMenu = menu;
        menu.PlacementTarget = anchor;
        menu.Placement = mode;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 圆角透明按钮模板。★ Button 没有 CornerRadius 属性，不改模板就是方角系统控件，
    /// 在一整块圆角板上非常扎眼。
    /// <para>Background / BorderBrush 走 TemplateBinding：工具带靠这两个属性表达"当前选中"。</para>
    /// </summary>
    private static ControlTemplate BuildGhostTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ======================================================================
    //  主题
    // ======================================================================

    /// <summary>
    /// 木色包边厚度（DIP）。★ r103：十套板面统一用这一条木框；r108 按用户要求细了一半。
    /// <para>按板宽取 1%（夹在 5~11）：560 的小板是 6、1200 的大板到顶 11。
    /// 原来是 2%（10~22），在教室大屏上像一圈窗套，抢黑板本身的画面。</para>
    /// </summary>
    private double WoodFrameWidth
    {
        get
        {
            double w = double.IsNaN(Width) || Width < 1 ? DoodleBoardMetrics.DefaultWidth : Width;
            return Math.Clamp(Math.Round(w * 0.01), 5, 11);
        }
    }

    /// <summary>
    /// 统一的木框填充：一条对角渐变，深浅木色交替走几道，模拟木纹的光带。
    /// ★ 只做一条静态画刷给所有板面共用（Freeze 后不占渲染开销），
    /// 所以「像不像黑板」这件事只由木框决定，与十套板面材质互不干扰。
    /// </summary>
    private static readonly Brush WoodFrameFill = BuildWoodFrameFill();

    private static Brush BuildWoodFrameFill()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        // ★ 色标压得比第一版深一档：浅色板（亚麻米 / 薄荷纸 / 白板）上浅木色会和板面糊成一片，
        //   深木才能保证十套板面都看得出「一条木框」。
        (double At, uint Rgb)[] stops =
        {
            (0.00, 0x5E3E20), (0.12, 0x7C5430), (0.26, 0x664324),
            (0.40, 0x8A5C33), (0.54, 0x674324), (0.68, 0x7E5532),
            (0.82, 0x64421F), (0.93, 0x77502D), (1.00, 0x4E3319)
        };
        foreach (var s in stops)
            b.GradientStops.Add(new GradientStop(
                Color.FromRgb((byte)(s.Rgb >> 16), (byte)(s.Rgb >> 8), (byte)s.Rgb), s.At));
        b.Freeze();
        return b;
    }

    private void ApplyTheme()
    {
        _theme = DoodleBoardThemes.Get(_cfg.ThemeKey);

        // ★ r103：所有板面统一木色包边 —— 远看就是一块挂在墙上的真黑板。
        //   木色在 Border 的 Background 上铺满、正文靠 Padding 内缩露出木条，
        //   BorderThickness 只留 1px 深木描边压边（若拿它当木条，BorderBrush 会把渐变整片盖掉）。
        //   各主题自带的 Border / FrameWidth 保留在模型里兼容老数据，但不再决定这块板的边框。
        _frame.Background = WoodFrameFill;
        _frame.BorderBrush = ParseBrush("#5B3B1F");
        _frame.BorderThickness = new Thickness(1);
        _frame.Padding = new Thickness(WoodFrameWidth - 1);
        // 外圆角跟着木条走：内面是 4，半径差不超过木条宽度，四个角上才不会露出"月牙"
        _frame.CornerRadius = new CornerRadius(Math.Max(7, Math.Min(12, WoodFrameWidth + 3)));

        _surface.Background = ParseBrush(_theme.Background);
        // 木框与书写面之间压一道暗线：浅色板上木色再深也可能和板面接近，这条缝负责把边界说清楚
        _surface.BorderBrush = ParseBrush("#3A2612");
        _surface.BorderThickness = new Thickness(1);
        // 木框内角要小圆角，圆角一大就会在木条上露出四个"月牙"
        _surface.CornerRadius = new CornerRadius(4);

        // 板头文字由独立样式控制（可单独放大到 96）；跟随主题时也走同一个入口
        ApplyHeaderStyle();

        // 板头两个图标是常驻的，颜色得跟着主题走（工具带每次重建会自己用新色）
        var headBrush = ParseBrush(HeadForeground);
        if (_closeIcon != null) _closeIcon.Stroke = headBrush;
        if (_settingsIcon != null) _settingsIcon.Stroke = headBrush;

        if (_noteManageBtn != null) _noteManageBtn.Foreground = ParseBrush(HeadForeground);
        if (_pageDropBtn != null) _pageDropBtn.Foreground = ParseBrush(HeadForeground);
        if (_titleText != null) _titleText.Foreground = ParseBrush(HeadForeground);
        if (_lockBtn != null) _lockBtn.Foreground = ParseBrush(HeadForeground);
        UpdateLockButton();

        ApplyInkStyle();

        // ★ 便签层和笔迹层永远都在页面上，不再靠 Visibility 切成两张"页面"；
        // 只由 SetInkActive() 改命中权。这样写在便签上的字不会被藏起来。
        RebuildToolBar();
        RebuildNotes();
    }

    // ---- 可调参数（一律走 DoodleStyle 的夹取，界面与存档不会打架） ----

    /// <summary>便签标题字号。</summary>
    private double FontSize => DoodleStyle.ClampFont(_cfg.FontSize ?? DoodleStyle.DefaultFontSize);

    /// <summary>便签正文字号。</summary>
    private double BodyFontSize => Math.Max(10.5, FontSize - DoodleStyle.BodyShrink);

    /// <summary>自定义文字色；没设就跟随主题（缺交那种警示红仍优先）。</summary>
    private string? CustomTextColor =>
        string.IsNullOrWhiteSpace(_cfg.TextColor) ? null : _cfg.TextColor;

    private string TextColor => CustomTextColor ?? _theme.NoteText;

    private double PenWidth => DoodleStyle.ClampPen(
        _cfg.PenWidth ?? (_cfg.ThickPen ? 4.2 : DoodleStyle.DefaultPenWidth));

    private string PenColor => string.IsNullOrWhiteSpace(_cfg.PenColor)
        ? _theme.DefaultPen
        : _cfg.PenColor!;

    private double EraserSize => DoodleStyle.ClampEraser(_cfg.EraserSize ?? DoodleStyle.DefaultEraser);

    /// <summary>
    /// 把笔色、笔宽、橡皮直径一次性套到 InkCanvas 上。
    /// <para>★ 橡皮必须用 <c>EraseByPoint + EraserShape</c> 才有"范围"可言；
    /// <c>EraseByStroke</c> 是整笔擦除，跟大小无关，调了也不会变。</para>
    /// </summary>
    private void ApplyInkStyle()
    {
        var da = _ink.DefaultDrawingAttributes;
        da.Color = DoodleIcons.Parse(PenColor);
        da.Width = PenWidth;
        da.Height = PenWidth;

        double e = EraserSize;
        _ink.EraserShape = new EllipseStylusShape(e, e);
    }

    private void SetInkMode(InkCanvasEditingMode mode) =>
        SetInkActive(mode != InkCanvasEditingMode.None, mode);

    private void StepFont(double delta)
    {
        _cfg.FontSize = DoodleStyle.ClampFont(FontSize + delta);
        DoodleBoardService.SaveConfig(_cfg);
        RebuildToolBar();
        RebuildNotes();
    }

    private void SetTextColor(string hex)
    {
        _cfg.TextColor = string.Equals(hex, _theme.NoteText, StringComparison.OrdinalIgnoreCase) ? null : hex;
        DoodleBoardService.SaveConfig(_cfg);
        RebuildToolBar();
        RebuildNotes();
    }

    private void StepPen(double delta)
    {
        _cfg.PenWidth = DoodleStyle.ClampPen(PenWidth + delta);
        _cfg.ThickPen = _cfg.PenWidth > 4;
        ApplyInkStyle();
        DoodleBoardService.SaveConfig(_cfg);
        RebuildToolBar();
    }

    private void SetPenColor(string hex)
    {
        _cfg.PenColor = string.Equals(hex, _theme.DefaultPen, StringComparison.OrdinalIgnoreCase) ? null : hex;
        ApplyInkStyle();
        DoodleBoardService.SaveConfig(_cfg);
        RebuildToolBar();
    }

    private void StepEraser(double delta)
    {
        _cfg.EraserSize = DoodleStyle.ClampEraser(EraserSize + delta);
        ApplyInkStyle();
        DoodleBoardService.SaveConfig(_cfg);
        RebuildToolBar();
    }

    /// <summary>
    /// 板头文字与图标色。深色板上直接用主题的 muted（#9BB1A4）在 36px 按钮里偏暗，
    /// 提亮一档才看得清是几笔画而不是几个点。
    /// </summary>
    /// <summary>
    /// 板头文字色。深色板直接用板面墨色（各主题的墨色本身就是"浅色粉笔"），
    /// 比原先写死的偏绿灰 <c>#C7D6CC</c> 更亮、也不和石墨板 / 夜空蓝的冷调打架。
    /// </summary>
    private string HeadForeground => _theme.Chalk ? _theme.Ink : _theme.Muted;

    // ======================================================================
    //  便签
    // ======================================================================

    /// <summary>
    /// 按板宽决定便签列数。板子现在能拖到工作区那么大，所以列数上限不再是 3 列 ——
    /// 拖动过程中每跨过一个阈值就重排一次（见构造函数里的 SizeChanged）。
    /// </summary>
    /// <summary>
    /// 按板宽与当前字号决定便签列数。
    /// <para>★ 只看板宽是不够的：30 号字在 760 宽的板上排 3 列，每张卡只剩 240 宽，
    /// 正文必然被压成「上午 8:00 各…」。字号越大、单卡可读最小宽度越大，列数要相应减少。</para>
    /// </summary>
    private int ColumnsFor(double boardWidth)
    {
        double avail = Math.Max(180,
            (double.IsNaN(boardWidth) ? DoodleBoardMetrics.DefaultWidth : boardWidth) - 20 - 16);

        int byWidth = avail >= 1480 ? 5 : avail >= 1120 ? 4 : avail >= 720 ? 3 : avail >= 480 ? 2 : 1;

        // 每号字约需 11.5 DIP 的卡宽（含左右内边距与卡间隙），13 号 ≈ 150 DIP
        double minCard = Math.Max(150, FontSize * 11.5);
        int byFont = Math.Max(1, (int)Math.Floor(avail / minCard));

        return Math.Max(1, Math.Min(byWidth, byFont));
    }

    /// <summary>便签卡之间的间隙与自动排布起点。</summary>
    private const double NoteGap = 10;
    private const double NotePad = 12;
    private const double NoteDefaultW = 210;
    private const double NoteDefaultH = 108;

    /// <summary>
    /// 重建当前页的全部便签。每张便签若被用户拖过（Placement 有值）就按记忆的位置尺寸摆，
    /// 否则按自动网格排。★ 自动网格只填没拖过的卡，且按"当前可用区"算列数 ——
    /// 这样新加模块不会覆盖用户精心摆好的位置。
    /// </summary>
    private void RebuildNotes()
    {
        _notesCanvas.Children.Clear();
        _firstAutoTitle = null;
        _firstAutoBody = null;
        _firstAutoCard = null;
        _firstAutoKey = null;

        var auto = DiagAutoOverride ?? DoodleAutoNotes.Load(
            Page.Modules, Page.CustomModules);
        int count = auto.Count + Page.Notes.Count;

        // 可用区：用板的实际宽，没 Show 时用配置宽（诊断窗口还没布局）
        // ★ r103：木色包边会吃掉左右各 11 DIP，算列数 / 卡宽时要先扣掉，否则最右一列会被裁。
        double boardW = (double.IsNaN(ActualWidth) || ActualWidth < 40 ? Width : ActualWidth)
                      - WoodFrameWidth * 2;
        double availW = Math.Max(240, boardW - NotePad * 2);
        int cols = Math.Max(1, Math.Min(ColumnsFor(boardW), Math.Max(1, count)));
        double cellW = Math.Max(150, (availW - NoteGap * (cols - 1)) / cols);

        int placed = 0;

        // ★ 宽卡（作业 / 缺交「全部科目合成一张」）先摆，每个独占一整行、按可用宽铺开。
        //   后面普通卡整体从宽卡底部往下排，不会压到长文卡上。
        double yBase = NotePad;
        foreach (var a in auto.Where(x => x.Wide))
        {
            var item = a;
            var style = AutoStyleFor(item);
            bool locked = style?.Locked == true;
            var card = BuildNoteCard(item.Title, item.Body, item.Source, false, null, null,
                style, locked ? null : () => EditAutoNote(item),
                locked ? null : item.Key, item.Module, item.Lines);

            double w = Math.Min(availW, Math.Max(cellW, 560));
            card.Width = w;
            // 同步测量拿真实高度：卡片是 Auto 高度，靠它把后续卡的起始 y 让出来
            card.Measure(new Size(w, double.PositiveInfinity));
            double measured = Math.Max(120, card.DesiredSize.Height);
            double usedH = Page.Placements.TryGetValue(item.Key, out var wp) && wp?.Height is double ph
                ? ph : measured;

            PlaceCard(card, item.Key, 0, 1, w, locked ? null : () => EditAutoNote(item), locked, yBase);
            yBase += usedH + NoteGap;
        }

        foreach (var a in auto.Where(x => !x.Wide))
        {
            var item = a;
            var style = AutoStyleFor(item);
            bool locked = style?.Locked == true;
            var card = BuildNoteCard(item.Title, item.Body, item.Source, false, null, null,
                style, locked ? null : () => EditAutoNote(item),
                locked ? null : item.Key, item.Module, item.Lines);
            PlaceCard(card, item.Key, placed++, cols, cellW,
                locked ? null : () => EditAutoNote(item), locked, yBase);
        }

        foreach (var n in Page.Notes.ToList())
        {
            var note = n;
            var style = AutoStyleFor(note.Id);
            bool locked = style?.Locked == true;
            var card = BuildNoteCard(note.Title, note.Body, "", true,
                locked ? null : () =>
                {
                    Page.Notes.Remove(note);
                    Page.Placements.Remove(note.Id);
                    RebuildNotes();
                    DoodleBoardService.SaveConfig(_cfg);
                },
                locked ? null : () => EditNote(note), style, null,
                locked ? null : note.Id, DoodleModules.Custom);
            PlaceCard(card, note.Id, placed++, cols, cellW, locked ? null : () => EditNote(note), locked, yBase);
        }

        // 空态提示：没有一张卡时给一句话，免得整块板看着像坏了
        if (count == 0)
        {
            _notesCanvas.Children.Add(new TextBlock
            {
                Text = "这块板面还没有内容——点板头「便签管理」添加内容模块或便签。",
                FontSize = Math.Max(11, Math.Round(FontSize * 0.95)),
                Foreground = ParseBrush(_theme.Muted),
                TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(220, availW - 20),
                IsHitTestVisible = false
            });
        }

        // ★ 便签区不再放「+ 新增便签」按钮：新增入口已统一收进板头「便签管理」，
        // 板面本身只负责展示，少一个按钮就少一处误触、也少一格被占掉的位置。

        // 画布要够高，否则拖到下方的便签会被裁掉
        int rows = (placed + cols - 1) / cols + 1;
        _notesCanvas.MinHeight = Math.Max(460, NotePad + rows * (NoteDefaultH + NoteGap) + NotePad + 24);
    }

    /// <summary>把一张卡按记忆位置或自动网格摆进画布。</summary>
    /// <param name="yBase">自动网格的起始 y：宽卡先占掉顶部，普通卡整体下移。</param>
    private void PlaceCard(FrameworkElement card, string key, int index, int cols, double cellW,
        Action? onActivate, bool locked = false, double yBase = NotePad)
    {
        Page.Placements.TryGetValue(key, out var p);

        double x = p?.X ?? (NotePad + (index % cols) * (cellW + NoteGap));
        double y = p?.Y ?? (yBase + (index / cols) * (NoteDefaultH + NoteGap));
        double w = p?.Width ?? cellW;

        // ★ 用户要求便签自动换行：没被拖过的卡片高度交给内容（Auto），
        // 正文多行时卡片自己长高；只有用户拖角改过尺寸才用记忆高度。
        double? h = p?.Height;

        // 夹到画布里：手改 json 写个负数 / 离谱大值也不能让卡消失
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        w = Math.Clamp(w, 120, 900);
        if (h.HasValue) h = Math.Clamp(h.Value, 78, 700);

        card.Width = w;
        card.Height = h ?? double.NaN;
        // ★ r104：课表卡被拖过高度 → 各行按卡片实际高度平均分布（拖多大就铺多开）
        if (h.HasValue) DistributeScheduleRows(card, h.Value);
        // ★ 给卡片自身打标记：拖动 / 诊断都靠它找卡，不用猜可视树层级
        card.Tag = "NoteCard";
        card.Uid = key;
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        Panel.SetZIndex(card, index + 1);
        _notesCanvas.Children.Add(card);

        AttachCardDrag(card, key, w, h ?? NoteDefaultH, onActivate, locked);
    }

    /// <summary>
    /// 课表卡：用户拖过高度时，各行按卡片实际高度平均分布。
    /// <para>★ 只对课表做 —— 作业 / 缺交一科一行讲究「紧凑好扫」，撑开反而更难读；
    /// 课表一行只有「第N节 科目」几个字，拖大后行挤在顶上会显得上面密下面空。</para>
    /// <para>★ 高度不够时直接放弃均分：宁可保持自然行高（内容溢出可拖大卡片），
    /// 也不把行压到比一行字还矮，那样文字会互相盖住。</para>
    /// </summary>
    private void DistributeScheduleRows(FrameworkElement card, double height)
    {
        if (FindByTag(card, "ScheduleLines") is not Grid rows || rows.RowDefinitions.Count < 2)
            return;

        double minRow = Math.Max(16, BodyFontSize * 1.35);
        if (height < rows.RowDefinitions.Count * minRow) return;

        foreach (var rd in rows.RowDefinitions)
        {
            rd.Height = new GridLength(1, GridUnitType.Star);
            rd.MinHeight = minRow;
        }
        // 均分后行高本身已经是空隙来源，取消每行额外的 margin；文字在行内垂直居中
        foreach (var child in rows.Children)
        {
            if (child is not TextBlock tb) continue;
            tb.Margin = new Thickness(0);
            tb.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    /// <summary>
    /// 给卡片装"拖正文移动 + 拖右下角缩放 + 双击编辑"。
    /// <para>★ 必须同时处理鼠标与触屏：一体机上手不会发 Mouse 事件，只挂 Mouse 会完全拖不动。</para>
    /// <para>★ 单击只拖动，双击才编辑：否则触屏想挪一张便签就会先弹出编辑窗，根本拖不成。</para>
    /// </summary>
    private void AttachCardDrag(FrameworkElement card, string key, double w, double h,
        Action? onActivate, bool locked = false)
    {
        // 单张便签被锁定时，整张卡不响应拖动 / 双击 / 缩放（与整板 Locked 同一效果）。
        bool CardLocked() => locked || _cfg.Locked;
        bool moving = false, resizing = false;
        Point start = new(), origin = new();
        double ow = w, oh = h;
        DateTime lastTap = DateTime.MinValue;
        Point lastTapPos = new Point(-999, -999);

        // ★ 这个处理器必须排在移动处理器前面：第二下 MouseLeftButtonDown 的 ClickCount=2，
        // 编辑窗弹出来后，移动分支自己会跳过，不会一边编辑一边偷偷搬位置。
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (CardLocked() || onActivate == null || e.ClickCount < 2) return;
            e.Handled = true;
            onActivate();
        };

        card.MouseLeftButtonDown += (_, e) =>
        {
            if (CardLocked() || e.ClickCount > 1) return;
            moving = true;
            start = e.GetPosition(_notesCanvas);
            origin = new Point(Canvas.GetLeft(card), Canvas.GetTop(card));
            card.CaptureMouse();
            e.Handled = true;
        };
        card.MouseMove += (_, e) =>
        {
            if (!moving) return;
            var cur = e.GetPosition(_notesCanvas);
            Canvas.SetLeft(card, Math.Max(0, origin.X + cur.X - start.X));
            Canvas.SetTop(card, Math.Max(0, origin.Y + cur.Y - start.Y));
            e.Handled = true;
        };
        void EndMove()
        {
            if (!moving) return;
            moving = false;
            card.ReleaseMouseCapture();
            card.ReleaseAllTouchCaptures();
            RememberPlacement(card, key);
        }
        card.MouseLeftButtonUp += (_, _) => EndMove();
        card.LostMouseCapture += (_, _) => EndMove();
        card.TouchDown += (_, e) =>
        {
            if (CardLocked()) return;
            var now = DateTime.Now;
            var pt = e.TouchDevice.GetTouchPoint(_notesCanvas).Position;
            // 手指双击没有 ClickCount，自己按 420ms / 14px 识别；命中就只编辑、不开始拖动
            if (onActivate != null && (now - lastTap).TotalMilliseconds < 420 &&
                Math.Abs(pt.X - lastTapPos.X) < 14 && Math.Abs(pt.Y - lastTapPos.Y) < 14)
            {
                lastTap = DateTime.MinValue;
                moving = false;
                card.ReleaseMouseCapture();
                card.ReleaseAllTouchCaptures();
                onActivate();
                e.Handled = true;
                return;
            }
            lastTap = now;
            lastTapPos = pt;
            moving = true;
            start = pt;
            origin = new Point(Canvas.GetLeft(card), Canvas.GetTop(card));
            card.CaptureTouch(e.TouchDevice);
            e.Handled = true;
        };
        card.TouchMove += (_, e) =>
        {
            if (!moving) return;
            var cur = e.TouchDevice.GetTouchPoint(_notesCanvas).Position;
            Canvas.SetLeft(card, Math.Max(0, origin.X + cur.X - start.X));
            Canvas.SetTop(card, Math.Max(0, origin.Y + cur.Y - start.Y));
            e.Handled = true;
        };
        card.TouchUp += (_, _) => EndMove();

        // 右下角缩放手柄由 BuildNoteCard 放进卡片里，用 Tag 标记
        var grip = FindByTag(card, "NoteGrip");
        if (grip == null) return;

        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (_cfg.Locked) return;
            resizing = true;
            start = e.GetPosition(_notesCanvas);
            ow = card.ActualWidth; oh = card.ActualHeight;
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!resizing) return;
            var cur = e.GetPosition(_notesCanvas);
            card.Width = Math.Clamp(ow + cur.X - start.X, 120, 900);
            card.Height = Math.Clamp(oh + cur.Y - start.Y, 78, 700);
            e.Handled = true;
        };
        void EndResize()
        {
            if (!resizing) return;
            resizing = false;
            grip.ReleaseMouseCapture();
            grip.ReleaseAllTouchCaptures();
            RememberPlacement(card, key);
        }
        grip.MouseLeftButtonUp += (_, _) => EndResize();
        grip.LostMouseCapture += (_, _) => EndResize();
        grip.TouchDown += (_, e) =>
        {
            if (_cfg.Locked) return;
            resizing = true;
            start = e.TouchDevice.GetTouchPoint(_notesCanvas).Position;
            ow = card.ActualWidth; oh = card.ActualHeight;
            grip.CaptureTouch(e.TouchDevice);
            e.Handled = true;
        };
        grip.TouchMove += (_, e) =>
        {
            if (!resizing) return;
            var cur = e.TouchDevice.GetTouchPoint(_notesCanvas).Position;
            card.Width = Math.Clamp(ow + cur.X - start.X, 120, 900);
            card.Height = Math.Clamp(oh + cur.Y - start.Y, 78, 700);
            e.Handled = true;
        };
        grip.TouchUp += (_, _) => EndResize();
    }

    private void RememberPlacement(FrameworkElement card, string key)
    {
        Page.Placements[key] = new DoodleNotePlacement
        {
            X = Math.Round(Canvas.GetLeft(card), 1),
            Y = Math.Round(Canvas.GetTop(card), 1),
            Width = Math.Round(card.ActualWidth, 1),
            Height = Math.Round(card.ActualHeight, 1)
        };
        DoodleBoardService.SaveConfig(_cfg);
    }

    private static FrameworkElement? FindByTag(DependencyObject root, string tag)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root))
        {
            if (c is FrameworkElement fe && string.Equals(fe.Tag as string, tag, StringComparison.Ordinal)) return fe;
            if (c is DependencyObject d)
            {
                var found = FindByTag(d, tag);
                if (found != null) return found;
            }
        }
        return null;
    }

    private FrameworkElement BuildNoteCard(string title, string body, string source, bool manual,
        Action? onDelete, Action? onEdit, DoodleAutoStyle? auto = null, Action? onDoubleClick = null,
        string? dragKey = null, string module = DoodleModules.Notice, List<AutoLine>? lines = null)
    {
        // 字号 / 字色都从配置来：用户调多大就多大，内边距与最小高度跟着一起长，
        // 否则字号调大后卡片还是老高度，文字会顶出卡片下沿。
        double fs = FontSize;
        double bfs = BodyFontSize;

        // ---- 单张自动便签的格式覆盖（「套用悬浮便签格式」改出来的）----
        // ★ 大屏编辑窗写的是直接字号（可到 88），优先用；老数据没有才退回档位相对倍率
        // （公共档位表只有 14~38，且基准档 = 1.0 倍，直接套会把板撑爆）。
        double fsTitle = auto?.TitleSize is double ts && ts > 0
            ? Math.Clamp(ts, DoodleStyle.MinFontSize, DoodleStyle.MaxFontSize)
            : Math.Round(fs * ScaleOf(auto?.Title), 1);
        double fsBody = auto?.ContentSize is double cs && cs > 0
            ? Math.Clamp(cs, DoodleStyle.MinFontSize, DoodleStyle.MaxFontSize)
            : Math.Round(bfs * ScaleOf(auto?.Content), 1);
        bool titleBold = auto?.Title?.Bold ?? true;
        bool bodyBold = auto?.Content?.Bold ?? false;
        double fsMax = Math.Max(fsTitle, fsBody);

        double padV = Math.Round(6 + (fsTitle - DoodleStyle.DefaultFontSize) * 0.4, 1);
        double padH = Math.Round(9 + (fsTitle - DoodleStyle.DefaultFontSize) * 0.3, 1);

        // ★ r99：这张便签自己的风格（HomeworkThemes）。空 = 跟随黑板材质；
        //   非空时底色 / 边框 / 默认字色都改用它 —— 同一块板上每张卡可以各有风格。
        HomeworkTheme? noteTheme = string.IsNullOrWhiteSpace(auto?.NoteTheme)
            ? null : HomeworkThemes.Find(auto!.NoteTheme);

        // 优先级：这张便签自己的字色 > 板级字色 > 便签风格色 > 黑板主题色。
        // 缺交的红色是警示语义，只有前两者都没指定时才用。
        string titleColor = !string.IsNullOrWhiteSpace(auto?.Title?.Color) ? auto!.Title!.Color!
                          : CustomTextColor
                          ?? (source == "缺交" ? "#C0392B" : (noteTheme?.TitleColor ?? _theme.NoteText));
        string bodyColor = !string.IsNullOrWhiteSpace(auto?.Content?.Color) ? auto!.Content!.Color!
                         : CustomTextColor
                         ?? (noteTheme?.Foreground ?? _theme.NoteSub);

        double gapV = Math.Round(4 + (fs - DoodleStyle.DefaultFontSize) * 0.35, 1);
        var outer = new Grid
        {
            Margin = new Thickness(0, gapV * 0.5, 8, gapV),
            MinHeight = Math.Max(58, Math.Round(fsMax * 4.3))
        };

        var card = new Border
        {
            Background = ParseBrush(noteTheme != null ? noteTheme.Background
                : (manual ? _theme.ManualBackground : _theme.NoteBackground)),
            BorderBrush = ParseBrush(noteTheme != null ? noteTheme.BorderColor
                : (manual ? _theme.ManualBorder : _theme.NoteBorder)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            // 手动便签的左右内边距交给内部 Grid 处理，好让左侧色条贴住卡片边缘
            Padding = manual ? new Thickness(0) : new Thickness(padH, padV, padH, padV + 2),
            Margin = new Thickness(0, 3, 0, 0),
            // MinHeight 会盖过 PlaceCard 写的固定 Height：大字号时卡片自己长高，不把文字顶出边框
            MinHeight = Math.Max(78, Math.Round(fsMax * 3.2 + 24)),
            Cursor = onEdit != null ? Cursors.Hand : Cursors.Arrow
        };

        // ★ r104：标题占 Auto 行、正文占 Star 行。
        //   原来是一个 StackPanel，高度只按内容长 —— 卡片被拖高后正文拿不到剩余空间，
        //   课表卡「按卡片高度平均分布各行」就无从谈起（StackPanel 从不把高度分给子元素）。
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ★ r103：卡片左上角原来还有一行浅色的模块名（作业 / 缺交 / 值日 / 通知）。
        //   用户反馈「都有标题了，这行是多余的」—— 删掉后标题与正文整体上移一行，
        //   卡片也更紧凑。source 仍然参与标题配色（缺交的警示红），只是不再画出来。

        var titleBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "（无标题）" : title,
            FontSize = fsTitle,
            FontWeight = titleBold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = ParseBrush(titleColor),
            // ★ 用户要求：便签内容按宽度自动换行、也支持回车换行，不再单行截断。
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, onDelete != null ? 18 : 0, 0)
        };
        Grid.SetRow(titleBlock, 0);
        stack.Children.Add(titleBlock);
        if (!manual && _firstAutoTitle == null)
        {
            _firstAutoTitle = titleBlock;
            _firstAutoCard = card;
            _firstAutoKey = dragKey ?? "";
        }

        // ---- 行首「科目」的独立格式（r104）----
        // 没单独设过就用「比内容略大 + 加粗」：科目和内容一眼要能分开，这是用户的原始诉求。
        double fsSubject = auto?.SubjectSize is double ss && ss > 0
            ? Math.Clamp(ss, DoodleStyle.MinFontSize, DoodleStyle.MaxFontSize)
            : Math.Round(fsBody * 1.3, 1);
        bool subjectBold = auto?.Subject?.Bold ?? true;
        string subjectColor = !string.IsNullOrWhiteSpace(auto?.Subject?.Color)
            ? auto!.Subject!.Color!
            : bodyColor;
        // 行与行之间留一点空隙：整段 TextBlock 时行是紧贴的，一节课一行看着像挤在一起
        double lineGap = Math.Max(3, Math.Round(fsBody * 0.5, 1));

        FrameworkElement? bodyElement = null;
        if (!manual && lines != null && lines.Count > 0)
        {
            // ★ 结构化正文：一行一条，行内再分科目 / 内容两段（Run），各自套自己的格式。
            //   Tag 用来给课表卡做「按高度均分」（PlaceCard 里按 Tag 找这个 Grid）。
            var rows = new Grid
            {
                Tag = string.Equals(module, DoodleModules.Schedule, StringComparison.OrdinalIgnoreCase)
                    ? "ScheduleLines" : "NoteLines"
            };
            for (int i = 0; i < lines.Count; i++)
            {
                rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var line = lines[i];
                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, i == 0 ? 0 : lineGap, 0, 0)
                };
                if (!string.IsNullOrWhiteSpace(line.Subject))
                {
                    tb.Inlines.Add(new Run(line.Subject)
                    {
                        FontSize = fsSubject,
                        FontWeight = subjectBold ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = ParseBrush(subjectColor)
                    });
                    tb.Inlines.Add(new Run(string.IsNullOrEmpty(line.Separator) ? "：" : line.Separator)
                    {
                        FontSize = fsSubject,
                        Foreground = ParseBrush(subjectColor)
                    });
                }
                tb.Inlines.Add(new Run(DoodleAutoNotes.Cut(line.Text, 120))
                {
                    FontSize = fsBody,
                    FontWeight = bodyBold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = ParseBrush(bodyColor)
                });
                Grid.SetRow(tb, i);
                rows.Children.Add(tb);
                if (!manual && _firstAutoBody == null) _firstAutoBody = tb;
            }
            bodyElement = rows;
        }
        else if (!string.IsNullOrWhiteSpace(body))
        {
            var bodyBlock = new TextBlock
            {
                Text = body,
                FontSize = fsBody,
                FontWeight = bodyBold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = ParseBrush(bodyColor),
                // ★ 自动换行 + 回车强制换行：去掉"最多两行 + 省略号"限制，
                //   行高交给 WPF 自己算（固定 LineHeight 会让强制分段的行挤在一起），
                //   内容多长就显示多长，卡片会随内容长高。
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            if (!manual && _firstAutoBody == null) _firstAutoBody = bodyBlock;
            bodyElement = bodyBlock;
        }

        if (bodyElement != null)
        {
            Grid.SetRow(bodyElement, 1);
            stack.Children.Add(bodyElement);
        }

        card.Child = WrapWithManualBar(stack, manual, padH, padV);

        // ★ 编辑触发统一交给 AttachCardDrag：单击要留给拖动摆放，双击才弹原生编辑窗。
        // 这里只负责"看起来能点"的反馈，不再自己挂 MouseLeftButtonDown。
        bool canEdit = onEdit != null || onDoubleClick != null;
        if (canEdit)
        {
            card.Cursor = Cursors.Hand;
            card.ToolTip = "双击打开编辑窗修改内容（内容会同步到对应公示 / 模块）";
        }

        outer.Children.Add(card);

        // 删除按钮压在卡片右上角（不占布局），窄卡片里尤其重要：
        // 让它占一行会把标题挤成两个字就截断。
        if (onDelete != null)
        {
            var del = new Button
            {
                Content = "×",
                Width = 18,
                Height = 18,
                FontSize = 12,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ParseBrush(_theme.Muted),
                Cursor = Cursors.Hand,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 2, 0),
                ToolTip = "删掉这张便签"
            };
            del.Click += (_, e) => { e.Handled = true; onDelete(); };
            outer.Children.Add(del);
        }

        // 胶带 / 图钉：露在卡片上边之外，所以必须挂在 Grid 上而不是卡片内部（会被圆角裁掉）
        // 胶带宽度跟着字号走：30 号字的卡片上贴一条 30×7 的小胶带，比例会像贴错了
        double pinScale = 1 + (fsMax - DoodleStyle.DefaultFontSize) * 0.035;
        var pin = new Border
        {
            Width = (_theme.PinRound ? 8 : 30) * pinScale,
            Height = (_theme.PinRound ? 8 : 7) * pinScale,
            CornerRadius = new CornerRadius(_theme.PinRound ? 4 * pinScale : 1),
            Background = ParseBrush(_theme.Pin),
            Opacity = _theme.Chalk ? 0.46 : 0.62,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        outer.Children.Add(pin);

        // 右下角缩放手柄：44 DIP 命中区（触屏下限），视觉上只画一个 18px 的 L 角
        if (dragKey != null)
        {
            var grip = new Border
            {
                Tag = "NoteGrip",
                Width = DoodleBoardMetrics.ToolButtonSize,
                Height = DoodleBoardMetrics.ToolButtonSize,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -10, -10),
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNWSE,
                Child = new Border
                {
                    Width = 20, Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 10, 10),
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = ParseBrush(_theme.Accent),
                    BorderThickness = new Thickness(1),
                    Background = ParseBrush("#CC25303D"),
                    Child = new TextBlock
                    {
                        Text = "◢", FontSize = 13,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 1, 1)
                    }
                }
            };
            outer.Children.Add(grip);
        }

        return outer;
    }

    /// <summary>
    /// 手动便签左侧加一条 3px 暖色条。
    /// <para>★ 只靠底色区分不够：亚麻米 / 教育蓝这些浅色板上，米白与淡黄几乎同色，
    /// 老师远看根本分不出"哪条是通知、哪条是自己随手写的"。</para>
    /// </summary>
    private FrameworkElement WrapWithManualBar(UIElement content, bool manual, double padH, double padV)
    {
        if (!manual) return (FrameworkElement)content;

        var grid = new Grid();
        // 列宽要 > 色条 + 间距：只给 3 的话，带右边距的色条会被挤成 0 宽（整条消失）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Background = ParseBrush("#E8B457"),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 7, 0),
            Opacity = _theme.Chalk ? 0.9 : 0.85
        };
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

        var el = (FrameworkElement)content;
        el.Margin = new Thickness(padH, padV, padH, padV + 2);
        Grid.SetColumn(el, 1);
        grid.Children.Add(el);

        return grid;
    }

    // ======================================================================
    //  自动便签的字体格式（套用悬浮便签的编辑窗，但只改格式）
    // ======================================================================

    /// <summary>
    /// 悬浮便签字号档位 → 相对倍率。
    /// <para>★ 基准档「特大」= 1.0：老师什么都不改时，便签看起来和原来一模一样，
    /// 不会因为打开过一次编辑窗就突然从 13 号变成 27 号。</para>
    /// </summary>
    private static double ScaleOf(TextStyle? style)
    {
        if (style == null) return 1.0;
        double baseSize = HwTextStyle.SizeOf(HwTextStyle.ExtraLargeLevel);
        if (baseSize <= 0) return 1.0;
        return Math.Clamp(HwTextStyle.SizeOf(style.SizeLevel) / baseSize, 0.5, 1.6);
    }

    private DoodleAutoStyle? AutoStyleFor(AutoNote note) => AutoStyleFor(note.Key);

    /// <summary>手动便签的格式也放在同一张表里，键就是便签 Id（与摆放记忆共用）。</summary>
    private DoodleAutoStyle? AutoStyleFor(string key)
    {
        if (Page.AutoStyles == null) return null;
        return Page.AutoStyles.TryGetValue(key, out var st) ? st : null;
    }

    private enum DoodleEditorTarget
    {
        NoticeEditor, HomeworkEditor, MissingEditor, CountdownEditor,
        DutyEditor, CustomModuleEditor, ScheduleHint, None
    }

    /// <summary>模块 → 双击后要开的原生编辑窗。诊断与真实点击共用这一份映射。</summary>
    private static DoodleEditorTarget EditorTargetFor(string module) => module switch
    {
        DoodleModules.Notice => DoodleEditorTarget.NoticeEditor,
        DoodleModules.Homework => DoodleEditorTarget.HomeworkEditor,
        DoodleModules.Missing => DoodleEditorTarget.MissingEditor,
        DoodleModules.Countdown => DoodleEditorTarget.CountdownEditor,
        DoodleModules.Duty => DoodleEditorTarget.DutyEditor,
        DoodleModules.Custom => DoodleEditorTarget.CustomModuleEditor,
        DoodleModules.Schedule => DoodleEditorTarget.ScheduleHint,
        _ => DoodleEditorTarget.None
    };

    /// <summary>
    /// 读「整份公示」的总标题与全部科目记录，供大屏编辑窗一次性编辑。
    /// <para>★ 一条记录 = 一条 <see cref="HomeworkItem"/> / <see cref="MissingHomeworkItem"/>（科目 + 内容），
    /// <b>不按正文换行拆</b> —— 正文里的回车只是排版，不是记录分隔符（r99 按行拆，一条科目被拆成多行文本，
    /// 保存时又把多行拼回同一条，看起来就"不是一行一条记录"）。</para>
    /// </summary>
    internal static (string Title, List<DoodleBigEditEntry> Entries) LoadWholeBoardData(string module)
    {
        var list = new List<DoodleBigEditEntry>();
        string title = "";
        try
        {
            if (module == DoodleModules.Homework)
            {
                var board = Store.LoadHomework();
                title = board?.Title ?? "";
                foreach (var h in board?.LiveItems ?? Enumerable.Empty<HomeworkItem>())
                    list.Add(new DoodleBigEditEntry { Id = h.Id, Title = h.Subject, Body = h.Content });
            }
            else if (module == DoodleModules.Missing)
            {
                var board = Store.LoadMissingBoard();
                title = board?.Title ?? "";
                foreach (var m in board?.LiveItems ?? Enumerable.Empty<MissingHomeworkItem>())
                    list.Add(new DoodleBigEditEntry { Id = m.Id, Title = m.Subject, Body = m.Students });
            }
            else if (module == DoodleModules.Duty)
            {
                // ★ 值日不是「一条记录」而是一张按星期排的表：周一到周五各一条，
                //   每条三个字段（星期 / 值日生姓名 / 值日内容）。整份读出来才能一窗改全，
                //   也才不会像上一版那样把「今日值日」这个卡片标题写进当天的岗位名里。
                var board = Store.LoadDuty();
                title = board?.Title ?? "";
                foreach (var d in board?.SortedItems() ?? new List<DutyItem>())
                    list.Add(new DoodleBigEditEntry
                    {
                        Id = d.Id ?? "", Title = d.Day ?? "",
                        Body = d.Names ?? "", Extra = d.Duty ?? ""
                    });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("涂鸦板读整份公示失败: " + ex.Message);
        }
        return (title, list);
    }

    /// <summary>
    /// 双击自动便签 → 按来源打开<b>对应的原生编辑窗</b>（能改内容），不是只改格式的小窗。
    /// <para>★ 三类公示窗（通知 / 作业 / 缺交）自己管落盘；作业 / 缺交的构造要传板对象，
    /// 保存走 BuildBoard()+Store。改完统一刷新板，这样别的地方编辑的内容也能立刻看到。</para>
    /// <para>★ 作业 / 缺交双击打开的是<b>整份公示</b>：编辑窗列出全部科目记录（每条「科目 + 内容」两框，可增删），
    /// 保存时逐条落回真实数据；黑板上仍是一个科目一张便签。</para>
    /// </summary>
    private void EditAutoNote(AutoNote note)
    {
        if (_cfg.Locked) return;

        // ★ r104：课表内容仍然只能在「课程表」里改，但格式与风格要能在这里调 ——
        //   用户要求「第N节」也算科目位，可以单独设字号 / 粗细 / 颜色。
        //   原来直接弹框拦掉，等于这张卡的格式永远改不了。
        bool schedule = string.Equals(note.Module, DoodleModules.Schedule, StringComparison.OrdinalIgnoreCase);

        Page.AutoStyles ??= new Dictionary<string, DoodleAutoStyle>();
        Page.AutoStyles.TryGetValue(note.Key, out var style);

        // ★ 作业 / 缺交 / 值日都是「整份」：一条记录 = 一条真实数据（带 Id），
        //   编辑窗列出全部记录，保存时逐条落回。值日漏了这一步，才会出现「确定后自己长出值日内容」。
        bool whole = note.Module is DoodleModules.Homework or DoodleModules.Missing
            or DoodleModules.Duty;
        var (boardTitle, entries) = whole
            ? LoadWholeBoardData(note.Module)
            : ("", new List<DoodleBigEditEntry>());

        var dlg = new DoodleBigScreenEditWindow(new DoodleBigEditTarget
        {
            Key = note.Key,
            Module = note.Module,
            ItemId = note.Id,
            Title = note.Title,
            Body = note.Body,
            // 倒计时的正文是按日期算出来的天数，写回去没有意义，只让改事项名；
            // 课表同理（内容是整周课表的投影），但两者的格式与风格都能调
            BodyEditable = note.Module != DoodleModules.Countdown && !schedule,
            BodyHint = note.Module == DoodleModules.Countdown
                ? "倒计时天数由日期自动计算；这里改的是事项名和字体格式。"
                : schedule
                    ? "课程来自整周课表，请到主程序「课程表」修改；这里只调格式与风格。"
                    : "",
            Style = style,
            ThemeKey = _cfg.ThemeKey,
            // ★ 编辑窗的「主题风格」是这张便签自己的风格，不是整块黑板的材质
            NoteTheme = style?.NoteTheme ?? "",
            // 作业 / 缺交 / 值日按「科目 + 内容」双输入框展示，按钮是「添加科目 / 添加记录」
            MultiEntry = note.Module is DoodleModules.Homework or DoodleModules.Missing
                or DoodleModules.Duty,
            // ★ 作业 / 缺交：整份编辑，一条记录 = 一条真实数据（带 Id）
            WholeBoard = whole,
            BoardTitle = boardTitle,
            Entries = entries
        })
        { Owner = this };

        if (dlg.ShowDialog() != true) return;
        SaveBigEdit(note.Module, note.Id, note.Key, dlg.Draft);
    }

    /// <summary>
    /// 把整份编辑结果落回作业公示：改的改、新加的新加、删掉的打墓碑。
    /// <para>★ 删条目只打 <c>Deleted=true</c> 墓碑，不物理移除 —— 物理删除会让对端一条晚到的
    /// 旧字段变更单把条目「复活」。空科目 + 空内容的草稿行直接忽略，不产生幽灵条目。</para>
    /// </summary>
    internal static void ApplyHomeworkEntries(HomeworkBoard board, List<DoodleBigEditEntry>? entries)
    {
        board.Items ??= new List<HomeworkItem>();
        var byId = board.Items
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in entries ?? new List<DoodleBigEditEntry>())
        {
            string subject = (e.Title ?? "").Trim();
            string content = (e.Body ?? "").Trim();
            if (subject.Length == 0 && content.Length == 0) continue;

            if (!string.IsNullOrEmpty(e.Id) && byId.TryGetValue(e.Id, out var old))
            {
                old.Subject = subject;
                old.Content = content;
                old.Deleted = false;   // 之前打过墓碑的同 Id 条目被重新编辑，等于复活
                kept.Add(e.Id);
            }
            else
            {
                board.Items.Add(new HomeworkItem { Subject = subject, Content = content, Origin = "local" });
            }
        }

        // 本次草稿里没有的旧条目 = 用户在编辑窗里删掉了
        foreach (var it in board.Items)
        {
            if (it.Deleted || string.IsNullOrEmpty(it.Id)) continue;
            if (!kept.Contains(it.Id)) it.Deleted = true;
        }
    }

    /// <summary>缺交名单版的整份落回：一条记录 = 一条 <see cref="MissingHomeworkItem"/>（科目 + 名单）。</summary>
    internal static void ApplyMissingEntries(MissingBoard board, List<DoodleBigEditEntry>? entries)
    {
        board.Items ??= new List<MissingHomeworkItem>();
        var byId = board.Items
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in entries ?? new List<DoodleBigEditEntry>())
        {
            string subject = (e.Title ?? "").Trim();
            string students = (e.Body ?? "").Trim();
            if (subject.Length == 0 && students.Length == 0) continue;

            if (!string.IsNullOrEmpty(e.Id) && byId.TryGetValue(e.Id, out var old))
            {
                old.Subject = subject;
                old.Students = students;
                old.Deleted = false;
                kept.Add(e.Id);
            }
            else
            {
                board.Items.Add(new MissingHomeworkItem { Subject = subject, Students = students, Origin = "local" });
            }
        }

        foreach (var it in board.Items)
        {
            if (it.Deleted || string.IsNullOrEmpty(it.Id)) continue;
            if (!kept.Contains(it.Id)) it.Deleted = true;
        }
    }

    /// <summary>
    /// 值日整份落回：一条记录 = 一条 <see cref="DutyItem"/>（星期 + 姓名 + 值日内容）。
    /// <para>★ 上一版把值日当单条处理，保存时写 <c>item.Duty = 卡片标题("今日值日")</c>、
    /// <c>item.Names = 拼接正文</c>，结果每编辑一次就往当天那条里塞一份「今日值日」，
    /// 老师看到的就是「确定退出后自动添加值日内容」。现在逐条按 Id 落回，标题只写 board.Title。</para>
    /// <para>★ 删掉的条目照旧只打墓碑，不物理删除（同步链要求）。</para>
    /// </summary>
    internal static void ApplyDutyEntries(DutyBoard board, List<DoodleBigEditEntry>? entries)
    {
        board.Items ??= new List<DutyItem>();
        var byId = board.Items
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in entries ?? new List<DoodleBigEditEntry>())
        {
            string day = (e.Title ?? "").Trim();
            string names = (e.Body ?? "").Trim();
            string duty = (e.Extra ?? "").Trim();
            if (day.Length == 0 && names.Length == 0 && duty.Length == 0) continue;

            if (!string.IsNullOrEmpty(e.Id) && byId.TryGetValue(e.Id, out var old))
            {
                old.Day = day;
                old.Names = names;
                old.Duty = duty;
                old.Deleted = false;
                kept.Add(e.Id);
            }
            else
            {
                board.Items.Add(new DutyItem { Day = day, Names = names, Duty = duty, Origin = "local" });
            }
        }

        foreach (var it in board.Items)
        {
            if (it.Deleted || string.IsNullOrEmpty(it.Id)) continue;
            if (!kept.Contains(it.Id)) it.Deleted = true;
        }
    }

    /// <summary>
    /// 把大屏编辑窗的结果写回原数据源。★ 内容仍只有一份：主程序 / 悬浮窗改的是同一条公示，
    /// 所以保存后要 Raise 对应 Changed，让别处的窗口也跟着刷新。
    /// </summary>
    private void SaveBigEdit(string module, string itemId, string key, DoodleBigEditDraft d)
    {
        bool contentFound = true;
        switch (module)
        {
            case DoodleModules.Notice:
            {
                var list = Store.LoadNotifications() ?? new List<Notification>();
                var item = list.FirstOrDefault(x => x.Id == itemId);
                if (item == null) contentFound = false;
                else
                {
                    item.Title = d.Title;
                    item.Content = d.Body;
                    Store.SaveNotifications(list);
                    AppEvents.RaiseNotificationChanged();
                }
                break;
            }
            case DoodleModules.Homework:
            {
                // ★ 整份编辑：条目逐条落回（改 / 增 / 墓碑），不再把「科目：内容」拼成一段写进某一条
                var board = Store.LoadHomework();
                if (board == null) contentFound = false;
                else
                {
                    if (!string.IsNullOrWhiteSpace(d.Title)) board.Title = d.Title;
                    ApplyHomeworkEntries(board, d.Entries);
                    Store.SaveHomework(board);
                    AppEvents.RaiseHomeworkChanged();
                }
                break;
            }
            case DoodleModules.Missing:
            {
                var board = Store.LoadMissingBoard();
                if (board == null) contentFound = false;
                else
                {
                    if (!string.IsNullOrWhiteSpace(d.Title)) board.Title = d.Title;
                    ApplyMissingEntries(board, d.Entries);
                    Store.SaveMissingBoard(board);
                    AppEvents.RaiseMissingChanged();
                }
                break;
            }
            case DoodleModules.Countdown:
            {
                var board = Store.LoadCountdown();
                var item = board?.Items.FirstOrDefault(x => x.Name == itemId);
                if (board == null || item == null) contentFound = false;
                else
                {
                    item.Name = d.Title;
                    Store.SaveCountdown(board);
                    AppEvents.RaiseCountdownChanged();
                }
                break;
            }
            case DoodleModules.Duty:
            {
                // ★ 整份值日表：标题写 board.Title，条目逐条按 Id 落回（星期 / 姓名 / 值日内容）。
                //   绝不再把卡片标题「今日值日」写进当天的岗位名里。
                var board = Store.LoadDuty();
                if (board == null) contentFound = false;
                else
                {
                    if (!string.IsNullOrWhiteSpace(d.Title)) board.Title = d.Title;
                    ApplyDutyEntries(board, d.Entries);
                    Store.SaveDuty(board);
                    AppEvents.RaiseDutyChanged();
                }
                break;
            }
            case DoodleModules.Custom:
            {
                var m = Page.CustomModules.FirstOrDefault(x => x.Id == itemId);
                if (m == null) contentFound = false;
                else { m.Title = d.Title; m.Body = d.Body; }
                break;
            }
            case "manual":
                // 手动便签的内容由调用方直接写 Page.Notes；这里只统一处理样式和主题
                break;
        }

        if (!contentFound)
            MessageBox.Show(this, "原始公示里没找到这一条，内容没改动；字体和主题设置仍然保存。",
                "板面内容", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (d.SaveStyle)
        {
            var st = Page.AutoStyles.TryGetValue(key, out var old) ? old : new DoodleAutoStyle();
            st.TitleSize = d.TitleSize;
            st.ContentSize = d.ContentSize;
            // ★ r104：科目单独一套（作业 / 缺交的科目名、课表的第N节）。
            //   颜色为空也要造 TextStyle 承载 Bold —— 科目默认加粗，这是它和内容的第一眼区别。
            st.SubjectSize = d.SubjectSize > 0 ? d.SubjectSize : null;
            st.Subject = string.IsNullOrWhiteSpace(d.SubjectColor) && !d.SubjectBold
                ? null : new TextStyle { Color = d.SubjectColor, Bold = d.SubjectBold };
            // 颜色为空仍要造 TextStyle 承载 Bold，否则加粗存不下来。
            st.Title = string.IsNullOrWhiteSpace(d.TitleColor) && !d.TitleBold
                ? null : new TextStyle { Color = d.TitleColor, Bold = d.TitleBold };
            st.Content = string.IsNullOrWhiteSpace(d.BodyColor) && !d.BodyBold
                ? null : new TextStyle { Color = d.BodyColor, Bold = d.BodyBold };
            // 单张便签锁定状态（黑板上不能拖 / 缩放 / 双击编辑）。
            st.Locked = d.Locked;
            // ★ r99：编辑窗里选的风格只属于这张便签，绝不改整块黑板的 ThemeKey。
            if (d.SaveNoteTheme) st.NoteTheme = d.NoteTheme ?? "";
            Page.AutoStyles[key] = st;
        }

        // 早期版本在这里 SetTheme(d.ThemeKey)，把「便签风格」误当成「黑板材质」套到整块板；
        // r99 已按用户要求彻底移除，SaveTheme 恒 false。

        DoodleBoardService.SaveConfig(_cfg);
        RebuildNotes();
    }

    /// <summary>别处（主窗 / 悬浮窗）改了内容时同步刷新板面。</summary>
    private void SubscribeContentChanges()
    {
        void Refresh() { Dispatcher.BeginInvoke(new Action(RebuildNotes)); }
        AppEvents.NotificationChanged += Refresh;
        AppEvents.HomeworkChanged += Refresh;
        AppEvents.MissingChanged += Refresh;
        AppEvents.CountdownChanged += Refresh;
        AppEvents.DutyChanged += Refresh;
        AppEvents.ScheduleChanged += Refresh;
        Closed += (_, _) =>
        {
            AppEvents.NotificationChanged -= Refresh;
            AppEvents.HomeworkChanged -= Refresh;
            AppEvents.MissingChanged -= Refresh;
            AppEvents.CountdownChanged -= Refresh;
            AppEvents.DutyChanged -= Refresh;
            AppEvents.ScheduleChanged -= Refresh;
        };
    }

    private void ClearAutoStyles()
    {
        if (Page.AutoStyles == null || Page.AutoStyles.Count == 0) return;
        Page.AutoStyles.Clear();
        RebuildNotes();
        DoodleBoardService.SaveConfig(_cfg);
    }

    /// <summary>诊断用：第一张自动便签标题的实际字号（证明格式画到了卡片上）。</summary>
    internal double DiagAutoTitleFontSize => _firstAutoTitle?.FontSize ?? 0;

    /// <summary>诊断用：第一张自动便签标题是否加粗。</summary>
    internal bool DiagAutoTitleBold => _firstAutoTitle?.FontWeight == FontWeights.Bold;

    /// <summary>诊断用：第一张自动便签正文是否自动换行、且没有两行截断。</summary>
    internal bool DiagAutoBodyWraps =>
        _firstAutoBody != null &&
        _firstAutoBody.TextWrapping == TextWrapping.Wrap &&
        _firstAutoBody.TextTrimming == TextTrimming.None &&
        double.IsPositiveInfinity(_firstAutoBody.MaxHeight);

    /// <summary>r104 诊断：第一张卡片第一行里的所有 Run（科目 / 分隔符 / 内容）。</summary>
    private List<Run> FirstLineRuns()
    {
        var list = new List<Run>();
        if (_firstAutoBody?.Inlines == null) return list;
        foreach (var inline in _firstAutoBody.Inlines)
            if (inline is Run r) list.Add(r);
        return list;
    }

    /// <summary>诊断：行首「科目」的实际字号（证明科目有自己的格式，不跟内容同号）。</summary>
    internal double DiagFirstNoteSubjectFontSize
    {
        get
        {
            var runs = FirstLineRuns();
            return runs.Count > 0 ? runs[0].FontSize : 0;
        }
    }

    internal bool DiagFirstNoteSubjectBold
    {
        get
        {
            var runs = FirstLineRuns();
            return runs.Count > 0 && runs[0].FontWeight == FontWeights.Bold;
        }
    }

    internal string DiagFirstNoteSubjectColor
    {
        get
        {
            var runs = FirstLineRuns();
            return (runs.Count > 0 ? runs[0].Foreground as SolidColorBrush : null)?.Color.ToString() ?? "";
        }
    }

    /// <summary>诊断：行内内容的实际字号（与科目对比，验证两者确实分开）。</summary>
    internal double DiagFirstNoteBodyFontSize
    {
        get
        {
            var runs = FirstLineRuns();
            return runs.Count > 0 ? runs[^1].FontSize : 0;
        }
    }

    /// <summary>诊断：给第一张卡设一套科目格式（字号 / 颜色 / 加粗），用来验证真的画到卡片上。</summary>
    internal void DiagSetSubjectStyle(double size, string color, bool bold)
    {
        if (string.IsNullOrEmpty(_firstAutoKey)) return;
        var st = Page.AutoStyles.TryGetValue(_firstAutoKey, out var old) ? old : new DoodleAutoStyle();
        st.SubjectSize = size;
        st.Subject = new TextStyle { Color = color, Bold = bold };
        Page.AutoStyles[_firstAutoKey] = st;
        RebuildNotes();
    }

    /// <summary>诊断：课表卡的行是否已切到「按卡片高度均分」（Star 行）。</summary>
    internal bool DiagScheduleRowsAreStar
    {
        get
        {
            foreach (var child in _notesCanvas.Children)
            {
                if (child is not FrameworkElement fe) continue;
                if (FindByTag(fe, "ScheduleLines") is not Grid g || g.RowDefinitions.Count < 2) continue;
                return g.RowDefinitions.All(rd => rd.Height.IsStar && rd.MinHeight > 15);
            }
            return false;
        }
    }

    /// <summary>诊断用：第一张自动便签标题也自动换行（不再单行截断）。</summary>
    internal bool DiagAutoTitleWraps =>
        _firstAutoTitle != null &&
        _firstAutoTitle.TextWrapping == TextWrapping.Wrap;

    /// <summary>诊断用：第一张自动便签标题的实际颜色。</summary>
    internal string DiagAutoTitleColor =>
        (_firstAutoTitle?.Foreground as SolidColorBrush)?.Color.ToString() ?? "";

    /// <summary>诊断用：字号档位 → 倍率（<c>--doodle-check</c> 验基准档必须为 1.0）。</summary>
    internal static double DiagScaleForLevel(int level) =>
        ScaleOf(new TextStyle { SizeLevel = level });

    /// <summary>诊断用：当前自动便签里带格式覆盖的张数。</summary>
    internal int DiagAutoStyledCount =>
        Page.AutoStyles == null ? 0 : Page.AutoStyles.Count;

    /// <summary>诊断用：改一张自动便签的格式（不弹窗，供 <c>--doodle-check</c> 验证落盘与渲染）。</summary>
    internal void DiagApplyAutoStyle(string key, bool bold, string color, int sizeLevel)
    {
        Page.AutoStyles ??= new Dictionary<string, DoodleAutoStyle>();
        Page.AutoStyles[key] = new DoodleAutoStyle
        {
            Title = new TextStyle { Bold = bold, Color = color, SizeLevel = sizeLevel },
            Content = new TextStyle { Bold = bold, Color = color, SizeLevel = sizeLevel }
        };
        RebuildNotes();
        DoodleBoardService.SaveConfig(_cfg);
    }

    // ---- r94 新功能诊断：多板面 / 自选模块 / 拖角缩放 / 原生编辑窗分派 ----

    internal int DiagPageCount => _cfg.Pages?.Count ?? 0;
    internal int DiagActivePage => _cfg.ActivePage;
    internal string DiagPageLabel => _pageText?.Text ?? "";
    internal IReadOnlyList<string> DiagModules => Page.Modules.ToList();

    /// <summary>板头文本拼串：板名|班级|时间|页码，缺一项诊断就能看见。</summary>
    internal string DiagHeaderInfo =>
        $"{_titleText?.Text}|{_classText?.Text}|{_clockText?.Text}|{_pageText?.Text}";

    /// <summary>右上角菜单的一级项，用来验"板面 / 内容模块 / 黑板风格 / 新建板"都在。</summary>
    internal IReadOnlyList<string> DiagTopMenuHeaders =>
        BuildBoardMenu().Items.OfType<MenuItem>()
            .Select(m => m.Header?.ToString() ?? "").ToList();

    internal string DiagEditorFor(string module) => EditorTargetFor(module).ToString();

    // ---- r96：同页双层 / 大字号 / 按页笔迹 / 板头样式 ----
    internal bool DiagInkActive => _inkActive;
    internal bool DiagInkHitTest => _ink.IsHitTestVisible;
    internal bool DiagNotesHitTest => _notesHost.IsHitTestVisible;
    internal void DiagSetInkActive(bool on) => SetInkActive(on);
    internal string DiagInkPath => InkFile;
    internal int DiagInkStrokeCount => _ink.Strokes.Count;
    internal double DiagHeaderTitleFontSize => _titleText?.FontSize ?? 0;
    internal double DiagFirstNoteTitleFontSize => _firstAutoTitle?.FontSize ?? 0;
    internal int DiagThemeCount => DoodleBoardThemes.All.Count;
    /// <summary>整块黑板当前的材质 key（用来证明便签风格没有偷偷改黑板主题）。</summary>
    internal string DiagBoardThemeKey => _cfg.ThemeKey;

    internal void DiagApplyBigFont(double size)
    {
        Page.AutoStyles ??= new Dictionary<string, DoodleAutoStyle>();
        var key = "notice:abc123";
        var st = Page.AutoStyles.TryGetValue(key, out var old) ? old : new DoodleAutoStyle();
        st.TitleSize = size;
        st.ContentSize = Math.Max(14, size * 0.72);
        Page.AutoStyles[key] = st;
        RebuildNotes();
    }

    internal void DiagApplyHeaderStyle(DoodleHeaderStyle style)
    {
        _cfg.HeaderStyle = style;
        ApplyHeaderStyle();
    }

    internal void DiagSwitchPage(int index) => SwitchPage(index);

    internal void DiagToggleModule(string key, bool on) => ToggleModule(key, on);

    // ---- r97：板头直达入口 / 清空便签 / 手写工具带 ----
    internal IReadOnlyList<string> DiagHeadTextButtons => WalkButtons(_head)
        .Where(b => b.Content is string)
        .Select(b => b.Content!.ToString() ?? "")
        .ToList();

    internal IReadOnlyList<string> DiagNoteManageHeaders
    {
        get
        {
            var item = BuildNoteManageMenuItem();
            return item.Items.OfType<MenuItem>()
                .Select(m => m.Header?.ToString() ?? "")
                .ToList();
        }
    }

    internal int DiagRenderedNoteCount
    {
        get
        {
            int n = 0;
            foreach (var child in _notesCanvas.Children)
                if (string.Equals((child as FrameworkElement)?.Tag as string, "NoteCard", StringComparison.Ordinal))
                    n++;
            return n;
        }
    }

    internal void DiagClearPage() => ClearPageNotes();
    internal void DiagAddPage() => AddPage();
    internal bool DiagLocked => _cfg.Locked;
    internal void DiagSetLocked(bool on) => SetLocked(on);
    /// <summary>r108：锁定按钮已挪进工具带（在「手写 / 便签」切换按钮左边）。</summary>
    internal string DiagLockText => _lockBtn?.Content?.ToString() ?? "";
    /// <summary>r108：板名钉在工具带最右边（= 整块板最右下角）。</summary>
    internal string DiagCornerTitle => _titleText?.Text ?? "";

    /// <summary>r108：锁定按钮确实排在「手写 / 便签」切换按钮的左边，并且真的挂在工具带上。</summary>
    internal bool DiagLockBeforeModeButton
    {
        get
        {
            if (_lockBtn == null || _modeBtn == null) return false;
            int li = _toolItems.Children.IndexOf(_lockBtn);
            int mi = _toolItems.Children.IndexOf(_modeBtn);
            return li >= 0 && mi > li;
        }
    }

    /// <summary>r108：板名在工具带里右停靠（不再是压在卡片上的浮层）。</summary>
    internal bool DiagTitleInToolBar
    {
        get
        {
            if (_titleText == null) return false;
            if (_titleText.Parent is not DockPanel row) return false;
            return DockPanel.GetDock(_titleText) == Dock.Right
                && ReferenceEquals(row.Parent, _toolBar);
        }
    }
    /// <summary>r99：工具带最前面的切换按钮改回文字（不再和画笔图标撞成「两支笔」）。</summary>
    internal bool DiagModeSwitchIsText
    {
        get
        {
            var s = _modeBtn?.Content as string;
            return s == "手写" || s == "便签";
        }
    }

    /// <summary>板头信息与黑板上边沿的留白。</summary>
    internal double DiagHeadTopGap => HeadTopGap;

    /// <summary>r103：木色包边的实际厚度（Padding = 木条宽度 − 1px 描边）。</summary>
    internal double DiagWoodFrameWidth => _frame.Padding.Left + _frame.BorderThickness.Left;

    /// <summary>r103：木框必须是渐变木色（纯色 = 没吃到统一包边）。r108 起木条细了一半（5~11）。</summary>
    internal bool DiagWoodFrameIsWood =>
        _frame.Background is LinearGradientBrush g && g.GradientStops.Count >= 4
        && DiagWoodFrameWidth >= 4 && DiagWoodFrameWidth <= 14;

    /// <summary>r103：便签卡里不再有那行浅色模块名（标题就是唯一的小标题）。</summary>
    internal bool DiagNoteHasNoSourceLabel
    {
        get
        {
            var card = _firstAutoCard;
            if (card?.Child is not Panel content) return false;
            var texts = new List<string>();
            CollectTexts(content, texts);
            // 只禁止「module 名单独成行」的那种文本；标题里带「作业」两个字（各科作业公示）不算
            return !texts.Any(t =>
            {
                string s = (t ?? "").Trim();
                return s == "作业" || s == "缺交" || s == "值日" || s == "通知"
                    || s == "课表" || s == "倒计时" || s == "自定义";
            });
        }
    }

    private static void CollectTexts(DependencyObject node, List<string> into)
    {
        // 用逻辑树：诊断窗口不一定 Show 过，未渲染时可视树是空的，逻辑树仍然完整
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is TextBlock tb) into.Add(tb.Text ?? "");
            else if (child is DependencyObject d) CollectTexts(d, into);
        }
    }

    /// <summary>板头只有一行：班级 / 时间和菜单按钮在同一行（不再上下两行）。</summary>
    internal bool DiagHeadSingleRow =>
        _headGrid != null && _headGrid.RowDefinitions.Count <= 1;

    /// <summary>班级文字与菜单按钮的纵向中心差（DIP）：同一行且都居中时应接近 0。</summary>
    internal double DiagHeadAlignDelta
    {
        get
        {
            if (_headGrid == null || _classText == null || _settingsBtn == null) return 999;
            try
            {
                _headGrid.UpdateLayout();
                var a = _classText.TranslatePoint(
                    new Point(0, _classText.ActualHeight / 2), _headGrid);
                var b = _settingsBtn.TranslatePoint(
                    new Point(0, _settingsBtn.ActualHeight / 2), _headGrid);
                return Math.Abs(a.Y - b.Y);
            }
            catch { return 999; }
        }
    }

    /// <summary>第一张自动便签卡片的实际底色（验证便签风格只作用于这一张）。</summary>
    internal string DiagFirstNoteBackground =>
        (_firstAutoCard?.Background as SolidColorBrush)?.Color.ToString() ?? "";

    /// <summary>诊断：给第一张自动便签单独套一套便签风格，然后重建卡片。</summary>
    internal void DiagSetFirstAutoNoteTheme(string themeKey)
    {
        var auto = DiagAutoOverride ?? DoodleAutoNotes.Load(Page.Modules, Page.CustomModules);
        if (auto.Count == 0) return;
        // ★ 宽卡（作业 / 缺交合并卡）排在最前，先构建的就是它 —— 用实际记录下来的键，
        //   不能拿 auto[0] 猜（否则改的是通知卡的样式，断言读的是作业卡的底色）。
        string k = string.IsNullOrEmpty(_firstAutoKey) ? auto[0].Key : _firstAutoKey!;
        Page.AutoStyles ??= new Dictionary<string, DoodleAutoStyle>();
        var st = Page.AutoStyles.TryGetValue(k, out var old) ? old : new DoodleAutoStyle();
        st.NoteTheme = themeKey;
        Page.AutoStyles[k] = st;
        RebuildNotes();
    }

    internal void DiagSetInkTool(bool eraser) =>
        SetInkActive(true, eraser ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink);

    internal bool DiagHasPenSlider => _penSlider != null;
    internal bool DiagHasEraserSlider => _eraserSlider != null;
    internal bool DiagHasColorCapsule => _colorCapsuleBtn != null;

    internal int DiagGripCount
    {
        get
        {
            int n = 0;
            CountByTag(_notesCanvas, "NoteGrip", ref n);
            return n;
        }
    }

    internal string DiagFirstNoteKey =>
        (FindByTag(_notesCanvas, "NoteCard") as FrameworkElement)?.Uid ?? "";

    internal Size DiagFirstNoteSize
    {
        get
        {
            var card = FindByTag(_notesCanvas, "NoteCard") as FrameworkElement;
            return card == null ? new Size(0, 0)
                : new Size(card.ActualWidth, card.ActualHeight);
        }
    }

    /// <summary>第一张便签缩放手柄中心的物理屏幕坐标，供真实鼠标探针直接发消息。</summary>
    internal Point DiagFirstNoteGripCenter
    {
        get
        {
            var grip = FindByTag(_notesCanvas, "NoteGrip") as FrameworkElement;
            if (grip == null || grip.ActualWidth < 1) return new Point(-1, -1);
            return grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
        }
    }

    internal double DiagFirstNoteGripHit
    {
        get
        {
            var grip = FindByTag(_notesCanvas, "NoteGrip") as FrameworkElement;
            return grip == null ? 0 : Math.Min(grip.ActualWidth, grip.ActualHeight);
        }
    }

    internal DoodleNotePlacement? DiagFirstPlacement
    {
        get
        {
            string key = DiagFirstNoteKey;
            return !string.IsNullOrEmpty(key) && Page.Placements.TryGetValue(key, out var p)
                ? p : null;
        }
    }

    private static void CountByTag(DependencyObject root, string tag, ref int count)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is FrameworkElement fe && string.Equals(fe.Tag as string, tag, StringComparison.Ordinal))
                count++;
            if (child is DependencyObject d) CountByTag(d, tag, ref count);
        }
    }

    private void EditNote(DoodleNote note)
    {
        Page.AutoStyles ??= new Dictionary<string, DoodleAutoStyle>();
        Page.AutoStyles.TryGetValue(note.Id, out var style);
        var dlg = new DoodleBigScreenEditWindow(new DoodleBigEditTarget
        {
            Key = note.Id,
            ItemId = note.Id,
            Title = note.Title,
            Body = note.Body,
            BodyHint = "手动便签只存在这台机器上，不进班级同步包。",
            Style = style,
            ThemeKey = _cfg.ThemeKey,
            NoteTheme = style?.NoteTheme ?? ""
        })
        { Owner = this };

        if (dlg.ShowDialog() != true) return;
        note.Title = dlg.Draft.Title;
        note.Body = dlg.Draft.Body;
        SaveBigEdit("manual", note.Id, note.Id, dlg.Draft);
    }

    private void RenameBoard()
    {
        var dlg = new DoodleNoteEditWindow(_cfg.Name, "", showBody: false) { Owner = this };
        dlg.Title = "改板名";
        if (dlg.ShowDialog() != true) return;
        SetBoardName(dlg.NoteTitle);
    }

    /// <summary>改名并同步板头 / 窗口标题 / 配置。管理窗改完也走这里，避免两处各写一遍。</summary>
    internal void SetBoardName(string? name)
    {
        _cfg.Name = string.IsNullOrWhiteSpace(name) ? "涂鸦板" : name.Trim();
        if (_titleText != null) _titleText.Text = _cfg.Name;
        Title = _cfg.Name;
        DoodleBoardService.SaveConfig(_cfg);
    }

    // ======================================================================
    //  手写笔迹
    // ======================================================================

    /// <summary>笔迹按「板 + 当前页」独立存，切页不串字。</summary>
    private string InkFile => DoodleBoardService.InkPath(_cfg.Id, Page.Id);

    private void LoadInk()
    {
        try
        {
            var path = InkFile;
            if (!File.Exists(path)) return;
            using var fs = File.OpenRead(path);
            _loadingInk = true;
            _ink.Strokes = new StrokeCollection(fs);
        }
        catch (Exception ex)
        {
            // 笔迹坏了不能连累整块板打不开：清空重来，把原因留给用户看
            System.Diagnostics.Debug.WriteLine("涂鸦板笔迹读取失败: " + ex.Message);
        }
        finally
        {
            _loadingInk = false;
        }
    }

    private void ScheduleInkSave()
    {
        if (_loadingInk) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveInk()
    {
        try
        {
            var path = InkFile;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (_ink.Strokes.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            using var fs = File.Create(path);
            _ink.Strokes.Save(fs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("涂鸦板笔迹保存失败: " + ex.Message);
        }
    }

    private void ClearInk()
    {
        if (_ink.Strokes.Count == 0) return;
        var r = MessageBox.Show(this, "清空这块板的全部笔迹？", "涂鸦板",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        _ink.Strokes.Clear();
        SaveInk();
    }

    // ======================================================================
    //  几何 / 持久化
    // ======================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var src = PresentationSource.FromVisual(this) as HwndSource;
        src?.AddHook(WndProc);
        SendToBottom();
        ClampToWorkArea();
        UpdateSizeBadge();
        _sizeBadge.Visibility = Visibility.Collapsed;

        // 上次退出时是收起的，就照原样收着 —— 但展开高度要留在 _expandedHeight 里，
        // 不然点「展开」只能得到一块默认高的小板。
        if (_cfg.Collapsed)
        {
            _expandedHeight = Height;
            SetCollapsed(true, persist: false);
        }
    }

    /// <summary>
    /// 打开时把尺寸夹回工作区。换到更小的屏幕后，上次存的 1400×900 会有一截在屏幕外，
    /// 连 14 DIP 的缩放边都摸不到 —— 只夹这一次，之后用户拖多大都不再干预。
    /// </summary>
    private void ClampToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        double w = DoodleBoardMetrics.ClampWidthTo(Width, wa.Width);
        // ★ 收起时不能夹高度：ClampHeightTo 的保底是 MinHeight(220)，会把板名条又撑成一块黑板
        double h = _collapsed ? Height : DoodleBoardMetrics.ClampHeightTo(Height, wa.Height);
        if (Math.Abs(w - Width) < 0.5 && Math.Abs(h - Height) < 0.5) return;

        _applyingGeometry = true;
        try
        {
            Width = w;
            Height = h;
        }
        finally { _applyingGeometry = false; }
    }

    /// <summary>当前显示器工作区的像素尺寸（多屏时取窗口所在那块）。</summary>
    private static (int W, int H) WorkAreaPx(IntPtr hwnd)
    {
        try
        {
            var s = System.Windows.Forms.Screen.FromHandle(hwnd);
            if (s != null) return (s.WorkingArea.Width, s.WorkingArea.Height);

            var p = System.Windows.Forms.Screen.PrimaryScreen;
            if (p != null) return (p.WorkingArea.Width, p.WorkingArea.Height);
        }
        catch { /* 取不到就退回兜底，不该让整条拖动链路挂掉 */ }

        return (1920, 1080);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PersistGeometry()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        _applyingGeometry = true;
        try
        {
            _cfg.Left = Left;
            _cfg.Top = Top;
            _cfg.Width = Width;
            // ★ 收起时 Height 是板名条的高度（44 上下）。直接存它，下次启动 ClampHeight
            //   会把它夹到最小高度，再展开就只能得到一块 220 高的残板 —— 所以存展开高度。
            _cfg.Height = _collapsed ? _expandedHeight : Height;
            _cfg.Collapsed = _collapsed;
            DoodleBoardService.SaveConfig(_cfg);
            SaveInk();
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    private void UpdateSizeBadge()
    {
        _sizeBadge.Text = $"{(int)Math.Round(Width)} × {(int)Math.Round(Height)}";
        _sizeBadge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    /// <summary>服务在外部改了配置（换主题 / 切用途 / 改尺寸）后调用。</summary>
    public void ApplyConfig(DoodleBoardConfig cfg)
    {
        _cfg.ThemeKey = cfg.ThemeKey;
        _cfg.Mode = cfg.Mode;

        // 板恒定在底层：即使配置里带着 TopMost=true（老数据）也不设，否则一改主题就浮到最前
        _cfg.TopMost = false;
        Topmost = false;

        // 样式参数一起同步，否则服务改完配置窗口还按旧值画
        _cfg.ThickPen = cfg.ThickPen;
        _cfg.FontSize = cfg.FontSize;
        _cfg.TextColor = cfg.TextColor;
        _cfg.PenWidth = cfg.PenWidth;
        _cfg.PenColor = cfg.PenColor;
        _cfg.EraserSize = cfg.EraserSize;
        _cfg.HeaderStyle = (cfg.HeaderStyle ?? new DoodleHeaderStyle()).Clone();

        // ★ 板面 / 模块 / 摆放都在"页"上。只同步老的板级 AutoStyles 会把老师在别的页
        // 选的模块、拖好的位置全部冲掉 —— 统一从页深拷贝，再让老字段跟着当前页保持一致。
        _cfg.Pages = (cfg.Pages ?? new List<DoodleBoardPage>()).Select(p => p.Clone()).ToList();
        _cfg.ActivePage = Math.Clamp(cfg.ActivePage, 0, Math.Max(0, _cfg.Pages.Count - 1));
        var page = _cfg.EnsurePage();
        _cfg.Notes = page.Notes.Select(n => new DoodleNote
        {
            Id = n.Id, Title = n.Title, Body = n.Body
        }).ToList();
        _cfg.AutoStyles = page.AutoStyles.ToDictionary(
            kv => kv.Key, kv => kv.Value?.Clone() ?? new DoodleAutoStyle());

        if (cfg.Width is double w && cfg.Height is double h)
        {
            _applyingGeometry = true;
            try
            {
                Width = DoodleBoardMetrics.ClampWidth(w);
                Height = DoodleBoardMetrics.ClampHeight(h);
            }
            finally { _applyingGeometry = false; }
        }

        ApplyTheme();
        UpdateHeaderInfo();
        RebuildNotes();
    }

    public void SetMode(string mode)
    {
        _cfg.Mode = mode;
        SetInkActive(string.Equals(mode, DoodleModes.Ink, StringComparison.OrdinalIgnoreCase));
        DoodleBoardService.SaveConfig(_cfg);
    }

    public void SetTheme(string themeKey)
    {
        _cfg.ThemeKey = themeKey;
        ApplyTheme();
        DoodleBoardService.SaveConfig(_cfg);
    }

    public void ApplyPreset(double w, double h)
    {
        _applyingGeometry = true;
        try
        {
            Width = DoodleBoardMetrics.ClampWidth(w);
            Height = DoodleBoardMetrics.ClampHeight(h);
        }
        finally { _applyingGeometry = false; }
        UpdateSizeBadge();
        PersistGeometry();
    }

    /// <summary>
    /// 铺满当前屏幕：留 40 的边距，绝不贴着屏幕边和任务栏 ——
    /// 贴边之后 14 DIP 的缩放命中区就跑到屏幕外，用户再也拖不回来。
    /// </summary>
    public void FillWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        ApplyPreset(
            Math.Max(DoodleBoardMetrics.MinWidth, wa.Width - 40),
            Math.Max(DoodleBoardMetrics.MinHeight, wa.Height - 40));

        _applyingGeometry = true;
        try
        {
            Left = wa.Left + 20;
            Top = wa.Top + 20;
        }
        finally { _applyingGeometry = false; }

        PersistGeometry();
    }

    public void SetLocked(bool on)
    {
        _cfg.Locked = on;

        if (on && _inkActive)
        {
            // 锁上一律退出落笔层（先立刻掐断，不等重排）。
            // ★ 工具带的重排推到下一拍：锁定按钮现在就在工具带里，
            //   在它自己的 Click 里把整条工具带清空重建，会留下一个没松开的鼠标捕获。
            _inkActive = false;
            _ink.IsHitTestVisible = false;
            _ink.EditingMode = InkCanvasEditingMode.None;
            _notesHost.IsHitTestVisible = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateModeButton();
                ApplyInkStyle();
                RebuildToolBar();
            }), DispatcherPriority.Background);
        }

        UpdateLockButton();
        DoodleBoardService.SaveConfig(_cfg);
    }

    private void UpdateLockButton()
    {
        if (_lockBtn == null) return;
        _lockBtn.Content = _cfg.Locked ? "已锁定" : "锁定";
        _lockBtn.ToolTip = _cfg.Locked
            ? "已锁定：点这里解锁（当前不能拖板、写笔迹或改便签）"
            : "锁定后不能拖板、写笔迹或改便签";
        var accent = DoodleIcons.Parse(_theme.Accent);
        _lockBtn.Background = _cfg.Locked
            ? new SolidColorBrush(Color.FromArgb(0x44, accent.R, accent.G, accent.B))
            : Brushes.Transparent;
        _lockBtn.BorderBrush = _cfg.Locked
            ? new SolidColorBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B))
            : ParseBrush(_theme.Divider);
    }

    /// <summary>把这块板送到所有窗口之下（钩子装好后的首次定位）。</summary>
    private void SendToBottom()
    {
        try
        {
            var src = PresentationSource.FromVisual(this) as HwndSource;
            if (src == null) return;
            SetWindowPos(src.Handle, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { /* 取不到句柄不影响其它功能 */ }
    }

    /// <summary>诊断用：确认这块板真的不在最前（WPF 层与 Win32 扩展样式两层都要查）。</summary>
    internal bool DiagIsBottomMost
    {
        get
        {
            if (Topmost) return false;
            try
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null) return false;
                return (GetWindowLong(src.Handle, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>诊断用：本窗口句柄（供真实点击/遮挡探针使用）。</summary>
    internal IntPtr DiagHandle =>
        (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;

    // ======================================================================
    //  原生拖动 / 缩放
    // ======================================================================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // ★ 这里刻意「什么都不做」：
        //   在 WM_WINDOWPOSCHANGING 里把 hwndInsertAfter 改写成 HWND_BOTTOM，或者收到
        //   WM_ACTIVATE(WA_INACTIVE) 就去 SetWindowPos，都会打断系统自己的 modal 拖动/缩放循环，
        //   实测表现为「拖角拖不动、拖板头也不动」，严重时卡死在拖动松手那一刻。
        //   真正让这块板待在最底层的办法是 ShowActivated=false（见构造函数），
        //   窗口不抢激活就永远不会被顶到最前，一次压底 + 永不激活就够了。

        // ★ 收起成板名条后，整条板头都是 HTCAPTION（系统拖动区），WPF 的
        //   MouseLeftButtonDown 根本收不到 → 「双击展开」只能在这一层接。
        if (msg == WM_NCLBUTTONDBLCLK && _collapsed && wParam.ToInt32() == HTCAPTION)
        {
            SetCollapsed(false);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_GETMINMAXINFO)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            mmi.ptMinTrackSize.X = (int)Math.Round(DoodleBoardMetrics.MinWidth * dpi.DpiScaleX);
            // ★ 收起时最小高度必须跟着放宽：系统会按 ptMinTrackSize 拒绝更小的尺寸，
            //   不改的话 SetCollapsed 里设 75 会被系统默默顶回 220，板名条下面空一大片。
            double minH = _collapsed ? CollapsedWindowHeight() : DoodleBoardMetrics.MinHeight;
            mmi.ptMinTrackSize.Y = (int)Math.Round(minH * dpi.DpiScaleY);

            // ★ 尺寸上限不再是 900×620：用户想拖多大就多大，唯一的天花板是当前屏幕的工作区。
            // 用工作区而不是整屏 —— 拖到覆盖任务栏的尺寸后，底边就在屏幕外，再也抓不住了。
            var (workW, workH) = WorkAreaPx(hwnd);
            int maxW = (int)Math.Min(Math.Round(DoodleBoardMetrics.HardMaxWidth * dpi.DpiScaleX), workW);
            int maxH = (int)Math.Min(Math.Round(DoodleBoardMetrics.HardMaxHeight * dpi.DpiScaleY), workH);
            mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, maxW);
            mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, maxH);

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        int lp = lParam.ToInt32();
        int sx = (short)(lp & 0xFFFF);
        int sy = (short)((lp >> 16) & 0xFFFF);
        var client = PointFromScreen(new Point(sx, sy));

        double w = ActualWidth, h = ActualHeight;
        const double m = DoodleBoardMetrics.ResizeMargin;

        // 锁定：整窗不给拖动 / 缩放，但按钮仍要能点
        if (_cfg.Locked)
        {
            handled = true;
            return (IntPtr)HTCLIENT;
        }

        bool left = client.X <= m;
        bool right = client.X >= w - m;
        bool top = client.Y <= m;
        bool bottom = client.Y >= h - m;

        if (top && left) { handled = true; return (IntPtr)HTTOPLEFT; }
        if (top && right) { handled = true; return (IntPtr)HTTOPRIGHT; }
        if (bottom && left) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
        if (bottom && right) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
        if (left) { handled = true; return (IntPtr)HTLEFT; }
        if (right) { handled = true; return (IntPtr)HTRIGHT; }
        if (top) { handled = true; return (IntPtr)HTTOP; }
        if (bottom) { handled = true; return (IntPtr)HTBOTTOM; }

        // 按钮永远优先（否则点关闭会变成拖窗口）
        var hit = VisualTreeHelper.HitTest(this, client)?.VisualHit;
        var probe = hit;
        while (probe != null)
        {
            if (probe is System.Windows.Controls.Primitives.ButtonBase)
            {
                handled = true;
                return (IntPtr)HTCLIENT;
            }
            probe = VisualTreeHelper.GetParent(probe);
        }

        // 板头 = 拖动条；板面交给内容（便签要能点、手写要能落笔）。
        // ★ 板头高度不能用固定 HeadHeight（大字号会超出），木框的内缩也要算进去，否则木条那一圈会被当成板面。
        double headH = _head != null && _head.ActualHeight > 4 ? _head.ActualHeight : DoodleBoardMetrics.HeadHeight;
        double headBottom = headH + _frame.Padding.Top + _frame.BorderThickness.Top;
        if (client.Y <= headBottom)
        {
            handled = true;
            return (IntPtr)HTCAPTION;
        }

        handled = true;
        return (IntPtr)HTCLIENT;
    }

    // ======================================================================
    //  诊断辅助（仅供 --shot doodle* 用，正常运行不会碰到）
    // ======================================================================

    /// <summary>诊断注入的自动便签；为 null 时走真实数据。</summary>
    internal List<AutoNote>? DiagAutoOverride;

    internal void DiagRebuildNotes() => RebuildNotes();

    internal string DiagLayoutInfo()
    {
        var root = (Grid)_surface.Child;
        return $"frame={_frame.ActualHeight:0} surface={_surface.ActualHeight:0} root={root.ActualHeight:0} "
             + $"rows=[{root.RowDefinitions[0].ActualHeight:0},{root.RowDefinitions[1].ActualHeight:0},{root.RowDefinitions[2].ActualHeight:0}] "
             + $"tool={_toolBar.ActualHeight:0} win={ActualHeight:0} notes={_notesHost.ActualHeight:0}";
    }

    /// <summary>诊断用：工具带里所有按钮的最小边长。触屏下限 44，小于这个数手指点不准。</summary>
    internal double DiagMinToolHit
    {
        get
        {
            double m = double.MaxValue;
            foreach (var b in ToolButtons())
            {
                if (b.ActualWidth <= 0) continue;
                m = Math.Min(m, Math.Min(b.ActualWidth, b.ActualHeight));
            }
            return m > 10000 ? 0 : Math.Round(m, 1);
        }
    }

    /// <summary>诊断用：工具带里有几个"图标按钮"（Content 是 Path，而不是色点或文字）。</summary>
    internal int DiagToolIconCount
    {
        get
        {
            int n = 0;
            foreach (var b in ToolButtons())
                if (b.Content is PathShape) n++;
            return n;
        }
    }

    /// <summary>
    /// 递归取工具带里的按钮。
    /// ★ 必须走 <see cref="LogicalTreeHelper"/>：按钮现在被包在分组 StackPanel 里，
    /// 而且未 Show 的窗口可视树是空的，VisualTreeHelper 会一个都找不到。
    /// </summary>
    private IEnumerable<Button> ToolButtons()
    {
        foreach (var b in WalkButtons(_toolItems)) yield return b;
    }

    private static IEnumerable<Button> WalkButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button btn)
            {
                yield return btn;
            }
            else if (child is DependencyObject d)
            {
                foreach (var inner in WalkButtons(d)) yield return inner;
            }
        }
    }

    /// <summary>诊断用：关闭按钮是否真的可见可点（防止被 HTCAPTION 覆盖或挤出板头）。</summary>
    internal bool DiagCloseUsable =>
        _closeBtn != null && _closeBtn.IsVisible && _closeBtn.ActualWidth > 10 && _closeBtn.IsEnabled;

    /// <summary>造几条手写笔迹，让截图能看到"写过的板"是什么样子。</summary>
    internal void DiagSeedInk()
    {
        var color = ParseColor(_theme.DefaultPen);
        var attr = new DrawingAttributes { Color = color, Width = 3.0, Height = 3.0, FitToCurve = true };

        void Line(double x0, double y0, double x1, double y1)
        {
            var pts = new StylusPointCollection();
            int steps = 14;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                // 轻微正弦扰动，看起来像手写而不是直线
                pts.Add(new StylusPoint(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t + Math.Sin(t * Math.PI * 2) * 2.2));
            }
            _ink.Strokes.Add(new Stroke(pts, attr));
        }

        Line(40, 46, 250, 42);
        Line(40, 78, 200, 74);
        Line(40, 110, 150, 108);
        Line(300, 200, 340, 156);
    }

    // ======================================================================
    //  工具
    // ======================================================================

    private static Brush ParseBrush(string? hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    private static Color ParseColor(string? hex)
    {
        try
        {
            return string.IsNullOrWhiteSpace(hex)
                ? Color.FromRgb(0x25, 0x30, 0x3D)
                : (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x25, 0x30, 0x3D);
        }
    }
}
