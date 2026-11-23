using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 课表更新回执：教室机写、发布端读。
///
/// <para>写：<c>status/u-&lt;ActorId&gt;.json</c>，每台只覆盖自己那份，Seq 递增。
/// 读：把每条回执和当前 manifest 比一次，得出「最新 / 版本落后 / 失败 / 失联 / 数据异常」。</para>
///
/// <para>★ 失联 ≠ 失败：数据源连不上时教室机根本写不了回执，那份「没有文件」只能说明「不知道」。
/// ★ 墙钟只用于显示：现场机器时间不准是常态，状态判定靠 Seq 与 TargetSha。</para>
/// </summary>
public static class UpdateReceiptService
{
    static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>单份回执超过这个大小直接当异常，不解析（共享盘上的文件谁都能写）。</summary>
    const int MaxReceiptChars = 16 * 1024;

    static string AliasPath => Path.Combine(Store.AppDir, "receipt-aliases.json");
    static string SeenPath => Path.Combine(Store.AppDir, "receipt-seen.json");

    // =====================================================================
    //  教室机侧：写
    // =====================================================================

    /// <summary>回执文件名（纯 ASCII，ActorId 本身已是十六进制串）。</summary>
    public static string FileName(string actorId)
    {
        string safe = new string((actorId ?? "unknown").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (safe.Length == 0) safe = "unknown";
        if (safe.Length > 48) safe = safe[..48];
        return "u-" + safe + ".json";
    }

    /// <summary>
    /// 写一份回执。读回自己旧那份拿 Seq（跨重装 / 恢复备份时 Seq 会倒退，发布端据此报警）。
    /// ★ 调用方负责 try/catch：写回执失败绝不能反过来让更新失败。
    /// </summary>
    public static long Write(IFeedSource src, UpdateReceipt r, DateTime? createdAt = null)
    {
        if (!Store.EnablePersistence) return r.Seq;   // 游客模式不写盘，也不假装上报

        string name = FileName(r.ActorId);
        long prev = 0;
        try
        {
            var oldText = src.ReadStatus(name);
            if (!string.IsNullOrWhiteSpace(oldText) && oldText.Length <= MaxReceiptChars)
            {
                var old = JsonSerializer.Deserialize<UpdateReceipt>(oldText, Opt);
                if (old != null) prev = Math.Max(0, old.Seq);
            }
        }
        catch { /* 旧回执坏了就从 1 重新数，不当成写失败 */ }

        r.Seq = prev + 1;
        if (string.IsNullOrWhiteSpace(r.ReceiptId)) r.ReceiptId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(r.DeviceName)) r.DeviceName = Environment.MachineName;
        r.CreatedAt = createdAt ?? DateTime.Now;   // 诊断可指定旧时间造「失联」场景；生产恒为现在

        src.WriteStatusOwn(name, JsonSerializer.Serialize(r, Opt));
        return r.Seq;
    }

    /// <summary>读本机上一份回执的写入时间（心跳节流用）；读不到返回 null。</summary>
    public static DateTime? LastWrittenAt(IFeedSource src, string actorId)
    {
        try
        {
            var text = src.ReadStatus(FileName(actorId));
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxReceiptChars) return null;
            var old = JsonSerializer.Deserialize<UpdateReceipt>(text, Opt);
            return old?.CreatedAt;
        }
        catch { return null; }
    }

    /// <summary>把异常消息压成一行人话：去掉路径、账号、地址与完整异常栈。</summary>
    public static string SafeReason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string s = text.Split('\n', '\r')[0].Trim();
        s = Regex.Replace(s, @"[A-Za-z]:\\[^\s，,；;）)]+", "<路径>");
        s = Regex.Replace(s, @"(?i)(ftp|https?)://[^\s，,；;）)]+", "<地址>");
        s = Regex.Replace(s, @"(?i)(password|pwd|密码)\s*[=:：]\s*\S+", "$1=<已隐藏>");
        if (s.Length > 180) s = s[..180] + "…";
        return s;
    }

    /// <summary>按更新流程的出错文本归一个机器可判的码。</summary>
    public static string ReasonCode(string? message)
    {
        string s = message ?? "";
        if (s.Contains("高于本程序支持", StringComparison.Ordinal)) return "SCHEMA_NEW";
        if (s.Contains("下载失败", StringComparison.Ordinal)) return "DOWNLOAD_FAIL";
        if (s.Contains("数据包无效", StringComparison.Ordinal) || s.Contains("校验", StringComparison.Ordinal))
            return "VERIFY_FAIL";
        if (s.Contains("应用失败", StringComparison.Ordinal)) return "APPLY_FAIL";
        if (s.Contains("manifest", StringComparison.OrdinalIgnoreCase) || s.Contains("检查失败", StringComparison.Ordinal))
            return "CHECK_FAIL";
        return "UNKNOWN";
    }

    // =====================================================================
    //  发布端侧：读 + 判定
    // =====================================================================

    /// <summary>一台机的判定结果。</summary>
    public class DeviceStatus
    {
        public string ActorId { get; set; } = "";
        public string Name { get; set; } = "";         // 别名 > 机器名 > ActorId
        public string? Alias { get; set; }
        public string Code { get; set; } = "missing"; // latest | behind | failed | stale | invalid
        public string Label { get; set; } = "未上报";
        public string Detail { get; set; } = "";
        public string Reason { get; set; } = "";
        public UpdateReceipt? Receipt { get; set; }
    }

    public class Summary
    {
        public int Total, Latest, Behind, Failed, Stale;
    }

    /// <summary>
    /// 汇总发布端看到的所有回执。<paramref name="currentSha"/> = 当前 manifest 的 DataSha256。
    /// <para>只读远端；别名与「上次见到的 Seq」存在发布机本地，不随包下发。</para>
    /// </summary>
    public static (Summary Summary, List<DeviceStatus> Devices) Collect(
        IFeedSource src, string currentSha, int intervalMinutes, bool persistSeen = true)
    {
        var aliases = LoadAliases();
        var seen = LoadSeen();
        var devices = new List<DeviceStatus>();
        var now = new Dictionary<string, long>();
        var sum = new Summary();

        int staleMinutes = Math.Max(60, Math.Max(1, intervalMinutes) * 2);

        foreach (string name in src.ListStatus())
        {
            if (!name.StartsWith("u-", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.Ordinal)) continue;

            string? json = src.ReadStatus(name);
            if (json == null) continue;
            if (json.Length > MaxReceiptChars)
            {
                sum.Failed++;
                devices.Add(new DeviceStatus
                {
                    ActorId = name, Name = name, Code = "invalid", Label = "数据异常",
                    Detail = "回执文件过大，已忽略。"
                });
                continue;
            }

            UpdateReceipt? r;
            try { r = JsonSerializer.Deserialize<UpdateReceipt>(json, Opt); }
            catch { r = null; }
            if (r == null || string.IsNullOrWhiteSpace(r.ActorId))
            {
                sum.Failed++;
                devices.Add(new DeviceStatus
                {
                    ActorId = name, Name = name, Code = "invalid", Label = "数据异常",
                    Detail = "回执内容无法解析（可能写了一半或被人改过）。"
                });
                continue;
            }

            aliases.Names.TryGetValue(r.ActorId, out string? alias);
            string display = string.IsNullOrWhiteSpace(alias)
                ? (string.IsNullOrWhiteSpace(r.DeviceName) ? r.ActorId : r.DeviceName)
                : alias!;

            var d = new DeviceStatus { ActorId = r.ActorId, Alias = alias, Name = display, Receipt = r };
            string sha = r.TargetSha ?? "";
            string state = (r.State ?? "checked").ToLowerInvariant();
            double ageMin = (DateTime.UtcNow - r.CreatedAt.ToUniversalTime()).TotalMinutes;

            // ① 明确的失败最优先 —— 哪怕它也是旧包，先看错误
            if (state == "failed")
            {
                d.Code = "failed";
                d.Label = "失败";
                d.Detail = string.IsNullOrEmpty(r.ReasonCode) ? "本机检查 / 更新失败" : r.ReasonCode;
                d.Reason = r.ReasonText;
                sum.Failed++;
            }
            // ② Seq 倒退：恢复备份 / 换了机器，这份回执已经不能代表当前设备
            else if (seen.TryGetValue(r.ActorId, out long lastSeq) && r.Seq < lastSeq)
            {
                d.Code = "invalid";
                d.Label = "序号倒退";
                d.Detail = "本机回执从 " + lastSeq + " 退回 " + r.Seq + "（可能恢复了备份）";
                sum.Failed++;
            }
            // ③ 超过两个检查周期没有任何新回执 = 失联（不是失败，是「不知道」）
            else if (ageMin > staleMinutes)
            {
                d.Code = "stale";
                d.Label = "失联";
                d.Detail = "距上次上报 " + Math.Round(ageMin) + " 分钟（阈值 " + staleMinutes + " 分钟）";
                sum.Stale++;
            }
            // ④ 回的是别的包 —— 在线，只是还没拉到这次发布的版本
            else if (!string.IsNullOrWhiteSpace(currentSha) &&
                     !string.Equals(sha, currentSha, StringComparison.OrdinalIgnoreCase))
            {
                d.Code = "behind";
                d.Label = "版本落后";
                d.Detail = "本机停在 " + (r.TargetBuiltAt == default ? "未知" : r.TargetBuiltAt.ToString("MM-dd HH:mm"))
                           + " 的包";
                sum.Behind++;
            }
            // ⑤ 回的就是当前包，且这次没报失败
            else if (string.IsNullOrWhiteSpace(sha))
            {
                d.Code = "invalid";
                d.Label = "数据异常";
                d.Detail = "回执没有记录目标包摘要";
                sum.Failed++;
            }
            else
            {
                d.Code = "latest";
                d.Label = "最新";
                d.Detail = state == "applied"
                    ? "已应用 " + r.TargetBuiltAt.ToString("MM-dd HH:mm") + " 的版本"
                    : "已核对，内容无变化";
                sum.Latest++;
            }

            now[r.ActorId] = Math.Max(r.Seq, seen.TryGetValue(r.ActorId, out var prevSeq) ? prevSeq : 0);
            devices.Add(d);
        }

        devices.Sort((a, b) =>
        {
            int rank(string c) => c switch { "failed" => 0, "invalid" => 1, "stale" => 2, "behind" => 3, _ => 4 };
            int x = rank(a.Code).CompareTo(rank(b.Code));
            return x != 0 ? x : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        sum.Total = devices.Count;

        if (persistSeen && Store.EnablePersistence) SaveSeen(now);
        return (sum, devices);
    }

    // =====================================================================
    //  发布端本地：别名表 / 高水位序号
    // =====================================================================

    public static UpdateReceiptAliases LoadAliases()
    {
        try
        {
            if (!File.Exists(AliasPath)) return new UpdateReceiptAliases();
            var a = JsonSerializer.Deserialize<UpdateReceiptAliases>(File.ReadAllText(AliasPath), Opt)
                    ?? new UpdateReceiptAliases();
            a.Names ??= new Dictionary<string, string>();
            return a;
        }
        catch { return new UpdateReceiptAliases(); }
    }

    public static bool SaveAlias(string actorId, string alias)
    {
        if (!Store.EnablePersistence || string.IsNullOrWhiteSpace(actorId)) return false;
        try
        {
            var a = LoadAliases();
            alias = alias.Trim();
            if (alias.Length == 0) a.Names.Remove(actorId);
            else a.Names[actorId] = alias;
            File.WriteAllText(AliasPath, JsonSerializer.Serialize(a, Opt), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    static Dictionary<string, long> LoadSeen()
    {
        try
        {
            if (!File.Exists(SeenPath)) return new Dictionary<string, long>();
            return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(SeenPath), Opt)
                   ?? new Dictionary<string, long>();
        }
        catch { return new Dictionary<string, long>(); }
    }

    static void SaveSeen(Dictionary<string, long> now)
    {
        try
        {
            // 合并而不是覆盖：本次没回执的机器仍记住上次 Seq，等它回来还能识别倒退
            var all = LoadSeen();
            foreach (var kv in now) all[kv.Key] = kv.Value;
            File.WriteAllText(SeenPath, JsonSerializer.Serialize(all, Opt), new UTF8Encoding(false));
        }
        catch { }
    }
}
