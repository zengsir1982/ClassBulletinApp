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

    /// <summary>启动后是否自动弹出作业公示悬浮窗。</summary>
    public bool AutoShowHomework { get; set; } = true;

    /// <summary>作业公示便签固定摆放的左上角 X（为空表示未设置，首次弹出时自动定位）。</summary>
    public double? HwWinLeft { get; set; }

    /// <summary>作业公示便签固定摆放的左上角 Y。</summary>
    public double? HwWinTop { get; set; }

    /// <summary>作业公示便签的宽度（为空表示用默认宽度）。</summary>
    public double? HwWinWidth { get; set; }

    /// <summary>作业公示便签的高度。</summary>
    public double? HwWinHeight { get; set; }

    /// <summary>作业公示便签是否置顶显示（默认置顶，教室展示用）。</summary>
    public bool HwWinTopMost { get; set; } = true;

    /// <summary>启动后是否自动弹出缺交作业名单便签。</summary>
    public bool AutoShowMissing { get; set; } = true;

    /// <summary>缺交作业名单便签固定摆放的左上角 X（为空表示未设置，首次弹出时自动定位）。</summary>
    public double? MsWinLeft { get; set; }

    /// <summary>缺交作业名单便签固定摆放的左上角 Y。</summary>
    public double? MsWinTop { get; set; }

    /// <summary>缺交作业名单便签的宽度。</summary>
    public double? MsWinWidth { get; set; }

    /// <summary>缺交作业名单便签的高度。</summary>
    public double? MsWinHeight { get; set; }

    /// <summary>缺交作业名单便签是否置顶显示。</summary>
    public bool MsWinTopMost { get; set; } = true;

    /// <summary>启动后是否自动弹出教师个人课表悬浮窗。</summary>
    public bool AutoShowTeacher { get; set; }

    /// <summary>
    /// 桌面有窗口最大化时，自动隐藏课表与通知悬浮窗，避免遮挡正在使用的程序（不影响操作）。
    /// 仅在“最大化窗口存在”时隐藏；窗口恢复/关闭后自动重新显示。
    /// </summary>
    public bool AutoHideOnMaxWindow { get; set; }

    /// <summary>当前选中的教师（用于教师个人课表）。</summary>
    public string SelectedTeacher { get; set; } = "";

    /// <summary>
    /// 悬浮课表当前显示的对象类型：
    /// "Class" = 显示某个班级的课表（默认）；
    /// "Teacher" = 显示某位教师的个人课表。
    /// 悬浮课表标题双击、控制面板顶栏的班级 / 教师下拉都会更新此值。
    /// </summary>
    public string ScheduleKind { get; set; } = "Class";

    /// <summary>应用作业公示栏壁纸前系统原有壁纸路径，用于「清除」时恢复。</summary>
    public string SavedWallpaper { get; set; } = "";

    /// <summary>上下课提醒设置（课前 / 上课 / 下课 / 放学弹窗提示）。</summary>
    public ReminderSettings Reminder { get; set; } = new();

    /// <summary>桌面背面（当天课表 / 作业公示合成壁纸）设置。</summary>
    public BackdropSettings Backdrop { get; set; } = new();

    /// <summary>界面主题配色方案名称（见 ThemeManager 预设）。</summary>
    public string Theme { get; set; } = "ClassicBlue";

    /// <summary>悬浮课表底部的常用软件 / 文件夹快捷入口。</summary>
    public System.Collections.Generic.List<QuickLaunchItem> QuickLaunchers { get; set; } = new();

    /// <summary>常用快捷是否仅显示图标（不显示文字，更节省空间）。</summary>
    public bool QlIconOnly { get; set; }

    /// <summary>常用快捷图标大小（逻辑像素，16~48）。</summary>
    public double QlIconSize { get; set; } = 28;

    /// <summary>作业公示便签是否被锁定（锁定后不能移动 / 缩放 / 编辑，防误操作）。</summary>
    public bool HwWinLocked { get; set; }

    /// <summary>缺交作业名单便签是否被锁定。</summary>
    public bool MsWinLocked { get; set; }

    /// <summary>用户自建的自定义便签（教师寄语、通知等自由内容），可同时存在多张。</summary>
    public System.Collections.Generic.List<CustomNote> CustomNotes { get; set; } = new();

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

    /// <summary>通知悬浮窗布局（顶部 / 底部字幕条）。</summary>
    public PanelLayout NotificationLayout { get; set; } = new()
    {
        Anchor = "Top",
        Width = 980,
        Height = 60,
        Opacity = 0.96,
        TopMost = true,
        ClickThrough = false,
        AutoHide = false,
        AutoHideDuringClass = false,
        FullWidth = false,
        HAnchor = "Center",
        FontSize = 28,
        MarqueeSpeed = 120
    };
}

/// <summary>悬浮课表底部的常用软件 / 文件夹快捷入口项。</summary>
public class QuickLaunchItem
{
    /// <summary>显示名称（留空则自动取文件 / 文件夹名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>可执行文件(.exe/.bat/.lnk)或文件夹的完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>true = 文件夹（用资源管理器打开）；false = 程序 / 文件（直接打开）。</summary>
    public bool IsFolder { get; set; }
}
