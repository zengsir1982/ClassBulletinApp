using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 「这一格最终显示什么」的唯一入口：把覆盖记录按<b>显示日期</b>展开。
///
/// 存在理由：r8 起每条覆盖都带生效区间（<see cref="LessonOverride.ValidFrom"/> /
/// <see cref="LessonOverride.ValidTo"/>），不能再像以前那样"有记录就套上去"——
/// 那样上周的调代课会永久顶在本周课表上。
///
/// 约定：
/// - 展示层（悬浮课表 / 周视图 / 桌面背面 / 打印）一律先经过这里，不许各自另算；
/// - 同一格同一日出现多条时，先按<b>来源优先级</b>取（手动 / 长期改课 &gt; 远程 &gt; 导入临时表），
///   同级再取 <see cref="LessonOverride.UpdatedAt"/> 更新的一条。
///   ★ 来源优先级是显式的，不再依赖"手动总是后写所以 UpdatedAt 更新"这个脆弱假设——
///   否则"被同步软覆盖 → 恢复（取消停用）→ 再手改"这条链路会因为 temp 的 UpdatedAt 比
///   原 manual 新，导致恢复/再改都看不见（见 r14 修复）。
/// - 没有区间的旧记录视为长期有效（迁移兜底，避免旧数据凭空消失）；
/// - <b>被主机同步停用</b>的记录（<see cref="LessonOverride.Suspended"/>）不参与显示，
///   但仍留在 overrides.json 里，可由管理员一键恢复（恢复后来源优先级保证它压过 temp）。
/// </summary>
public static class OverrideExpander
{
    /// <summary>"班级|星期|节次" 三元组键（不含生效区间），用于同一日期内定位格子。</summary>
    public static string CellKey(string className, int dayIdx, int period)
        => $"{className}|{dayIdx}|{period}";

    /// <summary>某班在指定日期生效的覆盖，按「班级|星期|节次」归并为字典。</summary>
    public static Dictionary<string, LessonOverride> MapForDate(
        IEnumerable<LessonOverride>? all, string? className, DateTime date)
    {
        var map = new Dictionary<string, LessonOverride>(StringComparer.Ordinal);
        if (all == null || string.IsNullOrEmpty(className)) return map;

        foreach (var o in all)
        {
            if (o == null) continue;
            if (!string.Equals(o.ClassName, className, StringComparison.Ordinal)) continue;
            if (!o.IsActiveOn(date)) continue;

            var key = o.CellKey;
            if (!map.TryGetValue(key, out var prev) || Better(o, prev))
                map[key] = o;
        }
        return map;
    }

    /// <summary>某班在指定日期生效的全部覆盖（未归并，用于"该日无底表、逐节列出"的分支）。</summary>
    public static List<LessonOverride> ForDate(
        IEnumerable<LessonOverride>? all, string? className, DateTime date)
        => (all ?? Enumerable.Empty<LessonOverride>())
           .Where(o => o != null
                       && string.Equals(o.ClassName, className, StringComparison.Ordinal)
                       && o.IsActiveOn(date))
           .ToList();

    /// <summary>
    /// 某格在某周（起始日 ~ +6 天，含首尾）内有交集的记录。
    /// 用于"清除本周该格""改本周该格"时的定位——不能再用拼接字符串比 Key，
    /// 那会把其他周同一格的记录一起删掉。
    /// </summary>
    public static List<LessonOverride> ForCellInWeek(
        IEnumerable<LessonOverride>? all, string? className, int dayIdx, int period, DateTime weekStart)
    {
        var (start, end) = WeekHelper.WeekWindowOf(weekStart);
        return (all ?? Enumerable.Empty<LessonOverride>())
               .Where(o => o != null
                           && string.Equals(o.ClassName, className, StringComparison.Ordinal)
                           && o.DayIdx == dayIdx && o.Period == period
                           && o.OverlapsRange(start, end))
               .ToList();
    }

    /// <summary>某班在某周内有交集的全部记录（"清除本周"用）。</summary>
    public static List<LessonOverride> ForClassInWeek(
        IEnumerable<LessonOverride>? all, string? className, DateTime weekStart)
    {
        var (start, end) = WeekHelper.WeekWindowOf(weekStart);
        return (all ?? Enumerable.Empty<LessonOverride>())
               .Where(o => o != null
                           && string.Equals(o.ClassName, className, StringComparison.Ordinal)
                           && o.OverlapsRange(start, end))
               .ToList();
    }

    /// <summary>
    /// 被「数据源覆盖手工调课」策略停用的记录（可指定班级，null = 全部）。
    /// 给「恢复被主机停用的本机调课」界面用。
    /// <para>★ 显示路径（<see cref="MapForDate"/> / <see cref="ForDate"/>）已经通过
    /// <see cref="LessonOverride.IsActiveOn"/> 自动跳过停用记录，这里只是把它们"捞出来"供恢复。</para>
    /// </summary>
    public static List<LessonOverride> SuspendedFor(
        IEnumerable<LessonOverride>? all, string? className = null)
        => (all ?? Enumerable.Empty<LessonOverride>())
           .Where(o => o != null
                       && o.Suspended
                       && (string.IsNullOrEmpty(className)
                           || string.Equals(o.ClassName, className, StringComparison.Ordinal)))
           .OrderBy(o => o.ClassName, StringComparer.Ordinal)
           .ThenBy(o => o.DayIdx).ThenBy(o => o.Period)
           .ToList();

    /// <summary>
    /// 来源优先级：手动改课 / 长期改课（改的是底表本身，属本机长期决策）最高，远程其次，导入临时表最低。
    /// <para>★ 这是"本机老师的手工安排应当压过主机下发的临时课表"的显式实现，
    /// 不再靠 UpdatedAt 谁更新来碰运气（否则恢复 / 再手改会被更晚同步来的 temp 盖住）。</para>
    /// </summary>
    static int SourceRank(LessonOverride o) => o.Source switch
    {
        "manual" or "basePatch" => 3,
        "remote" => 2,
        "temp" => 1,
        _ => 0
    };

    /// <summary>两记录争同一格时取更优者：先比来源优先级，同级再比 <see cref="IsNewer"/>。</summary>
    static bool Better(LessonOverride candidate, LessonOverride current)
    {
        int rc = SourceRank(candidate), rr = SourceRank(current);
        if (rc != rr) return rc > rr;
        return IsNewer(candidate, current);
    }

    static bool IsNewer(LessonOverride a, LessonOverride b)
    {
        if (a.UpdatedAt != b.UpdatedAt) return a.UpdatedAt > b.UpdatedAt;
        // 同一时刻（批量导入）时，按生效起始日靠后的算"更新"
        return (a.ValidFrom ?? DateOnly.MinValue) >= (b.ValidFrom ?? DateOnly.MinValue);
    }
}
