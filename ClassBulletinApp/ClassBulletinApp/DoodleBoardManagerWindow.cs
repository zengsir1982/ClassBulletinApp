using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;

// 项目同时启用 WPF / WinForms，同名类型统一指向 WPF 版本
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 本机所有黑板的集中管理入口：打开 / 收起 / 改名 / 删除。
///
/// <para>★ 数据只来自 <c>settings.json</c> 的 <see cref="AppSettings.DoodleBoards"/>，
/// 不进班级同步包 —— 板上的东西是老师本地随手写贴的。</para>
///
/// <para>★「收起」只关窗口，配置和笔迹都保留；只有「删除」才真正移除。
/// 这两件事在早期版本被合并成一个 ×，导致关掉的板再也找不回来。</para>
/// </summary>
public class DoodleBoardManagerWindow : Window
{
    private static readonly Brush Primary = Hex("#1E4FA3");
    private static readonly Brush Danger = Hex("#C0392B");
    private static readonly Brush Ink = Hex("#25303D");
    private static readonly Brush Muted = Hex("#667085");
    private static readonly Brush Line = Hex("#E1E6EE");
    private static readonly Brush Soft = Hex("#F4F7FB");

    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = new();

    /// <summary>诊断用：注入假列表（截图不该为了看一眼就去写用户设置）。</summary>
    internal static IReadOnlyList<DoodleBoardConfig>? DiagConfigsOverride;
    /// <summary>诊断用：这批 Id 显示为「正在显示」。</summary>
    internal static HashSet<string>? DiagOpenIds;

    public DoodleBoardManagerWindow()
    {
        Title = "黑板管理";
        Width = 820;
        MinWidth = 660;
        MaxHeight = 780;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // 与涂鸦板 / 控制面板统一字体，避免中文 fallback 把数字和正文排成两套字
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Background = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(20) };

        // ---------- 顶部：标题 + 规则说明 + 汇总 ----------
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock
        {
            Text = "黑板管理",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = Ink
        });
        head.Children.Add(new TextBlock
        {
            Text = "这里能看到本机创建过的所有黑板。点 × 收起不会丢内容，只有点「删除」才会真正删掉。",
            FontSize = 12.5,
            Foreground = Muted,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        _summary.FontSize = 12.5;
        _summary.Foreground = Primary;
        _summary.Margin = new Thickness(0, 8, 0, 0);
        head.Children.Add(_summary);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---------- 底部：新建 / 刷新 / 关闭 ----------
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        bar.Children.Add(MakeButton("＋ 新建黑板", Primary, Brushes.White, () =>
        {
            try { DoodleBoardService.CreateNew(); }
            catch (Exception ex) { ShowError(ex); }
            Refresh();
        }));
        bar.Children.Add(MakeButton("刷新", Soft, Ink, Refresh));
        var close = MakeButton("关闭", Soft, Ink, Close);
        close.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(close);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 列表 ----------
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.Content = _rows;
        root.Children.Add(scroll);

        Content = root;
        Refresh();
    }

    /// <summary>重新读配置并重画列表；任何操作完成后都走这里，状态不会 stale。</summary>
    private void Refresh()
    {
        _rows.Children.Clear();

        var cfgs = DiagConfigsOverride ?? DoodleBoardService.AllConfigs;
        int open = cfgs.Count(c => IsOpen(c));
        _summary.Text = cfgs.Count == 0
            ? "还没有黑板。点下面的「＋ 新建黑板」放一块。"
            : $"共 {cfgs.Count} 块黑板 · {open} 块正在显示";

        if (cfgs.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "新建的黑板会出现在桌面上，并收进托盘「涂鸦板 ▸」列表。\n关掉后仍可在那里重新打开。",
                FontSize = 13,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8, 24, 8, 24)
            });
            return;
        }

        foreach (var cfg in cfgs) _rows.Children.Add(BuildRow(cfg));
    }

    private Border BuildRow(DoodleBoardConfig cfg)
    {
        bool isOpen = IsOpen(cfg);
        string name = string.IsNullOrWhiteSpace(cfg.Name) ? "涂鸦板" : cfg.Name;
        string theme = DoodleBoardThemes.Get(cfg.ThemeKey).Name;
        string mode = string.Equals(cfg.Mode, DoodleModes.Ink, StringComparison.OrdinalIgnoreCase)
            ? "手写黑板" : "便签板";
        int pages = Math.Max(1, cfg.Pages?.Count ?? 1);
        int notes = cfg.Pages != null && cfg.Pages.Count > 0
            ? cfg.Pages.Sum(p => p?.Notes?.Count ?? 0)
            : cfg.Notes?.Count ?? 0;

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 第一行：板名 + 状态 + 摘要（右侧）
        var line = new DockPanel { LastChildFill = false };
        var title = new TextBlock
        {
            Text = name,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(title, Dock.Left);
        line.Children.Add(title);

        var badge = new Border
        {
            Background = isOpen ? Primary : Soft,
            BorderBrush = isOpen ? Primary : Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = isOpen ? "正在显示" : "已收起",
            FontSize = 11.5,
            Foreground = isOpen ? Brushes.White : Muted
        };
        DockPanel.SetDock(badge, Dock.Left);
        line.Children.Add(badge);

        var meta = new TextBlock
        {
            Text = $"{theme} · {mode} · {pages} 个板面 · {notes} 张便签",
            FontSize = 12.5,
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        DockPanel.SetDock(meta, Dock.Right);
        line.Children.Add(meta);
        Grid.SetRow(line, 0);
        grid.Children.Add(line);

        // 第二行：操作按钮（44 DIP 命中区，一体机手指可点）
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        buttons.Children.Add(MakeButton(isOpen ? "切到前面" : "打开", Primary, Brushes.White, () =>
        {
            try { DoodleBoardService.OpenOrActivate(cfg.Id); }
            catch (Exception ex) { ShowError(ex); }
            Refresh();
        }));
        if (isOpen)
        {
            buttons.Children.Add(MakeButton("收起", Soft, Ink, () =>
            {
                DoodleBoardService.CloseById(cfg.Id);
                Refresh();
            }));
        }
        buttons.Children.Add(MakeButton("改名…", Soft, Ink, () => Rename(cfg)));
        var del = MakeButton("删除", Soft, Danger, () => Delete(cfg));
        del.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(del);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);

        card.Child = grid;
        return card;
    }

    private static bool IsOpen(DoodleBoardConfig cfg) =>
        DiagOpenIds != null
            ? DiagOpenIds.Contains(cfg.Id)
            : DoodleBoardService.FindOpen(cfg.Id) != null;

    private void Rename(DoodleBoardConfig cfg)
    {
        var dlg = new DoodleNoteEditWindow(cfg.Name, "", showBody: false)
        {
            Owner = this,
            Title = "改黑板名"
        };
        if (dlg.ShowDialog() != true) return;
        if (!DoodleBoardService.RenameConfig(cfg.Id, dlg.NoteTitle)) return;
        Refresh();
    }

    private void Delete(DoodleBoardConfig cfg)
    {
        string label = string.IsNullOrWhiteSpace(cfg.Name) ? "这块黑板" : $"「{cfg.Name}」";
        if (MessageBox.Show(this,
                $"彻底删除{label}？板上的便签和手写笔迹都会删掉，且无法恢复。\n如果只是暂时不想看到，请用板头的 × 收起。",
                "删除黑板", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        DoodleBoardService.DeleteById(cfg.Id);
        Refresh();
    }

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, "操作失败：" + ex.Message, "黑板管理", MessageBoxButton.OK, MessageBoxImage.Error);

    private static Button MakeButton(string text, Brush bg, Brush fg, Action action)
    {
        var b = new Button
        {
            Content = text,
            Height = 44,                    // 触屏下限
            MinWidth = 92,
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 13.5,
            Foreground = fg,
            Background = bg,
            BorderBrush = bg,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static Brush Hex(string s)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s);
        return new SolidColorBrush(c);
    }

    // ---- 诊断探针：只暴露只读状态，操作一律走服务层，诊断里不弹模态框 ----
    internal int DiagRowCount => _rows.Children.OfType<Border>().Count();
    internal string DiagSummary => _summary.Text ?? "";
    internal void DiagRefresh() => Refresh();
}
