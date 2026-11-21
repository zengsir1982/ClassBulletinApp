using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 任课安排表导入 / 模板生成（竖表：一行一科，列 科目 | 教师 | 地点 | 周课时）。
/// <para>表头可以在前几行任意位置（自动找含「科目 / 学科 / 课程」的那一行）；
/// 也兼容只给三列、或表头顺序不同。找不到科目列就明确报错，不静默当空表。</para>
/// </summary>
public static class SubjectTableImporter
{
    public static List<SubjectPair> Import(string path)
    {
        var list = new List<SubjectPair>();
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used == null) return list;

        int firstRow = used.FirstRow().RowNumber();
        int lastRow = used.LastRow().RowNumber();
        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol = used.LastColumn().ColumnNumber();

        int headerRow = -1, cSubject = -1, cTeacher = -1, cLocation = -1, cPeriods = -1;
        for (int r = firstRow; r <= Math.Min(firstRow + 5, lastRow); r++)
        {
            int s = -1, t = -1, l = -1, n = -1;
            for (int c = firstCol; c <= lastCol; c++)
            {
                var text = ws.Cell(r, c).GetString().Trim();
                if (s < 0 && (text.Contains("科目") || text.Contains("学科") || text.Contains("课程"))) s = c;
                else if (t < 0 && (text.Contains("教师") || text.Contains("老师") || text.Contains("任课"))) t = c;
                else if (l < 0 && (text.Contains("地点") || text.Contains("教室"))) l = c;
                else if (n < 0 && (text.Contains("课时") || text.Contains("节数"))) n = c;
            }
            if (s > 0) { headerRow = r; cSubject = s; cTeacher = t; cLocation = l; cPeriods = n; break; }
        }
        if (headerRow < 0)
            throw new InvalidOperationException("没找到「科目」列。请用模板格式：第一行列标题 科目 | 教师 | 地点 | 周课时。");

        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            string subject = ws.Cell(r, cSubject).GetString().Trim();
            if (subject.Length == 0) continue;
            var pair = new SubjectPair { Subject = subject };
            if (cTeacher > 0) pair.Teacher = ws.Cell(r, cTeacher).GetString().Trim();
            if (cLocation > 0) pair.Location = ws.Cell(r, cLocation).GetString().Trim();
            if (cPeriods > 0 && int.TryParse(ws.Cell(r, cPeriods).GetString().Trim(), out int n))
                pair.WeeklyPeriods = Math.Clamp(n, 1, 10);
            list.Add(pair);
        }
        return list;
    }

    public static void WriteTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("任课安排");
        ws.Cell(1, 1).Value = "科目";
        ws.Cell(1, 2).Value = "教师";
        ws.Cell(1, 3).Value = "地点";
        ws.Cell(1, 4).Value = "周课时";
        ws.Row(1).Style.Font.Bold = true;

        var samples = new[]
        {
            new object[] { "语文", "张老师", "七(1)班", 5 },
            new object[] { "数学", "李老师", "七(1)班", 5 },
            new object[] { "英语", "王老师", "七(1)班", 4 },
            new object[] { "体育", "", "", 3 },
        };
        for (int i = 0; i < samples.Length; i++)
            for (int c = 0; c < samples[i].Length; c++)
                ws.Cell(i + 2, c + 1).Value = XLCellValue.FromObject(samples[i][c]);

        ws.Cell(samples.Length + 3, 1).Value = "说明：一行一科；教师 / 地点 / 周课时可留空（周课时留空按 1 计）。";
        ws.Cell(samples.Length + 3, 1).Style.Font.Italic = true;
        ws.Cell(samples.Length + 3, 1).Style.Font.FontColor = XLColor.Gray;

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}
