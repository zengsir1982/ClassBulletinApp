using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassBulletinApp;

/// <summary>
/// 桌面窗口探测：判断当前是否存在“最大化”的顶层可见窗口（用于“桌面有窗口最大化时自动隐藏悬浮窗”）。
/// 排除本程序自身进程、工具窗口（WS_EX_TOOLWINDOW）与不可见/最小化窗口。
/// </summary>
public static class WinDetect
{
    private const int SW_MAXIMIZE = 3;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

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

    /// <summary>当前是否存在最大化且可见的顶层窗口（排除本程序与工具窗口）。</summary>
    public static bool IsMaximizedWindowPresent()
    {
        try
        {
            int myPid = Process.GetCurrentProcess().Id;
            bool found = false;
            EnumWindows((hWnd, _) =>
            {
                if (found) return true;
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0 || pid == (uint)myPid) return true;   // 排除自身

                // 排除工具窗口（如悬浮提示、托盘类窗口）
                if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(hWnd, ref wp)) return true;
                if (wp.showCmd == SW_MAXIMIZE)
                {
                    found = true;
                    return false;   // 找到一个即可停止
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        catch
        {
            return false;
        }
    }
}
