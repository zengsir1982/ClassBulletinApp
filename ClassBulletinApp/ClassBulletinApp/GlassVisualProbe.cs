using System;
using System.IO;
using System.Threading;
using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// r73 玻璃真机自测：<c>--glass-probe &lt;输出png&gt;</c>。
///
/// <para>★ 为什么需要它：DWM 的亚克力 / 云母是<b>系统合成器</b>画的，WPF 的
/// <c>RenderTargetBitmap</c>（项目所有 <c>--shot</c> 都用它）只能抓应用自己的渲染结果，
/// 永远看不到玻璃。这里改用屏幕像素 <c>CopyFromScreen</c>，抓的是 DWM 合成之后的最终画面。</para>
///
/// <para>一体机验收时跑一下，把生成的 PNG 发回来即可判断玻璃到底有没有生效、
/// 有没有发黑、文字是否还清晰；日志里还带系统 Build 与实际采用的档位。</para>
/// </summary>
internal static class GlassVisualProbe
{
    internal static string Run(string png, string? mode = null)
    {
        // ★ 对照底图：先在同一个位置铺一张高对比度彩色窗口，玻璃窗口叠在它上面。
        //   纯色桌面看不出「有没有透出后方内容」，红/蓝/黑/白条纹一眼就能分辨：
        //   真亚克力 = 条纹被模糊成柔和色块；没生效 = 一片均匀灰白。
        var backdrop = new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Width = 560,
            Height = 340,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = 160,
            Top = 140
        };
        var stripes = new System.Windows.Controls.Grid();
        stripes.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        stripes.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        var left = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE7, 0x4C, 0x3C))
        };
        var right = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x29, 0x80, 0xB9))
        };
        System.Windows.Controls.Grid.SetColumn(right, 1);
        stripes.Children.Add(left);
        stripes.Children.Add(right);
        // 中间几道黑白横条：模糊后会变成灰带，实色盖住则完全消失
        var bars = new System.Windows.Controls.StackPanel
        {
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        for (int i = 0; i < 6; i++)
            bars.Children.Add(new System.Windows.Controls.Border
            {
                Height = 18,
                Margin = new System.Windows.Thickness(0, 6, 0, 6),
                Background = i % 2 == 0 ? System.Windows.Media.Brushes.Black
                                        : System.Windows.Media.Brushes.White
            });
        stripes.Children.Add(bars);
        backdrop.Content = stripes;
        backdrop.Show();
        backdrop.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render,
                                   new Action(() => { }));

        var w = new System.Windows.Window
        {
            Title = "班级公布栏 · 玻璃验证",
            Width = 560,
            Height = 340,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = 160,
            Top = 140,
            Background = System.Windows.Media.Brushes.White
        };

        // ★ bare：内容<b>完全不铺底色</b>，只有一行字。
        //   之前几轮对照实验里内容根 Grid 自己有一层 #F2F5FA 实色，本来就把窗口盖死了，
        //   于是"看不到后方条纹"可能只是被自己的实色挡住，而不是系统 backdrop 没生效。
        bool bare = string.Equals(mode, "bare", StringComparison.OrdinalIgnoreCase);

        var grid = new System.Windows.Controls.Grid();
        if (!bare)
        {
            grid.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xFA));
        }
        grid.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bare ? "裸内容 · 无底色" + Environment.NewLine + "应能直接看到后方红蓝条纹"
                        : "全局玻璃 · DWM 合成验证" + Environment.NewLine + "这行字必须清晰、底色应能透出桌面",
            FontSize = 24,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.Black,
            TextAlignment = System.Windows.TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        w.Content = grid;

        // r73d：chrome 模式走「分层窗 + 自绘标题栏」那条真玻璃路径。
        // ★ Prepare 必须在 Show 之前（WPF 不允许显示后再改 AllowsTransparency）。
        bool chrome = string.Equals(mode, "chrome", StringComparison.OrdinalIgnoreCase);
        if (chrome) GlassChrome.Prepare(w, force: true);

        w.Show();
        // 让 Loaded / 首帧渲染跑完，否则 IsLoaded 还是 false，Apply 会直接跳过
        w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));

        var s = new AppSettings
        {
            GlassEnabled = true,
            GlassAllWindows = true,
            GlassOpacity = 45,
            GlassBackdropMode = string.IsNullOrWhiteSpace(mode) ? GlassBackdrop.ModeAuto : mode!
        };
        bool ok = chrome ? GlassChrome.Wrap(w) : GlassBackdrop.Apply(w, s, force: true);
        string kind = chrome ? "GlassChrome" : GlassBackdrop.Resolve(GlassBackdrop.WinBuild, s.GlassBackdropMode).ToString();

        w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
        Thread.Sleep(350);   // DWM 合成在独立进程，给它一帧时间

        // WPF 的 Left/Top/Width 是 DIP，CopyFromScreen 要物理像素
        var source = System.Windows.PresentationSource.FromVisual(w);
        double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        var topLeft = w.PointToScreen(new System.Windows.Point(0, 0));
        int pw = Math.Max(1, (int)(w.Width * sx));
        int ph = Math.Max(1, (int)(w.Height * sy));

        using (var bmp = new System.Drawing.Bitmap(pw, ph))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(new System.Drawing.Point((int)topLeft.X, (int)topLeft.Y),
                                 System.Drawing.Point.Empty, new System.Drawing.Size(pw, ph));
            string? dir = Path.GetDirectoryName(Path.GetFullPath(png));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        string msg = "ok=" + ok
                   + " build=" + GlassBackdrop.WinBuild
                   + " kind=" + kind
                   + " dpi=" + sx.ToString("0.##")
                   + " px=" + pw + "x" + ph
                   + " chromeErr=" + GlassChrome.LastError
                   + " png=" + Path.GetFullPath(png)
                   + " size=" + new FileInfo(png).Length;
        try { w.Close(); } catch { }
        try { backdrop.Close(); } catch { }
        return msg;
    }
}
