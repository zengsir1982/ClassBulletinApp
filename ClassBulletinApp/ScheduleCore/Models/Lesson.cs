namespace ScheduleCore.Models;

public class Lesson
{
    public int Period { get; set; }
    public string Subject { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    public int Week { get; set; } // 0=全周, 1=单周, 2=双周
    public string TimeRange { get; set; } = "";
}
