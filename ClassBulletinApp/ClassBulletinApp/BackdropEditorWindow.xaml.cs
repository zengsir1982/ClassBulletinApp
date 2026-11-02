using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScheduleCore.Models;
using ScheduleCore.Services;

// UseWindowsForms 会同时引入 System.Drawing / System.Windows.Forms 的同名类型，这里显式指定 WPF 版
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 桌面背景便签「所见即所得」排版窗口：模拟整块屏幕，便签可直接拖动调整位置、拖动四角 / 四边改变大小。
/// 替代原先在「布局设置」里用滑块调整位置 / 宽度 / 偏移的方式，操作更直观。
/// 提交时把便签的屏幕上矩形换算回模型（Anchor=TopLeft + 偏移 + 宽度 / 固定尺寸）写回设置并（若已应用）重绘壁纸。
/// </summary>
public partial class BackdropEditorWindow : Window
{
    private readonly AppSettings _s;
    private readonly double _W, _H, _scale;
    private double _disp = 1;
    private readonly List<CardEdit> _cards = new();
    private CardEdit? _sel;
    private readonly Canvas _canvas;

    private const double HANDLE = 12;     // 缩放手柄在预览里的像素尺寸（不受屏幕缩放影响，始终好抓）
    private const double MINW = 200;
    private const double MINH = 160;

    // 拖动 / 缩放过程中的临时状态
    private CardEdit? _move;
    private Point _startPt;
    private double _startX, _startY;
    private CardEdit? _resize;
    private string _dir = "";
    private double _rStartX, _rStartY, _rStartW, _rStartH;

    private sealed class CardEdit
    {
        public string Kind = "";
        /// <summary>自定义便签的 Id（Kind 以 "note:" 开头时有效）。</summary>
        public string NoteId = "";
        /// <summary>侧栏 / 提示里显示的名字（自定义便签用便签标题）。</summary>
        public string Label = "";
        public Border Card = null!;
        public double X, Y, W, H;
        public bool Fixed;
        public HomeworkWallpaperLayout Lay = null!;
        public Func<Border> Build = null!;
        public Grid Proxy = null!;
        public Border Selection = null!;
        public TextBlock SizeLabel = null!;
        public Dictionary<string, Border> Handles = new();
    }

    public BackdropEditorWindow()
    {
        InitializeComponent();

        // 小屏（如 1366×768）适配：窗口尺寸不超过工作区，保证预览区与底部按钮都可见
        var wa = SystemParameters.WorkArea;
        if (wa.Width > 0 && wa.Height > 0)
        {
            Width = Math.Max(760, Math.Min(Width, wa.Width - 40));
            Height = Math.Max(520, Math.Min(Height, wa.Height - 32));
        }

        _s = Store.LoadSettings();
        var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        _W = b.Width;
        _H = b.Height;
        _scale = _H / 1080.0;                       // 只用于偏移换算（与渲染器 Place 一致）
        _typo = BackdropService.PrimaryDpiScale();   // 字号比例尺（与壁纸一致）
        _canvas = PreviewCanvas;
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _canvas)) Select(null);
        };
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 根据预览容器实际尺寸计算缩放，使整屏恰好放进预览区
        double hostW = Math.Max(200, PreviewHost.ActualWidth - 24);
        double hostH = Math.Max(150, PreviewHost.ActualHeight - 24);
        _disp = Math.Min(hostW / _W, hostH / _H);
        _canvas.Width = _W * _disp;
        _canvas.Height = _H * _disp;

        SetBackground();
        BuildCards();
        if (_cards.Count == 0)
            TxtSel.Text = "当前没有勾选要显示的便签。请先在「布局设置 → 桌面背景」里勾选「当天课表 / 作业公示 / 缺交作业名单 / 自定义便签」之一，再打开本窗口。";
    }

    // ---------- 底图 ----------
    private void SetBackground()
    {
        var b = _s.Backdrop ?? new BackdropSettings();
        Brush bg;
        if (b.UseOriginWallpaper && !string.IsNullOrWhiteSpace(_s.SavedWallpaper) && File.Exists(_s.SavedWallpaper))
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(_s.SavedWallpaper, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                bg = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            catch { bg = new LinearGradientBrush(Colors.WhiteSmoke, Colors.LightGray, 120); }
        }
        else
        {
            bg = new LinearGradientBrush(Colors.WhiteSmoke, Colors.LightGray, 120);
        }
        _canvas.Background = bg;
    }

    // ---------- 构建便签 ----------
    /// <summary>
    /// 字号比例尺：壁纸按物理像素渲染，便签字号是 DIP 值，必须乘主屏 DPI 才和悬浮窗一样大。
    /// 注意与 <c>_scale</c>（屏高/1080，只用于偏移换算）区分：混用会让字号与悬浮窗不一致。
    /// </summary>
    private readonly double _typo;

    private Func<double, double> ScaleFont => v => Math.Max(8, v * _typo);

    private void BuildCards()
    {
        var b = _s.Backdrop ?? new BackdropSettings();
        var sf = ScaleFont;

        if (b.ShowSchedule)
        {
            var lay = b.ScheduleLayout ?? new HomeworkWallpaperLayout { Anchor = "MiddleLeft", WidthRatio = 0.38 };
            var built = BackdropService.BuildToday(_s);
            if (built.HasValue)
            {
                var theme = HomeworkThemes.Find(b.ScheduleTheme);
                _cards.Add(MakeCard("schedule", () =>
                {
                    double ww = BackdropRenderer.ResolveCardWidth(lay, _W, 0.38);
                    return BackdropRenderer.BuildScheduleCard(built.Value.Res, built.Value.Title,
                        built.Value.DateText, built.Value.WeekText, _typo, sf, ww, theme);
                }, lay));
            }
        }

        if (b.ShowHomework)
        {
            var board = Store.LoadHomework();
            if (board != null)
            {
                var lay = b.HomeworkLayout ?? board.Layout ?? new HomeworkWallpaperLayout { Anchor = "MiddleRight", WidthRatio = 0.4 };
                _cards.Add(MakeCard("homework", () => BackdropRenderer.BuildHomeworkCard(board, _typo, sf), lay));
            }
        }

        if (b.ShowMissing)
        {
            var mb = Store.LoadMissingBoard();
            var lay = b.MissingLayout ?? new HomeworkWallpaperLayout { Anchor = "BottomRight", UseFixedSize = true, FixedWidth = 420, FixedHeight = 360 };
            _cards.Add(MakeCard("missing", () => BackdropRenderer.BuildMissingCard(mb, _typo, sf), lay));
        }

        // 自定义便签：按设置里的显示开关，一张一卡（各自独立位置 / 大小）
        foreach (var note in BackdropService.VisibleCustomNotes(_s, b))
        {
            var entry = b.EntryOf(note.Id);
            var lay = entry.Layout ?? (entry.Layout = new HomeworkWallpaperLayout());
            var captured = note;
            _cards.Add(MakeCard("note:" + note.Id,
                () => BackdropRenderer.BuildCustomNoteCard(captured, _typo, sf), lay,
                string.IsNullOrWhiteSpace(note.Title) ? "自定义便签" : note.Title, note.Id));
        }

        foreach (var ce in _cards) AddProxy(ce);
        BuildSideList();
        DumpCards("build");
    }

    private CardEdit MakeCard(string kind, Func<Border> build, HomeworkWallpaperLayout lay,
        string? label = null, string noteId = "")
    {
        var card = build();
        double defRatio = kind == "schedule" ? 0.38 : 0.4;
        var tmp = new Canvas { Width = _W, Height = _H };
        BackdropRenderer.Place(tmp, card, lay, _W, _H, defRatio);
        double x = Canvas.GetLeft(card);
        double y = Canvas.GetTop(card);
        double w = card.Width;
        double h = card.Height;
        tmp.Children.Remove(card);

        // 初始让便签完整可见（即便原偏移把它推到屏外）
        w = Math.Min(w, _W);
        h = Math.Min(h, _H);
        x = Math.Clamp(x, 0, Math.Max(0, _W - w));
        y = Math.Clamp(y, 0, Math.Max(0, _H - h));

        return new CardEdit
        {
            Kind = kind,
            NoteId = noteId,
            Label = string.IsNullOrWhiteSpace(label) ? DefaultKindName(kind) : label!,
            Card = card,
            Lay = lay,
            Fixed = lay.UseFixedSize,
            X = x,
            Y = y,
            W = w,
            H = h,
            Build = build
        };
    }

    // ---------- 代理（可拖动 / 缩放的预览元素） ----------
    private void AddProxy(CardEdit ce)
    {
        var proxy = new Grid { Tag = ce, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        ce.Proxy = proxy;

        var cardHost = new Border { Background = Brushes.Transparent, IsHitTestVisible = false };
        ce.Card.LayoutTransform = new ScaleTransform(_disp, _disp);
        cardHost.Child = ce.Card;

        var sel = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true
        };
        ce.Selection = sel;

        proxy.Children.Add(cardHost);
        proxy.Children.Add(sel);

        foreach (var d in new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW" })
        {
            var h = MakeHandle(d);
            ce.Handles[d] = h;
            proxy.Children.Add(h);
        }

        proxy.MouseLeftButtonDown += OnProxyDown;
        proxy.MouseLeftButtonUp += OnProxyUp;
        proxy.MouseMove += OnProxyMove;

        Canvas.SetLeft(proxy, ce.X * _disp);
        Canvas.SetTop(proxy, ce.Y * _disp);
        proxy.Width = ce.W * _disp;
        proxy.Height = ce.H * _disp;

        UpdateFixedHandles(ce);
        _canvas.Children.Add(proxy);
    }

    private Border MakeHandle(string dir)
    {
        Cursor cur = dir switch
        {
            "N" or "S" => Cursors.SizeNS,
            "E" or "W" => Cursors.SizeWE,
            "NE" or "SW" => Cursors.SizeNESW,
            _ => Cursors.SizeNWSE
        };
        var h = new Border
        {
            Width = HANDLE,
            Height = HANDLE,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(2),
            Cursor = cur,
            Tag = dir,
            SnapsToDevicePixels = true
        };
        switch (dir)
        {
            case "N": h.HorizontalAlignment = HorizontalAlignment.Center; h.VerticalAlignment = VerticalAlignment.Top; h.Margin = new Thickness(0, -HANDLE / 2, 0, 0); break;
            case "S": h.HorizontalAlignment = HorizontalAlignment.Center; h.VerticalAlignment = VerticalAlignment.Bottom; h.Margin = new Thickness(0, 0, 0, -HANDLE / 2); break;
            case "E": h.HorizontalAlignment = HorizontalAlignment.Right; h.VerticalAlignment = VerticalAlignment.Center; h.Margin = new Thickness(0, 0, -HANDLE / 2, 0); break;
            case "W": h.HorizontalAlignment = HorizontalAlignment.Left; h.VerticalAlignment = VerticalAlignment.Center; h.Margin = new Thickness(-HANDLE / 2, 0, 0, 0); break;
            case "NE": h.HorizontalAlignment = HorizontalAlignment.Right; h.VerticalAlignment = VerticalAlignment.Top; h.Margin = new Thickness(0, -HANDLE / 2, -HANDLE / 2, 0); break;
            case "NW": h.HorizontalAlignment = HorizontalAlignment.Left; h.VerticalAlignment = VerticalAlignment.Top; h.Margin = new Thickness(-HANDLE / 2, -HANDLE / 2, 0, 0); break;
            case "SE": h.HorizontalAlignment = HorizontalAlignment.Right; h.VerticalAlignment = VerticalAlignment.Bottom; h.Margin = new Thickness(0, 0, -HANDLE / 2, -HANDLE / 2); break;
            case "SW": h.HorizontalAlignment = HorizontalAlignment.Left; h.VerticalAlignment = VerticalAlignment.Bottom; h.Margin = new Thickness(-HANDLE / 2, 0, 0, -HANDLE / 2); break;
        }
        h.PreviewMouseLeftButtonDown += OnHandleDown;
        h.PreviewMouseLeftButtonUp += OnHandleUp;
        h.PreviewMouseMove += OnHandleMove;
        return h;
    }

    /// <summary>手柄只在「选中该便签」时显示；未启用固定大小时隐藏上下两边的手柄（高度随内容自适应）。</summary>
    private void UpdateFixedHandles(CardEdit ce)
    {
        bool selected = ReferenceEquals(ce, _sel);
        foreach (var kv in ce.Handles)
        {
            bool vertical = kv.Key.Contains('N') || kv.Key.Contains('S');
            bool allowed = !vertical || ce.Fixed;
            kv.Value.Visibility = selected && allowed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------- 选中 ----------
    private void Select(CardEdit? ce)
    {
        _sel = ce;
        foreach (var c in _cards)
        {
            c.Selection.Visibility = ReferenceEquals(c, ce) ? Visibility.Visible : Visibility.Collapsed;
            UpdateFixedHandles(c);
        }
        TxtSel.Text = ce == null
            ? "点击任意便签即可选中：拖动便签移动位置，拖动四角 / 四边改变大小。"
            : $"正在编辑：{ce.Label}（{SizeText(ce)}）";
    }

    // ---------- 拖动移动 ----------
    private void OnProxyDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border hb && hb.Tag is string && ceHandles.Contains(hb.Tag)) return; // 手柄自行处理
        var proxy = (Grid)sender;
        var ce = (CardEdit)proxy.Tag;
        Select(ce);
        proxy.CaptureMouse();
        _move = ce;
        _startPt = e.GetPosition(_canvas);
        _startX = ce.X;
        _startY = ce.Y;
        e.Handled = true;
    }

    private void OnProxyMove(object sender, MouseEventArgs e)
    {
        if (_move == null) return;
        var pt = e.GetPosition(_canvas);
        double dx = (pt.X - _startPt.X) / _disp;
        double dy = (pt.Y - _startPt.Y) / _disp;
        _move.X = Math.Clamp(_startX + dx, 0, Math.Max(0, _W - _move.W));
        _move.Y = Math.Clamp(_startY + dy, 0, Math.Max(0, _H - _move.H));
        Canvas.SetLeft(_move.Proxy, _move.X * _disp);
        Canvas.SetTop(_move.Proxy, _move.Y * _disp);
        if (ReferenceEquals(_sel, _move)) TxtSel.Text = $"正在编辑：{_move.Label}（{SizeText(_move)}）";
    }

    private void OnProxyUp(object sender, MouseButtonEventArgs e)
    {
        ((Grid)sender).ReleaseMouseCapture();
        _move = null;
        DumpCards("move");
    }

    /// <summary>诊断：设置环境变量 CBP_EDITOR_DEBUG=1 时，把每张便签的矩形写入临时日志，
    /// 供自动化测试精确核对「拖动 / 缩放 / 保存 / 重新打开」的结果（正常使用不产生任何 IO）。</summary>
    private void DumpCards(string tag)
    {
        if (Environment.GetEnvironmentVariable("CBP_EDITOR_DEBUG") != "1") return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(tag).Append(" screen=").Append(_W).Append('x').Append(_H)
              .Append(" disp=").Append(_disp.ToString("0.####")).AppendLine();
            foreach (var c in _cards)
                sb.AppendLine($"{c.Kind} x={c.X:0.##} y={c.Y:0.##} w={c.W:0.##} h={c.H:0.##} fixed={c.Fixed}");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.editor.cards.log"), sb.ToString());
        }
        catch { }
    }

    // ---------- 缩放 ----------
    private static readonly HashSet<string> ceHandles = new() { "N", "S", "E", "W", "NE", "NW", "SE", "SW" };

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        var hb = (Border)sender;
        var proxy = (Grid)hb.Parent;
        var ce = (CardEdit)proxy.Tag;
        Select(ce);
        hb.CaptureMouse();
        _resize = ce;
        _dir = (string)hb.Tag;
        _startPt = e.GetPosition(_canvas);
        _rStartX = ce.X;
        _rStartY = ce.Y;
        _rStartW = ce.W;
        _rStartH = ce.H;
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (_resize == null) return;
        var pt = e.GetPosition(_canvas);
        double dx = (pt.X - _startPt.X) / _disp;
        double dy = (pt.Y - _startPt.Y) / _disp;
        double nx = _rStartX, ny = _rStartY, nw = _rStartW, nh = _rStartH;

        if (_dir.Contains('E')) nw = _rStartW + dx;
        if (_dir.Contains('W')) { nw = _rStartW - dx; nx = _rStartX + dx; }
        if (_dir.Contains('S')) nh = _rStartH + dy;
        if (_dir.Contains('N')) { nh = _rStartH - dy; ny = _rStartY + dy; }

        nw = Math.Clamp(nw, MINW, _W - 20);
        nh = Math.Clamp(nh, _resize.Fixed ? MINH : 40, _H - 20);
        nx = Math.Clamp(nx, 0, Math.Max(0, _W - nw));
        ny = Math.Clamp(ny, 0, Math.Max(0, _H - nh));

        _resize.X = nx;
        _resize.Y = ny;
        _resize.W = nw;
        _resize.H = nh;
        RebuildAndPosition(_resize);
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        ((Border)sender).ReleaseMouseCapture();
        _resize = null;
        _dir = "";
        DumpCards("resize");
    }

    /// <summary>
    /// 按新的宽度 / 固定状态重建便签（内容会按新宽度重新折行、固定高度内等比缩放），再更新预览位置与大小。
    /// </summary>
    private void RebuildAndPosition(CardEdit ce)
    {
        double defRatio = ce.Kind == "schedule" ? 0.38 : 0.4;
        ce.Lay.WidthRatio = ce.W / _W;
        if (ce.Fixed)
        {
            ce.Lay.UseFixedSize = true;
            ce.Lay.FixedWidth = ce.W;
            ce.Lay.FixedHeight = ce.H;
        }
        else
        {
            ce.Lay.UseFixedSize = false;
            ce.Lay.FixedHeight = 0;
        }

        var card = ce.Build();
        var tmp = new Canvas { Width = _W, Height = _H };
        BackdropRenderer.Place(tmp, card, ce.Lay, _W, _H, defRatio);
        double w = card.Width;
        double h = card.Height;
        tmp.Children.Remove(card);

        ce.Card = card;
        ce.W = w;
        ce.H = h;
        card.LayoutTransform = new ScaleTransform(_disp, _disp);

        // 替换代理里的旧卡
        var host = (Border)ce.Proxy.Children[0];
        if (host.Child is UIElement old && old != card) host.Child = null;
        host.Child = card;

        Canvas.SetLeft(ce.Proxy, ce.X * _disp);
        Canvas.SetTop(ce.Proxy, ce.Y * _disp);
        ce.Proxy.Width = w * _disp;
        ce.Proxy.Height = h * _disp;

        // 高度变化后可能顶出屏幕下沿，拉回可见区
        double maxY = Math.Max(0, _H - ce.H);
        if (ce.Y > maxY)
        {
            ce.Y = maxY;
            Canvas.SetTop(ce.Proxy, ce.Y * _disp);
        }

        UpdateSizeLabel(ce);
        if (ReferenceEquals(_sel, ce)) TxtSel.Text = $"正在编辑：{ce.Label}（{SizeText(ce)}）";
    }

    // ---------- 侧栏 ----------
    private void BuildSideList()
    {
        CardList.Children.Clear();
        foreach (var ce in _cards)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            row.Children.Add(new TextBlock
            {
                Text = ce.Label,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37))
            });

            var cb = new CheckBox
            {
                Content = "固定大小（位置 / 高度稳定）",
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = ce.Fixed
            };
            cb.Checked += (_, _) => ToggleFixed(ce, true);
            cb.Unchecked += (_, _) => ToggleFixed(ce, false);
            row.Children.Add(cb);

            var lbl = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            ce.SizeLabel = lbl;
            UpdateSizeLabel(ce);
            row.Children.Add(lbl);

            CardList.Children.Add(row);
        }
    }

    private void ToggleFixed(CardEdit ce, bool val)
    {
        ce.Fixed = val;
        if (val)
        {
            ce.Lay.FixedWidth = ce.W;
            ce.Lay.FixedHeight = ce.H;
        }
        UpdateFixedHandles(ce);
        RebuildAndPosition(ce);
        Select(ce);
    }

    private void UpdateSizeLabel(CardEdit ce)
    {
        if (ce.SizeLabel == null) return;
        string tip = ce.Fixed ? "" : "　未固定：高度随内容自适应，可拉左右两边改宽度";
        ce.SizeLabel.Text = $"大小：{ce.W:0} × {ce.H:0} 像素（占屏宽 {ce.W / _W * 100:0}%）{tip}";
    }

    private static string DefaultKindName(string kind) => kind switch
    {
        "schedule" => "当天课表",
        "homework" => "作业公示",
        "missing" => "缺交作业名单",
        _ => "自定义便签"
    };

    private static string SizeText(CardEdit ce) => $"{ce.W:0} × {ce.H:0} 像素";

    // ---------- 读取悬浮窗当前位置 / 大小 ----------
    /// <summary>
    /// 把当前悬浮窗（当天课表 / 作业公示 / 缺交名单 / 自定义便签）的实际位置与大小，直接读取为本排版的对应便签矩形，
    /// 并自动勾选「固定大小」，使桌面背景与悬浮窗的位置、尺寸完全一致。
    /// <para>· 只处理当前已勾选显示的便签（未显示的没有排版卡片）。</para>
    /// <para>· 桌面背景只按主屏渲染：中心点落在副屏 / 主屏之外的悬浮窗会被跳过并提示。</para>
    /// <para>· 位置取自悬浮窗的持久化坐标（窗口可见时即其当前位置），从未摆放过的会被跳过。</para>
    /// <para>读取结果只作用于本窗口的预览，确认无误后仍需点「应用并关闭」才会写入并重绘壁纸。</para>
    /// </summary>
    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_cards.Count == 0)
        {
            TxtSel.Text = "当前没有勾选要显示的便签。请先在「布局设置 → 桌面背景」里勾选「当天课表 / 作业公示 / 缺交作业名单 / 自定义便签」之一，再打开本窗口。";
            return;
        }

        var s = Store.LoadSettings();
        var b = s.Backdrop ?? new BackdropSettings();

        // WPF 逻辑像素(DIP) → 主屏物理像素（桌面背景按主屏物理分辨率渲染）。
        // 教室机通常单屏，本窗口与悬浮窗同在（主）屏；按本窗口 DPI 换算即可。
        double k = 1.0;
        try { var d = VisualTreeHelper.GetDpi(this); if (d.DpiScaleX > 0) k = d.DpiScaleX; } catch { }

        var notes = new List<string>();
        int done = 0;

        foreach (var ce in _cards)
        {
            string name = ce.Label;
            double? dl = null, dt = null, dw = null, dh = null;

            switch (ce.Kind)
            {
                case "schedule":
                    var sl = s.ScheduleLayout;
                    if (sl?.Left is double l1 && sl.Top is double t1 && sl.Width > 0 && sl.Height > 0)
                    { dl = l1; dt = t1; dw = sl.Width; dh = sl.Height; }
                    break;
                case "homework":
                    if (s.HwWinLeft is double l2 && s.HwWinTop is double t2
                        && s.HwWinWidth is double w2 && s.HwWinHeight is double h2)
                    { dl = l2; dt = t2; dw = w2; dh = h2; }
                    break;
                case "missing":
                    if (s.MsWinLeft is double l3 && s.MsWinTop is double t3
                        && s.MsWinWidth is double w3 && s.MsWinHeight is double h3)
                    { dl = l3; dt = t3; dw = w3; dh = h3; }
                    break;
                default:
                    if (ce.NoteId.Length > 0)
                    {
                        var note = s.CustomNotes?.FirstOrDefault(n => n.Id == ce.NoteId);
                        if (note?.Left is double l4 && note.Top is double t4
                            && note.Width is double w4 && note.Height is double h4)
                        { dl = l4; dt = t4; dw = w4; dh = h4; }
                    }
                    break;
            }

            if (dl == null || dt == null || dw == null || dh == null)
            {
                notes.Add($"{name}：没有可用的悬浮窗位置（请先打开该便签，摆放好并调整好大小）");
                continue;
            }

            // DIP → 物理像素
            double px = dl.Value * k, py = dt.Value * k, pw = dw.Value * k, ph = dh.Value * k;

            // 桌面背景只渲染主屏：中心点需落在主屏范围内
            double cx = px + pw / 2, cy = py + ph / 2;
            if (cx < 0 || cy < 0 || cx > _W || cy > _H)
            {
                notes.Add($"{name}：在副屏或主屏之外（桌面背景只渲染主屏），已跳过");
                continue;
            }

            // 夹进主屏，保证整块便签完整可见
            pw = Math.Clamp(pw, MINW, Math.Max(MINW, _W));
            ph = Math.Clamp(ph, MINH, Math.Max(MINH, _H));
            px = Math.Clamp(px, 0, Math.Max(0, _W - pw));
            py = Math.Clamp(py, 0, Math.Max(0, _H - ph));

            ce.X = px; ce.Y = py; ce.W = pw; ce.H = ph;
            ce.Fixed = true;                 // 锁定尺寸：高度不随内容漂移
            RebuildAndPosition(ce);          // 按新矩形重建卡片并刷新预览
            done++;
        }

        // 补充提示：有悬浮窗、但桌面背景没勾选的项目
        if (!b.ShowSchedule && s.ScheduleLayout?.Left.HasValue == true)
            notes.Add("当天课表未勾选「显示在桌面背景」，未同步");
        if (!b.ShowHomework && s.HwWinLeft.HasValue)
            notes.Add("作业公示未勾选「显示在桌面背景」，未同步");
        if (!b.ShowMissing && s.MsWinLeft.HasValue)
            notes.Add("缺交作业名单未勾选「显示在桌面背景」，未同步");
        foreach (var note in s.CustomNotes ?? new List<CustomNote>())
            if (note.Left.HasValue && !b.EntryOf(note.Id).Visible)
                notes.Add($"{(string.IsNullOrWhiteSpace(note.Title) ? "自定义便签" : note.Title)}未勾选「显示在桌面背景」，未同步");

        if (done > 0)
        {
            string extra = notes.Count > 0 ? "\n" + string.Join("；", notes) + "。" : "";
            TxtSel.Text = $"已读取 {done} 张便签的位置与大小，并锁定为固定尺寸。确认无误后点「应用并关闭」。{extra}";
        }
        else
        {
            TxtSel.Text = "未能读取到可用的悬浮窗位置。\n" + string.Join("；", notes) + "。";
        }

        DumpCards("capture");
    }

    // ---------- 提交 ----------
    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var b = _s.Backdrop ??= new BackdropSettings();
        foreach (var ce in _cards)
        {
            ce.Lay.Anchor = "TopLeft";
            ce.Lay.OffsetX = ce.X / _scale - 60;
            ce.Lay.OffsetY = ce.Y / _scale - 60;
            ce.Lay.WidthRatio = ce.W / _W;
            ce.Lay.UseFixedSize = ce.Fixed;
            ce.Lay.FixedWidth = ce.W;
            ce.Lay.FixedHeight = ce.Fixed ? ce.H : 0;
            if (ce.Kind == "schedule") b.ScheduleLayout = ce.Lay;
            else if (ce.Kind == "homework") b.HomeworkLayout = ce.Lay;
            else if (ce.NoteId.Length > 0) b.EntryOf(ce.NoteId).Layout = ce.Lay;
            else b.MissingLayout = ce.Lay;
        }

        Store.SaveSettings(_s);

        // 作业位置统一写回 homework.json，保证旧入口一致
        if (b.HomeworkLayout != null)
        {
            var board = Store.LoadHomework();
            if (board != null)
            {
                board.Layout = b.HomeworkLayout;
                Store.SaveHomework(board);
            }
        }

        if (BackdropService.IsApplied()) BackdropService.Apply(silent: true);
        AppEvents.RaiseBackdropChanged();

        try { DialogResult = true; } catch (InvalidOperationException) { }
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch (InvalidOperationException) { }
        Close();
    }
}
