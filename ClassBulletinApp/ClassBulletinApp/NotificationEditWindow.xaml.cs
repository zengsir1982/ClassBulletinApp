using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ClassBulletinApp.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColors = System.Windows.Media.Colors;

namespace ClassBulletinApp;

public partial class NotificationEditWindow : Window
{
    private readonly ObservableCollection<Notification> _items = new();

    /// <summary>r72 焦点跟随：全窗只有一条工具条，把标题 / 内容输入框映射到各自的草稿样式。</summary>
    private FocusStyleBinder? _binder;

    // 编辑区的「当前草稿样式」：列表是多条通知，输入框一次只编辑一条。
    // 用独立对象而不是直接改列表项，老师改了格式但没点「添加 / 更新」时不会悄悄写进别的条目。
    private TextStyle _draftTitle = NewDefaultTitleStyle();
    private TextStyle _draftContent = NewDefaultContentStyle();

    /// <summary>默认档（标准 = 档位 0）配合渲染端的基准字号换算，外观与加功能之前完全一致。</summary>
    private static TextStyle NewDefaultTitleStyle() => new() { Bold = true, SizeLevel = 0 };
    private static TextStyle NewDefaultContentStyle() => new() { SizeLevel = 0 };

    public NotificationEditWindow()
    {
        InitializeComponent();
        Lst.ItemsSource = _items;
        DtpDue.SelectedDate = DateTime.Today;   // 默认有效期为当天
        Reload();
        ApplyScopePermission();
        BuildAppearance();   // 便签外观色点：必须在 InitializeComponent 之后，ThemeChips 才存在

        // 委托取「当前草稿」：切换条目 / 重置草稿后工具条自动指向新对象，不用重绑。
        _binder = new FocusStyleBinder(this, PickStyle);
        _binder.Bind(TxtTitle, () => _draftTitle, "通知标题");
        _binder.Bind(TxtContent, () => _draftContent, "通知正文");
        // 置灰态也绑定本窗的默认档位：工具条控件出厂显示的是「27 特大」，
        // 不先 Attach 一次，老师点输入框时会看到字号从 27 突然跳到 19，以为格式被改小了。
        PickStyle.Attach(_draftTitle, "通知标题");
        PickStyle.Deactivate();
        ApplyFormatToEditor();
    
        GlassChrome.Prepare(this);}

    // ---------- 诊断钩子（--ui-check 用，同程序集可见） ----------
    internal FocusStyleBinder? DiagBinder => _binder;
    internal System.Windows.Controls.TextBox DiagTitleBox => TxtTitle;
    internal System.Windows.Controls.TextBox DiagContentBox => TxtContent;
    internal TextStyle DiagTitleStyle => _draftTitle;
    internal TextStyle DiagContentStyle => _draftContent;
    internal ClassBulletinApp.Controls.TextStylePicker DiagPicker => PickStyle;
    internal void DiagLoadDraft(Notification? n) => LoadDraftFrom(n);

    /// <summary>把某条通知的样式载入草稿（老数据 / 同步落地的数据没有样式对象 → 用默认外观）。</summary>
    private void LoadDraftFrom(Notification? n)
    {
        _draftTitle = n?.TitleStyle?.Clone() ?? NewDefaultTitleStyle();
        _draftContent = n?.ContentStyle?.Clone() ?? NewDefaultContentStyle();
        // 换了条目：作用对象变了，先置灰，等老师点下一个输入框再激活（不悄悄回退到标题）
        _binder?.Deactivate();
        ApplyFormatToEditor();
    }

    /// <summary>把当前草稿样式即时应用到两个输入框（所见即所得）；颜色只在工具条预览里显示。</summary>
    private void ApplyFormatToEditor()
    {
        if (TxtTitle == null) return;
        TxtTitle.FontWeight = _draftTitle.Bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
        TxtTitle.FontSize = Math.Max(10, HwTextRender.ScaleSize(_draftTitle, 15) - 1);
        TxtContent.FontWeight = _draftContent.Bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
        TxtContent.FontSize = Math.Max(10, HwTextRender.ScaleSize(_draftContent, 14) - 1);
    }

    private void Style_Changed(object? sender, EventArgs e) => ApplyFormatToEditor();

    /// <summary>
    /// 按当前身份决定能否勾「全校通知」。
    /// <para>无 <see cref="UserSession.CanSendSchoolNotice"/> 权限时禁用勾选框并给出原因；
    /// 已存在的全校通知仍显示为勾选（只读），避免误降级。</para>
    /// </summary>
    private void ApplyScopePermission()
    {
        bool canSchool = UserSession.CanSendSchoolNotice;
        ChkSchool.IsEnabled = canSchool;
        ChkSchool.ToolTip = canSchool ? null : UserSession.DenySendSchoolNotice;
    }

    /// <summary>
    /// 计算最终范围：无权限时新增=本班、编辑=保留原范围（不降级、不越权升全校）。
    /// </summary>
    private bool EffectiveSchoolScope(bool existingIsSchool)
    {
        if (UserSession.CanSendSchoolNotice) return ChkSchool.IsChecked == true;
        return existingIsSchool;
    }

    // ---------- 便签主题风格（r79d）：并进「文字格式」那一行，不另起一行 ----------
    //
    // 形态与 HomeworkEditWindow 的「主题风格」一致：ComboBox + 色块/名称项。
    // 字号不在这里给：左边那条 TextStylePicker 已经管「当前这一条通知」的字号，
    // 便签整体的字号倍率仍由「便签管理 → 风格…」那个独立窗管，两处职责别混。

    /// <summary>主题下拉项：色块画刷预先解析好，XAML 里只做 Fill 绑定。</summary>
    private sealed class ThemeOption
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public System.Windows.Media.Brush Background { get; init; } = WpfBrushes.White;
    }

    /// <summary>待落盘的便签主题键（跟 settings.json 的 NoticeNoteTheme 同步）。</summary>
    private string _noteTheme = "blue";

    /// <summary>构造期填下拉时别把「回填选中项」当成老师改了设置，否则打开窗口就写一次盘。</summary>
    private bool _appearanceLoading;

    /// <summary>构造期调用：读出当前主题并填充下拉。</summary>
    private void BuildAppearance()
    {
        try { _noteTheme = NormalizeTheme(Store.LoadSettings().NoticeNoteTheme); }
        catch { _noteTheme = "blue"; }

        _appearanceLoading = true;
        try
        {
            CbNoteTheme.ItemsSource = HomeworkThemes.Presets
                .Select(t => new ThemeOption
                {
                    Key = t.Key,
                    Name = t.Name,
                    Background = ThemeHelper.Brush(t.Background, WpfColors.White)
                })
                .ToList();
            CbNoteTheme.SelectedIndex = HomeworkThemes.IndexOf(_noteTheme);
        }
        finally { _appearanceLoading = false; }
    }

    /// <summary>未知键一律回落到「清新蓝」（与 NoticeWindow.LoadStyle 的口径一致）。</summary>
    private static string NormalizeTheme(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "blue";
        string k = key.Trim();
        foreach (var t in HomeworkThemes.Presets)
            if (string.Equals(t.Key, k, StringComparison.OrdinalIgnoreCase)) return t.Key;
        return "blue";
    }

    private void CbNoteTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_appearanceLoading || CbNoteTheme.SelectedItem is not ThemeOption opt) return;
        if (string.Equals(_noteTheme, opt.Key, StringComparison.OrdinalIgnoreCase)) return;
        _noteTheme = opt.Key;
        CommitAppearance();
    }

    /// <summary>
    /// 立即落盘并让背后那块真便签重绘。
    /// <para>本窗没有「取消」按钮（只有「关闭」），所以按显示设置页的规矩做「逐项落盘」。</para>
    /// <para>★ 只写 <c>NoticeNoteTheme</c>，<b>绝不顺手写 NoticeNoteScale</b>：字号倍率由
    /// 「便签管理 → 风格…」那个独立窗负责，这里若拿打开窗口时的旧值回填，会把老师在别处
    /// 刚改过的字号悄悄冲掉。</para>
    /// </summary>
    private void CommitAppearance()
    {
        try
        {
            var s = Store.LoadSettings();
            s.NoticeNoteTheme = _noteTheme;
            if (Store.EnablePersistence) Store.SaveSettings(s);

            // 让已经打开的通知公告便签立刻重绘（所见即所得）。
            // ★ 不能只刷 Owner：从「便签管理 → 通知公告 → 编辑…」进来时 Owner 是主窗不是便签，
            // 那块便签明明显示着却不会变，老师会以为没生效。
            RefreshOpenNotices();
        }
        catch { }
    }

    /// <summary>把当前所有已开的通知公告便签重读风格并重绘。</summary>
    private static void RefreshOpenNotices()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        foreach (Window w in app.Windows)
        {
            if (w is not NoticeWindow nw) continue;
            try { nw.Reload(); } catch { }
        }
    }

    // ---------- 便签主题诊断钩子（--ui-check 用） ----------
    // 走真实的 ComboBox.SelectedIndex，连 SelectionChanged 一起验，不绕过 UI 直接调私有方法。
    internal int DiagThemeItemCount => CbNoteTheme?.Items.Count ?? 0;
    internal string DiagNoteTheme => _noteTheme;
    internal void DiagPickTheme(string key) => CbNoteTheme.SelectedIndex = HomeworkThemes.IndexOf(key);

    /// <summary>窗口仍是 4 行（标题 / 列表 / 表单 / 按钮），没有为外观多起一行。</summary>
    internal int DiagRowCount => RootGrid.RowDefinitions.Count;

    /// <summary>主题下拉必须和「文字格式」工具条待在同一个容器里。未 Show 的窗口可视树为空，只能走逻辑树。</summary>
    internal bool DiagThemeInFormatBar
    {
        get
        {
            var bar = System.Windows.LogicalTreeHelper.GetParent(PickStyle);
            var wrap = System.Windows.LogicalTreeHelper.GetParent(CbNoteTheme);
            var grand = wrap is System.Windows.DependencyObject d
                ? System.Windows.LogicalTreeHelper.GetParent(d) : null;
            return bar != null && ReferenceEquals(bar, grand);
        }
    }

    private void Reload()
    {
        _items.Clear();
        // 勾上「显示已撤下」才把墓碑项也列出来，平时只列还在用的
        bool showDeleted = ChkShowDeleted.IsChecked == true;
        var src = showDeleted ? Store.LoadNotificationsAll() : Store.LoadNotifications();
        foreach (var n in src
                     .Where(x => showDeleted || !x.Deleted)
                     .OrderBy(x => x.Deleted)
                     .ThenByDescending(x => x.Pinned)
                     .ThenBy(x => x.DueAt ?? DateTime.MaxValue)
                     .ThenByDescending(x => x.CreatedAt))
            _items.Add(n);
    }

    /// <summary>
    /// 把界面上的条目 patch 回全量列表（含墓碑）再保存。
    /// <para>★ 绝不能直接 <c>SaveNotifications(_items)</c>：_items 只装当前显示的那部分，
    /// 直接保存会把墓碑整批冲掉，等于又退回物理删除。</para>
    /// </summary>
    private void Persist()
    {
        var all = Store.LoadNotificationsAll();
        var byId = new Dictionary<string, Notification>();
        foreach (var x in all) byId[x.Id] = x;
        foreach (var n in _items)
        {
            if (byId.TryGetValue(n.Id, out var target))
            {
                target.Title = n.Title;
                target.Content = n.Content;
                target.Scope = n.Scope;
                target.Pinned = n.Pinned;
                target.DueAt = n.DueAt;
                // r72：标题 / 正文的文字格式也要 patch 回全量，否则「更新选中」后样式悄悄丢失
                target.TitleStyle = n.TitleStyle?.Clone();
                target.ContentStyle = n.ContentStyle?.Clone();
            }
            else all.Add(n);
        }
        Store.SaveNotifications(all);
        if (Store.LastWriteError != null)
            System.Windows.MessageBox.Show(Store.LastWriteError, "没有保存", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>日历选中的日期 → 绝对截止时间（当天 23:59；留空 = 长期 → null）。</summary>
    private DateTime? SelectedDueAt()
        => DtpDue.SelectedDate is DateTime d ? d.Date.AddHours(23).AddMinutes(59) : null;

    private void ChkShowDeleted_Click(object sender, RoutedEventArgs e) => Reload();

    private void Lst_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Lst.SelectedItem is Notification n)
        {
            TxtTitle.Text = n.Title;
            TxtContent.Text = n.Content;
            ChkSchool.IsChecked = n.Scope == "school";
            DtpDue.SelectedDate = n.DueAt?.Date;   // null（长期有效）会让日历留空
            LoadDraftFrom(n);                      // 格式条切到这条通知自己的样式
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var title = TxtTitle.Text.Trim();
        var content = TxtContent.Text.Trim();
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return;
        var n = new Notification
        {
            Title = title,
            Content = content,
            CreatedAt = DateTime.Now,
            Scope = EffectiveSchoolScope(false) ? "school" : "local",
            DueAt = SelectedDueAt(),
            TitleStyle = _draftTitle.Clone(),
            ContentStyle = _draftContent.Clone()
        };
        _items.Insert(0, n);
        Persist();
        TxtTitle.Text = "";
        TxtContent.Text = "";
        ChkSchool.IsChecked = false;
        LoadDraftFrom(null);                    // 新条目从默认外观重新开始，别把上一条的格式串过来
        Reload();
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not Notification n)
        {
            System.Windows.MessageBox.Show("请先在列表中选中要更新的通知。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bool reviving = n.Deleted;
        n.Title = TxtTitle.Text.Trim();
        n.Content = TxtContent.Text.Trim();
        n.Scope = EffectiveSchoolScope(n.Scope == "school") ? "school" : "local";
        n.DueAt = SelectedDueAt();
        n.TitleStyle = _draftTitle.Clone();
        n.ContentStyle = _draftContent.Clone();
        // 对一条「已撤下」的项点更新，就是把它恢复回来
        if (reviving)
        {
            n.Deleted = false;
            n.DeletedAt = null;
        }
        Persist();
        Reload();
        Lst.Items.Refresh();
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not Notification n)
        {
            System.Windows.MessageBox.Show("请先在列表中选中要撤下的通知。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = System.Windows.MessageBox.Show(
            $"确定撤下通知「{(string.IsNullOrEmpty(n.Title) ? n.Content : n.Title)}」吗？\r\n" +
            "撤下后不再显示在通知公告便签和跑马灯上，但数据保留，勾选「显示已撤下」可以恢复。",
            "撤下通知", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        // 打墓碑而不是移除：物理删除会让晚到的旧变更单把它重新加回来
        var all = Store.LoadNotificationsAll();
        var target = all.FirstOrDefault(x => x.Id == n.Id);
        if (target != null)
        {
            target.Deleted = true;
            target.DeletedAt = DateTime.Now;
            Store.SaveNotifications(all);
        }
        TxtTitle.Text = "";
        TxtContent.Text = "";
        ChkSchool.IsChecked = false;
        LoadDraftFrom(null);
        Reload();
        AppEvents.RaiseNotificationChanged();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
