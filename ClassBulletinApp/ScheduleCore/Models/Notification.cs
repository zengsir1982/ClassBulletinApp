using System;

namespace ScheduleCore.Models;

public class Notification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string PinButtonText => Pinned ? "取消置顶" : "置顶";
}
