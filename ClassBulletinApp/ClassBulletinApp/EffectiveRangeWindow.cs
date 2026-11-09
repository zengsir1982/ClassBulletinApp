using System;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Services;

// RadioButton 已在 GlobalUsings 全局别名（System.Windows.Forms 里也有同名类型）
using DatePicker = System.Windows.Controls.DatePicker;

namespace ClassBulletinApp;

/// <summary>
/// "这份临时调整适用于哪一段时间？"的小选择框。
///
/// 存在理由（设计 §4.5.5）：临时课表 / 手工调课都必须带生效区间，
/// 否则会永久顶在课表上；而我们的总课表是快照、里面没有日期，只能问用户。
/// 提供「本周 / 下周 / 下两周 / 自定义」四个快捷项，默认「本周」。
/// </summary>
public class EffectiveRangeWindow : Window
{
    /// <summary>生效起始日（含）。</summary>
    public DateTime From { get; private set; }

    /// <summary>生效结束日（含）。</summary>
    public DateTime To { get; private set; }

    readonly DatePicker _dpFrom = new();
    readonly DatePicker _dpTo = new();
    readonly RadioButton _rbCustom = new();

    /// <param name="anchor">以哪个日期所在周为"本周"（一般传显示日 / 今天）。</param>
    /// <param name="headline">顶部说明文字。</param>
    public EffectiveRangeWindow(DateTime anchor, string headline)
    {
        Title = "选择生效时间";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // 「本周」跟着设置里的周起始日走，否则页面是一周日开头、这里却是周一到周日，用户会选错周
        var (thisStart, thisEnd) = WeekHelper.WeekWindow(anchor, Store.LoadSettings().WeekStartDay);
        var nextStart = thisEnd.AddDays(1);
        var nextEnd = nextStart.AddDays(6);
        var twoEnd = nextStart.AddDays(13);

        From = thisStart;
        To = thisEnd;

        var root = new Grid { Margin = new Thickness(22, 20, 22, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var sp = new StackPanel();
        Grid.SetRow(sp, 0);

        // r77：标题 + 问号同一行（问号贴行末）；用 Grid 而不是 StackPanel，
        // 这样长标题仍然能换行，问号也不会被挤出窗口。
        var headRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headText = new TextBlock
        {
            Text = headline,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headText, 0);
        headRow.Children.Add(headText);

        var headBubble = new ClassBulletinApp.Controls.HelpBubble
        {
            PopupTitle = "操作说明",
            Text = "请选择这份临时安排生效的时间段（默认本周）。过期后自动失效，基础课表不受影响。",
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(headBubble, 1);
        headRow.Children.Add(headBubble);
        sp.Children.Add(headRow);

        sp.Children.Add(MakeRadio($"本周（{thisStart:M月d日} – {thisEnd:M月d日}）", true));
        sp.Children.Add(MakeRadio($"下周（{nextStart:M月d日} – {nextEnd:M月d日}）", false));
        sp.Children.Add(MakeRadio($"下两周（{nextStart:M月d日} – {twoEnd:M月d日}）", false));

        _rbCustom.Content = "自定义日期：";
        _rbCustom.FontSize = 14;
        _rbCustom.GroupName = "range";
        _rbCustom.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(_rbCustom);

        var dpRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(22, 4, 0, 0)
        };
        _dpFrom.SelectedDate = thisStart;
        _dpFrom.Width = 140;
        _dpTo.SelectedDate = thisEnd;
        _dpTo.Width = 140;
        _dpFrom.IsEnabled = false;
        _dpTo.IsEnabled = false;
        dpRow.Children.Add(_dpFrom);
        dpRow.Children.Add(new TextBlock
        {
            Text = "  至  ",
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize = 14
        });
        dpRow.Children.Add(_dpTo);
        sp.Children.Add(dpRow);

        // 自定义被选中 / 取消时，开关日期选择器
        foreach (var rb in new[] { _rbCustom })
            rb.Checked += (_, _) => { _dpFrom.IsEnabled = true; _dpTo.IsEnabled = true; };

        root.Children.Add(sp);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 16)
        };
        var btnCancel = new Button { Content = "取消", Padding = new Thickness(16, 7, 16, 7), FontSize = 14, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnOk = new Button { Content = "确定", Padding = new Thickness(16, 7, 16, 7), FontSize = 14, IsDefault = true };
        btnOk.Click += (_, _) => Confirm();
        bar.Children.Add(btnCancel);
        bar.Children.Add(btnOk);

        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        Content = root;
    
        GlassChrome.Prepare(this);}

    RadioButton MakeRadio(string text, bool isChecked)
    {
        var rb = new RadioButton
        {
            Content = text,
            FontSize = 14,
            GroupName = "range",
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 0, 4),
            Tag = text
        };
        rb.Checked += (_, _) =>
        {
            if (!ReferenceEquals(rb, _rbCustom))
            {
                _dpFrom.IsEnabled = false;
                _dpTo.IsEnabled = false;
            }
        };
        return rb;
    }

    void Confirm()
    {
        // 找出被选中的快捷项：靠索引判断，比解析字符串稳
        var radios = new System.Collections.Generic.List<RadioButton>();
        if (Content is Grid g && g.Children[0] is StackPanel sp)
            foreach (var c in sp.Children)
                if (c is RadioButton rb) radios.Add(rb);

        var (thisStart, thisEnd) = (From, To);
        int idx = radios.FindIndex(r => r.IsChecked == true);
        switch (idx)
        {
            case 0:  // 本周
                break;
            case 1:  // 下周
                From = thisEnd.AddDays(1);
                To = thisEnd.AddDays(7);
                break;
            case 2:  // 下两周
                From = thisEnd.AddDays(1);
                To = thisEnd.AddDays(14);
                break;
            default: // 自定义
                if (_dpFrom.SelectedDate == null || _dpTo.SelectedDate == null)
                {
                    System.Windows.MessageBox.Show("请选择自定义的起止日期。", "生效时间", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                From = _dpFrom.SelectedDate.Value.Date;
                To = _dpTo.SelectedDate.Value.Date;
                if (To < From)
                {
                    System.Windows.MessageBox.Show("结束日期不能早于开始日期。", "生效时间", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                break;
        }

        DialogResult = true;
        Close();
    }
}
