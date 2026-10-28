using System.Reflection;

namespace ClassBulletinApp;

/// <summary>
/// 版本信息：统一从程序集版本号读取，发版只需改 ClassBulletinApp.csproj 里的 &lt;Version&gt;。
/// </summary>
public static class AppInfo
{
    /// <summary>形如「1.0.0」的版本号（用于窗口标题、关于、安装包名）。</summary>
    public static string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "1.0.0";
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}.0";
        }
    }
}
