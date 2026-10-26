using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class LessonOverrideWindow : Window
{
    private readonly string _className;
    private readonly int _dayIdx;
    private readonly int _period;

    public LessonOverrideWindow(string className, int dayIdx, int period, LessonView current, ScheduleFile? schedule)
    {
        InitializeComponent();
        _className = className;
        _dayIdx = dayIdx;
        _period = period;
        TxtTitle.Text = $"{className}　·　{DayName(dayIdx)}　·　第{period}节";

        TxtSubject.Text = current.Subject;
        TxtTeacher.Text = current.Teacher;
        TxtLocation.Text = current.LocationText.Trim(' ', '·');
        TxtNote.Text = current.OverrideNote;

        // 调课类型：优先用已有覆盖的类型，否则默认“调课”
        string curType = (current.IsOverridden && !string.IsNullOrEmpty(current.OverrideType)) ? current.OverrideType : "调";
        CbType.SelectedIndex = curType == "代" ? 1 : 0;

        BuildSubjectList(schedule);
    }

    /// <summary>构建本班科目下拉：列出课表中出现过的科目，并记住各自教师，便于快速选择自动带出。</summary>
    private void BuildSubjectList(ScheduleFile? schedule)
    {
        var map = new Dictionary<string, string>(); // 科目 -> 教师（取首次出现）
        if (schedule != null)
        {
            var cs = schedule.Classes.FirstOrDefault(c => c.ClassName == _className);
            if (cs != null)
            {
                foreach (var day in cs.Data.Values)
                    foreach (var lessons in day.Values)
                        foreach (var l in lessons)
                            if (!string.IsNullOrWhiteSpace(l.Subject) && !map.ContainsKey(l.Subject))
                                map[l.Subject] = l.Teacher;
            }
        }

        var items = map.Select(kv => new SubjectTeacherItem { Subject = kv.Key, Teacher = kv.Value }).ToList();
        // 确保当前已有科目也在列表里（可能手填的不在课表中）
        if (!string.IsNullOrWhiteSpace(TxtSubject.Text) && !items.Any(i => i.Subject == TxtSubject.Text))
            items.Insert(0, new SubjectTeacherItem { Subject = TxtSubject.Text, Teacher = TxtTeacher.Text });

        CbSubjects.ItemsSource = items;
        CbSubjects.DisplayMemberPath = nameof(SubjectTeacherItem.Subject);
        CbSubjects.SelectedValuePath = nameof(SubjectTeacherItem.Subject);

        var match = items.FirstOrDefault(i => i.Subject == TxtSubject.Text);
        CbSubjects.SelectedItem = match;
    }

    private void CbSubjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbSubjects.SelectedItem is SubjectTeacherItem it)
        {
            TxtSubject.Text = it.Subject;
            TxtTeacher.Text = it.Teacher ?? "";
        }
    }

    private static string DayName(int d) => d switch
    {
        0 => "星期一", 1 => "星期二", 2 => "星期三", 3 => "星期四", 4 => "星期五",
        _ => "周末"
    };

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var list = Store.LoadOverrides();
        var ov = list.FirstOrDefault(x => x.Key == $"{_className}|{_dayIdx}|{_period}");
        if (ov == null)
        {
            ov = new LessonOverride { ClassName = _className, DayIdx = _dayIdx, Period = _period };
            list.Add(ov);
        }
        ov.Subject = TxtSubject.Text.Trim();
        ov.Teacher = TxtTeacher.Text.Trim();
        ov.Location = TxtLocation.Text.Trim();
        ov.Note = TxtNote.Text.Trim();
        ov.Type = CbType.SelectedIndex == 1 ? "代" : "调";
        ov.UpdatedAt = System.DateTime.Now;
        Store.SaveOverrides(list);
        DialogResult = true;
        Close();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var list = Store.LoadOverrides();
        list.RemoveAll(x => x.Key == $"{_className}|{_dayIdx}|{_period}");
        Store.SaveOverrides(list);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private class SubjectTeacherItem
    {
        public string Subject { get; set; } = "";
        public string? Teacher { get; set; }
    }
}
