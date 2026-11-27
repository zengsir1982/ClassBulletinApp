using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 项目同时启用 WPF 与 WinForms（托盘 NotifyIcon），全局 using 会让这些控件名产生歧义，
// 这里显式指定 WPF 版本。
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace ClassBulletinApp;

/// <summary>
/// 「下发设置到教室机」专用面板（r112）。
///
/// <para>只负责<b>生成要下发的设置内容</b>并推到数据源的 manifest，不改动发布机自身设置
/// （提醒设置例外：它整组沿用本机值，见界面说明）。</para>
///
/// <para>与「随包通道」的分工：随包通道只带观感类且内容不变不升代次；
/// 本面板带完整白名单（含更新来源组），用 PushId 判定，教室机必应用。</para>
/// </summary>
public partial class SettingsPushWindow : Window
{
    AppSettings _s = new();
    IFeedSource? _src;
    bool _busy;

    public SettingsPushWindow()
    {
        InitializeComponent();
        _s = Store.LoadSettings();
        Loaded += (_, _) => LoadFromSettings();
        ChkIncludeSource.Checked += (_, _) => SetSourcePanelEnabled(true);
        ChkIncludeSource.Unchecked += (_, _) => SetSourcePanelEnabled(false);
        ChkReminder.Checked += (_, _) => UpdateReminderSummary();
        ChkReminder.Unchecked += (_, _) => UpdateReminderSummary();
        UpdateRevSummary();

        // r148：底栏预览跟着任何勾选 / 输入变化走。用冒泡路由一次性挂上，
        // 不用逐控件订阅（六组里光 CheckBox 就有二十来个，漏一个预览就会说谎）。
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => UpdatePushPreview()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => UpdatePushPreview()));
        AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler((_, _) => UpdatePushPreview()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => UpdatePushPreview()));
        Loaded += (_, _) => UpdatePushPreview();
    }

    /// <summary>r144：⑥ 代次重置。勾上主项才放开「重下整包」子项（依赖关系要看得见）。</summary>
    void ChkResetRev_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkResetPackage == null) return;       // XAML 解析期可能先触发一次
        bool on = ChkResetRev.IsChecked == true;
        ChkResetPackage.IsEnabled = on;
        if (!on) ChkResetPackage.IsChecked = false;
        UpdateRevSummary();
    }

    /// <summary>把本机当前的代次摊开给人看 —— 管理员据此判断该不该清。</summary>
    void UpdateRevSummary()
    {
        if (TxtRevSummary == null) return;
        try
        {
            var st = Store.LoadSettings();
            TxtRevSummary.Text = $"本机当前：已应用设置代次 {st.AppliedSettingsRev} · 已应用教学日历代次 {st.AppliedCalendarRev}"
                               + $" · 课表包摘要 {(string.IsNullOrEmpty(st.LastAppliedSha) ? "空" : "已记")}";
        }
        catch { TxtRevSummary.Text = ""; }
    }

    void SetSourcePanelEnabled(bool on)
    {
        if (PnlSource == null) return;
        PnlSource.IsEnabled = on;
        PnlSource.Opacity = on ? 1.0 : 0.55;
    }

    void LoadFromSettings()
    {
        _src = FeedSourceFactory.Create(_s);
        TxtSourceInfo.Text = _src == null
            ? "数据源：本机还没配置自动更新数据源。请先在「自动更新设置…」里配好共享路径或 FTP，再回来下发。"
            : $"数据源：{_src.Kind} · {_src.Describe}";

        if (_src == null) BtnPush.IsEnabled = false;

        SelectWeekStart(_s.WeekStartDay);

        ChkPanelOnStart.IsChecked = _s.ShowPanelOnStart;
        ChkAutoSchedule.IsChecked = _s.AutoShowSchedule;
        ChkAutoNotification.IsChecked = _s.AutoShowNotification;
        ChkAutoHomework.IsChecked = _s.AutoShowHomework;
        ChkAutoMissing.IsChecked = _s.AutoShowMissing;
        ChkAutoNotice.IsChecked = _s.AutoShowNotice;
        ChkDoodleRestore.IsChecked = _s.DoodleRestoreOnStart;
        ChkDoodleCollapsed.IsChecked = _s.DoodleStartCollapsed;

        ChkReminder.IsChecked = true;
        ChkCalendar.IsChecked = true;
        UpdateCalendarSummary();

        ChkSyncEnabled.IsChecked = _s.BoardSyncEnabled;
        ChkSyncPopup.IsChecked = _s.BoardSyncPopupDuringBreak;
        ChkSyncFullScreen.IsChecked = _s.BoardSyncFullScreenReminder;
        ChkSyncVoice.IsChecked = _s.BoardSyncVoiceReminder;
        ChkSyncAutoApply.IsChecked = _s.BoardSyncAutoApply;
        ChkSyncTeacherPolling.IsChecked = _s.BoardSyncTeacherPolling;

        ChkIncludeSource.IsChecked = false;
        ChkAutoUpdate.IsChecked = _s.AutoUpdateEnabled;
        SelectSourceKind(_s.UpdateSourceKind);
        TxtFolder.Text = _s.UpdateFolderPath ?? "";
        TxtFtpUrl.Text = _s.UpdateFtpUrl ?? "";
        TxtInterval.Text = _s.UpdateIntervalMinutes.ToString();
        TxtDelay.Text = _s.UpdateStartupDelaySeconds.ToString();
        TxtSyncRoot.Text = _s.BoardSyncRoot ?? "";

        SetSourcePanelEnabled(false);
        UpdateReminderSummary();
    }

    void UpdateReminderSummary()
    {
        if (TxtReminderSummary == null) return;
        if (ChkReminder.IsChecked != true)
        {
            TxtReminderSummary.Text = "不下发提醒设置，教室机保持原样。";
            TxtReminderSummary.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        var r = _s.Reminder;
        if (r == null)
        {
            TxtReminderSummary.Text = "本机没有提醒配置，将下发空值。";
            return;
        }

        string lead = r.ClassStart == null ? "—" : r.ClassStart.LeadSeconds.ToString() + " 秒";
        TxtReminderSummary.Text =
            $"将下发：总开关 {(r.Enabled ? "开" : "关")} · 上课提前 {lead} · "
            + $"{(r.VoiceEnabled ? "语音开" : "语音关")} · 显示 {r.DisplaySeconds} 秒";
        TxtReminderSummary.Foreground = (System.Windows.Media.Brush)FindResource("BodyBrush");
    }

    /// <summary>r143：教学日历预览。开学日 + 条数 + 前 3 条，让管理员下发前能对一眼。</summary>
    void UpdateCalendarSummary()
    {
        if (TxtCalendarSummary == null) return;
        if (ChkCalendar.IsChecked != true)
        {
            TxtCalendarSummary.Text = "不下发教学日历，教室机保持原样。";
            TxtCalendarSummary.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        var sf = Store.LoadSchedule();
        var rules = (sf?.Calendar ?? new System.Collections.Generic.List<SchoolCalendarRule>())
            .OrderBy(r => r.Date, StringComparer.Ordinal).ToList();
        string start = string.IsNullOrWhiteSpace(_s.SemesterStartMonday)
            ? "未设置（教室机会按系统日历周估算）"
            : _s.SemesterStartMonday;

        var sb = new System.Text.StringBuilder();
        sb.Append("将下发：开学日 ").Append(start).Append(" · 校历 ").Append(rules.Count).Append(" 条");
        foreach (var r in rules.Take(3)) sb.Append("\n· ").Append(r.DisplayText);
        if (rules.Count > 3) sb.Append($"\n· 另有 {rules.Count - 3} 条…");

        TxtCalendarSummary.Text = sb.ToString();
        TxtCalendarSummary.Foreground = (System.Windows.Media.Brush)FindResource("BodyBrush");
    }

    void ChkCalendar_Changed(object sender, RoutedEventArgs e) => UpdateCalendarSummary();

    /// <summary>就地打开校历窗口改完再回来 —— 改的是本机的那份，回来后预览自动刷新。</summary>
    void BtnOpenCalendar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new SchoolCalendarWindow(DateTime.Today) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开校历失败：" + ex.Message, "校历", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _s = Store.LoadSettings();      // 开学日可能被改过
        UpdateCalendarSummary();
    }

    void SelectWeekStart(int day)
    {
        foreach (var o in CmbWeekStart.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(o.Tag?.ToString(), out int v) && v == day)
            {
                CmbWeekStart.SelectedItem = o;
                return;
            }
        }
        CmbWeekStart.SelectedIndex = 0;
    }

    void SelectSourceKind(string? kind)
    {
        string k = (kind ?? "").Trim().ToLowerInvariant();
        foreach (var o in CmbSourceKind.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(o.Tag?.ToString(), k, StringComparison.Ordinal))
            {
                CmbSourceKind.SelectedItem = o;
                return;
            }
        }
        CmbSourceKind.SelectedIndex = 0;
    }

    /// <param name="saveCalendarRev">true = 真正下发时把校历代次写回本机（下次不再涨）；
    /// false = 仅用于界面预览，只读不落盘，免得浏览一遍就把代次顶高了。</param>
    PackageSettings BuildPayload(bool saveCalendarRev = true)
    {
        int weekStart = 1;
        if (CmbWeekStart.SelectedItem is ComboBoxItem wi && int.TryParse(wi.Tag?.ToString(), out int wv))
            weekStart = wv;

        var ps = new PackageSettings
        {
            WeekStartDay = weekStart,
            ShowPanelOnStart = ChkPanelOnStart.IsChecked == true,
            AutoShowSchedule = ChkAutoSchedule.IsChecked == true,
            AutoShowNotification = ChkAutoNotification.IsChecked == true,
            AutoShowHomework = ChkAutoHomework.IsChecked == true,
            AutoShowMissing = ChkAutoMissing.IsChecked == true,
            AutoShowNotice = ChkAutoNotice.IsChecked == true,
            DoodleRestoreOnStart = ChkDoodleRestore.IsChecked == true,
            DoodleStartCollapsed = ChkDoodleCollapsed.IsChecked == true,
            Reminder = ChkReminder.IsChecked == true ? _s.Reminder : null,

            BoardSyncEnabled = ChkSyncEnabled.IsChecked == true,
            BoardSyncPopupDuringBreak = ChkSyncPopup.IsChecked == true,
            BoardSyncFullScreenReminder = ChkSyncFullScreen.IsChecked == true,
            BoardSyncVoiceReminder = ChkSyncVoice.IsChecked == true,
            BoardSyncAutoApply = ChkSyncAutoApply.IsChecked == true,
            BoardSyncTeacherPolling = ChkSyncTeacherPolling.IsChecked == true
        };

        if (ChkIncludeSource.IsChecked == true)
        {
            ps.SourceGroupIncluded = true;
            ps.AutoUpdateEnabled = ChkAutoUpdate.IsChecked == true;
            ps.UpdateSourceKind = (CmbSourceKind.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "folder";
            ps.UpdateFolderPath = TxtFolder.Text.Trim();
            ps.UpdateFtpUrl = TxtFtpUrl.Text.Trim();
            ps.UpdateIntervalMinutes = ParseInt(TxtInterval.Text, _s.UpdateIntervalMinutes);
            ps.UpdateStartupDelaySeconds = ParseInt(TxtDelay.Text, _s.UpdateStartupDelaySeconds);
            ps.BoardSyncRoot = TxtSyncRoot.Text.Trim();
        }

        // r143 教学日历：开学日 + 校历整组。代次按内容指纹算，内容没变就不涨 ——
        // 否则每次下发都把代次推高，教室机就得跟着重刷一遍校历。
        if (ChkCalendar.IsChecked == true)
        {
            var st = Store.LoadSettings();               // 用最新那份算代次，算完写回，代次才连续
            SchedulePackage.IncludeCalendarGroup(ps, st, Store.LoadSchedule());
            if (saveCalendarRev) Store.SaveSettings(st);
        }

        return ps;
    }

    static int ParseInt(string s, int fallback)
        => int.TryParse((s ?? "").Trim(), out int v) ? v : fallback;

    /// <summary>r144：代次重置指令（只在勾选时带）。★ 它不是设置内容，走 manifest 上的独立字段，
    /// 所以不会污染内容指纹、也不会让「内容一致就不重发」的判断失效。</summary>
    SettingsPushService.RevResetRequest BuildResetRequest() => new()
    {
        Applied = ChkResetRev.IsChecked == true,
        PackageSha = ChkResetRev.IsChecked == true && ChkResetPackage.IsChecked == true
    };

    /// <summary>r148：所有「会影响所有教室机」的选项统一在这里登记，供预览和确认共用一份文案。</summary>
    List<string> CollectWarnings(PackageSettings ps, SettingsPushService.RevResetRequest reset)
    {
        var list = new List<string>();
        if (ps.SourceGroupIncluded)
            list.Add("更新来源 / 同步根目录会被发布机的值覆盖（"
                     + $"来源 {ps.UpdateSourceKind}"
                     + (string.IsNullOrWhiteSpace(ps.UpdateFolderPath) ? "" : $"，共享路径 {ps.UpdateFolderPath}")
                     + (string.IsNullOrWhiteSpace(ps.UpdateFtpUrl) ? "" : $"，FTP {ps.UpdateFtpUrl}")
                     + (string.IsNullOrWhiteSpace(ps.BoardSyncRoot) ? "" : $"，同步根 {ps.BoardSyncRoot}")
                     + "）。填错会让教室机再也拉不到更新。");
        if (ChkForce.IsChecked == true)
            list.Add("强制重新下发：内容与远端一致也会重写 manifest，所有教室机下次检查都会重新应用一遍设置。");
        if (reset.Any)
            list.Add(RevResetService.Describe(reset.Applied, reset.PackageSha)
                     + "。清零不可撤销，教室机会把收到的内容全部当成新的应用一遍。");
        return list;
    }

    /// <summary>r148：底栏实时预览。读本机设置但不写盘，纯展示。</summary>
    void UpdatePushPreview()
    {
        if (TxtPushPreview == null) return;
        try
        {
            var ps = BuildPayload(saveCalendarRev: false);
            var changes = SettingsPushService.DescribeChanges(Store.LoadSettings(), ps);
            var warnings = CollectWarnings(ps, BuildResetRequest());

            var sb = new System.Text.StringBuilder();
            sb.Append("本次下发：").Append(SettingsPushService.Summary(ps));
            if (ps.CalendarIncluded)
                sb.Append($"（校历代次 {ps.CalendarRev} · {ps.Calendar?.Count ?? 0} 条）");
            sb.Append("\n相对本机：").Append(DescribeForPreview(changes));
            if (warnings.Count > 0) sb.Append("\n⚠ ").Append(string.Join("；", warnings));

            TxtPushPreview.Text = sb.ToString();
            TxtPushPreview.Foreground = (System.Windows.Media.Brush)FindResource(
                warnings.Count > 0 ? "DangerBrush" : "BodyBrush");
        }
        catch (Exception ex)
        {
            TxtPushPreview.Text = "预览失败：" + ex.Message;
        }
    }

    static string DescribeForPreview(List<string> changes) => changes.Count == 0
        ? "无改动"
        : string.Join("、", changes.Take(5)) + (changes.Count > 5 ? $" 等 {changes.Count} 项" : "");

    async void BtnPush_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _src == null) return;

        var ps = BuildPayload();
        var reset = BuildResetRequest();

        // r148：三处危险项（更新来源覆盖 / 强制重推 / 重置代次）共用一次确认，
        // 原先是两三个弹窗连着弹，管理员容易一路点确认过去。
        var warnings = CollectWarnings(ps, reset);
        if (warnings.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("本次下发会影响所有教室机，请核对后再继续。\n\n");
            sb.Append("下发内容：").Append(SettingsPushService.Summary(ps)).Append('\n');
            sb.Append("相对本机改动：")
              .Append(DescribeForPreview(SettingsPushService.DescribeChanges(Store.LoadSettings(), ps)))
              .Append("\n\n危险项：\n");
            foreach (var w in warnings) sb.Append("· ").Append(w).Append('\n');
            sb.Append("\n确定继续吗？");

            if (MessageBox.Show(sb.ToString(), "确认下发设置", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        _busy = true;
        BtnPush.IsEnabled = false;
        BtnPush.Content = "下发中…";
        TxtResult.Text = "正在上传到数据源…";

        try
        {
            bool force = ChkForce.IsChecked == true || reset.Any;
            var r = await System.Threading.Tasks.Task.Run(
                () => SettingsPushService.Push(_src!, ps, force, reset));

            if (!r.Ok)
            {
                TxtResult.Text = r.Message;
                TxtResult.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                return;
            }

            TxtResult.Text = r.Message;
            TxtResult.Foreground = (System.Windows.Media.Brush)FindResource("BodyBrush");

            if (!r.Skipped)
            {
                MessageBox.Show(
                    r.Message + "\n\n教室机会在下次自动检查时应用（默认 30 分钟内，可在「自动更新设置」里调整间隔）。",
                    "设置已下发", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            TxtResult.Text = "下发失败：" + ex.Message;
            TxtResult.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        finally
        {
            _busy = false;
            BtnPush.IsEnabled = true;
            BtnPush.Content = "下发";
        }
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
