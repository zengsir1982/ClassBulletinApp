using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScheduleCore.Models;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 「桌面背面」渲染器：把「当天课表」与「作业公示」绘制成一张与屏幕等大的 PNG，用作桌面壁纸。
/// 两块卡片的位置 / 宽度各自独立（九宫格锚点 + 像素偏移 + 宽度比例），可只显示其中一块。
/// 底图优先使用系统原壁纸（铺满裁切），读不到时回退为浅色渐变。
/// </summary>
public static class BackdropRenderer
{
    /// <summary>
    /// 生成桌面背面壁纸。
    /// </summary>
    /// <param name="today">当天课表（可为 null，此时不画课表卡片）。</param>
    /// <param name="title">课表卡标题（班级名 / 教师名）。</param>
    /// <param name="subtitle">课表卡副标题（日期 + 周次 + 星期）。</param>
    /// <param name="homework">作业公示内容（可为 null）。</param>
    /// <param name="b">桌面背面设置。</param>
    /// <param name="backgroundPath">系统原壁纸路径（可为空）。</param>
    public static string RenderToPng(TodayResult? today, string title, string dateText, string weekText,
        HomeworkBoard? homework, BackdropSettings b, int width, int height,
        string? backgroundPath, string outPath)
    {
        double scale = height / 1080.0;
        double ScaleFont(double v) => Math.Max(12, v * scale);

        var root = new Grid
        {
            Width = width,
            Height = height,
            Background = MakeBackground(width, height, backgroundPath, b.UseOriginWallpaper)
        };

        var canvas = new Canvas { Width = width, Height = height };
        root.Children.Add(canvas);

        if (b.ShowSchedule && today != null && today.Rows.Count > 0)
        {
            var sl = b.ScheduleLayout;
            double ratio = sl == null ? 0.38 : Math.Clamp(sl.WidthRatio <= 0 ? 0.38 : sl.WidthRatio, 0.18, 0.95);
            double cardW = width * ratio;
            var card = BuildScheduleCard(today, title, dateText, weekText, scale, ScaleFont, cardW);
            Place(canvas, card, b.ScheduleLayout, width, height, scale);
        }

        if (b.ShowHomework && homework != null)
        {
            var card = BuildHomeworkCard(homework, scale, ScaleFont);
            Place(canvas, card, b.HomeworkLayout ?? new HomeworkWallpaperLayout(), width, height, scale);
        }

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
        return outPath;
    }

    // ---------- 底图 ----------
    private static Brush MakeBackground(int width, int height, string? backgroundPath, bool useOrigin)
    {
        var fallback = new LinearGradientBrush(
            Color.FromRgb(0xEA, 0xF1, 0xFD), Color.FromRgb(0xD7, 0xE3, 0xFB), 120);

        if (!useOrigin || string.IsNullOrWhiteSpace(backgroundPath) || !File.Exists(backgroundPath))
            return fallback;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(backgroundPath!, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        }
        catch
        {
            return fallback;
        }
    }

    // ---------- 卡片定位 ----------
    private static void Place(Canvas canvas, FrameworkElement card, HomeworkWallpaperLayout lay,
        double width, double height, double scale)
    {
        double ratio = Math.Clamp(lay.WidthRatio <= 0 ? 0.4 : lay.WidthRatio, 0.18, 0.95);
        card.Width = width * ratio;
        card.Measure(new Size(card.Width, double.PositiveInfinity));
        card.Height = card.DesiredSize.Height;

        AnchorOf(lay.Anchor, out string hAlign, out string vAlign);
        double baseM = 60 * scale;
        double ox = lay.OffsetX * scale;
        double oy = lay.OffsetY * scale;

        double x = hAlign switch
        {
            "Left" => baseM + ox,
            "Center" => (width - card.Width) / 2 + ox,
            _ => width - baseM - card.Width + ox
        };
        double y = vAlign switch
        {
            "Top" => baseM + oy,
            "Center" => (height - card.Height) / 2 + oy,
            _ => height - baseM - card.Height + oy
        };

        // 允许偏移把卡片推到屏幕外，但保留至少 40 像素可见，避免"设了位置却看不见"
        x = Math.Clamp(x, -card.Width + 40, width - 40);
        y = Math.Clamp(y, -card.Height + 40, height - 40);

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        canvas.Children.Add(card);
    }

    // ---------- 课表卡片 ----------
    private static Border BuildScheduleCard(TodayResult today, string title, string dateText, string weekText,
        double scale, Func<double, double> ScaleFont, double cardWidth)
    {
        var stack = new StackPanel();

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16 * scale) };
        string titleText = string.IsNullOrWhiteSpace(title) ? "当天课表" : title;

        // 标题：按卡片实际宽度自适应字号（窄卡片自动缩小，宽卡片可更大），避免溢出或过小
        double pad = 36 * scale;
        double availW = Math.Max(80, cardWidth - 2 * pad);
        var titleFamily = new FontFamily("Microsoft YaHei");
        double titleSize = FitFontSize(titleText, availW, 42 * scale, 22 * scale, 56 * scale, titleFamily);

        head.Children.Add(new TextBlock
        {
            Text = titleText,
            FontSize = titleSize,
            FontFamily = titleFamily,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 分隔线：标题与日期/周次之间留一道浅色横线，视觉分组更清爽
        head.Children.Add(new Border
        {
            Height = 2 * scale,
            Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xE3, 0xF1)),
            Margin = new Thickness(0, 12 * scale, 0, 12 * scale)
        });

        // 日期（单独一行）
        head.Children.Add(new TextBlock
        {
            Text = dateText ?? "",
            FontSize = ScaleFont(20),
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x68)),
            Margin = new Thickness(0, 0, 0, 6 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        // 第几周 · 星期几（单独一行，主题色 + 浅色胶囊底，醒目美观）
        var weekWrap = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 0)
        };
        var weekPill = new Border
        {
            CornerRadius = new CornerRadius(8 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x2D, 0x6C, 0xDF)),
            Padding = new Thickness(12 * scale, 4 * scale, 12 * scale, 4 * scale)
        };
        weekPill.Child = new TextBlock
        {
            Text = weekText ?? "",
            FontSize = ScaleFont(19),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA8))
        };
        weekWrap.Children.Add(weekPill);
        head.Children.Add(weekWrap);

        stack.Children.Add(head);

        foreach (var row in today.Rows)
        {
            var line = new Grid { Margin = new Thickness(0, 0, 0, 8 * scale) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.85, GridUnitType.Star) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.PeriodText) ? "" : row.PeriodText,
                FontSize = ScaleFont(19),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF))
            });
            if (!string.IsNullOrWhiteSpace(row.TimeRange))
            {
                left.Children.Add(new TextBlock
                {
                    Text = row.TimeRange,
                    FontSize = ScaleFont(14),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))
                });
            }
            Grid.SetColumn(left, 0);
            line.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var lesson = row.Lessons.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Subject));
            bool empty = lesson == null || string.IsNullOrWhiteSpace(lesson.Subject);

            right.Children.Add(new TextBlock
            {
                Text = empty ? "—" : lesson!.Subject,
                FontSize = ScaleFont(22),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(empty
                    ? Color.FromRgb(0xC0, 0xC6, 0xCF)
                    : Color.FromRgb(0x23, 0x2A, 0x33)),
                TextWrapping = TextWrapping.Wrap
            });

            if (!empty)
            {
                string sub = (lesson!.Teacher ?? "");
                string loc = (lesson.LocationText ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(loc))
                    sub = (sub + "　" + loc).Trim();
                if (lesson.IsOverridden && !string.IsNullOrWhiteSpace(lesson.OverrideBadgeText))
                    sub = lesson.OverrideBadgeText + (string.IsNullOrWhiteSpace(sub) ? "" : "　" + sub);

                if (!string.IsNullOrWhiteSpace(sub))
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = sub,
                        FontSize = ScaleFont(15),
                        Foreground = new SolidColorBrush(lesson.IsOverridden
                            ? Color.FromRgb(0xE6, 0x7E, 0x22)
                            : Color.FromRgb(0x55, 0x60, 0x70)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2 * scale, 0, 0)
                    });
                }
            }

            Grid.SetColumn(right, 1);
            line.Children.Add(right);
            stack.Children.Add(line);
        }

        if (today.Rows.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "今日休息，无课程安排",
                FontSize = ScaleFont(20),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x7B, 0x3F))
            });
        }

        return Card(stack, scale);
    }

    // ---------- 作业卡片 ----------
    private static Border BuildHomeworkCard(HomeworkBoard board, double scale, Func<double, double> ScaleFont)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "各科作业公示" : board.Title,
            FontSize = ScaleFont(34),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37)),
            Margin = new Thickness(0, 0, 0, 16 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        if (board.Items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（暂无作业）",
                FontSize = ScaleFont(20),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))
            });
        }
        else
        {
            foreach (var it in board.Items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12 * scale) };
                row.Children.Add(new TextBlock
                {
                    Text = "【" + (it.Subject ?? "") + "】",
                    FontSize = ScaleFont(22),
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)),
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Content) ? "（无）" : it.Content,
                    FontSize = ScaleFont(19),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x3B, 0x47)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4 * scale, 0, 0)
                });
                stack.Children.Add(row);
            }
        }

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            FontSize = ScaleFont(14),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xBF)),
            Margin = new Thickness(0, 14 * scale, 0, 0)
        });

        return Card(stack, scale);
    }

    private static Border Card(StackPanel content, double scale) => new()
    {
        CornerRadius = new CornerRadius(18 * scale),
        Background = new SolidColorBrush(Color.FromArgb(0xF4, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xEC)),
        BorderThickness = new Thickness(2 * scale),
        Padding = new Thickness(36 * scale),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 24 * scale,
            Opacity = 0.25,
            ShadowDepth = 6 * scale
        },
        Child = content
    };

    /// <summary>
    /// 按可用宽度计算标题的合适字号：以 baseSize 测量文本宽度，按比例缩放到恰好放得下（留 4% 安全余量），
    /// 并限制在 [minSize, maxSize] 之间。用于「标题文字大小自适应容器」。
    /// 测量与渲染必须使用同一 FontFamily，否则实际宽度与测量不一致会导致折行 / 溢出。
    /// </summary>
    private static double FitFontSize(string text, double availWidth, double baseSize, double minSize, double maxSize, FontFamily fontFamily)
    {
        if (string.IsNullOrWhiteSpace(text) || availWidth <= 0) return baseSize;
        try
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                baseSize,
                Brushes.Black,
                1.0);
            double measured = ft.Width;
            if (measured <= 0) return baseSize;
            double size = baseSize * (availWidth * 0.96) / measured;
            return Math.Max(minSize, Math.Min(maxSize, size));
        }
        catch
        {
            return baseSize;
        }
    }

    /// <summary>九宫格锚点 → 水平/垂直对齐方式（兼容旧值 Right / Center 等）。</summary>
    private static void AnchorOf(string? anchor, out string hAlign, out string vAlign)
    {
        switch ((anchor ?? "").Trim())
        {
            case "TopLeft": hAlign = "Left"; vAlign = "Top"; break;
            case "TopCenter": hAlign = "Center"; vAlign = "Top"; break;
            case "TopRight": hAlign = "Right"; vAlign = "Top"; break;
            case "MiddleLeft": hAlign = "Left"; vAlign = "Center"; break;
            case "MiddleCenter": hAlign = "Center"; vAlign = "Center"; break;
            case "MiddleRight": hAlign = "Right"; vAlign = "Center"; break;
            case "BottomLeft": hAlign = "Left"; vAlign = "Bottom"; break;
            case "BottomCenter": hAlign = "Center"; vAlign = "Bottom"; break;
            case "BottomRight": hAlign = "Right"; vAlign = "Bottom"; break;
            // 旧值兼容
            case "Left": hAlign = "Left"; vAlign = "Center"; break;
            case "Top": hAlign = "Center"; vAlign = "Top"; break;
            case "Bottom": hAlign = "Center"; vAlign = "Bottom"; break;
            case "Center": hAlign = "Center"; vAlign = "Center"; break;
            default: hAlign = "Right"; vAlign = "Center"; break;   // "Right"
        }
    }
}
