using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 「代次」维护：查看与清零本机的各个高水位 / 计数器（r144）。
///
/// <para><b>为什么需要这个功能。</b>课表和设置靠"代次"决定谁覆盖谁，而代次是**每台机器各自累加的本地数字**。
/// 一旦某台教室机因为恢复备份、换机、改过 users.json 等原因把 <see cref="AppSettings.AppliedCalendarRev"/>
/// 抬到一个虚高的值（比如 999），发布端再怎么发（当前才 3）都追不上 ——
/// 表现就是「明明下发了，教室机毫无反应」，而且现场看不出任何错误。
/// 清零高水位是唯一的出路。</para>
///
/// <para><b>只动本机。</b>下发侧让教室机清零走另一条路：
/// <see cref="SchedulePackage.PackageManifest.RevResetId"/> 一次性指令。</para>
/// </summary>
public static class RevResetService
{
    /// <summary>可重置的计数器。枚举名会写进日志，改名前先想清楚。</summary>
    public enum RevKind
    {
        /// <summary>本机已应用的数据包设置代次（教室机侧）。</summary>
        AppliedSettings,

        /// <summary>本机已应用的教学日历代次（教室机侧）。</summary>
        AppliedCalendar,

        /// <summary>本机记住的推送凭据（教室机侧，清掉 = 下一次下发一定重放）。</summary>
        PushId,

        /// <summary>本机记住的整包摘要（教室机侧，清掉 = 下次检查一定重下整包）。</summary>
        PackageSha,

        /// <summary>发布端的设置代次计数器。</summary>
        SettingsPublish,

        /// <summary>发布端的教学日历代次计数器。</summary>
        CalendarPublish,

        /// <summary>发布端的整包内容版本号。</summary>
        PackagePublish
    }

    /// <summary>一项计数器的当前值 + 给人看的说明。</summary>
    public sealed class RevItem
    {
        public RevKind Kind { get; init; }
        public string Name { get; init; } = "";
        public string Side { get; init; } = "";
        public string Value { get; init; } = "";
        public string Hint { get; init; } = "";

        /// <summary>是否为发布端（计数器）项 —— 界面按这个分组。</summary>
        public bool IsPublisher => Side == "发布端";
    }

    /// <summary>读一遍本机所有代次，供窗口展示。空串表示该项没设过。</summary>
    public static List<RevItem> Snapshot(AppSettings? st = null)
    {
        st ??= Store.LoadSettings();
        return new List<RevItem>
        {
            new()
            {
                Kind = RevKind.AppliedSettings, Side = "教室机", Name = "已应用设置代次",
                Value = st.AppliedSettingsRev.ToString(),
                Hint = "整包里的设置代次高于它才会覆盖本机。虚高就永远收不到设置。"
            },
            new()
            {
                Kind = RevKind.AppliedCalendar, Side = "教室机", Name = "已应用教学日历代次",
                Value = st.AppliedCalendarRev.ToString(),
                Hint = "校历的高水位。它虚高时，整包和「下发设置」都推不进校历（最典型的卡死）。"
            },
            new()
            {
                Kind = RevKind.PushId, Side = "教室机", Name = "已应用的推送凭据",
                Value = string.IsNullOrEmpty(st.LastSettingsPushId) ? "（空）" : Short(st.LastSettingsPushId),
                Hint = "清掉后，下一次「下发设置」无论内容是否相同都会重新应用一遍。"
            },
            new()
            {
                Kind = RevKind.PackageSha, Side = "教室机", Name = "已下载的课表包摘要",
                Value = string.IsNullOrEmpty(st.LastAppliedSha) ? "（空）" : Short(st.LastAppliedSha),
                Hint = "清掉后，下次自动检查会认为「有新版本」，重新下载并应用一次整包。"
            },
            new()
            {
                Kind = RevKind.SettingsPublish, Side = "发布端", Name = "设置代次计数器",
                Value = st.SettingsPublishRev.ToString(),
                Hint = "导出整包时写进 meta.SettingsRev，内容变了才 +1。"
            },
            new()
            {
                Kind = RevKind.CalendarPublish, Side = "发布端", Name = "教学日历代次计数器",
                Value = st.CalendarPublishRev.ToString(),
                Hint = "导出整包 / 下发时写进 CalendarRev。"
            },
            new()
            {
                Kind = RevKind.PackagePublish, Side = "发布端", Name = "整包内容版本号",
                Value = st.PackageRev.ToString(),
                Hint = "写进 manifest.Rev。★ 它不参与「有没有新版本」的判断（那个只看摘要）。"
            }
        };
    }

    static string Short(string s) => s.Length <= 12 ? s : s[..12] + "…";

    /// <summary>教室机侧四项（界面上的「A. 本机已应用」整组）。</summary>
    public static RevKind[] ClassroomKinds() => new[]
    {
        RevKind.AppliedSettings, RevKind.AppliedCalendar, RevKind.PushId, RevKind.PackageSha
    };

    /// <summary>发布端三项。</summary>
    public static RevKind[] PublisherKinds() => new[]
    {
        RevKind.SettingsPublish, RevKind.CalendarPublish, RevKind.PackagePublish
    };

    /// <summary>
    /// 清零选中的计数器并立即落盘。
    /// <para>★ 用「清零」而不是「减一 / 设成某个值」：代次本来就没有真实语义，
    /// 唯一的用途是比大小，归零之后任何一次正常发布都能重新把它推上去。</para>
    /// </summary>
    /// <returns>逐项变更说明，如「已应用设置代次 999 → 0」。</returns>
    public static List<string> Reset(IEnumerable<RevKind> kinds)
    {
        var st = Store.LoadSettings();
        var done = new List<string>();

        foreach (var k in kinds.Distinct())
        {
            switch (k)
            {
                case RevKind.AppliedSettings:
                    done.Add($"已应用设置代次 {st.AppliedSettingsRev} → 0");
                    st.AppliedSettingsRev = 0;
                    break;
                case RevKind.AppliedCalendar:
                    done.Add($"已应用教学日历代次 {st.AppliedCalendarRev} → 0");
                    st.AppliedCalendarRev = 0;
                    break;
                case RevKind.PushId:
                    done.Add("已应用的推送凭据已清空（下次下发必定重放）");
                    st.LastSettingsPushId = "";
                    break;
                case RevKind.PackageSha:
                    done.Add("已下载的课表包摘要已清空（下次检查会重下整包）");
                    st.LastAppliedSha = "";
                    break;
                case RevKind.SettingsPublish:
                    done.Add($"设置代次计数器 {st.SettingsPublishRev} → 0");
                    st.SettingsPublishRev = 0;
                    // 指纹也要清：否则下次导出会认为"内容没变"，沿用旧代次 0，教室机反而不认
                    st.SettingsPublishFingerprint = "";
                    break;
                case RevKind.CalendarPublish:
                    done.Add($"教学日历代次计数器 {st.CalendarPublishRev} → 0");
                    st.CalendarPublishRev = 0;
                    st.CalendarPublishFingerprint = "";
                    break;
                case RevKind.PackagePublish:
                    done.Add($"整包内容版本号 {st.PackageRev} → 0");
                    st.PackageRev = 0;
                    break;
            }
        }

        if (done.Count > 0) Store.SaveSettings(st);
        return done;
    }

    /// <summary>
    /// 教室机端：按 manifest 里的一次性指令清零。
    /// <para>★ 只认没执行过的令牌（<see cref="AppSettings.LastRevResetId"/>），
    /// 保证同一道指令不会每轮询一次就把本机代次重新打回 0。</para>
    /// <para>★ 调用方负责在返回 true 后立刻 <c>Store.SaveSettings</c> —— 令牌必须落盘，
    /// 否则进程重启后又执行一遍。</para>
    /// </summary>
    /// <returns>是否真的执行了重置。</returns>
    public static bool ApplyInstruction(string? resetId, bool resetApplied, bool resetPackage, AppSettings st)
    {
        if (string.IsNullOrWhiteSpace(resetId)) return false;
        if (string.Equals(resetId.Trim(), st.LastRevResetId, StringComparison.Ordinal)) return false;

        st.LastRevResetId = resetId.Trim();
        if (resetApplied)
        {
            st.AppliedSettingsRev = 0;
            st.AppliedCalendarRev = 0;
        }
        if (resetPackage) st.LastAppliedSha = "";
        return true;
    }

    /// <summary>把一次重置写成给人看的一句话（写进 LastAppliedSettingsInfo，便于回溯）。</summary>
    public static string Describe(bool resetApplied, bool resetPackage)
    {
        var parts = new List<string>();
        if (resetApplied) parts.Add("已应用代次全部清零");
        if (resetPackage) parts.Add("课表包摘要已清空（将重新下载）");
        return parts.Count == 0 ? "收到重置指令但内容为空" : "按管理员指令重置代次：" + string.Join("、", parts);
    }
}
