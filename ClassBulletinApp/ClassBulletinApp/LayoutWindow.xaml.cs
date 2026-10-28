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

    /// <summary>把界面控件当前值同步进内存设置对象 _s（不落盘）。实时预览与「保存并应用」共用，
    /// 保证 RaisedLayoutChanged 传出去的布局对象携带滑块最新值，悬浮窗才能即时变化。</summary>
    private void SyncControlsToSettings()
    {
        var sl = _s.ScheduleLayout;
        sl.Anchor = CbSchAnchor.SelectedIndex switch { 0 => "Left", 1 => "Right", 2 => "Top", 3 => "Bottom", _ => "Center" };
        sl.Width = SlSchWidth.Value;
        sl.FullWidth = false;   // 宽度已完全由滑块（Width）决定
        sl.Height = SlSchHeight.Value;
        sl.Opacity = SlSchOpacity.Value;
        sl.OverlayOpacity = SlSchOverlay.Value;
        sl.TopMost = ChkSchTop.IsChecked == true;
        sl.ClickThrough = ChkSchClick.IsChecked == true;
        sl.AutoHide = ChkSchHide.IsChecked == true && !sl.ClickThrough;
        sl.AutoHideDuringClass = ChkSchClassHide.IsChecked == true && !sl.ClickThrough;
        sl.EdgePeek = SlSchPeek.Value;
        sl.EdgeTrigger = SlSchEdge.Value;

        var nl = _s.NotificationLayout;
        nl.Anchor = CbNotAnchor.SelectedIndex == 1 ? "Bottom" : "Top";
        nl.Height = SlNotHeight.Value;
        nl.Width = SlNotWidth.Value;
        nl.FullWidth = false;   // 宽度已完全由滑块（Width）决定，清除历史遗留的整屏宽强制开关
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
    }

    /// <summary>拖动课表 / 通知的尺寸、透明度、锚点等时，实时让已打开的悬浮窗同步（所见即所得）。
    /// 先同步控件值到 _s，再把内存中正在编辑的布局对象传过去，避免悬浮窗每次都重新读盘拿不到最新滑块值。</summary>
    private void WireLivePreview()
    {
        void Preview(object? _, EventArgs __)
        {
            SyncControlsToSettings();
            AppEvents.RaiseLayoutChanged(_s.ScheduleLayout, _s.NotificationLayout);
        }

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
        SyncControlsToSettings();
        Store.SaveSettings(_s);
        AppEvents.RaiseLayoutChanged();
        // 注意：本窗口既可能以 ShowDialog 打开（可设 DialogResult），也可能由便签「位置」按钮以 Show 打开，
        // 后者设 DialogResult 会抛异常，因此这里容错处理。
        try { DialogResult = true; } catch (InvalidOperationException) { }
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch (InvalidOperationException) { }
        Close();
    }

    // ---------- 桌面背景（当天课表 + 作业公示 + 缺交名单 合成壁纸） ----------
    private void ReloadBackdropUi()
    {
        var b = _s.Backdrop ?? new BackdropSettings();
        bool ready = _hwReady;
        _hwReady = false;
        try
        {
            ChkWpSchedule.IsChecked = b.ShowSchedule;
            ChkWpHomework.IsChecked = b.ShowHomework;
            ChkWpMissing.IsChecked = b.ShowMissing;
            ChkWpOrigin.IsChecked = b.UseOriginWallpaper;

            FillThemeCombo(CbWpSchTheme, b.ScheduleTheme);
            FillThemeCombo(CbWpHwTheme, Store.LoadHomework()?.Theme);
            FillThemeCombo(CbWpMsTheme, Store.LoadMissingBoard().Theme);

            var sl = b.ScheduleLayout ?? new HomeworkWallpaperLayout();
            FillAnchorCombo(CbWpSchAnchor, sl.Anchor, 3);
            SlWpSchWidth.Value = Math.Clamp(sl.WidthRatio <= 0 ? 0.38 : sl.WidthRatio, SlWpSchWidth.Minimum, SlWpSchWidth.Maximum);
            SlWpSchX.Value = Math.Clamp(sl.OffsetX, SlWpSchX.Minimum, SlWpSchX.Maximum);
            SlWpSchY.Value = Math.Clamp(sl.OffsetY, SlWpSchY.Minimum, SlWpSchY.Maximum);
            ChkSchFixed.IsChecked = sl.UseFixedSize;
            SlWpSchFixedW.Value = Math.Clamp(sl.FixedWidth <= 0 ? 460 : sl.FixedWidth, SlWpSchFixedW.Minimum, SlWpSchFixedW.Maximum);
            SlWpSchFixedH.Value = Math.Clamp(sl.FixedHeight, SlWpSchFixedH.Minimum, SlWpSchFixedH.Maximum);

            ApplyHwLayoutToUi(b.HomeworkLayout ?? Store.LoadHomework()?.Layout);
            ApplyMsLayoutToUi(b.MissingLayout);
        }
        finally { _hwReady = ready; }
        UpdateWpLabels();
        UpdateMsLabels();
    }

    /// <summary>填充便签主题下拉（带色块预览），并选中当前主题。</summary>
    private static void FillThemeCombo(System.Windows.Controls.ComboBox cb, string? key)
    {
        if (cb.ItemsSource == null)
        {
            cb.ItemsSource = HomeworkThemes.Presets
                .Select(t => new
                {
                    t.Name,
                    Background = (System.Windows.Media.Brush)ThemeHelper.Brush(t.Background, System.Windows.Media.Colors.White)
                })
                .ToList();
        }
        cb.SelectedIndex = HomeworkThemes.IndexOf(key);
    }

    /// <summary>取主题下拉当前选中的主题标识。</summary>
    private static string SelectedThemeKey(System.Windows.Controls.ComboBox cb, string fallback)
    {
        int i = cb.SelectedIndex;
        return i >= 0 && i < HomeworkThemes.Presets.Count ? HomeworkThemes.Presets[i].Key : fallback;
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
            ChkHwFixed.IsChecked = lay?.UseFixedSize == true;
            SlHwFixedW.Value = Math.Clamp(lay?.FixedWidth ?? 420, SlHwFixedW.Minimum, SlHwFixedW.Maximum);
            SlHwFixedH.Value = Math.Clamp(lay?.FixedHeight ?? 0, SlHwFixedH.Minimum, SlHwFixedH.Maximum);
        }
        finally { _hwReady = ready; }
        UpdateHwLabels();
    }

    private HomeworkWallpaperLayout CurrentHwLayout() => new()
    {
        Anchor = ComboAnchor(CbHwAnchor, "MiddleRight"),
        OffsetX = SlHwX.Value,
        OffsetY = SlHwY.Value,
        WidthRatio = SlHwWidth.Value,
        UseFixedSize = ChkHwFixed.IsChecked == true,
        FixedWidth = SlHwFixedW.Value,
        FixedHeight = SlHwFixedH.Value
    };

    private HomeworkWallpaperLayout CurrentWpScheduleLayout() => new()
    {
        Anchor = ComboAnchor(CbWpSchAnchor, "MiddleLeft"),
        OffsetX = SlWpSchX.Value,
        OffsetY = SlWpSchY.Value,
        WidthRatio = SlWpSchWidth.Value,
        UseFixedSize = ChkSchFixed.IsChecked == true,
        FixedWidth = SlWpSchFixedW.Value,
        FixedHeight = SlWpSchFixedH.Value
    };

    private void ApplyMsLayoutToUi(HomeworkWallpaperLayout? lay)
    {
        bool ready = _hwReady;
        _hwReady = false;
        try
        {
            FillAnchorCombo(CbMsAnchor, lay?.Anchor, 8);
            SlMsX.Value = Math.Clamp(lay?.OffsetX ?? 0, SlMsX.Minimum, SlMsX.Maximum);
            SlMsY.Value = Math.Clamp(lay?.OffsetY ?? 0, SlMsY.Minimum, SlMsY.Maximum);
            double ratio = (lay?.WidthRatio ?? 0) <= 0 ? 0.4 : lay!.WidthRatio;
            SlMsWidth.Value = Math.Clamp(ratio, SlMsWidth.Minimum, SlMsWidth.Maximum);
            ChkMsFixed.IsChecked = lay?.UseFixedSize == true;
            SlMsFixedW.Value = Math.Clamp(lay?.FixedWidth ?? 420, SlMsFixedW.Minimum, SlMsFixedW.Maximum);
            SlMsFixedH.Value = Math.Clamp(lay?.FixedHeight ?? 360, SlMsFixedH.Minimum, SlMsFixedH.Maximum);
        }
        finally { _hwReady = ready; }
        UpdateMsLabels();
    }

    private HomeworkWallpaperLayout CurrentMsLayout() => new()
    {
        Anchor = ComboAnchor(CbMsAnchor, "BottomRight"),
        OffsetX = SlMsX.Value,
        OffsetY = SlMsY.Value,
        WidthRatio = SlMsWidth.Value,
        UseFixedSize = ChkMsFixed.IsChecked == true,
        FixedWidth = SlMsFixedW.Value,
        FixedHeight = SlMsFixedH.Value
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

    private void MsLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hwReady) return;
        UpdateMsLabels();
        ScheduleWpSave();
        ScheduleWpRepaint();
    }

    /// <summary>作业 / 缺交便签的主题下拉：主题存在各自内容文件里，切换后立即落盘并通知其他窗口刷新。</summary>
    private void WpHwTheme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_hwReady) return;
        try
        {
            if (ReferenceEquals(sender, CbWpHwTheme))
            {
                var board = Store.LoadHomework() ?? new HomeworkBoard();
                board.Theme = SelectedThemeKey(CbWpHwTheme, "classic");
                Store.SaveHomework(board);
                AppEvents.RaiseHomeworkChanged();
            }
            else if (ReferenceEquals(sender, CbWpMsTheme))
            {
                var mb = Store.LoadMissingBoard();
                mb.Theme = SelectedThemeKey(CbWpMsTheme, "pink");
                Store.SaveMissingBoard(mb);
                AppEvents.RaiseMissingChanged();
            }
        }
        catch { }

        ScheduleWpSave();
        ScheduleWpRepaint();
        AppEvents.RaiseBackdropChanged();
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
        if (TxtHwFixedW != null) TxtHwFixedW.Text = $"{SlHwFixedW.Value:0} 像素";
        if (TxtHwFixedH != null)
            TxtHwFixedH.Text = SlHwFixedH.Value <= 0 ? "随内容自适应" : $"{SlHwFixedH.Value:0} 像素";
    }

    private void UpdateWpLabels()
    {
        if (SlWpSchX == null || SlWpSchY == null || SlWpSchWidth == null) return;
        if (TxtWpSchX != null) TxtWpSchX.Text = $"{SlWpSchX.Value:0} 像素";
        if (TxtWpSchY != null) TxtWpSchY.Text = $"{SlWpSchY.Value:0} 像素";
        if (TxtWpSchWidth != null) TxtWpSchWidth.Text = $"{SlWpSchWidth.Value:0%}";
        if (TxtWpSchFixedW != null) TxtWpSchFixedW.Text = $"{SlWpSchFixedW.Value:0} 像素";
        if (TxtWpSchFixedH != null)
            TxtWpSchFixedH.Text = SlWpSchFixedH.Value <= 0 ? "随内容自适应" : $"{SlWpSchFixedH.Value:0} 像素";
    }

    private void UpdateMsLabels()
    {
        if (SlMsX == null || SlMsY == null || SlMsWidth == null) return;
        if (TxtMsX != null) TxtMsX.Text = $"{SlMsX.Value:0} 像素";
        if (TxtMsY != null) TxtMsY.Text = $"{SlMsY.Value:0} 像素";
        if (TxtMsWidth != null) TxtMsWidth.Text = $"{SlMsWidth.Value:0%}";
        if (TxtMsFixedW != null) TxtMsFixedW.Text = $"{SlMsFixedW.Value:0} 像素";
        if (TxtMsFixedH != null)
            TxtMsFixedH.Text = SlMsFixedH.Value <= 0 ? "随内容自适应" : $"{SlMsFixedH.Value:0} 像素";
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

    /// <summary>把界面当前的桌面背景设置写入设置文件。
    /// 注意：必须写入本窗口的内存对象 _s（而不是重新 LoadSettings 得到的副本），
    /// 否则「应用到桌面背景」触发的 BackdropChanged → ReloadBackdropUi 会拿旧的 _s 回写界面，
    /// 导致刚调好的水平 / 垂直偏移被弹回默认值。</summary>
    private void SaveWpSettings()
    {
        try
        {
            var s = _s;
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowSchedule = ChkWpSchedule.IsChecked == true;
            s.Backdrop.ShowHomework = ChkWpHomework.IsChecked == true;
            s.Backdrop.ShowMissing = ChkWpMissing.IsChecked == true;
            s.Backdrop.UseOriginWallpaper = ChkWpOrigin.IsChecked == true;
            s.Backdrop.ScheduleTheme = SelectedThemeKey(CbWpSchTheme, "blue");
            s.Backdrop.ScheduleLayout = CurrentWpScheduleLayout();
            s.Backdrop.HomeworkLayout = CurrentHwLayout();
            s.Backdrop.MissingLayout = CurrentMsLayout();
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
        if (ChkWpSchedule.IsChecked != true && ChkWpHomework.IsChecked != true && ChkWpMissing.IsChecked != true)
        {
            TxtWpStatus.Text = "请至少勾选「当天课表」「作业公示」「缺交作业名单」其中一项。";
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
            var s = _s;
            s.Backdrop ??= new BackdropSettings();
            s.Backdrop.ShowSchedule = false;
            s.Backdrop.ShowHomework = false;
            s.Backdrop.ShowMissing = false;
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
