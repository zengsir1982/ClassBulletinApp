using System;
using System.IO;
using System.Linq;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 数据包自动更新：检查 → 下载 → 校验 → 应用。
///
/// <para>判断"有没有更新"只比 <c>manifest.DataSha256</c>（整包摘要），
/// 与本机 <c>LastAppliedSha</c> 对照；<b>不看文件时间戳</b>。</para>
///
/// <para>任何一步失败都不抛异常、不破坏本地数据：检查失败就当没更新，下载失败就保留旧数据。</para>
/// </summary>
public static class UpdateService
{
    public class CheckResult
    {
        public bool Ok { get; set; }
        public bool HasUpdate { get; set; }
        public string Message { get; set; } = "";
        public string RemoteSha { get; set; } = "";
        public PackageManifest? Manifest { get; set; }
    }

    public class ApplyOutcome
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public SchedulePackage.InspectResult? Package { get; set; }
        public SchedulePackage.ApplyResult? Applied { get; set; }
    }

    /// <summary>只读检查，不下载、不写盘。</summary>
    public static CheckResult Check(IFeedSource src, AppSettings st)
    {
        var r = new CheckResult();
        try
        {
            var json = src.ReadManifest();
            if (json == null)
            {
                r.Message = "数据源上没有 manifest.json —— 还没有人发布过数据包。";
                return r;
            }

            var man = SchedulePackage.ParseManifest(json);
            if (man == null)
            {
                r.Message = "manifest.json 无法解析（可能上传不完整）。";
                return r;
            }
            if (man.Schema > SchedulePackage.SchemaVersion)
            {
                r.Message = $"数据包格式 v{man.Schema} 高于本程序支持的 v{SchedulePackage.SchemaVersion}，请先升级程序。";
                return r;
            }
            if (!string.Equals(man.Kind, "data", StringComparison.OrdinalIgnoreCase))
            {
                r.Message = "这不是数据包（kind 不是 data）。";
                return r;
            }

            r.Ok = true;
            r.Manifest = man;
            r.RemoteSha = man.DataSha256 ?? "";
            r.HasUpdate = !string.Equals(st.LastAppliedSha ?? "", r.RemoteSha, StringComparison.OrdinalIgnoreCase);
            r.Message = r.HasUpdate
                ? $"发现新版本：{man.BuiltAt:MM-dd HH:mm} 打包，{man.ClassCount} 个班、调代 {man.OverrideCount} 条"
                  + (string.IsNullOrEmpty(man.OverrideRange) ? "" : $"（{man.OverrideRange}）")
                : $"已是最新版本（{man.BuiltAt:MM-dd HH:mm}）。";
            return r;
        }
        catch (Exception ex)
        {
            r.Message = "检查失败：" + ex.Message;
            return r;
        }
    }

    /// <summary>下载到临时文件并校验（<b>不应用</b>），供"先预览再确认"的流程使用。</summary>
    public static ApplyOutcome Download(IFeedSource src)
    {
        var r = new ApplyOutcome();
        string tmp = Path.Combine(Path.GetTempPath(),
            "cbupdate-" + Guid.NewGuid().ToString("N")[..8] + ".cbpack");
        try
        {
            src.DownloadPackage(tmp);

            var ins = SchedulePackage.Inspect(tmp);
            if (!ins.Ok)
            {
                r.Message = "下载的数据包无效：" + ins.Error;
                return r;
            }

            r.Ok = true;
            r.Package = ins;
            r.Message = ins.Summary;
            return r;
        }
        catch (Exception ex)
        {
            r.Message = "下载失败：" + ex.Message;
            return r;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 下载 + 校验 + 应用。成功后把整包摘要写进设置，下次检查即可判断"已是最新"。
    /// </summary>
    public static ApplyOutcome DownloadAndApply(IFeedSource src)
    {
        var r = new ApplyOutcome();
        string tmp = Path.Combine(Path.GetTempPath(),
            "cbupdate-" + Guid.NewGuid().ToString("N")[..8] + ".cbpack");
        try
        {
            src.DownloadPackage(tmp);

            var ins = SchedulePackage.Inspect(tmp);
            if (!ins.Ok)
            {
                r.Message = "下载的数据包无效：" + ins.Error;
                return r;
            }

            var ap = SchedulePackage.Apply(ins);
            r.Package = ins;
            r.Applied = ap;
            if (!ap.Ok)
            {
                r.Message = "应用失败：" + ap.Error;
                return r;
            }

            var st = Store.LoadSettings();
            st.LastAppliedSha = ins.Manifest?.DataSha256 ?? "";
            st.LastAppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            st.LastUpdateCheckInfo = $"已应用 {ins.Manifest?.BuiltAt:MM-dd HH:mm} 的版本";
            Store.SaveSettings(st);

            r.Ok = true;
            r.Message = $"已更新：{ins.Manifest?.ClassCount} 个班、调代 {ap.OverrideAdded} 条";
            return r;
        }
        catch (Exception ex)
        {
            r.Message = "更新失败：" + ex.Message;
            return r;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 发布：把本机当前数据打包并上传到数据源（先传包、后传 manifest）。
    /// 用于"主控机手动发布"；从 source 目录的 Excel 自动打包见 <see cref="PublishFromSource"/>。
    /// </summary>
    public static (bool Ok, string Message) PublishCurrent(IFeedSource src, string builtBy)
    {
        string tmp = Path.Combine(Path.GetTempPath(),
            "cbpublish-" + Guid.NewGuid().ToString("N")[..8] + ".cbpack");
        try
        {
            // ★ overridesAuthoritative = true：手工「发布到数据源」＝把本机这份数据当成全校真相。
            //   教室机导入后会清掉本机全部导入来源的调代课，再写入包内记录，
            //   于是"本机有、包里没有"的旧调代标记会被取消、还原成底表原课程
            //   （临时课表都是一周一份，包里没有该周 = 该周没有调代课）。
            var st0 = Store.LoadSettings();
            bool includeManual = st0.OverwriteManualOverrides;

            // 策略变了但文件没变时摘要不会变 → 各机认为"已是最新" → 策略永远下发不下去。
            // 这里与 SourcePublisher 用同一套规则：比对远端旧包，策略不同就 Rev+1。
            int rev = st0.PackageRev;
            try
            {
                var old = SchedulePackage.ParseManifest(src.ReadManifest());
                if (old != null)
                {
                    rev = Math.Max(rev, old.Rev);
                    if (old.OverrideIncludeManual != includeManual || !old.OverrideAuthoritative)
                        rev = Math.Max(rev + 1, old.Rev + 1);
                }
            }
            catch { /* 连不上就按本地 Rev 走 */ }

            if (rev != st0.PackageRev)
            {
                st0.PackageRev = rev;
                Store.SaveSettings(st0);
            }

            var ex = SchedulePackage.Export(tmp, Store.LoadSchedule(), Store.LoadOverrides(),
                Store.LoadNotifications(), st0, builtBy,
                overridesAuthoritative: true,
                includeManualOverrides: includeManual,
                rev: rev,
                users: Store.LoadUsers().Users);
            if (!ex.Ok) return (false, "打包失败：" + ex.Error);

            string manifestJson = ReadManifestFromPackage(tmp);
            if (string.IsNullOrEmpty(manifestJson))
                return (false, "打包后读不到 manifest.json。");

            src.Upload(tmp, manifestJson);

            var st = Store.LoadSettings();
            st.LastAppliedSha = SchedulePackage.ReadDataSha(manifestJson);
            st.LastAppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            st.LastUpdateCheckInfo = $"本机已发布 {DateTime.Now:MM-dd HH:mm} 的版本";
            Store.SaveSettings(st);

            return (true, $"已发布：{ex.ClassCount} 个班、调代 {ex.OverrideCount} 条，{ex.Size / 1024.0:0.#} KB");
        }
        catch (Exception ex2)
        {
            return (false, "发布失败：" + ex2.Message);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    static string ReadManifestFromPackage(string pkgPath)
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

    /// <summary>设置里描述数据源的一句话（用于界面回显）。</summary>
    public static string DescribeSource(AppSettings st)
    {
        var src = FeedSourceFactory.Create(st);
        if (src == null) return "未配置";
        return $"{src.Kind} · {src.Describe}";
    }
}
