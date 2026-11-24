using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassBulletinApp.Help;

/// <summary>
/// 极简标记 → <see cref="FlowDocument"/>。
///
/// <para><b>为什么自己写而不是引 Markdown 库</b>：这里只需要六种东西（小标题 / 列表 / 图片 /
/// 提示框 / 加粗 / 行内按钮名），引一个库反而要处理表格、脚注、HTML 混排这些永远用不上的东西，
/// 还多一个升级要跟的依赖。帮助是"给老师看的一张纸"，不是排版系统。</para>
///
/// <para><b>支持的行级写法</b>：</para>
/// <list type="bullet">
/// <item><c>## 小标题</c></item>
/// <item><c>- 列表项</c> / <c>1. 编号项</c></item>
/// <item><c>&gt; 提示</c>（浅蓝框）、<c>!&gt; 注意</c>（浅红框）</item>
/// <item><c>![](today.jpg)</c>（整行一张图，按内容宽度缩放）</item>
/// <item><c>**加粗**</c>、<c>`按钮名`</c>、<c>[相关说明](topic:start)</c></item>
/// </list>
/// </summary>
public static class HelpMarkup
{
    /// <summary>图片最大显示宽度（内容列约 700，留一点余量避免横向滚动）。</summary>
    const double ImageWidth = 690;

    /// <summary>
    /// 图片最大显示高度。<b>必须限高</b>：一张 900px 高的整屏图会把第一屏全占掉，
    /// 老师看到的就是"一张图，什么都读不到"，还得先滚一下才知道下面有字。
    /// </summary>
    const double ImageMaxHeight = 430;

    /// <summary>
    /// 渲染成文档。<paramref name="onLink"/> 收到的是 <c>topic:xxx</c> 里的 <c>xxx</c>。
    /// </summary>
    public static FlowDocument Render(string body, Action<string> onLink)
    {
        var doc = new FlowDocument
        {
            FontSize = 13.5,
            LineHeight = 23,
            PagePadding = new Thickness(0, 0, 12, 24),
            Foreground = ListRowStyle.HexBrush("#1F2430")
        };

        var lines = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];
            string t = line.TrimEnd();

            if (t.Trim().Length == 0) { i++; continue; }

            // ---- 图片 ----
            var mImg = Regex.Match(t.Trim(), @"^!\[\]\((.+?)\)$");
            if (mImg.Success)
            {
                var img = TryLoadImage(mImg.Groups[1].Value.Trim());
                if (img != null)
                {
                    // 按原始比例缩到"宽 ≤690 且高 ≤430"，两个方向都守，别让一张长图吃掉整屏
                    double ar = img.PixelHeight <= 0 ? 1.6 : img.PixelWidth / (double)img.PixelHeight;
                    double w = ImageWidth, h = w / ar;
                    if (h > ImageMaxHeight) { h = ImageMaxHeight; w = h * ar; }

                    var visual = new Image
                    {
                        Source = img,
                        Width = w,
                        Height = h,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        ToolTip = "点一下看大图"
                    };
                    // 首屏限高保证"图不占满一屏"，字看不清就点开 —— 两件事都要
                    visual.MouseLeftButtonUp += (_, _) =>
                    {
                        try { new ImagePreviewWindow(img, "查看大图 · 点一下关闭").Show(); }
                        catch { /* 看图失败不该影响读帮助 */ }
                    };
                    doc.Blocks.Add(new BlockUIContainer(new Border
                    {
                        Child = visual,
                        Margin = new Thickness(0, 6, 0, 10),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(1),
                        BorderBrush = ListRowStyle.HexBrush("#DDE3EC"),
                        ClipToBounds = true
                    }));

                    // 图后紧跟一行 `△ 说明` 时并进同一个块，避免说明飘在下一段
                    i++;
                    if (i < lines.Length && lines[i].TrimStart().StartsWith("△", StringComparison.Ordinal))
                    {
                        doc.Blocks.Add(new Paragraph(new Run(lines[i].TrimStart().TrimStart('△').Trim()))
                        {
                            FontSize = 12,
                            Foreground = ListRowStyle.HexBrush("#667085"),
                            Margin = new Thickness(2, 0, 0, 10)
                        });
                        i++;
                    }
                    continue;
                }
                i++;
                continue;
            }

            // ---- 提示框 ----
            if (t.StartsWith("!>", StringComparison.Ordinal) || t.StartsWith(">", StringComparison.Ordinal))
            {
                bool warn = t.StartsWith("!>", StringComparison.Ordinal);
                string text = warn ? t.Substring(2).Trim() : t.Substring(1).Trim();

                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = ListRowStyle.HexBrush(warn ? "#8A4B08" : "#1E3F73")
                };
                AddInlines(tb.Inlines, text, onLink);

                doc.Blocks.Add(new BlockUIContainer(new Border
                {
                    Background = ListRowStyle.HexBrush(warn ? "#FEF6E8" : "#EEF4FD"),
                    BorderBrush = ListRowStyle.HexBrush(warn ? "#F0C48A" : "#C9DCF7"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 6, 0, 10),
                    Child = tb
                }));
                i++;
                continue;
            }

            // ---- 小标题 ----
            if (t.StartsWith("##", StringComparison.Ordinal))
            {
                var p = new Paragraph
                {
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 14, 0, 4),
                    Foreground = ListRowStyle.HexBrush("#16233A")
                };
                AddInlines(p.Inlines, t.TrimStart('#').Trim(), onLink);
                doc.Blocks.Add(p);
                i++;
                continue;
            }

            // ---- 列表 / 编号 ----
            if (IsBullet(t) || IsOrdered(t))
            {
                bool ordered = IsOrdered(t);
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(20, 2, 0, 8),
                    Padding = new Thickness(0)
                };

                while (i < lines.Length)
                {
                    string cur = lines[i].Trim();
                    if (cur.Length == 0) break;
                    if (ordered ? !IsOrdered(cur) : !IsBullet(cur)) break;

                    string content = ordered
                        ? Regex.Replace(cur, @"^\d+[.、)]\s*", "")
                        : cur.Substring(1).Trim();

                    var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AddInlines(p.Inlines, content, onLink);
                    list.ListItems.Add(new ListItem(p));
                    i++;
                }

                doc.Blocks.Add(list);
                continue;
            }

            // ---- 普通段落 ----
            var para = new Paragraph { Margin = new Thickness(0, 3, 0, 6) };
            AddInlines(para.Inlines, t.Trim(), onLink);
            doc.Blocks.Add(para);
            i++;
        }

        return doc;
    }

    static bool IsBullet(string t) => t.StartsWith("- ", StringComparison.Ordinal) || t == "-";
    static bool IsOrdered(string t) => Regex.IsMatch(t, @"^\d+[.、)]\s+");

    /// <summary>行内解析：<c>**粗体**</c>、<c>`按钮名`</c>、<c>[文字](topic:id)</c>。</summary>
    static void AddInlines(InlineCollection target, string text, Action<string> onLink)
    {
        int i = 0;
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            target.Add(new Run(buf.ToString()));
            buf.Clear();
        }

        while (i < text.Length)
        {
            // **粗体**
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    target.Add(new Bold(new Run(text.Substring(i + 2, end - i - 2))));
                    i = end + 2;
                    continue;
                }
            }

            // `按钮名`
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > 0)
                {
                    Flush();
                    target.Add(new Run(text.Substring(i + 1, end - i - 1))
                    {
                        Background = ListRowStyle.HexBrush("#EDF1F8"),
                        Foreground = ListRowStyle.HexBrush("#1E4FA3"),
                        FontWeight = FontWeights.SemiBold
                    });
                    i = end + 1;
                    continue;
                }
            }

            // [文字](topic:id)
            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > 0 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    int endParen = text.IndexOf(')', close + 2);
                    if (endParen > 0)
                    {
                        string label = text.Substring(i + 1, close - i - 1);
                        string href = text.Substring(close + 2, endParen - close - 2).Trim();

                        Flush();
                        var link = new Hyperlink(new Run(label))
                        {
                            Foreground = ListRowStyle.HexBrush("#1E4FA3"),
                            TextDecorations = TextDecorations.Underline,
                            Cursor = System.Windows.Input.Cursors.Hand,
                            Tag = href
                        };
                        link.Click += (_, _) =>
                        {
                            if (href.StartsWith("topic:", StringComparison.OrdinalIgnoreCase))
                                onLink(href.Substring(6));
                        };
                        target.Add(link);
                        i = endParen + 1;
                        continue;
                    }
                }
            }

            buf.Append(text[i]);
            i++;
        }

        Flush();
    }

    /// <summary>图片名 → <c>Resources/Help/&lt;name&gt;</c>。加载失败返回 null（帮助照样能看，只是少张图）。</summary>
    static BitmapImage? TryLoadImage(string name)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/Help/" + name, UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;   // 图丢了不该让整个帮助打不开
        }
    }
}
