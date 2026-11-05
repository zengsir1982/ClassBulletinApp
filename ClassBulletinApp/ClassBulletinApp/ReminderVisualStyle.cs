using System;
using System.Windows.Media;
using ScheduleCore.Models;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace ClassBulletinApp;

/// <summary>
/// 提醒弹窗的配色解析：把 <see cref="ReminderSettings"/> 里的“风格预设 + 不透明度 + 字号倍数”
/// 折算成可以直接赋给控件的 Brush / 字号。
///
/// <para><b>为什么单独抽一个类：</b>同一套外观要同时套在三个地方 —— 上下课提醒小窗、
/// 「课表有变化」小窗、全屏大字提示。三处各写一遍必然出现“小窗调了、全屏没变”的问题，
/// 所以配色只在这里算，三个窗口都来取。</para>
///
/// <para><b>不透明度只作用于底色</b>，文字保持全不透明 —— 半透明的卡片配半透明的字，
/// 在教室后排就是一片糊，看不清等于没提醒。</para>
/// </summary>
internal sealed class ReminderVisualStyle
{
    /// <summary>预设键（存进 settings 的就是这些字符串）。</summary>
    public static readonly (string Key, string Name)[] Presets =
    {
        ("classic", "经典白 · 白底深字"),
        ("blue",    "深海蓝 · 蓝底白字"),
        ("dark",    "墨黑黄 · 黑底黄字"),
        ("green",   "护眼绿 · 浅绿深字"),
        ("custom",  "自定义 · 自己选底色与字色")
    };

    public Color Card { get; init; } = Colors.White;
    public Color Text { get; init; } = Color.FromRgb(0x1F, 0x2A, 0x37);
    public double Opacity { get; init; } = 1.0;
    public double FontScale { get; init; } = 1.0;

    /// <summary>底色偏暗（决定小标签、提示语这些辅助元素该用浅字还是深字）。</summary>
    public bool IsDark => Luma(Card) < 140;

    /// <summary>卡片底色（已乘不透明度）。</summary>
    public Brush CardBrush => new SolidColorBrush(WithAlpha(Card, (byte)Math.Round(255 * Math.Clamp(Opacity, 0.55, 1.0))));

    /// <summary>文字色；<paramref name="factor"/> 用来做“次要文字”（0.82 = 八成不透明的同一颜色）。</summary>
    public Brush TextBrush(double factor = 1.0)
        => new SolidColorBrush(WithAlpha(Text, (byte)Math.Round(255 * Math.Clamp(factor, 0.25, 1.0))));

    /// <summary>卡片描边：文字色的淡版，深浅底都能看出边界。</summary>
    public Brush BorderBrush => TextBrush(0.24);

    /// <summary>小标签 / 提示条的底色（文字色淡填充）。</summary>
    public Brush AccentFill => TextBrush(0.16);

    /// <summary>小标签 / 提示条的描边。</summary>
    public Brush AccentBorder => TextBrush(0.34);

    public static ReminderVisualStyle From(ReminderSettings? r)
    {
        double op = r == null ? 1 : Math.Clamp(r.PopupOpacity <= 0 ? 1 : r.PopupOpacity, 0.55, 1.0);
        double fs = r == null ? 1 : Math.Clamp(r.PopupFontScale <= 0 ? 1 : r.PopupFontScale, 0.8, 1.8);

        var (card, text) = Resolve(r);
        return new ReminderVisualStyle { Card = card, Text = text, Opacity = op, FontScale = fs };
    }

    private static (Color Card, Color Text) Resolve(ReminderSettings? r)
    {
        string key = (r?.PopupStyle ?? "classic").Trim().ToLowerInvariant();
        switch (key)
        {
            case "blue": return (Hex("#123A6B"), Hex("#FFFFFF"));
            case "dark": return (Hex("#141414"), Hex("#FFD54F"));
            case "green": return (Hex("#E7F4E9"), Hex("#14532D"));
            case "custom":
                // 自定义时若用户只改了一项，另一项回落到经典白，避免出现“黑底黑字”这类全糊的组合
                var c = Parse(r?.PopupCardColor, Hex("#FFFFFF"));
                var t = Parse(r?.PopupTextColor, Hex("#1F2A37"));
                if (Luma(c) < 60 && Luma(t) < 60) t = Hex("#FFFFFF");    // 双黑兜底
                if (Luma(c) > 200 && Luma(t) > 200) t = Hex("#1F2A37");  // 双白兜底
                return (c, t);
            default: return (Hex("#FFFFFF"), Hex("#1F2A37"));
        }
    }

    private static Color Parse(string? hex, Color fallback)
    {
        var v = (hex ?? "").Trim();
        if (v.Length == 0) return fallback;
        try
        {
            var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(v);
            return c;
        }
        catch { return fallback; }
    }

    private static Color Hex(string hex) => Parse(hex, Colors.White);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
}
