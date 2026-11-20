using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 项目同时启用 WPF 与 WinForms（托盘 NotifyIcon），全局 using 会让这些控件名产生歧义，
// 这里显式指定 WPF 版本。
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;

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

        ChkSyncEnabled.IsChecked = _s.BoardSyncEnabled;
        ChkSyncPopup.IsChecked = _s.BoardSyncPopupDuringBreak;
        ChkSyncFullScreen.IsChecked = _s.BoardSyncFullScreenReminder;
        ChkSyncVoice.IsChecked = _s.BoardSyncVoiceReminder;
        ChkSyncAutoApply.IsChecked = _s.BoardSyncAutoApply;

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

    PackageSettings BuildPayload()
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
            BoardSyncAutoApply = ChkSyncAutoApply.IsChecked == true
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

        return ps;
    }

    static int ParseInt(string s, int fallback)
        => int.TryParse((s ?? "").Trim(), out int v) ? v : fallback;

    async void BtnPush_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _src == null) return;

        var ps = BuildPayload();

        if (ps.SourceGroupIncluded)
        {
            string tip = "本次会连同「更新来源 / 同步根目录」一起下发，将覆盖所有教室机的数据源设置。\n\n"
                       + $"来源类型：{ps.UpdateSourceKind}\n"
                       + (string.IsNullOrWhiteSpace(ps.UpdateFolderPath) ? "" : $"共享路径：{ps.UpdateFolderPath}\n")
                       + (string.IsNullOrWhiteSpace(ps.UpdateFtpUrl) ? "" : $"FTP 地址：{ps.UpdateFtpUrl}\n")
                       + (string.IsNullOrWhiteSpace(ps.BoardSyncRoot) ? "" : $"同步根目录：{ps.BoardSyncRoot}\n")
                       + "\n如果填错，教室机会再也拉不到更新。确定继续吗？";

            if (MessageBox.Show(tip, "确认下发数据源设置", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        _busy = true;
        BtnPush.IsEnabled = false;
        BtnPush.Content = "下发中…";
        TxtResult.Text = "正在上传到数据源…";

        try
        {
            bool force = ChkForce.IsChecked == true;
            var r = await System.Threading.Tasks.Task.Run(
                () => SettingsPushService.Push(_src!, ps, force));

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
