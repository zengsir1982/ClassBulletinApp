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
        }
        finally { _hwReady = ready; }
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
    /// 导致刚调好的内容 / 主题被弹回旧值。
    /// 位置与大小由「所见即所得排版」窗口（BackdropEditorWindow）维护，这里不再改写它们的 Layout。</summary>
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
            Store.SaveSettings(s);
        }
        catch { }
    }

    /// <summary>打开「所见即所得」排版窗口：在模拟屏幕上直接拖动便签调整位置、拉四角 / 四边改变大小。</summary>
    private void BtnWpEdit_Click(object sender, RoutedEventArgs e)
    {
        SaveWpSettings();   // 先把勾选 / 主题落盘，编辑器读到的就是最新状态

        if (ChkWpSchedule.IsChecked != true && ChkWpHomework.IsChecked != true && ChkWpMissing.IsChecked != true)
        {
            TxtWpStatus.Text = "请先勾选要显示的便签（当天课表 / 作业公示 / 缺交作业名单），再打开排版窗口。";
            return;
        }

        bool saved = false;
        try
        {
            var w = new BackdropEditorWindow { Owner = this };
            saved = w.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            TxtWpStatus.Text = "打不开排版窗口：" + ex.Message;
            return;
        }

        _s.Backdrop = Store.LoadSettings().Backdrop;   // 编辑器已落盘，重新读回最新布局
        ReloadBackdropUi();

        if (!saved)
        {
            TxtWpStatus.Text = "已取消，便签位置 / 大小保持不变。";
            return;
        }
        TxtWpStatus.Text = "便签位置与大小已保存" + (BackdropService.IsApplied() ? "，壁纸已重绘。" : "，点「应用到桌面背景」即可生效。");
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
