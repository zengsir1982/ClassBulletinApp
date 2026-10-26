using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassBulletinApp;

/// <summary>
/// 前台窗口探测：判断当前前台是否为文档 / 演示类程序（用于“有文档打开时自动隐藏悬浮窗”）。
/// 仅用进程名判定，天然排除本程序自身，避免误隐藏。
/// </summary>
public static class DocDetect
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // 文档 / 演示 / PDF / 常见编辑器进程名（不含浏览器，避免正常上网时误隐藏）
    private static readonly HashSet<string> DocProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "POWERPNT", "EXCEL", "ONENOTE", "OUTLOOK", "MSACCESS",
        "WPS", "WPP", "ET", "WPSOFFICE",
        "AcroRd32", "Acrobat", "AcroPDF", "FoxitReader", "FoxitPDFReader",
        "SumatraPDF", "PDFXCview", "Notepad", "notepad++", "wordpad", "WINWORDC"
    };

    /// <summary>当前前台是否为文档/演示程序（且非本程序）。</summary>
    public static bool IsDocumentForeground()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;

            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return false;

            int myPid = Process.GetCurrentProcess().Id;
            if (pid == (uint)myPid) return false;   // 本程序（控制面板/悬浮窗）前台 → 不隐藏

            using var proc = Process.GetProcessById((int)pid);
            return DocProcesses.Contains(proc.ProcessName);
        }
        catch
        {
            return false;
        }
    }
}
