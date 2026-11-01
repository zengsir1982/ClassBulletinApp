using System;
using System.Text.Json.Serialization;

namespace ScheduleCore.Models;

/// <summary>
/// 单节课的临时覆盖（调课/代课/换教室等）。
/// 用于在总课表之外，临时替换某班某天某节课的内容，优先级高于导入的课表。
///
/// 自 r8 起每条记录都带「生效区间」（<see cref="ValidFrom"/> / <see cref="ValidTo"/>）：
/// 临时调整只在指定日期范围内生效，过期自然失效，不再依赖"每周一清零"。
/// 迁移前的旧记录两个字段都是 null，由 <c>Store.LoadOverrides()</c> 自动归入其 UpdatedAt 所在周。
/// </summary>
public class LessonOverride
{
    public string ClassName { get; set; } = "";
    public int DayIdx { get; set; }   // 0=周一 .. 6=周日（日历星期，不是"窗口第几天"）
    public int Period { get; set; }   // 第几节

    public string Subject { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    public string Note { get; set; } = "";   // 调课原因/备注，如「运动会临时调整」

    /// <summary>调代类型：空=普通手动调课；"调"=临时调课；"代"=代课。</summary>
    public string Type { get; set; } = "";

    /// <summary>是否把该格调成「空课」（无课）。与 Subject 同时存在时优先视为清空。</summary>
    public bool IsEmpty { get; set; }

    /// <summary>
    /// 来源：
    /// <c>manual</c>=本机手动编辑；
    /// <c>temp</c>=从临时总课表导入 / 二一调代课缓存；
    /// <c>remote</c>=远程同步（预留，当前无写入方）；
    /// <c>basePatch</c>=长期改课（改的是底表本身，永不参与同步清除）；
    /// <c>shared</c>=班主任从本人电脑提交（<b>预留</b>：二期"班主任改本班课表"才产生，当前恒为空）。
    /// </summary>
    public string Source { get; set; } = "manual";

    /// <summary>生效起始日期（含），null = 未迁移的旧记录（视为长期有效，启动时会被自动归周）。</summary>
    public DateOnly? ValidFrom { get; set; }

    /// <summary>生效结束日期（含），null = 未迁移的旧记录。</summary>
    public DateOnly? ValidTo { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // ================== 被主机同步停用（软覆盖） ==================

    /// <summary>
    /// 是否已被「数据源覆盖手工调课」策略停用。
    /// <para>★ 停用 ≠ 删除：记录仍在 overrides.json 里，只是不再参与显示，
    /// 管理员把策略改回「保留」后可在「恢复被主机停用的本机调课」里一键复活。</para>
    /// <para>为什么不用删除：教室机上的手工调课是老师的工作成果，删掉只能靠 20 份轮转备份找回，
    /// 而教室机现场没人会去翻备份。</para>
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>被停用的时间；<see cref="Suspended"/> 为 false 时无意义。</summary>
    public DateTime? SuspendedAt { get; set; }

    /// <summary>被哪一次同步停用（如 <c>包 2026-11-01 09:20</c>），用于界面追溯。</summary>
    public string SuspendedBy { get; set; } = "";

    // ================== 提交人（预留） ==================

    /// <summary>
    /// 提交人用户名（<b>预留</b>）。二期「班主任在本机改本班课表」时填入，
    /// 用于冲突仲裁与界面标注"这条来自八(22)班班主任"。一期恒为空串。
    /// </summary>
    public string SubmittedBy { get; set; } = "";

    /// <summary>是否设置了生效区间（旧数据为 false）。</summary>
    [JsonIgnore]
    public bool HasRange => ValidFrom.HasValue && ValidTo.HasValue;

    /// <summary>
    /// 该记录在指定日期是否生效（含首尾两天）。
    /// 没有区间的旧记录一律视为生效，保证迁移失败时也不会"数据凭空消失"。
    /// <para>★ 被主机停用的记录一律不生效（但记录仍保留，可恢复）。</para>
    /// </summary>
    public bool IsActiveOn(DateTime date)
    {
        if (Suspended) return false;
        if (!HasRange) return true;
        var d = DateOnly.FromDateTime(date);
        return d >= ValidFrom!.Value && d <= ValidTo!.Value;
    }

    /// <summary>该记录的生效区间是否与 [from, to] 有交集。</summary>
    public bool OverlapsRange(DateTime from, DateTime to)
    {
        if (!HasRange) return true;
        return ValidFrom!.Value <= DateOnly.FromDateTime(to)
            && ValidTo!.Value >= DateOnly.FromDateTime(from);
    }

    /// <summary>
    /// 唯一键：班级|星期|节次|生效起始日。
    /// ★ 必须带生效起始日，否则"下周再改同一格"会覆盖掉本周那条。
    /// </summary>
    [JsonIgnore]
    public string Key =>
        $"{ClassName}|{DayIdx}|{Period}|{(ValidFrom.HasValue ? ValidFrom.Value.ToString("yyyy-MM-dd") : "")}";

    /// <summary>"班级|星期|节次" 三元组键（同一日期内定位格子用，不含生效区间）。</summary>
    [JsonIgnore]
    public string CellKey => $"{ClassName}|{DayIdx}|{Period}";
}
