namespace ScheduleCore.Models;

/// <summary>
/// 悬浮显示面板的布局与外观设置（课表窗 / 通知窗共用）。
/// Anchor：Left 左 / Right 右 / Top 上 / Bottom 下 / Center 居中。
/// </summary>
public class PanelLayout
{
    /// <summary>屏幕锚点位置。</summary>
    public string Anchor { get; set; } = "Right";

    /// <summary>窗口逻辑宽度（像素）。<=0 时由 FullWidth 或自动决定。</summary>
    public double Width { get; set; } = 400;

    /// <summary>窗口逻辑高度（像素）。</summary>
    public double Height { get; set; } = 720;

    /// <summary>整体不透明度 0.2~1.0。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>是否置顶显示。</summary>
    public bool TopMost { get; set; } = true;

    /// <summary>是否鼠标穿透（点击落到底层窗口，纯展示）。与 AutoHide 互斥。</summary>
    public bool ClickThrough { get; set; } = false;

    /// <summary>是否自动靠边隐藏（鼠标靠近屏幕边缘时滑出，离开后缩回）。与 ClickThrough 互斥。</summary>
    public bool AutoHide { get; set; } = false;

    /// <summary>上课时间自动隐藏：上课期间自动缩到屏幕边沿，只有把鼠标/触摸移到屏幕边沿才滑出（不影响上课用屏）。与 ClickThrough 互斥。</summary>
    public bool AutoHideDuringClass { get; set; } = false;

    /// <summary>是否横跨整个屏幕宽度（用于顶部/底部通知条）。</summary>
    public bool FullWidth { get; set; } = false;

    /// <summary>通知字幕字号。</summary>
    public double FontSize { get; set; } = 28;

    /// <summary>通知字幕滚动速度（像素/秒）。</summary>
    public double MarqueeSpeed { get; set; } = 120;
}
