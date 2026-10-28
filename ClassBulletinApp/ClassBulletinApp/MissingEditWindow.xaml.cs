using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
// 项目同时引用 WPF 与 WinForms（均有同名类型），此处用别名消歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 缺交作业名单编辑对话框：修改内容 / 文字格式（加粗 / 颜色 / 字号）/ 主题风格。
/// 便签悬浮窗双击、控制面板「缺交名单」区块都可打开它；确定后立即保存并同步桌面背景便签。
/// </summary>
public partial class MissingEditWindow : Window
{
    private readonly ObservableCollection<MissingHomeworkItem> _miss = new();
    private string _theme = "pink";

    /// <summary>供下拉使用的主题选项（携带已解析好的画刷，避免 XAML 里做类型转换）。</summary>
    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
        public HomeworkTheme Theme { get; init; } = new();
    }

    public MissingEditWindow(MissingBoard board)
    {
        InitializeComponent();

        _theme = string.IsNullOrWhiteSpace(board.Theme) ? "pink" : board.Theme;
        TxtTitle.Text = string.IsNullOrWhiteSpace(board.Title) ? "缺交作业名单" : board.Title;

        foreach (var m in board.Items ?? new()) _miss.Add(Clone(m));
        if (_miss.Count == 0) _miss.Add(new MissingHomeworkItem());
        MissItems.ItemsSource = _miss;

        CbTheme.ItemsSource = HomeworkThemes.Presets
            .Select(t => new ThemeOption
            {
                Key = t.Key,
                Name = t.Name,
                Background = ThemeHelper.Brush(t.Background, System.Windows.Media.Colors.White),
                Theme = t
            })
            .ToList();
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);
        ApplyPreview(HomeworkThemes.Find(_theme));
    }

    private static MissingHomeworkItem Clone(MissingHomeworkItem m) => new()
    {
        Subject = m.Subject, Students = m.Students,
        Bold = m.Bold, Color = m.Color, SizeLevel = m.SizeLevel
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
        PreviewCard.Background = ThemeHelper.Brush(t.Background, System.Windows.Media.Colors.White);
        PreviewCard.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xE3, 0xE8, 0xEF));
        PreviewTitle.Foreground = ThemeHelper.Brush(t.TitleColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        PreviewLabel.Foreground = ThemeHelper.Brush(t.MissingColor, Color.FromRgb(0xC0, 0x39, 0x2B));
        PreviewBody.Foreground = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
    }

    // ---------- 增删 ----------
    private void BtnAddMiss_Click(object sender, RoutedEventArgs e) => _miss.Add(new MissingHomeworkItem());

    private void BtnDelMiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is MissingHomeworkItem m) _miss.Remove(m);
        if (_miss.Count == 0) _miss.Add(new MissingHomeworkItem());
    }

    // ---------- 结果 ----------
    public MissingBoard BuildBoard() => new()
    {
        Title = (TxtTitle.Text ?? "").Trim(),
        Items = _miss
            .Where(m => !string.IsNullOrWhiteSpace(m.Subject) || !string.IsNullOrWhiteSpace(m.Students))
            .Select(m => new MissingHomeworkItem
            {
                Subject = (m.Subject ?? "").Trim(),
                Students = (m.Students ?? "").Trim(),
                Bold = m.Bold,
                Color = m.Color ?? "",
                SizeLevel = m.SizeLevel
            })
            .ToList(),
        Theme = _theme,
        UpdatedAt = DateTime.Now
    };

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
