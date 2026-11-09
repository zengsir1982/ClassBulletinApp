using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassBulletinApp.Controls;
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

    /// <summary>
    /// 本窗口里被删掉的旧条目：不物理删除，改成 <c>Deleted=true</c> 墓碑，
    /// 保存时一起写回。物理删除会让对端一条晚到的旧字段变更单把条目「复活」。
    /// </summary>
    private readonly List<HomeworkItem> _tombstones = new();

    /// <summary>打开时的原始板，用来保留 ClassKey / SchemaRev / Layout 等不参与编辑的字段。</summary>
    private readonly HomeworkBoard _source;

    private string _theme = "classic";

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

    public HomeworkEditWindow(HomeworkBoard board)
    {
        InitializeComponent();

        _source = board ?? new HomeworkBoard();
        _theme = string.IsNullOrWhiteSpace(_source.Theme) ? "classic" : _source.Theme;
        TxtTitle.Text = string.IsNullOrWhiteSpace(_source.Title) ? "各科作业公示" : _source.Title;

        _titleStyle.CopyFrom(_source.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel });

        // 墓碑不显示在编辑列表里，但要原样带回，否则保存时会把它们丢掉
        foreach (var it in _source.Items ?? new())
        {
            if (it.Deleted) _tombstones.Add(Clone(it));
            else _items.Add(Clone(it));
        }
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
        ChkLocked.IsChecked = NoteLock.Load("homework");

        // ★ r69 焦点跟随：总标题静态绑定；列表里每条的科目 / 内容在 GotFocus 时动态登记。
        //   行内的紧凑控件仍绑同一个样式对象，两种入口互不冲突。
        _binder = new FocusStyleBinder(this, PickStyle);
        _binder.Bind(TxtTitle, () => _titleStyle, "总标题");
    
        GlassChrome.Prepare(this);}

    private void HwSubject_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not HomeworkItem it) return;
        _binder?.BindAndActivate(fe, it.SubjectStyle, "科目");
    }

    private void HwContent_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not HomeworkItem it) return;
        _binder?.BindAndActivate(fe, it.ContentStyle, "作业内容");
    }

    // 老数据里样式对象可能为 null：兜底也用「特大」，避免新建条目忽然缩回标准档。
    private static TextStyle CopyStyle(TextStyle? s)
        => (s ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone();

    /// <summary>编辑期副本。<b>必须带上 Id / 墓碑 / 来源 / 派生关系 / 字段版本</b>，
    /// 否则用户点一次确定，所有同步身份字段就全丢了。</summary>
    private static HomeworkItem Clone(HomeworkItem x) => new()
    {
        Id = x.Id, Deleted = x.Deleted, Origin = x.Origin, Parent = x.Parent,
        FieldHlc = x.FieldHlc == null ? new() : new Dictionary<string, string>(x.FieldHlc),
        Subject = x.Subject, Content = x.Content,
        Bold = x.Bold, Color = x.Color, SizeLevel = x.SizeLevel,
        SubjectStyle = CopyStyle(x.SubjectStyle)
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
        PreviewCard.BorderBrush = ThemeHelper.Brush(t.BorderColor, Color.FromRgb(0xE1, 0xE6, 0xEE));
        PreviewTitle.Foreground = ThemeHelper.Brush(t.TitleColor, Color.FromRgb(0x1B, 0x28, 0x36));
        PreviewSubject.Foreground = ThemeHelper.Brush(t.Accent, Color.FromRgb(0x1E, 0x4F, 0xA3));
        PreviewBody.Foreground = ThemeHelper.Brush(t.Foreground, Color.FromRgb(0x33, 0x3B, 0x47));
    }

    // ---------- 增删 ----------
    private void BtnAddHw_Click(object sender, RoutedEventArgs e)
        => _items.Add(new HomeworkItem { Origin = "local" });

    private void BtnDelHw_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not HomeworkItem it) return;
        _items.Remove(it);
        // 只有「本来就有内容的旧条目」才需要留墓碑；空白占位行直接丢掉即可
        if (!string.IsNullOrWhiteSpace(it.Subject) || !string.IsNullOrWhiteSpace(it.Content))
        {
            it.Deleted = true;
            _tombstones.Add(it);
        }
        if (_items.Count == 0) _items.Add(new HomeworkItem());
    }

    // ---------- 结果 ----------
    /// <summary>按当前编辑内容生成作业公示数据（布局沿用设置中的桌面背景位置）。</summary>
    public HomeworkBoard BuildBoard() => new()
    {
        // ★ 不参与编辑的同步字段必须原样带回，绝不能因为 new HomeworkBoard() 就丢掉
        ClassKey = _source.ClassKey,
        SchemaRev = _source.SchemaRev,
        Title = (TxtTitle.Text ?? "").Trim(),
        TitleStyle = CopyStyle(_titleStyle),
        Items = _items
            .Where(x => !string.IsNullOrWhiteSpace(x.Subject) || !string.IsNullOrWhiteSpace(x.Content))
            .Select(x =>
            {
                var c = Clone(x);
                c.Subject = (x.Subject ?? "").Trim();
                c.Content = (x.Content ?? "").Trim();
                c.Color = x.Color ?? "";
                c.Deleted = false;
                return c;
            })
            .Concat(_tombstones)   // 墓碑必须写回，防止对端旧变更单把已删条目复活
            .ToList(),
        // 缺交名单已独立存储，这里原样带回避免覆盖丢失。
        // ★ 用 _source 而不是重新读盘：读盘会丢掉内存里刚补好的稳定 Id，也白白多一次 I/O。
        Missing = _source.Missing ?? new(),
        Theme = _theme,
        UpdatedAt = DateTime.Now,
        Layout = Store.LoadSettings().Backdrop?.HomeworkLayout ?? new HomeworkWallpaperLayout()
    };

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        NoteLock.Save("homework", ChkLocked.IsChecked == true);
        DialogResult = true;
    }

    /// <summary>手写输入：识别结果插回"最后点过的那个输入框"（触摸屏没有实体键盘时的主要输入方式）。</summary>
    private void BtnHand_Click(object sender, RoutedEventArgs e) => HandwritingService.Open(this);

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
