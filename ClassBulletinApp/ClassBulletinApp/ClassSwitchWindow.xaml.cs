using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 悬浮课表标题「班级区」双击弹出的班级切换窗口：列出已导入课表中的所有班级，
/// 选定后写入 settings.SelectedClass 并触发刷新。
/// </summary>
public partial class ClassSwitchWindow : Window
{
    public ClassSwitchWindow(string? currentClass)
    {
        InitializeComponent();

        var schedule = Store.LoadSchedule();
        var names = schedule?.Classes.Select(c => c.ClassName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList() ?? new List<string>();

        CbClass.ItemsSource = names;
        if (!string.IsNullOrEmpty(currentClass) && names.Contains(currentClass))
            CbClass.SelectedItem = currentClass;
        else if (names.Count > 0)
            CbClass.SelectedIndex = 0;

        if (names.Count == 0)
        {
            CbClass.IsEnabled = false;
            BtnOk.IsEnabled = false;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (CbClass.SelectedItem is not string cls || string.IsNullOrEmpty(cls))
            return;
        var s = Store.LoadSettings();
        s.SelectedClass = cls;
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
