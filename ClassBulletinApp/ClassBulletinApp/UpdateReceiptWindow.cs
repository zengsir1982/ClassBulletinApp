using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Wpf = System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 发布机看「各教室机更新课表回执」的面板。
///
/// <para>不复用 <see cref="BoardSyncWindow"/> 的六态台账：那套是「一条内容有没有被谁确认」，
/// 这里是「一台机器停在哪个包」，混在一起会把人绕晕。</para>
///
/// <para>只读远端 status/ 目录；设备别名存在发布机本地（不随包下发）。</para>
/// </summary>
public class UpdateReceiptWindow : Window
{
    readonly IFeedSource? _src;
    string _currentSha;
    readonly int _intervalMinutes;
    readonly Wpf.ListBox _list = new();
    readonly Wpf.StackPanel _detail = new();
    readonly Wpf.TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Wpf.TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    // ★ 详情每次重建：不能把同一对控件反复 Add 到新的 StackPanel（WPF 会报「已是别的元素的逻辑子元素」）
    Wpf.TextBox _aliasBox = new();
    Wpf.Button _aliasSave = new();

    List<UpdateReceiptService.DeviceStatus> _devices = new();

    static readonly Color Ink = Color.FromRgb(0x23, 0x3A, 0x52);
    static readonly Color Muted = Color.FromRgb(0x77, 0x88, 0x9A);
    static readonly Color Green = Color.FromRgb(0x1F, 0x7A, 0x45);
    static readonly Color Amber = Color.FromRgb(0xB0, 0x76, 0x20);
    static readonly Color Red = Color.FromRgb(0xC0, 0x39, 0x2B);
    static readonly Color Blue = Color.FromRgb(0x1F, 0x55, 0xA8);

    /// <summary>诊断用计数与判定序列（只读）。</summary>
    public int DiagRowCount => _list.Items.Count;
    public string DiagStates => string.Join(",", _devices.Select(d => d.Code));
    public string DiagNames => string.Join(",", _devices.Select(d => d.Name));
    public string DiagSummaryText => _summary.Text;
    public string DiagDetailText => _detailText;
    string _detailText = "";

    /// <summary>
    /// ★ r153：构造只搭壳，<b>不碰数据源</b>。以前这里同步读 manifest + 扫 status/，
    /// 共享盘不可达时点开这个窗口要等几十秒（任务管理器里的"未响应"）。
    /// 现在先显示「正在读取…」，窗口起来后再后台读。
    /// </summary>
    public UpdateReceiptWindow(IFeedSource? feed = null, string? currentSha = null, int? intervalMinutes = null)
    {
        var st = Store.LoadSettings();
        _src = feed ?? FeedSourceFactory.Create(st);
        _currentSha = currentSha ?? "";
        _intervalMinutes = intervalMinutes ?? st.UpdateIntervalMinutes;

        Title = "📡 更新回执 · 各教室机课表更新状态";
        Width = 1040;
        Height = 700;
        MinWidth = 860;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xF9));

        BuildUi();
        if (_src == null) ShowEmptyState();
        else
        {
            _summary.Text = "正在读取设备回执…";
            _status.Text = "正在读取数据源里的 status/ 目录；共享盘 / FTP 不可达时这一步可能要等十几秒，窗口不会卡死。";
            Loaded += (_, _) => RefreshAsync();
        }
        GlassChrome.Prepare(this);
    }

    void ShowEmptyState()
    {
        _devices = new List<UpdateReceiptService.DeviceStatus>();
        _list.Items.Clear();
        _summary.Text = "未配置更新数据源 —— 没有可读取的回执。";
        _status.Text = "回执不进数据包、不改代次；每台教室机在更新数据源根目录的 status/ 下写自己那份。";
        _detail.Children.Clear();
        _detailText = "";
    }

    public void Refresh() => RefreshAsync();

    bool _busy;

    /// <summary>后台读一次；按钮「立即刷新」与窗口首次显示都走这里。界面全程可点。</summary>
    public async void RefreshAsync()
    {
        if (_src == null) { ShowEmptyState(); return; }
        if (_busy) return;
        _busy = true;

        var src = _src;
        string seed = _currentSha;
        int interval = _intervalMinutes;
        _summary.Text = "正在读取设备回执…";
        _status.Text = "正在读取数据源里的 status/ 目录；共享盘 / FTP 不可达时可能要等十几秒，窗口不会卡死。";

        try
        {
            var (sha, sum, devs) = await Task.Run(() =>
            {
                string s = seed;
                if (string.IsNullOrWhiteSpace(s))
                    s = SchedulePackage.ParseManifest(src.ReadManifest())?.DataSha256 ?? "";
                var r = UpdateReceiptService.Collect(src, s, interval);
                return (s, r.Summary, r.Devices);
            });
            _currentSha = sha;
            Render(sum, devs);
        }
        catch (Exception ex)
        {
            _devices = new List<UpdateReceiptService.DeviceStatus>();
            _list.Items.Clear();
            _summary.Text = "读取回执失败 —— 现在拿不到各机状态。";
            _status.Text = "读取失败：" + UpdateReceiptService.SafeReason(ex.Message);
            _detail.Children.Clear();
            _detailText = "";
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// 同步读一次。<b>只给诊断 / 截图用</b>（那里的"数据源"是本机临时目录，不会阻塞）。
    /// 正常界面路径一律走 <see cref="RefreshAsync"/>。
    /// </summary>
    public void RefreshSync()
    {
        if (_src == null) { ShowEmptyState(); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(_currentSha))
                _currentSha = SchedulePackage.ParseManifest(_src.ReadManifest())?.DataSha256 ?? "";
            var result = UpdateReceiptService.Collect(_src, _currentSha, _intervalMinutes);
            Render(result.Summary, result.Devices);
        }
        catch (Exception ex)
        {
            _devices = new List<UpdateReceiptService.DeviceStatus>();
            _list.Items.Clear();
            _summary.Text = "读取回执失败 —— 现在拿不到各机状态。";
            _status.Text = "读取失败：" + UpdateReceiptService.SafeReason(ex.Message);
        }
    }

    void Render(UpdateReceiptService.Summary sum, List<UpdateReceiptService.DeviceStatus> devs)
    {
        _devices = devs;
        _list.Items.Clear();
        foreach (var d in devs) _list.Items.Add(BuildRow(d));

        if (!sum.Reachable)
        {
            // 「连不上」不是「一台都没上报」：色也不该是正常色
            _summary.Text = "读不到数据源的 status/ 目录 —— 现在不知道各机状态。";
            _status.Text = $"数据源：{_src!.Describe}　｜　连不上（网络 / 共享名 / 权限 / FTP 设置）。"
                + "注意：这既不代表「都已更新」，也不代表「都没更新」，是「不知道」。";
        }
        else
        {
            _summary.Text = $"共 {sum.Total} 台　✅ 最新 {sum.Latest}　⏳ 版本落后 {sum.Behind}　" +
                            $"⚠ 失败/异常 {sum.Failed}　💤 失联 {sum.Stale}";
            _status.Text = $"数据源：{_src!.Describe}　当前包摘要：{(string.IsNullOrWhiteSpace(_currentSha) ? "未发布基线" : _currentSha[..Math.Min(12, _currentSha.Length)] + "…")}" +
                           "　｜　「最新」是回执里的包摘要和当前包一致；「失联」只代表超过两个检查周期没上报，不等于没更新。";
        }

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        else { _detail.Children.Clear(); _detailText = ""; }
    }

    void BuildUi()
    {
        var root = new Wpf.Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 0 顶栏
        root.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 1 汇总
        root.RowDefinitions.Add(new Wpf.RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2 主体
        root.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });   // 3 状态栏
        Content = root;

        // ---- 顶栏 ----
        var head = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Wpf.TextBlock
        {
            Text = "更新回执", FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        });
        var btnRefresh = new Wpf.Button
        {
            Content = "↻ 立即刷新", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 10, 0), ToolTip = "重新读取数据源根目录下 status/ 里的回执"
        };
        btnRefresh.Click += (_, _) => Refresh();
        head.Children.Add(btnRefresh);
        var btnClose = new Wpf.Button
        {
            Content = "关闭", Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        btnClose.Click += (_, _) => Close();
        head.Children.Add(btnClose);
        Wpf.Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ---- 汇总条 ----
        var sumBorder = new Wpf.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xE4, 0xF7)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 10),
            Child = _summary
        };
        _summary.FontSize = 13;
        _summary.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x55, 0x88));
        Wpf.Grid.SetRow(sumBorder, 1);
        root.Children.Add(sumBorder);

        // ---- 主体：左设备列表 / 右详情 ----
        var body = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 8) };
        body.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(430) });
        body.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new Wpf.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionChanged += (_, _) => ShowDetail();
        Wpf.Grid.SetColumn(_list, 0);
        body.Children.Add(_list);

        var detailBorder = new Wpf.Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE7, 0xF0)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8)
        };
        var scroll = new Wpf.ScrollViewer { VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto };
        _detail.Margin = new Thickness(16);
        scroll.Content = _detail;
        detailBorder.Child = scroll;
        Wpf.Grid.SetColumn(detailBorder, 2);
        body.Children.Add(detailBorder);
        Wpf.Grid.SetRow(body, 2);
        root.Children.Add(body);

        // ---- 状态栏 ----
        _status.FontSize = 12;
        _status.Foreground = new SolidColorBrush(Muted);
        Wpf.Grid.SetRow(_status, 3);
        root.Children.Add(_status);
    }

    Wpf.Border BuildRow(UpdateReceiptService.DeviceStatus d)
    {
        Color tone = d.Code switch
        {
            "latest" => Green,
            "behind" => Amber,
            "stale" => Muted,
            _ => Red
        };

        var sp = new Wpf.StackPanel { Margin = new Thickness(2, 4, 2, 4) };
        var line1 = new Wpf.Grid();
        line1.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line1.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });
        line1.Children.Add(new Wpf.TextBlock
        {
            Text = d.Name, FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink), TextTrimming = TextTrimming.CharacterEllipsis
        });
        var badge = new Wpf.Border
        {
            Background = new SolidColorBrush(tone), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 2, 9, 2), VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new Wpf.TextBlock { Text = d.Label, FontSize = 11.5, Foreground = Brushes.White };
        Wpf.Grid.SetColumn(badge, 1);
        line1.Children.Add(badge);
        sp.Children.Add(line1);

        sp.Children.Add(new Wpf.TextBlock
        {
            Text = d.Detail, FontSize = 12, Foreground = new SolidColorBrush(Muted),
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(d.Reason))
            sp.Children.Add(new Wpf.TextBlock
            {
                Text = "⚠ " + d.Reason, FontSize = 12, Foreground = new SolidColorBrush(Red),
                Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap
            });

        return new Wpf.Border
        {
            Child = sp, Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE7, 0xF0)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6)
        };
    }

    void ShowDetail()
    {
        _detail.Children.Clear();
        if (_list.SelectedItem is not Wpf.Border selectedRow || _list.SelectedIndex < 0 ||
            _list.SelectedIndex >= _devices.Count)
        {
            _detailText = "";
            return;
        }
        var d = _devices[_list.SelectedIndex];
        var r = d.Receipt;

        _detail.Children.Add(new Wpf.TextBlock
        {
            Text = "设备详情", FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink), Margin = new Thickness(0, 0, 0, 12)
        });

        // 别名编辑（只存在发布机本地，不随包下发）
        var aliasRow = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        aliasRow.Children.Add(new Wpf.TextBlock
        {
            Text = "教室别名", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Foreground = new SolidColorBrush(Ink)
        });
        _aliasBox = new Wpf.TextBox
        {
            Text = d.Alias ?? "", FontSize = 13, Height = 28, Width = 190,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _aliasSave = new Wpf.Button
        {
            Content = "保存别名", Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0)
        };
        _aliasSave.Click += (_, _) => SaveAliasNow();
        aliasRow.Children.Add(_aliasBox);
        aliasRow.Children.Add(_aliasSave);
        _detail.Children.Add(aliasRow);

        var fields = new List<(string, string)>
        {
            ("当前判定", d.Label + " —— " + d.Detail),
            ("设备 Id", d.ActorId),
            ("机器名", r?.DeviceName ?? "（未上报）"),
        };
        if (r != null)
        {
            fields.AddRange(new[]
            {
                ("回执序号", "Seq " + r.Seq + "　·　" + r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("目标包", (r.TargetBuiltAt == default ? "未知" : r.TargetBuiltAt.ToString("yyyy-MM-dd HH:mm"))
                           + "　代次 " + r.TargetRev + "　" + (string.IsNullOrWhiteSpace(r.TargetBuiltBy) ? "未标注来源" : r.TargetBuiltBy)),
                ("目标摘要", string.IsNullOrWhiteSpace(r.TargetSha) ? "（空）" : r.TargetSha),
                ("结果", r.State + (r.PackageDone ? " · 整包已应用" : " · 本轮未换包")
                         + (r.SettingsDone ? " · 设置已下发" : "")
                         + (r.RevResetDone ? " · 代次已重置" : "")),
                ("本机摘要", string.IsNullOrWhiteSpace(r.AppliedSha) ? "（空）" : r.AppliedSha),
                ("读写数", $"班级 {r.ClassCount}　调代 +{r.OverrideAdded}/~{r.OverrideRemoved}　" +
                           $"通知 {r.NoticeAdded}　用户 {r.UserCount}　停用手工调课 {r.ManualSuspended}"),
                ("代次读数", $"设置 {r.AppliedSettingsRev}　日历 {r.AppliedCalendarRev}　推送凭据 {r.LastSettingsPushId}"),
                ("程序版本", r.AppVersion),
            });
            if (!string.IsNullOrWhiteSpace(r.ReasonCode) || !string.IsNullOrWhiteSpace(r.ReasonText))
                fields.Add(("原因", r.ReasonCode + "　" + r.ReasonText));
        }

        var tbl = new Wpf.Grid { Margin = new Thickness(0, 0, 0, 8) };
        tbl.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(84) });
        tbl.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var (k, v) in fields)
        {
            tbl.RowDefinitions.Add(new Wpf.RowDefinition { Height = GridLength.Auto });
            var key = new Wpf.TextBlock
            {
                Text = k, FontSize = 12.5, Foreground = new SolidColorBrush(Muted),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 7)
            };
            var val = new Wpf.TextBlock
            {
                Text = v, FontSize = 12.5, Foreground = new SolidColorBrush(Ink),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7)
            };
            Wpf.Grid.SetRow(key, row); Wpf.Grid.SetColumn(key, 0);
            Wpf.Grid.SetRow(val, row); Wpf.Grid.SetColumn(val, 1);
            tbl.Children.Add(key); tbl.Children.Add(val);
            row++;
        }
        _detail.Children.Add(tbl);

        _detailText = string.Join("\n", fields.Select(f => f.Item1 + "=" + f.Item2));
    }

    void SaveAliasNow()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _devices.Count) return;
        var d = _devices[_list.SelectedIndex];
        if (!UpdateReceiptService.SaveAlias(d.ActorId, _aliasBox.Text))
        {
            MessageBox.Show(this, "别名没能保存（本机不允许写盘）。", "保存别名",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Refresh();
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _devices.FindIndex(x => x.ActorId == d.ActorId));
    }

    // ---- 诊断钩子（r153：改走同步版，诊断上下文没有消息泵，异步刷新会让断言读到旧列表） ----
    public void DiagRefresh() => RefreshSync();
    public void DiagSelect(int index)
    {
        if (index >= 0 && index < _list.Items.Count) _list.SelectedIndex = index;
    }
    public bool DiagSetAlias(string actorId, string alias)
    {
        bool ok = UpdateReceiptService.SaveAlias(actorId, alias);
        RefreshSync();
        return ok;
    }
}
