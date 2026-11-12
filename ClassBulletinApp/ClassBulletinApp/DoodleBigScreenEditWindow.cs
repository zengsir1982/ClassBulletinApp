using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClassBulletinApp.Controls;
using ScheduleCore.Models;

// 项目同时引用 WPF 与 WinForms / System.Drawing，同名类型必须逐个消歧义（CS0104）
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Rectangle = System.Windows.Shapes.Rectangle;
using Border = System.Windows.Controls.Border;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using DockPanel = System.Windows.Controls.DockPanel;
using Grid = System.Windows.Controls.Grid;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using WrapPanel = System.Windows.Controls.WrapPanel;

namespace ClassBulletinApp;

/// <summary>
/// 大屏编辑窗里的一条记录。
/// <para>作业 / 缺交 / 值日是「科目 + 内容」两个输入框；值日多一个「值日内容」，
/// 对应 <c>DutyItem</c> 的 Day / Names / Duty 三个字段。</para>
/// </summary>
public sealed class DoodleBigEditEntry
{
    /// <summary>
    /// 这条记录在公示数据里的稳定 Id（<c>HomeworkItem.Id</c> / <c>MissingHomeworkItem.Id</c> / <c>DutyItem.Id</c>）。
    /// <para>★ 空 = 本次新加的条目，保存时由 Store 补稳定 Id；非空才能做到「改这条就是改这条」，
    /// 而不是像 r99 那样把多条压成一段文本再塞回同一条。</para>
    /// </summary>
    public string Id = "";

    /// <summary>科目 / 记录名（第一个输入框）。值日时是星期。</summary>
    public string Title = "";

    /// <summary>内容（第二个输入框，允许回车换行）。值日时是值日生姓名。</summary>
    public string Body = "";

    /// <summary>第三个字段，只有值日用到：值日内容 / 岗位（如「扫地」）。</summary>
    public string Extra = "";

    /// <summary>科目输入框的文字格式（文字格式条按焦点切到这里）。</summary>
    public TextStyle TitleStyle = new();

    /// <summary>内容输入框的文字格式。</summary>
    public TextStyle BodyStyle = new();

    /// <summary>值日内容输入框的文字格式。</summary>
    public TextStyle ExtraStyle = new();
}

/// <summary>大屏编辑窗交回的结果。内容写回哪一份数据源由黑板决定，窗口本身不碰 Store。</summary>
public sealed class DoodleBigEditDraft
{
    public string Title = "";
    public string Body = "";
    public double TitleSize = 32;
    public double ContentSize = 24;
    public string TitleColor = "";
    public string BodyColor = "";
    public bool TitleBold = true;
    public bool BodyBold = false;

    /// <summary>
    /// ★ r104：行首「科目」的格式。0 = 没设过（用板默认「比内容略大 + 加粗」）。
    /// <para>作业 / 缺交的科目名、课表的「第N节」都用它，与内容分开。</para>
    /// </summary>
    public double SubjectSize;
    public string SubjectColor = "";
    public bool SubjectBold = true;

    /// <summary>保留兼容：黑板主题 key。<b>r99 起编辑窗不再改整块黑板</b>，恒为 false 的 SaveTheme 保证这一点。</summary>
    public string ThemeKey = "";
    public bool SaveStyle = true;
    public bool SaveTheme = false;

    /// <summary>★ 这张便签自己的风格（HomeworkThemes key），写进单张便签的 AutoStyle，不改黑板。</summary>
    public string NoteTheme = "";
    public bool SaveNoteTheme = true;

    /// <summary>锁住这张便签：锁定后在黑板上不能拖动 / 缩放 / 双击编辑。</summary>
    public bool Locked;

    /// <summary>正文条目列表（「+ 添加科目」）。单文本模块只有一条。</summary>
    public List<DoodleBigEditEntry> Entries = new();
}

/// <summary>双击一张便签时传给大屏编辑窗的上下文。</summary>
public sealed class DoodleBigEditTarget
{
    public string Key = "";
    public string Module = "";
    public string ItemId = "";
    public string Title = "";
    public string Body = "";
    public bool BodyEditable = true;
    public string BodyHint = "";
    /// <summary>true = 作业 / 缺交 / 值日，显示多条记录与「＋ 添加…」。</summary>
    public bool MultiEntry;

    /// <summary>
    /// true = 编辑<b>整份公示</b>（作业 / 缺交）：<see cref="Entries"/> 是这份公示的全部科目记录，
    /// 每条对应公示里的一条真实数据，可增删；保存时逐条落回。
    /// <para>★ 与 <see cref="MultiEntry"/> 的区别：MultiEntry 只是版式（双输入框），
    /// WholeBoard 才决定「一条记录 = 一条数据」，不再把多条拼成「科目：内容」文本塞进同一条。</para>
    /// </summary>
    public bool WholeBoard;

    /// <summary>整份模式下公示的总标题（写回 <c>HomeworkBoard.Title</c> / <c>MissingBoard.Title</c>）。</summary>
    public string BoardTitle = "";

    /// <summary>整份模式的初始记录（带稳定 Id）。空表 = 这份公示还没有条目，窗口补一条空记录。</summary>
    public List<DoodleBigEditEntry> Entries = new();

    public DoodleAutoStyle? Style;
    /// <summary>兼容旧调用：黑板主题。r99 起只保留不用。</summary>
    public string ThemeKey = DoodleBoardThemes.DefaultKey;
    /// <summary>★ 这张便签当前自己的风格（HomeworkThemes key；空 = 经典便签黄）。</summary>
    public string NoteTheme = "";
}

/// <summary>
/// 教室大屏专用的便签编辑窗。
/// <para>★ r99 按老师给的悬浮便签编辑窗样张整窗重做：① 便签预览 → ② 主题风格 + 锁定便签
/// → ③ 文字格式条（<b>焦点跟随</b>，鼠标点哪个输入框就改哪个框的格式）→ ④ 总标题
/// → ⑤ 内容区（单文本一个大输入框；作业 / 缺交 / 值日是「科目 + 内容」两个输入框 + 添加科目）
/// → ⑥ 底栏。</para>
/// <para>★ 「主题风格」是<b>这张便签自己的风格</b>（HomeworkThemes，经典便签黄 / 护眼绿…），
/// 不是整块黑板的材质，改一张不影响其它卡。字号档位走大屏表 13~88，不污染公示共用的档位表。</para>
/// </summary>
public class DoodleBigScreenEditWindow : Window
{
    private readonly DoodleBigEditTarget _target;
    private readonly DoodleBigEditDraft _draft = new();
    private readonly ObservableCollection<DoodleBigEditEntry> _entries = new();

    // 每个可聚焦字段一份样式；文字格式条由 FocusStyleBinder 按焦点在这些对象间切换
    private readonly TextStyle _titleStyle = new();
    private readonly TextStyle _bodyStyle = new();
    /// <summary>★ r104：行首「科目」的格式。所有条目的科目框共用这一份 —— 科目是一类东西，不该各条各调。</summary>
    private readonly TextStyle _subjectStyle = new();
    private readonly List<(TextBox Box, TextStyle Style)> _fieldBoxes = new();
    private FocusStyleBinder? _binder;

    private readonly TextStylePicker _picker = new();
    private readonly Border _previewCard = new();
    private readonly TextBlock _pvTitle = new();
    private readonly TextBlock _pvBody = new();
    private readonly TextBox _titleBox = new();
    private readonly ComboBox _themeCombo = new();
    private readonly CheckBox _lockCheck = new();
    private readonly StackPanel _entryHost = new();
    private readonly Button _addBtn = new();
    private TextBox? _singleBodyBox;

    public DoodleBigScreenEditWindow(DoodleBigEditTarget target)
    {
        _target = target ?? new DoodleBigEditTarget();
        var src = _target.Style;

        // 初始格式：大屏档位下标直接读 TitleSize / ContentSize，格式条才不会一打开就跳档。
        // 老数据只存了标题字号时，正文跟着标题降一档（而不是掉回默认 24），
        // 否则教室大屏上「标题 88 / 正文 24」的层级会突然倒挂。
        int titleLevel = DoodleStyle.BigIndexOf(src?.TitleSize ?? DoodleStyle.BigSizeOf(3));
        int bodyLevel = src?.ContentSize is double c && c > 0
            ? DoodleStyle.BigIndexOf(c)
            : Math.Max(0, titleLevel - 1);
        _titleStyle.SizeLevel = Math.Clamp(titleLevel, 0, DoodleStyle.BigFontSizes.Length - 1);
        _bodyStyle.SizeLevel = Math.Clamp(bodyLevel, 0, DoodleStyle.BigFontSizes.Length - 1);
        _titleStyle.Bold = src?.Title?.Bold ?? true;
        _titleStyle.Color = src?.Title?.Color ?? "";
        _bodyStyle.Bold = src?.Content?.Bold ?? false;
        _bodyStyle.Color = src?.Content?.Color ?? "";

        // ★ r104：科目格式。没设过就取「内容再大一档 + 加粗」，一打开就能看出科目与内容的区别；
        //   设过就按存的直接字号还原档位（否则再打开会跳档）。
        int subjectLevel = src?.SubjectSize is double s && s > 0
            ? DoodleStyle.BigIndexOf(s)
            : Math.Min(DoodleStyle.BigFontSizes.Length - 1, bodyLevel + 1);
        _subjectStyle.SizeLevel = Math.Clamp(subjectLevel, 0, DoodleStyle.BigFontSizes.Length - 1);
        _subjectStyle.Bold = src?.Subject?.Bold ?? true;
        _subjectStyle.Color = src?.Subject?.Color ?? "";

        _draft.Title = _target.WholeBoard ? _target.BoardTitle : _target.Title;
        _draft.Body = _target.Body;
        _draft.TitleSize = DoodleStyle.BigSizeOf(_titleStyle.SizeLevel);
        _draft.ContentSize = DoodleStyle.BigSizeOf(_bodyStyle.SizeLevel);
        _draft.ThemeKey = _target.ThemeKey;
        _draft.NoteTheme = _target.NoteTheme ?? "";
        _draft.Locked = src?.Locked ?? false;
        _draft.SaveTheme = false;

        SeedEntries();

        Title = "编辑黑板便签 · 内容、格式与风格";
        Width = 840;
        Height = 820;
        MinWidth = 700;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Background = Brushes.White;

        var root = new Grid();
        for (int i = 0; i < 6; i++)
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });

        BuildPreview(root);
        BuildThemeRow(root);
        BuildFormatRow(root);
        BuildTitleRow(root);
        BuildContentArea(root);
        BuildFooter(root);

        Content = root;

        // ★ 焦点跟随：总标题 / 单一正文静态登记；多科目条目的输入框在 GotFocus 里动态登记。
        _binder = new FocusStyleBinder(this, _picker);
        _binder.Bind(_titleBox, () => _titleStyle, "总标题");
        _fieldBoxes.Add((_titleBox, _titleStyle));
        if (_singleBodyBox != null)
        {
            var box = _singleBodyBox;
            _binder.Bind(box, () => _bodyStyle, "便签内容");
            _fieldBoxes.Add((box, _bodyStyle));
        }

        BuildEntries();
        ApplyFieldVisuals();
        ApplyNoteThemeAndPreview();
    }

    public DoodleBigEditDraft Draft => _draft;

    // ==================================================================
    //  ① 预览
    // ==================================================================
    private void BuildPreview(Grid root)
    {
        var outer = new Border
        {
            Background = Brushes.White,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12, 16, 12)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "便签预览", FontSize = 11, Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 6)
        });

        _previewCard.CornerRadius = new CornerRadius(10);
        _previewCard.Padding = new Thickness(16, 12, 16, 14);
        _previewCard.BorderThickness = new Thickness(1);
        _previewCard.MinHeight = 104;

        var inner = new StackPanel();
        _pvTitle.TextWrapping = TextWrapping.Wrap;
        _pvBody.TextWrapping = TextWrapping.Wrap;
        _pvBody.Margin = new Thickness(0, 8, 0, 0);
        inner.Children.Add(_pvTitle);
        inner.Children.Add(_pvBody);

        _previewCard.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 132,
            Content = inner
        };
        sp.Children.Add(_previewCard);
        outer.Child = sp;
        Grid.SetRow(outer, 0);
        root.Children.Add(outer);
    }

    // ==================================================================
    //  ② 主题风格（便签各自） + 锁定便签
    // ==================================================================
    private void BuildThemeRow(Grid root)
    {
        var row = new Border
        {
            Background = Brushes.White,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        wrap.Children.Add(new TextBlock
        {
            Text = "主题风格", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        _themeCombo.Width = 210;
        _themeCombo.Height = 38;
        _themeCombo.FontSize = 14;
        _themeCombo.VerticalContentAlignment = VerticalAlignment.Center;
        foreach (var t in HomeworkThemes.Presets)
        {
            var item = new ComboBoxItem { Tag = t.Key };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
            line.Children.Add(new Rectangle
            {
                Width = 18, Height = 18, RadiusX = 4, RadiusY = 4,
                Fill = Hex(t.Background), Stroke = Hex(t.BorderColor), StrokeThickness = 1,
                Margin = new Thickness(0, 0, 8, 0)
            });
            line.Children.Add(new TextBlock
            {
                Text = t.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center
            });
            item.Content = line;
            _themeCombo.Items.Add(item);
        }
        _themeCombo.SelectedIndex = HomeworkThemes.IndexOf(_draft.NoteTheme);
        _themeCombo.SelectionChanged += (_, _) => ApplyNoteThemeAndPreview();
        wrap.Children.Add(_themeCombo);

        _lockCheck.Content = "锁定便签";
        _lockCheck.FontSize = 14;
        _lockCheck.VerticalAlignment = VerticalAlignment.Center;
        _lockCheck.Margin = new Thickness(20, 0, 0, 0);
        _lockCheck.IsChecked = _draft.Locked;
        _lockCheck.ToolTip = "锁定后在黑板上不能拖动 / 缩放 / 双击编辑，防止课堂上误碰";
        wrap.Children.Add(_lockCheck);

        row.Child = wrap;
        Grid.SetRow(row, 1);
        root.Children.Add(row);
    }

    // ==================================================================
    //  ③ 文字格式条（焦点跟随，大屏档位）
    // ==================================================================
    private void BuildFormatRow(Grid root)
    {
        var row = new Border
        {
            Background = Soft,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var dock = new DockPanel { LastChildFill = false };
        var label = new TextBlock
        {
            Text = "文字格式", FontSize = 13, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        DockPanel.SetDock(label, Dock.Left);
        dock.Children.Add(label);

        // ★ 复用悬浮便签编辑窗那条格式条：B / 字号下拉 / A⁺ A⁻ / 色板 / 清除 + 「正在设置：xxx」。
        //   BigScreen=true 只把档位表换成 13~88 的大屏表，公共公示档位表一字不动。
        _picker.BigScreen = true;
        _picker.StyleChanged += (_, _) => { ApplyFieldVisuals(); ApplyNoteThemeAndPreview(); };
        dock.Children.Add(_picker);

        row.Child = dock;
        Grid.SetRow(row, 2);
        root.Children.Add(row);
    }

    // ==================================================================
    //  ④ 总标题
    // ==================================================================
    private void BuildTitleRow(Grid root)
    {
        var row = new Border
        {
            Background = Brushes.White,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var dock = new DockPanel { LastChildFill = true };
        var label = new TextBlock
        {
            Text = _target.WholeBoard ? "公示总标题" : "总标题",
            FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(label, Dock.Left);
        dock.Children.Add(label);

        _titleBox.Text = _draft.Title;
        _titleBox.FontSize = 16;
        _titleBox.Height = 40;
        _titleBox.Padding = new Thickness(10, 6, 10, 6);
        _titleBox.VerticalContentAlignment = VerticalAlignment.Center;
        _titleBox.Margin = new Thickness(8, 0, 0, 0);
        _titleBox.BorderBrush = InputBorder;
        _titleBox.Tag = _titleStyle;
        _titleBox.TextChanged += (_, _) => ApplyNoteThemeAndPreview();
        dock.Children.Add(_titleBox);

        row.Child = dock;
        Grid.SetRow(row, 3);
        root.Children.Add(row);
    }

    // ==================================================================
    //  ⑤ 内容区：单文本大输入框 / 多科目「科目 + 内容」双输入框
    // ==================================================================
    private void BuildContentArea(Grid root)
    {
        var area = new Grid { Margin = new Thickness(14, 10, 14, 6) };
        area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        string hintText;
        if (!string.IsNullOrWhiteSpace(_target.BodyHint))
            hintText = _target.BodyHint;
        else if (_target.WholeBoard && IsDuty)
            hintText = "一周一条记录：左边选星期，中间填值日生姓名，右边填值日内容（可空）。保存后写回整份值日表。";
        else if (_target.WholeBoard)
            hintText = "一个科目一条记录：左科目、右内容。内容里的回车只用于排版；保存后写回整份公示。";
        else if (_target.MultiEntry)
            hintText = "一条记录 = 一个科目：左边填科目 / 名称，右边填内容；内容里按回车可以强制换行。";
        else
            hintText = "便签内容（支持自动换行；按回车即可强制分段换行）";

        var hintRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };

        // ★ r104：课表是单条只读正文，没有「科目输入框」可以拿焦点 ——
        //   给一个明确入口，点它之后格式条改的就是每行开头的「第N节」。
        if (string.Equals(_target.Module, DoodleModules.Schedule, StringComparison.OrdinalIgnoreCase))
        {
            var subjBtn = new Button
            {
                Content = "设置「第N节」格式",
                Height = 38, Padding = new Thickness(12, 0, 12, 0), FontSize = 13,
                Background = PrimarySoft, Foreground = Ink, BorderBrush = Hex("#B9CBE8"),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "点这里后，上面文字格式条改的就是每行开头的「第N节」"
            };
            subjBtn.Click += (_, _) => _binder?.Activate(_subjectStyle, "科目（第N节）");
            DockPanel.SetDock(subjBtn, Dock.Right);
            hintRow.Children.Add(subjBtn);
        }

        var hint = new TextBlock
        {
            Text = hintText, FontSize = 12, Foreground = Muted,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        hintRow.Children.Add(hint);
        Grid.SetRow(hintRow, 0);
        area.Children.Add(hintRow);

        if (_target.MultiEntry)
        {
            var host = new DockPanel { LastChildFill = true };
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            var section = new TextBlock
            {
                Text = SectionLabel(), FontSize = 16, FontWeight = FontWeights.Bold,
                Foreground = Ink, VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(section, Dock.Left);
            head.Children.Add(section);

            _addBtn.Content = AddLabel();
            _addBtn.Height = 40;
            _addBtn.Padding = new Thickness(14, 0, 14, 0);
            _addBtn.FontSize = 14;
            _addBtn.Background = PrimarySoft;
            _addBtn.BorderBrush = Hex("#B9CBE8");
            _addBtn.Foreground = Ink;
            _addBtn.Cursor = Cursors.Hand;
            _addBtn.Click += (_, _) =>
            {
                // 值日新加一条时自动挑一个还没用过的星期，省得每行都从周一改起
                if (IsDuty) _entries.Add(NewEntry(NextFreeDay(), "", "", ""));
                else _entries.Add(NewEntry("", ""));
                BuildEntries();
                ApplyNoteThemeAndPreview();
            };
            DockPanel.SetDock(_addBtn, Dock.Right);
            head.Children.Add(_addBtn);

            DockPanel.SetDock(head, Dock.Top);
            host.Children.Add(head);
            host.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _entryHost
            });
            Grid.SetRow(host, 1);
            area.Children.Add(host);
        }
        else
        {
            _singleBodyBox = new TextBox
            {
                Text = _draft.Body,
                FontSize = 20,
                FontWeight = FontWeights.Normal,
                Foreground = Ink,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MinHeight = 180,
                Padding = new Thickness(12),
                Background = Brushes.White,
                BorderBrush = InputBorder,
                IsReadOnly = !_target.BodyEditable,
                VerticalContentAlignment = VerticalAlignment.Top,
                Tag = _bodyStyle
            };
            _singleBodyBox.TextChanged += (_, _) => ApplyNoteThemeAndPreview();
            Grid.SetRow(_singleBodyBox, 1);
            area.Children.Add(_singleBodyBox);
        }

        Grid.SetRow(area, 4);
        root.Children.Add(area);
    }

    private string SectionLabel() => _target.Module switch
    {
        DoodleModules.Homework => "各科作业",
        DoodleModules.Missing => "缺交记录",
        DoodleModules.Duty => "值日安排",
        _ => "正文条目"
    };

    private string AddLabel() => _target.Module switch
    {
        DoodleModules.Homework => "＋ 添加科目",
        DoodleModules.Missing => "＋ 添加记录",
        DoodleModules.Duty => "＋ 添加一条",
        _ => "＋ 增加条目"
    };

    // ==================================================================
    //  ⑥ 底栏
    // ==================================================================
    private void BuildFooter(Grid root)
    {
        var row = new Border
        {
            Background = Brushes.White,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var dock = new DockPanel { LastChildFill = false };

        var hand = new Button
        {
            Content = "✎ 手写输入", Height = 44, Padding = new Thickness(14, 0, 14, 0),
            FontSize = 15, Background = PrimarySoft, BorderBrush = Hex("#B9CBE8"),
            Foreground = Ink, Cursor = Cursors.Hand,
            ToolTip = "先用手指点一下要输入的输入框，再点这里手写"
        };
        hand.Click += (_, _) => HandwritingService.Open(this);
        DockPanel.SetDock(hand, Dock.Left);
        dock.Children.Add(hand);

        var ok = FooterButton("确定", Primary, Brushes.White, 24);
        ok.IsDefault = true;
        ok.Click += (_, _) => { Commit(); DialogResult = true; Close(); };
        DockPanel.SetDock(ok, Dock.Right);
        dock.Children.Add(ok);

        var cancel = FooterButton("取消", Brushes.White, Ink, 24);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancel, Dock.Right);
        dock.Children.Add(cancel);

        row.Child = dock;
        Grid.SetRow(row, 5);
        root.Children.Add(row);
    }

    private static Button FooterButton(string text, Brush bg, Brush fg, double pad) => new()
    {
        Content = text, Height = 44, MinWidth = 100, Padding = new Thickness(pad, 0, pad, 0),
        FontSize = 15, FontWeight = FontWeights.Bold, Foreground = fg, Background = bg,
        BorderBrush = bg == Brushes.White ? InputBorder : bg,
        BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        Margin = new Thickness(10, 0, 0, 0)
    };

    // ==================================================================
    //  条目（作业 / 缺交 / 值日）：每条 = 科目输入框 + 内容输入框 + 删除
    // ==================================================================
    private DoodleBigEditEntry NewEntry(string subject, string content, string id = "", string extra = "") => new()
    {
        Id = id, Title = subject, Body = content, Extra = extra,
        // ★ 科目框共用 _subjectStyle：改任一条的科目格式，所有科目一起变（板上也只有一个科目格式）
        TitleStyle = _subjectStyle,
        BodyStyle = _bodyStyle.Clone(),
        ExtraStyle = _bodyStyle.Clone()
    };

    /// <summary>值日模块：每条三个字段（星期 / 姓名 / 值日内容），和原生值日编辑窗一致。</summary>
    private bool IsDuty => string.Equals(_target.Module, DoodleModules.Duty, StringComparison.OrdinalIgnoreCase);

    private void SeedEntries()
    {
        // ★ 整份模式：条目直接来自公示数据（每条带稳定 Id），一条记录就是一个科目。
        //   绝不按 Body 里的换行去猜条目 —— 那个换行只是内容排版，不是记录分隔符。
        if (_target.Entries.Count > 0)
        {
            foreach (var e in _target.Entries)
                _entries.Add(NewEntry(e.Title, e.Body, e.Id, e.Extra));
            return;
        }

        // 整份模式下一条都没有（公示还是空的）：补一条空记录，别按正文猜
        if (_target.WholeBoard)
        {
            _entries.Add(NewEntry("", "", "", ""));
            return;
        }

        if (!_target.MultiEntry)
        {
            _entries.Add(NewEntry("", _draft.Body));
            return;
        }

        // 老路径（值日等没有结构化条目来源的调用）：按行粗略拆分，只为版式能编辑
        var lines = string.IsNullOrEmpty(_draft.Body)
            ? new List<string> { "" }
            : _draft.Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        foreach (var line in lines)
        {
            var (subject, content) = SplitLine(line);
            _entries.Add(NewEntry(subject, content));
        }
        if (_entries.Count == 0) _entries.Add(NewEntry("", ""));
    }

    /// <summary>一行「科目：内容」拆成两个字段；没有分隔符时整行算内容。</summary>
    private static (string Subject, string Content) SplitLine(string line)
    {
        int i = line.IndexOfAny(new[] { '：', ':', '｜', '|' });
        if (i <= 0) return ("", line.Trim());
        return (line.Substring(0, i).Trim(), line.Substring(i + 1).Trim());
    }

    /// <summary>值日新加一条时挑一个还没用过的星期（周一到周日；都用过就回周一）。</summary>
    private string NextFreeDay()
    {
        var used = new HashSet<string>(_entries.Select(x => (x.Title ?? "").Trim()), StringComparer.Ordinal);
        for (int i = 1; i < DutyItem.DayNames.Length; i++)
            if (!used.Contains(DutyItem.DayNames[i])) return DutyItem.DayNames[i];
        return "周一";
    }

    /// <summary>一条记录里的普通输入框（单行、可换行两用）。</summary>
    private TextBox MakeEntryBox(string text, string tip, TextStyle style, bool multiline)
    {
        var box = new TextBox
        {
            Text = text, FontSize = 20, MinHeight = multiline ? 56 : 44,
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Background = Brushes.White, BorderBrush = InputBorder,
            ToolTip = tip, Tag = style
        };
        return box;
    }

    private void BuildEntries()
    {
        if (!_target.MultiEntry) return;
        _entryHost.Children.Clear();
        _fieldBoxes.Clear();
        _fieldBoxes.Add((_titleBox, _titleStyle));

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            int idx = i;
            var row = new Border
            {
                Background = Soft, BorderBrush = Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 0, 5)
            };
            var grid = new Grid();
            if (IsDuty)
            {
                // 值日三列：星期 / 值日生姓名 / 值日内容（可空）—— 对齐原生值日编辑窗
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (IsDuty)
            {
                // ① 星期：原生值日窗就是下拉；老数据里的「每天 / 周末」也保留成可选项
                var dayBox = new ComboBox
                {
                    FontSize = 18, MinHeight = 44, Padding = new Thickness(6),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Brushes.White, BorderBrush = InputBorder,
                    ToolTip = "星期"
                };
                foreach (var d in DutyItem.DayNames) dayBox.Items.Add(d);
                string cur = (entry.Title ?? "").Trim();
                if (cur.Length > 0 && !DutyItem.DayNames.Contains(cur)) dayBox.Items.Add(cur);
                if (cur.Length == 0) { cur = DutyItem.DayNames[1]; entry.Title = cur; }
                dayBox.SelectedItem = cur;
                dayBox.SelectionChanged += (_, _) =>
                {
                    if (dayBox.SelectedItem is string s) { entry.Title = s; ApplyNoteThemeAndPreview(); }
                };
                Grid.SetColumn(dayBox, 0);
                grid.Children.Add(dayBox);

                // ② 值日生姓名
                var namesBox = MakeEntryBox(entry.Body, "值日生姓名（多人用逗号分隔）",
                    entry.BodyStyle, false);
                namesBox.TextChanged += (_, _) => { entry.Body = namesBox.Text; ApplyNoteThemeAndPreview(); };
                namesBox.GotFocus += (_, _) =>
                    _binder?.BindAndActivate(namesBox, entry.BodyStyle, "值日生姓名");
                Grid.SetColumn(namesBox, 1);
                grid.Children.Add(namesBox);

                // ③ 值日内容 / 岗位（可空，如「扫地」）
                var dutyBox = MakeEntryBox(entry.Extra, "值日内容（可空，如 扫地）",
                    entry.ExtraStyle, false);
                dutyBox.TextChanged += (_, _) => { entry.Extra = dutyBox.Text; ApplyNoteThemeAndPreview(); };
                dutyBox.GotFocus += (_, _) =>
                    _binder?.BindAndActivate(dutyBox, entry.ExtraStyle, "值日内容");
                Grid.SetColumn(dutyBox, 2);
                grid.Children.Add(dutyBox);

                _fieldBoxes.Add((namesBox, entry.BodyStyle));
                _fieldBoxes.Add((dutyBox, entry.ExtraStyle));
            }
            else
            {
                var subjectBox = new TextBox
                {
                    Text = entry.Title, FontSize = 20, MinHeight = 44,
                    Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Brushes.White, BorderBrush = InputBorder,
                    ToolTip = "科目 / 名称", Tag = entry.TitleStyle
                };
                subjectBox.TextChanged += (_, _) => { entry.Title = subjectBox.Text; ApplyNoteThemeAndPreview(); };
                subjectBox.GotFocus += (_, _) =>
                    _binder?.BindAndActivate(subjectBox, entry.TitleStyle, "科目");
                Grid.SetColumn(subjectBox, 0);
                grid.Children.Add(subjectBox);

                var contentBox = MakeEntryBox(entry.Body, "内容（可回车换行）", entry.BodyStyle, true);
                contentBox.TextChanged += (_, _) => { entry.Body = contentBox.Text; ApplyNoteThemeAndPreview(); };
                contentBox.GotFocus += (_, _) =>
                    _binder?.BindAndActivate(contentBox, entry.BodyStyle, "内容");
                Grid.SetColumn(contentBox, 1);
                grid.Children.Add(contentBox);

                _fieldBoxes.Add((subjectBox, entry.TitleStyle));
                _fieldBoxes.Add((contentBox, entry.BodyStyle));
            }

            var del = new Button
            {
                Content = "删除", FontSize = 13, MinHeight = 40,
                Padding = new Thickness(10, 0, 10, 0),
                Background = DangerSoft, Foreground = Danger, BorderBrush = DangerBorder,
                Cursor = Cursors.Hand, IsEnabled = _entries.Count > 1
            };
            del.Click += (_, _) => { _entries.RemoveAt(idx); BuildEntries(); ApplyNoteThemeAndPreview(); };
            Grid.SetColumn(del, IsDuty ? 3 : 2);
            grid.Children.Add(del);

            row.Child = grid;
            _entryHost.Children.Add(row);
        }
        ApplyFieldVisuals();
    }

    /// <summary>格式一改，所有输入框立刻按各自 TextStyle 变字号 / 粗细 / 颜色（所见即所得）。</summary>
    private void ApplyFieldVisuals()
    {
        foreach (var (box, st) in _fieldBoxes)
        {
            // ★ 总标题输入框保持普通 16 号（参考悬浮便签编辑窗的「总标题」就是普通字）：
            //   上一版让它跟着格式条涨到 52 号，40px 高的输入框直接把字切成半截。
            //   正文 / 科目 / 内容框跟随档位，但夹到 26 以内 —— 输入框高度有限，
            //   真正的大字号在顶部预览卡里看，那里才是「教室后排效果」。
            bool isTitle = ReferenceEquals(box, _titleBox);
            box.FontSize = isTitle ? 16 : Math.Clamp(DoodleStyle.BigSizeOf(st.SizeLevel), 13, 26);
            box.FontWeight = st.Bold ? FontWeights.Bold : FontWeights.Normal;
            box.Foreground = string.IsNullOrWhiteSpace(st.Color) ? Ink : Hex(st.Color);
        }
    }

    /// <summary>预览 + 便签风格一起刷新：主题改的是这张卡的底色，不是黑板材质。</summary>
    private void ApplyNoteThemeAndPreview()
    {
        string key = (_themeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                     ?? _draft.NoteTheme;
        _draft.NoteTheme = key;
        var th = HomeworkThemes.Find(key);

        _previewCard.Background = Hex(th.Background);
        _previewCard.BorderBrush = Hex(th.BorderColor);
        _pvTitle.Text = _titleBox.Text;
        // 预览卡固定高度，88 号原样画会把正文挤进滚动条截掉半行；这里夹到「明显很大但仍完整」，
        // 真实 88 号效果由黑板上的便签呈现（BuildNoteCard 用的是不夹取的 DIP 字号）。
        _pvTitle.FontSize = Math.Clamp(DoodleStyle.BigSizeOf(_titleStyle.SizeLevel), 16, 44);
        _pvTitle.FontWeight = _titleStyle.Bold ? FontWeights.Bold : FontWeights.Normal;
        _pvTitle.Foreground = string.IsNullOrWhiteSpace(_titleStyle.Color)
            ? Hex(th.TitleColor) : Hex(_titleStyle.Color);

        // ★ r104：预览正文按行画 —— 科目与内容各用各的格式，调科目格式时一眼能看到区别。
        //   原来整段塞进 Text + 换行，科目和内容永远同一种字，改科目格式在预览里毫无反应。
        double fsS = Math.Clamp(DoodleStyle.BigSizeOf(_subjectStyle.SizeLevel), 14, 34);
        double fsB = Math.Clamp(DoodleStyle.BigSizeOf(_bodyStyle.SizeLevel), 14, 32);
        Brush bodyBrush = string.IsNullOrWhiteSpace(_bodyStyle.Color) ? Hex(th.Foreground) : Hex(_bodyStyle.Color);
        Brush subjBrush = string.IsNullOrWhiteSpace(_subjectStyle.Color) ? bodyBrush : Hex(_subjectStyle.Color);

        _pvBody.Inlines.Clear();
        _pvBody.FontSize = fsB;
        _pvBody.FontWeight = _bodyStyle.Bold ? FontWeights.Bold : FontWeights.Normal;
        _pvBody.Foreground = bodyBrush;
        // 行高给到「较大那个字号」的 1.6 倍：科目比内容大时不会上下贴住
        _pvBody.LineHeight = Math.Round(Math.Max(fsS, fsB) * 1.6, 1);
        _pvBody.LineStackingStrategy = LineStackingStrategy.MaxHeight;

        var subjWeight = _subjectStyle.Bold ? FontWeights.Bold : FontWeights.Normal;
        if (_singleBodyBox != null || !_target.MultiEntry)
        {
            // 课表是只读整段文本，但每行开头的「第N节」也是科目位：拆出来单独套科目格式，
            // 否则「设置第N节格式」在预览里毫无反应。
            bool isSchedule = string.Equals(_target.Module, DoodleModules.Schedule,
                StringComparison.OrdinalIgnoreCase);
            var parts = PreviewBodyText().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) _pvBody.Inlines.Add(new LineBreak());
                if (isSchedule && TrySplitPeriod(parts[i], out string period, out string rest))
                {
                    _pvBody.Inlines.Add(new Run(period + " ")
                    {
                        FontSize = fsS, FontWeight = subjWeight, Foreground = subjBrush
                    });
                    _pvBody.Inlines.Add(new Run(rest));
                }
                else
                {
                    _pvBody.Inlines.Add(new Run(parts[i]));
                }
            }
            return;
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (i > 0) _pvBody.Inlines.Add(new LineBreak());
            string s = (e.Title ?? "").Trim();
            string b = (e.Body ?? "").Trim();
            string x = (e.Extra ?? "").Trim();

            if (IsDuty)
            {
                string left = x.Length == 0 ? b : (b.Length == 0 ? x : x + "：" + b);
                _pvBody.Inlines.Add(new Run(s.Length == 0 ? left : s + "  " + left));
                continue;
            }
            if (s.Length > 0)
            {
                _pvBody.Inlines.Add(new Run(s)
                {
                    FontSize = fsS, FontWeight = subjWeight, Foreground = subjBrush
                });
                if (b.Length > 0)
                    _pvBody.Inlines.Add(new Run("：") { FontSize = fsS, Foreground = subjBrush });
            }
            if (b.Length > 0) _pvBody.Inlines.Add(new Run(b));
        }
    }

    /// <summary>把「第3节 数学」拆成节次与课程名；不是这个形态就返回 false。</summary>
    private static bool TrySplitPeriod(string line, out string period, out string rest)
    {
        period = "";
        rest = (line ?? "").Trim();
        string s = rest;
        if (s.Length < 3 || s[0] != '第') return false;
        int i = s.IndexOf('节');
        if (i < 2 || i > 4) return false;
        period = s.Substring(0, i + 1);
        rest = s.Substring(i + 1).Trim();
        return true;
    }

    private string PreviewBodyText()
    {
        if (_singleBodyBox != null) return _singleBodyBox.Text;
        if (!_target.MultiEntry) return _entries.Count > 0 ? _entries[0].Body : "";

        if (IsDuty)
            return string.Join("\n", _entries.Select(e =>
            {
                string day = (e.Title ?? "").Trim();
                string names = (e.Body ?? "").Trim();
                string duty = (e.Extra ?? "").Trim();
                if (day.Length == 0 && names.Length == 0 && duty.Length == 0) return "";
                string left = duty.Length == 0 ? names
                    : (names.Length == 0 ? duty : duty + "：" + names);
                return day.Length == 0 ? left : day + "  " + left;
            }).Where(x => x.Length > 0));

        return string.Join("\n", _entries.Select(e =>
        {
            string s = (e.Title ?? "").Trim();
            string b = (e.Body ?? "").Trim();
            if (s.Length == 0) return b;
            return b.Length == 0 ? s : s + "：" + b;
        }));
    }

    /// <summary>多条记录用换行拼回正文：★ 回车强制换行原样保留，不压成空格。</summary>
    private string JoinEntries()
    {
        var lines = new List<string>();
        foreach (var e in _entries)
        {
            string s = (e.Title ?? "").Trim();
            string b = (e.Body ?? "").Trim();
            string x = (e.Extra ?? "").Trim();

            if (IsDuty)
            {
                if (s.Length == 0 && b.Length == 0 && x.Length == 0) continue;
                string left = x.Length == 0 ? b : (b.Length == 0 ? x : x + "：" + b);
                lines.Add(s.Length == 0 ? left : s + "  " + left);
                continue;
            }

            if (s.Length == 0 && b.Length == 0) continue;
            if (s.Length == 0) lines.Add(b);
            else lines.Add(b.Length == 0 ? s : s + "：" + b);
        }
        return string.Join("\n", lines);
    }

    /// <summary>把界面上的值收进 Draft。★ 不碰整块黑板主题（SaveTheme 恒 false）。</summary>
    private void Commit()
    {
        _draft.Title = (_titleBox.Text ?? "").Trim();
        // 整份模式的「公示总标题」允许留空（就沿用原来的），不要硬造一个「未命名」写回 board.Title
        if (!_target.WholeBoard && string.IsNullOrWhiteSpace(_draft.Title)) _draft.Title = "未命名";
        _draft.Body = _target.MultiEntry
            ? JoinEntries()
            : (_singleBodyBox?.Text ?? _draft.Body);

        _draft.Entries = _entries.Select(e => new DoodleBigEditEntry
        {
            Id = e.Id ?? "", Title = e.Title ?? "", Body = e.Body ?? "", Extra = e.Extra ?? "",
            TitleStyle = e.TitleStyle.Clone(), BodyStyle = e.BodyStyle.Clone(),
            ExtraStyle = e.ExtraStyle.Clone()
        }).ToList();

        _draft.TitleSize = DoodleStyle.BigSizeOf(_titleStyle.SizeLevel);
        _draft.ContentSize = DoodleStyle.BigSizeOf(_bodyStyle.SizeLevel);
        _draft.TitleColor = _titleStyle.Color;
        _draft.BodyColor = _bodyStyle.Color;
        _draft.TitleBold = _titleStyle.Bold;
        _draft.BodyBold = _bodyStyle.Bold;
        // ★ r104：科目格式（作业 / 缺交的科目名、课表的第N节）
        _draft.SubjectSize = DoodleStyle.BigSizeOf(_subjectStyle.SizeLevel);
        _draft.SubjectColor = _subjectStyle.Color;
        _draft.SubjectBold = _subjectStyle.Bold;
        _draft.Locked = _lockCheck.IsChecked == true;
        _draft.SaveStyle = true;
        _draft.SaveNoteTheme = true;
        _draft.SaveTheme = false;
        _draft.NoteTheme = (_themeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    }

    // ---------- 诊断探针（--doodle-check）----------
    internal bool DiagPickerIsBigScreen => _picker.BigScreen;
    internal bool DiagPickerFollowsFocus => _binder != null;
    internal int DiagNoteThemeCount => _themeCombo.Items.Count;
    internal int DiagEntryFieldCount => _fieldBoxes.Count;
    internal int DiagEntryCount => _entries.Count;
    internal bool DiagIsWholeBoard => _target.WholeBoard;
    internal string DiagEntryId(int index) =>
        index >= 0 && index < _entries.Count ? _entries[index].Id ?? "" : "";
    internal string DiagAddButtonText => _addBtn.Content?.ToString() ?? "";
    internal bool DiagThemeDoesNotTouchBoard => !_draft.SaveTheme;
    internal void DiagSetSingleBody(string text)
    {
        if (_singleBodyBox != null) _singleBodyBox.Text = text;
    }
    internal void DiagSetSubject(int index, string subject, string content)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries[index].Title = subject;
        _entries[index].Body = content;
    }
    internal void DiagSetDuty(int index, string day, string names, string duty)
    {
        if (index < 0 || index >= _entries.Count) return;
        _entries[index].Title = day;
        _entries[index].Body = names;
        _entries[index].Extra = duty;
    }
    internal string DiagEntryExtra(int index) =>
        index >= 0 && index < _entries.Count ? _entries[index].Extra ?? "" : "";
    internal bool DiagIsDuty => IsDuty;
    internal void DiagCommit() => Commit();

    // ---------- 共用画刷 ----------
    private static readonly Brush Primary = Hex("#1E4FA3");
    private static readonly Brush PrimarySoft = Hex("#EAF1FD");
    private static readonly Brush Danger = Hex("#C0392B");
    private static readonly Brush DangerSoft = Hex("#FDEDEC");
    private static readonly Brush DangerBorder = Hex("#F0B7AE");
    private static readonly Brush InputBorder = Hex("#C7D0DC");
    private static readonly Brush Ink = Hex("#25303D");
    private static readonly Brush Muted = Hex("#667085");
    private static readonly Brush Line = Hex("#E1E6EE");
    private static readonly Brush Soft = Hex("#F4F7FB");

    private static Brush Hex(string s)
    {
        var c = (Color)ColorConverter.ConvertFromString(s);
        return new SolidColorBrush(c);
    }
}
