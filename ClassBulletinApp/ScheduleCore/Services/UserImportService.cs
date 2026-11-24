using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>一行导入计划要做的事。</summary>
public enum UserImportKind
{
    /// <summary>新建账号。</summary>
    Add,
    /// <summary>改已有账号的若干字段。</summary>
    Update,
    /// <summary>跳过（缺密码、角色认不出、是班主任却没班级…）。</summary>
    Skip
}

/// <summary>
/// 导入计划的一行。<b>只描述"打算做什么"，不碰 <see cref="UserBook"/></b> ——
/// 用户点确认后才由 <see cref="UserImportService.Apply"/> 统一应用，
/// 这样"预览"和"写盘"不会因为中途抛异常而做了一半，取消时一个字节都没动。
/// </summary>
public sealed class UserImportPlan
{
    public UserImportKind Kind;

    public string Username = "";

    /// <summary>一句人话说明：改了什么 / 为什么跳过。</summary>
    public string Note = "";

    /// <summary>要改的目标账号（新增时为 null）。</summary>
    public AppUser? Target;

    /// <summary>新增时的账号对象（已设好字段与密码哈希）。</summary>
    public AppUser? Fresh;

    // ---- 载荷：null = 这一项不动（空单元格一律不动，避免"顺手清空"）----
    public string? DisplayName;
    public string? Role;
    public string? Subject;
    public string? Password;
    public List<string>? Classes;
    public List<string>? HeadClasses;
    public bool? Enabled;

    /// <summary>这一行是否真的会写盘（跳过行不算）。</summary>
    public bool Applies => Kind != UserImportKind.Skip && (Kind == UserImportKind.Add ? Fresh != null : Target != null);
}

/// <summary>一次导入解析出来的全部结果（供预览窗直接显示）。</summary>
public sealed class UserImportResult
{
    public List<UserImportPlan> Plans { get; } = new();

    /// <summary>与现有资料完全一致、不需要改的行数。</summary>
    public int Same;
    public int Add;
    public int Update;
    /// <summary>会被停用的行数（含新增就停用的）。</summary>
    public int Disable;
    /// <summary>会被启用的行数。</summary>
    public int Enable;

    /// <summary>按这张表改完会不会一个启用的管理员都不剩（应用时会自动兜底）。</summary>
    public bool LoseAllAdmins;

    public int Skip => Plans.Count(p => p.Kind == UserImportKind.Skip);
    public bool AnyChange => Plans.Any(p => p.Applies);
}

/// <summary>
/// 账号名单的导入：读表 → 按表头认列 → 比对成计划 → （人工确认后）应用。
///
/// <para><b>为什么认表头而不是认列位置</b>：拿到导出的表之后，人一定会插一列"备注"、
/// 或者把密码列挪个位置。按位置读的话，一挪就整张表错位——把显示名写进用户名、
/// 把角色写进密码，而界面只会说"更新 213 个"。按列名认，挪列、插列都不影响；
/// 认不出表头的老表格自动退回位置模式，升级前后照样能导。</para>
///
/// <para><b>空单元格＝不动，写「-」＝清空</b>：一张只改密码的表不该把所有人的班级洗掉。
/// 想真的清空某一项，必须写一个可见的记号，不能靠"空着"来表达。</para>
/// </summary>
public static class UserImportService
{
    /// <summary>可被识别的字段。</summary>
    public enum Field { User, Display, Role, Classes, Password, Subject, Heads, Enabled }

    /// <summary>一条"字段 → 列号"对照表；-1 表示这个字段表里没有（没有就不动它）。</summary>
    public sealed class ColumnMap
    {
        readonly Dictionary<Field, int> _m = new();

        public bool HeaderMode;

        public int this[Field f] => _m.TryGetValue(f, out int i) ? i : -1;

        public bool Has(Field f) => this[f] >= 0;

        public void Set(Field f, int i)
        {
            if (!_m.ContainsKey(f)) _m[f] = i;
        }
    }

    /// <summary>表头别名。认得越宽，老师自己改表头写法越不容易导入失败。</summary>
    static readonly Dictionary<string, Field> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["用户名"] = Field.User, ["账号"] = Field.User, ["帐号"] = Field.User,
        ["登录名"] = Field.User, ["登录账号"] = Field.User, ["登录帐号"] = Field.User,
        ["用户"] = Field.User, ["username"] = Field.User, ["user"] = Field.User, ["account"] = Field.User,

        ["显示名"] = Field.Display, ["显示名称"] = Field.Display, ["姓名"] = Field.Display,
        ["名字"] = Field.Display, ["displayname"] = Field.Display, ["name"] = Field.Display,

        ["角色"] = Field.Role, ["身份"] = Field.Role, ["权限"] = Field.Role, ["role"] = Field.Role,

        ["任教班级"] = Field.Classes, ["班级"] = Field.Classes, ["任课班级"] = Field.Classes,
        ["所教班级"] = Field.Classes, ["任教"] = Field.Classes,
        ["class"] = Field.Classes, ["classes"] = Field.Classes,

        ["新密码"] = Field.Password, ["密码"] = Field.Password, ["口令"] = Field.Password,
        ["password"] = Field.Password, ["pwd"] = Field.Password,

        ["任课科目"] = Field.Subject, ["科目"] = Field.Subject, ["学科"] = Field.Subject,
        ["subject"] = Field.Subject,

        ["班主任班级"] = Field.Heads, ["班主任班"] = Field.Heads, ["所在班主任班级"] = Field.Heads,
        ["headclass"] = Field.Heads, ["headclasses"] = Field.Heads,

        ["状态"] = Field.Enabled, ["启用"] = Field.Enabled, ["是否启用"] = Field.Enabled,
        ["启用状态"] = Field.Enabled, ["status"] = Field.Enabled, ["enabled"] = Field.Enabled
    };

    /// <summary>单元格写这些表示"清空这一项"（空单元格一律理解为"不动"）。</summary>
    public static bool IsClearCell(string cell)
    {
        var t = (cell ?? "").Trim();
        return t == "-" || t == "—" || t == "－" || t == "无" || t == "/";
    }

    /// <summary>表头归一化：去空格、去括号补充说明、转小写。只用于认列，不用于取值。</summary>
    static string NormKey(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        int cut = s.IndexOfAny(new[] { '（', '(', '【', '[', '：', ':' });
        if (cut > 0) s = s.Substring(0, cut);
        return s.Replace(" ", "").Replace("\u3000", "").Replace("*", "").Trim();
    }

    /// <summary>
    /// 按第一行表头建列对照。认不出「用户名」就返回 null，交给老的位置模式兜底。
    /// </summary>
    public static ColumnMap? MapColumns(IReadOnlyList<string> header)
    {
        var m = new ColumnMap { HeaderMode = true };
        for (int i = 0; i < header.Count; i++)
        {
            var key = NormKey(header[i]);
            if (key.Length == 0) continue;
            if (Aliases.TryGetValue(key, out var f)) m.Set(f, i);
        }
        return m.Has(Field.User) ? m : null;
    }

    /// <summary>老格式：用户名 | 显示名 | 角色 | 班级 | 密码 | 任课科目（不多认列，免得多出来的列被误解）。</summary>
    public static ColumnMap Positional()
    {
        var m = new ColumnMap();
        m.Set(Field.User, 0);
        m.Set(Field.Display, 1);
        m.Set(Field.Role, 2);
        m.Set(Field.Classes, 3);
        m.Set(Field.Password, 4);
        m.Set(Field.Subject, 5);
        return m;
    }

    /// <summary>班级 / 科目这类列表值：空＝不动，写「-」＝清空。</summary>
    public static List<string>? ParseList(string cell)
    {
        var t = (cell ?? "").Trim();
        if (t.Length == 0) return null;
        if (IsClearCell(t)) return new List<string>();
        return t.Split(new[] { ',', '，', '、', ';', '；', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
    }

    static bool SameList(List<string> a, List<string>? b)
    {
        b ??= new List<string>();
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    static bool? ParseEnabled(string text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        return t switch
        {
            "启用" or "正常" or "开启" or "是" or "1" or "true" or "yes" or "y" or "√" => true,
            "停用" or "禁用" or "关闭" or "否" or "0" or "false" or "no" or "n" or "×" => false,
            _ => null
        };
    }

    public static string? ParseRole(string text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        return t switch
        {
            "管理员" or "admin" or "administrator" => UserRole.Admin,
            "班主任" or "teacher" or "老师" => UserRole.Teacher,
            "科任老师" or "科任" or "任课老师" or "subject" or "subjectteacher" or "subject_teacher" => UserRole.SubjectTeacher,
            "学生" or "电教" or "电教管理员" or "student" or "local" => UserRole.Student,
            _ => null
        };
    }

    static string Cell(string[] r, int i) => i >= 0 && i < r.Length ? (r[i] ?? "").Trim() : "";

    /// <summary>把表里的每一行比对成"要做什么"。<b>只算不改。</b></summary>
    public static UserImportResult Build(UserBook book, IReadOnlyList<string[]> dataRows, ColumnMap map)
    {
        var res = new UserImportResult();

        foreach (var r in dataRows)
        {
            string user = Cell(r, map[Field.User]);
            if (user.Length == 0) continue;                      // 空行不算"跳过"，别制造噪音

            string displayCell = Cell(r, map[Field.Display]);
            string roleCell = Cell(r, map[Field.Role]);
            string pwd = Cell(r, map[Field.Password]);
            string subjectCell = Cell(r, map[Field.Subject]);
            string statusCell = Cell(r, map[Field.Enabled]);

            var classes = map.Has(Field.Classes) ? ParseList(Cell(r, map[Field.Classes])) : null;
            var heads = map.Has(Field.Heads) ? ParseList(Cell(r, map[Field.Heads])) : null;

            string? role = ParseRole(roleCell);
            if (roleCell.Length > 0 && role == null)
            {
                res.Plans.Add(new UserImportPlan
                {
                    Kind = UserImportKind.Skip,
                    Username = user,
                    Note = $"角色「{roleCell}」认不出（可写 管理员 / 班主任 / 科任老师 / 电教管理员）"
                });
                continue;
            }

            string? display = null;
            if (displayCell.Length > 0) display = IsClearCell(displayCell) ? "" : displayCell;

            string? subject = null;
            if (map.Has(Field.Subject) && subjectCell.Length > 0)
                subject = IsClearCell(subjectCell) ? "" : BoardIds.NormalizeSubject(subjectCell);

            bool? enabled = null;
            string statusNote = "";
            if (map.Has(Field.Enabled) && statusCell.Length > 0)
            {
                enabled = ParseEnabled(statusCell);
                if (enabled == null) statusNote = $"（状态「{statusCell}」认不出，没改）";
            }

            var exist = book.Find(user);

            // ---- 新账号 ----
            if (exist == null)
            {
                if (pwd.Length == 0)
                {
                    res.Plans.Add(new UserImportPlan
                    {
                        Kind = UserImportKind.Skip,
                        Username = user,
                        Note = "新增账号必须填密码"
                    });
                    continue;
                }

                // 没写角色就推断：有班级＝班主任，没班级＝电教管理员（比默认成管理员安全）
                string r0 = role ?? ((classes == null || classes.Count == 0) ? UserRole.Student : UserRole.Teacher);
                if ((r0 == UserRole.Teacher || r0 == UserRole.SubjectTeacher)
                    && (classes == null || classes.Count == 0))
                {
                    res.Plans.Add(new UserImportPlan
                    {
                        Kind = UserImportKind.Skip,
                        Username = user,
                        Note = $"是{UserRole.Display(r0)}但没填任教班级"
                    });
                    continue;
                }

                var fresh = new AppUser
                {
                    Username = user,
                    DisplayName = display ?? "",
                    Role = r0,
                    ClassNames = classes ?? new List<string>(),
                    HeadClassNames = heads ?? new List<string>(),
                    Subject = subject ?? "",
                    Enabled = enabled ?? true
                };
                PasswordHasher.SetPassword(fresh, pwd);

                res.Plans.Add(new UserImportPlan
                {
                    Kind = UserImportKind.Add,
                    Username = user,
                    Fresh = fresh,
                    Note = $"新建（{UserRole.Display(r0)}"
                         + (fresh.ClassNames.Count > 0 ? "，" + string.Join("、", fresh.ClassNames) : "")
                         + "）" + statusNote
                });
                res.Add++;
                if (enabled == false) res.Disable++;
                continue;
            }

            // ---- 已有账号：逐字段比对，只有真的变了才写 ----
            var plan = new UserImportPlan { Kind = UserImportKind.Update, Username = user, Target = exist };
            var ch = new List<string>();

            if (display != null && !string.Equals(display, exist.DisplayName ?? "", StringComparison.Ordinal))
            {
                plan.DisplayName = display;
                ch.Add("显示名→" + (display.Length == 0 ? "（空）" : display));
            }

            if (role != null && !string.Equals(role, exist.Role, StringComparison.OrdinalIgnoreCase))
            {
                plan.Role = role;
                ch.Add("角色→" + UserRole.Display(role));
            }

            if (classes != null && !SameList(classes, exist.ClassNames))
            {
                plan.Classes = classes;
                ch.Add("任教班级→" + (classes.Count == 0 ? "（清空）" : string.Join("、", classes)));
            }

            if (heads != null && !SameList(heads, exist.HeadClassNames))
            {
                plan.HeadClasses = heads;
                ch.Add("班主任班级→" + (heads.Count == 0 ? "（按任教班级算）" : string.Join("、", heads)));
            }

            if (subject != null && !string.Equals(subject, exist.Subject ?? "", StringComparison.Ordinal))
            {
                plan.Subject = subject;
                ch.Add("任课科目→" + (subject.Length == 0 ? "（不限）" : subject));
            }

            if (enabled != null && enabled.Value != exist.Enabled)
            {
                plan.Enabled = enabled;
                ch.Add(enabled.Value ? "启用" : "停用");
                if (enabled.Value) res.Enable++; else res.Disable++;
            }

            if (pwd.Length != 0) { plan.Password = pwd; ch.Add("重置密码"); }

            if (ch.Count == 0)
            {
                res.Same++;
                // 状态写错这类提醒不能吞掉，否则"我明明填了停用"会变成静默无效
                if (statusNote.Length > 0)
                {
                    plan.Kind = UserImportKind.Skip;
                    plan.Note = "资料一致" + statusNote;
                    res.Plans.Add(plan);
                }
                continue;
            }

            plan.Note = string.Join("；", ch) + statusNote;
            res.Plans.Add(plan);
            res.Update++;
        }

        // 预警：会不会把管理员改没了（应用时会兜底，但先让人知道）
        int projected = book.AdminCount;
        foreach (var p in res.Plans)
        {
            if (p.Kind == UserImportKind.Add && p.Fresh != null)
            {
                if (p.Fresh.Role == UserRole.Admin && p.Fresh.Enabled) projected++;
            }
            else if (p.Kind == UserImportKind.Update && p.Target != null)
            {
                bool wasAdmin = p.Target.Enabled && p.Target.Role == UserRole.Admin;
                if (!wasAdmin) continue;
                if (p.Role != null && p.Role != UserRole.Admin) projected--;
                else if (p.Enabled == false) projected--;
            }
        }
        res.LoseAllAdmins = projected <= 0;

        return res;
    }

    /// <summary>把计划写进用户表（此时才真的改），并保住"至少一个启用的管理员"。</summary>
    public static void Apply(UserBook book, IEnumerable<UserImportPlan> plans)
    {
        foreach (var p in plans)
        {
            if (p.Kind == UserImportKind.Add)
            {
                if (p.Fresh != null) book.Users.Add(p.Fresh);
                continue;
            }
            if (p.Kind != UserImportKind.Update || p.Target == null) continue;

            var u = p.Target;                       // payload 为 null 的字段一律不动
            if (p.DisplayName != null) u.DisplayName = p.DisplayName;
            if (p.Role != null) u.Role = p.Role;
            if (p.Classes != null) u.ClassNames = p.Classes;
            if (p.HeadClasses != null) u.HeadClassNames = p.HeadClasses;
            if (p.Subject != null) u.Subject = p.Subject;
            if (p.Enabled != null) u.Enabled = p.Enabled.Value;
            if (p.Password != null) PasswordHasher.SetPassword(u, p.Password);
        }

        // 兜底：不能出现"没有任何启用的管理员"，否则没人能再管理用户
        if (book.AdminCount == 0)
        {
            var first = book.Users.FirstOrDefault();
            if (first != null) { first.Role = UserRole.Admin; first.Enabled = true; }
        }
    }

    /// <summary>读表：返回<b>含表头</b>的全部行，由调用方决定怎么解释第一行。</summary>
    public static List<string[]> ReadTable(string path)
    {
        var rows = new List<string[]>();

        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in ReadCsvLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                rows.Add(SplitCsv(line));
            }
            return rows;
        }

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet(1);
        foreach (var row in ws.RowsUsed())
        {
            int last = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            var vals = new List<string>();
            for (int c = 1; c <= Math.Max(last, 1); c++)
                vals.Add(row.Cell(c).GetString() ?? "");
            rows.Add(vals.ToArray());
        }
        return rows;
    }

    /// <summary>
    /// 读 CSV 并自动判断编码：先按 UTF-8 严格解码，失败再按 GB18030（936）。
    /// WPS / Excel 在中文系统上"另存为 CSV"默认是 GB 系，用 UTF-8 硬读会整列乱码。
    /// </summary>
    static string[] ReadCsvLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var strict = new System.Text.UTF8Encoding(false, true);
            return strict.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
        catch (System.Text.DecoderFallbackException)
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding(936).GetString(bytes)
                    .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            }
            catch { return File.ReadAllLines(path); }
        }
    }

    /// <summary>按引号规则切一行 CSV（<c>"a,b",c</c> → <c>["a,b"]</c>、<c>["c"]</c>）。</summary>
    static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quote = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quote)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quote = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quote = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }

        cells.Add(sb.ToString());
        return cells.ToArray();
    }
}
