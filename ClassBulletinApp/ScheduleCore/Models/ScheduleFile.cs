using System;
using System.Collections.Generic;

namespace ScheduleCore.Models;

public class ScheduleFile
{
    public string SourceFile { get; set; } = "";

    /// <summary>
    /// 参与合并的源文件名（如七/八/九年级三份总课表）。
    /// 旧数据只有 <see cref="SourceFile"/>，此处为空；合并导入时逐份追加。
    /// </summary>
    public List<string> SourceFiles { get; set; } = new();

    public DateTime ImportedAt { get; set; }
    public List<ClassSchedule> Classes { get; set; } = new();

    /// <summary>
    /// 各年级的固定时段（大课间 / 眼保健操 / 午休）。键 = 年级名，如 "八年级"。
    /// 由二一排课 project-list 的 commonSectionList 推导；Excel 导入路径不填（无此信息），
    /// 因此为空时渲染层不插入固定行，行为与旧版一致。
    /// </summary>
    public Dictionary<string, List<FixedSlot>> FixedByGrade { get; set; } = new();
}

/// <summary>
/// 固定时段：不占课节序号，按「第 AfterPeriod 节之后」插入到当天课表里。
/// </summary>
public class FixedSlot
{
    /// <summary>名称：大课间 / 眼保健操 / 午休。</summary>
    public string Name { get; set; } = "";

    /// <summary>全局节次（1-8，上午 1-4、下午 5-8），表示插入在这一节之后。</summary>
    public int AfterPeriod { get; set; }

    /// <summary>该固定时段的时间区间（用相邻两节课的间隙推出），如 "08:40-09:10"。</summary>
    public string TimeRange { get; set; } = "";
}
