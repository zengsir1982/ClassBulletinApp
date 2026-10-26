using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassBulletinApp;

/// <summary>
/// 桌面窗口探测：
/// 1) 是否存在“最大化”的顶层可见窗口（桌面有窗口最大化 → 隐藏悬浮窗）；
/// 2) 是否存在“全屏”窗口（课件播放 / 文档放映 / 视频全屏等，窗口铺满整屏 → 隐藏悬浮窗）。
/// 均排除本程序自身进程、工具窗口与不可见 / 最小化窗口。
/// </summary>
public static class WinDetect
{
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 2;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private const uint GW_OWNER = 4;
    private const int WS_EX_TOPMOST = 0x00000008;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public int ptMinPosition_x;
        public int ptMinPosition_y;
        public int ptMaxPosition_x;
        public int ptMaxPosition_y;
        public int rcNormalPosition_left;
        public int rcNormalPosition_top;
        public int rcNormalPosition_right;
        public int rcNormalPosition_bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>外壳 / 系统窗口类名，不应作为“用户全屏窗口”。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "DV2ControlHost", "BaseBar", "ReBarWindow32", "TrayNotifyWnd", "NotifyIconOverflowWindow",
        // UWP 核心窗口（后台常驻时矩形仍铺满整屏，但并非在放映），不应算作全屏演示
        "Windows.UI.Core.CoreWindow", "Windows.UI.Composition.DesktopWindowSiteBridge"
    };

    /// <summary>是否存在最大化的顶层可见窗口（排除自身与工具窗口）。</summary>
    public static bool IsMaximizedWindowPresent() => Scan().Maximized;

    /// <summary>是否存在铺满整个屏幕的顶层可见窗口（课件放映 / 全屏视频等）。</summary>
    public static bool IsFullscreenWindowPresent() => Scan().Fullscreen;

    /// <summary>
    /// 按开关一次性扫描：返回是否应自动隐藏悬浮窗（全屏优先，其次最大化）。
    /// 两个条件合并为一次 EnumWindows，开销更小。
    /// </summary>
    public static bool ShouldAutoHide(bool onFullscreen, bool onMaximized)
    {
        if (!onFullscreen && !onMaximized) return false;
        var r = Scan();
        return (onFullscreen && r.Fullscreen) || (onMaximized && r.Maximized);
    }

    private static (bool Fullscreen, bool Maximized) Scan()
    {
        try
        {
            int myPid = Process.GetCurrentProcess().Id;
            bool full = false, max = false;
            IntPtr foreground = GetForegroundWindow();

            var screens = new List<System.Drawing.Rectangle>();
            try
            {
                foreach (var sc in System.Windows.Forms.Screen.AllScreens)
                    screens.Add(sc.Bounds);
            }
            catch
            {
                screens.Add(new System.Drawing.Rectangle(0, 0,
                    System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 0,
                    System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 0));
            }

            EnumWindows((hWnd, _) =>
            {
                if (full && max) return false;
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == 0 || pid == (uint)myPid) return true;      // 排除自身

                    int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
                    if ((ex & WS_EX_TOOLWINDOW) != 0) return true;        // 工具窗口
                    if ((GetWindowLong(hWnd, GWL_STYLE) & WS_CHILD) != 0) return true;

                    // 排除任务栏 / 桌面等外壳窗口
                    var cn = new System.Text.StringBuilder(256);
                    GetClassName(hWnd, cn, cn.Capacity);
                    if (ShellClasses.Contains(cn.ToString())) return true;

                    var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (GetWindowPlacement(hWnd, ref wp) && wp.showCmd == SW_MINIMIZE) return true;

                    if (!max && GetWindowPlacement(hWnd, ref wp) && wp.showCmd == SW_MAXIMIZE) max = true;

                    // 全屏判定：窗口矩形铺满任一屏幕（容差 6 像素，兼容边框 rounding）
                    if (!full && GetWindowRect(hWnd, out var rc) && rc.Width > 0 && rc.Height > 0)
                    {
                        foreach (var sc in screens)
                        {
                            if (sc.Width <= 0 || sc.Height <= 0) continue;
                            if (rc.Left <= sc.X + 6 && rc.Top <= sc.Y + 6 &&
                                rc.Right >= sc.Right - 6 && rc.Bottom >= sc.Bottom - 6)
                            {
                                // 关键：必须是真正在放映——要么它是前台窗口（或其进程是前台进程），
                                // 要么它设置了始终置顶。否则后台常驻却铺满整屏的窗口会被误判为全屏演示。
                                GetWindowThreadProcessId(foreground, out uint fgPid);
                                bool isForegroundWindow = hWnd == foreground || pid == fgPid;
                                bool isAlwaysOnTop = (ex & WS_EX_TOPMOST) != 0;
                                if (isForegroundWindow || isAlwaysOnTop) full = true;
                                break;
                            }
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            return (full, max);
        }
        catch
        {
            return (false, false);
        }
    }
}
