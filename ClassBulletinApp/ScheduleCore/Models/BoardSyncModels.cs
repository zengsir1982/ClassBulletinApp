using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ScheduleCore.Models;

/// <summary>
/// 班级内容同步（二期）的常量与状态名。
///
/// <para>★ 红线：这里的一切都走共享根目录下的 <c>board/</c> 子目录，
/// 与发布链根目录的 <c>manifest.json</c> / <c>data.cbpack</c> 完全隔离；
/// 主包 <c>SchedulePackage</c> 也永远不打包作业 / 缺交 / 本班通知。</para>
///
/// <para>★ 无锁共享目录/FTP 的唯一写规则：<b>每台设备只新建自己独占的文件，从不改写别人的文件</b>。
/// 撤回不是「改原消息」，而是再发一条 <c>revoke</c> 新消息；回执也只新建，不覆盖。</para>
/// </summary>
public static class BoardSyncConsts
{
    public const string Root = "board";
    public const string Messages = "messages";
    public const string Receipts = "receipts";
    public const string Snapshots = "snapshots";
    public const string StateFile = "board_sync.json";
    public const int Rev = 1;

    public const string EntityHomework = "homework";
    public const string EntityMissing = "missing";
    public const string EntityNotice = "notice";

    /// <summary>
    /// 值日生安排。与作业 / 缺交完全同一套逻辑（一条 = 某一天的某个岗位 + 值日生），
    /// 只是字段换成 星期 / 值日内容 / 值日生。
    /// </summary>
    public const string EntityDuty = "duty";

    public const string OpUpsert = "upsert";
    public const string OpDelete = "delete";
    public const string OpRevoke = "revoke";

    public const string DecReplace = "replace";
    public const string DecAppend = "append";
    public const string DecIgnore = "ignore";
    public const string DecDelete = "delete";
    public const string DecKeep = "keep";

    public const string StSent = "已发出";
    public const string StDelivered = "已送达";
    public const string StSeen = "已阅";
    public const string StApplied = "已应用";
    public const string StVerified = "已核对";
    public const string StIgnored = "已忽略";
    public const string StExpired = "已过期";
    public const string StConflict = "冲突";
    public const string StRetry = "待重试";
    public const string StRevoked = "已撤回";

    public const string RcDelivered = "delivered";
    public const string RcSeen = "seen";
    public const string RcIgnored = "ignored";
    public const string RcApplied = "applied";
    public const string RcConflict = "conflict";
    public const string RcVerified = "verified";

    /// <summary>教室机现场撤回了这次替换 / 删除，原文已还原。教师机见到它把台账标成「已撤回」。</summary>
    public const string RcRestored = "restored";

    public const string DirOut = "out";
    public const string DirIn = "in";

    /// <summary>教师机看到的状态顺序（用于核对视图排序）。</summary>
    public static readonly string[] StatusOrder =
    {
        StSent, StDelivered, StSeen, StApplied, StVerified, StConflict, StIgnored, StExpired, StRetry, StRevoked
    };

    public static string EntityName(string? entity) => entity switch
    {
        EntityHomework => "作业公示",
        EntityMissing => "缺交名单",
        EntityNotice => "本班通知",
        EntityDuty => "值日安排",
        _ => entity ?? "?"
    };

    /// <summary>四种内容类型的固定顺序（界面下拉 / 卡片 / 预览统一用这一份，别各处硬编码）。</summary>
    public static readonly string[] AllEntities =
        { EntityHomework, EntityMissing, EntityDuty, EntityNotice };

    public static string OpName(string? op) => op switch
    {
        OpUpsert => "新增 / 修改",
        OpDelete => "删除",
        OpRevoke => "撤回",
        _ => op ?? "?"
    };

    public static string DecisionName(string? d) => d switch
    {
        DecReplace => "替换",
        DecAppend => "增加（两条都留）",
        DecIgnore => "忽略 / 稍后处理",
        DecDelete => "删除该条",
        DecKeep => "保留不改",
        _ => d ?? "?"
    };

    /// <summary>删除类变更单只能二选一，不能「增加」出一条已删的东西。</summary>
    public static string[] DecisionsFor(string? op)
        => op == OpDelete ? new[] { DecDelete, DecKeep } : new[] { DecAppend, DecReplace, DecIgnore };

    /// <summary>字段键 → 中文标签（回执 / 详情排版统一用这一处，别在界面里硬编码）。</summary>
    public static string FieldLabel(string key) => key switch
    {
        "subject" => "科目",
        "content" => "内容",
        "students" => "缺交学生",
        "title" => "标题",
        "day" => "星期",
        "duty" => "值日内容",
        "names" => "值日生",
        "bold" => "加粗",
        "color" => "颜色",
        "sizeLevel" => "字号档位",
        "pinned" => "置顶",
        "due" => "截止日期",
        _ => key
    };

    /// <summary>
    /// 把字段字典排成给人看的多行文本：一行一个字段、中文标签、空值显示「（空）」。
    /// 原来拼接成一长串 subject：xxx　content：xxx 是回执难读的主因。
    /// </summary>
    public static string FormatFields(IDictionary<string, string>? fields, string indent = "")
    {
        if (fields == null || fields.Count == 0) return indent + "（无内容）";
        // 内容字段优先、格式字段靠后，读的人先看到「改了什么」
        var order = new[] { "subject", "day", "students", "title", "content", "duty", "names",
                            "due", "bold", "color", "sizeLevel", "pinned" };
        var keys = fields.Keys
            .OrderBy(k => { int i = Array.IndexOf(order, k); return i < 0 ? 99 : i; })
            .ThenBy(k => k, StringComparer.Ordinal);
        return string.Join("\n", keys.Select(k =>
        {
            string v = fields[k] ?? "";
            if (k == "bold" || k == "pinned") v = v == "true" ? "是" : "否";
            if (v.Length == 0) v = "（空）";
            return indent + FieldLabel(k) + "：" + v;
        }));
    }

    /// <summary>给列表 / 下拉用的一行摘要，长文本截断。</summary>
    public static string Brief(IDictionary<string, string>? fields)
    {
        if (fields == null || fields.Count == 0) return "（空条目）";
        // 值日的第一段是「星期」（它占了作业里「科目」的位置）
        string main = fields.TryGetValue("subject", out var s) ? s ?? ""
                    : fields.TryGetValue("day", out var d) ? d ?? "" : "";
        string body = fields.TryGetValue("content", out var c) ? c ?? ""
                    : fields.TryGetValue("students", out var st) ? st ?? ""
                    : fields.TryGetValue("title", out var t) ? t ?? ""
                    : fields.TryGetValue("duty", out var du) ? du ?? "" : "";
        // 值日生名单跟在内容后面一段：「周一：扫地、拖地｜张三、李四」
        if (fields.TryGetValue("names", out var nm) && !string.IsNullOrWhiteSpace(nm))
        {
            string names = nm.Replace('\n', ' ').Replace('\r', ' ').Trim();
            body = body.Trim().Length > 0 ? body.Trim() + "｜" + names : names;
        }
        body = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (body.Length > 42) body = body[..42] + "…";
        return string.IsNullOrWhiteSpace(main) ? body : main + "：" + body;
    }

    /// <summary>「一次安排一周」在变更单里的固定星期标签（占 Fields["day"]，让老版也能读懂这是一条值日单）。</summary>
    public const string WeekDayTag = "整周";

    /// <summary>一周逐日摘要（单行，进 Fields["names"]，参与核对）。</summary>
    public static string WeekRowsBrief(System.Collections.Generic.IEnumerable<BoardDutyRow>? rows)
    {
        var list = (rows ?? Array.Empty<BoardDutyRow>()).ToList();
        if (list.Count == 0) return "（空周表）";
        return string.Join("；", list.Select(r =>
        {
            string body = ((r.Duty ?? "").Trim() + "｜" + (r.Names ?? "").Trim()).Trim('｜');
            return (r.Day ?? "").Trim() + (body.Length > 0 ? "：" + body : "");
        }));
    }

    /// <summary>一周逐日明细（多行，只给人看，不参与核对）。</summary>
    public static string WeekRowsText(System.Collections.Generic.IEnumerable<BoardDutyRow>? rows)
    {
        var list = (rows ?? Array.Empty<BoardDutyRow>()).ToList();
        if (list.Count == 0) return "（空周表）";
        return string.Join("\n", list.Select(r =>
            (r.Day ?? "").Trim() + "　" + (r.Duty ?? "").Trim() + "｜" + (r.Names ?? "").Trim()));
    }

    /// <summary>
    /// 周安排的字段快照：仍用 day/duty/names 三个键，让台账、回执、核对、摘要都能走老排版，
    /// 逐日明细放在 <c>names</c> 里（<see cref="WeekRowsBrief"/>）。
    /// </summary>
    public static System.Collections.Generic.Dictionary<string, string> WeekFields(
        System.Collections.Generic.IEnumerable<BoardDutyRow> rows)
    {
        var list = rows.ToList();
        return new System.Collections.Generic.Dictionary<string, string>
        {
            ["day"] = WeekDayTag,
            ["duty"] = "共 " + list.Count + " 天",
            ["names"] = WeekRowsBrief(list)
        };
    }

    /// <summary>周安排在教室机裁决栏里的按钮文字（普通「增加 / 替换」会让人以为只改一天）。</summary>
    public static string WeeklyDecisionName(string? d) => d switch
    {
        DecAppend => "追加整周（保留原表）",
        DecReplace => "整表换成这一周",
        DecIgnore => "忽略 / 稍后处理",
        _ => DecisionName(d)
    };

    /// <summary>
    /// 把「字段 → 样式」排成人话，一行一个字段：<c>科目样式：加粗 · 特大 · 红色</c>。
    /// 界面 / 回执共用这一处，别在窗里自己拼。
    /// </summary>
    public static string FormatStyles(IDictionary<string, BoardTextStyle>? styles, string indent = "")
    {
        if (styles == null || styles.Count == 0) return "";
        var order = new[] { "subject", "day", "students", "duty", "names", "title", "content" };
        return string.Join("\n", styles.Keys
            .OrderBy(k => { int i = Array.IndexOf(order, k); return i < 0 ? 99 : i; })
            .ThenBy(k => k, StringComparer.Ordinal)
            .Select(k => indent + FieldLabel(k) + "样式：" + (styles[k]?.Caption ?? "")));
    }
}

/// <summary>
/// 一条字段的富文本样式（加粗 / 颜色 / 字号档位），变更单与教室机快照共用。
///
/// <para>★ 按「字段名 → 样式」存：作业 = subject / content；缺交 = subject / students。
/// <b>缺少某个键 = 沿用教室机该字段现在的样式</b>，绝不悄悄把用户自己调过的格式压回默认。</para>
///
/// <para>★ 为什么不再用平铺的 bold / color / sizeLevel：那三个键只够描述「内容」一套格式，
/// 而作业的科目名和正文是分别可调的（<c>SubjectStyle</c> / <c>ContentStyle</c>），
/// 旧格式传到教室机必然丢一半。</para>
/// </summary>
public class BoardTextStyle
{
    public bool Bold { get; set; }
    public string Color { get; set; } = "";
    public int SizeLevel { get; set; } = HwTextStyle.ExtraLargeLevel;

    public static BoardTextStyle From(TextStyle? s) => s == null
        ? new BoardTextStyle()
        : new BoardTextStyle { Bold = s.Bold, Color = s.Color ?? "", SizeLevel = s.SizeLevel };

    public TextStyle ToTextStyle() => new()
    {
        Bold = Bold,
        Color = Color ?? "",
        SizeLevel = Math.Clamp(SizeLevel, 0, HwTextStyle.Sizes.Length - 1)
    };

    public bool SameAs(BoardTextStyle? o)
        => o != null && o.Bold == Bold && (o.Color ?? "") == (Color ?? "") && o.SizeLevel == SizeLevel;

    [JsonIgnore]
    public string LevelName
        => HwTextStyle.LevelNames[Math.Clamp(SizeLevel, 0, HwTextStyle.LevelNames.Length - 1)];

    /// <summary>给人看的短描述，如「加粗 · 特大 · 红色」。</summary>
    [JsonIgnore]
    public string Caption => (Bold ? "加粗 · " : "") + LevelName + " · " + HwTextStyle.ColorName(Color);
}

/// <summary>确定性 Id / 签名工具。两端独立算出同一个值，绝不能用随机 Guid 当条目 Id。</summary>
public static class BoardSyncIds
{
    public static string Hash(string raw)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw ?? "")))
                  .ToLowerInvariant();

    public static string New(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>班级目录名只用 ASCII：中文班名在 GBK FTP 上会乱码。</summary>
    public static string ClassFolder(string classKey) => "c-" + Hash(classKey ?? "")[..12];

    /// <summary>「增加」派生的新 Id：同一变更单在任何机器上重放都得到同一 Id（幂等）。</summary>
    public static string DerivedItemId(string? parent, string msgId)
        => (string.IsNullOrWhiteSpace(parent) ? "new" : parent) + "#" + Hash(msgId)[..8];

    /// <summary>
    /// 条目当前值的签名（base 校验用）。字段顺序固定，避免字典枚举顺序造成假漂移。
    /// <para>★ 样式也进签名：只改颜色 / 字号、文字一字不动，也必须算「变过」，
    /// 否则教师机会以为教室机没动过，直接覆盖掉对方调好的格式。</para>
    /// </summary>
    public static string Signature(string entity, string itemId, IDictionary<string, string>? fields,
                                   IDictionary<string, BoardTextStyle>? styles = null)
    {
        var sb = new StringBuilder();
        sb.Append(entity).Append('|').Append(itemId).Append('|');
        if (fields != null)
        {
            foreach (var k in fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(k).Append('=').Append(fields[k] ?? "").Append(';');
        }
        if (styles != null && styles.Count > 0)
        {
            sb.Append("|fmt|");
            foreach (var k in styles.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var st = styles[k] ?? new BoardTextStyle();
                sb.Append(k).Append(':').Append(st.Bold ? '1' : '0').Append(',')
                  .Append(st.SizeLevel).Append(',').Append(st.Color ?? "").Append(';');
            }
        }
        return Hash(sb.ToString())[..16];
    }
}

/// <summary>
/// 结构化变更单：教师机只发「改哪一条、改成什么」，绝不发整张作业表。
/// 教室机是本机数据的唯一写者，按条目 Id 应用。
/// </summary>
/// <summary>
/// 「一次安排一周」里的一天：星期 / 值日内容 / 值日生。
/// <para>★ 一周表格里<b>空行会跳过</b>，只有填了内容或名单的行才进变更单 —— 老师常常只安排周一到周五。</para>
/// </summary>
public class BoardDutyRow
{
    public string Day { get; set; } = "";
    public string Duty { get; set; } = "";
    public string Names { get; set; } = "";
}

public class BoardChangeOrder
{
    public int Rev { get; set; } = BoardSyncConsts.Rev;
    public string MsgId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string ClassKey { get; set; } = "";
    public string ClassFolder { get; set; } = "";

    public string Entity { get; set; } = BoardSyncConsts.EntityHomework;
    public string Op { get; set; } = BoardSyncConsts.OpUpsert;

    /// <summary>目标条目稳定 Id；新增时可空（教室机派生）。</summary>
    public string ItemId { get; set; } = "";

    /// <summary>教师机看到的条目当前值签名；教室机应用前比对，不一致 = base 漂移。</summary>
    public string BaseSignature { get; set; } = "";

    /// <summary>字段快照：作业 = subject/content；缺交 = subject/students；通知 = title/content/pinned。</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>
    /// 字段级富文本样式（字段名 → 加粗 / 颜色 / 字号）。
    /// <para>作业 = subject / content；缺交 = subject / students；通知没有样式。
    /// <b>缺键 = 沿用教室机该字段现有样式</b>，空字典 = 老变更单，行为与升级前完全一致。</para>
    /// </summary>
    public Dictionary<string, BoardTextStyle> Styles { get; set; } = new();

    /// <summary>
    /// 「一次安排一周」的逐日内容。空 = 普通单条值日安排（老行为一点不变）。
    /// <para>★ 一周只用<b>一张</b>变更单：教室机只裁决一次、台账只有一条、撤回也是整周一起撤。
    /// 按天拆成 7 条会让电教管理员连点 7 次，且没法整周一起撤回。</para>
    /// </summary>
    public List<BoardDutyRow> WeekRows { get; set; } = new();

    /// <summary>
    /// 教师机指定「直接增加」：教室机收到本班通知后自动按「增加」落地并回执，
    /// <b>不需要电教管理员在收件箱里点确认</b>。
    /// <para>★ 只对「本班通知 + 新增」有效；替换 / 删除仍必须人工裁决 ——
    /// 自动增加只是新增一条，不覆盖教室机原有内容，所以不违反「教室机本地修改」这条铁律。</para>
    /// <para>★ 教室机仍可用 <c>AppSettings.BoardSyncAutoApply</c> 整体关掉这个能力；
    /// 关掉后这类单会退回普通收件箱，绝不丢消息。</para>
    /// </summary>
    public bool AutoApply { get; set; }

    /// <summary>
    /// 本班通知的截止时间（null = 长期有效）。发布时由老师选「今天 / 3 天后 / 1 周后 / 1 月后 / 长期」。
    /// <para>★ 教室机落地时原样写进 <c>Notification.DueAt</c>，<b>按这份绝对时间判断到期</b>，
    /// 不再按天换算 —— 否则教师机今天发、教室机三天后才拉到，会一收到就快过期。</para>
    /// </summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// 教师机指定「覆盖同标题的旧通知」：教室机落地时，把同班范围内标题相同且仍在显示的旧通知打墓碑，
    /// 再写入新的这条。
    /// <para>★ 由发布老师逐条决定，<b>默认 false</b> —— 勾错的代价是「上周还没过期的重要通知被静默顶掉」，
    /// 比「多一条重复」严重得多。</para>
    /// <para>★ 只对通知生效；置顶的旧通知不覆盖（老师置顶 = 这条要留着）。</para>
    /// </summary>
    public bool OverwriteSame { get; set; }

    public string Sender { get; set; } = "";
    public string SenderRole { get; set; } = "";
    public string SenderSubject { get; set; } = "";
    public string ValidDay { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? RevokeOf { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>教师机选中目标条目时，教室机那条内容的原文（给人看，回执里显示「替换了什么」）。</summary>
    public string TargetText { get; set; } = "";

    /// <summary>目标是否来自教室机内容快照（false = 本机条目 / 人工指定，base 校验要区别对待）。</summary>
    public bool FromSnapshot { get; set; }

    /// <summary>
    /// 发送方要求「课间全屏提醒」：教室机在非上课时间用整屏大字卡片强提示一次，
    /// 让全班当场注意到（普通变更单只弹右下角小卡片）。
    /// <para>只是<b>请求</b>：教室机还要满足三个条件才会真的全屏 —— 本机允许全屏提醒、
    /// 当前是课间 / 放学后、由教室机默认身份（电教管理员）接收。上课时间绝不打断课堂。</para>
    /// </summary>
    public bool ForceBreakReminder { get; set; }

    /// <summary>
    /// 课间全屏提醒要显示的<b>完整正文</b>（含标题、正文以及本次新增的字段，例如截止日期）。
    /// <para>★ 与 <see cref="Summary"/> 分开：Summary 是台账里的一行摘要（「新增 作业公示：语文：…」），
    /// 直接拿来全屏念 / 显示会丢掉后半段内容。发布窗不再让老师另填「提醒内容」，
    /// 而是把「这次发出去的完整内容」自动放这里。</para>
    /// <para>★ 老变更单没有这个字段，反序列化后为空串；教室机收集全屏文本时回退到 <see cref="Summary"/>，
    /// 历史消息不会突然变成空白卡片。</para>
    /// </summary>
    public string ForceText { get; set; } = "";

    /// <summary>
    /// 语音播报文本（空 = 不播报）。教师机在发布窗勾「🔊 语音提醒」后填入，
    /// 教室机收到这条变更单时用系统语音朗读一遍（讲台离得远也听得见）。
    /// </summary>
    public string VoiceText { get; set; } = "";

    /// <summary>
    /// 语音播报时机：true = 收到就播（上课时间也播，紧急通知用）；
    /// false = 课间 / 放学后才播（默认，上课时间先排队，绝不打断课堂）。
    /// </summary>
    public bool VoiceImmediate { get; set; }

    [JsonIgnore] public bool IsDelete => Op == BoardSyncConsts.OpDelete;
    [JsonIgnore] public bool IsRevoke => Op == BoardSyncConsts.OpRevoke;
    /// <summary>是不是「一次安排一周」的整周值日单。</summary>
    [JsonIgnore] public bool IsWeeklyDuty => Entity == BoardSyncConsts.EntityDuty && WeekRows.Count > 0;
    [JsonIgnore] public string FileName => $"m-{ActorId}-{MsgId}.json";
    [JsonIgnore] public string DisplayFields => BoardSyncConsts.FormatFields(Fields);
}

/// <summary>
/// 回执：教室机应用（或忽略）后只新建一份，绝不改写。
/// 「已阅」不是「已改好」的凭证 —— 只有 applied / verified 才代表条目真的落地。
/// </summary>
public class BoardReceipt
{
    public int Rev { get; set; } = BoardSyncConsts.Rev;
    public string ReceiptId { get; set; } = "";
    public string MsgId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string ClassFolder { get; set; } = "";
    public string State { get; set; } = BoardSyncConsts.RcSeen;

    /// <summary>电教管理员的裁决：replace / append / ignore；删除类 delete / keep。</summary>
    public string Decision { get; set; } = "";
    public string DecisionId { get; set; } = "";

    /// <summary>应用后本机数据版本（写盘回读的 JSON 哈希），供教师机核对。</summary>
    public string DataVersion { get; set; } = "";
    public string AppliedItemId { get; set; } = "";

    /// <summary>「一次安排一周」落地的所有条目 Id（撤回整周替换时要把这几条一起软删除）。</summary>
    public List<string> AppliedItemIds { get; set; } = new();

    /// <summary>整周「替换」前的整张值日便签 JSON（可一键还原整周；单条替换仍用 ArchivedSnapshot）。</summary>
    public string ArchivedBoard { get; set; } = "";

    public Dictionary<string, string> AppliedFields { get; set; } = new();

    /// <summary>应用后各字段的真实样式（教室机落地的格式，教师机据此确认「格式也传过去了」）。</summary>
    public Dictionary<string, BoardTextStyle> AppliedStyles { get; set; } = new();

    /// <summary>「替换」时存档被覆盖版本的全文，随时可人工撤回。</summary>
    public string ArchivedSnapshot { get; set; } = "";

    /// <summary>被覆盖版本的字段化副本（给人看，不用去读一坨 JSON）。</summary>
    public Dictionary<string, string> ArchivedFields { get; set; } = new();

    /// <summary>被覆盖版本各字段的样式（还原时会按整条存档回到教室机，这里只是给人看）。</summary>
    public Dictionary<string, BoardTextStyle> ArchivedStyles { get; set; } = new();

    /// <summary>被替换 / 被删除的目标条目原文（来自变更单 TargetText）。</summary>
    public string TargetText { get; set; } = "";

    /// <summary>做出裁决的账号（maker-checker 留痕）。</summary>
    public string DecisionBy { get; set; } = "";

    /// <summary>处理说明：一行人话，说明替换了什么、有没有 base 漂移。</summary>
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string FileName => $"r-{ActorId}-{MsgId}-{DecisionId}.json";
}

/// <summary>本机消息台账里的一条（同一 MsgId 只有一条，靠 MsgId 幂等）。</summary>
public class BoardMessageRecord
{
    public string MsgId { get; set; } = "";
    public string Direction { get; set; } = BoardSyncConsts.DirIn;
    public string Status { get; set; } = BoardSyncConsts.StSent;
    public BoardChangeOrder Order { get; set; } = new();
    public bool Seen { get; set; }
    public string Decision { get; set; } = "";
    public string DecisionId { get; set; } = "";
    public string DataVersion { get; set; } = "";
    public string AppliedItemId { get; set; } = "";
    public List<string> AppliedItemIds { get; set; } = new();
    public string ArchivedBoard { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // ---- 回执明细（教师机核对视图排版用，全部由回执带回，不猜、不重新拼） ----
    public Dictionary<string, string> AppliedFields { get; set; } = new();
    public Dictionary<string, BoardTextStyle> AppliedStyles { get; set; } = new();
    public Dictionary<string, string> ArchivedFields { get; set; } = new();
    public Dictionary<string, BoardTextStyle> ArchivedStyles { get; set; } = new();
    public string ArchivedSnapshot { get; set; } = "";
    public string TargetText { get; set; } = "";
    public string DecisionBy { get; set; } = "";
    public string ReceiptTime { get; set; } = "";

    /// <summary>已处理过的回执 Id，防止重复 / 迟到回执把状态倒退。</summary>
    public List<string> ReceiptIds { get; set; } = new();

    [JsonIgnore] public bool IsTerminal => Status is BoardSyncConsts.StVerified
        or BoardSyncConsts.StIgnored or BoardSyncConsts.StExpired
        or BoardSyncConsts.StRevoked or BoardSyncConsts.StConflict;
}

/// <summary>本机台账（存 Store.AppDir\board_sync.json），不进主数据包。</summary>
public class BoardSyncState
{
    public int Rev { get; set; } = BoardSyncConsts.Rev;
    public string ActorId { get; set; } = "";
    public List<BoardMessageRecord> Records { get; set; } = new();
}

/// <summary>教室机内容快照里的一条：稳定 Id + 字段 + 当前值签名。</summary>
public class BoardSnapshotItem
{
    public string Entity { get; set; } = BoardSyncConsts.EntityHomework;
    public string Id { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new();

    /// <summary>该条目各字段当前的样式（教师机选目标条目时带出，避免把人家的格式改掉）。</summary>
    public Dictionary<string, BoardTextStyle> Styles { get; set; } = new();
    public string Signature { get; set; } = "";

    /// <summary>classroom = 教室机快照；local = 教师机本机回退条目（教室机上不一定存在）。</summary>
    [JsonIgnore] public string Source { get; set; } = "classroom";
    [JsonIgnore] public string Brief => BoardSyncConsts.Brief(Fields);
    [JsonIgnore] public string Display => "〔" + BoardSyncConsts.EntityName(Entity) + "〕" + Brief;
    [JsonIgnore] public string SourceTag => Source == "classroom" ? "教室机" : "本机";
}

/// <summary>
/// 教室机把「本机现在有什么」发布到 <c>board/&lt;班级&gt;/snapshots/</c>，
/// 教师机据此选目标条目并带出 base 签名 —— 没有这一步，目标条目必然为空，
/// 「替换」就会退化成「新增」。
///
/// <para>★ 文件名固定为 <c>s-&lt;actorId&gt;.json</c>：每台设备只覆盖自己独占的那一份，
/// 绝不改写别人的文件；读端取 IsClassroom=true 且 CreatedAt 最新的一份。</para>
/// </summary>
public class BoardSnapshot
{
    public int Rev { get; set; } = BoardSyncConsts.Rev;
    public string ClassKey { get; set; } = "";
    public string ClassFolder { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public bool IsClassroom { get; set; } = true;

    /// <summary>本机内容指纹，没变就不重复写快照（避免每轮轮询刷文件）。</summary>
    public string ContentVersion { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<BoardSnapshotItem> Items { get; set; } = new();

    [JsonIgnore] public string FileName => "s-" + ActorId + ".json";

    // 方法本来就不会被序列化，别对它加 JsonIgnore（CS0592）
    public IEnumerable<BoardSnapshotItem> For(string entity)
        => (Items ?? new List<BoardSnapshotItem>()).Where(x => x.Entity == entity);
}
