using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮课表标题双击弹出的统一切换窗口：既可在「教师个人课表」中按姓名查询切换教师，
/// 也可切到「班级课表」选择某个班。确定后写入 AppSettings（ScheduleKind + 选中项）并通知刷新。
/// </summary>
public partial class ScheduleTargetWindow : Window
{
    private List<string> _teachers = new();
    private List<string> _classes = new();
    private bool _loading = true;

    public ScheduleTargetWindow()
    {
        InitializeComponent();

        _teachers = Names(Store.LoadTeacherSchedule());
        _classes = Names(Store.LoadSchedule());

        var s = Store.LoadSettings();
        bool teacherMode = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase) && _teachers.Count > 0;

        RbTeacher.IsEnabled = _teachers.Count > 0;
        RbClass.IsEnabled = _classes.Count > 0;
        RbTeacher.IsChecked = teacherMode;
        RbClass.IsChecked = !teacherMode;

        _loading = false;
        ApplyFilter();

        string current = teacherMode ? s.SelectedTeacher : s.SelectedClass;
        if (!string.IsNullOrEmpty(current) && ListTarget.Items.Contains(current))
            ListTarget.SelectedItem = current;
        else if (ListTarget.Items.Count > 0)
            ListTarget.SelectedIndex = 0;
        ListTarget.ScrollIntoView(ListTarget.SelectedItem);
        TxtSearch.Focus();
    }

    static List<string> Names(ScheduleCore.Models.ScheduleFile? sf)
    {
        if (sf?.Classes == null) return new List<string>();
        return sf.Classes
            .Select(c => c.ClassName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, TeacherExtractor.ZhComparer())
            .ToList();
    }

    private bool IsTeacherMode => RbTeacher.IsChecked == true;

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TxtSearch.Text = "";
        ApplyFilter();
        if (ListTarget.Items.Count > 0) ListTarget.SelectedIndex = 0;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_loading) return;
        var src = IsTeacherMode ? _teachers : _classes;
        var q = (TxtSearch.Text ?? "").Trim();
        var list = string.IsNullOrEmpty(q)
            ? src.ToList()
            : src.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        ListTarget.ItemsSource = list;
    }

    private void TxtSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ListTarget.Items.Count > 0)
            {
                ListTarget.SelectedIndex = 0;
                ApplySelection();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ListTarget.Items.Count > 0)
        {
            ListTarget.SelectedIndex = 0;
            ListTarget.Focus();
            e.Handled = true;
        }
    }

    private void ListTarget_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ApplySelection();

    private void BtnOk_Click(object sender, RoutedEventArgs e) => ApplySelection();

    private void ApplySelection()
    {
        if (ListTarget.SelectedItem is not string name || string.IsNullOrEmpty(name))
        {
            System.Windows.MessageBox.Show("请先选择一个" + (IsTeacherMode ? "教师" : "班级") + "。",
                "切换课表", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var s = Store.LoadSettings();
        if (IsTeacherMode)
        {
            s.ScheduleKind = "Teacher";
            s.SelectedTeacher = name;
        }
        else
        {
            s.ScheduleKind = "Class";
            s.SelectedClass = name;
        }
        Store.SaveSettings(s);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
