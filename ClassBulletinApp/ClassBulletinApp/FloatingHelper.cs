using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮窗通用辅助：鼠标穿透、按锚点贴边定位、自动靠边隐藏。
/// </summary>
public static class FloatingHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WM_NCHITTEST = 0x84;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // 记录每个窗口已挂载的命中测试钩子，便于切换时移除，避免重复挂载
    private static readonly Dictionary<IntPtr, HwndSourceHook> Hooks = new();

    /// <summary>
    /// 开启/关闭鼠标穿透：开启后窗口不接收鼠标（点击落到底层窗口，纯展示）。
    /// interactive 中的元素保持可点击（如隐藏按钮、标题区、通知条），其余区域穿透。
    /// </summary>
    public static void SetClickThrough(Window w, bool on, params FrameworkElement[] interactive)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;

        // 移除该窗口上一次挂载的钩子
        if (Hooks.TryGetValue(h, out var prev))
        {
            var old = HwndSource.FromHwnd(h);
            old?.RemoveHook(prev);
            Hooks.Remove(h);
        }

        IntPtr ex = GetWindowLongPtr(h, GWL_EXSTYLE);
        if (on) ex = new IntPtr(ex.ToInt64() | WS_EX_TRANSPARENT);
        else ex = new IntPtr(ex.ToInt64() & ~WS_EX_TRANSPARENT);
        SetWindowLongPtr(h, GWL_EXSTYLE, ex);

        if (on && interactive.Length > 0)
        {
            var src = HwndSource.FromHwnd(h);
            if (src == null) return;
            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_NCHITTEST)
                {
                    // lParam 低位=物理 X，高位=物理 Y（屏幕物理像素）
                    int px = (short)(lParam.ToInt64() & 0xFFFF);
                    int py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                    System.Windows.Point devicePt = new System.Windows.Point(px, py);
                    System.Windows.Point logicalPt;
                    if (src.CompositionTarget != null)
                        logicalPt = src.CompositionTarget.TransformFromDevice.Transform(devicePt);
                    else
                        logicalPt = devicePt;
                    System.Windows.Point rel = w.PointFromScreen(logicalPt);

                    foreach (var el in interactive)
                    {
                        if (el == null) continue;
                        if (el.ActualWidth <= 0 || el.ActualHeight <= 0) continue;
                        var tb = el.TransformToVisual(w)
                                  .TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                        if (tb.Contains(rel))
                        {
                            // 关键：必须显式返回 HTCLIENT。
                            // 只把 handled 置 false 会让 DefWindowProc 因 WS_EX_TRANSPARENT
                            // 仍然返回 HTTRANSPARENT，导致该区域收不到鼠标消息（表现为不可点击）。
                            handled = true;
                            return new IntPtr(HTCLIENT);
                        }
                    }
                    handled = true;                   // 其余区域穿透到底层窗口
                    return new IntPtr(HTTRANSPARENT);
                }
                return IntPtr.Zero;
            };
            src.AddHook(hook);
            Hooks[h] = hook;
        }
    }

    /// <summary>
    /// 按锚点把窗口贴到屏幕对应边/角，并设置尺寸与透明度、置顶、穿透。
    /// clickThrough / opacity 为 null 时使用布局中的默认值；置顶显示态可传入覆盖值。
    /// </summary>
    public static void ApplyLayout(Window w, PanelLayout l, bool? clickThrough = null, double? opacity = null)
    {
        var sa = SystemParameters.WorkArea;
        double wW = l.FullWidth ? sa.Width : (l.Width > 0 ? l.Width : sa.Width);
        double wH = l.Height > 0 ? l.Height : sa.Height;

        double left, top;
        switch (l.Anchor)
        {
            case "Left":
                left = sa.Left; top = sa.Top + (sa.Height - wH) / 2; break;
            case "Right":
                left = sa.Right - wW; top = sa.Top + (sa.Height - wH) / 2; break;
            case "Top":
                left = sa.Left; top = sa.Top; wW = sa.Width; break;
            case "Bottom":
                left = sa.Left; top = sa.Bottom - wH; wW = sa.Width; break;
            default: // Center
                left = sa.Left + (sa.Width - wW) / 2;
                top = sa.Top + (sa.Height - wH) / 2; break;
        }

        w.Left = left;
        w.Top = top;
        w.Width = wW;
        w.Height = wH;
        w.Opacity = Math.Clamp(opacity ?? l.Opacity, 0.2, 1.0);
        w.Topmost = l.TopMost;
        // 鼠标穿透与可交互元素由各窗口在 ApplyLayout 后单独调用 SetClickThrough 设置
    }
}

/// <summary>
/// 自动靠边隐藏：隐藏时不再把窗口移出屏幕，而是<b>缩小成一条悬浮小条</b>（可拖动、位置可记忆），
/// 鼠标移到小条上时滑出还原为完整面板，离开后缩回小条。
/// enforceHide 谓词决定当前是否允许隐藏：AutoHide 常开，或“上课时间自动隐藏”且正在上课。
/// </summary>
public class AutoHider : IDisposable
{
    private readonly Window _w;
    private readonly PanelLayout _l;
    private readonly Func<bool> _enforceHide;
    private readonly Action<bool>? _onCompactChanged;
    private readonly DispatcherTimer _t;
    private Rect _shown, _hidden;
    private double _curLeft, _curTop, _curW, _curH;
    private bool _lastEnforce;
    private bool _compact;
    private const double Ease = 0.32;

    // 悬停延时展开：鼠标刚碰到小条时先不展开，避免“想拖动却立刻滑出大面板”
    private readonly DispatcherTimer _hover;
    private const int HoverDelayMs = 350;
    private bool _hovering;
    private bool _expanding;
    private bool _suppressUntilLeave;

    /// <summary>拖动等操作期间置为 true 可临时停止自动隐藏动画，避免操作时窗口缩回。</summary>
    public bool Suspended { get; set; }

    /// <summary>当前是否处于“缩小小条”状态。</summary>
    public bool IsCompact => _compact;

    /// <summary>小条当前矩形（屏幕坐标），供窗口做拖动定位与保存。</summary>
    public Rect DockRect => _hidden;

    public AutoHider(Window w, PanelLayout l, Func<bool> enforceHide, Action<bool>? onCompactChanged = null)
    {
        _w = w;
        _l = l;
        _enforceHide = enforceHide;
        _onCompactChanged = onCompactChanged;
        ComputeRects();
        _lastEnforce = _enforceHide();
        SetCur(_lastEnforce, snap: true);
        ApplyRect();
        SetCompact(_lastEnforce, fire: false);
        // 构造时就已处于隐藏态时，同步一次界面（否则窗口已缩小却仍显示完整面板内容）
        if (_compact) _onCompactChanged?.Invoke(true);

        _w.MouseEnter += OnEnter;
        _w.MouseLeave += OnLeave;
        _w.TouchEnter += OnEnter;
        _w.TouchLeave += OnLeave;

        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverDelayMs) };
        _hover.Tick += (_, _) => { _hover.Stop(); if (_hovering && !_suppressUntilLeave) _expanding = true; };

        _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _t.Tick += Tick;
        _t.Start();
    }

    private void OnEnter(object? sender, EventArgs e)
    {
        _hovering = true;
        _hover.Stop();
        _hover.Start();
    }

    private void OnLeave(object? sender, EventArgs e)
    {
        _hovering = false;
        _suppressUntilLeave = false;
        _expanding = false;
        _hover.Stop();
    }

    /// <summary>拖动小条前调用：取消正在计时的悬停展开。</summary>
    public void CancelHover()
    {
        _hover.Stop();
        _expanding = false;
    }

    /// <summary>拖动结束后调用：保持小条状态，等鼠标移出再移入才会重新展开。</summary>
    public void SuppressExpand()
    {
        _hover.Stop();
        _expanding = false;
        _suppressUntilLeave = true;
    }

    private void ComputeRects()
    {
        var sa = SystemParameters.WorkArea;
        double wW = _l.FullWidth ? sa.Width : (_l.Width > 0 ? _l.Width : sa.Width);
        double wH = _l.Height > 0 ? _l.Height : sa.Height;
        _w.Width = wW;
        _w.Height = wH;

        // 展开态位置（与 ApplyLayout 一致）
        switch (_l.Anchor)
        {
            case "Left":
                _shown = new Rect(sa.Left, sa.Top + (sa.Height - wH) / 2, wW, wH); break;
            case "Right":
                _shown = new Rect(sa.Right - wW, sa.Top + (sa.Height - wH) / 2, wW, wH); break;
            case "Top":
                _shown = new Rect(sa.Left, sa.Top, sa.Width, wH); break;
            case "Bottom":
                _shown = new Rect(sa.Left, sa.Bottom - wH, sa.Width, wH); break;
            default:
                _shown = new Rect(sa.Left + (sa.Width - wW) / 2, sa.Top + (sa.Height - wH) / 2, wW, wH); break;
        }

        // 缩小态：位置优先用用户拖动后记住的坐标，否则按锚点贴边
        double dw = _l.DockWidth > 0 ? _l.DockWidth : 96;
        double dh = _l.DockHeight > 0 ? _l.DockHeight : 36;
        double dx, dy;
        if (!double.IsNaN(_l.DockLeft) && !double.IsNaN(_l.DockTop))
        {
            dx = _l.DockLeft; dy = _l.DockTop;
        }
        else
        {
            switch (_l.Anchor)
            {
                case "Left": dx = sa.Left + 2; dy = sa.Top + (sa.Height - dh) / 2; break;
                case "Right": dx = sa.Right - dw - 2; dy = sa.Top + (sa.Height - dh) / 2; break;
                case "Top": dx = sa.Left + (sa.Width - dw) / 2; dy = sa.Top + 2; break;
                case "Bottom": dx = sa.Left + (sa.Width - dw) / 2; dy = sa.Bottom - dh - 2; break;
                default: dx = sa.Left + (sa.Width - dw) / 2; dy = sa.Top + (sa.Height - dh) / 2; break;
            }
        }
        _hidden = new Rect(ClampX(dx, dw), ClampY(dy, dh), dw, dh);
    }

    private static double ClampX(double x, double w)
    {
        var sa = SystemParameters.WorkArea;
        return Math.Min(Math.Max(x, sa.Left), sa.Right - w);
    }

    private static double ClampY(double y, double h)
    {
        var sa = SystemParameters.WorkArea;
        return Math.Min(Math.Max(y, sa.Top), sa.Bottom - h);
    }

    /// <summary>拖动小条后更新停留位置（立即生效，不做动画）。</summary>
    public void SetDockPosition(double left, double top)
    {
        double w = _hidden.Width, h = _hidden.Height;
        double x = ClampX(left, w), y = ClampY(top, h);
        _hidden = new Rect(x, y, w, h);
        _l.DockLeft = x;
        _l.DockTop = y;
        _curLeft = x; _curTop = y; _curW = w; _curH = h;
        ApplyRect();
    }

    /// <summary>忘记自定义位置，恢复到按锚点的默认贴边位置。</summary>
    public void ResetDockPosition()
    {
        _l.DockLeft = double.NaN;
        _l.DockTop = double.NaN;
        ComputeRects();
        if (_compact) SetCur(true, snap: true);
        ApplyRect();
    }

    private void SetCur(bool hidden, bool snap)
    {
        double tL = hidden ? _hidden.X : _shown.X;
        double tT = hidden ? _hidden.Y : _shown.Y;
        double tW = hidden ? _hidden.Width : _shown.Width;
        double tH = hidden ? _hidden.Height : _shown.Height;
        if (snap)
        {
            _curLeft = tL; _curTop = tT; _curW = tW; _curH = tH;
        }
    }

    private void ApplyRect()
    {
        _w.Left = _curLeft;
        _w.Top = _curTop;
        _w.Width = _curW;
        _w.Height = _curH;
    }

    private void SetCompact(bool value, bool fire)
    {
        if (_compact == value) return;
        _compact = value;
        if (fire) _onCompactChanged?.Invoke(value);
    }

    private void Tick(object? sender, EventArgs e)
    {
        // 拖动等操作期间暂停动画，避免操作过程中窗口缩回导致拖不动
        if (Suspended) return;

        bool enforce = _enforceHide();
        if (enforce != _lastEnforce)
        {
            _lastEnforce = enforce;
            if (enforce) { _curLeft = _shown.X; _curTop = _shown.Y; _curW = _shown.Width; _curH = _shown.Height; }
        }

        // 悬停延时到了才展开：刚放上去时保持小条，便于拖动它挪位置
        bool targetShown = enforce ? _expanding : true;
        SetCompact(!targetShown, fire: true);

        double tL = targetShown ? _shown.X : _hidden.X;
        double tT = targetShown ? _shown.Y : _hidden.Y;
        double tW = targetShown ? _shown.Width : _hidden.Width;
        double tH = targetShown ? _shown.Height : _hidden.Height;

        _curLeft += (tL - _curLeft) * Ease;
        _curTop += (tT - _curTop) * Ease;
        _curW += (tW - _curW) * Ease;
        _curH += (tH - _curH) * Ease;
        if (Math.Abs(tL - _curLeft) < 0.5) _curLeft = tL;
        if (Math.Abs(tT - _curTop) < 0.5) _curTop = tT;
        if (Math.Abs(tW - _curW) < 0.5) _curW = tW;
        if (Math.Abs(tH - _curH) < 0.5) _curH = tH;
        ApplyRect();
    }

    public void Dispose()
    {
        _t.Stop();
        _hover.Stop();
        _w.MouseEnter -= OnEnter;
        _w.MouseLeave -= OnLeave;
        _w.TouchEnter -= OnEnter;
        _w.TouchLeave -= OnLeave;
    }
}
