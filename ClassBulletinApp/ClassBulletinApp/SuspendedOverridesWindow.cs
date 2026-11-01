using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 与 WinForms 同名的控件统一指向 WPF 版本
using ListBox = System.Windows.Controls.ListBox;

namespace ClassBulletinApp;

/// <summary>
/// 「恢复被主机停用的本机调课」。
///
/// <para>存在理由：数据源被设成「以数据源为准」时，各教室机上老师自己拖的调课会被
/// <b>停用</b>（不是删除）。这是一个可以撤销的操作，但教室里没人会去翻 20 份轮转备份，
/// 所以必须给一个一眼能看懂、一键能恢复的入口。</para>
///
/// <para>恢复后如果策略仍是「覆盖」，下一次同步会再次停用 —— 界面必须把这个后果说明白，
/// 否则老师会以为"恢复了又自己没了"。</para>
/// </summary>
public class SuspendedOverridesWindow : Window
{
    static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    readonly ListBox _list = new();
    readonly TextBlock _hint = new();
    readonly Button _btnRestore = new();

    /// <summary>当前停用记录（与 Store 里那份是同一批对象引用，恢复时直接改再整体落盘）。</summary>
    readonly List<LessonOverride> _all;
    List<LessonOverride> _items = new();

    public SuspendedOverridesWindow()
    {
        Title = "恢复被主机停用的本机调课";
        Width = 760;
        Height = 540;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = Store.LoadOverrides();

        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

        // ---------- 顶部说明 ----------
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "被主机停用的本机调课",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _hint.FontSize = 12.5;
        _hint.Foreground = System.Windows.Media.Brushes.DimGray;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 0, 0, 10);
        head.Children.Add(_hint);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---------- 底部按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        var btnAll = new Button
        {
            Content = "全选",
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0)
        };
        btnAll.Click += (_, _) => _list.SelectAll();

        _btnRestore.Content = "恢复选中的调课";
        _btnRestore.Padding = new Thickness(14, 7, 14, 7);
        _btnRestore.FontSize = 13;
        _btnRestore.Margin = new Thickness(0, 0, 8, 0);
        _btnRestore.Background = HexBrush("#E8F6EE");
        _btnRestore.BorderBrush = HexBrush("#7FC99A");
        _btnRestore.Click += (_, _) => RestoreSelected();

        var btnClose = new Button
        {
            Content = "关闭",
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 13,
            Background = HexBrush("#F3F4F6"),
            BorderBrush = HexBrush("#D1D5DB")
        };
        btnClose.Click += (_, _) => Close();

        bar.Children.Add(btnAll);
        bar.Children.Add(_btnRestore);
        bar.Children.Add(btnClose);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 列表 ----------
        _list.SelectionMode = System.Windows.Controls.SelectionMode.Extended;
        _list.FontSize = 13;
        _list.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_list);

        Content = root;
        Reload();
    }

    static System.Windows.Media.Brush HexBrush(string hex)
    {
        try
        {
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        }
        catch { return System.Windows.Media.Brushes.WhiteSmoke; }
    }

    /// <summary>当前真正生效的同步策略（教室机看包里下发的值，不看本机编辑项）。</summary>
    static bool EffectiveOverwrite()
    {
        var s = Store.LoadSettings();
        return s.PackageOverwriteManual ?? false;
    }

    void Reload()
    {
        _items = OverrideExpander.SuspendedFor(_all);
        _list.Items.Clear();

        foreach (var o in _items)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = Describe(o),
                Tag = o,
                Padding = new Thickness(6, 4, 6, 4)
            });
        }

        bool overwrite = EffectiveOverwrite();

        if (_items.Count == 0)
        {
            _hint.Text = overwrite
                ? "当前没有停用的记录。\n"
                  + "（主机策略为「以数据源为准」：本机手工调课一旦同步就会被停用，但记录会保留在这里。）"
                : "当前没有停用的记录（主机策略为「保留本机手工调课」，本机手改不会被同步清掉）。";
        }
        else
        {
            _hint.Text = $"共 {_items.Count} 条本机手工调课被主机同步停用（记录都还在，恢复后立即可见）。\n"
                + (overwrite
                    ? "⚠ 主机当前策略仍是「以数据源为准」：恢复后，下一次同步会再次把它们停用。"
                      + "要让恢复真正生效，需要管理员把同步策略改回「保留本机手工调课」。"
                    : "主机策略为「保留本机手工调课」，恢复后不会再被停用。");
        }

        _btnRestore.IsEnabled = _items.Count > 0;
    }

    static string Describe(LessonOverride o)
    {
        var day = o.DayIdx >= 0 && o.DayIdx < 7 ? DayNames[o.DayIdx] : $"第{o.DayIdx}天";
        var what = o.IsEmpty
            ? "（调空）"
            : (string.IsNullOrWhiteSpace(o.Subject) ? "（空）" : o.Subject)
              + (string.IsNullOrWhiteSpace(o.Teacher) ? "" : " / " + o.Teacher);

        var range = o.HasRange
            ? $"{o.ValidFrom:MM-dd}~{o.ValidTo:MM-dd}"
            : "长期";

        var by = string.IsNullOrWhiteSpace(o.SuspendedBy) ? "" : $"　停用于 {o.SuspendedBy}";
        var note = string.IsNullOrWhiteSpace(o.Note) ? "" : $"　备注：{o.Note}";

        return $"{o.ClassName}　{day} 第{o.Period}节　{what}　（{range}）{by}{note}";
    }

    void RestoreSelected()
    {
        var picked = _list.SelectedItems.OfType<ListBoxItem>()
                          .Select(i => i.Tag as LessonOverride)
                          .Where(o => o != null)
                          .Cast<LessonOverride>()
                          .ToList();

        if (picked.Count == 0)
        {
            MessageBox.Show("请先在列表里选择要恢复的记录（可按住 Ctrl 多选，或点「全选」）。",
                "恢复调课", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (EffectiveOverwrite())
        {
            var ans = MessageBox.Show(
                "主机当前的同步策略是「以数据源为准」：恢复后，下一次同步会再次把这些调课停用。\n\n"
                + "要让恢复真正生效，需要管理员把策略改回「保留本机手工调课」。\n\n仍要现在恢复吗？",
                "恢复调课", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.OK) return;
        }

        int n = 0;
        foreach (var o in picked)
        {
            if (!o.Suspended) continue;
            o.Suspended = false;
            o.SuspendedAt = null;
            o.SuspendedBy = "";
            n++;
        }

        if (n > 0)
        {
            Store.SaveOverrides(_all);
            AppEvents.RaiseScheduleChanged();   // 悬浮窗 / 周视图立即重建
        }

        Reload();
        MessageBox.Show(n > 0 ? $"已恢复 {n} 条调课。" : "选中的记录已经不在停用状态。",
            "恢复调课", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
