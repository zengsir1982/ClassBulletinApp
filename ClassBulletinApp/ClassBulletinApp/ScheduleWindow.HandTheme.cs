using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Services;

// 全局 using 带 System.Drawing，Color / Brush / Brushes / Pen 会撞名（见 HandInk.cs 同样处理）
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮课表「板书模式」的观感（r145）：书写区底色 + 笔色。
///
/// <para>★ 为什么底色要单独做一个可换的档位，而不是写死 #FDFDFB：
/// 教室里一整天的光线差别很大 —— 上午投影开着是冷白光，下午西晒是暖黄，
/// 白底反光刺眼；有的班习惯把黑板当第二块板（墨绿 / 深灰），有的老师就爱米黄纸感。
/// 同一块板换一种底色，看起来完全是两种东西，而实现成本只是换一个背景刷。</para>
///
/// <para>★ 深色底必须配套浅色笔：颜色写死在设置里、底色换了笔不变，就是「写了看不见」。
/// 所以切深色底时若手里的笔是深色，自动换成该底色的默认笔色；切回浅色底时反过来。
/// 这条规则只兜底，菜单里「白」那支笔随时能手动挑。</para>
/// </summary>
public partial class ScheduleWindow
{
    /// <summary>一档底色：背景色 + 横线色 + 是否深色 + 该底色下的默认笔色。</summary>
    private sealed record HandLook(string Key, string Name, string Bg, string Line, bool Dark, string Ink);

    /// <summary>
    /// 底色档位（顺序即菜单顺序，第一个是出厂默认）。
    /// <para>Line 为空 = 不打横线（纯色板）。横线间距固定 44，与原来的备忘纸一致。</para>
    /// </summary>
    private static readonly HandLook[] HandLooks =
    {
        new("paper", "备忘纸（米白横线）", "#FDFDFB", "#EDF0F4", false, "#1F2A37"),
        new("white", "纯白（无横线）",     "#FFFFFF", "",        false, "#1F2A37"),
        new("cream", "米黄护眼",           "#FBF6E8", "#EFE7D0", false, "#1F2A37"),
        new("mint",  "淡青护眼",           "#E9F3EE", "#D8E8DE", false, "#1F2A37"),
        new("sky",   "淡蓝",               "#EDF3FB", "#DDE8F5", false, "#1F2A37"),
        new("green", "墨绿黑板",           "#2E4A3C", "#3B5C4B", true,  "#F2F5F9"),
        new("slate", "深灰夜写",           "#232A33", "#2F3843", true,  "#F2F5F9")
    };

    /// <summary>当前底色档位 Key（落盘在 <c>AppSettings.ScheduleHandTheme</c>）。</summary>
    private string _handThemeKey = HandLooks[0].Key;

    // ======================================================================
    //  底色
    // ======================================================================

    private void BtnHandTheme_Click(object sender, RoutedEventArgs e) => ShowHandThemeMenu();

    /// <summary>换个底色（save=true 时写进设置）。</summary>
    private void ApplyHandTheme(string? key, bool save)
    {
        var look = ResolveLook(key);
        _handThemeKey = look.Key;

        if (HandPanel != null)
            HandPanel.Background = new SolidColorBrush(DoodleIcons.Parse(look.Bg));
        if (HandPaper != null)
            HandPaper.Background = string.IsNullOrWhiteSpace(look.Line) ? null : BuildRuledBrush(look.Line);

        // 笔色跟着底色走：这笔在这个底上看不清，就换成该底色的默认笔色
        if (!ContrastOk(_handPenColor, look.Bg)) _handPenColor = look.Ink;

        ApplyHandPen();
        UpdateHandToolVisual();
        UpdateHandThemeButton();
        if (save) SaveHandLook();
    }

    /// <summary>
    /// 横线背景刷（与 XAML 里那张备忘纸完全同构：44 一格、线宽 1 压在格底）。
    /// <para>★ 用平铺绘图刷而不是逐个画线：窗口宽度会随拖动一直变，平铺刷不用重算。</para>
    /// </summary>
    private static Brush BuildRuledBrush(string lineHex)
    {
        var pen = new Pen(new SolidColorBrush(DoodleIcons.Parse(lineHex)), 1);
        pen.Freeze();
        var gd = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 43.5), new Point(1000, 43.5)));
        gd.Freeze();
        var brush = new DrawingBrush(gd)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, 1000, 44),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 1000, 44),
            ViewboxUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 这笔写在这个底上还看得清吗（按明度差判断）。
    /// <para>★ 不用「深底配浅笔」这种二选一规则：红笔在墨绿黑板上其实也糊，
    /// 而白笔在米黄纸上同样看不见 —— 直接比明度差最省事，阈值 0.28 是肉眼刚好能分出来的位置。</para>
    /// </summary>
    private static bool ContrastOk(string? penHex, string bgHex)
    {
        return Math.Abs(InkLum(penHex) - InkLum(bgHex)) >= 0.28;
    }

    private static double InkLum(string? hex)
    {
        var c = DoodleIcons.Parse(hex ?? "#1F2A37");
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }

    private static HandLook ResolveLook(string? key) =>
        HandLooks.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? HandLooks[0];

    /// <summary>
    /// 底色的选择菜单：左边一块该底色的色块（带线色边框），右边名字，当前档打勾。
    /// <para>用 ContextMenu 而不是常驻一排色块：手写区最窄 280，排不下 7 个色块，
    /// 而且底色是「开课前一分钟才想换」的东西，不该长期占着界面。</para>
    /// </summary>
    private void ShowHandThemeMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = BtnHandTheme,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (var look in HandLooks)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var swatch = new Border
            {
                Width = 22,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(DoodleIcons.Parse(look.Bg)),
                BorderBrush = new SolidColorBrush(DoodleIcons.Parse(
                    string.IsNullOrWhiteSpace(look.Line) ? "#CFD8E3" : look.Line)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock
            {
                Text = look.Name + (look.Key == _handThemeKey ? " ✓" : ""),
                VerticalAlignment = VerticalAlignment.Center
            });

            var mi = new MenuItem { Header = row };
            string picked = look.Key;
            mi.Click += (_, _) => ApplyHandTheme(picked, save: true);
            menu.Items.Add(mi);
        }

        menu.Items.Add(new Separator());
        var tip = new MenuItem { Header = "深色底会自动换成白笔", IsEnabled = false };
        menu.Items.Add(tip);
        menu.IsOpen = true;
    }

    /// <summary>蓝条上那颗底色按钮：一块当前底色的小色块 + 「底色」两字。</summary>
    private void UpdateHandThemeButton()
    {
        if (BtnHandTheme == null) return;

        var look = HandLooks.FirstOrDefault(t => t.Key == _handThemeKey) ?? HandLooks[0];
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(DoodleIcons.Parse(look.Bg)),
            BorderBrush = new SolidColorBrush(DoodleIcons.Parse(
                string.IsNullOrWhiteSpace(look.Line) ? "#CFD8E3" : look.Line)),
            BorderThickness = new Thickness(1.2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = "底色",
            FontSize = 12.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        BtnHandTheme.Content = row;
        BtnHandTheme.ToolTip = "书写区底色：" + look.Name + "（点一下可换，共 " + HandLooks.Length + " 种）";
    }

    // ======================================================================
    //  存取
    // ======================================================================

    private void LoadHandLook()
    {
        string theme = "";
        string pen = "";
        try
        {
            var s = Store.LoadSettings();
            theme = s.ScheduleHandTheme ?? "";
            pen = s.ScheduleHandPenColor ?? "";
        }
        catch { /* 读不出来就用出厂观感 */ }

        var look = ResolveLook(theme);
        // 用户自己挑的笔色优先，但要在这个底色上看得清 —— 看不清就别硬留（等于写了白写）
        if (!string.IsNullOrWhiteSpace(pen) && pen.StartsWith("#") && ContrastOk(pen, look.Bg))
            _handPenColor = pen;

        ApplyHandTheme(look.Key, save: false);
    }

    private void SaveHandLook()
    {
        try
        {
            var s = Store.LoadSettings();
            s.ScheduleHandTheme = _handThemeKey;
            s.ScheduleHandPenColor = _handPenColor;
            Store.SaveSettings(s);
        }
        catch { /* 存不上只是下次回到默认底色，不影响写字 */ }
    }

    /// <summary>笔色单独存（笔菜单里换颜色时调）。</summary>
    private void SaveHandPenColor() => SaveHandLook();

    // ======================================================================
    //  诊断（--ui-check）
    // ======================================================================

    internal int DiagHandLookCount => HandLooks.Length;
    internal string DiagHandThemeNames => string.Join("/", HandLooks.Select(t => t.Name));
    internal string DiagHandThemeKey => _handThemeKey;
    internal string DiagHandPenColor => _handPenColor;

    /// <summary>诊断用：书写区实际底色（16 进制 #AARRGGBB，取 HandPanel.Background）。</summary>
    internal string DiagHandCanvasBg =>
        HandPanel?.Background is SolidColorBrush sb ? sb.Color.ToString() : "";

    /// <summary>诊断用：横线层当前是不是「有横线」（纯白那档应变成 null）。</summary>
    internal bool DiagHandHasRuledLines => HandPaper?.Background is DrawingBrush;

    /// <summary>诊断用：把底色切到某一档（会像真人点菜单一样落盘）。</summary>
    internal void DiagSetHandTheme(string key) => ApplyHandTheme(key, save: true);

    internal int DiagHandPage => _handPage;
    internal int DiagHandPageCount => _handPageCount;

    /// <summary>诊断用：加一页（走真人那条路，含占位文件）。</summary>
    internal int DiagAddHandPage()
    {
        AddHandPage();
        return _handPageCount;
    }

    /// <summary>诊断用：删当前页（跳过确认框，其余完全一样）。</summary>
    internal int DiagDeleteHandPage()
    {
        DeleteCurrentHandPage();
        return _handPageCount;
    }

    internal void DiagGotoHandPage(int page) => GotoHandPage(page);

    /// <summary>诊断用：立刻落盘（正常情况下靠 500ms 防抖计时器）。</summary>
    internal void DiagSaveHandInkNow() => SaveHandInkNow();

    /// <summary>诊断用：某天第 N 页的笔迹文件在不在。</summary>
    internal bool DiagHandPageFileExists(int page) => System.IO.File.Exists(HandFile(_handDate, page));
}
