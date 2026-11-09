using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 班级内容摆渡通道（二期专用）。
///
/// <para>★ 与 <see cref="IFeedSource"/> 完全分开：<see cref="IFeedSource"/> 只负责发布链的
/// <c>manifest.json</c> + <c>data.cbpack</c>；本接口只在共享根下新建 <c>board/&lt;班级&gt;/messages|receipts/</c>，
/// 绝不读写主包两个文件，也绝不会触发课表刷新。</para>
///
/// <para>★ 只新建、不改写：<see cref="WriteNew"/> 在目标已存在时必须返回 false。
/// UNC 侧靠「同目录 tmp → File.Move 不覆盖」保证原子；FTP 没有 rename/CreateNew，
/// 靠 NLST 查重 + STOR 唯一文件名绕开，文件名自带 actorId + msgId，同一台机不会并发同 id。</para>
/// </summary>
public interface IBoardTransport
{
    string Kind { get; }
    string Describe { get; }

    /// <summary>列出某个班级某个桶里的 .json 文件名（只是文件名，不含路径）。目录不存在返回空。</summary>
    IReadOnlyList<string> List(string classFolder, string bucket);

    /// <summary>读一个文件；不存在返回 null；连不上 / 内容损坏时由调用方决定怎么报错。</summary>
    string? Read(string classFolder, string bucket, string name);

    /// <summary>只新建。已存在返回 false，绝不覆盖。</summary>
    bool WriteNew(string classFolder, string bucket, string name, string text);

    /// <summary>
    /// 写「自己独占的状态文件」（目前只有教室机内容快照）。文件名带 actorId，
    /// 允许覆盖<b>自己上次</b>写的那份，绝不覆盖别人的文件；Folder 侧用 tmp→Move 保证原子。
    /// </summary>
    bool WriteOwn(string classFolder, string bucket, string name, string text);

    (bool Ok, string Message) Test();
}

public static class BoardTransportFactory
{
    /// <summary>
    /// UNC 优先 + FTP 兼容：跟随现有「自动更新数据源」的配置；
    /// 也可在设置里用 <see cref="AppSettings.BoardSyncRoot"/> 单独指定摆渡根。
    /// 没配数据源时返回 null（同步功能不可用，但绝不影响本机编辑）。
    /// </summary>
    public static IBoardTransport? Create(AppSettings s)
    {
        string custom = (s.BoardSyncRoot ?? "").Trim();
        switch ((s.UpdateSourceKind ?? "").Trim().ToLowerInvariant())
        {
            case "folder":
                string dir = custom.Length > 0 ? custom : (s.UpdateFolderPath ?? "").Trim();
                return string.IsNullOrWhiteSpace(dir) ? null : new FolderBoardTransport(dir);

            case "ftp":
                string url = (s.UpdateFtpUrl ?? "").Trim();
                if (custom.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)) url = custom;
                return string.IsNullOrWhiteSpace(url)
                    ? null
                    : new FtpBoardTransport(url, s.UpdateFtpUser ?? "", s.UpdateFtpPassword ?? "");

            default:
                return null;
        }
    }
}

/// <summary>UNC 共享目录 / U 盘 / 本地目录（诊断时指向 CB_DATA_DIR\share 即可全离线跑）。</summary>
public class FolderBoardTransport : IBoardTransport
{
    readonly string _root;

    public FolderBoardTransport(string root)
    {
        _root = root.TrimEnd('\\', '/');
    }

    public string Kind => "Folder";
    public string Describe => Path.Combine(_root, BoardSyncConsts.Root);

    string DirOf(string classFolder, string bucket)
        => Path.Combine(_root, BoardSyncConsts.Root, classFolder, bucket);

    public IReadOnlyList<string> List(string classFolder, string bucket)
    {
        var dir = DirOf(classFolder, bucket);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.json")
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToArray();
    }

    public string? Read(string classFolder, string bucket, string name)
    {
        var path = Path.Combine(DirOf(classFolder, bucket), name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public bool WriteNew(string classFolder, string bucket, string name, string text)
    {
        var dir = DirOf(classFolder, bucket);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);
        if (File.Exists(dest)) return false;

        // 同目录 tmp → 不带 overwrite 的 Move：远端永远读不到半截 JSON。
        var tmp = Path.Combine(dir, name + ".tmp");
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, dest);   // 目标已存在会抛 IOException —— 这正是我们要的「不覆盖」
            return true;
        }
        catch (IOException)
        {
            return false;           // 并发下别人先写了同名文件：等价于「已经发过」，幂等成功
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public bool WriteOwn(string classFolder, string bucket, string name, string text)
    {
        var dir = DirOf(classFolder, bucket);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);
        var tmp = Path.Combine(dir, name + ".tmp");
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, dest, true);   // 只覆盖自己那份；别的设备文件名不同，碰不到
            return true;
        }
        catch (IOException)
        {
            return false;
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
            Directory.CreateDirectory(Path.Combine(_root, BoardSyncConsts.Root));
            return (true, "已连接：" + Describe);
        }
        catch (Exception ex)
        {
            return (false, "摆渡目录不可写：" + ex.Message);
        }
    }
}

/// <summary>
/// FTP 兼容实现：RETR / STOR / NLST / MKD。
/// 服务器不支持列目录时，慢路径全量对账会失败并明确报错，不会静默丢消息。
/// </summary>
public class FtpBoardTransport : IBoardTransport
{
    readonly string _base;
    readonly string _user;
    readonly string _pwd;

    public FtpBoardTransport(string baseUrl, string user, string pwd)
    {
        _base = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        _user = user ?? "";
        _pwd = pwd ?? "";
    }

    public string Kind => "Ftp";
    public string Describe => _base + BoardSyncConsts.Root + "/";

    string DirUrl(string classFolder, string bucket)
        => _base + BoardSyncConsts.Root + "/" + classFolder + "/" + bucket + "/";

#pragma warning disable SYSLIB0014   // HttpClient 不支持 ftp，FtpWebRequest 是 .NET 内置唯一实现
    FtpWebRequest Make(string url, string method)
    {
        var req = (FtpWebRequest)WebRequest.Create(new Uri(url));
#pragma warning restore SYSLIB0014
        req.Method = method;
        req.UsePassive = true;
        req.UseBinary = true;
        req.Timeout = 8000;
        req.ReadWriteTimeout = 20000;
        req.KeepAlive = false;
        req.Credentials = string.IsNullOrEmpty(_user)
            ? new NetworkCredential("anonymous", "anonymous")
            : new NetworkCredential(_user, _pwd);
        return req;
    }

    static bool IsMissing(WebException we)
        => we.Response is FtpWebResponse r &&
           (r.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable ||
            r.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailableOrBusy);

    void EnsureDir(string url)
    {
        try
        {
            using var resp = (FtpWebResponse)Make(url, WebRequestMethods.Ftp.MakeDirectory).GetResponse();
        }
        catch (WebException)
        {
            // 550 通常是「目录已存在」，也有服务器不允许 MKD 但目录其实可写。
            // 这里一律放行：真正读写失败时会在 List / Read / WriteNew 里大声报错，不会静默丢消息。
        }
    }

    void EnsureTree(string classFolder, string bucket)
    {
        EnsureDir(_base + BoardSyncConsts.Root + "/");
        EnsureDir(_base + BoardSyncConsts.Root + "/" + classFolder + "/");
        EnsureDir(DirUrl(classFolder, bucket));
    }

    public IReadOnlyList<string> List(string classFolder, string bucket)
    {
        try
        {
            var req = Make(DirUrl(classFolder, bucket), WebRequestMethods.Ftp.ListDirectory);
            using var resp = (FtpWebResponse)req.GetResponse();
            using var s = resp.GetResponseStream();
            using var r = new StreamReader(s, Encoding.UTF8);
            var all = r.ReadToEnd();
            return all.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                      .Select(x => x.Trim().TrimEnd('\r'))
                      .Select(x => x.Contains('/') ? x[(x.LastIndexOf('/') + 1)..] : x)
                      .Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                      .Distinct(StringComparer.Ordinal)
                      .OrderBy(x => x, StringComparer.Ordinal)
                      .ToArray();
        }
        catch (WebException we) when (IsMissing(we))
        {
            return Array.Empty<string>();   // 目录还没建 = 还没有消息
        }
    }

    public string? Read(string classFolder, string bucket, string name)
    {
        try
        {
            var req = Make(DirUrl(classFolder, bucket) + name, WebRequestMethods.Ftp.DownloadFile);
            using var resp = (FtpWebResponse)req.GetResponse();
            using var s = resp.GetResponseStream();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (WebException we) when (IsMissing(we))
        {
            return null;
        }
    }

    public bool WriteNew(string classFolder, string bucket, string name, string text)
    {
        EnsureTree(classFolder, bucket);
        if (List(classFolder, bucket).Contains(name, StringComparer.Ordinal)) return false;

        var req = Make(DirUrl(classFolder, bucket) + name, WebRequestMethods.Ftp.UploadFile);
        byte[] data = new UTF8Encoding(false).GetBytes(text);
        req.ContentLength = data.Length;
        using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        using var resp = (FtpWebResponse)req.GetResponse();
        return true;
    }

    public bool WriteOwn(string classFolder, string bucket, string name, string text)
    {
        EnsureTree(classFolder, bucket);
        // FTP 无 rename，STOR 覆盖自己那份即可；读端对快照做严格 JSON 校验，
        // 万一读到半截会明确报「快照暂不可用」，不会当成空内容静默处理。
        var req = Make(DirUrl(classFolder, bucket) + name, WebRequestMethods.Ftp.UploadFile);
        byte[] data = new UTF8Encoding(false).GetBytes(text);
        req.ContentLength = data.Length;
        using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        using var resp = (FtpWebResponse)req.GetResponse();
        return true;
    }

    public (bool Ok, string Message) Test()
    {
        try
        {
            EnsureDir(_base + BoardSyncConsts.Root + "/");
            return (true, "已连接：" + Describe);
        }
        catch (Exception ex)
        {
            return (false, "FTP 摆渡目录不可用：" + ex.Message);
        }
    }
}
