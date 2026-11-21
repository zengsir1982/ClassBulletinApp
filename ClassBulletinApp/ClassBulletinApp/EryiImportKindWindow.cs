using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Services;

// RadioButton / Button / Orientation / MessageBox 已在 GlobalUsings 全局别名

namespace ClassBulletinApp;

/// <summary>
/// 「二一课表专用导入」选完文件后弹出的类型选择框。
///
/// 存在理由：二一的「全校总课表」和「临时课表」表格结构完全一样，
/// 「单班」和「教师个人」布局也完全一样，无法从内容可靠区分——只能问用户。
/// 程序按文件名/标题先猜一个（智能预选），用户确认或改一下即可。
/// </summary>
public class EryiImportKindWindow : Window
{
    /// <summary>用户选定的导入类型。</summary>
    public EryiImportKind Kind { get; private set; }

    enum K { Base, Temp, Single, Teacher }

    readonly IReadOnlyList<string> _files;
    readonly RadioButton[] _radios = new RadioButton[4];

    /// <param name="files">本次选中的课表文件路径（用于预选和展示）。</param>
    public EryiImportKindWindow(IReadOnlyList<string> files)
    {
        _files = files ?? Array.Empty<string>();
        Kind = Recommend(_files, out string why);

        // 权限：总课表 / 单班 / 教师走 ManageData，临时课表可单独授权 ImportTemp。
        // 预选到的类型若当前角色无权，落到第一个有权项，避免点了"导入"却被拒。
        if (!Allowed(Kind))
            Kind = new[] { EryiImportKind.Base, EryiImportKind.Temp, EryiImportKind.Single, EryiImportKind.Teacher }
                       .First(Allowed);

        Title = "二一课表专用导入 · 选择类型";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(22, 20, 22, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var sp = new StackPanel();
        Grid.SetRow(sp, 0);

        sp.Children.Add(new TextBlock
        {
            Text = "这份文件属于哪种二一课表？",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        // 文件名列表 + 预选理由：让用户一眼看出程序为什么这么猜
        var names = string.Join("、", _files.Select(Path.GetFileName));
        sp.Children.Add(new TextBlock
        {
            Text = "文件：" + names,
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        sp.Children.Add(new TextBlock
        {
            Text = "已按文件名预选：" + why + (Kind == Recommend(_files, out _) ? "。" : "（当前角色无权，已改选可用项）。") + " 如不对，请在下面改选。",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        sp.Children.Add(MakeRadio(K.Base, "全校总课表（七 / 八 / 九年级，可多选）", "更新基础课表 + 自动整理教师课表"));
        sp.Children.Add(MakeRadio(K.Temp, "临时课表（含 调 / 代 标记）", "只生成调代覆盖，不改基础课表；需选生效区间"));
        sp.Children.Add(MakeRadio(K.Single, "单个班级课表", "只更新这一个班，合并进现有课表"));
        sp.Children.Add(MakeRadio(K.Teacher, "单个教师课表（教师个人适用）", "导入并切换到该教师的个人课表"));

        root.Children.Add(sp);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 16)
        };
        var btnCancel = new Button { Content = "取消", Padding = new Thickness(16, 7, 16, 7), FontSize = 14, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnOk = new Button { Content = "导入", Padding = new Thickness(16, 7, 16, 7), FontSize = 14, IsDefault = true };
        btnOk.Click += (_, _) => { Commit(); };
        bar.Children.Add(btnCancel);
        bar.Children.Add(btnOk);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        Content = root;
        GlassChrome.Prepare(this);
    }

    StackPanel MakeRadio(K key, string title, string desc)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        bool allowed = Allowed(ToPublic(key));
        var rb = new RadioButton
        {
            Content = title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            GroupName = "eryi-kind",
            IsChecked = Kind == ToPublic(key),
            IsEnabled = allowed,
            Margin = new Thickness(0, 0, 0, 2)
        };
        panel.Children.Add(rb);
        panel.Children.Add(new TextBlock
        {
            Text = allowed ? desc : desc + "（当前角色无权，已禁用）",
            FontSize = 12,
            Foreground = allowed ? System.Windows.Media.Brushes.DimGray : System.Windows.Media.Brushes.Firebrick,
            Margin = new Thickness(22, 0, 0, 0)
        });
        _radios[(int)key] = rb;
        return panel;
    }

    void Commit()
    {
        for (int i = 0; i < _radios.Length; i++)
            if (_radios[i]?.IsChecked == true && Allowed((EryiImportKind)i))
            {
                Kind = (EryiImportKind)i;
                break;
            }
        DialogResult = true;
        Close();
    }

    static EryiImportKind ToPublic(K k) => (EryiImportKind)k;

    /// <summary>当前角色是否有权使用该类型（判定只走 UserSession 唯一入口）。</summary>
    static bool Allowed(EryiImportKind k) => k == EryiImportKind.Temp
        ? UserSession.CanImportTemp
        : UserSession.CanManageData;

    /// <summary>按文件名猜类型：含"临时"→临时；含"教师/老师"→教师；含"班"→单班；否则总课表。</summary>
    internal static EryiImportKind Recommend(IReadOnlyList<string> files, out string why)
    {
        var names = (files ?? Array.Empty<string>()).Select(Path.GetFileNameWithoutExtension).ToList();
        string s = string.Join(" ", names);
        if (s.Contains("临时", StringComparison.Ordinal))
        { why = "文件名含「临时」"; return EryiImportKind.Temp; }
        if (s.Contains("教师", StringComparison.Ordinal) || s.Contains("老师", StringComparison.Ordinal))
        { why = "文件名含「教师 / 老师」"; return EryiImportKind.Teacher; }
        if (s.Contains("班", StringComparison.Ordinal))
        { why = "文件名含「班」"; return EryiImportKind.Single; }
        why = "未发现临时 / 教师 / 班等特征，默认按总课表";
        return EryiImportKind.Base;
    }
}

/// <summary>二一课表专用导入的四种类型（顺序与单选框索引一致）。</summary>
public enum EryiImportKind
{
    Base = 0,
    Temp = 1,
    Single = 2,
    Teacher = 3
}
