using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace ScheduleCore.Services;

/// <summary>
/// 课表模板探测器（r118）。扫描一个 xlsx，猜出它的布局参数（星期方向、主维度、表头位置、
/// 节次写法、单元格格式），供 <see cref="ScheduleGeneralImporter"/> 按参数解析成统一模型。
///
/// 设计原则：
/// - 只读探测，不改任何数据；探测失败不抛异常，返回低置信度 + 诊断原因，让 UI 交给用户微调。
/// - 所有"格式知识"（星期别名、节次正则、班级写法）集中在这里，新增模板只改别名表。
/// - 探测基于"打分"而不是"硬性约定"：某一项没认出来只扣分，不否定整个文件。
/// </summary>
public static class ScheduleTemplateProbe
{
    // ---------- 探测出的布局参数（UI 可逐项微调后再解析）----------

    public enum DayOrientation { Horizontal, Vertical }
    public enum MainDimension { ClassesInRows, ClassesInColumns, Single }
    public enum PeriodStyle { LabelWithNumber, MorningNumber, PlainNumber }
    public enum CellLayout { NewLine, Space, Parenthesis }

    /// <summary>节次标签的排布方向：纵排=A列每节一行（二一/常规）；横排=第N行每节一列（东凤传统格式）。</summary>
    public enum PeriodOrientation { Vertical, Horizontal }

    /// <summary>单元格里科目与教师的组织方式：同格（换行/空格/括号分隔）或分两行（科目行 + 其下教师行）。</summary>
    public enum CellSpan { SingleRow, TwoRows }

    /// <summary>一份完整的模板参数。UI 允许改这里的大部分字段，改完重新解析。</summary>
    public class TemplateHints
    {
        public int SheetIndex { get; set; }
        public string SheetName { get; set; } = "";

        public DayOrientation DayOrientation { get; set; } = DayOrientation.Horizontal;
        public MainDimension MainDimension { get; set; } = MainDimension.ClassesInRows;

        /// <summary>星期表头所在行（横排）或列（纵列），1-based。0=未识别。</summary>
        public int DayHeaderIndex { get; set; }
        /// <summary>节次标签所在列（星期横排+班在行）或行（星期纵列），1-based。0=未识别。</summary>
        public int PeriodLabelIndex { get; set; }
        /// <summary>数据起始行（横排布局）或起始列（纵列布局），1-based。0=未识别。</summary>
        public int DataStartIndex { get; set; }
        /// <summary>名称列（班在行）/名称行（班在列）/单班的班级名单元格行，1-based。0=未识别。</summary>
        public int NameIndex { get; set; }

        public PeriodStyle PeriodStyle { get; set; } = PeriodStyle.LabelWithNumber;
        public CellLayout CellLayout { get; set; } = CellLayout.NewLine;
        public PeriodOrientation PeriodOrientation { get; set; } = PeriodOrientation.Vertical;
        public CellSpan CellSpan { get; set; } = CellSpan.SingleRow;

        /// <summary>单班/单教师课表的名字（从标题格取），多班布局留空。</summary>
        public string SingleName { get; set; } = "";
        /// <summary>这是教师课表（单元格第二行是"所教班级"而非教师姓名）？</summary>
        public bool IsTeacherSchedule { get; set; }

        /// <summary>
        /// 命中二一排课系统的"天块内横排节次"专用布局（表头下方每个星期块内都横排着完整的
        /// 第1节..第8节 + 大课间/眼保健操）。这种布局由专用解析器 ScheduleImporter 处理最稳，
        /// 智能导入检测到它时直接转交专用解析器，不走通用逐格解析。
        /// </summary>
        public bool UseBuiltInEryiParser { get; set; }

        /// <summary>探测置信度 0~100。UI 低于阈值时强制显示预览。</summary>
        public int Confidence { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public class ProbeResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public List<TemplateHints> Candidates { get; set; } = new();
        public TemplateHints Best => Candidates.FirstOrDefault()!;
    }

    // ---------- 关键词 / 正则别名表（新增格式主要改这里）----------

    static readonly Regex DayShortRe = new(@"^(星期|周|礼拜)[一二三四五六日天]$", RegexOptions.IgnoreCase);
    /// <summary>纯单字星期（一/二/三…）——仅在表头行且出现多个时才认，避免误伤正文。</summary>
    static readonly Regex DayBareRe = new(@"^[一二三四五六日]$");
    static readonly Regex DayEnRe = new(@"^(mon|tue|wed|thu|fri|sat|sun)", RegexOptions.IgnoreCase);
    static readonly Regex PeriodLabelRe = new(@"第\s*(\d+)\s*节|第\s*(\d+)\s*课时|Period\s*(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex MorningNumberRe = new(@"^(上午|下午|早读|晚修)\s*(\d+)?$");
    static readonly Regex PlainNumberRe = new(@"^\d{1,2}$");
    static readonly Regex TimeRe = new(@"(\d{1,2})[:\:～\-](\d{1,2})");
    // 班级名：中文年级/数字 + 班；也接受 "Class 1" 之类
    static readonly Regex ClassNameRe = new(@"(年级|[高初][一二三]|\d)[\s（(]*\d*\s*[)）]?\s*班|^[一二三四五六七八九十]+班$|^Class\s*\d+", RegexOptions.IgnoreCase);
    static readonly Regex TeacherTitleRe = new(@"教师课程表|老师课程表|教师课表");

    static bool IsDayHeader(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        // 必须是完整形式"星期一/周一/礼拜一/Monday"，或带短后缀；
        // 绝不认"时间 星期""周偲琪""一"这类，避免把表头文字/人名/正文误判成星期。
        if (DayShortRe.IsMatch(s)) return true;
        if (DayEnRe.IsMatch(s) && Regex.IsMatch(s, @"^(mon|tue|wed|thu|fri|sat|sun)(day)?\b|^星期", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(s, @"^(星期|周|礼拜)[一二三四五六日天][\s（(]?[^\d]{0,6}$")) return true;
        return false;
    }

    static int DayIndex(string s)
    {
        s = s.Trim();
        if (Regex.IsMatch(s, @"[一]|^mon", RegexOptions.IgnoreCase)) return 0;
        if (Regex.IsMatch(s, @"[二]|^tue", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(s, @"[三]|^wed", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(s, @"[四]|^thu", RegexOptions.IgnoreCase)) return 3;
        if (Regex.IsMatch(s, @"[五]|^fri", RegexOptions.IgnoreCase)) return 4;
        if (Regex.IsMatch(s, @"[六]|^sat", RegexOptions.IgnoreCase)) return 5;
        if (Regex.IsMatch(s, @"[日天]|^sun", RegexOptions.IgnoreCase)) return 6;
        return -1;
    }

    /// <summary>探测一个文件里所有 sheet，按置信度从高到低返回候选。</summary>
    public static ProbeResult Probe(string path)
    {
        var res = new ProbeResult();
        if (!File.Exists(path)) { res.Message = "文件不存在"; return res; }
        try
        {
            using var wb = new XLWorkbook(path);
            for (int i = 1; i <= wb.Worksheets.Count; i++)
            {
                var h = ProbeSheet(wb.Worksheet(i), i);
                res.Candidates.Add(h);
            }
        }
        catch (Exception ex)
        {
            res.Message = "读取失败：" + ex.Message;
            return res;
        }
        res.Candidates = res.Candidates.OrderByDescending(h => h.Confidence).ToList();
        res.Ok = res.Candidates.Count > 0;
        res.Message = res.Ok
            ? $"识别到 {res.Candidates.Count} 个候选工作表，最佳置信度 {res.Best.Confidence}%"
            : "未识别到可用的课表布局";
        return res;
    }

    static TemplateHints ProbeSheet(IXLWorksheet ws, int sheetIndex)
    {
        var h = new TemplateHints { SheetIndex = sheetIndex, SheetName = ws.Name };
        int lastRow = Math.Min(ws.LastCellUsed()?.Address.RowNumber ?? 1, 200);
        int lastCol = Math.Min(ws.LastCellUsed()?.Address.ColumnNumber ?? 1, 120);

        // 标题格（用于单班名字）：
        // 优先取"八年级(22)班-班级课程表" / "曾胜康-教师课程表" / "五年级(2)班班主任"这类
        // 带着班级/教师身份的第 2~3 行文本；找不到才退回第 1 行的大标题（学校名 + 学期）。
        string title = "";
        string bigTitle = "";
        for (int r = 1; r <= Math.Min(4, lastRow); r++)
            for (int c = 1; c <= Math.Min(3, lastCol); c++)
            {
                var v = Clean(ws.Cell(r, c).GetString());
                if (v.Length == 0 || v.Length > 60) continue;
                if (r == 1 && bigTitle.Length == 0) bigTitle = v;
                // 身份行：含"班"或"教师课程表/老师"，且比当前 title 短而精
                bool identity = ClassNameRe.IsMatch(v) || TeacherTitleRe.IsMatch(v) || v.Contains("班主任");
                if (identity && (title.Length == 0 || v.Length < title.Length)) title = v;
            }
        if (title.Length == 0) title = bigTitle;

        // 找所有"星期"表头格的位置。
        // ★ 纵列总表/老式单班里，星期标记（星期一..五）分散在 A 列的多行上，
        //   必须扫全表（不再只扫前12行），否则只找到 1 个就判不出纵列布局。
        var dayCells = new List<(int r, int c, int day)>();
        for (int r = 1; r <= lastRow; r++)
            for (int c = 1; c <= lastCol; c++)
            {
                var v = ws.Cell(r, c).GetString();
                if (IsDayHeader(v)) dayCells.Add((r, c, DayIndex(v)));
            }

        if (dayCells.Count == 0)
        {
            h.Confidence = 5;
            h.Notes.Add("未找到星期表头（星期一/周一/Monday 等）");
            h.SingleName = title;
            return h;
        }

        // 判定方向：同一行里出现多个星期 → 横排；同一列里出现多个 → 纵列
        var byRow = dayCells.GroupBy(x => x.r).OrderByDescending(g => g.Count()).First();
        var byCol = dayCells.GroupBy(x => x.c).OrderByDescending(g => g.Count()).First();
        bool horizontal = byRow.Count() >= byCol.Count();
        h.DayOrientation = horizontal ? DayOrientation.Horizontal : DayOrientation.Vertical;
        h.DayHeaderIndex = horizontal ? byRow.Key : byCol.Key;
        h.Confidence += 25;

        if (horizontal)
        {
            // 表头行确定后，找该行里连续的星期列（按顺序对应周一..周日）
            var cols = dayCells.Where(x => x.r == h.DayHeaderIndex).OrderBy(x => x.c).ToList();
            if (cols.Count < 2) { h.Notes.Add("横排星期表头只找到 1 列"); h.Confidence -= 8; }

            // ★ 二一专用布局识别：表头下一行里"第N节"出现多次（每个星期块内都重复排了一遍）
            //   这种布局是天块横排、块内节次也横排，通用逐格解析会拿错列，交给专用解析器。
            int periodLabelRow = h.DayHeaderIndex + 1;
            int labelHit = 0;
            for (int c = 1; c <= lastCol; c++)
                if (PeriodLabelRe.IsMatch(ws.Cell(periodLabelRow, c).GetString())) labelHit++;
            if (labelHit >= 5)
            {
                h.UseBuiltInEryiParser = true;
                h.Confidence = 95;
                h.Notes.Add("识别为二一系统标准总课表（天块内横排节次），将用专用解析器");
                h.MainDimension = MainDimension.ClassesInRows;
                h.NameIndex = 1;
                return h;
            }

            // 节次标签列：表头行下一行起，找"第N节/上午N/数字"最多的一列（一般在A/B列）
            var labelCol = FindPeriodLabelColumn(ws, h.DayHeaderIndex + 1, lastRow, Math.Min(4, lastCol), out var pstyle);
            h.PeriodLabelIndex = labelCol;
            h.PeriodStyle = pstyle;
            if (labelCol > 0) h.Confidence += 20; else h.Notes.Add("未找到节次标签列");

            h.DataStartIndex = h.DayHeaderIndex + 1;
            h.NameIndex = labelCol;

            // 班级名：若节次列右侧第一个星期列之前有"班"字样列 → 多班（班在行）；否则单班
            int firstDayCol = cols.Count > 0 ? cols.Min(x => x.c) : 2;
            var nameCol = FindColumnMatching(ws, h.DayHeaderIndex + 1, Math.Min(lastRow, h.DayHeaderIndex + 30),
                Math.Max(1, firstDayCol - 1), ClassNameRe);
            if (nameCol > 0 && labelCol > 0 && nameCol != labelCol)
            {
                h.MainDimension = MainDimension.ClassesInRows;
                h.NameIndex = nameCol;
                h.Confidence += 20;
            }
            else if (ClassNameRe.IsMatch(title))
            {
                h.MainDimension = MainDimension.Single;
                h.SingleName = title;
                h.Confidence += 12;
            }
            else
            {
                h.MainDimension = MainDimension.Single;
                h.SingleName = title;
                h.Confidence += 5;
            }
            h.IsTeacherSchedule = TeacherTitleRe.IsMatch(title);
        }
        else
        {
            // 纵列：星期在某一列的多行上（A4/A25/A46...），班级在星期表头行的上一行横排（R3 D..）。
            var rows = dayCells.Where(x => x.c == h.DayHeaderIndex).OrderBy(x => x.r).ToList();
            int headerRow = rows.Count > 0 ? rows.Min(x => x.r) : 1;
            // 班级表头常在星期行的上一行（纵列总表 R3），也兼容同一行
            int classHeaderRow = -1, nameColsCount = -1;
            foreach (int row in new[] { headerRow - 1, headerRow, headerRow + 1 })
            {
                if (row < 1) continue;
                int cnt = 0;
                for (int c = h.DayHeaderIndex + 1; c <= lastCol; c++)
                    if (ClassNameRe.IsMatch(ws.Cell(row, c).GetString())) cnt++;
                if (cnt > nameColsCount) { nameColsCount = cnt; classHeaderRow = row; }
            }

            if (nameColsCount >= 2)
            {
                h.MainDimension = MainDimension.ClassesInColumns;
                h.NameIndex = classHeaderRow;
                h.Confidence += 20;
                // 节次标签列：星期列右侧第一列里"数字/早读/大课间"最多的那列（纵列总表是 C 列）
                int labelCol = lastCol >= h.DayHeaderIndex + 2 ? h.DayHeaderIndex + 2 : h.DayHeaderIndex + 1;
                h.PeriodLabelIndex = labelCol;
                h.PeriodStyle = DetectPeriodStyleColumn(ws, labelCol, headerRow + 1, Math.Min(lastRow, headerRow + 25));
                h.DataStartIndex = h.DayHeaderIndex + 1;
                h.Notes.Add("识别为纵列总表（星期在列 + 班级在表头行）");
            }
            else
            {
                h.MainDimension = MainDimension.Single;
                h.SingleName = title;
                h.Confidence += 8;
            }
            h.IsTeacherSchedule = TeacherTitleRe.IsMatch(title);
        }

        // 单元格格式：采样几个数据格，看分隔符
        h.CellLayout = DetectCellLayout(ws, h, lastRow, lastCol);

        // ★ 增强探测：识别"东凤传统格式"——节次横排 + 科目/教师双行格。
        //   这类布局前面按"节次纵排"的假设会认错方向，这里统一复核并纠正。
        EnhanceForTraditionalFormat(ws, h, title, dayCells, lastRow, lastCol);

        h.Confidence = Math.Max(0, Math.Min(100, h.Confidence));
        if (h.Confidence >= 70) h.Notes.Add("布局识别较可靠");
        else h.Notes.Add("置信度偏低，建议核对右侧参数");
        return h;
    }

    /// <summary>
    /// 复核并纠正"东凤传统格式"：节次标签横排在一行里（如 1/2/3 或 第1节/第2节），
    /// 且科目与其教师分两行存放（第一行科目，第二行教师名）。
    /// 三种子布局：横行总表（星期横排+班在行）、纵列总表（星期纵列+班在列）、老式单班（星期纵列+单班）。
    /// </summary>
    static void EnhanceForTraditionalFormat(IXLWorksheet ws, TemplateHints h, string title,
        List<(int r, int c, int day)> dayCells, int lastRow, int lastCol)
    {
        // 找"节次标签横排"的行：某一行里出现 >=3 个节次标记（数字或第N节），且彼此在不同列
        int periodRow = 0, periodHits = 0;
        for (int r = 1; r <= Math.Min(10, lastRow); r++)
        {
            int hits = 0;
            for (int c = 1; c <= lastCol; c++)
            {
                var v = Clean(ws.Cell(r, c).GetString());
                if (PeriodLabelRe.IsMatch(v) || PlainNumberRe.IsMatch(v)) hits++;
            }
            if (hits > periodHits) { periodHits = hits; periodRow = r; }
        }
        bool hasHorizontalPeriods = periodHits >= 3;

        // 判定"双行格"：在星期数据区里，某些格的第二行是纯教师名（2~4 个汉字，无数字无标点）
        bool twoRows = false;
        if (hasHorizontalPeriods)
        {
            int sampleDayRow = dayCells.Count > 0 ? dayCells.Min(x => x.r) + 2 : periodRow + 1;
            for (int r = periodRow + 1; r <= Math.Min(periodRow + 8, lastRow) && !twoRows; r++)
                for (int c = 1; c <= Math.Min(lastCol, 40); c++)
                {
                    var v = Clean(ws.Cell(r, c).GetString());
                    var prev = r > 1 ? Clean(ws.Cell(r - 1, c).GetString()) : "";
                    // 上一格有科目（含汉字），本格是纯人名（2-4 汉字）——典型的教师行
                    if (prev.Length >= 2 && Regex.IsMatch(v, @"^[\u4e00-\u9fa5]{2,4}$") && !Regex.IsMatch(prev, @"^\d+$"))
                    { twoRows = true; break; }
                }
        }

        if (!hasHorizontalPeriods)
        {
            // ★ 纵列总表：节次纵排在 C 列（不是横排），星期在 A 列、班级在表头行横排，
            //   每个数据格占两行（科目行 + 教师行）。前面"节次横排"的假设认不出它，这里补识别。
            if (h.DayOrientation == DayOrientation.Vertical && h.MainDimension == MainDimension.ClassesInColumns)
            {
                int dayColV = h.DayHeaderIndex;
                int classRowV = h.NameIndex > 0 ? h.NameIndex : 3;
                int labelColV = lastCol >= dayColV + 2 ? dayColV + 2 : dayColV + 1;
                bool verticalTwoRows = false;
                for (int r = classRowV + 1; r <= Math.Min(lastRow, classRowV + 60) && !verticalTwoRows; r++)
                {
                    var lbl = Clean(ws.Cell(r, labelColV).GetString());
                    if (lbl.Length == 0) continue;                       // 科目行应有节次/时段标签
                    var nextLbl = Clean(ws.Cell(r + 1, labelColV).GetString());
                    if (nextLbl.Length > 0) continue;                    // 下一行也有标签 → 不是教师行
                    // 下一行在若干班级列里还有内容（教师名）→ 判定为双行格
                    for (int c = dayColV + 1; c <= Math.Min(lastCol, dayColV + 8); c++)
                        if (Clean(ws.Cell(r + 1, c).GetString()).Length >= 2) { verticalTwoRows = true; break; }
                }
                if (verticalTwoRows)
                {
                    h.CellSpan = CellSpan.TwoRows;
                    h.PeriodOrientation = PeriodOrientation.Vertical;
                    h.Confidence += 15;
                    h.Notes.Add("识别为传统纵列总表（星期在列 + 班级在行 + 节次纵排 + 科目教师双行）");
                }
            }
            return; // 其它情况不是横排节次传统格式，保持原判
        }

        h.PeriodOrientation = PeriodOrientation.Horizontal;
        h.PeriodLabelIndex = periodRow;
        if (twoRows) h.CellSpan = CellSpan.TwoRows;
        h.Confidence += 15;

        bool dayHorizontal = h.DayOrientation == DayOrientation.Horizontal;
        if (dayHorizontal)
        {
            // 横行总表：星期横排 + 班在 A 列（班级格是合并的，每个班占两行）
            h.MainDimension = MainDimension.ClassesInRows;
            h.NameIndex = 1;
            h.DataStartIndex = periodRow + 1;
            h.PeriodStyle = DetectPeriodStyleRow(ws, periodRow, 1, lastCol);
            h.Notes.Add("识别为传统横行总表（节次横排 + 班级在行 + 科目教师双行）");
        }
        else
        {
            // 星期纵列：再看班级是否横排在第3行 → 纵列总表；否则老式单班
            int headerRow = dayCells.Count > 0 ? dayCells.Min(x => x.r) : 1;
            int classCols = 0;
            for (int c = h.DayHeaderIndex + 1; c <= lastCol; c++)
                if (ClassNameRe.IsMatch(ws.Cell(headerRow - 1 >= 1 ? headerRow - 1 : headerRow, c).GetString())
                    || ClassNameRe.IsMatch(ws.Cell(headerRow, c).GetString())) classCols++;
            if (classCols >= 2)
            {
                h.MainDimension = MainDimension.ClassesInColumns;
                h.NameIndex = headerRow - 1 >= 1 ? headerRow - 1 : headerRow;
                h.DataStartIndex = h.DayHeaderIndex + 1;
                h.Notes.Add("识别为传统纵列总表（星期在列 + 班级在行 + 科目教师双行）");
            }
            else
            {
                h.MainDimension = MainDimension.Single;
                h.SingleName = title;
                h.Notes.Add("识别为传统单班表（星期在列 + 节次横排 + 科目教师双行）");
            }
            h.PeriodStyle = DetectPeriodStyleRow(ws, periodRow, 1, lastCol);
        }
    }

    static PeriodStyle DetectPeriodStyleRow(IXLWorksheet ws, int row, int fromCol, int toCol)
    {
        int lbl = 0, plain = 0;
        for (int c = fromCol; c <= toCol; c++)
        {
            var v = Clean(ws.Cell(row, c).GetString());
            if (PeriodLabelRe.IsMatch(v)) lbl++;
            else if (PlainNumberRe.IsMatch(v)) plain++;
        }
        return lbl >= plain && lbl > 0 ? PeriodStyle.LabelWithNumber : PeriodStyle.PlainNumber;
    }

    static string Clean(string s) => (s ?? "").Replace("\r", "").Trim();

    static int FindPeriodLabelColumn(IXLWorksheet ws, int fromRow, int toRow, int maxCol, out PeriodStyle style)
    {
        int bestCol = 0, bestScore = 0;
        style = PeriodStyle.LabelWithNumber;
        for (int c = 1; c <= maxCol; c++)
        {
            int score = 0;
            PeriodStyle localStyle = PeriodStyle.LabelWithNumber;
            for (int r = fromRow; r <= toRow; r++)
            {
                var v = Clean(ws.Cell(r, c).GetString());
                if (v.Length == 0) continue;
                if (PeriodLabelRe.IsMatch(v)) { score += 3; localStyle = PeriodStyle.LabelWithNumber; }
                else if (MorningNumberRe.IsMatch(v)) { score += 2; localStyle = PeriodStyle.MorningNumber; }
                else if (PlainNumberRe.IsMatch(v)) { score += 1; localStyle = PeriodStyle.PlainNumber; }
            }
            if (score > bestScore) { bestScore = score; bestCol = c; style = localStyle; }
        }
        return bestCol;
    }

    static PeriodStyle DetectPeriodStyleColumn(IXLWorksheet ws, int col, int fromRow, int toRow)
    {
        int lbl = 0, morn = 0, plain = 0;
        for (int r = fromRow; r <= toRow; r++)
        {
            var v = Clean(ws.Cell(r, col).GetString());
            if (v.Length == 0) continue;
            if (PeriodLabelRe.IsMatch(v)) lbl++;
            else if (MorningNumberRe.IsMatch(v)) morn++;
            else if (PlainNumberRe.IsMatch(v)) plain++;
        }
        if (lbl >= morn && lbl >= plain && lbl > 0) return PeriodStyle.LabelWithNumber;
        if (morn >= plain && morn > 0) return PeriodStyle.MorningNumber;
        return plain > 0 ? PeriodStyle.PlainNumber : PeriodStyle.LabelWithNumber;
    }

    static int FindColumnMatching(IXLWorksheet ws, int fromRow, int toRow, int col, Regex re)
    {
        int best = 0, bestHits = 0;
        for (int r = fromRow; r <= toRow; r++)
        {
            var v = Clean(ws.Cell(r, col).GetString());
            if (v.Length > 0 && re.IsMatch(v)) bestHits++;
        }
        if (bestHits > 0) best = col;
        return best;
    }

    static CellLayout DetectCellLayout(IXLWorksheet ws, TemplateHints h, int lastRow, int lastCol)
    {
        int nl = 0, sp = 0, pa = 0;
        int step = Math.Max(1, lastRow / 12);
        for (int r = h.DayHeaderIndex + 1; r <= lastRow; r += step)
            for (int c = 1; c <= lastCol; c++)
            {
                var v = ws.Cell(r, c).GetString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (v.Contains('\n') || v.Contains('\r')) nl++;
                else if (Regex.IsMatch(v, @"[\u4e00-\u9fa5]\s+[\u4e00-\u9fa5]")) sp++;
                else if (Regex.IsMatch(v, @"[\u4e00-\u9fa5][（(][\u4e00-\u9fa5（）()]+[)）]")) pa++;
            }
        if (pa > nl && pa > sp) return CellLayout.Parenthesis;
        if (sp > nl) return CellLayout.Space;
        return CellLayout.NewLine;
    }
}
