using System;
using System.Collections.Generic;

namespace ScheduleCore.Models;

/// <summary>单科作业条目。</summary>
public class HomeworkItem
{
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";

    /// <summary>作业内容是否加粗。</summary>
    public bool Bold { get; set; }

    /// <summary>作业内容颜色（#RRGGBB；空表示默认墨色）。</summary>
    public string Color { get; set; } = "";

    /// <summary>作业内容字号档位：0=标准 1=小 2=大 3=特大。</summary>
    public int SizeLevel { get; set; }
}

/// <summary>作业文本样式档位工具：悬浮窗与桌面背景渲染共用，保证两边显示一致。</summary>
public static class HwTextStyle
{
    /// <summary>字号档位对应的基准字号（0=标准 1=小 2=大 3=特大），渲染时再按卡片比例缩放。</summary>
    public static readonly double[] Sizes = { 19, 16, 23, 27 };

    /// <summary>取指定档位的字号（越界自动收敛到合法范围）。</summary>
    public static double SizeOf(int level) => Sizes[Math.Clamp(level, 0, Sizes.Length - 1)];

    /// <summary>档位名称，用于下拉与提示。</summary>
    public static readonly string[] LevelNames = { "标准", "小", "大", "特大" };

    /// <summary>预设颜色（值 / 显示名），悬浮窗下拉与桌面背景渲染共用。</summary>
    public static readonly (string Value, string Name)[] PresetColors =
    {
        ("", "默认墨色"),
        ("#C0392B", "红色"),
        ("#2D6CDF", "蓝色"),
        ("#1E8E3E", "绿色"),
        ("#E07B00", "橙色"),
        ("#7B3FBF", "紫色")
    };
}

/// <summary>
/// 作业公示图在桌面壁纸上的摆放方式：
/// Anchor 为九宫格锚点，OffsetX / OffsetY 为相对锚点的像素偏移（右、下为正），
/// WidthRatio 为卡片宽度占屏幕宽度的比例。
/// </summary>
public class HomeworkWallpaperLayout
{
    /// <summary>
    /// 九宫格锚点：TopLeft / TopCenter / TopRight / MiddleLeft / MiddleCenter /
    /// MiddleRight / BottomLeft / BottomCenter / BottomRight（兼容旧值 Right = MiddleRight）。
    /// </summary>
    public string Anchor { get; set; } = "MiddleRight";

    /// <summary>水平偏移（像素，向右为正）。</summary>
    public double OffsetX { get; set; }

    /// <summary>垂直偏移（像素，向下为正）。</summary>
    public double OffsetY { get; set; }

    /// <summary>卡片宽度占屏幕宽度的比例 0.25~0.9。</summary>
    public double WidthRatio { get; set; } = 0.4;
}

/// <summary>单条缺交作业记录：可指定科目（留空表示通用），以及缺交学生姓名（换行 / 逗号分隔）。</summary>
public class MissingHomeworkItem
{
    /// <summary>科目（留空表示通用 / 不区分科目）。</summary>
    public string Subject { get; set; } = "";

    /// <summary>缺交学生姓名：每行一个或用逗号 / 顿号分隔均可。</summary>
    public string Students { get; set; } = "";

    /// <summary>名单是否加粗。</summary>
    public bool Bold { get; set; }

    /// <summary>名单颜色（#RRGGBB；空表示默认墨色）。</summary>
    public string Color { get; set; } = "";

    /// <summary>名单字号档位：0=标准 1=小 2=大 3=特大。</summary>
    public int SizeLevel { get; set; }
}

/// <summary>作业公示栏内容：标题 + 各科目作业 + 缺交作业名单 + 桌面背景摆放位置。</summary>
public class HomeworkBoard
{
    public string Title { get; set; } = "各科作业公示";
    public List<HomeworkItem> Items { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>缺交作业名单（可按科目登记未交学生）。</summary>
    public List<MissingHomeworkItem> Missing { get; set; } = new();

    /// <summary>作为桌面壁纸时的显示位置与大小。</summary>
    public HomeworkWallpaperLayout Layout { get; set; } = new();
}
