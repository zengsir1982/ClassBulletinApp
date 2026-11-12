using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;

namespace ClassBulletinApp;

/// <summary>
/// 板头文字格式窗：板名 / 班级 / 时间的字号、粗体、颜色。
/// <para>做成独立小窗而不是塞进便签编辑窗：老师只想把「班级 + 时间」放大给后排看，
/// 不该被迫先选一条便签。字号上限 96，与便签的大屏档位同源。</para>
/// </summary>
public class DoodleHeaderStyleWindow : Window
{
    private static readonly double[] TitleSizes = { 14, 20, 28, 38, 50, 64, 80, 96 };
    private static readonly string[] SizeNames = { "常规", "较大", "大", "特大", "超大", "巨大", "极大", "屏幕" };

    private readonly DoodleHeaderStyle _draft;
    private readonly WrapPanel _titleSizes = new();
    private readonly WrapPanel _infoSizes = new();
    private readonly WrapPanel _colors = new();
    private readonly TextBlock _previewTitle = new();
    private readonly TextBlock _previewInfo = new();
    private readonly CheckBox _bold = new();
    private readonly TextBlock _readout = new();

    public DoodleHeaderStyleWindow(DoodleHeaderStyle? source)
    {
        var s = source ?? new DoodleHeaderStyle();
        _draft = s.Clone();

        Title = "板头文字格式";
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Background = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(20) };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = "板头文字格式", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Ink });
        head.Children.Add(new TextBlock
        {
            Text = "教室后排看不清班级和时间时，把字号往上调。板头会自动变高，不会裁掉文字。",
            FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = MkButton("取消", Soft, Ink);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MkButton("保存", Primary, Brushes.White);
        save.Click += (_, _) =>
        {
            _draft.Bold = _bold.IsChecked == true;
            DialogResult = true;
            Close();
        };
        bar.Children.Add(cancel);
        bar.Children.Add(save);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel();

        var preview = new Border
        {
            Background = Hex("#F4F7FB"), BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), MinHeight = 110
        };
        var pv = new StackPanel();
        _previewTitle.Text = "高三(2)班 · 今日公示";
        _previewTitle.FontWeight = FontWeights.Bold;
        _previewInfo.Text = "高三(2)班    11-11 14:30";
        _previewInfo.Margin = new Thickness(0, 10, 0, 0);
        pv.Children.Add(_previewTitle);
        pv.Children.Add(_previewInfo);
        preview.Child = pv;
        stack.Children.Add(preview);

        stack.Children.Add(Section("板名 / 标题字号"));
        _titleSizes.Orientation = Orientation.Horizontal;
        stack.Children.Add(_titleSizes);

        stack.Children.Add(Section("班级 / 时间字号"));
        _infoSizes.Orientation = Orientation.Horizontal;
        stack.Children.Add(_infoSizes);
        _readout.FontSize = 13;
        _readout.Foreground = Primary;
        _readout.Margin = new Thickness(0, 2, 0, 8);
        stack.Children.Add(_readout);

        stack.Children.Add(Section("文字颜色"));
        _colors.Orientation = Orientation.Horizontal;
        stack.Children.Add(_colors);

        _bold.Content = "加粗";
        _bold.FontSize = 15;
        _bold.IsChecked = s.Bold;
        _bold.Margin = new Thickness(0, 12, 0, 0);
        _bold.Checked += (_, _) => UpdatePreview();
        _bold.Unchecked += (_, _) => UpdatePreview();
        stack.Children.Add(_bold);

        body.Content = stack;
        root.Children.Add(body);
        Content = root;

        BuildSizeButtons();
        BuildColors();
        UpdatePreview();
    }

    public DoodleHeaderStyle Draft => _draft;

    private void BuildSizeButtons()
    {
        _titleSizes.Children.Clear();
        _infoSizes.Children.Clear();
        for (int i = 0; i < TitleSizes.Length; i++)
        {
            int n = i;
            _titleSizes.Children.Add(MkChoice(SizeNames[i],
                Math.Abs(_draft.SafeTitleSize - TitleSizes[i]) < 0.5,
                () => { _draft.TitleSize = TitleSizes[n]; BuildSizeButtons(); UpdatePreview(); }, 78));
            _infoSizes.Children.Add(MkChoice(SizeNames[i],
                Math.Abs(_draft.SafeInfoSize - TitleSizes[i]) < 0.5,
                () => { _draft.InfoSize = TitleSizes[n]; BuildSizeButtons(); UpdatePreview(); }, 78));
        }
        _readout.Text = $"板名 {_draft.SafeTitleSize:0}  ·  班级 / 时间 {_draft.SafeInfoSize:0}";
    }

    private void BuildColors()
    {
        _colors.Children.Clear();
        var options = new (string Name, string Hex)[]
        {
            ("跟随主题", ""), ("墨黑", "#25303D"), ("纯白", "#FFFFFF"),
            ("亮黄", "#FFE081"), ("警示红", "#C0392B")
        };
        foreach (var (name, hex) in options)
        {
            bool on = string.Equals(hex, _draft.Color ?? "", StringComparison.OrdinalIgnoreCase);
            var b = MkChoice(name, on, () => { _draft.Color = hex; BuildColors(); UpdatePreview(); }, 92);
            _colors.Children.Add(b);
        }
    }

    private void UpdatePreview()
    {
        Brush fg = string.IsNullOrWhiteSpace(_draft.Color) ? Ink : Hex(_draft.Color);
        _previewTitle.FontSize = Math.Min(_draft.SafeTitleSize, 52);
        _previewInfo.FontSize = Math.Min(_draft.SafeInfoSize, 44);
        _previewTitle.Foreground = fg;
        _previewInfo.Foreground = fg;
        _previewTitle.FontWeight = _bold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Ink,
        Margin = new Thickness(0, 14, 0, 6)
    };

    private static Button MkChoice(string text, bool selected, Action action, double width)
    {
        var b = new Button
        {
            Content = text, Width = width, Height = 46, Margin = new Thickness(0, 0, 6, 6),
            FontSize = 14, Foreground = selected ? Brushes.White : Ink,
            Background = selected ? Primary : Soft,
            BorderBrush = selected ? Primary : Line, BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static Button MkButton(string text, Brush bg, Brush fg) => new()
    {
        Content = text, Height = 48, MinWidth = 116, Margin = new Thickness(8, 0, 0, 0),
        FontSize = 15, FontWeight = FontWeights.Bold, Foreground = fg,
        Background = bg, BorderBrush = bg, BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static readonly Brush Primary = Hex("#1E4FA3");
    private static readonly Brush Ink = Hex("#25303D");
    private static readonly Brush Muted = Hex("#667085");
    private static readonly Brush Line = Hex("#E1E6EE");
    private static readonly Brush Soft = Hex("#F4F7FB");

    private static Brush Hex(string s)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s);
        return new SolidColorBrush(c);
    }
}
