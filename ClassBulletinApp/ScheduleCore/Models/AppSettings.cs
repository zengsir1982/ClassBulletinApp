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
    /// 是否记住登录账号（下次在登录界面预填用户名）。默认开。
    /// <para>与「记住密码」「自动登录」彼此独立：用户可以只记账号不记密码。</para>
    /// </summary>
    public bool RememberUsername { get; set; } = true;

    /// <summary>
    /// 是否记住登录密码（下次在登录界面预填密码）。默认开（与旧版"记住账号密码"行为一致）。
    /// <para>★ 密码明文保存在本机 settings.json，仅作教室机的简易保护；
    /// 公用电脑建议关闭。关闭时会清空已保存的密码（<see cref="AutoLoginPassword"/>）。</para>
    /// </summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>
    /// 开机时自动登录（跳过登录界面，默认开）。
    /// <para>需要同时「记住密码」才有凭据；管理员账号永不参与自动登录。</para>
    /// <para>关闭后下次启动会回到登录界面，可换账号或改用「游客体验」。</para>
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

    /// <summary>
    /// 启动后是否自动弹出通知悬浮窗（顶部跑马灯通知条）。<b>默认开</b>：
    /// 与「置顶常驻 + 鼠标穿透 + 悬停变透明 + 上课时间自动隐藏」配套，开机即常驻又不挡课堂。
    /// <para>老配置由 <c>Store.UpgradeNotificationShowDefaults</c> 一次性升级；用户手动关掉不会被打回。</para>
    /// </summary>
    public bool AutoShowNotification { get; set; } = true;

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

    /// <summary>
    /// 启动后是否自动弹出「事项列表」便签（通知 / 公告的便签形态）。
    /// <para>★ 默认 <c>true</c>：它才是通知的完整清单，跑马灯只滚最新两条摘要。
    /// 老配置没有这个字段，反序列化后取字段初始值 → 升级后自动出现，符合「通知改便签」的意图。</para>
    /// </summary>
    public bool AutoShowNotice { get; set; } = true;

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

    /// <summary>
    /// 触摸屏：手指点输入框时自动弹出系统触摸键盘（默认开）。
    /// <para>教室一体机通常没有实体键盘，没有这一项就只能看见光标闪。</para>
    /// <para>只在 <b>触摸</b> 触发时弹出，鼠标点击不弹，避免接着鼠标办公时被键盘挡住。</para>
    /// <para>关闭后仍可手动点托盘/设置里的「触摸键盘」按钮调出。</para>
    /// </summary>
    public bool TouchKeyboardAutoShow { get; set; } = true;

    /// <summary>
    /// 是否显示「手写输入」悬浮球（<b>默认关</b>）。
    /// <para><b>为什么默认关：</b>球是常驻在屏幕上的一个小圆，对用鼠标的教室机是干扰；
    /// 而且不是每台教室机都装了手写识别组件。需要的人从「显示设置」勾一下、
    /// 或任务栏图标右键菜单里打开，位置会被记住。</para>
    /// <para><b>为什么要有悬浮球：</b>手写按钮原本只加在作业 / 缺交两个编辑窗里，倒计时、值日、
    /// 通知、用户中心这些有输入框的地方都没有入口。与其给每个窗口各加一颗按钮
    /// （漏一个就是一句「这里为什么不能手写」），不如给一个全局常驻的小球，一次做好处处可用。</para>
    /// <para>球可以在屏幕上拖动、位置会记住；点一下弹出手写面板，写完点候选字直接插回
    /// 「最后一次点过的输入框」。设为桌面背景时自动隐藏。</para>
    /// </summary>
    public bool ShowHandwritingBall { get; set; } = false;

    /// <summary>手写悬浮球左上角 X（DIP 屏幕坐标）。为空 = 用默认位置（主屏右下角）。</summary>
    public double? HandBallLeft { get; set; }

    /// <summary>手写悬浮球左上角 Y（DIP 屏幕坐标）。</summary>
    public double? HandBallTop { get; set; }

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

    /// <summary>
    /// 「悬浮课表 / 通知条常驻显示透明度」默认值升级代次：0 = 还没升过；1 = 已把旧默认 0.55 改为不透明。
    /// 只用来保证这次调整只生效一次，用户之后手动调低不会被打回。
    /// </summary>
    public int OverlayOpacityDefaultsRev { get; set; }

    /// <summary>
    /// 「通知条默认置顶常驻 + 鼠标穿透 + 悬停变透明」升级代次：
    /// 0 = 还没升过；1 = 已把老配置的通知条一次性改成这套默认。
    /// <para>★ 与 <see cref="OverlayOpacityDefaultsRev"/> 分开记：那个只动透明度，
    /// 这个动的是「置顶 / 穿透 / 悬停淡出」三件行为，各升各的，互不干扰。</para>
    /// </summary>
    public int NotificationOverlayRev { get; set; }

    /// <summary>
    /// 「通知条开机默认显示 + 上课时间自动隐藏」升级代次：0 = 未升过；1 = 已把老配置一次性改成这套默认。
    /// <para>★ 与 <see cref="NotificationOverlayRev"/> 分开记：那个只动「置顶 / 穿透 / 悬停淡出」，
    /// 这个动的是「开机自动显示」与「上课自动隐藏」，各升各的，互不干扰。</para>
    /// </summary>
    public int NotificationShowDefaultsRev { get; set; }

    /// <summary>
    /// 「作业 / 缺交条目稳定 Id」落盘迁移代次：0 = 未做过；1 = 已备份并把带 Id 的数据写回磁盘。
    /// <para>★ 读盘时始终会在内存里补齐确定性 Id（两端同数据必然得到同一 Id），
    /// 所以这个代次只用来控制「显式落盘迁移 + 迁移前自动备份」不要重复做。</para>
    /// </summary>
    public int BoardMigrationRev { get; set; }

    /// <summary>界面主题配色方案名称（见 ThemeManager 预设）。</summary>
    public string Theme { get; set; } = "ClassicBlue";

    /// <summary>
    /// 全局玻璃底色效果是否开启。<b>默认关闭</b>：教室一体机 / 远程会话零额外开销，
    /// 老用户升级后界面一个像素都不变，想要的人自己去「布局与显示设置」打开。
    /// </summary>
    public bool GlassEnabled { get; set; }

    /// <summary>
    /// 玻璃通透度（0~65，0 = 完全实色即现状，65 = 最透，默认 45）。
    /// <para>★ 只改悬浮窗根 Border 底色的 alpha，<b>文字与按钮始终 100% 不透明</b>；
    /// 与 <see cref="PanelLayout.Opacity"/> / <c>OverlayOpacity</c> 的「整窗透明度」是两回事，互不干扰。</para>
    /// </summary>
    public double GlassOpacity { get; set; } = 45;

    /// <summary>玻璃默认值升级代次：0 = 未升级；1 = 已规范过通透度越界值。</summary>
    public int GlassDefaultsRev { get; set; }

    /// <summary>
    /// r74：<b>悬浮窗</b>（悬浮课表 / 各便签 / 通知条 …，即所有 <c>IGlassSurface</c>）是否用玻璃。
    /// <para>与普通窗口的 <see cref="GlassAllWindows"/> <b>分开开关、分开通透度</b>：
    /// 便签只占一小块，透一点好看；主窗内容多，往往想更实。总闸仍是 <see cref="GlassEnabled"/>。</para>
    /// <para>老 settings.json 没有这个字段，反序列化会保留这里的 true，不需要 Rev 迁移。</para>
    /// </summary>
    public bool GlassFloatEnabled { get; set; } = true;

    /// <summary>
    /// r74：<b>普通窗口</b>（主窗 / 设置 / 编辑窗 / 向导 …，即 <c>GlassChrome</c> 接管的分层窗）的玻璃通透度。
    /// <para>0~65，0 = 完全实色；与悬浮窗的 <see cref="GlassOpacity"/> 完全独立。
    /// 首次升级由 <c>Store.UpgradeGlassSplit</c> 把老的通透度复制过来，老用户观感不变。</para>
    /// </summary>
    public double GlassChromeOpacity { get; set; } = 45;

    /// <summary>
    /// r74「悬浮窗 / 普通窗口通透度拆分」迁移代次：0 = 未迁移；1 = 已把老的
    /// <see cref="GlassOpacity"/> 复制到 <see cref="GlassChromeOpacity"/>。
    /// </summary>
    public int GlassSplitRev { get; set; }

    /// <summary>
    /// r73：普通窗口（主窗 / 设置 / 编辑 / 向导等）是否也走系统级毛玻璃。
    /// <para>默认开 —— 打开 <see cref="GlassEnabled"/> 总闸后就是「真·全局玻璃」。
    /// 老 settings.json 没有这个字段，反序列化会保留这里的 true，不需要 Rev 迁移；
    /// 觉得卡或远程桌面效果不对，可在「布局与显示设置」里单独关掉，不影响悬浮窗。</para>
    /// </summary>
    public bool GlassAllWindows { get; set; } = true;

    /// <summary>
    /// r73：普通窗口用哪种玻璃实现：auto（按系统版本自动）/ acrylic / mica / frosted（磨砂面板回退）。
    /// <para>auto 在 Win11 22H2+ 走 DWM 亚克力、Win10 1809+ 走 AcrylicBlurBehind、更老的系统自动磨砂。</para>
    /// </summary>
    public string GlassBackdropMode { get; set; } = "auto";

    /// <summary>悬浮课表底部的常用软件 / 文件夹快捷入口。</summary>
    public System.Collections.Generic.List<QuickLaunchItem> QuickLaunchers { get; set; } = new();

    /// <summary>常用快捷是否仅显示图标（不显示文字，更节省空间）。</summary>
    public bool QlIconOnly { get; set; }

    /// <summary>常用快捷图标大小（逻辑像素，16~48）。</summary>
    public double QlIconSize { get; set; } = 28;

    /// <summary>
    /// 悬浮课表底部内置快捷（作业 / 缺交 / 便签 / 倒计时 / 值日 / 设置）胶囊的字号（逻辑像素）。
    /// <para>0 = 自动：跟着悬浮课表宽度挑档（窄窗收小，宽窗放大），见 QuickActionStyle。</para>
    /// <para><b>六个胶囊共用这一个值</b>：它们是一整排，大小不一致会一眼看出「后加的三个没跟上」。</para>
    /// </summary>
    public double QaBuiltinFontSize { get; set; }

    /// <summary>
    /// 内置快捷胶囊「显示哪几个」的配置，<b>按角色分别保存</b>：键 = 角色（admin / teacher / subject / student），
    /// 值 = 动作键（hw / missing / note / countdown / duty / settings / info / publish / week）→ 是否显示。
    /// <para>某角色没有条目 = 用该角色的出厂默认（电教管理员：作业 / 缺交 / 便签 / 倒数 / 值日 / 信息；
    /// 老师与管理员：发布 / 便签 / 周课 / 倒数 / 设置 / 信息）。</para>
    /// <para>★ 按角色分开存：同一台机器上电教管理员和班主任都会登录，
    /// 老师把「作业」关掉不该让第二天的电教管理员也找不到它。</para>
    /// </summary>
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, bool>>
        QaVisibleByRole { get; set; } = new();

    /// <summary>
    /// 消息台账的显示保留天数（默认 7 天，0 = 永久全部显示）。
    /// <para>★ 只决定台账窗口「默认显示哪一段」：记录不会自动删除，未处理的收件箱也永远显示。
    /// 真要删除必须人工点台账里的「清除台账…」（见 BoardSyncService.ClearLedger）。</para>
    /// </summary>
    public int BoardSyncLedgerKeepDays { get; set; } = 7;

    /// <summary>
    /// 悬浮课表 / 桌面背景是否把单双周里「不是本周」的那节课也一起显示（灰色弱化）。
    /// <para><b>默认 false = 只显示本周实际要上的课。</b>同一格单双周两门课时，旧行为是把两周的课
    /// 上下都列出来、非本周那节显示成灰色 —— 信息全，但一屏里一半是灰字，看着乱。
    /// 只有真的需要「下周还是这门吗」时才去勾选。</para>
    /// <para>只影响显示，不影响调代课、上课提醒、变化检测（那些仍按完整数据算）。</para>
    /// </summary>
    public bool ShowAlternateWeekLessons { get; set; }

    /// <summary>
    /// 悬浮课表的触屏滚动方式：true = 双指上下划动滚动（默认），false = 单指拖动滚动。
    /// <para><b>为什么默认双指：</b>单指被「拖动卡片交换两节课」占用了。只要
    /// <c>ScrollViewer.PanningMode</c> 不是 None，触摸一按下就被它捕获并标记为已处理，
    /// WPF 不再把触摸提升成鼠标事件 —— 手指永远拖不动课程卡。改成双指后单指彻底交还给
    /// 拖动 / 长按 / 点击，双指滚动自己实现。</para>
    /// <para><b>为什么要留开关：</b>老式一体机（红外交互框、早期电阻屏）可能只上报单点触摸，
    /// 双指根本没有第二根手指，那就只能切回单指滚动 —— 代价是手指无法拖动换节（鼠标可以）。</para>
    /// </summary>
    public bool TwoFingerScroll { get; set; } = true;

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
        OverlayOpacity = 1.0,
        TopMost = false,
        ClickThrough = false,
        AutoHide = false,
        AutoHideDuringClass = false
    };

    /// <summary>
    /// 通知悬浮窗布局（顶部 / 底部字幕条）。
    /// <para>★ 出厂默认是<b>置顶常驻 + 鼠标穿透 + 鼠标移上去变透明 + 上课时间自动隐藏</b>：
    /// 通知条本来就是纯展示，常驻最前让全班随时看得见，穿透让下面的课件照常点，
    /// 老师把鼠标移过去时它自己让开（透明），上课期间自动缩到边沿、下课再回来。
    /// 老配置由 <c>Store.UpgradeNotificationOverlayDefaults</c> 与
    /// <c>Store.UpgradeNotificationShowDefaults</c> 分两次一次性升级（各记各的代次）。</para>
    /// </summary>
    public PanelLayout NotificationLayout { get; set; } = new()
    {
        Anchor = "Top",
        Width = 980,
        Height = 60,
        Opacity = 0.96,
        OverlayOpacity = 1.0,
        TopMost = true,
        ClickThrough = true,
        HoverFade = true,
        AutoHide = false,
        AutoHideDuringClass = true,
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

    // ==================== 班级内容同步（二期：结构化变更单摆渡） ====================

    /// <summary>是否启用「办公室教师机 ⇄ 教室机」班级内容同步。默认关，老配置升级后行为不变。</summary>
    public bool BoardSyncEnabled { get; set; }

    /// <summary>
    /// 摆渡根目录。留空 = 跟随上面的自动更新数据源（共享目录 / FTP 根）。
    /// 想和发布包分开放时，可单独填另一个 UNC 路径或 ftp:// 地址。
    /// </summary>
    public string BoardSyncRoot { get; set; } = "";

    /// <summary>
    /// 本机唯一设备标识：消息 / 回执文件名都带它，保证「每台设备只写自己独占的文件」。
    /// 首次启用时生成一次并落盘；两端各自独立，绝不相同。
    /// </summary>
    public string BoardSyncActorId { get; set; } = "";

    /// <summary>轮询摆渡目录的间隔（秒）。默认 60，下限 15，避免给共享盘/FTP 造成登录压力。</summary>
    public int BoardSyncPollSeconds { get; set; } = 60;

    /// <summary>有新变更单时在课间弹提示窗；上课期间静默入档，绝不打断课堂。</summary>
    public bool BoardSyncPopupDuringBreak { get; set; } = true;

    /// <summary>
    /// 允许「课间全屏提醒」：教师机发布时可以勾选「让全班当场注意到」，
    /// 教室机收到这种变更单后，在课间 / 放学后弹一次整屏大字卡片（上课时间绝不打断课堂）。
    /// <para>默认开 —— 发不发由发送方逐条决定；教室机这边只是想整体关掉强提醒时才关。</para>
    /// </summary>
    public bool BoardSyncFullScreenReminder { get; set; } = true;

    /// <summary>
    /// 教室机收到「带语音提醒」的变更单后是否朗读（音色 / 语速 / 音量沿用「提醒设置」里那套）。
    /// <para>默认开 —— 发不发由发送方逐条决定（发布窗里的「🔊 语音提醒」），
    /// 教室机只想整体静音时才关这个开关。</para>
    /// <para>★ 与 <see cref="ReminderSettings.VoiceEnabled"/> 分开：上下课提醒的语音开关
    /// 不该顺手决定班级信息要不要念出来，否则「关了上下课语音 → 通知也不念」很难查。</para>
    /// </summary>
    public bool BoardSyncVoiceReminder { get; set; } = true;

    /// <summary>
    /// 教室机是否允许「教师机指定直接增加」的本班通知自动落地（默认允许）。
    /// <para>★ 只是教室机侧的总开关：关掉之后这类变更单<b>不丢</b>，退回普通收件箱由电教管理员裁决。
    /// 教师机发布窗里的勾选只是「请求」，最终听这台机器的。自动增加永远只是新增一条，
    /// 不覆盖 / 不删除教室机原有内容，所以不会抹掉教室机本地改过的东西。</para>
    /// </summary>
    public bool BoardSyncAutoApply { get; set; } = true;

    /// <summary>
    /// 「我的任教班级」（逗号 / 顿号分隔）：在「信息管理」页从下拉里逐个增加，
    /// 形成本机自己的班级列表；既作为订阅范围，也作为发布信息时的可勾选目标。
    /// <para>★ 兼容旧值：老版本这个字段叫「额外订阅班级」，存的也是逗号分隔班级名，
    /// 升级后直接当成任教班级列表显示，不需要迁移、也不会丢订阅。</para>
    /// </summary>
    public string BoardSyncExtraClasses { get; set; } = "";

    /// <summary>
    /// 「作业公示 / 缺交名单」默认字号升级代次：1 = 把仍是「标准」档的标题 / 科目 / 内容 / 名单
    /// 一次性升到「特大」（后排也能看清）。只跑一次，之后用户手动调回标准不会被打回。
    /// </summary>
    public int BoardFontDefaultsRev { get; set; }

    /// <summary>
    /// 本机是否是「教室机」：教室机会把本机作业 / 缺交 / 本班通知的当前内容发布成快照，
    /// 供教师机新建变更单时选目标条目。用 student（电教管理员）身份登录时自动视为教室机，
    /// 此开关用于身份与机器不对应的场合（以及诊断）。默认 false = 教师机，不发布快照。
    /// </summary>
    public bool BoardSyncIsClassroom { get; set; }

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
    public const string ToggleHandBall = "toggle_handball";
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
        new() { Id = ToggleHandBall,   Label = "手写悬浮球",   Hint = "开 / 关屏幕上的手写小圆（勾选表示当前已开）" },
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

    /// <summary>
    /// 内置默认菜单：打开控制面板 / 用户中心 / 手写悬浮球 / 退出（用户没配过时用它）。
    /// <para>手写球默认是关的，把它放进默认菜单是为了"想用的人不用先翻到显示设置去找"。</para>
    /// </summary>
    public static System.Collections.Generic.List<TrayMenuItem> Defaults() => new()
    {
        new TrayMenuItem { Command = Panel },
        new TrayMenuItem { Command = UserCenter },
        new TrayMenuItem { Command = Separator },
        new TrayMenuItem { Command = ToggleHandBall },
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
