using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 与 ClearOverridesWindow 同一套别名规矩：托盘用的 System.Windows.Forms 也注入了同名类型
using MediaColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 通知公告便签的「风格」设置（r79）：配色预设 + 字号。
///
/// <para><b>为什么单独开一个窗。</b>便签的配色一直写死在代码里（<c>ThemeKey = "blue"</c>），
/// 想换成护眼绿或暗夜黑板只能改代码。这里把已有的 <c>HomeworkThemes</c> 七套预设搬出来给用户挑，
/// 并且带一块<b>按真实便签结构渲染的预览</b> —— 只看色卡是选不出效果的，色卡和便签的
/// 头部 / 正文 / 截止标签用的是不同深浅的三四支色。</para>
///
/// <para><b>作用范围。</b>只影响本机的通知公告便签外观，写进 settings.json，
/// 不进数据包 —— 同一条通知在教室机上可以各有各的便签样式。</para>
/// </summary>
public class NoticeStyleWindow : Window
{
    readonly WrapPanel _swatches = new() { Margin = new Thickness(0, 0, 0, 4) };
    readonly RadioButton _small = new() { Content = "小", FontSize = 14, Margin = new Thickness(0, 0, 22, 0) };
    readonly RadioButton _normal = new() { Content = "标准", FontSize = 14, Margin = new Thickness(0, 0, 22, 0) };
    readonly RadioButton _large = new() { Content = "大", FontSize = 14 };

    // 预览里的四个元素：头部底、标题字、正文、截止标签
    readonly Border _previewNote = new();
    readonly Border _previewHeader = new();
    readonly TextBlock _previewTitle = new();
    readonly TextBlock _previewBody = new();
    readonly TextBlock _previewDue = new();

    string _themeKey;
    double _scale;

    /// <summary>保存后的主题键；取消时为 null。</summary>
    public string? ResultTheme { get; private set; }

    /// <summary>保存后的字号倍率。</summary>
    public double ResultScale { get; private set; } = 1.0;

    public NoticeStyleWindow()
    {
        Title = "通知公告便签 · 风格";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var s = Store.LoadSettings();
        _themeKey = string.IsNullOrWhiteSpace(s.NoticeNoteTheme) ? "blue" : s.NoticeNoteTheme.Trim();
        _scale = s.NoticeNoteScale <= 0.01 ? 1.0 : s.NoticeNoteScale;
        if (!HomeworkThemes.Presets.Any(t => string.Equals(t.Key, _themeKey, StringComparison.OrdinalIgnoreCase)))
            _themeKey = "blue";

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "便签风格",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x1B, 0x28, 0x36))
        });
        root.Children.Add(new TextBlock
        {
            Text = "只影响本机的通知公告便签，不会随数据包同步到别的教室机。",
            FontSize = 12,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x66, 0x70, 0x85)),
            Margin = new Thickness(0, 4, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        // ---------- ① 配色 ----------
        root.Children.Add(Label("配色"));
        _swatches.Orientation = Orientation.Horizontal;
        root.Children.Add(_swatches);

        // ---------- ② 字号 ----------
        root.Children.Add(Label("字号"));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        sizeRow.Children.Add(_small);
        sizeRow.Children.Add(_normal);
        sizeRow.Children.Add(_large);
        _normal.IsChecked = _scale >= 0.999 && _scale <= 1.001;
        _small.IsChecked = _scale < 0.999;
        _large.IsChecked = _scale > 1.001;
        _small.Checked += (_, _) => { _scale = 0.9; RefreshPreview(); };
        _normal.Checked += (_, _) => { _scale = 1.0; RefreshPreview(); };
        _large.Checked += (_, _) => { _scale = 1.15; RefreshPreview(); };
        root.Children.Add(sizeRow);

        // ---------- ③ 预览 ----------
        root.Children.Add(Label("预览"));
        root.Children.Add(BuildPreview());

        // ---------- 按钮 ----------
        var reset = new Button
        {
            Content = "恢复默认",
            FontSize = 13,
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HAlign.Left,
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x4F, 0xA3)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "回到「清新蓝 · 标准字号」"
        };
        reset.Click += (_, _) =>
        {
            _themeKey = "blue";
            _scale = 1.0;
            _normal.IsChecked = true;
            RefreshSwatches();
            RefreshPreview();
        };

        var cancel = new Button
        {
            Content = "取消",
            FontSize = 14,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xF3, 0xF4, 0xF6)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xC7, 0xCF, 0xDA))
        };
        cancel.Click += (_, _) => { ResultTheme = null; DialogResult = false; };

        var ok = new Button
        {
            Content = "保存",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(28, 8, 28, 8),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xE9, 0xEF, 0xF9)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xC2, 0xD2, 0xE9)),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x14, 0x3A, 0x7A))
        };
        ok.Click += (_, _) =>
        {
            ResultTheme = _themeKey;
            ResultScale = _scale;
            DialogResult = true;
        };

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(reset, Dock.Left);
        buttons.Children.Add(reset);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right };
        right.Children.Add(cancel);
        right.Children.Add(ok);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        root.Children.Add(buttons);

        Content = root;
        RefreshSwatches();
        RefreshPreview();

        GlassChrome.Prepare(this);
    }

    static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(MediaColor.FromRgb(0x47, 0x54, 0x67)),
        Margin = new Thickness(0, 6, 0, 8)
    };

    /// <summary>七张色卡：底色 / 头部 / 描边 / 字色都按各预设真实赋值，点一下即选中。</summary>
    void RefreshSwatches()
    {
        _swatches.Children.Clear();

        foreach (var t in HomeworkThemes.Presets)
        {
            bool picked = string.Equals(t.Key, _themeKey, StringComparison.OrdinalIgnoreCase);
            var bg = ThemeHelper.Brush(t.Background, Colors.White);
            var fg = ThemeHelper.Brush(t.Foreground, Colors.Black);
            var head = ThemeHelper.Brush(t.HeaderBackground, Colors.White);

            var inner = new StackPanel();
            inner.Children.Add(new Border
            {
                Height = 16,
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = head
            });
            inner.Children.Add(new TextBlock
            {
                Text = t.Name,
                FontSize = 12,
                Foreground = fg,
                Margin = new Thickness(8, 7, 8, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var card = new Border
            {
                Width = 110,
                Margin = new Thickness(0, 0, 8, 10),
                Background = bg,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(picked ? 2 : 1),
                BorderBrush = picked
                    ? new SolidColorBrush(MediaColor.FromRgb(0x14, 0x3A, 0x7A))
                    : ThemeHelper.Brush(t.BorderColor, Colors.Gray),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = inner,
                ToolTip = picked ? t.Name + "（当前）" : "点击选用「" + t.Name + "」"
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                _themeKey = t.Key;
                RefreshSwatches();
                RefreshPreview();
                e.Handled = true;
            };
            _swatches.Children.Add(card);
        }
    }

    /// <summary>预览便签：结构照着真实的 <see cref="NoticeWindow"/> 抄 —— 头部条 + 圆点 + 标题 + 截止标签。</summary>
    Border BuildPreview()
    {
        _previewHeader.Height = 30;
        _previewHeader.CornerRadius = new CornerRadius(11, 11, 0, 0);
        _previewHeader.Padding = new Thickness(12, 0, 12, 0);
        _previewHeader.Child = new TextBlock
        {
            Text = "通知公告",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VAlign.Center
        };

        _previewTitle.FontWeight = FontWeights.Bold;
        _previewBody.TextWrapping = TextWrapping.Wrap;
        _previewBody.Margin = new Thickness(0, 2, 0, 0);
        _previewDue.FontSize = 11;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VAlign.Top,
            Margin = new Thickness(0, 7, 7, 0)
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var body = new StackPanel();
        body.Children.Add(_previewTitle);
        body.Children.Add(_previewBody);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        Grid.SetColumn(_previewDue, 2);
        grid.Children.Add(_previewDue);

        var inner = new StackPanel();
        inner.Children.Add(_previewHeader);
        inner.Children.Add(new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 12),
            Children = { grid }
        });

        _previewNote.CornerRadius = new CornerRadius(12);
        _previewNote.BorderThickness = new Thickness(1);
        _previewNote.Width = 320;
        _previewNote.HorizontalAlignment = HAlign.Left;
        _previewNote.Margin = new Thickness(0, 0, 0, 6);
        _previewNote.Child = inner;
        _previewNote.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            Opacity = 0.18,
            ShadowDepth = 3,
            Direction = 270
        };

        // 用一个占位 dot 的引用给 RefreshPreview 上色
        _dot = dot;
        return new Border { Child = _previewNote };
    }

    Border? _dot;

    void RefreshPreview()
    {
        var t = HomeworkThemes.Find(_themeKey);
        var fg = ThemeHelper.Brush(t.Foreground, Colors.Black);
        var muted = ThemeHelper.Brush(t.MutedColor, Colors.Gray);
        var accent = ThemeHelper.Brush(t.Accent, Colors.Blue);

        _previewNote.Background = ThemeHelper.Brush(t.Background, Colors.White);
        _previewNote.BorderBrush = ThemeHelper.Brush(t.BorderColor, Colors.Gray);
        _previewHeader.Background = ThemeHelper.Brush(t.HeaderBackground, Colors.White);
        if (_previewHeader.Child is TextBlock h)
            h.Foreground = ThemeHelper.Brush(t.HeaderForeground, Colors.Black);
        if (_dot != null) _dot.Background = accent;

        _previewTitle.Text = "学习辅导";
        _previewTitle.FontSize = 14 * _scale;
        _previewTitle.Foreground = fg;

        _previewBody.Text = "明天下午第 8 节后检查作业本，记得带齐。";
        _previewBody.FontSize = 13 * _scale;
        _previewBody.Foreground = muted;

        _previewDue.Text = "明天";
        _previewDue.Foreground = new SolidColorBrush(MediaColor.FromRgb(0xC2, 0x41, 0x0C));
        _previewDue.FontWeight = FontWeights.Bold;
        _previewDue.Margin = new Thickness(8, 3, 0, 0);
        _previewDue.VerticalAlignment = VAlign.Top;
    }
}
