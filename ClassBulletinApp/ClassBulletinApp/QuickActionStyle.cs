using System;
using System.Windows;
using System.Windows.Controls;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮课表底部三个内置快捷（作业 / 缺交 / 便签）胶囊的大小口径。
/// <para>· 悬浮课表本体与「管理」窗口里的预览都调这里，保证看到的就是实际效果。</para>
/// <para>· 「自动」档按窗口宽度挑一档字号：窄窗（220 左右）收小以免三个挤成两行，
///   宽窗放大一点，看起来更整齐。</para>
/// </summary>
public static class QuickActionStyle
{
    /// <summary>标准字号（与改版前一致）。</summary>
    public const double Standard = 12.5;

    /// <summary>设置里「自动」档的标记值（0 或负数都按自动处理）。</summary>
    public const double Auto = 0;

    /// <summary>自动档的最小 / 最大字号。</summary>
    private const double MinAuto = 10.5;
    private const double MaxAuto = 16;

    /// <summary>
    /// 把「设置值 + 当前窗口宽度」换算成实际字号。
    /// </summary>
    /// <param name="setting">设置里的字号；0 / 负数 = 随窗口宽度自动。</param>
    /// <param name="windowWidth">悬浮课表当前宽度（DIP）；未知时传 420（默认宽度）。</param>
    public static double Resolve(double setting, double windowWidth)
    {
        if (setting > 0) return Math.Clamp(setting, 9, 22);

        double w = windowWidth > 0 ? windowWidth : 420;
        // 台阶式而不是连续缩放：连续缩放会让每次拖动都跳一点字号，看着反而"发抖"
        double fs = w switch
        {
            < 240 => 11,
            < 290 => 11.5,
            < 350 => 12,
            < 430 => Standard,
            < 520 => 13.5,
            < 640 => 14.5,
            < 800 => 15.5,
            _ => MaxAuto
        };
        return Math.Clamp(fs, MinAuto, MaxAuto);
    }

    /// <summary>按字号设置胶囊的内边距与间距（与原来的 12.5 → 8,4 / 间距 5 等比例）。</summary>
    public static void Apply(Button? btn, double fontSize)
    {
        if (btn == null) return;

        btn.FontSize = fontSize;

        double padX = Math.Max(5, Math.Round(fontSize * 0.64));
        double padY = Math.Max(2, Math.Round(fontSize * 0.32));
        btn.Padding = new Thickness(padX, padY, padX, padY);
        btn.Margin = new Thickness(0, 0, Math.Max(4, Math.Round(fontSize * 0.4)), 4);
    }
}
