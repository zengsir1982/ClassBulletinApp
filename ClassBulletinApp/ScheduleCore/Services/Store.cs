using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

public static class Store
{
    static string? _appDirOverride;

    /// <summary>
    /// 数据目录。默认 <c>%APPDATA%\ClassBulletinApp</c>；
    /// 可用 <see cref="SetAppDir"/> 改到别处（自动化测试隔离、将来做便携版）。
    /// </summary>
    public static string AppDir =>
        _appDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassBulletinApp");

    /// <summary>覆盖数据目录（传 null 恢复默认）。仅供测试 / 便携版使用。</summary>
    public static void SetAppDir(string? dir)
    {
        _appDirOverride = string.IsNullOrWhiteSpace(dir) ? null : dir;
        // 换目录必须让读缓存立即失效：否则 TTL 内会拿上一个目录的 settings / schedule
        // （--board-check、--sync-check 用多个副本模拟两台机器时曾踩到这个坑）。
        _settingsCacheValid = false;
        _scheduleCacheValid = false;
    }

    static string SchedulePath => Path.Combine(AppDir, "schedule.json");

    /// <summary>
    /// 持久化总开关：游客模式下为 false，所有 Save* 直接跳过（改动只留内存、不落盘），
    /// 退出后自动丢弃；登录（admin）通过 LoginWindow 置为 true 后正常保存。
    /// </summary>
    private static bool _enablePersistence;
    private static AppSettings? _deferredSettings;

    /// <summary>
    /// 持久化总开关：游客模式下为 false，所有 Save* 直接跳过（改动只留内存、不落盘），
    /// 退出后自动丢弃；登录（admin / 受限账号）通过 LoginWindow 置为 true 后正常保存。
    /// <para><b>从 false 切到 true 时立即落盘缓存：</b>开关关闭期间（如登录界面构造期、
    /// 游客模式）的 <see cref="SaveSettings"/> 不再静默丢弃，而是先缓存到
    /// <see cref="_deferredSettings"/>，待开关打开时一次性写出。这样「启动迁移（延迟 0→2）」
    /// 与「登录界面的记住密码 / 自动登录」等改动，绝不会因开关时机问题而停留在内存、
    /// 磁盘仍是旧值（曾导致「上课中变化立即弹窗」的 bug）。</para>
    /// </summary>
    public static bool EnablePersistence
    {
        get => _enablePersistence;
        set
        {
            bool wasOff = !_enablePersistence;
            _enablePersistence = value;
            if (value && wasOff)
            {
                if (_deferredSettings != null)
                {
                    var d = _deferredSettings;
                    _deferredSettings = null;
                    SaveSettings(d);   // 此刻开关已为 true，会真正写盘
                }
                // ★ 登录成功这一瞬间必须让设置缓存失效：启动早期（登录界面构造 / 游客模式）
                //   EnablePersistence 还是 false，那些「要写作业 / 缺交文件」的升级会被跳过。
                //   若此刻不给它们一次重试机会，升级代次会停在旧值 —— 正是
                //   「作业公示标题改了特大、缺交名单标题还是标准」那个坑。
                _settingsCacheValid = false;

                // 立刻补做一次（不等下一次 LoadSettings）：这一步就是「登录后标题自动变特大」的落点
                try
                {
                    var st = LoadSettings();
                    if (UpgradeBoardFontDefaults(st)) SaveSettings(st);
                }
                catch { /* 升级失败绝不能挡住登录 */ }
            }
        }
    }

    /// <summary>
    /// 完全禁止任何写盘（含 <see cref="ForceWrite"/>）。诊断 / 截图模式用：
    /// <c>--ui-check</c>、<c>--shot</c> 跑一遍不该在真实数据目录里留下迁移结果，
    /// 否则用户只是截个图，下次启动身份就变了。
    /// </summary>
    public static bool SuppressWrites { get; set; }

    static string TeacherSchedulePath => Path.Combine(AppDir, "teacher_schedule.json");
    static string NotificationsPath => Path.Combine(AppDir, "notifications.json");
    static string SettingsPath => Path.Combine(AppDir, "settings.json");
    static string OverridesPath => Path.Combine(AppDir, "overrides.json");
    static string UsersPath => Path.Combine(AppDir, "users.json");
    static string HomeworkPath => Path.Combine(AppDir, "homework.json");
    static string MissingPath => Path.Combine(AppDir, "missing.json");
    static string CountdownPath => Path.Combine(AppDir, "countdown.json");
    static string DutyPath => Path.Combine(AppDir, "duty.json");

    static JsonSerializerOptions Opt => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static void Ensure() => Directory.CreateDirectory(AppDir);

    /// <summary>
    /// 通用 JSON 读取：文件不存在或内容为空（0 字节 / 仅空白）时返回 null，
    /// 统一防止「文件存在但被清空/截断」导致 Deserialize 抛 JsonException 而让程序启动崩溃。
    /// </summary>
    static T? ReadJsonOrNull<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonSerializer.Deserialize<T>(text, Opt);
    }

    /// <summary>
    /// 最近一次 Save* 的写盘结果。null = 成功。
    /// <para>★ 静默失败是历史坑（游客登录 / 磁盘只读 / 被占用时 Save 直接 return，
    /// 界面却弹「已保存」）。调用方保存后应检查这里并给出明确提示。</para>
    /// </summary>
    public static string? LastWriteError { get; private set; }

    /// <summary>
    /// 原子写 JSON：先写 .tmp，再 Move 覆盖，最后回读校验。
    /// <para>整文件 File.WriteAllText 一旦中途失败会留下半截 JSON，
    /// 下次启动反序列化直接抛异常；tmp→move 保证读者看到的永远是完整文件。</para>
    /// </summary>
    /// <summary>
    /// 原子写 JSON：tmp → 同目录 Move（覆盖）→ 回读校验。
    /// 二期班级内容同步的本机台账也复用这一份，保证「写盘失败绝不报已保存」。
    /// </summary>
    public static bool WriteJsonAtomic<T>(string path, T value)
    {
        // 诊断 / 截图模式（--ui-check、--shot）必须彻底静默，绝不改数据副本以外的内容
        if (SuppressWrites) { LastWriteError = "诊断模式禁止写盘"; return false; }
        try
        {
            Ensure();
            string text = JsonSerializer.Serialize(value, Opt);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);

            // 写后回读：内容不一致一律当失败，不能让界面以为保存成功
            string back = File.ReadAllText(path);
            if (!string.Equals(back, text, StringComparison.Ordinal))
            {
                LastWriteError = "写盘后回读校验不一致：" + Path.GetFileName(path);
                return false;
            }
            LastWriteError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastWriteError = "写盘失败（" + Path.GetFileName(path) + "）：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 内存补齐作业条目的稳定 Id（确定性、幂等、不写盘）。
    /// <para>★ 两端拿到同一份老数据时，因算法只依赖内容，各自补出的 Id 必然一致 ——
    /// 这是「不需要先跑一趟基线」的关键。磁盘上的老文件保持原样，
    /// 直到用户保存或显式调用 <see cref="MigrateBoardIdsToDisk"/> 才写回。</para>
    /// <para>★ <b>绝对不要拿本机 SelectedClass 当 ClassKey</b>：办公室机与教室机当前显示的班级
    /// 可能不同，会把同一条老数据算出两个 Id，合并后变成重复行。老数据一律保持空 ClassKey
    /// （语义＝「本机老数据，未分片」）；按班分片由同步层的变更单显式赋值。</para>
    /// </summary>
    static bool EnsureIdsInMemory(HomeworkBoard? board)
    {
        if (board == null) return false;
        var items = board.Items ??= new();
        var legacy = board.Missing ??= new();
        if (board.SchemaRev >= 1 && !items.Any(x => string.IsNullOrEmpty(x.Id))
            && !legacy.Any(x => string.IsNullOrEmpty(x.Id))) return false;   // 快路径，不再读设置

        bool changed = BoardIds.Ensure(items, "hw", board.ClassKey,
            x => x.Subject, x => x.Content, (x, id) => x.Id = id);
        // 旧版缺交数组（兼容字段）也要补 Id，否则将来迁移时会重复
        if (BoardIds.Ensure(legacy, "ms", board.ClassKey,
                x => x.Subject, x => x.Students, (x, id) => x.Id = id)) changed = true;
        if (board.SchemaRev < 1) { board.SchemaRev = 1; changed = true; }
        return changed;
    }

    /// <summary>内存补齐缺交条目的稳定 Id（同上，不写盘）。</summary>
    static bool EnsureIdsInMemory(MissingBoard? board)
    {
        if (board == null) return false;
        var items = board.Items ??= new();
        if (board.SchemaRev >= 1 && !items.Any(x => string.IsNullOrEmpty(x.Id))) return false;

        bool changed = BoardIds.Ensure(items, "ms", board.ClassKey,
            x => x.Subject, x => x.Students, (x, id) => x.Id = id);
        if (board.SchemaRev < 1) { board.SchemaRev = 1; changed = true; }
        return changed;
    }

    /// <summary>
    /// 内存补齐值日条目的稳定 Id（同上，不写盘）。
    /// <para>★ 值日的「科目」位用星期、「内容」位用「值日内容 + 分隔符 + 名单」：
    /// 只写值日内容的话，「周一 扫地」和「周二 扫地」会算出同一个 Id。</para>
    /// </summary>
    static bool EnsureIdsInMemory(DutyBoard? board)
    {
        if (board == null) return false;
        var items = board.Items ??= new();
        if (board.SchemaRev >= 1 && !items.Any(x => string.IsNullOrEmpty(x.Id))) return false;

        bool changed = BoardIds.Ensure(items, "duty", board.ClassKey,
            x => x.Day, x => (x.Duty ?? "") + "\u001F" + (x.Names ?? ""), (x, id) => x.Id = id);
        if (board.SchemaRev < 1) { board.SchemaRev = 1; changed = true; }
        return changed;
    }

    /// <summary>
    /// 显式把带稳定 Id 的作业 / 缺交数据写回磁盘（迁移前自动备份、只做一次）。
    /// 供诊断 --board-check 与升级流程调用；正常启动不主动改用户文件。
    /// </summary>
    public static string MigrateBoardIdsToDisk()
    {
        if (!EnablePersistence) return "FAIL 游客模式不写盘，无法迁移";
        if (SuppressWrites) return "FAIL 诊断模式禁止写盘，无法迁移";
        try
        {
            var s = LoadSettings();
            if (s.BoardMigrationRev >= 1) return "OK 已迁移过（BoardMigrationRev=1），幂等跳过";

            string backup = BackupNow("作业/缺交稳定 Id 迁移前自动存档");
            var hw = ReadJsonOrNull<HomeworkBoard>(HomeworkPath) ?? new HomeworkBoard();
            var mb = ReadJsonOrNull<MissingBoard>(MissingPath) ?? new MissingBoard();
            EnsureIdsInMemory(hw);
            EnsureIdsInMemory(mb);

            if (!WriteJsonAtomic(HomeworkPath, hw)) return "FAIL " + LastWriteError;
            if (!WriteJsonAtomic(MissingPath, mb)) return "FAIL " + LastWriteError;

            s.BoardMigrationRev = 1;
            SaveSettings(s);
            return $"OK 已备份到 {(string.IsNullOrEmpty(backup) ? "(无文件)" : backup)}，" +
                   $"作业 {hw.Items.Count} 条 / 缺交 {mb.Items.Count} 条已带 Id";
        }
        catch (Exception ex) { return "FAIL " + ex.Message; }
    }

    public static void SaveSchedule(ScheduleFile sf)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(SchedulePath, JsonSerializer.Serialize(sf, Opt));
        _scheduleCacheValid = false;   // 下一次 LoadSchedule 重新读，绝不给出写之前的旧对象
    }

    // 课表 / 设置读盘缓存：以「文件最后写入时间」为签名，文件一变（本进程写、别的进程写、
    // 同步包覆盖）签名就变，自动失效 —— 不需要手工 Clear，也不会有读到旧值的风险。
    // ★ 为什么要加：调一次课要读 3~4 遍 schedule.json（全校总课表，几百 KB~几 MB）：
    //   ApplyDrag 里读一次、Rebuild 里读一次、Upsert 比对再读一次…… 一体机上就是
    //   松手后明显「卡一下」。加了缓存后同一帧内只真正读一次。
    static ScheduleFile? _scheduleCache;
    static DateTime _scheduleCacheStamp = DateTime.MinValue;
    static DateTime _scheduleCacheAt;
    static bool _scheduleCacheValid;

    static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    public static ScheduleFile? LoadSchedule()
    {
        var path = SchedulePath;
        var stamp = StampOf(path);
        // 3 秒 TTL：只用来吞掉"同一次操作里连续读好几遍"，过期就重读 ——
        // 万一别处原地改了这个对象又没保存，最多 3 秒后也会回到磁盘的真实内容。
        if (_scheduleCacheValid && stamp == _scheduleCacheStamp
            && (DateTime.UtcNow - _scheduleCacheAt).TotalSeconds < 3)
            return _scheduleCache;

        var sf = ReadJsonOrNull<ScheduleFile>(path);
        _scheduleCache = sf;
        _scheduleCacheStamp = stamp;
        _scheduleCacheAt = DateTime.UtcNow;
        _scheduleCacheValid = true;
        return sf;
    }

    public static void SaveTeacherSchedule(ScheduleFile sf)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(TeacherSchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadTeacherSchedule()
        => ReadJsonOrNull<ScheduleFile>(TeacherSchedulePath);

    public static void SaveHomework(HomeworkBoard board)
    {
        if (!EnablePersistence) { LastWriteError = "游客模式：改动没有保存"; return; }
        EnsureIdsInMemory(board);   // 保存时兜底：绝不让无 Id 的新条目落盘
        WriteJsonAtomic(HomeworkPath, board);
    }

    /// <summary>读作业公示。老数据无 Id 时在内存补齐（确定性），不改磁盘文件。</summary>
    public static HomeworkBoard? LoadHomework()
    {
        var board = ReadJsonOrNull<HomeworkBoard>(HomeworkPath);
        EnsureIdsInMemory(board);
        return board;
    }

    public static void SaveMissingBoard(MissingBoard board)
    {
        if (!EnablePersistence) { LastWriteError = "游客模式：改动没有保存"; return; }
        EnsureIdsInMemory(board);
        WriteJsonAtomic(MissingPath, board);
    }

    public static void SaveCountdown(CountdownBoard board)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(CountdownPath, JsonSerializer.Serialize(board, Opt));
    }

    /// <summary>读取倒计时便签（没有文件时返回一个空白板，由界面提示「双击添加」）。</summary>
    public static CountdownBoard LoadCountdown()
        => ReadJsonOrNull<CountdownBoard>(CountdownPath) ?? new CountdownBoard();

    /// <summary>
    /// 保存值日生名单。
    /// <para>★ 与作业 / 缺交完全一致：补齐稳定 Id → 原子写（tmp→Move→回读校验）→ 失败写 LastWriteError。
    /// 老实现是裸 <c>File.WriteAllText</c>：写失败不留痕、界面照样说「已保存」，
    /// 而值日现在要参与变更单同步（远端按 Id 指条目），一个没写进去的 Id 就会让「替换」失败。</para>
    /// </summary>
    public static void SaveDuty(DutyBoard board)
    {
        if (!EnablePersistence) { LastWriteError = "游客模式：改动没有保存"; return; }
        EnsureIdsInMemory(board);
        WriteJsonAtomic(DutyPath, board);
    }

    /// <summary>读取值日生名单（没有文件时返回一个空白板）。老数据无 Id 时在内存补齐（确定性），不改磁盘文件。</summary>
    public static DutyBoard LoadDuty()
    {
        var board = ReadJsonOrNull<DutyBoard>(DutyPath) ?? new DutyBoard();
        EnsureIdsInMemory(board);
        return board;
    }

    /// <summary>
    /// 读取「缺交作业名单」独立便签数据。首次调用（还没有 missing.json）时，
    /// 自动把旧版 homework.json 里的 Missing 列表迁移过来，迁移后清空旧字段避免两处重复。
    /// </summary>
    public static MissingBoard LoadMissingBoard()
    {
        if (File.Exists(MissingPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<MissingBoard>(File.ReadAllText(MissingPath), Opt);
                if (loaded != null) { EnsureIdsInMemory(loaded); return loaded; }
            }
            catch { }
            var blank = new MissingBoard();
            EnsureIdsInMemory(blank);
            return blank;
        }

        var board = new MissingBoard();
        try
        {
            var hw = LoadHomework();
            if (hw?.Missing is { Count: > 0 })
            {
                board.Items = hw.Missing;   // LoadHomework 已给旧数组补好确定性 Id
                board.UpdatedAt = hw.UpdatedAt;
                hw.Missing = new();
                if (EnablePersistence) WriteJsonAtomic(HomeworkPath, hw);
            }
        }
        catch { }
        EnsureIdsInMemory(board);
        SaveMissingBoard(board);
        return board;
    }

    public static void SaveNotifications(List<Notification> list)
    {
        if (!EnablePersistence) { LastWriteError = "游客模式：改动没有保存"; return; }
        WriteJsonAtomic(NotificationsPath, list);
    }

    /// <summary>
    /// 业务上仍然存在的通知（<b>已过滤墓碑</b>）。过期项<b>不过滤</b> —— 过期只在便签 / 跑马灯
    /// 这类显示层用 <see cref="Notification.IsLive"/> 判断，编辑窗要能看到并恢复它们。
    /// <para>★ 要写回的场景（编辑窗、消息链落地）必须用 <see cref="LoadNotificationsAll"/>，
    /// 否则拿过滤后的列表保存会把墓碑整批丢掉，等于又变成物理删除。</para>
    /// </summary>
    public static List<Notification> LoadNotifications()
        => (ReadJsonOrNull<List<Notification>>(NotificationsPath) ?? new())
           .Where(x => !x.Deleted).ToList();

    /// <summary>全量读取，<b>含墓碑</b>。只给写回方与诊断用。</summary>
    public static List<Notification> LoadNotificationsAll()
        => ReadJsonOrNull<List<Notification>>(NotificationsPath) ?? new();

    public static void SaveSettings(AppSettings s)
    {
        if (s == null) return;
        if (!EnablePersistence)
        {
            // 开关关闭期间（登录界面构造期 / 游客模式）：先缓存，待开关打开时一次性落盘，
            // 不再静默丢弃——否则「延迟 0→2」等迁移结果永远停在内存、磁盘仍是旧值。
            _deferredSettings = s;
            return;
        }
        _deferredSettings = null;
        Ensure();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Opt));
        _settingsCache = s;                       // 刚写进去的就是当前值，直接当缓存
        _settingsCacheStamp = StampOf(SettingsPath);
        _settingsCacheValid = true;
    }

    static AppSettings? _settingsCache;
    static DateTime _settingsCacheStamp;
    static DateTime _settingsCacheAt;
    static bool _settingsCacheValid;

    public static AppSettings LoadSettings()
    {
        var path = SettingsPath;
        var stamp = StampOf(path);
        bool cached = _settingsCacheValid && _settingsCache != null
                      && stamp == _settingsCacheStamp
                      && (DateTime.UtcNow - _settingsCacheAt).TotalSeconds < 2;
        var s = cached ? _settingsCache! : (ReadJsonOrNull<AppSettings>(path) ?? new());

        if (!cached)
        {
            // 旧版本的设置界面允许存 0~1440，而运行期只认 5~720 —— 于是"设 3 实际按 5 跑"，
            // 设置窗显示的还不是真实生效值。自愈成合法区间并落盘，两处就永远一致了。
            if (NormalizeUpdateInterval(s)) SaveSettings(s);
            // 「课表变化提醒」的新默认值（全屏大字 / 15 秒 / 语音关）也要落到老配置上，否则改了默认
            // 只有新装的机器生效，老机器还按 5 秒小窗跑。
            if (UpgradeChangeReminderDefaults(s)) SaveSettings(s);
            // 「常驻显示透明度」改默认为不透明：老配置里存着 0.55，只改模型默认值对老机器无效。
            if (UpgradeOverlayOpacityDefaults(s)) SaveSettings(s);
            // 通知条默认「置顶常驻 + 鼠标穿透 + 悬停变透明」：老配置里 ClickThrough/HoverFade 都是 false。
            if (UpgradeNotificationOverlayDefaults(s)) SaveSettings(s);
            // 「作业公示 / 缺交名单」默认字号改为特大：老数据里存着标准档(0)，只改模型默认值对老机器无效。
            if (UpgradeBoardFontDefaults(s)) SaveSettings(s);
            // 「全局玻璃底色」：出厂默认关闭，只把越界的通透度规范回合法区间。
            if (UpgradeGlassDefaults(s)) SaveSettings(s);
            // r74：悬浮窗与普通窗口通透度拆成两个字段，老配置先把老值复制过来，观感不变。
            if (UpgradeGlassSplit(s)) SaveSettings(s);
            // 通知条「开机默认显示 + 上课时间自动隐藏」：老配置里 AutoShowNotification /
            // AutoHideDuringClass 都明明白白存着 false，只改模型默认值对已装机器一个字节都不生效。
            // ★ 必须排在 UpgradeNotificationOverlayDefaults 之后：那个方法会把上课自动隐藏压回 false。
            if (UpgradeNotificationShowDefaults(s)) SaveSettings(s);
            _settingsCache = s;
            _settingsCacheStamp = StampOf(path);
            _settingsCacheAt = DateTime.UtcNow;
            _settingsCacheValid = true;
        }
        return s;
    }

    /// <summary>
    /// 把「课表变化提醒」升到新默认值：<b>全屏大字 / 停留 15 秒 / 语音开</b>，
    /// 并把「上课中变化延后」的 0 分钟修正为 2 分钟。
    /// <para><b>为什么是强制写而不判断"是否仍是旧默认值"：</b>这几项在旧版里本来就有默认值
    /// （小窗 / 5 秒 / 语音开），用户可能已经手动调过（比如调成 10 秒），
    /// 按"旧默认值才升"的写法会把这些机器漏掉，导致"改了默认值却看不出变化"。</para>
    /// <para><b>代次（<see cref="AppSettings.ReminderDefaultsRev"/>）：</b>1 = 全屏 / 15 秒 / 语音关；
    /// 2 = 再把语音改回开；3 = 延迟 0 分钟（上课中也立即弹）改回 2 分钟。
    /// 代次只单向递增，保证每次调整只生效一次 —— 用户升级后再改回去，下次启动不会被打回。</para>
    /// </summary>
    /// <summary>
    /// 「常驻显示透明度」默认值升级：旧版悬浮课表与通知条默认 0.55 半透明，新默认改为完全不透明。
    /// <para>只把恰好还是旧默认值 0.55、或非法值（小于等于 0）的改成 1.0；用户手动调过的
    /// 其它数值一律保留。代次保证只跑一次，之后用户再调低也不会在下次启动被打回。</para>
    /// </summary>
    public static bool UpgradeOverlayOpacityDefaults(AppSettings s)
    {
        if (s == null) return false;
        if (s.OverlayOpacityDefaultsRev >= 1) return false;
        s.OverlayOpacityDefaultsRev = 1;
        foreach (var l in new[] { s.ScheduleLayout, s.NotificationLayout })
        {
            if (l == null) continue;
            if (l.OverlayOpacity <= 0 || System.Math.Abs(l.OverlayOpacity - 0.55) < 0.001)
                l.OverlayOpacity = 1.0;
        }
        return true;
    }

    /// <summary>
    /// 「通知条」默认行为一次性升级：<b>置顶常驻 + 鼠标穿透 + 鼠标移上去变透明</b>。
    ///
    /// <para><b>为什么老机器必须显式改：</b>settings.json 里明明白白存着
    /// <c>"ClickThrough": false</c> / <c>"HoverFade": false</c>，只改模型默认值对已装机器
    /// 一个字节都不生效（与「常驻显示透明度 0.55」是同一个坑）。</para>
    ///
    /// <para><b>为什么敢强制覆盖：</b>通知条是纯展示面板，穿透 + 常驻最前正是它唯一的合理形态；
    /// 而这三个开关在旧版里<b>没有一个是默认开的</b>，所以「已有人手动调过」的概率极低 ——
    /// 真调过的人也可以在「布局设置 → 通知悬浮窗」里改回去，代次只跑一次不会反复打回。</para>
    /// </summary>
    public static bool UpgradeNotificationOverlayDefaults(AppSettings s)
    {
        if (s == null) return false;
        if (s.NotificationOverlayRev >= 1) return false;
        s.NotificationOverlayRev = 1;

        var l = s.NotificationLayout ??= new PanelLayout();
        l.TopMost = true;        // 常驻最前，全班随时看得见
        l.ClickThrough = true;   // 纯展示：点击落到底层，课件照常点
        l.HoverFade = true;      // 鼠标移过去自动让开
        // 穿透与自动靠边隐藏互斥：升级后必须把自动隐藏关掉，否则穿透立刻被 ApplyCurrentLayout 判为失效
        l.AutoHide = false;
        l.AutoHideDuringClass = false;
        return true;
    }

    /// <summary>
    /// 通知条「开机默认显示 + 上课时间自动隐藏」一次性升级。
    ///
    /// <para><b>为什么必须显式改：</b>老 settings.json 里存着 <c>"AutoShowNotification": false</c>
    /// 与 <c>"AutoHideDuringClass": false</c>，只改 <see cref="AppSettings"/> 字段初始值对已装机器无效
    /// （与「常驻显示透明度 0.55」是同一个坑）。</para>
    ///
    /// <para><b>为什么不与「穿透」互斥：</b>「上课时间自动隐藏」只是上课期间把条子滑到屏幕边沿，
    /// 「鼠标穿透」只决定鼠标点击落到底层窗口 —— 两者互不干涉，可以同时成立。
    /// 真正与穿透互斥的是「自动靠边隐藏」（隐藏后要留一条可点的细边，穿透态点不到），
    /// 所以这里只把 <c>AutoHide</c> 压掉。</para>
    ///
    /// <para><b>代次：</b>只跑一次，用户之后手动关掉「上课隐藏」或「开机显示」不会被打回。
    /// 启动早期 <see cref="EnablePersistence"/> 还是 false、诊断模式 <see cref="SuppressWrites"/> 为真时
    /// 直接返回且不记 Rev，避免「假装升级完成、以后永远不重试」。</para>
    /// </summary>
    public static bool UpgradeNotificationShowDefaults(AppSettings s)
    {
        if (s == null) return false;
        if (s.NotificationShowDefaultsRev >= 1) return false;
        if (!EnablePersistence) return false;
        if (SuppressWrites) return false;
        s.NotificationShowDefaultsRev = 1;

        s.AutoShowNotification = true;
        var l = s.NotificationLayout ??= new PanelLayout();
        l.TopMost = true;
        l.ClickThrough = true;
        l.HoverFade = true;
        l.AutoHide = false;             // 唯一与穿透互斥的一项
        l.AutoHideDuringClass = true;   // 上课期间自动缩到边沿，下课恢复
        return true;
    }

    /// <summary>
    /// 「全局玻璃底色」默认值升级：出厂关闭，只需把非法 / 越界的通透度规范回 0~65。
    /// <para>★ 跟 <see cref="UpgradeBoardFontDefaults"/> 同一个教训：本方法由
    /// <see cref="LoadSettings"/> 调用，启动早期 <see cref="EnablePersistence"/> 还是 false，
    /// 诊断 / 截图模式又设了 <see cref="SuppressWrites"/> —— 那时写盘会静默失败，
    /// 但 Rev 若照记就成了「假装升级完成、以后永远不重试」。所以两种情况下直接返回、绝不记 Rev。</para>
    /// <para>★ 默认值靠 <see cref="AppSettings"/> 的字段初始化（而不是只靠这次升级）保证：
    /// 即使 SuppressWrites=true 的诊断模式读不到落盘结果，玻璃默认仍是「关闭 / 45%」，
    /// 不会让 --shot 截图意外变成半透明。</para>
    /// </summary>
    public static bool UpgradeGlassDefaults(AppSettings s)
    {
        if (s == null) return false;
        if (s.GlassDefaultsRev >= 1) return false;
        if (!EnablePersistence) return false;
        if (SuppressWrites) return false;
        s.GlassDefaultsRev = 1;
        if (!double.IsFinite(s.GlassOpacity) || s.GlassOpacity < 0 || s.GlassOpacity > 65)
            s.GlassOpacity = 45;
        return true;
    }

    /// <summary>
    /// r74「悬浮窗 / 普通窗口通透度拆分」一次性迁移：把老的单一 <c>GlassOpacity</c> 复制给
    /// 新字段 <c>GlassChromeOpacity</c>。
    /// <para>★ 为什么必须迁移而不是只靠字段默认值：老用户可能把通透度调到 60，只改默认值会让
    /// 主窗突然回到 45 —— 屏幕上一眼就能看出变化。复制一次，升级前后观感完全一致；
    /// 之后两组各调各的，代次保证不会在下一次启动被打回。</para>
    /// <para>★ 与 <see cref="UpgradeGlassDefaults"/> 同一套守则：本方法由
    /// <see cref="LoadSettings"/> 在启动早期调用，那时 <see cref="EnablePersistence"/> 还是 false，
    /// 诊断 / 截图模式又设了 <see cref="SuppressWrites"/> —— 写盘会静默失败，
    /// 所以两种情况直接返回、绝不记 Rev（否则成了「假装升级完成、以后永远不重试」）。</para>
    /// </summary>
    public static bool UpgradeGlassSplit(AppSettings s)
    {
        if (s == null) return false;
        if (s.GlassSplitRev >= 1) return false;
        if (!EnablePersistence) return false;
        if (SuppressWrites) return false;
        MigrateGlassSplit(s);
        return true;
    }

    /// <summary>
    /// <see cref="UpgradeGlassSplit"/> 的纯逻辑部分（<b>没有写盘守卫</b>，诊断可以单独断言）。
    /// <para>代次 ≥ 1 直接返回，保证用户之后两组各调各的值不会在下次启动被打回。</para>
    /// </summary>
    public static void MigrateGlassSplit(AppSettings s)
    {
        if (s == null || s.GlassSplitRev >= 1) return;
        s.GlassSplitRev = 1;
        // 老值可能是非法数（历史脏数据）—— 规范后再复制，绝不把 NaN 写成通透度
        s.GlassChromeOpacity = double.IsFinite(s.GlassOpacity) ? Math.Clamp(s.GlassOpacity, 0, 65) : 45;
    }

    /// <summary>
    /// 「作业公示 / 缺交名单」默认字号一次性升级到「特大」（教室后排也看得清）。
    /// <para>★ 只把<b>仍是标准档(0)</b>的标题 / 科目 / 内容 / 名单改成特大档(3)；
    /// 用户已经明确选过别的档位一律保留。代次 <see cref="AppSettings.BoardFontDefaultsRev"/> 保证只跑一次：
    /// 升级后再手动调回「标准」，下次启动不会被打回。</para>
    /// <para>★ 不能只改模型默认值：老 homework.json / missing.json 里明明白白存着
    /// "SizeLevel": 0，反序列化会把构造函数的默认特大覆盖回标准 —— 这正是"改了默认却看不出变化"的老坑。</para>
    /// <para>★ <b>代次 2 修的是什么：</b>本方法由 <see cref="LoadSettings"/> 调用，而启动早期
    /// （登录界面构造 / 游客模式）<see cref="EnablePersistence"/> 还是 false —— 那时
    /// <c>SaveHomework</c> / <c>SaveMissingBoard</c> 只是静默返回，文件一个字节都没改，
    /// 但这里照样把 Rev 记成 1。结果：Rev 说自己做完了，磁盘上标题还是标准档，永远不再重试。
    /// 所以现在①没开写盘就直接返回、绝不记 Rev；②Rev 提到 2，把这批机器补做一次。</para>
    /// </summary>
    public static bool UpgradeBoardFontDefaults(AppSettings s)
    {
        if (s == null || s.BoardFontDefaultsRev >= 2) return false;

        // 写不了盘就别动代次：宁可下次启动再试，也不能让 Rev 撒谎
        if (!EnablePersistence) return false;
        if (SuppressWrites) return false;

        static bool Bump(TextStyle? st)
        {
            if (st == null || st.SizeLevel != 0) return false;
            st.SizeLevel = HwTextStyle.ExtraLargeLevel;
            return true;
        }

        try
        {
            var hw = ReadJsonOrNull<HomeworkBoard>(HomeworkPath);
            if (hw != null)
            {
                // 代次 2 起：标题也要升（老机器上条目早就是特大了，只有标题还卡在标准档）
                bool changed = Bump(hw.TitleStyle);
                if (s.BoardFontDefaultsRev < 1)
                {
                    foreach (var it in hw.Items ?? new())
                    {
                        if (it == null) continue;
                        changed |= Bump(it.ContentStyle);
                        changed |= Bump(it.SubjectStyle);
                    }
                }
                if (changed) SaveHomework(hw);
            }

            var ms = ReadJsonOrNull<MissingBoard>(MissingPath);
            if (ms != null)
            {
                bool changed = Bump(ms.TitleStyle);
                if (s.BoardFontDefaultsRev < 1)
                {
                    foreach (var it in ms.Items ?? new())
                    {
                        if (it == null) continue;
                        changed |= Bump(it.ContentStyle);
                        changed |= Bump(it.SubjectStyle);
                    }
                }
                if (changed) SaveMissingBoard(ms);
            }
        }
        catch
        {
            // 升级失败不能挡住启动，也不能把 Rev 记成已完成：下次启动再试一次。
            return false;
        }

        s.BoardFontDefaultsRev = 2;
        return true;
    }

    public static bool UpgradeChangeReminderDefaults(AppSettings s)
    {
        if (s == null) return false;
        if (s.ReminderDefaultsRev >= 3) return false;

        var r = s.Reminder ??= new ReminderSettings();

        // Rev 1→2：全屏大字 / 停留 15 秒 / 语音开
        if (s.ReminderDefaultsRev < 2)
        {
            r.ChangeFullScreen = true;   // 教室里后排也看得清
            r.ChangeSeconds = 15;        // 15 秒够读完，又不至于一直挂在屏幕上
            r.ChangeVoice = true;        // 语音播报默认开
        }

        // Rev 2→3：把「延迟 0 分钟」改回 2 分钟。
        // 0 = 上课时间也立即弹窗，正是用户反馈"更新课表时课堂上被打断"的配置；
        // 只改 0，用户明确设了 5 / 10 等其它值不动。
        if (r.ChangeDelayMinutes <= 0) r.ChangeDelayMinutes = 2;

        s.ReminderDefaultsRev = 3;
        return true;
    }

    /// <summary>
    /// 把检查间隔夹到 <see cref="AppSettings.MinUpdateIntervalMinutes"/>~<see cref="AppSettings.MaxUpdateIntervalMinutes"/>。
    /// 返回是否发生了改动（幂等；游客模式下不落盘，下次启动重算结果一致）。
    /// </summary>
    public static bool NormalizeUpdateInterval(AppSettings s)
    {
        if (s == null) return false;
        int v = Math.Clamp(s.UpdateIntervalMinutes,
                           AppSettings.MinUpdateIntervalMinutes,
                           AppSettings.MaxUpdateIntervalMinutes);
        if (v == s.UpdateIntervalMinutes) return false;
        s.UpdateIntervalMinutes = v;
        return true;
    }

    /// <summary>
    /// 「本机手动调课」刚刚写盘。<b>参数 = 受影响的格子键</b>（<see cref="LessonOverride.CellKey"/>，
    /// 即「班级|星期|节次」，同一格不同周算同一格）。
    ///
    /// <para><b>为什么要有这个事件：</b>「课表有变化」监视器只比对结果、不关心来源，
    /// 所以老师自己拖一节课也会被它当成"课表被改了"，紧接着弹小窗、播报语音、状态栏挂红字 ——
    /// 自己刚动完手就被告知"课表有变化"，只会让人以为出了错。</para>
    ///
    /// <para><b>为什么放在 Store 而不是各个调用点：</b>手动入口有六处（拖课、撤销、编辑窗、
    /// 周课表拖动 / 清格 / 清全部），逐个挂标记早晚会漏掉将来新增的一个；而所有手写入最终都
    /// 经过 <see cref="SaveOverrides"/>，在这里判一次就全都覆盖了。</para>
    ///
    /// <para><b>只在"变化的记录全是 manual"时才触发：</b>差异里只要牵涉 <c>temp</c>（导入临时表）
    /// 或 <c>remote</c>（同步包），就不发这个事件，监视器照旧提醒 —— 那些变化确实该告诉老师。</para>
    /// </summary>
    public static event Action<IReadOnlyCollection<string>>? LocalManualEdit;

    /// <summary>
    /// 覆盖表刚被<b>非本机手动</b>的来源改过（<c>temp</c> / <c>remote</c> / <c>basePatch</c>），
    /// 参数 = 受影响的格子键。
    /// <para><b>为什么要发这个：</b>同一格如果老师 30 秒内先手动调、随后又被同步包覆盖，
    /// 只靠 <see cref="LocalManualEdit"/> 记下的"手动格"会把后面这次真变化一起吞掉。
    /// 监视器收到本事件后就把这几格的"手动"标记清掉，保证外部改动一定提醒。</para>
    /// </summary>
    public static event Action<IReadOnlyCollection<string>>? ExternalEdit;

    public static void SaveOverrides(List<LessonOverride> list)
    {
        if (!EnablePersistence) return;
        Ensure();

        // 必须在写盘之前取样：写完之后再读就只剩新内容，比对不出差异了。
        var (manualCells, externalCells) = DetectChange(list);

        File.WriteAllText(OverridesPath, JsonSerializer.Serialize(list, Opt));

        try
        {
            if (manualCells.Count > 0) LocalManualEdit?.Invoke(manualCells);
            if (externalCells.Count > 0) ExternalEdit?.Invoke(externalCells);
        }
        catch { /* 通知失败不影响保存 */ }
    }

    /// <summary>
    /// 比对"盘上的旧覆盖表"和"即将写入的新覆盖表"。
    /// <para>返回 <c>(Manual, External)</c>：整批差异全部由 manual 记录构成时给 Manual（监视器据此不提醒）；
    /// 只要掺进一条非 manual 记录，就整批算 External（宁可多提醒一次，也不能把同步包改动吞掉）。</para>
    /// <para>纯只读：用 <see cref="ReadJsonOrNull{T}"/> 直接读文件，不走 <see cref="LoadOverrides"/>，
    /// 否则会顺带触发旧数据迁移并二次写盘。</para>
    /// </summary>
    static (List<string> Manual, List<string> External) DetectChange(List<LessonOverride> after)
    {
        var none = (new List<string>(), new List<string>());
        try
        {
            var before = ReadJsonOrNull<List<LessonOverride>>(OverridesPath);
            if (before == null) return none;   // 首次写盘：没有"旧状态"，谈不上变化

            var oldMap = new Dictionary<string, LessonOverride>(StringComparer.Ordinal);
            foreach (var o in before)
                if (o != null && !string.IsNullOrEmpty(o.Key)) oldMap[o.Key] = o;

            var newMap = new Dictionary<string, LessonOverride>(StringComparer.Ordinal);
            foreach (var o in after)
                if (o != null && !string.IsNullOrEmpty(o.Key)) newMap[o.Key] = o;

            // 1) 找出内容真的变了的记录（新增 / 删除 / 字段变化）。
            //    不把 UpdatedAt 算进签名：同一条记录被原样重写一遍不算"课表变了"。
            var changed = new List<string>();
            foreach (var kv in newMap)
            {
                if (!oldMap.TryGetValue(kv.Key, out var o) || Signature(o) != Signature(kv.Value))
                    changed.Add(kv.Key);
            }
            foreach (var kv in oldMap)
                if (!newMap.ContainsKey(kv.Key)) changed.Add(kv.Key);

            if (changed.Count == 0) return none;

            // 2) 逐条看来源：全是手动才算"老师自己调的"
            var cells = new List<string>();
            foreach (var k in changed)
            {
                LessonOverride? rec = newMap.TryGetValue(k, out var n) ? n
                                     : oldMap.TryGetValue(k, out var b) ? b : null;
                if (rec == null) continue;

                bool oldManual = !oldMap.TryGetValue(k, out var bo) || SourceOf(bo) == "manual";
                bool newManual = !newMap.TryGetValue(k, out var bn) || SourceOf(bn) == "manual";
                if (!oldManual || !newManual)
                {
                    // 掺了外部来源 → 整批当作外部变化（清掉手动标记，照旧提醒）
                    var ext = new List<string>();
                    foreach (var kk in changed)
                    {
                        LessonOverride? r2 = newMap.TryGetValue(kk, out var n2) ? n2
                                           : oldMap.TryGetValue(kk, out var b2) ? b2 : null;
                        if (r2 != null && !ext.Contains(r2.CellKey)) ext.Add(r2.CellKey);
                    }
                    return (new List<string>(), ext);
                }

                if (!cells.Contains(rec.CellKey)) cells.Add(rec.CellKey);
            }
            return (cells, new List<string>());
        }
        catch
        {
            return none;   // 判不出来就别抑制，宁可多提醒一次
        }
    }

    static string SourceOf(LessonOverride o)
        => string.IsNullOrWhiteSpace(o.Source) ? "manual" : o.Source.Trim().ToLowerInvariant();

    /// <summary>记录内容签名：只含影响"显示成什么"的字段（不含 UpdatedAt）。</summary>
    static string Signature(LessonOverride o)
        => string.Join('\u0001',
            o.Subject ?? "", o.Teacher ?? "", o.Location ?? "", o.Type ?? "",
            o.IsEmpty ? "1" : "0", o.Suspended ? "1" : "0",
            o.ValidFrom?.ToString("yyyy-MM-dd") ?? "", o.ValidTo?.ToString("yyyy-MM-dd") ?? "",
            SourceOf(o));

    public static List<LessonOverride> LoadOverrides()
    {
        var list = ReadJsonOrNull<List<LessonOverride>>(OverridesPath) ?? new();
        if (MigrateOverrideRanges(list))
        {
            SaveOverrides(list);   // 旧数据自动归周并落盘（幂等）

            // 记下"发生过迁移"这件事，供升级提示里说明；只写第一次
            try
            {
                var s = LoadSettings();
                if (string.IsNullOrEmpty(s.OverridesMigratedAt))
                {
                    s.OverridesMigratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    SaveSettings(s);
                }
            }
            catch { /* 记不上不影响迁移本身 */ }
        }
        return list;
    }

    /// <summary>
    /// 旧数据迁移：r8 之前的覆盖记录没有生效区间，按 <see cref="LessonOverride.UpdatedAt"/> 归入其所在周，
    /// 与设计 §4.8 的推荐方案一致（绝不迁成"长期有效"）。返回是否发生了改动。
    ///
    /// 说明：迁移是纯本地、幂等的；没有 UpdatedAt 的记录归入当前周。
    /// 游客模式下 <see cref="SaveOverrides"/> 不落盘，下次启动会重新计算，结果一致。
    /// </summary>
    public static bool MigrateOverrideRanges(List<LessonOverride> list)
    {
        bool changed = false;
        foreach (var o in list)
        {
            if (o == null) continue;
            if (o.HasRange)
            {
                if (o.ValidTo!.Value < o.ValidFrom!.Value)   // 脏数据：区间反了就校正
                {
                    (o.ValidFrom, o.ValidTo) = (o.ValidTo, o.ValidFrom);
                    changed = true;
                }
                continue;
            }

            var anchor = o.UpdatedAt == default ? DateTime.Today : o.UpdatedAt;
            var (start, end) = WeekHelper.WeekWindow(anchor);
            o.ValidFrom = DateOnly.FromDateTime(start);
            o.ValidTo = DateOnly.FromDateTime(end);
            changed = true;
        }
        return changed;
    }

    // ================== 用户表 ==================

    /// <summary>本机账号的固定用户名（电教管理员 / 学生角色，教室机开机自动登录用）。</summary>
    public const string LocalUserName = "local";

    public static void SaveUsers(UserBook book)
    {
        // ★ 诊断 / 截图模式（--ui-check、--shot）必须彻底静默：
        //   否则只是在教室机上截个图，就把真实用户表改掉了。
        if (SuppressWrites) return;
        if (!EnablePersistence) return;
        Ensure();
        // 补齐缺失的角色权限并提到 Schema 2：users.json 里能直接看到这几项，
        // 出问题时管理员能手改（权限体系本来就是"界面不给入口"，不是加密）。
        book.EnsurePolicyDefaults();
        book.UpdatedAt = DateTime.Now;
        File.WriteAllText(UsersPath, JsonSerializer.Serialize(book, Opt));
    }

    /// <summary>
    /// 读取用户表。文件不存在时<b>自动从旧版单账号迁移</b>：
    /// <list type="bullet">
    /// <item>原 <see cref="AppSettings.Username"/> / <see cref="AppSettings.Password"/> 迁成<b>管理员</b>
    /// （密码明文转哈希）—— 不然升级后老师发现改不了任何设置；</item>
    /// <item>另外建一个 <c>local</c>（电教管理员 / 学生）账号，密码 <c>000</c>，
    /// 并把自动登录切到它 —— 教室机开机只能是"能控显示、不能改数据"的身份。</item>
    /// </list>
    /// <para>★ 自动登录永远不指向管理员：否则谁坐在电脑前谁就是管理员，权限等于零。</para>
    /// </summary>
    public static UserBook LoadUsers()
    {
        var book = ReadJsonOrNull<UserBook>(UsersPath);
        if (book != null && book.Users.Count > 0)
        {
            // ★ 升级自愈：老 users.json（Schema &lt; 4）里没有「科任老师」这一条，
            //   而 PolicyFor 在 Schema&gt;=2 时"缺 = 全关"，于是管理员把某个账号设成科任老师后
            //   他会连作业都改不了，界面上还看不出哪里错了。读取时就补齐（只补缺、不覆盖），
            //   有改动才落盘，避免每次启动都重写用户表。
            if (book.EnsurePolicyDefaults())
                ForceWrite(UsersPath, JsonSerializer.Serialize(book, Opt));
            return book;
        }

        book = MigrateFromSingleAccount();

        // 迁移出来的新表直接带上角色权限的出厂默认，省得用户首次打开用户管理时看不到这几项
        book.EnsurePolicyDefaults();

        // ★ 这里必须<b>强制落盘</b>，不能走 SaveUsers 的 EnablePersistence 开关：
        //   迁移时还没走到"登录成功、打开持久化"那一步，如果跳过写盘，
        //   users.json 永远不存在 → 每次启动都重新迁移 → 密码盐每次不同 → 自动登录必定失败。
        ForceWrite(UsersPath, JsonSerializer.Serialize(book, Opt));
        return book;
    }

    static UserBook MigrateFromSingleAccount()
    {
        var book = new UserBook();
        var s = LoadSettings();

        string adminName = string.IsNullOrWhiteSpace(s.Username) ? "admin" : s.Username.Trim();
        string adminPwd = string.IsNullOrEmpty(s.Password) ? "000" : s.Password;

        var admin = new AppUser
        {
            Username = adminName,
            DisplayName = "管理员",
            Role = UserRole.Admin
        };
        // 用户名本身就是班级名时（很多教室机这么设），顺带让它能改本班课表
        if (!string.IsNullOrWhiteSpace(s.SelectedClass) &&
            string.Equals(s.SelectedClass, adminName, StringComparison.Ordinal))
            admin.ClassNames.Add(adminName);
        PasswordHasher.SetPassword(admin, adminPwd);
        book.Users.Add(admin);

        // 本机账号：教室机的日常身份
        if (!string.Equals(adminName, LocalUserName, StringComparison.OrdinalIgnoreCase))
        {
            var local = new AppUser
            {
                Username = LocalUserName,
                DisplayName = "电教管理员",
                Role = UserRole.Student
            };
            PasswordHasher.SetPassword(local, "000");
            book.Users.Add(local);

            // 自动登录切到受限账号（旧版是直接记住 admin 的账号密码，升级后必须降级）
            // ★ 只换凭据，不动 s.AutoLogin 本身：用户如果主动取消过"开机自动登录"，
            //   升级不该把它又打开（那台机器会突然变成无需密码直接进）。
            if (s.AutoLogin)
            {
                s.AutoLoginUser = LocalUserName;
                s.AutoLoginPassword = "000";
            }
            else
            {
                // 关掉的状态下更要清掉旧的管理员明文密码，留在 settings.json 里没有意义
                s.AutoLoginUser = "";
                s.AutoLoginPassword = "";
            }
            ForceWrite(SettingsPath, JsonSerializer.Serialize(s, Opt));
        }

        return book;
    }

    /// <summary>
    /// 无视持久化开关直接写文件。只给"程序自己必须先落地才能正常工作"的初始化数据用
    /// （用户表迁移），用户操作永远走 Save* 系列。
    /// </summary>
    static void ForceWrite(string path, string content)
    {
        if (SuppressWrites) return;
        try
        {
            Ensure();
            File.WriteAllText(path, content);
        }
        catch { /* 写不进去也不能让登录流程崩掉 */ }
    }

    // ================== 自动备份 ==================

    public static string BackupRoot => Path.Combine(AppDir, "backup");

    /// <summary>备份保留份数（超出后删最旧的）。</summary>
    public const int BackupKeep = 20;

    /// <summary>
    /// 在任何"覆盖型写盘"（导入数据包 / 导入总课表 / 清除全部）前调用：
    /// 把当前所有数据文件复制到 <c>backup/&lt;时间戳&gt;/</c>，返回备份目录。
    /// 没有任何可备份文件时返回空串。失败不抛异常（备份不该阻断业务）。
    /// </summary>
    public static string BackupNow(string reason = "")
    {
        try
        {
            var files = new[]
            {
                SchedulePath, TeacherSchedulePath, OverridesPath,
                NotificationsPath, SettingsPath, HomeworkPath, MissingPath,
                CountdownPath, DutyPath, UsersPath
            }.Where(File.Exists).ToList();

            if (files.Count == 0) return "";

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(BackupRoot, stamp);
            int n = 1;
            while (Directory.Exists(dir)) dir = Path.Combine(BackupRoot, $"{stamp}-{n++}");
            Directory.CreateDirectory(dir);

            foreach (var f in files)
            {
                try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); } catch { }
            }
            try
            {
                File.WriteAllText(Path.Combine(dir, "_reason.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{reason}");
            }
            catch { }

            PruneBackups();
            return dir;
        }
        catch
        {
            return "";
        }
    }

    static void PruneBackups()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return;
            var dirs = Directory.GetDirectories(BackupRoot)
                                .OrderByDescending(d => d, StringComparer.Ordinal)
                                .Skip(BackupKeep)
                                .ToList();
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch { }
            }
        }
        catch { }
    }

    public class BackupInfo
    {
        public string Dir { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime Time { get; set; }
        public string Reason { get; set; } = "";
        public int ClassCount { get; set; }
        public int OverrideCount { get; set; }
        public string Display => $"{Time:MM-dd HH:mm}　{ClassCount} 个班 · 调代 {OverrideCount} 条"
            + (string.IsNullOrWhiteSpace(Reason) ? "" : $"　（{Reason}）");
    }

    /// <summary>列出可用备份（最新在前），并读出各份的班级数/调代条数供界面显示。</summary>
    public static List<BackupInfo> ListBackups()
    {
        var list = new List<BackupInfo>();
        try
        {
            if (!Directory.Exists(BackupRoot)) return list;
            foreach (var dir in Directory.GetDirectories(BackupRoot)
                                         .OrderByDescending(d => d, StringComparer.Ordinal))
            {
                var info = new BackupInfo { Dir = dir, Name = Path.GetFileName(dir) };

                if (DateTime.TryParseExact(info.Name.Length >= 15 ? info.Name[..15] : info.Name,
                        "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t))
                    info.Time = t;
                else
                    info.Time = Directory.GetLastWriteTime(dir);

                var rf = Path.Combine(dir, "_reason.txt");
                if (File.Exists(rf))
                {
                    try
                    {
                        var lines = File.ReadAllLines(rf);
                        info.Reason = lines.Length > 1 ? string.Join(" ", lines.Skip(1)).Trim() : "";
                    }
                    catch { }
                }

                try
                {
                    var sp = Path.Combine(dir, "schedule.json");
                    if (File.Exists(sp))
                    {
                        var sf = JsonSerializer.Deserialize<ScheduleFile>(File.ReadAllText(sp), Opt);
                        info.ClassCount = sf?.Classes.Count ?? 0;
                    }
                    var op = Path.Combine(dir, "overrides.json");
                    if (File.Exists(op))
                    {
                        var ov = JsonSerializer.Deserialize<List<LessonOverride>>(File.ReadAllText(op), Opt);
                        info.OverrideCount = ov?.Count ?? 0;
                    }
                }
                catch { }

                list.Add(info);
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 把某份备份整体恢复回运行目录（先备份当前状态，再覆盖）。
    /// 返回 (是否成功, 提示)。
    /// </summary>
    public static (bool Ok, string Message) RestoreBackup(string backupDir)
    {
        try
        {
            if (!Directory.Exists(backupDir)) return (false, "备份目录不存在。");

            var files = new[]
            {
                "schedule.json", "teacher_schedule.json", "overrides.json",
                "notifications.json", "settings.json", "homework.json", "missing.json",
                "countdown.json", "duty.json", "users.json"
            };

            int restored = 0;
            BackupNow("恢复备份前自动存档");     // 先给当前状态留一份，防止恢复错了

            Ensure();
            foreach (var name in files)
            {
                var src = Path.Combine(backupDir, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(AppDir, name), true);
                restored++;
            }
            return restored > 0
                ? (true, $"已恢复 {restored} 个文件。")
                : (false, "该备份里没有可恢复的文件。");
        }
        catch (Exception ex)
        {
            return (false, "恢复失败：" + ex.Message);
        }
    }
}
