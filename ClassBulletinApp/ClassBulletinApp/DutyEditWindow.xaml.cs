using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
/// 值日生名单编辑对话框：卡片标题、主题风格、标题 / 内容字体样式，以及「星期 + 姓名 + 值日内容」清单。
/// <para>改动先在内存里编辑，点「确定」才落盘。</para>
/// </summary>
public partial class DutyEditWindow : Window
{
    /// <summary>星期下拉的选项（XAML 里用 <c>{x:Static}</c> 直接取，避免每行 ComboBox 各建一份集合）。</summary>
    public static string[] DayNameList => DutyItem.DayNames;

    private readonly DutyBoard _draft;
    private readonly ObservableCollection<DutyItem> _items = new();

    /// <summary>
    /// 本次编辑里被删掉的条目：<b>只移出列表、不物理删除</b>，保存时带着 Deleted=true 一起写回。
    /// <para>★ 物理删掉会让远端一条晚到的旧指令把它「复活」，而且回执里再也对不上原条目。</para>
    /// </summary>
    private readonly List<DutyItem> _tombstones = new();
    private string _theme = "green";
    private bool _loading;

    /// <summary>r69：全窗唯一的字体工具条，按焦点切换作用对象。</summary>
    private FocusStyleBinder? _binder;

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
        ChkLocked.IsChecked = NoteLock.Load(DutyWindow.PlacementKey);
        _theme = string.IsNullOrWhiteSpace(_draft.Theme) ? "green" : _draft.Theme;
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);

        _draft.TitleStyle ??= new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };
        _draft.ContentStyle ??= new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel };
        _draft.DayStyle ??= new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel };
        _draft.DutyStyle ??= new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel };

        // 原有墓碑先收进 _tombstones：它们不在列表里显示，但保存时必须原样写回
        foreach (var it in _draft.Items ?? new List<DutyItem>())
            if (it.Deleted) _tombstones.Add(it);

        foreach (var it in _draft.SortedItems()) _items.Add(it);
        GridItems.ItemsSource = _items;

        // ★ r69 焦点跟随：卡片标题走 GotFocus，表格按「当前单元格所在列」映射。
        //   四套样式对象一个都没丢，只是从四行控件收成了一条工具条。
        _binder = new FocusStyleBinder(this, PickStyle);
        _binder.Bind(TxtTitle, () => _draft.TitleStyle, "卡片标题");
        _binder.Bind(GridItems, ResolveGridStyle);
        GridItems.CurrentCellChanged += (_, __) =>
        {
            // 只在焦点确实落在表格里时切换，避免点「添加一条」按钮时把工具条又点亮
            if (GridItems.IsKeyboardFocusWithin)
            {
                var (st, name) = ResolveGridStyle();
                _binder.Activate(st, name);
            }
        };

        _loading = false;
    
        GlassChrome.Prepare(this);}

    /// <summary>DataGrid 当前列 → 对应样式（0=星期 / 1=姓名 / 2=值日内容）。</summary>
    private (TextStyle? Style, string Name) ResolveGridStyle()
    {
        var cell = GridItems.CurrentCell;
        int idx = cell.Column == null ? -1 : GridItems.Columns.IndexOf(cell.Column);
        return idx switch
        {
            0 => (_draft.DayStyle, "星期"),
            2 => (_draft.DutyStyle, "值日内容"),
            _ => (_draft.ContentStyle, "值日生姓名")
        };
    }

    /// <summary>
    /// 深拷贝一份草稿：取消时直接丢弃，磁盘上的数据一点没动。
    /// <para>★ <b>必须连身份字段一起复制</b>（Id / Deleted / Origin / Parent / FieldHlc）。
    /// 只拷 Day/Duty/Names 会让每次打开编辑窗都把 Id 洗掉 —— 远端按 Id 指的那一条就再也找不到了，
    /// 「替换」只能退化成「新增」。这是作业 / 缺交编辑窗踩过的同一个坑。</para>
    /// </summary>
    private static DutyBoard Clone(DutyBoard b) => new()
    {
        ClassKey = b.ClassKey,
        SchemaRev = b.SchemaRev,
        Title = b.Title,
        TitleStyle = (b.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        ContentStyle = (b.ContentStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        DayStyle = (b.DayStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        DutyStyle = (b.DutyStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        Theme = b.Theme,
        HighlightToday = b.HighlightToday,
        UpdatedAt = b.UpdatedAt,
        Items = (b.Items ?? new List<DutyItem>())
            .Select(x => new DutyItem
            {
                Id = x.Id, Deleted = x.Deleted, Origin = x.Origin, Parent = x.Parent,
                FieldHlc = new Dictionary<string, string>(x.FieldHlc ?? new()),
                Day = x.Day, Duty = x.Duty, Names = x.Names
            })
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
        // 只移出列表 + 打墓碑，保存时仍写回文件：物理删除会让远端一条晚到的旧指令把它「复活」，
        // 也会让已发出的变更单回执对不上原条目
        sel.Deleted = true;
        if (!_tombstones.Contains(sel)) _tombstones.Add(sel);
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
                // ★ 班级键与代次必须带回：丢了 ClassKey，这条便签就不再属于任何班，
                //   内容快照 / 变更单都按班分片，等于把它从同步里抹掉
                ClassKey = _draft.ClassKey,
                SchemaRev = _draft.SchemaRev,
                Title = (TxtTitle.Text ?? "").Trim(),
                TitleStyle = (_draft.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
                ContentStyle = (_draft.ContentStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
                DayStyle = (_draft.DayStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
                DutyStyle = (_draft.DutyStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
                Theme = _theme,
                HighlightToday = ChkHighlight.IsChecked == true,
                // 可见条目 + 墓碑，全部写回：删掉的条目只是不再显示
                Items = _items.Concat(_tombstones).Distinct().ToList(),
                UpdatedAt = DateTime.Now
            };

            Store.SaveDuty(board);
            NoteLock.Save(DutyWindow.PlacementKey, ChkLocked.IsChecked == true);
            if (Store.LastWriteError != null)
            {
                MessageBox.Show("保存值日生名单失败：" + Store.LastWriteError +
                                "\n\n改动没有写进磁盘，关掉重开会回到原来的名单。",
                    "值日生名单", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

    /// <summary>手写输入兜底入口（悬浮球被隐藏时用）：识别结果插回最后点过的输入框。</summary>
    private void BtnHand_Click(object sender, RoutedEventArgs e) => HandwritingService.Open(this);
}
