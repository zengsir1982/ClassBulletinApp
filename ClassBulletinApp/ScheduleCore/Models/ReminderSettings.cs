namespace ScheduleCore.Models;

/// <summary>
/// 上下课提醒设置（参照 ClassIsland 的课前/上课/下课/放学提醒设计）：
/// 依据当前显示的班级 / 教师课表的节次时间，到点自动弹出提醒窗。
/// </summary>
public class ReminderSettings
{
    /// <summary>是否启用上下课提醒（总开关）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>上课前提前多少分钟提醒（0 = 不做课前提醒）。参照 ClassIsland 默认课前 1 分钟提示“准备上课”。</summary>
    public int BeforeClassMinutes { get; set; } = 1;

    /// <summary>下课前提前多少分钟提醒（0 = 仅在下课时提示“课间休息”）。</summary>
    public int BeforeEndMinutes { get; set; } = 0;

    /// <summary>当天最后一节课结束后是否提示放学（参照 ClassIsland 的放学提醒）。</summary>
    public bool AfterSchoolRemind { get; set; } = true;

    /// <summary>提醒时是否播放提示音。</summary>
    public bool PlaySound { get; set; }

    /// <summary>提醒窗口显示时长（秒）。</summary>
    public int DisplaySeconds { get; set; } = 8;
}
