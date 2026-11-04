using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScheduleCore.Services;

// WinForms 与 WPF 同名类型（项目同时引用两者），这里显式指定 WPF 版本。
using TextBox = System.Windows.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;

namespace ClassBulletinApp;

/// <summary>
/// 系统触摸键盘（TabTip，Win10/11 自带，含手写布局）的调起与收起。
/// <para><b>为什么要自己调：</b>WPF 桌面程序在 Win10 桌面模式下点文本框，系统不会自动弹键盘；
/// 教室一体机没有实体键盘，老师就只能盯着光标闪。</para>
/// <para><b>为什么用 TabTip 而不是 osk：</b>TabTip 带标准键盘 / 手写 / 表情，osk 只有标准键盘。</para>
/// <para><b>弹不出怎么办：</b>TabTip 被组策略或精简系统禁用时退回 osk；两者都没有就静默失败，
/// 不弹错误框打断输入（老师还能用别的方式输入）。</para>
/// </summary>
public static class TouchKeyboard
{
    private const string TabTipPath = @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe";

    /// <summary>触摸键盘的窗口类名。Win10 / Win11 都是这个（Win11 由 TextInputHost 托管，类名不变）。</summary>
    private const string TabTipClass = "IPTip_Main_Window";

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static string OskPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "osk.exe");

    /// <summary>本机是否存在可用的屏幕键盘程序。</summary>
    public static bool IsAvailable => File.Exists(TabTipPath) || File.Exists(OskPath);

    private static IntPtr TipHandle() => FindWindow(TabTipClass, null);

    /// <summary>触摸键盘当前是否已经弹出（避免重复启动进程）。</summary>
    public static bool IsShowing
    {
        get
        {
            try
            {
                var h = TipHandle();
                return h != IntPtr.Zero && IsWindowVisible(h);
            }
            catch { return false; }
        }
    }

    /// <summary>弹出触摸键盘。已在显示时直接返回。</summary>
    public static void Show()
    {
        try
        {
            if (IsShowing) return;

            if (File.Exists(TabTipPath))
            {
                Process.Start(new ProcessStartInfo(TabTipPath) { UseShellExecute = true });
                return;
            }

            if (File.Exists(OskPath))
                Process.Start(new ProcessStartInfo(OskPath) { UseShellExecute = true });
        }
        catch
        {
            // 弹不出就算了：不能让"键盘没装"变成"程序报错"。
        }
    }

    /// <summary>收起触摸键盘。</summary>
    public static void Hide()
    {
        try
        {
            var h = TipHandle();
            if (h != IntPtr.Zero) PostMessage(h, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
        }
        catch { }
    }
}

/// <summary>
/// 全局输入绑定：
/// 1) 手指点输入框时自动弹出触摸键盘（鼠标点击不弹）；
/// 2) 记住最后获得焦点的文本框，供「手写输入」把识别结果插回原位置。
/// <para>用 <see cref="EventManager.RegisterClassHandler"/> 挂类级处理器，一次注册对所有窗口生效，
/// 不需要在每个 XAML 里逐个加事件（几十个输入框，漏一个就"这个框不弹"）。</para>
/// </summary>
public static class TouchInputBinder
{
    private static bool _installed;

    /// <summary>缓存的"自动弹键盘"开关，避免每次触摸都去读 settings.json（会触发迁移与写盘）。</summary>
    private static bool _autoShow = true;

    /// <summary>
    /// 临时压住"自动弹键盘"。手写面板打开期间必须置 true：
    /// 触摸键盘和手写面板都在屏幕底部，一起弹会互相挡，而且手写时根本不需要键盘。
    /// </summary>
    public static bool SuppressAutoShow { get; set; }

    private static TextBox? _lastBox;

    /// <summary>安装全局处理器（幂等）。</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        RefreshSetting();

        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(OnPreviewTouchDown), true);

        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(OnPreviewTouchDown), true);

        EventManager.RegisterClassHandler(typeof(TextBox), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), true);
    }

    /// <summary>设置里改了开关后调用，刷新缓存。</summary>
    public static void RefreshSetting()
    {
        try { _autoShow = Store.LoadSettings().TouchKeyboardAutoShow; }
        catch { _autoShow = true; }
    }

    /// <summary>最后获得焦点的文本框（手写插入的目标）。</summary>
    public static TextBox? LastFocusedTextBox => _lastBox;

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not TextBox tb) return;

        // 手写面板自己的「结果」框不算输入目标：否则第二次打开手写，
        // 文字会插进面板自己的结果框里，形成自我循环。
        try
        {
            if (Window.GetWindow(tb) is HandwritingPopup) return;
        }
        catch { }

        _lastBox = tb;
    }

    private static void OnPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        if (SuppressAutoShow) return;
        if (!_autoShow) return;
        if (sender is not UIElement el) return;

        TouchKeyboard.Show();

        // TabTip 在绝大多数系统里是非激活窗口，不会抢焦点；
        // 万一抢了，打出来的字就进不去输入框，所以下一轮空闲时补一次焦点。
        var dispatcher = (sender as DispatcherObject)?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() =>
        {
            if (Keyboard.FocusedElement == null) el.Focus();
        }), DispatcherPriority.ApplicationIdle);
    }
}
