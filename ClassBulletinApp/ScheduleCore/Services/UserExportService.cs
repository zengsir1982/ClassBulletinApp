using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 把账号资料导成一张可读、可改、能原样导回的电子表格。
///
/// <para><b>定位</b>：这是「账号清单 / 批量编辑底稿」，<b>不是</b>完整备份 ——
/// 密码只有 PBKDF2 哈希 + 盐，绝不进表格（一张全校口令哈希表流到群里等于集体失守）。
/// 要真正备份本机账号，走「设置 → 数据管理 → 备份与恢复」（连 users.json 一起打包）。
/// 这句话同时写在表格第二页第一行，避免有人拿它当备份还原后问"密码怎么都没了"。</para>
///
/// <para><b>为什么恒为 9 列</b>：第 1~6 列的顺序与 <c>UserManagerWindow.ImportUsers</c>
/// 的老位置模式完全一致（老表格照样能导），第 7~8 列是 r161 新增的「班主任班级」「状态」，
/// 第 9 列只作参考。列名同时充当导入时的表头认列依据，改列名就等于改导入行为 ——
/// 所以 <see cref="Headers"/> 是唯一权威，不要在别处再抄一份字符串数组。</para>
/// </summary>
public static class UserExportService
{
    /// <summary>账号表的列标题（顺序即写出顺序，也是导入时的首选别名）。</summary>
    public static readonly string[] Headers =
    {
        "用户名", "显示名", "角色", "任教班级", "新密码", "任课科目", "班主任班级", "状态", "来源"
    };

    /// <summary>班级列表的连接符（与导入的分隔符清单一致，导出→导回才不会把一个班拆成两个）。</summary>
    public const string ClassSeparator = "、";

    /// <summary>主色（与界面 #1E4FA3 一致）。</summary>
    const string PrimaryHex = "#1E4FA3";

    /// <summary>一行的渲染结果：谁的、哪几列、什么值。</summary>
    static string[] CellsOf(AppUser u) => new[]
    {
        u.Username ?? "",
        u.DisplayName ?? "",
        UserRole.Display(u.Role),
        Join(u.ClassNames),
        "",                                  // 新密码：恒空（见类注释）
        u.Subject ?? "",
        Join(u.HeadClassNames),
        u.Enabled ? "启用" : "停用",
        string.IsNullOrWhiteSpace(u.GeneratedFromSchedule) ? "手工" : "课表生成"
    };

    static string Join(List<string>? list)
        => list == null || list.Count == 0
            ? ""
            : string.Join(ClassSeparator, list.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0));

    /// <summary>
    /// 写出工作簿。调用方负责挑好 <paramref name="users"/>（全部或当前筛选结果），
    /// 并<b>在后台线程</b>调本方法 —— 目标可能是网络盘 / OneDrive，写 200 行也会卡几秒。
    /// </summary>
    /// <param name="book">用户表（用于第 3 页的权限矩阵）。</param>
    /// <param name="users">要导出的账号，按传入顺序写。</param>
    /// <param name="path">目标 .xlsx 路径。</param>
    /// <param name="scopeNote">第 2 页顶部的一句范围说明（如「本次导出全部 213 个账号」）。</param>
    public static void Export(UserBook book, IReadOnlyList<AppUser> users, string path, string scopeNote)
    {
        using var wb = new XLWorkbook();
        WriteSheet(wb, users);
        WriteGuide(wb, users.Count, scopeNote);
        WritePolicyMatrix(wb, book);
        wb.SaveAs(path);
    }

    // ---------------- 第 1 页：账号表 ----------------

    static void WriteSheet(XLWorkbook wb, IReadOnlyList<AppUser> users)
    {
        var ws = wb.AddWorksheet("账号表");

        for (int i = 0; i < Headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = Headers[i];
            c.Style.Font.Bold = true;
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml(PrimaryHex);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        ws.Row(1).Height = 24;

        for (int r = 0; r < users.Count; r++)
        {
            var cells = CellsOf(users[r]);
            for (int i = 0; i < cells.Length; i++)
            {
                var c = ws.Cell(r + 2, i + 1);
                c.Value = cells[i];
                c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }

            // 停用的账号整行淡下去：一眼能看出"这些人登不进来"，又不会漏看
            if (!users[r].Enabled)
            {
                var row = ws.Range(r + 2, 1, r + 2, Headers.Length);
                row.Style.Font.FontColor = XLColor.FromHtml("#8A8F98");
            }

            // 任教班级 / 班主任班级 往往两个班以上，自动换行比加宽列更实用
            ws.Cell(r + 2, 4).Style.Alignment.WrapText = true;
            ws.Cell(r + 2, 7).Style.Alignment.WrapText = true;
            ws.Cell(r + 2, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r + 2, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // 列宽按内容估：中文按 2 个字符宽算，够用又不会宽到看不全
        double[] widths = { 16, 22, 12, 34, 10, 12, 30, 8, 12 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];

        ws.SheetView.FreezeRows(1);
        if (users.Count > 0)
        {
            var used = ws.Range(1, 1, users.Count + 1, Headers.Length);
            used.SetAutoFilter();
            used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            used.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
            used.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D6DCE6");
            used.Style.Border.InsideBorderColor = XLColor.FromHtml("#D6DCE6");
        }
    }

    // ---------------- 第 2 页：填写说明 ----------------

    static void WriteGuide(XLWorkbook wb, int count, string scopeNote)
    {
        var ws = wb.AddWorksheet("填写说明");
        ws.Column(1).Width = 14;
        ws.Column(2).Width = 52;
        ws.Column(3).Width = 46;

        int r = 1;

        var t = ws.Cell(r++, 1);
        t.Value = "这张表是「账号清单 / 批量编辑底稿」，不是完整备份";
        t.Style.Font.Bold = true;
        t.Style.Font.FontSize = 14;
        t.Style.Font.FontColor = XLColor.FromHtml(PrimaryHex);

        var note = ws.Cell(r++, 1);
        note.Value = "密码只保存 PBKDF2 哈希与盐，出于安全考虑绝不出现在表格里，所以「新密码」列导出时永远为空。"
                   + "要完整备份本机账号（连密码一起），请用软件里的「设置 → 数据管理 → 备份与恢复」。";
        note.Style.Alignment.WrapText = true;
        ws.Range(r - 1, 1, r - 1, 3).Merge();
        ws.Row(r - 1).Height = 32;

        ws.Cell(r++, 1).Value = scopeNote;
        r++;

        var headRow = r;
        string[] guide = { "列名", "含义", "导入时的行为" };
        for (int i = 0; i < guide.Length; i++)
        {
            var c = ws.Cell(headRow, i + 1);
            c.Value = guide[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9EFF9");
            c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }
        r++;

        var rows = new[]
        {
            new[] { "用户名", "登录用的名字，不能重复", "必填。认不出这一列会直接拒绝导入；同名＝更新，不同名＝新增" },
            new[] { "显示名", "如「八(22)班 班主任 张三」，可留空", "留空则保留原值" },
            new[] { "角色", "管理员 / 班主任 / 科任老师 / 电教管理员\n（也可写 admin / teacher / subject / student）", "留空按「有班级＝班主任，没班级＝电教管理员」推断" },
            new[] { "任教班级", "多个班用「、」分隔（逗号、分号也可以）", "留空＝这一项不动；写「-」＝清空（老师清空后就看不到任何班）" },
            new[] { "新密码", "导出时恒为空；要改密码就在这里填明文", "留空＝保留原密码（不会把密码清掉）；新增账号必须填" },
            new[] { "任课科目", "如「数学」；留空表示不限科目", "留空不覆盖原值" },
            new[] { "班主任班级", "能改课表的班（通常 1 个），多个用「、」分隔", "留空＝不动；写「-」＝改回「按任教班级算」" },
            new[] { "状态", "启用 / 停用", "留空＝不改；写「停用」会真的登不进来，导入前会再确认一次" },
            new[] { "来源", "课表生成 / 手工", "只作参考，导入时忽略" }
        };

        foreach (var row in rows)
        {
            for (int i = 0; i < 3; i++)
            {
                var c = ws.Cell(r, i + 1);
                c.Value = row[i];
                c.Style.Alignment.WrapText = true;
                c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                c.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
                c.Style.Border.BottomBorderColor = XLColor.FromHtml("#E1E6EE");
            }
            ws.Row(r).Height = 30;
            r++;
        }

        r++;
        var tips = new[]
        {
            "· 空单元格＝这一项不动（不会把原来的班级洗掉）；真想清空就写一个「-」。",
            "· 可以插入新列、调换列顺序：导入时按第一行的表头认列，不看位置。",
            "· 但删掉「用户名」这一列就没法导了。",
            "· 本表共 " + count + " 个账号。导出后请勿发到班级群、家长群 —— 里面有全校账号名单。",
            "· 改完导回时，软件会先列出「新增／更新／停用／跳过」的明细，确认后才真正写盘。"
        };
        foreach (var line in tips)
        {
            var c = ws.Cell(r++, 1);
            c.Value = line;
            c.Style.Alignment.WrapText = true;
            ws.Range(r - 1, 1, r - 1, 3).Merge();
        }
    }

    // ---------------- 第 3 页：角色权限矩阵 ----------------

    /// <summary>一张"谁能干什么"的对勾表 —— 比挤在问号气泡里的那段文字好核对。</summary>
    static void WritePolicyMatrix(XLWorkbook wb, UserBook book)
    {
        var ws = wb.AddWorksheet("角色权限");

        var roles = new[] { UserRole.Admin, UserRole.Teacher, UserRole.SubjectTeacher, UserRole.Student };
        var caps = new (string Name, Func<RolePolicy, bool> Get)[]
        {
            ("改本班课表", p => p.EditSchedule),
            ("作业 / 缺交名单", p => p.EditBoard),
            ("恢复被停用的调课", p => p.RestoreSuspended),
            ("导入临时课表", p => p.ImportTemp),
            ("导入总课表 / 清除调课", p => p.ManageData),
            ("数据包 / 备份恢复", p => p.ManagePackage),
            ("发全校通知", p => p.SendSchoolNotice),
            ("修改自动更新设置", p => p.ManageUpdate),
            ("发布信息（发变更单）", p => p.PublishInfo)
        };

        ws.Cell(1, 1).Value = "能力";
        for (int i = 0; i < roles.Length; i++)
            ws.Cell(1, i + 2).Value = UserRole.Display(roles[i]);

        for (int c = 1; c <= roles.Length + 1; c++)
        {
            var cell = ws.Cell(1, c);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(PrimaryHex);
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        for (int i = 0; i < caps.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = caps[i].Name;
            for (int j = 0; j < roles.Length; j++)
            {
                bool on = caps[i].Get(book.PolicyFor(roles[j]));
                var cell = ws.Cell(i + 2, j + 2);
                cell.Value = on ? "√" : "—";
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Font.FontColor = on ? XLColor.FromHtml("#1E7B4F") : XLColor.FromHtml("#9AA1AC");
            }
        }

        int last = caps.Length + 3;
        var foot = ws.Cell(last, 1);
        foot.Value = "管理员恒为全权、不进策略表（否则谁都能把自己锁死）；「发布到数据源」与「管理用户」刻意不开放配置。"
                   + "本表由导出时的用户表生成，改角色权限后请重新导出。";
        foot.Style.Alignment.WrapText = true;
        ws.Range(last, 1, last, roles.Length + 1).Merge();
        ws.Row(last).Height = 34;

        ws.Column(1).Width = 26;
        for (int i = 2; i <= roles.Length + 1; i++) ws.Column(i).Width = 14;
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, caps.Length + 1, roles.Length + 1).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        ws.Range(1, 1, caps.Length + 1, roles.Length + 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(1, 1, caps.Length + 1, roles.Length + 1).Style.Border.OutsideBorderColor = XLColor.FromHtml("#D6DCE6");
        ws.Range(1, 1, caps.Length + 1, roles.Length + 1).Style.Border.InsideBorderColor = XLColor.FromHtml("#D6DCE6");
    }
}
