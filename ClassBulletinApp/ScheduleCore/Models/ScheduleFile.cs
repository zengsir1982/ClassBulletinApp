using System;
using System.Collections.Generic;

namespace ScheduleCore.Models;

public class ScheduleFile
{
    public string SourceFile { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public List<ClassSchedule> Classes { get; set; } = new();
}
