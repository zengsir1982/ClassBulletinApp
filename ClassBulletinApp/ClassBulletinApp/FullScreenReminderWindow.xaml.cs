using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClassBulletinApp;

/// <summary>
/// 课表变化「全屏提示」窗：整屏压暗 + 居中大字卡片，<c>seconds</c> 秒后自动关闭，点任意处也能关。
/// <para>与 <see cref="ReminderPopup"/>（顶部小窗）二选一，由「提醒设置 → 课表变化提醒」决定。</para>
/// <para>★ 用 <c>ShowActivated=False</c>：教室屏上弹提醒不该把焦点从正在用的软件里抢走。</para>
/// </summary>
public partial class FullScreenReminderWindow : Window
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
    }

    /// <summary>显示提示；重复调用会重置自动关闭的倒计时。</summary>
    public void ShowReminder(string title, string detail, int seconds)
    {
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
