using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// 上下课提醒 / 课表变化提示共用的置顶小窗：不抢焦点、数秒后自动消失。
/// <para>位置与大小可在「提醒设置」里按所见即所得拖动调整，结果存 <see cref="ReminderSettings"/>，
/// 每次显示都按存档还原（<see cref="ReminderSettings.PopupScale"/> 同时决定宽度与字号）。</para>
/// </summary>
public partial class ReminderPopup : Window
{
    /// <summary>缩放 1 时的窗口宽度。</summary>
    private const double BaseWidth = 430;

    /// <summary>缩放 1 时的卡片宽度（= 窗口宽度 - 两侧各 10 的外边距）。</summary>
    private const double CardWidth = 410;

    private const double MinScale = 0.6;
    private const double MaxScale = 2.0;

    private DispatcherTimer? _closeTimer;

    /// <summary>当前生效的缩放比例。</summary>
    private double _scale = 1;

    /// <summary>非空 = 正在「调整位置与大小」模式。</summary>
    private ReminderSettings? _layout;
    private Action<ReminderSettings>? _onLayoutSaved;

    // 右边缘拖拽
    private bool _gripDragging;
    private double _gripStartWidth;
    private double _gripStartX;

    public ReminderPopup() : this(false) { }

    /// <param name="interactive">
    /// true = 用于「调整位置与大小」：可拖动、可拖右边框、按钮可点。
    /// false = 正常提醒：整窗鼠标穿透，不抢焦点。
    /// </param>
    public ReminderPopup(bool interactive)
    {
        InitializeComponent();

        ShowActivated = interactive;
        IsHitTestVisible = interactive;
        MinWidth = BaseWidth * MinScale;
        MaxWidth = BaseWidth * MaxScale;
    }

    // ---------- 对外接口 ----------

    /// <summary>显示一条提醒（正常模式）。</summary>
    public void ShowReminder(string title, string detail, int seconds, ReminderSettings? layout = null)
    {
        TxtTitle.Text = title;
        TxtDetail.Text = detail;
        EditBar.Visibility = Visibility.Collapsed;
        GripRight.Visibility = Visibility.Collapsed;

        ApplyLayout(layout);
        Show();

        _closeTimer?.Stop();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(3, seconds)) };
        _closeTimer.Tick += (_, _) => { _closeTimer?.Stop(); Hide(); };
        _closeTimer.Start();
    }

    /// <summary>
    /// 进入「调整位置与大小」：窗口直接摆在当前存档的位置，用户拖动移动、拖右边框改大小，
    /// 保存后回调新布局（调用方负责落盘）。窗口关闭后本实例不再复用。
    /// </summary>
    public void BeginLayoutEdit(ReminderSettings src, Action<ReminderSettings> onSaved)
    {
        _layout = src;
        _onLayoutSaved = onSaved;

        // 用真实提醒的示例文案，改大小的时候看到的就是实际效果
        TxtTitle.Text = "课表已更新";
        TxtDetail.Text = "第3节 数学 → 英语；第5节 体育 → 数学";
        EditBar.Visibility = Visibility.Visible;
        GripRight.Visibility = Visibility.Visible;

        ApplyLayout(src);

        Loaded += (_, _) =>
        {
            try { Card.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x6C, 0xDF)); } catch { }
            PlaceBySettings(src);
            Activate();
            Focus();
        };

        if (!IsVisible) Show();
    }

    // ---------- 位置 / 大小 ----------

    /// <summary>按设置套用缩放并还原位置（位置等布局完成后再放，避免高度还没测算出来）。</summary>
    private void ApplyLayout(ReminderSettings? r)
    {
        double s = r != null && r.PopupScale > 0 ? r.PopupScale : 1;
        ApplyScale(s);
        Dispatcher.BeginInvoke(new Action(() => PlaceBySettings(r)), DispatcherPriority.Loaded);
    }

    private void ApplyScale(double scale)
    {
        try
        {
            _scale = Math.Clamp(scale, MinScale, MaxScale);

            // 窗口用 SizeToContent=WidthAndHeight：改卡片宽度就等于改窗口大小，字号同步等比放大
            Card.Width = CardWidth * _scale;
            Card.Margin = new Thickness(10 * _scale);
            Card.Padding = new Thickness(20 * _scale, 14 * _scale, 20 * _scale, 14 * _scale);
            Card.CornerRadius = new CornerRadius(16 * _scale);

            TxtTitle.FontSize = 24 * _scale;
            TxtDetail.FontSize = 15 * _scale;
            TxtDetail.Margin = new Thickness(0, 6 * _scale, 0, 0);
        }
        catch { }
    }

    /// <summary>把窗口摆到设置里的位置；没存过就顶部居中。</summary>
    private void PlaceBySettings(ReminderSettings? r)
    {
        try
        {
            var wa = FloatingHelper.WorkAreaDip(this);
            double w = ActualWidth > 0 ? ActualWidth : BaseWidth;
            double h = ActualHeight > 0 ? ActualHeight : 90 * _scale;

            double left = r?.PopupLeft is double l ? l : wa.Left + Math.Max(0, (wa.Width - w) / 2);
            double top = r?.PopupTop is double t ? t : wa.Top + 72;

            var rect = FloatingHelper.ClampRectDip(this, new Rect(left, top, w, h));
            Left = rect.X;
            Top = rect.Y;
        }
        catch { }
    }

    // ---------- 调整模式：拖动移动 / 拖右边框改大小 ----------

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_layout == null) return;

        // 落在按钮上就交给按钮，别把点击吞成拖动
        var node = e.OriginalSource as DependencyObject;
        while (node != null)
        {
            if (node is Button) return;
            node = VisualTreeHelper.GetParent(node);
        }

        try { DragMove(); } catch { }
        e.Handled = true;
    }

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_layout == null) return;
        _gripDragging = true;
        _gripStartWidth = Card.Width > 0 ? Card.Width : Card.ActualWidth;
        _gripStartX = e.GetPosition(this).X;
        try { GripRight.CaptureMouse(); } catch { }
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_gripDragging) return;
        double dx = e.GetPosition(this).X - _gripStartX;
        if (Math.Abs(dx) < 0.5) return;
        ApplyScale((_gripStartWidth + dx) / CardWidth);
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        try { GripRight.ReleaseMouseCapture(); } catch { }
        e.Handled = true;
    }

    // ---------- 保存 / 取消 ----------

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_layout == null) return;
        if (e.Key == Key.Enter) { e.Handled = true; CommitLayout(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelLayout(); }
    }

    private void BtnSaveLayout_Click(object sender, RoutedEventArgs e) => CommitLayout();

    private void BtnCancelLayout_Click(object sender, RoutedEventArgs e) => CancelLayout();

    private void CommitLayout()
    {
        var target = _layout;
        var cb = _onLayoutSaved;
        if (target == null) return;

        double w = Card.ActualWidth > 1 ? Card.ActualWidth : CardWidth * _scale;
        target.PopupScale = Math.Round(Math.Clamp(w / CardWidth, MinScale, MaxScale), 2);
        target.PopupLeft = Math.Round(Left, 1);
        target.PopupTop = Math.Round(Top, 1);

        EndLayoutEdit();
        try { cb?.Invoke(target); } catch { }
    }

    private void CancelLayout() => EndLayoutEdit();

    // ---------- 自动化自测用（--shot remindlayoutsave） ----------

    /// <summary>诊断用：直接把缩放设成指定值，等价于用户把右边缘拖到该大小。</summary>
    internal void DiagApplyScale(double scale) => ApplyScale(scale);

    /// <summary>诊断用：等价于点「保存」。</summary>
    internal void DiagCommit() => CommitLayout();

    private void EndLayoutEdit()
    {
        _layout = null;
        _onLayoutSaved = null;
        try { Close(); } catch { }
    }
}
