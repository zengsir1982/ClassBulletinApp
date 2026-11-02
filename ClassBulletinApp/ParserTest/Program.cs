using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScheduleCore.Models;
using ScheduleCore.Services;

var precedenceMode = args.Length > 1 && args[1] == "precedence";
if (precedenceMode)
{
    RunPrecedenceTests();
    return;
}

var packPolicyMode = args.Length > 1 && args[1] == "packpolicy";
if (packPolicyMode)
{
    RunPackPolicyTests();
    return;
}

var policyDefaultsMode = args.Length > 1 && args[1] == "policydefaults";
if (policyDefaultsMode)
{
    RunPolicyDefaultTests();
    return;
}

var artifactMode = args.Length > 1 && args[1] == "artifacts";
if (artifactMode)
{
    RunArtifactTests();
    return;
}

var updateVerMode = args.Length > 1 && args[1] == "updatever";
if (updateVerMode)
{
    RunUpdateVersionTests();
    return;
}

var path = args.Length > 0 ? args[0] :
    @"F:\study\班级公布栏\全校班级临时总课表.xlsx";

Console.WriteLine("导入文件: " + path);
var sf = ScheduleImporter.Import(path);
Console.WriteLine($"班级数: {sf.Classes.Count}");
Console.WriteLine($"导入时间: {sf.ImportedAt:yyyy-MM-dd HH:mm}");

foreach (var c in sf.Classes.Take(3))
{
    Console.WriteLine($"\n=== {c.ClassName} 星期一 ===");
    if (c.Data.TryGetValue(0, out var day))
        foreach (var p in day.Keys.OrderBy(k => k))
            foreach (var l in day[p])
                Console.WriteLine($"  第{p}节 {l.TimeRange} {l.Subject} / {l.Teacher} {(l.Location)} {(l.Week == 1 ? "单周" : l.Week == 2 ? "双周" : "")}");
}

Console.WriteLine("\n=== 导入临时课表（调/代 标记）===");
var (tsf, overrides) = ScheduleImporter.ImportTempSchedule(path);
Console.WriteLine($"基础课表班级数: {tsf.Classes.Count}");
Console.WriteLine($"调/代覆盖条数: {overrides.Count}");
int tiao = overrides.Count(o => o.Type == "调");
int dai = overrides.Count(o => o.Type == "代");
Console.WriteLine($"  其中 调课={tiao}  代课={dai}");
Console.WriteLine("  样例（含地点/组合标记）:");
foreach (var o in overrides.Where(o => !string.IsNullOrEmpty(o.Location) || o.Subject.Contains("体育")).Take(6))
    Console.WriteLine($"    {o.ClassName} 周{o.DayIdx + 1} 第{o.Period}节 [{o.Type}] {o.Subject} / {o.Teacher} {o.Location}");

// 验证基础课表已剥离 (调)/(代) 标记
var sample = tsf.Classes.First(c => c.ClassName == "八年级(14)班");
if (sample.Data.TryGetValue(0, out var d0))
    foreach (var p in d0.Keys.OrderBy(k => k))
        foreach (var l in d0[p])
            if (l.Subject.Contains("("))
                Console.WriteLine($"  [警告] 仍有括号残留: {l.Subject}");
Console.WriteLine("  标记剥离检查: 若上方无 [警告] 则科目名已干净。");

Console.WriteLine("\nDONE");

// ===== 单班导入校验（可选：第二个参数传 "single"） =====
if (args.Length > 1 && args[1] == "single")
{
    Console.WriteLine("\n=== 单班导入校验 ===");
    var singlePath = @"F:\study\班级公布栏\八年级X班-班级课程表.xlsx";
    var ssf = ScheduleImporter.ImportSingleClass(singlePath);
    Console.WriteLine($"班级数: {ssf.Classes.Count}");
    foreach (var c in ssf.Classes)
    {
        Console.WriteLine($"=== {c.ClassName} ===");
        for (int d = 0; d < 5; d++)
        {
            if (c.Data.TryGetValue(d, out var day))
            {
                string[] names = { "周一", "周二", "周三", "周四", "周五" };
                Console.WriteLine($" {names[d]}:");
                foreach (var p in day.Keys.OrderBy(k => k))
                    foreach (var l in day[p])
                        Console.WriteLine($"   第{p}节 {l.TimeRange} {l.Subject} / {l.Teacher} {l.Location}");
            }
        }
    }
    Console.WriteLine("SINGLE DONE");
}

// ===== 教师个人课表导入校验（第三个参数传 "teacher"） =====
if (args.Length > 1 && args[1] == "teacher")
{
    Console.WriteLine("\n=== 教师课表导入校验 ===");
    var teacherPath = @"F:/study/班级公布栏/某某老师-教师课程表.xlsx";
    var ts = ScheduleImporter.ImportTeacherSchedule(teacherPath);
    Console.WriteLine($"教师数: {ts.Classes.Count}");
    foreach (var c in ts.Classes)
    {
        Console.WriteLine($"=== 教师: {c.ClassName} ===");
        for (int d = 0; d < 5; d++)
        {
            if (c.Data.TryGetValue(d, out var day))
            {
                string[] names = { "周一", "周二", "周三", "周四", "周五" };
                Console.WriteLine($" {names[d]}:");
                foreach (var p in day.Keys.OrderBy(k => k))
                    foreach (var l in day[p])
                        // 教师课表：Teacher 字段实为所教班级
                        Console.WriteLine($"   第{p}节 {l.TimeRange} {l.Subject} / 班级={l.Teacher} {l.Location}");
            }
        }
    }
    Console.WriteLine("TEACHER DONE");
}

// ===== 教师提取校验（第二个参数传 "extract"） =====
if (args.Length > 1 && args[1] == "extract")
{
    Console.WriteLine("\n=== 从全校课表反向整理教师个人课表 ===");
    var teachers = TeacherExtractor.Build(sf);
    Console.WriteLine($"提取到的教师人数: {teachers.Classes.Count}");
    var sampleT = teachers.Classes.FirstOrDefault(c => c.ClassName.Contains("曾"))
                  ?? teachers.Classes.FirstOrDefault();
    if (sampleT != null)
    {
        Console.WriteLine($"=== 样例教师: {sampleT.ClassName} ===");
        string[] names = { "周一", "周二", "周三", "周四", "周五" };
        for (int d = 0; d < 5; d++)
        {
            if (!sampleT.Data.TryGetValue(d, out var day)) continue;
            Console.WriteLine($" {names[d]}:");
            foreach (var p in day.Keys.OrderBy(k => k))
                foreach (var l in day[p])
                    Console.WriteLine($"   第{p}节 {l.TimeRange} {l.Subject} / 班级={l.Teacher} {l.Location}");
        }
    }
    Console.WriteLine($"全校教师课时总数: {teachers.Classes.Sum(c => c.Data.Values.Sum(d => d.Values.Sum(v => v.Count)))}");
    Console.WriteLine("EXTRACT DONE");

    // 带第三个参数 "save" 时才落盘：用于在没有重新导入全校课表的情况下，
    // 从已有的全校班课表反向重建教师个人课表（App 导入时也会自动做这一步）。
    if (args.Contains("save"))
    {
        Store.SaveTeacherSchedule(teachers);
        Console.WriteLine($"已写入教师课表：教师 {teachers.Classes.Count} 人 -> " + Store.AppDir);
    }
}

// ===== 回归：覆盖来源优先级（manual 压过 temp，恢复后仍可见）—— r14 修复 =====
static void RunPrecedenceTests()
{
    Console.WriteLine("\n=== 覆盖来源优先级回归测试（r14）===");
    var date = new DateTime(2026, 11, 2);          // 某周一
    var vf = DateOnly.FromDateTime(date);
    var vt = vf.AddDays(6);

    LessonOverride M(string src, bool suspended, DateTime updated) => new LessonOverride
    {
        ClassName = "测试班", DayIdx = 0, Period = 1, Source = src,
        Subject = src == "manual" ? "手动课" : "临时课",
        ValidFrom = vf, ValidTo = vt, UpdatedAt = updated, Suspended = suspended
    };

    string Key() => OverrideExpander.CellKey("测试班", 0, 1);

    // 场景A：manual(旧) + temp(新)，manual 活动 → 应选 manual（手动 > 导入）
    var a = new List<LessonOverride>
    {
        M("manual", false, date.AddHours(-2)),
        M("temp", false, date.AddHours(1))
    };
    Assert(OverrideExpander.MapForDate(a, "测试班", date).TryGetValue(Key(), out var ra) && ra.Source == "manual",
        "场景A: 活动的 manual 应压过更新更晚的 temp");

    // 场景B：manual 被停用 + temp 活动 → 应选 temp（停用记录不显示）
    var b = new List<LessonOverride>
    {
        M("manual", true, date.AddHours(-2)),
        M("temp", false, date.AddHours(1))
    };
    Assert(OverrideExpander.MapForDate(b, "测试班", date).TryGetValue(Key(), out var rb) && rb.Source == "temp",
        "场景B: 停用的 manual 不显示，应回退到 temp");

    // 场景C（本次修复核心）：恢复后的 manual(UpdatedAt 仍旧) + temp(新) → 应选 manual
    var c = new List<LessonOverride>
    {
        M("manual", false, date.AddHours(-2)),   // 恢复后未刷新时间
        M("temp", false, date.AddHours(1))
    };
    Assert(OverrideExpander.MapForDate(c, "测试班", date).TryGetValue(Key(), out var rc2) && rc2.Source == "manual",
        "场景C: 恢复后的 manual 即使 UpdatedAt 更旧也应压过 temp");

    Console.WriteLine("PRECEDENCE DONE");
}

static void Assert(bool cond, string msg)
{
    Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + msg);
    if (!cond) Environment.Exit(1);
}

// ===== 回归：角色权限必须随数据包下发（r15 修复） =====
// 现场反馈：发布机在「角色权限」里改了电教管理员权限，教室机收不到。
// 根因是 SchedulePackage.Export 重新 new UserBook 时只拷了 Users 列表、丢了 Policies。
static void RunPackPolicyTests()
{
    Console.WriteLine("\n=== 角色权限随包下发回归测试（r15）===");

    UserBook MakeBook(bool studentCanEditSchedule)
    {
        var book = new UserBook();
        // 两个账号（带哈希/盐，避免被 Inspect 丢弃）
        var admin = new AppUser { Username = "admin", Role = UserRole.Admin };
        var local = new AppUser { Username = "local", Role = UserRole.Student };
        PasswordHasher.SetPassword(admin, "000");
        PasswordHasher.SetPassword(local, "000");
        book.Users.Add(admin);
        book.Users.Add(local);
        // 自定义权限：班级主任默认即可；电教管理员按入参决定（默认是全关）
        book.Policies[UserRole.Teacher] = RolePolicy.DefaultFor(UserRole.Teacher);
        book.Policies[UserRole.Student] = new RolePolicy { EditSchedule = studentCanEditSchedule };
        book.EnsurePolicyDefaults();
        return book;
    }

    ScheduleFile MakeSchedule()
    {
        var sf = new ScheduleFile();
        sf.Classes.Add(new ClassSchedule { ClassName = "测试班" });
        return sf;
    }

    string pkgA = Path.Combine(Path.GetTempPath(), "cbpack_polA.cbpack");
    string pkgB = Path.Combine(Path.GetTempPath(), "cbpack_polB.cbpack");
    if (File.Exists(pkgA)) File.Delete(pkgA);
    if (File.Exists(pkgB)) File.Delete(pkgB);

    // A：电教管理员不可改课表；B：电教管理员可改课表（仅权限不同，其余完全一致）
    var exA = SchedulePackage.Export(pkgA, MakeSchedule(), Array.Empty<LessonOverride>(),
        Array.Empty<Notification>(), new AppSettings(), "test", userBook: MakeBook(false));
    var exB = SchedulePackage.Export(pkgB, MakeSchedule(), Array.Empty<LessonOverride>(),
        Array.Empty<Notification>(), new AppSettings(), "test", userBook: MakeBook(true));
    Assert(exA.Ok && exB.Ok, "A/B 两个包都应导出成功");

    // 解出 B，验证权限确实进了包
    var insB = SchedulePackage.Inspect(pkgB);
    Assert(insB.Ok, "B 包应能解包");
    Assert(insB.Users != null && insB.Users.Policies.ContainsKey(UserRole.Student),
        "B 包的用户表应带 student 角色的 Policies");
    Assert(insB.Users!.PolicyFor(UserRole.Student).EditSchedule == true,
        "B 包里电教管理员应可改课表（随包下发）");
    Assert(insB.Users.PolicyFor(UserRole.Teacher).EditSchedule == true,
        "B 包里班主任权限也应保留");

    // 关键：仅权限不同 → 整包摘要必须不同，否则教室机判定已是最新、不会重新同步
    string ReadSha(string pkg)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(pkg);
        var e = z.GetEntry("manifest.json")!;
        using var s = e.Open();
        using var r = new StreamReader(s);
        var man = System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(
            r.ReadToEnd(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return man?.DataSha256 ?? "";
    }
    Assert(ReadSha(pkgA) != ReadSha(pkgB),
        "仅权限不同的两个包，整包摘要必须不同（否则教室机不会重新同步）");

    File.Delete(pkgA); File.Delete(pkgB);
    Console.WriteLine("PACKPOLICY DONE");
}

// ===== 回归：角色默认权限 + 老用户表升级补齐（r16） =====
// 现场需求：新增「科任老师」角色；班主任/电教默认能改本班课表+作业+恢复停用+自动更新；
// 数据包/备份恢复单独成一项能力并默认给班主任。升级不能让老教室机行为错乱。
static void RunPolicyDefaultTests()
{
    Console.WriteLine("\n=== 角色默认权限 / 升级补齐回归（r16）===");

    var teacher = RolePolicy.DefaultFor(UserRole.Teacher);
    Assert(teacher.EditSchedule && teacher.EditBoard && teacher.RestoreSuspended,
        "班主任默认能改本班课表 / 作业 / 恢复停用调课");
    Assert(teacher.ManagePackage, "班主任默认能用数据包导入导出 / 备份恢复");
    Assert(!teacher.ManageData, "班主任默认不能导入总课表 / 清除调课");
    Assert(!teacher.SendSchoolNotice, "班主任默认不能发全校通知（需管理员授权）");
    Assert(teacher.ManageUpdate, "班主任默认可改自动更新设置");

    var subject = RolePolicy.DefaultFor(UserRole.SubjectTeacher);
    Assert(UserRole.IsValid(UserRole.SubjectTeacher), "subject 必须是合法角色");
    Assert(subject.EditBoard, "科任老师默认能改作业 / 缺交名单");
    Assert(!subject.EditSchedule, "科任老师默认不能改课表");
    Assert(!subject.ManageData && !subject.ManagePackage && !subject.ImportTemp,
        "科任老师默认不能导数据");
    Assert(!subject.SendSchoolNotice, "科任老师默认不能发全校通知");
    Assert(subject.ManageUpdate, "科任老师默认可改自动更新设置（所有账号都能改本机更新来源）");

    var student = RolePolicy.DefaultFor(UserRole.Student);
    Assert(student.EditSchedule && student.EditBoard && student.RestoreSuspended && student.ManageUpdate,
        "电教管理员默认能改本班课表 / 作业 / 恢复停用调课 / 自动更新设置");
    Assert(!student.ManagePackage, "电教管理员默认不能收发数据包");
    Assert(!student.SendSchoolNotice, "电教管理员默认不能发全校通知");

    // 升级路径：Schema=3 的老表（没有 subject 策略、班主任没有 ManagePackage）
    var book = new UserBook { Schema = 3 };
    book.Policies[UserRole.Teacher] = new RolePolicy { EditSchedule = true, EditBoard = true, ManageUpdate = true };
    book.Policies[UserRole.Student] = new RolePolicy { EditSchedule = true, ManageUpdate = true };

    Assert(book.EnsurePolicyDefaults(), "老用户表补齐后应报告有改动");
    Assert(book.Schema == 4, "补齐后 Schema 应为 4");
    Assert(book.FindPolicy(UserRole.SubjectTeacher) != null, "升级后应自动补上科任老师策略");
    Assert(book.FindPolicy(UserRole.SubjectTeacher)!.EditBoard, "补上的科任老师策略默认能改作业");
    Assert(book.PolicyFor(UserRole.Teacher).ManagePackage, "升级后班主任应补开数据包 / 备份恢复");
    Assert(!book.PolicyFor(UserRole.Teacher).SendSchoolNotice, "升级不能擅自打开全校通知");

    // 幂等：再来一次不应产生新改动（否则每次保存都会改写 users.json → 摘要天天变）
    Assert(!book.EnsurePolicyDefaults(), "第二次补齐应无改动（幂等）");

    Console.WriteLine("POLICYDEFAULTS DONE");
}

// ===== 回归：副本文件过滤（r17）=====
// 现场背景：三台发布机都能往共享目录拖课表，两个人同时打开同一份 Excel 时，
// Excel 会生成「xxx (用户的冲突副本).xlsx」。这类文件同样以 .xlsx 结尾，
// 若不排除会被当成真课表导入，且按文件名排序后可能盖掉正本 —— 发布报告里看不出异常。
/// <summary>「软件更新」版本比较回归：Gitee 标签 vs 当前版本（r22）。</summary>
static void RunUpdateVersionTests()
{
    Console.WriteLine("\n=== 版本号比较回归（r22）===");

    // 真新版本
    Assert(AppVersionCompare.IsNewer("v1.0.6", "1.0.5"), "v1.0.6 > 1.0.5");
    Assert(AppVersionCompare.IsNewer("v1.0.10", "1.0.9"), "v1.0.10 > 1.0.9（不能按字符串比）");
    Assert(AppVersionCompare.IsNewer("2.0.0", "1.9.9"), "2.0.0 > 1.9.9");
    Assert(AppVersionCompare.IsNewer("1.0.6.1", "1.0.6"), "1.0.6.1 > 1.0.6");
    Assert(AppVersionCompare.IsNewer("v1.0.6-beta", "1.0.5"), "带后缀的标签也能比");

    // 同版本 / 更旧
    Assert(!AppVersionCompare.IsNewer("v1.0.5", "1.0.5"), "同版本不算新");
    Assert(!AppVersionCompare.IsNewer("1.0.5.0", "1.0.5"), "1.0.5.0 与 1.0.5 等价");
    Assert(!AppVersionCompare.IsNewer("v1.0.4", "1.0.5"), "1.0.4 不比 1.0.5 新");
    Assert(!AppVersionCompare.IsNewer("v0.9.9", "1.0.5"), "0.9.9 不比 1.0.5 新");

    // 归一化
    Assert(AppVersionCompare.Clean("v1.0.5") == "1.0.5", "Clean 去掉 v 前缀");
    Assert(AppVersionCompare.Clean("1.0.5-beta") == "1.0.5", "Clean 截掉后缀");
    Assert(AppVersionCompare.Clean("") == "", "Clean 空串");
}

static void RunArtifactTests()
{
    Console.WriteLine("\n=== 副本文件过滤回归（r17）===");

    // 正例：Office / WPS / 手工复制留下的副本命名
    foreach (var n in new[]
    {
        "中山市东凤中学2026-2027第一学期课程表(八年级)-全校班级总课表 (ZENGSIR 的冲突副本).xlsx",
        "中山市东凤中学2026-2027第一学期课程表(八年级)-全校班级总课表 (用户的冲突副本).xlsx",
        "八年级总课表 - 副本.xlsx",
        "八年级总课表 - 副本 (2).xlsx",
        "2026-11-02_临时课表_八年级 - 副本.xlsx",
        "八年级总课表 - 复制.xlsx",
        "Copy of 八年级总课表.xlsx",
        "八年级总课表 copy.xlsx",
        "八年级总课表~RF12345.xlsx",
    })
        Assert(SourcePublisher.IsArtifactName(n), "应判为副本：" + n);

    // 反例：真实课表名里带年份、年级括号，绝不能误杀
    foreach (var n in new[]
    {
        "中山市东凤中学2026-2027第一学期课程表(八年级)-全校班级总课表.xlsx",
        "中山市东凤中学2026-2027第一学期课程表(九年级)-全校班级总课表.xlsx",
        "2026-11-02_临时课表_八年级.xlsx",
        "八年级(14)班-班级课程表.xlsx",
    })
        Assert(!SourcePublisher.IsArtifactName(n), "不应判为副本：" + n);

    // 端到端：目录里混入副本，预检必须跳过并列出来
    var dir = Path.Combine(Path.GetTempPath(), "cbartifact-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try
    {
        foreach (var n in new[] { "七年级总课表.xlsx", "八年级总课表.xlsx", "九年级总课表.xlsx" })
            File.WriteAllText(Path.Combine(dir, n), "x");
        File.WriteAllText(Path.Combine(dir, "八年级总课表 - 副本.xlsx"), "x");

        var p = SourcePublisher.Scan(dir);
        Assert(p.Ok, "预检应通过（有 3 份正本）");
        Assert(p.BaseFiles.Count == 3, $"正本应为 3 份，实际 {p.BaseFiles.Count}");
        Assert(p.IgnoredFiles.Count == 1, $"副本应被跳过 1 个，实际 {p.IgnoredFiles.Count}");
        Assert(p.HasRisk, "发现副本时预检应标记有风险，才能提醒用户去清理");

        string f1 = SourcePublisher.SourceFingerprint(dir);
        File.Delete(Path.Combine(dir, "八年级总课表 - 副本.xlsx"));
        string f2 = SourcePublisher.SourceFingerprint(dir);
        Assert(f1.Length > 0 && f1 != f2,
            "清理副本后指纹必须变化（否则各机会认为没有新内容，一直不重新发布）");

        // 只剩副本时必须明确报错，不能当成"目录空了"糊过去
        var dir2 = Path.Combine(dir, "only");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "七年级总课表 - 副本.xlsx"), "x");
        var p2 = SourcePublisher.Scan(dir2);
        Assert(!p2.Ok && p2.Message.Contains("副本"), "只剩副本时应报「只有副本文件」");
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    Console.WriteLine("ARTIFACTS DONE");
}
