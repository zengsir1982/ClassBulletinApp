using System;
using System.Collections.Generic;
using ScheduleCore.Models;
using ScheduleCore.Services;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace ClassBulletinApp;

/// <summary>
/// r73d「真·全局玻璃」：把<b>普通窗口</b>（主窗、设置、编辑窗、各弹窗）改造成
/// <c>AllowsTransparency=True</c> 的分层窗口 + 自绘标题栏，卡片底色按通透度带 alpha，
/// 于是它们和悬浮便签一样能真正透出桌面。
///
/// <para>★ 为什么必须走这条路（都是实测结论，不是推测）：
/// <list type="bullet">
/// <item>DWM 的 <c>DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE)</c> 在 Win11 上对普通顶层窗口
///       只返回成功、画面没有任何变化（--glass-probe 三种档位抓图 md5 完全一致）；</item>
/// <item>非分层 WPF 窗口<b>不支持逐像素 alpha</b>：把 Window.Background 设成 Transparent 后
///       透明区域被合成为<b>纯黑</b>（--glass-probe bare 抓图拍到整块黑），
///       半透明白则被预乘成一片灰实色 —— 所以「只调底色 alpha」对普通窗口天然不成立。</item>
/// </list></para>
///
/// <para>★ 由此定下的两段式挂载（WPF 的硬限制，绕不过去）：
/// <list type="number">
/// <item><c>AllowsTransparency</c> 只能在<b>窗口显示之前</b>设置，显示之后再设会抛
///       <c>InvalidOperationException</c>（--glass-probe runtime 实测）。
///       所以窗口样式在 <c>Initialized</c> 阶段改（XAML 窗口在 InitializeComponent 末尾触发，早于 Show）；</item>
/// <item>内容包装（自绘标题栏 + 圆角卡片）放 <c>Loaded</c>：那时 Content 一定就位，
///       也不用担心构造函数后面又改内容。</item>
/// </list></para>
///
/// <para>★ 四条不破的铁律：
/// <list type="number">
/// <item>总闸关闭时<b>完全不介入</b>（<c>GlassEnabled</c> 默认 false）：没开玻璃的机器
///       一行代码都不跑，窗口结构、尺寸、标题栏全是原生的；</item>
/// <item>诊断 / 截图模式一律跳过（<see cref="App.IsDiagnostic"/>）：分层窗会改变
///       <c>--shot</c> 的渲染结果与窗口尺寸，截图必须保持原生；</item>
/// <item>悬浮窗（<see cref="IGlassSurface"/>）与本来就是 <c>WindowStyle=None</c> 的窗口不碰，
///       它们由 r65 的 <see cref="GlassBrush"/> 负责；</item>
/// <item>拖动 / 拉边复用项目里已在用的 <see cref="FloatChrome"/>（WM_NCHITTEST 原生处理），
///       不自造手势，避免撞上「触屏 500ms / 10px」那套铁律。</item>
/// </list></para>
///
/// <para>⚠ 因为 ①，<b>运行中关掉玻璃回不到原生标题栏</b>（分层窗一旦创建就改不回去）。
/// 这时改成「卡片变不透明 + 保留自绘标题栏」，新开的窗口才完全回到原生外观。</para>
/// </summary>
internal static class GlassChrome
{
    /// <summary>自绘标题栏高度（DIP）。按钮 40×34，触摸也够得着。</summary>
    internal const double BarHeight = 40;

    /// <summary>改造前的原值，用于「关闭玻璃 → 卡片变不透明」与诊断还原。</summary>
    sealed class Saved
    {
        public double Height = double.NaN;      // 原 Height（可能为 NaN = 自动）
        public WM.Brush? Background;
        public W.UIElement? Content;
        public WC.Border? Root;                 // 插进去的圆角卡片
    }

    static readonly Dictionary<W.Window, Saved> _map = new();
    static readonly Dictionary<W.Window, bool> _prepared = new();

    static readonly W.DependencyProperty OrigContentAlphaProperty = W.DependencyProperty.RegisterAttached(
        "GlassChromeOrigAlpha", typeof(byte), typeof(GlassChrome), new W.PropertyMetadata((byte)255));

    static bool _installed;

    /// <summary>玻璃是否已在这个窗口上生效（已包装）。</summary>
    internal static bool IsAttached(W.Window w) => w != null && _map.ContainsKey(w);

    /// <summary>是否已改成无边框分层窗（Show 之前就定下了）。</summary>
    internal static bool IsPrepared(W.Window w) => w != null && _prepared.ContainsKey(w);

    internal static int AttachedCount => _map.Count;

    /// <summary>在 App.OnStartup 里注册一次：此后所有窗口自动按设置套玻璃。</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        // ① Show 之前改窗口样式只能靠各窗口构造函数里的 GlassChrome.Prepare(this)
        //    （WPF 的 Initialized 不是路由事件，注册不了类处理器）；这里只挂 ②。
        // ② Show 之后：包装内容（自绘标题栏 + 圆角卡片）
        W.EventManager.RegisterClassHandler(typeof(W.Window), W.FrameworkElement.LoadedEvent,
            new W.RoutedEventHandler((s, _) => { if (s is W.Window w) Wrap(w); }), true);
    }

    /// <summary>设置变更时刷新已打开的窗口（通透度实时；关掉总闸则卡片变不透明）。</summary>
    internal static void RefreshAll(AppSettings? settings = null)
    {
        var app = W.Application.Current;
        if (app == null) return;
        foreach (W.Window w in app.Windows) Apply(w, settings);
    }

    /// <summary>
    /// 已打开的窗口按当前设置刷新：开 → 调通透度；关 → 卡片变不透明
    /// （分层窗回不去原生标题栏，见类注释里的 ⚠）。
    /// </summary>
    internal static void Apply(W.Window w, AppSettings? settings = null)
    {
        if (w == null || !IsAttached(w)) return;
        var s = settings ?? Store.LoadSettings();
        if (SavedOf(w) is not { Root: { } root }) return;
        bool on = Want(s);
        double percent = GlassBrush.ChromePercent(s);
        root.Background = on ? Tint(w, percent) : Opaque(w);
        // ★ r74：内层浅色底的 alpha 也要跟着走。原来只在 Wrap 时算一次，
        //   拖动「主窗通透度」滑块只有卡片底色在变、内容层纹丝不动，看起来就像没生效。
        if (on && SavedOf(w)?.Content is { } inner) LoseOpacity(inner, percent);
    }

    static Saved? SavedOf(W.Window w) => _map.TryGetValue(w, out var v) ? v : null;

    /// <summary>总闸 + 普通窗口开关 + 普通窗口通透度 &gt; 0 三者同时成立才做玻璃（r74 起与悬浮窗分开）。</summary>
    internal static bool Want(AppSettings? s)
        => s?.GlassEnabled == true && s?.GlassAllWindows == true
           && GlassBrush.ChromePercent(s) > 0;

    /// <summary>
    /// 能不能改造：只接受「原生带标题栏的普通窗口」。
    /// 悬浮便签、分层窗（提醒条 / 手写球）、无边框窗（各种便签）一律不动。
    /// </summary>
    internal static bool CanAttach(W.Window w)
    {
        if (w == null) return false;
        if (w is IGlassSurface) return false;
        if (w.AllowsTransparency) return false;
        if (w.WindowStyle != W.WindowStyle.SingleBorderWindow) return false;
        if (w.Content is not W.UIElement) return false;
        return true;
    }

    // ---------- ① Show 之前：改成无边框分层窗 ----------

    internal static bool Prepare(W.Window w, bool force = false)
    {
        if (w == null) return false;
        if (IsPrepared(w)) return true;
        if (!force && (!Want(Store.LoadSettings()) || App.IsDiagnostic)) return false;
        if (!CanAttach(w)) return false;

        try
        {
            var saved = new Saved
            {
                Height = w.Height,
                Background = w.Background,
                Content = w.Content as W.UIElement
            };
            // ★ 顺序不能反：AllowsTransparency 要求 WindowStyle 已经是 None
            w.WindowStyle = W.WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = WM.Brushes.Transparent;
            _prepared[w] = true;
            _map[w] = saved;

            // 自绘标题栏吃掉 40 DIP，固定高度的窗口要补回来，否则内容被压扁
            if (!double.IsNaN(saved.Height) && (w.SizeToContent & W.SizeToContent.Height) == 0)
                w.Height = saved.Height + BarHeight;

            w.Closed += (_, _) => { _map.Remove(w); _prepared.Remove(w); };
            return true;
        }
        catch
        {
            // 任何一步失败就退回原样：宁可没玻璃，也不能让窗口坏掉
            _prepared.Remove(w);
            _map.Remove(w);
            try { w.WindowStyle = W.WindowStyle.SingleBorderWindow; } catch { }
            return false;
        }
    }

    // ---------- ② Show 之后：包装内容 ----------

    internal static bool Wrap(W.Window w, double? percentOverride = null)
    {
        if (w == null) return false;
        if (IsAttached(w) && SavedOf(w)?.Root != null) return true;

        var saved = SavedOf(w);
        if (saved?.Content == null || !ReferenceEquals(w.Content, saved.Content)) return false;

        try
        {
            // ★ 诊断可以传 percentOverride：用户的设置里主窗通透度可能是 0（= 实色、不做玻璃），
            //   那时按设置算出来的 alpha 就是 255，断言「必须带 alpha」会假失败。
            var percent = percentOverride ?? GlassBrush.ChromePercent(Store.LoadSettings());

            var card = new WC.Border
            {
                CornerRadius = new W.CornerRadius(12),
                BorderThickness = new W.Thickness(1),
                BorderBrush = new WM.SolidColorBrush(WM.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                Background = Tint(w, percent),
                SnapsToDevicePixels = true
            };
            // 根内容自己那层实色底也按通透度调低，否则整片实色会把玻璃盖住
            LoseOpacity(saved.Content, percent);

            var dock = new WC.DockPanel();
            var bar = BuildBar(w);
            WC.DockPanel.SetDock(bar, WC.Dock.Top);
            // ★ 必须先把原内容从窗口上摘下来（它还挂在 Window 的逻辑树上），
            //   否则 dock.Children.Add 会抛「元素已经是另一个元素的逻辑子元素」。
            w.Content = null;
            dock.Children.Add(bar);
            dock.Children.Add(saved.Content);
            card.Child = dock;

            var outer = new WC.Grid();
            outer.Children.Add(card);

            w.Content = outer;
            saved.Root = card;

            // 拖动 / 拉边 / 圆角各自兜底：任何一条挂了也不能让窗口变成「没有标题栏又关不掉」
            try { FloatChrome.Enable(w, () => IsAttached(w)); } catch { }
            try { FloatChrome.EnableDrag(bar, w); } catch { }
            bar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                ToggleMax(w);
                e.Handled = true;
            };
            w.StateChanged += (_, _) => SyncCorners(w);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            // ★ 安全网：主路径失败就退成「最简标题栏」，至少能拖动和关闭
            return WrapMinimal(w, saved);
        }
    }

    /// <summary>最近一次包装失败的原因（诊断 / 真机排查用；正常应为空串）。</summary>
    internal static string LastError = "";

    /// <summary>
    /// 兜底标题栏：主路径（圆角卡片 + 自绘按钮模板）万一失败时用。
    /// 只保证「有标题、能拖动、能关闭」——绝不能让窗口变成无边框且关不掉。
    /// </summary>
    static bool WrapMinimal(W.Window w, Saved saved)
    {
        try
        {
            if (saved.Content == null) return false;
            w.Content = null;
            var g = new WC.Grid();
            g.RowDefinitions.Add(new WC.RowDefinition { Height = new W.GridLength(BarHeight) });
            g.RowDefinitions.Add(new WC.RowDefinition { Height = new W.GridLength(1, W.GridUnitType.Star) });

            var bar = new WC.Border
            {
                Background = new WM.SolidColorBrush(WM.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                Padding = new W.Thickness(12, 0, 6, 0)
            };
            var row = new WC.DockPanel { LastChildFill = true };
            var close = new WC.Button
            {
                Content = "✕",
                Width = 40,
                Height = 34,
                Focusable = false,
                ToolTip = "关闭"
            };
            close.Click += (_, _) => { try { w.Close(); } catch { } };
            WC.DockPanel.SetDock(close, WC.Dock.Right);
            row.Children.Add(close);
            row.Children.Add(new WC.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(w.Title) ? "班级公布栏" : w.Title,
                FontSize = 13,
                FontWeight = W.FontWeights.SemiBold,
                VerticalAlignment = W.VerticalAlignment.Center
            });
            bar.Child = row;

            WC.Grid.SetRow(bar, 0);
            WC.Grid.SetRow(saved.Content, 1);
            g.Children.Add(bar);
            g.Children.Add(saved.Content);
            w.Content = g;
            saved.Root = null;
            try { FloatChrome.Enable(w, () => IsAttached(w)); } catch { }
            try { FloatChrome.EnableDrag(bar, w); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 还原 / 关闭玻璃 ----------

    /// <summary>
    /// 关闭玻璃：分层窗回不到原生标题栏（见类注释），于是把卡片变回不透明，
    /// 视觉上就是「普通窗口 + 自绘标题栏」，新开的窗口才完全回到原生外观。
    /// </summary>
    internal static void ToOpaque(W.Window w)
    {
        if (SavedOf(w)?.Root is WC.Border root) root.Background = Opaque(w);
    }

    /// <summary>诊断 / 关窗时彻底拆掉包装，把内容还回窗口。</summary>
    internal static void Unwrap(W.Window w)
    {
        var saved = SavedOf(w);
        if (saved == null) return;
        try
        {
            RestoreContentAlpha(saved.Content);
            if (saved.Root?.Child is WC.DockPanel dock && saved.Content != null)
            {
                dock.Children.Clear();
                w.Content = saved.Content;
            }
            saved.Root = null;
        }
        catch { }
    }

    /// <summary>圆角在最大化时去掉，否则四角会露出桌面。</summary>
    static void SyncCorners(W.Window w)
    {
        if (SavedOf(w)?.Root is not WC.Border root) return;
        root.CornerRadius = w.WindowState == W.WindowState.Maximized
            ? new W.CornerRadius(0) : new W.CornerRadius(12);
        if (w.WindowState == W.WindowState.Maximized)
        {
            var wa = W.SystemParameters.WorkArea;
            w.MaxHeight = wa.Height;
            w.MaxWidth = wa.Width;
        }
    }

    static void ToggleMax(W.Window w)
    {
        if (w.ResizeMode == W.ResizeMode.NoResize) return;
        if (w.WindowState == W.WindowState.Maximized) { w.WindowState = W.WindowState.Normal; return; }
        var wa = W.SystemParameters.WorkArea;
        w.MaxHeight = wa.Height;
        w.MaxWidth = wa.Width;
        w.WindowState = W.WindowState.Maximized;
    }

    // ---------- 底色 / 内容层 ----------

    static WM.Brush Tint(W.Window w, double percent)
    {
        var c = BaseColor(w);
        byte a = GlassBrush.AlphaOf(percent);
        var b = new WM.SolidColorBrush(WM.Color.FromArgb(a, c.R, c.G, c.B));
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    static WM.Brush Opaque(W.Window w)
    {
        var c = BaseColor(w);
        var b = new WM.SolidColorBrush(WM.Color.FromArgb(255, c.R, c.G, c.B));
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    /// <summary>卡片底色：沿用窗口原本的底色（Prepare 前记下的那份）。</summary>
    static WM.Color BaseColor(W.Window w)
    {
        if (SavedOf(w)?.Background is WM.SolidColorBrush ob && ob.Color.A > 0) return ob.Color;
        if (w.Background is WM.SolidColorBrush sb && sb.Color.A > 0) return sb.Color;
        return WM.Colors.White;
    }

    static W.DependencyProperty? BackgroundPropertyOf(W.UIElement el)
        => el is WC.Border ? WC.Border.BackgroundProperty
         : el is WC.Panel ? WC.Panel.BackgroundProperty
         : el is WC.Control ? WC.Control.BackgroundProperty : null;

    /// <summary>只调低根内容那一层「不透明浅色」底的 alpha；里面的白卡片保持实色，文字始终清晰。</summary>
    /// <para>★ r74：必须幂等 —— <see cref="Apply"/> 每次调通透度都会重来一遍。
    /// 原始 alpha 只在第一次记下，否则第二次会拿「已经被调低的值」当基准，通透度就只能单向变透、调不回去。</para>
    static void LoseOpacity(W.UIElement inner, double percent)
    {
        if (inner is not W.FrameworkElement fe) return;
        var prop = BackgroundPropertyOf(inner);
        if (prop == null) return;
        if (fe.ReadLocalValue(prop) is not WM.SolidColorBrush sb) return;
        var c = sb.Color;

        bool first = fe.ReadLocalValue(OrigContentAlphaProperty) == W.DependencyProperty.UnsetValue;
        if (first)
        {
            if (c.A < 200 || (c.R + c.G + c.B) / 3 < 190) return;   // 只动不透明的浅色底
            fe.SetValue(OrigContentAlphaProperty, c.A);
        }
        byte origA = (byte)fe.GetValue(OrigContentAlphaProperty);
        byte a = (byte)Math.Min(origA, GlassBrush.AlphaOf(percent));
        if (c.A == a) return;   // 已经是目标值：不写，避免无意义的属性变更通知
        var nb = new WM.SolidColorBrush(WM.Color.FromArgb(a, c.R, c.G, c.B));
        if (nb.CanFreeze) nb.Freeze();
        fe.SetValue(prop, nb);
    }

    static void RestoreContentAlpha(W.UIElement? el)
    {
        if (el is not W.FrameworkElement fe) return;
        var prop = BackgroundPropertyOf(el);
        if (prop == null) return;
        if (fe.ReadLocalValue(OrigContentAlphaProperty) == W.DependencyProperty.UnsetValue) return;
        byte a = (byte)fe.GetValue(OrigContentAlphaProperty);
        if (fe.ReadLocalValue(prop) is WM.SolidColorBrush sb)
            fe.SetValue(prop, new WM.SolidColorBrush(WM.Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B)));
        fe.ClearValue(OrigContentAlphaProperty);
    }

    // ---------- 自绘标题栏 ----------

    static W.FrameworkElement BuildBar(W.Window w)
    {
        var bar = new WC.Border
        {
            Height = BarHeight,
            Background = WM.Brushes.Transparent,
            Padding = new W.Thickness(14, 0, 6, 0),
            CornerRadius = new W.CornerRadius(12, 12, 0, 0),
            ToolTip = "按住这里可拖动窗口；双击最大化 / 还原"
        };

        var grid = new WC.Grid();
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1, W.GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = W.GridLength.Auto });

        grid.Children.Add(new WC.TextBlock
        {
            Text = string.IsNullOrWhiteSpace(w.Title) ? "班级公布栏" : w.Title,
            FontSize = 13,
            FontWeight = W.FontWeights.SemiBold,
            Foreground = new WM.SolidColorBrush(WM.Color.FromRgb(0x1F, 0x2A, 0x37)),
            VerticalAlignment = W.VerticalAlignment.Center,
            TextTrimming = W.TextTrimming.CharacterEllipsis
        });

        var buttons = new WC.StackPanel
        {
            Orientation = WC.Orientation.Horizontal,
            VerticalAlignment = W.VerticalAlignment.Center
        };
        buttons.Children.Add(CaptionButton("—", "最小化", null, () => w.WindowState = W.WindowState.Minimized));
        if (w.ResizeMode != W.ResizeMode.NoResize)
            buttons.Children.Add(CaptionButton("□", "最大化 / 还原", null, () => ToggleMax(w)));
        buttons.Children.Add(CaptionButton("✕", "关闭", WM.Color.FromRgb(0xC0, 0x39, 0x2B), () => w.Close()));

        WC.Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        bar.Child = grid;
        return bar;
    }

    /// <summary>标题栏按钮：40×34 命中区（触摸够大），悬停变色，关闭按钮悬停变红。</summary>
    static WC.Button CaptionButton(string glyph, string tip, WM.Color? hoverBg, Action click)
    {
        var btn = new WC.Button
        {
            Content = glyph,
            Width = 40,
            Height = 34,
            FontSize = 13,
            ToolTip = tip,
            Focusable = false,
            Background = WM.Brushes.Transparent,
            BorderThickness = new W.Thickness(0),
            Foreground = new WM.SolidColorBrush(WM.Color.FromRgb(0x37, 0x41, 0x51)),
            Margin = new W.Thickness(2, 0, 0, 0)
        };

        // 项目里没有隐式 Button 样式，这里自绘一个轻量模板（圆角 + 悬停底色）
        var template = new WC.ControlTemplate(typeof(WC.Button));
        var border = new W.FrameworkElementFactory(typeof(WC.Border));
        border.SetValue(WC.Border.BackgroundProperty,
            new W.TemplateBindingExtension(WC.Control.BackgroundProperty));
        border.SetValue(WC.Border.CornerRadiusProperty, new W.CornerRadius(6));
        var text = new W.FrameworkElementFactory(typeof(WC.ContentPresenter));
        text.SetValue(WC.ContentPresenter.HorizontalAlignmentProperty, W.HorizontalAlignment.Center);
        text.SetValue(WC.ContentPresenter.VerticalAlignmentProperty, W.VerticalAlignment.Center);
        border.AppendChild(text);
        template.VisualTree = border;
        btn.Template = template;

        var hover = new WM.SolidColorBrush(hoverBg ?? WM.Color.FromArgb(0x1A, 0x1F, 0x2A, 0x37));
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = hover;
            if (hoverBg.HasValue) btn.Foreground = WM.Brushes.White;
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = WM.Brushes.Transparent;
            if (hoverBg.HasValue) btn.Foreground = new WM.SolidColorBrush(WM.Color.FromRgb(0x37, 0x41, 0x51));
        };
        btn.Click += (_, _) => { try { click(); } catch { } };
        return btn;
    }

    // ---------- 诊断 ----------

    /// <summary>诊断：是否分层 / 标题栏在不在 / 卡片 alpha（0 = 没接上）。</summary>
    internal static (bool Layered, bool BarFound, byte Alpha) DiagState(W.Window w)
    {
        if (!IsAttached(w) || SavedOf(w)?.Root is not WC.Border root) return (false, false, 0);
        byte a = root.Background is WM.SolidColorBrush sb ? sb.Color.A : (byte)0;
        bool bar = root.Child is WC.DockPanel dock && dock.Children.Count > 0
                   && dock.Children[0] is W.FrameworkElement fe && Math.Abs(fe.Height - BarHeight) < 0.5;
        return (w.AllowsTransparency, bar, a);
    }

    /// <summary>
    /// 诊断：显示后能否切分层窗（WPF 限制，用来锁住「必须在 Show 之前设」这条）。
    /// 返回 "EX ..." 表示被拒绝 —— 这正是两段式挂载存在的原因。
    /// </summary>
    internal static string DiagRuntimeToggle()
    {
        W.Window? w = null;
        try
        {
            w = new W.Window
            {
                Width = 120,
                Height = 80,
                WindowStartupLocation = W.WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                Content = new WC.Border()
            };
            w.Show();
            w.WindowStyle = W.WindowStyle.None;
            w.AllowsTransparency = true;
            string r = "unexpected-ok";
            try { w.Close(); } catch { }
            return r;
        }
        catch (Exception ex)
        {
            try { w?.Close(); } catch { }
            return "EX " + ex.GetType().Name + ": " + ex.Message;
        }
    }
}
