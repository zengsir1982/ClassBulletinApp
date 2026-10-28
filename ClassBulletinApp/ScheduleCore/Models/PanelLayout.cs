namespace ScheduleCore.Models;

/// <summary>
/// 悬浮显示面板的布局与外观设置（课表窗 / 通知窗共用）。
/// Anchor：Left 左 / Right 右 / Top 上 / Bottom 下 / Center 居中。
/// </summary>
public class PanelLayout
{
    /// <summary>屏幕锚点位置（仅在没有自由坐标时作为回退；同时决定自动隐藏往哪条屏幕边滑出）。</summary>
    public string Anchor { get; set; } = "Right";

    /// <summary>
    /// 自由摆放的左上角 X（逻辑像素，可直接由鼠标拖动窗口写入）。
    /// 为 null 表示还没有拖动过，此时按 Anchor 贴边定位。
    /// </summary>
    public double? Left { get; set; }

    /// <summary>自由摆放的左上角 Y（逻辑像素）。</summary>
    public double? Top { get; set; }

    /// <summary>窗口逻辑宽度（像素）。&gt;0 时即为窗口宽度；&lt;=0 时回退整屏宽（宽度滑块 = 唯一决定因素）。</summary>
    public double Width { get; set; } = 400;

    /// <summary>窗口逻辑高度（像素）。</summary>
    public double Height { get; set; } = 720;

    /// <summary>整体不透明度 0.2~1.0（用于“自动隐藏”/普通置顶显示的基础透明度）。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>“置顶显示”时的透明度（同时开启鼠标穿透，方便操作底层窗口）。建议 0.4~0.7。</summary>
    public double OverlayOpacity { get; set; } = 0.55;

    /// <summary>是否置顶显示。</summary>
    public bool TopMost { get; set; } = true;

    /// <summary>是否鼠标穿透（点击落到底层窗口，纯展示）。与 AutoHide 互斥。</summary>
    public bool ClickThrough { get; set; } = false;

    /// <summary>是否自动靠边隐藏（鼠标靠近屏幕边缘时滑出，离开后缩回）。与 ClickThrough 互斥。</summary>
    public bool AutoHide { get; set; } = false;

    /// <summary>上课时间自动隐藏：上课期间自动缩到屏幕边沿，只有把鼠标/触摸移到屏幕边沿才滑出（不影响上课用屏）。与 ClickThrough 互斥。</summary>
    public bool AutoHideDuringClass { get; set; } = false;

    /// <summary>
    /// 自动隐藏缩回后仍露在屏幕边沿的像素数。
    /// 0 = 完全移出屏幕（默认，最干净），大于 0 时保留一条可点击的细边。
    /// </summary>
    public double EdgePeek { get; set; }

    /// <summary>
    /// 屏幕边沿感应宽度（像素）：鼠标 / 触摸进入屏幕对应边沿这一范围内即滑出。
    /// 与 EdgePeek 相互独立，EdgePeek = 0 时也能被感应到。
    /// </summary>
    public double EdgeTrigger { get; set; } = 6;

    /// <summary>
    /// 历史字段（保留以兼容旧存档）。宽度现已完全由 Width 决定（Width&lt;=0 即整屏宽），
    /// 本字段不再参与宽度计算——否则旧存档里的 true 会强制整屏宽、让宽度滑块失效。
    /// </summary>
    public bool FullWidth { get; set; } = false;

    /// <summary>
    /// 顶部 / 底部通知条的水平位置（仅对 Top / Bottom 锚点有效）：
    /// Left 居左 / Center 居中（默认）/ Right 居右。宽度为 Width，Width&lt;=0 时回退整屏宽。
    /// </summary>
    public string HAnchor { get; set; } = "Center";

    /// <summary>通知字幕字号。</summary>
    public double FontSize { get; set; } = 28;

    /// <summary>通知字幕字体（留空 = 系统默认微软雅黑）。</summary>
    public string FontFamily { get; set; } = "";

    /// <summary>通知字幕文字颜色（#RRGGBB，留空 = 白色）。</summary>
    public string TextColor { get; set; } = "";

    /// <summary>通知条背景色（#RRGGBB，留空 = 跟随界面主题的深色）。</summary>
    public string BarColor { get; set; } = "";

    /// <summary>通知字幕滚动速度（像素/秒）。</summary>
    public double MarqueeSpeed { get; set; } = 120;

    /// <summary>是否已由用户拖动过（有自由坐标）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasFreePosition => Left.HasValue && Top.HasValue;
}
