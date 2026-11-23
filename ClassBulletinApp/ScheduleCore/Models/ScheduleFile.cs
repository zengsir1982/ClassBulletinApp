using System;
using System.Collections.Generic;
using System.Linq;

namespace ScheduleCore.Models;

public class ScheduleFile
{
    public string SourceFile { get; set; } = "";

    /// <summary>
    /// r136：学期 / 课表版本名称（如「2026 学年第一学期」）。只从导入表标题猜建议值、由人确认，
    /// 与本机「课表方案」(SavedSchedulePlan，格子模板) 是两回事。随课表下发。
    /// </summary>
    public string TermName { get; set; } = "";

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

    /// <summary>
    /// 校历 / 调休规则（r133）：某一天按哪天的课表上课，或哪一天放假。
    /// <para>★ 放在课表里而不是 <see cref="AppSettings"/>：调休是全校统一口径，
    /// 必须随数据包下发到每台教室机（放本机设置就得逐台手动设，容易不一致）。</para>
    /// </summary>
    public List<SchoolCalendarRule> Calendar { get; set; } = new();

    /// <summary>
    /// r135：当前课表使用的作息时间表（节次时间 + 公共时间）。
    /// <para>放在课表里而不是本机设置：一份课表一份作息 —— 个人用 = 本机生效，
    /// 学校用 = 随数据包下发到所有教室机，同一套机制覆盖两种场景。</para>
    /// <para>为空时渲染链完全走旧路径（二一 FixedByGrade → Lesson.TimeRange），升级零变化。</para>
    /// </summary>
    public BellSchedule? Bell { get; set; }

    /// <summary>
    /// r136：基础数据权威清单（班级 / 科目 / 教师）。自动提取 + 手动维护的来源标记与墓碑都在这里，
    /// 与 Classes（真正排课内容）分开：Classes 仍是渲染的唯一数据源，这三份清单只用于集中维护与展示。
    /// <para>随课表下发；包 Apply 时用 <see cref="MergeBaseDataFrom"/> 并回本机的手动 / 已排除项。</para>
    /// </summary>
    public List<BaseDataEntry> ClassEntries { get; set; } = new();
    public List<BaseDataEntry> SubjectEntries { get; set; } = new();
    public List<BaseDataEntry> TeacherEntries { get; set; } = new();

    /// <summary>
    /// r136：整包 Apply 时把本机清单里的「手动 / 已排除」项并回包内清单。
    /// <para>与 <see cref="MergeManualFrom"/> 同理：包是整份覆盖，不并回来的话管理员手填的名单、
    /// 以及人工排除（墓碑）会被包冲掉，下次整理又把旧名字捞回来。</para>
    /// </summary>
    /// <returns>并回的项数（班级 / 科目 / 教师合计）。</returns>
    public int MergeBaseDataFrom(ScheduleFile? previous)
    {
        if (previous == null || ReferenceEquals(previous, this)) return 0;
        int kept = 0;
        MergeList(ClassEntries, previous.ClassEntries, ref kept);
        MergeList(SubjectEntries, previous.SubjectEntries, ref kept);
        MergeList(TeacherEntries, previous.TeacherEntries, ref kept);
        return kept;
    }

    private static void MergeList(List<BaseDataEntry> target, List<BaseDataEntry> source, ref int kept)
    {
        foreach (var e in source.Where(e => e.Origin != BaseDataOrigin.Auto).ToList())
        {
            var existing = target.FirstOrDefault(x =>
                string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // 本机已存在同名：手动 / 排除优先级高于包里的自动项
                if (existing.Origin == BaseDataOrigin.Auto)
                {
                    existing.Origin = e.Origin;
                    existing.Tombstoned = e.Tombstoned;
                    existing.RenamedTo = e.RenamedTo;
                    kept++;
                }
            }
            else
            {
                target.Add(e);
                kept++;
            }
        }
    }

    /// <summary>
    /// 把 <paramref name="previous"/> 里的「手动录入」班级并回本对象（同名以本对象为准）。
    /// <para>r129：自动更新 / 数据包导入是「整份覆盖」底表，不并回来的话用户手排的课表会被抹掉。
    /// 纯内存操作，不读不写文件；返回补回的班数，便于状态栏说明。</para>
    /// </summary>
    /// <param name="allow">
    /// 可选的准入判断。包同步时传「本机有课程设置」——只保留在这台机器上手排过的班，
    /// 别的机器同步过来的手动班仍以包为准（发布端删掉后教室机也会跟着消失）。
    /// </param>
    public int MergeManualFrom(ScheduleFile? previous, Func<ClassSchedule, bool>? allow = null)
    {
        if (previous == null || ReferenceEquals(previous, this)) return 0;
        int kept = 0;
        foreach (var c in previous.Classes.Where(c => c.IsManual).ToList())
        {
            if (allow != null && !allow(c)) continue;
            if (Classes.Any(x => string.Equals(x.ClassName, c.ClassName, StringComparison.OrdinalIgnoreCase))) continue;
            Classes.Add(c);
            kept++;
        }
        return kept;
    }
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

/// <summary>
/// r135：一份课表的作息时间表。从「时间表与科目设置」里选一份模板拷贝而来，
/// 节次时间（Periods）与公共时间（FixedSlots）都在这里，随课表下发。
/// </summary>
public class BellSchedule
{
    /// <summary>来源模板名，便于在窗口里认出是哪一份。</summary>
    public string Name { get; set; } = "";

    public DateTime? UpdatedAt { get; set; }

    /// <summary>哪台机器/谁设置的，出事能追。</summary>
    public string UpdatedFrom { get; set; } = "";

    /// <summary>节次时间：Index 1-8 → "08:00-08:45"。逐节回退，不整体切换。</summary>
    public List<PeriodTime> Periods { get; set; } = new();

    /// <summary>公共时间：大课间 / 眼保健操 / 午休。</summary>
    public List<FixedSlot> FixedSlots { get; set; } = new();

    public bool IsEmpty => Periods.Count == 0 && FixedSlots.Count == 0;
}
