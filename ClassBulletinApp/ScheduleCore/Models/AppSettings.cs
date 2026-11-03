namespace ScheduleCore.Models;

public class AppSettings
{
    public string SelectedClass { get; set; } = "";
    public string LastImportPath { get; set; } = "";

    /// <summary>登录用户名（默认 admin）。当 Username 与某个班级名一致时，启动后默认显示该班课表。</summary>
    public string Username { get; set; } = "admin";

    /// <summary>登录密码（默认 000 三位数字，明文字符串存储，仅用于教室展示软件的简易保护）。</summary>
    public string Password { get; set; } = "000";

    /// <summary>电子邮箱（用于「忘记密码」时找回 / 重置，可留空）。</summary>
    public string Email { get; set; } = "";

    /// <summary>手机号码（用于「忘记密码」时找回 / 重置，可留空）。</summary>
    public string Phone { get; set; } = "";

    /// <summary>密保问题（用户中心设置，用于「忘记密码」验证身份；空表示未设置）。</summary>
    public string SecurityQuestion { get; set; } = "";

    /// <summary>密保答案（与 SecurityQuestion 对应，明文存储，仅用于本地找回）。</summary>
    public string SecurityAnswer { get; set; } = "";

    // r8：已删除 LastClearedMonday（"每周一自动清零调代课"的标记）。
    // 调代课改由 LessonOverride.ValidFrom/ValidTo 生效区间自然过期，旧 settings.json
    // 里残留的该字段会被 System.Text.Json 自动忽略，下次保存即消失。


    /// <summary>上次成功登录的用户名（用于登录界面默认记住，下次只需输入密码）。</summary>
    public string LastUsername { get; set; } = "";

    /// <summary>
    /// 记住账号密码并在启动时自动登录（默认开）。
    /// <para>教室机每天开机就要用，每次输账号密码太麻烦，因此默认开启；
    /// 需要重新选择「游客体验」或换账号时，到「用户中心」取消勾选，下次启动就会回到登录界面。</para>
    /// <para>关闭后账号密码仍留在 <see cref="Username"/> / <see cref="Password"/> 里（登录界面会预填，密码以 * 显示），
    /// 只是启动时不再自动进入。</para>
    /// </summary>
    public bool AutoLogin { get; set; } = true;

    /// <summary>
    /// 自动登录用的账号名。<b>只能是班主任或电教管理员（学生）</b>，管理员永不参与自动登录。
    /// <para>理由：教室机开机自动进入，如果这个账号是管理员，等于"谁坐到电脑前谁是管理员"。</para>
    /// </summary>
    public string AutoLoginUser { get; set; } = "";

    /// <summary>
    /// 自动登录账号的密码（明文）。
    /// <para>为什么这里存明文：自动登录必须在<b>无人输入</b>的情况下完成，哈希没法反推。
    /// 代价可控：只有受限角色（学生 / 班主任）会被记在这里，管理员密码从不落到这个字段。</para>
    /// </summary>
    public string AutoLoginPassword { get; set; } = "";

    /// <summary>
    /// 课表「显示日」模式：
    /// "Today" = 自动显示今天（默认，调休时改选其他日需切到 Fixed）；
    /// "Fixed" = 固定显示 FixedDisplayDate 这一天（用于调休上课：选某工作日显示其课表）。
    /// </summary>
    public string DisplayDateMode { get; set; } = "Today";

    /// <summary>Fixed 模式下显示的日期 (yyyy-MM-dd)。</summary>
    public string FixedDisplayDate { get; set; } = "";

    /// <summary>
    /// 开学第一周星期一的日期 (yyyy-MM-dd)。留空则回退到 ISO 周奇偶性估算。
    /// 用于把单双周与学校实际教学周对齐。
    /// </summary>
    public string SemesterStartMonday { get; set; } = "";

    /// <summary>
    /// 一周从星期几开始（0=周日、1=周一 … 6=周六）。默认 <b>周日</b>：
    /// 周日晚就能看到下周一的课。只影响"周视图的列顺序"与"本周是哪 7 天"，
    /// 不影响课表格子的 DayIdx（0=周一..6=周日，数据层永远按周一编号）。
    /// </summary>
    public int WeekStartDay { get; set; } = 0;   // DayOfWeek.Sunday

    /// <summary>是否开机自启（写入当前用户 Run 注册表）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>启动时直接进入全屏展示模式。</summary>
    public bool LaunchFullscreen { get; set; }

    /// <summary>启动即进入防误触锁定模式（需配合 LaunchFullscreen 或单独生效）。</summary>
    public bool LaunchLocked { get; set; }

    /// <summary>启动后是否自动弹出课表悬浮窗。</summary>
    public bool AutoShowSchedule { get; set; } = true;

    /// <summary>启动后是否自动弹出通知悬浮窗（首次安装默认关闭，仅显示悬浮课表与控制面板）。</summary>
    public bool AutoShowNotification { get; set; }

    /// <summary>启动后是否自动弹出作业公示悬浮窗（首次安装默认关闭，仅显示悬浮课表与控制面板）。</summary>
    public bool AutoShowHomework { get; set; }

    /// <summary>作业公示便签固定摆放的左上角 X（为空表示未设置，首次弹出时自动定位）。</summary>
    public double? HwWinLeft { get; set; }

    /// <summary>作业公示便签固定摆放的左上角 Y。</summary>
    public double? HwWinTop { get; set; }

    /// <summary>作业公示便签的宽度（为空表示用默认宽度）。</summary>
    public double? HwWinWidth { get; set; }

    /// <summary>作业公示便签的高度。</summary>
    public double? HwWinHeight { get; set; }

    /// <summary>作业公示便签是否置顶显示（默认置于普通层级，仅在点击「置顶」后才置顶）。</summary>
    public bool HwWinTopMost { get; set; }

    /// <summary>启动后是否自动弹出缺交作业名单便签（首次安装默认关闭，仅显示悬浮课表与控制面板）。</summary>
    public bool AutoShowMissing { get; set; }

    /// <summary>缺交作业名单便签固定摆放的左上角 X（为空表示未设置，首次弹出时自动定位）。</summary>
    public double? MsWinLeft { get; set; }

    /// <summary>缺交作业名单便签固定摆放的左上角 Y。</summary>
    public double? MsWinTop { get; set; }

    /// <summary>缺交作业名单便签的宽度。</summary>
    public double? MsWinWidth { get; set; }

    /// <summary>缺交作业名单便签的高度。</summary>
    public double? MsWinHeight { get; set; }

    /// <summary>缺交作业名单便签是否置顶显示（默认置于普通层级）。</summary>
    public bool MsWinTopMost { get; set; }

    /// <summary>启动后是否自动弹出教师个人课表悬浮窗。</summary>
    public bool AutoShowTeacher { get; set; }

    /// <summary>
    /// 桌面有窗口最大化时，自动隐藏课表与通知悬浮窗，避免遮挡正在使用的程序（不影响操作）。
    /// 仅在“最大化窗口存在”时隐藏；窗口恢复/关闭后自动重新显示。
    /// </summary>
    public bool AutoHideOnMaxWindow { get; set; }

    /// <summary>当前选中的教师（用于教师个人课表）。</summary>
    public string SelectedTeacher { get; set; } = "";

    /// <summary>
    /// 悬浮课表当前显示的对象类型：
    /// "Class" = 显示某个班级的课表（默认）；
    /// "Teacher" = 显示某位教师的个人课表。
    /// 悬浮课表标题双击、控制面板顶栏的班级 / 教师下拉都会更新此值。
    /// </summary>
    public string ScheduleKind { get; set; } = "Class";

    /// <summary>
    /// 课表数据源：
    /// "ErYi" = 从二一排课抓包落盘的 JSON 缓存读取（默认，主通道）；
    /// "Excel" = 走现有 ScheduleImporter 手动 Excel 导入（兜底）。
    /// 任一档失败都会在启动逻辑里回退到另一档，避免展示空白。
    /// </summary>
    public string ScheduleSource { get; set; } = "ErYi";

    /// <summary>二一缓存目录（project-list / grade-tree-* / class-lectures-* / substitute-list-* 所在目录）。
    /// 留空则默认使用 AppData 下的 "eryi" 子目录。</summary>
    public string ErYiDataDir { get; set; } = "";

    /// <summary>二一实时拉取的登录态 Cookie（仅 FetchLive 用）。默认空 = 关闭实时拉取，走缓存/Excel。</summary>
    public string ErYiCookie { get; set; } = "";

    /// <summary>二一排课 API 基址（默认 https://paike.21cnjy.com/query）。</summary>
    public string ErYiBaseUrl { get; set; } = "https://paike.21cnjy.com/query";

    /// <summary>应用作业公示栏壁纸前系统原有壁纸路径，用于「清除」时恢复。</summary>
    public string SavedWallpaper { get; set; } = "";

    /// <summary>上下课提醒设置（课前 / 上课 / 下课 / 放学弹窗提示）。</summary>
    public ReminderSettings Reminder { get; set; } = new();

    /// <summary>桌面背面（当天课表 / 作业公示合成壁纸）设置。</summary>
    public BackdropSettings Backdrop { get; set; } = new();

    /// <summary>
    /// 已提示过「升级说明」的程序版本（如 <c>1.0.2.0</c>）。与当前版本不同则启动时提示一次，
    /// 提示后写入当前版本，同版本不再打扰。空 = 从未提示过。
    /// </summary>
    public string UpgradeNoticeShownFor { get; set; } = "";

    /// <summary>
    /// 旧调课记录被自动归入"各自所在周"的时间（yyyy-MM-dd HH:mm）。空 = 从未发生迁移。
    /// 用于升级提示里说明"你原来的调课记录已经被限定到具体某几天"。
    /// </summary>
    public string OverridesMigratedAt { get; set; } = "";

    /// <summary>
    /// 「课表变化提醒」默认值的升级代次：0 = 还没升过；1 = 已按"全屏大字 / 15 秒 / 语音关"升过；
    /// 2 = 再把语音改回默认开。
    /// <para>只用来保证每次调整只做一次 —— 否则用户手动改回小窗后，下次启动又被打回全屏。</para>
    /// </summary>
    public int ReminderDefaultsRev { get; set; }

    /// <summary>界面主题配色方案名称（见 ThemeManager 预设）。</summary>
    public string Theme { get; set; } = "ClassicBlue";

    /// <summary>悬浮课表底部的常用软件 / 文件夹快捷入口。</summary>
    public System.Collections.Generic.List<QuickLaunchItem> QuickLaunchers { get; set; } = new();

    /// <summary>常用快捷是否仅显示图标（不显示文字，更节省空间）。</summary>
    public bool QlIconOnly { get; set; }

    /// <summary>常用快捷图标大小（逻辑像素，16~48）。</summary>
    public double QlIconSize { get; set; } = 28;

    /// <summary>
    /// 悬浮课表底部三个内置快捷（作业 / 缺交 / 便签）胶囊的字号（逻辑像素）。
    /// <para>0 = 自动：跟着悬浮课表宽度挑档（窄窗收小，宽窗放大），见 QuickActionStyle。</para>
    /// </summary>
    public double QaBuiltinFontSize { get; set; }

    /// <summary>
    /// 上一次在悬浮课表点「更新课表」是否失败。
    /// <para>失败原因不放在按钮上（按钮始终是「更新课表」，随时可以重试），而是由悬浮课表状态栏
    /// 用红字滚动提示；红字一直留到下一次更新成功（或数据已被自动更新刷新）才消失。
    /// 存在设置里，所以关掉再打开悬浮课表也还看得见。</para>
    /// </summary>
    public bool QaUpdateFailed { get; set; }

    /// <summary>「更新课表」失败的那次时刻（yyyy-MM-dd HH:mm:ss），用于判断之后是否已经更新成功。</summary>
    public string QaUpdateFailedAt { get; set; } = "";

    /// <summary>上一次「更新课表」失败的原因（单行文本），状态栏红字提示用；成功时清空。</summary>
    public string QaUpdateFailedMessage { get; set; } = "";

    /// <summary>
    /// 悬浮课表日期右侧的实时时钟显示方式：0 = 不显示，1 = 时:分，2 = 时:分:秒。默认不显示。
    /// <para>时钟每秒刷新，字号比日期小一号、颜色略淡，与日期同一行右对齐，不额外占高度。</para>
    /// </summary>
    public int ScheduleClockMode { get; set; }

    /// <summary>
    /// 各便签悬浮窗的位置 / 大小 / 置顶 / 锁定，键 = <c>countdown</c>（倒计时）/ <c>duty</c>（值日生）。
    /// <para>用字典而不是逐个加字段：以后再加便签只需新增一个键，AppSettings 不会越来越长。</para>
    /// </summary>
    public System.Collections.Generic.Dictionary<string, NoteWindowPlacement> NoteWindows { get; set; } = new();

    /// <summary>取某个便签窗口的摆放设置；没有就补一个默认对象（由调用方决定何时保存）。</summary>
    public NoteWindowPlacement PlacementOf(string key)
    {
        NoteWindows ??= new System.Collections.Generic.Dictionary<string, NoteWindowPlacement>();
        if (!NoteWindows.TryGetValue(key, out var p) || p == null)
        {
            p = new NoteWindowPlacement();
            NoteWindows[key] = p;
        }
        return p;
    }

    /// <summary>作业公示便签是否被锁定（锁定后不能移动 / 缩放 / 编辑，防误操作）。</summary>
    public bool HwWinLocked { get; set; }

    /// <summary>缺交作业名单便签是否被锁定。</summary>
    public bool MsWinLocked { get; set; }

    /// <summary>用户自建的自定义便签（教师寄语、通知等自由内容），可同时存在多张。</summary>
    public System.Collections.Generic.List<CustomNote> CustomNotes { get; set; } = new();

    /// <summary>课表悬浮窗布局（位置 / 大小 / 透明度 / 置顶 / 穿透 / 靠边）。</summary>
    public PanelLayout ScheduleLayout { get; set; } = new()
    {
        Anchor = "Right",
        Width = 420,
        Height = 760,
        Opacity = 0.96,
        TopMost = false,
        ClickThrough = false,
        AutoHide = false,
        AutoHideDuringClass = false
    };

    /// <summary>通知悬浮窗布局（顶部 / 底部字幕条）。</summary>
    public PanelLayout NotificationLayout { get; set; } = new()
    {
        Anchor = "Top",
        Width = 980,
        Height = 60,
        Opacity = 0.96,
        TopMost = true,
        ClickThrough = false,
        AutoHide = false,
        AutoHideDuringClass = false,
        FullWidth = false,
        HAnchor = "Center",
        FontSize = 28,
        MarqueeSpeed = 120
    };

    // ==================== 自动更新（离线版分发） ====================

    /// <summary>是否启用"定时检查数据包更新"。</summary>
    public bool AutoUpdateEnabled { get; set; }

    /// <summary>
    /// 数据源类型：<c>"Off"</c> / <c>"Folder"</c>（共享文件夹或 U 盘）/ <c>"Ftp"</c>。
    /// </summary>
    public string UpdateSourceKind { get; set; } = "Off";

    /// <summary>共享文件夹路径，如 <c>\\server\课表</c> 或 <c>F:\课表</c>（U 盘）。</summary>
    public string UpdateFolderPath { get; set; } = "";

    /// <summary>FTP 根地址，如 <c>ftp://10.0.0.5/schedule/</c>（末尾带斜杠）。</summary>
    public string UpdateFtpUrl { get; set; } = "";
    public string UpdateFtpUser { get; set; } = "";
    public string UpdateFtpPassword { get; set; } = "";

    /// <summary>
    /// 检查间隔的合法下限（分钟）。设置界面、运行期定时器、状态行文案**必须共用这一份**，
    /// 否则会出现"设置窗写 3、实际按 5 跑"这种静默改写（r9 修过一次，别再写成两处字面量）。
    /// </summary>
    public const int MinUpdateIntervalMinutes = 5;

    /// <summary>检查间隔的合法上限（分钟）。</summary>
    public const int MaxUpdateIntervalMinutes = 720;

    /// <summary>
    /// 检查间隔（分钟），默认 30。合法区间 <see cref="MinUpdateIntervalMinutes"/>~<see cref="MaxUpdateIntervalMinutes"/>，
    /// 越界值在 <c>Store.LoadSettings</c> 里自动归一。
    /// <para>注意：数据新鲜度主要由发布侧的自动发布保证，调小它只影响"本机多久去拉一次"，
    /// 30 台机器同时设成 1~2 分钟会给共享盘/FTP 带来不必要的登录压力。</para>
    /// </summary>
    public int UpdateIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// 发现新版本时是否先弹窗确认（false = 静默自动应用）。
    /// <para>★ 默认 <c>false</c>：教室屏是无人值守的，后台检查发现新版本必须直接应用，
    /// 否则会"发现却不更新"（见 <see cref="MainWindow"/> 的 silent 分支修复）。
    /// 想让人确认时再打开这项即可（仅影响手动点「立即检查更新」，后台永远自动应用）。</para>
    /// </summary>
    public bool UpdateConfirmBeforeApply { get; set; } = false;

    /// <summary>启动后延迟多少秒开始第一次检查（错开 30 台同时拉取）。</summary>
    public int UpdateStartupDelaySeconds { get; set; } = 30;

    /// <summary>本机是否兼任"发布机"（把 source 目录里的 Excel 解析后生成数据包上传到 publish）。</summary>
    public bool PublishEnabled { get; set; }

    /// <summary>发布源目录（存 Excel 的地方），默认与 UpdateFolderPath 同级的 source 子目录。</summary>
    public string PublishSourcePath { get; set; } = "";

    /// <summary>
    /// 发布机是否监视 source 目录：里面的 Excel 一变（含文件名、大小、最后写入时间）
    /// 就自动重新打包上传，不必每次手动点「发布到数据源」。默认开。
    /// </summary>
    public bool AutoPublishOnSourceChange { get; set; } = true;

    /// <summary>
    /// 同步策略：是否连<b>本机手工调课</b>一起覆盖。默认 <c>false</c>（保留）。
    /// <para>打开后，教室机上老师自己拖的调课会在下一次同步时被<b>停用</b>（不是删除），
    /// 可由管理员在「恢复被主机停用的本机调课」里复活。</para>
    /// <para>★ 这个值只在勾了「发布机」的机器上可编辑，且<b>实际生效的是包里的值</b>
    /// （<c>manifest.OverrideIncludeManual</c>）；本机这份只是发布时决定往包里写什么。</para>
    /// <para>长期改课（basePatch）任何时候都不受影响。</para>
    /// </summary>
    public bool OverwriteManualOverrides { get; set; }

    /// <summary>
    /// 内容版本号（写进 <c>manifest.Rev</c>）。
    /// <para>★ 存在的唯一理由：策略开关变了但 Excel 没变时，文件摘要不变 → 各机会认为
    /// "已是最新" → 策略永远下发不下去。这时 Rev 自增，摘要随之变化，强制各机重新 Apply。</para>
    /// </summary>
    public int PackageRev { get; set; }

    /// <summary>
    /// 最近一次收到的数据包所声明的同步策略（教室机只读）。
    /// <para>null = 还没收到过带策略的包（旧版本）→ 按「保留手工调课」显示。</para>
    /// <para>★ 教室机上真正生效的是这个值，不是 <see cref="OverwriteManualOverrides"/>
    /// （后者只在发布机上可编辑，表示"我要往包里写什么"）。</para>
    /// </summary>
    public bool? PackageOverwriteManual { get; set; }

    /// <summary>上一次成功应用的数据包 SHA-256（用于判断"是否已是最新"）。</summary>
    public string LastAppliedSha { get; set; } = "";

    /// <summary>上一次成功应用的时间（yyyy-MM-dd HH:mm）。</summary>
    public string LastAppliedAt { get; set; } = "";

    /// <summary>上一次检查的结果描述（显示在数据管理页，便于排查）。</summary>
    public string LastUpdateCheckInfo { get; set; } = "";

    /// <summary>
    /// 访问 Gitee 仓库所需的口令（可留空）。仅在仓库为<b>私有</b>时用于「检查软件更新」；
    /// 仓库设为公开后就不需要了。只存本机 settings.json，绝不随数据包下发。
    /// </summary>
    public string GiteeToken { get; set; } = "";

    /// <summary>
    /// 任务栏图标右键菜单里的项目（可自定义文字、顺序、取舍）。
    /// <para>★ 空列表 = 从未配置过 → 用 <see cref="TrayCommands.Defaults"/> 的内置三项。
    /// 一旦用户在「任务栏菜单…」里保存过，就以这里的顺序为准。</para>
    /// </summary>
    public System.Collections.Generic.List<TrayMenuItem> TrayMenuItems { get; set; } = new();
}

/// <summary>任务栏右键菜单里的一项。</summary>
public class TrayMenuItem
{
    /// <summary>显示文字；留空则用该命令的默认文字。</summary>
    public string Text { get; set; } = "";

    /// <summary>命令标识，取值见 <see cref="TrayCommands"/>。</summary>
    public string Command { get; set; } = "";

    public TrayMenuItem Clone() => new() { Text = Text, Command = Command };
}

/// <summary>
/// 任务栏右键菜单可用的命令清单。
/// <para>★ 菜单项只存"命令标识"，不存代码 / 路径：教室机之间会同步设置，
/// 存可执行路径等于给了别人一个远程执行入口。</para>
/// </summary>
public static class TrayCommands
{
    public const string Separator = "separator";
    public const string Panel = "panel";
    public const string UserCenter = "user";
    public const string ToggleSchedule = "toggle_schedule";
    public const string ToggleNotice = "toggle_notice";
    public const string ToggleHomework = "toggle_homework";
    public const string ToggleMissing = "toggle_missing";
    public const string BackdropSchedule = "backdrop_schedule";
    public const string BackdropHomework = "backdrop_homework";
    public const string BackdropMissing = "backdrop_missing";
    public const string BackdropOff = "backdrop_off";
    public const string Exit = "exit";

    /// <summary>一条命令的默认文字与说明。</summary>
    public sealed class Def
    {
        public string Id = "";
        public string Label = "";
        public string Hint = "";
    }

    public static readonly Def[] All =
    {
        new() { Id = Panel,           Label = "打开控制面板", Hint = "显示主窗口" },
        new() { Id = UserCenter,      Label = "用户中心",     Hint = "显示主窗口并切到「用户中心」标签" },
        new() { Id = Separator,       Label = "—— 分隔线 ——", Hint = "只显示一条横线" },
        new() { Id = ToggleSchedule,  Label = "课表悬浮",     Hint = "开 / 关课表悬浮窗（勾选表示当前已开）" },
        new() { Id = ToggleNotice,    Label = "通知悬浮",     Hint = "开 / 关顶部通知跑马灯" },
        new() { Id = ToggleHomework,  Label = "作业悬浮",     Hint = "开 / 关作业公示便签" },
        new() { Id = ToggleMissing,   Label = "缺交悬浮",     Hint = "开 / 关缺交名单便签" },
        new() { Id = BackdropSchedule, Label = "当天课表 → 桌面背景", Hint = "把当天课表画到桌面壁纸上" },
        new() { Id = BackdropHomework, Label = "作业公示 → 桌面背景", Hint = "把作业公示画到桌面壁纸上" },
        new() { Id = BackdropMissing,  Label = "缺交名单 → 桌面背景", Hint = "把缺交名单画到桌面壁纸上" },
        new() { Id = BackdropOff,      Label = "取消桌面背景", Hint = "撤掉画在壁纸上的内容，并把它们恢复成悬浮窗" },
        new() { Id = Exit,            Label = "退出",         Hint = "完全退出程序（含所有悬浮窗）" }
    };

    public static Def? Find(string? id)
    {
        string key = (id ?? "").Trim();
        foreach (var d in All)
            if (string.Equals(d.Id, key, System.StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    public static bool IsValid(string? id) => Find(id) != null;

    public static string LabelOf(string? id) => Find(id)?.Label ?? "（未知命令）";

    public static string HintOf(string? id) => Find(id)?.Hint ?? "";

    /// <summary>内置默认菜单：打开控制面板 / 用户中心 / 退出（用户没配过时用它）。</summary>
    public static System.Collections.Generic.List<TrayMenuItem> Defaults() => new()
    {
        new TrayMenuItem { Command = Panel },
        new TrayMenuItem { Command = UserCenter },
        new TrayMenuItem { Command = Separator },
        new TrayMenuItem { Command = Exit }
    };
}

/// <summary>悬浮课表底部的常用软件 / 文件夹快捷入口项。</summary>
public class QuickLaunchItem
{
    /// <summary>显示名称（留空则自动取文件 / 文件夹名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>可执行文件(.exe/.bat/.lnk)或文件夹的完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>true = 文件夹（用资源管理器打开）；false = 程序 / 文件（直接打开）。</summary>
    public bool IsFolder { get; set; }
}
