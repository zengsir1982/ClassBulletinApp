using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using HAlign = System.Windows.HorizontalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// r140：「保存到该班」撞上同名的导入课表时的三选一。
/// <para>原来借 MessageBox 的「是 / 否 / 取消」——「是」到底会发生什么全靠正文里那几行解释，
/// 而且两个按钮的默认含义和用户预期相反（点「是」不是覆盖，是另存一份），很容易点错。
/// 现在按钮直接写结果：<see cref="Choice.Separate"/>「分开存（推荐）」/
/// <see cref="Choice.Rename"/>「改个班名」/<see cref="Choice.Cancel"/>「取消」。</para>
/// <para>★ 语义没变：仍是另存一份带「（手录）」尾巴的独立课表，绝不覆盖导入的那份
/// （同名覆盖会被下一次自动更新静默吃掉，见 ScheduleFile.MergeManualFrom）。</para>
/// </summary>
internal class SaveClassConflictDialog : Window
{
    public enum Choice { Separate, Rename, Cancel }

    /// <summary>用户的选择（关窗口默认按「取消」处理，绝不误存）。</summary>
    public Choice Result { get; private set; } = Choice.Cancel;

    public SaveClassConflictDialog(string className, string suggestedName)
    {
        Title = "班级重名";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var sp = new StackPanel { Margin = new Thickness(18) };

        sp.Children.Add(new TextBlock
        {
            Text = "「" + className + "」已有学校导入的课表",
            FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        sp.Children.Add(new TextBlock
        {
            Text = "手录的这份会单独存一份，两边互不覆盖 —— 学校下次自动更新或重新导入时，你手排的课表不会被冲掉。",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // 结果预览：把「会存成什么名字」直接摆出来，比正文里一句话更好核对
        var box = new Border
        {
            Background = Res("PrimarySoftBrush", Brushes.AliceBlue),
            BorderBrush = Res("PrimarySoftBorderBrush", Brushes.LightSteelBlue),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 16)
        };
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock
        {
            Text = "将另存为", FontSize = 12, Foreground = Res("MutedBrush", Brushes.Gray)
        });
        inner.Children.Add(new TextBlock
        {
            Text = suggestedName, FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = Res("PrimaryBrush", Brushes.SteelBlue), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        box.Child = inner;
        sp.Children.Add(box);

        // 提前说清副作用：不然过几天在班级下拉 / 大屏里看到两个班，会以为出了 bug
        sp.Children.Add(new TextBlock
        {
            Text = "大屏与班级下拉里会同时看到这两个班，各自独立、互不影响。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = Res("MutedBrush", Brushes.Gray),
            Margin = new Thickness(0, -8, 0, 14)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right };

        var bSep = MakeButton("分开存（推荐）", "AppPrimaryButton");
        bSep.IsDefault = true;
        bSep.Click += (_, _) => Done(Choice.Separate);
        var bRename = MakeButton("改个班名", "AppGhostButton");
        bRename.Click += (_, _) => Done(Choice.Rename);
        var bCancel = MakeButton("取消", "AppGhostButton");
        bCancel.IsCancel = true;
        bCancel.Click += (_, _) => Done(Choice.Cancel);

        row.Children.Add(bSep);
        row.Children.Add(bRename);
        row.Children.Add(bCancel);
        sp.Children.Add(row);

        Content = sp;
    }

    private void Done(Choice c)
    {
        Result = c;
        DialogResult = true;
        Close();
    }

    private Button MakeButton(string text, string styleKey)
    {
        var b = new Button
        {
            Content = text, FontSize = 13, Padding = new Thickness(14, 7, 14, 7),
            MinHeight = 32, Margin = new Thickness(8, 0, 0, 0)
        };
        b.SetResourceReference(StyleProperty, styleKey);
        return b;
    }

    private static System.Windows.Media.Brush Res(string key, System.Windows.Media.Brush fallback)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
}
