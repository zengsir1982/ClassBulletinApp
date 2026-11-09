using System;
using System.Windows;
// ★ 本项目同时 UseWPF + UseWindowsForms，且开了 ImplicitUsings：
//   UserControl / KeyEventArgs / Key 在两个命名空间里都有一份，必须全限定，
//   否则 XAML 生成的临时工程会直接 CS0104（r64 加 ChromePalette 时踩过同一个坑）。
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;

namespace ClassBulletinApp.Controls;

/// <summary>
/// 「问号小图标 + 点击 Popup」：把整段常显的操作说明 / 注意事项收起来，界面只留一颗 18px 圆标。
///
/// <para>★ 为什么不用 ToolTip：一体机触屏没有 hover，长按出来的工具提示又和「长按拖动」
/// 撞车；Popup 由点击触发，34×34 命中区按手指尺寸给，点一下就开、点外面就关。</para>
///
/// <para>★ 为什么单开：一屏里往往有十几个问号，同时弹几个卡片会互相盖住内容，
/// 也让人分不清哪个是哪个。静态 <c>_open</c> 保证打开新的先关掉旧的。</para>
///
/// <para>★ 键盘：Tab 到按钮 → Enter / 空格打开；Popup 内任意位置按 Esc 关闭并把焦点还给按钮。
/// 键盘可达是给用鼠标+键盘办公的老师留的，触屏用户走点击即可。</para>
/// </summary>
public partial class HelpBubble : System.Windows.Controls.UserControl
{
    static HelpBubble? _open;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HelpBubble),
        new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty PopupTitleProperty = DependencyProperty.Register(
        nameof(PopupTitle), typeof(string), typeof(HelpBubble),
        new PropertyMetadata("操作说明", OnContentChanged));

    public static readonly DependencyProperty BubbleWidthProperty = DependencyProperty.Register(
        nameof(BubbleWidth), typeof(double), typeof(HelpBubble),
        new PropertyMetadata(420.0, OnContentChanged));

    /// <summary>说明正文。同一区块的多段说明用换行（\n\n）合并到这一个属性里。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>弹层标题，默认「操作说明」。</summary>
    public string PopupTitle
    {
        get => (string)GetValue(PopupTitleProperty);
        set => SetValue(PopupTitleProperty, value);
    }

    /// <summary>正文最大宽度（逻辑像素），默认 420。</summary>
    public double BubbleWidth
    {
        get => (double)GetValue(BubbleWidthProperty);
        set => SetValue(BubbleWidthProperty, value);
    }

    public HelpBubble()
    {
        InitializeComponent();
        // PreviewKeyDown + handledEventsToo：Popup 里的关闭按钮拿到焦点时，Esc 也要能关掉
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKey), true);
        Unloaded += (_, _) => CloseBubble();
        ApplyContent();
    }

    static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as HelpBubble)?.ApplyContent();

    void ApplyContent()
    {
        // DP 可能在 XAML 解析过程中先于 InitializeComponent 完成就被赋值，做一次空守卫
        if (TxtBody == null || TxtTitle == null) return;
        TxtBody.Text = Text ?? "";
        TxtBody.MaxWidth = Math.Max(160, BubbleWidth);
        TxtTitle.Text = string.IsNullOrWhiteSpace(PopupTitle) ? "操作说明" : PopupTitle;
        // 图标自身的悬浮提示保留（这是一句操作指引，不是要收起来的长说明）
        BtnHelp.ToolTip = "点击查看" + (string.IsNullOrWhiteSpace(PopupTitle) ? "操作说明" : PopupTitle);
    }

    void BtnHelp_Click(object sender, RoutedEventArgs e) => Toggle();

    void BtnHelp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            Toggle();
            e.Handled = true;
        }
    }

    void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Bubble.IsOpen)
        {
            CloseBubble();
            BtnHelp.Focus();
            e.Handled = true;
        }
    }

    void Toggle()
    {
        if (Bubble.IsOpen) CloseBubble();
        else OpenBubble();
    }

    /// <summary>诊断（--ui-check）用：气泡当前是否展开。</summary>
    public bool IsBubbleOpen => Bubble != null && Bubble.IsOpen;

    public void OpenBubble()
    {
        if (_open != null && !ReferenceEquals(_open, this)) _open.CloseBubble();
        _open = this;
        Bubble.IsOpen = true;
    }

    public void CloseBubble()
    {
        if (Bubble != null && Bubble.IsOpen) Bubble.IsOpen = false;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    void Bubble_Opened(object sender, EventArgs e) => _open = this;

    void Bubble_Closed(object sender, EventArgs e)
    {
        if (ReferenceEquals(_open, this)) _open = null;
    }

    void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        CloseBubble();
        BtnHelp.Focus();
    }
}
