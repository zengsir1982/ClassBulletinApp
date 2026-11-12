using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 涂鸦板用的小编辑窗：写 / 改一张手动便签，或改板名（<c>showBody:false</c>）。
/// <para>★ 刻意做成自绘无边框：这块板本身就是圆角材质板，弹一个系统灰边框对话框出来
/// 在观感上是断裂的。</para>
/// </summary>
public sealed class DoodleNoteEditWindow : Window
{
    private readonly TextBox _title;
    private readonly TextBox _body;
    private readonly bool _showBody;

    public string NoteTitle => _title.Text.Trim();
    public string NoteBody => _body.Text.Trim();

    public DoodleNoteEditWindow(string title, string body, bool showBody = true)
    {
        _showBody = showBody;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Width = 380;
        Height = showBody ? 264 : 156;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE6, 0xEE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14)
        };
        Content = card;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = root;

        var head = new TextBlock
        {
            Text = showBody ? "编辑便签" : "改板名",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x3A)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        head.MouseLeftButtonDown += (_, _) => DragMove();
        root.Children.Add(head);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        _title = new TextBox
        {
            Text = title ?? "",
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE6, 0xEE)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD))
        };
        stack.Children.Add(_title);

        _body = new TextBox
        {
            Text = body ?? "",
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Height = 92,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE6, 0xEE)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD))
        };
        if (showBody) stack.Children.Add(_body);

        Grid.SetRow(stack, 1);
        root.Children.Add(stack);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancel = MakeButton("取消", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        bar.Children.Add(cancel);

        var ok = MakeButton("确定", true);
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        bar.Children.Add(ok);

        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            { DialogResult = true; Close(); }
        };

        Loaded += (_, _) =>
        {
            _title.Focus();
            _title.SelectAll();
        };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = 76,
            Height = 34,
            FontSize = 12,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(primary ? Colors.White : Color.FromRgb(0x47, 0x54, 0x67)),
            Background = new SolidColorBrush(primary
                ? Color.FromRgb(0x1E, 0x4F, 0xA3)
                : Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(primary
                ? Color.FromRgb(0x1E, 0x4F, 0xA3)
                : Color.FromRgb(0xE1, 0xE6, 0xEE)),
            BorderThickness = new Thickness(1),
            Focusable = false
        };
        return btn;
    }
}
