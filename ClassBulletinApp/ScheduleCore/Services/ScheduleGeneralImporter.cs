using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ScheduleCore.Models;
using static ScheduleCore.Services.ScheduleTemplateProbe;

namespace ScheduleCore.Services;

/// <summary>
/// 通用课表解析器（r118）：按 <see cref="TemplateHints"/> 的参数把不同模板的 xlsx 归一化成
/// <see cref="ScheduleFile"/>（星期 0=周一..6=周日 → 节次 → Lesson 列表）。
///
/// 与 <see cref="ScheduleImporter"/> 的关系：后者是针对二一导出格式的专用高速通道，保持不变；
/// 本类服务于"智能导入"入口，覆盖二一格式以外的模板（横排/纵列/单班、换行/空格/括号单元格）。
/// 二者产出同一个模型，落盘/渲染/覆盖链路完全复用。
/// </summary>
public static class ScheduleGeneralImporter
{
    static readonly Regex TimeRe = new(@"\d{1,2}\s*[:：]\s*\d{2}\s*[-～~至]\s*\d{1,2}\s*[:：]\s*\d{2}");
    static readonly Regex PeriodNumRe = new(@"(\d+)");
    static readonly Regex SwapRe = new(@"[（(]\s*(调|代)\s*[)）]");

    public class GeneralResult
    {
        public ScheduleFile Schedule { get; set; } = new();
        /// <summary>调/代标记格（用于生成临时覆盖；智能导入默认只导基础课表，这里仅供参考）。</summary>
        public List<SwapMark> Marks { get; set; } = new();
        /// <summary>解析诊断：未识别格数、跳过的行列等。</summary>
        public int UnparsedCells { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public class SwapMark
    {
        public string ClassName { get; set; } = "";
        public int DayIdx { get; set; }
        public int Period { get; set; }
        public string Subject { get; set; } = "";
        public string Teacher { get; set; } = "";
        public string Location { get; set; } = "";
        public string Mark { get; set; } = "";
    }

    /// <summary>按给定参数解析文件。参数来自探测器，也可由 UI 微调后重解析。</summary>
    public static GeneralResult Parse(string path, TemplateHints h)
    {
        var result = new GeneralResult();
        result.Schedule.SourceFile = path;
        result.Schedule.ImportedAt = DateTime.Now;

        using var wb = new XLWorkbook(path);
        if (h.SheetIndex < 1 || h.SheetIndex > wb.Worksheets.Count)
            throw new InvalidDataException($"工作表序号 {h.SheetIndex} 超出范围（共 {wb.Worksheets.Count} 张）");
        var ws = wb.Worksheet(h.SheetIndex);
        int lastRow = ws.LastCellUsed()?.Address.RowNumber ?? 1;
        int lastCol = ws.LastCellUsed()?.Address.ColumnNumber ?? 1;

        if (h.PeriodOrientation == PeriodOrientation.Horizontal || h.CellSpan == CellSpan.TwoRows)
            ParseTraditional(ws, h, lastRow, lastCol, result);
        else if (h.MainDimension == MainDimension.ClassesInRows && h.DayOrientation == DayOrientation.Horizontal)
            ParseClassesInRows(ws, h, lastRow, lastCol, result);
        else if (h.MainDimension == MainDimension.ClassesInColumns)
            ParseClassesInColumns(ws, h, lastRow, lastCol, result);
        else
            ParseSingle(ws, h, lastRow, lastCol, result);

        ScheduleImporter.SortClasses(result.Schedule);
        return result;
    }

    // ============ 布局四（东凤传统格式）：节次横排 + 科目/教师双行 ============
    // 三种子布局：横行总表（星期横排+班在行）、纵列总表（星期纵列+班在列）、老式单班（星期纵列+单班）。
    // 共同特征：节次标签横排在 periodRow 这一行的若干列上；每个授课格占"两行"——
    //   科目行（如 "生物 樊玉清" 或 "生物(樊玉清)"）与其下一行的教师名（如 "樊玉清"）。
    static void ParseTraditional(IXLWorksheet ws, TemplateHints h, int lastRow, int lastCol, GeneralResult result)
    {
        int periodRow = h.PeriodLabelIndex;
        if (periodRow <= 0) { result.Notes.Add("节次标签行未识别"); return; }

        // 建立"节次列 → 节次号"映射（节次标签横排）
        var periodCols = new List<(int col, int period, string time)>();
        // 时间通常在节次行的下一行（第6行 "8:10～8:50"），先收集同一列的时间文本
        for (int c = 1; c <= lastCol; c++)
        {
            var label = Clean(ws.Cell(periodRow, c).GetString());
            if (label.Length == 0) continue;
            var (period, time, isFixed) = ParsePeriodLabel(label, h.PeriodStyle);
            string time2 = "";
            string below = Clean(ws.Cell(periodRow + 1, c).GetString());
            var tm = TimeRe.Match(below);
            if (tm.Success) time2 = tm.Value;
            if (period > 0) periodCols.Add((c, period, time.Length > 0 ? time : time2));
        }
        if (periodCols.Count == 0) { result.Notes.Add("节次横排行未找到节次号"); return; }

        // 纵列总表：节次标签纵排在 C 列（每行一节），不是上面建的"横排节次列映射"，
        //   用专门的逐行扫描版本（它自己按 labelCol 找节次、按星期列归属、按下格取教师）。
        if (h.MainDimension == MainDimension.ClassesInColumns && h.PeriodOrientation == PeriodOrientation.Vertical)
        {
            ParseTraditional_ClassesInColumnsVertical(ws, h, lastRow, lastCol, result);
            return;
        }

        if (h.DayOrientation == DayOrientation.Horizontal && h.MainDimension == MainDimension.ClassesInRows)
            ParseTraditional_ClassesInRows(ws, h, periodCols, periodRow, lastRow, result);
        else if (h.MainDimension == MainDimension.ClassesInColumns)
            ParseTraditional_ClassesInColumns(ws, h, periodCols, periodRow, lastRow, result);
        else
            ParseTraditional_Single(ws, h, periodCols, periodRow, lastRow, result);
    }

    static void ParseTraditional_ClassesInRows(IXLWorksheet ws, TemplateHints h,
        List<(int col, int period, string time)> periodCols, int periodRow, int lastRow, GeneralResult result)
    {
        // 星期横排：在 dayHeaderIndex 行找星期列；每个星期块内包含若干节次列
        var dayCols = new List<(int col, int day)>();
        for (int c = 1; c <= lastCol(ws); c++)
        {
            var v = Clean(ws.Cell(h.DayHeaderIndex, c).GetString());
            if (IsDayCell(v)) dayCols.Add((c, DayOf(v)));
        }
        // 班级在 A 列，每班占两行（科目行 + 教师行）。数据从节次行下一行开始。
        int dataRow = periodRow + 1;
        // 若节次下一行是"时间行"（如 8:10～8:50），班级实际从第 dataRow+1 行开始
        if (periodCols.All(p => TimeRe.IsMatch(Clean(ws.Cell(periodRow + 1, p.col).GetString())))) dataRow = periodRow + 2;

        for (int r = dataRow; r <= lastRow; r += h.CellSpan == CellSpan.TwoRows ? 2 : 1)
        {
            string name = Clean(ws.Cell(r, h.NameIndex).GetString());
            if (name.Length == 0) continue;
            name = NormalizeName(name, false);
            if (!IsClassNameLoose(name)) continue;
            var cs = EnsureClass(result.Schedule, name);

            foreach (var (col, period, time) in periodCols)
            {
                // 归属星期：节次列属于哪个星期块（列号 >= 该星期列 且 < 下一个星期列）
                int di = DayOfColumn(col, dayCols);
                var raw = Clean(ws.Cell(r, col).GetString());
                var teacherRow = h.CellSpan == CellSpan.TwoRows ? Clean(ws.Cell(r + 1, col).GetString()) : "";
                var lessons = ParseTraditionalCell(raw, teacherRow, h.CellLayout, period, time, h.CustomSeparator);
                if (lessons.Count == 0) { if (raw.Length > 0) result.UnparsedCells++; continue; }
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                cs.Data[di][period] = lessons;
            }
        }
    }

    static void ParseTraditional_ClassesInColumns(IXLWorksheet ws, TemplateHints h,
        List<(int col, int period, string time)> periodCols, int periodRow, int lastRow, GeneralResult result)
    {
        // 星期纵列（A列），班级横排在 NameIndex 行 D..；节次纵列（C列）另有 periodCols？
        // 纵列总表里节次其实在 C 列纵排（C=早读/1/大课间/2...），不是横排——
        // 但 periodCols 可能仍从"第5行"取到误判。这里按星期纵列 + 班级在列的通用逻辑解析。
        int dayCol = h.DayHeaderIndex;
        int classRow = h.NameIndex;
        var classCols = new List<(int col, string name)>();
        for (int c = dayCol + 1; c <= lastCol(ws); c++)
        {
            var v = Clean(ws.Cell(classRow, c).GetString());
            if (v.Length > 0 && !IsDayCell(v)) classCols.Add((c, NormalizeName(v, false)));
        }
        // 节次：优先用纵列（dayCol 右侧的节次列），从 periodRow 起扫描每个数据行
        int labelCol = lastCol(ws) >= dayCol + 2 ? dayCol + 2 : dayCol + 1;
        var rowPeriods = new List<(int row, int period, string time)>();
        for (int r = periodRow; r <= lastRow; r++)
        {
            var label = Clean(ws.Cell(r, labelCol).GetString());
            var (period, time, _) = ParsePeriodLabel(label, h.PeriodStyle);
            if (period > 0) rowPeriods.Add((r, period, time));
        }
        var dayMarks = new List<(int row, int day)>();
        for (int r = periodRow; r <= lastRow; r++)
        {
            var v = Clean(ws.Cell(r, dayCol).GetString());
            if (IsDayCell(v)) dayMarks.Add((r, DayOf(v)));
        }
        foreach (var (col, name) in classCols)
            foreach (var (row, period, time) in rowPeriods)
            {
                var mark = dayMarks.Where(x => x.row <= row).OrderByDescending(x => x.row).FirstOrDefault();
                int di = mark.day >= 0 ? mark.day : 0;
                var raw = Clean(ws.Cell(row, col).GetString());
                var teacherRow = h.CellSpan == CellSpan.TwoRows ? Clean(ws.Cell(row + 1, col).GetString()) : "";
                var lessons = ParseTraditionalCell(raw, teacherRow, h.CellLayout, period, time, h.CustomSeparator);
                if (lessons.Count == 0) { if (raw.Length > 0) result.UnparsedCells++; continue; }
                var cs = EnsureClass(result.Schedule, name);
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                cs.Data[di][period] = lessons;
            }
    }

    /// <summary>
    /// 传统纵列总表：星期在 A 列分块（每块 21 行），班级在第 3 行横排，节次标签在 C 列的
    /// "科目行"上，紧接的下一行（C 列空）是教师行。逐行扫描：C 列有节次标签 → 科目行，
    /// 其下一行 → 教师行，两格一起交给 ParseTraditionalCell 判定单双周 / 全周。
    /// </summary>
    static void ParseTraditional_ClassesInColumnsVertical(IXLWorksheet ws, TemplateHints h,
        int lastRow, int lastCol, GeneralResult result)
    {
        int dayCol = h.DayHeaderIndex;                       // A 列
        int classRow = h.NameIndex > 0 ? h.NameIndex : 3;    // 第 3 行班级名
        int labelCol = lastCol >= dayCol + 2 ? dayCol + 2 : dayCol + 1; // C 列节次

        // 班级列：班级名行的 D.. 起
        var classCols = new List<(int col, string name)>();
        for (int c = dayCol + 1; c <= lastCol; c++)
        {
            var v = Clean(ws.Cell(classRow, c).GetString());
            if (v.Length > 0 && !IsDayCell(v)) classCols.Add((c, NormalizeName(v, false)));
        }
        if (classCols.Count == 0) { result.Notes.Add("纵列总表班级表头行未找到班级名"); return; }

        // 星期标记：A 列所有"星期一/二..."，按行排序，用于给节次行归属星期
        var dayMarks = new List<(int row, int day)>();
        for (int r = 1; r <= lastRow; r++)
        {
            var v = Clean(ws.Cell(r, dayCol).GetString());
            if (IsDayCell(v)) { int d = DayOf(v); if (d >= 0) dayMarks.Add((r, d)); }
        }

        for (int r = classRow + 1; r <= lastRow; r++)
        {
            var label = Clean(ws.Cell(r, labelCol).GetString());
            if (label.Length == 0) continue;
            var (period, time, isFixed) = ParsePeriodLabel(label, h.PeriodStyle);
            if (period <= 0 || isFixed) continue;            // 早读/大课间/眼保健操等跳过

            // 科目行 = r（C 列有节次标签），教师行 = r+1（C 列应为空）
            int subjectRowIdx = r;
            int teacherRowIdx = r + 1;
            var mark = dayMarks.Where(x => x.row <= subjectRowIdx).OrderByDescending(x => x.row).FirstOrDefault();
            int di = mark.day >= 0 ? mark.day : 0;

            foreach (var (col, name) in classCols)
            {
                var raw = Clean(ws.Cell(subjectRowIdx, col).GetString());
                if (raw.Length == 0) continue;
                var teacherRow = Clean(ws.Cell(teacherRowIdx, col).GetString());
                var lessons = ParseTraditionalCell(raw, teacherRow, h.CellLayout, period, time, h.CustomSeparator);
                if (lessons.Count == 0) { result.UnparsedCells++; continue; }
                var cs = EnsureClass(result.Schedule, name);
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                cs.Data[di][period] = lessons;
            }
        }
        if (dayMarks.Count == 0) result.Notes.Add("纵列总表未在 A 列找到星期标记");
    }

    static void ParseTraditional_Single(IXLWorksheet ws, TemplateHints h,
        List<(int col, int period, string time)> periodCols, int periodRow, int lastRow, GeneralResult result)
    {
        // 老式单班：星期纵列（A列，如 A7=星期一 / A9=星期二...），节次横排（periodRow 行）。
        //   每个"星期"标记所在行 = 科目行，其下一行 = 教师行。
        string name = NormalizeName(h.SingleName, h.IsTeacherSchedule);
        if (name.Length == 0) name = ws.Name;
        var cs = new ClassSchedule { ClassName = name };
        int dayCol = h.DayHeaderIndex;

        // 扫描所有"星期"标记行，逐个作为科目行解析
        for (int r = periodRow + 1; r <= lastRow; r++)
        {
            var dayLabel = Clean(ws.Cell(r, dayCol).GetString());
            if (!IsDayCell(dayLabel)) continue;
            int di = DayOf(dayLabel);
            if (di < 0) continue;
            foreach (var (col, period, time) in periodCols)
            {
                var raw = Clean(ws.Cell(r, col).GetString());
                var teacherRow = h.CellSpan == CellSpan.TwoRows ? Clean(ws.Cell(r + 1, col).GetString()) : "";
                var lessons = ParseTraditionalCell(raw, teacherRow, h.CellLayout, period, time, h.CustomSeparator);
                if (lessons.Count == 0) { if (raw.Length > 0) result.UnparsedCells++; continue; }
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                cs.Data[di][period] = lessons;
            }
        }
        result.Schedule.Classes.Add(cs);
    }

    /// <summary>
    /// 拆一格传统格式的文本，返回 (科目, 教师, 是否自带科目标记)。
    /// "自带科目标记"=这一格里同时写出了科目和教师（空格分两段 / 括号(教师) / 竖线），
    /// 用于区分"单双周双格"和"全周科目+教师两格"。
    /// </summary>
    static (string subj, string teach, bool hasMarker) SplitTraditionalCell(string text, CellLayout layout, string sep = "")
    {
        text = Clean(text);
        if (text.Length == 0) return ("", "", false);
        if (layout == CellLayout.Custom)
        {
            // r143：自定义符号。左右两段各算「自带标记」——只看有没有第二段，
            // 语义与「括号」「空格」一致：两段都有字就是单双周，只有一段就是纯科目。
            var parts = SplitBySeparator(text, sep);
            if (parts.Count >= 2) return (parts[0], parts[1], parts[0].Length > 0);
            return (text, "", false);
        }
        if (layout == CellLayout.Parenthesis)
        {
            var m = Regex.Match(text, @"^\s*(.*?)[（(](.*?)[)）]\s*(.*)$");
            if (m.Success)
            {
                string s = m.Groups[1].Value.Trim();
                string t = m.Groups[2].Value.Trim();
                // 括号前有科目文字才算"自带科目"；"(许同琛)"这种只是教师
                return (s, t, s.Length > 0);
            }
            return (text, "", false);
        }
        if (layout == CellLayout.Space)
        {
            var parts = Regex.Split(text, @"\s+").Where(x => x.Length > 0).ToArray();
            if (parts.Length >= 2) return (parts[0], parts[1], true);
            return (parts.Length == 1 ? parts[0] : "", "", false);
        }
        // NewLine / 竖线
        var seg = text.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (seg.Length >= 2) return (seg[0], seg[1], true);
        return (seg.Length == 1 ? seg[0] : "", "", false);
    }

    /// <summary>清理科目名：去调代标记、去 -单/-双 后缀（Week 由位置决定，不看字面）。</summary>
    static string CleanTraditionalSubject(string s)
    {
        s = Regex.Replace(s ?? "", @"[（(]\s*(调|代)\s*[)）]", "").Trim();
        s = Regex.Replace(s, WeekMarkTail, "").Trim();
        return s;
    }

    /// <summary>
    /// 结尾的单双周标记（r130）：-单 / 双周 / （单） 都算，标记本身不进科目名。
    /// 只认结尾，避免误伤「双减」「单价」这种开头的词。
    /// </summary>
    internal const string WeekMarkTail = @"[（(]?\s*[-－—]?\s*[单双]\s*(周)?\s*[)）]?\s*$";

    /// <summary>
    /// 传统格式"两行格"的一节课：上格(科目行) + 下格(教师行)。两种语义自动区分：
    /// - 下格自带科目（如"地理 梁伟玲" / "地理(梁伟玲)"）→ 单双周：上格=单周(W1)，下格=双周(W2)。
    /// - 下格只有教师/人名（如"许同琛" / "(许同琛)"）→ 全周：上格科目 + 下格教师，W0。
    /// 这与二一课表靠"-单/-双"字面识别是两套独立逻辑，互不干扰。
    /// </summary>
    static List<Lesson> ParseTraditionalCell(string subjectRow, string teacherRow, CellLayout layout, int period, string time,
        string sep = "")
    {
        var up = SplitTraditionalCell(subjectRow, layout, sep);
        var low = SplitTraditionalCell(teacherRow, layout, sep);
        string subjU = CleanTraditionalSubject(up.subj);
        string subjL = CleanTraditionalSubject(low.subj);

        // 分支一：单双周——下格同时写出了科目+教师，说明上下两格是两门不同的课
        if (low.hasMarker && subjL.Length > 0 && subjU.Length > 0)
        {
            string teachL = low.teach;
            string teachU = up.teach;
            // 教师兜底：纯汉字人名则采用
            if (teachU.Length == 0 && Regex.IsMatch(subjectRow, @"^[\u4e00-\u9fa5]{2,5}$")) teachU = subjectRow;
            if (teachL.Length == 0 && Regex.IsMatch(teacherRow, @"^[\u4e00-\u9fa5]{2,5}$")) teachL = teacherRow;
            var list = new List<Lesson>
            {
                new Lesson { Period = period, Subject = subjU, Teacher = teachU, Week = 1, TimeRange = time },
                new Lesson { Period = period, Subject = subjL, Teacher = teachL, Week = 2, TimeRange = time }
            };
            return list;
        }

        // 分支二：全周——上格是科目，下格是教师
        string subj = subjU;
        if (subj.Length == 0) return new List<Lesson>();
        string teach = up.teach;
        if (teach.Length == 0)
        {
            if (low.teach.Length > 0) teach = low.teach;
            else if (Regex.IsMatch(teacherRow, @"^[\u4e00-\u9fa5]{2,5}$")) teach = teacherRow;
        }
        return new List<Lesson> { new Lesson { Period = period, Subject = subj, Teacher = teach, Week = 0, TimeRange = time } };
    }

    static int lastCol(IXLWorksheet ws) => ws.LastCellUsed()?.Address.ColumnNumber ?? 1;

    static bool IsClassNameLoose(string s)
        => Regex.IsMatch(s, @"班$|课程表|^\d+年级") || Regex.IsMatch(s, @"[一二三四五六七八九十]\s*[（(]?\d*\s*[)）]?\s*班");

    static int DayOfColumn(int col, List<(int col, int day)> dayCols)
    {
        int result = 0;
        foreach (var (c, d) in dayCols.OrderBy(x => x.col))
            if (col >= c) result = d; else break;
        return result;
    }

    // ============ 布局一：星期横排 + 班在行（二一总表 / 横行格式.xls 转 xlsx）============
    static void ParseClassesInRows(IXLWorksheet ws, TemplateHints h, int lastRow, int lastCol, GeneralResult result)
    {
        // 收集星期列（表头行里所有命中的列，按列序）
        var dayCols = new List<int>();
        for (int c = 1; c <= lastCol; c++)
            if (IsDayCell(ws.Cell(h.DayHeaderIndex, c).GetString())) dayCols.Add(c);
        if (dayCols.Count == 0) { result.Notes.Add("表头行未找到星期列，改用连续列"); for (int c = 2; c <= Math.Min(lastCol, 6); c++) dayCols.Add(c); }

        // 节次标签列（默认 A 列）—— 收集每个数据行的 (行号, 节次, 时间)
        int labelCol = h.PeriodLabelIndex > 0 ? h.PeriodLabelIndex : 1;
        var periods = new List<(int row, int period, string time, bool isFixed)>();
        for (int r = h.DayHeaderIndex + 1; r <= lastRow; r++)
        {
            var label = Clean(ws.Cell(r, labelCol).GetString());
            var (period, time, isFixed) = ParsePeriodLabel(label, h.PeriodStyle);
            if (period > 0) periods.Add((r, period, time, isFixed));
        }

        int nameCol = h.NameIndex > 0 && h.NameIndex != labelCol ? h.NameIndex : 1;
        // r130：模板里写明「同一班级的班级名可只写在第一行，其余行留空」，
        // 但这里原来遇到空班名整行跳过 —— 用户按模板填后只有第一行能进库。
        // 改成沿用上一个班名（Excel 里合并/省略班名是最常见的填法）。
        string lastClass = "";
        foreach (var (row, period, time, _) in periods)
        {
            var rowName = Clean(ws.Cell(row, nameCol).GetString());
            if (rowName.Length == 0) rowName = Clean(ws.Cell(row, 1).GetString());
            if (rowName.Length > 0) lastClass = rowName;
            for (int di = 0; di < dayCols.Count; di++)
            {
                var raw = Clean(ws.Cell(row, dayCols[di]).GetString());
                if (raw.Length == 0) continue;
                // 班级名在节次行同一行取（多班布局每行一个班）；整块都留空时沿用上一行
                var className = lastClass;
                if (className.Length == 0) continue;
                var lessons = ParseCell(raw, h.CellLayout, period, time, h.IsTeacherSchedule, h.CustomSeparator);
                if (lessons.Count == 0) { result.UnparsedCells++; continue; }
                var cs = EnsureClass(result.Schedule, className);
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                cs.Data[di][period] = lessons;
            }
        }
        if (periods.Count == 0) result.Notes.Add("未识别到任何节次行，请检查「节次标签列」是否正确");
    }

    // ============ 布局二：星期纵列 + 班在列（纵列格式.xls）============
    static void ParseClassesInColumns(IXLWorksheet ws, TemplateHints h, int lastRow, int lastCol, GeneralResult result)
    {
        // 星期在 h.DayHeaderIndex 列，从上到下；班级在 h.NameIndex 行，从左到右
        int dayCol = h.DayHeaderIndex;
        int classRow = h.NameIndex;
        var classCols = new List<(int col, string name)>();
        for (int c = dayCol + 1; c <= lastCol; c++)
        {
            var name = Clean(ws.Cell(classRow, c).GetString());
            if (name.Length > 0 && !IsDayCell(name)) classCols.Add((c, name));
        }
        if (classCols.Count == 0) { result.Notes.Add("班级表头行未找到班级名"); return; }

        // 节次标签：星期列右边一列（"上午/1/大课间..."），或者表头行下一行
        int labelCol = h.PeriodLabelIndex > 0 ? h.PeriodLabelIndex : dayCol + 1;
        var periods = new List<(int row, int period, string time)>();
        for (int r = classRow + 1; r <= lastRow; r++)
        {
            var label = Clean(ws.Cell(r, labelCol).GetString());
            var (period, time, _) = ParsePeriodLabel(label, h.PeriodStyle);
            if (period > 0) periods.Add((r, period, time));
        }
        // 星期分组：星期列上出现的"星期一/二..."标记，按行区间归属
        var dayMarks = new List<(int row, int day)>();
        for (int r = classRow + 1; r <= lastRow; r++)
        {
            var v = Clean(ws.Cell(r, dayCol).GetString());
            if (IsDayCell(v)) dayMarks.Add((r, DayOf(v)));
        }

        foreach (var (col, name) in classCols)
            foreach (var (row, period, time) in periods)
            {
                // 该行属于哪个星期：取不超过该行的最后一个星期标记
                var mark = dayMarks.Where(x => x.row <= row).OrderByDescending(x => x.row).FirstOrDefault();
                int di = mark.day >= 0 ? mark.day : 0;
                var raw = Clean(ws.Cell(row, col).GetString());
                if (raw.Length == 0) continue;
                var lessons = ParseCell(raw, h.CellLayout, period, time, h.IsTeacherSchedule, h.CustomSeparator);
                if (lessons.Count == 0) { result.UnparsedCells++; continue; }
                var cs = EnsureClass(result.Schedule, name);
                if (!cs.Data.ContainsKey(di)) cs.Data[di] = new Dictionary<int, List<Lesson>>();
                cs.Data[di][period] = lessons;
            }
        if (periods.Count == 0) result.Notes.Add("未识别到节次行");
    }

    // ============ 布局三：单班 / 单教师（二一单班、课程表.xls、安乐小学）============
    static void ParseSingle(IXLWorksheet ws, TemplateHints h, int lastRow, int lastCol, GeneralResult result)
    {
        string name = !string.IsNullOrWhiteSpace(h.SingleName) ? h.SingleName : ws.Name;
        name = NormalizeName(name, h.IsTeacherSchedule);
        if (name.Length == 0) name = ws.Name;
        var cs = new ClassSchedule { ClassName = name };

        // 找星期列：表头行（横排）里所有命中的列
        var dayCols = new List<(int col, int day)>();
        for (int c = 1; c <= lastCol; c++)
        {
            var v = Clean(ws.Cell(h.DayHeaderIndex, c).GetString());
            if (IsDayCell(v)) dayCols.Add((c, DayOf(v)));
        }
        if (dayCols.Count == 0) { result.Notes.Add("单班布局未找到星期列"); result.Schedule.Classes.Add(cs); return; }

        int labelCol = h.PeriodLabelIndex > 0 ? h.PeriodLabelIndex : 1;
        for (int r = h.DayHeaderIndex + 1; r <= lastRow; r++)
        {
            var label = Clean(ws.Cell(r, labelCol).GetString());
            var (period, time, _) = ParsePeriodLabel(label, h.PeriodStyle);
            if (period <= 0) continue;
            foreach (var (col, day) in dayCols)
            {
                var raw = Clean(ws.Cell(r, col).GetString());
                if (raw.Length == 0) continue;
                var lessons = ParseCell(raw, h.CellLayout, period, time, h.IsTeacherSchedule, h.CustomSeparator);
                if (lessons.Count == 0) { result.UnparsedCells++; continue; }
                if (!cs.Data.ContainsKey(day)) cs.Data[day] = new Dictionary<int, List<Lesson>>();
                cs.Data[day][period] = lessons;
            }
        }
        result.Schedule.Classes.Add(cs);
    }

    // ============ 单元格解析：把一格文本拆成 Lesson 列表 ============
    internal static List<Lesson> ParseCell(string raw, CellLayout layout, int period, string time, bool isTeacher,
        string sep = "")
    {
        raw = Clean(raw);
        if (raw.Length == 0) return new List<Lesson>();

        string subjectText, teacherText, locationText;
        switch (layout)
        {
            case CellLayout.Custom:
            {
                // r143：用户自己填的分隔符。「科目｜教师」或「科目｜教师｜地点」，多出来的段都当地点。
                var parts = SplitBySeparator(raw, sep);
                subjectText = parts.Count > 0 ? parts[0] : "";
                teacherText = parts.Count > 1 ? parts[1] : "";
                locationText = parts.Count > 2 ? string.Join(" ", parts.Skip(2)) : "";
                break;
            }
            case CellLayout.Space:
            {
                // "生物 樊玉清" / "体育与健康 王涛 操场" —— 第一段科目，后续按空格连接
                var parts = Regex.Split(raw, @"\s+").Where(x => x.Length > 0).ToArray();
                subjectText = parts.Length > 0 ? parts[0] : "";
                teacherText = parts.Length > 1 ? parts[1] : "";
                locationText = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : "";
                break;
            }
            case CellLayout.Parenthesis:
            {
                // "生物(樊玉清)" / "生物(七（1）班)" —— 括号内是教师或班级
                var m = Regex.Match(raw, @"^(.*?)[（(](.*?)[)）]\s*(.*)$");
                if (m.Success)
                {
                    subjectText = m.Groups[1].Value.Trim();
                    teacherText = m.Groups[2].Value.Trim();
                    locationText = m.Groups[3].Value.Trim();
                }
                else { subjectText = raw; teacherText = ""; locationText = ""; }
                break;
            }
            default: // NewLine
            {
                var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                subjectText = lines.Length > 0 ? lines[0] : "";
                teacherText = lines.Length > 1 ? lines[1] : "";
                locationText = lines.Length > 2 ? lines[2] : "";
                break;
            }
        }

        // 调代标记
        subjectText = SwapRe.Replace(subjectText, "").Trim();
        // 单双周后缀：-单 / -双 / 单 / 双（结尾），以及二一常见的 "心理-单 / 书法-双"
        var subjects = SplitMulti(subjectText, layout == CellLayout.NewLine ? '|' : '|');
        var teachers = SplitMulti(teacherText, '|');
        var lessons = new List<Lesson>();
        for (int i = 0; i < subjects.Count; i++)
        {
            var subj = subjects[i];
            int week = 0;
            if (subj.Contains("单") || subj.Contains("单周")) week = 1;
            else if (subj.Contains("双") || subj.Contains("双周")) week = 2;
            // 清理单双周后缀：既支持 "心理-单" / "书法-双"，也支持 "美术双" / "综合2.1-单" / "语文（单）"
            // 只在结尾清理，且保留科目主体（避免误伤"双减""单价"这类词做开头的情况）
            subj = Regex.Replace(subj, WeekMarkTail, "").Trim();
            if (subj.Length == 0) { result_noop(); continue; }
            var teach = i < teachers.Count ? teachers[i] : "";
            lessons.Add(new Lesson
            {
                Period = period,
                Subject = subj,
                Teacher = teach,
                Location = locationText,
                Week = week,
                TimeRange = time
            });
        }
        return lessons;
    }

    static void result_noop() { }

    static List<string> SplitMulti(string s, char sep)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        var arr = s.Split(new[] { sep, '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(x => x.Length > 0).ToList();
        return arr.Count > 0 ? arr : new List<string> { s.Trim() };
    }

    /// <summary>
    /// r143：按「自定义分隔符」拆一格。支持多字符分隔符（如 <c>" - "</c>）。
    /// <para>★ 分隔符为空时退回「空白」拆法，绝不让用户填了个空就把整格吞掉。</para>
    /// </summary>
    static List<string> SplitBySeparator(string text, string sep)
    {
        text = Clean(text);
        if (text.Length == 0) return new List<string>();
        if (string.IsNullOrWhiteSpace(sep))
            return Regex.Split(text, @"\s+").Where(x => x.Length > 0).ToList();
        return text.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(x => x.Length > 0).ToList();
    }

    // ============ 辅助 ============

    static ClassSchedule EnsureClass(ScheduleFile sf, string name)
    {
        var cs = sf.Classes.FirstOrDefault(x => x.ClassName == name);
        if (cs == null) { cs = new ClassSchedule { ClassName = name }; sf.Classes.Add(cs); }
        return cs;
    }

    static string Clean(string s) => (s ?? "").Replace("\r", "").Trim();

    /// <summary>把标题文本清理成纯班级名 / 教师名：去掉"XX课程表""班主任：XXX"等后缀。</summary>
    internal static string NormalizeName(string s, bool isTeacher)
    {
        s = Clean(s);
        if (s.Length == 0) return s;
        if (isTeacher)
        {
            // "曾胜康-教师课程表" / "樊玉清(11节)" → "曾胜康" / "樊玉清"
            s = Regex.Replace(s, @"[-—－]?\s*(教师|老师)?课程表.*$", "").Trim();
            s = Regex.Replace(s, @"[（(]\s*\d+\s*节\s*[)）].*$", "").Trim();
        }
        else
        {
            // "八年级(22)班-班级课程表" / "五年级(2)班班主任：邝银姬" → "八年级(22)班"
            var m = Regex.Match(s, @"^.*?班");
            if (m.Success && m.Length < s.Length) s = m.Value;
        }
        return s.Trim('-', '—', '－', ' ', '：', ':');
    }

    static bool IsDayCell(string s)
    {
        s = Clean(s);
        if (s.Length == 0) return false;
        return Regex.IsMatch(s, @"^(星期|周|礼拜)?[一二三四五六日天]($|[\s（(])")
            || Regex.IsMatch(s, @"^(mon|tue|wed|thu|fri|sat|sun)", RegexOptions.IgnoreCase);
    }

    static int DayOf(string s)
    {
        s = Clean(s);
        if (s.Contains("一")) return 0;
        if (s.Contains("二")) return 1;
        if (s.Contains("三")) return 2;
        if (s.Contains("四")) return 3;
        if (s.Contains("五")) return 4;
        if (s.Contains("六")) return 5;
        if (s.Contains("日") || s.Contains("天")) return 6;
        if (Regex.IsMatch(s, @"^mon", RegexOptions.IgnoreCase)) return 0;
        if (Regex.IsMatch(s, @"^tue", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(s, @"^wed", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(s, @"^thu", RegexOptions.IgnoreCase)) return 3;
        if (Regex.IsMatch(s, @"^fri", RegexOptions.IgnoreCase)) return 4;
        if (Regex.IsMatch(s, @"^sat", RegexOptions.IgnoreCase)) return 5;
        if (Regex.IsMatch(s, @"^sun", RegexOptions.IgnoreCase)) return 6;
        return -1;
    }

    /// <summary>把节次标签文本解析成 (节次, 时间, 是否固定时段)。固定时段（大课间/眼保健操）返回 0。</summary>
    internal static (int period, string time, bool isFixed) ParsePeriodLabel(string label, PeriodStyle style)
    {
        label = Clean(label);
        if (label.Length == 0) return (0, "", false);
        string time = TimeRe.Match(label).Value;

        // 固定时段：不含数字且是已知的固定词
        if (Regex.IsMatch(label, @"大课间|眼保健操|午休|早读|晚修|团队活动|活动")) return (0, "", true);

        int period = 0;
        var m = Regex.Match(label, @"第\s*(\d+)\s*[节课时]");
        if (m.Success) period = int.Parse(m.Groups[1].Value);
        else
        {
            m = Regex.Match(label, @"(?:上午|下午|AM|PM)\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) period = int.Parse(m.Groups[1].Value);
            else
            {
                // 取该格里的第一个数字（"8:10" 不算——时间里的数字要排除）
                var withoutTime = TimeRe.Replace(label, "");
                m = Regex.Match(withoutTime, @"\d+");
                if (m.Success) period = int.Parse(m.Value);
            }
        }
        return (period, time, false);
    }
}
