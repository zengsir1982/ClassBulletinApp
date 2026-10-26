using System;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>开启/关闭鼠标穿透：开启后窗口不接收鼠标，点击落到底层窗口（纯展示）。</summary>
    public static void SetClickThrough(Window w, bool on)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        IntPtr ex = GetWindowLongPtr(h, GWL_EXSTYLE);
        if (on) ex = new IntPtr(ex.ToInt64() | WS_EX_TRANSPARENT);
        else ex = new IntPtr(ex.ToInt64() & ~WS_EX_TRANSPARENT);
        SetWindowLongPtr(h, GWL_EXSTYLE, ex);
    }

    /// <summary>按锚点把窗口贴到屏幕对应边/角，并设置尺寸与透明度、置顶。</summary>
    public static void ApplyLayout(Window w, PanelLayout l)
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
        w.Opacity = Math.Clamp(l.Opacity, 0.2, 1.0);
        w.Topmost = l.TopMost;
        SetClickThrough(w, l.ClickThrough);
    }
}

/// <summary>
/// 自动靠边隐藏：窗口停靠在屏幕边缘，只有把鼠标/触摸移到屏幕边沿（露出的细条）才滑出，
/// 离开后平滑缩回成一条细边。采用事件驱动（MouseEnter/Leave + TouchEnter/Leave），
/// 因此“滑出”必然发生在指针真正落在边沿细条上，契合“移动到屏幕边沿才滑出”的需求，
/// 且天然兼容触摸，不受 DPI 坐标换算影响。
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
    private const double Sliver = 20;   // 缩回时留在屏幕边沿的细条宽度（也是触发滑出的热区）
    private const double Ease = 0.3;    // 滑入滑出缓动系数

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
        // 强制状态切换时，把当前位置重置到目标起点，避免从错误位置滑出
        if (enforce != _lastEnforce)
        {
            _lastEnforce = enforce;
            _curLeft = enforce ? _hidden.X : _shown.X;
            _curTop = enforce ? _hidden.Y : _shown.Y;
        }

        // 强制隐藏时：指针落在边沿细条(pointerOver)才滑出，否则缩回边沿；
        // 非强制时（如“上课时间自动隐藏”但当前是课间）：始终完整显示。
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
