using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScheduleCore.Models;

/// <summary>
/// 一段可独立设置外观的文本（加粗 / 颜色 / 字号档位）。
/// <para>便签的「标题」「科目名」「条目名」共用这一套，界面上的
/// <c>TextStylePicker</c> 直接绑定本对象，改一个属性立刻生效（实现 INotifyPropertyChanged）。</para>
/// <para>★ <see cref="Color"/> 为空表示"跟随主题"（标题取主题的标题色、科目取强调色），
/// 这样默认外观与加这个功能之前完全一致；只有用户真的选了颜色才写入 #RRGGBB。</para>
/// </summary>
public class TextStyle : INotifyPropertyChanged
{
    private bool _bold;
    private string _color = "";
    private int _sizeLevel;

    /// <summary>是否加粗。</summary>
    public bool Bold
    {
        get => _bold;
        set => Set(ref _bold, value);
    }

    /// <summary>颜色（#RRGGBB；空 = 跟随主题）。</summary>
    public string Color
    {
        get => _color;
        set => Set(ref _color, value ?? "");
    }

    /// <summary>字号档位（下标见 <see cref="HwTextStyle.LevelNames"/>）。</summary>
    public int SizeLevel
    {
        get => _sizeLevel;
        set => Set(ref _sizeLevel, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>深拷贝（用于"取消"时丢弃临时改动）。</summary>
    public TextStyle Clone() => new() { Bold = Bold, Color = Color, SizeLevel = SizeLevel };

    /// <summary>把另一份样式复制到本对象（保留对象引用，绑定不会断）。</summary>
    public void CopyFrom(TextStyle? other)
    {
        if (other == null) return;
        Bold = other.Bold;
        Color = other.Color;
        SizeLevel = other.SizeLevel;
    }

    /// <summary>是否为"完全默认"（全不加粗、跟随主题、标准档）。</summary>
    public bool IsDefault => !Bold && string.IsNullOrEmpty(Color) && SizeLevel == 0;
}

/// <summary>
/// 便签文字的取用口径：标题 / 科目 / 正文各有一套基准偏移，保证"默认档位"渲染出来和加功能之前一模一样。
/// <para>正文沿用历史口径 <c>SizeOf(level) - 4</c>；科目与正文同口径（默认 15 号）；
/// 标题比标准档小 2（默认 17 号）。</para>
/// </summary>
public static class HwTextRender
{
    /// <summary>标题相对标准档的偏移。</summary>
    public const double TitleDelta = -2;

    /// <summary>正文相对标准档的偏移（历史口径，勿改）。</summary>
    public const double BodyDelta = -4;

    /// <summary>缺交便签科目名相对标准档的偏移（该便签历史上比作业便签小 1 号，保持原样）。</summary>
    public const double MissingSubjectDelta = -5;

    /// <summary>标题字号。</summary>
    public static double TitleSize(int level) => Math.Max(10, HwTextStyle.SizeOf(level) + TitleDelta);

    /// <summary>正文 / 科目字号。</summary>
    public static double BodySize(int level) => Math.Max(10, HwTextStyle.SizeOf(level) + BodyDelta);

    /// <summary>缺交便签科目名字号。</summary>
    public static double MissingSubjectSize(int level) => Math.Max(10, HwTextStyle.SizeOf(level) + MissingSubjectDelta);

    /// <summary>
    /// 按「档位 + 指定偏移」取字号（<paramref name="delta"/> = 0 时就是档位基准字号）。
    /// <para>供倒计时天数这类"自己一套基准"的文本使用，免得为每种文本都加一个 Delta 常量。</para>
    /// </summary>
    public static double SizeOf(int level, double delta) => Math.Max(10, HwTextStyle.SizeOf(level) + delta);

    /// <summary>按「档位 + 指定偏移」取字号（样式为空时按标准档）。</summary>
    public static double SizeOf(TextStyle? s, double delta) => SizeOf(s?.SizeLevel ?? 0, delta);

    /// <summary>
    /// 按「基准字号 × 档位比例」取字号：标准档正好等于 <paramref name="baseSize"/>，
    /// 各档的缩放幅度与正文一致。
    /// <para>用于值日生的「星期」「值日内容」这类历史基准比正文更小的文字 ——
    /// 若改用固定的负偏移，用户选「小」档会掉到 9 号以下，小到看不清。</para>
    /// </summary>
    public static double ScaleSize(TextStyle? s, double baseSize)
    {
        double ratio = HwTextStyle.SizeOf(s?.SizeLevel ?? 0) / HwTextStyle.SizeOf(0);
        return Math.Max(8, baseSize * ratio);
    }

    /// <summary>取样式里的字号（样式为空时按标准档）。</summary>
    public static double TitleSize(TextStyle? s) => TitleSize(s?.SizeLevel ?? 0);

    /// <summary>取样式里的字号（样式为空时按标准档）。</summary>
    public static double BodySize(TextStyle? s) => BodySize(s?.SizeLevel ?? 0);

    /// <summary>缺交便签科目名字号（样式为空时按标准档）。</summary>
    public static double MissingSubjectSize(TextStyle? s) => MissingSubjectSize(s?.SizeLevel ?? 0);

    /// <summary>取颜色：样式里为空时返回调用方给的主题色。</summary>
    public static string ColorOr(TextStyle? s, string fallback)
        => string.IsNullOrWhiteSpace(s?.Color) ? fallback : s!.Color;

    /// <summary>取粗细：样式为空时用调用方给的默认值（标题 / 科目默认加粗）。</summary>
    public static bool BoldOr(TextStyle? s, bool fallback) => s?.Bold ?? fallback;
}
