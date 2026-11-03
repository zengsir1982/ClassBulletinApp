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
/// 「桌面背面」渲染器：把「当天课表」「作业公示」「缺交作业名单」「自定义便签」绘制成一张与屏幕等大的 PNG，用作桌面壁纸。
/// 每张便签的便签主题、位置 / 大小（九宫格锚点 + 像素偏移 + 宽度比例或固定像素）各自独立，可只显示其中任意几张。
/// <para>★ 字号口径：所有字号 / 内边距都直接取悬浮便签、悬浮课表里的 <b>DIP 数值</b>，再乘以主屏 DPI 缩放得到物理像素。
/// 这样壁纸上的字与屏幕上的悬浮窗<b>一样大</b>（壁纸按物理分辨率渲染，悬浮窗按 DIP 渲染后由系统放大）。
/// 早期版本用「屏高/1080」当比例尺，在 125% / 150% 缩放的机器上会与悬浮窗差 20%~50%，故改为此口径。</para>
/// <para>固定大小时内容按比例缩小以完整放进便签内（不溢出、不截断）；底图优先使用系统原壁纸（铺满裁切），读不到时回退为浅色渐变。</para>
/// </summary>
public static class BackdropRenderer
{
    /// <summary>卡片内边距（DIP）：与悬浮便签「外框 1 + 内容边距 12」对齐，保证折行位置一致。</summary>
    private const double PAD_DIP = 13;

    /// <summary>卡片圆角（DIP）：与悬浮便签 CornerRadius=12 一致。</summary>
    private const double RADIUS_DIP = 12;

    /// <summary>
    /// 生成桌面背面壁纸。
    /// </summary>
    /// <param name="today">当天课表（可为 null，此时不画课表便签）。</param>
    /// <param name="title">课表便签标题（班级名 / 教师名）。</param>
    /// <param name="subtitle">课表便签副标题（日期 + 周次 + 星期）。</param>
    /// <param name="homework">作业公示内容（可为 null）。</param>
    /// <param name="missing">缺交作业名单（可为 null）。</param>
    /// <param name="customNotes">要画到壁纸上的自定义便签（调用方已按各自开关筛选；可为 null）。</param>
    /// <param name="b">桌面背面设置。</param>
    /// <param name="dpi">主屏 DPI 缩放（物理像素 / 逻辑像素），用于把 DIP 字号换算成壁纸上的像素。</param>
    /// <param name="backgroundPath">系统原壁纸路径（可为空）。</param>
    public static string RenderToPng(TodayResult? today, string title, string dateText, string weekText,
        HomeworkBoard? homework, MissingBoard? missing, IReadOnlyList<CustomNote>? customNotes,
        BackdropSettings b, int width, int height, double dpi,
        string? backgroundPath, string outPath,
        CountdownBoard? countdown = null, DutyBoard? duty = null)
    {
        // typo = 每个 DIP 对应多少像素；所有字号 / 间距都按它换算，与悬浮窗保持同一视觉大小
        double typo = dpi > 0.1 ? dpi : 1.0;
        double ScaleFont(double v) => Math.Max(8, v * typo);

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
            var card = BuildScheduleCard(today, title, dateText, weekText, typo, ScaleFont,
                ResolveCardWidth(lay, width, 0.38), theme);
            Place(canvas, card, lay, width, height, 0.38);
        }

        if (b.ShowHomework && homework != null)
        {
            var lay = b.HomeworkLayout ?? new HomeworkWallpaperLayout();
            var card = BuildHomeworkCard(homework, typo, ScaleFont);
            Place(canvas, card, lay, width, height);
        }

        if (b.ShowMissing && missing != null)
        {
            var card = BuildMissingCard(missing, typo, ScaleFont);
            Place(canvas, card, b.MissingLayout ?? new HomeworkWallpaperLayout(), width, height);
        }

        if (b.ShowCountdown && countdown != null)
        {
            var lay = b.CountdownLayout ?? new HomeworkWallpaperLayout();
            // 卡片宽度跟着内容收窄，名字与天数挨在一起像一块整体（用户在排版里拖过大小则按他的）
            var card = BuildCountdownCard(countdown, typo, ScaleFont, ResolveCardWidth(lay, width, 0.4));
            Place(canvas, card, lay, width, height, fitContent: lay.FitContent ?? false);
        }

        if (b.ShowDuty && duty != null)
        {
            var card = BuildDutyCard(duty, typo, ScaleFont);
            Place(canvas, card, b.DutyLayout ?? new HomeworkWallpaperLayout(), width, height);
        }

        // 自定义便签：每张各自的位置 / 大小，各自的主题（取自便签自身）
        if (customNotes != null)
        {
            foreach (var note in customNotes)
            {
                if (note == null || string.IsNullOrEmpty(note.Id)) continue;
                var entry = b.EntryOf(note.Id);
                if (!entry.Visible) continue;
                var card = BuildCustomNoteCard(note, typo, ScaleFont);
                Place(canvas, card, entry.Layout ?? new HomeworkWallpaperLayout(), width, height);
            }
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
    /// <param name="fitContent">
    /// true = 卡片宽度跟着内容走（<see cref="HomeworkWallpaperLayout.FixedWidth"/> 只当上限），
    /// 高度也按内容自适应。用于倒计时卡：名字与天数紧挨着排，卡才不会拖着半屏空白。
    /// </param>
    internal static void Place(Canvas canvas, Border card, HomeworkWallpaperLayout lay,
        double width, double height, double defRatio = 0.4, bool fitContent = false)
    {
        double targetW = ResolveCardWidth(lay, width, defRatio);
        if (fitContent)
        {
            card.MaxWidth = targetW;
            card.Width = double.NaN;      // 不设宽 = 由内容决定
        }
        else
        {
            card.Width = targetW;
        }

        double padX = card.Padding.Left + card.Padding.Right;
        double innerW = Math.Max(80, targetW - padX);

        // 固定尺寸的「等比缩放到框内」只用于非自适应卡片；自适应卡片高度也随内容
        if (!fitContent && lay.UseFixedSize && lay.FixedHeight > 0 && card.Child is FrameworkElement content)
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
            card.Measure(new Size(double.IsNaN(card.Width) ? targetW : card.Width, double.PositiveInfinity));
            cardH = card.DesiredSize.Height;
            card.Height = cardH;
        }
        double cardW = double.IsNaN(card.Width) ? card.DesiredSize.Width : card.Width;

        AnchorOf(lay.Anchor, out string hAlign, out string vAlign);
        double scale = height / 1080.0;
        double baseM = 60 * scale;
        double ox = lay.OffsetX * scale;
        double oy = lay.OffsetY * scale;

        double x = hAlign switch
        {
            "Left" => baseM + ox,
            "Center" => (width - cardW) / 2 + ox,
            _ => width - baseM - cardW + ox
        };
        double y = vAlign switch
        {
            "Top" => baseM + oy,
            "Center" => (height - cardH) / 2 + oy,
            _ => height - baseM - cardH + oy
        };

        // 允许偏移把便签推到屏幕外，但保留至少 40 像素可见，避免"设了位置却看不见"
        x = Math.Clamp(x, -cardW + 40, width - 40);
        y = Math.Clamp(y, -cardH + 40, height - 40);

        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        canvas.Children.Add(card);
    }

    // ---------- 当天课表便签 ----------
    /// <summary>
    /// 课表便签。<b>字号与行距全部对齐悬浮课表</b>（标题 23 / 日期 14 / 周次 14 / 节次 18 / 时间 11 / 课名 19 / 教师 13，
    /// 课程行内边距 10、圆角 10、行间距 8，左列固定 74 DIP），保证「设为背景」后与屏幕上的悬浮课表看起来是同一张。
    /// </summary>
    /// <param name="scale">每个 DIP 对应的像素数（主屏 DPI 缩放）。</param>
    internal static Border BuildScheduleCard(TodayResult today, string title, string dateText, string weekText,
        double scale, Func<double, double> ScaleFont, double cardWidth, HomeworkTheme theme)
    {
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x23, 0x2A, 0x33));
        var accent = ParseHwColor(theme.Accent, Color.FromRgb(0x2D, 0x6C, 0xDF));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0x9C, 0xA3, 0xAF));
        var border = ParseHwColor(theme.BorderColor, Color.FromRgb(0xDC, 0xE3, 0xF1));

        var stack = new StackPanel();

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10 * scale) };
        string titleText = string.IsNullOrWhiteSpace(title) ? "当天课表" : title;

        // 标题：基准 23（与悬浮课表标题一致），仅在便签过窄时按宽度自适应缩小，避免溢出
        double pad = PAD_DIP * scale;
        double availW = Math.Max(80, cardWidth - 2 * pad);
        var titleFamily = new FontFamily("Microsoft YaHei");
        double titleSize = FitFontSize(titleText, availW, 23 * scale, 14 * scale, 23 * scale, titleFamily);

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
            Height = 1 * scale,
            Background = BrushOf(border),
            Margin = new Thickness(0, 8 * scale, 0, 8 * scale)
        });

        // 日期（单独一行）
        head.Children.Add(new TextBlock
        {
            Text = dateText ?? "",
            FontSize = ScaleFont(14),
            Foreground = BrushOf(fg, 0xD0),
            Margin = new Thickness(0, 0, 0, 6 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        // 第几周 · 星期几（单独一行，主题色 + 浅色胶囊底）
        var weekWrap = new StackPanel { Orientation = Orientation.Horizontal };
        var weekPill = new Border
        {
            CornerRadius = new CornerRadius(6 * scale),
            Background = BrushOf(accent, 0x2E),
            Padding = new Thickness(8 * scale, 2 * scale, 8 * scale, 2 * scale)
        };
        weekPill.Child = new TextBlock
        {
            Text = weekText ?? "",
            FontSize = ScaleFont(14),
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent)
        };
        weekWrap.Children.Add(weekPill);
        head.Children.Add(weekWrap);

        stack.Children.Add(head);

        foreach (var row in today.Rows)
        {
            // 固定时段（大课间/眼保健操/午休）不占壁纸空间：壁纸卡片高度紧张，优先保留 8 节课
            if (row.IsFixed) continue;

            // 课程行：白描边圆角卡片，与悬浮课表的课程行同结构（左列节次+时间，右列课名+教师）
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74 * scale) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.PeriodText) ? "" : row.PeriodText,
                FontSize = ScaleFont(18),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(row.TimeRange))
            {
                left.Children.Add(new TextBlock
                {
                    Text = row.TimeRange,
                    FontSize = ScaleFont(11),
                    Foreground = new SolidColorBrush(muted),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3 * scale, 0, 0)
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
                FontSize = ScaleFont(19),
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
                        FontSize = ScaleFont(13),
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

            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10 * scale),
                BorderThickness = new Thickness(1 * scale),
                BorderBrush = BrushOf(border, 0x66),
                Padding = new Thickness(10 * scale),
                Margin = new Thickness(0, 0, 0, 8 * scale),
                Child = line
            });
        }

        if (today.Rows.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "今日休息，无课程安排",
                FontSize = ScaleFont(14),
                Foreground = new SolidColorBrush(muted)
            });
        }

        return Card(stack, scale, theme);
    }

    // ---------- 作业公示便签 ----------

    /// <summary>解析文本颜色（#RRGGBB）；为空或非法时返回默认色。</summary>
    // ---------- 控制面板预览：把便签渲染成位图 ----------
    /// <summary>
    /// 控制面板里的缩略图。字号按「默认便签宽度 380 DIP」等比缩小，
    /// 因此缩略图与屏幕上 380 宽的便签观感一致（尺寸不同、比例相同）。
    /// </summary>
    private static double PreviewScale(double width) => Math.Max(0.45, width / 380.0);

    /// <summary>作业公示便签预览图（与桌面便签 / 悬浮便签同一套渲染，所见即所得）。</summary>
    internal static BitmapSource RenderHomeworkPreview(HomeworkBoard board, double width = 320, double maxHeight = 400)
    {
        double scale = PreviewScale(width);
        var card = BuildHomeworkCard(board, scale, v => Math.Max(9, v * scale));
        return RenderCardBitmap(card, width, maxHeight);
    }

    /// <summary>缺交作业名单便签预览图。</summary>
    internal static BitmapSource RenderMissingPreview(MissingBoard board, double width = 320, double maxHeight = 400)
    {
        double scale = PreviewScale(width);
        var card = BuildMissingCard(board, scale, v => Math.Max(9, v * scale));
        return RenderCardBitmap(card, width, maxHeight);
    }

    /// <summary>自定义便签预览图（与控制面板 / 悬浮便签同一套渲染）。</summary>
    internal static BitmapSource RenderCustomNotePreview(CustomNote note, double width = 320, double maxHeight = 400)
    {
        double scale = PreviewScale(width);
        var card = BuildCustomNoteCard(note, scale, v => Math.Max(9, v * scale));
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

    /// <summary>
    /// 作业公示便签。<b>字号与间距对齐悬浮便签</b>（标题 17 / 科目 15 / 正文 档位-4 / 更新时间 11，行间距 10）。
    /// </summary>
    internal static Border BuildHomeworkCard(HomeworkBoard board, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(board.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var accent = ParseHwColor(theme.Accent, Color.FromRgb(0x2D, 0x6C, 0xDF));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();
        var titleStyle = board.TitleStyle;
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "各科作业公示" : board.Title,
            FontSize = ScaleFont(HwTextRender.TitleSize(titleStyle)),
            FontWeight = HwTextRender.BoldOr(titleStyle, true) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(titleStyle, theme.TitleColor), titleColor)),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        if (board.Items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（暂无作业）",
                FontSize = ScaleFont(14),
                Foreground = new SolidColorBrush(muted)
            });
        }
        else
        {
            foreach (var it in board.Items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10 * scale) };
                if (!string.IsNullOrWhiteSpace(it.Subject))
                {
                    var ss = it.SubjectStyle;
                    row.Children.Add(new TextBlock
                    {
                        Text = "【" + (it.Subject ?? "") + "】",
                        FontSize = ScaleFont(HwTextRender.BodySize(ss)),
                        FontWeight = HwTextRender.BoldOr(ss, true) ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(ss, theme.Accent), accent)),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Content) ? "（无）" : it.Content,
                    FontSize = ScaleFont(Math.Max(10, HwTextStyle.SizeOf(it.SizeLevel) - 4)),
                    FontWeight = it.Bold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(ParseHwColor(it.Color, fg)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2 * scale, 0, 0)
                });
                stack.Children.Add(row);
            }
        }

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = ScaleFont(11),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 8 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    // ---------- 缺交作业名单便签（独立） ----------
    /// <summary>
    /// 缺交作业名单便签。<b>字号与间距对齐悬浮便签</b>（标题 17 / 科目标签 14 / 名单 档位-4 / 更新时间 11）。
    /// </summary>
    internal static Border BuildMissingCard(MissingBoard board, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(board.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        var missingColor = ParseHwColor(theme.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();
        var titleStyle = board.TitleStyle;
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "缺交作业名单" : board.Title,
            FontSize = ScaleFont(HwTextRender.TitleSize(titleStyle)),
            FontWeight = HwTextRender.BoldOr(titleStyle, true) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(titleStyle, theme.TitleColor), titleColor)),
            Margin = new Thickness(0, 0, 0, 10 * scale),
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
                FontSize = ScaleFont(14),
                Foreground = new SolidColorBrush(muted)
            });
        }
        else
        {
            foreach (var m in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10 * scale) };
                string label = string.IsNullOrWhiteSpace(m.Subject) ? "缺交：" : "【" + m.Subject + "】缺交：";
                var ss = m.SubjectStyle;
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = ScaleFont(HwTextRender.MissingSubjectSize(ss)),
                    FontWeight = HwTextRender.BoldOr(ss, true) ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(ss, theme.MissingColor), missingColor)),
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(m.Students))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = m.Students,
                        FontSize = ScaleFont(Math.Max(10, HwTextStyle.SizeOf(m.SizeLevel) - 4)),
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
            Text = "更新于 " + board.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = ScaleFont(11),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 8 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    // ---------- 自定义便签（用户自建，仅标题 + 自由正文） ----------
    /// <summary>
    /// 自定义便签。<b>字号与间距对齐悬浮便签</b>（标题 17 / 正文 档位-4 / 更新时间 11）。
    /// </summary>
    internal static Border BuildCustomNoteCard(CustomNote note, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(note.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var titleColor = ParseHwColor(theme.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();
        var titleStyle = note.TitleStyle;
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title) ? "自定义便签" : note.Title,
            FontSize = ScaleFont(HwTextRender.TitleSize(titleStyle)),
            FontWeight = HwTextRender.BoldOr(titleStyle, true) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(titleStyle, theme.TitleColor), titleColor)),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        var bodyColor = string.IsNullOrWhiteSpace(note.Color)
            ? fg
            : ParseHwColor(note.Color, Color.FromRgb(0x33, 0x3B, 0x47));
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Content) ? "（暂无内容）" : note.Content,
            FontSize = ScaleFont(Math.Max(10, HwTextStyle.SizeOf(note.SizeLevel) - 4)),
            FontWeight = note.Bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(bodyColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + note.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = ScaleFont(11),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 8 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    // ---------- 倒计时便签 ----------
    /// <summary>
    /// 倒计时便签。<b>字号与间距对齐悬浮便签</b>（标题 = 标题样式 / 事件名 = 内容样式 / 天数比事件名大 4）。
    /// 右侧天数用强调色，已过去的用灰色，今天用醒目色（由「高亮今天」开关控制）。
    /// </summary>
    internal static Border BuildCountdownCard(CountdownBoard board, double scale, Func<double, double> ScaleFont, double cardWidth = 0)
    {
        var theme = HomeworkThemes.Find(board.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var accent = ParseHwColor(theme.Accent, Color.FromRgb(0xA1, 0x74, 0x00));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));
        var hot = ParseHwColor(theme.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B));

        var stack = new StackPanel();

        var ts = board.TitleStyle;
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "倒计时" : board.Title,
            FontSize = ScaleFont(HwTextRender.TitleSize(ts)),
            FontWeight = HwTextRender.BoldOr(ts, true) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(ts, theme.TitleColor),
                                            ParseHwColor(theme.TitleColor, Color.FromRgb(0x8D, 0x6E, 0x00)))),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        var cs = board.ContentStyle;
        double bodySize = HwTextRender.BodySize(cs);
        var bodyBrush = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(cs, theme.Foreground), fg));
        var bodyWeight = HwTextRender.BoldOr(cs, false) ? FontWeights.Bold : FontWeights.Normal;

        // 天数单独一套样式（默认比条目大 4 号、加粗、主题强调色 —— 与加设置项之前一致）
        var ds = board.DaysStyle;
        double daysSize = HwTextRender.SizeOf(ds, 0);
        double unitSize = Math.Max(9, daysSize - 7);
        var daysWeight = HwTextRender.BoldOr(ds, true) ? FontWeights.Bold : FontWeights.Normal;
        var daysColor = ParseHwColor(HwTextRender.ColorOr(ds, theme.Accent), accent);
        var daysUnitColor = ParseHwColor(HwTextRender.ColorOr(ds, theme.MutedColor), muted);

        var items = board.SortedItems();

        // 卡片内宽（= 用户设定的宽度上限）：事件名 / 备注超长时截断，不让卡片撑到屏宽
        double innerW = cardWidth > 0 ? Math.Max(80, cardWidth - PAD_DIP * 2 * scale) : double.PositiveInfinity;

        // 天数区占位按本卡最长的一条估：名字的 MaxWidth 要给它让位，否则「名字 + 天数」会溢出
        double rightW = 0;
        foreach (var it in items)
        {
            if (it.Target() == null) { rightW = Math.Max(rightW, unitSize * 5); continue; }
            int d0 = it.DaysLeft();
            rightW = Math.Max(rightW, d0 == 0
                ? daysSize * 4
                : d0 < 0
                    ? unitSize * (4 + (-d0).ToString().Length)
                    : daysSize * 0.62 * d0.ToString().Length + unitSize * 1.2);
        }
        double nameMaxWidth = innerW > 0
            ? Math.Max(40, innerW - (rightW + 10) * scale)
            : double.PositiveInfinity;

        if (items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（暂无倒计时，双击添加）",
                FontSize = ScaleFont(14),
                Foreground = new SolidColorBrush(muted)
            });
        }
        else
        {
            foreach (var it in items)
            {
                bool hasNote = !string.IsNullOrWhiteSpace(it.Note);

                // 一条 = 一个竖排小整体：第 0 行「事件名 + 天数」用水平 StackPanel 紧挨着排
                // （Grid 的跨列备注会反过来撑宽列，把名字和天数拉开一道缝）；第 1 行是备注
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, (hasNote ? 10 : 7) * scale) };
                var line = new StackPanel { Orientation = Orientation.Horizontal };

                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Name) ? "（未命名）" : it.Name,
                    FontSize = ScaleFont(bodySize),
                    FontWeight = bodyWeight,
                    Foreground = bodyBrush,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = nameMaxWidth,
                    VerticalAlignment = VerticalAlignment.Center
                };
                line.Children.Add(name);

                // 天数：还有 30 天 / 就是今天 / 已过 3 天；紧跟事件名
                var right = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8 * scale, 0, 0, 0)
                };
                bool hasDate = it.Target() != null;
                int days = it.DaysLeft();
                bool isToday = hasDate && days == 0 && board.HighlightToday;

                if (!hasDate)
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = "未设日期", FontSize = ScaleFont(unitSize),
                        Foreground = new SolidColorBrush(daysUnitColor)
                    });
                }
                else if (days < 0)
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = $"已过{-days}天", FontSize = ScaleFont(unitSize),
                        Foreground = new SolidColorBrush(daysUnitColor)
                    });
                }
                else if (days == 0)
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = "就是今天", FontSize = ScaleFont(daysSize),
                        FontWeight = daysWeight,
                        Foreground = new SolidColorBrush(isToday ? hot : daysColor)
                    });
                }
                else
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = days.ToString(),
                        FontSize = ScaleFont(daysSize), FontWeight = daysWeight,
                        Foreground = new SolidColorBrush(daysColor)
                    });
                    // 与悬浮便签同一口径：「天」前不写空格，只留 1px 呼吸位
                    right.Children.Add(new TextBlock
                    {
                        Text = "天", FontSize = ScaleFont(unitSize),
                        Foreground = new SolidColorBrush(daysUnitColor),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(1 * scale, 0, 0, Math.Max(0, (daysSize - unitSize) / 4) * scale)
                    });
                }

                line.Children.Add(right);
                block.Children.Add(line);

                if (hasNote)
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = it.Note,
                        FontSize = ScaleFont(unitSize),
                        Foreground = new SolidColorBrush(muted),
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = innerW,
                        Margin = new Thickness(0, 1 * scale, 0, 0)
                    });
                }

                stack.Children.Add(block);
            }
        }

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = ScaleFont(11),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 8 * scale, 0, 0)
        });

        // 天数靠右贴边，右侧内边距收到 5（其余三边仍 13，标题 / 备注才不会贴边）
        return Card(stack, scale, theme, padRightDip: 5);
    }

    // ---------- 值日生名单便签 ----------
    /// <summary>
    /// 值日生名单便签。<b>字号与间距对齐悬浮便签</b>；「今天」那一行加一条强调色竖条 + 淡底，
    /// 一眼看出今天谁值日。
    /// </summary>
    internal static Border BuildDutyCard(DutyBoard board, double scale, Func<double, double> ScaleFont)
    {
        var theme = HomeworkThemes.Find(board.Theme);
        var fg = ParseHwColor(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        var accent = ParseHwColor(theme.Accent, Color.FromRgb(0x2E, 0x7D, 0x32));
        var muted = ParseHwColor(theme.MutedColor, Color.FromRgb(0xB0, 0xB6, 0xBF));

        var stack = new StackPanel();

        var ts = board.TitleStyle;
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(board.Title) ? "值日生名单" : board.Title,
            FontSize = ScaleFont(HwTextRender.TitleSize(ts)),
            FontWeight = HwTextRender.BoldOr(ts, true) ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(ts, theme.TitleColor),
                                            ParseHwColor(theme.TitleColor, Color.FromRgb(0x1B, 0x5E, 0x20)))),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            TextWrapping = TextWrapping.Wrap
        });

        var cs = board.ContentStyle;
        double bodySize = HwTextRender.BodySize(cs);
        var bodyBrush = new SolidColorBrush(ParseHwColor(HwTextRender.ColorOr(cs, theme.Foreground), fg));
        var bodyWeight = HwTextRender.BoldOr(cs, false) ? FontWeights.Bold : FontWeights.Normal;

        // 星期列 / 值日内容各有自己的字号颜色（默认 12 号加粗弱色 / 11 号弱色，与加设置项之前一致）
        var ds = board.DayStyle;
        double daySize = HwTextRender.ScaleSize(ds, 12);
        var dayWeight = HwTextRender.BoldOr(ds, true) ? FontWeights.Bold : FontWeights.Normal;
        var dayColorNormal = ParseHwColor(HwTextRender.ColorOr(ds, theme.MutedColor), muted);
        var dayColorToday = ParseHwColor(HwTextRender.ColorOr(ds, theme.Accent), accent);

        var dutySt = board.DutyStyle;
        double dutySize = HwTextRender.ScaleSize(dutySt, 11);
        var dutyWeight = HwTextRender.BoldOr(dutySt, false) ? FontWeights.Bold : FontWeights.Normal;
        var dutyColor = ParseHwColor(HwTextRender.ColorOr(dutySt, theme.MutedColor), muted);

        string todayName = WeekDayText(DateTime.Today.DayOfWeek);

        var items = board.SortedItems();
        if (items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（暂无值日安排，双击添加）",
                FontSize = ScaleFont(14),
                Foreground = new SolidColorBrush(muted)
            });
        }
        else
        {
            foreach (var it in items)
            {
                string day = it.DayText();
                bool isToday = board.HighlightToday && string.Equals(day, todayName, StringComparison.Ordinal);

                var row = new Border
                {
                    Margin = new Thickness(0, 0, 0, 6 * scale),
                    Padding = new Thickness(isToday ? 6 * scale : 0, isToday ? 4 * scale : 0, 0, isToday ? 4 * scale : 0),
                    CornerRadius = new CornerRadius(6 * scale),
                    Background = isToday ? BrushOf(accent, 0x22) : null,
                    BorderThickness = isToday ? new Thickness(2 * scale, 0, 0, 0) : new Thickness(0),
                    BorderBrush = isToday ? new SolidColorBrush(accent) : null
                };

                var panel = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock
                {
                    Text = day,
                    FontSize = ScaleFont(daySize),
                    FontWeight = dayWeight,
                    Foreground = new SolidColorBrush(isToday ? dayColorToday : dayColorNormal),
                    Margin = new Thickness(0, 0, 8 * scale, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                head.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(it.Names) ? "（未填写）" : it.Names,
                    FontSize = ScaleFont(bodySize),
                    FontWeight = bodyWeight,
                    Foreground = bodyBrush,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(head);

                if (!string.IsNullOrWhiteSpace(it.Duty))
                    panel.Children.Add(new TextBlock
                    {
                        Text = it.Duty,
                        FontSize = ScaleFont(dutySize),
                        FontWeight = dutyWeight,
                        Foreground = new SolidColorBrush(dutyColor),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1 * scale, 0, 0)
                    });

                row.Child = panel;
                stack.Children.Add(row);
            }
        }

        stack.Children.Add(new TextBlock
        {
            Text = "更新于 " + board.UpdatedAt.ToString("MM-dd HH:mm"),
            FontSize = ScaleFont(11),
            Foreground = new SolidColorBrush(muted),
            Margin = new Thickness(0, 8 * scale, 0, 0)
        });

        return Card(stack, scale, theme);
    }

    /// <summary>DayOfWeek → 「周一 … 周日」（与 <see cref="DutyItem.DayNames"/> 口径一致）。</summary>
    internal static string WeekDayText(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    /// <summary>带透明度的纯色画刷（用于便签底色 / 半透明文字等）。</summary>
    private static SolidColorBrush BrushOf(Color c, byte alpha = 0xFF)
        => new(Color.FromArgb(alpha, c.R, c.G, c.B));

    /// <summary>便签外框：圆角 / 描边 / 内边距与悬浮便签一致（12 / 1 / 13 DIP），按便签主题配色。</summary>
    /// <param name="padRightDip">右侧内边距（DIP）；倒计时卡天数靠右贴边，右侧收窄到 5，别在「天」字后面吊一截空</param>
    private static Border Card(StackPanel content, double scale, HomeworkTheme? theme = null, double padRightDip = PAD_DIP) => new()
    {
        CornerRadius = new CornerRadius(RADIUS_DIP * scale),
        Background = theme == null
            ? BrushOf(Color.FromRgb(0xFF, 0xFF, 0xFF), 0xF6)
            : BrushOf(ParseHwColor(theme.Background, Color.FromRgb(0xFF, 0xFF, 0xFF)), 0xF7),
        BorderBrush = theme == null
            ? new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xEC))
            : BrushOf(ParseHwColor(theme.BorderColor, Color.FromRgb(0xC9, 0xD6, 0xEC))),
        BorderThickness = new Thickness(1 * scale),
        Padding = new Thickness(PAD_DIP * scale, PAD_DIP * scale, padRightDip * scale, PAD_DIP * scale),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14 * scale,
            Opacity = 0.28,
            ShadowDepth = 4 * scale
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
