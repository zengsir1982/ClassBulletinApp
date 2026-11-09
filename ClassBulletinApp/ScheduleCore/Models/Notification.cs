using System;
using System.Text.Json.Serialization;

namespace ScheduleCore.Models;

public class Notification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    /// <summary>
    /// 标题的文字格式（加粗 / 颜色 / 字号档位），由通知编辑窗的「文字格式」工具条设置。
    /// <para>★ <c>null</c> = 老数据、教师机同步落地的数据包 —— 渲染端按「默认外观」处理，
    /// 和加这个功能之前一字不差。只有本机编辑窗真正调过格式才会写入对象。</para>
    /// <para>★ 只在本机生效：变更单 / 快照 / <c>BoardChangeOrder.Styles</c> 一律不带通知样式
    /// （r71 已把发布窗的字体设置去掉），教室机始终用自己这套默认外观。</para>
    /// </summary>
    public TextStyle? TitleStyle { get; set; }

    /// <summary>正文的文字格式；<c>null</c> = 默认外观（同 <see cref="TitleStyle"/>）。</summary>
    public TextStyle? ContentStyle { get; set; }

    /// <summary>
    /// 样式签名：便签 30 秒轮询重绘与壁纸重绘都要靠它发现「只改了格式、文字没变」的情况。
    /// <para>不参与序列化（JsonIgnore），纯计算属性。</para>
    /// </summary>
    [JsonIgnore]
    public string StyleSignature =>
        (TitleStyle is { } ts ? $"{ts.Bold}/{ts.Color}/{ts.SizeLevel}" : "-") + "|" +
        (ContentStyle is { } cs ? $"{cs.Bold}/{cs.Color}/{cs.SizeLevel}" : "-");

    /// <summary>
    /// 通知范围（数据包分发用）：
    /// "school" = 全校通知 —— 会打进数据包同步到所有教室机；
    /// "local"  = 本班通知 —— 只留在本机，数据包既不带出也不覆盖。默认 local。
    /// 旧数据没有该字段，反序列化后为 local。
    /// </summary>
    public string Scope { get; set; } = "local";

    /// <summary>
    /// 截止时间（null = 长期有效）。到期后<b>只在显示层撤下</b>，数据保留、可在编辑窗恢复。
    /// <para>★ 由发布老师在发布时选「今天 / 3 天后 / 1 周后 / 1 月后 / 长期」，教室机不替老师决定。
    /// 置顶通知即使到期也继续显示（老师置顶 = 这条要长期挂着）。</para>
    /// </summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// 墓碑：<b>绝不物理删除</b>。到期撤下、教师机撤回、被同标题覆盖都只置 true。
    /// <para>★ 为什么通知也必须有墓碑：以前撤回是 <c>list.Remove</c>，而落地时用
    /// <c>if (!list.Any(x => x.Id == appliedId)) list.Add(...)</c> 去重 ——
    /// 一条已撤回的通知只要旧变更单被再拉取一次（换机、重建收件箱、共享盘晚到），
    /// 就会被重新加回来。值日那边同样的位置早就改成软删除，注释写着
    /// 「物理删掉会让晚到的旧指令把它复活」，通知这里一直没跟上。</para>
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>打墓碑的时间，用于编辑窗按「撤下时间」排序与展示。</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>是否已过截止时间（置顶不算过期，仍继续显示）。</summary>
    [JsonIgnore]
    public bool IsExpired => !Pinned && DueAt is DateTime d && DateTime.Now > d;

    /// <summary>是否该出现在便签 / 跑马灯里：未打墓碑且未过期。</summary>
    [JsonIgnore]
    public bool IsLive => !Deleted && !IsExpired;

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm");

    /// <summary>列表里的墓碑标记（空串 = 正常项，控件不占宽）。</summary>
    [JsonIgnore]
    public string StateText => Deleted ? "（已撤下）" : "";

    /// <summary>事项便签右侧的截止标签：今天 / 明天 / 11-15 / 长期 / 已过期。</summary>
    [JsonIgnore]
    public string DueText
    {
        get
        {
            if (DueAt is not DateTime d) return "长期";
            var today = DateTime.Today;
            int days = (d.Date - today).Days;
            if (days < 0) return "已过期";
            if (days == 0) return "今天";
            if (days == 1) return "明天";
            if (days == 2) return "后天";
            // 跨年才带年份，同年只显示月-日，便签上更短更好扫
            return d.Year == today.Year ? d.ToString("MM-dd") : d.ToString("yyyy-MM-dd");
        }
    }

    public string PinButtonText => Pinned ? "取消置顶" : "置顶";

    /// <summary>界面上的范围标签。</summary>
    public string ScopeText => Scope == "school" ? "全校" : "本班";

    /// <summary>列表里「切换范围」按钮的文案：显示的是"点了会变成什么"。</summary>
    public string ScopeButtonText => Scope == "school" ? "设为本班" : "设为全校";
}
