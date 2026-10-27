using System;
using System.Collections.Generic;

namespace ScheduleCore.Models;

/// <summary>单科作业条目。</summary>
public class HomeworkItem
{
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";
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
