using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ClassBulletinApp;

/// <summary>实现本接口的窗口会被 <see cref="GlassBrush.RefreshAll"/> 统一刷新玻璃底色。</summary>
internal interface IGlassSurface
{
    void ApplyGlass(AppSettings? settings = null);
}

/// <summary>
/// 全局玻璃底色：只改「窗口根 Border 的底色与描边的 alpha」，文字与其它控件保持完全不透明。
///
/// <para><b>★ 为什么绝不碰 Window.Opacity：</b>通知条的 <c>HoverFader</c> 用
/// <c>BeginAnimation(OpacityProperty, HoldEnd)</c> 占着整窗透明度，<c>ApplyLayout</c> 后的
/// <c>Resync()</c> 还会清掉任何挂上去的动画，<c>--ui-check</c> 里也有 base→0→base 的断言。
/// 玻璃若写 Window.Opacity，穿透态通知条立刻失效。</para>
///
/// <para><b>★ 为什么只改现有 Border、不新增全屏元素：</b><c>FloatingHelper.SetClickThrough</c>
/// 的 WM_NCHITTEST 白名单靠 <c>TransformToVisual</c> 算可点区域，多叠一层会改变命中结果。</para>
///
/// <para><b>★ 为什么不用 ThemeManager / ChromePalette.HexOf / ThemeHelper.Parse：</b>
/// 这三条取色链都会丢掉 8 位 ARGB 的 alpha，玻璃色只能直接 <c>Color.FromArgb</c> 构造。</para>
/// </summary>
internal static class GlassBrush
{
    /// <summary>通透度上限：0 = 实色（现状），65 = 最透（底色仍保留 35% 不透明，文字永远清晰）。</summary>
    public const double MaxPercent = 65.0;
    public const double DefaultPercent = 45.0;

    // 记录「玻璃开启之前」的原始底色 alpha 与描边，供关闭时精确还原。
    // 用附加属性而不是字段：同一个 Border 可能被多个入口反复 Apply，必须幂等。
    static readonly DependencyProperty OrigAlphaProperty = DependencyProperty.RegisterAttached(
        "GlassOrigAlpha", typeof(byte), typeof(GlassBrush), new PropertyMetadata((byte)255));
    static readonly DependencyProperty OrigEdgeProperty = DependencyProperty.RegisterAttached(
        "GlassOrigEdge", typeof(Brush), typeof(GlassBrush), new PropertyMetadata(null));

    // 磨砂边缘：半透明白，只换颜色、不动 BorderThickness（零布局影响）
    static readonly SolidColorBrush Edge = Frozen(Color.FromArgb(190, 0xFF, 0xFF, 0xFF));

    public static double ClampPercent(double p)
        => double.IsFinite(p) ? Math.Clamp(p, 0, MaxPercent) : DefaultPercent;

    public static byte AlphaOf(double percent)
        => (byte)Math.Round(255 * (1 - ClampPercent(percent) / 100.0));

    /// <summary>r74：普通窗口组（分层窗）的通透度（已规范）。与悬浮窗的 <see cref="AppSettings.GlassOpacity"/> 分开。</summary>
    public static double ChromePercent(AppSettings? s)
        => ClampPercent(s?.GlassChromeOpacity ?? DefaultPercent);

    /// <summary>悬浮窗组是否开玻璃：总闸 + 悬浮窗开关 + 通透度 &gt; 0。</summary>
    public static bool FloatWant(AppSettings? s)
        => s?.GlassEnabled == true && s?.GlassFloatEnabled == true && ClampPercent(s?.GlassOpacity ?? DefaultPercent) > 0;

    /// <summary>从设置读取开关与通透度后应用到单个根 Border。</summary>
    public static void Apply(Border? root, AppSettings? settings = null)
    {
        var s = settings ?? Store.LoadSettings();
        ApplyCore(root, FloatWant(s), s?.GlassOpacity ?? DefaultPercent);
    }

    /// <summary>纯内存版本（诊断用）：不读设置，直接给开关与通透度。</summary>
    internal static void ApplyCore(Border? root, bool enabled, double percent)
    {
        if (root == null) return;
        bool on = enabled && ClampPercent(percent) > 0;
        byte glassA = AlphaOf(percent);

        if (root.Background is SolidColorBrush sb)
        {
            var c = sb.Color;
            // 只记录一次：提醒卡原本就带 PopupOpacity 的 alpha，不能被玻璃值污染
            if (root.ReadLocalValue(OrigAlphaProperty) == DependencyProperty.UnsetValue)
                root.SetValue(OrigAlphaProperty, c.A);
            byte origA = (byte)root.GetValue(OrigAlphaProperty);
            if (on)
                root.Background = Frozen(Color.FromArgb((byte)Math.Min(origA, glassA), c.R, c.G, c.B));
            else if (c.A != origA)
                root.Background = Frozen(Color.FromArgb(origA, c.R, c.G, c.B));
            // ★ 关闭且当前已是原始 alpha 时一律不写：通知条的 ApplyGlass 会先
            // SetResourceReference 重绑动态资源，这里再写本地值会切断主题跟随。
            // 默认关闭 = 对所有窗口「零改动」，这条必须守住。
        }

        // 动态资源绑定的描边一律跳过：改成本地值会切断主题跟随
        var local = root.ReadLocalValue(Border.BorderBrushProperty);
        bool edgeSafe = local == DependencyProperty.UnsetValue || local == null || local is Brush;
        if (!edgeSafe) return;

        if (on)
        {
            // ApplyTheme / ApplyBarStyle 可能重设过描边，此时把新值作为还原基准
            if (root.ReadLocalValue(OrigEdgeProperty) == DependencyProperty.UnsetValue
                || !ReferenceEquals(root.BorderBrush, Edge))
                root.SetValue(OrigEdgeProperty, root.BorderBrush);
            root.BorderBrush = Edge;
        }
        else if (root.ReadLocalValue(OrigEdgeProperty) != DependencyProperty.UnsetValue)
        {
            // 只有「当前仍是我们设的 Edge」才需要还原；ApplyTheme 若已重设过描边，
            // 直接采用它的新值，别把旧主题色写回去。
            if (ReferenceEquals(root.BorderBrush, Edge))
                root.BorderBrush = root.GetValue(OrigEdgeProperty) as Brush;
            root.ClearValue(OrigEdgeProperty);
        }
    }

    /// <summary>设置保存 / 实时预览时刷新所有已打开的玻璃窗口。</summary>
    public static void RefreshAll(AppSettings? settings = null)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        foreach (Window w in app.Windows)
            if (w is IGlassSurface g) g.ApplyGlass(settings);
        // r73d：普通窗口是分层窗 + 自绘标题栏，通透度变了实时刷新；关掉总闸则卡片变不透明
        try { GlassChrome.RefreshAll(settings); } catch { }
        // r73：一次刷新同时带上普通窗口的系统级 backdrop，开关 / 通透度 / 效果档位都实时生效
        try { GlassBackdrop.RefreshAll(settings); } catch { }
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }
}
