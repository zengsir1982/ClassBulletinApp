using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClassBulletinApp;

/// <summary>
/// 「鼠标移到窗口上就把它变透明」：常驻显示的通知条压在屏幕最上层，
/// 老师把鼠标移过去时让它自己让开（透明度降到 <see cref="FadedOpacity"/>，默认 0 = 完全看不见），
/// 离开后再淡回设定值（<c>OverlayOpacity</c> / <c>Opacity</c>）。
///
/// <para>★ <b>为什么必须靠全局光标轮询，不能用 MouseEnter / IsMouseOver：</b>
/// 常驻显示默认是「鼠标穿透」（WS_EX_TRANSPARENT + WM_NCHITTEST 返回 HTTRANSPARENT），
/// 窗口根本不接收鼠标消息 —— 依赖 WPF 的鼠标事件永远不会触发，功能看起来「完全没生效」。
/// 这与 <see cref="AutoHider"/> 用 GetCursorPos 唤醒屏幕外窗口是同一个理由。</para>
///
/// <para>★ <b>为什么是「变透明」而不是「缩小 / 移开」：</b>通知条的作用是让全班随时看得见，
/// 移开会给人「通知没了」的错觉；只是变透明，鼠标一离开立刻回来，且不影响任何布局和点击。</para>
///
/// <para>★ 60ms 一次轮询（约 16 次/秒）在教室机上几乎测不出 CPU 占用；
/// 用 30ms 那种高频率对这件「慢动作」没有意义。</para>
/// </summary>
public class HoverFader : IDisposable
{
    private readonly Window _w;
    private readonly DispatcherTimer _t;

    /// <summary>当前是否允许淡出（布局里的 HoverFade 开关 + 窗口确实显示着）。</summary>
    private readonly Func<bool> _enabled;

    /// <summary>鼠标离开后应恢复到的透明度（<c>OverlayOpacity</c> 或 <c>Opacity</c>，由宿主窗口决定）。</summary>
    private readonly Func<double> _restore;

    /// <summary>真正的「让开」透明度。0 = 完全看不见（用户要求的默认值）。</summary>
    public double FadedOpacity { get; set; }

    /// <summary>淡入 / 淡出时长。太长会「追不上鼠标」，太短会觉得闪。</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(140);

    /// <summary>此刻是否处于「让开」状态（诊断 / 截图脚本可读）。</summary>
    public bool IsFaded { get; private set; }

    /// <summary>拖动 / 编辑等操作期间置 true 可临时冻结，避免手指按在条子上时它突然消失。</summary>
    public bool Suspended { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    public HoverFader(Window w, Func<bool> enabled, Func<double> restore, double fadedOpacity = 0)
    {
        _w = w;
        _enabled = enabled;
        _restore = restore;
        FadedOpacity = fadedOpacity;

        _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _t.Tick += Tick;
        _t.Start();
    }

    /// <summary>全局光标是否落在窗口矩形内（物理像素 → 该窗口 DIP 后比较）。</summary>
    private bool CursorInWindow()
    {
        try
        {
            if (!GetCursorPos(out var p)) return false;
            var src = PresentationSource.FromVisual(_w);
            // 项目同时引用了 WPF 与 WinForms，Point / Rect 都有同名类型 —— 一律全限定
            System.Windows.Point pt = new(p.X, p.Y);
            if (src?.CompositionTarget != null)
                pt = src.CompositionTarget.TransformFromDevice.Transform(pt);

            double w = _w.ActualWidth > 0 ? _w.ActualWidth : _w.Width;
            double h = _w.ActualHeight > 0 ? _w.ActualHeight : _w.Height;
            if (w <= 0 || h <= 0 || double.IsNaN(_w.Left) || double.IsNaN(_w.Top)) return false;

            // 内缩 2px：正好卡在边界上时鼠标微动会让状态来回抖
            var r = new Rect(_w.Left + 2, _w.Top + 2, Math.Max(0, w - 4), Math.Max(0, h - 4));
            return r.Contains(pt);
        }
        catch { return false; }
    }

    private void Tick(object? sender, EventArgs e)
    {
        // 拖动 / 编辑期间：先无条件恢复可见再冻结。否则「鼠标按下时它正好是透明的」，
        // 用户就是在拖一个自己看不见的东西（放手后又闪回来，像是拖动把条子弄丢了）。
        if (Suspended)
        {
            if (IsFaded) Apply(_restore(), animate: true);
            return;
        }
        try
        {
            if (!_enabled())
            {
                if (IsFaded) Apply(_restore(), animate: true);
                return;
            }
            if (!_w.IsVisible || _w.WindowState == WindowState.Minimized) return;

            bool inside = CursorInWindow();
            if (inside == IsFaded) return;        // 状态没变，别每 60ms 都重启一次动画
            Apply(inside ? FadedOpacity : _restore(), animate: true);
        }
        catch { /* 窗口销毁过程中读 ActualWidth 可能抛，忽略即可 */ }
    }

    private void Apply(double target, bool animate)
    {
        IsFaded = target <= 0.001;
        try
        {
            double to = Math.Clamp(target, 0, 1);
            if (!animate)
            {
                _w.BeginAnimation(UIElement.OpacityProperty, null);
                _w.Opacity = to;
                return;
            }
            var anim = new DoubleAnimation(to, Duration)
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        catch { }
    }

    /// <summary>
    /// 布局被重新应用（<c>ApplyLayout</c> 会直接写 <c>Window.Opacity</c>，把动画顶掉）之后调用，
    /// 把透明度重新对齐到「此刻应有的值」——否则鼠标正停在条子上时改一下设置，条子会突然闪回来。
    /// </summary>
    public void Resync()
    {
        try
        {
            // 未启用淡出：保持宿主设好的值，什么都别做
            if (!_enabled()) { IsFaded = false; return; }
            Apply(IsFaded ? FadedOpacity : _restore(), animate: false);
        }
        catch { }
    }

    /// <summary>
    /// 诊断用（<c>--ui-check</c>）：不依赖真实鼠标位置，直接把状态切到「让开 / 恢复」。
    /// <para>★ 为什么需要这个钩子：轮询靠 <c>GetCursorPos</c>，自动化里没法把光标精确放到
    /// 那个窗口上；而这段最容易坏的恰恰是「动画有没有真的改到 Window.Opacity」，
    /// 坏了以后功能看起来就是完全没生效。</para>
    /// </summary>
    internal void ForceFade(bool faded) => Apply(faded ? FadedOpacity : _restore(), animate: false);

    /// <summary>诊断：宿主窗口此刻的透明度（ForceFade 之后读它判断有没有真的变）。</summary>
    internal double CurrentOpacity => _w.Opacity;

    public void Dispose()
    {
        try { _t.Stop(); } catch { }
    }
}
