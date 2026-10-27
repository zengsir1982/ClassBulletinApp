using System;
using System.Windows;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class LayoutWindow : Window
{
    private readonly AppSettings _s;

    /// <summary>打开时默认选中的标签页下标（0=课表悬浮窗，1=通知悬浮窗，2=桌面背景）。</summary>
    public int InitialTabIndex { get; set; } = 0;

    private bool _hwReady;
    private DispatcherTimer? _wpSaveTimer;
    private DispatcherTimer? _hwApplyTimer;

    private static readonly string[] HwAnchors =
    {
        "TopLeft", "TopCenter", "TopRight",
        "MiddleLeft", "MiddleCenter", "MiddleRight",
        "BottomLeft", "BottomCenter", "BottomRight"
    };

    public LayoutWindow()
    {
        InitializeComponent();
        _s = Store.LoadSettings();
        LoadValues();
        WireLabels();
        ReloadBackdropUi();
        _hwReady = true;
        AppEvents.BackdropChanged += OnBackdropChanged;
        MainTabs.SelectedIndex = Math.Clamp(InitialTabIndex, 0, MainTabs.Items.Count - 1);
    }

    private void OnBackdropChanged() => ReloadBackdropUi();

    private void LoadValues()
    {
        var sl = _s.ScheduleLayout;
        CbSchAnchor.SelectedIndex = sl.Anchor switch
        {
            "Left" => 0, "Right" => 1, "Top" => 2, "Bottom" => 3, _ => 4
        };
        SlSchWidth.Value = sl.Width;
        SlSchHeight.Value = sl.Height;
        SlSchOpacity.Value = sl.Opacity;
        SlSchOverlay.Value = sl.OverlayOpacity;
        ChkSchTop.IsChecked = sl.TopMost;
        ChkSchClick.IsChecked = sl.ClickThrough;
        ChkSchHide.IsChecked = sl.AutoHide;
        ChkSchClassHide.IsChecked = sl.AutoHideDuringClass;
        SlSchPeek.Value = Math.Clamp(sl.EdgePeek, SlSchPeek.Minimum, SlSchPeek.Maximum);
        SlSchEdge.Value = Math.Clamp(sl.EdgeTrigger, SlSchEdge.Minimum, SlSchEdge.Maximum);

        var nl = _s.NotificationLayout;
        CbNotAnchor.SelectedIndex = nl.Anchor == "Bottom" ? 1 : 0;
        SlNotHeight.Value = nl.Height;
        SlNotWidth.Value = nl.Width;
        CbNotHAnchor.SelectedIndex = nl.HAnchor switch { "Left" => 0, "Right" => 2, _ => 1 };
        SlNotFont.Value = nl.FontSize;
        SlNotSpeed.Value = nl.MarqueeSpeed;
        SlNotOpacity.Value = nl.Opacity;
        SlNotOverlay.Value = nl.OverlayOpacity;
        ChkNotTop.IsChecked = nl.TopMost;
        ChkNotClick.IsChecked = nl.ClickThrough;
        ChkNotHide.IsChecked = nl.AutoHide;
        ChkNotClassHide.IsChecked = nl.AutoHideDuringClass;
        SlNotPeek.Value = Math.Clamp(nl.EdgePeek, SlNotPeek.Minimum, SlNotPeek.Maximum);
        SlNotEdge.Value = Math.Clamp(nl.EdgeTrigger, SlNotEdge.Minimum, SlNotEdge.Maximum);
    }

    private void WireLabels()
    {
        SlSchWidth.ValueChanged += (_, _) => TxtSchWidth.Text = $"{SlSchWidth.Value:0} 像素";
        SlSchHeight.ValueChanged += (_, _) => TxtSchHeight.Text = $"{SlSchHeight.Value:0} 像素";
        SlSchOpacity.ValueChanged += (_, _) => TxtSchOpacity.Text = $"{SlSchOpacity.Value:0%}";
        SlSchOverlay.ValueChanged += (_, _) => TxtSchOverlay.Text = $"{SlSchOverlay.Value:0%}";
        SlNotHeight.ValueChanged += (_, _) => TxtNotHeight.Text = $"{SlNotHeight.Value:0} 像素";
        SlNotWidth.ValueChanged += (_, _) => TxtNotWidth.Text = SlNotWidth.Value <= 0
            ? "0 = 整屏宽（横跨整个屏幕）"
            : $"{SlNotWidth.Value:0} 像素";
        SlNotFont.ValueChanged += (_, _) => TxtNotFont.Text = $"{SlNotFont.Value:0} 号";
        SlNotSpeed.ValueChanged += (_, _) => TxtNotSpeed.Text = $"{SlNotSpeed.Value:0} 像素/秒";
        SlNotOpacity.ValueChanged += (_, _) => TxtNotOpacity.Text = $"{SlNotOpacity.Value:0%}";
        SlNotOverlay.ValueChanged += (_, _) => TxtNotOverlay.Text = $"{SlNotOverlay.Value:0%}";
        SlSchPeek.ValueChanged += (_, _) => TxtSchPeek.Text = SlSchPeek.Value <= 0
            ? "0 像素 · 完全移出屏幕（推荐）"
            : $"{SlSchPeek.Value:0} 像素（保留一条可点的细边）";
        SlSchEdge.ValueChanged += (_, _) => TxtSchEdge.Text = $"{SlSchEdge.Value:0} 像素";
        SlNotPeek.ValueChanged += (_, _) => TxtNotPeek.Text = SlNotPeek.Value <= 0
            ? "0 像素 · 完全移出屏幕（推荐）"
            : $"{SlNotPeek.Value:0} 像素（保留一条可点的细边）";
        SlNotEdge.ValueChanged += (_, _) => TxtNotEdge.Text = $"{SlNotEdge.Value:0} 像素";

        WireLivePreview();
    }

    /// <summary>拖动课表 / 通知的尺寸、透明度、锚点等时，实时让已打开的悬浮窗同步（所见即所得）。</summary>
    private void WireLivePreview()
    {
        void Preview(object? _, EventArgs __) => AppEvents.RaiseLayoutChanged();

        SlSchWidth.ValueChanged += Preview;
        SlSchHeight.ValueChanged += Preview;
        SlSchOpacity.ValueChanged += Preview;
        SlSchOverlay.ValueChanged += Preview;
        SlSchPeek.ValueChanged += Preview;
        SlSchEdge.ValueChanged += Preview;
        CbSchAnchor.SelectionChanged += Preview;

        SlNotHeight.ValueChanged += Preview;
        SlNotWidth.ValueChanged += Preview;
        SlNotFont.ValueChanged += Preview;
        SlNotSpeed.ValueChanged += Preview;
        SlNotOpacity.ValueChanged += Preview;
        SlNotOverlay.ValueChanged += Preview;
        SlNotPeek.ValueChanged += Preview;
        SlNotEdge.ValueChanged += Preview;
        CbNotAnchor.SelectionChanged += Preview;
        CbNotHAnchor.SelectionChanged += Preview;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var sl = _s.ScheduleLayout;
        sl.Anchor = CbSchAnchor.SelectedIndex switch { 0 => "Left", 1 => "Right", 2 => "Top", 3 => "Bottom", _ => "Center" };
        sl.Width = SlSchWidth.Value;
        sl.Height = SlSchHeight.Value;
        sl.Opacity = SlSchOpacity.Value;
        sl.OverlayOpacity = SlSchOverlay.Value;
        sl.TopMost = ChkSchTop.IsChecked == true;
        sl.ClickThrough = ChkSchClick.IsChecked == true;
        // 鼠标穿透与靠边隐藏互斥：穿透开启时强制关闭两类靠边隐藏
        sl.AutoHide = ChkSchHide.IsChecked == true && !sl.ClickThrough;
        sl.AutoHideDuringClass = ChkSchClassHide.IsChecked == true && !sl.ClickThrough;
        sl.EdgePeek = SlSchPeek.Value;
        sl.EdgeTrigger = SlSchEdge.Value;

        var nl = _s.NotificationLayout;
        nl.Anchor = CbNotAnchor.SelectedIndex == 1 ? "Bottom" : "Top";
        nl.Height = SlNotHeight.Value;
        nl.Width = SlNotWidth.Value;
        nl.HAnchor = CbNotHAnchor.SelectedIndex switch { 0 => "Left", 2 => "Right", _ => "Center" };
        nl.FontSize = SlNotFont.Value;
        nl.MarqueeSpeed = SlNotSpeed.Value;
        nl.Opacity = SlNotOpacity.Value;
        nl.OverlayOpacity = SlNotOverlay.Value;
        nl.TopMost = ChkNotTop.IsChecked == true;
        nl.ClickThrough = ChkNotClick.IsChecked == true;
        nl.AutoHide = ChkNotHide.IsChecked == true && !nl.ClickThrough;
        nl.AutoHideDuringClass = ChkNotClassHide.IsChecked == true && !nl.ClickThrough;
        nl.EdgePeek = SlNotPeek.Value;
        nl.EdgeTrigger = SlNotEdge.Value;

        Store.SaveSettings(_s);
        AppEvents.RaiseLayoutChanged();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ---------- 桌面背景（当天课表 + 作业公示 合成壁纸） ----------
    private void ReloadBackdropUi()
    {
        var b = _s.Backdrop ?? new BackdropSettings();
        bool ready = _hwReady;
        _hwReady = false;
        try
        {
            ChkWpSchedule.IsChecked = b.ShowSchedule;
            ChkWpHomework.IsChecked = b.ShowHomework;
            ChkWpOrigin.IsChecked = b.UseOriginWallpaper;

            var sl = b.ScheduleLayout ?? new HomeworkWallpaperLayout();
            FillAnchorCombo(CbWpSchAnchor, sl.Anchor, 3);
            SlWpSchWidth.Value = Math.Clamp(sl.WidthRatio <= 0 ? 0.38 : sl.WidthRatio, SlWpSchWidth.Minimum, SlWpSchWidth.Maximum);
            SlWpSchX.Value = Math.Clamp(sl.OffsetX, SlWpSchX.Minimum, SlWpSchX.Maximum);
            SlWpSchY.Value = Math.Clamp(sl.OffsetY, SlWpSchY.Minimum, SlWpSchY.Maximum);

            ApplyHwLayoutToUi(b.HomeworkLayout ?? Store.LoadHomework()?.Layout);
        }
        finally { _hwReady = ready; }
        UpdateWpLabels();
    }

    private static void FillAnchorCombo(System.Windows.Controls.ComboBox cb, string? anchor, int fallback)
    {
        int idx = Array.IndexOf(HwAnchors, (anchor ?? "").Trim());
        if (idx < 0)
        {
            idx = (anchor ?? "").Trim() switch
            {
                "Left" => 3, "Right" => 5, "Top" => 1, "Bottom" => 7, "Center" => 4, _ => fallback
            };
        }
        cb.SelectedIndex = Math.Clamp(idx, 0, HwAnchors.Length - 1);
    }

    private static string ComboAnchor(System.Windows.Controls.ComboBox cb, string fallback)
        => cb.SelectedIndex >= 0 && cb.SelectedIndex < HwAnchors.Length ? HwAnchors[cb.SelectedIndex] : fallback;

    private void ApplyHwLayoutToUi(HomeworkWallpaperLayout? lay)
    {
        bool ready = _hwReady;
        _hwReady = false;
        try
        {
            FillAnchorCombo(CbHwAnchor, lay?.Anchor, 5);
            SlHwX.Value = Math.Clamp(lay?.OffsetX ?? 0, SlHwX.Minimum, SlHwX.Maximum);
            SlHwY.Value = Math.Clamp(lay?.OffsetY ?? 0, SlHwY.Minimum, SlHwY.Maximum);
            double ratio = (lay?.WidthRatio ?? 0) <= 0 ? 0.4 : lay!.WidthRatio;
            SlHwWidth.Value = Math.Clamp(ratio, SlHwWidth.Minimum, SlHwWidth.Maximum);
        }
        finally { _hwReady = ready; }
        UpdateHwLabels();
    }

    private HomeworkWallpaperLayout CurrentHwLayout() => new()
    {
        Anchor = ComboAnchor(CbHwAnchor, "MiddleRight"),
        OffsetX = SlHwX.Value,
        OffsetY = SlHwY.Value,
        WidthRatio = SlHwWidth.Value
    };

    private HomeworkWallpaperLayout CurrentWpScheduleLayout() => new()
    {
        Anchor = ComboAnchor(CbWpSchAnchor, "MiddleLeft"),
        OffsetX = SlWpSchX.Value,
        OffsetY = SlWpSchY.Value,
        WidthRatio = SlWpSchWidth.Value
    };

    private void HwLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hwReady) return;
        UpdateHwLabels();
        ScheduleWpSave();
        ScheduleWpRepaint();
    }

    private void WpLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hwReady) return;
        UpdateWpLabels();
        ScheduleWpSave();
        ScheduleWpRepaint();
    }

    private void WpSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hwReady) return;
        SaveWpSettings();
        ScheduleWpRepaint();
        TxtWpStatus.Text = "桌面背景内容已更新" + (BackdropService.IsApplied() ? "，正在重绘壁纸…" : "，点「应用到桌面背景」即可生效。");
    }

    private void UpdateHwLabels()
    {
        if (SlHwX == null || SlHwY == null || SlHwWidth == null) return;
        if (TxtHwX != null) TxtHwX.Text = $"{SlHwX.Value:0} 像素";
        if (TxtHwY != null) TxtHwY.Text = $"{SlHwY.Value:0} 像素";
        if (TxtHwWidth != null) TxtHwWidth.Text = $"{SlHwWidth.Value:0%}";
    }

    private void UpdateWpLabels()
    {
        if (SlWpSchX == null || SlWpSchY == null || SlWpSchWidth == null) return;
        if (TxtWpSchX != null) TxtWpSchX.Text = $"{SlWpSchX.Value:0} 像素";
        if (TxtWpSchY != null) TxtWpSchY.Text = $"{SlWpSchY.Value:0} 像素";
        if (TxtWpSchWidth != null) TxtWpSchWidth.Text = $"{SlWpSchWidth.Value:0%}";
    }

    private void ScheduleWpSave()
    {
        _wpSaveTimer?.Stop();
        _wpSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _wpSaveTimer.Tick += (_, _) =>
        {
            _wpSaveTimer?.Stop();
            SaveWpSettings();
        };
        _wpSaveTimer.Start();
    }

    private void SaveWpSettings()
    {
        try
        {
            var s = Store.LoadSettings();
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowSchedule = ChkWpSchedule.IsChecked == true;
            s.Backdrop.ShowHomework = ChkWpHomework.IsChecked == true;
            s.Backdrop.UseOriginWallpaper = ChkWpOrigin.IsChecked == true;
            s.Backdrop.ScheduleLayout = CurrentWpScheduleLayout();
            s.Backdrop.HomeworkLayout = CurrentHwLayout();
            Store.SaveSettings(s);

            // 作业内容文件里也写一份布局，保证老入口 / 其他窗口读到的位置一致
            var board = Store.LoadHomework();
            if (board != null)
            {
                board.Layout = s.Backdrop.HomeworkLayout;
                Store.SaveHomework(board);
            }
        }
        catch { }
    }

    private void ScheduleWpRepaint()
    {
        if (!BackdropService.IsApplied()) return;

        _hwApplyTimer?.Stop();
        _hwApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hwApplyTimer.Tick += (_, _) =>
        {
            _hwApplyTimer?.Stop();
            BackdropService.Apply(silent: true);
        };
        _hwApplyTimer.Start();
    }

    private void BtnApplyWp_Click(object sender, RoutedEventArgs e)
    {
        SaveWpSettings();
        if (ChkWpSchedule.IsChecked != true && ChkWpHomework.IsChecked != true)
        {
            TxtWpStatus.Text = "请至少勾选「当天课表」或「作业公示」其中一项。";
            return;
        }
        BackdropService.Apply(silent: false, out string msg);
        TxtWpStatus.Text = msg;
        AppEvents.RaiseBackdropChanged();
    }

    private void BtnClearWp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowSchedule = false;
            s.Backdrop.ShowHomework = false;
            Store.SaveSettings(s);

            BackdropService.Clear();
            ReloadBackdropUi();
            AppEvents.RaiseBackdropChanged();
            TxtWpStatus.Text = "已清除桌面背景，恢复系统原壁纸。";
        }
        catch (Exception ex)
        {
            TxtWpStatus.Text = "清除桌面背景失败：" + ex.Message;
        }
    }
}
