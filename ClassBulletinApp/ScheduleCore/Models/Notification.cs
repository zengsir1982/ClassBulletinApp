using System;

namespace ScheduleCore.Models;

public class Notification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    /// <summary>
    /// 通知范围（数据包分发用）：
    /// "school" = 全校通知 —— 会打进数据包同步到所有教室机；
    /// "local"  = 本班通知 —— 只留在本机，数据包既不带出也不覆盖。默认 local。
    /// 旧数据没有该字段，反序列化后为 local。
    /// </summary>
    public string Scope { get; set; } = "local";

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string PinButtonText => Pinned ? "取消置顶" : "置顶";

    /// <summary>界面上的范围标签。</summary>
    public string ScopeText => Scope == "school" ? "全校" : "本班";

    /// <summary>列表里「切换范围」按钮的文案：显示的是"点了会变成什么"。</summary>
    public string ScopeButtonText => Scope == "school" ? "设为本班" : "设为全校";
}
