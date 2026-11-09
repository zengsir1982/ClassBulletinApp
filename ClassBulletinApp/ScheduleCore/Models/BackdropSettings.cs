namespace ScheduleCore.Models;

/// <summary>
/// 「桌面背面」设置：把「当天课表」「作业公示」「缺交作业名单」以及各张「自定义便签」
/// 渲染成一张图片并设为桌面壁纸。
/// 各便签可同时显示，位置 / 大小 / 主题各自独立（九宫格锚点 + 像素偏移 + 宽度比例或固定像素尺寸）。
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

    /// <summary>壁纸中是否绘制「倒计时」便签。</summary>
    public bool ShowCountdown { get; set; }

    /// <summary>壁纸中是否绘制「值日生名单」便签。</summary>
    public bool ShowDuty { get; set; }

    /// <summary>壁纸中是否绘制「事项列表」便签（通知 / 公告的便签形态）。</summary>
    public bool ShowNotice { get; set; }

    /// <summary>事项列表便签在壁纸上的位置 / 大小（主题固定取自「清新蓝」）。</summary>
    public HomeworkWallpaperLayout NoticeLayout { get; set; } = new()
    {
        // 其余 5 个锚点已被课表 / 作业 / 缺交 / 倒计时 / 值日占满，事项便签默认落左上
        Anchor = "TopLeft",
        UseFixedSize = true,
        FixedWidth = 420,
        FixedHeight = 320
    };

    /// <summary>倒计时便签在壁纸上的位置 / 大小（主题取自 countdown.json 的 Theme）。</summary>
    public HomeworkWallpaperLayout CountdownLayout { get; set; } = new()
    {
        Anchor = "TopRight",
        UseFixedSize = true,
        FixedWidth = 360,
        FixedHeight = 240
    };

    /// <summary>值日生便签在壁纸上的位置 / 大小（主题取自 duty.json 的 Theme）。</summary>
    public HomeworkWallpaperLayout DutyLayout { get; set; } = new()
    {
        Anchor = "BottomLeft",
        UseFixedSize = true,
        FixedWidth = 400,
        FixedHeight = 340
    };

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

    /// <summary>
    /// 自定义便签在壁纸上的显示开关与排版，键 = 便签 Id（可同时显示多张，各自独立）。
    /// 没有键的便签视为「未显示在桌面背景」。
    /// </summary>
    public Dictionary<string, CustomNoteBackdrop> CustomNotes { get; set; } = new();

    /// <summary>取某张自定义便签的壁纸配置，没有就按默认值补一个（不落盘，由调用方决定何时保存）。</summary>
    public CustomNoteBackdrop EntryOf(string id)
    {
        CustomNotes ??= new Dictionary<string, CustomNoteBackdrop>();
        if (!CustomNotes.TryGetValue(id, out var entry) || entry == null)
        {
            entry = new CustomNoteBackdrop();
            CustomNotes[id] = entry;
        }
        return entry;
    }

    /// <summary>是否有任意一张自定义便签要画到壁纸上。</summary>
    public bool ShowAnyCustomNote()
    {
        if (CustomNotes == null) return false;
        foreach (var kv in CustomNotes)
            if (kv.Value != null && kv.Value.Visible) return true;
        return false;
    }

    /// <summary>
    /// 「设为背景」时被一并隐藏掉的悬浮窗清单，元素形如 <c>Schedule</c> / <c>Homework</c> /
    /// <c>Missing</c> / <c>note:&lt;便签Id&gt;</c>。
    /// <para>★ 取消背景时只把这里记着的窗口放回来：
    /// 用户本来就关掉、根本没上壁纸的便签，不该因为「取消背景」突然跳出来。</para>
    /// </summary>
    public List<string> PinnedRestores { get; set; } = new();

    /// <summary>记下一个待恢复的悬浮窗（重复记不会叠加）。</summary>
    public void AddPinnedRestore(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        PinnedRestores ??= new List<string>();
        if (!PinnedRestores.Contains(token, StringComparer.OrdinalIgnoreCase))
            PinnedRestores.Add(token);
    }
}

/// <summary>单张自定义便签的壁纸设置：是否显示 + 位置 / 大小。</summary>
public class CustomNoteBackdrop
{
    /// <summary>是否在桌面背景上显示这张便签。</summary>
    public bool Visible { get; set; }

    /// <summary>在壁纸上的位置与大小（锚点 + 偏移 + 宽度比例 / 固定像素）。</summary>
    public HomeworkWallpaperLayout Layout { get; set; } = new()
    {
        Anchor = "TopLeft",
        UseFixedSize = true,
        FixedWidth = 420,
        FixedHeight = 360
    };
}
