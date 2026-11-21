using System;
using System.Collections.Generic;
using System.Linq;

namespace ScheduleCore.Models;

/// <summary>
/// 手动录入课表用到的三类模板，统一落在本机 schedule_templates.json。
/// <para>设计约定：这是<b>发布端私有</b>数据，不进数据包、不下发到教室机——
/// 各校作息/科目不同，下发会盖掉别人的模板。窗口只在本机读写。</para>
/// </summary>
public class ScheduleTemplates
{
    public List<PeriodTemplate> PeriodTemplates { get; set; } = new();
    public List<SubjectLibrary> SubjectLibraries { get; set; } = new();
    public List<SavedSchedulePlan> Plans { get; set; } = new();

    /// <summary>按班级存的「上次用的课程设置」，进录入窗口自动调出（r125）。</summary>
    public List<ClassSetup> ClassSetups { get; set; } = new();

    /// <summary>返回一份带内置预置项的新对象（文件不存在 / 损坏时用）。</summary>
    public static ScheduleTemplates CreateWithBuiltIns()
    {
        var t = new ScheduleTemplates();

        t.PeriodTemplates.Add(new PeriodTemplate
        {
            Name = "小学 4 节", IsBuiltIn = true, IsDefault = false,
            Periods = new List<PeriodTime>
            {
                new() { Index = 1, TimeRange = "08:30-09:10" },
                new() { Index = 2, TimeRange = "09:30-10:10" },
                new() { Index = 3, TimeRange = "10:40-11:20" },
                new() { Index = 4, TimeRange = "14:00-14:40" },
            },
            FixedSlots = new List<FixedSlot>
            {
                new() { Name = "升旗", AfterPeriod = 0, TimeRange = "08:10-08:30" },
                new() { Name = "大课间", AfterPeriod = 2, TimeRange = "10:10-10:40" },
                new() { Name = "午休", AfterPeriod = 3, TimeRange = "11:20-14:00" },
            }
        });
        t.PeriodTemplates.Add(new PeriodTemplate
        {
            Name = "初中 7 节", IsBuiltIn = true, IsDefault = true,
            Periods = new List<PeriodTime>
            {
                new() { Index = 1, TimeRange = "08:10-08:55" },
                new() { Index = 2, TimeRange = "09:05-09:50" },
                new() { Index = 3, TimeRange = "10:10-10:55" },
                new() { Index = 4, TimeRange = "11:05-11:50" },
                new() { Index = 5, TimeRange = "14:30-15:15" },
                new() { Index = 6, TimeRange = "15:25-16:10" },
                new() { Index = 7, TimeRange = "16:20-17:05" },
            },
            FixedSlots = new List<FixedSlot>
            {
                new() { Name = "升旗", AfterPeriod = 0, TimeRange = "07:50-08:10" },
                new() { Name = "大课间", AfterPeriod = 2, TimeRange = "09:50-10:10" },
                new() { Name = "眼保健操", AfterPeriod = 3, TimeRange = "10:55-11:05" },
                new() { Name = "午休", AfterPeriod = 4, TimeRange = "11:50-14:30" },
            }
        });
        t.PeriodTemplates.Add(new PeriodTemplate
        {
            Name = "高中 8 节", IsBuiltIn = true, IsDefault = false,
            Periods = new List<PeriodTime>
            {
                new() { Index = 1, TimeRange = "08:00-08:45" },
                new() { Index = 2, TimeRange = "08:55-09:40" },
                new() { Index = 3, TimeRange = "10:00-10:45" },
                new() { Index = 4, TimeRange = "10:55-11:40" },
                new() { Index = 5, TimeRange = "14:00-14:45" },
                new() { Index = 6, TimeRange = "14:55-15:40" },
                new() { Index = 7, TimeRange = "16:00-16:45" },
                new() { Index = 8, TimeRange = "16:55-17:40" },
            },
            FixedSlots = new List<FixedSlot>
            {
                new() { Name = "升旗", AfterPeriod = 0, TimeRange = "07:40-08:00" },
                new() { Name = "大课间", AfterPeriod = 2, TimeRange = "09:40-10:00" },
                new() { Name = "眼保健操", AfterPeriod = 3, TimeRange = "10:45-10:55" },
                new() { Name = "午休", AfterPeriod = 4, TimeRange = "11:40-14:00" },
            }
        });

        string[] common = { "语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史", "地理",
                            "体育", "音乐", "美术", "信息技术", "班会", "自习" };
        var lib = new SubjectLibrary { Name = "本校常用", IsBuiltIn = true, IsDefault = true };
        foreach (var s in common) lib.Items.Add(new SubjectItem { Subject = s });
        t.SubjectLibraries.Add(lib);

        return t;
    }

    /// <summary>当前默认节次模板（没有标记默认的取第一个）。</summary>
    public PeriodTemplate? DefaultPeriodTemplate
    {
        get
        {
            if (PeriodTemplates.Count == 0) return null;
            return PeriodTemplates.FirstOrDefault(p => p.IsDefault) ?? PeriodTemplates[0];
        }
    }

    public SubjectLibrary? DefaultSubjectLibrary
    {
        get
        {
            if (SubjectLibraries.Count == 0) return null;
            return SubjectLibraries.FirstOrDefault(s => s.IsDefault) ?? SubjectLibraries[0];
        }
    }

    /// <summary>取某班上次保存的课程设置（忽略首尾空格、不分大小写）。</summary>
    public ClassSetup? FindSetup(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        string key = className.Trim();
        return ClassSetups.FirstOrDefault(c => string.Equals((c.ClassName ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>写入/覆盖某班的课程设置（同班只保留一份）。</summary>
    public void UpsertSetup(ClassSetup setup)
    {
        if (setup == null || string.IsNullOrWhiteSpace(setup.ClassName)) return;
        ClassSetups.RemoveAll(c => string.Equals((c.ClassName ?? "").Trim(), setup.ClassName.Trim(), StringComparison.OrdinalIgnoreCase));
        ClassSetups.Add(setup);
    }
}

/// <summary>一套作息时间表（决定录入网格有几行、每行显示什么时间，以及大课间等公共时间插在哪）。</summary>
public class PeriodTemplate
{
    public string Name { get; set; } = "";
    public List<PeriodTime> Periods { get; set; } = new();

    /// <summary>
    /// 公共时间（大课间 / 眼保健操 / 午休 / 升旗）：按「第 N 节之后」插入，<b>不占课节序号</b>。
    /// 复用二一导入同款 <see cref="FixedSlot"/>；二一数据存在时以 FixedByGrade 优先，否则用这里的。
    /// </summary>
    public List<FixedSlot> FixedSlots { get; set; } = new();

    public bool IsBuiltIn { get; set; }
    public bool IsDefault { get; set; }
    public override string ToString() => Name;
}

public class PeriodTime
{
    public int Index { get; set; }
    public string TimeRange { get; set; } = "";
}

/// <summary>科目库里的一条：科目 + 常用教师（教师可留空）。</summary>
public class SubjectItem
{
    public string Subject { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(Teacher) ? Subject : Subject + " · " + Teacher;
}

/// <summary>一套科目库（预置常见科目，教师留空由用户填）。</summary>
public class SubjectLibrary
{
    public string Name { get; set; } = "";
    public List<SubjectItem> Items { get; set; } = new();
    public bool IsBuiltIn { get; set; }
    public bool IsDefault { get; set; }
    public override string ToString() => Name;
}

/// <summary>
/// 整份课表方案：把录好的一个班的网格命名保存，随时套用到任意班。
/// Data 结构与 <see cref="ClassSchedule.Data"/> 一致（day -> period -> lessons）。
/// </summary>
public class SavedSchedulePlan
{
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public string PeriodTemplateName { get; set; } = "";
    public Dictionary<int, Dictionary<int, List<Lesson>>> Data { get; set; } = new();
    public override string ToString() => Name;
}

/// <summary>
/// 一个班的「课程设置快照」：任课配对清单 + 上次用的节次模板 / 科目库 / 课表方案。
/// <para>r125 起在导入、新增、编辑、删除配对后自动静默落盘（schedule_templates.json），
/// 下次进录入窗口选到该班即自动调出，不用重新导入或输入。本机私有，不进数据包。</para>
/// </summary>
public class ClassSetup
{
    public string ClassName { get; set; } = "";
    public string PeriodTemplateName { get; set; } = "";
    public string SubjectLibraryName { get; set; } = "";
    public string PlanName { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<SubjectPair> Pairs { get; set; } = new();

    public ClassSetup Clone() => new()
    {
        ClassName = ClassName,
        PeriodTemplateName = PeriodTemplateName,
        SubjectLibraryName = SubjectLibraryName,
        PlanName = PlanName,
        SavedAt = SavedAt,
        Pairs = Pairs.Select(p => p.Clone()).ToList()
    };

    public override string ToString() => ClassName + "（" + Pairs.Count + " 门）";
}

/// <summary>
/// 手动录入时的「任课配对」：科目+教师(+地点)配成一条，批量刷到课表格子上。
/// <para>单门模式：Week 取 0 全周 / 1 单周 / 2 双周；双门模式 <see cref="TwoLessons"/>=true
/// 时一格写两门，单周固定 <see cref="Subject"/>、双周固定 <see cref="Subject2"/>（对齐 r119）。</para>
/// <para>窗口内工作数据，由所属班级的 <see cref="ClassSetup"/> 自动落盘（r125）；不进数据包。</para>
/// </summary>
public class SubjectPair
{
    public string Subject { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    /// <summary>单门模式的周次：0=全周，1=单周，2=双周。</summary>
    public int Week { get; set; }

    /// <summary>一格两门：单周 <see cref="Subject"/> / 双周 <see cref="Subject2"/>。</summary>
    public bool TwoLessons { get; set; }
    public string Subject2 { get; set; } = "";
    public string Teacher2 { get; set; } = "";
    public string Location2 { get; set; } = "";

    /// <summary>自动铺到空位时用的周课时数（1~10）。</summary>
    public int WeeklyPeriods { get; set; } = 1;

    public bool IsEmpty => TwoLessons
        ? string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(Subject2)
        : string.IsNullOrWhiteSpace(Subject);

    /// <summary>生成写到某一格上的 Lesson 列表：单门 1 条，双门 2 条（W1/W2）。</summary>
    public List<Lesson> ToLessons(int period)
    {
        if (TwoLessons)
            return new List<Lesson>
            {
                new Lesson { Period = period, Subject = Subject, Teacher = Teacher, Location = Location, Week = 1 },
                new Lesson { Period = period, Subject = Subject2, Teacher = Teacher2, Location = Location2, Week = 2 }
            };
        return new List<Lesson>
        {
            new Lesson { Period = period, Subject = Subject, Teacher = Teacher, Location = Location, Week = Week }
        };
    }

    /// <summary>按科目名在课程设置里找配对（命中 Subject 或 Subject2；忽略首尾空格与大小写）。</summary>
    public static SubjectPair? FindBySubject(IEnumerable<SubjectPair>? pairs, string? subject)
    {
        if (pairs == null || string.IsNullOrWhiteSpace(subject)) return null;
        string key = subject.Trim();
        foreach (var p in pairs)
        {
            if (string.Equals((p.Subject ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase)) return p;
            if (p.TwoLessons && string.Equals((p.Subject2 ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    public SubjectPair Clone() => new()
    {
        Subject = Subject, Teacher = Teacher, Location = Location, Week = Week,
        TwoLessons = TwoLessons, Subject2 = Subject2, Teacher2 = Teacher2, Location2 = Location2,
        WeeklyPeriods = WeeklyPeriods
    };

    public override string ToString()
    {
        if (TwoLessons)
            return "单 " + Describe(Subject, Teacher) + "  /  双 " + Describe(Subject2, Teacher2);
        string s = Describe(Subject, Teacher);
        return Week == 1 ? "单周 " + s : Week == 2 ? "双周 " + s : s;
    }

    static string Describe(string subject, string teacher)
        => string.IsNullOrWhiteSpace(teacher) ? subject : subject + "·" + teacher;
}
