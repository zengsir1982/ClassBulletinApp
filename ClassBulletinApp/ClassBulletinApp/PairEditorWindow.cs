using System;
using System.Windows;
using ScheduleCore.Models;

namespace ClassBulletinApp;

/// <summary>
/// 任课配对编辑器（纯代码 Window）：一条配对可设为「单门」（全周 / 单周 / 双周），
/// 也可设为「一格两门」——单周一门、双周一门（对齐 r119 传统双行格语义）。
/// <para>科目框可编辑：既可从科目库下拉选，也能直接敲新科目。</para>
/// </summary>
internal class PairEditorWindow : Window
{
    public SubjectPair? Result { get; private set; }

    readonly SubjectLibrary? _lib;
    readonly System.Windows.Controls.CheckBox _chkTwo = new()
    {
        Content = "一格两门（单周一门 / 双周一门）", FontSize = 13, Margin = new Thickness(0, 4, 0, 0)
    };

    // 单门
    readonly System.Windows.Controls.StackPanel _single = new();
    readonly System.Windows.Controls.ComboBox _cbSubject = new() { FontSize = 14, IsEditable = true, Width = 250 };
    readonly System.Windows.Controls.TextBox _tbTeacher = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.TextBox _tbLocation = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.ComboBox _cbWeek = new() { FontSize = 14, Width = 120 };

    // 双门
    readonly System.Windows.Controls.StackPanel _double = new();
    readonly System.Windows.Controls.ComboBox _cbSub1 = new() { FontSize = 14, IsEditable = true, Width = 250 };
    readonly System.Windows.Controls.TextBox _tbTea1 = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.TextBox _tbLoc1 = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.ComboBox _cbSub2 = new() { FontSize = 14, IsEditable = true, Width = 250 };
    readonly System.Windows.Controls.TextBox _tbTea2 = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.TextBox _tbLoc2 = new() { FontSize = 14, Width = 250 };

    readonly System.Windows.Controls.TextBox _tbPeriods = new() { FontSize = 14, Width = 80 };

    public PairEditorWindow(SubjectPair? source, SubjectLibrary? lib)
    {
        _lib = lib;
        Title = source == null ? "新增任课配对" : "编辑任课配对";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        foreach (var s in new[] { _cbSubject, _cbSub1, _cbSub2 })
            if (_lib != null) foreach (var it in _lib.Items) s.Items.Add(it.Subject);
        foreach (var w in new[] { "全周", "单周", "双周" }) _cbWeek.Items.Add(w);
        _cbWeek.SelectedIndex = 0;

        _single.Children.Add(Row("科目", _cbSubject));
        _single.Children.Add(Row("教师", _tbTeacher));
        _single.Children.Add(Row("地点", _tbLocation));
        _single.Children.Add(Row("周次", _cbWeek));

        _double.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "单周（第 1 门）", FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0)
        });
        _double.Children.Add(Row("科目", _cbSub1));
        _double.Children.Add(Row("教师", _tbTea1));
        _double.Children.Add(Row("地点", _tbLoc1));
        _double.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "双周（第 2 门）", FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0)
        });
        _double.Children.Add(Row("科目", _cbSub2));
        _double.Children.Add(Row("教师", _tbTea2));
        _double.Children.Add(Row("地点", _tbLoc2));

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(_chkTwo);
        root.Children.Add(_single);
        root.Children.Add(_double);
        root.Children.Add(Row("周课时", _tbPeriods));
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "周课时供「自动铺到空位」使用；一格两门时按 1 门计。",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(80, 2, 0, 0)
        });

        var bar = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var btnCancel = new Button { Content = "取消", FontSize = 13, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnOk = new Button { Content = "确定", FontSize = 13, Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        btnOk.Click += (_, _) => Commit();
        bar.Children.Add(btnCancel);
        bar.Children.Add(btnOk);
        root.Children.Add(bar);
        Content = root;

        void ApplyMode()
        {
            bool two = _chkTwo.IsChecked == true;
            _single.Visibility = two ? Visibility.Collapsed : Visibility.Visible;
            _double.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
        }
        _chkTwo.Checked += (_, _) => ApplyMode();
        _chkTwo.Unchecked += (_, _) => ApplyMode();

        if (source != null)
        {
            _chkTwo.IsChecked = source.TwoLessons;
            _cbSubject.Text = source.Subject;
            _tbTeacher.Text = source.Teacher;
            _tbLocation.Text = source.Location;
            _cbWeek.SelectedIndex = source.Week switch { 1 => 1, 2 => 2, _ => 0 };
            _cbSub1.Text = source.Subject;
            _tbTea1.Text = source.Teacher;
            _tbLoc1.Text = source.Location;
            _cbSub2.Text = source.Subject2;
            _tbTea2.Text = source.Teacher2;
            _tbLoc2.Text = source.Location2;
        }
        ApplyMode();
        _tbPeriods.Text = (source?.WeeklyPeriods > 0 ? source.WeeklyPeriods : 1).ToString();
        GlassChrome.Prepare(this);
    }

    void Commit()
    {
        int periods = int.TryParse(_tbPeriods.Text.Trim(), out int n) ? Math.Clamp(n, 1, 10) : 1;
        var pair = new SubjectPair { WeeklyPeriods = periods, TwoLessons = _chkTwo.IsChecked == true };
        if (pair.TwoLessons)
        {
            pair.Subject = _cbSub1.Text.Trim(); pair.Teacher = _tbTea1.Text.Trim(); pair.Location = _tbLoc1.Text.Trim();
            pair.Subject2 = _cbSub2.Text.Trim(); pair.Teacher2 = _tbTea2.Text.Trim(); pair.Location2 = _tbLoc2.Text.Trim();
            if ((pair.Subject.Length == 0 && pair.Teacher.Length == 0) ||
                (pair.Subject2.Length == 0 && pair.Teacher2.Length == 0))
            {
                System.Windows.MessageBox.Show("单周和双周两门都要至少填科目或教师。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        else
        {
            pair.Subject = _cbSubject.Text.Trim();
            pair.Teacher = _tbTeacher.Text.Trim();
            pair.Location = _tbLocation.Text.Trim();
            pair.Week = _cbWeek.SelectedIndex switch { 1 => 1, 2 => 2, _ => 0 };
            if (pair.Subject.Length == 0 && pair.Teacher.Length == 0)
            {
                System.Windows.MessageBox.Show("科目和教师至少填一个。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        Result = pair;
        DialogResult = true;
        Close();
    }

    static System.Windows.Controls.StackPanel Row(string label, System.Windows.UIElement input)
    {
        var sp = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label + "：", FontSize = 13, Width = 80, VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(input);
        return sp;
    }
}
