using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// 课表变化「全屏提示」窗：整屏压暗 + 居中大字卡片，<c>seconds</c> 秒后自动关闭，点任意处也能关。
/// <para>与 <see cref="ReminderPopup"/>（顶部小窗）二选一，由「提醒设置 → 课表变化提醒」决定。</para>
/// <para>★ 用 <c>ShowActivated=False</c>：教室屏上弹提醒不该把焦点从正在用的软件里抢走。</para>
/// </summary>
public partial class FullScreenReminderWindow : Window, IGlassSurface
{
    private DispatcherTimer? _closeTimer;

    public FullScreenReminderWindow()
    {
        InitializeComponent();

        // 全屏：WPF 不允许「ShowActivated=false + WindowState=Maximized」同时出现（会抛异常关闭窗口），
        // 所以这里手动按主屏尺寸铺满，效果一样（含任务栏区域）。
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    
        GlassChrome.Prepare(this);}

    /// <summary>显示提示；重复调用会重置自动关闭的倒计时。</summary>
    /// <param name="style">外观来源（与顶部小窗共用同一套风格）；null = 经典白。</param>
    /// <param name="tag">左上角那枚小标签的文字；空 = 沿用「课表有变化」。</param>
    public void ShowReminder(string title, string detail, int seconds, ReminderSettings? style = null, string? tag = null)
    {
        ApplyStyle(style);

        TxtTag.Text = string.IsNullOrWhiteSpace(tag) ? "课表有变化" : tag!;
        TxtTitle.Text = title ?? "";
        TxtDetail.Text = detail ?? "";
        TxtDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        TxtHint.Text = $"{Math.Max(1, seconds)} 秒后自动关闭　·　点击任意位置立即关闭";

        if (!IsVisible) Show();
        Activate();   // 需要能收到点击；ShowActivated=False 时只有这里会短暂置前

        _closeTimer?.Stop();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, seconds)) };
        _closeTimer.Tick += (_, _) => { _closeTimer?.Stop(); Hide(); };
        _closeTimer.Start();
    }

    /// <summary>
    /// 套用「提醒设置 → 外观风格」：底色（含不透明度）、文字色、字号倍数。
    /// <para>⚠ 与顶部小窗必须走同一个 <see cref="ReminderVisualStyle"/>，否则会出现
    /// 「小窗调成黑底黄字、全屏还是白底黑字」这种两套皮的情况。</para>
    /// </summary>
    private void ApplyStyle(ReminderSettings? r)
    {
        try
        {
            var st = ReminderVisualStyle.From(r);

            Card.Background = st.CardBrush;
            Card.BorderBrush = st.BorderBrush;

            TxtTitle.Foreground = st.TextBrush();
            TxtDetail.Foreground = st.TextBrush(0.82);
            TxtHint.Foreground = st.TextBrush(0.55);

            TagBorder.Background = st.AccentFill;
            TagBorder.BorderBrush = st.AccentBorder;
            TxtTag.Foreground = st.TextBrush();

            double fs = st.FontScale;
            TxtTitle.FontSize = 54 * fs;
            TxtDetail.FontSize = 24 * fs;
            TxtTag.FontSize = Math.Clamp(22 * fs, 16, 34);
            ApplyGlass();
        }
        catch
        {
            // 配色出问题也要把提示弹出来，不能让提醒本身失效
        }
    }

    /// <summary>IGlassSurface：全屏遮罩 Root 不动，只让中间卡片变玻璃。</summary>
    public void ApplyGlass(ScheduleCore.Models.AppSettings? settings = null)
        => GlassBrush.Apply(Card, settings);

    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        _closeTimer?.Stop();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer?.Stop();
        base.OnClosed(e);
    }
}
