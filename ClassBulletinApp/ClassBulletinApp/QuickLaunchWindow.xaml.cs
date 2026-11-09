using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;
// 项目同时引用了 WPF 与 WinForms/System.Drawing（均有同名类型），此处用别名消歧义
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfImageSource = System.Windows.Media.ImageSource;

namespace ClassBulletinApp;

/// <summary>
/// 应用快捷方式管理：增删常用软件 / 文件夹，设置「仅显示图标」与图标大小，并实时预览。
/// 确定后写回 AppSettings（悬浮课表底部应用栏）。
/// </summary>
public partial class QuickLaunchWindow : Window
{
    private readonly ObservableCollection<QuickLaunchItem> _items = new();
    private bool _iconOnly;
    private double _size = 28;

    /// <summary>内置快捷（六个胶囊）的字号；0 = 随悬浮课表宽度自动。</summary>
    private double _builtinSize;

    /// <summary>初始化期间滑块 / 勾选框会触发事件，此标志避免把初始值当成用户改动。</summary>
    private bool _builtinReady;

    /// <summary>内置快捷「显示哪几个」的草稿：点「确定」才写盘，取消则原样保留。</summary>
    private readonly System.Collections.Generic.Dictionary<string, bool> _builtinVisible =
        new(StringComparer.Ordinal);

    public QuickLaunchWindow()
    {
        InitializeComponent();
        var s = Store.LoadSettings();
        foreach (var it in s.QuickLaunchers ?? new System.Collections.Generic.List<QuickLaunchItem>())
            _items.Add(it);
        _iconOnly = s.QlIconOnly;
        _size = s.QlIconSize > 0 ? s.QlIconSize : 28;
        _builtinSize = s.QaBuiltinFontSize;

        ChkIconOnly.IsChecked = _iconOnly;
        SlIconSize.Value = _size;
        TxtIconSize.Text = ((int)_size) + " px";

        InitBuiltinSize();
        InitBuiltinToggles();
        RenderList();
        RenderPreview();
        RenderBuiltinPreview();
    
        GlassChrome.Prepare(this);}

    // ---------- 内置快捷（九个胶囊）的显示开关 ----------

    /// <summary>
    /// 九个入口的勾选框：勾上 = 悬浮课表底部显示。
    /// <para>★ 没有权限的入口（例如电教管理员的「发布」）直接禁用并给原因：
    /// 让它可勾但实际不显示，用户会以为设置没保存。</para>
    /// <para>★ 出厂默认按角色走（见 QuickActionCatalog）：电教管理员是作业 / 缺交 / 便签 / 倒数 / 值日 / 信息，
    /// 老师与管理员是发布 / 便签 / 周课 / 倒数 / 设置 / 信息。</para>
    /// </summary>
    private void InitBuiltinToggles()
    {
        if (BuiltinToggleWrap == null) return;
        BuiltinToggleWrap.Children.Clear();
        _builtinVisible.Clear();

        var current = QuickActionCatalog.VisibleMap();
        foreach (var key in QuickActionCatalog.OrderFor(QuickActionCatalog.RoleKey()))
        {
            var e = QuickActionCatalog.Find(key);
            if (e == null) continue;

            bool on = current.TryGetValue(key, out bool v) && v;
            bool allowed = QuickActionCatalog.IsAllowed(key);
            _builtinVisible[key] = on;

            // ★ 项目同时引用了 WPF 与 WinForms，CheckBox 两个命名空间都有，必须全限定
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = e.Text,
                FontSize = 13,
                Margin = new Thickness(0, 0, 16, 4),
                IsChecked = on,
                IsEnabled = allowed,
                Tag = key,
                ToolTip = allowed ? e.Tip : QuickActionCatalog.DenyReason(key)
            };
            cb.Checked += (_, _) => SetBuiltinVisible(key, true);
            cb.Unchecked += (_, _) => SetBuiltinVisible(key, false);
            BuiltinToggleWrap.Children.Add(cb);
        }

        if (BuiltinToggleWrap.Children.Count == 0)
            BuiltinToggleWrap.Children.Add(new TextBlock
            {
                Text = "（当前账号没有可显示的入口）", FontSize = 12,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x98, 0xA2, 0xB3))
            });
    }

    private void SetBuiltinVisible(string key, bool on)
    {
        _builtinVisible[key] = on;
        RenderBuiltinPreview();
    }

    // ---------- 内置快捷（六个胶囊）的大小 ----------

    /// <summary>
    /// 用设置值初始化滑块与「自动」勾选。
    /// <para>自动档下滑块不动、灰掉，但停在"当前实际换算出来的字号"上 —— 让用户看得见自动到底给了多大。</para>
    /// </summary>
    private void InitBuiltinSize()
    {
        _builtinReady = false;
        try
        {
            bool auto = _builtinSize <= 0;
            double fs = QuickActionStyle.Resolve(_builtinSize, OwnerWidth());

            ChkBuiltinAuto.IsChecked = auto;
            SlBuiltinSize.Value = Math.Clamp(fs, SlBuiltinSize.Minimum, SlBuiltinSize.Maximum);
            SlBuiltinSize.IsEnabled = !auto;
        }
        finally { _builtinReady = true; }
    }

    /// <summary>
    /// 悬浮课表当前宽度（DIP）。<c>Owner</c> 在构造期还没赋值（对象初始化器是构造之后才设置的），
    /// 所以再退一步找当前开着的悬浮课表；都没有就按默认宽度 420 算。
    /// </summary>
    private double OwnerWidth()
    {
        if (Owner is Window w && w.ActualWidth > 0) return w.ActualWidth;

        // ★ 项目同时引用了 WinForms 与 WPF，Application 必须全限定（同名类型）
        var app = System.Windows.Application.Current;
        if (app != null)
        {
            foreach (Window win in app.Windows)
            {
                if (win is ScheduleWindow sw && sw.ActualWidth > 0) return sw.ActualWidth;
            }
        }
        return 420;
    }

    private void SlBuiltinSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载期设置 Minimum/Maximum 会触发本事件，此时其它控件还没实例化
        if (!_builtinReady || TxtBuiltinSize == null) return;
        if (ChkBuiltinAuto.IsChecked == true) return;   // 自动档：滑块只是显示换算结果，不是输入

        _builtinSize = SlBuiltinSize.Value;
        RenderBuiltinPreview();
    }

    private void ChkBuiltinAuto_Click(object sender, RoutedEventArgs e)
    {
        if (TxtBuiltinSize == null) return;

        _builtinReady = false;
        try
        {
            bool auto = ChkBuiltinAuto.IsChecked == true;
            SlBuiltinSize.IsEnabled = !auto;

            if (auto)
            {
                _builtinSize = 0;
                // 停在自动换算值上，让用户知道自动是多大
                double fs = QuickActionStyle.Resolve(0, OwnerWidth());
                SlBuiltinSize.Value = Math.Clamp(fs, SlBuiltinSize.Minimum, SlBuiltinSize.Maximum);
            }
            else
            {
                _builtinSize = SlBuiltinSize.Value;
            }
        }
        finally { _builtinReady = true; }

        RenderBuiltinPreview();
    }

    /// <summary>
    /// 预览内置快捷胶囊（与悬浮课表底部同一套样式 + 同一套大小换算）。
    /// 「自动」档按悬浮课表当前宽度换算，所以这里能直接看出窄窗下会不会挤成两行。
    /// <para>只画「勾上了、且有权限」的那几个 —— 预览显示的必须就是悬浮课表真实的样子。</para>
    /// </summary>
    private void RenderBuiltinPreview()
    {
        if (BuiltinPreview == null) return;
        BuiltinPreview.Children.Clear();

        double ownerWidth = OwnerWidth();
        double fs = QuickActionStyle.Resolve(_builtinSize, ownerWidth);
        var style = TryFindResource("QuickActionBtn") as Style;

        void Add(string text, string bg, string border)
        {
            var b = new Button
            {
                Style = style,
                Background = HexBrush(bg),
                BorderBrush = HexBrush(border),
                Content = new TextBlock { Text = text },
                IsTabStop = false,
                Focusable = false,
                IsHitTestVisible = false          // 纯预览，点了不该触发任何事
            };
            QuickActionStyle.Apply(b, fs);
            BuiltinPreview.Children.Add(b);
        }

        foreach (var key in QuickActionCatalog.OrderFor(QuickActionCatalog.RoleKey()))
        {
            if (!_builtinVisible.TryGetValue(key, out bool on) || !on) continue;
            if (!QuickActionCatalog.IsAllowed(key)) continue;
            var e = QuickActionCatalog.Find(key);
            if (e != null) Add(e.Text, e.Bg, e.Border);
        }

        if (BuiltinPreview.Children.Count == 0)
            BuiltinPreview.Children.Add(new TextBlock
            {
                Text = "（全部隐藏：悬浮课表底部不会显示内置快捷）",
                FontSize = 12,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x98, 0xA2, 0xB3))
            });

        if (TxtBuiltinSize != null) TxtBuiltinSize.Text = $"字号 {fs:0.#}";
        if (TxtBuiltinHint != null)
            TxtBuiltinHint.Text = _builtinSize > 0
                ? $"固定 {fs:0.#} 号（滑块 9~22；改完点「确定」生效）"
                : $"自动：按悬浮课表宽度 {Math.Round(ownerWidth)} 换算为 {fs:0.#} 号（窗口窄了会自动收小，几个不挤成两行）";
    }

    private static WpfBrush HexBrush(string hex)
        => new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    // ---------- 预览（与悬浮课表底部应用栏完全一致） ----------
    private void RenderPreview()
    {
        if (PreviewWrap == null) return;
        PreviewWrap.Children.Clear();
        TxtPreviewEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var icon = (QlIconConverter)Resources["QlIcon"];
        foreach (var item in _items)
        {
            var border = new Border
            {
                Background = WpfBrushes.White,
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE1, 0xE6, 0xEE)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var panel = new DockPanel { LastChildFill = false };
            var open = new Button
            {
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3, 6, 3),
                IsEnabled = false,
                ToolTip = item.Path
            };
            var img = new Image
            {
                Width = _size,
                Height = _size,
                Margin = new Thickness(0, 0, _iconOnly ? 0 : 5, 0),
                Source = icon.Convert(item.Path, typeof(WpfImageSource), null, null) as WpfImageSource
            };
            if (_iconOnly)
            {
                open.Content = img;
            }
            else
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(img);
                sp.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                open.Content = sp;
            }
            DockPanel.SetDock(open, Dock.Left);
            panel.Children.Add(open);
            border.Child = panel;
            PreviewWrap.Children.Add(border);
        }
    }

    // ---------- 列表（增删管理） ----------
    private void RenderList()
    {
        if (ListPanel == null) return;
        ListPanel.Children.Clear();
        if (_items.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = "（还没有添加任何快捷方式）",
                FontSize = 13,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x98, 0xA2, 0xB3)),
                Margin = new Thickness(2, 4, 0, 0)
            });
            return;
        }

        var icon = (QlIconConverter)Resources["QlIcon"];
        foreach (var item in _items)
        {
            var row = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconImg = new Image
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = icon.Convert(item.Path, typeof(WpfImageSource), null, null) as WpfImageSource
            };
            DockPanel.SetDock(iconImg, Dock.Left);
            row.Children.Add(iconImg);

            var name = new TextBlock
            {
                Text = (string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);

            var del = new Button
            {
                Content = "删除",
                FontSize = 12,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(10, 0, 0, 0),
                Background = new SolidColorBrush(WpfColor.FromRgb(0xFD, 0xE8, 0xE8)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE0, 0xA0, 0xA0)),
                Tag = item
            };
            del.Click += (_, _) =>
            {
                _items.Remove(item);
                RenderList();
                RenderPreview();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            ListPanel.Children.Add(row);
        }
    }

    // ---------- 添加 ----------
    private void BtnAddProgram_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "程序与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            Title = "选择要添加的软件"
        };
        if (dlg.ShowDialog() == true) AddItem(dlg.FileName, false);
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要添加的文件夹" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddItem(dlg.SelectedPath, true);
    }

    private void AddItem(string path, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var name = isFolder
            ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))
            : System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) name = path;
        _items.Add(new QuickLaunchItem { Name = name, Path = path, IsFolder = isFolder });
        RenderList();
        RenderPreview();
    }

    // ---------- 图标模式 ----------
    private void ChkIconOnly_Click(object sender, RoutedEventArgs e)
    {
        _iconOnly = ChkIconOnly.IsChecked == true;
        RenderPreview();
    }

    private void SlIconSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载期设置 Minimum/Maximum 会触发本事件，此时 TxtIconSize 尚未实例化，需跳过
        if (TxtIconSize == null) return;
        _size = SlIconSize.Value;
        TxtIconSize.Text = ((int)_size) + " px";
        RenderPreview();
    }

    // ---------- 结果 ----------
    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = Store.LoadSettings();
            s.QuickLaunchers = new System.Collections.Generic.List<QuickLaunchItem>(_items);
            s.QlIconOnly = _iconOnly;
            s.QlIconSize = _size;
            s.QaBuiltinFontSize = _builtinSize;   // 0 = 自动（随悬浮课表宽度）
            QuickActionCatalog.WriteInto(s, _builtinVisible);   // 按当前角色保存九个入口的显隐
            Store.SaveSettings(s);

            // 悬浮课表立刻重排，不必重启
            AppEvents.RaiseQuickActionsChanged();
            DialogResult = true;
        }
        catch { DialogResult = false; }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
