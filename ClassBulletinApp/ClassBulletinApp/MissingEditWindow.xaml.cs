using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ClassBulletinApp.Controls;
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

    /// <summary>被删掉的旧记录，保存时以墓碑形式写回（物理删除会被对端旧变更单复活）。</summary>
    private readonly List<MissingHomeworkItem> _tombstones = new();

    /// <summary>打开时的原始板，用来保留 ClassKey / SchemaRev。</summary>
    private readonly MissingBoard _source;

    private string _theme = "pink";

    /// <summary>总标题的样式（编辑期副本，确定时才写回）。</summary>
    private readonly TextStyle _titleStyle = new();

    /// <summary>r69：顶部唯一一条工具条，按焦点切换作用对象。</summary>
    private FocusStyleBinder? _binder;

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

        _source = board ?? new MissingBoard();
        _theme = string.IsNullOrWhiteSpace(_source.Theme) ? "pink" : _source.Theme;
        TxtTitle.Text = string.IsNullOrWhiteSpace(_source.Title) ? "缺交作业名单" : _source.Title;

        _titleStyle.CopyFrom(_source.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel });

        // 墓碑不进编辑列表，但必须原样带回，否则保存一次就把删除标记丢了
        foreach (var m in _source.Items ?? new())
        {
            if (m.Deleted) _tombstones.Add(Clone(m));
            else _miss.Add(Clone(m));
        }
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
        ChkLocked.IsChecked = NoteLock.Load("missing");
        ApplyPreview(HomeworkThemes.Find(_theme));

        // ★ r69 焦点跟随：总标题静态绑定；列表里每条的科目 / 名单在 GotFocus 时动态登记。
        _binder = new FocusStyleBinder(this, PickStyle);
        _binder.Bind(TxtTitle, () => _titleStyle, "总标题");
    
        GlassChrome.Prepare(this);}

    private void MsSubject_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MissingHomeworkItem it) return;
        _binder?.BindAndActivate(fe, it.SubjectStyle, "科目");
    }

    private void MsStudents_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MissingHomeworkItem it) return;
        _binder?.BindAndActivate(fe, it.ContentStyle, "缺交名单");
    }

    // 老数据里样式对象可能为 null：兜底也用「特大」，避免新建名单忽然缩回标准档。
    private static TextStyle CopyStyle(TextStyle? s)
        => (s ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone();

    /// <summary>编辑期副本。<b>必须带上 Id / 墓碑 / 来源 / 派生关系 / 字段版本</b>，
    /// 否则用户点一次确定，所有同步身份字段就全丢了。</summary>
    private static MissingHomeworkItem Clone(MissingHomeworkItem m) => new()
    {
        Id = m.Id, Deleted = m.Deleted, Origin = m.Origin, Parent = m.Parent,
        FieldHlc = m.FieldHlc == null ? new() : new Dictionary<string, string>(m.FieldHlc),
        Subject = m.Subject, Students = m.Students,
        Bold = m.Bold, Color = m.Color, SizeLevel = m.SizeLevel,
        SubjectStyle = CopyStyle(m.SubjectStyle)
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
    private void BtnAddMiss_Click(object sender, RoutedEventArgs e)
        => _miss.Add(new MissingHomeworkItem { Origin = "local" });

    private void BtnDelMiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not MissingHomeworkItem m) return;
        _miss.Remove(m);
        // 只有「本来就有内容的旧条目」才需要留墓碑；空白占位行直接丢掉即可
        if (!string.IsNullOrWhiteSpace(m.Subject) || !string.IsNullOrWhiteSpace(m.Students))
        {
            m.Deleted = true;
            _tombstones.Add(m);
        }
        if (_miss.Count == 0) _miss.Add(new MissingHomeworkItem());
    }

    // ---------- 结果 ----------
    public MissingBoard BuildBoard() => new()
    {
        // ★ 不参与编辑的同步字段必须原样带回，绝不能因为 new MissingBoard() 就丢掉
        ClassKey = _source.ClassKey,
        SchemaRev = _source.SchemaRev,
        Title = (TxtTitle.Text ?? "").Trim(),
        TitleStyle = CopyStyle(_titleStyle),
        Items = _miss
            .Where(m => !string.IsNullOrWhiteSpace(m.Subject) || !string.IsNullOrWhiteSpace(m.Students))
            .Select(m =>
            {
                var c = Clone(m);
                c.Subject = (m.Subject ?? "").Trim();
                c.Students = (m.Students ?? "").Trim();
                c.Color = m.Color ?? "";
                c.Deleted = false;
                return c;
            })
            .Concat(_tombstones)   // 墓碑必须写回，防止对端旧变更单把已删条目复活
            .ToList(),
        Theme = _theme,
        UpdatedAt = DateTime.Now
    };

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        NoteLock.Save("missing", ChkLocked.IsChecked == true);
        DialogResult = true;
    }

    /// <summary>手写输入：识别结果插回"最后点过的那个输入框"（姓名生僻字的主要输入方式）。</summary>
    private void BtnHand_Click(object sender, RoutedEventArgs e) => HandwritingService.Open(this);

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
