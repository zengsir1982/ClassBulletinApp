namespace ScheduleCore.Models;

/// <summary>
/// 一张便签悬浮窗的位置与状态（存在 <see cref="AppSettings.NoteWindows"/> 里，键 = countdown / duty）。
/// <para>倒计时与值日生共用这一套，将来再加便签只需加一个键，不必往 AppSettings 里堆一堆字段。</para>
/// </summary>
public class NoteWindowPlacement
{
    /// <summary>窗口左上角 X（DIP）；null = 首次打开自动定位。</summary>
    public double? Left { get; set; }

    /// <summary>窗口左上角 Y（DIP）。</summary>
    public double? Top { get; set; }

    /// <summary>窗口宽度（DIP）；0 / null = 用默认宽度。</summary>
    public double? Width { get; set; }

    /// <summary>窗口高度（DIP）；0 / null = 用默认高度。</summary>
    public double? Height { get; set; }

    /// <summary>是否置顶显示。</summary>
    public bool TopMost { get; set; }

    /// <summary>是否锁定（锁定后不能移动 / 缩放 / 编辑）。</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 是否在屏幕上显示（控制面板 / 快捷按钮可切换）。
    /// <para>★ 默认 false：升级到新版本时不主动往桌面上多弹两张空白便签，
    /// 用户点过「倒计时 / 值日」以后才记成 true，之后每次启动自动恢复。</para>
    /// </summary>
    public bool Visible { get; set; }
}
