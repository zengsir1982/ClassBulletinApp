using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>生成计划里一条账号的处理方式。</summary>
public enum TeacherAccountAction
{
    /// <summary>课表上有、用户表里没有 → 新建。</summary>
    Create,

    /// <summary>工具之前生成过、且没被手工改过 → 按当前课表刷新。</summary>
    Update,

    /// <summary>工具之前生成过、内容与新算的一致 → 什么都不做。</summary>
    Unchanged,

    /// <summary>同名账号已存在但不是本工具生成的（手工建的）→ 不覆盖。</summary>
    SkipExisting,

    /// <summary>工具生成过，但管理员手工改过 → 保留人工结果，不刷新。</summary>
    SkipManual
}

/// <summary>一条待生成 / 待刷新的账号草稿。<b>只描述，不落盘。</b></summary>
public class TeacherAccountDraft
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = UserRole.SubjectTeacher;
    public List<string> ClassNames { get; set; } = new();
    public List<string> HeadClassNames { get; set; } = new();
    public string Subject { get; set; } = "";

    /// <summary>课表算出来的科目（课时最多的那门）。用来判断"人工覆盖了没有"。</summary>
    public string AutoSubject { get; set; } = "";

    /// <summary>
    /// 这个人<b>实际任教</b>的全部科目（按课时从多到少，已排除班主任课）。
    /// <para>预览窗选中若干人时，把这些人教的科目排到批量下拉的最前面，省得在四十多门课里翻 —— 
    /// 只放主科不够用：兼教两门的人经常要按"另一门"归类。</para>
    /// </summary>
    public List<string> TaughtSubjects { get; set; } = new();

    /// <summary>科目是不是管理员在预览窗里手工指定的（而不是课表算的）。</summary>
    public bool SubjectManual { get; set; }

    public TeacherAccountAction Action { get; set; }

    /// <summary>已存在的账号对象（新建时为 null）。</summary>
    public AppUser? Existing { get; set; }

    public string RoleCaption => UserRole.Display(Role);

    public string ClassCaption => ClassNames.Count == 0 ? "—" : string.Join("、", ClassNames);

    public string HeadCaption => HeadClassNames.Count == 0 ? "—" : string.Join("、", HeadClassNames);

    /// <summary>★ 手工指定的科目后面挂个 <c>*</c>，让人一眼看出这不是课表算的。</summary>
    public string SubjectCaption =>
        string.IsNullOrEmpty(Subject) ? (SubjectManual ? "不限*" : "不限")
                                      : (SubjectManual ? Subject + "*" : Subject);

    /// <summary>科目来源的一句话说明（给列表 ToolTip 用）。</summary>
    public string SubjectSourceCaption =>
        SubjectManual
            ? $"手工指定为「{(Subject.Length == 0 ? "不限" : Subject)}」（课表算的是「{(AutoSubject.Length == 0 ? "不限" : AutoSubject)}」）"
            : (AutoSubject.Length == 0 ? "课表算不出主科（只教班主任课或没有任课科目）" : "按课时最多的科目自动判定");

    public string ActionCaption => Action switch
    {
        TeacherAccountAction.Create => "新增",
        TeacherAccountAction.Update => "更新",
        TeacherAccountAction.Unchanged => "无变化",
        TeacherAccountAction.SkipExisting => "跳过（已有账号）",
        TeacherAccountAction.SkipManual => "跳过（手工改过）",
        _ => "—"
    };
}

/// <summary>一次「从课表生成教师账号」的完整计划。</summary>
public class TeacherAccountPlan
{
    /// <summary>归一到比较形式的班主任课科目名（如 <c>班会</c>）。</summary>
    public List<string> HeadSubjects { get; set; } = new();

    public List<TeacherAccountDraft> Items { get; set; } = new();

    /// <summary>需要人看一眼的问题（0 命中、一班两位班主任、像场地名被跳过等）。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>底表里实际出现过的科目名（只读展示，方便核对班主任课到底叫什么）。</summary>
    public List<string> SubjectsInSchedule { get; set; } = new();

    public int TotalClasses { get; set; }

    /// <summary>命中班主任课的班级数。</summary>
    public int HeadHitClasses { get; set; }

    /// <summary>本次生效的「手工指定科目」条数（来自发布端本地记住的覆盖表）。</summary>
    public int OverrideApplied { get; set; }

    public int Count(TeacherAccountAction a) => Items.Count(i => i.Action == a);

    /// <summary>本次真正会写盘的条数（新增 + 更新）。</summary>
    public int ChangeCount => Count(TeacherAccountAction.Create) + Count(TeacherAccountAction.Update);

    public string Summary =>
        $"共 {TotalClasses} 个班，命中班主任 {HeadHitClasses} 个；"
        + $"将新增 {Count(TeacherAccountAction.Create)} 个、更新 {Count(TeacherAccountAction.Update)} 个、"
        + $"无变化 {Count(TeacherAccountAction.Unchanged)} 个、"
        + $"跳过 {Count(TeacherAccountAction.SkipExisting) + Count(TeacherAccountAction.SkipManual)} 个。"
        + (OverrideApplied > 0 ? $"（其中 {OverrideApplied} 个科目是手工指定）" : "");
}

/// <summary>
/// 从课表底表反推全校教师账号。
///
/// <para><b>口径</b>（用户 2026-11-23 拍板）：</para>
/// <list type="number">
/// <item>教师姓名来自课表底表（<see cref="ScheduleFile.Classes"/> 的 <c>Lesson.Teacher</c>），
/// 只读渲染用的那份数据，<b>不读</b>「教师个人课表」与调代课记录 ——
/// 后两者会把代课老师、以及被当成教师名的班级名混进来。</item>
/// <item>某班的<b>班主任课</b>（默认「班会」，可配多个科目名）由谁上，谁就是这个班的班主任；
/// 其余任教老师是科任老师。</item>
/// <item>角色：当至少一个班班主任 → <see cref="UserRole.Teacher"/>；否则 → <see cref="UserRole.SubjectTeacher"/>。</item>
/// <item>班级：<see cref="AppUser.ClassNames"/> = 全部任教班（发作业用）；
/// <see cref="AppUser.HeadClassNames"/> = 当班主任的班（改课表用）。</item>
/// <item>科目：默认取<b>课时最多</b>的那门课（班主任课不算任教科目）；管理员可在预览窗里
/// 逐个 / 批量改成别的科目或"不限"，改动记在发布端本地，下次生成自动沿用。</item>
/// <item>密码：<see cref="DefaultPassword"/>，只写 PBKDF2 哈希 + 盐，明文不落盘。</item>
/// <item>已有账号一律不覆盖；本工具生成过、且没被手工改过的才按指纹刷新。</item>
/// </list>
///
/// <para>★ 为什么规则只放在发布端、不进数据包：这套东西只影响「怎么建账号」，
/// 是一次性管理动作；塞进下发设置就得跟着包到处跑，改一次全校多一次同步。</para>
/// </summary>
public static class TeacherAccountGenerator
{
    /// <summary>默认的「班主任课」科目名。</summary>
    public const string DefaultHeadSubject = "班会";

    /// <summary>生成账号的初始密码。</summary>
    public const string DefaultPassword = "123";

    /// <summary>本工具写入的账号在 <see cref="AppUser.GeneratedFromSchedule"/> 里留的标记。</summary>
    public const string SourceTag = "schedule";

    static readonly char[] Separators = { ',', '，', '、', ';', '；', '/', '|', ' ', '\t' };

    /// <summary>把用户输入的科目名列表拆成归一化集合（空输入 → 默认「班会」）。</summary>
    public static List<string> ParseHeadSubjects(string? text)
    {
        string raw = string.IsNullOrWhiteSpace(text) ? DefaultHeadSubject : text;
        return raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                  .Select(BoardIds.NormalizeSubject)
                  .Where(s => s.Length > 0)
                  .Distinct(StringComparer.Ordinal)
                  .ToList();
    }

    /// <summary>
    /// 扫一遍课表，产出「要建 / 要刷哪些账号」的计划。<b>不写任何文件。</b>
    /// </summary>
    /// <param name="schedule">课表底表；为空或没有班级时返回带警告的空计划。</param>
    /// <param name="book">现有用户表（用来判断新增 / 跳过 / 刷新）。</param>
    /// <param name="headSubjectText">班主任课科目名，逗号分隔；空 = 「班会」。</param>
    /// <param name="subjectOverrides">
    /// 手工指定的科目：用户名 → 科目名（空串 = 不限）。来自发布端本地记住的覆盖表，
    /// 只影响本次计划，不随数据包下发。
    /// </param>
    public static TeacherAccountPlan Build(ScheduleFile? schedule, UserBook? book, string? headSubjectText,
                                           IReadOnlyDictionary<string, string>? subjectOverrides = null)
    {
        var plan = new TeacherAccountPlan { HeadSubjects = ParseHeadSubjects(headSubjectText) };
        book ??= new UserBook();

        if (schedule?.Classes == null || schedule.Classes.Count == 0)
        {
            plan.Warnings.Add("本机还没有课表：请先导入（或用自动更新拉取）全校总课表，再来生成教师账号。");
            return plan;
        }

        var headSet = new HashSet<string>(plan.HeadSubjects, StringComparer.Ordinal);
        var subjectNames = new SortedSet<string>(StringComparer.Ordinal);
        var byTeacher = new Dictionary<string, TeacherFacts>(StringComparer.Ordinal);
        var headByClass = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenClass = new HashSet<string>(StringComparer.Ordinal);
        var classCount = 0;
        var classesWithoutHead = new List<string>();
        var headConflicts = new List<string>();
        var filteredNames = new List<string>();

        foreach (var cs in schedule.Classes)
        {
            string cls = RealClassName(cs?.ClassName);
            if (cls.Length == 0) continue;
            if (seenClass.Add(cls)) classCount++;

            string headTeacher = "";

            foreach (var day in cs!.Data ?? new Dictionary<int, Dictionary<int, List<Lesson>>>())
            {
                foreach (var slot in day.Value ?? new Dictionary<int, List<Lesson>>())
                {
                    foreach (var lesson in slot.Value ?? new List<Lesson>())
                    {
                        string rawTeacher = (lesson.Teacher ?? "").Trim();
                        if (rawTeacher.Length == 0) continue;

                        string name = TeacherExtractor.NormalizeTeacherName(rawTeacher);
                        if (name.Length == 0)
                        {
                            // 名字里带数字/字母、过长、或含「室/楼/场」这类场地词 —— 记下来让人核对
                            if (filteredNames.Count < 8 && !filteredNames.Contains(rawTeacher))
                                filteredNames.Add(rawTeacher);
                            continue;
                        }

                        string rawSubject = (lesson.Subject ?? "").Trim();
                        if (rawSubject.Length > 0) subjectNames.Add(rawSubject);

                        if (!byTeacher.TryGetValue(name, out var facts))
                        {
                            facts = new TeacherFacts();
                            byTeacher[name] = facts;
                        }
                        facts.Classes.Add(cls);

                        // 科目口径：先去掉「-单/-双」尾巴再归一化，让「语文」「语文-单」算同一门
                        string subjectKey = BoardIds.NormalizeSubject(StripWeekSuffix(rawSubject));
                        if (subjectKey.Length > 0)
                        {
                            facts.SubjectHours.TryGetValue(subjectKey, out int h);
                            facts.SubjectHours[subjectKey] = h + 1;
                        }

                        if (IsHeadSubject(headSet, rawSubject))
                        {
                            if (headTeacher.Length == 0)
                            {
                                headTeacher = name;
                            }
                            else if (!string.Equals(headTeacher, name, StringComparison.Ordinal))
                            {
                                headConflicts.Add(
                                    $"{cls}：班主任课出现了两位老师（{headTeacher}、{name}），已按第一位「{headTeacher}」算。");
                            }
                        }
                    }
                }
            }

            if (headTeacher.Length == 0) classesWithoutHead.Add(cls);
            else headByClass[cls] = headTeacher;
        }

        // 把「当班主任的班」回填到对应老师身上
        foreach (var kv in headByClass)
            if (byTeacher.TryGetValue(kv.Value, out var facts))
                facts.Heads.Add(kv.Key);

        plan.TotalClasses = classCount;
        plan.HeadHitClasses = headByClass.Count;
        plan.SubjectsInSchedule = subjectNames.ToList();

        int overrideApplied = 0;

        // ---------- 逐人生成草稿 ----------
        foreach (var name in byTeacher.Keys.OrderBy(x => x, TeacherExtractor.ZhComparer()))
        {
            var facts = byTeacher[name];
            var classes = facts.Classes.OrderBy(x => x, ScheduleImporter.ClassNameComparer()).ToList();
            var heads = facts.Heads.OrderBy(x => x, ScheduleImporter.ClassNameComparer()).ToList();

            string role = heads.Count > 0 ? UserRole.Teacher : UserRole.SubjectTeacher;

            // 默认科目 = 课时最多的那门（班主任课不算，否则专职班主任会被填成"班会"）
            string auto = PickMainSubject(facts.SubjectHours, headSet);
            string subject = auto;
            bool manual = false;

            if (subjectOverrides != null && subjectOverrides.TryGetValue(name, out var ov))
            {
                subject = BoardIds.NormalizeSubject(ov ?? "");
                manual = true;
                overrideApplied++;
            }

            string display = BuildDisplayName(heads, classes);

            var draft = new TeacherAccountDraft
            {
                Username = name,
                DisplayName = display,
                Role = role,
                ClassNames = classes,
                HeadClassNames = heads,
                Subject = subject,
                AutoSubject = auto,
                SubjectManual = manual,
                TaughtSubjects = facts.SubjectHours
                    .Where(kv => kv.Value > 0 && !IsHeadSubject(headSet, kv.Key))
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key)
                    .ToList()
            };

            var exist = book.Find(name);
            if (exist == null)
            {
                draft.Action = TeacherAccountAction.Create;
            }
            else
            {
                draft.Existing = exist;
                if (string.IsNullOrWhiteSpace(exist.GeneratedFromSchedule))
                {
                    // 手工建的账号：不碰。同名很可能是同一个人，但"谁建的谁负责"。
                    draft.Action = TeacherAccountAction.SkipExisting;
                }
                else
                {
                    string last = (exist.GenerationFingerprint ?? "").Trim();
                    string current = Fingerprint(exist.Role, exist.ClassNames, exist.HeadClassNames,
                                                 exist.Subject, exist.DisplayName);

                    if (last.Length == 0 || !string.Equals(current, last, StringComparison.Ordinal))
                    {
                        // 指纹对不上 = 上次生成之后有人手工改过 → 保留人工结果
                        draft.Action = TeacherAccountAction.SkipManual;
                    }
                    else
                    {
                        string want = Fingerprint(role, classes, heads, subject, display);
                        draft.Action = string.Equals(want, current, StringComparison.Ordinal)
                            ? TeacherAccountAction.Unchanged
                            : TeacherAccountAction.Update;
                    }
                }
            }

            plan.Items.Add(draft);
        }

        plan.OverrideApplied = overrideApplied;

        // ---------- 警告 ----------
        if (plan.HeadHitClasses == 0)
        {
            plan.Warnings.Insert(0,
                $"没有任何班级命中「{string.Join("、", plan.HeadSubjects)}」这门课，所以一个班主任都认不出来。"
                + "请照下面「底表科目名」里的实际写法改成学校真正用的班主任课（如「团队活动」），再点重新预览。");
        }
        else if (classesWithoutHead.Count > 0)
        {
            plan.Warnings.Add(
                $"{classesWithoutHead.Count} 个班没排「{string.Join("、", plan.HeadSubjects)}」，"
                + $"这些班不会有班主任账号：{Sample(classesWithoutHead, 6)}");
        }

        if (headConflicts.Count > 0) plan.Warnings.AddRange(headConflicts);

        if (filteredNames.Count > 0)
        {
            plan.Warnings.Add("以下任课记录不像人名（含数字 / 字母、超过 6 字或含场地词），已跳过："
                              + string.Join("、", filteredNames));
        }

        if (plan.Items.Count == 0)
            plan.Warnings.Add("课表里没提取到任何教师，请确认这份课表是不是总课表（每格都填了任课教师）。");

        return plan;
    }

    /// <summary>
    /// 把计划写进用户表（新增 + 更新）。<b>调用方负责落盘。</b>
    /// </summary>
    /// <returns>实际新增 + 更新的条数。</returns>
    public static int Apply(TeacherAccountPlan? plan, UserBook? book)
    {
        if (plan == null || book == null) return 0;

        int changed = 0;
        foreach (var d in plan.Items)
        {
            if (d.Action != TeacherAccountAction.Create && d.Action != TeacherAccountAction.Update)
                continue;

            var u = d.Existing;
            if (u == null)
            {
                u = new AppUser { Username = d.Username, CreatedAt = DateTime.Now, Enabled = true };
                book.Users.Add(u);
            }

            u.DisplayName = d.DisplayName;
            u.Role = d.Role;
            u.ClassNames = new List<string>(d.ClassNames);
            u.HeadClassNames = new List<string>(d.HeadClassNames);
            u.Subject = d.Subject;
            u.GeneratedFromSchedule = SourceTag;
            u.GenerationFingerprint = Fingerprint(u.Role, u.ClassNames, u.HeadClassNames, u.Subject, u.DisplayName);

            // ★ 只有新建才设初始密码：刷新已有账号绝不能把人家改过的密码冲回 123
            if (d.Action == TeacherAccountAction.Create)
                PasswordHasher.SetPassword(u, DefaultPassword);

            changed++;
        }
        return changed;
    }

    /// <summary>按账号当前关键字段算指纹（用于判断"生成后有没有被手工改过"）。</summary>
    public static string Fingerprint(string? role, IEnumerable<string>? classNames,
                                    IEnumerable<string>? headClassNames, string? subject, string? displayName)
    {
        string raw = string.Join("\u001f",
            (role ?? "").Trim().ToLowerInvariant(),
            JoinSorted(classNames),
            JoinSorted(headClassNames),
            BoardIds.NormalizeSubject(subject),
            (displayName ?? "").Trim());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
                      .ToLowerInvariant()[..16];
    }

    /// <summary>按账号当前字段算指纹（重载，省得到处拆字段）。</summary>
    public static string Fingerprint(AppUser? u)
        => u == null ? "" : Fingerprint(u.Role, u.ClassNames, u.HeadClassNames, u.Subject, u.DisplayName);

    /// <summary>
    /// 挑「主科」：课时最多的一门。<b>班主任课不参与</b>——否则带班又只教班会的老师
    /// 会被填成"班会"，科目限权就没意义了。
    /// <para>并列时按科目名的码位序取第一个：必须确定，否则同一份课表每次算出不同结果，
    /// 指纹就一直对不上、账号每次都被判成"要更新"。</para>
    /// </summary>
    static string PickMainSubject(Dictionary<string, int> hours, HashSet<string> headSet)
    {
        string best = "";
        int bestHours = 0;
        foreach (var kv in hours)
        {
            if (kv.Value <= 0) continue;
            if (IsHeadSubject(headSet, kv.Key)) continue;

            if (kv.Value > bestHours
                || (kv.Value == bestHours && best.Length > 0
                    && string.CompareOrdinal(kv.Key, best) < 0))
            {
                best = kv.Key;
                bestHours = kv.Value;
            }
        }
        return best;
    }

    // ================== 内部小工具 ==================

    /// <summary>排序 + 去重后拼接：指纹不受班级 / 科目录入顺序影响。</summary>
    static string JoinSorted(IEnumerable<string>? items)
        => items == null
            ? ""
            : string.Join(",", items.Select(x => (x ?? "").Trim())
                                    .Where(x => x.Length > 0)
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>
    /// 该科目是不是「班主任课」。先精确比，再容忍「xx-单 / xx-双」这种单双周写法。
    /// <para>★ 只做这两种匹配，不做包含 / 模糊 —— 猜错一次就是全校班主任认错人。</para>
    /// </summary>
    static bool IsHeadSubject(HashSet<string> headSet, string? rawSubject)
    {
        string normalized = BoardIds.NormalizeSubject(rawSubject);
        if (normalized.Length == 0) return false;
        if (headSet.Contains(normalized)) return true;

        string stripped = BoardIds.NormalizeSubject(StripWeekSuffix(rawSubject));
        return stripped.Length > 0 && headSet.Contains(stripped);
    }

    /// <summary>去掉科目名尾部的「-单」「-双」（单双周拆开写的课表很常见）。</summary>
    static string StripWeekSuffix(string? subject)
    {
        string s = (subject ?? "").Trim();
        if (s.EndsWith("-单", StringComparison.Ordinal) || s.EndsWith("-双", StringComparison.Ordinal))
            s = s[..^2].TrimEnd('-', ' ');
        return s;
    }

    /// <summary>去掉手动录入班的「（手录）」尾巴，让账号里的班名与导入班一致。</summary>
    static string RealClassName(string? name)
    {
        string s = (name ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.EndsWith(ClassSchedule.LegacyManualSuffix, StringComparison.Ordinal))
            return s[..^ClassSchedule.LegacyManualSuffix.Length].Trim();
        if (s.EndsWith(ClassSchedule.ManualSuffix, StringComparison.Ordinal))
            return s[..^ClassSchedule.ManualSuffix.Length].Trim();
        return s;
    }

    /// <summary>显示名：「八(22)班 班主任」/「6个班 科任」。用户名列已有姓名，这里不重复。</summary>
    static string BuildDisplayName(List<string> heads, List<string> classes)
    {
        if (heads.Count == 1) return heads[0] + " 班主任";
        if (heads.Count > 1) return $"{heads[0]}等{heads.Count}个班 班主任";
        if (classes.Count == 1) return classes[0] + " 科任";
        return classes.Count + "个班 科任";
    }

    static string Sample(List<string> items, int n)
        => items.Count <= n
            ? string.Join("、", items)
            : string.Join("、", items.Take(n)) + $" 等 {items.Count} 个";

    /// <summary>扫描过程中的「一个老师身上攒到的东西」。</summary>
    class TeacherFacts
    {
        public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Heads { get; } = new(StringComparer.Ordinal);

        /// <summary>归一化科目名 → 课时数（格数）。决定"主科"用。</summary>
        public Dictionary<string, int> SubjectHours { get; } = new(StringComparer.Ordinal);
    }
}
