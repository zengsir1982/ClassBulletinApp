using ClosedXML.Excel;

namespace ScheduleCore.Services;

/// <summary>
/// 课表导入模板生成器（r124）：产出 4 种标准 .xlsx，格式严格对齐
/// <see cref="ScheduleTemplateProbe"/> / <see cref="ScheduleGeneralImporter"/> 能识别的布局，
/// 用户按模板填写后再走「智能导入课表」，识别准确率接近 100%。
///
/// <para>r130：模板补上单双周写法。同一格单周、双周上不同课时，用与二一课表同一套标记
/// 「科目-单 / 科目-双」，两门之间用竖线「|」隔开（教师行同样用「|」对齐），
/// 例：<c>语文-单|数学-双</c> 换行 <c>张老师|李老师</c>。
/// 解析端 <see cref="ScheduleGeneralImporter.ParseCell"/> 本来就支持这个写法，
/// 之前只是模板没写、用户不知道，导致单双周课程没地方填。</para>
/// </summary>
public static class ImportTemplateWriter
{
    public enum TemplateKind { Base, TempBase, SingleClass, Teacher }

    public static string DefaultFileName(TemplateKind kind) => kind switch
    {
        TemplateKind.Base => "总课表模板.xlsx",
        TemplateKind.TempBase => "临时总课表模板.xlsx",
        TemplateKind.SingleClass => "单班课表模板.xlsx",
        _ => "教师课表模板.xlsx"
    };

    public static string KindLabel(TemplateKind kind) => kind switch
    {
        TemplateKind.Base => "总课表（全校多班）",
        TemplateKind.TempBase => "临时总课表",
        TemplateKind.SingleClass => "单班课表",
        _ => "单个教师课表"
    };

    static readonly string[] Days = { "星期一", "星期二", "星期三", "星期四", "星期五" };

    /// <summary>单双周说明（4 种模板共用一段文案，避免各处写得不一致）。</summary>
    public const string WeekHint =
        "单双周：同一节单周、双周上不同课时，一格写「语文-单|数学-双」（两门之间用竖线 | 隔开），"
        + "下一行教师同样用 | 对齐写「张老师|李老师」；只写一门全周的课不用加任何标记。";

    public static void Write(string path, TemplateKind kind)
    {
        using var wb = new XLWorkbook();
        switch (kind)
        {
            case TemplateKind.Base: WriteMultiClass(wb, "总课程表", temp: false); break;
            case TemplateKind.TempBase: WriteMultiClass(wb, "临时总课表（生效日期：____ 至 ____）", temp: true); break;
            case TemplateKind.SingleClass: WriteSingle(wb, false); break;
            default: WriteSingle(wb, true); break;
        }
        wb.SaveAs(path);
    }

    /// <summary>多班总课表：A 列班级、B 列节次、C~G 星期一~五，格内「科目 换行 教师」。</summary>
    static void WriteMultiClass(XLWorkbook wb, string title, bool temp)
    {
        var ws = wb.Worksheets.Add("总课表");
        ws.Cell(1, 1).Value = title;
        ws.Range(1, 1, 1, 7).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = "班级";
        ws.Cell(2, 2).Value = "节次";
        for (int d = 0; d < Days.Length; d++) ws.Cell(2, d + 3).Value = Days[d];
        HeaderStyle(ws.Range(2, 1, 2, 7));

        string[] classes = { "七年级(1)班", "七年级(2)班" };
        int row = 3;
        foreach (var cls in classes)
            for (int p = 1; p <= 7; p++)
            {
                ws.Cell(row, 1).Value = cls;
                ws.Cell(row, 2).Value = "第" + p + "节";
                if (cls == classes[0])
                {
                    if (p == 1) Example(ws.Cell(row, 3), "语文\n张老师");
                    if (p == 2) Example(ws.Cell(row, 4), "数学\n李老师");
                }
                else
                {
                    // 星期三第 3 节：一格两门 = 单双周（r130 新增示例）
                    if (p == 3) Example(ws.Cell(row, 5), "语文-单|数学-双\n张老师|李老师");
                    // 临时课表才有调代课的写法
                    if (temp && p == 4) Example(ws.Cell(row, 6), "体育（调）\n王老师");
                }
                row++;
            }

        int note = row + 1;
        Note(ws, ref note, "填写说明：一行一节课；同一班级的班级名可只写在第一行，其余行留空即可；"
            + "格内先写科目、换行写教师（也可用空格分隔或「科目(教师)」）。", 7, 44);
        Note(ws, ref note, WeekHint, 7, 48);
        if (temp)
            Note(ws, ref note, "调课 / 代课：科目后面加「（调）」或「（代）」，如「体育（调）」；导入时再选生效日期区间。", 7, 32);
        Note(ws, ref note, "浅黄格是示例，按自己学校填之前删掉即可。", 7, 24);

        Finish(ws, 7, row + 1);
    }

    /// <summary>单班 / 单教师课表：A 列节次，B~F 星期一~五；单班格内「科目 换行 教师」，教师格内「班级 换行 科目」。</summary>
    static void WriteSingle(XLWorkbook wb, bool teacher)
    {
        var ws = wb.Worksheets.Add(teacher ? "教师课表" : "班级课表");
        ws.Cell(1, 1).Value = teacher ? "张老师-教师课程表" : "七年级(1)班-班级课程表";
        ws.Range(1, 1, 1, 6).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = "节次";
        for (int d = 0; d < Days.Length; d++) ws.Cell(2, d + 2).Value = Days[d];
        HeaderStyle(ws.Range(2, 1, 2, 6));

        for (int p = 1; p <= 7; p++)
        {
            ws.Cell(p + 2, 1).Value = "第" + p + "节";
            if (p == 1) Example(ws.Cell(p + 2, 2), teacher ? "七年级(1)班\n语文" : "语文\n张老师");
            if (p == 2) Example(ws.Cell(p + 2, 3), teacher ? "七年级(2)班\n数学" : "数学\n李老师");
            // 星期五第 3 节：单双周示例。教师课表的「科目」位置写的是班级，所以两门是两班轮换
            if (p == 3)
                Example(ws.Cell(p + 2, 6), teacher ? "七年级(3)班-单|七年级(4)班-双\n语文|语文" : "语文-单|数学-双\n张老师|李老师");
        }

        int note = 11;
        Note(ws, ref note, teacher
            ? "填写说明：标题保留「XX-教师课程表」；格内先写班级、换行写科目。"
            : "填写说明：标题保留「XX班-班级课程表」；格内先写科目、换行写教师。", 6, 30);
        Note(ws, ref note, teacher
            ? "单双周：同一节单周、双周教不同班时，一格写「七年级(3)班-单|七年级(4)班-双」（两班之间用竖线 | 隔开），"
              + "下一行科目同样用 | 对齐写「语文|语文」。"
            : WeekHint, 6, 48);
        Note(ws, ref note, "浅黄格是示例，按自己学校填之前删掉即可。", 6, 24);
        Finish(ws, 6, 11);
    }

    /// <summary>示例格：浅黄底 + 自动换行，跟用户要填的白格一眼区分开。</summary>
    static void Example(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF7E0");
        cell.Style.Alignment.WrapText = true;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    /// <summary>表尾说明：合并整行 + 自动换行，长文案不会溢出到表格外面。</summary>
    static void Note(IXLWorksheet ws, ref int row, string text, int lastCol, double height)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        var c = ws.Cell(row, 1);
        c.Value = text;
        c.Style.Font.Italic = true;
        c.Style.Font.FontColor = XLColor.Gray;
        c.Style.Alignment.WrapText = true;
        c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Row(row).Height = height;
        row++;
    }

    static void HeaderStyle(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE9F7");
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }

    /// <summary>收尾：列宽 + 数据行高。<paramref name="firstNoteRow"/> 起的说明行高度单独设，不能被这里覆盖。</summary>
    static void Finish(IXLWorksheet ws, int lastCol, int firstNoteRow)
    {
        ws.Columns(1, lastCol).Width = 18;
        ws.Column(1).Width = 20;
        for (int r = 2; r < firstNoteRow; r++) ws.Row(r).Height = 26;
        ws.Row(1).Height = 30;
        var used = ws.RangeUsed();
        if (used != null)
            used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    }
}
