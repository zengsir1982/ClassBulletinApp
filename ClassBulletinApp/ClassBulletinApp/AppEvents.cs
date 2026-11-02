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

    /// <summary>
    /// 「取消桌面背景」后请主窗口把当初隐藏掉的悬浮窗放回来。
    /// <para>参数为清单，元素形如 <c>Schedule</c> / <c>Homework</c> / <c>Missing</c> / <c>note:&lt;Id&gt;</c>。</para>
    /// <para>★ 用事件而不是直接操作窗口：取消背景的入口可能在布局窗口 / 托盘菜单里，
    /// 窗口实例归主窗口所有，直接持有会引入空引用与循环依赖。</para>
    /// </summary>
    public static event Action<string[]>? RestoreFloating;

    /// <summary>「取消桌面背景」完成（成功与否, 一句话结果）。</summary>
    public static event Action<bool, string>? BackdropOffDone;

    /// <summary>提醒弹幕：把一段文案临时推送到通知条滚动显示（text, 显示秒数）。</summary>
    public static event Action<string, int>? ReminderDanmaku;

    /// <summary>
    /// 悬浮课表底部「作业公示」快捷按钮被点击 —— 由主窗口负责打开（并保证置顶、落在屏幕内）。
    /// <para>用事件而不是直接调用 MainWindow：悬浮窗不一定由主窗口创建
    /// （诊断模式 <c>--shot schedulewin</c> 会单独 new 一个），直接持有引用会引入空引用与循环依赖。</para>
    /// </summary>
    public static event Action? QuickOpenHomework;

    /// <summary>悬浮课表底部「缺交」快捷按钮被点击。</summary>
    public static event Action? QuickOpenMissing;

    /// <summary>悬浮课表底部「便签」快捷按钮被点击（显示并置前自定义便签；一张都没有时新建一张）。</summary>
    public static event Action? QuickOpenCustomNote;

    /// <summary>悬浮课表底部「更新课表」快捷按钮被点击（检查并应用数据源更新）。</summary>
    public static event Action? QuickCheckUpdate;

    /// <summary>
    /// 「更新课表」执行完毕（成功与否, 一句话结果）。
    /// <para>悬浮窗据此把按钮从「⏳ 更新中…」恢复，并短暂显示结果 —— 手动更新必须给出明确反馈，
    /// 否则用户不知道是没更新到还是点了没反应。</para>
    /// </summary>
    public static event Action<bool, string>? QuickCheckUpdateDone;

    public static void RaiseScheduleChanged() => ScheduleChanged?.Invoke();
    public static void RaiseLayoutChanged(PanelLayout? schedule = null, PanelLayout? notification = null)
        => LayoutChanged?.Invoke(schedule, notification);
    public static void RaiseNotificationChanged() => NotificationChanged?.Invoke();
    public static void RaiseHomeworkChanged() => HomeworkChanged?.Invoke();
    public static void RaiseMissingChanged() => MissingChanged?.Invoke();
    public static void RaiseCustomNoteChanged(string? id = null) => CustomNoteChanged?.Invoke(id);
    public static void RaiseBackdropChanged() => BackdropChanged?.Invoke();
    public static void RaiseRestoreFloating(string[] tokens) => RestoreFloating?.Invoke(tokens);
    public static void RaiseBackdropOffDone(bool ok, string message) => BackdropOffDone?.Invoke(ok, message);
    public static void RaiseReminderDanmaku(string text, int seconds) => ReminderDanmaku?.Invoke(text, seconds);

    public static void RaiseQuickOpenHomework() => QuickOpenHomework?.Invoke();
    public static void RaiseQuickOpenMissing() => QuickOpenMissing?.Invoke();
    public static void RaiseQuickOpenCustomNote() => QuickOpenCustomNote?.Invoke();
    public static void RaiseQuickCheckUpdate() => QuickCheckUpdate?.Invoke();
    public static void RaiseQuickCheckUpdateDone(bool ok, string message)
        => QuickCheckUpdateDone?.Invoke(ok, message);
}
