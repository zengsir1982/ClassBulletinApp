using System;
using System.Windows;
using System.Windows.Threading;

namespace ClassBulletinApp;

/// <summary>
/// 上下课提醒弹窗：置顶、不抢焦点、数秒后自动消失（复用单例）。
/// </summary>
public partial class ReminderPopup : Window
{
    private DispatcherTimer? _closeTimer;

    public ReminderPopup()
    {
        InitializeComponent();
        // 显示在主屏工作区顶部居中（首次定位）
        Loaded += (_, _) =>
        {
            var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            if (wa.HasValue)
            {
                Left = wa.Value.Left + Math.Max(0, (wa.Value.Width - ActualWidth) / 2);
                Top = wa.Value.Top + 72;
            }
        };
    }

    public void ShowReminder(string title, string detail, int seconds)
    {
        TxtTitle.Text = title;
        TxtDetail.Text = detail;
        Show();

        _closeTimer?.Stop();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(3, seconds)) };
        _closeTimer.Tick += (_, _) => { _closeTimer?.Stop(); Hide(); };
        _closeTimer.Start();
    }
}
