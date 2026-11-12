using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 涂鸦板的多块管理：新建 / 恢复 / 关闭，配置读写，笔迹文件路径。
/// <para>★ 配置只存本机 <c>settings.json</c> 的 <see cref="AppSettings.DoodleBoards"/>，
/// 笔迹按板 Id 单独存 ISF。两者都不进班级同步包 —— 板上是老师随手写的东西。</para>
/// </summary>
public static class DoodleBoardService
{
    private static readonly List<DoodleBoardWindow> _open = new();

    /// <summary>当前打开着的板（顺序 = 打开顺序）。</summary>
    public static IReadOnlyList<DoodleBoardWindow> Open => _open;

    /// <summary>
    /// 笔迹文件路径。传 pageId = 按页独立（<c>&lt;板Id&gt;_&lt;页Id&gt;.isf</c>）；
    /// 不传 = 老版按板一份的路径，只用于迁移 / 兼容老数据。
    /// </summary>
    public static string InkPath(string id, string? pageId = null)
    {
        var safe = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        string name = string.IsNullOrWhiteSpace(pageId) ? safe : safe + "_" + pageId;
        return Path.Combine(Store.AppDir, "doodle", name + ".isf");
    }

    /// <summary>把老版「按板一份」的笔迹搬到第一页；目标已存在就留着，不覆盖用户后来写的内容。</summary>
    public static void MigrateLegacyInk(string boardId, string pageId)
    {
        try
        {
            string legacy = InkPath(boardId);
            string page = InkPath(boardId, pageId);
            if (File.Exists(legacy) && !File.Exists(page)) File.Move(legacy, page);
        }
        catch { /* 文件被占用 / 读坏，留着原件不影响功能 */ }
    }

    /// <summary>删除一块板的所有页笔迹（含老版按板那份）。</summary>
    public static void DeleteInkForBoard(string id)
    {
        try
        {
            var dir = Path.Combine(Store.AppDir, "doodle");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, id + "*.isf"))
                File.Delete(f);
        }
        catch { /* 文件被占用就留着，不影响功能 */ }
    }

    /// <summary>
    /// 新建一块板并显示。位置按「已开板数量」斜向错开，避免叠成一摞看不出来开了几张。
    /// </summary>
    public static DoodleBoardWindow CreateNew(string? themeKey = null, string? mode = null)
    {
        var cfg = new DoodleBoardConfig
        {
            Name = NextName(),
            ThemeKey = string.IsNullOrWhiteSpace(themeKey) ? DoodleBoardThemes.DefaultKey : themeKey,
            Mode = string.IsNullOrWhiteSpace(mode) ? DoodleModes.Notes : mode
        };

        var (x, y) = NextPosition();

        // 手写模式底部有两条工具带（工具 + 颜色），默认尺寸要给够，否则一开就只剩两百来像素能写
        bool ink = string.Equals(cfg.Mode, DoodleModes.Ink, StringComparison.OrdinalIgnoreCase);
        cfg.Left = x;
        cfg.Top = y;
        cfg.Width = ink ? DoodleBoardMetrics.InkDefaultWidth : DoodleBoardMetrics.DefaultWidth;
        cfg.Height = ink ? DoodleBoardMetrics.InkDefaultHeight : DoodleBoardMetrics.DefaultHeight;

        var s = Store.LoadSettings();
        s.DoodleBoards ??= new List<DoodleBoardConfig>();
        s.DoodleBoards.Add(cfg.Clone());
        Store.SaveSettings(s);

        return Show(cfg);
    }

    /// <summary>把一张配置变成窗口并登记。</summary>
    public static DoodleBoardWindow Show(DoodleBoardConfig cfg)
    {
        var win = new DoodleBoardWindow(cfg);
        PlaceOnScreen(win, cfg);

        win.RequestClose += (_, removeConfig) => CloseBoard(win, removeConfig);
        win.Closed += (_, _) => _open.Remove(win);

        _open.Add(win);
        win.Show();
        return win;
    }

    /// <summary>
    /// 启动时把上次留着的板都恢复出来。
    /// <para><paramref name="startCollapsed"/> = 这次启动先收成一条板名条（设置里勾了「启动时先收起」）。
    /// ★ 只改这一份临时配置，不动存盘的那份：用户点「展开」后按正常逻辑写回。</para>
    /// </summary>
    public static void RestoreAll(bool startCollapsed = false)
    {
        var s = Store.LoadSettings();
        if (s.DoodleBoards == null || s.DoodleBoards.Count == 0) return;

        foreach (var saved in s.DoodleBoards.ToList())
        {
            try
            {
                var cfg = saved;
                if (startCollapsed)
                {
                    cfg = saved.Clone();
                    cfg.Collapsed = true;
                }
                Show(cfg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("涂鸦板恢复失败: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 关掉一块板；<paramref name="removeConfig"/> = 同时从设置里删掉（下次启动不再恢复）。
    /// <para>★ 顺序不能反：窗口的 Closing 事件会 PersistGeometry() 把配置写回，
    /// 先删再关的话「删除」会被自己的收尾写盘救回来，板永远删不掉。</para>
    /// </summary>
    public static void CloseBoard(DoodleBoardWindow win, bool removeConfig)
    {
        string id = win.Config.Id;
        try { win.Close(); } catch { /* 已关 */ }
        if (removeConfig) RemoveConfig(id);
    }

    /// <summary>当前是否有任何一块板开着（控制面板的「黑板：开/关」按钮靠它取状态）。</summary>
    public static bool AnyOpen => _open.Count > 0;

    /// <summary>
    /// 把开着的板全部收起（配置与笔迹都留着，随时能再打开）。
    /// <para>板书模式要占掉半边屏幕，写字时黑板还压在旁边等于白写。</para>
    /// </summary>
    public static void HideAllOpen()
    {
        if (_open.Count == 0) return;
        foreach (var w in _open.ToList())
        {
            try { CloseBoard(w, removeConfig: false); } catch { /* 已关 */ }
        }
    }

    /// <summary>
    /// 把设置里记着的板全部打开；一块都没建过就新建一块。
    /// <para>与 <see cref="RestoreAll"/> 的区别：已经开着的不会再开一遍。</para>
    /// </summary>
    public static void ShowAll()
    {
        var cfgs = AllConfigs;
        if (cfgs.Count == 0) { CreateNew(); return; }

        foreach (var cfg in cfgs.ToList())
        {
            if (FindOpen(cfg.Id) != null) continue;
            try { Show(cfg); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板打开失败: " + ex.Message); }
        }
    }

    /// <summary>设置里记录的全部板（按保存顺序），供托盘列表 / 管理窗读取。</summary>
    public static IReadOnlyList<DoodleBoardConfig> AllConfigs =>
        Store.LoadSettings().DoodleBoards ?? new List<DoodleBoardConfig>();

    /// <summary>找一块当前开着的板；没开返回 null。</summary>
    public static DoodleBoardWindow? FindOpen(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _open.FirstOrDefault(w => w.Config.Id == id);

    /// <summary>从托盘 / 管理窗打开一块板；已经开着就只把它带到前面。</summary>
    public static DoodleBoardWindow? OpenOrActivate(string id)
    {
        var existing = FindOpen(id);
        if (existing != null)
        {
            existing.BringToFront();
            return existing;
        }
        var cfg = AllConfigs.FirstOrDefault(c => c.Id == id);
        return cfg == null ? null : Show(cfg);
    }

    /// <summary>管理窗「收起」：只关窗口，配置和笔迹保留。</summary>
    public static void CloseById(string id)
    {
        var win = FindOpen(id);
        if (win != null) CloseBoard(win, removeConfig: false);
    }

    /// <summary>管理窗「删除」：关窗口后连配置和笔迹一起删。</summary>
    public static void DeleteById(string id)
    {
        var win = FindOpen(id);
        if (win != null) CloseBoard(win, removeConfig: true);
        else RemoveConfig(id);
    }

    /// <summary>改名并同步已打开窗口的板头；没开就直接改设置里的那一条。</summary>
    public static bool RenameConfig(string id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string clean = name.Trim();

        var win = FindOpen(id);
        if (win != null)
        {
            win.SetBoardName(clean);   // 内部会 SaveConfig，用运行时最新状态整条替换
            return true;
        }

        var s = Store.LoadSettings();
        var cfg = s.DoodleBoards?.FirstOrDefault(x => x.Id == id);
        if (cfg == null) return false;
        cfg.Name = clean;
        Store.SaveSettings(s);
        return true;
    }

    /// <summary>关闭全部板（不删配置，下次启动会回来）。</summary>
    public static void CloseAll()
    {
        foreach (var w in _open.ToList())
        {
            try { w.Close(); } catch { /* 已关 */ }
        }
        _open.Clear();
    }

    private static void RemoveConfig(string id)
    {
        var s = Store.LoadSettings();
        if (s.DoodleBoards == null) return;
        int removed = s.DoodleBoards.RemoveAll(x => x.Id == id);
        if (removed > 0) Store.SaveSettings(s);

        // 顺手把这块板所有页的笔迹都删掉，否则删一块板留一串 ISF 垃圾文件
        DeleteInkForBoard(id);
    }

    /// <summary>
    /// 保存单块板的配置：按 Id 替换，不整表重写其他板的条目。
    /// <para>调用很频繁（拖动停手后 420ms 一次），所以务必只碰自己那一条。</para>
    /// </summary>
    public static void SaveConfig(DoodleBoardConfig cfg)
    {
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.Id)) return;
        try
        {
            var s = Store.LoadSettings();
            s.DoodleBoards ??= new List<DoodleBoardConfig>();
            int idx = s.DoodleBoards.FindIndex(x => x.Id == cfg.Id);
            if (idx >= 0) s.DoodleBoards[idx] = cfg.Clone();
            else s.DoodleBoards.Add(cfg.Clone());
            Store.SaveSettings(s);

            if (!string.IsNullOrEmpty(Store.LastWriteError))
                System.Diagnostics.Debug.WriteLine("涂鸦板配置写入失败: " + Store.LastWriteError);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("涂鸦板配置保存异常: " + ex.Message);
        }
    }

    private static string NextName()
    {
        var s = Store.LoadSettings();
        int n = (s.DoodleBoards?.Count ?? 0) + 1;
        return "涂鸦板 " + n;
    }

    private static (double X, double Y) NextPosition()
    {
        var area = SystemParameters.WorkArea;
        int n = _open.Count;

        double w = DoodleBoardMetrics.DefaultWidth;
        double h = DoodleBoardMetrics.DefaultHeight;
        // 位置按便签板尺寸估算即可：斜向错开是"别叠成一摞"，不需要精确到像素

        // 从右上角往左下斜着排，避开常见的主窗口居中区域
        double x = area.Right - w - 40 - n * 44;
        double y = area.Top + 40 + n * 44;

        x = Math.Max(area.Left + 20, Math.Min(x, area.Right - w - 20));
        y = Math.Max(area.Top + 20, Math.Min(y, area.Bottom - h - 20));
        return (x, y);
    }

    /// <summary>
    /// 摆放窗口：有历史坐标就用历史的（并夹回屏幕内），没有就用默认位。
    /// <para>★ 必须先 Show 再夹 —— 未 Show 的窗口算不出 WorkArea 换算，位置会被算成 NaN。</para>
    /// </summary>
    private static void PlaceOnScreen(DoodleBoardWindow win, DoodleBoardConfig cfg)
    {
        if (cfg.Left is double l && cfg.Top is double t &&
            !double.IsNaN(l) && !double.IsNaN(t))
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = l;
            win.Top = t;
        }

        win.SizeChanged += (_, _) => { };   // 占位：尺寸由用户拖动决定，不自动纠正
    }
}

/// <summary>板上一张只读的自动便签（来自通知 / 作业 / 缺交）。</summary>
public sealed class AutoLine
{
    /// <summary>
    /// 行首的「科目」部分：作业 / 缺交是科目名，课表是「第N节」。
    /// <para>★ r104：删掉卡片上那行浅色模块名之后，老师要求科目本身能单独设字号 / 粗细 / 颜色，
    /// 与后面的内容区分开 —— 所以正文必须保留结构，不能再是一整段拼好的字符串。</para>
    /// </summary>
    public string Subject = "";

    /// <summary>行内主体（作业内容 / 缺交学生 / 课程名）。</summary>
    public string Text = "";

    /// <summary>科目与主体之间的分隔符：作业 / 缺交用「：」，课表用空格。</summary>
    public string Separator = "：";
}

public sealed class AutoNote
{
    /// <summary>来源显示名（通知 / 作业 / 缺交 / 课表 / 倒计时 / 值日 / 自定义）。</summary>
    public string Source = "";

    /// <summary>模块键，见 <see cref="DoodleModules"/>。决定双击打开哪个编辑窗。</summary>
    public string Module = DoodleModules.Notice;

    /// <summary>源条目 Id（通知 / 作业 / 缺交各自的 Id）。空 = 老数据没带 Id。</summary>
    public string Id = "";

    public string Title = "";
    public string Body = "";

    /// <summary>
    /// 结构化正文：一行一条，行内再分「科目 / 主体」两段。
    /// <para>★ 只有作业 / 缺交 / 课表会填（其余仍是 <see cref="Body"/> 一整段）。
    /// <see cref="Body"/> 仍然一起生成 —— 编辑窗、诊断、老逻辑都还按它读，两边必须同步。</para>
    /// </summary>
    public List<AutoLine> Lines { get; set; } = new();

    /// <summary>
    /// 长文卡：作业 / 缺交把<b>所有科目合成一张</b>，正文一科一行。
    /// <para>★ 排布时这种卡独占一整行、按可用宽度铺开 —— 3 列网格里放一张 10 行的
    /// "数学：…/语文：…" 会被挤成竖条，一行只有三四个字，教室里根本读不了。</para>
    /// </summary>
    public bool Wide { get; set; }

    /// <summary>
    /// 格式覆盖用的稳定键。
    /// <para>★ 用「来源 + 条目 Id」而不是标题：老师改了通知标题，格式不该跟着丢；
    /// 只有拿不到 Id 的老数据才退回标题当键。</para>
    /// </summary>
    public string Key => (string.IsNullOrWhiteSpace(Module) ? Source : Module) + ":"
                       + (string.IsNullOrWhiteSpace(Id) ? Title : Id);
}

/// <summary>
/// 把通知 / 作业 / 缺交折成板上的自动便签。
/// <para>★ 作业 / 缺交<b>全部科目合成一张卡</b>：正文一科一行，和悬浮便签同一形态。
/// 早期是「一个科目一张」，科目一多就把板铺满、手动便签没地方放。</para>
/// <para>★ 通知仍保留 2 条上限：通知是"最新几条"的语义，不是逐条对应一个对象。</para>
/// </summary>
internal static class DoodleAutoNotes
{
    /// <summary>作业 / 缺交单模块最大卡片数。正常班级十来科，这个量级排得下。</summary>
    internal const int MaxAutoEntries = 12;

    /// <summary>
    /// 按板当前页选中的模块折成自动便签。
    /// <para>★ <paramref name="selected"/> 为 null = 老配置，用默认三类；
    /// 空表 = 用户主动选了「本页不放任何便签」，必须真的一张都不出。
    /// 早期把「空」也当默认，导致清空菜单点了没反应。</para>
    /// </summary>
    public static List<AutoNote> Load(IReadOnlyList<string>? selected,
        IReadOnlyList<DoodleCustomModule>? custom = null)
    {
        var wanted = selected == null
            ? new List<string>(DoodleModules.Defaults)
            : new List<string>(selected);
        bool Has(string k) => wanted.Any(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
        var list = new List<AutoNote>();

        if (Has(DoodleModules.Notice))
        {
            try
            {
                foreach (var n in (Store.LoadNotifications() ?? new List<Notification>())
                             .Where(x => !x.Deleted)
                             .OrderByDescending(x => x.Pinned).ThenByDescending(x => x.CreatedAt).Take(2))
                    list.Add(new AutoNote
                    {
                        Source = "通知", Module = DoodleModules.Notice, Id = n.Id ?? "",
                        Title = string.IsNullOrWhiteSpace(n.Title) ? "通知" : n.Title,
                        Body = Cut(n.Content, 60)
                    });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板读通知失败: " + ex.Message); }
        }

        if (Has(DoodleModules.Homework))
        {
            try
            {
                list.AddRange(FromHomework(Store.LoadHomework()));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板读作业失败: " + ex.Message); }
        }

        if (Has(DoodleModules.Missing))
        {
            try
            {
                list.AddRange(FromMissing(Store.LoadMissingBoard()));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板读缺交失败: " + ex.Message); }
        }

        if (Has(DoodleModules.Schedule))
        {
            try
            {
                // 今天没课就看明天：教室里下午放学后板子还开着，全空一片不如给明天的
                var (label, lessons) = TodayOrNextLessons();
                if (lessons.Count > 0)
                    list.Add(new AutoNote
                    {
                        Source = label, Module = DoodleModules.Schedule, Id = label,
                        Title = label + "课程",
                        Body = BuildScheduleBody(lessons.Select(x => x.Period + " " + x.Subject)),
                        // ★ 节次单独成段（可用「科目」格式加粗放大），课程名用内容格式
                        Lines = lessons.Select(x => new AutoLine
                        {
                            Subject = x.Period, Text = x.Subject, Separator = " "
                        }).ToList()
                    });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板读课表失败: " + ex.Message); }
        }

        if (Has(DoodleModules.Countdown))
        {
            try
            {
                var cd = Store.LoadCountdown();
                var next = (cd?.Items ?? new List<CountdownItem>())
                    .Where(x => x.DaysLeft() >= 0).OrderBy(x => x.DaysLeft()).FirstOrDefault();
                if (next != null)
                    list.Add(new AutoNote
                    {
                        Source = "倒计时", Module = DoodleModules.Countdown, Id = next.Name,
                        Title = next.Name, Body = next.DaysText()
                    });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板读倒计时失败: " + ex.Message); }
        }

        if (Has(DoodleModules.Duty))
        {
            try
            {
                var duty = Store.LoadDuty();
                string today = DutyItem.DayNames[(int)DateTime.Now.DayOfWeek == 0 ? 7 : (int)DateTime.Now.DayOfWeek];
                var rows = (duty?.Items ?? new List<DutyItem>())
                    .Where(x => !x.Deleted && (x.Day == today || x.Day == "每天")).Take(2).ToList();
                if (rows.Count > 0)
                    list.Add(new AutoNote
                    {
                        Source = "值日", Module = DoodleModules.Duty, Id = today,
                        Title = "今日值日",
                        Body = Cut(string.Join(" · ", rows.Select(m =>
                            (string.IsNullOrWhiteSpace(m.Duty) ? "" : m.Duty + "：") + m.Names)), 60)
                    });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("涂鸦板读值日失败: " + ex.Message); }
        }

        if (Has(DoodleModules.Custom) && custom != null)
        {
            foreach (var m in custom)
                list.Add(new AutoNote
                {
                    Source = "自定义", Module = DoodleModules.Custom, Id = m.Id,
                    Title = string.IsNullOrWhiteSpace(m.Title) ? "自定义模块" : m.Title,
                    Body = Cut(m.Body, 120)
                });
        }

        return list;
    }

    /// <summary>
    /// 作业 → 自动便签：<b>全部科目合成一张卡</b>，正文一科一行。
    /// <para>★ 原来一科一张：科目一多板上就被作业卡铺满，手动便签没地方放，
    /// 而且和悬浮便签的形态不一致（悬浮便签本来就是一张里列各科）。现在同形态。</para>
    /// </summary>
    internal static List<AutoNote> FromHomework(HomeworkBoard? hw)
    {
        var list = new List<AutoNote>();
        if (hw?.Items == null) return list;

        var lines = new List<string>();
        var structs = new List<AutoLine>();
        foreach (var h in hw.Items.Where(x => !x.Deleted).Take(MaxAutoEntries))
        {
            string subject = (h.Subject ?? "").Trim();
            string content = (h.Content ?? "").Trim();
            if (subject.Length == 0 && content.Length == 0) continue;
            lines.Add(subject.Length == 0 ? content
                    : content.Length == 0 ? subject
                    : subject + "：" + content);
            structs.Add(new AutoLine
            {
                Subject = content.Length == 0 ? "" : subject,
                Text = content.Length == 0 ? subject : content
            });
        }
        if (lines.Count == 0) return list;

        list.Add(new AutoNote
        {
            Source = "作业", Module = DoodleModules.Homework, Id = "all", Wide = true,
            Title = string.IsNullOrWhiteSpace(hw.Title) ? "各科作业公示" : hw.Title.Trim(),
            Body = Cut(string.Join("\n", lines), 400),
            Lines = structs
        });
        return list;
    }

    /// <summary>
    /// 缺交 → 自动便签：<b>全部科目合成一张卡</b>，正文一科一行（与作业同口径）。
    /// </summary>
    internal static List<AutoNote> FromMissing(MissingBoard? miss)
    {
        var list = new List<AutoNote>();
        if (miss?.Items == null) return list;

        var lines = new List<string>();
        var structs = new List<AutoLine>();
        foreach (var m in miss.Items.Where(x => !x.Deleted).Take(MaxAutoEntries))
        {
            string subject = (m.Subject ?? "").Trim();
            string students = (m.Students ?? "").Trim();
            if (subject.Length == 0 && students.Length == 0) continue;
            lines.Add(subject.Length == 0 ? students
                    : students.Length == 0 ? subject
                    : subject + "：" + students);
            structs.Add(new AutoLine
            {
                Subject = students.Length == 0 ? "" : subject,
                Text = students.Length == 0 ? subject : students
            });
        }
        if (lines.Count == 0) return list;

        list.Add(new AutoNote
        {
            Source = "缺交", Module = DoodleModules.Missing, Id = "all", Wide = true,
            Title = string.IsNullOrWhiteSpace(miss.Title) ? "缺交作业名单" : miss.Title.Trim(),
            Body = Cut(string.Join("\n", lines), 400),
            Lines = structs
        });
        return list;
    }

    /// <summary>
    /// 课表正文：<b>一节课一行</b>。
    /// <para>★ 原来用「 · 」把所有课挤成一行，教室大屏上远看就是一串字，分不清第几节上什么。
    /// 现在按「第X节 科目」逐行排，行数 = 当天节数。</para>
    /// <para>★ 预算给 200 字符：一整天 8 节 × 约 8 字 = 64 字，`Cut` 不会截出省略号；
    /// 仍走 `Cut` 是为了防御手改 json 塞进来超长课程名把板撑爆。</para>
    /// </summary>
    internal static string BuildScheduleBody(IEnumerable<string> lessons)
        => Cut(string.Join("\n", lessons), 200);

    /// <summary>今天有课就用今天，没有就用明天，都没有返回空表。</summary>
    private static (string Label, List<(string Period, string Subject)> Lessons) TodayOrNextLessons()
    {
        var sf = Store.LoadSchedule();
        var settings = Store.LoadSettings();
        string cls = (settings.SelectedClass ?? "").Trim();
        var cs = sf?.Classes?.FirstOrDefault(c =>
            string.Equals(c.ClassName, cls, StringComparison.OrdinalIgnoreCase))
            ?? sf?.Classes?.FirstOrDefault();
        if (cs?.Data == null) return ("今日", new List<(string, string)>());

        for (int offset = 0; offset <= 1; offset++)
        {
            var dt = DateTime.Now.AddDays(offset);
            int day = ((int)dt.DayOfWeek + 6) % 7;   // 0=周一
            var lines = new List<(string Period, string Subject)>();
            foreach (var period in cs.Data.Where(kv => kv.Key == day).SelectMany(kv => kv.Value)
                         .OrderBy(kv => kv.Key))
            {
                foreach (var l in period.Value.Where(x => !string.IsNullOrWhiteSpace(x.Subject)).Take(1))
                    lines.Add(("第" + period.Key + "节", l.Subject));
            }
            if (lines.Count > 0) return (offset == 0 ? "今日" : "明日", lines);
        }
        return ("今日", new List<(string, string)>());
    }

    /// <summary>
    /// r99：★ 正文里的回车换行必须保留 —— 老实现把 <c>\r</c> / <c>\n</c> 一律换成空格，
    /// 老师在编辑窗里按回车分段，写回公示后重新折成便签又被压平，看起来就是「强制换行没生效」。
    /// 现在按行裁剪：每行独立 trim，行与行之间仍用 <c>\n</c> 连接，总长度仍受 max 约束（防止把板撑爆）。
    /// </summary>
    internal static string Cut(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();
        int budget = max;
        foreach (var raw in lines)
        {
            if (budget <= 0) break;
            string line = raw.Trim();
            if (line.Length > budget)
            {
                kept.Add(line.Substring(0, Math.Max(1, budget)).TrimEnd() + "…");
                budget = 0;
            }
            else
            {
                kept.Add(line);
                budget -= Math.Max(1, line.Length);
            }
        }
        return string.Join("\n", kept).TrimEnd();
    }
}
