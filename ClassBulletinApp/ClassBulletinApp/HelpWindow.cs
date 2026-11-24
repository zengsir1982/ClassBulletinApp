using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClassBulletinApp.Help;

// WPF / WinForms 同名类型消歧义
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 帮助窗口：卡片式首页（8 篇快速上手 + 6 条故障速查）+ 单篇正文 + 关键字搜索。
///
/// <para><b>为什么不是一本说明书</b>：老师不会在没事的时候翻目录。所以首页不做目录树，
/// 直接摆"我要做什么"的卡片；每篇开头第一行就给结论，看得下去就往下看，看不下去也能照做。
/// 真正会让人打电话来的不是"怎么发作业"，而是"发了怎么没出现" —— 所以故障速查与操作篇同等权重。</para>
///
/// <para><b>为什么不用 WebView2</b>：教室机上多一个运行时依赖，就多一处"点了没反应"。
/// 这里用 WPF 自己渲染，零依赖，风格与主程序一致。</para>
/// </summary>
public class HelpWindow : Window
{
    readonly TextBox _search = new();
    readonly ContentControl _stage = new();
    readonly TextBlock _crumb = new();

    static readonly string[] GroupOrder = { "quick", "fix" };

    static string GroupTitle(string g) => g == "fix" ? "出问题怎么办" : "快速上手";
    static string GroupHint(string g) => g == "fix"
        ? "按症状找：连不上、卡住、密码、班级不对、发了没出现"
        : "每篇三步以内，配一张图，照着点就行";

    public HelpWindow(string? startId = null)
    {
        Title = "帮助 · 班级公布栏";
        Width = 1120;
        Height = 720;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ChromePalette.BrushOf(ChromePalette.Surface, "#F4F6FA");

        var root = new DockPanel { LastChildFill = true };
        Content = root;

        // ---------------- 顶栏：标题 + 搜索 ----------------
        var bar = new Border
        {
            Background = Brushes.White,
            BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 12, 20, 12)
        };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var barRow = new DockPanel { LastChildFill = false };
        bar.Child = barRow;

        var titleBox = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(titleBox, Dock.Left);
        barRow.Children.Add(titleBox);

        titleBox.Children.Add(new TextBlock
        {
            Text = "帮助",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ChromePalette.BrushOf(ChromePalette.Ink, "#1F2430")
        });

        _crumb.FontSize = 12.5;
        _crumb.Margin = new Thickness(12, 0, 0, 0);
        _crumb.VerticalAlignment = VerticalAlignment.Center;
        _crumb.Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085");
        titleBox.Children.Add(_crumb);

        // 搜索框靠右
        var searchBox = new Border
        {
            Background = Brushes.White,
            BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0, 8, 0),
            Width = 300,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        DockPanel.SetDock(searchBox, Dock.Right);
        barRow.Children.Add(searchBox);

        var inner = new DockPanel();
        searchBox.Child = inner;

        inner.Children.Add(new TextBlock
        {
            Text = "找",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
        });

        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.FontSize = 13;
        _search.Padding = new Thickness(0, 8, 0, 8);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = "输入关键词，比如「作业」「密码」「连不上」。按 Esc 清空。";
        _search.TextChanged += (_, _) => Navigate();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _search.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Enter) { OpenFirstSearchHit(); e.Handled = true; }
        };
        inner.Children.Add(_search);

        // ---------------- 主体 ----------------
        _stage.Margin = new Thickness(20, 16, 20, 16);
        root.Children.Add(_stage);

        var start = HelpCatalog.Find(startId) ?? HelpCatalog.Topics.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(startId) || start == null)
        {
            ShowHome();
        }
        else
        {
            ShowTopic(start.Id);
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { _search.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                if (_search.Text.Length > 0) { _search.Text = ""; e.Handled = true; }
                else ShowHome();
            }
        };

        GlassChrome.Prepare(this);
    }

    /// <summary>从别处打开帮助时复用同一个窗口（避免连按 F1 开出一堆窗口）。</summary>
    static HelpWindow? _open;

    public static void ShowHelp(Window? owner, string? topicId)
    {
        try
        {
            if (_open != null && _open.IsLoaded)
            {
                _open.ShowTopic(topicId);
                _open.Activate();
                return;
            }
        }
        catch { /* 窗口已被关掉，走下面新建 */ }

        var w = new HelpWindow(topicId);
        if (owner != null && owner.IsLoaded) w.Owner = owner;
        w.Closed += (_, _) => _open = null;
        _open = w;
        w.Show();
    }

    void Navigate()
    {
        string kw = (_search.Text ?? "").Trim();
        if (kw.Length == 0) { ShowHome(); return; }
        ShowSearch(kw);
    }

    void OpenFirstSearchHit()
    {
        string kw = (_search.Text ?? "").Trim();
        if (kw.Length == 0) return;
        var hit = HelpCatalog.Search(kw).FirstOrDefault();
        if (hit != null) OpenTopic(hit);
    }

    void OpenTopic(HelpTopic t)
    {
        _search.Text = "";
        ShowTopic(t.Id);
    }

    // ================== 首页 ==================

    void ShowHome()
    {
        _crumb.Text = "";

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "选一件你现在要做的事",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "每篇都在三步以内讲完，配一张整屏图（点图可放大）。找不到就直接在上面搜。",
            FontSize = 12.5,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085"),
            Margin = new Thickness(2, 0, 0, 14)
        });

        foreach (var g in GroupOrder)
        {
            var topics = HelpCatalog.Group(g).ToList();
            if (topics.Count == 0) continue;

            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 6, 0, 8),
                Children =
                {
                    new TextBlock
                    {
                        Text = GroupTitle(g),
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = GroupHint(g),
                        FontSize = 12,
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
                    }
                }
            });

            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var t in topics) wrap.Children.Add(Card(t));
            panel.Children.Add(wrap);
        }

        _stage.Content = Scroll(panel);
    }

    static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0)
    };

    /// <summary>一张卡片：标题 + 一句摘要。用 Border 而不是 Button —— 按钮自带的内边距和焦点框在卡片上很难看。</summary>
    Border Card(HelpTopic t)
    {
        bool fix = t.Group == "fix";
        string accent = fix ? "#B45309" : "#1E4FA3";

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = t.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ChromePalette.BrushOf(ChromePalette.Ink, "#1F2430")
        });
        stack.Children.Add(new TextBlock
        {
            Text = t.Summary,
            FontSize = 11.5,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
        });

        var b = new Border
        {
            Width = 338,
            Height = 86,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14, 12, 12, 12),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = t.Summary
        };

        // 左侧一道色条区分两组（快速上手 = 蓝，出问题 = 橙），扫视时不用读文字
        var dock = new DockPanel();
        dock.Children.Add(new Border
        {
            Width = 3,
            Background = ListRowStyle.HexBrush(accent),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 10, 0)
        });
        dock.Children.Add(stack);
        b.Child = dock;
        b.Width = 338;

        b.MouseEnter += (_, _) =>
        {
            b.BorderBrush = ListRowStyle.HexBrush(accent);
            b.Background = ListRowStyle.HexBrush("#FBFCFE");
        };
        b.MouseLeave += (_, _) =>
        {
            b.BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE");
            b.Background = Brushes.White;
        };
        b.MouseLeftButtonUp += (_, _) => OpenTopic(t);

        return b;
    }

    // ================== 搜索 ==================

    void ShowSearch(string kw)
    {
        var hits = HelpCatalog.Search(kw);
        _crumb.Text = $"搜索「{kw}」";

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = hits.Count == 0 ? $"没有找到「{kw}」" : $"找到 {hits.Count} 篇",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 10)
        });

        if (hits.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "换个说法试试，比如「作业」「密码」「连不上」「导入」。\n或者点左上角清空搜索，回到卡片列表自己找。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
            });
        }

        var wrap = new WrapPanel();
        foreach (var t in hits) wrap.Children.Add(Card(t));
        panel.Children.Add(wrap);

        _stage.Content = Scroll(panel);
    }

    // ================== 单篇 ==================

    void ShowTopic(string? id)
    {
        var t = HelpCatalog.Find(id) ?? HelpCatalog.Topics.FirstOrDefault();
        if (t == null)
        {
            _stage.Content = new TextBlock
            {
                Text = "帮助内容没有随程序一起打包（Resources/Help/help.txt 缺失）。\n请重新安装或联系维护人。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
            };
            return;
        }

        _search.Text = "";
        _crumb.Text = "› " + t.Title;

        var root = new DockPanel { LastChildFill = true };

        // 返回条
        var backBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(backBar, Dock.Top);
        root.Children.Add(backBar);

        var back = new Button
        {
            Content = "← 返回目录",
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        back.Click += (_, _) => { _search.Text = ""; ShowHome(); };
        backBar.Children.Add(back);

        // 正文
        var doc = HelpMarkup.Render(t.Body, tid => ShowTopic(tid));
        var viewer = new FlowDocumentScrollViewer
        {
            Document = doc,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Focusable = false
        };

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = ChromePalette.BrushOf(ChromePalette.Line, "#E1E6EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 18, 22, 18)
        };

        var stack = new DockPanel { LastChildFill = true };
        var titleBlock = new TextBlock
        {
            Text = t.Title,
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = ChromePalette.BrushOf(ChromePalette.Ink, "#1F2430")
        };
        DockPanel.SetDock(titleBlock, Dock.Top);
        stack.Children.Add(titleBlock);

        var sub = new TextBlock
        {
            Text = t.Summary,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ChromePalette.BrushOf(ChromePalette.Muted, "#667085")
        };
        DockPanel.SetDock(sub, Dock.Top);
        stack.Children.Add(sub);

        stack.Children.Add(viewer);
        card.Child = stack;

        viewer.Padding = new Thickness(0);
        card.Padding = new Thickness(24, 18, 24, 14);

        root.Children.Add(card);
        _stage.Content = root;
    }
}
