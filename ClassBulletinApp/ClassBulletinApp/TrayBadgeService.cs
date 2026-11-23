using System;
using System.Drawing;
using System.Windows.Threading;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 任务栏托盘图标上的待办角标（r147）。
/// <para>WinForms <c>NotifyIcon</c> 没有原生角标，只能自己画位图换 Icon。</para>
/// <para>★ <b>生命周期铁律</b>：<see cref="Badged"/> 每次返回<b>全新</b> Icon，调用方先用它替换
/// <c>NotifyIcon.Icon</c>、<b>之后</b>再释放旧的自绘 Icon（系统图标 <see cref="Plain"/> 不能释放）。
/// 绝不在服务里缓存并自行 Dispose——闪动与刷新交织时，缓存实例可能正被托盘引用着。</para>
/// <para>★ 数字最多两位，32×32 下再大就看不清；真正的"显著"靠红点 + ToolTip 文字 + 有限次闪动。</para>
/// </summary>
public static class TrayBadgeService
{
    static readonly System.Drawing.Font BadgeFont =
        new("Microsoft YaHei UI", 11f, System.Drawing.FontStyle.Bold);

    /// <summary>无待办的原始图标（系统共享句柄，<b>不可</b> Dispose）。</summary>
    public static Icon Plain => SystemIcons.Application;

    /// <summary>是否系统共享图标（替换时据此决定能不能 Dispose）。</summary>
    public static bool IsSystemIcon(Icon? icon) => ReferenceEquals(icon, SystemIcons.Application);

    /// <summary>按待办数画一份带角标的图标。返回的新 Icon 由调用方负责替换后释放旧的。</summary>
    public static Icon Badged(int count)
    {
        var bmp = new Bitmap(32, 32);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var baseBmp = SystemIcons.Application.ToBitmap();
            g.DrawImage(baseBmp, new Rectangle(0, 0, 32, 32));

            if (count > 0)
            {
                string text = PendingTodoService.BadgeText(count);
                // 右下角红圆 + 白字：16px 托盘区里数字很小，但红色一眼能看见
                using var badgeBrush = new SolidBrush(Color.FromArgb(0xC0, 0x39, 0x2B));
                g.FillEllipse(badgeBrush, 17, 17, 15, 15);
                using var pen = new Pen(Color.White, 1.5f);
                g.DrawEllipse(pen, 17, 17, 15, 15);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(text, BadgeFont, Brushes.White,
                    new RectangleF(17, 17, 15, 15), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        finally { bmp.Dispose(); }
    }
}

/// <summary>
/// 托盘闪动：只在"待办刚出现"时闪几下就停，绝不做微信那种永久闪（教室里会逼疯人）。
/// </summary>
public static class TrayFlash
{
    static DispatcherTimer? _timer;
    static int _left;
    static Action<bool>? _apply;   // true = 带角标，false = 原始图标

    public static bool IsRunning => _timer != null && _timer.IsEnabled;

    /// <param name="apply">切图标的回调；由调用方管 Icon 的替换与释放。</param>
    /// <param name="times">闪几次（每次 = 一次暗 + 一次亮）。</param>
    public static void Start(Action<bool> apply, int times = 4)
    {
        Stop();
        _apply = apply;
        _left = Math.Max(1, times) * 2;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _timer.Tick += (_, _) =>
        {
            _left--;
            try { _apply?.Invoke(_left % 2 == 0); } catch { }
            if (_left <= 0) Stop();
        };
        _timer.Start();
    }

    public static void Stop()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer = null;
        _apply = null;
    }
}
