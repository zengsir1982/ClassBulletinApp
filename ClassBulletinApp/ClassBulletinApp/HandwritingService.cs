using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// WinForms 与 WPF 同名类型（项目同时引用两者），这里显式指定 WPF 版本。
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;

namespace ClassBulletinApp;

/// <summary>
/// 「手写输入」的统一入口：打开手写面板，把识别结果插回<b>最后获得焦点的输入框</b>。
/// <para>两条入口共用同一套逻辑：</para>
/// <list type="bullet">
/// <item><b>编辑窗底部的「✎ 手写输入」按钮</b> → 模态打开（写完就回来，不干扰编辑窗）。</item>
/// <item><b>全局悬浮球</b> → 非模态打开（面板不锁住别的窗口，可以边看边写）。</item>
/// </list>
/// <para><b>没有目标输入框时怎么办：</b>不弹"请先点输入框"这种打断人的提示，而是照常打开面板，
/// 识别结果进面板里的「结果」框，可以一键复制到任何地方（甚至是别的程序）。</para>
/// <para><b>为什么要能删：</b>手写多半是用来<b>改</b>已有内容的（比如把「张三」改成「李四」）——
/// 写完发现前面还有旧字删不掉，就得在触屏上小心翼翼地点到字缝里按退格，比写字还费劲。
/// 所以面板里直接给「删一字」和「清空输入框」，作用对象就是那个目标框。</para>
/// </summary>
public static class HandwritingService
{
    /// <summary>从编辑窗按钮打开（模态）。</summary>
    public static void Open(Window? owner) => Show(owner, modal: true);

    /// <summary>从悬浮球打开（非模态，不锁住其他窗口）。</summary>
    public static void OpenFromBall() => Show(null, modal: false);

    private static void Show(Window? owner, bool modal)
    {
        var target = UsableTarget();
        var handle = target == null ? null : new TargetHandle(target);

        var win = new HandwritingPopup(
            handle == null ? null : text => { var tb = handle.Get(); if (tb != null) Insert(tb, text); },
            handle == null ? null : () => DeleteBack(handle.Get()),
            handle == null ? null : () => ClearAll(handle.Get()));

        win.Modal = modal;

        // Owner 必须是可见窗口，否则 ShowDialog 会抛 InvalidOperationException。
        // 非模态（悬浮球）不设 Owner：球是 NOACTIVATE 的常驻小窗，挂上去只会让面板跟着它跑。
        if (modal && owner != null && owner.IsVisible) win.Owner = owner;

        win.PlaceSmart(target == null ? null : ScreenRectDip(target));

        // 手写期间压住系统触摸键盘：两个面板都在底部，一起弹会互相挡。
        TouchInputBinder.SuppressAutoShow = true;
        TouchKeyboard.Hide();
        win.Closed += (_, _) => TouchInputBinder.SuppressAutoShow = false;

        // 从球打开时先把球藏起来：面板一出现，球就没用了，还挡视线；关掉面板再按设置恢复。
        // 编辑窗按钮打开的模态面板不动球 —— 那是"顺手用一下"，收起球反而让人以为丢了。
        if (!modal)
        {
            App.HandwritingBall?.ApplyVisibility(false);
            win.Closed += (_, _) => App.EnsureHandwritingBall();
        }

        if (modal)
        {
            win.ShowDialog();
        }
        else
        {
            win.Show();
            win.Activate();          // 球本身不激活，面板得自己抢一次焦点，否则点不动
        }
    }

    /// <summary>最后一次点过的输入框；已经关掉 / 禁用 / 不可见的都当作没有。</summary>
    private static TextBox? UsableTarget()
    {
        var tb = TouchInputBinder.LastFocusedTextBox;
        return tb == null ? null : Check(tb);
    }

    /// <summary>框还在不在、还能不能用。面板可能开很久，期间编辑窗被关掉是常事。</summary>
    private static TextBox? Check(TextBox? tb)
    {
        if (tb == null) return null;
        try
        {
            if (!tb.IsVisible || !tb.IsEnabled) return null;
            if (Window.GetWindow(tb) is not Window w || !w.IsVisible) return null;
            return tb;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 弱引用抱住目标框：面板是非模态的，可能开着好几分钟。
    /// 强引用会把已经关掉的编辑窗一直留在内存里（连带它的课表数据），弱引用则让它正常回收。
    /// </summary>
    private sealed class TargetHandle
    {
        private readonly WeakReference<TextBox> _ref;

        public TargetHandle(TextBox tb) => _ref = new WeakReference<TextBox>(tb);

        public TextBox? Get() => _ref.TryGetTarget(out var tb) ? Check(tb) : null;
    }

    /// <summary>元素在屏幕上的矩形（DIP），用于让面板避开它。取不到就返回 null（面板居中）。</summary>
    private static Rect? ScreenRectDip(FrameworkElement el)
    {
        try
        {
            var src = PresentationSource.FromVisual(el);
            if (src?.CompositionTarget == null) return null;

            var m = src.CompositionTarget.TransformFromDevice;
            var p0 = m.Transform(el.PointToScreen(new Point(0, 0)));
            var p1 = m.Transform(el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight)));
            return new Rect(p0, p1);
        }
        catch
        {
            return null;
        }
    }

    private static void Insert(TextBox tb, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (tb.SelectionLength > 0)
        {
            tb.SelectedText = text;
        }
        else
        {
            string current = tb.Text ?? "";
            int caret = Math.Clamp(tb.CaretIndex, 0, current.Length);
            tb.Text = current.Insert(caret, text);
            tb.CaretIndex = caret + text.Length;
        }

        // 模态打开时目标窗被禁用，Focus() 会返回 false 而不是抛异常；插进去就够了。
        try { tb.Focus(); } catch { }
    }

    /// <summary>删掉光标前一个字（有选区就删选区）。</summary>
    private static void DeleteBack(TextBox? tb)
    {
        if (tb == null) return;

        if (tb.SelectionLength > 0)
        {
            tb.SelectedText = "";
        }
        else
        {
            string s = tb.Text ?? "";
            int caret = Math.Clamp(tb.CaretIndex, 0, s.Length);
            if (caret <= 0) return;

            // 不把代理对（emoji 等）拆成半个字符，否则文本框里会剩一个"豆腐块"
            int n = 1;
            if (caret >= 2 && char.IsLowSurrogate(s[caret - 1]) && char.IsHighSurrogate(s[caret - 2]))
                n = 2;

            tb.Text = s.Remove(caret - n, n);
            tb.CaretIndex = caret - n;
        }

        try { tb.Focus(); } catch { }
    }

    /// <summary>清空输入框（面板里要二次点击确认才会走到这里）。</summary>
    private static void ClearAll(TextBox? tb)
    {
        if (tb == null) return;

        tb.Text = "";
        tb.CaretIndex = 0;
        try { tb.Focus(); } catch { }
    }
}
