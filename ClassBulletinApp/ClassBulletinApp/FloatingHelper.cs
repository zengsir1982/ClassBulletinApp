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
    /// 布局里有自由坐标（用户拖动过）时直接用该坐标，不再按锚点贴边。
    /// </summary>
    public static void ApplyLayout(Window w, PanelLayout l, bool? clickThrough = null, double? opacity = null)
    {
        var sa = SystemParameters.WorkArea;
        double wW = l.Width > 0 ? l.Width : sa.Width;
        double wH = l.Height > 0 ? l.Height : sa.Height;

        // 尺寸先收进当前屏幕（防止历史存档里的超大尺寸把窗口撑出屏幕）
        var area = w.IsLoaded ? WorkAreaDip(w) : sa;
        wW = Math.Clamp(wW, 120, Math.Max(120, area.Width));
        wH = Math.Clamp(wH, 60, Math.Max(60, area.Height));

        if (l.HasFreePosition)
        {
            var r = new Rect(l.Left!.Value, l.Top!.Value, wW, wH);
            r = ClampRectDip(w, r);
            w.Left = r.X; w.Top = r.Y; w.Width = r.Width; w.Height = r.Height;
        }
        else
        {
            double left, top;
            switch (l.Anchor)
            {
                case "Left":
                    left = sa.Left; top = sa.Top + (sa.Height - wH) / 2; break;
                case "Right":
                    left = sa.Right - wW; top = sa.Top + (sa.Height - wH) / 2; break;
                case "Top":
                    top = sa.Top;
                    left = HLeft(l, sa, wW); break;
                case "Bottom":
                    top = sa.Bottom - wH;
                    left = HLeft(l, sa, wW); break;
                default: // Center
                    left = sa.Left + (sa.Width - wW) / 2;
                    top = sa.Top + (sa.Height - wH) / 2; break;
            }
            w.Left = left;
            w.Top = top;
            w.Width = wW;
            w.Height = wH;
        }

        w.Opacity = Math.Clamp(opacity ?? l.Opacity, 0.2, 1.0);
        w.Topmost = l.TopMost;
        // 鼠标穿透与可交互元素由各窗口在 ApplyLayout 后单独调用 SetClickThrough 设置
    }

    // ---------- DPI 感知的屏幕工作区 / 收屏 / 居中 ----------
    // 关键：WinForms 的 Screen.WorkingArea 是「物理像素」，而 WPF 的 Left/Top/Width 是「逻辑像素」。
    // 125% / 150% 缩放下两者差一个缩放系数，混用会把窗口算到屏幕外（便签「开启后不出现」的根因）。

    /// <summary>窗口当前所在显示器的 DPI 缩放系数（1.0 = 96 DPI）。</summary>
    private static double DpiScale(Visual v)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(v);
            return dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        }
        catch { return 1.0; }
    }

    /// <summary>窗口所在屏幕的工作区（逻辑像素）。</summary>
    public static Rect WorkAreaDip(Window w)
    {
        try
        {
            double k = DpiScale(w);
            double dl = double.IsNaN(w.Left) ? 0 : w.Left;
            double dt = double.IsNaN(w.Top) ? 0 : w.Top;
            double dw = w.Width > 0 && !double.IsNaN(w.Width) ? w.Width : 400;
            double dh = w.Height > 0 && !double.IsNaN(w.Height) ? w.Height : 400;
            var phys = new System.Drawing.Rectangle(
                (int)Math.Round(dl * k), (int)Math.Round(dt * k),
                (int)Math.Round(dw * k), (int)Math.Round(dh * k));
            var scr = System.Windows.Forms.Screen.FromRectangle(phys)
                      ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (scr == null) return SystemParameters.WorkArea;
            var a = scr.WorkingArea;
            return new Rect(a.Left / k, a.Top / k, a.Width / k, a.Height / k);
        }
        catch { return SystemParameters.WorkArea; }
    }

    /// <summary>把矩形完整收进窗口所在屏幕的工作区（逻辑像素，四边都拦）。</summary>
    public static Rect ClampRectDip(Window w, Rect r)
    {
        try
        {
            var a = WorkAreaDip(w);
            double width = Math.Min(r.Width, a.Width);
            double height = Math.Min(r.Height, a.Height);
            double x = Math.Clamp(r.X, a.Left, Math.Max(a.Left, a.Right - width));
            double y = Math.Clamp(r.Y, a.Top, Math.Max(a.Top, a.Bottom - height));
            return new Rect(x, y, width, height);
        }
        catch { return r; }
    }

    /// <summary>
    /// 窗口当前是否大部分（默认 55%）落在所在屏幕工作区内。
    /// 用于判断「存档坐标是否还有效」以及「此刻的窗口位置是否值得保存」（自动隐藏态窗口在屏外，不该存）。
    /// </summary>
    public static bool IsMostlyOnScreen(Window w, double minRatio = 0.55)
    {
        try
        {
            if (double.IsNaN(w.Left) || double.IsNaN(w.Top) || w.Width <= 0 || w.Height <= 0) return false;
            var area = WorkAreaDip(w);
            var r = new Rect(w.Left, w.Top, w.Width, w.Height);
            var inter = Rect.Intersect(r, area);
            if (inter.IsEmpty || inter.Width <= 0 || inter.Height <= 0) return false;
            return inter.Width * inter.Height >= Math.Max(1, r.Width * r.Height) * minRatio;
        }
        catch { return false; }
    }

    /// <summary>「拖到屏幕边沿即开启自动隐藏」的判定阈值（逻辑像素）：窗口任一条边离屏幕工作区对应边沿 ≤ 此值即视为贴边。</summary>
    public const double EdgeAutoHideThreshold = 28;

    /// <summary>
    /// 窗口是否贴近某条屏幕边（逻辑像素）。用于「拖动到屏幕边沿自动开启自动隐藏」：
    /// 贴边=开，离边=关。取参与判定的各边间隙最小值与阈值比较。
    /// <para>某个方向必须是「还有足够活动空间」才参与判定，否则会误判：
    /// ① 顶部通知条横向铺满，左右间隙恒为 0；② 课表悬浮窗很高（几近满屏高），
    /// 纵向怎么放都离上下边很近。这两种情况若仍四边取最小，就会永远判为贴边，
    /// 「拖离边沿自动关闭」就失效。故该方向剩余空间不足（≤ 3×阈值）时忽略该方向。</para>
    /// </summary>
    public static bool IsNearEdge(Window w, double threshold = EdgeAutoHideThreshold)
    {
        try
        {
            if (double.IsNaN(w.Left) || double.IsNaN(w.Top) || w.Width <= 0 || w.Height <= 0) return false;
            var a = WorkAreaDip(w);
            double dl = w.Left - a.Left;
            double dr = a.Right - (w.Left + w.Width);
            double dt = w.Top - a.Top;
            double db = a.Bottom - (w.Top + w.Height);

            // 该方向还能自由活动（剩余空间 > 3 倍阈值）时，才拿这一方向的距离来判定
            bool useH = (a.Width - w.Width) > threshold * 3;
            bool useV = (a.Height - w.Height) > threshold * 3;

            double min;
            if (!useH && !useV) min = 0;                        // 几乎铺满整屏：始终算贴边
            else if (!useH) min = Math.Min(dt, db);             // 只能上下动：只看上下
            else if (!useV) min = Math.Min(dl, dr);             // 只能左右动：只看左右
            else min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            return min <= threshold;
        }
        catch { return false; }
    }

    /// <summary>把已显示的窗口收回屏幕内（尺寸不超过屏幕）。</summary>
    public static void ClampToScreen(Window w)
    {
        try
        {
            var r = ClampRectDip(w, new Rect(w.Left, w.Top, w.Width, w.Height));
            if (Math.Abs(r.X - w.Left) > 0.5) w.Left = r.X;
            if (Math.Abs(r.Y - w.Top) > 0.5) w.Top = r.Y;
            if (r.Width < w.Width) w.Width = r.Width;
            if (r.Height < w.Height) w.Height = r.Height;
        }
        catch { }
    }

    /// <summary>把窗口摆到所在屏幕（无窗口位置时用主屏）的正中央。</summary>
    public static void CenterOnScreen(Window w)
    {
        try
        {
            var a = WorkAreaDip(w);
            w.Left = a.Left + Math.Max(0, (a.Width - w.Width) / 2);
            w.Top = a.Top + Math.Max(0, (a.Height - w.Height) / 2);
        }
        catch { }
    }

    /// <summary>窗口离哪条屏幕边最近（供自动隐藏选择滑出方向）。</summary>
    public static string NearestAnchor(Window w)
    {
        try
        {
            var a = WorkAreaDip(w);
            double cx = w.Left + w.Width / 2, cy = w.Top + w.Height / 2;
            double dl = cx - a.Left, dr = a.Right - cx, dt = cy - a.Top, db = a.Bottom - cy;
            double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            if (min == dl) return "Left";
            if (min == dr) return "Right";
            if (min == dt) return "Top";
            return "Bottom";
        }
        catch { return "Right"; }
    }

    /// <summary>把窗口提到最前但不改变用户的「置顶」偏好（先置顶再还原）。</summary>
    public static void BringToFront(Window w)
    {
        try
        {
            bool wasTop = w.Topmost;
            w.Topmost = true;
            w.Topmost = wasTop;
            w.Activate();
        }
        catch { }
    }

    /// <summary>顶部 / 底部通知条的水平左坐标（按 HAnchor 居左 / 居中 / 居右）。</summary>
    internal static double HLeft(PanelLayout l, Rect sa, double wW)
    {
        return l.HAnchor switch
        {
            "Left" => sa.Left,
            "Right" => sa.Right - wW,
            _ => sa.Left + (sa.Width - wW) / 2
        };
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
        double wW = _l.Width > 0 ? _l.Width : sa.Width;
        double wH = _l.Height > 0 ? _l.Height : sa.Height;

        // 工作区按「窗口所在屏幕 + DPI 换算后的逻辑像素」取，避免 125% 缩放下算到屏幕外
        var area = FloatingHelper.WorkAreaDip(_w);
        wW = Math.Clamp(wW, 120, Math.Max(120, area.Width));
        wH = Math.Clamp(wH, 60, Math.Max(60, area.Height));
        _w.Width = wW;
        _w.Height = wH;

        double peek = Math.Clamp(_l.EdgePeek, 0, MaxPeek);
        double hLeft = FloatingHelper.HLeft(_l, sa, wW);

        // 展开位置：拖动过就用自由坐标，否则按锚点贴边
        if (_l.HasFreePosition)
        {
            _shown = FloatingHelper.ClampRectDip(_w, new Rect(_l.Left!.Value, _l.Top!.Value, wW, wH));
        }
        else
        {
            _shown = _l.Anchor switch
            {
                "Left" => new Rect(area.Left, area.Top + (area.Height - wH) / 2, wW, wH),
                "Right" => new Rect(area.Right - wW, area.Top + (area.Height - wH) / 2, wW, wH),
                "Top" => new Rect(hLeft, area.Top, wW, wH),
                "Bottom" => new Rect(hLeft, area.Bottom - wH, wW, wH),
                _ => new Rect(area.Left + (area.Width - wW) / 2, area.Top + (area.Height - wH) / 2, wW, wH)
            };
        }

        _hidden = _l.Anchor switch
        {
            "Left" => new Rect(area.Left - (wW - peek), _shown.Y, wW, wH),
            "Right" => new Rect(area.Right - peek, _shown.Y, wW, wH),
            "Top" => new Rect(_shown.X, area.Top - (wH - peek), wW, wH),
            "Bottom" => new Rect(_shown.X, area.Bottom - peek, wW, wH),
            _ => _shown
        };
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
