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
/// 自动靠边隐藏：窗口默认完全缩到屏幕外（Layout.EdgePeek &gt; 0 时保留一条细边），
/// 只有把鼠标 / 触摸移到屏幕对应边沿（Layout.EdgeTrigger 像素范围内）才平滑滑出，
/// 指针离开窗口且离开边沿后自动缩回。
/// 判定完全基于全局光标位置轮询（GetCursorPos），因此窗口即使整体移出屏幕也能被唤醒。
/// enforceHide 谓词决定当前是否应强制隐藏：AutoHide 常开，或「上课时间自动隐藏」且正在上课。
/// </summary>
public class AutoHider : IDisposable
{
    private readonly Window _w;
    private readonly PanelLayout _l;
    private readonly Func<bool> _enforceHide;
    private readonly DispatcherTimer _t;
    private Rect _shown, _hidden;
    private double _curLeft, _curTop;
    private bool _expanded;
    private bool _lastEnforce;
    private const double Ease = 0.35;
    private const int MaxPeek = 48;   // 露出条最多 48 像素，避免隐藏后仍占屏幕

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    /// <summary>拖动等操作期间置为 true 可临时停止自动隐藏，避免操作时窗口缩回。</summary>
    public bool Suspended { get; set; }

    public AutoHider(Window w, PanelLayout l, Func<bool> enforceHide)
    {
        _w = w; _l = l;
        _enforceHide = enforceHide;
        ComputeRects();
        _lastEnforce = _enforceHide();
        _expanded = !_lastEnforce;
        _curLeft = _expanded ? _shown.X : _hidden.X;
        _curTop = _expanded ? _shown.Y : _hidden.Y;
        _w.Left = _curLeft;
        _w.Top = _curTop;

        _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _t.Tick += Tick;
        _t.Start();
    }

    private void ComputeRects()
    {
        var sa = SystemParameters.WorkArea;
        double wW = _l.FullWidth ? sa.Width : (_l.Width > 0 ? _l.Width : sa.Width);
        double wH = _l.Height > 0 ? _l.Height : sa.Height;
        _w.Width = wW;
        _w.Height = wH;

        double peek = Math.Clamp(_l.EdgePeek, 0, MaxPeek);

        switch (_l.Anchor)
        {
            case "Left":
                _shown = new Rect(sa.Left, sa.Top + (sa.Height - wH) / 2, wW, wH);
                _hidden = new Rect(sa.Left - (wW - peek), _shown.Y, wW, wH);
                break;
            case "Right":
                _shown = new Rect(sa.Right - wW, sa.Top + (sa.Height - wH) / 2, wW, wH);
                _hidden = new Rect(sa.Right - peek, _shown.Y, wW, wH);
                break;
            case "Top":
                _shown = new Rect(sa.Left, sa.Top, sa.Width, wH);
                _hidden = new Rect(sa.Left, sa.Top - (wH - peek), sa.Width, wH);
                break;
            case "Bottom":
                _shown = new Rect(sa.Left, sa.Bottom - wH, sa.Width, wH);
                _hidden = new Rect(sa.Left, sa.Bottom - peek, sa.Width, wH);
                break;
            default:
                _shown = new Rect(sa.Left + (sa.Width - wW) / 2, sa.Top + (sa.Height - wH) / 2, wW, wH);
                _hidden = _shown;
                break;
        }
    }

    /// <summary>全局光标是否落在屏幕对应边沿的感应区内（物理像素比较，含触摸指针）。</summary>
    private bool CursorNearEdge()
    {
        try
        {
            if (!GetCursorPos(out var p)) return false;
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
            var b = scr.Bounds;
            double trig = Math.Max(2, _l.EdgeTrigger);
            return _l.Anchor switch
            {
                "Left" => p.X <= b.Left + trig,
                "Right" => p.X >= b.Right - 1 - trig,
                "Top" => p.Y <= b.Top + trig,
                "Bottom" => p.Y >= b.Bottom - 1 - trig,
                _ => false   // 居中锚点没有边沿可贴
            };
        }
        catch { return false; }
    }

    /// <summary>全局光标是否在当前窗口矩形内（窗口整体移出时自然为 false）。</summary>
    private bool CursorInWindow()
    {
        try
        {
            if (!GetCursorPos(out var p)) return false;
            var src = PresentationSource.FromVisual(_w);
            System.Windows.Point pt = new(p.X, p.Y);
            if (src?.CompositionTarget != null)
                pt = src.CompositionTarget.TransformFromDevice.Transform(pt);

            double w = _w.ActualWidth > 0 ? _w.ActualWidth : _w.Width;
            double h = _w.ActualHeight > 0 ? _w.ActualHeight : _w.Height;
            // 内缩 2px，避免在窗口边界上来回抖动
            var r = new Rect(_w.Left + 2, _w.Top + 2, Math.Max(0, w - 4), Math.Max(0, h - 4));
            return r.Contains(pt);
        }
        catch { return false; }
    }

    private void Tick(object? sender, EventArgs e)
    {
        // 拖动等操作期间暂停自动隐藏，避免操作过程中窗口缩回导致拖不动
        if (Suspended) return;

        bool enforce = _enforceHide();
        if (enforce != _lastEnforce)
        {
            _lastEnforce = enforce;
            _expanded = !enforce;
            if (enforce) { _curLeft = _hidden.X; _curTop = _hidden.Y; }
            else { _curLeft = _shown.X; _curTop = _shown.Y; }
        }

        if (enforce)
        {
            bool edge = CursorNearEdge();
            bool over = CursorInWindow();
            // 带滞回的状态机：隐藏态只能被「屏幕边沿感应」唤醒，
            // 展开态则只要指针还在窗口内或边沿区内就保持，避免细边处来回抖动。
            if (_expanded)
            {
                if (!edge && !over) _expanded = false;
            }
            else if (edge || over)
            {
                _expanded = true;
            }
        }
        else
        {
            _expanded = true;
        }

        double tLeft = _expanded ? _shown.X : _hidden.X;
        double tTop = _expanded ? _shown.Y : _hidden.Y;
        _curLeft += (tLeft - _curLeft) * Ease;
        _curTop += (tTop - _curTop) * Ease;
        if (Math.Abs(tLeft - _curLeft) < 0.5) _curLeft = tLeft;
        if (Math.Abs(tTop - _curTop) < 0.5) _curTop = tTop;
        _w.Left = _curLeft;
        _w.Top = _curTop;
    }

    public void Dispose() => _t.Stop();
}
