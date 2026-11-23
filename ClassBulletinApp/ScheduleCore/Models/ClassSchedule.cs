using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ScheduleCore.Models;

public class ClassSchedule
{
    /// <summary>
    /// 手动录入班级的班名尾巴。手动录入的课表与「电子表格导入 / 自动更新」的课表各存一份，
    /// 靠这个尾巴区分同名班，避免互相覆盖（r129）。
    /// <para>r140：从「（手动录入）」缩成「（手录）」——弹窗与班级下拉里短一点更好读。
    /// 旧数据仍带长尾巴，见 <see cref="LegacyManualSuffix"/>。</para>
    /// </summary>
    public const string ManualSuffix = "（手录）";

    /// <summary>r128~r139 用过的长尾巴。老数据（尤其没有 Origin 字段的）靠它继续认得出来。</summary>
    public const string LegacyManualSuffix = "（手动录入）";

    public string ClassName { get; set; } = "";

    /// <summary>
    /// 课表来源标记："Manual" = 手动录入；空（或其它）= 电子表格 / 自动更新导入。
    /// <para>★ 导入与自动更新落盘前会把 Manual 的班级原样保留（<c>MainWindow.ApplyImportedSchedule</c>），
    /// 所以手动录入的课表不会因为一次自动更新就消失。</para>
    /// </summary>
    public string Origin { get; set; } = "";

    /// <summary>
    /// 手动录入时选的那份作息时间表（本机模板名）。用于重新打开时恢复下拉，
    /// 也用于在大屏按该模板的公共时间渲染。
    /// </summary>
    public string PeriodTemplateName { get; set; } = "";

    /// <summary>
    /// 保存那一刻的公共时间快照（大课间 / 眼保健操 / 午休 …）。
    /// <para>为什么不只记模板名：数据包会把 schedule.json 发到教室机，而模板只存发布端本机；
    /// 快照跟着课表走，教室机没有模板也能显示正确的公共时间。</para>
    /// </summary>
    public List<FixedSlot> FixedSlots { get; set; } = new();

    // dayIndex(0=周一..4=周五) -> period -> lessons(可能含单双周交替)
    public Dictionary<int, Dictionary<int, List<Lesson>>> Data { get; set; } = new();

    /// <summary>
    /// 是不是手动录入的课表。除显式标记外，班名带 <see cref="ManualSuffix"/>（或旧的
    /// <see cref="LegacyManualSuffix"/>）的也算 —— 兼容 r128 及更早版本保存的、还没有 Origin 字段的旧数据。
    /// </summary>
    [JsonIgnore]
    public bool IsManual => string.Equals(Origin, "Manual", StringComparison.OrdinalIgnoreCase)
                            || HasManualSuffix(ClassName);

    /// <summary>班名带手录尾巴（新 / 旧两种都算）。</summary>
    public static bool HasManualSuffix(string? name)
    {
        string n = name ?? "";
        return n.EndsWith(ManualSuffix, StringComparison.Ordinal)
               || n.EndsWith(LegacyManualSuffix, StringComparison.Ordinal);
    }

    /// <summary>给班名补上「（手录）」尾巴（已经有尾巴、新旧都算，就不重复加）。</summary>
    public static string WithManualSuffix(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return n;
        return HasManualSuffix(n) ? n : n + ManualSuffix;
    }
}
