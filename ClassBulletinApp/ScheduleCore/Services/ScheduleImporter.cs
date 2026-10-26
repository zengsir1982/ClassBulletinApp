using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

public static class ScheduleImporter
{
    /// <summary>导入全校总课程表（基础课表）。科目中的 (调)/(代) 标记会被剥离，仅保留干净科目名。</summary>
    public static ScheduleFile Import(string path)
    {
        var (sf, _) = ParseAll(path);
        return sf;
    }

    /// <summary>
    /// 导入临时总课程表：返回 (基础课表, 调/代课覆盖列表)。
    /// 基础课表中的 (调)/(代) 标记被剥离为干净科目；同时为每个带标记的单元格生成一条
    /// LessonOverride（Type=调/代，Source=temp），用于悬浮窗显示「已调课 / 代课」徽章。
    /// </summary>
    public static (ScheduleFile schedule, List<LessonOverride> overrides) ImportTempSchedule(string path)
    {
        var (sf, marks) = ParseAll(path);
        var overrides = new List<LessonOverride>();
        foreach (var m in marks)
        {
            overrides.Add(new LessonOverride
            {
                ClassName = m.ClassName,
                DayIdx = m.DayIdx,
                Period = m.Period,
                Subject = m.Subject,
                Teacher = m.Teacher,
                Location = m.Location,
                Type = m.Mark,
                Source = "temp",
                Note = m.Mark == "代" ? "代课" : "临时调课",
                UpdatedAt = DateTime.Now
            });
        }
        return (sf, overrides);
    }

    /// <summary>
    /// 解析入口：先尝试以只读方式直接打开文件；若文件被其他进程（如 Excel）占用而打不开，
    /// 则自动拷贝到 %TEMP% 临时副本再解析，解析后删除临时副本。彻底无法读取时抛出友好异常。
    /// </summary>
    static (ScheduleFile sf, List<MarkCell> marks) ParseAll(string path)
    {
        string readPath = ResolveReadablePath(path);
        try
        {
            return ParseAllCore(readPath, path);
        }
        finally
        {
            if (readPath != path && File.Exists(readPath))
            {
                try { File.Delete(readPath); } catch { }
            }
        }
    }

    /// <summary>
    /// 取得一个可读取的路径：若源文件当前可读则直接返回；否则拷贝到临时文件再解析。
    /// </summary>
    static string ResolveReadablePath(string path)
    {
        if (!File.Exists(path)) return path; // 不存在则交给解析器报“文件不存在”
        // 先尝试以只读方式直接打开；若文件被 Excel 等以读共享打开而打不开，则进入拷贝分支
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return path;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // 被占用：拷贝到临时文件再解析（Excel 通常允许其他进程读取源文件内容）
        var tmp = Path.Combine(Path.GetTempPath(),
            "ClassBulletinApp_import_" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            File.Copy(path, tmp, true);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"无法读取文件「{Path.GetFileName(path)}」。请先关闭 Excel 中打开的该文件，或确认它未被其他程序（如云同步/杀毒软件）占用后重试。",
                ex);
        }
        return tmp;
    }

    static (ScheduleFile sf, List<MarkCell> marks) ParseAllCore(string path, string originalPath)
    {
        var result = new ScheduleFile { SourceFile = originalPath, ImportedAt = DateTime.Now };
        var marks = new List<MarkCell>();

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Contains("总课表")) ?? wb.Worksheet(1);

        int lastCol = ws.LastCellUsed()?.Address.ColumnNumber ?? 1;
        int lastRow = ws.LastCellUsed()?.Address.RowNumber ?? 4;

        // 第2行：定位星期表头所在的起始列
        var dayCols = new List<int>();
        for (int c = 1; c <= lastCol; c++)
        {
            var v = ws.Cell(2, c).GetString();
            if (v.Contains("星期")) dayCols.Add(c);
        }

        // 每个 day block（12 列）解析时段（第X节 + 时间区间）
        var blocks = new List<List<(int col, int period, string time)>>();
        var periodRe = new Regex(@"第(\d+)节");
        var timeRe = new Regex(@"(\d{1,2}:\d{2})-(\d{1,2}:\d{2})");
        foreach (var start in dayCols)
        {
            var slots = new List<(int, int, string)>();
            for (int off = 0; off < 12; off++)
            {
                int c = start + off;
                var label = ws.Cell(3, c).GetString();
                var m = periodRe.Match(label);
                if (m.Success)
                {
                    var tm = "";
                    var tm2 = timeRe.Match(label);
                    if (tm2.Success) tm = tm2.Value;
                    slots.Add((c, int.Parse(m.Groups[1].Value), tm));
                }
                else
                {
                    slots.Add((c, -1, ""));
                }
            }
            blocks.Add(slots);
        }

        // 班级行：从第4行开始，A 列为班名
        for (int r = 4; r <= lastRow; r++)
        {
            var name = ws.Cell(r, 1).GetString().Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var cs = new ClassSchedule { ClassName = name };
            for (int di = 0; di < blocks.Count; di++)
            {
                var dayMap = new Dictionary<int, List<Lesson>>();
                foreach (var (c, period, time) in blocks[di])
                {
                    if (period < 0) continue;
                    var raw = ws.Cell(r, c).GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var pc = ParseCellDetail(raw, period, time);
                    if (pc.Lessons.Count > 0) dayMap[period] = pc.Lessons;
                    if (!string.IsNullOrEmpty(pc.SwapMark) && pc.Lessons.Count > 0)
                    {
                        var first = pc.Lessons[0];
                        marks.Add(new MarkCell
                        {
                            ClassName = name,
                            DayIdx = di,
                            Period = period,
                            Subject = first.Subject,
                            Teacher = first.Teacher,
                            Location = first.Location,
                            Mark = pc.SwapMark
                        });
                    }
                }
                cs.Data[di] = dayMap;
            }
            result.Classes.Add(cs);
        }

        return (result, marks);
    }

    static List<Lesson> ParseCell(string raw, int period, string time)
        => ParseCellDetail(raw, period, time).Lessons;

    /// <summary>
    /// 解析单个单元格。剥离 (调)/(代) 标记得到干净科目，并通过 SwapMark 回传该格的调代类型
    /// （含「代」优先记为代课，否则记调课）。
    /// </summary>
    static ParsedCell ParseCellDetail(string raw, int period, string time)
    {
        var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(x => x.Trim())
                      .Where(x => x.Length > 0)
                      .ToList();
        if (lines.Count == 0) return new ParsedCell();

        var subjectLine = lines[0];
        var teacherLine = lines.Count > 1 ? lines[1] : "";
        var locationLine = lines.Count > 2 ? lines[2] : "";

        // 调代标记优先看整格（兼容 "(调)(代)" 组合）
        string mark = raw.Contains("(代)") ? "代" : (raw.Contains("(调)") ? "调" : "");

        var subjects = subjectLine
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var teachers = teacherLine
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var lessons = new List<Lesson>();
        for (int i = 0; i < subjects.Length; i++)
        {
            // 剥离 (调)/(代)/(调代) 等标记后缀，得到干净科目名
            var subj = Regex.Replace(subjects[i], @"\([调代]+\)", "").Trim();
            var teach = i < teachers.Length ? teachers[i] : "";
            int week = 0;
            if (subj.Contains("单")) week = 1;
            else if (subj.Contains("双")) week = 2;
            lessons.Add(new Lesson
            {
                Period = period,
                Subject = subj,
                Teacher = teach,
                Location = locationLine,
                Week = week,
                TimeRange = time
            });
        }
        return new ParsedCell { Lessons = lessons, SwapMark = mark };
    }

    class ParsedCell
    {
        public List<Lesson> Lessons { get; set; } = new();
        public string SwapMark { get; set; } = "";
    }

    class MarkCell
    {
        public string ClassName { get; set; } = "";
        public int DayIdx { get; set; }
        public int Period { get; set; }
        public string Subject { get; set; } = "";
        public string Teacher { get; set; } = "";
        public string Location { get; set; } = "";
        public string Mark { get; set; } = "";
    }
}
