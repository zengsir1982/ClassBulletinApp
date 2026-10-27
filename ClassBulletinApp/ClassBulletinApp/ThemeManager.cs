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
    private static readonly Dictionary<string, (string Primary, string Dark, string Light, string Border)> Palette = new()
    {
        ["ClassicBlue"]   = ("#2D6CDF", "#1E4FA8", "#EAF1FD", "#9DBDF0"),
        ["FreshGreen"]    = ("#16A34A", "#0F7A38", "#E7F6EC", "#8FD9AE"),
        ["ElegantPurple"] = ("#7C3AED", "#5B21B6", "#F1E9FD", "#C4A6F0"),
        ["VibrantOrange"] = ("#EA580C", "#B45309", "#FDEDE2", "#F5B98C"),
        ["CalmTeal"]      = ("#0D9488", "#0B6B62", "#E2F5F2", "#8FD3CB"),
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
