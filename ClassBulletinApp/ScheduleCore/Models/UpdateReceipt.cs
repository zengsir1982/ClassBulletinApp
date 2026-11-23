using System;

namespace ScheduleCore.Models;

/// <summary>
/// 教室机「更新课表」回执：本机最近一次检查更新做了什么。
///
/// <para>★ 刻意<b>不</b>放进 <c>SchedulePackage</c>：不进整包摘要、不改任何代次。
/// 它走更新数据源根目录下的 <c>status/u-&lt;设备Id&gt;.json</c>，每台机器只覆盖自己那一份。</para>
///
/// <para>发布端不能把「State=applied」当成静态标记：今天成功、明天发新包就落后了。
/// 所以回执里必须带 <see cref="TargetSha"/>（回的是哪个包），由界面再和当前 manifest 比一次。</para>
///
/// <para>所有字段都是不可信输入（共享盘谁都能写），读取端必须做类型 / 长度校验。</para>
/// </summary>
public class UpdateReceipt
{
    /// <summary>结构版本，将来加字段时用于兼容。</summary>
    public int Rev { get; set; } = 1;

    /// <summary>本机递增序号。发布端用它识别「回执倒退」（恢复备份 / 换机留下的旧文件）。</summary>
    public long Seq { get; set; }

    /// <summary>本次检查的唯一 Id（方便发布端去重 / 排错）。</summary>
    public string ReceiptId { get; set; } = "";

    /// <summary>与班级内容同步共用的稳定设备 Id（<c>BoardSyncService.EnsureActorId()</c>）。</summary>
    public string ActorId { get; set; } = "";

    /// <summary>机器名（换机会变，只做线索，不做主键）。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>管理员在发布端给这台机器起的名字（不随包下发，只存在发布机本地）。</summary>
    public string Alias { get; set; } = "";

    // ---- 回的是哪个包 ----
    /// <summary>本次检查读到的 <c>manifest.DataSha256</c>；发布端拿它和当前包比，判断「落后 / 最新」。</summary>
    public string TargetSha { get; set; } = "";
    public DateTime TargetBuiltAt { get; set; }
    public int TargetRev { get; set; }
    public string TargetBuiltBy { get; set; } = "";

    // ---- 这次做成了什么 ----
    /// <summary>是否真的下载并应用了整包。</summary>
    public bool PackageDone { get; set; }

    /// <summary>独立设置下发通道（PushId）是否落地。</summary>
    public bool SettingsDone { get; set; }

    /// <summary>是否执行了「重置代次」指令（r144）。</summary>
    public bool RevResetDone { get; set; }

    /// <summary>checked（查过、没变化） / applied（有落地） / failed（失败） / partial（包成功但下发失败）。</summary>
    public string State { get; set; } = "checked";

    /// <summary>机器可判的错误码：SCHEMA_NEW / DOWNLOAD_FAIL / VERIFY_FAIL / APPLY_FAIL / CHECK_FAIL / ...</summary>
    public string ReasonCode { get; set; } = "";

    /// <summary>一行人话。已去掉路径、账号、完整异常栈（见 <c>UpdateReceiptService.SafeReason</c>）。</summary>
    public string ReasonText { get; set; } = "";

    // ---- 落地后的读数（给发布端核对「到底改了什么」） ----
    public string AppliedSha { get; set; } = "";
    public int AppliedSettingsRev { get; set; }
    public int AppliedCalendarRev { get; set; }
    public string LastSettingsPushId { get; set; } = "";

    public int ClassCount { get; set; }
    public int OverrideAdded { get; set; }
    public int OverrideRemoved { get; set; }
    public int NoticeAdded { get; set; }
    public int UserCount { get; set; }
    public int ManualSuspended { get; set; }

    // ---- 元信息 ----
    /// <summary>程序版本（用于发现「教室机程序太旧、看不懂新包」）。</summary>
    public string AppVersion { get; set; } = "";

    /// <summary>写入本机时间。只用于显示 —— 教室机墙钟常不准，状态判定不看它。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>发布机本地的「设备 Id → 教室位置」别名表；不随包下发、不写进设置白名单。</summary>
public class UpdateReceiptAliases
{
    public int Schema { get; set; } = 1;
    public System.Collections.Generic.Dictionary<string, string> Names { get; set; } = new();
}
