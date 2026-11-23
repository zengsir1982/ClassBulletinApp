using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 数据源抽象：只做两件事 —— 读 <c>manifest.json</c>、取/放 <c>data.cbpack</c>。
///
/// <para>三种传输（共享文件夹 / U 盘 / FTP）走完全相同的代码路径，
/// "有没有更新"一律用 manifest 里的 SHA-256 判断，<b>不看文件时间戳</b>
/// （FTP 的时间戳常因时区或服务器设置错乱，不可信）。</para>
///
/// <para>服务器端只需两个固定 ASCII 文件名，避免中文文件名在 GBK FTP 上乱码：
/// <c>manifest.json</c> + <c>data.cbpack</c>。</para>
/// </summary>
public interface IFeedSource
{
    /// <summary>Folder / Ftp。</summary>
    string Kind { get; }

    /// <summary>给人看的地址描述。</summary>
    string Describe { get; }

    /// <summary>读远端 manifest.json 原文；文件不存在返回 null；连不上则抛异常。</summary>
    string? ReadManifest();

    /// <summary>把远端 data.cbpack 下载到 <paramref name="destPath"/>。</summary>
    void DownloadPackage(string destPath);

    /// <summary>发布：上传数据包与 manifest。<b>先传包、后传 manifest</b>，避免别人读到"新清单 + 旧数据"。</summary>
    void Upload(string localPackagePath, string manifestJson);

    /// <summary>
    /// <b>只</b>上传 manifest，不动 data.cbpack。
    /// <para>供「独立设置下发」通道使用：manifest 不参与整包摘要，只改它能做到
    /// "下发设置但教室机不重下课表包"。</para>
    /// </summary>
    void UploadManifestOnly(string manifestJson);

    /// <summary>
    /// r146：列出更新源根目录下 <c>status/</c> 里的回执文件名（只含文件名，不含路径）。
    /// 连不上 / 目录不存在时返回空表，不抛异常（发布端只是「还不知道」，不是失败）。
    /// </summary>
    System.Collections.Generic.List<string> ListStatus();

    /// <summary>读一份回执原文；不存在返回 null。名字必须来自 <see cref="ListStatus"/>，不接受外部路径。</summary>
    string? ReadStatus(string name);

    /// <summary>教室机写自己那份回执：tmp → 改名，只覆盖同名文件，绝不碰别人的。</summary>
    void WriteStatusOwn(string name, string json);

    /// <summary>连通性测试（不抛异常）。</summary>
    (bool Ok, string Message) Test();
}

public static class FeedSourceFactory
{
    public const string ManifestName = "manifest.json";
    public const string PackageName = "data.cbpack";

    /// <summary>回执固定放在更新源根目录下这个子目录，与 manifest / 数据包并列。</summary>
    public const string StatusDir = "status";

    /// <summary>回执文件名白名单：只接受 ASCII 字母数字与 -_.，挡住 ../ 与路径分隔符。</summary>
    internal static bool ValidStatusName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return false;
        if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) return false;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
        return true;
    }

    /// <summary>按设置创建数据源；配置不完整时返回 null。</summary>
    public static IFeedSource? Create(AppSettings s)
    {
        switch ((s.UpdateSourceKind ?? "").Trim().ToLowerInvariant())
        {
            case "folder":
                var p = (s.UpdateFolderPath ?? "").Trim();
                return string.IsNullOrWhiteSpace(p) ? null : new FolderFeedSource(p);

            case "ftp":
                var u = (s.UpdateFtpUrl ?? "").Trim();
                return string.IsNullOrWhiteSpace(u)
                    ? null
                    : new FtpFeedSource(u, s.UpdateFtpUser ?? "", s.UpdateFtpPassword ?? "");

            default:
                return null;
        }
    }
}

/// <summary>共享文件夹 / U 盘 / 本地目录。</summary>
public class FolderFeedSource : IFeedSource
{
    readonly string _dir;

    public FolderFeedSource(string dir)
    {
        _dir = dir.TrimEnd('\\', '/');
    }

    public string Kind => "Folder";
    public string Describe => _dir;

    public string? ReadManifest()
    {
        var path = Path.Combine(_dir, FeedSourceFactory.ManifestName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void DownloadPackage(string destPath)
    {
        var src = Path.Combine(_dir, FeedSourceFactory.PackageName);
        if (!File.Exists(src))
            throw new FileNotFoundException("共享位置里没有 data.cbpack（只有 manifest.json）。", src);

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(src, destPath, true);
    }

    public void Upload(string localPackagePath, string manifestJson)
    {
        Directory.CreateDirectory(_dir);

        // 先传包（.tmp 再改名），最后写 manifest —— 保证别人永远读不到半截数据
        var pkgTmp = Path.Combine(_dir, FeedSourceFactory.PackageName + ".tmp");
        File.Copy(localPackagePath, pkgTmp, true);
        File.Move(pkgTmp, Path.Combine(_dir, FeedSourceFactory.PackageName), true);

        var manTmp = Path.Combine(_dir, FeedSourceFactory.ManifestName + ".tmp");
        File.WriteAllText(manTmp, manifestJson, new UTF8Encoding(false));
        File.Move(manTmp, Path.Combine(_dir, FeedSourceFactory.ManifestName), true);
    }

    public void UploadManifestOnly(string manifestJson)
    {
        Directory.CreateDirectory(_dir);
        // .tmp 再改名：别人永远读不到写了一半的清单
        var manTmp = Path.Combine(_dir, FeedSourceFactory.ManifestName + ".tmp");
        File.WriteAllText(manTmp, manifestJson, new UTF8Encoding(false));
        File.Move(manTmp, Path.Combine(_dir, FeedSourceFactory.ManifestName), true);
    }

    // ---------- r146：更新回执通道（status/ 目录） ----------

    string StatusPath(string name) => Path.Combine(_dir, FeedSourceFactory.StatusDir, name);

    public System.Collections.Generic.List<string> ListStatus()
    {
        try
        {
            string dir = Path.Combine(_dir, FeedSourceFactory.StatusDir);
            if (!Directory.Exists(dir)) return new System.Collections.Generic.List<string>();
            var names = new System.Collections.Generic.List<string>();
            foreach (var f in Directory.GetFiles(dir, "u-*.json"))
            {
                string n = Path.GetFileName(f);
                if (FeedSourceFactory.ValidStatusName(n)) names.Add(n);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch { return new System.Collections.Generic.List<string>(); }
    }

    public string? ReadStatus(string name)
    {
        if (!FeedSourceFactory.ValidStatusName(name)) return null;
        try
        {
            string p = StatusPath(name);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    public void WriteStatusOwn(string name, string json)
    {
        if (!FeedSourceFactory.ValidStatusName(name)) return;
        string dir = Path.Combine(_dir, FeedSourceFactory.StatusDir);
        Directory.CreateDirectory(dir);
        // 每台机器独占自己的文件名；tmp 带随机串，避免两台机器同时写时撞临时文件
        string tmp = Path.Combine(dir, name + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, StatusPath(name), true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public (bool Ok, string Message) Test()
    {
        try
        {
            if (!Directory.Exists(_dir))
                return (false, "目录不存在或无权访问：" + _dir);

            var man = Path.Combine(_dir, FeedSourceFactory.ManifestName);
            var pkg = Path.Combine(_dir, FeedSourceFactory.PackageName);

            if (!File.Exists(man))
                return (false, "已连上目录，但没有 manifest.json。请先有人发布一次数据包。");

            return File.Exists(pkg)
                ? (true, "已连接，数据包存在。")
                : (false, "有 manifest.json 但没有 data.cbpack，包可能上传到一半。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>
/// FTP（只读消费 + 发布上传）。
/// 用 .NET 内置 <see cref="FtpWebRequest"/>，不引入第三方库；
/// 只做 RETR / STOR，不需要列目录，因此不受各家 LIST 格式差异影响。
/// </summary>
public class FtpFeedSource : IFeedSource
{
    readonly string _base;
    readonly string _user;
    readonly string _pwd;

    public FtpFeedSource(string baseUrl, string user, string pwd)
    {
        _base = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        _user = user ?? "";
        _pwd = pwd ?? "";
    }

    public string Kind => "Ftp";
    public string Describe => _base;

    FtpWebRequest Make(string name, string method)
    {
        // SYSLIB0014 提示 WebRequest 已过时、建议改用 HttpClient —— 但 HttpClient 不支持 ftp，
        // FtpWebRequest 仍是 .NET 内置唯一的 ftp 实现，这里只做 RETR/STOR，没有替代品。
#pragma warning disable SYSLIB0014
        var req = (FtpWebRequest)WebRequest.Create(new Uri(_base + name));
#pragma warning restore SYSLIB0014
        req.Method = method;
        req.UsePassive = true;          // 教室机多在 NAT 后，被动模式更通
        req.UseBinary = true;
        req.Timeout = 8000;
        req.ReadWriteTimeout = 20000;
        req.KeepAlive = false;
        req.Credentials = string.IsNullOrEmpty(_user)
            ? new NetworkCredential("anonymous", "anonymous")
            : new NetworkCredential(_user, _pwd);
        return req;
    }

    static bool IsNotFound(WebException we)
        => we.Response is FtpWebResponse r &&
           (r.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable ||
            r.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailableOrBusy);

    public string? ReadManifest()
    {
        try
        {
            var req = Make(FeedSourceFactory.ManifestName, WebRequestMethods.Ftp.DownloadFile);
            using var resp = (FtpWebResponse)req.GetResponse();
            using var s = resp.GetResponseStream();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (WebException we) when (IsNotFound(we))
        {
            return null;      // 服务器上没有这个文件 = 还没人发布过
        }
    }

    public void DownloadPackage(string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var req = Make(FeedSourceFactory.PackageName, WebRequestMethods.Ftp.DownloadFile);
        using var resp = (FtpWebResponse)req.GetResponse();
        using var s = resp.GetResponseStream();
        using var fs = File.Create(destPath);
        s.CopyTo(fs);
    }

    public void Upload(string localPackagePath, string manifestJson)
    {
        UploadBytes(FeedSourceFactory.PackageName, File.ReadAllBytes(localPackagePath));
        UploadBytes(FeedSourceFactory.ManifestName, new UTF8Encoding(false).GetBytes(manifestJson));
    }

    void UploadBytes(string name, byte[] data)
    {
        var req = Make(name, WebRequestMethods.Ftp.UploadFile);
        req.ContentLength = data.Length;
        using (var s = req.GetRequestStream())
        {
            s.Write(data, 0, data.Length);
        }
        using var resp = (FtpWebResponse)req.GetResponse();
    }

    public void UploadManifestOnly(string manifestJson)
        => UploadBytes(FeedSourceFactory.ManifestName, new UTF8Encoding(false).GetBytes(manifestJson));

    // ---------- r146：更新回执通道（status/ 目录） ----------

    public System.Collections.Generic.List<string> ListStatus()
    {
        var names = new System.Collections.Generic.List<string>();
        try
        {
            var req = Make(FeedSourceFactory.StatusDir + "/", WebRequestMethods.Ftp.ListDirectory);
            using var resp = (FtpWebResponse)req.GetResponse();
            using var s = resp.GetResponseStream();
            using var r = new StreamReader(s);
            while (r.ReadLine() is { } line)
            {
                // 有的服务器 NLST 返回带路径的全名，只留最后一段
                string n = line.Trim().Split('/', '\\').Last();
                if (n.StartsWith("u-", StringComparison.Ordinal) &&
                    n.EndsWith(".json", StringComparison.Ordinal) &&
                    FeedSourceFactory.ValidStatusName(n) && !names.Contains(n))
                    names.Add(n);
            }
        }
        catch { /* 目录不存在 / 列表被禁 = 还没有回执，不当成错误 */ }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public string? ReadStatus(string name)
    {
        if (!FeedSourceFactory.ValidStatusName(name)) return null;
        try
        {
            var req = Make(FeedSourceFactory.StatusDir + "/" + name, WebRequestMethods.Ftp.DownloadFile);
            using var resp = (FtpWebResponse)req.GetResponse();
            using var s = resp.GetResponseStream();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (WebException we) when (IsNotFound(we)) { return null; }
        catch { return null; }
    }

    public void WriteStatusOwn(string name, string json)
    {
        if (!FeedSourceFactory.ValidStatusName(name)) return;
        // 目录可能还没建：试一次，失败就当管理员已预先建好（很多 FTP 禁止客户端建目录）
        try
        {
            var d = Make(FeedSourceFactory.StatusDir + "/", WebRequestMethods.Ftp.MakeDirectory);
            using var dr = (FtpWebResponse)d.GetResponse();
        }
        catch { }
        UploadBytes(FeedSourceFactory.StatusDir + "/" + name, new UTF8Encoding(false).GetBytes(json));
    }

    public (bool Ok, string Message) Test()
    {
        try
        {
            var m = ReadManifest();
            if (m == null)
                return (false, "已连上 FTP，但根目录没有 manifest.json（还没发布过，或路径不对）。");
            return (true, "已连接，manifest 存在。");
        }
        catch (WebException we)
        {
            // Undefined = 根本没连上（DNS/端口/防火墙），把无意义的 "Undefined" 藏掉
            string code = we.Response is FtpWebResponse r && r.StatusCode != FtpStatusCode.Undefined
                ? $"（{r.StatusCode}）"
                : "";
            return (false, $"连接失败{code}：{we.Message}　请检查地址、端口、账号密码，以及是否开启了被动模式。");
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }
}
