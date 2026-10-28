namespace ScheduleCore.Models;

/// <summary>
/// 「桌面背面」设置：把「当天课表」「作业公示」「缺交作业名单」三张便签渲染成一张图片并设为桌面壁纸。
/// 三者可同时显示，位置 / 大小 / 主题各自独立（九宫格锚点 + 像素偏移 + 宽度比例或固定像素尺寸）。
/// 壁纸位于所有窗口之下，既不影响电脑操作，也不遮挡正在使用的程序。
/// </summary>
public class BackdropSettings
{
    /// <summary>壁纸中是否绘制「当天课表」。</summary>
    public bool ShowSchedule { get; set; }

    /// <summary>壁纸中是否绘制「作业公示」。</summary>
    public bool ShowHomework { get; set; }

    /// <summary>壁纸中是否绘制「缺交作业名单」（独立便签，位置 / 主题 / 大小可单独设置）。</summary>
    public bool ShowMissing { get; set; }

    /// <summary>当天课表便签的主题风格标识（见 HomeworkThemes.Presets）。</summary>
    public string ScheduleTheme { get; set; } = "blue";

    /// <summary>当天课表卡片的位置 / 宽度。</summary>
    public HomeworkWallpaperLayout ScheduleLayout { get; set; } = new()
    {
        Anchor = "MiddleLeft",
        WidthRatio = 0.38
    };

    /// <summary>
    /// 作业公示卡片的位置 / 宽度。为 null 时沿用 homework.json 里的 Layout（兼容旧数据）。
    /// </summary>
    public HomeworkWallpaperLayout? HomeworkLayout { get; set; }

    /// <summary>缺交作业名单便签的位置 / 大小（主题取自 missing.json 的 Theme）。</summary>
    public HomeworkWallpaperLayout MissingLayout { get; set; } = new()
    {
        Anchor = "BottomRight",
        UseFixedSize = true,
        FixedWidth = 420,
        FixedHeight = 360
    };

    /// <summary>
    /// 是否把系统原有壁纸作为底图（推荐）：读取 SavedWallpaper，铺满画布后再叠加卡片。
    /// 关闭或读不到原图时使用浅色渐变底。
    /// </summary>
    public bool UseOriginWallpaper { get; set; } = true;
}
