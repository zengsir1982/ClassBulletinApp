using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfImageSource = System.Windows.Media.ImageSource;

namespace ClassBulletinApp;

/// <summary>
/// 应用快捷方式管理：增删常用软件 / 文件夹，设置「仅显示图标」与图标大小，并实时预览。
/// 确定后写回 AppSettings（悬浮课表底部应用栏）。
/// </summary>
public partial class QuickLaunchWindow : Window
{
    private readonly ObservableCollection<QuickLaunchItem> _items = new();
    private bool _iconOnly;
    private double _size = 28;

    /// <summary>内置快捷（作业 / 缺交 / 便签）胶囊的字号；0 = 随悬浮课表宽度自动。</summary>
    private double _builtinSize;

    /// <summary>初始化期间 ComboBox 会触发 SelectionChanged，此标志避免把初始值当成用户改动。</summary>
    private bool _builtinReady;

    public QuickLaunchWindow()
    {
        InitializeComponent();
        var s = Store.LoadSettings();
        foreach (var it in s.QuickLaunchers ?? new System.Collections.Generic.List<QuickLaunchItem>())
            _items.Add(it);
        _iconOnly = s.QlIconOnly;
        _size = s.QlIconSize > 0 ? s.QlIconSize : 28;
        _builtinSize = s.QaBuiltinFontSize;

        ChkIconOnly.IsChecked = _iconOnly;
        SlIconSize.Value = _size;
        TxtIconSize.Text = ((int)_size) + " px";

        SelectBuiltinSizeOption();
        RenderList();
        RenderPreview();
        RenderBuiltinPreview();
    }

    // ---------- 内置快捷（作业 / 缺交 / 便签）的大小 ----------

    /// <summary>把设置值对应到下拉项（找不到就回到「自动」）。</summary>
    private void SelectBuiltinSizeOption()
    {
        _builtinReady = false;
        try
        {
            foreach (var obj in CbBuiltinSize.Items)
            {
                if (obj is ComboBoxItem it && TryParseTag(it, out double v)
                    && Math.Abs(v - _builtinSize) < 0.01)
                {
                    it.IsSelected = true;
                    return;
                }
            }
            if (CbBuiltinSize.Items.Count > 0)
                ((ComboBoxItem)CbBuiltinSize.Items[0]).IsSelected = true;
        }
        finally { _builtinReady = true; }
    }

    private static bool TryParseTag(ComboBoxItem item, out double value)
        => double.TryParse(item.Tag as string ?? "", System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out value);

    private double SelectedBuiltinSize()
        => CbBuiltinSize.SelectedItem is ComboBoxItem it && TryParseTag(it, out double v) ? v : 0;

    private void CbBuiltinSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_builtinReady) return;
        _builtinSize = SelectedBuiltinSize();
        RenderBuiltinPreview();
    }

    /// <summary>
    /// 预览三个内置快捷胶囊（与悬浮课表底部同一套样式 + 同一套大小换算）。
    /// 「自动」档按悬浮课表当前宽度换算，所以这里能直接看出窄窗下会不会挤成两行。
    /// </summary>
    private void RenderBuiltinPreview()
    {
        if (BuiltinPreview == null) return;
        BuiltinPreview.Children.Clear();

        double ownerWidth = Owner is Window w && w.ActualWidth > 0 ? w.ActualWidth : 420;
        double fs = QuickActionStyle.Resolve(_builtinSize, ownerWidth);
        var style = TryFindResource("QuickActionBtn") as Style;

        void Add(string text, string bg, string border)
        {
            var b = new Button
            {
                Style = style,
                Background = HexBrush(bg),
                BorderBrush = HexBrush(border),
                Content = new TextBlock { Text = text },
                IsTabStop = false,
                Focusable = false,
                IsHitTestVisible = false          // 纯预览，点了不该触发任何事
            };
            QuickActionStyle.Apply(b, fs);
            BuiltinPreview.Children.Add(b);
        }

        Add("📋 作业", "#EAF2FD", "#9EC1F0");
        Add("📝 缺交", "#FFF4E5", "#F0C48A");
        Add("📌 便签", "#F2EDFD", "#BFAFE8");

        if (TxtBuiltinHint != null)
            TxtBuiltinHint.Text = _builtinSize > 0
                ? $"当前按字号 {fs:0.#} 显示（固定档）"
                : $"自动：按悬浮课表宽度 {Math.Round(ownerWidth)} 换算为字号 {fs:0.#}";
    }

    private static WpfBrush HexBrush(string hex)
        => new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    // ---------- 预览（与悬浮课表底部应用栏完全一致） ----------
    private void RenderPreview()
    {
        if (PreviewWrap == null) return;
        PreviewWrap.Children.Clear();
        TxtPreviewEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var icon = (QlIconConverter)Resources["QlIcon"];
        foreach (var item in _items)
        {
            var border = new Border
            {
                Background = WpfBrushes.White,
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE3, 0xE8, 0xEF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var panel = new DockPanel { LastChildFill = false };
            var open = new Button
            {
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3, 6, 3),
                IsEnabled = false,
                ToolTip = item.Path
            };
            var img = new Image
            {
                Width = _size,
                Height = _size,
                Margin = new Thickness(0, 0, _iconOnly ? 0 : 5, 0),
                Source = icon.Convert(item.Path, typeof(WpfImageSource), null, null) as WpfImageSource
            };
            if (_iconOnly)
            {
                open.Content = img;
            }
            else
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(img);
                sp.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                open.Content = sp;
            }
            DockPanel.SetDock(open, Dock.Left);
            panel.Children.Add(open);
            border.Child = panel;
            PreviewWrap.Children.Add(border);
        }
    }

    // ---------- 列表（增删管理） ----------
    private void RenderList()
    {
        if (ListPanel == null) return;
        ListPanel.Children.Clear();
        if (_items.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = "（还没有添加任何快捷方式）",
                FontSize = 13,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x9C, 0xA3, 0xAF)),
                Margin = new Thickness(2, 4, 0, 0)
            });
            return;
        }

        var icon = (QlIconConverter)Resources["QlIcon"];
        foreach (var item in _items)
        {
            var row = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconImg = new Image
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = icon.Convert(item.Path, typeof(WpfImageSource), null, null) as WpfImageSource
            };
            DockPanel.SetDock(iconImg, Dock.Left);
            row.Children.Add(iconImg);

            var name = new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);

            var del = new Button
            {
                Content = "删除",
                FontSize = 12,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(10, 0, 0, 0),
                Background = new SolidColorBrush(WpfColor.FromRgb(0xFD, 0xE8, 0xE8)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE0, 0xA0, 0xA0)),
                Tag = item
            };
            del.Click += (_, _) =>
            {
                _items.Remove(item);
                RenderList();
                RenderPreview();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            ListPanel.Children.Add(row);
        }
    }

    // ---------- 添加 ----------
    private void BtnAddProgram_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "程序与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            Title = "选择要添加的软件"
        };
        if (dlg.ShowDialog() == true) AddItem(dlg.FileName, false);
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加的文件夹" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddItem(dlg.SelectedPath, true);
    }

    private void AddItem(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var name = isFolder
            ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))
            : System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) name = path;
        _items.Add(new QuickLaunchItem { Name = name, Path = path, IsFolder = isFolder });
        RenderList();
        RenderPreview();
    }

    // ---------- 图标模式 ----------
    private void ChkIconOnly_Click(object sender, RoutedEventArgs e)
    {
        _iconOnly = ChkIconOnly.IsChecked == true;
        RenderPreview();
    }

    private void SlIconSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载期设置 Minimum/Maximum 会触发本事件，此时 TxtIconSize 尚未实例化，需跳过
        if (TxtIconSize == null) return;
        _size = SlIconSize.Value;
        TxtIconSize.Text = ((int)_size) + " px";
        RenderPreview();
    }

    // ---------- 结果 ----------
    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.QuickLaunchers = new System.Collections.Generic.List<QuickLaunchItem>(_items);
            s.QlIconOnly = _iconOnly;
            s.QlIconSize = _size;
            s.QaBuiltinFontSize = _builtinSize;   // 0 = 自动（随悬浮课表宽度）
            Store.SaveSettings(s);
            DialogResult = true;
        }
        catch { DialogResult = false; }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
