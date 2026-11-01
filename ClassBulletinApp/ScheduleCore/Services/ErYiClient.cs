using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 二一排课数据源客户端（ScheduleCore 服务层，与 ScheduleImporter 并列）。
///
/// 主路径（推荐）：读取「浏览器抓包落盘的 JSON」目录
///   project-list.json / grade-tree-&lt;年级&gt;.json / class-lectures-&lt;年级&gt;-&lt;CID&gt;.json / substitute-list-&lt;年级&gt;.json
/// 归一化为 ScheduleCore 的 ScheduleFile（全校课表）+ List&lt;LessonOverride&gt;（今日生效的调代课）。
///
/// 兜底：Excel 导入保留在 ScheduleImporter，不在此处理。
/// 可选增强：配置 Cookie 后 FetchLive() 直连 API 实时拉取并写回缓存（默认关闭，接口需登录）。
///
/// 注意：class-lectures 的真实响应字段以已知结构编写，落盘真实样本后跑一次即可验证；
///       若字段名有差异，仅需微调下方 DTO 属性即可，不影响整体管线。
/// </summary>
public static class ErYiClient
{
    public const string DefaultBaseUrl = "https://paike.21cnjy.com/query";
    const int SlotsPerDay = 8;

    static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true };

    // ===================== 结果元数据 =====================

    /// <summary>整个二一数据集的元信息（供 UI 展示 / 固定项渲染）。</summary>
    public class ErYiMeta
    {
        public List<GradeInfo> Grades { get; set; } = new();
        public int ClassCount { get; set; }
        public int OverrideCount { get; set; }
        public string LoadedFrom { get; set; } = "";
    }

    /// <summary>单年级信息：时间表 + 固定项。</summary>
    public class GradeInfo
    {
        public string Grade { get; set; } = "";
        public string ProjectId { get; set; } = "";
        /// <summary>全局节次 period(1-based, 1-8) -> "start-end"，每日 8 节标准时段（上午1-4、下午5-8）。</summary>
        public Dictionary<int, string> PeriodTimes { get; set; } = new();
        /// <summary>固定项（大课间 / 眼保健操 / 午休），按 afterSection 排序。</summary>
        public List<FixedSection> Fixed { get; set; } = new();
    }

    /// <summary>固定项：在某 part 内第 afterSection 节（0-based）之后插入。</summary>
    public class FixedSection
    {
        public string Name { get; set; } = "";
        public int Part { get; set; }
        public int AfterSection { get; set; }
    }

    /// <summary>BuildFromCache 的返回包。</summary>
    public class ErYiResult
    {
        public ScheduleFile Schedule { get; set; } = new();
        public List<LessonOverride> Overrides { get; set; } = new();
        public ErYiMeta Meta { get; set; } = new();
    }

    // ===================== DTO（二一 JSON 结构） =====================

    private class ProjectListDto { public List<ProjectDto>? Data { get; set; } }
    private class ProjectDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<SectionDto>? NodeSectionList { get; set; }
        public List<CommonSectionDto>? CommonSectionList { get; set; }
    }
    private class SectionDto
    {
        public int Part { get; set; }
        public int Section { get; set; }
        public string? Name { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }
    private class CommonSectionDto
    {
        public string? Name { get; set; }
        public int Part { get; set; }
        public int PositionStart { get; set; }
        public int PositionEnd { get; set; }
    }

    private class GradeTreeDto { public List<GradeNodeDto>? Data { get; set; } }
    private class GradeNodeDto
    {
        public string? GradeName { get; set; }
        public List<ClassNodeDto>? List { get; set; }
    }
    private class ClassNodeDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private class LecturesDto { public List<LectureDto>? Data { get; set; } }
    private class LectureDto
    {
        public string? ClassName { get; set; }
        public int Slot { get; set; }
        public string? SubjectName { get; set; }
        public string? TeacherNames { get; set; }
        public string? RoomName { get; set; }
        public int? AlternateWeek { get; set; }  // 0=全周 1=单周 2=双周；真实数据里可能为 null（按全周处理）
        public string? Memo { get; set; }
    }

    private class SubsDto { public List<SubDto>? Data { get; set; } }
    private class SubDto
    {
        public string? ClassName { get; set; }
        public int SrcSlot { get; set; }
        public int DestSlot { get; set; }
        public string? SubjectName { get; set; }
        public string? TeacherNames { get; set; }
        public string? SubstituteTeacherNames { get; set; }
        public string? ValidStartDate { get; set; }
        public string? ValidEndDate { get; set; }
        public string? Status { get; set; }
    }

    // ===================== 主入口：从缓存目录构建 =====================

    /// <summary>
    /// 从抓包落盘的 JSON 目录构建 (ScheduleFile, 今日生效的 LessonOverride, 元数据)。
    /// 目录不存在或为空时返回空结果（调用方应回退 Excel）。
    /// </summary>
    public static ErYiResult BuildFromCache(string dir)
    {
        var result = new ErYiResult();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return result;

        var meta = result.Meta;

        // ---- 1) project-list：时间表 + 固定项（按年级）----
        var periodByGrade = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        var fixedByGrade = new Dictionary<string, List<FixedSection>>(StringComparer.Ordinal);
        var plPath = Path.Combine(dir, "project-list.json");
        if (File.Exists(plPath))
        {
            try
            {
                var pl = JsonSerializer.Deserialize<ProjectListDto>(File.ReadAllText(plPath), JsonOpt);
                if (pl?.Data != null)
                {
                    foreach (var p in pl.Data)
                    {
                        var grade = ParseGrade(p.Name ?? "");
                        var gi = new GradeInfo { Grade = grade, ProjectId = p.Id ?? "" };
                        var times = new Dictionary<int, string>();
                        if (p.NodeSectionList != null)
                            foreach (var s in p.NodeSectionList.OrderBy(x => x.Part).ThenBy(x => x.Section))
                            {
                                // 二一节点时段：part1=上午1-4节(section 0-3)，part3=下午5-8节(section 0-3)。
                                // 上下午 section 都从 0 开始，不能用 section 单独作 key（会被覆盖），
                                // 映射为全局节次 1-8：period = part==1 ? section+1 : section+5。
                                int period = s.Part == 1 ? s.Section + 1 : s.Section + 5;
                                times[period] = $"{s.StartTime}-{s.EndTime}";
                            }
                        gi.PeriodTimes = times;
                        var fx = new List<FixedSection>();
                        var slots = new List<FixedSlot>();
                        if (p.CommonSectionList != null)
                            foreach (var c in p.CommonSectionList)
                            {
                                fx.Add(new FixedSection { Name = c.Name ?? "", Part = c.Part, AfterSection = c.PositionStart });

                                // 固定项（大课间/眼保健操/午休）不占课节序号，靠 part + positionStart 定位：
                                // part1=上午（第1-4节）、part3=下午（第5-8节），positionStart = 该 part 内第 K 节之后（0-based），
                                // 换算成全局节次 after = K+1（上午）/ K+5（下午）。
                                int after = c.Part == 3 ? c.PositionStart + 5 : c.PositionStart + 1;
                                slots.Add(new FixedSlot
                                {
                                    Name = (c.Name ?? "").Trim(),
                                    AfterPeriod = after,
                                    TimeRange = GapTime(times, after)   // 用前后两节的间隙推出时间区间
                                });
                            }
                        gi.Fixed = fx;
                        meta.Grades.Add(gi);
                        periodByGrade[grade] = times;
                        fixedByGrade[grade] = fx;

                        // 固定时段随 ScheduleFile 一起持久化，供 BoardBuilder 在渲染当天课表时插入
                        if (!string.IsNullOrEmpty(grade) && slots.Any(x => !string.IsNullOrEmpty(x.Name)))
                            result.Schedule.FixedByGrade[grade] = slots
                                .Where(x => !string.IsNullOrEmpty(x.Name))
                                .OrderBy(x => x.AfterPeriod)
                                .ToList();
                    }
                }
            }
            catch { /* 损坏的 project-list 不致命，时间表/固定项缺失而已 */ }
        }

        // ---- 2) class-lectures-*：合并全校所有班到同一 ScheduleFile ----
        foreach (var f in Directory.GetFiles(dir, "class-lectures-*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            List<LectureDto>? lectures = null;
            try
            {
                var lec = JsonSerializer.Deserialize<LecturesDto>(File.ReadAllText(f), JsonOpt);
                lectures = lec?.Data;
            }
            catch { continue; }
            if (lectures == null) continue;

            foreach (var l in lectures)
            {
                if (string.IsNullOrWhiteSpace(l.ClassName)) continue;
                int day = l.Slot / SlotsPerDay;
                int period = l.Slot % SlotsPerDay + 1;   // 1-based 节次

                var cs = result.Schedule.Classes.FirstOrDefault(x => x.ClassName == l.ClassName);
                if (cs == null)
                {
                    cs = new ClassSchedule { ClassName = l.ClassName! };
                    result.Schedule.Classes.Add(cs);
                }
                if (!cs.Data.ContainsKey(day)) cs.Data[day] = new Dictionary<int, List<Lesson>>();
                if (!cs.Data[day].ContainsKey(period)) cs.Data[day][period] = new List<Lesson>();

                var subj = (l.SubjectName ?? "").Trim();
                if (string.IsNullOrEmpty(subj)) subj = (l.Memo ?? "").Trim();
                if (string.IsNullOrEmpty(subj)) subj = "（空）";

                // 时间表：取该班所在年级的全局节次(1-8)对应时段，注入 Lesson.TimeRange 支撑当前课高亮
                var grade = ParseGrade(l.ClassName);
                periodByGrade.TryGetValue(grade, out var times);
                string time = "";
                if (times != null && times.TryGetValue(l.Slot % SlotsPerDay + 1, out var t)) time = t;

                cs.Data[day][period].Add(new Lesson
                {
                    Period = period,
                    Subject = subj,
                    Teacher = l.TeacherNames ?? "",
                    Location = l.RoomName ?? "",
                    Week = l.AlternateWeek ?? 0,   // 0/1/2 与现有单双周一致；null 视为全周
                    TimeRange = time
                });
            }
        }

        // ---- 3) substitute-list-*：调代课（Source=temp，带服务端生效区间）----
        foreach (var f in Directory.GetFiles(dir, "substitute-list-*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            List<SubDto>? subs = null;
            try
            {
                var sd = JsonSerializer.Deserialize<SubsDto>(File.ReadAllText(f), JsonOpt);
                subs = sd?.Data;
            }
            catch { continue; }
            if (subs == null) continue;

            foreach (var s in subs)
            {
                if (string.IsNullOrWhiteSpace(s.ClassName)) continue;
                bool hasSub = !string.IsNullOrWhiteSpace(s.SubstituteTeacherNames);
                bool moved = s.SrcSlot != s.DestSlot;
                if (!hasSub && !moved) continue;                 // 无实际变动
                if (s.Status == "已失效") continue;               // 服务端历史，跳过
                if (string.IsNullOrWhiteSpace(s.ValidStartDate) || string.IsNullOrWhiteSpace(s.ValidEndDate)) continue;
                // ★ r8：不再按"今天"过滤（那会让周末打开的整周调代课全部消失，见设计 §3.2/D5）。
                //   这里把服务端的 validStart~validEnd 原样写进记录的生效区间，
                //   由 OverrideExpander 按"显示日期"求交决定这一格今天/这一周是否生效。
                if (!DateOnly.TryParse(s.ValidStartDate, out var vFrom)) continue;
                if (!DateOnly.TryParse(s.ValidEndDate, out var vTo)) continue;
                if (vTo < vFrom) continue;

                // 科目：代课记录的 subjectName 常写成"代课"占位（真实数据里确有），
                // 此时从该班原课表的 srcSlot 反查真实科目，避免界面显示"代课"。
                string subject = (s.SubjectName ?? "").Trim();
                if (string.IsNullOrEmpty(subject) || subject == "代课")
                {
                    var ownCs = result.Schedule.Classes.FirstOrDefault(x => x.ClassName == s.ClassName);
                    int sDay0 = s.SrcSlot / SlotsPerDay;
                    int sPeriod0 = s.SrcSlot % SlotsPerDay + 1;
                    if (ownCs != null && ownCs.Data.TryGetValue(sDay0, out var sdm) && sdm.TryGetValue(sPeriod0, out var sls))
                    {
                        var orig = sls.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Subject) && l.Subject != "代课");
                        if (orig != null) subject = orig.Subject;
                    }
                    if (string.IsNullOrEmpty(subject)) subject = "代课";
                }

                int dDay = s.DestSlot / SlotsPerDay;
                int dPeriod = s.DestSlot % SlotsPerDay + 1;
                result.Overrides.Add(new LessonOverride
                {
                    ClassName = s.ClassName!,
                    DayIdx = dDay,
                    Period = dPeriod,
                    Subject = subject,
                    Teacher = hasSub ? s.SubstituteTeacherNames! : (s.TeacherNames ?? ""),
                    Location = "",
                    Type = hasSub ? "代" : "调",
                    IsEmpty = false,
                    Source = "temp",
                    ValidFrom = vFrom,
                    ValidTo = vTo,
                    Note = hasSub ? "代课" : "临时调课",
                    UpdatedAt = DateTime.Now
                });
            }
        }

        // 调代覆盖去重（跨多份 substitute-list 或重抓时防止重复）
        result.Overrides = result.Overrides
            .GroupBy(o => o.Key, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        meta.ClassCount = result.Schedule.Classes.Count;
        meta.OverrideCount = result.Overrides.Count;
        meta.LoadedFrom = dir;
        result.Schedule.SourceFile = "ErYiCache:" + dir;
        result.Schedule.ImportedAt = DateTime.Now;
        return result;
    }

    // ===================== 可选：实时拉取（配置门槛，默认关闭） =====================

    /// <summary>
    /// 实时拉取并写回缓存目录。需登录态 Cookie，接口脆弱且 ToS 偏灰，默认不启用。
    /// 目前 project-list 端点 URL 尚未确认，先留结构化桩；确认后填入实现即可。
    /// 调用方应捕获异常并回退到缓存 / Excel。
    /// </summary>
    public static void FetchLive(string baseUrl, string cookie, string dir)
    {
        // 参考 batch_download_classes.js 的循环：project-list → grade-tree → class-lectures → substitute-list
        // 用 HttpClient 注入 cookie 请求三接口，写盘成与浏览器抓包一致的文件名。
        throw new NotImplementedException(
            "实时拉取为可选增强：project-list 端点 URL 待确认，且需登录态 Cookie。当前请使用「浏览器抓包落盘」主路径。");
    }

    // ===================== 工具 =====================

    /// <summary>从班级名反查年级（七/八/九年级）。</summary>
    public static string ParseGrade(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        if (name.Contains("七年级")) return "七年级";
        if (name.Contains("八年级")) return "八年级";
        if (name.Contains("九年级")) return "九年级";
        return "";
    }

    /// <summary>
    /// 推算固定时段的真实时间区间：取「第 afterPeriod 节的结束时间」到「下一节的开始时间」。
    /// 例如大课间在第1节后：08:40-09:10；午休在第4节后：11:35-14:00。缺数据时返回空串。
    /// </summary>
    static string GapTime(Dictionary<int, string> times, int afterPeriod)
    {
        if (times == null || times.Count == 0) return "";
        if (!times.TryGetValue(afterPeriod, out var cur)) return "";
        if (!times.TryGetValue(afterPeriod + 1, out var nxt)) return "";
        var a = cur.Split('-');
        var b = nxt.Split('-');
        if (a.Length != 2 || b.Length != 2) return "";
        return $"{a[1].Trim()}-{b[0].Trim()}";
    }
}
