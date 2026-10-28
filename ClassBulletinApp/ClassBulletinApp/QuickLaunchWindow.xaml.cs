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

    public QuickLaunchWindow()
    {
        InitializeComponent();
        var s = Store.LoadSettings();
        foreach (var it in s.QuickLaunchers ?? new System.Collections.Generic.List<QuickLaunchItem>())
            _items.Add(it);
        _iconOnly = s.QlIconOnly;
        _size = s.QlIconSize > 0 ? s.QlIconSize : 28;

        ChkIconOnly.IsChecked = _iconOnly;
        SlIconSize.Value = _size;
        TxtIconSize.Text = ((int)_size) + " px";

        RenderList();
        RenderPreview();
    }

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
            Store.SaveSettings(s);
            DialogResult = true;
        }
        catch { DialogResult = false; }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
