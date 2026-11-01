using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 发布机：把 <c>source/</c> 目录里的 Excel 重新解析、打包成数据包，上传到 <c>publish/</c>。
///
/// <para><b>约定</b>（贴在共享盘/FTP 上就要这么放）：</para>
/// <code>
/// source/七年级-总课表.xlsx          文件名不含"临时" = 基础课表，按班级名自动合并
/// source/八年级-总课表.xlsx
/// source/九年级-总课表.xlsx
/// source/2026-10-26_临时课表_八年级.xlsx   文件名以 yyyy-MM-dd 开头 = 该日期起 7 天内生效的调代课
/// </code>
///
/// <para><b>为什么文件名必须带日期</b>：临时课表的 Excel 里没有任何"这是哪一周"的信息，
/// 教务每周覆盖同一个文件名。不靠文件名带日期就只能猜"当前周"，
/// 于是"周五上传下周的"会被当成本周、下周反而没有调代课。</para>
///
/// <para>本类<b>不改本机数据</b>，只做"读 Excel → 生成数据包 → 上传"。</para>
/// </summary>
public static class SourcePublisher
{
    public class Result
    {
        public bool Ok { get; set; }
        public bool Skipped { get; set; }

        /// <summary>发布出去的数据包整包摘要（写入本机 LastAppliedSha，避免自己再从数据源下回来一遍）。</summary>
        public string DataSha { get; set; } = "";
        public string Message { get; set; } = "";
        public int BaseFiles { get; set; }
        public int TempFiles { get; set; }
        public int ClassCount { get; set; }
        public int OverrideCount { get; set; }

        /// <summary>本次包里"完整描述"的周（yyyy-MM-dd 周起始日），这些周内未标记调代的格子会回到底表。</summary>
        public List<string> CoveredWeeks { get; set; } = new();

        /// <summary>source 里没有总课表，底表退回了本机现有课表。</summary>
        public bool BaseFromLocal { get; set; }

        /// <summary>覆盖里有、底表里没有的班级数 / 条数（这些调代课在教室机上永远显示不出来）。</summary>
        public int OrphanClasses { get; set; }
        public int OrphanOverrides { get; set; }

        /// <summary>本次发布是否顺带把本机数据也同步成了这个版本。</summary>
        public bool LocalApplied { get; set; }

        /// <summary>本机本来就是这个版本，无需再次落地。</summary>
        public bool LocalSkipped { get; set; }
        public int LocalClassCount { get; set; }
        public int LocalOverrideCount { get; set; }
        public string LocalBackupDir { get; set; } = "";
        public string LocalError { get; set; } = "";

        /// <summary>需要提醒用户的问题（不影响发布成功，但不处理会留坑）。</summary>
        public List<string> Warnings { get; set; } = new();
    }

    static readonly Regex DatePrefixRe = new(@"(\d{4})-(\d{1,2})-(\d{1,2})");

    /// <summary>发布前预检结果（只扫文件名，不解析 Excel、不上传）。</summary>
    public class Precheck
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public List<string> BaseFiles { get; set; } = new();
        public List<string> TempFiles { get; set; } = new();
        public List<string> BadTempFiles { get; set; } = new();
        public List<DateTime> TempWeeks { get; set; } = new();

        /// <summary>source 里没有总课表，底表将退回本机现有课表。</summary>
        public bool BaseFromLocal { get; set; }
        public int LocalClassCount { get; set; }

        /// <summary>是否需要在人点"确定"之前就把问题摆出来。</summary>
        public bool HasRisk => BaseFromLocal || BadTempFiles.Count > 0;

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();

            if (BaseFiles.Count > 0)
            {
                sb.AppendLine($"总课表（底表）：{BaseFiles.Count} 份");
                foreach (var b in BaseFiles.Take(6)) sb.AppendLine("    · " + b);
            }
            else
            {
                sb.AppendLine($"总课表（底表）：⚠ 一份都没有，将退回本机现有课表（{LocalClassCount} 个班）");
            }

            if (TempFiles.Count > 0)
            {
                sb.AppendLine($"临时课表：{TempFiles.Count} 份");
                for (int i = 0; i < TempFiles.Count && i < 6; i++)
                {
                    string range = i < TempWeeks.Count
                        ? $"　→ {TempWeeks[i]:MM-dd} ~ {TempWeeks[i].AddDays(6):MM-dd}"
                        : "";
                    sb.AppendLine("    · " + TempFiles[i] + range);
                }
                sb.AppendLine("    文件名里的日期按「所在周」对齐：写周三也会算进本周，不会溢到下周。");
                sb.AppendLine("    临时课表按整周快照处理：这些周内没标 调/代 的格子会还原成底表原课程（= 取消调代）。");
                sb.AppendLine("    同步后以数据源为准：source 里没有临时课表的那几周，各机上的旧调代标记也会一并取消。");
            }
            else
            {
                sb.AppendLine("临时课表：没有（将沿用本机现有调代课）");
            }

            if (BadTempFiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 以下临时课表文件名没有以日期开头，无法判断生效周，发布会被中止：");
                foreach (var b in BadTempFiles) sb.AppendLine("    · " + b);
                sb.AppendLine("    正确写法：2026-11-02_临时课表_八年级.xlsx");
            }

            if (BaseFromLocal)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ source 里没有总课表，底表只能用本机这份。");
                sb.AppendLine("  如果本机只导入过某个年级，其他年级的课表和调代课都发不出去。");
                sb.AppendLine("  建议把七/八/九年级「全校班级总课表」也放进 source 目录。");
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>发布前预检：只扫文件名与目录，不解析 Excel、不上传。</summary>
    public static Precheck Scan(string sourceDir)
    {
        var p = new Precheck();
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            p.Message = "发布源目录不存在：" + sourceDir;
            return p;
        }

        int weekStartDay = WeekHelper.NormalizeStartDay(Store.LoadSettings().WeekStartDay);

        foreach (var f in Directory.GetFiles(sourceDir, "*.xlsx")
                                   .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                                   .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            if (name.Contains("临时"))
            {
                var d = ParseDatePrefix(name);
                if (d.HasValue)
                {
                    p.TempFiles.Add(name);
                    p.TempWeeks.Add(WeekHelper.WeekStart(d.Value, weekStartDay).Date);   // 与发布时同一套对齐
                }
                else p.BadTempFiles.Add(name);
            }
            else
            {
                p.BaseFiles.Add(name);
            }
        }

        if (p.BaseFiles.Count == 0)
        {
            p.BaseFromLocal = true;
            p.LocalClassCount = Store.LoadSchedule()?.Classes.Count ?? 0;
        }

        if (p.BaseFiles.Count == 0 && p.TempFiles.Count == 0)
        {
            p.Message = "source 目录里没有可用的 .xlsx（总课表或临时课表）。";
            return p;
        }

        p.Ok = true;
        return p;
    }

    /// <summary>
    /// 发布数据包。
    /// </summary>
    /// <param name="applyToLocal">
    /// true = 上传成功后<b>顺带把这份包应用到本机</b>。发布机自己也必须是最新的：
    /// 以前只上传不落地，而界面又把 LastAppliedSha 记成了新包摘要，于是发布机永远显示
    /// "已是最新"、自己反而看不到调代课（2026-10-31 实测复现）。
    /// </param>
    public static Result Publish(IFeedSource src, string sourceDir, string builtBy, bool applyToLocal = true)
    {
        var r = new Result();

        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            r.Message = "发布源目录不存在：" + sourceDir;
            return r;
        }

        string tmpPkg = Path.Combine(Path.GetTempPath(),
            "cbsource-" + Guid.NewGuid().ToString("N")[..8] + ".cbpack");

        try
        {
            // ---------- 1) 扫文件 ----------
            var all = Directory.GetFiles(sourceDir, "*.xlsx")
                               .Where(f => !Path.GetFileName(f).StartsWith("~$"))   // Excel 锁文件
                               .OrderBy(f => f, StringComparer.Ordinal)
                               .ToList();

            if (all.Count == 0)
            {
                r.Message = "source 目录里没有 .xlsx 文件。";
                return r;
            }

            var baseFiles = new List<string>();
            var tempFiles = new List<(string Path, DateTime WeekStart)>();
            var noDate = new List<string>();

            // 临时课表的文件名日期一律对齐到「那一周的起始日」。
            // 教务改本周的临时表时习惯写当天（比如周三 10-29），若按"该日期起 7 天"算，
            // 区间会变成 10-29~11-04：既漏掉本周前三天，又吃掉下周前三天。
            int weekStartDay = WeekHelper.NormalizeStartDay(Store.LoadSettings().WeekStartDay);

            foreach (var f in all)
            {
                var name = Path.GetFileName(f);
                bool isTemp = name.Contains("临时");

                if (isTemp)
                {
                    var d = ParseDatePrefix(name);
                    if (d.HasValue) tempFiles.Add((f, WeekHelper.WeekStart(d.Value, weekStartDay).Date));
                    else noDate.Add(name);
                }
                else
                {
                    baseFiles.Add(f);
                }
            }

            if (noDate.Count > 0)
            {
                r.Message = "以下临时课表文件名没有以日期开头，无法判断生效周，已跳过：" +
                            string.Join("、", noDate) +
                            "　正确写法示例：2026-10-26_临时课表_八年级.xlsx";
                return r;
            }

            if (baseFiles.Count == 0 && tempFiles.Count == 0)
            {
                r.Message = "source 目录里没有可识别的总课表或临时课表。";
                return r;
            }

            // ---------- 2) 底表：source 里的总课表按班合并；没有则用本机现有底表 ----------
            // ★ 同样的 Excel 必须产出同样的数据包：包里所有时间戳一律取「源文件最后写入时间」，
            //   绝不能用 DateTime.Now。否则 DataSha256 每次发布都不一样，30 台教室机会天天判成
            //   "有新版本"而重下重装（还会每次生成一份自动备份）。
            DateTime stamp = baseFiles.Concat(tempFiles.Select(t => t.Path))
                                      .Select(File.GetLastWriteTime)
                                      .DefaultIfEmpty(DateTime.Now)
                                      .Max();

            ScheduleFile? merged = null;
            if (baseFiles.Count > 0)
            {
                merged = new ScheduleFile();
                foreach (var f in baseFiles)
                {
                    ScheduleFile sf;
                    try { sf = ScheduleImporter.Import(f); }
                    catch (Exception ex)
                    {
                        r.Message = $"解析「{Path.GetFileName(f)}」失败：{ex.Message}";
                        return r;
                    }
                    if (sf.Classes.Count == 0)
                    {
                        r.Message = $"「{Path.GetFileName(f)}」里没有识别到班级，可能不是全校总课程表。";
                        return r;
                    }
                    foreach (var c in sf.Classes)
                    {
                        merged.Classes.RemoveAll(x => x.ClassName == c.ClassName);
                        merged.Classes.Add(c);
                    }
                }
                ScheduleImporter.SortClasses(merged);
                merged.SourceFile = $"source 合并 {baseFiles.Count} 份";
                merged.SourceFiles = baseFiles.Select(Path.GetFileName).ToList()!;
                merged.ImportedAt = stamp;
            }
            else
            {
                merged = Store.LoadSchedule();
                r.BaseFromLocal = true;
            }

            if (merged == null || merged.Classes.Count == 0)
            {
                r.Message = "既没有底表可用（source 里没有总课表，本机也没有导入过）。";
                return r;
            }

            // ---------- 3) 临时课表：对齐后的周起始日 ~ +6 天 ----------
            var overrides = new List<LessonOverride>();
            foreach (var (path, weekStart) in tempFiles.OrderBy(x => x.WeekStart))
            {
                try
                {
                    var (ov, _) = ScheduleImporter.ImportTempOverrides(path, weekStart, weekStart.AddDays(6));
                    // 每条记录的时间戳用「它自己那份文件」的最后写入时间：
                    // ① 同样的 Excel 必产出同样的包（不能出现 DateTime.Now，否则摘要每次都变）；
                    // ② 同一周里后改的那份，UpdatedAt 自然更新，重叠时按"新的胜出"生效。
                    DateTime mt = File.GetLastWriteTime(path);
                    foreach (var o in ov) o.UpdatedAt = mt;
                    overrides.AddRange(ov);
                }
                catch (Exception ex)
                {
                    r.Message = $"解析「{Path.GetFileName(path)}」失败：{ex.Message}";
                    return r;
                }
            }

            // 长期改课（basePatch）来自本机，不在 source 里
            var localBase = Store.LoadOverrides().Where(o => o.Source == "basePatch").ToList();
            overrides.AddRange(localBase);

            // 若 source 完全没有临时课表，沿用本机现有的调代课（避免"只放总课表就把调代课全清空"）
            bool fallback = false;
            if (tempFiles.Count == 0)
            {
                var keep = Store.LoadOverrides().Where(o => o.Source is "temp" or "remote").ToList();
                overrides.AddRange(keep);
                fallback = true;
            }

            // 同一格同一周可能被两份临时表都提到（比如本周表后来改过一次）。
            // 按 Key 去重、保留 UpdatedAt 最新的那条 —— 包内不该出现同 Key 的两条记录，
            // 否则 manifest 报的条数和教室机实际生效的条数对不上。
            overrides = overrides
                .GroupBy(o => o.Key, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(o => o.UpdatedAt).First())
                .OrderBy(o => o.ClassName, StringComparer.Ordinal)
                .ThenBy(o => o.DayIdx).ThenBy(o => o.Period)
                .ToList();

            // 覆盖里有、底表里没有的班级：这些调代课写进包也永远落不到格子上
            // （典型场景：source 只放了临时课表、没放总课表，底表退回本机只含八年级的旧表）
            var baseClassSet = merged.Classes.Select(c => c.ClassName).ToHashSet(StringComparer.Ordinal);
            var orphans = overrides.Where(o => !baseClassSet.Contains(o.ClassName)).ToList();
            r.OrphanClasses = orphans.Select(o => o.ClassName).Distinct().Count();
            r.OrphanOverrides = orphans.Count;

            if (r.BaseFromLocal)
            {
                r.Warnings.Add(
                    $"source 目录里没有全校总课表，底表退回了本机现有的 {merged.Classes.Count} 个班。" +
                    "请把七/八/九年级「全校班级总课表」也放进 source 目录，否则其他年级的课表发不出去。");
            }
            if (r.OrphanOverrides > 0)
            {
                var names = orphans.Select(o => o.ClassName).Distinct().OrderBy(s => s, StringComparer.Ordinal);
                r.Warnings.Add(
                    $"有 {r.OrphanOverrides} 条调代课属于底表里没有的 {r.OrphanClasses} 个班" +
                    $"（{string.Join("、", names.Take(6))}{(r.OrphanClasses > 6 ? " 等" : "")}），" +
                    "这些记录在教室机上永远不会显示。多半是底表缺了对应年级。");
            }

            // ---------- 4) 打包 ----------
            // ★ 包要声明"这几周的调代课完全由我说了算"：临时课表是整周快照，
            //   某一周只要有临时课表，该周内**没被标记 调/代 的格子就必须回到底表原课程**。
            //   以前只按"恰好有改动的那几天"给范围，于是取消了调代的格子在各机上一直挂着旧记录，
            //   看起来像"调代课取消不掉"（2026-11-01 现场反馈）。
            DateOnly? clearFrom = null, clearTo = null;
            if (tempFiles.Count > 0)
            {
                var weeks = tempFiles.Select(t => t.WeekStart.Date).Distinct().OrderBy(d => d).ToList();
                clearFrom = DateOnly.FromDateTime(weeks.Min());
                clearTo = DateOnly.FromDateTime(weeks.Max().AddDays(6));
                r.CoveredWeeks = weeks.Select(d => d.ToString("yyyy-MM-dd")).ToList();
            }

            var notices = Store.LoadNotifications();
            // ★ overridesAuthoritative：source 里有临时课表时，本包就是"调代课的唯一真相"。
            //   教室机导入后清掉本机全部 temp/remote —— 包括 source 里已经没有临时课表的那些周，
            //   那些周上的旧调代标记必须取消、还原成底表原课程（2026-11-01 现场反馈）。
            bool authoritative = tempFiles.Count > 0;

            // ---------- 3.9) 内容版本号 Rev ----------
            // ★ 策略开关（是否覆盖手工调课 / 是否权威）只存在 manifest 里，不改任何文件。
            //   如果摘要只由文件算出，就会出现"改了策略但摘要不变 → 各机认为已是最新 → 永远不 Apply"。
            //   所以策略一旦与远端旧包不同，就把 Rev 自增，摘要随之变化，强制各机重新拉一次。
            var setNow = Store.LoadSettings();
            bool includeManual = setNow.OverwriteManualOverrides;

            int rev = setNow.PackageRev;
            string? remoteJsonEarly = null;
            try { remoteJsonEarly = src.ReadManifest(); } catch { /* 连不上就按本地 Rev 走 */ }

            var oldManifest = SchedulePackage.ParseManifest(remoteJsonEarly);
            if (oldManifest != null)
            {
                rev = Math.Max(rev, oldManifest.Rev);
                bool policyChanged = oldManifest.OverrideIncludeManual != includeManual
                                  || oldManifest.OverrideAuthoritative != authoritative;
                if (policyChanged) rev = Math.Max(rev + 1, oldManifest.Rev + 1);
            }
            if (rev != setNow.PackageRev)
            {
                setNow.PackageRev = rev;
                Store.SaveSettings(setNow);
            }

            var exp = SchedulePackage.Export(tmpPkg, merged, overrides, notices,
                setNow, string.IsNullOrWhiteSpace(builtBy) ? Environment.MachineName : builtBy,
                clearFrom, clearTo,
                overridesAuthoritative: authoritative,
                includeManualOverrides: includeManual,
                rev: rev,
                users: Store.LoadUsers().Users);
            if (!exp.Ok)
            {
                r.Message = "打包失败：" + exp.Error;
                return r;
            }

            var manifestJson = ReadManifest(tmpPkg);
            var localSha = SchedulePackage.ReadDataSha(manifestJson);
            r.DataSha = localSha;

            // 把这份包应用到本机。发布机自己也必须是最新的，否则"发布成功但本机什么都没有"。
            void ApplyToLocal()
            {
                if (!applyToLocal) return;
                try
                {
                    // 本机已经是这个版本（且调代条数一致）就不重复落地，省一份备份
                    var cur = Store.LoadSettings();
                    int curOv = Store.LoadOverrides().Count(o => o.Source is "temp" or "remote" or "basePatch");
                    if (string.Equals(cur.LastAppliedSha, localSha, StringComparison.OrdinalIgnoreCase)
                        && curOv == exp.OverrideCount)
                    {
                        r.LocalSkipped = true;
                        return;
                    }

                    var ins = SchedulePackage.Inspect(tmpPkg);
                    if (!ins.Ok) { r.LocalError = "本机同步失败：" + ins.Error; return; }

                    var ap = SchedulePackage.Apply(ins);
                    r.LocalApplied = ap.Ok;
                    r.LocalClassCount = ap.ClassCount;
                    r.LocalOverrideCount = ap.OverrideAdded;
                    r.LocalBackupDir = ap.BackupDir;
                    if (!ap.Ok) r.LocalError = "本机同步失败：" + ap.Error;
                    foreach (var w in ap.Warnings) r.Warnings.Add(w);
                }
                catch (Exception ex)
                {
                    r.LocalError = "本机同步失败：" + ex.Message;
                }
            }

            // 发布成功即代表"远端就是这个版本"，记下来避免自己再下一遍。
            // 本机同步失败时**不记**，让下次自动检查重新拉一遍这个包来自愈。
            void RecordReleased()
            {
                if (!r.Ok || !string.IsNullOrEmpty(r.LocalError)) return;
                var st2 = Store.LoadSettings();
                st2.LastAppliedSha = localSha;
                st2.LastAppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                st2.LastUpdateCheckInfo = (r.Skipped ? "数据源已是最新（本次未上传）" : "本机已发布新版本")
                    + (r.LocalApplied ? $"，本机已同步 {r.LocalClassCount} 个班" : "");
                Store.SaveSettings(st2);
            }

            // ---------- 5) 与远端比对：已经一样就不重复上传 ----------
            // 复用打包前读到的那份远端清单，不再多打一次网络往返（FTP 一次就是 8 秒超时）。
            string? remoteJson = remoteJsonEarly;

            if (remoteJson != null && string.Equals(SchedulePackage.ReadDataSha(remoteJson), localSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                r.Ok = true;
                r.Skipped = true;
                r.Message = "远端已是最新（内容与本机算出的完全一致），无需重复上传。";
                r.ClassCount = exp.ClassCount;
                r.OverrideCount = exp.OverrideCount;
                ApplyToLocal();     // ★ 远端有新版本不代表本机有：发布机就是靠这一步追上来的
                RecordReleased();
                return r;
            }

            src.Upload(tmpPkg, manifestJson);

            r.Ok = true;
            r.BaseFiles = baseFiles.Count;
            r.TempFiles = tempFiles.Count;
            r.ClassCount = exp.ClassCount;
            r.OverrideCount = exp.OverrideCount;
            r.Message = $"已发布：底表 {(baseFiles.Count > 0 ? baseFiles.Count + " 份" : "沿用本机")} → {exp.ClassCount} 个班，"
                      + $"调代 {exp.OverrideCount} 条({exp.OverrideRange})"
                      + (tempFiles.Count > 0 ? $"，临时课表 {tempFiles.Count} 份（{r.CoveredWeeks.Count} 周）" : "")
                      + (tempFiles.Count > 0
                            ? "；这些周内未标记 调/代 的格子已还原为底表原课程，其余周（source 里没有临时课表的）也一并取消旧调代"
                            : "")
                      + (fallback ? "　⚠ source 里没有临时课表，已沿用本机现有调代课" : "");

            ApplyToLocal();
            if (r.LocalApplied)
                r.Message += $"；本机已同步（{r.LocalClassCount} 个班、调代 {r.LocalOverrideCount} 条）";
            RecordReleased();
            return r;
        }
        catch (Exception ex2)
        {
            r.Message = "发布失败：" + ex2.Message;
            return r;
        }
        finally
        {
            try { if (File.Exists(tmpPkg)) File.Delete(tmpPkg); } catch { }
        }
    }

    /// <summary>
    /// source 目录的"指纹"：文件名 + 大小 + 最后写入时间的 SHA-256 前 16 位。
    /// 用来判断"Excel 有没有变过"，不解析文件内容，几十毫秒就能算完。
    /// 返回空串表示目录不存在或没有 xlsx。
    /// </summary>
    public static string SourceFingerprint(string sourceDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return "";

        var sb = new System.Text.StringBuilder();
        foreach (var f in Directory.GetFiles(sourceDir, "*.xlsx")
                                   .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                                   .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var fi = new FileInfo(f);
            sb.Append(fi.Name).Append('|')
              .Append(fi.Length).Append('|')
              .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        }

        if (sb.Length == 0) return "";

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>从文件名开头取 yyyy-MM-dd 作为这一周的起始日。</summary>
    public static DateTime? ParseDatePrefix(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var m = DatePrefixRe.Match(name);
        if (!m.Success) return null;
        if (int.TryParse(m.Groups[1].Value, out var y) &&
            int.TryParse(m.Groups[2].Value, out var mo) &&
            int.TryParse(m.Groups[3].Value, out var d) &&
            y >= 2000 && y <= 2100 && mo >= 1 && mo <= 12 && d >= 1 && d <= 31)
        {
            try { return new DateTime(y, mo, d); } catch { }
        }
        return null;
    }

    static string ReadManifest(string pkgPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(pkgPath);
            var e = zip.GetEntry(FeedSourceFactory.ManifestName);
            if (e == null) return "";
            using var s = e.Open();
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }
}
