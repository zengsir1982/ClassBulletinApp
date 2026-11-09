using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleCore.Models;

namespace ClassBulletinApp.Controls;

/// <summary>
/// 「焦点跟随」字体工具条的绑定器：把<b>当前聚焦的输入框 / 被点选的预览块</b>映射到对应的
/// <see cref="TextStyle"/>，一条工具条服务全窗所有字段。
/// <para>用法：窗口构造末尾 <c>new FocusStyleBinder(this, PickStyle)</c>，
/// 然后对每个可聚焦字段调 <see cref="Bind"/>；列表模板里的动态控件用
/// <see cref="BindAndActivate"/> 在 GotFocus 里临时登记。</para>
/// </summary>
/// <remarks>
/// ★ 三条容易踩的坑：
/// <list type="number">
/// <item>工具条里的按钮一律 <c>Focusable=False</c>，点按钮不会抢走输入框焦点；
///       万一焦点进了工具条（比如字号下拉展开），<see cref="TextStylePicker.HasFocusWithin"/>
///       会让这里保持激活，不误判成「焦点丢失」。</item>
/// <item>失焦判定必须 <c>Dispatcher.BeginInvoke</c> 延后一拍：焦点可能正在从 A 输入框
///       转移到 B 输入框，立刻判会闪一下灰。</item>
/// <item>置灰时<b>不换作用对象</b>，只停用。悄悄回退到「标题」会让老师误改别的字段。</item>
/// </list>
/// </remarks>
internal sealed class FocusStyleBinder
{
    private readonly System.Windows.Window _win;
    private readonly TextStylePicker _bar;

    /// <summary>控件 → 取样式对象的委托。用委托而不是直接存对象：切换便签 / 重建草稿后仍能取到最新的。</summary>
    private readonly Dictionary<FrameworkElement, Func<(TextStyle? Style, string Name)>> _map = new();

    public FocusStyleBinder(System.Windows.Window win, TextStylePicker bar)
    {
        _win = win ?? throw new ArgumentNullException(nameof(win));
        _bar = bar ?? throw new ArgumentNullException(nameof(bar));

        // handledEventsToo=true：DataGrid / ComboBox 会自己吞掉焦点事件，不这么做收不到
        win.AddHandler(Keyboard.GotKeyboardFocusEvent,
            new RoutedEventHandler(OnAnyGotFocus), true);
        win.AddHandler(Keyboard.LostKeyboardFocusEvent,
            new RoutedEventHandler(OnAnyLostFocus), true);

        bar.Deactivate();
    }

    /// <summary>登记一个静态字段（标题输入框、正文输入框等）。</summary>
    public void Bind(FrameworkElement el, Func<TextStyle?> style, string name)
    {
        if (el == null) return;
        _map[el] = () => (style(), name);
    }

    /// <summary>
    /// 登记一个「作用对象会变」的容器（DataGrid 按当前单元格列决定映射到哪个样式）。
    /// 名字也由委托返回，所以「星期 / 姓名 / 内容」能各自显示。
    /// </summary>
    public void Bind(FrameworkElement el, Func<(TextStyle? Style, string Name)> resolve)
    {
        if (el == null) return;
        _map[el] = resolve;
    }

    /// <summary>
    /// 登记并立即激活一个动态控件（列表模板里的输入框 / 可点选的预览块）。
    /// 模板生成的控件没法在构造时登记，只能在焦点事件里补一次。
    /// </summary>
    public void BindAndActivate(FrameworkElement el, TextStyle? style, string name)
    {
        if (el != null) _map[el] = () => (style, name);
        Activate(style, name);
    }

    /// <summary>手动切换作用对象（例如点了只读预览块）。</summary>
    public void Activate(TextStyle? style, string name)
    {
        if (style == null) { _bar.Deactivate(); return; }
        _bar.Attach(style, name);
    }

    public void Deactivate() => _bar.Deactivate();

    /// <summary>
    /// 诊断用：跳过路由事件，直接按某个源控件解析并激活，用来验证「字段 → 样式」映射本身。
    /// <para>为什么需要它：<c>Keyboard.GotKeyboardFocusEvent</c> 要求专用的
    /// <c>KeyboardFocusChangedEventArgs</c>，在未 Show 的窗口上手工 RaiseEvent 很容易构造不出来；
    /// 而诊断真正要锁的是映射表有没有把两个字段指向两个对象，不是 WPF 的路由机制。</para>
    /// </summary>
    internal void DiagFocus(FrameworkElement el)
    {
        if (TryResolve(el, out var style, out var name)) Activate(style, name);
        else _bar.Deactivate();
    }

    // ---------- 焦点判定 ----------

    private void OnAnyGotFocus(object sender, RoutedEventArgs e)
    {
        if (_bar.HasFocusWithin) return;     // 焦点还在工具条里（下拉展开）→ 保持
        if (e.OriginalSource is not DependencyObject node) return;

        if (TryResolve(node, out var style, out var name))
        {
            Activate(style, name);
            return;
        }

        // 焦点落在了非映射控件（窗口空白、按钮、主题下拉…）→ 置灰停用
        _bar.Deactivate();
    }

    private void OnAnyLostFocus(object sender, RoutedEventArgs e)
    {
        // 延后一拍：焦点可能正在转移到另一个映射控件
        _win.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_bar.HasFocusWithin) return;

            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null) { _bar.Deactivate(); return; }

            if (!TryResolve(focused, out _, out _)) _bar.Deactivate();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>沿可视树向上找登记过的控件（TextBox 内部还有 Grid / ScrollViewer 等包装层）。</summary>
    private bool TryResolve(DependencyObject? node, out TextStyle? style, out string name)
    {
        style = null;
        name = "";
        var cur = node;
        while (cur != null)
        {
            if (cur is FrameworkElement fe && _map.TryGetValue(fe, out var f))
            {
                (style, name) = f();
                return style != null;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }
}
