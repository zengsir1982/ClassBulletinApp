using System;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 「关于」里那段运行信息（版本 / 身份 / 课表规模 / 数据源 / 数据目录 / 上次检查更新）的唯一取值处。
/// <para>★ 两个展示位置（独立「关于」标签页、<see cref="AboutWindow"/> 弹窗）都用这里的数据，
/// 免得一处改了另一处还是老样子 —— 尤其版本号必须来自 <see cref="AppInfo.VersionText"/>，绝不硬编码。</para>
/// <para>本类<b>只读</b>：不写设置、不碰数据，游客身份也能取（看版本不涉及权限）。</para>
/// </summary>
public static class AppSummary
{
    public sealed class Info
    {
        /// <summary>形如 1.0.5（不带 v）。</summary>
        public string Version = "";

        /// <summary>角色中文名。</summary>
        public string Role = "";

        /// <summary>当前账号（游客则为「游客」）。</summary>
        public string User = "";

        /// <summary>「30 个班」或「未导入」。</summary>
        public string ClassText = "";

        /// <summary>数据源类型 + 地址（不含 FTP 密码）。</summary>
        public string SourceText = "";

        /// <summary>「数据包 v1 · 用户表 v4」。</summary>
        public string SchemaText = "";

        /// <summary>数据目录。</summary>
        public string DataDir = "";

        /// <summary>上次检查更新的结果（没有则「尚未检查」）。</summary>
        public string LastCheck = "";

        /// <summary>点「复制版本信息」写进剪贴板的整段文本。</summary>
        public string CopyText = "";
    }

    public static Info Build()
    {
        var info = new Info();

        try
        {
            info.Version = AppInfo.VersionText;
            info.User = UserSession.Current?.Username ?? "游客";
            info.Role = UserSession.RoleDisplay;

            int classCount = 0;
            try { classCount = Store.LoadSchedule()?.Classes.Count ?? 0; } catch { }
            info.ClassText = classCount > 0 ? $"{classCount} 个班" : "未导入";

            int bookSchema = 4;
            try { bookSchema = Store.LoadUsers().Schema; } catch { }
            info.SchemaText = $"数据包 v{SchedulePackage.SchemaVersion}　·　用户表 v{bookSchema}";

            info.DataDir = Store.AppDir;

            string lastCheck = "尚未检查";
            AppSettingsSnapshot snap = AppSettingsSnapshot.Read();
            if (!string.IsNullOrWhiteSpace(snap.LastUpdateCheckInfo)) lastCheck = snap.LastUpdateCheckInfo.Trim();
            info.LastCheck = lastCheck;

            // 数据源描述：排查"教室机收不到更新"时第一眼要看的东西（Describe 不含 FTP 密码）
            string srcText = "未配置（不检查更新）";
            try
            {
                var feed = FeedSourceFactory.Create(snap.Settings);
                if (feed != null) srcText = $"{KindText(feed.Kind)}　{feed.Describe}";
            }
            catch { }
            info.SourceText = srcText;

            info.CopyText =
                $"班级公布栏 v{info.Version}" + Environment.NewLine +
                $"当前身份：{info.Role}（{info.User}）" + Environment.NewLine +
                $"课表规模：{info.ClassText}" + Environment.NewLine +
                $"数据源：{info.SourceText}" + Environment.NewLine +
                $"数据格式：{info.SchemaText}" + Environment.NewLine +
                $"数据目录：{info.DataDir}" + Environment.NewLine +
                $"上次检查更新：{info.LastCheck}";
        }
        catch { }

        return info;
    }

    /// <summary>读一次设置（读失败也要能显示其余信息，所以单独兜一层）。</summary>
    private sealed class AppSettingsSnapshot
    {
        public ScheduleCore.Models.AppSettings Settings = new();
        public string LastUpdateCheckInfo = "";

        public static AppSettingsSnapshot Read()
        {
            var snap = new AppSettingsSnapshot();
            try
            {
                var s = Store.LoadSettings();
                snap.Settings = s;
                snap.LastUpdateCheckInfo = s.LastUpdateCheckInfo ?? "";
            }
            catch { }
            return snap;
        }
    }

    /// <summary>数据源类型的中文名（Kind 里存的是 Folder / Ftp 等内部标识）。</summary>
    public static string KindText(string kind) => kind switch
    {
        "Ftp" => "FTP 服务器",
        "Folder" => "共享文件夹 / 本地目录",
        _ => kind
    };
}
