using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms（均有同名类型），此处用别名消歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;

namespace ClassBulletinApp;

/// <summary>
/// 倒计时便签编辑对话框：卡片标题、主题风格、标题 / 条目字体样式，以及事件清单
/// （事件名 + 目标日期 + 备注），右侧自动算出「还有 N 天」。
/// <para>改动先在内存里编辑，点「确定」才落盘 —— 取消不该把用户原来的倒计时弄乱。</para>
/// </summary>
public partial class CountdownEditWindow : Window
{
    private readonly CountdownBoard _draft;
    private readonly ObservableCollection<CountdownItem> _items = new();
    private string _theme = "classic";
    private bool _loading;

    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
    }

    public CountdownEditWindow()
    {
        InitializeComponent();

        _draft = Clone(Store.LoadCountdown());

        _loading = true;
        CbTheme.ItemsSource = HomeworkThemes.Presets
            .Select(t => new ThemeOption
            {
                Key = t.Key,
                Name = t.Name,
                Background = ThemeHelper.Brush(t.Background, Colors.White)
            })
            .ToList();

        TxtTitle.Text = _draft.Title ?? "";
        ChkHighlight.IsChecked = _draft.HighlightToday;
        _theme = string.IsNullOrWhiteSpace(_draft.Theme) ? "classic" : _draft.Theme;
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);

        // 样式控件直接绑草稿对象：改一下立刻反映到右侧预览
        _draft.TitleStyle ??= new TextStyle { Bold = true };
        _draft.ContentStyle ??= new TextStyle();
        _draft.DaysStyle ??= new TextStyle();
        PickTitleStyle.StyleData = _draft.TitleStyle;
        PickContentStyle.StyleData = _draft.ContentStyle;
        PickDaysStyle.StyleData = _draft.DaysStyle;

        foreach (var it in _draft.SortedItems()) _items.Add(it);
        GridItems.ItemsSource = _items;

        _loading = false;
    }

    /// <summary>深拷贝一份草稿：取消时直接丢弃，磁盘上的数据一点没动。</summary>
    private static CountdownBoard Clone(CountdownBoard b) => new()
    {
        Title = b.Title,
        TitleStyle = (b.TitleStyle ?? new TextStyle { Bold = true }).Clone(),
        ContentStyle = (b.ContentStyle ?? new TextStyle()).Clone(),
        DaysStyle = (b.DaysStyle ?? new TextStyle()).Clone(),
        Theme = b.Theme,
        HighlightToday = b.HighlightToday,
        UpdatedAt = b.UpdatedAt,
        Items = (b.Items ?? new List<CountdownItem>())
            .Select(x => new CountdownItem { Name = x.Name, TargetDate = x.TargetDate, Note = x.Note })
            .ToList()
    };

    // ---------- 事件 ----------
    private void CbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbTheme.SelectedItem is ThemeOption opt) _theme = opt.Key;
    }

    private void Style_Changed(object? sender, EventArgs e)
    {
        // 预览由 TextStylePicker 自己负责；这里什么都不用做，保留钩子便于将来扩展
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var item = new CountdownItem { Name = "新事件", TargetDate = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd") };
        _items.Add(item);
        GridItems.SelectedItem = item;
        GridItems.ScrollIntoView(item);
    }

    private void BtnDel_Click(object sender, RoutedEventArgs e)
    {
        if (GridItems.SelectedItem is not CountdownItem sel) { MessageBox.Show("请先在列表里选中要删除的那一条。", "倒计时"); return; }
        _items.Remove(sel);
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void BtnDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (GridItems.SelectedItem is not CountdownItem sel) return;
        int i = _items.IndexOf(sel);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        GridItems.SelectedItem = sel;
        GridItems.ScrollIntoView(sel);
    }

    // ---------- 保存 ----------
    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 列表可能正在编辑中：先提交单元格，否则最后敲的一格会丢
            GridItems.CommitEdit(DataGridEditingUnit.Cell, true);
            GridItems.CommitEdit(DataGridEditingUnit.Row, true);

            var board = new CountdownBoard
            {
                Title = (TxtTitle.Text ?? "").Trim(),
                TitleStyle = (_draft.TitleStyle ?? new TextStyle { Bold = true }).Clone(),
                ContentStyle = (_draft.ContentStyle ?? new TextStyle()).Clone(),
                DaysStyle = (_draft.DaysStyle ?? new TextStyle()).Clone(),
                Theme = _theme,
                HighlightToday = ChkHighlight.IsChecked == true,
                Items = _items.ToList(),
                UpdatedAt = DateTime.Now
            };

            Store.SaveCountdown(board);

            // 已经画在桌面壁纸上的话，立即重绘（否则壁纸停在旧天数 / 旧事件上）
            if (Store.LoadSettings().Backdrop?.ShowCountdown == true)
                BackdropService.Apply(silent: true, out _);

            AppEvents.RaiseCountdownChanged();
            AppEvents.RaiseBackdropChanged();   // 倒计时可能已画在壁纸上，内容变了要重绘
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存倒计时失败：" + ex.Message, "倒计时", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>手写输入兜底入口（悬浮球被隐藏时用）：识别结果插回最后点过的输入框。</summary>
    private void BtnHand_Click(object sender, RoutedEventArgs e) => HandwritingService.Open(this);
}
