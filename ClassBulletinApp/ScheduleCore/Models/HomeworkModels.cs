using System;
using System.Collections.Generic;

namespace ScheduleCore.Models;

/// <summary>单科作业条目。</summary>
public class HomeworkItem
{
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>作业公示栏内容：标题 + 各科目作业。</summary>
public class HomeworkBoard
{
    public string Title { get; set; } = "各科作业公示";
    public List<HomeworkItem> Items { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
