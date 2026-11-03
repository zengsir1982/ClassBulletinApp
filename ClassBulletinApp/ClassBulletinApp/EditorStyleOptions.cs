using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using ScheduleCore.Models;
// 项目同时引用了 WPF 与 WinForms（均有同名类型），此处用别名消歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>字号下拉项（Level 才是持久化值，Name 只用于显示）。</summary>
public sealed class SizeOption
{
    public int Level { get; init; }
    public string Name { get; init; } = "";
    public double Size { get; init; }

    /// <summary>下拉项自身的字体大小：让用户直接看出各档位差别（限制在可读范围，避免超大档把下拉撑爆）。</summary>
    public double PreviewSize => Math.Clamp(Size, 11, 19);
}

/// <summary>颜色下拉项（色块 + 名称）。</summary>
public sealed class ColorOption
{
    public string Value { get; init; } = "";
    public string Name { get; init; } = "";
    public SolidColorBrush Swatch { get; init; } = Brushes.Black;
}

/// <summary>
/// 三个编辑窗口（作业 / 缺交 / 自定义便签）共用的「字号 + 颜色」选项构造与自定义取色。
/// 选项一律来自 <see cref="HwTextStyle"/>，保证悬浮窗、壁纸、编辑器三处口径一致。
/// </summary>
internal static class EditorStyleOptions
{
    /// <summary>默认墨色（空值 / 无法解析时的回退色）。</summary>
    private static readonly Color DefaultInk = Color.FromRgb(0x33, 0x3B, 0x47);

    /// <summary>字号下拉项，按字号从小到大排列。</summary>
    public static List<SizeOption> Sizes() =>
        HwTextStyle.SizeOrder
            .Where(l => l >= 0 && l < HwTextStyle.Sizes.Length)
            .Select(l => new SizeOption
            {
                Level = l,
                Name = HwTextStyle.LevelNames[l],
                Size = HwTextStyle.Sizes[l]
            })
            .ToList();

    /// <summary>预设颜色下拉项。</summary>
    public static List<ColorOption> Colors() =>
        HwTextStyle.PresetColors.Select(c => MakeColor(c.Value, c.Name)).ToList();

    /// <summary>构造单个颜色项；<paramref name="name"/> 为空时自动取预设名或 hex。</summary>
    public static ColorOption MakeColor(string? value, string? name = null) => new()
    {
        Value = value ?? "",
        Name = string.IsNullOrWhiteSpace(name) ? HwTextStyle.ColorName(value) : name!,
        Swatch = new SolidColorBrush(ThemeHelper.Parse(value, DefaultInk))
    };

    /// <summary>
    /// 把（可能是自定义的）颜色补进下拉列表并返回该项。
    /// 老数据里存的自定义色如果在预设表里找不到，直接补一条，避免下拉变成"空白 + 一改就丢色"。
    /// </summary>
    public static ColorOption EnsureColor(IList<ColorOption> list, string? hex)
    {
        var v = (hex ?? "").Trim();
        var hit = list.FirstOrDefault(x => string.Equals(x.Value, v, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;

        // 自定义色直接显示 #RRGGBB：下拉只有 112 宽，"自定义 #RRGGBB" 会被截成"自定义 #8C"看不出是哪个色
        var opt = MakeColor(v, string.IsNullOrEmpty(v) ? HwTextStyle.PresetColors[0].Name : v.ToUpperInvariant());
        list.Add(opt);
        return opt;
    }

    /// <summary>打开系统取色器；确定后返回 #RRGGBB。取消返回 false。</summary>
    public static bool TryPickColor(string? current, out string hex)
    {
        hex = "";
        try
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                AllowFullOpen = true
            };

            var c = ThemeHelper.Parse(current, DefaultInk);
            try { dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B); } catch { }

            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

            hex = "#" + dlg.Color.R.ToString("X2") + dlg.Color.G.ToString("X2") + dlg.Color.B.ToString("X2");
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>颜色值（#RRGGBB，空 = 默认墨色）→ 画刷，用于编辑行内的即时预览。</summary>
internal sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new SolidColorBrush(ThemeHelper.Parse(value as string, Color.FromRgb(0x33, 0x3B, 0x47)));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字号档位 → 编辑行内字号（比便签正文小 4，与便签渲染口径一致）。</summary>
internal sealed class SizeLevelToFontSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int level = value is int i ? i : 0;
        return Math.Max(10, HwTextStyle.SizeOf(level) - 4);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
