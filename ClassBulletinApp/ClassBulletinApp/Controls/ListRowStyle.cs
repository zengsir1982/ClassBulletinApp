using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// ★ 本项目 UseWPF + UseWindowsForms 且开了 ImplicitUsings：Brush / Brushes / Color / Control
//   在两个命名空间里各有一份，不显式指定就是 CS0104。
using Control = System.Windows.Controls.Control;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace ClassBulletinApp;

/// <summary>
/// 直接 <c>new ListBoxItem</c> 塞进 <c>ListBox.Items</c> 时的行样式（选中 / 悬停底色）。
///
/// <para>★ 两件事必须自己做，否则"选中了看不出来"：</para>
/// <list type="number">
/// <item>行是代码 new 出来的实例，<c>ItemContainerStyle</c> 管不到它们 —— Style 得一个个挂到 item 上；</item>
/// <item>只设 <c>Background</c> 没用：主题模板的选中态取的是系统画笔（非活动窗口那个灰几乎看不见），
/// 必须自己给 <c>ListBoxItem</c> 模板，让 Border 直接绑 <c>Background</c>。</item>
/// </list>
///
/// <para>用户管理（r155 起支持多选）与课表生成账号两个窗共用这一份，避免"一个窗口能看出选中、另一个看不出"。</para>
/// </summary>
internal static class ListRowStyle
{
    static Style? _style;

    internal static Style Get()
    {
        if (_style != null) return _style;

        var s = new Style(typeof(ListBoxItem));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 5, 6, 5)));
        s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                                 System.Windows.HorizontalAlignment.Stretch));

        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        s.Setters.Add(new Setter(Control.TemplateProperty,
                                 new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd }));

        // 悬停与选中：选中放后面 → 同时满足时选中色优先
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, HexBrush("#F1F5FC")));
        s.Triggers.Add(hover);

        var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Control.BackgroundProperty, HexBrush("#DCE8FA")));
        s.Triggers.Add(sel);

        _style = s;
        return s;
    }

    internal static System.Windows.Media.Brush HexBrush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch { return Brushes.WhiteSmoke; }
    }
}
