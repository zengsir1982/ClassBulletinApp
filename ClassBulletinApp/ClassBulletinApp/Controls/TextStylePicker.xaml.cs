using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
// 项目同时引用 WPF 与 WinForms，Color 需消歧义
using Color = System.Windows.Media.Color;

namespace ClassBulletinApp.Controls;

/// <summary>
/// 统一的「加粗 + 字号 + 颜色 + 调色 + 预览」一行控件，绑定一个 <see cref="ScheduleCore.Models.TextStyle"/>。
/// <para>为什么要有这个控件：作业 / 缺交 / 自定义便签 / 倒计时 / 值日生都要能调字体大小和颜色，
/// 如果每个窗口各写一套下拉，设置区会越排越长、宽度还不一致。这里一次性统一。</para>
/// <para>用法：<c>&lt;ctl:TextStylePicker StyleData="{Binding TitleStyle}" Label="标题"/&gt;</c>，
/// 用户一改就写回绑定的样式对象（无需额外确认按钮）。</para>
/// </summary>
// 项目同时引用 WPF 与 WinForms，UserControl / Color 等同名类型必须全限定或起别名
public partial class TextStylePicker : System.Windows.Controls.UserControl
{
    private bool _syncing;
    private TextStyle? _hooked;

    public TextStylePicker()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshAll();
    }

    // ---------- 依赖属性 ----------

    /// <summary>要编辑的样式对象。为空时控件保持空白（避免未初始化就写值）。</summary>
    public static readonly DependencyProperty StyleDataProperty = DependencyProperty.Register(
        nameof(StyleData), typeof(TextStyle), typeof(TextStylePicker),
        new PropertyMetadata(null, OnStyleDataChanged));

    public TextStyle? StyleData
    {
        get => (TextStyle?)GetValue(StyleDataProperty);
        set => SetValue(StyleDataProperty, value);
    }

    /// <summary>左侧标签文字（如「标题」「科目名」）。为空则不占位。</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(TextStylePicker),
        new PropertyMetadata("", OnLabelChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>紧凑模式：隐藏预览与标签，只用「加粗 + 字号 + 颜色」（用在每条一行的编辑列表里）。</summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(TextStylePicker),
        new PropertyMetadata(false, OnCompactChanged));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>预览文字内容。</summary>
    public static readonly DependencyProperty PreviewTextProperty = DependencyProperty.Register(
        nameof(PreviewText), typeof(string), typeof(TextStylePicker),
        new PropertyMetadata("预览", OnPreviewTextChanged));

    public string PreviewText
    {
        get => (string)GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    /// <summary>样式被用户改动时触发（宿主可据此刷新预览 / 标记为已修改）。</summary>
    public event EventHandler? StyleChanged;

    // ---------- 属性变化 ----------

    private static void OnStyleDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextStylePicker p) return;
        p.Hook(e.OldValue as TextStyle, e.NewValue as TextStyle);
        p.RefreshAll();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextStylePicker p) p.ApplyLayout();
    }

    private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextStylePicker p) p.ApplyLayout();
    }

    private static void OnPreviewTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextStylePicker p) p.UpdatePreview();
    }

    private void Hook(TextStyle? oldStyle, TextStyle? newStyle)
    {
        if (_hooked != null) _hooked.PropertyChanged -= OnStylePropertyChanged;
        _hooked = newStyle;
        if (_hooked != null) _hooked.PropertyChanged += OnStylePropertyChanged;
    }

    private void OnStylePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 外部（例如「套用主题默认」）改了样式，控件里的下拉要跟着走
        if (_syncing) return;
        Dispatcher.BeginInvoke(new Action(RefreshAll));
    }

    // ---------- 刷新 ----------

    /// <summary>把样式的值灌进各下拉，并刷新预览。</summary>
    private void RefreshAll()
    {
        var s = StyleData;
        _syncing = true;
        try
        {
            ApplyLayout();

            CbSize.ItemsSource = EditorStyleOptions.Sizes();
            ChkBold.IsChecked = s?.Bold ?? false;
            CbSize.SelectedValue = s?.SizeLevel ?? 0;

            var colors = EditorStyleOptions.Colors();
            var cur = EditorStyleOptions.EnsureColor(colors, s?.Color);
            CbColor.ItemsSource = colors;
            CbColor.SelectedValue = cur.Value;
        }
        finally
        {
            _syncing = false;
        }
        UpdatePreview();
    }

    private void ApplyLayout()
    {
        bool hasLabel = !string.IsNullOrWhiteSpace(Label);
        Lbl.Text = Label;
        Lbl.Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed;
        PreviewBox.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        Margin = Compact ? new Thickness(0) : new Thickness(0, 2, 0, 2);

        // 紧凑模式下「加粗」缩成一个 B 按钮，省出宽度给字号 / 颜色下拉
        ChkBold.Content = Compact ? "B" : "加粗";
        ChkBold.FontWeight = Compact ? FontWeights.Bold : FontWeights.Normal;
        ChkBold.Width = Compact ? 30 : double.NaN;
        ChkBold.Margin = Compact ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 10, 0);
    }

    private void UpdatePreview()
    {
        var s = StyleData;
        TxtPreview.Text = string.IsNullOrEmpty(PreviewText) ? "预览" : PreviewText;
        TxtPreview.FontSize = HwTextStyle.SizeOf(s?.SizeLevel ?? 0);
        TxtPreview.FontWeight = (s?.Bold ?? false) ? FontWeights.Bold : FontWeights.Normal;
        TxtPreview.Foreground = string.IsNullOrWhiteSpace(s?.Color)
            ? new SolidColorBrush(Color.FromRgb(0x33, 0x3B, 0x47))
            : ThemeHelper.Brush(s!.Color, Color.FromRgb(0x33, 0x3B, 0x47));
    }

    // ---------- 用户操作 ----------

    private void Bold_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || StyleData == null) return;
        StyleData.Bold = ChkBold.IsChecked == true;
        UpdatePreview();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Size_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || StyleData == null) return;
        if (CbSize.SelectedValue is not int level) return;
        StyleData.SizeLevel = level;
        UpdatePreview();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Color_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || StyleData == null) return;
        if (CbColor.SelectedValue is not string hex) return;
        StyleData.Color = hex;
        UpdatePreview();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BtnPick_Click(object sender, RoutedEventArgs e)
    {
        var s = StyleData;
        if (s == null) return;
        if (!EditorStyleOptions.TryPickColor(s.Color, out var hex)) return;

        s.Color = hex;

        // 自定义色不在预设表里，补一条进去，否则下拉会显示空白、再动一下就丢色
        _syncing = true;
        try
        {
            var colors = EditorStyleOptions.Colors();
            var opt = EditorStyleOptions.EnsureColor(colors, hex);
            CbColor.ItemsSource = colors;
            CbColor.SelectedValue = opt.Value;
        }
        finally
        {
            _syncing = false;
        }

        UpdatePreview();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }
}
