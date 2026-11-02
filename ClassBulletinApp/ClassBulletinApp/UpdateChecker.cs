using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 「软件更新」：从 Gitee 仓库的 Release 读取<b>程序自身</b>的最新版本。
///
/// <para>★ 与「数据管理 → 自动更新」完全无关：那个是教室机的课表数据包（manifest + cbpack），
/// 走共享目录 / FTP；这里只回答"程序有没有新版"，不参与数据同步。</para>
///
/// <para>★ 仓库若为私有，匿名读 API 会返回 404 —— 此时退回本机设置里的
/// <see cref="ScheduleCore.Models.AppSettings.GiteeToken"/>（可留空）。
/// 把仓库设为公开后，教室里谁都不用配口令。</para>
/// </summary>
public static class UpdateChecker
{
    /// <summary>Gitee 仓库的属主。</summary>
    public const string RepoOwner = "zengsir1982";

    /// <summary>Gitee 仓库名。</summary>
    public const string RepoName = "class-bulletin-app";

    /// <summary>发布页（浏览器里看全部版本与安装包）。</summary>
    public static string ReleasePageUrl => $"https://gitee.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>仓库主页（仓库私有时，用户要去这里改可见性）。</summary>
    public static string RepoUrl => $"https://gitee.com/{RepoOwner}/{RepoName}";

    /// <summary>一个发布版本的关键信息。</summary>
    public sealed class ReleaseInfo
    {
        /// <summary>形如 v1.0.6（Gitee 上的标签名）。</summary>
        public string Tag = "";

        /// <summary>发布标题。</summary>
        public string Name = "";

        /// <summary>发布说明（Markdown 原文，界面按纯文本显示）。</summary>
        public string Body = "";

        /// <summary>这个版本的发布页地址。</summary>
        public string PageUrl = "";

        /// <summary>安装包直链（没有附件时为空，此时只能用发布页）。</summary>
        public string AssetUrl = "";

        /// <summary>安装包文件名。</summary>
        public string AssetName = "";

        /// <summary>发布时间（原样字符串）。</summary>
        public string PublishedAt = "";

        /// <summary>是否比当前运行版本更新。</summary>
        public bool IsNewer;
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("ClassBulletinApp/" + AppInfo.VersionText); } catch { }
        return c;
    }

    /// <summary>
    /// 联网检查最新版本。
    /// </summary>
    /// <returns>
    /// Ok=false 时 Message 是给人看的原因（断网 / 私有仓库 / 没有发布记录）；
    /// Ok=true 时 Info 一定非空，并通过 <see cref="ReleaseInfo.IsNewer"/> 说明是否有新版。
    /// </returns>
    public static async Task<(bool Ok, string Message, ReleaseInfo? Info)> CheckAsync()
    {
        string token = "";
        try { token = Store.LoadSettings().GiteeToken ?? ""; } catch { }

        string api = $"https://gitee.com/api/v5/repos/{RepoOwner}/{RepoName}/releases/latest";

        var urls = new List<string> { api };
        if (!string.IsNullOrWhiteSpace(token))
            urls.Add(api + "?access_token=" + Uri.EscapeDataString(token.Trim()));

        string lastErr = "";
        bool looksPrivate = false;

        foreach (var url in urls)
        {
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    // 私有仓库匿名访问：Gitee 返回 404 / "Not Found Project"
                    if (code == (int)HttpStatusCode.NotFound
                        || code == (int)HttpStatusCode.Unauthorized
                        || code == (int)HttpStatusCode.Forbidden)
                    {
                        looksPrivate = true;
                    }
                    lastErr = $"HTTP {code}";
                    if (text.Length > 0 && text.Length < 120) lastErr += "　" + text.Trim();
                    continue;
                }

                var info = Parse(text);
                if (info == null)
                {
                    lastErr = "Gitee 返回的内容无法识别";
                    continue;
                }

                info.IsNewer = IsNewer(info.Tag, AppInfo.VersionText);
                string msg = info.IsNewer
                    ? $"发现新版本 {info.Tag}（当前 v{AppInfo.VersionText}）"
                    : $"已是最新版本（当前 v{AppInfo.VersionText}，Gitee 最新 {info.Tag}）";
                return (true, msg, info);
            }
            catch (Exception ex)
            {
                lastErr = ex is TaskCanceledException ? "连接超时" : ex.Message;
            }
        }

        string fail = looksPrivate
            ? "读不到 Gitee 上的发布版本：该仓库目前是「私有」的，教室机无法匿名读取。"
              + "可以到 Gitee 把仓库改为公开（推荐，此后谁都不用配口令），"
              + "或在本机设置文件里填写 GiteeToken；也可以直接点「打开发布页」用浏览器查看。"
            : "连不上 Gitee（" + (string.IsNullOrWhiteSpace(lastErr) ? "网络不可用" : lastErr) + "）。可以点「打开发布页」用浏览器查看。";

        return (false, fail, null);
    }

    /// <summary>解析 Gitee 的 release JSON（只取本软件需要的字段，缺字段不报错）。</summary>
    private static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var info = new ReleaseInfo
            {
                Tag = tag.Trim(),
                Name = GetString(root, "name"),
                Body = GetString(root, "body"),
                PublishedAt = GetString(root, "created_at"),
                PageUrl = $"{ReleasePageUrl}/tag/{Uri.EscapeDataString(tag.Trim())}"
            };

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string url = GetString(a, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    string name = GetString(a, "name");
                    // 优先挑安装包（.exe），没有就取第一个附件
                    bool prefer = name.Contains(".exe", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrEmpty(info.AssetUrl) || prefer)
                    {
                        info.AssetUrl = url;
                        info.AssetName = name;
                    }
                    if (prefer) break;
                }
            }

            return info;
        }
        catch { return null; }
    }

    private static string GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    /// <summary>tag 是否比当前版本新（1.0.10 &gt; 1.0.9，不会被字符串比较骗到）。</summary>
    public static bool IsNewer(string tag, string current) => AppVersionCompare.IsNewer(tag, current);
}
