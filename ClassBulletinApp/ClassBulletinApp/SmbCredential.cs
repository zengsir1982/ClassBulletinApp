using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassBulletinApp;

/// <summary>
/// 把 SMB（共享文件夹）凭据写进 Windows 凭据管理器 —— 等价于命令行
/// <c>cmdkey /add:主机 /user:账号 /pass:密码</c>。
///
/// <para>★ 为什么不自己用 <c>WNetAddConnection2W</c> 建连接：凭据管理器是"系统级预存"，
/// Windows 的 SMB 重定向器在访问 UNC 路径时会自己去查它。也就是说 ——
/// 只要凭据躺在这里，<c>Directory.Exists(@"\\主机\共享")</c> 就自动带上身份，
/// <c>SourcePublisher</c> / <c>UpdateService</c> 一行都不用改。</para>
///
/// <para>★ 密码只进凭据管理器，绝不写进 settings.json：settings.json 是明文 JSON，
/// 30 台教室机随数据包传来传去，一旦落盘等于把共享账号公开。</para>
///
/// <para>★ 为什么不用 <c>Process.Start("cmdkey", ...)</c>：密码会出现在命令行里，
/// 任务管理器、进程审计、命令历史都能看到。CredWrite 走 API，密码只经过本进程内存。</para>
/// </summary>
internal static class SmbCredential
{
    const int CredTypeDomainPassword = 2;
    const int CredPersistLocalMachine = 2;   // 重启后仍有效（与 cmdkey 默认一致）

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    static extern void CredFree(IntPtr buffer);

    // ---------- 目标名归一 ----------

    /// <summary>
    /// 从任意写法里取出"凭据 target"，即主机名或 IP。
    /// <para>★ cmdkey 最常踩的坑就在这：凭据是按 target 字符串精确匹配的。
    /// 程序里写 <c>\\192.168.40.168\dfzx\source</c>，凭据就必须挂在 <c>192.168.40.168</c> 上；
    /// 若挂成 <c>\\192.168.40.168</c>（带反斜杠）或 <c>ZLOVEK</c>（主机名），就会"填了密码还是弹框"。</para>
    /// </summary>
    public static string TargetOf(string? pathOrHost)
    {
        var s = (pathOrHost ?? "").Trim();
        if (s.Length == 0) return "";

        // ftp:// 之类不是 SMB，直接拒
        if (s.Contains("://")) return "";

        if (s.StartsWith(@"\\", StringComparison.Ordinal)) s = s.Substring(2);
        else if (s.StartsWith("//", StringComparison.Ordinal)) s = s.Substring(2);

        int cut = s.IndexOfAny(new[] { '\\', '/' });
        if (cut > 0) s = s.Substring(0, cut);

        return s.Trim();
    }

    /// <summary>从 UNC 路径里取出共享名（<c>\\host\dfzx\source</c> → dfzx）。</summary>
    public static string ShareOf(string? uncPath)
    {
        var s = (uncPath ?? "").Trim().TrimStart('\\', '/');
        int first = s.IndexOfAny(new[] { '\\', '/' });
        if (first < 0) return "";
        var rest = s.Substring(first + 1);
        int second = rest.IndexOfAny(new[] { '\\', '/' });
        return second < 0 ? rest : rest.Substring(0, second);
    }

    // ---------- 读 / 写 / 删 ----------

    /// <summary>当前 Windows 用户下，这个 target 有没有存过凭据。返回已存的用户名（读不到返回 null）。</summary>
    public static string? ReadUser(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        if (!CredRead(target, CredTypeDomainPassword, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return string.IsNullOrWhiteSpace(c.UserName) ? null : c.UserName;
        }
        finally { CredFree(ptr); }
    }

    /// <summary>
    /// 写入（覆盖）凭据。先删再写，避免旧密码残留导致"改了密码还是用旧的"。
    /// 失败抛 <see cref="Win32Exception"/>，由调用方翻成中文。
    /// </summary>
    public static void Write(string target, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("凭据目标（主机名/IP）为空。");
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("用户名为空。");

        Delete(target);   // 忽略是否成功：不存在时 CredDelete 本来就会返回 false

        IntPtr blob = IntPtr.Zero;
        try
        {
            blob = Marshal.StringToCoTaskMemUni(password ?? "");

            var c = new CREDENTIAL
            {
                Type = CredTypeDomainPassword,
                TargetName = target,
                Comment = "班级公布栏：课程表共享目录账号",
                CredentialBlobSize = (password ?? "").Length * 2,   // UTF-16，不含结尾 \0
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = user
            };

            if (!CredWrite(ref c, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (blob != IntPtr.Zero) Marshal.FreeCoTaskMem(blob);
        }
    }

    public static void Delete(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        CredDelete(target, CredTypeDomainPassword, 0);
    }

    // ---------- 断开连接 / 映射盘符 ----------

    const int ResourceTypeDisk = 1;
    const int ConnectUpdateProfile = 0x1;    // 写进用户配置，重启后自动恢复（= 映射时的"登录时重新连接"）

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    static extern int WNetAddConnection2W(ref NETRESOURCE res, string? password, string? username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    static extern int WNetCancelConnection2W(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    /// <summary>
    /// 断开到这台电脑的连接（等价 <c>net use \\主机\共享 /delete /y</c>）。
    /// <para>★ 换凭据之前必须先断：凭据管理器里的条目改了，<b>已经建立的会话仍挂在旧身份上</b>。
    /// 不断开就点"测试连接"，测出来的是旧账号的结果，会让人误以为新账号已经配好了
    /// （2026-11-02 在本机实测踩到：凭据里存的是 zeng，测试却一路通过）。</para>
    /// <para>★ 为什么要带共享名：实测 Windows 对 <c>WNetCancelConnection2(@"\\主机")</c>
    /// 直接返回 2250（找不到网络连接），<c>net use \\主机 /delete</c> 也一样报 2250 ——
    /// 服务器级断开在这套系统上不生效，必须指定到具体共享。所以这里先断 <c>\\主机\共享</c>，
    /// 再兜一次 <c>\\主机\IPC$</c>（每个 SMB 会话都会连它），覆盖只知道主机名的情况。</para>
    /// </summary>
    /// <returns>true = 至少断掉了一条连接；false = 本来就没有连接（不算错误）。</returns>
    public static bool DisconnectHost(string hostOrUnc)
    {
        var h = TargetOf(hostOrUnc);
        if (h.Length == 0) return false;

        var share = ShareOf(hostOrUnc);
        if (share.Length > 0 && WNetCancelConnection2W(@"\\" + h + "\\" + share, 0, true) == 0)
            return true;

        return WNetCancelConnection2W(@"\\" + h + "\\IPC$", 0, true) == 0;
    }

    /// <summary>找一个没被占用的盘符（从 Z 往前找），全占满返回空串。</summary>
    public static string FreeDriveLetter()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                used.Add(d.Name.TrimEnd('\\', ':'));
        }
        catch { /* 枚举失败就当作都没占用，真冲突时 WNetAddConnection2 会返回 85 */ }

        for (char c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c.ToString())) return c.ToString();
        return "";
    }

    /// <summary>
    /// 把共享目录映射成盘符（等价"映射网络驱动器"）。
    /// <para>用户名为 null 时 Windows 会去用本机<b>已保存的凭据</b>，
    /// 所以调用前必须先 <see cref="Write"/> 成功 —— 这样密码不用经过命令、也不会留在任何地方。</para>
    /// <para>★ 映射出来的盘符只给"人拖文件"用。程序里的发布源目录必须写 UNC
    /// （<c>\\主机\共享\source</c>）：盘符只在交互登录会话里存在，开机早期和服务 / 计划任务看不到它，
    /// 填成 <c>Z:\</c> 会变成"源目录不存在"。</para>
    /// </summary>
    public static (bool Ok, string Message, string Drive) MapDrive(string uncPath)
    {
        var unc = (uncPath ?? "").Trim().TrimEnd('\\');
        if (unc.Length == 0 || TargetOf(unc).Length == 0)
            return (false, "共享路径不是 \\\\主机\\共享 的形式。", "");

        var letter = FreeDriveLetter();
        if (letter.Length == 0) return (false, "D 到 Z 的盘符都被占用了，先腾一个出来。", "");

        var res = new NETRESOURCE
        {
            dwType = ResourceTypeDisk,
            lpLocalName = letter + ":",
            lpRemoteName = unc
        };

        int rc = WNetAddConnection2W(ref res, null, null, ConnectUpdateProfile);
        if (rc == 0) return (true, "", letter + ":");

        return (false, Explain(rc), "");
    }

    /// <summary>
    /// 断开一个盘符映射（等价 <c>net use Z: /delete</c>）。
    /// <para>flags 传 <c>CONNECT_UPDATE_PROFILE</c>：只有这样才会把持久化记录一起删掉，
    /// 否则重启后那个盘符会自己回来。</para>
    /// </summary>
    public static bool UnmapDrive(string drive)
    {
        var d = (drive ?? "").Trim().TrimEnd('\\');
        if (d.Length == 0) return false;
        if (!d.EndsWith(':')) d += ":";
        return WNetCancelConnection2W(d, ConnectUpdateProfile, true) == 0;
    }

    // ---------- 探路 ----------

    /// <summary>探一次共享目录，返回 (是否可读, 说明, xlsx 数量, 是否可写入)。不抛异常。</summary>
    /// <remarks>
    /// ★ 不用 <c>Directory.Exists</c> 判存在：它把"无权限"和"路径不存在"都吞成 false，
    /// 现场排查时这两种原因的处理办法完全不同（一个去给权限、一个去改共享名），
    /// 只说"目录打不开"会让人白查半天。这里直接列目录，让异常类型把原因分开。
    /// </remarks>
    public static (bool Ok, string Message, int XlsxCount, bool CanWrite) Probe(string uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath))
            return (false, "路径是空的。", 0, false);

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(uncPath);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "能连上这台电脑，但被拒绝访问：" + uncPath
                + "\n→ 账号或密码不对，或者这个账号没有该共享的读取权限"
                + "（共享权限和 NTFS 权限都要给，取交集）。", 0, false);
        }
        catch (DirectoryNotFoundException)
        {
            return (false, "路径不存在：" + uncPath
                + "\n→ 共享名写错了，或者共享目录下面没有这个子目录。", 0, false);
        }
        catch (Exception ex)
        {
            int code = ex.HResult & 0xFFFF;
            var msg = code > 0 && code < 16000 ? Explain(code) : ex.Message;
            return (false, "访问失败：" + msg, 0, false);
        }

        int n = 0, artifacts = 0;
        foreach (var f in entries)
        {
            if (!f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(f);
            if (name.StartsWith("~$", StringComparison.Ordinal)) continue;            // Excel 锁文件
            // 副本文件不算数：发布时会被跳过，这里数进去会造成"向导说 4 份、实际只用 3 份"。
            if (ScheduleCore.Services.SourcePublisher.IsArtifactName(name)) { artifacts++; continue; }
            n++;
        }

        // 顺手做一次真实的写测试：用户的最终目的是往这个目录<b>拖课表</b>，
        // 而只读账号在"读"这一步是完全通过的 —— 不测写，就会到人要放文件时才发现问题。
        string writeState;
        bool canWrite = false;
        try
        {
            var probe = Path.Combine(uncPath, "_写入测试_" + Guid.NewGuid().ToString("N")[..6] + ".tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.WriteByte(0x20);
            File.Delete(probe);
            canWrite = true;
            writeState = "可写入（能把课表直接拖进来）";
        }
        catch (UnauthorizedAccessException)
        {
            writeState = "只读（没有写权限，往这里拖课表会被拒绝；要放文件就把共享权限改成「更改」、NTFS 改成「修改」）";
        }
        catch (Exception ex)
        {
            writeState = "写入测试失败：" + ex.Message;
        }

        var head = n > 0
            ? $"连接成功，读到 {n} 个 Excel 文件。"
            : "连接成功，但这个目录里还没有 .xlsx 文件（先把课表放进去再发布）。";

        if (artifacts > 0) head += $"\n另有 {artifacts} 个副本文件，发布时会自动跳过。";

        return (true, head + "\n权限：" + writeState, n, canWrite);
    }

    /// <summary>把 Win32 错误码翻成人话 —— 这几个码占了实际故障的九成。</summary>
    public static string Explain(int code) => code switch
    {
        5 => "拒绝访问：账号密码是对的，但这个账号没有该共享的读取权限。"
           + "共享权限和 NTFS 权限（安全选项卡）两边都要给，取交集。",
        53 => "找不到网络路径：IP/主机名不对、那台电脑关机，或者 445 端口被防火墙拦了。",
        67 => "找不到共享名：共享名写错了。到办公电脑上执行 net share 看真实共享名。",
        86 => "密码不正确。",
        85 => "这个盘符已经被别的映射占用了，换一个盘符再试。",
        1200 => "盘符不可用（可能已被本地磁盘或光驱占用）。",
        1203 => "网络路径不存在，或者当前账号没有访问权限。",
        1204 => "网络提供程序无效：网络服务没起来，或这个路径不是 SMB。",
        1326 => "用户名或密码错误（也可能是账号名拼错）。",
        1219 => @"同一台服务器已经存在另一个账号的连接。先执行 net use \\主机 /delete 断开，或注销后重登，再重试。",
        1311 => "登录失败：办公电脑没开启共享、账号被禁用，或者网络发现/文件共享被关掉了。",
        1909 => "账号被锁定（输错密码次数过多）。",
        _ => $"系统返回错误码 {code}。"
    };
}
