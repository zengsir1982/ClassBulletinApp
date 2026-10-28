using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassBulletinApp;

/// <summary>
/// 把快捷方式路径（.exe / .lnk / 文件夹）转换为系统关联的小图标（ImageSource）。
/// 文件与文件夹均通过 SHGetFileInfo 取系统图标，失败时回退到应用程序默认图标。
/// </summary>
public class QlIconConverter : System.Windows.Data.IValueConverter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo? culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return null!;
        try
        {
            var info = new SHFILEINFO();
            var h = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON);
            if (h != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                using var icon = Icon.FromHandle(info.hIcon);
                using var bmp = icon.ToBitmap();
                var hbmp = bmp.GetHbitmap();
                try
                {
                    return Imaging.CreateBitmapSourceFromHBitmap(
                        hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    DeleteObject(hbmp);
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch { }

        // 取不到图标时回退到应用程序默认图标
        try
        {
            using var bmp = SystemIcons.Application.ToBitmap();
            var hbmp = bmp.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hbmp);
            }
        }
        catch { }

        return null!;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo? culture)
        => throw new NotImplementedException();
}
