using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace ClassBulletinApp;

/// <summary>
/// 无边框悬浮窗的「所见即所得」边框辅助：
/// 1) 边沿 / 四角 10px 内返回原生命中（HTLEFT 等），拖动即可改变窗口大小（需窗口 ResizeMode=CanResize）；
/// 2) 按住指定区域（标题条 / 通知条）直接拖动窗口，位移超过 6px 才真正开拖，单击 / 双击都不受影响，按钮除外；
/// 3) 用户手势开始 / 结束（WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE）回调：暂停自动隐藏，并在松手时落盘位置与大小
///    ——程序化改尺寸不会触发，避免把自动布局 / 自动隐藏的坐标误存成用户位置。
/// </summary>
public static class FloatChrome
{
    private const int WM_NCHITTEST = 0x84;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>拉边改大小的感应宽度（逻辑像素）。</summary>
    public const double EdgeMargin = 10;

    private sealed record Options(Func<bool>? CanInteract, Action? Start, Action? End);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    /// <summary>
    /// 开启窗口边沿缩放 + 用户手势回调。
    /// </summary>
    /// <param name="canInteract">返回 false 时整个窗口不响应（例如开启了鼠标穿透）。</param>
    /// <param name="onGestureStart">用户开始拖动 / 缩放时回调（暂停自动隐藏）。</param>
    /// <param name="onGestureEnd">用户松手时回调（落盘位置与大小、重建自动隐藏矩形）。</param>
    public static void Enable(Window w, Func<bool>? canInteract = null,
        Action? onGestureStart = null, Action? onGestureEnd = null)
    {
        var opt = new Options(canInteract, onGestureStart, onGestureEnd);
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Attach(w, opt);
        else w.SourceInitialized += (_, _) => Attach(w, opt);

    }

    // ---------- 诊断日志：CBP_FLOAT_DEBUG=1 时把命中测试 / 手势消息写进临时目录，便于排查拉边 / 拖动问题 ----------
    private static readonly bool _dbg = Environment.GetEnvironmentVariable("CBP_FLOAT_DEBUG") == "1";

    private static void Log(string text)
    {
        if (!_dbg) return;
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClassBulletinApp.float.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\n");
        }
        catch { }
    }

    private static void Attach(Window w, Options o)
    {
        if (PresentationSource.FromVisual(w) is not HwndSource src)
        {
            Log($"ATTACH FAIL [{w.Title}] no HwndSource");
            return;
        }
        Log($"ATTACH OK [{w.Title}]");
        src.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == WM_ENTERSIZEMOVE)
            {
                Log($"[{w.Title}] ENTERSIZEMOVE");
                try { o.Start?.Invoke(); } catch { }
                return IntPtr.Zero;
            }
            if (msg == WM_EXITSIZEMOVE)
            {
                Log($"[{w.Title}] EXITSIZEMOVE size={w.ActualWidth:0.#}x{w.ActualHeight:0.#} pos={w.Left:0.#},{w.Top:0.#}");
                try { o.End?.Invoke(); } catch { }
                return IntPtr.Zero;
            }
            if (msg != WM_NCHITTEST) return IntPtr.Zero;
            if (o.CanInteract != null && !o.CanInteract()) return IntPtr.Zero;

            // lParam：低 16 位 = 物理 X，高 16 位 = 物理 Y；PointFromScreen 内部按 DPI 换成逻辑像素
            long v = lp.ToInt64();
            int px = (short)(v & 0xFFFF);
            int py = (short)((v >> 16) & 0xFFFF);
            var p = w.PointFromScreen(new Point(px, py));
            double ww = w.ActualWidth, hh = w.ActualHeight;
            if (ww <= 0 || hh <= 0) return IntPtr.Zero;

            bool left = p.X <= EdgeMargin;
            bool right = p.X >= ww - EdgeMargin;
            bool top = p.Y <= EdgeMargin;
            bool bottom = p.Y >= hh - EdgeMargin;

            int code = 0;
            if (top && left) code = HTTOPLEFT;
            else if (top && right) code = HTTOPRIGHT;
            else if (bottom && left) code = HTBOTTOMLEFT;
            else if (bottom && right) code = HTBOTTOMRIGHT;
            else if (left) code = HTLEFT;
            else if (right) code = HTRIGHT;
            else if (top) code = HTTOP;
            else if (bottom) code = HTBOTTOM;

            if (code == 0) return IntPtr.Zero;
            Log($"[{w.Title}] HITTEST code={code} p={p.X:0.#},{p.Y:0.#} size={ww:0.#}x{hh:0.#}");
            handled = true;
            return (IntPtr)code;
        });
    }

    /// <summary>
    /// 按住 area 直接拖动窗口（按全局光标位移移动窗口，不依赖系统移动循环，任何无边框窗口都稳定）。
    /// 位移超过 6px 才开始拖，因此单击 / 双击（含双击换班、双击换日）都不受影响；按在按钮上不会拖。
    /// </summary>
    public static void EnableDrag(FrameworkElement area, Window w,
        Action? onDragStart = null, Action? onDragEnd = null, Func<bool>? canDrag = null)
    {
        bool pressed = false;
        bool dragging = false;
        double startX = 0, startY = 0;     // 全局光标（物理像素）
        double startLeft = 0, startTop = 0;

        void End(bool notify)
        {
            // 先清状态再释放捕获：释放捕获会触发 LostMouseCapture 并再次进入本方法，
            // 若在释放之后才读 dragging，就会误判为「不是拖动」而丢掉 onDragEnd 回调。
            bool was = dragging;
            dragging = false;
            pressed = false;
            Mouse.OverrideCursor = null;
            if (was && Mouse.Captured == area) Mouse.Capture(null);
            if (was && notify) onDragEnd?.Invoke();
        }

        area.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (canDrag != null && !canDrag()) return;
            if (IsOnButton(e.OriginalSource as DependencyObject)) return;   // 按钮自己处理点击
            if (!GetCursorPos(out var cp)) return;
            pressed = true;
            dragging = false;
            startX = cp.X; startY = cp.Y;
            startLeft = w.Left; startTop = w.Top;
        };

        area.PreviewMouseMove += (_, e) =>
        {
            if (!pressed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { End(true); return; }
            if (!GetCursorPos(out var cp)) return;

            double dx = cp.X - startX, dy = cp.Y - startY;
            if (!dragging)
            {
                if (Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;
                dragging = true;
                Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
                Mouse.Capture(area);
                onDragStart?.Invoke();
            }

            double k = DpiScale(w);
            w.Left = startLeft + dx / k;
            w.Top = startTop + dy / k;
            e.Handled = true;
        };

        area.PreviewMouseLeftButtonUp += (_, _) => End(true);
        area.LostMouseCapture += (_, _) => End(false);   // 捕获被抢走（Alt+Tab 等）就静默结束
    }

    private static double DpiScale(Visual v)
    {
        try
        {
            var d = VisualTreeHelper.GetDpi(v);
            return d.DpiScaleX <= 0 ? 1.0 : d.DpiScaleX;
        }
        catch { return 1.0; }
    }

    /// <summary>命中的元素是否属于按钮（按钮的点击不应变成拖窗口）。</summary>
    private static bool IsOnButton(DependencyObject? dep)
    {
        while (dep != null)
        {
            if (dep is System.Windows.Controls.Primitives.ButtonBase) return true;
            dep = dep is Visual ? VisualTreeHelper.GetParent(dep) : LogicalTreeHelper.GetParent(dep);
        }
        return false;
    }
}
