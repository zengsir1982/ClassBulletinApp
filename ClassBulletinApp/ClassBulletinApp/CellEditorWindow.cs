using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;

// 与 ManualScheduleWindow 同约定：可能歧义的类型一律写全名，避免 WinForms + WPF 双用导致 CS0104。

namespace ClassBulletinApp;

/// <summary>
/// 单格编辑器：科目 / 教师 / 地点 / 周次（全周 / 单周 / 双周 / 单双周两门）。
/// <para>r125：科目在下拉里选中后，若「课程设置」里有这门课，自动预填教师、地点、周次（都能改）；
/// 周次选到「单双周(两门)」时下半部分展开第 2 门，保存成一格两门（单周 W1 / 双周 W2）。</para>
/// <para>打开时若该格本来就是单双周两门，会自动进入两门模式并回填两门。</para>
/// </summary>
internal class CellEditorWindow : Window
{
    public List<Lesson> EditedLessons { get; private set; } = new();

    public Lesson? Edited => EditedLessons.FirstOrDefault();
    public bool Cleared { get; private set; }

    // 周次下拉索引 → Lesson.Week；索引 3 = 一格两门（本窗口专有，不代表 Week 值）
    const int WkAll = 0, WkOdd = 1, WkEven = 2, WkBoth = 3;

    readonly int _day, _period;
    readonly IReadOnlyList<SubjectPair> _pairs;
    readonly SubjectLibrary? _lib;

    readonly System.Windows.Controls.ComboBox _cbSubject = new() { FontSize = 14, IsEditable = true, Width = 250 };
    readonly System.Windows.Controls.TextBox _txtTeacher = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.TextBox _txtLocation = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.ComboBox _cbWeek = new() { FontSize = 14, Width = 130 };

    readonly System.Windows.Controls.TextBlock _firstHeader = new()
    {
        Text = "单周（第 1 门）", FontSize = 12, FontWeight = FontWeights.Bold,
        Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 2, 0, 0),
        Visibility = Visibility.Collapsed
    };

    readonly System.Windows.Controls.StackPanel _secondPanel = new() { Visibility = Visibility.Collapsed };
    readonly System.Windows.Controls.ComboBox _cbSubject2 = new() { FontSize = 14, IsEditable = true, Width = 250 };
    readonly System.Windows.Controls.TextBox _txtTeacher2 = new() { FontSize = 14, Width = 250 };
    readonly System.Windows.Controls.TextBox _txtLocation2 = new() { FontSize = 14, Width = 250 };

    /// <summary>程序回填期间置 true：避免 TextChanged / SelectionChanged 级联覆盖用户输入。</summary>
    bool _filling;

    public CellEditorWindow(int day, int period, IReadOnlyList<Lesson> lessons, SubjectLibrary? lib, IEnumerable<SubjectPair>? pairs = null)
    {
        _day = day;
        _period = period;
        _pairs = (pairs ?? Enumerable.Empty<SubjectPair>()).ToList();
        _lib = lib;

        Title = DayNames[day] + " 第" + period + "节";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // 科目下拉：科目库 + 课程设置里出现过的科目（去重）
        var subjects = new List<string>();
        if (lib != null) foreach (var it in lib.Items) subjects.Add(it.Subject);
        foreach (var p in _pairs)
        {
            subjects.Add(p.Subject);
            if (p.TwoLessons) subjects.Add(p.Subject2);
        }
        foreach (var s in subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _cbSubject.Items.Add(s);
            _cbSubject2.Items.Add(s);
        }

        _cbWeek.Items.Add("全周");
        _cbWeek.Items.Add("单周");
        _cbWeek.Items.Add("双周");
        _cbWeek.Items.Add("单双周（两门）");

        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 16, 18, 14) };

        var warn = new System.Windows.Controls.TextBlock
        {
            Text = lessons.Count > 2 ? "注意：原来这一格有 " + lessons.Count + " 门课，保存后只会留下下面填的课程。" : "",
            FontSize = 12, Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = lessons.Count > 2 ? Visibility.Visible : Visibility.Collapsed
        };
        root.Children.Add(warn);

        root.Children.Add(_firstHeader);
        root.Children.Add(Row("科目", _cbSubject));
        root.Children.Add(Row("教师", _txtTeacher));
        root.Children.Add(Row("地点", _txtLocation));

        var weekRow = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        weekRow.Children.Add(new System.Windows.Controls.TextBlock { Text = "周次：", FontSize = 13, Width = 60, VerticalAlignment = VerticalAlignment.Center });
        weekRow.Children.Add(_cbWeek);
        root.Children.Add(weekRow);

        _secondPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "双周（第 2 门）", FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0)
        });
        _secondPanel.Children.Add(Row("科目", _cbSubject2));
        _secondPanel.Children.Add(Row("教师", _txtTeacher2));
        _secondPanel.Children.Add(Row("地点", _txtLocation2));
        _secondPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "这门只在双周上；单周上第 1 门。",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(60, 2, 0, 0)
        });
        root.Children.Add(_secondPanel);

        // ---- 回填 ----
        var w1 = lessons.FirstOrDefault(l => l.Week == 1);
        var w2 = lessons.FirstOrDefault(l => l.Week == 2);
        if (w1 != null && w2 != null)
        {
            _filling = true;
            _cbSubject.Text = w1.Subject;
            _txtTeacher.Text = w1.Teacher;
            _txtLocation.Text = w1.Location;
            _cbSubject2.Text = w2.Subject;
            _txtTeacher2.Text = w2.Teacher;
            _txtLocation2.Text = w2.Location;
            _cbWeek.SelectedIndex = WkBoth;
            _filling = false;
        }
        else
        {
            var first = lessons.FirstOrDefault();
            if (first != null)
            {
                _filling = true;
                _cbSubject.Text = first.Subject;
                _txtTeacher.Text = first.Teacher;
                _txtLocation.Text = first.Location;
                _filling = false;
            }
            _cbWeek.SelectedIndex = first?.Week switch { 1 => WkOdd, 2 => WkEven, _ => WkAll };
        }
        ApplyWeekMode();

        // 选科目 → 预填该课在「课程设置」里的教师 / 地点 / 周次
        // 注意：IsEditable 的 ComboBox 在 SelectionChanged 里 Text 还是旧值，必须优先读 SelectedItem。
        _cbSubject.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            PrefillFromPairs(_cbSubject.SelectedItem as string ?? _cbSubject.Text, second: false);
        };
        _cbSubject2.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            PrefillFromPairs(_cbSubject2.SelectedItem as string ?? _cbSubject2.Text, second: true);
        };
        _cbWeek.SelectionChanged += (_, _) => ApplyWeekMode();

        var bar = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var btnClear = new Button { Content = "清空该格", FontSize = 13, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 12, 0) };
        btnClear.Click += (_, _) => { Cleared = true; DialogResult = true; Close(); };
        var btnCancel = new Button { Content = "取消", FontSize = 13, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnOk = new Button { Content = "确定", FontSize = 13, Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        btnOk.Click += (_, _) => Commit();
        bar.Children.Add(btnClear);
        bar.Children.Add(btnCancel);
        bar.Children.Add(btnOk);
        root.Children.Add(bar);

        Content = root;
        GlassChrome.Prepare(this);
    }

    /// <summary>周次选到「单双周（两门）」时展开第 2 门，并给第 1 门加个「单周」小标题。</summary>
    void ApplyWeekMode()
    {
        bool both = _cbWeek.SelectedIndex == WkBoth;
        _secondPanel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        _firstHeader.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按下拉里选的科目，从课程设置找配对：单门填教师/地点/周次；
    /// 若这条配对本身是「一格两门」，直接展开两门并整套填好（第 2 门也能单独改）。
    /// </summary>
    void PrefillFromPairs(string subject, bool second)
    {
        var p = SubjectPair.FindBySubject(_pairs, subject);
        if (p == null)
        {
            // 没有课程设置里的配对，就退回科目库自带的教师 / 地点（科目库没填教师时不动）
            var item = _lib?.Items.FirstOrDefault(i =>
                string.Equals((i.Subject ?? "").Trim(), (subject ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (item == null || (string.IsNullOrWhiteSpace(item.Teacher) && string.IsNullOrWhiteSpace(item.Location))) return;
            _filling = true;
            if (second)
            {
                if (!string.IsNullOrWhiteSpace(item.Teacher)) _txtTeacher2.Text = item.Teacher;
                if (!string.IsNullOrWhiteSpace(item.Location)) _txtLocation2.Text = item.Location;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(item.Teacher)) _txtTeacher.Text = item.Teacher;
                if (!string.IsNullOrWhiteSpace(item.Location)) _txtLocation.Text = item.Location;
            }
            _filling = false;
            return;
        }
        _filling = true;
        try
        {
            if (second)
            {
                bool hitSecond = p.TwoLessons
                    && string.Equals((p.Subject2 ?? "").Trim(), subject.Trim(), StringComparison.OrdinalIgnoreCase);
                _txtTeacher2.Text = hitSecond ? p.Teacher2 : p.Teacher;
                _txtLocation2.Text = hitSecond ? p.Location2 : p.Location;
            }
            else if (p.TwoLessons)
            {
                _cbWeek.SelectedIndex = WkBoth;
                _txtTeacher.Text = p.Teacher;
                _txtLocation.Text = p.Location;
                if (string.IsNullOrWhiteSpace(_cbSubject2.Text) || _cbSubject2.Text.Trim() == subject.Trim())
                    _cbSubject2.Text = p.Subject2;
                _txtTeacher2.Text = p.Teacher2;
                _txtLocation2.Text = p.Location2;
            }
            else
            {
                _txtTeacher.Text = p.Teacher;
                _txtLocation.Text = p.Location;
                _cbWeek.SelectedIndex = p.Week switch { 1 => WkOdd, 2 => WkEven, _ => WkAll };
            }
        }
        finally { _filling = false; }
        ApplyWeekMode();
    }

    /// <summary>
    /// 把界面内容拼成要写进格子的 Lesson 列表（单门 1 条 / 单双周 2 条）。
    /// 校验不过时弹提示并返回空列表——诊断也走这条路，不必真的弹对话框。
    /// </summary>
    internal List<Lesson> BuildLessons()
    {
        string s1 = _cbSubject.Text.Trim();
        string t1 = _txtTeacher.Text.Trim();
        string l1 = _txtLocation.Text.Trim();
        if (s1.Length == 0 && t1.Length == 0)
        {
            System.Windows.MessageBox.Show("科目和教师至少填一个；要清空请用「清空该格」。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return new List<Lesson>();
        }

        int week = _cbWeek.SelectedIndex switch { WkOdd => 1, WkEven => 2, _ => 0 };
        if (_cbWeek.SelectedIndex == WkBoth)
        {
            string s2 = _cbSubject2.Text.Trim();
            string t2 = _txtTeacher2.Text.Trim();
            if (s2.Length == 0 && t2.Length == 0)
            {
                System.Windows.MessageBox.Show("第 2 门（双周）也要至少填科目或教师。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return new List<Lesson>();
            }
            return new List<Lesson>
            {
                new Lesson { Period = _period, Subject = s1, Teacher = t1, Location = l1, Week = 1 },
                new Lesson { Period = _period, Subject = s2, Teacher = t2, Location = _txtLocation2.Text.Trim(), Week = 2 }
            };
        }
        return new List<Lesson>
        {
            new Lesson { Period = _period, Subject = s1, Teacher = t1, Location = l1, Week = week }
        };
    }

    void Commit()
    {
        var lessons = BuildLessons();
        if (lessons.Count == 0) return;
        EditedLessons = lessons;
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
            Text = label + "：", FontSize = 13, Width = 60, VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(input);
        return sp;
    }

    static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五" };
}
