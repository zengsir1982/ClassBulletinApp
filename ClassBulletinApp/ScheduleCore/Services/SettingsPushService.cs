using System;
using System.Collections.Generic;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 独立设置下发通道：把「设置」从数据包里拆出来，走 manifest 单独推送。
///
/// <para>为什么放在 manifest 里：manifest <b>不参与</b> <c>DataSha256</c> 的整包摘要计算，
/// 所以只改 manifest 时教室机不会误判"课表有更新"去下载整包 —— 真正做到"只下发设置、不动课表"。</para>
///
/// <para>为什么用 <see cref="SchedulePackage.PackageManifest.SettingsPushId"/> 而不是代次：
/// 代次是每台机器各自累加的本地计数器，一旦某台机器历史值虚高（如本机 236、包 33），
/// 就永远追不上、永久收不到下发。PushId 是"这一次推送"的身份，
/// 教室机只要发现与本机记录不同就应用，完全绕开代次单调性。</para>
///
/// <para>与「随包通道」的关系：两条通道互不干扰。
/// 随包通道（data.cbpack 的 meta.json）只带观感类设置、代次按内容指纹递增；
/// 本通道带完整白名单（含更新来源组），由管理员显式触发。</para>
/// </summary>
public static class SettingsPushService
{
    public class PushResult
    {
        public bool Ok { get; set; }

        /// <summary>内容与数据源上已有的完全一致，未重复上传。</summary>
        public bool Skipped { get; set; }

        public string Message { get; set; } = "";
        public int Rev { get; set; }
        public string PushId { get; set; } = "";

        /// <summary>本次推送涉及的设置项数（供界面展示）。</summary>
        public int ItemCount { get; set; }

        /// <summary>逐项变更说明（"显示 · 开学周 周一 → 周日"这种），可为空。</summary>
        public List<string> Changes { get; set; } = new();
    }

    public class ApplyResult
    {
        public bool Ok { get; set; }

        /// <summary>本次确实写入了本机设置。</summary>
        public bool Applied { get; set; }

        public string Message { get; set; } = "";
        public int Rev { get; set; }
        public List<string> Changes { get; set; } = new();

        /// <summary>本次写入改动了「提醒」组（主窗提醒设置页需要重载）。</summary>
        public bool ReminderTouched { get; set; }

        /// <summary>本次写入改动了「班级内容同步」组（主窗信息管理页需要重载）。</summary>
        public bool SyncTouched { get; set; }

        /// <summary>本次写入改动了「教学日历」（校历已落到 schedule.json，大屏 / 周视图要立刻重画）。</summary>
        public bool CalendarTouched { get; set; }

        /// <summary>本次落地的校历条数（-1 = 本机还没课表，什么都没写）。</summary>
        public int CalendarCount { get; set; }

        /// <summary>本次执行了代次重置指令（教室机侧高水位已清零）。</summary>
        public bool ResetDone { get; set; }
    }

    /// <summary>
    /// 下发时附带的「重置教室机代次」请求（r144）。
    /// <para>用独立类型而不是复用 <see cref="PackageSettings"/>：重置是<b>一次性指令</b>，
    /// 不属于设置内容，绝不能被算进内容指纹（否则每次下发都被判成"内容变了"）。</para>
    /// </summary>
    public class RevResetRequest
    {
        /// <summary>清零教室机的已应用设置代次 + 已应用教学日历代次。</summary>
        public bool Applied { get; set; }

        /// <summary>清空教室机记住的整包摘要（让它下次检查重新下载课表包）。</summary>
        public bool PackageSha { get; set; }

        public bool Any => Applied || PackageSha;
    }

    /// <summary>
    /// 发布端：把设置写进数据源的 manifest 并单独上传（不动 data.cbpack）。
    /// </summary>
    /// <param name="force">true = 即使内容与远端一致也重新下发（用于把被改乱的教室机拉回统一值）。</param>
    /// <param name="reset">同时下发「重置教室机代次」指令；带指令时强制写入 manifest（内容一样也要写）。</param>
    public static PushResult Push(IFeedSource src, PackageSettings ps, bool force = false,
        RevResetRequest? reset = null)
    {
        var r = new PushResult { ItemCount = CountItems(ps) };
        try
        {
            var man = SchedulePackage.ParseManifest(src.ReadManifest());
            if (man == null)
            {
                r.Message = "数据源上没有可用的 manifest.json —— 请先「发布到数据源」一次，再来下发设置。";
                return r;
            }

            bool withReset = reset?.Any == true;
            // 带重置指令时必须写 manifest，否则指令根本发不出去（指纹相同的分支会直接返回）
            if (withReset) force = true;

            string fp = SchedulePackage.SettingsFingerprint(ps);
            string remoteFp = man is { SettingsIncluded: true, Settings: not null }
                ? SchedulePackage.SettingsFingerprint(man.Settings)
                : "";

            if (!force && man.SettingsIncluded && fp == remoteFp)
            {
                r.Ok = true;
                r.Skipped = true;
                r.Rev = man.SettingsRev;
                r.PushId = man.SettingsPushId;
                r.Message = $"数据源上的设置与本次内容一致（代次 {man.SettingsRev}），未重复下发。"
                          + "如需强制重推（例如个别教室机被改乱），勾选「强制重新下发」后再试。";
                return r;
            }

            // 只动设置相关字段，其它（BuiltAt / DataSha256 / Files / Rev …）一律原样保留
            man.SettingsIncluded = true;
            man.Settings = ps;
            man.SettingsRev = Math.Max(1, man.SettingsRev + 1);
            man.SettingsPushId = Guid.NewGuid().ToString("N");

            if (withReset)
            {
                man.RevResetId = Guid.NewGuid().ToString("N");
                man.RevResetApplied = reset!.Applied;
                man.RevResetPackage = reset.PackageSha;
            }

            src.UploadManifestOnly(SchedulePackage.SerializeManifest(man));

            r.Ok = true;
            r.Rev = man.SettingsRev;
            r.PushId = man.SettingsPushId;
            r.Message = $"设置已下发（代次 {man.SettingsRev}，共 {r.ItemCount} 项）。"
                      + (withReset
                          ? "并附带「重置代次」指令：" + RevResetService.Describe(reset!.Applied, reset.PackageSha) + "。"
                          : "")
                      + "教室机下次检查时自动应用，不会重下课表包。";
            return r;
        }
        catch (Exception ex)
        {
            r.Message = "下发失败：" + ex.Message;
            return r;
        }
    }

    /// <summary>
    /// 教室机端：按 PushId 判断并应用 manifest 里的设置。
    /// <para>不下载、不应用课表，只写设置白名单。可安全地在每次定时检查里调用。</para>
    /// </summary>
    public static ApplyResult ApplyFromManifest(PackageManifest? man)
    {
        var r = new ApplyResult();

        if (man == null)
        {
            r.Ok = true;
            r.Message = "数据源没有单独下发的设置。";
            return r;
        }

        var st = Store.LoadSettings();

        // ★ r144：重置代次指令必须最先处理，而且要在「已应用过这次下发」的早退之前 ——
        //   管理员点「下发 + 重置」时，内容很可能与上次完全一样（PushId 不同但设置相同），
        //   早退就会把指令吞掉，现场表现是"点了没反应"。
        bool didReset = RevResetService.ApplyInstruction(man.RevResetId,
            man.RevResetApplied, man.RevResetPackage, st);
        if (didReset)
        {
            Store.SaveSettings(st);          // 令牌先落盘：不能因为下面任何一条早退路径丢掉它
            r.ResetDone = true;
            r.Applied = true;
        }

        if (!man.SettingsIncluded || man.Settings == null)
        {
            r.Ok = true;
            r.Message = didReset
                ? "已按管理员指令重置本机代次（本次下发不带设置内容）。"
                : "数据源没有单独下发的设置。";
            return r;
        }

        if (!string.IsNullOrEmpty(man.SettingsPushId) &&
            string.Equals(man.SettingsPushId, st.LastSettingsPushId, StringComparison.Ordinal))
        {
            r.Ok = true;
            r.Message = didReset
                ? "已按管理员指令重置本机代次（这次下发的设置内容与上次相同，未重复应用）。"
                : "已应用过这次设置下发。";
            return r;
        }

        // 老包（没有 PushId 的下发）走代次兜底，避免重复应用
        if (string.IsNullOrEmpty(man.SettingsPushId) && man.SettingsRev <= st.AppliedSettingsRev)
        {
            r.Ok = true;
            r.Message = didReset
                ? "已按管理员指令重置本机代次（设置代次仍低于本机，保留了本机设置）。"
                : $"设置代次 {man.SettingsRev} 未高于本机 {st.AppliedSettingsRev}，已保留本机设置。";
            return r;
        }

        var changes = DescribeChanges(st, man.Settings);
        var touched = TouchedGroups(st, man.Settings);
        if (didReset) changes.Insert(0, RevResetService.Describe(man.RevResetApplied, man.RevResetPackage));

        // r143 教学日历：校历本体不在 settings.json，得单独写进 schedule.json。
        // ★ 代次判定与整包侧方向相反：整包要求"更严"(>)，下发要求">=" ——
        //   同代次重发也要生效，否则现场被改乱的教室机永远推不回去。
        bool calTouched = false;
        int calCount = 0;
        bool calAccept = true;
        if (man.Settings.CalendarIncluded)
        {
            calAccept = man.Settings.CalendarRev >= st.AppliedCalendarRev;
            if (calAccept)
            {
                calCount = SchedulePackage.ApplyCalendarToLocalSchedule(man.Settings.Calendar);
                if (calCount >= 0)
                {
                    st.AppliedCalendarRev = Math.Max(st.AppliedCalendarRev, man.Settings.CalendarRev);
                    calTouched = true;
                    changes.Add($"教学日历 {calCount} 条（代次 {man.Settings.CalendarRev}）");
                }
            }
            else
            {
                changes.Add($"教学日历代次 {man.Settings.CalendarRev} 低于本机已应用 {st.AppliedCalendarRev}，已保留本机校历");
            }
        }

        SchedulePackage.ApplyPackageSettings(st, man.Settings, includeCalendar: calAccept);
        st.AppliedSettingsRev = Math.Max(st.AppliedSettingsRev, man.SettingsRev);
        st.LastSettingsPushId = man.SettingsPushId ?? "";
        st.LastAppliedSettingsInfo =
            $"{DateTime.Now:MM-dd HH:mm} 应用设置下发（代次 {man.SettingsRev}）："
            + (changes.Count == 0 ? "内容无变化" : string.Join("、", changes.Take(4))
               + (changes.Count > 4 ? $" 等 {changes.Count} 项" : ""));

        Store.SaveSettings(st);

        r.Ok = true;
        r.Applied = true;
        r.Rev = man.SettingsRev;
        r.Changes = changes;
        r.ReminderTouched = touched.Reminder;
        r.SyncTouched = touched.Sync;
        r.CalendarTouched = calTouched;
        r.CalendarCount = calCount;
        r.Message = changes.Count == 0
            ? $"已应用设置下发（代次 {man.SettingsRev}），内容与本机一致。"
            : $"已应用设置下发（代次 {man.SettingsRev}）：{string.Join("、", changes.Take(4))}"
              + (changes.Count > 4 ? $" 等 {changes.Count} 项" : "");

        if (!Store.EnablePersistence)
            r.Message += "（当前为游客模式，改动不会保存，重启后失效）";

        return r;
    }

    /// <summary>本次下发包含多少项设置（更新来源组按 7 项计，教学日历按 1 组计）。</summary>
    public static int CountItems(PackageSettings ps)
    {
        int n = 9 + 1 /* Reminder */ + 6 /* 同步开关（含教师机轮询） */;
        if (ps.SourceGroupIncluded) n += 7;
        if (ps.CalendarIncluded) n += 1;
        return n;
    }

    /// <summary>生成给人看的分组摘要，如「显示 9 项 · 提醒 1 组 · 同步 6 项 · 更新来源 7 项」。</summary>
    public static string Summary(PackageSettings ps)
    {
        var parts = new List<string> { "显示 9 项", "提醒 1 组", "同步 6 项" };
        if (ps.SourceGroupIncluded) parts.Add("更新来源 7 项");
        if (ps.CalendarIncluded) parts.Add("教学日历 1 组");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 本次下发会改动界面上的哪几组（r115）。
    /// <para>为什么要分：下发只写磁盘，界面控件必须显式重载才会变。而整页重载会冲掉正在输入的内容，
    /// 因此只重载"真的变了"的那组：提醒 → 主窗「提醒设置」页；同步开关 / 同步根目录 → 主窗「信息管理」页。</para>
    /// </summary>
    public static (bool Reminder, bool Sync) TouchedGroups(AppSettings st, PackageSettings ps)
    {
        bool reminder = false;
        if (ps.Reminder != null)
        {
            // 同类型、同一序列化器 → 属性顺序一致，可直接按串比对（含嵌套的三组提醒配置）
            string before = System.Text.Json.JsonSerializer.Serialize(st.Reminder ?? new ReminderSettings());
            string after = System.Text.Json.JsonSerializer.Serialize(ps.Reminder);
            reminder = !string.Equals(before, after, StringComparison.Ordinal);
        }

        bool sync = st.BoardSyncEnabled != ps.BoardSyncEnabled
                    || st.BoardSyncPopupDuringBreak != ps.BoardSyncPopupDuringBreak
                    || st.BoardSyncFullScreenReminder != ps.BoardSyncFullScreenReminder
                    || st.BoardSyncVoiceReminder != ps.BoardSyncVoiceReminder
                    || st.BoardSyncAutoApply != ps.BoardSyncAutoApply
                    || (ps.BoardSyncTeacherPolling.HasValue
                        && st.BoardSyncTeacherPolling != ps.BoardSyncTeacherPolling.Value)
                    || (ps.SourceGroupIncluded
                        && !string.Equals((st.BoardSyncRoot ?? "").Trim(),
                                          (ps.BoardSyncRoot ?? "").Trim(), StringComparison.Ordinal));

        return (reminder, sync);
    }

    /// <summary>逐项对比，返回"哪一项从什么变成了什么"（只列真的变了的）。</summary>
    public static List<string> DescribeChanges(AppSettings st, PackageSettings ps)
    {
        var list = new List<string>();
        void C(string label, object? before, object? after)
        {
            string b = Fmt(before), a = Fmt(after);
            if (!string.Equals(b, a, StringComparison.Ordinal))
                list.Add($"{label} {b}→{a}");
        }

        C("开学周", WeekName(st.WeekStartDay), WeekName(ps.WeekStartDay));
        C("启动显示面板", st.ShowPanelOnStart, ps.ShowPanelOnStart);
        C("自动展开课表", st.AutoShowSchedule, ps.AutoShowSchedule);
        C("自动展开通知", st.AutoShowNotification, ps.AutoShowNotification);
        C("自动展开作业", st.AutoShowHomework, ps.AutoShowHomework);
        C("自动展开缺交", st.AutoShowMissing, ps.AutoShowMissing);
        C("自动展开公告", st.AutoShowNotice, ps.AutoShowNotice);
        C("恢复黑板", st.DoodleRestoreOnStart, ps.DoodleRestoreOnStart);
        C("黑板收起启动", st.DoodleStartCollapsed, ps.DoodleStartCollapsed);
        // ★ r113：提醒必须逐项比对。原来只比 14 项、漏了提醒，
        //   于是"只改了提醒"的推送永远显示"内容与本机一致"（现场被误判成下发失败）。
        if (ps.Reminder != null)
        {
            var rb = st.Reminder ?? new ReminderSettings();
            var ra = ps.Reminder;
            C("提醒总开关", rb.Enabled, ra.Enabled);
            C("上课提醒", rb.ClassStart?.Enabled, ra.ClassStart?.Enabled);
            C("上课提前(秒)", rb.ClassStart?.LeadSeconds, ra.ClassStart?.LeadSeconds);
            C("下课提醒", rb.ClassEnd?.Enabled, ra.ClassEnd?.Enabled);
            C("放学提醒", rb.AfterSchool?.Enabled, ra.AfterSchool?.Enabled);
            C("提醒提示音", rb.PlaySound, ra.PlaySound);
            C("提醒语音", rb.VoiceEnabled, ra.VoiceEnabled);
            C("提醒停留(秒)", rb.DisplaySeconds, ra.DisplaySeconds);
            C("课表变化提醒", rb.ChangeEnabled, ra.ChangeEnabled);
        }

        C("班级内容同步", st.BoardSyncEnabled, ps.BoardSyncEnabled);
        C("课间弹窗", st.BoardSyncPopupDuringBreak, ps.BoardSyncPopupDuringBreak);
        C("全屏提醒", st.BoardSyncFullScreenReminder, ps.BoardSyncFullScreenReminder);
        C("语音提醒", st.BoardSyncVoiceReminder, ps.BoardSyncVoiceReminder);
        C("自动应用变动", st.BoardSyncAutoApply, ps.BoardSyncAutoApply);
        // 老包没这个字段（null）：不算变化，也不该在回执里写一条莫名其妙的「关→开」。
        if (ps.BoardSyncTeacherPolling.HasValue)
            C("教师机定时检查", st.BoardSyncTeacherPolling, ps.BoardSyncTeacherPolling.Value);

        if (ps.SourceGroupIncluded)
        {
            C("自动更新", st.AutoUpdateEnabled, ps.AutoUpdateEnabled);
            C("更新来源", st.UpdateSourceKind, ps.UpdateSourceKind);
            C("共享路径", st.UpdateFolderPath, ps.UpdateFolderPath);
            C("FTP 地址", st.UpdateFtpUrl, ps.UpdateFtpUrl);
            C("更新间隔", st.UpdateIntervalMinutes, ps.UpdateIntervalMinutes);
            C("启动延迟", st.UpdateStartupDelaySeconds, ps.UpdateStartupDelaySeconds);
            C("同步根目录", st.BoardSyncRoot, ps.BoardSyncRoot);
        }

        return list;
    }

    static string Fmt(object? v) => v switch
    {
        null => "空",
        bool b => b ? "开" : "关",
        string s => string.IsNullOrWhiteSpace(s) ? "空" : s,
        _ => v.ToString() ?? "空"
    };

    static string WeekName(int d) => d switch
    {
        0 => "周日",
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        _ => d.ToString()
    };
}
