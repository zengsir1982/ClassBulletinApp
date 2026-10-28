using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
// 项目同时引用了 WPF 与 WinForms（均有同名类型），此处用别名消歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 自定义便签编辑对话框：修改标题 / 正文 / 主题风格 / 锁定。
/// </summary>
public partial class CustomNoteEditWindow : Window
{
    private readonly CustomNote _src;
    private string _theme = "plain";

    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
    }

    public CustomNoteEditWindow(CustomNote note)
    {
        _src = note;
        InitializeComponent();

        _theme = string.IsNullOrWhiteSpace(note.Theme) ? "plain" : note.Theme;
        TxtTitle.Text = note.Title ?? "";
        TxtContent.Text = note.Content ?? "";
        ChkLocked.IsChecked = note.Locked;

        CbTheme.ItemsSource = HomeworkThemes.Presets
            .Select(t => new ThemeOption
            {
                Key = t.Key,
                Name = t.Name,
                Background = ThemeHelper.Brush(t.Background, Colors.White)
            })
            .ToList();
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);
    }

    /// <summary>按当前编辑内容生成便签数据（保留 Id / 摆放 / 显示等运行态字段）。</summary>
    public CustomNote BuildNote() => new()
    {
        Id = _src.Id,
        Title = (TxtTitle.Text ?? "").Trim(),
        Content = (TxtContent.Text ?? "").Trim(),
        Theme = _theme,
        Locked = ChkLocked.IsChecked == true,
        UpdatedAt = DateTime.Now,
        Left = _src.Left,
        Top = _src.Top,
        Width = _src.Width,
        Height = _src.Height,
        TopMost = _src.TopMost,
        Visible = _src.Visible
    };

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
