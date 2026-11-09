using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 班级内容同步（二期）核心服务：结构化变更单 + 六态状态机 + 人点确认 + 按条目 Id 应用 + 回执核对。
///
/// <para>★ 单向发布链（manifest/data.cbpack、SchedulePackage）与本服务零耦合；
/// 本服务只读写作业 / 缺交 / 本班通知三份本机 JSON，绝不调用 RaiseScheduleChanged()。</para>
///
/// <para>★ 教室机是本机数据的<b>唯一写者</b>：教师机只写共享目录里的新文件，
/// 教室机按条目 Id 应用，替换用软删除 + 全文存档，增加用确定性派生 Id，
/// 删除写墓碑；已删条目（墓碑）拒绝任何字段改写（TOMBSTONE_GUARD）。</para>
///
/// <para>★ 本机数据一共四类：作业公示 / 缺交名单 / 值日安排 / 本班通知。
/// 值日安排与作业公示走的是<b>同一条</b>链路（同样的 Id、墓碑、存档、撤回），只是字段不同 ——
/// 加第四类时最容易漏的是快照与 LocalHasItem，漏了就会表现为「替换悄悄变成新增」。</para>
///
/// <para>★ 写盘一律经 Store 的原子写且检查 LastWriteError；失败绝不回「已保存」。
/// 壁纸重绘由 BackdropService 的 1.2s 防抖订阅负责，这里<b>不</b>同步 Apply()。</para>
/// </summary>
public static class BoardSyncService
{
    static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static string? _volatileActorId;

    /// <summary>最近一次明确的错误（坏文件 / 写盘失败 / 未配置数据源）。</summary>
    public static string? LastError { get; private set; }

    static string StatePath => Path.Combine(Store.AppDir, BoardSyncConsts.StateFile);

    public static bool IsEnabled => Store.LoadSettings().BoardSyncEnabled && GetTransport() != null;

    public static IBoardTransport? GetTransport() => BoardTransportFactory.Create(Store.LoadSettings());

    /// <summary>本机唯一设备标识：落盘一次；游客 / 诊断禁止写盘时至少保证本次进程内稳定。</summary>
    public static string EnsureActorId()
    {
        var s = Store.LoadSettings();
        if (!string.IsNullOrWhiteSpace(s.BoardSyncActorId)) return s.BoardSyncActorId;
        if (_volatileActorId != null) return _volatileActorId;

        string id = "a-" + BoardSyncIds.Hash(
            Environment.MachineName + "|" + Guid.NewGuid().ToString("N"))[..10];
        s.BoardSyncActorId = id;
        Store.SaveSettings(s);
        _volatileActorId = id;
        return id;
    }

    /// <summary>坏台账绝不静默当空表：直接抛 InvalidDataException，由界面 / 诊断大声报出来。</summary>
    public static BoardSyncState LoadState()
    {
        if (!File.Exists(StatePath)) return FreshState();
        string text = File.ReadAllText(StatePath);
        if (string.IsNullOrWhiteSpace(text)) return FreshState();
        try
        {
            var st = JsonSerializer.Deserialize<BoardSyncState>(text, Opt) ?? FreshState();
            st.Records ??= new List<BoardMessageRecord>();
            foreach (var r in st.Records) r.ReceiptIds ??= new List<string>();
            if (string.IsNullOrWhiteSpace(st.ActorId)) st.ActorId = EnsureActorId();
            return st;
        }
        catch (JsonException ex)
        {
            LastError = "本机同步台账损坏：" + ex.Message;
            throw new InvalidDataException("board_sync.json 损坏，已停止同步以免丢消息：" + ex.Message, ex);
        }
    }

    static BoardSyncState FreshState() => new() { ActorId = EnsureActorId() };

    public static bool SaveState(BoardSyncState st)
    {
        if (!Store.WriteJsonAtomic(StatePath, st))
        {
            LastError = Store.LastWriteError ?? "同步台账写盘失败";
            return false;
        }
        LastError = null;
        return true;
    }

    public static (bool Ok, string Message) Test()
    {
        var t = GetTransport();
        if (t == null) return (false, "未配置数据源（请先在「自动更新 / 数据源」里设置共享目录或 FTP）。");
        return t.Test();
    }

    // ---------------- 发送端（教师机） ----------------

    /// <summary>
    /// 发送一条结构化变更单。draft 里 MsgId / ActorId / FileName 由本服务补齐；
    /// 同一 draft 重发会得到新 MsgId，重复读同一文件则由 MsgId 幂等挡住。
    /// </summary>
    public static (bool Ok, string Message, BoardChangeOrder? Order) Send(
        BoardChangeOrder draft, bool bypassRole = false)
    {
        // 台账「读→改→写」与后台轮询共用一把锁：轮询正在落账时发送，两边都不能丢记录
        lock (_stateGate) { return SendCore(draft, bypassRole); }
    }

    static (bool Ok, string Message, BoardChangeOrder? Order) SendCore(
        BoardChangeOrder draft, bool bypassRole = false)
    {
        LastError = null;
        if (!Store.EnablePersistence)
            return (false, "游客模式不写盘，变更单没有发出。请先登录。", null);
        // ★ 发布权限在这里兜最后一次：界面按钮会被禁灰，但快捷键 / 自动化仍可能绕过。
        //   电教管理员默认没有发布权限（他负责在教室机确认应用，不能自己发自己批）。
        //   bypassRole 只给「撤回」这种收尾动作与离线诊断用，不是新的发布入口。
        if (!bypassRole && !UserSession.CanPublishInfo)
            return (false, UserSession.DenyPublishInfo, null);

        var t = GetTransport();
        if (t == null) return (false, "未配置数据源，变更单没有发出。", null);

        var st = LoadState();
        var o = draft;
        o.Rev = BoardSyncConsts.Rev;
        o.MsgId = BoardSyncIds.New("m");
        o.ActorId = st.ActorId;
        o.ClassFolder = BoardSyncIds.ClassFolder(o.ClassKey);
        if (string.IsNullOrWhiteSpace(o.ValidDay)) o.ValidDay = DateTime.Today.ToString("yyyy-MM-dd");
        o.CreatedAt = DateTime.Now;
        if (string.IsNullOrWhiteSpace(o.Summary))
            o.Summary = BoardSyncConsts.EntityName(o.Entity) + " · " + BoardSyncConsts.OpName(o.Op);

        var rec = new BoardMessageRecord
        {
            MsgId = o.MsgId,
            Direction = BoardSyncConsts.DirOut,
            Status = BoardSyncConsts.StSent,
            Order = o,
            UpdatedAt = DateTime.Now
        };
        st.Records.Add(rec);
        if (!SaveState(st)) return (false, LastError ?? "台账写盘失败，变更单没有发出。", null);

        try
        {
            t.WriteNew(o.ClassFolder, BoardSyncConsts.Messages, o.FileName,
                       JsonSerializer.Serialize(o, Opt));
            // WriteNew 返回 false = 同名文件已存在（极少见，等价于「已经发过」），按幂等成功处理
        }
        catch (Exception ex)
        {
            // 共享盘断连：本机台账保留，明确标「待重试」，绝不假装已发出
            rec.Status = BoardSyncConsts.StRetry;
            rec.Note = "共享目录写入失败：" + ex.Message;
            SaveState(st);
            LastError = rec.Note;
            return (false, "变更单已存本机但没写进共享目录（待重试）：" + ex.Message, o);
        }
        return (true, "已发出：" + o.Summary, o);
    }

    /// <summary>撤回只发一条新的 revoke 消息，绝不改写原消息文件。</summary>
    public static (bool Ok, string Message) Revoke(string msgId)
    {
        lock (_stateGate) { return RevokeCore(msgId); }
    }

    static (bool Ok, string Message) RevokeCore(string msgId)
    {
        var st = LoadState();
        var rec = st.Records.FirstOrDefault(r => r.MsgId == msgId && r.Direction == BoardSyncConsts.DirOut);
        if (rec == null) return (false, "找不到这条已发出的变更单。");
        if (rec.Status == BoardSyncConsts.StRevoked) return (true, "这条变更单已经撤回过。");

        var res = Send(new BoardChangeOrder
        {
            ClassKey = rec.Order.ClassKey,
            Entity = rec.Order.Entity,
            Op = BoardSyncConsts.OpRevoke,
            ItemId = rec.Order.ItemId,
            RevokeOf = msgId,
            Sender = rec.Order.Sender,
            SenderRole = rec.Order.SenderRole,
            SenderSubject = rec.Order.SenderSubject,
            Summary = "撤回：" + rec.Order.Summary
        }, bypassRole: true);   // 撤回是收尾动作，不是新发布；不受角色发布权限影响
        if (!res.Ok) return (false, res.Message);

        st = LoadState();
        rec = st.Records.FirstOrDefault(r => r.MsgId == msgId);
        if (rec != null)
        {
            rec.Status = BoardSyncConsts.StRevoked;
            rec.Note = "教师机已撤回（教室机若已应用，需人工核对）";
            rec.UpdatedAt = DateTime.Now;
            SaveState(st);
        }
        return (true, "已发出撤回通知。教室机尚未应用的会自动标记「已撤回」。");
    }

    // ---------------- 轮询（后台线程执行，绝不占 UI 线程） ----------------

    /// <summary>
    /// 本机台账的「读 → 改 → 写」互斥。轮询在后台线程跑，界面上的「已阅 / 应用」同时在改，
    /// 没有这把锁就会互相覆盖 —— 一次 1 秒的共享盘轮询足以把用户刚点下的「已阅」冲回「已送达」。
    /// <para>★ 锁内只做内存操作 + 一次本地写盘，绝不在锁里访问共享盘。</para>
    /// </summary>
    static readonly object _stateGate = new();

    /// <summary>同一时刻只允许一轮轮询：定时器与手动「立即检查」不会叠着跑。</summary>
    static int _pulling;

    /// <summary>上一轮轮询里「发送方要求课间全屏提醒」的变更单摘要（没有就是空表）。</summary>
    public static List<string> LastPullForce { get; private set; } = new();

    /// <summary>
    /// 上一轮轮询里「发送方要求语音播报」的文本（没有就是空表）。
    /// <para>Immediate=true 表示「收到就播」（上课也播），false 表示课间才播。
    /// 与 <see cref="LastPullForce"/> 一样只在轮询线程写、由界面线程读，所以下一轮开头就清空。</para>
    /// </summary>
    public static List<(string Text, bool Immediate)> LastPullVoice { get; private set; } = new();

    /// <summary>
    /// 上一轮轮询里「教室机按教师机指定自动增加」的条数（收件提示卡据此把「待确认」条数算对）。
    /// <para>与 LastPullForce / LastPullVoice 一样：后台线程写、界面线程读，下一轮开头清空。</para>
    /// </summary>
    public static int LastPullAutoApplied { get; private set; }

    /// <summary>
    /// 一次轮询：列目录（快路径）→ 只处理没见过的 MsgId → 写「已送达」回执 → 读回执推进状态。
    /// <para>★ 全程网络 I/O，<b>必须</b>在后台线程调用。早先它跑在 DispatcherTimer 的 UI 线程里，
    /// 共享盘慢一次整个界面就冻住 —— 那是「操作有时候卡顿」的主因。</para>
    /// </summary>
    public static (int Added, int Bad, string Report) Pull(IEnumerable<string> classKeys)
    {
        LastError = null;
        if (Interlocked.CompareExchange(ref _pulling, 1, 0) != 0)
            return (0, 0, "上一轮检查还没结束，本次已跳过。");
        try { return PullCore(classKeys); }
        finally { Interlocked.Exchange(ref _pulling, 0); }
    }

    static (int Added, int Bad, string Report) PullCore(IEnumerable<string> classKeys)
    {
        LastPullForce = new List<string>();
        LastPullVoice = new List<(string Text, bool Immediate)>();
        LastPullAutoApplied = 0;
        var keys = (classKeys ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k))
                                                       .Distinct(StringComparer.Ordinal).ToArray();
        var t = GetTransport();
        if (t == null) return (0, 0, "未配置数据源，跳过轮询。");

        var known = LoadState();          // 只当快照用：判 ActorId / 已知 MsgId，真正落账在锁内重读
        int bad = 0;
        var notes = new List<string>();
        var incoming = new List<(string Cf, BoardChangeOrder Order)>();

        // ① 纯网络阶段：列目录 + 读消息文件，不碰本机台账
        foreach (var ck in keys)
        {
            string cf = BoardSyncIds.ClassFolder(ck);
            foreach (var name in SafeList(t, cf, BoardSyncConsts.Messages, notes))
            {
                if (!name.StartsWith("m-", StringComparison.Ordinal) ||
                    !name.EndsWith(".json", StringComparison.Ordinal)) continue;

                string? text = SafeRead(t, cf, BoardSyncConsts.Messages, name, notes);
                if (text == null) continue;

                BoardChangeOrder o;
                try
                {
                    o = JsonSerializer.Deserialize<BoardChangeOrder>(text, Opt)
                        ?? throw new JsonException("消息内容为空");
                    if (o.Rev != BoardSyncConsts.Rev)
                        throw new InvalidDataException("不认识的 Rev=" + o.Rev);
                    if (string.IsNullOrWhiteSpace(o.MsgId))
                        throw new InvalidDataException("缺少 MsgId");
                }
                catch (Exception ex)
                {
                    bad++;
                    notes.Add("坏文件 " + name + "：" + ex.Message);
                    LastError = string.Join("；", notes);
                    continue;      // 大声失败但绝不让它拖垮整轮轮询
                }

                if (o.ActorId == known.ActorId) continue;                  // 自己发的，不当收件
                if (known.Records.Any(r => r.MsgId == o.MsgId)) continue;   // 幂等
                incoming.Add((cf, o));
            }
        }

        // ② 台账阶段：锁内重读一次最新台账再落账，避免和界面操作互相覆盖
        int added = 0;
        var receipts = new List<BoardReceipt>();
        var autoIds = new List<string>();
        // 「直接增加」只是教师机的请求：教室机可以用 BoardSyncAutoApply 整体关掉，
        // 关掉后这类单退回普通收件箱，绝不丢消息。教师机 / 游客一律不自动应用。
        bool allowAuto = IsClassroomMachine && Store.LoadSettings().BoardSyncAutoApply;
        lock (_stateGate)
        {
            var st = LoadState();
            foreach (var (cf, o) in incoming)
            {
                if (st.Records.Any(r => r.MsgId == o.MsgId)) continue;   // 锁内再判一次
                st.Records.Add(new BoardMessageRecord
                {
                    MsgId = o.MsgId,
                    Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StDelivered,
                    Order = o,
                    UpdatedAt = DateTime.Now
                });
                added++;
                if (o.ForceBreakReminder)
                {
                    // 新单带 ForceText = 本次发出的完整内容（标题 + 正文 + 新增字段）；
                    // 老单没有这个字段，回退到 Summary，保证历史变更单的全屏提醒不丢字、不空白。
                    string forceText = !string.IsNullOrWhiteSpace(o.ForceText)
                        ? o.ForceText.Trim() : o.Summary;
                    if (!string.IsNullOrWhiteSpace(forceText)) LastPullForce.Add(forceText);
                }
                if (!string.IsNullOrWhiteSpace(o.VoiceText))
                    LastPullVoice.Add((o.VoiceText.Trim(), o.VoiceImmediate));

                // 只自动「新增一条通知」：替换 / 删除 / 整周值日单 / 撤回全部仍要人工裁决。
                if (allowAuto && o.AutoApply && o.Entity == BoardSyncConsts.EntityNotice
                    && o.Op == BoardSyncConsts.OpUpsert && !o.IsRevoke && !o.IsWeeklyDuty)
                    autoIds.Add(o.MsgId);

                if (o.IsRevoke && !string.IsNullOrWhiteSpace(o.RevokeOf))
                {
                    var target = st.Records.FirstOrDefault(r => r.MsgId == o.RevokeOf);
                    if (target != null && !target.IsTerminal)
                    {
                        target.Status = BoardSyncConsts.StRevoked;
                        target.Note = "教师机已撤回";
                        target.UpdatedAt = DateTime.Now;
                    }
                    else if (target != null)
                    {
                        target.Note = "教师机已撤回，但本机已经应用过，请人工核对";
                        target.UpdatedAt = DateTime.Now;
                    }
                }

                receipts.Add(new BoardReceipt
                {
                    ReceiptId = BoardSyncIds.New("rc"),
                    MsgId = o.MsgId,
                    ActorId = st.ActorId,
                    ClassFolder = cf,
                    State = BoardSyncConsts.RcDelivered,
                    DecisionId = "delivered",
                    Note = "教室机已收到变更单"
                });
            }

            ApplyRevokes(st);
            ExpireOld(st);       // 日期过期与回执无关，先结算一次
            SaveState(st);
        }

        // ③ 网络阶段：给新收下的变更单写「已送达」回执（台账已经先落盘，回执失败下轮可重试）
        foreach (var rc in receipts)
        {
            try { WriteReceipt(t, rc); }
            catch (Exception ex) { notes.Add("回执写入失败：" + ex.Message); }
        }

        // ④ 读回执（网络）→ ⑤ 锁内推进状态（本机）
        var (rcs, rcNotes) = ReadReceiptFiles(t, keys);
        notes.AddRange(rcNotes);
        int updated;
        lock (_stateGate)
        {
            var st2 = LoadState();
            updated = ApplyReceipts(st2, rcs);
            ExpireOld(st2);
            SaveState(st2);
        }

        // ⑥ 教师机指定「直接增加」的通知：教室机自己按「增加」落地并回执。
        //    放在锁外逐条走 Apply()（它自己会拿 _stateGate），网络也在锁外，不会把轮询卡住；
        //    ApplyCore 里的 auto 守卫保证它绝不会覆盖人工裁决 / 已终结 / 已撤回的记录。
        if (autoIds.Count > 0)
        {
            foreach (string id in autoIds)
            {
                var res = Apply(id, BoardSyncConsts.DecAppend, bypassRole: true, auto: true);
                if (res.Ok) LastPullAutoApplied++;
                else notes.Add("自动增加跳过：" + res.Message);
            }
        }

        // 教室机每轮顺手刷新「本机现在有什么」快照，教师机据此选目标条目。
        // 失败只记进报告，绝不拖垮轮询；教师机 / 游客不发布。
        if (IsClassroomMachine)
        {
            try
            {
                var snap = PublishSnapshot();
                if (!snap.Ok) notes.Add("快照：" + snap.Message);
            }
            catch (Exception ex) { notes.Add("快照异常：" + ex.Message); }
        }

        string report = $"新消息 {added} 条，回执推进 {updated} 条，坏文件 {bad} 个";
        if (notes.Count > 0) report += "；" + string.Join("；", notes.Take(6));
        return (added, bad, report);
    }

    /// <summary>教师机 / 教室机共用：只读自己订阅班级的回执桶（纯网络，不碰台账）。</summary>
    static (List<BoardReceipt> Items, List<string> Notes) ReadReceiptFiles(
        IBoardTransport t, string[] keys)
    {
        var items = new List<BoardReceipt>();
        var notes = new List<string>();
        foreach (var ck in keys)
        {
            string cf = BoardSyncIds.ClassFolder(ck);
            foreach (var name in SafeList(t, cf, BoardSyncConsts.Receipts, notes))
            {
                if (!name.StartsWith("r-", StringComparison.Ordinal) ||
                    !name.EndsWith(".json", StringComparison.Ordinal)) continue;

                string? text = SafeRead(t, cf, BoardSyncConsts.Receipts, name, notes);
                if (text == null) continue;

                try
                {
                    var rp = JsonSerializer.Deserialize<BoardReceipt>(text, Opt)
                             ?? throw new JsonException("回执内容为空");
                    if (string.IsNullOrWhiteSpace(rp.ReceiptId) || string.IsNullOrWhiteSpace(rp.MsgId))
                        throw new InvalidDataException("回执缺少 ReceiptId / MsgId");
                    items.Add(rp);
                }
                catch (Exception ex)
                {
                    notes.Add("坏回执 " + name + "：" + ex.Message);
                    LastError = "坏回执：" + name;
                }
            }
        }
        return (items, notes);
    }

    /// <summary>把读到的回执按 ReceiptId 幂等推进到本机台账（锁内调用，只做内存操作）。</summary>
    static int ApplyReceipts(BoardSyncState st, List<BoardReceipt> items)
    {
        int updated = 0;
        // ★ 回执文件的枚举顺序不可控：同一批里 restored（已撤回）可能排在 applied 前面，
        //   后到的 applied 会把状态重新算成「已核对」，撤回就白撤了。
        //   这里显式把 restored 放到最后处理 —— 撤回是终局，必须压过任何历史回执。
        foreach (var rp in items.OrderBy(x => x.State == BoardSyncConsts.RcRestored ? 1 : 0))
        {
            var rec = st.Records.FirstOrDefault(r => r.MsgId == rp.MsgId);
            if (rec == null) continue;                               // 回执比消息先到：下轮再读
            if (rec.ReceiptIds.Contains(rp.ReceiptId, StringComparer.Ordinal)) continue;
            rec.ReceiptIds.Add(rp.ReceiptId);

            // 已撤回是终局：迟到 / 乱序的 delivered / seen / applied / ignored / conflict
            // 都不能把它打回「已核对」或「已应用」。
            if (rec.Status == BoardSyncConsts.StRevoked && rp.State != BoardSyncConsts.RcRestored)
            {
                rec.UpdatedAt = DateTime.Now;
                continue;
            }

            switch (rp.State)
            {
                case BoardSyncConsts.RcDelivered:
                    Advance(rec, BoardSyncConsts.StDelivered); break;
                case BoardSyncConsts.RcSeen:
                    rec.Seen = true;
                    Advance(rec, BoardSyncConsts.StSeen); break;
                case BoardSyncConsts.RcIgnored:
                    rec.Decision = rp.Decision;
                    rec.DecisionId = rp.DecisionId;
                    rec.Note = rp.Note;
                    CopyReceiptDetail(rec, rp);
                    rec.Status = BoardSyncConsts.StIgnored; break;
                case BoardSyncConsts.RcConflict:
                    rec.Status = BoardSyncConsts.StConflict;
                    rec.Note = rp.Note;
                    CopyReceiptDetail(rec, rp); break;
                    case BoardSyncConsts.RcApplied:
                        rec.Decision = rp.Decision;
                        rec.DecisionId = rp.DecisionId;
                        rec.DataVersion = rp.DataVersion;
                        rec.AppliedItemId = rp.AppliedItemId;
                        rec.Note = rp.Note;
                        CopyReceiptDetail(rec, rp);
                        rec.Status = BoardSyncConsts.StApplied;
                        Verify(rec, rp);          // 有 dataVersion + 快照一致 → 已核对，否则冲突
                        break;

                    // 教室机现场撤回了这次替换 / 删除：教师机台账落成「已撤回」，
                    // 不能让一条已经还原的消息继续显示「已应用」——那会让人以为改动还在。
                    case BoardSyncConsts.RcRestored:
                        rec.Status = BoardSyncConsts.StRevoked;
                        rec.Note = string.IsNullOrWhiteSpace(rp.Note)
                            ? "教室机已撤回这次变更，原文已还原。" : rp.Note;
                        CopyReceiptDetail(rec, rp);
                        break;
            }
            rec.UpdatedAt = DateTime.Now;
            updated++;
        }
        return updated;
    }

    /// <summary>回执明细原样带进本机台账，供核对视图排版；不从变更单猜，也不重新拼。</summary>
    static void CopyReceiptDetail(BoardMessageRecord rec, BoardReceipt rp)
    {
        rec.AppliedFields = rp.AppliedFields == null
            ? new Dictionary<string, string>() : new Dictionary<string, string>(rp.AppliedFields);
        rec.AppliedStyles = rp.AppliedStyles == null
            ? new Dictionary<string, BoardTextStyle>() : new Dictionary<string, BoardTextStyle>(rp.AppliedStyles);
        rec.ArchivedFields = rp.ArchivedFields == null
            ? new Dictionary<string, string>() : new Dictionary<string, string>(rp.ArchivedFields);
        rec.ArchivedStyles = rp.ArchivedStyles == null
            ? new Dictionary<string, BoardTextStyle>() : new Dictionary<string, BoardTextStyle>(rp.ArchivedStyles);
        rec.ArchivedSnapshot = rp.ArchivedSnapshot ?? "";
        // ★ 整周替换的两个凭证必须原样带回：撤回要靠整表存档还原、要靠落地 Id 列表撤掉新行。
        //   留空等于把「还能不能再撤回」的证据擦掉（r58 撤回回执踩过同一个坑）。
        rec.ArchivedBoard = rp.ArchivedBoard ?? "";
        rec.AppliedItemIds = rp.AppliedItemIds == null
            ? new List<string>() : new List<string>(rp.AppliedItemIds);
        rec.TargetText = rp.TargetText ?? "";
        rec.DecisionBy = rp.DecisionBy ?? "";
        rec.ReceiptTime = rp.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>统一结算撤回：原消息可能和撤回消息在同一轮到达，顺序不保证。</summary>
    static void ApplyRevokes(BoardSyncState st)
    {
        foreach (var rv in st.Records
                 .Where(r => r.Order.IsRevoke && !string.IsNullOrWhiteSpace(r.Order.RevokeOf))
                 .ToList())
        {
            var target = st.Records.FirstOrDefault(r => r.MsgId == rv.Order.RevokeOf);
            if (target == null || target.Status == BoardSyncConsts.StRevoked) continue;
            if (!target.IsTerminal)
            {
                target.Status = BoardSyncConsts.StRevoked;
                target.Note = "教师机已撤回";
            }
            else
            {
                target.Note = "教师机已撤回，但本机已经应用过，请人工核对";
            }
            target.UpdatedAt = DateTime.Now;
        }
    }

    /// <summary>状态只允许按状态机顺序前进，迟到回执不能把「已核对」打回「已送达」。</summary>
    static void Advance(BoardMessageRecord rec, string next)
    {
        int a = Array.IndexOf(BoardSyncConsts.StatusOrder, rec.Status);
        int b = Array.IndexOf(BoardSyncConsts.StatusOrder, next);
        if (b >= a) rec.Status = next;
    }

    /// <summary>核对：回执里的应用后快照必须与变更单一致，且带 dataVersion，才算「已核对」。</summary>
    static void Verify(BoardMessageRecord rec, BoardReceipt rp)
    {
        var o = rec.Order;
        if (o.IsRevoke) return;
        // 「忽略 / 保留」是旁支终态：本来就没写盘、没有 dataVersion，不参与内容核对，
        // 也不能被下面的「缺 dataVersion」误判成冲突。
        if (rp.Decision is BoardSyncConsts.DecIgnore or BoardSyncConsts.DecKeep) return;
        if (string.IsNullOrWhiteSpace(rp.DataVersion))
        {
            rec.Status = BoardSyncConsts.StConflict;
            rec.Note = "回执缺 dataVersion，无法核对";
            return;
        }
        foreach (var kv in o.Fields)
        {
            if (kv.Key is "bold" or "color" or "sizeLevel" or "pinned") continue;  // 格式字段不参与内容核对
            string want = (kv.Value ?? "").Trim();
            string got = rp.AppliedFields.TryGetValue(kv.Key, out var v) ? (v ?? "").Trim() : "";
            if (!string.Equals(want, got, StringComparison.Ordinal))
            {
                rec.Status = BoardSyncConsts.StConflict;
                rec.Note = $"核对不一致：{kv.Key} 期望「{want}」实际「{got}」";
                return;
            }
        }
        rec.Status = BoardSyncConsts.StVerified;
        // 别把回执里那句人话覆盖成纯技术串：核对结论追加在后面，排版时两段都看得到
        rec.Note = (string.IsNullOrWhiteSpace(rp.Note) ? "" : rp.Note + " ") +
                   "✔ 已核对：教室机落地内容与变更单一致（dataVersion=" + rp.DataVersion + "）。";
    }

    static void ExpireOld(BoardSyncState st)
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        foreach (var r in st.Records)
        {
            if (r.Direction != BoardSyncConsts.DirIn || r.IsTerminal) continue;
            if (string.IsNullOrWhiteSpace(r.Order.ValidDay)) continue;
            if (string.CompareOrdinal(r.Order.ValidDay, today) < 0)
            {
                r.Status = BoardSyncConsts.StExpired;
                r.Note = "变更单已过期（validDay=" + r.Order.ValidDay + "）";
            }
        }
    }

    /// <summary>打开消息 = 「已阅」。已阅不等于已改好，只有应用回执才会推进到已应用 / 已核对。</summary>
    public static (bool Ok, string Message) MarkSeen(string msgId)
    {
        lock (_stateGate) { return MarkSeenCore(msgId); }
    }

    static (bool Ok, string Message) MarkSeenCore(string msgId)
    {
        var st = LoadState();
        var rec = st.Records.FirstOrDefault(r => r.MsgId == msgId);
        if (rec == null) return (false, "找不到这条消息。");
        if (rec.Seen) return (true, "已阅（重复打开不重复发回执）。");

        rec.Seen = true;
        Advance(rec, BoardSyncConsts.StSeen);
        rec.UpdatedAt = DateTime.Now;
        SaveState(st);

        WriteReceipt(null, new BoardReceipt
        {
            ReceiptId = BoardSyncIds.New("rc"),
            MsgId = msgId,
            ActorId = st.ActorId,
            ClassFolder = rec.Order.ClassFolder,
            State = BoardSyncConsts.RcSeen,
            DecisionId = "seen",
            Note = "电教管理员已打开查看"
        });
        return (true, "已标记已阅，并回执教师机。");
    }

    static bool WriteReceipt(IBoardTransport? t, BoardReceipt rp)
    {
        t ??= GetTransport();
        if (t == null) return false;
        try
        {
            return t.WriteNew(rp.ClassFolder, BoardSyncConsts.Receipts, rp.FileName,
                              JsonSerializer.Serialize(rp, Opt));
        }
        catch (Exception ex)
        {
            // 回执写失败不能让本机应用回滚（数据已经落盘），下轮轮询会补发
            LastError = "回执写入失败（下轮补发）：" + ex.Message;
            return false;
        }
    }

    /// <summary>列目录 / 读文件失败时明确记入报告并让本轮继续，绝不因一段断连拖垮整个轮询。</summary>
    static IReadOnlyList<string> SafeList(IBoardTransport t, string cf, string bucket, List<string> notes)
    {
        try { return t.List(cf, bucket); }
        catch (Exception ex)
        {
            string msg = "列目录失败 " + cf + "/" + bucket + "：" + ex.Message;
            notes.Add(msg);
            LastError = msg;
            return Array.Empty<string>();
        }
    }

    static string? SafeRead(IBoardTransport t, string cf, string bucket, string name, List<string> notes)
    {
        try { return t.Read(cf, bucket, name); }
        catch (Exception ex)
        {
            string msg = "读取失败 " + name + "：" + ex.Message;
            notes.Add(msg);
            LastError = msg;
            return null;
        }
    }

    // ---------------- 接收端（教室机）应用 ----------------

    /// <summary>
    /// 电教管理员裁决并应用。bypassRole 只给 --sync-check 诊断用：
    /// 正常运行必须由教室机默认身份（电教管理员）单人应用。
    /// <para>targetItemId / targetBaseSignature：教师机没拿到教室机快照时，
    /// 由电教管理员在教室机现场指定要替换 / 删除的本机条目及其当前值签名。</para>
    /// </summary>
    public static (bool Ok, string Message, BoardReceipt? Receipt) Apply(
        string msgId, string decision, bool bypassRole = false,
        string? targetItemId = null, string? targetBaseSignature = null, bool auto = false)
    {
        // 应用要改「台账 + 本机作业/缺交文件」，全程独占台账：后台轮询此刻只等，不会插进来写
        lock (_stateGate)
        {
            return ApplyCore(msgId, decision, bypassRole, targetItemId, targetBaseSignature, auto);
        }
    }

    static (bool Ok, string Message, BoardReceipt? Receipt) ApplyCore(
        string msgId, string decision, bool bypassRole,
        string? targetItemId, string? targetBaseSignature, bool auto = false)
    {
        LastError = null;
        var st = LoadState();
        var rec = st.Records.FirstOrDefault(r => r.MsgId == msgId);
        if (rec == null) return (false, "找不到这条变更单。", null);
        // 自动增加只认「还没处理过的新收件」，绝不覆盖人工裁决 / 已终结 / 已撤回的单
        if (auto)
        {
            if (decision != BoardSyncConsts.DecAppend)
                return (false, "自动应用只允许「增加」。", null);
            if (rec.Direction != BoardSyncConsts.DirIn || rec.IsTerminal ||
                !string.IsNullOrWhiteSpace(rec.Decision) ||
                rec.Status is not (BoardSyncConsts.StDelivered or BoardSyncConsts.StSeen))
                return (false, "这条已经有裁决或已终结，跳过自动增加。", null);
            if (rec.Status == BoardSyncConsts.StRevoked)
                return (false, "教师机已撤回这条，不再自动应用。", null);
        }
        if (!bypassRole && !UserSession.IsStudent)
            return (false, "只有电教管理员（教室机）能应用变更单；教师机只能发送与核对。", null);
        if (!BoardSyncConsts.DecisionsFor(rec.Order.Op).Contains(decision, StringComparer.Ordinal))
            return (false, "这个操作不支持裁决「" + decision + "」。", null);
        if (rec.Order.IsRevoke)
            return (false, "撤回通知不需要应用。", null);

        string decisionId = BoardSyncIds.Hash(msgId + "|" + decision)[..10];
        if (rec.DecisionId == decisionId && rec.IsTerminal)
            return (true, "这条变更单已经按同一裁决处理过（幂等跳过）。", null);

        var o = rec.Order;
        if (!string.IsNullOrWhiteSpace(o.ValidDay) &&
            string.CompareOrdinal(o.ValidDay, DateTime.Today.ToString("yyyy-MM-dd")) < 0)
        {
            rec.Status = BoardSyncConsts.StExpired;
            SaveState(st);
            return (false, "变更单已过期，未应用。", null);
        }

        // 教师机没拿到教室机快照时 ItemId 可能为空（这正是「替换变新增」的根因）。
        // 教室机可现场指定一条本机条目；没指定且本机也没有该 Id 时，替换 / 删除必须拒绝，
        // 绝不能悄悄走新增分支。
        string targetId = string.IsNullOrWhiteSpace(targetItemId) ? o.ItemId : targetItemId.Trim();
        string baseSig = targetBaseSignature ?? o.BaseSignature;
        bool manualTarget = !string.Equals(targetId, o.ItemId, StringComparison.Ordinal);
        // 整周「替换」的对象是整张值日表，没有单一目标条目 —— 这条守卫对它不适用。
        if (!o.IsWeeklyDuty &&
            decision is BoardSyncConsts.DecReplace or BoardSyncConsts.DecDelete &&
            !LocalHasItem(o.Entity, targetId))
        {
            string why = string.IsNullOrWhiteSpace(targetId) ? "（变更单没带目标 Id）" : "（" + targetId + "）";
            return (false, "教室里找不到要" +
                           (decision == BoardSyncConsts.DecReplace ? "替换" : "删除") +
                           "的目标条目" + why + "。点该裁决时可从本机现有条目里指定一条；" +
                           "若本来就是要新增一条，请改选「增加」。", null);
        }

        bool drift = false;
        string archived = "";
        var archivedFields = new Dictionary<string, string>();
        var archivedStyles = new Dictionary<string, BoardTextStyle>();
        string appliedId = "";
        var appliedItemIds = new List<string>();
        string archivedBoard = "";      // 整周替换前的整张值日便签 JSON
        string dataVersion = "";
        var appliedFields = new Dictionary<string, string>(o.Fields);
        var appliedStyles = new Dictionary<string, BoardTextStyle>();

        if (decision is not (BoardSyncConsts.DecIgnore or BoardSyncConsts.DecKeep))
        {
            switch (o.Entity)
            {
                case BoardSyncConsts.EntityHomework:
                {
                    var board = Store.LoadHomework() ?? new HomeworkBoard();
                    board.Items ??= new List<HomeworkItem>();
                    var found = board.Items.FirstOrDefault(x => x.Id == targetId);
                    if (found != null && found.Deleted)
                        return (false, "目标条目已是墓碑，拒绝改写（TOMBSTONE_GUARD）。", null);

                    drift = found != null && !string.IsNullOrWhiteSpace(baseSig) &&
                            BoardSyncIds.Signature(o.Entity, targetId, FieldsOf(found), StylesOf(found)) != baseSig;

                    if (decision is BoardSyncConsts.DecDelete or BoardSyncConsts.DecReplace)
                    {
                        if (found == null)
                            return (false, "本机找不到目标作业条目，未改动；请指定目标或改选「增加」。", null);
                        archived = JsonSerializer.Serialize(found, Opt);
                        archivedFields = FieldsOf(found);
                        archivedStyles = StylesOf(found);
                        found.Deleted = true;         // 软删除，被覆盖版本全文进回执存档，可撤回
                        if (decision == BoardSyncConsts.DecDelete) appliedId = found.Id;
                    }
                    if (decision != BoardSyncConsts.DecDelete)
                    {
                        appliedId = BoardSyncIds.DerivedItemId(targetId, o.MsgId);
                        if (!board.Items.Any(x => x.Id == appliedId && !x.Deleted))
                        {
                            var it = new HomeworkItem
                            {
                                Id = appliedId, Parent = targetId, Origin = "remote", Deleted = false
                            };
                            // 发布窗不再下发字体格式（Styles 恒为空）：
                            // 「替换」沿用被覆盖条目的现有样式，别把教室机自己调好的字号 / 颜色压回默认；
                            // 「新增」没有 found，就用 new HomeworkItem() 的出厂值（特大 + 默认墨色）。
                            // 老变更单仍带 Styles 时按原逻辑整块覆盖，历史数据行为不变。
                            if (found != null && (o.Styles == null || o.Styles.Count == 0))
                                InheritHomeworkStyle(it, found);
                            FillHomework(it, o.Fields, o.Styles);
                            board.Items.Add(it);
                        }
                    }
                    Store.SaveHomework(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError, null);
                    dataVersion = DataVersion(Store.LoadHomework());
                    var after = (Store.LoadHomework()?.Items ?? new List<HomeworkItem>())
                                .FirstOrDefault(x => x.Id == appliedId);
                    appliedFields = after != null ? FieldsOf(after) : new Dictionary<string, string>(o.Fields);
                    appliedStyles = after != null ? StylesOf(after) : new Dictionary<string, BoardTextStyle>();
                    break;
                }

                case BoardSyncConsts.EntityMissing:
                {
                    var board = Store.LoadMissingBoard() ?? new MissingBoard();
                    board.Items ??= new List<MissingHomeworkItem>();
                    var found = board.Items.FirstOrDefault(x => x.Id == targetId);
                    if (found != null && found.Deleted)
                        return (false, "目标条目已是墓碑，拒绝改写（TOMBSTONE_GUARD）。", null);

                    drift = found != null && !string.IsNullOrWhiteSpace(baseSig) &&
                            BoardSyncIds.Signature(o.Entity, targetId, FieldsOf(found), StylesOf(found)) != baseSig;

                    if (decision is BoardSyncConsts.DecDelete or BoardSyncConsts.DecReplace)
                    {
                        if (found == null)
                            return (false, "本机找不到目标缺交条目，未改动；请指定目标或改选「增加」。", null);
                        archived = JsonSerializer.Serialize(found, Opt);
                        archivedFields = FieldsOf(found);
                        archivedStyles = StylesOf(found);
                        found.Deleted = true;
                        if (decision == BoardSyncConsts.DecDelete) appliedId = found.Id;
                    }
                    if (decision != BoardSyncConsts.DecDelete)
                    {
                        appliedId = BoardSyncIds.DerivedItemId(targetId, o.MsgId);
                        if (!board.Items.Any(x => x.Id == appliedId && !x.Deleted))
                        {
                            var it = new MissingHomeworkItem
                            {
                                Id = appliedId, Parent = targetId, Origin = "remote", Deleted = false
                            };
                            if (found != null && (o.Styles == null || o.Styles.Count == 0))
                                InheritMissingStyle(it, found);   // 替换沿用教室机旧样式
                            FillMissing(it, o.Fields, o.Styles);
                            board.Items.Add(it);
                        }
                    }
                    Store.SaveMissingBoard(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError, null);
                    dataVersion = DataVersion(Store.LoadMissingBoard());
                    var after = (Store.LoadMissingBoard()?.Items ?? new List<MissingHomeworkItem>())
                                .FirstOrDefault(x => x.Id == appliedId);
                    appliedFields = after != null ? FieldsOf(after) : new Dictionary<string, string>(o.Fields);
                    appliedStyles = after != null ? StylesOf(after) : new Dictionary<string, BoardTextStyle>();
                    break;
                }

                case BoardSyncConsts.EntityNotice:
                {
                    // ★ 必须用全量（含墓碑）：拿过滤后的列表写回会把墓碑整批丢掉，等于又变回物理删除，
                    //   一条已撤回的通知就会在下一次拉取时被重新加回来。
                    var list = Store.LoadNotificationsAll();
                    var found = list.FirstOrDefault(x => x.Id == targetId && !x.Deleted);
                    drift = found != null && !string.IsNullOrWhiteSpace(baseSig) &&
                            BoardSyncIds.Signature(o.Entity, targetId, FieldsOf(found)) != baseSig;

                    if (decision is BoardSyncConsts.DecDelete or BoardSyncConsts.DecReplace)
                    {
                        if (found == null)
                            return (false, "本机找不到目标通知，未改动；请指定目标或改选「增加」。", null);
                        archived = JsonSerializer.Serialize(found, Opt);
                        archivedFields = FieldsOf(found);
                        // 打墓碑而不是移除：物理删除会让晚到的旧指令把它「复活」
                        found.Deleted = true;
                        found.DeletedAt = DateTime.Now;
                        if (decision == BoardSyncConsts.DecDelete) appliedId = found.Id;
                    }
                    if (decision != BoardSyncConsts.DecDelete)
                    {
                        appliedId = BoardSyncIds.DerivedItemId(targetId, o.MsgId);
                        // 命中墓碑也算「已经有了」—— 这正是「已撤回的通知不会被复活」的关键
                        if (!list.Any(x => x.Id == appliedId))
                        {
                            // 「覆盖同标题」由发布老师逐条决定；置顶的旧通知不覆盖
                            if (o.OverwriteSame)
                            {
                                string newTitle = (Get(o.Fields, "title") ?? "").Trim();
                                if (newTitle.Length > 0)
                                {
                                    foreach (var old in list.Where(x => !x.Deleted && !x.Pinned
                                                 && x.Scope == "local"
                                                 && (x.Title ?? "").Trim() == newTitle).ToList())
                                    {
                                        old.Deleted = true;
                                        old.DeletedAt = DateTime.Now;
                                    }
                                }
                            }
                            list.Add(new Notification
                            {
                                Id = appliedId,
                                Title = Get(o.Fields, "title"),
                                Content = Get(o.Fields, "content"),
                                Pinned = Get(o.Fields, "pinned") == "true",
                                Scope = "local",          // 本班通知绝不进主数据包
                                CreatedAt = DateTime.Now,
                                DueAt = o.DueAt                // 绝对时间点，不按天换算
                            });
                        }
                    }
                    Store.SaveNotifications(list);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError, null);
                    dataVersion = DataVersion(Store.LoadNotifications());
                    var after = Store.LoadNotificationsAll()
                                     .FirstOrDefault(x => x.Id == appliedId && !x.Deleted);
                    appliedFields = after != null ? FieldsOf(after)
                        : new Dictionary<string, string>(o.Fields);
                    break;
                }

                case BoardSyncConsts.EntityDuty when o.IsWeeklyDuty:
                {
                    // 一次安排一周：一条变更单落地 N 条值日条目，各自用确定性派生 Id
                    // （同一张单在两台机器上算出的 Id 必然一致，教室机本地改过的也能按 Id 对上）。
                    var board = Store.LoadDuty();
                    board.Items ??= new List<DutyItem>();

                    if (decision == BoardSyncConsts.DecReplace)
                    {
                        // 整表存档：把替换前的整张值日便签（含墓碑）原样留下来，撤回时整表还原。
                        archivedBoard = JsonSerializer.Serialize(board, Opt);
                        archived = "WEEKLY_BOARD";   // 非空标记：让「撤回这次替换」按钮出现
                        archivedFields = new Dictionary<string, string>
                        {
                            ["day"] = BoardSyncConsts.WeekDayTag,
                            ["duty"] = "原值日表 " + board.LiveCount + " 条",
                            ["names"] = BoardSyncConsts.WeekRowsBrief(
                                board.LiveItems.Select(x => new BoardDutyRow
                                { Day = x.Day, Duty = x.Duty, Names = x.Names }))
                        };
                        foreach (var x in board.Items)
                            if (!x.Deleted) x.Deleted = true;   // 软删除，物理删会让晚到的旧指令把它复活
                    }

                    var landed = new List<BoardDutyRow>();
                    for (int i = 0; i < o.WeekRows.Count; i++)
                    {
                        var r = o.WeekRows[i];
                        string id = BoardSyncIds.DerivedItemId("week-" + (r.Day ?? "") + "-" + i, o.MsgId);
                        var it = board.Items.FirstOrDefault(x => x.Id == id && !x.Deleted);
                        if (it == null)
                        {
                            it = new DutyItem
                            {
                                Id = id, Parent = "", Origin = "remote", Deleted = false,
                                Day = r.Day ?? "", Duty = r.Duty ?? "", Names = r.Names ?? ""
                            };
                            // 星期只认 DayNames：手写一个「星期一」会让这一条在便签里排到最后
                            FillDuty(it, new Dictionary<string, string>
                            { ["day"] = r.Day ?? "", ["duty"] = r.Duty ?? "", ["names"] = r.Names ?? "" });
                            board.Items.Add(it);
                        }
                        appliedItemIds.Add(id);
                        if (appliedId.Length == 0) appliedId = id;
                        landed.Add(new BoardDutyRow
                        { Day = it.Day ?? "", Duty = it.Duty ?? "", Names = it.Names ?? "" });
                    }

                    Store.SaveDuty(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError, null);
                    dataVersion = DataVersion(Store.LoadDuty());
                    // 核对走老通道：把落地后的整周表重新折成 day/duty/names 三个字段
                    appliedFields = BoardSyncConsts.WeekFields(landed);
                    appliedStyles = new Dictionary<string, BoardTextStyle>();
                    break;
                }

                case BoardSyncConsts.EntityDuty:
                {
                    // LoadDuty 永不返回 null（没文件时给一个空白板），所以这里不写 ?? 兜底
                    var board = Store.LoadDuty();
                    board.Items ??= new List<DutyItem>();
                    var found = board.Items.FirstOrDefault(x => x.Id == targetId);
                    if (found != null && found.Deleted)
                        return (false, "目标条目已是墓碑，拒绝改写（TOMBSTONE_GUARD）。", null);

                    drift = found != null && !string.IsNullOrWhiteSpace(baseSig) &&
                            BoardSyncIds.Signature(o.Entity, targetId, FieldsOf(found), StylesOf(found)) != baseSig;

                    if (decision is BoardSyncConsts.DecDelete or BoardSyncConsts.DecReplace)
                    {
                        if (found == null)
                            return (false, "本机找不到目标值日条目，未改动；请指定目标或改选「增加」。", null);
                        archived = JsonSerializer.Serialize(found, Opt);
                        archivedFields = FieldsOf(found);
                        archivedStyles = StylesOf(found);
                        found.Deleted = true;
                        if (decision == BoardSyncConsts.DecDelete) appliedId = found.Id;
                    }
                    if (decision != BoardSyncConsts.DecDelete)
                    {
                        appliedId = BoardSyncIds.DerivedItemId(targetId, o.MsgId);
                        if (!board.Items.Any(x => x.Id == appliedId && !x.Deleted))
                        {
                            var it = new DutyItem
                            {
                                Id = appliedId, Parent = targetId, Origin = "remote", Deleted = false
                            };
                            FillDuty(it, o.Fields);
                            board.Items.Add(it);
                        }
                    }
                    Store.SaveDuty(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError, null);
                    dataVersion = DataVersion(Store.LoadDuty());
                    var after = (Store.LoadDuty().Items ?? new List<DutyItem>())
                                .FirstOrDefault(x => x.Id == appliedId);
                    appliedFields = after != null ? FieldsOf(after) : new Dictionary<string, string>(o.Fields);
                    appliedStyles = new Dictionary<string, BoardTextStyle>();   // 值日不带字段级样式
                    break;
                }

                default:
                    return (false, "不认识的内容类型：" + o.Entity, null);
            }
        }
        bool ignored = decision is BoardSyncConsts.DecIgnore or BoardSyncConsts.DecKeep;
        if (decision == BoardSyncConsts.DecDelete) appliedFields = new Dictionary<string, string>();

        // 处理说明写成一句人话：说清「从什么改成什么」「原文存到哪了」「有没有本地改动」。
        string oldText = !string.IsNullOrWhiteSpace(o.TargetText)
            ? o.TargetText
            : archivedFields.Count > 0 ? BoardSyncConsts.Brief(archivedFields) : "（原文未记录）";
        string newText = BoardSyncConsts.Brief(o.Fields);
        string note = decision switch
        {
            BoardSyncConsts.DecReplace when o.IsWeeklyDuty =>
                "已把教室机值日表整表换成这一周（" + o.WeekRows.Count + " 天）；原表已完整存档，可按底部「撤回」整周还原。",
            BoardSyncConsts.DecAppend when o.IsWeeklyDuty =>
                "已把这一周 " + o.WeekRows.Count + " 天追加到教室机值日表；原有条目全部保留。",
            BoardSyncConsts.DecAppend when auto =>
                "⚡ 教师机指定「直接增加」：教室机已自动新增一条通知（无需电教管理员确认），原有通知全部保留。",
            BoardSyncConsts.DecReplace =>
                $"已替换：「{oldText}」→「{newText}」。原条目已移入存档（软删除），可按存档人工撤回。",
            BoardSyncConsts.DecAppend =>
                "已新增一条：" + newText + "；教室机原有内容全部保留。" +
                (!string.IsNullOrWhiteSpace(targetId) && !LocalHasItem(o.Entity, targetId)
                    ? "（变更单带的目标在教室里已不存在，按纯新增处理）" : ""),
            BoardSyncConsts.DecDelete =>
                "已删除：「" + oldText + "」（写墓碑，不做物理删除）。",
            BoardSyncConsts.DecIgnore => "按裁决忽略，本机数据未改动。",
            BoardSyncConsts.DecKeep => "按裁决保留不改，本机数据未改动。",
            _ => "已处理。"
        };
        if (manualTarget) note += " 目标由电教管理员在教室机现场指定。";
        // 富文本说清楚：老师最想确认的就是「我调的加粗 / 红色 / 特大有没有传过去」
        string styleText = BoardSyncConsts.FormatStyles(o.Styles).Replace("\n", "；");
        if (styleText.Length > 0 && !ignored) note += " 格式：" + styleText + "。";
        if (drift) note += " ⚠ base 有漂移：教室机本地内容在发出后改过，已按裁决执行。";
        else if (!ignored) note += " base 一致。";

        string decisionBy = auto ? "教室机自动增加"
                                 : UserSession.Current?.Username ?? "电教管理员";
        var rp = new BoardReceipt
        {
            ReceiptId = BoardSyncIds.New("rc"),
            MsgId = msgId,
            ActorId = st.ActorId,
            ClassFolder = o.ClassFolder,
            State = ignored ? BoardSyncConsts.RcIgnored : BoardSyncConsts.RcApplied,
            Decision = decision,
            DecisionId = decisionId,
            DataVersion = dataVersion,
            AppliedItemId = appliedId,
            AppliedItemIds = new List<string>(appliedItemIds),
            ArchivedBoard = archivedBoard,
            AppliedFields = appliedFields,
            AppliedStyles = appliedStyles,
            ArchivedSnapshot = archived,
            ArchivedFields = archivedFields,
            ArchivedStyles = archivedStyles,
            TargetText = oldText,
            DecisionBy = decisionBy,
            Note = note,
            CreatedAt = DateTime.Now
        };
        WriteReceipt(null, rp);

        rec.Decision = decision;
        rec.DecisionId = decisionId;
        rec.DataVersion = dataVersion;
        rec.AppliedItemId = appliedId;
        rec.Note = rp.Note;
        CopyReceiptDetail(rec, rp);   // 教室机本机台账也留一份完整回执明细
        rec.Status = ignored ? BoardSyncConsts.StIgnored : BoardSyncConsts.StApplied;
        rec.ReceiptIds.Add(rp.ReceiptId);
        rec.UpdatedAt = DateTime.Now;
        Verify(rec, rp);      // 教室机本机也推进到「已核对」
        SaveState(st);

        return (true, "已按「" + BoardSyncConsts.DecisionName(decision) + "」应用" +
                      (drift ? "（检测到 base 漂移）" : "") + "，回执已写。", rp);
    }

    // ---------------- 撤回：把「替换 / 删除」的存档原文还原回本机 ----------------

    /// <summary>
    /// 教室机现场撤回一次「替换 / 删除」：按回执里的整条存档原文还原，并把这次变更新增的那条软删除。
    ///
    /// <para>★ 只用 <see cref="BoardReceipt.ArchivedSnapshot"/>，绝不靠字段重新拼一条 ——
    /// 重新拼会丢掉 Id / 格式 / 墓碑标记，还原出来的就是一条「长得像」的新条目。</para>
    /// <para>★ 还原后写一份 <c>restored</c> 回执：教师机台账会变成「已撤回」并看到原因，
    /// 不给「悄悄改回去了但没人知道」留口子。</para>
    /// </summary>
    public static (bool Ok, string Message) RestoreArchived(string msgId, bool bypassRole = false)
    {
        lock (_stateGate) { return RestoreArchivedCore(msgId, bypassRole); }
    }

    static (bool Ok, string Message) RestoreArchivedCore(string msgId, bool bypassRole)
    {
        LastError = null;
        var st = LoadState();
        var rec = st.Records.FirstOrDefault(r => r.MsgId == msgId);
        if (rec == null) return (false, "找不到这条变更单。");
        if (!bypassRole && !UserSession.IsStudent)
            return (false, "只有电教管理员（教室机）能还原教室机的本机数据。");
        // 幂等判定必须在「有没有存档」之前：撤回回执会把台账里的存档字段重写一遍，
        // 先判存档的话第二次点撤回会被误报成「没有存档，无法还原」。
        if (rec.Status == BoardSyncConsts.StRevoked)
            return (true, "这条已经撤回过了（幂等跳过），本机数据没有再改动。");
        if (string.IsNullOrWhiteSpace(rec.ArchivedSnapshot))
            return (false, "这条变更单没有存档原文（只有「替换 / 删除」才存档），无法还原。");

        var o = rec.Order;
        string appliedId = rec.AppliedItemId;
        string restoredId = "";
        var restoredFields = new Dictionary<string, string>();

        try
        {
            if (o.IsWeeklyDuty)
            {
                // 整周替换的存档是「整张值日便签」，不是某一条 —— 必须整表还原，
                // 按单条 DutyItem 解析只会得到一条长得像却不完整的数据。
                if (string.IsNullOrWhiteSpace(rec.ArchivedBoard))
                    return (false, "整周存档缺失（只有「整表换成这一周」才存档），无法还原。");
                var oldBoard = JsonSerializer.Deserialize<DutyBoard>(rec.ArchivedBoard, Opt)
                               ?? throw new InvalidDataException("存档内容为空");
                oldBoard.Items ??= new List<DutyItem>();

                // 应用之后教室机本地新增的条目要留着：整表还原不该抹掉别人刚加的值日
                var nowBoard = Store.LoadDuty();
                foreach (var x in nowBoard.Items ?? new List<DutyItem>())
                {
                    if (oldBoard.Items.Any(y => string.Equals(y.Id, x.Id, StringComparison.Ordinal))) continue;
                    if (rec.AppliedItemIds.Any(id => string.Equals(id, x.Id, StringComparison.Ordinal))) continue;
                    oldBoard.Items.Add(x);
                }

                Store.SaveDuty(oldBoard);
                if (Store.LastWriteError != null) return (false, Store.LastWriteError);
                var restoredRows = oldBoard.LiveItems.Select(x => new BoardDutyRow
                { Day = x.Day, Duty = x.Duty, Names = x.Names }).ToList();
                restoredId = restoredRows.Count > 0 ? "week-restored" : "";
                restoredFields = BoardSyncConsts.WeekFields(restoredRows);
            }
            else
            switch (o.Entity)
            {
                case BoardSyncConsts.EntityHomework:
                {
                    var old = JsonSerializer.Deserialize<HomeworkItem>(rec.ArchivedSnapshot, Opt);
                    if (old == null) return (false, "存档原文无法解析，未改动。");
                    var board = Store.LoadHomework() ?? new HomeworkBoard();
                    board.Items ??= new List<HomeworkItem>();

                    // 撤掉这次替换出来的那条（软删除：物理删掉会让晚到的旧指令把它「复活」）
                    var made = board.Items.FirstOrDefault(x => x.Id == appliedId && !x.Deleted);
                    if (made != null && appliedId != old.Id) made.Deleted = true;

                    var back = board.Items.FirstOrDefault(x => x.Id == old.Id);
                    if (back == null)
                    {
                        old.Deleted = false;
                        board.Items.Add(old);
                    }
                    else
                    {
                        back.Deleted = false;
                        back.Subject = old.Subject;
                        back.Content = old.Content;
                        back.SubjectStyle = old.SubjectStyle;
                        back.ContentStyle = old.ContentStyle;
                    }
                    Store.SaveHomework(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError);

                    restoredId = old.Id;
                    var now = (Store.LoadHomework()?.Items ?? new List<HomeworkItem>())
                              .FirstOrDefault(x => x.Id == restoredId);
                    restoredFields = now != null ? FieldsOf(now) : FieldsOf(old);
                    break;
                }

                case BoardSyncConsts.EntityMissing:
                {
                    var old = JsonSerializer.Deserialize<MissingHomeworkItem>(rec.ArchivedSnapshot, Opt);
                    if (old == null) return (false, "存档原文无法解析，未改动。");
                    var board = Store.LoadMissingBoard() ?? new MissingBoard();
                    board.Items ??= new List<MissingHomeworkItem>();

                    var made = board.Items.FirstOrDefault(x => x.Id == appliedId && !x.Deleted);
                    if (made != null && appliedId != old.Id) made.Deleted = true;

                    var back = board.Items.FirstOrDefault(x => x.Id == old.Id);
                    if (back == null)
                    {
                        old.Deleted = false;
                        board.Items.Add(old);
                    }
                    else
                    {
                        back.Deleted = false;
                        back.Subject = old.Subject;
                        back.Students = old.Students;
                        back.SubjectStyle = old.SubjectStyle;
                        back.ContentStyle = old.ContentStyle;
                    }
                    Store.SaveMissingBoard(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError);

                    restoredId = old.Id;
                    var now = (Store.LoadMissingBoard()?.Items ?? new List<MissingHomeworkItem>())
                              .FirstOrDefault(x => x.Id == restoredId);
                    restoredFields = now != null ? FieldsOf(now) : FieldsOf(old);
                    break;
                }

                case BoardSyncConsts.EntityNotice:
                {
                    var old = JsonSerializer.Deserialize<Notification>(rec.ArchivedSnapshot, Opt);
                    if (old == null) return (false, "存档原文无法解析，未改动。");
                    var list = Store.LoadNotificationsAll();

                    // 撤掉这次替换 / 覆盖出来的那条（软删除，和值日一致）
                    if (!string.IsNullOrWhiteSpace(appliedId) && appliedId != old.Id)
                    {
                        var made = list.FirstOrDefault(x => x.Id == appliedId && !x.Deleted);
                        if (made != null) { made.Deleted = true; made.DeletedAt = DateTime.Now; }
                    }
                    // 存档原文可能已经在列表里（只是被打了墓碑）：复活它，而不是再插一条重复的
                    var back = list.FirstOrDefault(x => x.Id == old.Id);
                    if (back == null)
                    {
                        old.Deleted = false;
                        old.DeletedAt = null;
                        list.Add(old);
                    }
                    else
                    {
                        back.Deleted = false;
                        back.DeletedAt = null;
                        back.Title = old.Title;
                        back.Content = old.Content;
                        back.Pinned = old.Pinned;
                        back.DueAt = old.DueAt;
                    }

                    Store.SaveNotifications(list);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError);

                    restoredId = old.Id;
                    restoredFields = FieldsOf(old);
                    break;
                }

                case BoardSyncConsts.EntityDuty:
                {
                    var old = JsonSerializer.Deserialize<DutyItem>(rec.ArchivedSnapshot, Opt);
                    if (old == null) return (false, "存档原文无法解析，未改动。");
                    var board = Store.LoadDuty();
                    board.Items ??= new List<DutyItem>();

                    // 撤掉这次替换出来的那条（软删除：物理删掉会让晚到的旧指令把它「复活」）
                    var made = board.Items.FirstOrDefault(x => x.Id == appliedId && !x.Deleted);
                    if (made != null && appliedId != old.Id) made.Deleted = true;

                    var back = board.Items.FirstOrDefault(x => x.Id == old.Id);
                    if (back == null)
                    {
                        old.Deleted = false;
                        board.Items.Add(old);
                    }
                    else
                    {
                        back.Deleted = false;
                        back.Day = old.Day;
                        back.Duty = old.Duty;
                        back.Names = old.Names;
                    }
                    Store.SaveDuty(board);
                    if (Store.LastWriteError != null) return (false, Store.LastWriteError);

                    restoredId = old.Id;
                    var now = (Store.LoadDuty().Items ?? new List<DutyItem>())
                              .FirstOrDefault(x => x.Id == restoredId);
                    restoredFields = now != null ? FieldsOf(now) : FieldsOf(old);
                    break;
                }

                default:
                    return (false, "不认识的内容类型：" + o.Entity);
            }
        }
        catch (Exception ex)
        {
            LastError = "还原失败：" + ex.Message;
            return (false, LastError);
        }

        string note = o.IsWeeklyDuty
            ? "已撤回这次整周替换，教室机值日表已整表还原（" + BoardSyncConsts.Brief(restoredFields) + "）。"
            : "已撤回这次" +
              (rec.Decision == BoardSyncConsts.DecDelete ? "删除" : "替换") +
              "，原文已还原到教室机（「" + BoardSyncConsts.Brief(restoredFields) + "」）。";

        var rp = new BoardReceipt
        {
            ReceiptId = BoardSyncIds.New("rc"),
            MsgId = msgId,
            ActorId = st.ActorId,
            ClassFolder = o.ClassFolder,
            State = BoardSyncConsts.RcRestored,
            Decision = "restore",
            // 与 apply 回执的 decisionId 必须不同，否则回执文件名撞车
            DecisionId = BoardSyncIds.Hash(msgId + "|restore")[..10],
            AppliedItemId = restoredId,
            AppliedItemIds = new List<string>(rec.AppliedItemIds),
            AppliedFields = restoredFields,
            // 整周存档与单条存档都要原样带回：CopyReceiptDetail 会用这份回执覆盖台账明细，
            // 留空等于把「还能不能再还原」的凭证擦掉。
            ArchivedSnapshot = rec.ArchivedSnapshot,
            ArchivedBoard = rec.ArchivedBoard,
            ArchivedFields = rec.ArchivedFields,
            ArchivedStyles = rec.ArchivedStyles,
            TargetText = rec.TargetText,
            DecisionBy = UserSession.Current?.Username ?? "电教管理员",
            Note = note,
            CreatedAt = DateTime.Now
        };
        WriteReceipt(null, rp);

        rec.Status = BoardSyncConsts.StRevoked;
        rec.Note = note;
        rec.AppliedItemId = restoredId;
        rec.ReceiptIds.Add(rp.ReceiptId);
        rec.UpdatedAt = DateTime.Now;
        CopyReceiptDetail(rec, rp);
        SaveState(st);

        return (true, note + "教师机会在下一轮检查时看到「已撤回」回执。");
    }

    // ---------------- 字段快照 / 签名 / 版本 ----------------

    static string Get(IDictionary<string, string> f, string k)
        => f.TryGetValue(k, out var v) ? v ?? "" : "";

    public static Dictionary<string, string> FieldsOf(HomeworkItem x) => new()
    {
        ["subject"] = x.Subject ?? "", ["content"] = x.Content ?? ""
    };

    public static Dictionary<string, string> FieldsOf(MissingHomeworkItem x) => new()
    {
        ["subject"] = x.Subject ?? "", ["students"] = x.Students ?? ""
    };

    /// <summary>作业条目的字段级样式：科目名与正文各自一套（不再是「内容一套」）。</summary>
    public static Dictionary<string, BoardTextStyle> StylesOf(HomeworkItem x) => new()
    {
        ["subject"] = BoardTextStyle.From(x.SubjectStyle),
        ["content"] = BoardTextStyle.From(x.ContentStyle)
    };

    /// <summary>缺交条目的字段级样式：科目名与缺交名单各自一套。</summary>
    public static Dictionary<string, BoardTextStyle> StylesOf(MissingHomeworkItem x) => new()
    {
        ["subject"] = BoardTextStyle.From(x.SubjectStyle),
        ["students"] = BoardTextStyle.From(x.ContentStyle)
    };

    public static Dictionary<string, string> FieldsOf(Notification x) => new()
    {
        ["title"] = x.Title ?? "", ["content"] = x.Content ?? "",
        ["pinned"] = x.Pinned ? "true" : "false",
        ["due"] = x.DueAt is DateTime d ? d.ToString("yyyy-MM-dd HH:mm") : ""
    };

    /// <summary>值日条目的字段：星期 / 值日内容 / 值日生。</summary>
    public static Dictionary<string, string> FieldsOf(DutyItem x) => new()
    {
        ["day"] = x.Day ?? "", ["duty"] = x.Duty ?? "", ["names"] = x.Names ?? ""
    };

    /// <summary>
    /// 值日<b>没有</b>字段级样式：字体 / 颜色 / 字号是整张值日便签一套
    /// （<c>DutyBoard.DayStyle</c> / <c>DutyStyle</c> / <c>ContentStyle</c>），
    /// 不跟着单条走 —— 同一张表里一行红一行黑并不好看，而且远端逐条覆盖会让整表慢慢花掉。
    /// <para>所以值日的签名只含文字：改了格式不算「教室机本地变过」，也不会被远端改格式。</para>
    /// </summary>
    public static Dictionary<string, BoardTextStyle> StylesOf(DutyItem x) => new();

    public static string SignatureOf(string entity, string itemId, object? item) => item switch
    {
        HomeworkItem h => BoardSyncIds.Signature(entity, itemId, FieldsOf(h), StylesOf(h)),
        MissingHomeworkItem m => BoardSyncIds.Signature(entity, itemId, FieldsOf(m), StylesOf(m)),
        DutyItem d => BoardSyncIds.Signature(entity, itemId, FieldsOf(d)),
        Notification n => BoardSyncIds.Signature(entity, itemId, FieldsOf(n)),
        _ => ""
    };

    /// <summary>
    /// 「替换」时把被覆盖条目的字体样式原样带过来。
    /// <para>★ 教室机本地调过的字号 / 颜色属于本地数据，教师机无权覆盖 —— 这是消息链的铁律。
    /// 发布窗删掉格式条后 Styles 恒为空，若不继承，替换一次就会把人家调好的格式悄悄压回默认。</para>
    /// <para>★ 用值拷贝而不是直接赋同一个 TextStyle 引用：旧条目已软删并全文存档，
    /// 共享引用会让后续编辑顺着引用改到存档对象上。</para>
    /// </summary>
    static TextStyle CopyStyle(TextStyle? s) => s == null
        ? new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }
        : new TextStyle { Bold = s.Bold, Color = s.Color ?? "", SizeLevel = s.SizeLevel };

    static void InheritHomeworkStyle(HomeworkItem target, HomeworkItem source)
    {
        target.SubjectStyle = CopyStyle(source.SubjectStyle);
        target.ContentStyle = CopyStyle(source.ContentStyle);
        target.Bold = source.Bold;
        target.Color = source.Color;
        target.SizeLevel = source.SizeLevel;
    }

    static void InheritMissingStyle(MissingHomeworkItem target, MissingHomeworkItem source)
    {
        target.SubjectStyle = CopyStyle(source.SubjectStyle);
        target.ContentStyle = CopyStyle(source.ContentStyle);
        target.Bold = source.Bold;
        target.Color = source.Color;
        target.SizeLevel = source.SizeLevel;
    }

    /// <summary>
    /// 把变更单的字段 / 样式落到条目上。
    /// <para>★ 有结构化样式就整块替换；没有（老变更单 / 通知）才退回平铺的 bold / color / sizeLevel。
    /// 两边都没有时保持条目现有样式 —— 绝不把教室机调好的格式悄悄压回默认。</para>
    /// </summary>
    static void FillHomework(HomeworkItem it, Dictionary<string, string> f,
                             Dictionary<string, BoardTextStyle>? styles = null)
    {
        it.Subject = Get(f, "subject");
        it.Content = Get(f, "content");

        if (styles != null && styles.TryGetValue("subject", out var ss) && ss != null)
            it.SubjectStyle = ss.ToTextStyle();
        if (styles != null && styles.TryGetValue("content", out var cs) && cs != null)
            it.ContentStyle = cs.ToTextStyle();
        else if (styles == null || styles.Count == 0)
        {
            // 老变更单：只有内容一套平铺格式
            string legacy = Get(f, "color");
            if (legacy.Length > 0) it.Color = legacy;   // 空值不许抹掉继承来的老格式
            if (bool.TryParse(Get(f, "bold"), out var b)) it.Bold = b;
            if (int.TryParse(Get(f, "sizeLevel"), out var n)) it.SizeLevel = n;
        }
    }

    static void FillMissing(MissingHomeworkItem it, Dictionary<string, string> f,
                            Dictionary<string, BoardTextStyle>? styles = null)
    {
        it.Subject = Get(f, "subject");
        it.Students = Get(f, "students");

        if (styles != null && styles.TryGetValue("subject", out var ss) && ss != null)
            it.SubjectStyle = ss.ToTextStyle();
        if (styles != null && styles.TryGetValue("students", out var cs) && cs != null)
            it.ContentStyle = cs.ToTextStyle();
        else if (styles == null || styles.Count == 0)
        {
            string legacy = Get(f, "color");
            if (legacy.Length > 0) it.Color = legacy;   // 空值不许抹掉继承来的老格式
            if (bool.TryParse(Get(f, "bold"), out var b)) it.Bold = b;
            if (int.TryParse(Get(f, "sizeLevel"), out var n)) it.SizeLevel = n;
        }
    }

    /// <summary>
    /// 把变更单的字段落到值日条目上（值日没有字段级样式，格式由便签统一设置）。
    /// <para>★ 星期只接受 <see cref="DutyItem.DayNames"/> 里的值（「每天 / 周一…周日」）：
    /// 手写一个「星期一」会让这一条在便签里排到最后（DayOrder 认不出来），
    /// 表面上是「发出去了但教室机看不到」。</para>
    /// </summary>
    static void FillDuty(DutyItem it, Dictionary<string, string> f)
    {
        string day = Get(f, "day").Trim();
        if (Array.IndexOf(DutyItem.DayNames, day) >= 0) it.Day = day;
        else if (day.Length == 0) it.Day = "周一";
        it.Duty = Get(f, "duty");
        it.Names = Get(f, "names");
    }

    static string DataVersion(object? board)
        => BoardSyncIds.Hash(JsonSerializer.Serialize(board ?? new object(), Opt))[..16];

    // ---------------- 教室机内容快照（教师机选目标条目的依据） ----------------

    /// <summary>上次发布出去的内容指纹：内容没变就不重复写文件，避免每轮轮询刷共享盘。</summary>
    static string? _lastSnapshotVersion;

    /// <summary>快照写盘互斥：轮询尾、内容变化防抖、用户点保存可能同时触发，不许叠着写同一个文件。</summary>
    static readonly object _snapGate = new();

    /// <summary>本机是否作为教室机发布快照：显式设置，或用电教管理员身份登录。</summary>
    public static bool IsClassroomMachine
        => Store.LoadSettings().BoardSyncIsClassroom || UserSession.IsStudent;

    /// <summary>
    /// 把本机四类内容（作业 / 缺交 / 值日 / 本班通知）收成一份快照。
    /// 只读，不写盘；actorId 由调用方给，避免只读场景触发 EnsureActorId 的落盘副作用。
    /// </summary>
    public static BoardSnapshot BuildLocalSnapshot(string classKey, string? actorId = null)
    {
        var snap = new BoardSnapshot
        {
            ClassKey = classKey,
            ClassFolder = BoardSyncIds.ClassFolder(classKey),
            ActorId = actorId ?? Store.LoadSettings().BoardSyncActorId ?? "unknown",
            DeviceName = Environment.MachineName,
            IsClassroom = true,
            CreatedAt = DateTime.Now
        };

        foreach (var x in Store.LoadHomework()?.LiveItems ?? Enumerable.Empty<HomeworkItem>())
        {
            var f = FieldsOf(x);
            var stl = StylesOf(x);
            snap.Items.Add(new BoardSnapshotItem
            {
                Entity = BoardSyncConsts.EntityHomework, Id = x.Id, Fields = f, Styles = stl,
                Signature = BoardSyncIds.Signature(BoardSyncConsts.EntityHomework, x.Id, f, stl)
            });
        }
        foreach (var x in Store.LoadMissingBoard()?.LiveItems ?? Enumerable.Empty<MissingHomeworkItem>())
        {
            var f = FieldsOf(x);
            var stl = StylesOf(x);
            snap.Items.Add(new BoardSnapshotItem
            {
                Entity = BoardSyncConsts.EntityMissing, Id = x.Id, Fields = f, Styles = stl,
                Signature = BoardSyncIds.Signature(BoardSyncConsts.EntityMissing, x.Id, f, stl)
            });
        }
        foreach (var x in Store.LoadDuty().LiveItems)
        {
            var f = FieldsOf(x);
            var stl = StylesOf(x);
            snap.Items.Add(new BoardSnapshotItem
            {
                Entity = BoardSyncConsts.EntityDuty, Id = x.Id, Fields = f, Styles = stl,
                Signature = BoardSyncIds.Signature(BoardSyncConsts.EntityDuty, x.Id, f, stl)
            });
        }
        foreach (var x in Store.LoadNotifications().Where(n => n.Scope != "school"))
        {
            var f = FieldsOf(x);
            snap.Items.Add(new BoardSnapshotItem
            {
                Entity = BoardSyncConsts.EntityNotice, Id = x.Id, Fields = f,
                Signature = BoardSyncIds.Signature(BoardSyncConsts.EntityNotice, x.Id, f)
            });
        }
        snap.ContentVersion = BoardSyncIds.Hash(JsonSerializer.Serialize(snap.Items, Opt))[..16];
        return snap;
    }

    /// <summary>教室机把「本机现在有什么」发布到共享目录，教师机据此选目标条目。</summary>
    public static (bool Ok, string Message) PublishSnapshot(bool force = false, string? classKey = null)
    {
        // 同一时刻只发一份：另一路正在写就先跳过（下一轮/下次变化还会再来），绝不排队等共享盘
        if (!Monitor.TryEnter(_snapGate)) return (true, "上一份快照还在发布，本次跳过。");
        try { return PublishSnapshotCore(force, classKey); }
        finally { Monitor.Exit(_snapGate); }
    }

    static (bool Ok, string Message) PublishSnapshotCore(bool force, string? classKey)
    {
        if (!Store.EnablePersistence) return (false, "游客模式不发布内容快照。");
        var t = GetTransport();
        if (t == null || !IsEnabled) return (false, "未启用同步或没配数据源，跳过快照发布。");

        string ck = (classKey ?? Store.LoadSettings().SelectedClass ?? "").Trim();
        if (ck.Length == 0) return (false, "没有选定班级，跳过快照发布。");

        BoardSnapshot snap;
        try { snap = BuildLocalSnapshot(ck, EnsureActorId()); }
        catch (Exception ex)
        {
            LastError = "读取本机内容失败：" + ex.Message;
            return (false, LastError);
        }

        if (!force && _lastSnapshotVersion == snap.ContentVersion)
            return (true, "内容未变化，沿用已有快照。");

        try
        {
            bool ok = t.WriteOwn(snap.ClassFolder, BoardSyncConsts.Snapshots, snap.FileName,
                                 JsonSerializer.Serialize(snap, Opt));
            if (!ok)
            {
                LastError = "快照写入失败（目标文件被占用）。";
                return (false, LastError);
            }
        }
        catch (Exception ex)
        {
            LastError = "快照写入失败：" + ex.Message;
            return (false, LastError);
        }
        _lastSnapshotVersion = snap.ContentVersion;
        LastError = null;
        return (true, $"已发布 {snap.Items.Count} 条内容快照。");
    }

    /// <summary>
    /// 教师机读取最新一份教室机快照。没有快照时必须明确告诉用户，
    /// 绝不能悄悄退化成「目标条目为空」（那样「替换」会变成「新增」）。
    /// </summary>
    public static (BoardSnapshot? Snapshot, string Message) LoadRemoteSnapshot(string classKey)
    {
        var t = GetTransport();
        if (t == null) return (null, "没配摆渡目录，读不到教室机内容。");
        if (string.IsNullOrWhiteSpace(classKey)) return (null, "请先填目标班级。");

        string cf = BoardSyncIds.ClassFolder(classKey.Trim());
        string myId = Store.LoadSettings().BoardSyncActorId ?? "";
        var problems = new List<string>();
        BoardSnapshot? best = null;

        foreach (var name in SafeList(t, cf, BoardSyncConsts.Snapshots, problems))
        {
            if (!name.StartsWith("s-", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            string? text = SafeRead(t, cf, BoardSyncConsts.Snapshots, name, problems);
            if (text == null) continue;
            try
            {
                var snap = JsonSerializer.Deserialize<BoardSnapshot>(text, Opt)
                           ?? throw new JsonException("快照内容为空");
                if (snap.Rev != BoardSyncConsts.Rev)
                    throw new InvalidDataException("不认识的 Rev=" + snap.Rev);
                if (!snap.IsClassroom) continue;
                if (myId.Length > 0 && snap.ActorId == myId) continue;
                if (best == null || snap.CreatedAt > best.CreatedAt) best = snap;
            }
            catch (Exception ex)
            {
                // FTP 覆盖写可能在正好读取时给半截 JSON：大声报出来，绝不静默当空表
                problems.Add("坏快照 " + name + "：" + ex.Message);
            }
        }
        if (problems.Count > 0) LastError = string.Join("；", problems);
        if (best == null)
            return (null, "还没收到教室机的内容快照：请先在教室机启用同步并点一次「↻ 立即检查」" +
                          "（或等一次轮询）。这期间只能新增；若要点「替换」，教室机会让你现场指定目标条目。");

        double age = (DateTime.Now - best.CreatedAt).TotalMinutes;
        string msg = $"教室机 {best.DeviceName} · {best.CreatedAt:MM-dd HH:mm} · {best.Items.Count} 条" +
                     (age > 10 ? $"（距今约 {age:0} 分钟，可能不是最新）" : "（刚刚同步）");
        return (best, msg);
    }

    /// <summary>教室机裁决时给管理员挑目标用：列本机当前已有的同类条目。</summary>
    public static List<BoardSnapshotItem> LocalItemsForTarget(string entity, string classKey)
        => BuildLocalSnapshot(classKey).For(entity).ToList();

    /// <summary>界面判断「变更单自带的目标 Id 在教室里是否真的存在」。</summary>
    public static bool LocalHasItem(string entity, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            return entity switch
            {
                // 墓碑也算「存在」，让 case 里的 TOMBSTONE_GUARD 给出准确拒绝原因
                BoardSyncConsts.EntityHomework =>
                    Store.LoadHomework()?.Items.Any(x => x.Id == id) ?? false,
                BoardSyncConsts.EntityMissing =>
                    Store.LoadMissingBoard()?.Items.Any(x => x.Id == id) ?? false,
                BoardSyncConsts.EntityNotice =>
                    Store.LoadNotifications().Any(x => x.Id == id),
                BoardSyncConsts.EntityDuty =>
                    Store.LoadDuty().Items.Any(x => x.Id == id),
                _ => false
            };
        }
        catch { return false; }
    }

    // ---------------- 界面取数 ----------------

    public static List<BoardMessageRecord> Outbox(BoardSyncState? st = null)
        => (st ?? LoadState()).Records
           .Where(r => r.Direction == BoardSyncConsts.DirOut)
           .OrderByDescending(r => r.Order.CreatedAt).ToList();

    public static List<BoardMessageRecord> Inbox(BoardSyncState? st = null)
        => (st ?? LoadState()).Records
           .Where(r => r.Direction == BoardSyncConsts.DirIn && !r.Order.IsRevoke)
           .OrderByDescending(r => r.Order.CreatedAt).ToList();

    /// <summary>本机订阅的班级：当前显示班级 + 设置里额外填的（逗号 / 顿号分隔）。</summary>
    public static string[] SubscribedClasses()
    {
        var s = Store.LoadSettings();
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.SelectedClass)) list.Add(s.SelectedClass.Trim());
        string extra = s.BoardSyncExtraClasses ?? "";
        foreach (var p in extra.Split(new[] { ',', '，', '、', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p.Trim());
        return list.Distinct(StringComparer.Ordinal).ToArray();
    }

    // ---------------- 台账保留期 / 清除 ----------------

    /// <summary>一条台账记录的「关键时刻」：取最后更新时间与发出时间里较晚的那个。</summary>
    static DateTime Stamp(BoardMessageRecord r)
        => r.UpdatedAt > r.Order.CreatedAt ? r.UpdatedAt : r.Order.CreatedAt;

    /// <summary>
    /// 按保留天数过滤台账列表（0 = 不过滤，全部显示）。
    /// <para>★ 未处理的收件箱永远显示：7 天前收到但还没裁决的变更单如果被「有效期」藏起来，
    /// 电教管理员就再也找不到它了 —— 那等于静默丢消息。</para>
    /// </summary>
    public static List<BoardMessageRecord> FilterByKeepDays(IEnumerable<BoardMessageRecord> src, int days)
    {
        var list = (src ?? Enumerable.Empty<BoardMessageRecord>()).ToList();
        if (days <= 0) return list;
        var cutoff = DateTime.Now.AddDays(-days);
        return list.Where(r => Stamp(r) >= cutoff
                            || (r.Direction == BoardSyncConsts.DirIn && !r.IsTerminal)).ToList();
    }

    /// <summary>本机台账里「已终结 / 未处理」各有多少条（清除对话框用）。</summary>
    public static (int Total, int Terminal, int Pending) LedgerCounts(BoardSyncState? st = null)
    {
        var recs = (st ?? LoadState()).Records;
        int terminal = recs.Count(r => r.IsTerminal);
        return (recs.Count, terminal, recs.Count - terminal);
    }

    /// <summary>
    /// 人工清除台账（唯一会真的删记录的地方，绝不自动执行）。
    /// <para>权限：班主任 / 科任 / 管理员可清；电教管理员（教室机开机身份）只能看 ——
    /// 他误清会把待处理的消息一起清掉，而教师机那边的发送箱还留着，两边就对不上了。</para>
    /// <para>★ 清前先备份成 <c>board_sync.json.bak</c>：撤回替换靠的是记录里的存档，
    /// 清掉就真的找不回来了。</para>
    /// </summary>
    public static (bool Ok, string Message) ClearLedger(bool all)
    {
        if (UserSession.IsStudent)
            return (false, "电教管理员只能查看台账，不能清除（请用班主任 / 科任 / 管理员账号）。");
        if (!Store.EnablePersistence)
            return (false, "游客模式不写盘，台账没有清除。请先登录。");

        lock (_stateGate)
        {
            BoardSyncState st;
            try { st = LoadState(); }
            catch (Exception ex) { return (false, "台账读取失败：" + ex.Message); }

            int before = st.Records.Count;
            int n = all ? before : st.Records.RemoveAll(r => r.IsTerminal);
            if (n == 0) return (true, "没有可清除的记录：未处理的变更单不会被清除。");

            try
            {
                if (File.Exists(StatePath)) File.Copy(StatePath, StatePath + ".bak", true);
            }
            catch (Exception ex)
            {
                // 备份失败就不清：没有退路的删除比留着旧记录危险得多
                return (false, "备份台账失败，已放弃清除：" + ex.Message);
            }

            if (!SaveState(st))
                return (false, "清除后台账写盘失败：" + (Store.LastWriteError ?? "未知原因"));

            return (true, $"已清除 {n} 条记录（清前已备份为 board_sync.json.bak），剩余 {st.Records.Count} 条。");
        }
    }
}
