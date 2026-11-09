using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClassBulletinApp.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms（均有同名类型），此处用别名消歧义
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;

namespace ClassBulletinApp;

/// <summary>
/// 倒计时便签编辑对话框：卡片标题、主题风格、文字格式，以及「+ 添加事件」式事件列表。
/// <para>r69：列表从 DataGrid 改成卡片式（对齐作业公示）。</para>
/// <para>r73c：「剩余天数」不再是只能点选的预览块，而是卡片里一个<b>只读输入框</b> ——
/// 能落焦点，于是天数字体自动回到全窗唯一那条格式条上，不必再单独加一条工具条。</para>
/// <para>改动先在内存里编辑，点「确定」才落盘 —— 取消不该把用户原来的倒计时弄乱。</para>
/// </summary>
public partial class CountdownEditWindow : Window
{
    private readonly CountdownBoard _draft;
    private readonly ObservableCollection<CountdownItem> _items = new();
    private string _theme = "classic";
    private bool _loading;

    /// <summary>r69：全窗唯一的字体工具条，按焦点切换作用对象。</summary>
    private FocusStyleBinder? _binder;

    /// <summary>r73c：事件卡片模板里生成的「剩余」只读输入框，样式一改要逐个刷新。</summary>
    private readonly List<TextBox> _dayBoxes = new();

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
        ChkLocked.IsChecked = NoteLock.Load(CountdownWindow.PlacementKey);
        _theme = string.IsNullOrWhiteSpace(_draft.Theme) ? "classic" : _draft.Theme;
        CbTheme.SelectedIndex = HomeworkThemes.IndexOf(_theme);

        _draft.TitleStyle ??= new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };
        _draft.ContentStyle ??= new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel };
        _draft.DaysStyle ??= CountdownBoard.NewDaysStyle();

        foreach (var it in _draft.SortedItems()) _items.Add(it);
        EventItems.ItemsSource = _items;

        // ★ r69 焦点跟随：卡片标题走 GotFocus；卡片里的名称 / 日期 / 备注 / 剩余输入框
        //   在各自的 GotFocus 里动态登记，四类字段共用这一条工具条。
        _binder = new FocusStyleBinder(this, PickStyle);
        _binder.Bind(TxtTitle, () => _draft.TitleStyle, "卡片标题");

        // r73：样式对象自己就是 INotifyPropertyChanged，直接订阅它刷新预览 —— 比只靠工具条的
        // StyleChanged 更可靠（诊断里改属性、以后「套用主题默认」等任何入口都能立刻反映）。
        _draft.TitleStyle.PropertyChanged += (_, _) => RefreshPreviews();
        _draft.ContentStyle.PropertyChanged += (_, _) => RefreshPreviews();
        _draft.DaysStyle.PropertyChanged += (_, _) => RefreshPreviews();

        RefreshPreviews();
        _loading = false;
    
        GlassChrome.Prepare(this);}

    /// <summary>深拷贝一份草稿：取消时直接丢弃，磁盘上的数据一点没动。</summary>
    private static CountdownBoard Clone(CountdownBoard b) => new()
    {
        Title = b.Title,
        TitleStyle = (b.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        ContentStyle = (b.ContentStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
        DaysStyle = (b.DaysStyle ?? CountdownBoard.NewDaysStyle()).Clone(),
        Theme = b.Theme,
        HighlightToday = b.HighlightToday,
        UpdatedAt = b.UpdatedAt,
        Items = (b.Items ?? new List<CountdownItem>())
            .Select(x => new CountdownItem { Name = x.Name, TargetDate = x.TargetDate, Note = x.Note })
            .ToList()
    };

    // ---------- 焦点跟随 ----------

    /// <summary>
    /// 事件名称 / 日期 / 备注：三个输入框共用「条目样式」。
    /// </summary>
    private void CdText_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        _binder?.BindAndActivate(fe, _draft.ContentStyle, "事件文字");
    }

    /// <summary>
    /// 「剩余」只读输入框拿到焦点：工具条切到天数样式。
    /// <para>r73c 的关键就是这一步 —— 它是个真输入框，焦点天然会落到它身上，
    /// 不再依赖「点一个 160px 方块」那种真机上点不准的入口。</para>
    /// </summary>
    private void DaysText_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        _binder?.BindAndActivate(fe, _draft.DaysStyle, "剩余天数");
    }

    // ---------- r73 实时预览 ----------

    /// <summary>模板生成的天数输入框一进可视树就登记，样式变化时逐个刷新。</summary>
    private void DaysText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (!_dayBoxes.Contains(box)) _dayBoxes.Add(box);
        ApplyDaysBox(box);
    }

    private void DaysText_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) _dayBoxes.Remove(box);
    }

    /// <summary>
    /// 预览统一走「基准字号 × 档位比例」：标准档 = 原来的 16/13 号，新旧数据看起来一致。
    /// <para>天数是另一套口径：它跟悬浮便签一样直接用档位基准字号（特大 = 27 号），
    /// 这样编辑窗里看到的和便签上真正显示的一模一样。</para>
    /// </summary>
    private void RefreshPreviews()
    {
        ApplyText(PreviewTitle, _draft.TitleStyle, 16, true, "#1B2836");
        ApplyText(PreviewName, _draft.ContentStyle, 13, false, "#475467");
        ApplyDaysPreview(PreviewDays);
        foreach (var box in _dayBoxes.ToArray()) ApplyDaysBox(box);
    }

    private static void ApplyText(TextBlock tb, TextStyle? st, double baseSize,
                                  bool boldFallback, string colorFallback)
    {
        tb.FontSize = Math.Clamp(HwTextRender.ScaleSize(st, baseSize), 10, 30);
        tb.FontWeight = HwTextRender.BoldOr(st, boldFallback) ? FontWeights.Bold : FontWeights.Normal;
        tb.Foreground = ThemeHelper.Brush(HwTextRender.ColorOr(st, colorFallback),
                                          Color.FromRgb(0x37, 0x41, 0x51));
    }

    /// <summary>预览卡里的天数：默认红 + 加粗 + 特大号。</summary>
    private void ApplyDaysPreview(TextBlock tb)
    {
        tb.FontSize = Math.Clamp(HwTextRender.SizeOf(_draft.DaysStyle, 0), 10, 34);
        tb.FontWeight = HwTextRender.BoldOr(_draft.DaysStyle, true) ? FontWeights.Bold : FontWeights.Normal;
        tb.Foreground = DaysBrush();
    }

    /// <summary>事件卡片里的「剩余」输入框：字号按真实档位，改完立刻能看到多大。</summary>
    private void ApplyDaysBox(TextBox box)
    {
        box.FontSize = Math.Clamp(HwTextRender.SizeOf(_draft.DaysStyle, 0), 10, 44);
        box.FontWeight = HwTextRender.BoldOr(_draft.DaysStyle, true) ? FontWeights.Bold : FontWeights.Normal;
        box.Foreground = DaysBrush();
    }

    private SolidColorBrush DaysBrush() => ThemeHelper.Brush(
        HwTextRender.ColorOr(_draft.DaysStyle, CountdownBoard.DaysDefaultColor),
        Color.FromRgb(0xC0, 0x39, 0x2B));

    // ---------- 事件 ----------
    private void CbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CbTheme.SelectedItem is ThemeOption opt) _theme = opt.Key;
        RefreshPreviews();   // 颜色跟随主题时，换主题预览也要跟着换
    }

    private void Style_Changed(object? sender, EventArgs e) => RefreshPreviews();

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var item = new CountdownItem
        {
            Name = "新事件",
            TargetDate = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd")
        };
        _items.Add(item);
    }

    /// <summary>卡片右上角的删除按钮：带走的是当前这一条，不再需要先「选中一行」。</summary>
    private void BtnDelEvent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CountdownItem sel) return;
        _items.Remove(sel);
        _binder?.Deactivate();
    }

    // ---------- 保存 ----------
    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var board = new CountdownBoard
            {
                Title = (TxtTitle.Text ?? "").Trim(),
                TitleStyle = (_draft.TitleStyle ?? new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
                ContentStyle = (_draft.ContentStyle ?? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }).Clone(),
                DaysStyle = (_draft.DaysStyle ?? CountdownBoard.NewDaysStyle()).Clone(),
                Theme = _theme,
                HighlightToday = ChkHighlight.IsChecked == true,
                Items = _items.ToList(),   // 卡片式无需 CommitEdit：UpdateSourceTrigger=PropertyChanged 已即时写回
                UpdatedAt = DateTime.Now
            };

            Store.SaveCountdown(board);
            NoteLock.Save(CountdownWindow.PlacementKey, ChkLocked.IsChecked == true);

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

    // ---------- 诊断（--ui-check） ----------

    internal TextStyle DiagDaysStyle => _draft.DaysStyle!;
    internal TextStyle DiagContentStyle => _draft.ContentStyle!;
    internal Controls.TextStylePicker DiagPicker => PickStyle;
    internal TextBlock DiagPreviewDays => PreviewDays;
    internal TextBlock DiagPreviewName => PreviewName;
    internal int DiagDayBoxCount => _dayBoxes.Count;
    internal double DiagFirstDayBoxFontSize => _dayBoxes.Count == 0 ? -1 : _dayBoxes[0].FontSize;
    internal bool DiagDayBoxReadOnly => _dayBoxes.Count > 0 && _dayBoxes[0].IsReadOnly;

    /// <summary>诊断：新建一份板子的默认天数样式（红 + 加粗 + 特大号），用来锁住默认口径。</summary>
    internal static TextStyle DiagNewDaysStyle() => CountdownBoard.NewDaysStyle();

    /// <summary>
    /// 诊断：把焦点交给第一条事件的「剩余」输入框，验证工具条会切到天数样式。
    /// <para>它是个真输入框，正常情况下 <see cref="FrameworkElement.Focus"/> 就会触发
    /// <see cref="DaysText_GotFocus"/>；窗口停在屏幕外时键盘焦点可能拿不到，再按映射兜底一次。</para>
    /// </summary>
    internal bool DiagFocusDayBox()
    {
        EventItems.UpdateLayout();
        UpdateLayout();
        var box = _dayBoxes.FirstOrDefault();
        if (box == null) return false;
        box.Focus();
        if (!DiagPicker.IsActive || DiagPicker.TargetName != "剩余天数")
            _binder?.BindAndActivate(box, _draft.DaysStyle, "剩余天数");
        return true;
    }
}
