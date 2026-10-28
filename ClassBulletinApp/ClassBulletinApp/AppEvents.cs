using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// 跨窗口的轻量事件总线：课表数据变化、布局设置变化时通知所有悬浮窗刷新。
/// </summary>
public static class AppEvents
{
    /// <summary>课表 / 班级 / 调课发生变化。</summary>
    public static event Action? ScheduleChanged;

    /// <summary>
    /// 布局或外观设置发生变化（位置 / 大小 / 透明度 / 置顶 / 穿透 / 靠边）。
    /// 参数为「内存中正在编辑、尚未落盘的布局」：传入对应布局对象可实现拖动滑块时实时预览；
    /// 传 null 时各悬浮窗回落到从磁盘重新读取（如点击「保存并应用」后）。
    /// </summary>
    public static event Action<PanelLayout?, PanelLayout?>? LayoutChanged;

    /// <summary>通知 / 公告数据发生变化。</summary>
    public static event Action? NotificationChanged;

    /// <summary>作业公示（各科作业）数据发生变化。</summary>
    public static event Action? HomeworkChanged;

    /// <summary>缺交作业名单（独立便签）数据发生变化。</summary>
    public static event Action? MissingChanged;

    /// <summary>
    /// 自定义便签（用户自建便签）发生变化。
    /// 参数为变化的便签 Id；传 null 表示「任意 / 全部」（如新增或删除便签时通知列表刷新）。
    /// </summary>
    public static event Action<string?>? CustomNoteChanged;

    /// <summary>「桌面背面」（当天课表 / 作业公示壁纸）开关或内容发生变化。</summary>
    public static event Action? BackdropChanged;

    /// <summary>提醒弹幕：把一段文案临时推送到通知条滚动显示（text, 显示秒数）。</summary>
    public static event Action<string, int>? ReminderDanmaku;

    public static void RaiseScheduleChanged() => ScheduleChanged?.Invoke();
    public static void RaiseLayoutChanged(PanelLayout? schedule = null, PanelLayout? notification = null)
        => LayoutChanged?.Invoke(schedule, notification);
    public static void RaiseNotificationChanged() => NotificationChanged?.Invoke();
    public static void RaiseHomeworkChanged() => HomeworkChanged?.Invoke();
    public static void RaiseMissingChanged() => MissingChanged?.Invoke();
    public static void RaiseCustomNoteChanged(string? id = null) => CustomNoteChanged?.Invoke(id);
    public static void RaiseBackdropChanged() => BackdropChanged?.Invoke();
    public static void RaiseReminderDanmaku(string text, int seconds) => ReminderDanmaku?.Invoke(text, seconds);
}
