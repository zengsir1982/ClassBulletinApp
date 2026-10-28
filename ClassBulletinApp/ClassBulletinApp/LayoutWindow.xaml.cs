using System;
using System.Windows;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class LayoutWindow : Window
{
    private readonly AppSettings _s;

    private int _initialTabIndex;
    /// <summary>打开时默认选中的标签页下标（0=课表悬浮窗，1=通知悬浮窗，2=桌面背景）。
    /// 必须用属性 setter 生效：以 `new LayoutWindow { InitialTabIndex = 2 }` 形式创建时，
    /// 初始化器是在构造函数之后才赋值的，构ctor 里读到的永远是旧值（曾经的隐藏 bug）。</summary>
    public int InitialTabIndex
    {
        get => _initialTabIndex;
        set
        {
            _initialTabIndex = value;
            if (MainTabs != null && MainTabs.Items.Count > 0)
                MainTabs.SelectedIndex = Math.Clamp(value, 0, MainTabs.Items.Count - 1);
        }
    }

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
    }

    private void OnBackdropChanged() => ReloadBackdropUi();

    private void LoadValues()
    {
        var sl = _s.ScheduleLayout;
        SlSchOpacity.Value = sl.Opacity;
        SlSchOverlay.Value = sl.OverlayOpacity;
        ChkSchTop.IsChecked = sl.TopMost;
        ChkSchClick.IsChecked = sl.ClickThrough;
        ChkSchHide.IsChecked = sl.AutoHide;
        ChkSchClassHide.IsChecked = sl.AutoHideDuringClass;
        SlSchPeek.Value = Math.Clamp(sl.EdgePeek, SlSchPeek.Minimum, SlSchPeek.Maximum);
        SlSchEdge.Value = Math.Clamp(sl.EdgeTrigger, SlSchEdge.Minimum, SlSchEdge.Maximum);

        var nl = _s.NotificationLayout;
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
        SlSchOpacity.ValueChanged += (_, _) => TxtSchOpacity.Text = $"{SlSchOpacity.Value:0%}";
        SlSchOverlay.ValueChanged += (_, _) => TxtSchOverlay.Text = $"{SlSchOverlay.Value:0%}";
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
        RefreshValueLabels();   // 打开窗口时先按当前值填一遍数值提示（否则要拖动滑块才出现）
    }

    /// <summary>按滑块当前值刷新右下角的数值 / 说明文字。</summary>
    private void RefreshValueLabels()
    {
        TxtSchOpacity.Text = $"{SlSchOpacity.Value:0%}";
        TxtSchOverlay.Text = $"{SlSchOverlay.Value:0%}";
        TxtNotFont.Text = $"{SlNotFont.Value:0} 号";
        TxtNotSpeed.Text = $"{SlNotSpeed.Value:0} 像素/秒";
        TxtNotOpacity.Text = $"{SlNotOpacity.Value:0%}";
        TxtNotOverlay.Text = $"{SlNotOverlay.Value:0%}";
        TxtSchPeek.Text = SlSchPeek.Value <= 0 ? "0 像素 · 完全移出屏幕（推荐）" : $"{SlSchPeek.Value:0} 像素（保留一条可点的细边）";
        TxtSchEdge.Text = $"{SlSchEdge.Value:0} 像素";
        TxtNotPeek.Text = SlNotPeek.Value <= 0 ? "0 像素 · 完全移出屏幕（推荐）" : $"{SlNotPeek.Value:0} 像素（保留一条可点的细边）";
        TxtNotEdge.Text = $"{SlNotEdge.Value:0} 像素";
    }

    /// <summary>把界面控件当前值同步进内存设置对象 _s（不落盘）。实时预览与「保存并应用」共用，
    /// 保证 RaisedLayoutChanged 传出去的布局对象携带最新值，悬浮窗才能即时变化。
    /// 注意：位置 / 大小不在这里同步——它们只由用户直接拖动窗口写入（所见即所得）。</summary>
    private void SyncControlsToSettings()
    {
        var sl = _s.ScheduleLayout;
        sl.Opacity = SlSchOpacity.Value;
        sl.OverlayOpacity = SlSchOverlay.Value;
        sl.TopMost = ChkSchTop.IsChecked == true;
        sl.ClickThrough = ChkSchClick.IsChecked == true;
        sl.AutoHide = ChkSchHide.IsChecked == true && !sl.ClickThrough;
        sl.AutoHideDuringClass = ChkSchClassHide.IsChecked == true && !sl.ClickThrough;
        sl.EdgePeek = SlSchPeek.Value;
        sl.EdgeTrigger = SlSchEdge.Value;

        var nl = _s.NotificationLayout;
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

    /// <summary>拖动透明度 / 字号 / 自动隐藏参数时，实时让已打开的悬浮窗同步（所见即所得）。
    /// 先同步控件值到 _s，再把内存中正在编辑的布局对象传过去，避免悬浮窗每次都重新读盘拿不到最新值。</summary>
    private void WireLivePreview()
    {
        void Preview(object? _, EventArgs __)
        {
            SyncControlsToSettings();
            AppEvents.RaiseLayoutChanged(_s.ScheduleLayout, _s.NotificationLayout);
        }

        SlSchOpacity.ValueChanged += Preview;
        SlSchOverlay.ValueChanged += Preview;
        SlSchPeek.ValueChanged += Preview;
        SlSchEdge.ValueChanged += Preview;

        SlNotFont.ValueChanged += Preview;
        SlNotSpeed.ValueChanged += Preview;
        SlNotOpacity.ValueChanged += Preview;
        SlNotOverlay.ValueChanged += Preview;
        SlNotPeek.ValueChanged += Preview;
        SlNotEdge.ValueChanged += Preview;
    }

    // ---------- 找不到窗口时的兜底：放回屏幕正中央 ----------
    // （已移除「放回屏幕中央」「管理应用快捷方式」按钮：位置直接拖动即可；
    //   应用快捷方式仍可在悬浮课表底部「管理」按钮里设置。）

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
