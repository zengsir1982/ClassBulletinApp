using System.Collections.Generic;

namespace ClassBulletinApp;

public class PeriodRow
{
    public int DayIdx { get; set; }      // 0=周一 .. 4=周五
    public int Period { get; set; }      // 第几节
    public string PeriodText { get; set; } = "";
    public string TimeRange { get; set; } = "";
    public string CardBg { get; set; } = "#FFFFFF";
    public List<LessonView> Lessons { get; set; } = new();
}

public class LessonView
{
    public int DayIdx { get; set; }
    public int Period { get; set; }
    public string Subject { get; set; } = "";
    public string Teacher { get; set; } = "";
    /// <summary>教师字段前缀：班级课表显示“教师：”，教师个人课表显示“班级：”。</summary>
    public string TeacherPrefix { get; set; } = "教师：";
    public string LocationText { get; set; } = "";
    public string WeekTag { get; set; } = "";
    public string SubjectColor { get; set; } = "#222222";

    // 调课覆盖相关
    public bool IsOverridden { get; set; }
    public string OverrideType { get; set; } = "";   // "" / "调" / "代"
    public string OverrideBadgeText { get; set; } = "";   // 徽章文案：已调课 / 代课
    public string OverrideBadgeColor { get; set; } = "#E67E22"; // 徽章底色：调=橙，代=蓝
    public string OverrideNote { get; set; } = "";
}

/// <summary>周视图（整周课表）中的单个课程格子。</summary>
public class WeekCell
{
    public int DayIdx { get; set; }       // 0=周一 .. 6=周日
    public int Period { get; set; }       // 第几节
    public string DayName { get; set; } = "";
    public string PeriodText { get; set; } = "";     // 第X节（含时间）
    public string TimeRange { get; set; } = "";
    public string Subject { get; set; } = "";        // 有效科目（override 优先；空=无课）
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";       // 原始地点（不带前缀）
    public string LocationText { get; set; } = "";   // 显示用（带「 · 」前缀）
    public string WeekTag { get; set; } = "";        // 单周/双周标签
    public bool IsOverridden { get; set; }           // 是否被调课/代课/调空覆盖
    public bool IsEmpty { get; set; }                // 被调成空课
    public string Type { get; set; } = "";           // override 类型：调/代/空
    public string BadgeText { get; set; } = "";      // 徽章文案
    public string BadgeColor { get; set; } = "";     // 徽章底色
    public string SubjectColor { get; set; } = "#222222";
    public bool HasLesson => !string.IsNullOrEmpty(Subject);
}

/// <summary>整周课表数据：周一..周日 × 各节次。</summary>
public class WeekGridData
{
    public DateTime Monday { get; set; }
    public int MaxPeriod { get; set; } = 1;
    public List<WeekCell> Cells { get; set; } = new();
    public List<string> DayHeaders { get; set; } = new();   // 7 个，含星期与日期
    public List<string> PeriodHeaders { get; set; } = new(); // MaxPeriod 个
}
