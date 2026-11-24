using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp.Help;

/// <summary>
/// 点帮助里的截图 → 看大图。
///
/// <para>★ 为什么要这个窗：正文里的图必须限高（否则一张图吃掉一屏），
/// 但限高之后窗口类截图上的字会小到看不清。与其二选一，不如"首屏给小图，想看细节点开"。</para>
/// </summary>
public class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(BitmapSource source, string caption)
    {
        Title = string.IsNullOrWhiteSpace(caption) ? "查看大图" : caption;
        Width = 1080;
        Height = 760;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        var img = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(8)
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var root = new Grid { Background = Brushes.White };
        root.Children.Add(img);
        Content = root;

        // 双击 / Esc 关闭：双击是图片查看器的本能操作
        MouseLeftButtonUp += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        GlassChrome.Prepare(this);
    }
}
