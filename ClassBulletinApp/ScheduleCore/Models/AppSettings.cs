namespace ScheduleCore.Models;

public class AppSettings
{
    public string SelectedClass { get; set; } = "";
    public string LastImportPath { get; set; } = "";

    /// <summary>
    /// 课表「显示日」模式：
    /// "Today" = 自动显示今天（默认，调休时改选其他日需切到 Fixed）；
    /// "Fixed" = 固定显示 FixedDisplayDate 这一天（用于调休上课：选某工作日显示其课表）。
    /// </summary>
    public string DisplayDateMode { get; set; } = "Today";

    /// <summary>Fixed 模式下显示的日期 (yyyy-MM-dd)。</summary>
    public string FixedDisplayDate { get; set; } = "";

    /// <summary>
    /// 开学第一周星期一的日期 (yyyy-MM-dd)。留空则回退到 ISO 周奇偶性估算。
    /// 用于把单双周与学校实际教学周对齐。
    /// </summary>
    public string SemesterStartMonday { get; set; } = "";

    /// <summary>是否开机自启（写入当前用户 Run 注册表）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>启动时直接进入全屏展示模式。</summary>
    public bool LaunchFullscreen { get; set; }

    /// <summary>启动即进入防误触锁定模式（需配合 LaunchFullscreen 或单独生效）。</summary>
    public bool LaunchLocked { get; set; }

    /// <summary>启动后是否自动弹出课表悬浮窗。</summary>
    public bool AutoShowSchedule { get; set; } = true;

    /// <summary>启动后是否自动弹出通知悬浮窗。</summary>
    public bool AutoShowNotification { get; set; } = true;

    /// <summary>
    /// 有文档 / 演示程序（Word、PPT、WPS、PDF 等）在前台运行时，自动隐藏课表与通知悬浮窗，避免遮挡教学。
    /// </summary>
    public bool AutoHideOnDocOpen { get; set; }

    /// <summary>课表悬浮窗布局（位置 / 大小 / 透明度 / 置顶 / 穿透 / 靠边）。</summary>
    public PanelLayout ScheduleLayout { get; set; } = new()
    {
        Anchor = "Right",
        Width = 420,
        Height = 760,
        Opacity = 0.96,
        TopMost = true,
        ClickThrough = false,
        AutoHide = false,
        AutoHideDuringClass = false
    };

    /// <summary>通知悬浮窗布局（顶部字幕条）。</summary>
    public PanelLayout NotificationLayout { get; set; } = new()
    {
        Anchor = "Top",
        Width = 0,
        Height = 56,
        Opacity = 0.95,
        TopMost = true,
        ClickThrough = false,
        AutoHide = false,
        AutoHideDuringClass = false,
        FullWidth = true,
        FontSize = 28,
        MarqueeSpeed = 120
    };
}
