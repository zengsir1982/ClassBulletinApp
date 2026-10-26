using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮教师课表「教师区」双击弹出的教师切换窗口：列出已导入教师课表中的所有教师，
/// 选定后写入 settings.SelectedTeacher 并触发刷新。
/// </summary>
public partial class TeacherSwitchWindow : Window
{
    public TeacherSwitchWindow(string? currentTeacher)
    {
        InitializeComponent();

        var schedule = Store.LoadTeacherSchedule();
        var names = schedule?.Classes.Select(c => c.ClassName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList() ?? new List<string>();

        CbTeacher.ItemsSource = names;
        if (!string.IsNullOrEmpty(currentTeacher) && names.Contains(currentTeacher))
            CbTeacher.SelectedItem = currentTeacher;
        else if (names.Count > 0)
            CbTeacher.SelectedIndex = 0;

        if (names.Count == 0)
        {
            CbTeacher.IsEnabled = false;
            BtnOk.IsEnabled = false;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (CbTeacher.SelectedItem is not string t || string.IsNullOrEmpty(t))
            return;
        var s = Store.LoadSettings();
        s.SelectedTeacher = t;
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
