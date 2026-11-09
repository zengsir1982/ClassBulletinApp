using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassBulletinApp.Controls;
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
    private FocusStyleBinder? _binder;

    /// <summary>诊断用：验证「焦点 → 样式对象」映射（<c>--ui-check</c>）。</summary>
    internal FocusStyleBinder? DiagBinder => _binder;

    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
    }

    /// <summary>编辑指定便签（已知便签对象时用此构造）。</summary>
    public CustomNoteEditWindow(CustomNote note)
    {
        _src = note;
        InitializeComponent();
        InitCommon();
        LoadNote(note);
    
        GlassChrome.Prepare(this);}

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
    
        GlassChrome.Prepare(this);}

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

        // ★ r69 焦点跟随：委托取 _src 的当前样式对象，切换便签后自动指向新便签，不用重绑。
        _binder = new FocusStyleBinder(this, PickStyle);
        _binder.Bind(TxtTitle, () => _src.TitleStyle, "便签标题");
        _binder.Bind(TxtContent, () => _src.ContentStyle, "便签内容");

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

        // 标题 / 内容样式：老数据没有这两个对象时补「特大 + 默认墨色」的默认值
        note.TitleStyle ??= new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };
        note.ContentStyle ??= new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel };
        // 换了便签：作用对象变了，先置灰，等老师点下一个输入框再激活（不悄悄回退到标题）
        _binder?.Deactivate();

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
        TitleStyle = (_src.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        Content = (TxtContent.Text ?? "").Trim(),
        Theme = _theme,
        Locked = ChkLocked.IsChecked == true,
        Bold = _src.ContentStyle?.Bold ?? false,
        Color = _src.ContentStyle?.Color ?? "",
        SizeLevel = _src.ContentStyle?.SizeLevel ?? 0,
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

    /// <summary>样式控件改动后，把内容输入框的字体同步成最新（所见即所得）。</summary>
    private void Style_Changed(object? sender, EventArgs e) => ApplyFormatToEditor();

    /// <summary>把样式设置即时应用到内容输入框本身。</summary>
    private void ApplyFormatToEditor()
    {
        if (TxtContent == null) return;

        var cs = _src?.ContentStyle ?? new TextStyle();
        TxtContent.FontWeight = cs.Bold ? FontWeights.Bold : FontWeights.Normal;
        TxtContent.FontSize = Math.Max(10, HwTextStyle.SizeOf(cs.SizeLevel) - 4);   // 输入框比便签略小一点，便于排版

        var theme = HomeworkThemes.Find(_theme);
        var def = ThemeHelper.Brush(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        TxtContent.Foreground = string.IsNullOrWhiteSpace(cs.Color)
            ? def
            : ThemeHelper.Brush(cs.Color, Color.FromRgb(0x33, 0x3B, 0x47));
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
