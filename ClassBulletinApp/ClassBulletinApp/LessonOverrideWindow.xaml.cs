using System;
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

    /// <summary>本次修改归属的那一周的起始日（默认当前周；周视图打开时传该周周一）。</summary>
    private readonly DateTime _weekStart;

    /// <summary>底表（用于判断"改回原位"：与底表完全一致时不再保留调代标记）。</summary>
    private readonly ScheduleFile? _schedule;

    public LessonOverrideWindow(string className, int dayIdx, int period, LessonView current,
        ScheduleFile? schedule, DateTime? weekStart = null)
    {
        InitializeComponent();
        _className = className;
        _dayIdx = dayIdx;
        _period = period;
        _weekStart = weekStart ?? DateTime.Today;
        _schedule = schedule;

        TxtTitle.Text = $"{className}　·　{DayName(dayIdx)}　·　第{period}节";
        var (ws, we) = WeekHelper.WeekWindowOf(_weekStart);
        TxtScope.Text = $"本条仅在 {ws:M/d} – {we:M/d} 的{DayName(dayIdx)}第{period}节生效，过期自动失效（基础课表不变）。";

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
        // 班主任只能改自己班；电教管理员（学生）与游客不能改任何班
        if (!UserSession.CanEditClass(_className))
        {
            MessageBox.Show(UserSession.DenyEditClass(_className), "无法修改",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (ws, we) = WeekHelper.WeekWindowOf(_weekStart);
        var list = Store.LoadOverrides();

        string subject = TxtSubject.Text.Trim();
        string teacher = TxtTeacher.Text.Trim();
        string location = TxtLocation.Text.Trim();
        string note = TxtNote.Text.Trim();
        bool isEmpty = string.IsNullOrWhiteSpace(subject);

        // ★ 改回原位 = 取消调代：填的内容与该格底表原课程完全一致（且没加备注）时，
        //   直接删掉本周这条记录，而不是留一条"看起来是调课、其实是原课"的覆盖。
        //   备注有内容说明确实想留个说明，按"有改动"处理。
        if (note.Length == 0 && LessonComparer.IsSameAsBase(_schedule, _className, _dayIdx, _period,
                                                            subject, teacher, location, isEmpty))
        {
            int removed = list.RemoveAll(x => x.ClassName == _className && x.DayIdx == _dayIdx && x.Period == _period
                                              && x.Source == "manual" && x.OverlapsRange(ws, we));
            if (removed > 0) Store.SaveOverrides(list);
            DialogResult = true;
            Close();
            return;
        }

        // ★ 按「本周 + 该格」定位，不能再用 Key 字符串比：Key 现在带生效起始日，
        //   而且"本周改同一格"必须改写本周那条，不能碰其他周的记录。
        var ov = list.FirstOrDefault(x => x.ClassName == _className && x.DayIdx == _dayIdx && x.Period == _period
                                          && x.Source == "manual" && x.OverlapsRange(ws, we));
        if (ov == null)
        {
            ov = new LessonOverride { ClassName = _className, DayIdx = _dayIdx, Period = _period, Source = "manual" };
            list.Add(ov);
        }
        ov.Subject = subject;
        ov.Teacher = teacher;
        ov.Location = location;
        ov.Note = note;
        ov.Type = CbType.SelectedIndex == 1 ? "代" : "调";
        ov.IsEmpty = isEmpty;
        ov.ValidFrom = DateOnly.FromDateTime(ws);   // 旧记录若没有区间，这里一并补齐
        ov.ValidTo = DateOnly.FromDateTime(we);
        ov.UpdatedAt = DateTime.Now;
        Store.SaveOverrides(list);
        DialogResult = true;
        Close();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var (ws, we) = WeekHelper.WeekWindowOf(_weekStart);
        var list = Store.LoadOverrides();
        // 只清「本周该格」的记录，其他周的同一格保持不变
        int n = list.RemoveAll(x => x.ClassName == _className && x.DayIdx == _dayIdx && x.Period == _period
                                    && x.OverlapsRange(ws, we));
        if (n > 0) Store.SaveOverrides(list);
        TxtScope.Text = n > 0
            ? $"已清除本周（{ws:M/d} – {we:M/d}）这一格的 {n} 条调代记录。"
            : "本周这一格本来就没有调代记录。";
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
