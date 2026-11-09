using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleCore.Models;
// 项目同时引用 WPF 与 WinForms，Color 需消歧义
using Color = System.Windows.Media.Color;

namespace ClassBulletinApp.Controls;

/// <summary>
/// 全窗唯一的一条字体工具条：形态「B | 字号 A⁺ A⁻ | A▾ 色板 | 橡皮」+ 右侧「正在设置：xxx」。
/// <para>r69 起由 <see cref="FocusStyleBinder"/> 驱动：<b>作用对象 = 当前聚焦的输入框或被点选的预览块</b>，
/// 控件自己不再固定绑某一个样式；宿主调 <see cref="Attach"/> 切换，调 <see cref="SetActive"/> 置灰。</para>
/// <para>底层仍是 <c>TextStyle{Bold, SizeLevel, Color}</c> 三个字段，没有斜体 / 下划线 / 字体族，
/// 所以 I / U 两个位置<b>故意不摆按钮</b> —— 摆灰按钮老师会以为功能坏了。</para>
/// </summary>
// 项目同时引用 WPF 与 WinForms，UserControl / Color 等同名类型必须全限定或起别名
public partial class TextStylePicker : System.Windows.Controls.UserControl
{
    private bool _syncing;
    private TextStyle? _hooked;

    /// <summary>字号选项只构造一次（静态数据，反复重建会让下拉的选择状态闪一下）。</summary>
    private static List<SizeOption>? _sizes;

    public TextStylePicker()
    {
        InitializeComponent();
        // Popup 不在可视树里，ElementName 绑定偶有解析不到的窗口期；直接指给按钮更稳
        ColorPopup.PlacementTarget = BtnColor;
        Loaded += (_, _) => RefreshAll();
    }

    // ---------- 依赖属性 ----------

    /// <summary>要编辑的样式对象。焦点跟随时由 <see cref="Attach"/> 反复切换。</summary>
    public static readonly DependencyProperty StyleDataProperty = DependencyProperty.Register(
        nameof(StyleData), typeof(TextStyle), typeof(TextStylePicker),
        new PropertyMetadata(null, OnStyleDataChanged));

    public TextStyle? StyleData
    {
        get => (TextStyle?)GetValue(StyleDataProperty);
        set => SetValue(StyleDataProperty, value);
    }

    /// <summary>当前作用对象的名字（「卡片标题」「值日生姓名」…）。</summary>
    public static readonly DependencyProperty TargetNameProperty = DependencyProperty.Register(
        nameof(TargetName), typeof(string), typeof(TextStylePicker),
        new PropertyMetadata("", OnTargetChanged));

    public string TargetName
    {
        get => (string)GetValue(TargetNameProperty);
        set => SetValue(TargetNameProperty, value);
    }

    /// <summary>
    /// 是否已选中作用对象。false 时<b>整条工具条置灰停用</b>，文案变「未选中字段」。
    /// ★ 不自动回退到标题：焦点丢失后若偷偷改到别的字段，老师会误改。
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(TextStylePicker),
        new PropertyMetadata(true, OnTargetChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>旧版左侧标签，r69 起不再显示；保留属性只为兼容老 XAML 不报错。</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(TextStylePicker), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// 紧凑模式：只留「B + 字号 + 色板」，用在作业 / 缺交列表里每行一条的场景。
    /// ★ 紧凑版<b>不参与焦点跟随</b>：它嵌在条目里，跟着焦点跳来跳去反而乱。
    /// </summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(TextStylePicker),
        new PropertyMetadata(false, OnCompactChanged));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>旧版预览示例文字，r69 起不再显示；保留属性只为兼容老 XAML 不报错。</summary>
    public static readonly DependencyProperty PreviewTextProperty = DependencyProperty.Register(
        nameof(PreviewText), typeof(string), typeof(TextStylePicker), new PropertyMetadata("预览"));

    public string PreviewText
    {
        get => (string)GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    /// <summary>样式被用户改动时触发（宿主可据此刷新预览 / 标记为已修改）。</summary>
    public event EventHandler? StyleChanged;

    // ---------- 焦点跟随 API ----------

    /// <summary>把工具条切到新的作用对象并激活。</summary>
    public void Attach(TextStyle? style, string name)
    {
        StyleData = style;
        TargetName = name ?? "";
        IsActive = style != null;
        RefreshAll();
    }

    /// <summary>激活 / 置灰。置灰时保留最后一次作用对象（只是不能点），不回退到别的字段。</summary>
    public void SetActive(bool active) => IsActive = active;

    /// <summary>置灰停用（等价于 <c>SetActive(false)</c>）。</summary>
    public void Deactivate() => SetActive(false);

    /// <summary>工具条内部是否持有键盘焦点（ComboBox 展开时用于避免误判「焦点丢失」）。</summary>
    public bool HasFocusWithin => IsKeyboardFocusWithin;

    /// <summary>诊断（--ui-check）用：底板当前是否真的可点（置灰时按钮点不动）。</summary>
    internal bool IsBarEnabled => Bar != null && Bar.IsEnabled;

    // ---------- 属性变化 ----------

    private static void OnStyleDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextStylePicker p) return;
        p.Hook(e.OldValue as TextStyle, e.NewValue as TextStyle);
        p.RefreshAll();
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextStylePicker p) p.ApplyLayout();
    }

    private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextStylePicker p) p.ApplyLayout();
    }

    private void Hook(TextStyle? oldStyle, TextStyle? newStyle)
    {
        if (_hooked != null) _hooked.PropertyChanged -= OnStylePropertyChanged;
        _hooked = newStyle;
        if (_hooked != null) _hooked.PropertyChanged += OnStylePropertyChanged;
    }

    private void OnStylePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 外部（例如「套用主题默认」）改了样式，控件里的按钮 / 下拉要跟着走
        if (_syncing) return;
        Dispatcher.BeginInvoke(new Action(RefreshAll));
    }

    // ---------- 刷新 ----------

    /// <summary>把样式的值灌进工具条，并刷新摘要。</summary>
    private void RefreshAll()
    {
        var s = StyleData;
        _syncing = true;
        try
        {
            ApplyLayout();

            TglBold.IsChecked = s?.Bold ?? false;

            CbSize.ItemsSource = _sizes ??= EditorStyleOptions.Sizes();
            CbSize.SelectedValue = s?.SizeLevel ?? HwTextStyle.ExtraLargeLevel;
        }
        finally
        {
            _syncing = false;
        }

        RefreshSwatches();
        UpdateMeta();
        UpdateStepButtons();
    }

    private void ApplyLayout()
    {
        bool active = IsActive && StyleData != null;

        // 整条工具条置灰：Bar.IsEnabled=false 一并让里面所有按钮不可点，
        // 避免「看样子能点、点了没反应」——这是焦点跟随最容易踩的坑。
        Bar.IsEnabled = active;
        Bar.Opacity = active ? 1.0 : 0.55;

        // 置灰时给「下一步该做什么」的指引，而不是干巴巴一句「未选中字段」——
        // 老师第一次打开窗口看到整条灰工具条，得知道点一下输入框就能用。
        TxtTarget.Text = active ? "正在设置：" + TargetName : "点输入框后设置格式";
        TxtTarget.Foreground = active
            ? new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63))
            : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        // 作用对象都没了，还挂着上一次的「27 号」摘要只会让人以为工具条仍有效
        TxtPreviewMeta.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        // 紧凑版塞不下步进 / 清除 / 右侧状态，只留「B + 字号 + 色板」三个
        var compactVisibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        BtnSizeDown.Visibility = compactVisibility;
        BtnSizeUp.Visibility = compactVisibility;
        BtnClear.Visibility = compactVisibility;
        PreviewBox.Visibility = compactVisibility;

        Bar.Padding = Compact ? new Thickness(2, 1, 2, 1) : new Thickness(2);
        Margin = Compact ? new Thickness(0) : new Thickness(0, 2, 0, 2);
    }

    /// <summary>重建色板。自定义色会被 <see cref="EditorStyleOptions.EnsureColor"/> 补到末尾，不会丢。</summary>
    private void RefreshSwatches()
    {
        var s = StyleData;
        var list = EditorStyleOptions.Colors();
        var cur = EditorStyleOptions.EnsureColor(list, s?.Color);

        SwatchGrid.Children.Clear();
        foreach (var opt in list)
        {
            var swatch = new Border
            {
                Style = (System.Windows.Style)FindResource("BarSwatchStyle"),
                Background = opt.Swatch,
                ToolTip = opt.Name,
                Tag = opt.Value
            };

            bool selected = string.Equals(opt.Value ?? "", s?.Color ?? "", StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                swatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF));
                swatch.BorderThickness = new Thickness(2);
            }

            var value = opt.Value ?? "";
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                ApplyColor(value);
                ColorPopup.IsOpen = false;
            };
            SwatchGrid.Children.Add(swatch);
        }

        ColorBar.Fill = cur.Swatch;
    }

    /// <summary>右侧摘要：准确磅值 + 加粗状态。示例大字已去掉 —— 输入框本身就是所见即所得。</summary>
    private void UpdateMeta()
    {
        var s = StyleData;
        int level = s?.SizeLevel ?? HwTextStyle.ExtraLargeLevel;
        TxtPreviewMeta.Text = HwTextStyle.SizeOf(level).ToString("0") + " 号"
                              + ((s?.Bold ?? false) ? " · 加粗" : "");
    }

    /// <summary>字号已到两端时把对应的 A± 置灰（避免点了没反应）。</summary>
    private void UpdateStepButtons()
    {
        int pos = HwTextStyle.OrderIndexOf(StyleData?.SizeLevel ?? 0);
        BtnSizeDown.IsEnabled = pos > 0;
        BtnSizeUp.IsEnabled = pos < HwTextStyle.SizeOrder.Length - 1;
    }

    // ---------- 用户操作 ----------

    private void Bold_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || StyleData == null) return;
        StyleData.Bold = TglBold.IsChecked == true;
        UpdateMeta();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Size_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || StyleData == null) return;
        if (CbSize.SelectedValue is not int level) return;
        StyleData.SizeLevel = level;
        UpdateMeta();
        UpdateStepButtons();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按「显示顺序」而不是下标顺序步进 —— 下标顺序是 19,16,23,27… 乱序的。</summary>
    private void StepSize(int delta)
    {
        var s = StyleData;
        if (s == null) return;

        int pos = Math.Clamp(HwTextStyle.OrderIndexOf(s.SizeLevel) + delta,
                             0, HwTextStyle.SizeOrder.Length - 1);
        int level = HwTextStyle.SizeOrder[pos];
        if (level == s.SizeLevel) return;

        s.SizeLevel = level;
        _syncing = true;
        try { CbSize.SelectedValue = level; }
        finally { _syncing = false; }

        UpdateMeta();
        UpdateStepButtons();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BtnSizeDown_Click(object sender, RoutedEventArgs e) => StepSize(-1);

    private void BtnSizeUp_Click(object sender, RoutedEventArgs e) => StepSize(+1);

    private void BtnColor_Click(object sender, RoutedEventArgs e)
    {
        // 打开前重建一次：别处可能刚套用了主题默认色
        RefreshSwatches();
        ColorPopup.IsOpen = true;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var s = StyleData;
        if (s == null) return;

        // r69：默认全部「特大 + 默认墨色」，清除格式就回到这个默认，而不是回到「标准」。
        s.Bold = false;
        s.SizeLevel = HwTextStyle.ExtraLargeLevel;
        s.Color = "";

        _syncing = true;
        try
        {
            TglBold.IsChecked = false;
            CbSize.SelectedValue = HwTextStyle.ExtraLargeLevel;
        }
        finally
        {
            _syncing = false;
        }

        RefreshSwatches();
        UpdateMeta();
        UpdateStepButtons();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyColor(string hex)
    {
        var s = StyleData;
        if (s == null) return;
        s.Color = hex;
        RefreshSwatches();
        UpdateMeta();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>色板里的「更多颜色…」：走系统取色器，选完把自定义色补进色板。</summary>
    private void BtnPick_Click(object sender, RoutedEventArgs e)
    {
        var s = StyleData;
        if (s == null) return;
        if (!EditorStyleOptions.TryPickColor(s.Color, out var hex)) return;

        ApplyColor(hex);
        ColorPopup.IsOpen = false;
    }

    // ---------- 键盘 ----------

    /// <summary>Ctrl+B / Ctrl+减 / Ctrl+加（等号）在控件内任意焦点下都能调样式。</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (StyleData == null || !IsActive) return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.B)
            {
                StyleData.Bold = !StyleData.Bold;
                _syncing = true;
                try { TglBold.IsChecked = StyleData.Bold; }
                finally { _syncing = false; }
                UpdateMeta();
                StyleChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            // OemPlus / Add = 调大，OemMinus / Subtract = 调小
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                StepSize(+1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                StepSize(-1);
                e.Handled = true;
            }
        }
    }
}
