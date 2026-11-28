using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Wpf = System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 教师机「新建变更单」：只描述「改哪一条、改成什么」，不发整张表。
///
/// <para>★ 目标条目来自<b>教室机发布的内容快照</b>，不是教师机本机数据。
/// 办公室教师机本机通常没有教室机的作业 / 缺交内容，若用本机数据填下拉，
/// 目标必然为空 → 教室机选「替换」时找不到目标 → 悄悄退化成「新增」。
/// 没有快照时这里会明确提示，并允许教室机应用时现场指定目标。</para>
///
/// <para>发送前逐条走 UserSession.CanEditBoardItem（班级 + 任课科目双重限权）。</para>
/// </summary>
public class BoardSendWindow : Window
{
    /// <summary>
    /// 目标班级胶囊（r169）。原来是一个只读文本框写「八年级(1)班、八年级(2)班」，
    /// 多班时挤成一串逗号文本；现在按设计图改成「一个班一颗蓝底胶囊 + 右边灰色计数」。
    /// </summary>
    readonly Wpf.WrapPanel _classChips = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Wpf.TextBlock _classCount = new()
    {
        FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
    };
    readonly Wpf.ComboBox _entity = new();

    // 窗内临时增加班级：默认已带出账号所属班级，老师还能给别的班发。
    // ★ IsEditable = 能从下拉里选，也能直接手打一个名单外的班级名（一体机上没有右键菜单可用）。
    readonly Wpf.ComboBox _classInput = new()
    {
        FontSize = 13, IsEditable = true, IsTextSearchEnabled = true,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "从下拉里选一个班，或直接输入新班级名，再点「添加班级」"
    };
    readonly Wpf.Button _addClass = new()
    {
        Content = "添加班级", FontSize = 13, Padding = new Thickness(12, 6, 12, 6),
        MinHeight = 34, Margin = new Thickness(8, 0, 0, 0),
        ToolTip = "临时给这个班也发一份；只在本软件本次运行期间生效，不会改用户表"
    };
    readonly Wpf.Button _removeClass = new()
    {
        Content = "移除", FontSize = 13, Padding = new Thickness(12, 6, 12, 6),
        MinHeight = 34, Margin = new Thickness(6, 0, 0, 0),
        ToolTip = "把上面选中的班级从本次发布目标里去掉（至少保留一个班）"
    };
    readonly Wpf.ComboBox _op = new();
    readonly Wpf.ComboBox _item = new();

    /// <summary>值日专用：星期（每天 / 周一…周日）。只有内容类型是「值日安排」时才显示。</summary>
    readonly Wpf.ComboBox _day = new() { Width = 140 };
    readonly Wpf.StackPanel _dayRow = new();

    /// <summary>
    /// 值日「一次安排一周」：勾上后展开周一…周日 7 行表格，一次发布一张整周变更单。
    /// <para>★ 与单条模式的控件互斥显示；空行跳过，老师只填需要安排的那几天。</para>
    /// </summary>
    readonly Wpf.CheckBox _weekMode = new()
    {
        Content = "一次安排一周（展开 7 天表格，一张变更单发整周）",
        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "一周值日通常一起换：这里按星期逐行填，教室机只裁决一次、撤回也整周一起撤"
    };
    readonly Wpf.StackPanel _weekPanel = new();
    /// <summary>整周值日裁决方式：问号挂在「一次安排一周」勾选框那一行的行末（r77）。</summary>
    readonly Controls.HelpBubble _weekBubble = new()
    {
        PopupTitle = "整周值日的裁决方式",
        Text = "教室机裁决：「追加整周」= 保留原表、把这几天都加进去；" +
               "「整表换成这一周」= 旧值日全部存档后换成这张表（可整周一键撤回）。"
    };
    Wpf.StackPanel? _weekSwitch;
    readonly List<(string Day, Wpf.TextBox Duty, Wpf.TextBox Names)> _weekInputs = new();

    /// <summary>通知专用「直接增加」：教室机自动收下，不用电教管理员点确认。</summary>
    readonly Wpf.CheckBox _auto = new()
    {
        Content = "直接增加（教室机自动收下，无需电教管理员确认）",
        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "只会往教室机新增一条通知，不替换 / 不删除原有内容；删除或替换仍必须由电教管理员确认"
    };
    readonly Wpf.StackPanel _autoRow = new();
    readonly Controls.HelpBubble _autoBubble = new()
    {
        PopupTitle = "直接增加",
        Text = "勾上后教室机收到就自动按「增加」落地并回执，台账记「已应用」，仍可撤回。\n\n" +
               "注意：自动增加是「新增一条通知」，不是把教室里原有那条改掉；" +
               "要改旧通知请取消勾选并选中目标条目。"
    };
    bool _autoTouched;

    /// <summary>截止日期（仅通知显示）：日历选到哪天为止，默认今天。
    /// <para>★ 允许留空 = 长期有效 —— 比再塞一个「长期有效」勾选框少一个控件，
    /// 而且「不填就是不过期」是办公软件里大家都懂的约定。</para></summary>
    readonly Wpf.DatePicker _due = new()
    {
        FontSize = 13, Width = 190, VerticalAlignment = VerticalAlignment.Center,
        SelectedDateFormat = Wpf.DatePickerFormat.Long, FirstDayOfWeek = DayOfWeek.Monday
    };
    readonly Wpf.StackPanel _dueRow = new();
    readonly Wpf.TextBlock _dueHint = new()
    {
        Text = "留空 = 长期有效",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
    };

    /// <summary>「覆盖同标题的旧通知」：由发布老师逐条决定，默认不勾。</summary>
    readonly Wpf.CheckBox _overwrite = new()
    {
        Content = "覆盖同标题的旧通知（旧的这条不再显示，可在编辑窗恢复）",
        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "只影响同班范围内标题完全相同的旧通知；置顶的旧通知不会被覆盖。" +
                  "默认不勾 —— 勾错的代价是上周还没过期的重要通知被静默顶掉，比多一条重复严重得多。"
    };
    readonly Wpf.StackPanel _overwriteRow = new();

    /// <summary>需要按周模式 / 自动增加一起隐藏的那几行（BuildUi 里赋值）。
    /// r169：类型从 StackPanel 改成 Grid —— 行改成「标签列 + 内容列」两列布局。</summary>
    Wpf.Grid? _fieldRow1, _fieldRow2, _itemRow, _reminderRow;
    Controls.HelpBubble? _itemHint;

    readonly Wpf.TextBox _field1 = new();
    readonly Wpf.TextBox _field2 = new();
    readonly Wpf.TextBlock _lab1 = new();
    readonly Wpf.TextBlock _lab2 = new();
    readonly Wpf.TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap };
    readonly Wpf.TextBlock _snapshotInfo = new() { TextWrapping = TextWrapping.Wrap };
    readonly Wpf.TextBlock _targetHint = new() { TextWrapping = TextWrapping.Wrap };

    // r71：发布窗不再设字体格式条 —— 教室机的字体由它自己的便签设置决定，
    // 「替换」沿用被覆盖条目的样式，「新增」用出厂的特大 + 默认墨色。
    // BoardChangeOrder.Styles 字段仍保留，只为兼容历史消息与老版教室机。

    readonly Wpf.CheckBox _force = new()
    {
        Content = "课间全屏提醒",
        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "勾上后教室机会在课间 / 放学后用整屏大字提示一次；上课时间绝不打断课堂。" +
                  "全屏内容自动取本次发送的标题与完整正文，不用另填"
    };
    // 语音提醒：把这条信息在教室里念一遍（讲台离得远、学生未必看得到小卡片）。
    // 通知默认勾上；时机默认「课间播」，最急的可以改成「收到就播」。
    readonly Wpf.CheckBox _voice = new()
    {
        Content = "语音提醒",
        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "勾上后教室机用系统语音把播报内容念一遍；发布本班通知时默认勾上"
    };
    readonly Wpf.ComboBox _voiceWhen = new()
    {
        Width = 148, FontSize = 12, Margin = new Thickness(10, 0, 0, 0),
        ToolTip = "课间播 = 上课时间先排队，下课再念（默认）；收到就播 = 上课也立刻念，只给真正紧急的通知用"
    };
    /// <summary>
    /// 播报文字。r167：改成「按内容自动长高」——
    /// 通知念的是整段正文，原来那一个固定单行框只能露出十来个字，
    /// 老师根本看不出教室机等会儿会念什么。
    /// <para>★ 不设 Height，靠 MinLines / MaxLines 让它自己量：1 行起步、最多长到 5 行，
    /// 再长就在框里滚动。设了 Height 就不会自适应了。</para>
    /// <para>★ AcceptsReturn 保持 false：Enter 不该往播报词里插换行，
    /// 换行只由自动折行产生（TextWrapping=Wrap 已经能触发多行样式）。</para>
    /// </summary>
    readonly Wpf.TextBox _voiceText = new()
    {
        FontSize = 12, TextWrapping = TextWrapping.Wrap,
        MinLines = 1, MaxLines = 5,
        VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto
    };
    bool _voiceTouched;        // 用户亲手改过勾选：切换内容类型时不再自动勾 / 取消
    bool _voiceTextTouched;    // 用户亲手改过播报文字：不再用默认文案覆盖
    bool _voiceSync;           // 代码在改勾选 / 文字（不是用户操作），别把事件当成用户改动
    readonly Wpf.TextBox _preview = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
        Height = 132, FontSize = 12, Padding = new Thickness(8),
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD))
    };

    BoardSnapshot? _snapshot;
    bool _loading;
    bool _opSync;      // 代码重建「操作」下拉时，别把 SelectionChanged 当成用户改了操作
    readonly List<string> _classes = new();
    readonly string? _presetEntity;

    /// <summary>
    /// 确定后的变更单：一个班一条（多班批量发布时每个班各一条，Id / MsgId 各自独立）。
    /// 交给 BoardSyncWindow / 信息管理页统一逐条 Send()。
    /// </summary>
    public List<BoardChangeOrder> Results { get; } = new();

    /// <summary>兼容旧的调用点：取第一条（单班发布时就是唯一一条）。</summary>
    public BoardChangeOrder? Result => Results.FirstOrDefault();

    // ---- 诊断（--ui-check）用：只读暴露几个联动控件，确认「值日安排」那一套真的接上了 ----
    internal Wpf.ComboBox EntityBox => _entity;
    internal Wpf.ComboBox DayBox => _day;
    internal Wpf.StackPanel DayRow => _dayRow;
    internal Wpf.CheckBox WeekModeBox => _weekMode;
    internal Wpf.StackPanel WeekPanel => _weekPanel;
    internal int WeekInputRows => _weekInputs.Count;
    internal Wpf.CheckBox AutoBox => _auto;
    internal Wpf.StackPanel AutoRow => _autoRow;
    internal Wpf.DatePicker DueBox => _due;
    internal Wpf.CheckBox OverwriteBox => _overwrite;
    internal bool DueRowVisible => _dueRow.Visibility == Visibility.Visible;
    internal bool OverwriteRowVisible => _overwriteRow.Visibility == Visibility.Visible;
    // r71 起格式条整行移除，恒 false；留着这个属性让历史诊断断言继续有意义（"不该显示格式条"）。
    internal bool FormatRowsVisible => false;
    internal int ClassCount => _classes.Count;
    internal Wpf.ComboBox ClassInputBox => _classInput;
    /// <summary>诊断用：目标班级胶囊（r169 起一个班一颗）。</summary>
    internal Wpf.WrapPanel ClassChips => _classChips;
    internal Wpf.TextBlock ClassCountText => _classCount;
    internal Wpf.Button AddClassButton => _addClass;
    /// <summary>诊断用：提醒那一行（r169 起是 Grid，不是 StackPanel —— 按 Panel 取 Children 才不挑容器）。</summary>
    internal Wpf.Panel? ReminderRow => _reminderRow;
    internal Wpf.CheckBox ForceBox => _force;
    internal Wpf.CheckBox VoiceBox => _voice;
    internal bool WeekPanelVisible => _weekPanel.Visibility == Visibility.Visible;
    internal bool AutoRowVisible => _autoRow.Visibility == Visibility.Visible;
    // r71 诊断：全屏 / 语音的默认文案与输入框，用来锁住「通知念完整、其余念摘要」这条规则
    internal Wpf.TextBox Field1Box => _field1;
    internal Wpf.TextBox Field2Box => _field2;
    internal string DiagFullText => ComposeFullText();
    internal string DiagVoiceText => ComposeDefaultVoiceText();
    internal string DiagVoiceInput => _voiceText.Text ?? "";
    /// <summary>诊断用：播报框本身 —— 要验「不写死高度、开了自动换行」这两条。</summary>
    internal Wpf.TextBox VoiceTextBox => _voiceText;

    /// <summary>诊断用：程序化切换「一次安排一周」并立刻重画（Click 事件不会因代码赋值而触发）。</summary>
    internal void SetWeekModeForDiag(bool on)
    {
        _weekMode.IsChecked = on;
        Relabel();
    }

    /// <summary>
    /// classes = 信息管理页里勾选的任教班级（可单选 / 多选）；为空时回退到本机显示班级。
    /// entity = 从三张内容卡片点进来时预选的内容类型。
    /// </summary>
    public BoardSendWindow(IEnumerable<string>? classes = null, string? entity = null)
    {
        Title = "发布信息 · 新建班级内容变更单";
        // r169：宽度 700 → 760。设计图是「标签列 + 满宽控件」两列布局，
        // 700 时内容列只剩 570，目标条目那行「下拉 + 读取按钮 + 问号」挤得贴边。
        Width = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xF9));

        // 优先尊重调用方在「信息管理」页勾选的班级（老师明确选过就不擅自加别的班）。
        if (classes != null)
        {
            foreach (var c in classes)
                if (!string.IsNullOrWhiteSpace(c) && !_classes.Contains(c.Trim(), StringComparer.Ordinal))
                    _classes.Add(c.Trim());
        }
        // 没勾选时默认带出「当前账号的所属班级」：首次登录不再需要先去信息管理页手工添加班级。
        if (_classes.Count == 0)
        {
            foreach (var c in UserSession.SessionClassNames)
                if (!string.IsNullOrWhiteSpace(c) && !_classes.Contains(c.Trim(), StringComparer.Ordinal))
                    _classes.Add(c.Trim());
        }
        // 账号也没配班级（游客 / 诊断）时退回本机当前显示班级，保证窗口仍可用、诊断可构造。
        if (_classes.Count == 0)
        {
            string own = (Store.LoadSettings().SelectedClass ?? "").Trim();
            if (own.Length > 0) _classes.Add(own);
        }
        _presetEntity = entity;

        BuildUi();
        // r166：窗内所有 TextBox 在加载后统一成圆角输入框（各控件本地设过的 Height 仍然优先）
        Loaded += (_, _) => ApplyRoundedInputs(this);
    
        GlassChrome.Prepare(this);}

    // ---------------- r166：黑板发送窗视觉收敛 ----------------

    /// <summary>
    /// 单行输入框的圆角外观。WPF 默认 TextBox 模板是直角 + 系统描边，和主窗口的卡片风完全不像一个程序；
    /// 这里只做「圆角 8 + 1px 输入描边 + 聚焦时主色」，其余全部交给控件自己的本地值。
    /// </summary>
    static readonly Style RoundedInput = BuildRoundedInput(multiline: false);

    /// <summary>多行输入框（作业内容）：同样的框，但内距上下留 6，不锁高度。</summary>
    static readonly Style RoundedInputMulti = BuildRoundedInput(multiline: true);

    static Style BuildRoundedInput(bool multiline)
    {
        string height = multiline ? "" : "<Setter Property='Height' Value='32'/>";
        string padding = multiline ? "8,6" : "8,0";
        string vertical = multiline ? "Stretch" : "Center";
        string xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
            xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type TextBox}'>"
            + height +
            @"<Setter Property='Padding' Value='" + padding + @"'/>
          <Setter Property='VerticalContentAlignment' Value='" + vertical + @"'/>
          <Setter Property='BorderThickness' Value='1'/>
          <Setter Property='BorderBrush' Value='#C7CFDA'/>
          <Setter Property='Background' Value='White'/>
          <Setter Property='Foreground' Value='#1B2836'/>
          <Setter Property='Template'>
            <Setter.Value>
              <ControlTemplate TargetType='{x:Type TextBox}'>
                <Border x:Name='Bd' CornerRadius='8' Background='{TemplateBinding Background}'
                        BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
                  <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsKeyboardFocusWithin' Value='True'>
                    <Setter TargetName='Bd' Property='BorderBrush' Value='#1E4FA3'/>
                  </Trigger>
                  <Trigger Property='IsEnabled' Value='False'>
                    <Setter TargetName='Bd' Property='Background' Value='#F4F6FA'/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>
    /// 递归套用输入框 / 下拉框外观。
    /// <para>r166 只处理 TextBox —— 当时怕换 ComboBox 模板把「可编辑下拉」弄坏。</para>
    /// <para>r167：ComboBox 一并用 AppCombo（完整模板，PART_EditableTextBox 已写进模板里），
    /// 可编辑的那个班级下拉照常能打字、回车、回填。</para>
    /// <para>★ ComboBox 分支必须 continue，不能往里递归：模板里的 PART_EditableTextBox 也是 TextBox，
    /// 递归进去会被套上 RoundedInput，可编辑下拉就变成「圆角框套圆角框」。</para>
    /// </summary>
    void ApplyRoundedInputs(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Wpf.TextBox tb)
            {
                tb.Style = (tb.AcceptsReturn || tb.TextWrapping != TextWrapping.NoWrap)
                    ? RoundedInputMulti : RoundedInput;
            }
            else if (child is Wpf.ComboBox cb)
            {
                if (TryFindResource("AppCombo") is Style comboStyle) cb.Style = comboStyle;
                continue;
            }
            ApplyRoundedInputs(child);
        }
    }

    /// <summary>
    /// 行标签。r169：按设计图「固定列宽 88 + 文字右对齐 + 右留 12」——
    /// 不同字数的标签（科目 / 目标条目 / 教室机现状）右边缘齐一条竖线，
    /// 紧贴着输入框，扫读时不用来回找控件从哪开始。
    /// </summary>
    static Wpf.TextBlock Lab(string text)
    {
        var t = new Wpf.TextBlock { Text = text };
        StyleLab(t);
        return t;
    }

    /// <summary>
    /// 把「行标签」那套外观套到任意 TextBlock 上。
    /// <para>★ 字段行（科目 / 作业内容）的标签文字是按内容类型动态改的（通知变「通知标题」），
    /// 它们是字段里 new 出来的，不能走 <see cref="Lab"/>，必须在 BuildUi 里补一次同样的样式，
    /// 否则短标签（「科目」）会左对齐吊在列中间，和上下行对不齐。</para>
    /// </summary>
    static void StyleLab(Wpf.TextBlock t)
    {
        t.FontSize = 13;
        t.Width = LabelWidth - 12;
        t.TextAlignment = TextAlignment.Right;
        t.Margin = new Thickness(0, 0, 12, 0);
        t.VerticalAlignment = VerticalAlignment.Top;
        t.Padding = new Thickness(0, 8, 0, 0);   // 与输入框首行文字基线对齐（框高 34，行高约 20）
        t.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x54, 0x67));
    }

    /// <summary>标签列宽。表单项左边距 = 这个值（缩进的整行 / 提示都用 FormIndent）。</summary>
    const double LabelWidth = 88;

    /// <summary>表单项左边距 = 标签列宽。缩进的整行（勾选框 / 提示）都用它，别再手写数字。</summary>
    const double FormIndent = LabelWidth;

    /// <summary>
    /// 一行表单：第 0 列标签（固定 <see cref="LabelWidth"/>），第 1 列主控件（吃满剩余宽度），
    /// 之后每多一个元素各占一列 Auto（按钮 / 问号这类按自身宽度）。
    /// <para>★ r169 改成 Grid 两列：控件设横向 Stretch 就不会像原来那样靠手写 300 / 420 撑宽度，
    /// 窗口一有滚动条（内容高时出现）也不会把右侧控件挤出可视区。</para>
    /// </summary>
    static Wpf.Grid Row(UIElement lab, params UIElement[] content)
    {
        var g = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(LabelWidth) });
        g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 1; i < content.Length; i++)
            g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });

        Wpf.Grid.SetColumn(lab, 0);
        g.Children.Add(lab);
        for (int i = 0; i < content.Length; i++)
        {
            Wpf.Grid.SetColumn(content[i], i + 1);
            g.Children.Add(content[i]);
        }
        return g;
    }

    /// <summary>缩进到控件列的一整行（勾选框、行内提示、次要说明都挂这个 Margin）。</summary>
    static Thickness Indent(double top = 0, double bottom = 12)
        => new(FormIndent, top, 0, bottom);

    // ---------------- 目标班级（默认账号所属班级，窗内可临时增加） ----------------

    /// <summary>下拉候选 = 账号所属班级 + 信息管理页的任教班级 + 本次已选班级（去重）。</summary>
    IEnumerable<string> ClassCandidates()
    {
        var seen = new List<string>();
        void Add(string? c)
        {
            string v = (c ?? "").Trim();
            if (v.Length > 0 && !seen.Contains(v, StringComparer.Ordinal)) seen.Add(v);
        }
        foreach (var c in UserSession.SessionClassNames) Add(c);
        string extra = Store.LoadSettings().BoardSyncExtraClasses ?? "";
        foreach (var c in extra.Split(new[] { ',', '，', '、', ';', '；', '\n', '\r' },
                                      StringSplitOptions.RemoveEmptyEntries)) Add(c);
        foreach (var c in _classes) Add(c);
        return seen;
    }

    /// <summary>重画班级行：胶囊、下拉候选、操作下拉（多班时不允许「删除一条」）。</summary>
    void RefreshClassUi()
    {
        // r169：一个班一颗胶囊（蓝底圆角 + 小圆点），右边灰色计数。
        // 原来是只读文本框写「八年级(1)班、八年级(2)班」，班一多就成一串逗号文本，看不出到底几个班。
        _classChips.Children.Clear();
        foreach (var c in _classes)
        {
            var chip = new Wpf.Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xEF, 0xF9)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xD2, 0xE9)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "本次会发给这个班；用下面一行临时增加 / 移除，只在这次运行期间有效"
            };
            var inner = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal };
            inner.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3))
            });
            inner.Children.Add(new Wpf.TextBlock
            {
                Text = c, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x3A, 0x7A))
            });
            chip.Child = inner;
            _classChips.Children.Add(chip);
        }
        _classCount.Text = $"已加入 {_classes.Count} 个";

        _classInput.Items.Clear();
        foreach (var c in ClassCandidates()) _classInput.Items.Add(c);
        if (_classInput.Items.Count > 0) _classInput.SelectedIndex = 0;
        RefreshOpItems();
    }

    /// <summary>多班批量发布只能「新增」：替换 / 删除必须指到某一台教室机里那一条的具体 Id。</summary>
    void RefreshOpItems()
    {
        string cur = Op;
        _opSync = true;
        try
        {
            _op.Items.Clear();
            _op.Items.Add(new Wpf.ComboBoxItem { Content = "新增 / 修改一条", Tag = BoardSyncConsts.OpUpsert });
            if (_classes.Count <= 1)
                _op.Items.Add(new Wpf.ComboBoxItem { Content = "删除一条", Tag = BoardSyncConsts.OpDelete });
            int idx = 0;
            for (int i = 0; i < _op.Items.Count; i++)
                if ((_op.Items[i] as Wpf.ComboBoxItem)?.Tag as string == cur) { idx = i; break; }
            _op.SelectedIndex = idx;
        }
        finally { _opSync = false; }
    }

    void AddClassFromInput()
    {
        string v = (_classInput.Text ?? "").Trim();
        if (v.Length == 0) { _msg.Text = "请先选一个班级或输入班级名，再点「添加班级」。"; return; }
        if (_classes.Contains(v, StringComparer.Ordinal))
        { _msg.Text = "「" + v + "」已经在目标班级里了。"; return; }
        _classes.Add(v);
        // 加班级即扩权，但只在本次会话内：users.json 一个字节都不动，换账号 / 退出即失效。
        UserSession.GrantSessionClass(v);
        _msg.Text = "";
        RefreshClassUi();
        Relabel();
        RefreshSnapshot();
    }

    void RemoveClassFromInput()
    {
        string v = (_classInput.Text ?? "").Trim();
        if (!_classes.Contains(v, StringComparer.Ordinal))
        { _msg.Text = "「" + v + "」不在当前目标班级里。"; return; }
        if (_classes.Count <= 1) { _msg.Text = "至少要保留一个目标班级。"; return; }
        _classes.Remove(v);
        _msg.Text = "";
        RefreshClassUi();
        Relabel();
        RefreshSnapshot();
    }

    // ---------------- 值日「一次安排一周」 ----------------

    /// <summary>把周一…周日七行表格搭出来（BuildUi 调用一次）。</summary>
    void BuildWeekTable()
    {
        _weekPanel.Orientation = Wpf.Orientation.Vertical;
        _weekPanel.Margin = new Thickness(FormIndent, 0, 0, 12);
        _weekPanel.Children.Add(new Wpf.TextBlock
        {
            Text = "一周值日安排（留空的行自动跳过）", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x37, 0x22)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var head = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(62) });
        head.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(238) });
        head.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(238) });
        void Head(string t, int col)
        {
            var b = new Wpf.TextBlock
            {
                Text = t, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85))
            };
            Wpf.Grid.SetColumn(b, col);
            head.Children.Add(b);
        }
        Head("星期", 0); Head("值日内容", 1); Head("值日生", 2);
        _weekPanel.Children.Add(head);

        // 只列周一…周日：「每天」是固定岗位，本来就不属于「某一周」，仍走单条模式
        foreach (string day in DutyItem.DayNames.Skip(1))
        {
            var g = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(62) });
            g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(238) });
            g.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(238) });

            var lab = new Wpf.TextBlock
            {
                Text = day, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x28, 0x36))
            };
            var duty = new Wpf.TextBox { Width = 230, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            var names = new Wpf.TextBox
            {
                Width = 230, FontSize = 12, ToolTip = "姓名用逗号 / 顿号分隔，或每行一个"
            };
            Wpf.Grid.SetColumn(lab, 0); Wpf.Grid.SetColumn(duty, 1); Wpf.Grid.SetColumn(names, 2);
            g.Children.Add(lab); g.Children.Add(duty); g.Children.Add(names);
            _weekPanel.Children.Add(g);
            _weekInputs.Add((day, duty, names));
        }
    }

    // ---------------- 语音提醒 ----------------

    /// <summary>语音提醒控件初始化：时机下拉、勾选联动、默认文案。</summary>
    void InitVoiceControls()
    {
        _voiceWhen.Items.Add(new Wpf.ComboBoxItem { Content = "课间播（默认）", Tag = false });
        _voiceWhen.Items.Add(new Wpf.ComboBoxItem { Content = "收到就播（上课也播）", Tag = true });
        _voiceWhen.SelectedIndex = 0;

        _voice.Checked += (_, _) => { if (!_voiceSync) OnVoiceToggled(); };
        _voice.Unchecked += (_, _) => { if (!_voiceSync) OnVoiceToggled(); };
        _voiceText.TextChanged += (_, _) =>
        {
            if (!_voiceSync) _voiceTextTouched = (_voiceText.Text ?? "").Trim().Length > 0;
        };
        UpdateVoiceEnabled();
    }

    void OnVoiceToggled()
    {
        _voiceTouched = true;
        if (_voice.IsChecked == true && !_voiceTextTouched && (_voiceText.Text ?? "").Trim().Length == 0)
            SetVoiceText(ComposeDefaultVoiceText());
        UpdateVoiceEnabled();
    }

    void SetVoiceText(string text)
    {
        _voiceSync = true;
        try { _voiceText.Text = text ?? ""; }
        finally { _voiceSync = false; }
    }

    void SetVoiceChecked(bool on)
    {
        _voiceSync = true;
        try { _voice.IsChecked = on; }
        finally { _voiceSync = false; }
        UpdateVoiceEnabled();
    }

    /// <summary>没勾语音时播报内容与时机都灰掉：免得以为填了就会念。</summary>
    void UpdateVoiceEnabled()
    {
        bool on = _voice.IsChecked == true && _voice.IsEnabled;
        _voiceText.IsEnabled = on;
        _voiceWhen.IsEnabled = on;
        _voiceText.Foreground = on
            ? Brushes.Black
            : new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3));
    }

    /// <summary>默认播报文案：通知 = 标题 + 正文前一段；作业 / 缺交 = 类型 + 科目 + 内容；值日 = 星期 + 岗位 + 名单。</summary>
    string ComposeVoiceText()
    {
        string f1 = (_field1.Text ?? "").Trim();
        string f2 = (_field2.Text ?? "").Trim();
        if (f2.Length > 60) f2 = f2[..60];
        string tail = f2.Length > 0 ? "。" + f2 : "";
        return Entity switch
        {
            BoardSyncConsts.EntityNotice => "本班通知：" + f1 + tail,
            BoardSyncConsts.EntityMissing => "缺交名单：" + f1 + tail,
            // 一周安排别去念单日输入框里的残字，直接念「本周值日安排已更新 + 头几天」
            BoardSyncConsts.EntityDuty when _weekMode.IsChecked == true =>
                "值日安排：本周值日表已更新。" +
                string.Join("；", _weekInputs
                    .Where(x => (x.Duty.Text ?? "").Trim().Length > 0 || (x.Names.Text ?? "").Trim().Length > 0)
                    .Take(3)
                    .Select(x => x.Day + " " + (x.Duty.Text ?? "").Trim())),
            // 值日念起来要像人说话：「值日安排：周一 扫地、拖地，张三、李四」
            BoardSyncConsts.EntityDuty =>
                ("值日安排：" + SelectedDay + " " + f1).Trim() + (f2.Length > 0 ? "，" + f2 : ""),
            _ => "作业：" + f1 + tail
        };
    }

    /// <summary>
    /// 「完整内容 + 新增字段」：全屏提醒一律用这份，通知的语音播报也用这份。
    /// <para>★ 刻意按「一行一个字段」排版而不是拼成一句话：整屏大字卡片上，
    /// 老师一眼就要能分清标题、正文、有效期；挤成一行反而读不出来。</para>
    /// </summary>
    string ComposeFullText()
    {
        string f1 = (_field1.Text ?? "").Trim();
        string f2 = (_field2.Text ?? "").Trim();

        if (Entity == BoardSyncConsts.EntityDuty && _weekMode.IsChecked == true)
        {
            var rows = new List<BoardDutyRow>();
            foreach (var (day, tbDuty, tbNames) in _weekInputs)
            {
                string d = (tbDuty.Text ?? "").Trim();
                string n = (tbNames.Text ?? "").Trim();
                if (d.Length == 0 && n.Length == 0) continue;
                rows.Add(new BoardDutyRow { Day = day, Duty = d, Names = n });
            }
            return "值日安排（整周）\n" +
                   (rows.Count > 0 ? BoardSyncConsts.WeekRowsText(rows) : "（尚未填写）");
        }

        var sb = new System.Text.StringBuilder(BoardSyncConsts.EntityName(Entity));
        switch (Entity)
        {
            case BoardSyncConsts.EntityNotice:
                sb.Append("\n标题：").Append(f1).Append("\n内容：").Append(f2);
                // 通知的截止日期是本次新增的字段，也一并带进全屏 / 语音
                if (_due.SelectedDate is DateTime dd)
                    sb.Append("\n有效期至：").Append(dd.ToString("yyyy年M月d日"));
                break;
            case BoardSyncConsts.EntityMissing:
                sb.Append("\n科目：").Append(f1).Append("\n缺交学生：").Append(f2);
                break;
            case BoardSyncConsts.EntityDuty:
                sb.Append("\n星期：").Append(SelectedDay)
                  .Append("\n值日内容：").Append(f1)
                  .Append("\n值日生：").Append(f2);
                break;
            default:
                sb.Append("\n科目：").Append(f1).Append("\n作业内容：").Append(f2);
                break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 默认播报文案。
    /// <para>r167：通知改成「只念标题 + 正文」，不再念「本班通知」「标题：」「内容：」这些标签 ——
    /// 教室里听的人不需要被告知「这是一条通知」，标签念出来纯是噪音；
    /// 截止日期也只进全屏卡片，不念。作业 / 缺交 / 值日仍保留「作业：」这类两字前缀，
    /// 因为听的人得先知道这是哪一类事，否则「数学课本 P42」会被当成随口一句。</para>
    /// </summary>
    string ComposeDefaultVoiceText()
        => Entity == BoardSyncConsts.EntityNotice ? ComposeNoticeVoiceText() : ComposeVoiceText();

    /// <summary>通知播报词：标题。正文。 —— 标签一律不念，句号补齐读起来才不粘连。</summary>
    string ComposeNoticeVoiceText()
    {
        var parts = new List<string>();
        void Add(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return;
            if (!"。！？；…，、".Contains(s[^1])) s += "。";
            parts.Add(s);
        }
        Add(_field1.Text);
        Add(_field2.Text);
        return string.Join("", parts);
    }

    /// <summary>内容一改，自动把「播报内容」同步成发送的内容；老师亲手改过就不再覆盖。</summary>
    void RefreshAutoTexts()
    {
        if (!_voiceTextTouched) SetVoiceText(ComposeDefaultVoiceText());
    }

    void BuildUi()
    {
        var root = new Wpf.StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "把这条信息发给所选班级的教室机",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x28, 0x36))
        });
        // r166：标题与说明拆两行 —— 标题只说做什么，确认流程放到 12 号灰字里
        root.Children.Add(new Wpf.TextBlock
        {
            Text = "教室机由电教管理员确认后应用；回执可在「消息台账 / 回执」里核对。",
            FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85))
        });
        _snapshotInfo.FontSize = 12;
        _snapshotInfo.Margin = new Thickness(0, 0, 0, 14);
        _snapshotInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3));
        root.Children.Add(_snapshotInfo);

        // r169：目标班级改成「标签 + 胶囊 + 已加入 N 个」，下一行是「下拉 + 添加班级 + 移除」。
        // 默认已经带出账号所属班级；这里的增加 / 移除只在本软件运行期间生效，不改 users.json。
        var classHead = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        classHead.Children.Add(_classChips);
        classHead.Children.Add(_classCount);
        root.Children.Add(Row(Lab("目标班级"), classHead));

        // 空标签占位：让「添加 / 移除」那一行的输入框与上面所有框左边缘对齐
        root.Children.Add(Row(new Wpf.TextBlock(), _classInput, _addClass, _removeClass));
        _addClass.Click += (_, _) => AddClassFromInput();
        _removeClass.Click += (_, _) => RemoveClassFromInput();

        _entity.Items.Add(new Wpf.ComboBoxItem { Content = "作业公示", Tag = BoardSyncConsts.EntityHomework });
        _entity.Items.Add(new Wpf.ComboBoxItem { Content = "缺交名单", Tag = BoardSyncConsts.EntityMissing });
        _entity.Items.Add(new Wpf.ComboBoxItem { Content = "值日安排", Tag = BoardSyncConsts.EntityDuty });
        _entity.Items.Add(new Wpf.ComboBoxItem { Content = "本班通知", Tag = BoardSyncConsts.EntityNotice });
        _entity.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(_presetEntity))
        {
            for (int i = 0; i < _entity.Items.Count; i++)
                if ((_entity.Items[i] as Wpf.ComboBoxItem)?.Tag as string == _presetEntity)
                { _entity.SelectedIndex = i; break; }
        }
        _entity.SelectionChanged += (_, _) => { Relabel(); RefreshSnapshot(); };
        root.Children.Add(Row(Lab("内容类型"), _entity));

        // 下拉项由 RefreshClassUi → RefreshOpItems 统一构建：多班时自动去掉「删除一条」，
        // 因为多个班的条目 Id 各不相同，硬套第一个班的目标会改错班。
        _op.SelectionChanged += (_, _) => { if (!_opSync) Relabel(); };
        root.Children.Add(Row(Lab("操作"), _op));

        _item.SelectionChanged += (_, _) => { if (!_loading) OnItemChanged(); };
        var reload = new Wpf.Button
        {
            Content = "读取教室机内容", Padding = new Thickness(11, 5, 11, 5),
            FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        reload.Click += (_, _) => RefreshSnapshot();
        // r166：窗内三个次要按钮统一成主窗口的描边样式（原来是系统默认灰按钮）
        if (TryFindResource("AppGhostButton") is Style ghostBtn)
        {
            reload.Style = ghostBtn;
            _addClass.Style = ghostBtn;
            _removeClass.Style = ghostBtn;
        }
        _itemHint = new Controls.HelpBubble
        {
            PopupTitle = "目标条目",
            Text = "选「（新增一条）」= 教室机新增；选已有条目 = 带上教室机当前值签名，" +
                   "教室机才能按 Id 做「替换」并检测本地是否改过。\n\n" +
                   "发布窗不再设字体格式：替换沿用教室机那一条现有的字号 / 颜色，" +
                   "新增用便签出厂的特大字 + 默认墨色。"
        };
        _itemHint.VerticalAlignment = VerticalAlignment.Center;
        _itemRow = Row(Lab("目标条目"), _item, reload, _itemHint);
        root.Children.Add(_itemRow);
        _targetHint.FontSize = 12;
        _targetHint.Margin = Indent(bottom: 10);
        _targetHint.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x66, 0x3C));
        root.Children.Add(_targetHint);

        // 通知「直接增加」：只有本班通知 + 新增时可勾（默认开），删除 / 替换一律人工裁决。
        _autoRow.Orientation = Wpf.Orientation.Horizontal;
        _autoRow.Margin = Indent(top: 0, bottom: 2);
        _auto.Click += (_, _) => { _autoTouched = true; Relabel(); };
        _autoRow.Children.Add(_auto);
        _autoRow.Children.Add(_autoBubble);
        root.Children.Add(_autoRow);

        // 截止日期：通知到哪天为止。默认今天 —— 多数班级通知就是当天的事。
        _dueRow.Orientation = Wpf.Orientation.Horizontal;
        _dueRow.Margin = new Thickness(0, 0, 0, 2);
        _due.SelectedDate = DateTime.Today;
        _due.ToolTip = "到期后这条不再显示在通知公告便签与跑马灯上，但数据保留、可在编辑窗恢复；置顶的通知不会自动撤下";
        _dueRow.Children.Add(Lab("截止日期"));
        _dueRow.Children.Add(_due);
        _dueRow.Children.Add(_dueHint);
        root.Children.Add(_dueRow);

        _overwriteRow.Orientation = Wpf.Orientation.Horizontal;
        _overwriteRow.Margin = Indent(bottom: 12);
        _overwriteRow.Children.Add(_overwrite);
        root.Children.Add(_overwriteRow);

        // 值日专用：星期。作业 / 缺交 / 通知时整行隐藏，原有布局一像素都不动。
        foreach (var d in DutyItem.DayNames)
            _day.Items.Add(new Wpf.ComboBoxItem { Content = d, Tag = d });
        _day.SelectedIndex = 1;           // 默认「周一」（DayNames[0] 是「每天」）
        _dayRow.Orientation = Wpf.Orientation.Horizontal;
        _dayRow.Margin = new Thickness(0, 0, 0, 10);
        _dayRow.Children.Add(Lab("星期"));
        _dayRow.Children.Add(_day);
        _dayRow.Children.Add(new Wpf.TextBlock
        {
            Text = "「每天」用于每天都一样的固定岗位", FontSize = 11, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99))
        });
        root.Children.Add(_dayRow);

        // 一次安排一周：勾上后单日控件整组让位给 7 行表格（两者互斥，绝不混进同一张单）
        _weekMode.Click += (_, _) => Relabel();
        var weekSwitch = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal, Margin = Indent(bottom: 10)
        };
        _weekSwitch = weekSwitch;
        weekSwitch.Children.Add(_weekMode);
        weekSwitch.Children.Add(_weekBubble);
        root.Children.Add(weekSwitch);
        BuildWeekTable();
        root.Children.Add(_weekPanel);

        // r169：宽度交给 Grid 的内容列（Stretch），不再写死 420 —— 窗口出现纵向滚动条时
        // 固定宽度会把右边裁掉一截。内容框按设计图加高到 110，写满三行作业还有余量。
        _field2.Height = 110;
        _field2.TextWrapping = TextWrapping.Wrap;
        _field2.AcceptsReturn = true;
        _field2.VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto;
        // 字段行标签是动态文字（科目 / 通知标题 / 值日内容），这里补一次与 Lab() 一致的外观
        StyleLab(_lab1);
        StyleLab(_lab2);
        _fieldRow1 = Row(_lab1, _field1);
        root.Children.Add(_fieldRow1);
        _fieldRow2 = Row(_lab2, _field2);
        root.Children.Add(_fieldRow2);

        // 内容一改，全屏 / 语音的默认文案跟着变成「这次要发的完整内容」；
        // 老师亲手改过播报文字后再也不覆盖（_voiceTextTouched 守卫）。
        _field1.TextChanged += (_, _) => RefreshAutoTexts();
        _field2.TextChanged += (_, _) => RefreshAutoTexts();
        _due.SelectedDateChanged += (_, _) => RefreshAutoTexts();
        foreach (var (_, tbDuty, tbNames) in _weekInputs)
        {
            tbDuty.TextChanged += (_, _) => RefreshAutoTexts();
            tbNames.TextChanged += (_, _) => RefreshAutoTexts();
        }

        // 「播报内容」这一行：标签 + 两个勾选 + 时机下拉，紧挨着靠左排（r169 按设计图）。
        // ★ 手写 Grid 而不是用 Row()：Row 的第一列是 Star，会把「语音提醒」推到行中间去。
        InitVoiceControls();
        _voice.Margin = new Thickness(16, 0, 0, 0);
        _force.VerticalAlignment = VerticalAlignment.Center;
        _voice.VerticalAlignment = VerticalAlignment.Center;
        _voiceWhen.VerticalAlignment = VerticalAlignment.Center;
        _reminderRow = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 8) };
        _reminderRow.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(LabelWidth) });
        for (int i = 0; i < 3; i++)
            _reminderRow.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });
        _reminderRow.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var remLab = Lab("播报内容");
        Wpf.Grid.SetColumn(remLab, 0);
        _reminderRow.Children.Add(remLab);
        foreach (var (el, col) in new (UIElement, int)[] { (_force, 1), (_voice, 2), (_voiceWhen, 3) })
        {
            Wpf.Grid.SetColumn(el, col);
            _reminderRow.Children.Add(el);
        }
        root.Children.Add(_reminderRow);
        // 播报文字另起一行，左边缘与所有输入框对齐（标签列留白）
        root.Children.Add(Row(new Wpf.TextBlock(), _voiceText));

        // 教室机现状：只读浅灰底框（r167 起说明搬进框内首行），高度按设计图加高到 150
        _preview.Height = 150;
        _preview.Margin = new Thickness(0);
        // 先写一句占位：快照是异步拉的，网络慢时框里空着会让人以为「教室机没内容」
        SetPreview("正在读取…");
        root.Children.Add(Row(Lab("教室机现状"), _preview));

        _msg.FontSize = 12;
        _msg.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        _msg.Margin = Indent(top: 4, bottom: 6);
        root.Children.Add(_msg);

        // 底部：一条分隔线 + 右对齐按钮（设计图里按钮落在卡片外的操作栏里）
        var bar = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(20, 10, 20, 14)
        };
        // r167：顺序改成「取消 → 发布信息」：主按钮永远贴最右，
        // 手指从右边滑过来先碰到的是「发布」，取消退到它左边（误点的代价小得多）。
        var cancel = new Wpf.Button { Content = "取消", Padding = new Thickness(18, 7, 18, 7), FontSize = 13 };
        if (TryFindResource("AppGhostButton") is Style cancelStyle) cancel.Style = cancelStyle;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Wpf.Button
        {
            Content = "发布信息", Padding = new Thickness(20, 7, 20, 7), FontSize = 13,
            Margin = new Thickness(10, 0, 0, 0)
        };
        // r166：底部两个按钮换成主窗口同一套模板（实心主色 / 描边），不再是系统默认灰按钮
        if (TryFindResource("AppPrimaryButton") is Style okStyle) ok.Style = okStyle;
        ok.Click += (_, _) => Confirm();
        bar.Children.Add(cancel);
        bar.Children.Add(ok);

        var barHost = new Wpf.StackPanel();
        barHost.Children.Add(new Wpf.Border
        {
            Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xF0)),
            Margin = new Thickness(0)
        });
        barHost.Children.Add(bar);

        // 底部按钮固定在窗口最下、只有内容区滚动。
        // 125% 缩放的 1080p 工作区只有 816 DIP 高，加了「语音提醒」两块后一屏放不下 ——
        // 让「发布信息」跟着滚到底才能点，是最容易误事的做法。
        MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height - 40);
        var shell = new Wpf.Grid();
        shell.RowDefinitions.Add(new Wpf.RowDefinition());
        shell.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });
        var scroll = new Wpf.ScrollViewer
        {
            VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled,
            Content = root
        };
        Wpf.Grid.SetRow(scroll, 0);
        Wpf.Grid.SetRow(barHost, 1);
        shell.Children.Add(scroll);
        shell.Children.Add(barHost);
        Content = shell;
        RefreshClassUi();
        Relabel();
        RefreshSnapshot();
    }

    string Entity => (_entity.SelectedItem as Wpf.ComboBoxItem)?.Tag as string ?? BoardSyncConsts.EntityHomework;
    string Op => (_op.SelectedItem as Wpf.ComboBoxItem)?.Tag as string ?? BoardSyncConsts.OpUpsert;

    BoardSnapshotItem? SelectedItem
        => (_item.SelectedItem as Wpf.ComboBoxItem)?.Tag as BoardSnapshotItem;

    int _snapGen;

    /// <summary>
    /// 读教室机快照；读不到就回退教师机本机条目，但必须把这一点说清楚。
    /// <para>★ 读快照要连共享盘 / FTP，所以整段走后台线程 + 代次校验：
    /// 网络慢时窗口照常先出来，读完才把下拉填上；连点内容类型也不会串结果。</para>
    /// </summary>
    async void RefreshSnapshot()
    {
        int gen = ++_snapGen;
        _loading = true;
        bool multi = _classes.Count > 1;
        string cls = _classes.FirstOrDefault() ?? "";
        _item.IsEnabled = !multi;

        BoardSnapshot? snap = null;
        if (!BoardSyncService.IsEnabled)
        {
            // 没启用同步就别去碰共享盘 / FTP：诊断构造窗口、离线打开都不该被网络拖住
            _snapshotInfo.Text = "⚠ 同步尚未启用或没配数据源：下面列的是教师机本机条目，" +
                                 "教室机上未必存在；应用时教室机会让管理员现场指定目标。";
            _snapshotInfo.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x2B));
        }
        else
        {
            _snapshotInfo.Text = "正在读取教室机内容…";
            _snapshotInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x4F, 0xA3));
            try
            {
                var (s, msg) = await Task.Run(() => BoardSyncService.LoadRemoteSnapshot(cls));
                if (gen != _snapGen) return;      // 用户已切到别的内容类型，这轮结果丢掉
                snap = s;
                _snapshotInfo.Text = msg;
                _snapshotInfo.Foreground = new SolidColorBrush(snap == null
                    ? Color.FromRgb(0xC0, 0x6A, 0x2B)
                    : Color.FromRgb(0x1E, 0x4F, 0xA3));
            }
            catch (Exception ex)
            {
                if (gen != _snapGen) return;
                _snapshotInfo.Text = "读取教室机内容失败：" + ex.Message;
                _snapshotInfo.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            }
        }

        _snapshot = snap;
        _item.Items.Clear();
        _item.Items.Add(new Wpf.ComboBoxItem { Content = "（新增一条）", Tag = null });
        var items = _snapshot?.For(Entity).ToList() ?? new List<BoardSnapshotItem>();
        if (_snapshot == null) items = LocalFallback(Entity);

        foreach (var x in items)
            _item.Items.Add(new Wpf.ComboBoxItem
            {
                Content = (x.Source == "classroom" ? "〔教室机〕" : "〔本机〕") + x.Brief + "　" + Short(x.Id),
                Tag = x
            });
        _item.SelectedIndex = 0;
        _loading = false;
        _targetHint.Text = multi
            ? "批量发布：每个班各新增一条，内容相同、条目 Id 各自独立；不会替换 / 删除任何已有内容。"
            : items.Count == 0
                ? "教室机这一类内容目前是空的，只能新增一条。"
                : "共 " + items.Count + " 条可选；选中一条会自动带出教室机原文，改完再发出。";
        SetPreview(BuildPreview(items));
        if (multi)
        {
            _snapshotInfo.Text = "📦 已选中 " + _classes.Count + " 个班：每个班各发一条新信息。"
                                 + "上方预览是第一个班的教室机当前内容，仅供对照。";
            _snapshotInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x66, 0x3C));
        }
        ApplyAutoUi();   // 快照回来会重画目标条目行，这里把「直接增加」的状态再压回去
    }

    /// <summary>
    /// 没有教室机快照时的兜底：列教师机本机条目，但标明「本机」——
    /// 这些 Id 在教室机未必存在，应用时教室机会让管理员现场指定目标。
    /// </summary>
    List<BoardSnapshotItem> LocalFallback(string entity)
    {
        var list = new List<BoardSnapshotItem>();
        try
        {
            foreach (var x in BoardSyncService.LocalItemsForTarget(entity,
                         _classes.FirstOrDefault() ?? ""))
            {
                x.Source = "local";
                list.Add(x);
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 教室机现状框的内容 = 首行说明 + 正文。
    /// <para>首行说明放在框里、而不是标签旁边的一个独立小灰字：那样标签会被挤到
    /// 「小灰字 + 框」两个人的中间，看起来像没对齐。放进框里，标签就只对着一个框。</para>
    /// </summary>
    void SetPreview(string body)
        => _preview.Text = "（只读，来自共享目录里的教室机快照）\n\n" + body;

    static string BuildPreview(List<BoardSnapshotItem> items)
    {
        if (items.Count == 0) return "（还没有读到教室机的这一类内容）";
        var sb = new System.Text.StringBuilder();
        foreach (var g in items.GroupBy(x => x.Entity))
        {
            sb.AppendLine("【" + BoardSyncConsts.EntityName(g.Key) + "】");
            foreach (var x in g) sb.AppendLine("  · " + x.Brief + "   " + Short(x.Id) + StyleHint(x));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>预览里标出这一行当前的格式，免得老师以为「传过去就是默认样式」。</summary>
    static string StyleHint(BoardSnapshotItem x)
    {
        if (x.Styles == null || x.Styles.Count == 0) return "";
        var parts = x.Styles.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => BoardSyncConsts.FieldLabel(kv.Key) + " " + (kv.Value?.Caption ?? ""));
        return "\n      格式：" + string.Join("；", parts);
    }

    static string Short(string id)
        => string.IsNullOrEmpty(id) ? "" : id.Length <= 18 ? id : id[..10] + "…" + id[^5..];

    /// <summary>把星期下拉选中到指定值（认不出就保持原样）。</summary>
    void SelectDay(string? day)
    {
        string d = (day ?? "").Trim();
        if (d.Length == 0) return;
        for (int i = 0; i < _day.Items.Count; i++)
            if ((_day.Items[i] as Wpf.ComboBoxItem)?.Tag as string == d) { _day.SelectedIndex = i; return; }
    }

    string SelectedDay => (_day.SelectedItem as Wpf.ComboBoxItem)?.Tag as string ?? "周一";

    void OnItemChanged()
    {
        var si = SelectedItem;
        _targetHint.Text = si == null
            ? "当前是「新增一条」：教室机会直接新增，不会替换任何已有内容。"
            : "教室机原文：" + si.Brief + "　（" + si.SourceTag + "，Id " + Short(si.Id) + "）";
        _targetHint.Foreground = new SolidColorBrush(si == null
            ? Color.FromRgb(0x77, 0x88, 0x99) : Color.FromRgb(0x1F, 0x66, 0x3C));
        if (si == null || Op == BoardSyncConsts.OpDelete) return;

        string f1, f2;
        if (Entity == BoardSyncConsts.EntityDuty)
        {
            SelectDay(si.Fields.TryGetValue("day", out var dv) ? dv : "");
            f1 = si.Fields.TryGetValue("duty", out var du) ? du ?? "" : "";
            f2 = si.Fields.TryGetValue("names", out var nm) ? nm ?? "" : "";
        }
        else
        {
            f1 = si.Fields.TryGetValue("subject", out var s) ? s
               : si.Fields.TryGetValue("title", out var t) ? t : "";
            f2 = si.Fields.TryGetValue("content", out var c) ? c
               : si.Fields.TryGetValue("students", out var st) ? st : "";
        }
        _field1.Text = f1;
        _field2.Text = f2;
        // 样式不在教师机这边选：教室机应用「替换」时沿用被覆盖条目的现有格式，
        // 「新增」时用便签出厂的特大 + 墨色。带出原文会触发 TextChanged 自动刷新播报文案。
    }

    void Relabel()
    {
        bool notice = Entity == BoardSyncConsts.EntityNotice;
        bool duty = Entity == BoardSyncConsts.EntityDuty;
        bool weekly = duty && _weekMode.IsChecked == true;
        bool canDelete = Op == BoardSyncConsts.OpDelete;

        // 周表与单日控件互斥：周模式下把「星期 / 值日内容 / 值日生 / 目标条目」全部收起，
        // 免得老师以为上面那一行也会一起发出去。
        _dayRow.Visibility = duty && !weekly ? Visibility.Visible : Visibility.Collapsed;
        _weekPanel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
        if (_weekSwitch != null) _weekSwitch.Visibility = duty ? Visibility.Visible : Visibility.Collapsed;
        _weekMode.IsEnabled = !canDelete;
        if (_fieldRow1 != null) _fieldRow1.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible;
        if (_fieldRow2 != null) _fieldRow2.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible;
        if (_itemRow != null) _itemRow.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible;
        if (_itemHint != null) _itemHint.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible;
        if (_targetHint != null) _targetHint.Visibility = weekly ? Visibility.Collapsed : Visibility.Visible;

        _lab1.Text = notice ? "通知标题" : duty ? "值日内容" : "科目";
        _lab2.Text = duty ? "值日生"
                   : Entity == BoardSyncConsts.EntityMissing ? "缺交学生"
                   : notice ? "通知内容" : "作业内容";
        _field2.ToolTip = duty ? "值日生姓名：每行一个，或用逗号 / 顿号分隔" : null;
        _field1.IsEnabled = _field2.IsEnabled = Op != BoardSyncConsts.OpDelete;
        // r71：字段级格式条整行移除，字体由教室机便签自己的设置决定，这里不再有可藏的格式行。
        // 删除不需要「引起注意」：删掉的东西没什么可让全班当场看的
        _force.IsEnabled = Op != BoardSyncConsts.OpDelete;
        if (!_force.IsEnabled) _force.IsChecked = false;

        // 语音同理：通知默认勾上（讲台远，念一遍最有效），作业 / 缺交默认不念免得吵；
        // 用户自己改过就按用户的来，切内容类型不再动它。
        bool canVoice = Op != BoardSyncConsts.OpDelete;
        _voice.IsEnabled = canVoice;
        if (!canVoice) SetVoiceChecked(false);
        else if (!_voiceTouched) SetVoiceChecked(notice);
        // 换内容类型时把自动生成的播报文字按新类型重算一遍（用户手写的不动），
        // 否则「作业：语文…」会被当成通知的播报词原样念出去。
        if (!_voiceTextTouched) SetVoiceText(ComposeDefaultVoiceText());
        UpdateVoiceEnabled();

        _day.IsEnabled = !canDelete;

        // 通知「直接增加」：只给「本班通知 + 新增」，默认开；用户亲手改过就不再自动改。
        bool canAuto = notice && !canDelete && !weekly;
        _autoRow.Visibility = canAuto ? Visibility.Visible : Visibility.Collapsed;
        _autoBubble.Visibility = canAuto ? Visibility.Visible : Visibility.Collapsed;
        _auto.IsEnabled = canAuto;
        if (!canAuto) _auto.IsChecked = false;
        else if (!_autoTouched) _auto.IsChecked = true;
        ApplyAutoUi();

        // 截止日期与「覆盖同标题」只对通知有意义；删除时没有可设的
        bool canDue = notice && !canDelete && !weekly;
        _dueRow.Visibility = canDue ? Visibility.Visible : Visibility.Collapsed;
        _overwriteRow.Visibility = canDue ? Visibility.Visible : Visibility.Collapsed;
        _due.IsEnabled = canDue;
        _dueHint.Visibility = canDue ? Visibility.Visible : Visibility.Collapsed;
        _overwrite.IsEnabled = canDue;
        if (!canDue) _overwrite.IsChecked = false;
    }

    /// <summary>「直接增加」勾选后，目标条目那一行就失去意义：收起并写清楚自动增加到底是新增一条。</summary>
    void ApplyAutoUi()
    {
        bool weekly = Entity == BoardSyncConsts.EntityDuty && _weekMode.IsChecked == true;
        bool auto = _auto.IsChecked == true && _auto.IsEnabled;
        _item.IsEnabled = !auto && !weekly && _classes.Count <= 1;
        if (_itemRow != null)
            _itemRow.Visibility = (weekly || auto) ? Visibility.Collapsed : Visibility.Visible;
        if (_itemHint != null)
            _itemHint.Visibility = (weekly || auto) ? Visibility.Collapsed : Visibility.Visible;
        if (_targetHint == null) return;
        _targetHint.Visibility = (weekly || auto) ? Visibility.Collapsed : Visibility.Visible;
        if (auto)
        {
            _targetHint.Text = "已选「直接增加」：教室机收到就自动新增这一条通知，无需电教管理员确认；原有通知不会被动。";
            _targetHint.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x66, 0x3C));
        }
        else if (_targetHint.Text.StartsWith("已选「直接增加」", StringComparison.Ordinal))
        {
            _targetHint.Text = "选「（新增一条）」= 教室机新增；选已有条目 = 带出教室机原文，改完再发出。";
            _targetHint.Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x88, 0x99));
        }
    }

    void Confirm()
    {
        _msg.Text = "";
        if (_classes.Count == 0)
        {
            _msg.Text = "请先给当前账号设置所属班级，或用上面的「＋ 增加班级」临时加一个班。";
            return;
        }
        // 界面按钮会被禁灰，这里再兜一次：权限判定唯一入口是 UserSession。
        if (!UserSession.CanPublishInfo) { _msg.Text = UserSession.DenyPublishInfo; return; }

        string f1 = (_field1.Text ?? "").Trim();
        string f2 = (_field2.Text ?? "").Trim();
        string entity = Entity, op = Op;
        bool notice = entity == BoardSyncConsts.EntityNotice;
        bool duty = entity == BoardSyncConsts.EntityDuty;
        bool weekly = duty && _weekMode.IsChecked == true;
        bool auto = notice && op == BoardSyncConsts.OpUpsert && _auto.IsChecked == true;
        bool multi = _classes.Count > 1;
        if (multi || weekly) op = BoardSyncConsts.OpUpsert;   // 批量 / 整周发布绝不替换或删除单条

        // 一次安排一周：只收「填了内容或名单」的行，空行跳过（老师通常只安排周一到周五）。
        var weekRows = new List<BoardDutyRow>();
        if (weekly)
        {
            foreach (var (day, tbDuty, tbNames) in _weekInputs)
            {
                string d = (tbDuty.Text ?? "").Trim();
                string n = (tbNames.Text ?? "").Trim();
                if (d.Length == 0 && n.Length == 0) continue;
                weekRows.Add(new BoardDutyRow { Day = day, Duty = d, Names = n });
            }
            if (weekRows.Count == 0)
            {
                _msg.Text = "一周安排至少要填一天的值日内容或值日生（留空的行会自动跳过）。";
                return;
            }
        }

        // 逐班校验：科任老师只能给自己任教班级、自己科目的条目发变更单。
        foreach (string cls in _classes)
        {
            if (!UserSession.CanEditBoardFor(cls)) { _msg.Text = UserSession.DenyEditClass(cls); return; }
            // 通知与值日安排不属于任何科目：只校验班级范围，不套「任课科目」。
            // 值日生是全班的公共事务，逼科任老师去改自己那门课的科目串只会让它发不出去。
            if (!notice && !duty && op != BoardSyncConsts.OpDelete && !UserSession.CanEditBoardItem(cls, f1))
            { _msg.Text = "〔" + cls + "〕" + UserSession.DenyEditBoardItem(f1); return; }
        }
        if (op != BoardSyncConsts.OpDelete && !weekly && f1.Length == 0 && f2.Length == 0)
        {
            _msg.Text = "请填写要发布的内容。";
            return;
        }

        // 批量 / 整周 / 直接增加都没有「目标条目」：整周替换的是整张表，不是某一条；
        // 自动增加则是纯新增，带 ItemId 会让教室机误以为要按 Id 改一条。
        var si = (multi || weekly || auto) ? null : SelectedItem;
        string itemId = si?.Id ?? "";
        string baseSig = si?.Signature ?? "";
        string targetText = si?.Brief ?? "";
        bool fromSnapshot = si?.Source == "classroom";
        if (op == BoardSyncConsts.OpDelete && itemId.Length == 0)
        {
            _msg.Text = "删除必须先选目标条目；如果教室机那边还没有内容，请改选「新增 / 修改一条」。";
            return;
        }

        var fields = new Dictionary<string, string>();
        if (op != BoardSyncConsts.OpDelete)
        {
            if (weekly)
                fields = BoardSyncConsts.WeekFields(weekRows);
            else if (duty)
            { fields["day"] = SelectedDay; fields["duty"] = f1; fields["names"] = f2; }
            else if (entity == BoardSyncConsts.EntityMissing)
            { fields["subject"] = f1; fields["students"] = f2; }
            else if (notice)
            { fields["title"] = f1; fields["content"] = f2; }
            else
            { fields["subject"] = f1; fields["content"] = f2; }
        }

        // r71：发布窗不再下发字体格式。教室机应用时「替换」沿用被覆盖条目的样式、
        // 「新增」用出厂的特大 + 默认墨色；Styles 保留空字典只为兼容老版教室机与历史消息。
        var styles = new Dictionary<string, BoardTextStyle>();

        string newBrief = weekly
            ? "整周 " + weekRows.Count + " 天：" + BoardSyncConsts.WeekRowsBrief(weekRows)
            : BoardSyncConsts.Brief(fields);
        string summary = op == BoardSyncConsts.OpDelete
            ? "删除 " + BoardSyncConsts.EntityName(entity) + "：" + targetText
            : weekly
                ? "整周值日安排（" + weekRows.Count + " 天）：" + BoardSyncConsts.WeekRowsBrief(weekRows)
                : itemId.Length > 0
                    ? "修改 " + BoardSyncConsts.EntityName(entity) + "：" + targetText + " → " + newBrief
                    : "新增 " + BoardSyncConsts.EntityName(entity) + "：" + newBrief;

        Results.Clear();
        bool force = _force.IsChecked == true && op != BoardSyncConsts.OpDelete;

        // 全屏提醒 = 本次发送的完整内容（标题 + 正文 + 新增字段），自动带出、无需另填。
        // 老单没有这个字段时教室机回退到 Summary，历史消息不会变空白卡片。
        string forceText = force ? ComposeFullText() : "";

        // 语音：通知默认念完整内容，其余只念摘要；老师手写过就按老师写的来。
        bool voice = _voice.IsChecked == true && op != BoardSyncConsts.OpDelete;
        string voiceText = "";
        if (voice)
        {
            voiceText = (_voiceText.Text ?? "").Trim();
            if (voiceText.Length == 0) voiceText = ComposeDefaultVoiceText();
        }
        bool voiceNow = voice && _voiceWhen.SelectedItem is Wpf.ComboBoxItem vci && vci.Tag is bool b && b;

        // 截止日期：换算成绝对时间点（当天 23:59），教室机原样写入通知，不按天换算 ——
        // 否则教师机今天发、教室机三天后才拉到，会一收到就快过期。留空 = 长期有效。
        DateTime? dueAt = _due.SelectedDate is DateTime dd
            ? dd.Date.AddHours(23).AddMinutes(59)
            : null;
        bool isNotice = entity == BoardSyncConsts.EntityNotice && op != BoardSyncConsts.OpDelete;
        bool overwriteSame = isNotice && op == BoardSyncConsts.OpUpsert && _overwrite.IsChecked == true;

        foreach (string cls in _classes)
        {
            Results.Add(new BoardChangeOrder
            {
                ClassKey = cls,
                Entity = entity,
                Op = op,
                ItemId = itemId,
                BaseSignature = baseSig,
                TargetText = targetText,
                FromSnapshot = fromSnapshot,
                ForceBreakReminder = force,
                ForceText = forceText,
                VoiceText = voiceText,
                VoiceImmediate = voiceNow,
                AutoApply = auto,
                DueAt = isNotice ? dueAt : null,
                OverwriteSame = overwriteSame,
                WeekRows = weekly ? new List<BoardDutyRow>(weekRows) : new List<BoardDutyRow>(),
                Fields = new Dictionary<string, string>(fields),
                Styles = new Dictionary<string, BoardTextStyle>(styles),
                Sender = UserSession.Current?.Username ?? "",
                SenderRole = UserSession.Role,
                SenderSubject = UserSession.Current?.Subject ?? "",
                ValidDay = DateTime.Today.ToString("yyyy-MM-dd"),
                // 多班时在台账里带上班名，否则几条一模一样分不清发给谁的
                Summary = multi ? "【" + cls + "】" + summary : summary
            });
        }
        DialogResult = true;
        Close();
    }
}
