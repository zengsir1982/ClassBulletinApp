using System.Collections.Generic;

namespace ScheduleCore.Models;

public class ClassSchedule
{
    public string ClassName { get; set; } = "";
    // dayIndex(0=周一..4=周五) -> period -> lessons(可能含单双周交替)
    public Dictionary<int, Dictionary<int, List<Lesson>>> Data { get; set; } = new();
}
