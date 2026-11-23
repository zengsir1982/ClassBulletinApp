using System;
using System.Collections.Generic;
using System.Linq;

namespace ScheduleCore.Models;

/// <summary>
/// r136：基础数据清单项的来源标记。
/// <para>自动项在「从当前课表重新整理」时刷新；手动项保留；已排除项靠墓碑不复活。</para>
/// </summary>
public enum BaseDataOrigin
{
    /// <summary>自动从导入表 / 二一更新 / 现有课表提取，整理时会被刷新。</summary>
    Auto = 0,
    /// <summary>人工添加 / 改名，整理时原样保留，不随导入变化。</summary>
    Manual = 1,
    /// <summary>人工排除，打墓碑；重新整理时即使课表里又出现也不复活。</summary>
    Excluded = 2
}

/// <summary>
/// r136：一条基础数据清单项（班级 / 科目 / 教师共用）。
/// </summary>
public class BaseDataEntry
{
    /// <summary>显示名（班级名 / 科目名 / 教师名）。</summary>
    public string Name { get; set; } = "";

    public BaseDataOrigin Origin { get; set; } = BaseDataOrigin.Auto;

    /// <summary>墓碑：班级=已归档、科目/教师=已排除。整理时不复活。</summary>
    public bool Tombstoned { get; set; }

    /// <summary>改名后旧名保留并指向新名；整理遇到旧名时不复活、也不再新增。</summary>
    public string? RenamedTo { get; set; }

    /// <summary>自动提取时统计的出现次数，供窗口排序与显示。</summary>
    public int Count { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public bool IsActive => !Tombstoned;
}

/// <summary>
/// r136：自动整理时发现的分歧（同一节出现两种时间等），需要人工单选后才能写入。
/// </summary>
public class BaseDataConflict
{
    /// <summary>冲突键，例如 "第3节" 或 "作息节次数"。</summary>
    public string Key { get; set; } = "";

    /// <summary>候选值（去重后的多种写法）。</summary>
    public List<string> Candidates { get; set; } = new();

    /// <summary>人工选中的值；为空表示还没裁决。</summary>
    public string? Chosen { get; set; }

    public bool Resolved => !string.IsNullOrEmpty(Chosen);
}

/// <summary>
/// r136：「从当前课表重新整理」的结果，供窗口预览 / 冲突确认后再写入。
/// <para>纯内存对象，不落盘；确认后由 BaseDataExtractor.Apply 写回 ScheduleFile。</para>
/// </summary>
public class BaseDataExtractResult
{
    public string? SuggestedTermName { get; set; }
    public List<BaseDataEntry> Classes { get; set; } = new();
    public List<BaseDataEntry> Subjects { get; set; } = new();
    public List<BaseDataEntry> Teachers { get; set; } = new();

    /// <summary>从二一元数据 / Excel 表头识别到的节次时间（按 Index）。</summary>
    public List<PeriodTime> BellPeriods { get; set; } = new();
    public List<FixedSlot> BellFixedSlots { get; set; } = new();

    /// <summary>同一节出现多种时间的分歧，需人工单选。</summary>
    public List<BaseDataConflict> Conflicts { get; set; } = new();

    public int PendingCount => Conflicts.Count(c => !c.Resolved)
                             + (string.IsNullOrEmpty(SuggestedTermName) ? 0 : 0);

    public string Summary =>
        $"班级 +{Classes.Count(x => x.IsActive)}、科目 +{Subjects.Count(x => x.IsActive)}、" +
        $"教师 +{Teachers.Count(x => x.IsActive)}、作息节次识别 {BellPeriods.Count} 节" +
        (Conflicts.Count(c => !c.Resolved) > 0 ? $"、待确认 {Conflicts.Count(c => !c.Resolved)} 项" : "");
}
