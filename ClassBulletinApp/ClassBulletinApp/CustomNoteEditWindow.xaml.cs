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

    // ---- 只改格式（供涂鸦板的自动便签复用本窗） ----
    private readonly bool _formatOnly;
    private readonly double _boardTitleFont;
    private readonly double _boardBodyFont;

    /// <summary>确定后带回的标题格式（仅格式模式有意义）。</summary>
    public TextStyle? ResultTitleStyle { get; private set; }

    /// <summary>确定后带回的正文格式（仅格式模式有意义）。</summary>
    public TextStyle? ResultContentStyle { get; private set; }

    /// <summary>诊断用：验证「焦点 → 样式对象」映射（<c>--ui-check</c>）。</summary>
    internal FocusStyleBinder? DiagBinder => _binder;

    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public SolidColorBrush Background { get; init; } = Brushes.White;
    }

    /// <summary>编辑指定便签（已知便签对象时用此构造）。</summary>
    public CustomNoteEditWindow(CustomNote note) : this(note, false, 0, 0) { }

    /// <summary>
    /// <paramref name="formatOnly"/> = true 时只改字体格式：便签选择 / 新建 / 删除 / 主题 / 锁定整行隐藏，
    /// 标题与内容变成只读，确定时不写 settings.json，只把两份 <see cref="TextStyle"/> 带回。
    /// <para>给涂鸦板的「自动便签双击改格式」用：老师要的是同一个熟悉的编辑窗和同一条格式条，
    /// 但自动便签的文字属于通知 / 作业，不能在这个窗里被改掉。</para>
    /// <para><paramref name="boardTitleFont"/> / <paramref name="boardBodyFont"/> 是预览用的基准字号：
    /// 传 0 就用本窗原来的固定预览字号。</para>
    /// </summary>
    public CustomNoteEditWindow(CustomNote note, bool formatOnly,
        double boardTitleFont = 0, double boardBodyFont = 0)
    {
        _src = note;
        _formatOnly = formatOnly;
        _boardTitleFont = boardTitleFont;
        _boardBodyFont = boardBodyFont;

        InitializeComponent();
        InitCommon();
        LoadNote(note);
        if (_formatOnly) ApplyFormatOnlyMode();

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

    /// <summary>
    /// 「只改格式」模式的界面收窄：去掉便签切换 / 新建 / 删除 / 主题 / 锁定，
    /// 文字改成只读，标题与提示改成说明当前这张便签是从哪来的。
    /// </summary>
    private void ApplyFormatOnlyMode()
    {
        Title = "自动便签 · 文字格式";

        // 主题、锁定、切换便签对一个"从通知拉来的只读便签"全都不成立
        RowPicker.Visibility = Visibility.Collapsed;
        if (CbNote != null) CbNote.Visibility = Visibility.Collapsed;

        TxtTitle.IsReadOnly = true;
        TxtContent.IsReadOnly = true;
        TxtTitle.ToolTip = TxtContent.ToolTip = "自动便签的文字来自通知 / 作业，请到对应条目去改";
        TxtTitle.Background = TxtContent.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));

        TxtHint.Text = "文字来自通知 / 作业 / 缺交，这里只改格式：点标题或内容，上面的格式条就改那一栏。";

        // 打开即可改：默认把格式条挂到正文（最常改的就是正文的粗细和颜色）。
        // 不这么做的话格式条是灰的，老师会以为"这个窗不能改格式"。
        _binder?.Activate(_src?.ContentStyle, "便签内容");

        // ★ 光调 Activate 不够：窗口一显示、焦点若落到别的元素上，binder 会立刻判"焦点丢失"再置灰。
        // 真正的解法是让正文框真拿到键盘焦点（只读 TextBox 也能拿焦点，只是不能改字）。
        Loaded += (_, _) =>
        {
            TxtContent.Focus();
            TxtContent.CaretIndex = 0;
        };
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

        // ★ 格式模式下绝不落盘：这张"便签"是涂鸦板临时构造的，写进 CustomNotes
        // 会在桌面上凭空多出一张重复的悬浮便签。
        if (_formatOnly) return;

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

        // ★ 字号按"在这个宿主里实际显示多大"来定：
        //   悬浮便签宿主（默认）＝档位绝对值减 4（输入框比便签略小，便于排版）；
        //   涂鸦板宿主（格式模式）＝板的正文字号 × 档位倍率 —— 否则板上 13 号字，
        //   编辑窗里却按 32 号显示，老师会以为"我在板上看到的和这里不一样，是不是没生效"。
        if (_boardBodyFont > 0)
        {
            double refSize = HwTextStyle.SizeOf(HwTextStyle.ExtraLargeLevel);
            double scale = refSize > 0 ? HwTextStyle.SizeOf(cs.SizeLevel) / refSize : 1;
            TxtContent.FontSize = Math.Clamp(_boardBodyFont * scale, 10, 34);
        }
        else
        {
            TxtContent.FontSize = Math.Max(10, HwTextStyle.SizeOf(cs.SizeLevel) - 4);
        }

        var theme = HomeworkThemes.Find(_theme);
        var def = ThemeHelper.Brush(theme.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
        TxtContent.Foreground = string.IsNullOrWhiteSpace(cs.Color)
            ? def
            : ThemeHelper.Brush(cs.Color, Color.FromRgb(0x33, 0x3B, 0x47));

        ApplyFormatToPreview(def);
    }

    /// <summary>
    /// 预览块也套用当前格式。
    /// <para>★ 原来预览只跟着文字变、不跟着格式变，老师在格式条上改半天，上面的"便签预览"
    /// 一点反应都没有 —— 那等于没有预览。字号按调用方给的基准字号缩放，
    /// 这样板上的 13 号字和悬浮便签的 27 号字都能在同一块小预览里如实体现。</para>
    /// </summary>
    private void ApplyFormatToPreview(SolidColorBrush themeForeground)
    {
        if (PreviewTitle == null || PreviewBody == null) return;

        double baseSize = _boardTitleFont > 0 ? _boardTitleFont : 16;
        double baseBody = _boardBodyFont > 0 ? _boardBodyFont : 13;
        double refSize = HwTextStyle.SizeOf(HwTextStyle.ExtraLargeLevel);

        var ts = _src?.TitleStyle;
        var cs = _src?.ContentStyle;

        double titleScale = ts == null || refSize <= 0 ? 1 : HwTextStyle.SizeOf(ts.SizeLevel) / refSize;
        double bodyScale = cs == null || refSize <= 0 ? 1 : HwTextStyle.SizeOf(cs.SizeLevel) / refSize;

        PreviewTitle.FontSize = Math.Clamp(baseSize * titleScale, 10, 40);
        PreviewTitle.FontWeight = (ts?.Bold ?? true) ? FontWeights.Bold : FontWeights.Normal;
        PreviewTitle.Foreground = string.IsNullOrWhiteSpace(ts?.Color)
            ? themeForeground
            : ThemeHelper.Brush(ts!.Color, Color.FromRgb(0x33, 0x3B, 0x47));

        PreviewBody.FontSize = Math.Clamp(baseBody * bodyScale, 9, 34);
        PreviewBody.FontWeight = (cs?.Bold ?? false) ? FontWeights.Bold : FontWeights.Normal;
        PreviewBody.Foreground = string.IsNullOrWhiteSpace(cs?.Color)
            ? themeForeground
            : ThemeHelper.Brush(cs!.Color, Color.FromRgb(0x33, 0x3B, 0x47));
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
        if (_formatOnly)
        {
            ResultTitleStyle = (_src?.TitleStyle ?? new TextStyle()).Clone();
            ResultContentStyle = (_src?.ContentStyle ?? new TextStyle()).Clone();
            DialogResult = true;
            return;
        }

        CommitCurrent();
        AppEvents.RaiseCustomNoteChanged(_src?.Id);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 诊断用：走一遍「确定」的处理（不回 DialogResult —— 非模态窗口上设它会抛异常）。
    /// 用来验证两件事：格式能带回，且<b>没有</b>往 settings.CustomNotes 里塞一条假便签。
    /// </summary>
    internal void DiagCommitAsOk()
    {
        ResultTitleStyle = (_src?.TitleStyle ?? new TextStyle()).Clone();
        ResultContentStyle = (_src?.ContentStyle ?? new TextStyle()).Clone();
    }
}
