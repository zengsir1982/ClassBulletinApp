using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

public static class Store
{
    static string? _appDirOverride;

    /// <summary>
    /// 数据目录。默认 <c>%APPDATA%\ClassBulletinApp</c>；
    /// 可用 <see cref="SetAppDir"/> 改到别处（自动化测试隔离、将来做便携版）。
    /// </summary>
    public static string AppDir =>
        _appDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassBulletinApp");

    /// <summary>覆盖数据目录（传 null 恢复默认）。仅供测试 / 便携版使用。</summary>
    public static void SetAppDir(string? dir)
    {
        _appDirOverride = string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    static string SchedulePath => Path.Combine(AppDir, "schedule.json");

    /// <summary>
    /// 持久化总开关：游客模式下为 false，所有 Save* 直接跳过（改动只留内存、不落盘），
    /// 退出后自动丢弃；登录（admin）通过 LoginWindow 置为 true 后正常保存。
    /// </summary>
    public static bool EnablePersistence { get; set; }

    /// <summary>
    /// 完全禁止任何写盘（含 <see cref="ForceWrite"/>）。诊断 / 截图模式用：
    /// <c>--ui-check</c>、<c>--shot</c> 跑一遍不该在真实数据目录里留下迁移结果，
    /// 否则用户只是截个图，下次启动身份就变了。
    /// </summary>
    public static bool SuppressWrites { get; set; }

    static string TeacherSchedulePath => Path.Combine(AppDir, "teacher_schedule.json");
    static string NotificationsPath => Path.Combine(AppDir, "notifications.json");
    static string SettingsPath => Path.Combine(AppDir, "settings.json");
    static string OverridesPath => Path.Combine(AppDir, "overrides.json");
    static string UsersPath => Path.Combine(AppDir, "users.json");
    static string HomeworkPath => Path.Combine(AppDir, "homework.json");
    static string MissingPath => Path.Combine(AppDir, "missing.json");

    static JsonSerializerOptions Opt => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static void Ensure() => Directory.CreateDirectory(AppDir);

    /// <summary>
    /// 通用 JSON 读取：文件不存在或内容为空（0 字节 / 仅空白）时返回 null，
    /// 统一防止「文件存在但被清空/截断」导致 Deserialize 抛 JsonException 而让程序启动崩溃。
    /// </summary>
    static T? ReadJsonOrNull<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonSerializer.Deserialize<T>(text, Opt);
    }

    public static void SaveSchedule(ScheduleFile sf)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(SchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadSchedule()
        => ReadJsonOrNull<ScheduleFile>(SchedulePath);

    public static void SaveTeacherSchedule(ScheduleFile sf)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(TeacherSchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadTeacherSchedule()
        => ReadJsonOrNull<ScheduleFile>(TeacherSchedulePath);

    public static void SaveHomework(HomeworkBoard board)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(HomeworkPath, JsonSerializer.Serialize(board, Opt));
    }

    public static HomeworkBoard? LoadHomework()
        => ReadJsonOrNull<HomeworkBoard>(HomeworkPath);

    public static void SaveMissingBoard(MissingBoard board)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(MissingPath, JsonSerializer.Serialize(board, Opt));
    }

    /// <summary>
    /// 读取「缺交作业名单」独立便签数据。首次调用（还没有 missing.json）时，
    /// 自动把旧版 homework.json 里的 Missing 列表迁移过来，迁移后清空旧字段避免两处重复。
    /// </summary>
    public static MissingBoard LoadMissingBoard()
    {
        if (File.Exists(MissingPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<MissingBoard>(File.ReadAllText(MissingPath), Opt);
                if (loaded != null) return loaded;
            }
            catch { }
            return new MissingBoard();
        }

        var board = new MissingBoard();
        try
        {
            var hw = LoadHomework();
            if (hw?.Missing is { Count: > 0 })
            {
                board.Items = hw.Missing;
                board.UpdatedAt = hw.UpdatedAt;
                hw.Missing = new();
                File.WriteAllText(HomeworkPath, JsonSerializer.Serialize(hw, Opt));
            }
        }
        catch { }
        SaveMissingBoard(board);
        return board;
    }

    public static void SaveNotifications(List<Notification> list)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(NotificationsPath, JsonSerializer.Serialize(list, Opt));
    }

    public static List<Notification> LoadNotifications()
        => ReadJsonOrNull<List<Notification>>(NotificationsPath) ?? new();

    public static void SaveSettings(AppSettings s)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Opt));
    }

    public static AppSettings LoadSettings()
    {
        var s = ReadJsonOrNull<AppSettings>(SettingsPath) ?? new();
        // 旧版本的设置界面允许存 0~1440，而运行期只认 5~720 —— 于是"设 3 实际按 5 跑"，
        // 设置窗显示的还不是真实生效值。自愈成合法区间并落盘，两处就永远一致了。
        if (NormalizeUpdateInterval(s)) SaveSettings(s);
        return s;
    }

    /// <summary>
    /// 把检查间隔夹到 <see cref="AppSettings.MinUpdateIntervalMinutes"/>~<see cref="AppSettings.MaxUpdateIntervalMinutes"/>。
    /// 返回是否发生了改动（幂等；游客模式下不落盘，下次启动重算结果一致）。
    /// </summary>
    public static bool NormalizeUpdateInterval(AppSettings s)
    {
        if (s == null) return false;
        int v = Math.Clamp(s.UpdateIntervalMinutes,
                           AppSettings.MinUpdateIntervalMinutes,
                           AppSettings.MaxUpdateIntervalMinutes);
        if (v == s.UpdateIntervalMinutes) return false;
        s.UpdateIntervalMinutes = v;
        return true;
    }

    public static void SaveOverrides(List<LessonOverride> list)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(OverridesPath, JsonSerializer.Serialize(list, Opt));
    }

    public static List<LessonOverride> LoadOverrides()
    {
        var list = ReadJsonOrNull<List<LessonOverride>>(OverridesPath) ?? new();
        if (MigrateOverrideRanges(list))
        {
            SaveOverrides(list);   // 旧数据自动归周并落盘（幂等）

            // 记下"发生过迁移"这件事，供升级提示里说明；只写第一次
            try
            {
                var s = LoadSettings();
                if (string.IsNullOrEmpty(s.OverridesMigratedAt))
                {
                    s.OverridesMigratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    SaveSettings(s);
                }
            }
            catch { /* 记不上不影响迁移本身 */ }
        }
        return list;
    }

    /// <summary>
    /// 旧数据迁移：r8 之前的覆盖记录没有生效区间，按 <see cref="LessonOverride.UpdatedAt"/> 归入其所在周，
    /// 与设计 §4.8 的推荐方案一致（绝不迁成"长期有效"）。返回是否发生了改动。
    ///
    /// 说明：迁移是纯本地、幂等的；没有 UpdatedAt 的记录归入当前周。
    /// 游客模式下 <see cref="SaveOverrides"/> 不落盘，下次启动会重新计算，结果一致。
    /// </summary>
    public static bool MigrateOverrideRanges(List<LessonOverride> list)
    {
        bool changed = false;
        foreach (var o in list)
        {
            if (o == null) continue;
            if (o.HasRange)
            {
                if (o.ValidTo!.Value < o.ValidFrom!.Value)   // 脏数据：区间反了就校正
                {
                    (o.ValidFrom, o.ValidTo) = (o.ValidTo, o.ValidFrom);
                    changed = true;
                }
                continue;
            }

            var anchor = o.UpdatedAt == default ? DateTime.Today : o.UpdatedAt;
            var (start, end) = WeekHelper.WeekWindow(anchor);
            o.ValidFrom = DateOnly.FromDateTime(start);
            o.ValidTo = DateOnly.FromDateTime(end);
            changed = true;
        }
        return changed;
    }

    // ================== 用户表 ==================

    /// <summary>本机账号的固定用户名（电教管理员 / 学生角色，教室机开机自动登录用）。</summary>
    public const string LocalUserName = "local";

    public static void SaveUsers(UserBook book)
    {
        // ★ 诊断 / 截图模式（--ui-check、--shot）必须彻底静默：
        //   否则只是在教室机上截个图，就把真实用户表改掉了。
        if (SuppressWrites) return;
        if (!EnablePersistence) return;
        Ensure();
        book.UpdatedAt = DateTime.Now;
        File.WriteAllText(UsersPath, JsonSerializer.Serialize(book, Opt));
    }

    /// <summary>
    /// 读取用户表。文件不存在时<b>自动从旧版单账号迁移</b>：
    /// <list type="bullet">
    /// <item>原 <see cref="AppSettings.Username"/> / <see cref="AppSettings.Password"/> 迁成<b>管理员</b>
    /// （密码明文转哈希）—— 不然升级后老师发现改不了任何设置；</item>
    /// <item>另外建一个 <c>local</c>（电教管理员 / 学生）账号，密码 <c>000</c>，
    /// 并把自动登录切到它 —— 教室机开机只能是"能控显示、不能改数据"的身份。</item>
    /// </list>
    /// <para>★ 自动登录永远不指向管理员：否则谁坐在电脑前谁就是管理员，权限等于零。</para>
    /// </summary>
    public static UserBook LoadUsers()
    {
        var book = ReadJsonOrNull<UserBook>(UsersPath);
        if (book != null && book.Users.Count > 0) return book;

        book = MigrateFromSingleAccount();

        // ★ 这里必须<b>强制落盘</b>，不能走 SaveUsers 的 EnablePersistence 开关：
        //   迁移时还没走到"登录成功、打开持久化"那一步，如果跳过写盘，
        //   users.json 永远不存在 → 每次启动都重新迁移 → 密码盐每次不同 → 自动登录必定失败。
        ForceWrite(UsersPath, JsonSerializer.Serialize(book, Opt));
        return book;
    }

    static UserBook MigrateFromSingleAccount()
    {
        var book = new UserBook();
        var s = LoadSettings();

        string adminName = string.IsNullOrWhiteSpace(s.Username) ? "admin" : s.Username.Trim();
        string adminPwd = string.IsNullOrEmpty(s.Password) ? "000" : s.Password;

        var admin = new AppUser
        {
            Username = adminName,
            DisplayName = "管理员",
            Role = UserRole.Admin
        };
        // 用户名本身就是班级名时（很多教室机这么设），顺带让它能改本班课表
        if (!string.IsNullOrWhiteSpace(s.SelectedClass) &&
            string.Equals(s.SelectedClass, adminName, StringComparison.Ordinal))
            admin.ClassNames.Add(adminName);
        PasswordHasher.SetPassword(admin, adminPwd);
        book.Users.Add(admin);

        // 本机账号：教室机的日常身份
        if (!string.Equals(adminName, LocalUserName, StringComparison.OrdinalIgnoreCase))
        {
            var local = new AppUser
            {
                Username = LocalUserName,
                DisplayName = "电教管理员",
                Role = UserRole.Student
            };
            PasswordHasher.SetPassword(local, "000");
            book.Users.Add(local);

            // 自动登录切到受限账号（旧版是直接记住 admin 的账号密码，升级后必须降级）
            // ★ 只换凭据，不动 s.AutoLogin 本身：用户如果主动取消过"开机自动登录"，
            //   升级不该把它又打开（那台机器会突然变成无需密码直接进）。
            if (s.AutoLogin)
            {
                s.AutoLoginUser = LocalUserName;
                s.AutoLoginPassword = "000";
            }
            else
            {
                // 关掉的状态下更要清掉旧的管理员明文密码，留在 settings.json 里没有意义
                s.AutoLoginUser = "";
                s.AutoLoginPassword = "";
            }
            ForceWrite(SettingsPath, JsonSerializer.Serialize(s, Opt));
        }

        return book;
    }

    /// <summary>
    /// 无视持久化开关直接写文件。只给"程序自己必须先落地才能正常工作"的初始化数据用
    /// （用户表迁移），用户操作永远走 Save* 系列。
    /// </summary>
    static void ForceWrite(string path, string content)
    {
        if (SuppressWrites) return;
        try
        {
            Ensure();
            File.WriteAllText(path, content);
        }
        catch { /* 写不进去也不能让登录流程崩掉 */ }
    }

    // ================== 自动备份 ==================

    public static string BackupRoot => Path.Combine(AppDir, "backup");

    /// <summary>备份保留份数（超出后删最旧的）。</summary>
    public const int BackupKeep = 20;

    /// <summary>
    /// 在任何"覆盖型写盘"（导入数据包 / 导入总课表 / 清除全部）前调用：
    /// 把当前所有数据文件复制到 <c>backup/&lt;时间戳&gt;/</c>，返回备份目录。
    /// 没有任何可备份文件时返回空串。失败不抛异常（备份不该阻断业务）。
    /// </summary>
    public static string BackupNow(string reason = "")
    {
        try
        {
            var files = new[]
            {
                SchedulePath, TeacherSchedulePath, OverridesPath,
                NotificationsPath, SettingsPath, HomeworkPath, MissingPath, UsersPath
            }.Where(File.Exists).ToList();

            if (files.Count == 0) return "";

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(BackupRoot, stamp);
            int n = 1;
            while (Directory.Exists(dir)) dir = Path.Combine(BackupRoot, $"{stamp}-{n++}");
            Directory.CreateDirectory(dir);

            foreach (var f in files)
            {
                try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); } catch { }
            }
            try
            {
                File.WriteAllText(Path.Combine(dir, "_reason.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{reason}");
            }
            catch { }

            PruneBackups();
            return dir;
        }
        catch
        {
            return "";
        }
    }

    static void PruneBackups()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return;
            var dirs = Directory.GetDirectories(BackupRoot)
                                .OrderByDescending(d => d, StringComparer.Ordinal)
                                .Skip(BackupKeep)
                                .ToList();
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch { }
            }
        }
        catch { }
    }

    public class BackupInfo
    {
        public string Dir { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime Time { get; set; }
        public string Reason { get; set; } = "";
        public int ClassCount { get; set; }
        public int OverrideCount { get; set; }
        public string Display => $"{Time:MM-dd HH:mm}　{ClassCount} 个班 · 调代 {OverrideCount} 条"
            + (string.IsNullOrWhiteSpace(Reason) ? "" : $"　（{Reason}）");
    }

    /// <summary>列出可用备份（最新在前），并读出各份的班级数/调代条数供界面显示。</summary>
    public static List<BackupInfo> ListBackups()
    {
        var list = new List<BackupInfo>();
        try
        {
            if (!Directory.Exists(BackupRoot)) return list;
            foreach (var dir in Directory.GetDirectories(BackupRoot)
                                         .OrderByDescending(d => d, StringComparer.Ordinal))
            {
                var info = new BackupInfo { Dir = dir, Name = Path.GetFileName(dir) };

                if (DateTime.TryParseExact(info.Name.Length >= 15 ? info.Name[..15] : info.Name,
                        "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t))
                    info.Time = t;
                else
                    info.Time = Directory.GetLastWriteTime(dir);

                var rf = Path.Combine(dir, "_reason.txt");
                if (File.Exists(rf))
                {
                    try
                    {
                        var lines = File.ReadAllLines(rf);
                        info.Reason = lines.Length > 1 ? string.Join(" ", lines.Skip(1)).Trim() : "";
                    }
                    catch { }
                }

                try
                {
                    var sp = Path.Combine(dir, "schedule.json");
                    if (File.Exists(sp))
                    {
                        var sf = JsonSerializer.Deserialize<ScheduleFile>(File.ReadAllText(sp), Opt);
                        info.ClassCount = sf?.Classes.Count ?? 0;
                    }
                    var op = Path.Combine(dir, "overrides.json");
                    if (File.Exists(op))
                    {
                        var ov = JsonSerializer.Deserialize<List<LessonOverride>>(File.ReadAllText(op), Opt);
                        info.OverrideCount = ov?.Count ?? 0;
                    }
                }
                catch { }

                list.Add(info);
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 把某份备份整体恢复回运行目录（先备份当前状态，再覆盖）。
    /// 返回 (是否成功, 提示)。
    /// </summary>
    public static (bool Ok, string Message) RestoreBackup(string backupDir)
    {
        try
        {
            if (!Directory.Exists(backupDir)) return (false, "备份目录不存在。");

            var files = new[]
            {
                "schedule.json", "teacher_schedule.json", "overrides.json",
                "notifications.json", "settings.json", "homework.json", "missing.json", "users.json"
            };

            int restored = 0;
            BackupNow("恢复备份前自动存档");     // 先给当前状态留一份，防止恢复错了

            Ensure();
            foreach (var name in files)
            {
                var src = Path.Combine(backupDir, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(AppDir, name), true);
                restored++;
            }
            return restored > 0
                ? (true, $"已恢复 {restored} 个文件。")
                : (false, "该备份里没有可恢复的文件。");
        }
        catch (Exception ex)
        {
            return (false, "恢复失败：" + ex.Message);
        }
    }
}
