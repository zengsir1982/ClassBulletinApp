using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ClassBulletinApp;

/// <summary>
/// 界面主题配色方案：切换主色（标题栏 / 按钮 / 强调色）与同色系浅色，使界面符合个人风格。
/// 配色通过 Application.Resources 中的 DynamicResource 键在 XAML 中引用，运行时整体替换。
/// </summary>
public static class ThemeManager
{
    /// <summary>可选主题（键 / 显示名）。</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Themes = new List<(string, string)>
    {
        ("ClassicBlue", "经典蓝"),
        ("FreshGreen", "清新绿"),
        ("ElegantPurple", "优雅紫"),
        ("VibrantOrange", "活力橙"),
        ("CalmTeal", "静谧青"),
    };

    // 每个主题：主色 / 深主色 / 浅色（按钮背景）/ 浅色描边
    // r76：整套降饱和 + 加深一档（「沉稳」口径）。亮饱和的主色在教室投影 / 一体机低亮度屏上
    // 会发飘、和白字对比不稳；压深之后白字更容易看清，和红色天数也不互相抢。
    private static readonly Dictionary<string, (string Primary, string Dark, string Light, string Border)> Palette = new()
    {
        ["ClassicBlue"]   = ("#1E4FA3", "#143A7A", "#E9EFF9", "#C2D2E9"),
        ["FreshGreen"]    = ("#2E7D4F", "#1F5C39", "#E8F3EC", "#B7D6C4"),
        ["ElegantPurple"] = ("#6B4BA8", "#4E3679", "#EFEAF7", "#C3B5DC"),
        ["VibrantOrange"] = ("#C2703A", "#97522A", "#F7EDE6", "#DEBFA8"),
        ["CalmTeal"]      = ("#17706B", "#0F4F4A", "#E5F2F1", "#B7D9D6"),
    };

    /// <summary>把指定主题应用到整个应用程序（替换 Application.Resources 中的主色刷）。</summary>
    public static void Apply(string theme)
    {
        if (!Palette.TryGetValue(theme ?? "", out var p)) p = Palette["ClassicBlue"];

        var app = System.Windows.Application.Current;
        if (app == null) return;

        app.Resources["PrimaryBrush"]       = Make(p.Primary);
        app.Resources["PrimaryDarkBrush"]   = Make(p.Dark);
        app.Resources["PrimaryLightBrush"]  = Make(p.Light);
        app.Resources["PrimaryBorderBrush"] = Make(p.Border);
    }

    /// <summary>默认主色（用于首次启动、未配置主题时）。</summary>
    public static string DefaultPrimary => Palette["ClassicBlue"].Primary;

    private static SolidColorBrush Make(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            return new SolidColorBrush(Colors.Blue);
        }
    }
}
