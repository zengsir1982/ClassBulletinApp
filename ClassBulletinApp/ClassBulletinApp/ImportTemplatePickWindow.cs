using System;
using System.Windows;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>选择要下载的课表导入模板（4 选 1）。</summary>
internal class ImportTemplatePickWindow : Window
{
    public ImportTemplateWriter.TemplateKind Kind { get; private set; } = ImportTemplateWriter.TemplateKind.Base;

    public ImportTemplatePickWindow()
    {
        Title = "下载模板";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 16, 18, 14) };
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "选一种模板下载，按模板填好后用「智能导入课表」导入即可。"
                 + "模板里已写好示例（含单双周写法「语文-单|数学-双」和临时课表的「体育（调）」）。",
            FontSize = 13, Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });

        void AddBtn(string text, ImportTemplateWriter.TemplateKind kind)
        {
            var b = new Button
            {
                Content = text, FontSize = 13, Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            b.Click += (_, _) => { Kind = kind; DialogResult = true; Close(); };
            root.Children.Add(b);
        }

        AddBtn("总课表（全校多班）—— 一次导入七 / 八 / 九年级", ImportTemplateWriter.TemplateKind.Base);
        AddBtn("临时总课表 —— 只放调课 / 代课（带（调）（代）标记）", ImportTemplateWriter.TemplateKind.TempBase);
        AddBtn("单班课表 —— 只排一个班", ImportTemplateWriter.TemplateKind.SingleClass);
        AddBtn("单个教师课表 —— 只排一位教师", ImportTemplateWriter.TemplateKind.Teacher);

        var cancel = new Button
        {
            Content = "取消", FontSize = 13, Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        root.Children.Add(cancel);

        Content = root;
        GlassChrome.Prepare(this);
    }
}
