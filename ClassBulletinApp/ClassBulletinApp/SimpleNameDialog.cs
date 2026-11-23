using System.Windows;
using System.Windows.Controls;
using HAlign = System.Windows.HorizontalAlignment;
// 全局 using 同时带进了 WinForms，TextBox / CheckBox 会二义（CS0104），这里显式指到 WPF
using TextBox = System.Windows.Controls.TextBox;

namespace ClassBulletinApp;

/// <summary>
/// 轻量文本输入对话框（「另存为手录课表方案」等只有一个名字要填的场合）。
/// <para>r142：从已删除的课表基础数据维护窗口里搬出来独立成文件 —— 那边原本是「添加 / 改名」共用的，
/// 窗口删掉后只剩手录窗口还在用。类名去掉 BaseData 前缀，免得以后search 到一个不存在的模块。</para>
/// </summary>
internal class SimpleNameDialog : Window
{
    private readonly TextBox _tb = new();

    public string EnteredName => _tb.Text.Trim();

    /// <param name="placeholder">历史值：传进来的是"当前已有名字"时直接回填，否则留空。</param>
    public SimpleNameDialog(string title, string current, string placeholder)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });

        _tb.Text = current == placeholder ? "" : current;
        _tb.ToolTip = current;
        _tb.FontSize = 14;
        sp.Children.Add(_tb);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button { Content = "确定", Width = 78, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(EnteredName)) { DialogResult = true; Close(); }
        };
        var cancel = new Button { Content = "取消", Width = 78, IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        sp.Children.Add(row);

        Content = sp;
        Loaded += (_, _) => { _tb.Focus(); _tb.SelectAll(); };
    }
}
