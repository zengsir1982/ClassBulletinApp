using System.Windows;
using System.Windows.Media;

namespace ClassBulletinApp;

/// <summary>
/// P4：纯 C# 构造的窗口（没有 .xaml）写不了 {DynamicResource}，
/// 以前把主色复制成 "#E9EFF9" 之类的字面量，ThemeManager 切主题时它们不会跟着变。
/// 这里给出统一入口：
///   · Bind()    —— SetResourceReference，运行时切主题会立刻跟随（已创建控件的首选）；
///   · HexOf()   —— 返回 #RRGGBB 字符串，给 HexBrush(...) / MakeButton(...) 这类既有辅助方法用；
///   · BrushOf() —— 返回 Brush，给必须传画刷的场合用。
/// 三者都带硬编码兜底：Application 尚未创建、或资源键缺失时仍返回原色，不抛异常。
/// </summary>
internal static class ChromePalette
{
    public const string Primary = "PrimaryBrush";
    public const string PrimaryDark = "PrimaryDarkBrush";
    public const string PrimaryLight = "PrimaryLightBrush";
    public const string PrimaryBorder = "PrimaryBorderBrush";
    public const string Ink = "InkBrush";
    public const string Muted = "MutedBrush";
    public const string Line = "LineBrush";
    public const string Surface = "SurfaceBrush";

    /// <summary>动态绑定：ThemeManager 替换 App.Resources 后，控件颜色自动更新。</summary>
    public static void Bind(FrameworkElement target, DependencyProperty dp, string key)
    {
        try { target.SetResourceReference(dp, key); } catch { }
    }

    /// <summary>取当前主题色并返回 #RRGGBB；用于仍以字符串传色的旧辅助方法（构造时取值，重开窗口即跟随）。</summary>
    public static string HexOf(string key, string fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush sb)
                return "#" + sb.Color.R.ToString("X2") + sb.Color.G.ToString("X2") + sb.Color.B.ToString("X2");
        }
        catch { }
        return fallback;
    }

    /// <summary>取当前主题色的画刷；键缺失时按 fallback 造一个。</summary>
    public static System.Windows.Media.Brush BrushOf(string key, string fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.Brush b) return b;
        }
        catch { }
        try { return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback)!); }
        catch { return System.Windows.Media.Brushes.Transparent; }
    }
}
