using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 离线数据包（<c>.cbpack</c>）—— 公共同步数据的分发载体。
///
/// <para><b>只装"全校应该一样"的东西</b>：底表、调代覆盖（不含手工记录）、全校通知、开学周/主题等全局设置。
/// 本机专属内容（显示哪个班、窗口布局、密码、作业、缺交、自定义便签、快捷方式）<b>一律不进包</b>，
/// 导入时也不会被覆盖 —— 否则一台机器配好作业和窗口位置，拷到别的班就串了。</para>
///
/// <para>包内结构（zip）：</para>
/// <code>
/// manifest.json         清单 + 每个文件的 SHA-256
/// schedule.json         底表（90 班）
/// overrides.public.json 只含 temp / remote / basePatch
/// notices.school.json   只含 Scope = school 的通知
/// meta.json             开学周 / 主题 / 提醒
/// </code>
///
/// <para>教师表（teacher_schedule.json）<b>故意不打进包</b>：由底表重建只需 12 ms，
/// 各机导入后自己重建即可，包体积少一半。</para>
/// </summary>
public static class SchedulePackage
{
    public const int SchemaVersion = 1;

    public const string FileManifest = "manifest.json";
    public const string FileSchedule = "schedule.json";
    public const string FileOverrides = "overrides.public.json";
    public const string FileNotices = "notices.school.json";
    public const string FileMeta = "meta.json";

    /// <summary>
    /// 用户表（可选文件）。随包下发，管理员改一次全校生效。
    /// 旧包没有这个文件，导入时保持本机用户表不动。
    /// </summary>
    public const string FileUsers = "users.json";

    static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>包内不参与分发的来源（手工改课只属于本机）。</summary>
    static bool IsPublicOverride(LessonOverride o)
        => o.Source is "temp" or "remote" or "basePatch";

    /// <summary>
    /// 「导入来源」——同步时可以按周整体替换掉的记录。
    /// <para>★ <c>basePatch</c>（长期改课）永远不在此列：它改的是底表本身，
    /// 不属于任何一个"临时课表周"，清掉就找不回来了。</para>
    /// <para><c>manual</c> 也不在此列：是否覆盖由包里的
    /// <see cref="PackageManifest.OverrideIncludeManual"/> 决定，且是「停用」而不是删除。</para>
    /// </summary>
    static bool IsImportSource(LessonOverride o)
        => o.Source is "temp" or "remote";

    // ==================================================================
    //  导出
    // ==================================================================

    public class ExportResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public int ClassCount { get; set; }
        public int OverrideCount { get; set; }
        public int NoticeCount { get; set; }
        public string OverrideRange { get; set; } = "";
    }

    /// <summary>
    /// 导出数据包。只写入"公共"部分；<paramref name="overrides"/> 与 <paramref name="notices"/>
    /// 传全量即可，方法内部按来源/范围过滤。
    /// </summary>
    /// <param name="clearFrom">包所"完整描述"的时间范围起点（含）。</param>
    /// <param name="clearTo">同上终点（含）。</param>
    /// <param name="overridesAuthoritative">
    /// true = 本包对"调代课"是<b>完整权威</b>的：教室机收到后清掉本机<b>全部</b>导入来源的调代课
    /// （temp / remote），只保留包内的 + 本机手工记录 + 长期改课。
    /// <para>临时课表都是一周一份的快照，source 里没有的周就代表"那一周没有调代课"，
    /// 因此同步时没被覆盖到的周也必须把旧的调代标记取消、还原成底表原课程。</para>
    /// <para>只在"发布机从 source 目录重新生成包"时为 true；手工「导出数据包」保持 false（保守）。</para>
    /// </param>
    /// <remarks>
    /// ★ <paramref name="clearFrom"/> / <paramref name="clearTo"/> 是"这周的调代课由我说了算"的声明：
    /// 教室机收到包后会把这个范围内的 temp/remote 记录<b>整段清掉</b>再写入包内记录。
    /// 临时课表本身是"整周快照"，因此只要 source 里有某一周的临时课表，
    /// 那一周内<b>没有被标记 调/代 的格子就必须回到基础课表原课程</b>（= 取消调代）。
    /// 若这里只按"恰好有改动的那几周"算，取消调代的格子就会一直挂着旧记录清不掉。
    /// 不传则退化为"按包内记录自身的区间"（旧行为）。
    /// </remarks>
    public static ExportResult Export(
        string outPath,
        ScheduleFile? schedule,
        IReadOnlyList<LessonOverride>? overrides,
        IReadOnlyList<Notification>? notices,
        AppSettings? settings,
        string builtBy = "",
        DateOnly? clearFrom = null,
        DateOnly? clearTo = null,
        bool overridesAuthoritative = false,
        bool includeManualOverrides = false,
        int rev = 0,
        IReadOnlyList<AppUser>? users = null)
    {
        var res = new ExportResult { Path = outPath };

        if (schedule == null || schedule.Classes.Count == 0)
        {
            res.Error = "当前没有基础课表，无法导出数据包。请先导入全校总课程表。";
            return res;
        }

        try
        {
            var pubOverrides = (overrides ?? Array.Empty<LessonOverride>())
                .Where(IsPublicOverride)
                .ToList();

            var schoolNotices = (notices ?? Array.Empty<Notification>())
                .Where(n => n.Scope == "school")
                .ToList();

            var meta = new PackageMeta
            {
                SemesterStartMonday = settings?.SemesterStartMonday ?? "",
                Theme = settings?.Theme ?? "",
                Reminder = settings?.Reminder
            };

            // 包覆盖的时间范围：导入时用它决定"清掉本机哪一段的旧调代课"。
            // 即使包内没有任何调代课，也要给一个范围（当前周）——
            // 否则"这周全校都取消调课"这种情形下，各机上的旧记录永远清不掉、会一直显示。
            string range;
            var dated = pubOverrides.Where(o => o.HasRange).ToList();
            if (clearFrom.HasValue && clearTo.HasValue)
            {
                // 发布方明确声明了"这几周由本包完整描述"（见 Export 的 remarks）
                range = $"{clearFrom.Value:yyyy-MM-dd}~{clearTo.Value:yyyy-MM-dd}";
            }
            else if (dated.Count > 0)
            {
                var from = dated.Min(o => o.ValidFrom!.Value);
                var to = dated.Max(o => o.ValidTo!.Value);
                range = $"{from:yyyy-MM-dd}~{to:yyyy-MM-dd}";
            }
            else
            {
                var (ws, we) = WeekHelper.WeekWindow(DateTime.Today,
                    settings?.WeekStartDay ?? WeekHelper.DefaultWeekStartDay);
                range = $"{ws:yyyy-MM-dd}~{we:yyyy-MM-dd}";
            }

            var files = new List<PackageFile>();

            // 先删旧包，避免 AppEnd 出双份
            if (File.Exists(outPath)) File.Delete(outPath);

            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddEntry(zip, files, FileSchedule, JsonSerializer.Serialize(schedule, WriteOpt));
                AddEntry(zip, files, FileOverrides, JsonSerializer.Serialize(pubOverrides, WriteOpt));
                AddEntry(zip, files, FileNotices, JsonSerializer.Serialize(schoolNotices, WriteOpt));
                AddEntry(zip, files, FileMeta, JsonSerializer.Serialize(meta, WriteOpt));

                // 用户表随包下发（可选）。★ 只写用户名 / 哈希 / 盐 / 角色 / 班级，绝不含明文密码。
                //   按用户名排序后再序列化：同输入必同摘要，否则每次打包摘要都变、30 台天天重下。
                if (users is { Count: > 0 })
                {
                    var book = new UserBook
                    {
                        Users = users.OrderBy(u => u.Username, StringComparer.Ordinal).ToList(),
                        UpdatedAt = null
                    };
                    AddEntry(zip, files, FileUsers, JsonSerializer.Serialize(book, WriteOpt));
                }

                var manifest = new PackageManifest
                {
                    Schema = SchemaVersion,
                    Kind = "data",
                    BuiltAt = DateTime.Now,
                    BuiltBy = builtBy,
                    ClassCount = schedule.Classes.Count,
                    OverrideCount = pubOverrides.Count,
                    OverrideRange = range,
                    OverrideAuthoritative = overridesAuthoritative,
                    OverrideIncludeManual = includeManualOverrides,
                    Rev = rev,
                    NoticeCount = schoolNotices.Count,
                    UserCount = users?.Count ?? 0,
                    Files = files,
                    DataSha256 = ComputeDataSha(files, rev)
                };
                // manifest 最后写：它的内容依赖前面几个文件的校验和
                AddEntry(zip, null, FileManifest, JsonSerializer.Serialize(manifest, WriteOpt));
            }

            res.Ok = true;
            res.Size = new FileInfo(outPath).Length;
            res.ClassCount = schedule.Classes.Count;
            res.OverrideCount = pubOverrides.Count;
            res.NoticeCount = schoolNotices.Count;
            res.OverrideRange = range;
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
    }

    static void AddEntry(ZipArchive zip, List<PackageFile>? collector, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var s = entry.Open()) s.Write(bytes, 0, bytes.Length);

        collector?.Add(new PackageFile
        {
            Name = name,
            Sha256 = Sha(bytes),
            Size = bytes.Length
        });
    }

    static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>整包摘要：把各文件名+哈希按名排序拼接后再哈希，任何文件变化都会变。</summary>
    /// <summary>
    /// 整包摘要。
    /// <para>★ <paramref name="rev"/> 必须算进来：策略开关（是否覆盖手工调课、是否权威）
    /// 存在 manifest 里、不改任何文件，如果摘要只算文件，就会出现
    /// "改了策略但摘要不变 → 各机认为已是最新 → 永远不 Apply"。这个坑不显眼但必踩。</para>
    /// </summary>
    static string ComputeDataSha(List<PackageFile> files, int rev)
    {
        var sb = new StringBuilder();
        sb.Append("rev:").Append(rev).Append('\n');
        foreach (var f in files.OrderBy(x => x.Name, StringComparer.Ordinal))
            sb.Append(f.Name).Append(':').Append(f.Sha256).Append('\n');
        return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>从 manifest 原文解析出整包摘要（发布/检查时统一用它做版本比对）。</summary>
    public static string ReadDataSha(string manifestJson)
    {
        try
        {
            var m = JsonSerializer.Deserialize<PackageManifest>(manifestJson, ReadOpt);
            return m?.DataSha256 ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 反序列化 manifest（供检查 / 发布用）。
    /// 解析失败或传入空串时返回 null，调用方按"远端没有清单"处理。
    /// </summary>
    public static PackageManifest? ParseManifest(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return null;
        try { return JsonSerializer.Deserialize<PackageManifest>(manifestJson, ReadOpt); }
        catch { return null; }
    }

    // ==================================================================
    //  检查（不写盘）
    // ==================================================================

    public class InspectResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Path { get; set; } = "";
        public PackageManifest? Manifest { get; set; }
        public ScheduleFile? Schedule { get; set; }
        public List<LessonOverride> Overrides { get; set; } = new();
        public List<Notification> Notices { get; set; } = new();
        public PackageMeta? Meta { get; set; }

        /// <summary>
        /// 包内用户表（可选）。null = 本包不带用户表，导入时保持本机用户表不动。
        /// </summary>
        public UserBook? Users { get; set; }

        public List<string> Warnings { get; set; } = new();

        /// <summary>一句话摘要，用于预览窗口标题。</summary>
        public string Summary => Manifest == null ? "" :
            $"{Manifest.ClassCount} 个班 · 调代 {Manifest.OverrideCount} 条" +
            (string.IsNullOrEmpty(Manifest.OverrideRange) ? "" : $"（{Manifest.OverrideRange}）") +
            (Manifest.NoticeCount > 0 ? $" · 全校通知 {Manifest.NoticeCount} 条" : "");
    }

    /// <summary>只读检查：解包、校验 SHA-256、把内容读进内存，<b>不写任何本机数据</b>。</summary>
    public static InspectResult Inspect(string path)
    {
        var res = new InspectResult { Path = path };

        if (!File.Exists(path))
        {
            res.Error = "文件不存在：" + path;
            return res;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);

            var manifestEntry = zip.GetEntry(FileManifest);
            if (manifestEntry == null)
            {
                res.Error = "这不是班级公布栏数据包（缺少 manifest.json）。";
                return res;
            }

            var manifest = JsonSerializer.Deserialize<PackageManifest>(ReadText(manifestEntry), ReadOpt);
            if (manifest == null)
            {
                res.Error = "manifest.json 无法解析（可能已损坏）。";
                return res;
            }
            if (manifest.Schema > SchemaVersion)
            {
                res.Error = $"数据包格式版本为 {manifest.Schema}，本程序只支持到 {SchemaVersion}。请先升级程序。";
                return res;
            }
            if (!string.Equals(manifest.Kind, "data", StringComparison.OrdinalIgnoreCase))
            {
                res.Error = $"这是「{manifest.Kind}」包，不是数据包。";
                return res;
            }
            res.Manifest = manifest;

            // 逐文件校验 SHA-256 —— 防半传/损坏，比看时间戳可靠
            foreach (var pf in manifest.Files)
            {
                var e = zip.GetEntry(pf.Name);
                if (e == null)
                {
                    res.Error = $"数据包不完整：缺少 {pf.Name}。";
                    return res;
                }
                var bytes = ReadBytes(e);
                if (bytes.Length != pf.Size || Sha(bytes) != pf.Sha256)
                {
                    res.Error = $"文件校验失败：{pf.Name}（大小或校验和不符，包可能损坏或被改动）。";
                    return res;
                }
            }

            // 内容
            var se = zip.GetEntry(FileSchedule);
            if (se != null)
            {
                res.Schedule = SafeDeserialize<ScheduleFile>(ReadText(se), res.Warnings, FileSchedule);
            }

            var oe = zip.GetEntry(FileOverrides);
            if (oe != null)
            {
                res.Overrides = SafeDeserialize<List<LessonOverride>>(ReadText(oe), res.Warnings, FileOverrides) ?? new();
                res.Overrides.RemoveAll(o => !IsPublicOverride(o));
            }

            var ne = zip.GetEntry(FileNotices);
            if (ne != null)
            {
                res.Notices = SafeDeserialize<List<Notification>>(ReadText(ne), res.Warnings, FileNotices) ?? new();
                // 只接受全校通知，包里若混进本班通知就丢掉（防误打包外泄）
                int dropped = res.Notices.RemoveAll(n => n.Scope != "school");
                if (dropped > 0) res.Warnings.Add($"包里混有 {dropped} 条非全校通知，已忽略。");
            }

            var me = zip.GetEntry(FileMeta);
            if (me != null)
            {
                res.Meta = SafeDeserialize<PackageMeta>(ReadText(me), res.Warnings, FileMeta);
            }

            // 用户表（可选）。只接受结构完整的记录：没有用户名 / 哈希 / 盐的一律丢弃，
            // 否则会出现"能看到的账号但永远登不进去"这种莫名其妙的状态。
            var ue = zip.GetEntry(FileUsers);
            if (ue != null)
            {
                var book = SafeDeserialize<UserBook>(ReadText(ue), res.Warnings, FileUsers);
                if (book != null)
                {
                    int before = book.Users.Count;
                    book.Users.RemoveAll(u => u == null
                        || string.IsNullOrWhiteSpace(u.Username)
                        || string.IsNullOrEmpty(u.PasswordHash)
                        || string.IsNullOrEmpty(u.Salt));
                    if (book.Users.Count < before)
                        res.Warnings.Add($"用户表里有 {before - book.Users.Count} 条记录缺少用户名或密码，已丢弃。");

                    // 角色写错的按最低权限（student）处理，绝不因一个拼写错误给人管理员
                    foreach (var u in book.Users)
                    {
                        u.Role = (u.Role ?? "").Trim().ToLowerInvariant();
                        if (!UserRole.IsValid(u.Role)) u.Role = UserRole.Student;
                    }
                    res.Users = book;
                }
            }

            if (res.Schedule == null || res.Schedule.Classes.Count == 0)
            {
                res.Error = "数据包里没有可用的基础课表。";
                return res;
            }

            res.Ok = true;
            return res;
        }
        catch (InvalidDataException)
        {
            res.Error = "文件不是有效的 zip 包（可能下载未完成）。";
            return res;
        }
        catch (Exception ex)
        {
            res.Error = "读取数据包失败：" + ex.Message;
            return res;
        }
    }

    static T? SafeDeserialize<T>(string json, List<string> warnings, string name) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ReadOpt);
        }
        catch (Exception ex)
        {
            warnings.Add($"{name} 解析失败，已跳过：{ex.Message}");
            return null;
        }
    }

    static string ReadText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static byte[] ReadBytes(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ==================================================================
    //  应用（写盘）
    // ==================================================================

    public class ApplyResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string BackupDir { get; set; } = "";
        public int ClassCount { get; set; }
        public int OverrideAdded { get; set; }
        public int OverrideRemoved { get; set; }

        /// <summary>本次是「权威包」清理（清掉了本机全部导入来源的调代课，含没被覆盖的周）。</summary>
        public bool AuthoritativeCleared { get; set; }

        /// <summary>
        /// 本次被主机策略<b>停用</b>的本机手工调课条数（不是删除，可在「恢复被主机停用的本机调课」里复活）。
        /// </summary>
        public int ManualSuspended { get; set; }

        public int NoticeAdded { get; set; }
        public int NoticeKept { get; set; }

        /// <summary>本次随包更新的用户表条目数（0 = 包内没有用户表，本机用户表未动）。</summary>
        public int UserCount { get; set; }

        /// <summary>本次随包带来的角色权限与原来不同（界面需要重刷按钮可用性）。</summary>
        public bool PolicyApplied { get; set; }

        /// <summary>
        /// 应用后对当前登录身份的重新绑定结果。
        /// <para>★ <c>Removed</c> / <c>Disabled</c> 表示"我自己被这个包踢掉了"：
        /// 调用方必须提示并回登录窗，否则那台机器会带着一个已失效的身份继续跑，
        /// 用户还会以为自己有权限。</para>
        /// </summary>
        public RebindResult Identity { get; set; } = RebindResult.Guest;

        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// 包内用户表会不会让本机<b>开机后进不去</b>（自动登录账号被删 / 停用 / 升成管理员）。
    /// <para>本机没开自动登录、或没配自动登录账号时一律返回 false ——
    /// 那种机器本来就是开机弹登录窗的，用户表怎么变都不影响它。</para>
    /// </summary>
    static bool AutoLoginWouldBreak(UserBook incoming, out string why)
    {
        why = "";
        var st = Store.LoadSettings();
        if (!st.AutoLogin) return false;

        string name = (st.AutoLoginUser ?? "").Trim();
        if (name.Length == 0) return false;

        var u = incoming.Find(name);
        if (u == null) { why = $"包里已经没有账号「{name}」"; return true; }
        if (!u.Enabled) { why = $"账号「{name}」在包里是停用状态"; return true; }
        if (u.Role == UserRole.Admin) { why = $"账号「{name}」在包里变成了管理员，而管理员不参与自动登录"; return true; }
        return false;
    }

    /// <summary>两份用户表的角色权限是否不同（决定应用后要不要重刷界面按钮）。</summary>
    static bool PolicyDiffers(UserBook a, UserBook b)
    {
        foreach (var role in new[] { UserRole.Teacher, UserRole.Student })
            if (!string.Equals(a.PolicyFor(role).Describe(), b.PolicyFor(role).Describe(), StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// 把检查过的包写入本机。**写盘前自动备份**；本机手工调课（Source=manual）与
    /// 本班通知（Scope=local）一律保留。
    /// </summary>
    public static ApplyResult Apply(InspectResult pkg, bool writeMeta = true)
    {
        var res = new ApplyResult();
        if (pkg?.Schedule == null || pkg.Manifest == null)
        {
            res.Error = "没有可应用的数据包内容。";
            return res;
        }

        try
        {
            // 1) 备份（游客模式不落盘，也就无需备份）
            if (Store.EnablePersistence)
                res.BackupDir = Store.BackupNow("导入数据包 " + Path.GetFileName(pkg.Path));

            // 2) 底表：整份覆盖（包里是完整的 90 班底表）
            Store.SaveSchedule(pkg.Schedule);
            res.ClassCount = pkg.Schedule.Classes.Count;

            // 3) 调代覆盖：先按包声明的范围清理本机的 temp/remote，再加包内记录。
            //    临时课表本身是"整周快照"，所以这一段内没出现在包里的格子 = 该周没有调代课，
            //    必须回到底表原课程（= 取消调代）。basePatch（长期改课）不参与清除。
            //
            //    ★ 包声明 OverrideAuthoritative 时改为"清掉本机全部 temp/remote"：
            //      临时课表一周一份，source 里没有的周（更早的、中间的、将来的）同样属于
            //      "没有被这次同步覆盖到"，那些残留的调代标记也要一起取消，
            //      否则一旦翻到那几周的周视图，就会看到源数据里根本不存在的调代课。
            var local = Store.LoadOverrides();
            var (rf, rt) = ParseRange(pkg.Manifest.OverrideRange);

            // ★ 主机策略：本包是否连"本机手工调课"一起覆盖（旧包无此字段 → false → 保留）
            bool includeManual = pkg.Manifest.OverrideIncludeManual;

            // 「这一条要不要被本次同步处理」
            bool Hit(LessonOverride o)
            {
                if (IsImportSource(o)) return true;               // temp / remote：按包的规则清
                if (o.Source == "manual") return includeManual;    // 手工调课：看主机策略
                return false;                                      // basePatch 等：永远不动
            }

            // 命中范围：权威包 = 全部；否则 = 与包声明的区间有交集
            bool InScope(LessonOverride o)
            {
                if (pkg.Manifest.OverrideAuthoritative) return true;
                if (!rf.HasValue || !rt.HasValue) return false;
                if (!o.ValidFrom.HasValue || !o.ValidTo.HasValue) return true;   // 无区间的旧记录按命中算
                return o.ValidFrom.Value <= rt.Value && o.ValidTo.Value >= rf.Value;
            }

            string stamp = $"包 {pkg.Manifest.BuiltAt:MM-dd HH:mm}";
            int removed = 0, suspended = 0;

            foreach (var o in local.ToList())
            {
                if (!Hit(o) || !InScope(o)) continue;

                if (o.Source == "manual")
                {
                    // ★ 软覆盖：停用而不是删除。
                    //   教室机上的手工调课是老师的工作成果，删掉只能靠 20 份轮转备份找回，
                    //   而教室机现场没人会去翻备份。停用后不显示，策略改回去可一键恢复。
                    if (!o.Suspended)
                    {
                        o.Suspended = true;
                        o.SuspendedAt = DateTime.Now;
                        o.SuspendedBy = stamp;
                        suspended++;
                    }
                }
                else
                {
                    local.Remove(o);
                    removed++;
                }
            }

            if (pkg.Manifest.OverrideAuthoritative) res.AuthoritativeCleared = true;

            if (!pkg.Manifest.OverrideAuthoritative
                && !(rf.HasValue && rt.HasValue)
                && pkg.Overrides.Count == 0)
            {
                // 包里明确"这段时间没有调代课"，但没给范围 —— 不清（宁可留着让人看见，也不静默删数据）
                res.Warnings.Add("包内没有调代课且未标注生效范围，本机原有调代课未被清理。");
            }

            // 同 Key 去重后加入。
            // ★ 不能无差别 RemoveAll：被停用的手工记录若与包内记录同 Key，会被顺手吃掉，
            //   "可恢复"就凭空消失了。只清未停用的。
            foreach (var o in pkg.Overrides)
            {
                local.RemoveAll(x => x.Key == o.Key && !x.Suspended);
                local.Add(o);
            }

            Store.SaveOverrides(local);
            res.OverrideAdded = pkg.Overrides.Count;
            res.OverrideRemoved = removed;
            res.ManualSuspended = suspended;

            // 4) 通知：全校的覆盖，本班的保留
            if (pkg.Notices.Count > 0)
            {
                var cur = Store.LoadNotifications();
                cur.RemoveAll(n => n.Scope == "school");
                cur.AddRange(pkg.Notices);
                Store.SaveNotifications(cur);
                res.NoticeAdded = pkg.Notices.Count;
                res.NoticeKept = cur.Count(n => n.Scope != "school");
            }

            // 4.5) 用户表（可选）：包里有就整份替换本机用户表
            //      ★ 关键保护：包里一个启用中的管理员都没有时拒绝应用 ——
            //        否则一旦发错一版，30 台机器同时再也没人能做管理操作，只能手工改 json 救回来。
            if (pkg.Users != null && pkg.Users.Users.Count > 0)
            {
                if (pkg.Users.AdminCount == 0)
                {
                    res.Warnings.Add("包内用户表里没有启用中的管理员，已忽略（保留本机用户表）。");
                }
                else if (AutoLoginWouldBreak(pkg.Users, out string why))
                {
                    // ★ 教室机靠 AutoLoginUser 开机直接进（通常是 local）。包里把这个账号删了、
                    //   停用了、或升成管理员，那 30 台机器会同时停在登录窗——而且没人值班，
                    //   课表就挂了。宁可这次名单不同步，也不能让机器开不了机。
                    res.Warnings.Add($"包内用户表会让本机开机后无法自动登录（{why}），已忽略（保留本机用户表）。"
                                   + "请在发布机的「用户管理」里确认开机账号仍然存在，且角色是班主任或电教管理员。");
                }
                else
                {
                    bool policyChanged = PolicyDiffers(Store.LoadUsers(), pkg.Users);
                    Store.SaveUsers(pkg.Users);
                    res.UserCount = pkg.Users.Users.Count;
                    res.PolicyApplied = policyChanged;

                    // 角色 / 停用状态 / 角色权限任一变化都要立刻重绑：
                    // 否则要重启才生效，更糟的是"自己已被降级却仍按旧权限操作"。
                    res.Identity = UserSession.Rebind(pkg.Users);
                }
            }

            // 5) 全局设置白名单（只写这几项，其余一律不动）
            if (writeMeta && pkg.Meta != null)
            {
                var st = Store.LoadSettings();
                bool touched = false;

                if (!string.IsNullOrWhiteSpace(pkg.Meta.SemesterStartMonday) &&
                    st.SemesterStartMonday != pkg.Meta.SemesterStartMonday)
                {
                    st.SemesterStartMonday = pkg.Meta.SemesterStartMonday!;
                    touched = true;
                }
                if (!string.IsNullOrWhiteSpace(pkg.Meta.Theme) && st.Theme != pkg.Meta.Theme)
                {
                    st.Theme = pkg.Meta.Theme!;
                    touched = true;
                }
                if (pkg.Meta.Reminder != null)
                {
                    st.Reminder = pkg.Meta.Reminder;
                    touched = true;
                }
                if (touched) Store.SaveSettings(st);
            }

            // 6) 数据来源标记为 Excel（离线版主通道），避免下次启动又被二一缓存盖回去
            var s2 = Store.LoadSettings();
            bool stChanged = false;
            if (s2.ScheduleSource != "Excel") { s2.ScheduleSource = "Excel"; stChanged = true; }

            // 记下"这个包声明的同步策略"，供教室机界面显示（教室机自己不编辑这个值）
            if (s2.PackageOverwriteManual != pkg.Manifest.OverrideIncludeManual)
            {
                s2.PackageOverwriteManual = pkg.Manifest.OverrideIncludeManual;
                stChanged = true;
            }

            if (stChanged) Store.SaveSettings(s2);

            if (!Store.EnablePersistence)
                res.Warnings.Add("当前为「游客模式」，改动不会保存，重启后失效。请用账号登录后再导入。");

            res.Ok = true;
            return res;
        }
        catch (Exception ex)
        {
            res.Error = "应用数据包失败：" + ex.Message;
            return res;
        }
    }

    static (DateOnly? from, DateOnly? to) ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return (null, null);

        // ★ 只能按 '~' 拆：日期本身含 '-'，用 Split('~','-') 会把 "2026-10-26~2026-11-01"
        //   拆成 ["2026","10","26","2026","11","01"]，解析出的区间完全错位，
        //   结果就是"本机旧调代课清不掉"。
        var parts = range.Split('~');
        if (parts.Length < 2) return (null, null);
        if (DateOnly.TryParse(parts[0].Trim(), out var f) && DateOnly.TryParse(parts[1].Trim(), out var t))
            return (f, t);
        return (null, null);
    }
}

// ======================================================================
//  包内数据契约
// ======================================================================

public class PackageManifest
{
    public int Schema { get; set; } = 1;
    public string Kind { get; set; } = "data";
    public DateTime BuiltAt { get; set; } = DateTime.Now;
    public string BuiltBy { get; set; } = "";
    public int ClassCount { get; set; }
    public int OverrideCount { get; set; }

    /// <summary>包内调代课覆盖的时间范围（yyyy-MM-dd~yyyy-MM-dd）；空 = 无调代课。</summary>
    public string OverrideRange { get; set; } = "";

    /// <summary>
    /// 本包对调代课是否"完整权威"：true 时导入方清掉本机全部 temp/remote 记录再写入包内记录
    /// （source 里没有的周 = 那一周没有调代课，旧的必须取消、还原底表）。
    /// 旧包没有该字段，反序列化后为 false → 退回"按区间清理"。
    /// </summary>
    public bool OverrideAuthoritative { get; set; }

    /// <summary>
    /// 是否连<b>本机手工调课</b>（Source=manual）一起覆盖。
    /// <para>true 时导入方把命中的手工调课标记为「已停用」（<see cref="LessonOverride.Suspended"/>），
    /// <b>不删除</b>，可由管理员在教室机上一键恢复。</para>
    /// <para><c>basePatch</c>（长期改课）任何时候都不参与清除。</para>
    /// <para>旧包没有该字段 → false → 保留手工调课（旧行为）。</para>
    /// </summary>
    public bool OverrideIncludeManual { get; set; }

    /// <summary>
    /// 内容版本号。**唯一的强制刷新手段**：策略开关变了但 Excel 没变时，
    /// 发布方把 Rev+1，摘要随之变化，各机才会重新 Apply。
    /// 旧包没有该字段 → 0。
    /// </summary>
    public int Rev { get; set; }

    /// <summary>包内用户表条目数（0 = 本包不带用户表，各机保持原样）。</summary>
    public int UserCount { get; set; }

    public int NoticeCount { get; set; }

    /// <summary>
    /// 整包摘要（对各文件 sha 再哈希）。
    /// 只改调代课时 schedule.json 的 sha 不变，必须用整包摘要才能检测到更新。
    /// </summary>
    public string DataSha256 { get; set; } = "";

    public List<PackageFile> Files { get; set; } = new();
}

public class PackageFile
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>包内携带的全局设置（白名单，导入时只写这几项）。</summary>
public class PackageMeta
{
    public string SemesterStartMonday { get; set; } = "";
    public string Theme { get; set; } = "";
    public ReminderSettings? Reminder { get; set; }
}
