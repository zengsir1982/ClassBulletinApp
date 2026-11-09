using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Services;

// WinForms 与 WPF 同名类型（项目同时引用两者，且全局 using 已引入 System.Drawing / Forms），
// 这里显式指定 WPF 版本，否则 Point / MouseEventArgs 会报"不明确的引用"。
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace ClassBulletinApp;

/// <summary>
/// 「手写输入」悬浮球：屏幕边上一个常驻小圆，点一下弹出手写面板。
/// <para><b>为什么要有这个球：</b>手写入口原来只加在作业 / 缺交两个编辑窗的底部，
/// 倒计时、值日、通知、用户中心、登录窗这些有输入框的地方都没有入口。逐个窗口加按钮的话，
/// 以后每加一个窗口就要记得加一颗，漏一个就是一句「这里为什么不能手写」。
/// 球的定位是"全局入口"：一次做好，处处可用。</para>
/// <para><b>不抢焦点：</b>用 <c>WS_EX_NOACTIVATE</c>。点球的那一刻如果窗口被激活，
/// 当前输入框就失焦了，手写结果就插不回去。同时加了 <c>WS_EX_TOOLWINDOW</c>，
/// 让它不出现在 Alt+Tab 里（它本来也不是给人切换的窗口）。</para>
/// <para><b>拖动：</b>不用 <c>DragMove()</c>——它会吞掉 MouseUp，导致"点一下"永远触发不了。
/// 这里自己算位移，抬起时位移小于阈值算点击、否则算拖动。</para>
/// </summary>
public partial class HandwritingBallWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    /// <summary>位移超过这个值（物理像素）才算拖动，否则算点击。太灵敏会"想点却划走了"。</summary>
    private const double DragThreshold = 8;

    private Point _downScreen;      // 按下时的鼠标屏幕坐标（物理像素）
    private double _startLeft, _startTop;
    private double _dpi = 1.0;
    private bool _dragging;

    private IntPtr _hwnd;
    private DispatcherTimer? _enableGuard;

    public HandwritingBallWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RestorePosition();
    
        GlassChrome.Prepare(this);}

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;

            IntPtr ex = GetWindowLongPtr(h, GWL_EXSTYLE);
            SetWindowLongPtr(h, GWL_EXSTYLE,
                new IntPtr(ex.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }
        catch
        {
            // 设不上就退回普通行为：会被激活，但功能仍可用（只是插入目标可能丢）
        }

        _hwnd = new WindowInteropHelper(this).Handle;
        StartEnableGuard();
    }

    /// <summary>
    /// 守住"球必须是可用窗口"这件事。
    /// <para><b>为什么需要：</b>WPF 的 <c>ShowDialog()</c>（编辑窗、确认框都是）会把<b>同一线程里
    /// 所有可见窗口</b>挨个 <c>EnableWindow(FALSE)</c>，好让"对话框没关就不能点别处"。手写球也是同线程的
    /// 顶层窗口，因此一并被禁用 —— 表现就是<b>球拖不动、点下去毫无反应</b>。老师看到的时机往往是
    /// "打开对话框、弹出触摸键盘"之后，很容易误判成和触摸键盘打架，其实是模态禁用。</para>
    /// <para><b>为什么能在模态期间跑：</b><c>ShowDialog</c> 内部是 <c>Dispatcher.PushFrame</c>，
    /// 主线程的消息循环照常转，DispatcherTimer 会继续触发。</para>
    /// <para><b>为什么只重新启用球自己：</b>模态的语义（其余窗口仍禁用）完全不变，
    /// 球又是 <c>WS_EX_NOACTIVATE</c>，点它不会把焦点从对话框抢走。</para>
    /// </summary>
    private void StartEnableGuard()
    {
        if (_enableGuard != null) return;

        _enableGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _enableGuard.Tick += (_, _) => EnsureEnabled();
        _enableGuard.Start();
    }

    /// <summary>被模态对话框禁用就立刻解禁（200ms 内恢复，用户感觉不到）。</summary>
    private void EnsureEnabled()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            if (!IsWindowEnabled(_hwnd)) EnableWindow(_hwnd, true);
        }
        catch
        {
            // 窗口还没建好 / 已经销毁，忽略
        }
    }

    // ---------- 位置 ----------

    /// <summary>恢复上次拖到的位置；没有记录就放到主屏右侧、垂直居中（老师站着一眼能看到、手能够到）。</summary>
    private void RestorePosition()
    {
        var wa = SystemParameters.WorkArea;

        double left, top;
        try
        {
            var s = Store.LoadSettings();
            if (s.HandBallLeft is double lx && s.HandBallTop is double ly)
            {
                left = lx;
                top = ly;
            }
            else
            {
                left = wa.Right - Width - 24;
                top = wa.Top + (wa.Height - Height) / 2;
            }
        }
        catch
        {
            left = wa.Right - Width - 24;
            top = wa.Top + (wa.Height - Height) / 2;
        }

        // 存档坐标可能来自另一块屏 / 改过分辨率：跑出工作区就拉回来，别让球"消失"
        if (left < wa.Left) left = wa.Left + 8;
        if (top < wa.Top) top = wa.Top + 8;
        if (left + Width > wa.Right) left = wa.Right - Width - 8;
        if (top + Height > wa.Bottom) top = wa.Bottom - Height - 8;

        Left = left;
        Top = top;
    }

    /// <summary>拖完贴边：离哪条边近就吸到哪条边，省得每次都要对齐。</summary>
    private void SnapToEdge()
    {
        var wa = SystemParameters.WorkArea;
        const double margin = 10;
        const double threshold = 28;   // 与 FloatingHelper 的贴边阈值保持一致

        if (Left < wa.Left + margin) Left = wa.Left + margin;
        if (Top < wa.Top + margin) Top = wa.Top + margin;
        if (Left + Width > wa.Right - margin) Left = wa.Right - margin - Width;
        if (Top + Height > wa.Bottom - margin) Top = wa.Bottom - margin - Height;

        if (Math.Abs(Left - (wa.Left + margin)) < threshold) Left = wa.Left + margin;
        if (Math.Abs(Left + Width - (wa.Right - margin)) < threshold) Left = wa.Right - margin - Width;
        if (Math.Abs(Top - (wa.Top + margin)) < threshold) Top = wa.Top + margin;
        if (Math.Abs(Top + Height - (wa.Bottom - margin)) < threshold) Top = wa.Bottom - margin - Height;
    }

    /// <summary>拖完才存盘（和悬浮窗一致：拖动过程中不写盘，避免一秒钟几十次写文件）。</summary>
    private void SavePosition()
    {
        try
        {
            if (!FloatingHelper.IsFiniteRect(this)) return;   // NaN 坐标写进设置会让整个 JSON 序列化失败
            var s = Store.LoadSettings();
            s.HandBallLeft = Left;
            s.HandBallTop = Top;
            Store.SaveSettings(s);
        }
        catch
        {
            // 存不上只影响下次位置，不该让拖动报错
        }
    }

    // ---------- 拖动 / 点击 ----------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_dpi <= 0) _dpi = 1.0;
        }
        catch { _dpi = 1.0; }

        _downScreen = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        _dragging = false;

        BallFill.Opacity = 0.72;    // 按下反馈：触屏上不给反馈，老师会反复点
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Root.IsMouseCaptured) return;

        // 注意：窗口跟着动时，鼠标相对窗口的位置会反向变化，所以两者相加仍是"鼠标的真实屏幕坐标"
        var now = PointToScreen(e.GetPosition(this));
        double dx = now.X - _downScreen.X;
        double dy = now.Y - _downScreen.Y;

        if (!_dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
            _dragging = true;

        if (!_dragging) return;

        Left = _startLeft + dx / _dpi;
        Top = _startTop + dy / _dpi;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        BallFill.Opacity = 1.0;
        e.Handled = true;

        if (_dragging)
        {
            SnapToEdge();
            SavePosition();
            return;
        }

        // 没拖动 = 想手写
        OpenHandwriting();
    }

    // ---------- 操作 ----------

    private void OpenHandwriting()
    {
        try
        {
            // 点球的这一刻可能还压在模态对话框的禁用里，先解禁再开面板，
            // 否则面板虽然弹出来，主窗口那边仍是禁用状态，插入会静默失败。
            EnsureEnabled();

            HandwritingService.OpenFromBall();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开手写面板失败：" + ex.Message, "手写输入",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MenuWrite_Click(object sender, RoutedEventArgs e) => OpenHandwriting();

    private void MenuHide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.ShowHandwritingBall = false;
            Store.SaveSettings(s);
        }
        catch { }

        Hide();
        AppEvents.RaiseHandwritingBallChanged();   // 控制面板里的勾选要同步取消
    }

    /// <summary>按设置显示/隐藏（设置勾选变化、取消桌面背景时都要走这里，保证状态一致）。</summary>
    public void ApplyVisibility(bool visible)
    {
        try
        {
            if (visible)
            {
                if (!IsVisible)
                {
                    // Show() 不会抢焦点（ShowActivated=False）
                    Show();
                    RestorePosition();
                }
            }
            else if (IsVisible)
            {
                Hide();
            }
        }
        catch { }
    }
}
