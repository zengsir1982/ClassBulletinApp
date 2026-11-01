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
    static readonly Regex PeriodRe = new(@"第(\d+)节");
    static readonly Regex TimeRe = new(@"(\d{1,2}:\d{2})-(\d{1,2}:\d{2})");

    /// <summary>导入全校总课程表（基础课表）。科目中的 (调)/(代) 标记会被剥离，仅保留干净科目名。</summary>
    public static ScheduleFile Import(string path)
    {
        var (sf, _) = ParseAll(path);
        return sf;
    }

    static readonly Regex ClassNoRe = new(@"[（(]\s*(\d+)\s*[)）]");

    /// <summary>
    /// 按「年级（七→八→九）+ 班号数值」对班级排序。
    /// 合并三个年级的课表后若不排序，下拉列表会变成「八、九、七」这种按导入顺序的乱序；
    /// 班号必须按数值比较（字符串比较会把「(10)」排在「(2)」前面）。
    /// 无年级前缀的班级（单班/教师课表导入的）排到最后。
    /// </summary>
    public static void SortClasses(ScheduleFile sf)
    {
        if (sf?.Classes == null || sf.Classes.Count < 2) return;
        sf.Classes.Sort((a, b) =>
        {
            var (ga, na) = SplitClassName(a.ClassName);
            var (gb, nb) = SplitClassName(b.ClassName);
            int c = ga.CompareTo(gb);
            if (c != 0) return c;
            c = na.CompareTo(nb);
            if (c != 0) return c;
            return string.Compare(a.ClassName, b.ClassName, StringComparison.Ordinal);
        });
    }

    /// <summary>拆出「年级序号, 班号」用于排序。七年级=0、八年级=1、九年级=2，其它=90。</summary>
    static (int grade, int number) SplitClassName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (99, 9999);
        int grade =
            name.Contains("七年级") ? 0 :
            name.Contains("八年级") ? 1 :
            name.Contains("九年级") ? 2 : 90;
        var m = ClassNoRe.Match(name);
        int num = m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 9999;
        return (grade, num);
    }

    /// <summary>
    /// 导入临时总课程表 —— <b>只产出「带 (调)/(代) 标记」的覆盖记录，不产出也不替换基础课表</b>。
    /// 返回 (调代覆盖列表, 文件中出现的班级名)。
    /// 临时课表是对基础课表的“打补丁”：未带标记的格子与基础课表一致，
    /// 因此绝不能用它覆盖 schedule.json（否则等于换了一份底表，且七/九年级会被整级抹掉）。
    ///
    /// <paramref name="validFrom"/> / <paramref name="validTo"/> 为生效区间；
    /// 不传则默认「本周」（周一起算），与设计 §4.5.5 一致。
    /// </summary>
    public static (List<LessonOverride> overrides, List<string> classNames) ImportTempOverrides(
        string path, DateTime? validFrom = null, DateTime? validTo = null)
    {
        var (sf, marks) = ParseAll(path);
        var (ws, we) = WeekHelper.WeekWindow(validFrom ?? DateTime.Today);
        var from = DateOnly.FromDateTime((validFrom ?? ws).Date);
        var to = DateOnly.FromDateTime((validTo ?? we).Date);
        if (to < from) (from, to) = (to, from);
        return (BuildOverrides(marks, from, to), sf.Classes.Select(c => c.ClassName).ToList());
    }

    /// <summary>
    /// 兼容入口：返回 (从临时课表拼出的课表, 调代覆盖列表)。
    /// <b>仅供解析测试/对照使用</b>——App 导入请用 <see cref="ImportTempOverrides"/>，
    /// 不要把这里的 schedule 写入 Store.SaveSchedule（见 ImportTempOverrides 注释）。
    /// </summary>
    public static (ScheduleFile schedule, List<LessonOverride> overrides) ImportTempSchedule(string path)
    {
        var (sf, marks) = ParseAll(path);
        var (ws, we) = WeekHelper.WeekWindow(DateTime.Today);
        return (sf, BuildOverrides(marks, DateOnly.FromDateTime(ws), DateOnly.FromDateTime(we)));
    }

    /// <summary>把「(调)/(代) 标记格」转成覆盖记录（Type=调/代，Source=temp，带生效区间）。</summary>
    static List<LessonOverride> BuildOverrides(List<MarkCell> marks, DateOnly from, DateOnly to)
    {
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
                ValidFrom = from,
                ValidTo = to,
                Note = m.Mark == "代" ? "代课" : "临时调课",
                UpdatedAt = DateTime.Now
            });
        }
        return overrides;
    }

    /// <summary>
    /// 导入单个班级课表（如「八年级X班-班级课程表.xlsx」）。
    /// 文件布局约定：第1行为学校标题，第2行A列为班级名，随后一行含「星期」为表头，
    /// 其下每节一行（A列="第N节\n时间"，B列起为周一..周五的「科目\n教师[ \n地点]」）。
    /// 解析后仅含该一个班级，便于单班独立使用本软件。
    /// </summary>
    public static ScheduleFile ImportSingleClass(string path)
    {
        string readPath = ResolveReadablePath(path);
        try
        {
            return ImportSingleClassCore(readPath, path, false);
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
    /// 导入单个教师个人课表（如「某某老师-教师课程表.xlsx」）。
    /// 布局与单班课表一致，但第2行A列为教师名，单元格第二行为“所教班级”（而非教师）。
    /// 解析后以教师名为班级键，所教班级存入 Lesson.Teacher 字段（UI 中显示为“班级：”）。
    /// </summary>
    public static ScheduleFile ImportTeacherSchedule(string path)
    {
        string readPath = ResolveReadablePath(path);
        try
        {
            return ImportSingleClassCore(readPath, path, true);
        }
        finally
        {
            if (readPath != path && File.Exists(readPath))
            {
                try { File.Delete(readPath); } catch { }
            }
        }
    }

    static ScheduleFile ImportSingleClassCore(string path, string originalPath, bool isTeacher)
    {
        var result = new ScheduleFile { SourceFile = originalPath, ImportedAt = DateTime.Now };

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Contains("课程表")) ?? wb.Worksheet(1);

        int lastRow = ws.LastCellUsed()?.Address.RowNumber ?? 4;
        int lastCol = ws.LastCellUsed()?.Address.ColumnNumber ?? 6;

        // 名称：优先第2行A列，否则第1行A列，否则表名
        string className = ws.Cell(2, 1).GetString().Trim();
        if (string.IsNullOrEmpty(className)) className = ws.Cell(1, 1).GetString().Trim();
        if (string.IsNullOrEmpty(className)) className = ws.Name;
        if (isTeacher)
        {
            // 教师课表文件第2行形如「某某老师-教师课程表」，清理为纯教师名
            className = className.Replace("教师课程表", "").Replace("-", " ").Trim();
        }

        // 找到含「星期」的表头行（通常第3行）
        int dayRow = -1;
        for (int r = 1; r <= Math.Min(5, lastRow); r++)
        {
            bool has = false;
            for (int c = 1; c <= lastCol; c++)
                if (ws.Cell(r, c).GetString().Contains("星期")) { has = true; break; }
            if (has) { dayRow = r; break; }
        }
        if (dayRow < 0) dayRow = 3;

        // 收集星期列（按下标顺序对应 周一..周日）
        var dayCols = new List<int>();
        for (int c = 1; c <= lastCol; c++)
            if (ws.Cell(dayRow, c).GetString().Contains("星期")) dayCols.Add(c);
        if (dayCols.Count == 0) dayCols.Add(2);

        var cs = new ClassSchedule { ClassName = className };
        for (int r = dayRow + 1; r <= lastRow; r++)
        {
            var label = ws.Cell(r, 1).GetString();
            var m = PeriodRe.Match(label);
            if (!m.Success) continue;   // 跳过 大课间 / 午休 等非节次行
            int period = int.Parse(m.Groups[1].Value);
            var time = TimeRe.Match(label).Value;

            for (int di = 0; di < dayCols.Count; di++)
            {
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                int col = dayCols[di];
                var raw = ws.Cell(r, col).GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var pc = ParseCellDetail(raw, period, time);
                if (pc.Lessons.Count > 0) cs.Data[di][period] = pc.Lessons;
            }
        }

        result.Classes.Add(cs);
        return result;
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

        if (dayCols.Count == 0)
            throw new InvalidDataException(
                "未在第 2 行找到「星期」表头，无法识别课表布局。请确认选择的是「全校班级总课表」。");

        // 每个 day block 解析时段（第X节 + 时间区间）。
        // ★ 块宽不能写死 12 列：各年级的列布局并不相同 ——
        //   七年级每天 13 列（多一列「午休」），八/九年级每天 12 列。
        //   写死 12 列时，七年级每天最后一列（第 8 节）会被整列丢掉，
        //   30 个班 × 5 天 = 150 节课静默消失。
        //   改用「下一个星期列」来界定块宽，两种布局都能正确解析。
        var blocks = new List<List<(int col, int period, string time)>>();
        for (int di = 0; di < dayCols.Count; di++)
        {
            int start = dayCols[di];
            int end = di + 1 < dayCols.Count ? dayCols[di + 1] : lastCol + 1;   // 不含 end
            if (end <= start) end = start + 1;

            var slots = new List<(int, int, string)>();
            for (int c = start; c < end; c++)
            {
                var label = ws.Cell(3, c).GetString();
                var m = PeriodRe.Match(label);
                if (m.Success)
                {
                    var tm = "";
                    var tm2 = TimeRe.Match(label);
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
