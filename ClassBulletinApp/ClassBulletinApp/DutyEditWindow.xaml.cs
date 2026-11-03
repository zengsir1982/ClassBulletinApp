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
/// 值日生名单编辑对话框：卡片标题、主题风格、标题 / 内容字体样式，以及「星期 + 姓名 + 值日内容」清单。
/// <para>改动先在内存里编辑，点「确定」才落盘。</para>
/// </summary>
public partial class DutyEditWindow : Window
{
    /// <summary>星期下拉的选项（XAML 里用 <c>{x:Static}</c> 直接取，避免每行 ComboBox 各建一份集合）。</summary>
    public static string[] DayNameList => DutyItem.DayNames;

    private readonly DutyBoard _draft;
    private readonly ObservableCollection<DutyItem> _items = new();
    private string _theme = "green";
    private bool _loading;

    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
    }

    public DutyEditWindow()
    {
        InitializeComponent();

        _draft = Clone(Store.LoadDuty());

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
        _theme = string.IsNullOrWhiteSpace(_draft.Theme) ? "green" : _draft.Theme;
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);

        _draft.TitleStyle ??= new TextStyle { Bold = true };
        _draft.ContentStyle ??= new TextStyle();
        _draft.DayStyle ??= new TextStyle();
        _draft.DutyStyle ??= new TextStyle();
        PickTitleStyle.StyleData = _draft.TitleStyle;
        PickContentStyle.StyleData = _draft.ContentStyle;
        PickDayStyle.StyleData = _draft.DayStyle;
        PickDutyStyle.StyleData = _draft.DutyStyle;

        foreach (var it in _draft.SortedItems()) _items.Add(it);
        GridItems.ItemsSource = _items;

        _loading = false;
    }

    /// <summary>深拷贝一份草稿：取消时直接丢弃，磁盘上的数据一点没动。</summary>
    private static DutyBoard Clone(DutyBoard b) => new()
    {
        Title = b.Title,
        TitleStyle = (b.TitleStyle ?? new TextStyle { Bold = true }).Clone(),
        ContentStyle = (b.ContentStyle ?? new TextStyle()).Clone(),
        DayStyle = (b.DayStyle ?? new TextStyle()).Clone(),
        DutyStyle = (b.DutyStyle ?? new TextStyle()).Clone(),
        Theme = b.Theme,
        HighlightToday = b.HighlightToday,
        UpdatedAt = b.UpdatedAt,
        Items = (b.Items ?? new List<DutyItem>())
            .Select(x => new DutyItem { Day = x.Day, Duty = x.Duty, Names = x.Names })
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
        // 预览由 TextStylePicker 自己负责；保留钩子便于将来扩展
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var item = new DutyItem { Day = NextFreeDay(), Names = "" };
        _items.Add(item);
        GridItems.SelectedItem = item;
        GridItems.ScrollIntoView(item);
    }

    /// <summary>挑一个还没用过的星期（都用过了就默认周一）。</summary>
    private string NextFreeDay()
    {
        var used = new HashSet<string>(_items.Select(x => (x.Day ?? "").Trim()), StringComparer.Ordinal);
        foreach (var d in DutyItem.DayNames)
            if (!used.Contains(d)) return d;
        return "周一";
    }

    /// <summary>一键补齐周一到周五：教室里最常见的值日表就是这五天，省得一条条点。</summary>
    private void BtnTpl_Click(object sender, RoutedEventArgs e)
    {
        var used = new HashSet<string>(_items.Select(x => (x.Day ?? "").Trim()), StringComparer.Ordinal);
        int added = 0;
        foreach (var d in new[] { "周一", "周二", "周三", "周四", "周五" })
        {
            if (used.Contains(d)) continue;
            _items.Add(new DutyItem { Day = d, Names = "" });
            added++;
        }
        if (added == 0)
            MessageBox.Show("周一到周五都已经有了。", "值日生名单", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnDel_Click(object sender, RoutedEventArgs e)
    {
        if (GridItems.SelectedItem is not DutyItem sel) { MessageBox.Show("请先在列表里选中要删除的那一条。", "值日生名单"); return; }
        _items.Remove(sel);
    }

    // ---------- 保存 ----------
    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 列表可能正在编辑中：先提交单元格，否则最后敲的一格会丢
            GridItems.CommitEdit(DataGridEditingUnit.Cell, true);
            GridItems.CommitEdit(DataGridEditingUnit.Row, true);

            var board = new DutyBoard
            {
                Title = (TxtTitle.Text ?? "").Trim(),
                TitleStyle = (_draft.TitleStyle ?? new TextStyle { Bold = true }).Clone(),
                ContentStyle = (_draft.ContentStyle ?? new TextStyle()).Clone(),
                DayStyle = (_draft.DayStyle ?? new TextStyle()).Clone(),
                DutyStyle = (_draft.DutyStyle ?? new TextStyle()).Clone(),
                Theme = _theme,
                HighlightToday = ChkHighlight.IsChecked == true,
                Items = _items.ToList(),
                UpdatedAt = DateTime.Now
            };

            Store.SaveDuty(board);

            // 已经画在桌面壁纸上的话，立即重绘（否则壁纸停在旧名单上）
            if (Store.LoadSettings().Backdrop?.ShowDuty == true)
                BackdropService.Apply(silent: true, out _);

            AppEvents.RaiseDutyChanged();
            AppEvents.RaiseBackdropChanged();   // 值日名单可能已画在壁纸上，内容变了要重绘
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存值日生名单失败：" + ex.Message, "值日生名单", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
