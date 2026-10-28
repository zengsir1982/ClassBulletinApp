using System;
// 项目同时引用 WPF 与 WinForms（均有 Color / Brush 同名类型），此处全限定避免歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ClassBulletinApp;

/// <summary>主题配色小工具：把 #RRGGBB 文本解析为 WPF 颜色 / 画刷。</summary>
internal static class ThemeHelper
{
    /// <summary>解析 #RRGGBB；为空或非法时返回 fallback。</summary>
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex!.Trim().TrimStart('#');
        if (s.Length != 6) return fallback;
        try
        {
            return Color.FromRgb(
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { return fallback; }
    }

    /// <summary>解析 #RRGGBB 为画刷；为空或非法时返回 fallback 色。</summary>
    public static SolidColorBrush Brush(string? hex, Color fallback) => new SolidColorBrush(Parse(hex, fallback));

    /// <summary>按透明度生成画刷（用于便签底色等半透明场景）。</summary>
    public static SolidColorBrush Brush(string? hex, Color fallback, byte alpha)
    {
        var c = Parse(hex, fallback);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}
