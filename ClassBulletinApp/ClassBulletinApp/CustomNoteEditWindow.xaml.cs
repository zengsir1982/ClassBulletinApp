using System;
using System.Linq;
using System.Collections.Generic;
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
/// 自定义便签编辑对话框：新建 / 选择已有便签，修改标题、正文、文字格式（加粗 / 字色 / 字号）、
/// 主题风格与锁定。所有改动即时写入 settings.json（切换便签或点确定时落盘）。
/// </summary>
public partial class CustomNoteEditWindow : Window
{
    private CustomNote _src;
    private string _theme = "plain";
    private bool _loading;

    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
    }

    private sealed class ColorOption
    {
        public string Value { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Swatch { get; init; } = Brushes.Black;
    }

    /// <summary>编辑指定便签（已知便签对象时用此构造）。</summary>
    public CustomNoteEditWindow(CustomNote note)
    {
        _src = note;
        InitializeComponent();
        InitCommon();
        LoadNote(note);
    }

    /// <summary>按便签 Id 编辑（控制面板入口：自动带出全部便签供选择）。</summary>
    public CustomNoteEditWindow(string noteId)
    {
        var notes = Store.LoadSettings().CustomNotes ?? new List<CustomNote>();
        _src = notes.FirstOrDefault(x => x.Id == noteId)
               ?? notes.FirstOrDefault()
               ?? new CustomNote();
        InitializeComponent();
        InitCommon();
        ReloadNoteList();
        var target = notes.FirstOrDefault(x => x.Id == noteId) ?? _src;
        LoadNote(target);
        SelectNoteInList(target.Id);
    }

    // ---------- 初始化 ----------
    private void InitCommon()
    {
        _loading = true;

        CbTheme.ItemsSource = HomeworkThemes.Presets
            .Select(t => new ThemeOption
            {
                Key = t.Key,
                Name = t.Name,
                Background = ThemeHelper.Brush(t.Background, Colors.White)
            })
            .ToList();

        CbColor.ItemsSource = HwTextStyle.PresetColors
            .Select(c => new ColorOption
            {
                Value = c.Value,
                Name = c.Name,
                Swatch = string.IsNullOrEmpty(c.Value)
                    ? new SolidColorBrush(Color.FromRgb(0x33, 0x3B, 0x47))
                    : ThemeHelper.Brush(c.Value, Color.FromRgb(0x33, 0x3B, 0x47))
            })
            .ToList();

        CbSize.ItemsSource = HwTextStyle.LevelNames.ToList();

        _loading = false;
    }

    private void LoadNote(CustomNote note)
    {
        _loading = true;
        _src = note;
        _theme = string.IsNullOrWhiteSpace(note.Theme) ? "plain" : note.Theme;

        TxtTitle.Text = note.Title ?? "";
        TxtContent.Text = note.Content ?? "";
        ChkLocked.IsChecked = note.Locked;
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);

        ChkBold.IsChecked = note.Bold;
        int ci = 0;
        for (int i = 0; i < HwTextStyle.PresetColors.Length; i++)
            if (string.Equals(HwTextStyle.PresetColors[i].Value, note.Color ?? "", StringComparison.OrdinalIgnoreCase)) { ci = i; break; }
        CbColor.SelectedIndex = ci;
        CbSize.SelectedIndex = Math.Clamp(note.SizeLevel, 0, HwTextStyle.LevelNames.Length - 1);

        _loading = false;
        ApplyFormatToEditor();
    }

    private void ReloadNoteList()
    {
        if (CbNote == null) return;
        var notes = Store.LoadSettings().CustomNotes ?? new List<CustomNote>();
        CbNote.Items.Clear();
        foreach (var n in notes)
        {
            CbNote.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(n.Title) ? "（未命名便签）" : n.Title,
                Tag = n.Id
            });
        }
        BtnDeleteNote.IsEnabled = notes.Count > 0;
    }

    private void SelectNoteInList(string id)
    {
        if (CbNote == null) return;
        for (int i = 0; i < CbNote.Items.Count; i++)
        {
            if ((CbNote.Items[i] as ComboBoxItem)?.Tag as string == id)
            {
                _loading = true;
                CbNote.SelectedIndex = i;
                _loading = false;
                return;
            }
        }
    }

    /// <summary>把当前编辑内容写回 settings.json（切换便签 / 点确定时调用）。</summary>
    private void CommitCurrent()
    {
        if (_src == null) return;
        try
        {
            var saved = BuildNote();
            var s = Store.LoadSettings();
            int idx = s.CustomNotes.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) s.CustomNotes[idx] = saved;
            else s.CustomNotes.Add(saved);
            Store.SaveSettings(s);
            _src = saved;
        }
        catch { }
    }

    /// <summary>按当前编辑内容生成便签数据（保留 Id / 摆放 / 显示等运行态字段）。</summary>
    public CustomNote BuildNote() => new()
    {
        Id = _src.Id,
        Title = (TxtTitle.Text ?? "").Trim(),
        Content = (TxtContent.Text ?? "").Trim(),
        Theme = _theme,
        Locked = ChkLocked.IsChecked == true,
        Bold = ChkBold.IsChecked == true,
        Color = (CbColor.SelectedItem as ColorOption)?.Value ?? "",
        SizeLevel = Math.Max(0, CbSize.SelectedIndex),
        UpdatedAt = DateTime.Now,
        Left = _src.Left,
        Top = _src.Top,
        Width = _src.Width,
        Height = _src.Height,
        TopMost = _src.TopMost,
        Visible = _src.Visible
    };

    // ---------- 事件 ----------
    private void CbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbTheme.SelectedItem is ThemeOption opt) _theme = opt.Key;
        ApplyFormatToEditor();
    }

    private void Format_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyFormatToEditor();
    }

    /// <summary>把格式设置即时应用到内容输入框本身，所见即所得。</summary>
    private void ApplyFormatToEditor()
    {
        if (TxtContent == null || ChkBold == null || CbColor == null || CbSize == null) return;

        TxtContent.FontWeight = ChkBold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        TxtContent.FontSize = HwTextStyle.SizeOf(CbSize.SelectedIndex) - 4;   // 输入框比便签略小一点，便于排版

        var opt = CbColor.SelectedItem as ColorOption;
        var theme = HomeworkThemes.Find(_theme);
        var def = ThemeHelper.Brush(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        TxtContent.Foreground = opt == null || string.IsNullOrEmpty(opt.Value)
            ? def
            : ThemeHelper.Brush(opt.Value, Color.FromRgb(0x33, 0x3B, 0x47));
    }

    private void CbNote_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CbNote?.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string id || id == _src?.Id) return;

        CommitCurrent();                       // 先保存正在编辑的那张
        AppEvents.RaiseCustomNoteChanged(_src?.Id);

        var notes = Store.LoadSettings().CustomNotes ?? new List<CustomNote>();
        var next = notes.FirstOrDefault(x => x.Id == id);
        if (next == null) return;
        LoadNote(next);
    }

    private void BtnNewNote_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitCurrent();
            var note = new CustomNote
            {
                Title = "自定义便签",
                Content = "",
                Theme = _theme,
                Visible = true,
                UpdatedAt = DateTime.Now
            };
            var s = Store.LoadSettings();
            s.CustomNotes.Add(note);
            Store.SaveSettings(s);

            ReloadNoteList();
            LoadNote(note);
            SelectNoteInList(note.Id);
            AppEvents.RaiseCustomNoteChanged(note.Id);
            TxtTitle.Focus();
            TxtTitle.SelectAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show("新建便签失败：" + ex.Message, "自定义便签", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_src == null) return;
        var name = string.IsNullOrWhiteSpace(_src.Title) ? "这张便签" : "「" + _src.Title + "」";
        if (MessageBox.Show("确定删除" + name + "吗？此操作不可撤销。", "删除自定义便签",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var s = Store.LoadSettings();
            s.CustomNotes.RemoveAll(x => x.Id == _src.Id);
            Store.SaveSettings(s);
            AppEvents.RaiseCustomNoteChanged(_src.Id);

            var rest = s.CustomNotes;
            if (rest.Count == 0)
            {
                DialogResult = false;      // 没有便签了，直接关闭
                return;
            }
            ReloadNoteList();
            LoadNote(rest[0]);
            SelectNoteInList(rest[0].Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败：" + ex.Message, "自定义便签", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        CommitCurrent();
        AppEvents.RaiseCustomNoteChanged(_src?.Id);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
