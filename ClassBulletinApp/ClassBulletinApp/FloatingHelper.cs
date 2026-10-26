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
                            handled = false;          // 交还 WPF 正常命中测试 → 元素可点击
                            return IntPtr.Zero;
                        }
                    }
                    handled = true;                   // 其余区域穿透
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
/// 自动靠边隐藏：窗口停靠在屏幕边缘，只有把鼠标/触摸移到屏幕边沿（露出的细条）才滑出，
/// 离开后平滑缩回成一条细边。采用事件驱动（MouseEnter/Leave + TouchEnter/Leave）。
/// enforceHide 谓词决定当前是否应强制隐藏：AutoHide 常开，或“上课时间自动隐藏”且正在上课。
/// </summary>
public class AutoHider : IDisposable
{
    private readonly Window _w;
    private readonly PanelLayout _l;
    private readonly Func<bool> _enforceHide;
    private readonly DispatcherTimer _t;
    private Rect _shown, _hidden;
    private double _curLeft, _curTop;
    private bool _pointerOver;
    private bool _lastEnforce;
    private const double Sliver = 20;
    private const double Ease = 0.3;

    public AutoHider(Window w, PanelLayout l, Func<bool> enforceHide)
    {
        _w = w;
        _l = l;
        _enforceHide = enforceHide;
        ComputeRects();
        _lastEnforce = _enforceHide();
        _curLeft = _lastEnforce ? _hidden.X : _shown.X;
        _curTop = _lastEnforce ? _hidden.Y : _shown.Y;
        _w.Left = _curLeft;
        _w.Top = _curTop;

        _w.MouseEnter += OnEnter;
        _w.MouseLeave += OnLeave;
        _w.TouchEnter += OnEnter;
        _w.TouchLeave += OnLeave;

        _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _t.Tick += Tick;
        _t.Start();
    }

    private void OnEnter(object? sender, EventArgs e) => _pointerOver = true;
    private void OnLeave(object? sender, EventArgs e) => _pointerOver = false;

    private void ComputeRects()
    {
        var sa = SystemParameters.WorkArea;
        double wW = _l.FullWidth ? sa.Width : (_l.Width > 0 ? _l.Width : sa.Width);
        double wH = _l.Height > 0 ? _l.Height : sa.Height;
        _w.Width = wW;
        _w.Height = wH;

        switch (_l.Anchor)
        {
            case "Left":
                _shown = new Rect(sa.Left, sa.Top + (sa.Height - wH) / 2, wW, wH);
                _hidden = new Rect(sa.Left - (wW - Sliver), _shown.Y, wW, wH);
                break;
            case "Right":
                _shown = new Rect(sa.Right - wW, sa.Top + (sa.Height - wH) / 2, wW, wH);
                _hidden = new Rect(sa.Right - Sliver, _shown.Y, wW, wH);
                break;
            case "Top":
                _shown = new Rect(sa.Left, sa.Top, sa.Width, wH);
                _hidden = new Rect(sa.Left, sa.Top - (wH - Sliver), sa.Width, wH);
                break;
            case "Bottom":
                _shown = new Rect(sa.Left, sa.Bottom - wH, sa.Width, wH);
                _hidden = new Rect(sa.Left, sa.Bottom - Sliver, sa.Width, wH);
                break;
            default:
                _shown = new Rect(sa.Left + (sa.Width - wW) / 2, sa.Top + (sa.Height - wH) / 2, wW, wH);
                _hidden = _shown;
                break;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        bool enforce = _enforceHide();
        if (enforce != _lastEnforce)
        {
            _lastEnforce = enforce;
            _curLeft = enforce ? _hidden.X : _shown.X;
            _curTop = enforce ? _hidden.Y : _shown.Y;
        }

        bool targetShown = enforce ? _pointerOver : true;
        double tLeft = targetShown ? _shown.X : _hidden.X;
        double tTop = targetShown ? _shown.Y : _hidden.Y;
        _curLeft += (tLeft - _curLeft) * Ease;
        _curTop += (tTop - _curTop) * Ease;
        if (Math.Abs(tLeft - _curLeft) < 0.5) _curLeft = tLeft;
        if (Math.Abs(tTop - _curTop) < 0.5) _curTop = tTop;
        _w.Left = _curLeft;
        _w.Top = _curTop;
    }

    public void Dispose()
    {
        _t.Stop();
        _w.MouseEnter -= OnEnter;
        _w.MouseLeave -= OnLeave;
        _w.TouchEnter -= OnEnter;
        _w.TouchLeave -= OnLeave;
    }
}
