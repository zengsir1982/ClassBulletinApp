using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

// 与 WinForms 同名的控件统一指向 WPF 版本
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ClassBulletinApp;

/// <summary>
/// 任务栏图标右键菜单的编辑器。
///
/// <para>默认菜单是「打开控制面板 / 用户中心 / 手写悬浮球 / 退出」—— 以前那排"课表悬浮：开/关"
/// 和「自定义便签」子菜单，在控制面板里都能操作，塞进右键只会让菜单又长又容易点错。
/// 需要的话可以在这里把某个开关加回来，并改成自己习惯的名字。</para>
///
/// <para>★ 只存"命令标识 + 显示文字"，<b>不存任何可执行路径</b>：
/// 设置会随数据包在教室机之间流动，存路径等于开了个远程执行口子。</para>
/// </summary>
public class TrayMenuWindow : Window
{
    /// <summary>最多允许这么多项 —— 托盘菜单超过一屏就失去"快速入口"的意义了。</summary>
    const int MaxItems = 15;

    readonly List<TrayMenuItem> _items = new();
    readonly ListBox _list = new();
    readonly ComboBox _cmd = new();
    readonly TextBox _text = new();
    readonly TextBlock _status = new();

    public TrayMenuWindow()
    {
        Title = "任务栏右键菜单";
        Width = 660;
        SizeToContent = SizeToContent.Height;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        var s = Store.LoadSettings();
        var src = s.TrayMenuItems != null && s.TrayMenuItems.Count > 0 ? s.TrayMenuItems : TrayCommands.Defaults();
        foreach (var it in src) _items.Add(it.Clone());

        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true };

        // 内容区（说明 + 列表 + 添加栏）放一个竖向 StackPanel，最后作为"填满剩余"的子元素加入
        var body = new StackPanel();

        // ---------- 顶部说明 ----------
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = "任务栏图标右键菜单",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        head.Children.Add(new TextBlock
        {
            FontSize = 12.5,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Text = "自定义在右下角托盘图标上点右键时弹出的菜单。默认是「打开控制面板」「用户中心」「手写悬浮球」「退出」；"
                 + "需要哪些开关也可以增删。文字留空则用该命令的默认名称。\n"
                 + "★ 这些开关在控制面板里同样能操作，菜单加得越长越容易点错。"
        });
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);

        // ---------- 底部按钮 ----------
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };

        Button Mk(string text, string? bg = null)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(13, 7, 13, 7),
                FontSize = 13,
                Margin = new Thickness(0, 0, 8, 0)
            };
            if (bg != null) b.Background = HexBrush(bg);
            return b;
        }

        var btnReset = Mk("恢复默认", "#F1F1F1");
        btnReset.Click += (_, _) =>
        {
            _items.Clear();
            foreach (var d in TrayCommands.Defaults()) _items.Add(d);
            RefreshList();
            _status.Text = "已恢复默认菜单（未保存）。";
        };

        var btnSave = Mk("保存", "#E8F6EE");
        btnSave.Click += (_, _) => { if (SaveAll()) { DialogResult = true; Close(); } };

        var btnClose = Mk("取消");
        btnClose.Click += (_, _) => Close();

        foreach (var b in new[] { btnReset, btnSave, btnClose }) bar.Children.Add(b);

        _status.FontSize = 12;
        _status.Foreground = Brushes.Gray;
        _status.Margin = new Thickness(4, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_status);

        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        // ---------- 列表 + 排序按钮 ----------
        var mid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _list.Height = 220;
        _list.FontSize = 13.5;
        _list.SelectionChanged += (_, _) => RefreshButtons();
        Grid.SetColumn(_list, 0);
        mid.Children.Add(_list);

        var side = new StackPanel { Width = 96 };
        Button Side(string text, Action act)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(0, 6, 0, 6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            };
            b.Click += (_, _) => act();
            side.Children.Add(b);
            return b;
        }

        Button btnUp = Side("↑ 上移", () => Move(-1));
        Button btnDown = Side("↓ 下移", () => Move(1));
        Button btnDel = Side("删除", DeleteSelected);
        _btnUp = btnUp;
        _btnDown = btnDown;
        _btnDel = btnDel;

        Grid.SetColumn(side, 2);
        mid.Children.Add(side);

        body.Children.Add(mid);

        // ---------- 添加一行 ----------
        var add = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        _cmd.FontSize = 13;
        _cmd.Width = 300;
        foreach (var d in TrayCommands.All)
            _cmd.Items.Add(new ComboBoxItem { Content = $"{d.Label}　—　{d.Hint}", Tag = d.Id });
        _cmd.SelectedIndex = 0;
        add.Children.Add(_cmd);

        _text.FontSize = 13;
        _text.Width = 150;
        _text.Margin = new Thickness(8, 0, 0, 0);
        _text.ToolTip = "显示文字（留空 = 用默认名称）";
        add.Children.Add(_text);

        var btnAdd = new Button
        {
            Content = "＋ 添加",
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0)
        };
        btnAdd.Click += (_, _) => AddItem();
        add.Children.Add(btnAdd);

        body.Children.Add(add);
        root.Children.Add(body);

        Content = root;
        RefreshList();
    }

    Button? _btnUp, _btnDown, _btnDel;

    void RefreshList()
    {
        int keep = _list.SelectedIndex;
        _list.Items.Clear();
        for (int i = 0; i < _items.Count; i++)
            _list.Items.Add(Describe(i));
        if (_list.Items.Count > 0)
            _list.SelectedIndex = Math.Clamp(keep < 0 ? 0 : keep, 0, _list.Items.Count - 1);
        RefreshButtons();
    }

    /// <summary>一行在列表里的样子：菜单实际显示的文字，后面补上它是什么命令。</summary>
    string Describe(int index)
    {
        var it = _items[index];
        string cmd = (it.Command ?? "").Trim();
        if (cmd == TrayCommands.Separator) return "—————— 分隔线 ——————";

        string label = TrayCommands.LabelOf(cmd);
        string text = (it.Text ?? "").Trim();
        return text.Length == 0 ? label : $"{text}　（{label}）";
    }

    void RefreshButtons()
    {
        int i = _list.SelectedIndex;
        if (_btnUp != null) _btnUp.IsEnabled = i > 0;
        if (_btnDown != null) _btnDown.IsEnabled = i >= 0 && i < _items.Count - 1;
        if (_btnDel != null) _btnDel.IsEnabled = i >= 0;
    }

    void Move(int delta)
    {
        int i = _list.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        RefreshList();
        _list.SelectedIndex = j;
    }

    void DeleteSelected()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        _items.RemoveAt(i);
        RefreshList();
    }

    void AddItem()
    {
        if (_items.Count >= MaxItems)
        {
            _status.Text = $"最多 {MaxItems} 项，先删掉几个再加。";
            _status.Foreground = HexBrush("#C0392B");
            return;
        }

        string cmd = (_cmd.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (!TrayCommands.IsValid(cmd)) return;

        _items.Add(new TrayMenuItem { Command = cmd, Text = (_text.Text ?? "").Trim() });
        _text.Text = "";
        RefreshList();
        _list.SelectedIndex = _items.Count - 1;
        _status.Text = "已添加（未保存）。";
        _status.Foreground = Brushes.Gray;
    }

    bool SaveAll()
    {
        // 清掉无效命令（手改 json 或版本回退后残留的），免得菜单里出现一条点了没反应的项
        var clean = new List<TrayMenuItem>();
        foreach (var it in _items)
        {
            string cmd = (it.Command ?? "").Trim();
            if (!TrayCommands.IsValid(cmd)) continue;
            clean.Add(new TrayMenuItem
            {
                Command = cmd,
                Text = (it.Text ?? "").Trim()
            });
        }

        // 全是分隔线 / 全空：菜单等于没有，强制回默认
        int real = 0;
        foreach (var it in clean)
            if (it.Command != TrayCommands.Separator) real++;

        if (real == 0)
        {
            MessageBox.Show(this,
                "菜单里至少要有一项可执行的操作（例如「打开控制面板」或「退出」），否则托盘图标右键会点不出东西。",
                "任务栏右键菜单", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            var s = Store.LoadSettings();
            s.TrayMenuItems = clean;
            Store.SaveSettings(s);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "任务栏右键菜单",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    static Brush HexBrush(string hex)
    {
        try
        {
            return new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        }
        catch { return Brushes.WhiteSmoke; }
    }
}
