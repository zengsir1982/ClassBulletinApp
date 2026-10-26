using System;

namespace ScheduleCore.Models;

/// <summary>
/// 单节课的临时覆盖（调课/代课/换教室等）。
/// 用于在总课表之外，临时替换某班某天某节课的内容，优先级高于导入的课表。
/// </summary>
public class LessonOverride
{
    public string ClassName { get; set; } = "";
    public int DayIdx { get; set; }   // 0=周一 .. 4=周五
    public int Period { get; set; }   // 第几节

    public string Subject { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    public string Note { get; set; } = "";   // 调课原因/备注，如「运动会临时调整」

    /// <summary>调代类型：空=普通手动调课；"调"=临时调课；"代"=代课。</summary>
    public string Type { get; set; } = "";

    /// <summary>是否把该格调成「空课」（无课）。与 Subject 同时存在时优先视为清空。</summary>
    public bool IsEmpty { get; set; }

    /// <summary>来源：manual=手动编辑；temp=从临时总课表导入。用于导入时区分、避免误清手动记录。</summary>
    public string Source { get; set; } = "manual";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>唯一键：班级|星期|节次。</summary>
    public string Key => $"{ClassName}|{DayIdx}|{Period}";
}
