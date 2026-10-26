using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// 将作业公示栏内容渲染为 PNG 图片，用于设置为桌面壁纸（置于桌面背景显示，不影响操作）。
/// </summary>
public static class HomeworkRenderer
{
    public static string RenderToPng(HomeworkBoard board, int width, int height, string outPath)
    {
        double scale = height / 1080.0;
        double ScaleFont(double v) => Math.Max(12, v * scale);

        var root = new Grid
        {
            Width = width,
            Height = height,
            Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0xEA, 0xF1, 0xFD),
                System.Windows.Media.Color.FromRgb(0xD7, 0xE3, 0xFB), 120)
        };

        // 右侧作业卡片
        var card = new Border
        {
            CornerRadius = new CornerRadius(18 * scale),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xD6, 0xEC)),
            BorderThickness = new Thickness(2 * scale),
            Padding = new Thickness(40 * scale),
            Margin = new Thickness(0, 60 * scale, 60 * scale, 60 * scale),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = width * 0.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24 * scale,
                Opacity = 0.25,
                ShadowDepth = 6 * scale
            }
        };

        var stack = new StackPanel();
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "各科作业公示" : board.Title,
            FontSize = ScaleFont(34),
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x2A, 0x37)),
            Margin = new Thickness(0, 0, 0, 18 * scale)
        };
        stack.Children.Add(title);

        if (board.Items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "（暂无作业）",
                FontSize = ScaleFont(20),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF))
            };
            stack.Children.Add(empty);
        }
        else
        {
            foreach (var it in board.Items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14 * scale) };
                var subj = new TextBlock
                {
                    Text = "【" + (it.Subject ?? "") + "】",
                    FontSize = ScaleFont(22),
                    FontWeight = FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x6C, 0xDF)),
                    TextWrapping = TextWrapping.Wrap
                };
                var content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Content) ? "（无）" : it.Content,
                    FontSize = ScaleFont(19),
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x3B, 0x47)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4 * scale, 0, 0)
                };
                row.Children.Add(subj);
                row.Children.Add(content);
                stack.Children.Add(row);
            }
        }

        var foot = new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            FontSize = ScaleFont(14),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0xB6, 0xBF)),
            Margin = new Thickness(0, 18 * scale, 0, 0)
        };
        stack.Children.Add(foot);

        card.Child = stack;
        root.Children.Add(card);

        root.Measure(new System.Windows.Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fsStream = File.Create(outPath);
        enc.Save(fsStream);
        return outPath;
    }
}
