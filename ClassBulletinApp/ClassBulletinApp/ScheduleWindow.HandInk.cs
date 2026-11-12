using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 全局 using 里带了 System.Drawing（截图 / 壁纸渲染在用），Color / Brush / Brushes 会撞名
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using PathShape = System.Windows.Shapes.Path;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮课表的「板书模式」：在课表左边长出一块手写区（r105）。
/// <para>独立成 partial 文件是因为 <see cref="ScheduleWindow"/> 主文件已经三千多行，
/// 手势 / 拖动 / 课表渲染全在里面，板书这套完全不相干的东西混进去只会更难查。</para>
/// <para>★ 笔迹按「日期」存：<c>%AppData%\ClassBulletinApp\handink\yyyyMMdd.isf</c>，
/// 只保留最近 <see cref="HandKeepDays"/> 天 —— 一学期天天写，不清理会攒出几百个文件，
/// 而且那些板书根本没人回头看。</para>
/// </summary>
public partial class ScheduleWindow
{
    // ---------- 尺寸 ----------
    private const double HandDefaultWidth = 420;
    /// <summary>兜底下限：课表区比它还窄时用它（否则会拖出一条写不了字的窄缝）。</summary>
    private const double HandMinWidth = 280;
    /// <summary>上限只用来防手滑拖飞，真正的天花板是屏幕左边沿。</summary>
    private const double HandMaxWidth = 1400;
    /// <summary>板书模式下课表列的保底宽度（含窗口那圈固定边）。</summary>
    private const double HandCourseMinWidth = 220;
    /// <summary>没有真实窗口宽度时的课表列兜底宽（诊断窗口 / 首帧）。</summary>
    private const double HandCourseFallbackWidth = 520;

    /// <summary>笔迹保留天数（含今天）。</summary>
    internal const int HandKeepDays = 7;

    // ---------- 状态 ----------
    private bool _handMode;
    private double _handWidth = HandDefaultWidth;
    /// <summary>板书模式下「课表列」的宽度（窗口宽 − 手写区宽）。左沿拖动靠它把课表钉死。</summary>
    private double _handCourseWidth = HandDefaultWidth;
    /// <summary>上一次看到的窗口左边沿（DIP），用来认出「这一下是拖左沿，不是拖右沿」。</summary>
    private double _handLastLeft = double.NaN;
    private bool _handGeomGuard;
    /// <summary>当前板书页是哪一天。</summary>
    private DateTime _handDate = DateTime.Today;
    /// <summary>本次切到板书模式时窗口是「向左扩」还是「向右扩」，收起时按相反方向还原。</summary>
    private bool _handShiftedLeft;
    /// <summary>最近写下的笔画（撤销用）。切日期 / 清空会清掉。</summary>
    private readonly List<Stroke> _handUndo = new();
    private DispatcherTimer? _handSaveTimer;
    /// <summary>几何静止后落盘用的计时器（系统缩放没有「结束」事件）。</summary>
    private DispatcherTimer? _handGeomTimer;
    /// <summary>正在灌笔迹：这期间别触发保存，否则会把刚要读的文件又写一遍。</summary>
    private bool _handLoading;
    private string _handPenColor = "#1F2A37";
    private double _handPenWidth = 2.6;

    // ---------- 拖动 ----------
    /// <summary>0 = 没在拖；1 = 拖窗口左沿（改手写区宽）；2 = 拖两区中间那条缝（改课表宽）。</summary>
    private int _gripSide;
    /// <summary>按下时鼠标的屏幕横坐标（物理像素，避免窗口一移就自己跟着跑）。</summary>
    private double _gripStartCursorX;
    private double _gripStartLeft;
    private double _gripStartHand;
    private double _gripStartCourse;
    private double _gripStartWidth;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint p);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect r);

    // ---------- 工具条图标（选中态要跟着换色，所以留字段） ----------
    private PathShape? _iconPen, _iconEraser, _iconUndo, _iconClear;

    // ---------- 存储 ----------
    private static string HandDir => Path.Combine(Store.AppDir, "handink");

    private static string HandFile(DateTime d) => Path.Combine(HandDir, d.ToString("yyyyMMdd") + ".isf");

    // ======================================================================
    //  模式切换
    // ======================================================================

    /// <summary>
    /// 切到 / 切出板书模式。
    /// <para><paramref name="expandWindow"/> = false 只用于启动恢复：那时窗口宽高已经由
    /// 布局设好（里面已经含了手写区），再加减一次就会错位。</para>
    /// </summary>
    private void SetHandMode(bool on, bool expandWindow = true)
    {
        if (_handMode == on) return;
        _handMode = on;

        if (on)
        {
            // 手写区不能把窗口撑出屏幕：左边没地方就改成向右长（课表列右移一点，但都在屏内）
            // ⚠ 未 Show 的窗口 Width 是 NaN（诊断会走到这儿），先换成有效值再算
            double rawW = double.IsNaN(Width) || Width < 50 ? 0 : Width;
            double winW = rawW > 0
                ? rawW
                : (expandWindow ? HandCourseFallbackWidth : _handWidth + HandCourseFallbackWidth);
            double need = Math.Min(_handWidth, Math.Max(HandMinWidth, SystemParameters.WorkArea.Width - winW));
            _handWidth = Math.Clamp(need, HandMinWidth, HandMaxWidth);

            // 简洁模式下整窗都是课表列；启动恢复时窗口宽里已经含了手写区，要把手写区扣掉
            // ⚠ 此刻 HandCol 还是 0（下面才设），扣的是 _handWidth
            _handCourseWidth = Math.Max(HandCourseMinWidth,
                expandWindow ? winW : winW - _handWidth);

            if (expandWindow)
            {
                if (Left >= _handWidth)
                {
                    Left -= _handWidth;      // 课表列原地不动，这是用户预期的「往左长」
                    _handShiftedLeft = true;
                }
                else
                {
                    _handShiftedLeft = false;
                }
                Width = winW + _handWidth;
            }
            _handLastLeft = double.IsNaN(Left) ? double.NaN : Left;
        }

        HandPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HandSplitter.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (HandLeftHint != null) HandLeftHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HandCol.Width = new GridLength(on ? _handWidth : 0);
        BtnHandToggle.Content = on ? "收起板书" : "板书";
        // ★ 最窄宽度跟着手写区走（手写区 + 课表列保底），不写死 520：
        //   写死时，简洁模式下窗口若比 240 还窄，一进板书模式会被强制加宽 —— 那又把课表列改了。
        MinWidth = on ? Math.Max(HandMinWidth, HandWidthFloor) + HandCourseMinWidth : 220;

        if (on)
        {
            LoadHandInk(_handDate);
            UpdateHandHeader();
            ApplyHandPen();
            UpdateHandToolVisual();
            // 两侧蓝条要一样高，否则左边矮一截、看着像两块窗拼起来的
            Dispatcher.BeginInvoke(new Action(SyncHandHeaderHeight), DispatcherPriority.Loaded);
        }
        else
        {
            SaveHandInkNow();
            _handGeomTimer?.Stop();
            if (expandWindow)
            {
                // ★ 宽度以真实窗口矩形为准：用户拖过左沿之后 Width 可能是旧值，
                //   拿旧值减手写区宽 → 收起后窗口宽度就回不去了
                double current = WindowRectDip(out _, out double realW)
                    ? realW
                    : (double.IsNaN(Width) || Width < 50 ? 420 : Width);
                if (_handShiftedLeft) Left += _handWidth;
                Width = Math.Max(220, current - _handWidth);
            }
            _handShiftedLeft = false;
            _handLastLeft = double.NaN;
        }

        SaveHandMode();
        UpdateBackdropButton();
        if (expandWindow) SavePlacement();
    }

    private void SaveHandMode()
    {
        try
        {
            var s = Store.LoadSettings();
            s.ScheduleHandMode = _handMode;
            s.ScheduleHandWidth = _handWidth;
            Store.SaveSettings(s);
        }
        catch { /* 存不上只是下次打开不记得，不该影响写字 */ }
    }

    private void BtnHandToggle_Click(object sender, RoutedEventArgs e) => ToggleHandMode();

    /// <summary>「板书 / 收起板书」按钮的全部行为（诊断也走这一条，别另写一份）。</summary>
    private void ToggleHandMode()
    {
        bool on = !_handMode;
        if (on)
        {
            // 写字得有一块安静的地方：先把开着的黑板收起来（配置和笔迹都留着，随时能再打开）
            try { DoodleBoardService.HideAllOpen(); } catch { }

            SetHandMode(true);
            ForceResidentNoTop();
        }
        else
        {
            SetHandMode(false);
        }
    }

    /// <summary>
    /// 板书模式 = 常驻显示 + 不置顶。
    /// <para>★ 自动隐藏状态下窗口会缩到屏幕边上：手写区一半在屏外，写两笔窗口就自己跑了，
    /// 板书根本没法用。所以一进板书模式就把它固定成常驻；同时取消置顶，
    /// 免得整块板书压在课件 / PPT 上面。</para>
    /// <para>这是写进设置里的状态变更，退出板书模式也保持 —— 用户看到「自动隐藏 / 置顶」
    /// 两个按钮的文字和底色会跟着变，随时能改回去。</para>
    /// </summary>
    private void ForceResidentNoTop()
    {
        try
        {
            var s = Store.LoadSettings();
            var l = s.ScheduleLayout ??= new PanelLayout();
            l.AutoHide = false;
            l.AutoHideDuringClass = false;
            l.ClickThrough = false;
            l.TopMost = false;
            Store.SaveSettings(s);

            _layout = l;
            _clickThrough = false;
            Topmost = false;
            FloatingHelper.SetClickThrough(this, false);

            _hider?.Dispose();
            _hider = null;

            if (BtnHide != null) BtnHide.Content = "自动隐藏";
            UpdateTopButton();
        }
        catch { /* 设置读不出来就只把窗口属性改掉，功能照常 */ }
    }

    /// <summary>
    /// 手写区蓝条对齐课表蓝条。
    /// <para>课表那边按钮多时蓝条会变高（三个按钮竖排），左边只有「收起」一个，
    /// 不拉平就会矮一截 —— 一条横线断成两段，非常像两块窗口拼在一起。</para>
    /// </summary>
    private void SyncHandHeaderHeight()
    {
        if (HandHeader == null || HeaderBar == null) return;
        double h = HeaderBar.ActualHeight;
        if (h > 10 && Math.Abs(HandHeader.MinHeight - h) > 0.5)
            HandHeader.MinHeight = h;
    }

    private void BtnHandCollapse_Click(object sender, RoutedEventArgs e) => SetHandMode(false);

    /// <summary>
    /// 手写区最窄能拖到多窄。
    /// <para>按用户的要求：最小可以拉到「宽度只有课表区的宽度」—— 也就是窄到和课表一样宽为止。
    /// 课表特别窄 / 特别宽时用 280~640 收一圈，免得拖出一条写不了字的窄缝，
    /// 或者宽到怎么拖都收不回来。</para>
    /// </summary>
    private double HandWidthFloor =>
        Math.Clamp(_handCourseWidth > 60 ? _handCourseWidth : HandDefaultWidth, HandMinWidth, 640);

    /// <summary>当前窗口的真实左边沿与宽度（DIP）。系统缩放后 <c>Width</c> 不一定是真值，一律问 GetWindowRect。</summary>
    private bool WindowRectDip(out double left, out double width)
    {
        left = 0; width = 0;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var nr)) return false;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi < 0.1) dpi = 1;
        left = nr.Left / dpi;
        width = (nr.Right - nr.Left) / dpi;
        return width > 40;
    }

    // ======================================================================
    //  两条边界
    // ======================================================================
    //  ★ 语义（r107 用户拍定，别再改回去）：
    //    ① 手写区左侧那条边 → 改【手写区】宽度，课表列一个像素都不动；
    //    ② 中间那条缝（手写区右边沿 = 课表左边沿）→ 改【课表】宽度，手写区宽度一个像素都不动，
    //       整块跟着缝平移、窗口右边界钉死（跟拖窗口左边框同一个手感）。
    //    左沿走的是系统窗口缩放（系统只认「窗口变宽了」，不知道还有两列），所以这里补偿：
    //    认出左边沿动了，就把这次宽度变化整个记到手写区头上，课表列保持钉死。

    /// <summary>
    /// 窗口几何变了：分辨这一下拖的是左沿还是右沿。
    /// <para>拖左沿 = 手写区变宽；拖右沿 = 课表变宽（课表本来就在最右边，符合直觉）。</para>
    /// </summary>
    private void OnHandWindowGeometryChanged()
    {
        if (!_handMode || _gripSide != 0 || _handGeomGuard) return;
        if (!WindowRectDip(out double left, out double winW)) return;
        if (HandCol.Width.Value < 1) return;

        double prevLeft = _handLastLeft;
        _handLastLeft = left;

        if (!double.IsNaN(prevLeft) && Math.Abs(left - prevLeft) > 0.6)
            ApplyLeftEdgeResize(winW);                       // 左沿被拖：宽度变化全记给手写区
        else
            _handCourseWidth = Math.Max(HandCourseMinWidth, winW - HandCol.Width.Value);

        QueueHandGeometrySave();
    }

    /// <summary>
    /// 松手后延迟落盘：系统缩放（拖窗口边沿）没有「结束」事件，
    /// 只能靠「几何静止 0.7 秒」判断这一次调完了。
    /// <para>顺手把 <c>Width</c> 对齐到真实宽度 —— 系统缩放之后 Width 未必跟着更新，
    /// 留着旧值会让「收起板书」按错的宽度算，窗口宽度就回不去了。</para>
    /// </summary>
    private void QueueHandGeometrySave()
    {
        if (_handGeomTimer == null)
        {
            _handGeomTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _handGeomTimer.Tick += (_, _) =>
            {
                _handGeomTimer!.Stop();
                PersistHandGeometry();
            };
        }
        _handGeomTimer.Stop();
        _handGeomTimer.Start();
    }

    private void PersistHandGeometry()
    {
        if (!_handMode || _gripSide != 0) return;
        if (ActualWidth > 40 && (double.IsNaN(Width) || Math.Abs(Width - ActualWidth) > 0.5))
            Width = ActualWidth;
        SavePlacement();
    }

    /// <summary>
    /// 左沿拖过之后：课表列宽保持 <see cref="_handCourseWidth"/>，窗口宽度变化全部由手写区吸收。
    /// <para>纯几何，不碰窗口位置 —— 位置是系统给的，动它会在缩放循环里打架。</para>
    /// </summary>
    private void ApplyLeftEdgeResize(double winW)
    {
        double floor = HandWidthFloor;
        double upper = Math.Max(floor, winW - HandCourseMinWidth);
        double hand = Math.Clamp(winW - _handCourseWidth, floor, upper);
        if (Math.Abs(hand - HandCol.Width.Value) < 0.5) return;

        _handGeomGuard = true;
        try
        {
            HandCol.Width = new GridLength(hand);
            _handWidth = hand;
            double minW = Math.Max(HandMinWidth, HandWidthFloor) + HandCourseMinWidth;
            if (Math.Abs(MinWidth - minW) > 1) MinWidth = minW;
        }
        finally { _handGeomGuard = false; }
    }

    /// <summary>
    /// 按住两区中间那条缝开始拖。
    /// <para>★ 用自定义拖动而不是 GridSplitter：GridSplitter 在固定窗口宽里只能两列此消彼长，
    /// 拖出来的效果是「手写区被挤窄」—— 用户要的是手写区一点不变、课表变宽变窄。</para>
    /// </summary>
    private void HandSplitter_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_handMode) return;
        e.Handled = true;

        // 真值取自窗口矩形：用户拖过窗口边沿之后 Width 可能是旧的
        double w = double.IsNaN(Width) || Width < 50 ? 420 : Width;
        double l = double.IsNaN(Left) ? 0 : Left;
        WindowRectDip(out double rl, out double rw);
        if (rw > 40) { w = rw; l = rl; }

        _gripSide = 2;
        _gripStartCursorX = CursorScreenX();
        _gripStartLeft = l;
        _gripStartWidth = w;
        _gripStartHand = HandCol.Width.Value > 1 ? HandCol.Width.Value : _handWidth;
        _gripStartCourse = Math.Max(HandCourseMinWidth, w - _gripStartHand);

        HandSplitterLine.Background = new SolidColorBrush(Color.FromRgb(0x5F, 0x86, 0xB8));
        HandSplitter.CaptureMouse();
        if (_hider != null) _hider.Suspended = true;   // 拖的时候别让自动隐藏把窗口拉走
    }

    private void HandSplitter_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_gripSide != 2) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndHandDrag(); return; }

        // 屏幕像素抓光标（窗口自己也在移，用窗口内坐标会形成正反馈，越拖越飘）
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi < 0.1) dpi = 1;
        double dx = (_gripStartCursorX - CursorScreenX()) / dpi;   // 往左拖 = 正 = 缝左移 = 课表变宽

        // 右边界钉死，所以「课表最宽」= 窗口左沿正要贴住屏幕左边沿的那一刻
        double right = _gripStartLeft + _gripStartWidth;
        double maxCourse = Math.Max(HandCourseMinWidth,
            right - SystemParameters.WorkArea.Left - _gripStartHand);
        double course = Math.Clamp(_gripStartCourse + dx, HandCourseMinWidth, maxCourse);
        ApplyCourseWidthLive(course);
    }

    /// <summary>
    /// 只改课表列：手写区宽度一个像素都不动、窗口右边界钉死，整块随缝左右平移。
    /// </summary>
    private void ApplyCourseWidthLive(double course)
    {
        double right = _gripStartLeft + _gripStartWidth;
        double w = _gripStartHand + course;

        HandCol.Width = new GridLength(_gripStartHand);
        Width = w;
        Left = right - w;

        _handWidth = _gripStartHand;
        _handCourseWidth = course;
    }

    private void HandSplitter_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gripSide != 2) return;
        e.Handled = true;
        EndHandDrag();
    }

    private void EndHandDrag()
    {
        if (_gripSide == 0) return;
        _gripSide = 0;
        HandSplitter.ReleaseMouseCapture();
        HandSplitterLine.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xE3, 0xEC));
        if (_hider != null) _hider.Suspended = false;

        _handWidth = Math.Clamp(HandCol.Width.Value, HandMinWidth, HandMaxWidth);
        HandCol.Width = new GridLength(_handWidth);

        // 以真实窗口宽为准重算一次，别让累计误差留下来
        if (WindowRectDip(out double left, out double winW))
        {
            _handCourseWidth = Math.Max(HandCourseMinWidth, winW - _handWidth);
            _handLastLeft = left;
        }
        MinWidth = Math.Max(HandMinWidth, HandWidthFloor) + HandCourseMinWidth;

        SaveHandMode();
        SavePlacement();
    }

    private void HandSplitter_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_gripSide == 0)
            HandSplitterLine.Background = new SolidColorBrush(Color.FromRgb(0x9F, 0xB4, 0xCE));
    }

    private void HandSplitter_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_gripSide == 0)
            HandSplitterLine.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xE3, 0xEC));
    }

    private static double CursorScreenX() => GetCursorPos(out var p) ? p.X : 0;

    // ======================================================================
    //  日期页
    // ======================================================================

    private void BtnHandPrev_Click(object sender, RoutedEventArgs e) => JumpHandDay(-1);

    private void BtnHandNext_Click(object sender, RoutedEventArgs e) => JumpHandDay(1);

    /// <summary>点日期文字回今天（翻远了想回来，不必一格一格点）。</summary>
    private void TxtHandDate_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_handDate.Date == DateTime.Today) return;
        SaveHandInkNow();
        _handDate = DateTime.Today;
        LoadHandInk(_handDate);
        UpdateHandHeader();
    }

    private void JumpHandDay(int delta)
    {
        SaveHandInkNow();

        // 最多往回翻 6 天（正好是保留期），往后给 30 天：再远的历史文件已经清掉了，
        // 翻过去只会看到一片空白，反而让人以为笔迹丢了。
        var target = _handDate.AddDays(delta).Date;
        var oldest = DateTime.Today.AddDays(-(HandKeepDays - 1));
        var newest = DateTime.Today.AddDays(30);
        if (target < oldest || target > newest) return;

        _handDate = target;
        LoadHandInk(_handDate);
        UpdateHandHeader();
    }

    private void UpdateHandHeader()
    {
        if (TxtHandClass == null) return;

        // TxtClass 要等 Refresh 之后才有字；板书模式在 Loaded 里就展开，那时它还是空的，
        // 于是这里补一层设置兜底，否则蓝条上永远写着「小黑板」。
        string cls = (TxtClass?.Text ?? "").Trim();
        if (cls.Length == 0)
        {
            try { cls = (Store.LoadSettings().SelectedClass ?? "").Trim(); } catch { }
        }
        TxtHandClass.Text = cls.Length > 0 ? cls : "小黑板";

        string[] wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        string day = wk[(int)_handDate.DayOfWeek];
        bool today = _handDate.Date == DateTime.Today;

        TxtHandSub.Text = _handDate.ToString("yyyy年M月d日") + " " + day + (today ? " · 今天" : "");
        // 工具条上只留「今天 / M月d日」：星期几蓝条上已经写着了，写全了会把日期那组撑宽，
        // 手写区最窄时四个工具按钮就没地方放。
        TxtHandDate.Text = today ? "今天" : _handDate.ToString("M月d日");
        TxtHandDate.Foreground = today
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x5F, 0xA8))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
    }

    // ======================================================================
    //  笔迹存取
    // ======================================================================

    private void LoadHandInk(DateTime day)
    {
        _handLoading = true;
        try
        {
            HandInk.Strokes.Clear();
            _handUndo.Clear();

            string f = HandFile(day);
            if (!File.Exists(f)) return;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            HandInk.Strokes.Add(new StrokeCollection(fs));
        }
        catch
        {
            // 文件被占用 / 内容坏了：宁可当这天是白板，也不能让整块板书打不开
        }
        finally
        {
            _handLoading = false;
        }
    }

    /// <summary>落笔 / 擦除后延迟一会儿再写盘：一笔一存会把磁盘打满。</summary>
    private void ScheduleHandSave()
    {
        if (_handLoading || !_handMode) return;
        if (_handSaveTimer == null)
        {
            _handSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _handSaveTimer.Tick += (_, _) =>
            {
                _handSaveTimer!.Stop();
                SaveHandInkNow();
            };
        }
        _handSaveTimer.Stop();
        _handSaveTimer.Start();
    }

    private void SaveHandInkNow()
    {
        if (!_handMode || _handLoading) return;
        _handSaveTimer?.Stop();
        try
        {
            string f = HandFile(_handDate);
            if (HandInk.Strokes.Count == 0)
            {
                // 清空后不必留一个 0 字节的文件，下次打开照样是白板
                if (File.Exists(f)) File.Delete(f);
            }
            else
            {
                Directory.CreateDirectory(HandDir);
                using var fs = new FileStream(f, FileMode.Create, FileAccess.Write);
                HandInk.Strokes.Save(fs);
            }
        }
        catch { /* 写盘失败不影响继续写 */ }

        CleanupHandInk();
    }

    /// <summary>只留最近 <see cref="HandKeepDays"/> 天；更早的直接删掉。</summary>
    private static void CleanupHandInk()
    {
        try
        {
            if (!Directory.Exists(HandDir)) return;
            var oldest = DateTime.Today.AddDays(-(HandKeepDays - 1));

            foreach (var f in Directory.GetFiles(HandDir, "*.isf"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!DateTime.TryParseExact(name, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                    continue;
                if (d.Date < oldest) File.Delete(f);
            }
        }
        catch { /* 某个文件正被占用就下次再删 */ }
    }

    // ======================================================================
    //  笔具
    // ======================================================================

    private void ApplyHandPen()
    {
        var c = DoodleIcons.Parse(_handPenColor);
        HandInk.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = c,
            Width = _handPenWidth,
            Height = _handPenWidth,
            FitToCurve = true,
            StylusTip = StylusTip.Ellipse,
            IgnorePressure = false
        };
    }

    private void BtnHandPen_Click(object sender, RoutedEventArgs e)
    {
        // 已经是笔了 → 再点一次是「我想换颜色 / 粗细」
        bool already = HandInk.EditingMode == InkCanvasEditingMode.Ink;
        HandInk.EditingMode = InkCanvasEditingMode.Ink;
        ApplyHandPen();
        UpdateHandToolVisual();
        if (already) ShowHandPenMenu();
    }

    private void BtnHandEraser_Click(object sender, RoutedEventArgs e)
    {
        HandInk.EditingMode = InkCanvasEditingMode.EraseByStroke;
        UpdateHandToolVisual();
    }

    private void BtnHandUndo_Click(object sender, RoutedEventArgs e)
    {
        while (_handUndo.Count > 0)
        {
            var s = _handUndo[^1];
            _handUndo.RemoveAt(_handUndo.Count - 1);
            if (!HandInk.Strokes.Contains(s)) continue;   // 已经被橡皮擦掉的，跳过
            HandInk.Strokes.Remove(s);
            ScheduleHandSave();
            return;
        }
    }

    private void BtnHandClear_Click(object sender, RoutedEventArgs e)
    {
        if (HandInk.Strokes.Count == 0) return;

        string when = _handDate.Date == DateTime.Today ? "今天" : _handDate.ToString("M月d日");
        var r = MessageBox.Show(this, $"确定清空{when}的板书吗？\n（只清这一天的，其它日期不受影响）",
            "清空板书", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;

        HandInk.Strokes.Clear();
        _handUndo.Clear();
        SaveHandInkNow();
    }

    private void UpdateHandToolVisual()
    {
        bool pen = HandInk.EditingMode == InkCanvasEditingMode.Ink;

        if (BtnHandPen != null) BtnHandPen.Tag = pen ? "on" : null;
        if (BtnHandEraser != null) BtnHandEraser.Tag = pen ? null : "on";

        // 图标跟着选中态换色：笔选中时用当前墨色（一眼看出现在是什么颜色）
        if (_iconPen != null)
            _iconPen.Stroke = pen
                ? new SolidColorBrush(DoodleIcons.Parse(_handPenColor))
                : DoodleIcons.Solid(InkIconHex);
        if (_iconEraser != null)
            _iconEraser.Stroke = pen ? DoodleIcons.Solid(InkIconHex) : DoodleIcons.Solid("#1E5FA3");
    }

    // ======================================================================
    //  工具条装配
    // ======================================================================

    private const string InkIconHex = "#2B3A4A";

    /// <summary>
    /// 板书工具条：放大按钮、（在代码里）画图标、挂窗口几何监听。构造时调一次。
    /// <para>★ 图标全部是 <see cref="DoodleIcons"/> 的 24×24 线稿 Path：换色只改 Stroke，
    /// 不用出图，也绝不会像字体图标那样在一体机上显示成方框。</para>
    /// </summary>
    internal void InitHandChrome()
    {
        MakeToolButton(BtnHandPen, "笔", DoodleIcons.Pen(DoodleIcons.Solid(InkIconHex)), InkIconHex, out _iconPen);
        MakeToolButton(BtnHandEraser, "橡皮", DoodleIcons.Eraser(DoodleIcons.Solid(InkIconHex)), InkIconHex, out _iconEraser);
        MakeToolButton(BtnHandUndo, "撤销", DoodleIcons.Undo(DoodleIcons.Solid(InkIconHex)), InkIconHex, out _iconUndo);
        MakeToolButton(BtnHandClear, "清空", DoodleIcons.Trash(DoodleIcons.Solid("#A32D2D")), "#A32D2D", out _iconClear);

        if (BtnHandPrev != null) BtnHandPrev.Content = DoodleIcons.Prev(DoodleIcons.Solid("#5B6B7C"));
        if (BtnHandNext != null) BtnHandNext.Content = DoodleIcons.Next(DoodleIcons.Solid("#5B6B7C"));

        // 左沿 / 右沿被拖：手写区宽度与课表列宽各归各的（见 OnHandWindowGeometryChanged）
        SizeChanged += (_, _) => OnHandWindowGeometryChanged();
        LocationChanged += (_, _) => OnHandWindowGeometryChanged();
    }

    /// <summary>
    /// 把一个工具按钮变成「图标 + 小字」的方形大按钮（52×52）。
    /// <para>★ 竖排而不是横排：横排的「图标 + 两字」在 420 宽的手写区里会把第四个按钮挤出去，
    /// 竖排四个按钮一共 226 宽，最窄的手写区也放得下。</para>
    /// </summary>
    private static void MakeToolButton(Button b, string text, PathShape icon, string hex, out PathShape? keep)
    {
        if (b == null) { keep = null; return; }

        icon.Width = 23;
        icon.Height = 23;

        var col = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        col.Children.Add(icon);
        col.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Foreground = DoodleIcons.Solid(hex)
        });

        b.Content = col;
        b.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        b.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        keep = icon;
    }

    /// <summary>笔的小面板：4 色 × 3 档。做成菜单而不是常驻一排圆点，手写区拖窄时才不会挤。</summary>
    private void ShowHandPenMenu()
    {
        var menu = new ContextMenu { PlacementTarget = BtnHandPen, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        (string Hex, string Name)[] colors =
        {
            ("#1F2A37", "黑"), ("#C0392B", "红"), ("#1E5FA3", "蓝"), ("#2E7D32", "绿")
        };

        foreach (var (hex, name) in colors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(DoodleIcons.Parse(hex)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = name + (string.Equals(hex, _handPenColor, StringComparison.OrdinalIgnoreCase) ? " ✓" : ""),
                VerticalAlignment = VerticalAlignment.Center
            });

            var mi = new MenuItem { Header = row };
            string picked = hex;
            mi.Click += (_, _) =>
            {
                _handPenColor = picked;
                HandInk.EditingMode = InkCanvasEditingMode.Ink;
                ApplyHandPen();
                UpdateHandToolVisual();
                ScheduleHandSave();
            };
            menu.Items.Add(mi);
        }

        menu.Items.Add(new Separator());
        foreach (var (w, label) in new[] { (1.8, "细"), (2.6, "中"), (4.2, "粗") })
        {
            double width = w;
            var mi = new MenuItem
            {
                Header = label + (Math.Abs(_handPenWidth - w) < 0.01 ? " ✓" : ""),
                IsChecked = Math.Abs(_handPenWidth - w) < 0.01
            };
            mi.Click += (_, _) =>
            {
                _handPenWidth = width;
                HandInk.EditingMode = InkCanvasEditingMode.Ink;
                ApplyHandPen();
                UpdateHandToolVisual();
            };
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
    }

    private void HandInk_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (!_handLoading) _handUndo.Add(e.Stroke);
        ScheduleHandSave();
    }

    private void HandInk_StrokeErased(object sender, RoutedEventArgs e) => ScheduleHandSave();

    // ======================================================================
    //  手势守卫：手写区里的一切都不要被窗口级「拖动 / 换课」抢走
    // ======================================================================

    /// <summary>
    /// 事件源是否落在手写区里。
    /// <para>★ 必须挡在 <c>Window_PreviewMouseLeftButtonDown</c> 最前面：那个处理器会把
    /// 按下的位置当成「准备拖这一节课」，写字时手指一划就变成拖课表。</para>
    /// </summary>
    private bool IsWithinHandArea(DependencyObject? src)
    {
        if (!_handMode) return false;
        int guard = 0;
        while (src != null && guard++ < 64)
        {
            if (ReferenceEquals(src, HandPanel) || ReferenceEquals(src, HandSplitter)) return true;
            src = src is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(src)
                : LogicalTreeHelper.GetParent(src);
        }
        return false;
    }

    // ======================================================================
    //  诊断
    // ======================================================================

    /// <summary>诊断用：板书模式是否真的展开了（列宽 &gt; 0 才算）。</summary>
    internal bool DiagHandModeActive => _handMode && HandPanel.Visibility == Visibility.Visible && HandCol.Width.Value > 100;

    /// <summary>诊断用：手写区当前宽度。</summary>
    internal double DiagHandWidth => HandCol.Width.Value;

    /// <summary>诊断用：当前板书页有多少笔（灌完样例后应该 &gt; 0）。</summary>
    internal int DiagHandStrokeCount => HandInk.Strokes.Count;

    /// <summary>诊断用：保留天数常量（断言 7 天不被随手改掉）。</summary>
    internal int DiagHandKeepDays => HandKeepDays;

    /// <summary>诊断用：把板书模式打开 / 收起，不走窗口加宽（诊断窗口不需要真的挪位置）。</summary>
    internal void DiagSetHandMode(bool on) => SetHandMode(on, expandWindow: false);

    /// <summary>诊断用：走一遍「板书」按钮的完整逻辑（收黑板 + 常驻 + 取消置顶）。</summary>
    internal void DiagToggleHand() => ToggleHandMode();

    /// <summary>诊断用：板书模式是不是「常驻 + 不置顶」。</summary>
    internal bool DiagResidentNoTop =>
        !Topmost && (_layout == null || !_layout.AutoHide) && !_clickThrough;

    /// <summary>诊断用：课表列当前宽度（窗口宽 − 手写区宽）。未 Show 的窗口 Width 是 NaN，回落到缓存值。</summary>
    internal double DiagCourseWidth =>
        double.IsNaN(Width) || Width < 50
            ? _handCourseWidth
            : Math.Max(0, Width - HandCol.Width.Value);

    /// <summary>
    /// 诊断用：模拟「拖两区中间那条缝」，把课表宽拖到 <paramref name="course"/>。
    /// <para>断言点不是「宽度变了」，而是三条同时成立：① 手写区宽度一丝不变；
    /// ② 课表宽度跟着变；③ 窗口右边沿钉死不动。r106 的旧实现恰好三条全错。</para>
    /// </summary>
    internal void DiagDragCourseTo(double course)
    {
        double w = double.IsNaN(Width) || Width < 50
            ? HandCol.Width.Value + _handCourseWidth
            : Width;
        _gripSide = 2;
        _gripStartLeft = double.IsNaN(Left) ? 0 : Left;
        _gripStartWidth = w;
        _gripStartHand = HandCol.Width.Value > 1 ? HandCol.Width.Value : _handWidth;
        _gripStartCourse = Math.Max(HandCourseMinWidth, w - _gripStartHand);
        ApplyCourseWidthLive(Math.Clamp(course, HandCourseMinWidth, 4000));
        _gripSide = 0;
    }

    /// <summary>
    /// 诊断用：模拟「拖窗口左沿」把窗口宽度改成 <paramref name="winW"/>。
    /// <para>断言点：手写区宽度跟着变、课表宽度一丝不变（旧实现正好反了 —— 变的是课表）。</para>
    /// <para>已 Show 的窗口按真机那条路走：先改窗口宽度（左沿跟手），让几何回调去补偿；
    /// 未 Show 的窗口（ui-check）没有 HWND，直接调补偿那一步。</para>
    /// </summary>
    internal void DiagLeftResize(double winW)
    {
        if (ActualWidth > 40 && Math.Abs(winW - ActualWidth) > 0.5)
        {
            Left -= winW - ActualWidth;     // 右边界不动，左沿往左让
            Width = winW;
        }
        ApplyLeftEdgeResize(winW);
    }

    /// <summary>诊断用：工具条按钮的高度（教室大屏要求 ≥ 44，手指才点得准）。</summary>
    internal double DiagHandToolSize
    {
        get
        {
            double m = double.MaxValue;
            foreach (var b in new[] { BtnHandPen, BtnHandEraser, BtnHandUndo, BtnHandClear })
            {
                if (b == null || b.Height <= 0) continue;
                m = Math.Min(m, b.Height);
            }
            return m > 10000 ? 0 : Math.Round(m, 1);
        }
    }

    /// <summary>诊断用：四个工具按钮是不是都长出了图标（图标 + 文字，不再只有文字）。</summary>
    internal int DiagHandToolIconCount
    {
        get
        {
            int n = 0;
            foreach (var b in new[] { BtnHandPen, BtnHandEraser, BtnHandUndo, BtnHandClear })
            {
                if (b?.Content is System.Windows.Controls.StackPanel sp
                    && sp.Children.OfType<PathShape>().Any()) n++;
            }
            return n;
        }
    }

    /// <summary>诊断用：截图前的准备 —— 给一个有代表性的尺寸（手写区 420 + 课表 520），展开手写区并潦草写两笔。</summary>
    internal void DiagOpenHandForShot()
    {
        // 截图用的窗口没有恢复布局（宽度是默认值），先按「手写区 + 课表」摆一个真机尺寸，
        // 否则手写区会把课表列挤成一条缝，截出来的图看不出布局。
        _handMode = false;                      // 让 SetHandMode 真的重算一次 course / MinWidth
        _handWidth = HandDefaultWidth;
        Width = HandDefaultWidth + HandCourseFallbackWidth;
        SetHandMode(true, expandWindow: false);
        LoadHandInk(_handDate);
        DiagDrawHandSample();
        UpdateLayout();
    }

    /// <summary>诊断用：往手写区灌两笔，验证能写、能存、能撤。</summary>
    internal int DiagDrawHandSample()
    {
        var da = HandInk.DefaultDrawingAttributes;
        for (int i = 0; i < 2; i++)
        {
            var pts = new StylusPointCollection
            {
                new StylusPoint(20 + i * 40, 30),
                new StylusPoint(60 + i * 40, 50),
                new StylusPoint(90 + i * 40, 35)
            };
            HandInk.Strokes.Add(new Stroke(pts, da));
        }
        _handUndo.Clear();
        return HandInk.Strokes.Count;
    }
}
