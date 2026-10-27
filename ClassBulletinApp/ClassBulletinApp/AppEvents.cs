namespace ClassBulletinApp;

/// <summary>
/// 跨窗口的轻量事件总线：课表数据变化、布局设置变化时通知所有悬浮窗刷新。
/// </summary>
public static class AppEvents
{
    /// <summary>课表 / 班级 / 调课发生变化。</summary>
    public static event Action? ScheduleChanged;

    /// <summary>布局或外观设置发生变化（位置 / 大小 / 透明度 / 置顶 / 穿透 / 靠边）。</summary>
    public static event Action? LayoutChanged;

    /// <summary>通知 / 公告数据发生变化。</summary>
    public static event Action? NotificationChanged;

    /// <summary>「桌面背面」（当天课表 / 作业公示壁纸）开关或内容发生变化。</summary>
    public static event Action? BackdropChanged;

    public static void RaiseScheduleChanged() => ScheduleChanged?.Invoke();
    public static void RaiseLayoutChanged() => LayoutChanged?.Invoke();
    public static void RaiseNotificationChanged() => NotificationChanged?.Invoke();
    public static void RaiseBackdropChanged() => BackdropChanged?.Invoke();
}
