using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClassBulletinApp;

/// <summary>
/// 桌面壁纸设置助手：把生成的作业公示图片设为桌面壁纸（置于桌面背景），并支持恢复。
/// </summary>
public static class WallpaperHelper
{
    private const int SPI_SETDESKWALLPAPER = 20;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDWININI = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string? lpvParam, int fuWinIni);

    /// <summary>读取当前系统壁纸路径（用于清除时恢复）。</summary>
    public static string GetCurrentWallpaper()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            return key?.GetValue("Wallpaper") as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>把指定图片设为桌面壁纸（Fill 填充）。</summary>
    public static void SetWallpaper(string imagePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            key?.SetValue("WallpaperStyle", "10");   // 10 = Fill
            key?.SetValue("TileWallpaper", "0");
        }
        catch { }
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDWININI);
    }
}
