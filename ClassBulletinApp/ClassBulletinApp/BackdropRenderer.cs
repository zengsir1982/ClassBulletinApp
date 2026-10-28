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
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 「桌面背面」渲染器：把「当天课表」「作业公示」「缺交作业名单」三张便签绘制成一张与屏幕等大的 PNG，用作桌面壁纸。
/// 每张便签的便签主题、位置 / 大小（九宫格锚点 + 像素偏移 + 宽度比例或固定像素）各自独立，可只显示其中任意几张。
/// 固定大小时内容按比例缩小以完整放进便签内（不溢出、不截断）；底图优先使用系统原壁纸（铺满裁切），读不到时回退为浅色渐变。
/// </summary>
public static class BackdropRenderer
{
    /// <summary>卡片内边距（以 1080 高度为基准比例缩放），供构建与内容自适应共用。</summary>
    private const double PAD_BASE = 36;

    /// <summary>
    /// 生成桌面背面壁纸。
    /// </summary>
    /// <param name="today">当天课表（可为 null，此时不画课表便签）。</param>
    /// <param name="title">课表便签标题（班级名 / 教师名）。</param>
    /// <param name="subtitle">课表便签副标题（日期 + 周次 + 星期）。</param>
    /// <param name="homework">作业公示内容（可为 null）。</param>
    /// <param name="missing">缺交作业名单（可为 null）。</param>
    /// <param name="b">桌面背面设置。</param>
    /// <param name="backgroundPath">系统原壁纸路径（可为空）。</param>
    public static string RenderToPng(TodayResult? today, string title, string dateText, string weekText,
        HomeworkBoard? homework, MissingBoard? missing, BackdropSettings b, int width, int height,
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
            var lay = b.ScheduleLayout ?? new HomeworkWallpaperLayout();
            var theme = HomeworkThemes.Find(b.ScheduleTheme);
            var card = BuildScheduleCard(today, title, dateText, weekText, scale, ScaleFont,
                ResolveCardWidth(lay, width, 0.38), theme);
            Place(canvas, card, lay, width, height, 0.38);
        }

        if (b.ShowHomework && homework != null)
        {
            var lay = b.HomeworkLayout ?? new HomeworkWallpaperLayout();
            var card = BuildHomeworkCard(homework, scale, ScaleFont);
            Place(canvas, card, lay, width, height);
        }

        if (b.ShowMissing && missing != null)
        {
            var card = BuildMissingCard(missing, scale, ScaleFont);
            Place(canvas, card, b.MissingLayout ?? new HomeworkWallpaperLayout(), width, height);
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

    // ---------- 尺寸 / 定位 ----------
    /// <summary>便签宽度：固定大小模式用固定像素；否则按屏幕宽度比例。</summary>
    internal static double ResolveCardWidth(HomeworkWallpaperLayout lay, double width, double defRatio)
    {
        if (lay.UseFixedSize && lay.FixedWidth > 0)
            return Math.Clamp(lay.FixedWidth, 160, Math.Max(160, width - 40));
        double ratio = lay.WidthRatio <= 0 ? defRatio : lay.WidthRatio;
        return width * Math.Clamp(ratio, 0.12, 0.95);
    }

    /// <summary>
    /// 给便签定位并加入画布。
    /// 固定高度时：用 Viewbox（只缩小不放大）把内容整体等比缩放到便签内，
    /// 保证内容再长也完整显示在便签范围内，不溢出、不截断。
    /// </summary>
    internal static void Place(Canvas canvas, Border card, HomeworkWallpaperLayout lay,
        double width, double height, double defRatio = 0.4)
    {
        card.Width = ResolveCardWidth(lay, width, defRatio);

        double padX = card.Padding.Left + card.Padding.Right;
        double innerW = Math.Max(80, card.Width - padX);

        if (lay.UseFixedSize && lay.FixedHeight > 0 && card.Child is FrameworkElement content)
        {
            card.Height = Math.Clamp(lay.FixedHeight, 120, Math.Max(120, height - 40));
            // 内容宽度固定为便签内宽：文字按此宽度折行，再整体缩放到便签高度内
            content.Width = innerW;
            card.Child = null;   // 先断开原父子关系，否则 content 无法再挂到 Viewbox 下
            card.Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = content
            };
        }

        // 高度自适应时先量出实际高度，才能按锚点（中 / 下）正确摆放
        double cardH = card.Height;
        if (double.IsNaN(cardH) || cardH <= 0)
        {
            card.Measure(new Size(card.Width, double.PositiveInfinity));
            cardH = card.DesiredSize.Height;
            card.Height = cardH;
        }

        AnchorOf(lay.Anchor, out string hAlign, out string vAlign);
        double scale = height / 1080.0;
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
            "Center" => (height - cardH) / 2 + oy,
            _ => height - baseM - cardH + oy
        };

        // 允许偏移把便签推到屏幕外，但保留至少 40 像素可见，避免"设了位置却看不见"
        x = Math.Clamp(x, -card.Width + 40, width - 40);
        y = Math.Clamp(y, -cardH + 40, height - 40);

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        canvas.Children.Add(card);
    }

    // ---------- 当天课表便签 ----------
    internal static Border BuildScheduleCard(TodayResult today, string title, string dateText, string weekText,
        double scale, Func<double, double> ScaleFont, double cardWidth, HomeworkTheme theme)
    {
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x23, 0x2A, 0x33));
        var accent = ParseHwColor(theme.Accent, Color.FromRgb(0x2D, 0x6C, 0xDF));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0x9C, 0xA3, 0xAF));
        var border = ParseHwColor(theme.BorderColor, Color.FromRgb(0xDC, 0xE3, 0xF1));

        var stack = new StackPanel();

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16 * scale) };
        string titleText = string.IsNullOrWhiteSpace(title) ? "当天课表" : title;

        // 标题：按便签实际宽度自适应字号（窄便签自动缩小，宽便签可更大），避免溢出或过小
        double pad = PAD_BASE * scale;
        double availW = Math.Max(80, cardWidth - 2 * pad);
        var titleFamily = new FontFamily("Microsoft YaHei");
        double titleSize = FitFontSize(titleText, availW, 42 * scale, 22 * scale, 56 * scale, titleFamily);

        head.Children.Add(new TextBlock
        {
            Text = titleText,
            FontSize = titleSize,
            FontFamily = titleFamily,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(titleColor),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 分隔线：标题与日期/周次之间留一道主题色横线，视觉分组更清爽
        head.Children.Add(new Border
        {
            Height = 2 * scale,
            Background = BrushOf(border),
            Margin = new Thickness(0, 12 * scale, 0, 12 * scale)
        });

        // 日期（单独一行）
        head.Children.Add(new TextBlock
        {
            Text = dateText ?? "",
            FontSize = ScaleFont(20),
            Foreground = BrushOf(fg, 0xD0),
            Margin = new Thickness(0, 0, 0, 6 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        // 第几周 · 星期几（单独一行，主题色 + 浅色胶囊底）
        var weekWrap = new StackPanel { Orientation = Orientation.Horizontal };
        var weekPill = new Border
        {
            CornerRadius = new CornerRadius(8 * scale),
            Background = BrushOf(accent, 0x2E),
            Padding = new Thickness(12 * scale, 4 * scale, 12 * scale, 4 * scale)
        };
        weekPill.Child = new TextBlock
        {
            Text = weekText ?? "",
            FontSize = ScaleFont(19),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent)
        };
        weekWrap.Children.Add(weekPill);
        head.Children.Add(weekWrap);

        stack.Children.Add(head);

        foreach (var row in today.Rows)
        {
            // 左列：节次 + 时间段；给定最小宽度并允许折行，避免窄便签 / 固定小尺寸时时间被裁掉
            var line = new Grid { Margin = new Thickness(0, 0, 0, 10 * scale) };
            line.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.35, GridUnitType.Star),
                MinWidth = 96 * scale
            });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.65, GridUnitType.Star) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.PeriodText) ? "" : row.PeriodText,
                FontSize = ScaleFont(19),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(row.TimeRange))
            {
                left.Children.Add(new TextBlock
                {
                    Text = row.TimeRange,
                    FontSize = ScaleFont(13),
                    Foreground = new SolidColorBrush(muted),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3 * scale, 0, 0)
                });
            }
            Grid.SetColumn(left, 0);
            line.Children.Add(left);

            // 右列：课程 + 教师 / 地点；左侧留出间距，节次与课名不再贴在一起
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16 * scale, 0, 0, 0) };
            var lesson = row.Lessons.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Subject));
            bool empty = lesson == null || string.IsNullOrWhiteSpace(lesson.Subject);

            right.Children.Add(new TextBlock
            {
                Text = empty ? "—" : lesson!.Subject,
                FontSize = ScaleFont(22),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(empty ? muted : fg),
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
                        Foreground = lesson.IsOverridden
                            ? BrushOf(ParseHwColor(theme.MissingColor, Color.FromRgb(0xE6, 0x7E, 0x22)))
                            : BrushOf(fg, 0xB8),
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
                Foreground = new SolidColorBrush(muted)
            });
        }

        return Card(stack, scale, theme);
    }

    // ---------- 作业公示便签 ----------

    /// <summary>解析文本颜色（#RRGGBB）；为空或非法时返回默认色。</summary>
    // ---------- 控制面板预览：把便签渲染成位图 ----------
    /// <summary>作业公示便签预览图（与桌面便签 / 悬浮便签同一套渲染，所见即所得）。</summary>
    internal static BitmapSource RenderHomeworkPreview(HomeworkBoard board, double width = 320, double maxHeight = 400)
    {
        double scale = Math.Max(0.35, width / 760.0);
        var card = BuildHomeworkCard(board, scale, v => Math.Max(11, v * scale));
        return RenderCardBitmap(card, width, maxHeight);
    }

    /// <summary>缺交作业名单便签预览图。</summary>
    internal static BitmapSource RenderMissingPreview(MissingBoard board, double width = 320, double maxHeight = 400)
    {
        double scale = Math.Max(0.35, width / 760.0);
        var card = BuildMissingCard(board, scale, v => Math.Max(11, v * scale));
        return RenderCardBitmap(card, width, maxHeight);
    }

    /// <summary>自定义便签预览图（与控制面板 / 悬浮便签同一套渲染）。</summary>
    internal static BitmapSource RenderCustomNotePreview(CustomNote note, double width = 320, double maxHeight = 400)
    {
        double scale = Math.Max(0.35, width / 760.0);
        var card = BuildCustomNoteCard(note, scale, v => Math.Max(11, v * scale));
        return RenderCardBitmap(card, width, maxHeight);
    }

    /// <summary>把一张便签卡片排版成指定宽度后渲染为位图（超出最大高度时裁切）。</summary>
    private static BitmapSource RenderCardBitmap(FrameworkElement card, double width, double maxHeight)
    {
        card.Width = width;
        card.Measure(new Size(width, double.PositiveInfinity));
        double h = Math.Min(Math.Max(40, card.DesiredSize.Height), maxHeight);
        card.Arrange(new Rect(0, 0, width, h));
        card.UpdateLayout();
        card.Clip = new RectangleGeometry(new Rect(0, 0, width, h));

        int pw = Math.Max(1, (int)Math.Ceiling(width));
        int ph = Math.Max(1, (int)Math.Ceiling(h));
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(card);
        rtb.Freeze();
        return rtb;
    }

    private static Color ParseHwColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex!.Trim().TrimStart('#');
        if (s.Length != 6) return fallback;
        try
        {
            return Color.FromRgb(
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { return fallback; }
    }

    internal static Border BuildHomeworkCard(HomeworkBoard board, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(board.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var accent = ParseHwColor(theme.Accent, Color.FromRgb(0x2D, 0x6C, 0xDF));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "各科作业公示" : board.Title,
            FontSize = ScaleFont(34),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(titleColor),
            Margin = new Thickness(0, 0, 0, 16 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        if (board.Items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（暂无作业）",
                FontSize = ScaleFont(20),
                Foreground = new SolidColorBrush(muted)
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
                    Foreground = new SolidColorBrush(accent),
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Content) ? "（无）" : it.Content,
                    FontSize = ScaleFont(HwTextStyle.SizeOf(it.SizeLevel)),
                    FontWeight = it.Bold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(ParseHwColor(it.Color, fg)),
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
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 14 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    // ---------- 缺交作业名单便签（独立） ----------
    internal static Border BuildMissingCard(MissingBoard board, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(board.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        var missingColor = ParseHwColor(theme.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "缺交作业名单" : board.Title,
            FontSize = ScaleFont(32),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(titleColor),
            Margin = new Thickness(0, 0, 0, 14 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        var items = (board.Items ?? new List<MissingHomeworkItem>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Students) || !string.IsNullOrWhiteSpace(m.Subject))
            .ToList();

        if (items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（无人缺交）",
                FontSize = ScaleFont(20),
                Foreground = new SolidColorBrush(muted)
            });
        }
        else
        {
            foreach (var m in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10 * scale) };
                string label = string.IsNullOrWhiteSpace(m.Subject) ? "缺交：" : "【" + m.Subject + "】缺交：";
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = ScaleFont(19),
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(missingColor),
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(m.Students))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = m.Students,
                        FontSize = ScaleFont(HwTextStyle.SizeOf(m.SizeLevel)),
                        FontWeight = m.Bold ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(ParseHwColor(m.Color, fg)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2 * scale, 0, 0)
                    });
                }
                stack.Children.Add(row);
            }
        }

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            FontSize = ScaleFont(14),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 12 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    // ---------- 自定义便签（用户自建，仅标题 + 自由正文） ----------
    internal static Border BuildCustomNoteCard(CustomNote note, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(note.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title) ? "自定义便签" : note.Title,
            FontSize = ScaleFont(32),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(titleColor),
            Margin = new Thickness(0, 0, 0, 14 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        var bodyColor = string.IsNullOrWhiteSpace(note.Color)
            ? fg
            : ParseHwColor(note.Color, Color.FromRgb(0x33, 0x3B, 0x47));
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Content) ? "（暂无内容）" : note.Content,
            FontSize = ScaleFont(18) + (HwTextStyle.SizeOf(note.SizeLevel) - HwTextStyle.SizeOf(0)) * scale,
            FontWeight = note.Bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(bodyColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + note.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            FontSize = ScaleFont(14),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 14 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    /// <summary>带透明度的纯色画刷（用于便签底色 / 半透明文字等）。</summary>
    private static SolidColorBrush BrushOf(Color c, byte alpha = 0xFF)
        => new(Color.FromArgb(alpha, c.R, c.G, c.B));

    /// <summary>便签外框：按便签主题配色（与悬浮便签观感一致）。</summary>
    private static Border Card(StackPanel content, double scale, HomeworkTheme? theme = null) => new()
    {
        CornerRadius = new CornerRadius(18 * scale),
        Background = theme == null
            ? BrushOf(Color.FromRgb(0xFF, 0xFF, 0xFF), 0xF6)
            : BrushOf(ParseHwColor(theme.Background, Color.FromRgb(0xFF, 0xFF, 0xFF)), 0xF7),
        BorderBrush = theme == null
            ? new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xEC))
            : BrushOf(ParseHwColor(theme.BorderColor, Color.FromRgb(0xC9, 0xD6, 0xEC))),
        BorderThickness = new Thickness(2 * scale),
        Padding = new Thickness(PAD_BASE * scale),
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
