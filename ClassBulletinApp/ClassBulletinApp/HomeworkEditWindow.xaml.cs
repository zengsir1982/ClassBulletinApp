using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用 WPF 与 WinForms（均有同名类型），此处用别名消歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 作业公示编辑对话框：集中修改「内容 / 格式 / 主题风格」。
/// 便签悬浮窗双击、控制面板作业标签都可打开它；确定后保存并同步桌面背景。
/// 缺交作业名单已独立为 <see cref="MissingEditWindow"/> / <see cref="MissingWindow"/>。
/// </summary>
public partial class HomeworkEditWindow : Window
{
    private readonly ObservableCollection<HomeworkItem> _items = new();
    private string _theme = "classic";

    /// <summary>供下拉使用的主题选项（携带已解析好的画刷，避免 XAML 里做类型转换）。</summary>
    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
        public HomeworkTheme Theme { get; init; } = new();
    }

    public HomeworkEditWindow(HomeworkBoard board)
    {
        InitializeComponent();

        _theme = string.IsNullOrWhiteSpace(board.Theme) ? "classic" : board.Theme;
        TxtTitle.Text = string.IsNullOrWhiteSpace(board.Title) ? "各科作业公示" : board.Title;

        foreach (var it in board.Items ?? new()) _items.Add(Clone(it));
        if (_items.Count == 0) _items.Add(new HomeworkItem());

        HwItems.ItemsSource = _items;

        CbTheme.ItemsSource = HomeworkThemes.Presets
            .Select(t => new ThemeOption
            {
                Key = t.Key,
                Name = t.Name,
                Background = ThemeHelper.Brush(t.Background, Colors.White),
                Theme = t
            })
            .ToList();
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);
        ApplyPreview(HomeworkThemes.Find(_theme));
    }

    private static HomeworkItem Clone(HomeworkItem x) => new()
    {
        Subject = x.Subject, Content = x.Content,
        Bold = x.Bold, Color = x.Color, SizeLevel = x.SizeLevel
    };

    // ---------- 主题 ----------
    private void CbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbTheme.SelectedItem is ThemeOption opt)
        {
            _theme = opt.Key;
            ApplyPreview(opt.Theme);
        }
    }

    private void ApplyPreview(HomeworkTheme t)
    {
        PreviewCard.Background = ThemeHelper.Brush(t.Background, Colors.White);
        PreviewCard.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xE3, 0xE8, 0xEF));
        PreviewTitle.Foreground = ThemeHelper.Brush(t.TitleColor, Color.FromRgb(0x1F, 0x2A, 0x37));
        PreviewSubject.Foreground = ThemeHelper.Brush(t.Accent, Color.FromRgb(0x2D, 0x6C, 0xDF));
        PreviewBody.Foreground = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
    }

    // ---------- 增删 ----------
    private void BtnAddHw_Click(object sender, RoutedEventArgs e) => _items.Add(new HomeworkItem());

    private void BtnDelHw_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is HomeworkItem it) _items.Remove(it);
        if (_items.Count == 0) _items.Add(new HomeworkItem());
    }

    // ---------- 结果 ----------
    /// <summary>按当前编辑内容生成作业公示数据（布局沿用设置中的桌面背景位置）。</summary>
    public HomeworkBoard BuildBoard() => new()
    {
        Title = (TxtTitle.Text ?? "").Trim(),
        Items = _items
            .Where(x => !string.IsNullOrWhiteSpace(x.Subject) || !string.IsNullOrWhiteSpace(x.Content))
            .Select(x => new HomeworkItem
            {
                Subject = (x.Subject ?? "").Trim(),
                Content = (x.Content ?? "").Trim(),
                Bold = x.Bold,
                Color = x.Color ?? "",
                SizeLevel = x.SizeLevel
            })
            .ToList(),
        Missing = Store.LoadHomework()?.Missing ?? new(),   // 缺交名单已独立存储，这里原样带回避免覆盖丢失
        Theme = _theme,
        UpdatedAt = DateTime.Now,
        Layout = Store.LoadSettings().Backdrop?.HomeworkLayout ?? new HomeworkWallpaperLayout()
    };

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
