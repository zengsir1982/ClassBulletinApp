namespace ScheduleCore.Models;

/// <summary>涂鸦板的两种用途（存字符串，方便老版本忽略未知值）。</summary>
/// <summary>板上可展示的内容模块（自动便签的来源）。</summary>
public static class DoodleModules
{
    public const string Notice = "notice";
    public const string Homework = "homework";
    public const string Missing = "missing";
    public const string Schedule = "schedule";
    public const string Countdown = "countdown";
    public const string Duty = "duty";
    public const string Custom = "custom";

    /// <summary>默认展示的三类（与老版本行为一致）。</summary>
    public static readonly string[] Defaults = { Notice, Homework, Missing };

    public sealed record Info(string Key, string Name, string Hint, bool HasEditor);

    public static readonly IReadOnlyList<Info> All = new List<Info>
    {
        new(Notice,    "通知",   "最新两条班内通知",         true),
        new(Homework,  "作业",   "各科作业，一个科目一张卡片", true),
        new(Missing,   "缺交",   "各科缺交名单，一个科目一张", true),
        new(Schedule,  "课表",   "今日 / 明日课程",          false),
        new(Countdown, "倒计时", "最近的倒计时事件",         true),
        new(Duty,      "值日",   "今日值日（双击编辑整周值日表）", true),
        new(Custom,    "自定义", "自己写的文字模块（勾选即建一张）", true)
    };

    public static Info Find(string? key) =>
        All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static string NameOf(string? key) => Find(key).Name;
}

/// <summary>用户自己写的文字模块（作为自动便签展示，只存本机）。</summary>
public class DoodleCustomModule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "自定义模块";
    public string Body { get; set; } = "";
}

/// <summary>
/// 一张便签的摆放（位置 + 尺寸）。按便签稳定 Key 索引，不存在 = 用自动排布。
/// <para>★ 位置尺寸可空：null = 交给自动排布；只有用户真正拖过才写具体值，
/// 这样板子换尺寸、少了几张便签时，没动过的那些仍会自动重排。</para>
/// </summary>
public class DoodleNotePlacement
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }

    public DoodleNotePlacement Clone() => new() { X = X, Y = Y, Width = Width, Height = Height };
}

public static class DoodleModes
{
    /// <summary>便签容器：上半区自动拉通知 / 作业 / 缺交，下半区手动便签。</summary>
    public const string Notes = "notes";

    /// <summary>手写黑板：整块可落笔，笔迹单独存 ISF 文件。</summary>
    public const string Ink = "ink";
}

/// <summary>
/// 手动便签（只存本机，不参与班级同步）。
/// <para>与通知 / 作业那种有生命周期的条目不同，手动便签由老师自己写、自己删，
/// 日期翻篇不会清空 —— 所以两者在板上分区陈列，不混排。</para>
/// </summary>
public class DoodleNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>便签标题（第一行）。</summary>
    public string Title { get; set; } = "";

    /// <summary>便签正文（可为空）。</summary>
    public string Body { get; set; } = "";
}

/// <summary>
/// 涂鸦板的材质主题。
/// <para>★ 主题不只换底色：木框宽度、图钉形状、墨色都要一起换。
/// 只换 background 做出来的板，远看仍然是"同一个框换了颜色"，教室里分辨不出来。</para>
/// </summary>
public sealed class DoodleBoardTheme
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>板面底色。</summary>
    public string Background { get; init; } = "#FFFFFF";

    /// <summary>板的外框色（<see cref="FrameWidth"/> 为 0 时就是 1px 描边色）。</summary>
    public string Border { get; init; } = "#E1E6EE";

    /// <summary>外框厚度：黑板 7（木框）、白板 2（金属边）、其余 1。</summary>
    public double FrameWidth { get; init; } = 1;

    /// <summary>墨色：板头文字与便签标题的兜底色。</summary>
    public string Ink { get; init; } = "#1F2A3A";

    /// <summary>次要文字色。</summary>
    public string Muted { get; init; } = "#667085";

    /// <summary>自动便签（通知 / 作业 / 缺交）的底色与描边。</summary>
    public string NoteBackground { get; init; } = "#FFFFFF";
    public string NoteBorder { get; init; } = "#E7ECF1";

    /// <summary>手动便签的底色与描边（淡黄，与自动便签区分）。</summary>
    public string ManualBackground { get; init; } = "#FBF3E2";
    public string ManualBorder { get; init; } = "#EADFC4";

    /// <summary>便签标题 / 正文色（便签是浅色的，所以与板面墨色分开）。</summary>
    public string NoteText { get; init; } = "#3A3A34";
    public string NoteSub { get; init; } = "#6B6B60";

    /// <summary>胶带 / 图钉色。</summary>
    public string Pin { get; init; } = "#CFC7AF";

    /// <summary>图钉用圆点（软木主题）而不是胶带条。</summary>
    public bool PinRound { get; init; }

    /// <summary>强调色：手柄、选中态、笔色默认值。</summary>
    public string Accent { get; init; } = "#1E4FA3";

    /// <summary>分区标题与分隔线色（带 alpha，深色板上必须够淡）。</summary>
    public string Divider { get; init; } = "#263A3A34";

    /// <summary>深色板：手写默认用浅色粉笔。</summary>
    public bool Chalk { get; init; }

    /// <summary>默认笔色（粉笔白 / 深墨）。</summary>
    public string DefaultPen => Chalk ? "#E9F1EA" : "#25303D";
}

/// <summary>五套内置材质。</summary>
public static class DoodleBoardThemes
{
    public const string DefaultKey = "chalk";

    public static readonly IReadOnlyList<DoodleBoardTheme> All = new List<DoodleBoardTheme>
    {
        // ---- 十套板面，统一按「能在上面写字 + 贴上去的卡片看得出边界」来配色 ----
        // 规则：① 墨色与底色的明度差够大（远看也能读）；② 便签底色与板面至少差一个明度带，
        // 卡片边缘靠 NoteBorder 再压一道，贴上去有实物感；③ 深色板 Chalk=true，手写默认浅色笔。
        new()
        {
            Key = "chalk", Name = "黑板绿",
            Background = "#2C3A33", Border = "#B08A5E", FrameWidth = 7,
            Ink = "#E9F1EA", Muted = "#9BB1A4",
            NoteBackground = "#F2F5EC", NoteBorder = "#D8E0D2",
            ManualBackground = "#FBF3E2", ManualBorder = "#E0D3B2",
            NoteText = "#2C3A33", NoteSub = "#3F4A43",
            Pin = "#8FA394", Accent = "#F2D8A8", Divider = "#3BE9F1EA", Chalk = true
        },
        new()
        {
            Key = "linen", Name = "亚麻米",
            Background = "#E7E0D0", Border = "#CFC5AE", FrameWidth = 3,
            Ink = "#2F2A22", Muted = "#776E5C",
            NoteBackground = "#FFFFFF", NoteBorder = "#DCD4BF",
            ManualBackground = "#FBF0D8", ManualBorder = "#DFCBA4",
            NoteText = "#2F2A22", NoteSub = "#4A4438",
            Pin = "#C2B79A", Accent = "#1E4FA3", Divider = "#26322C24"
        },
        new()
        {
            Key = "brand", Name = "雾蓝板",
            Background = "#C9D6E4", Border = "#9FB3C8", FrameWidth = 3,
            Ink = "#16283C", Muted = "#4F6478",
            NoteBackground = "#FFFFFF", NoteBorder = "#C3D2E1",
            ManualBackground = "#FBF0D8", ManualBorder = "#DFCBA4",
            NoteText = "#16283C", NoteSub = "#3A4A5C",
            Pin = "#AFC1D2", Accent = "#1E4FA3", Divider = "#2616283C"
        },
        new()
        {
            Key = "cork", Name = "软木棕",
            Background = "#B98B5E", Border = "#8A6540", FrameWidth = 7,
            Ink = "#2E2116", Muted = "#5C4128",
            NoteBackground = "#FFFDF4", NoteBorder = "#D9C4A2",
            ManualBackground = "#FBEFD5", ManualBorder = "#D8BF95",
            NoteText = "#2E2116", NoteSub = "#4A3624",
            Pin = "#7A5636", PinRound = true, Accent = "#1E4FA3", Divider = "#2C2E2116"
        },
        new()
        {
            Key = "white", Name = "白板",
            Background = "#FFFFFF", Border = "#C6CFDA", FrameWidth = 2,
            Ink = "#1E2430", Muted = "#6B7280",
            NoteBackground = "#FFF7DA", NoteBorder = "#E6D9B4",
            ManualBackground = "#FFF4D6", ManualBorder = "#E0CFA2",
            NoteText = "#1E2430", NoteSub = "#3E4550",
            Pin = "#D5DCE5", Accent = "#1E4FA3", Divider = "#1F1E2430"
        },
        new()
        {
            Key = "nightblue", Name = "夜空蓝",
            Background = "#17253F", Border = "#4A5F80", FrameWidth = 6,
            Ink = "#EAF1FF", Muted = "#A5B8D6",
            NoteBackground = "#F2F6FD", NoteBorder = "#CBD8EC",
            ManualBackground = "#FBEFD5", ManualBorder = "#D8BF95",
            NoteText = "#17253F", NoteSub = "#3B4C6B",
            Pin = "#8FA3C4", Accent = "#F2D8A8", Divider = "#3BEAF1FF", Chalk = true
        },
        new()
        {
            Key = "contrast", Name = "高对比",
            Background = "#101010", Border = "#4A4A4A", FrameWidth = 4,
            Ink = "#FFD75E", Muted = "#C9C3AC",
            NoteBackground = "#FFFDF0", NoteBorder = "#D8D2BC",
            ManualBackground = "#FFF3D6", ManualBorder = "#D9C79A",
            NoteText = "#141414", NoteSub = "#333333",
            Pin = "#B0A98F", Accent = "#FFD75E", Divider = "#3BFFD75E", Chalk = true
        },
        new()
        {
            Key = "sunset", Name = "木纹板",
            Background = "#D3B183", Border = "#A17F55", FrameWidth = 7,
            Ink = "#3A2A18", Muted = "#6E5535",
            NoteBackground = "#FFFCF2", NoteBorder = "#D9C6A2",
            ManualBackground = "#FBEFD5", ManualBorder = "#D8BF95",
            NoteText = "#3A2A18", NoteSub = "#4E3B22",
            Pin = "#A17F55", Accent = "#C0392B", Divider = "#2C3A2A18"
        },
        new()
        {
            Key = "mint", Name = "薄荷纸",
            Background = "#DCEBE1", Border = "#AFCBB8", FrameWidth = 3,
            Ink = "#17402D", Muted = "#4E7259",
            NoteBackground = "#FFFFFF", NoteBorder = "#C6DCCB",
            ManualBackground = "#FBF0D8", ManualBorder = "#DFCBA4",
            NoteText = "#17402D", NoteSub = "#365441",
            Pin = "#B4CDBF", Accent = "#1F7A4D", Divider = "#2617402D"
        },
        new()
        {
            Key = "slate", Name = "石墨板",
            Background = "#4B555F", Border = "#2F363D", FrameWidth = 5,
            Ink = "#EDF1F5", Muted = "#C2CAD1",
            NoteBackground = "#FBFCFD", NoteBorder = "#D3DAE0",
            ManualBackground = "#FBEFD5", ManualBorder = "#D8BF95",
            NoteText = "#1F262D", NoteSub = "#3E474F",
            Pin = "#9AA5AE", Accent = "#F2D8A8", Divider = "#3BEDF1F5", Chalk = true
        }
    };

    /// <summary>按键取主题；未知键（老版本 / 手改 json）一律回落到默认，绝不抛。</summary>
    public static DoodleBoardTheme Get(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            foreach (var t in All)
                if (string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return All[0];
    }
}

/// <summary>
/// 一张<b>自动便签</b>（通知 / 作业 / 缺交）的文字外观覆盖。
/// <para>★ 为什么不写回通知 / 作业本身：那三个是全校共用的正式数据，
/// 在板上为了好看加个粗、改个色，绝不能顺着同步发到别的机器上去。
/// 所以格式只存在本机这块板的配置里，按「来源:条目 Id」索引，条目一过期自然失效。</para>
/// </summary>
public class DoodleAutoStyle
{
    /// <summary>标题格式；null = 用板默认（加粗 + 板字号）。</summary>
    public TextStyle? Title { get; set; }

    /// <summary>正文格式；null = 用板默认（常规 + 板字号下一档）。</summary>
    public TextStyle? Content { get; set; }

    /// <summary>
    /// 行首「科目」的格式（作业 / 缺交的科目名、课表的「第N节」）；null = 默认加粗 + 比内容大一档。
    /// <para>★ r104：正文按行保留结构后才可能单独设置 —— 用户要求科目与内容要能看出区别。</para>
    /// </summary>
    public TextStyle? Subject { get; set; }

    /// <summary>科目直接字号（同 <see cref="ContentSize"/>：突破共享档位 38 的上限）。</summary>
    public double? SubjectSize { get; set; }

    /// <summary>
    /// 大屏直接字号（DIP）；null = 退回 <see cref="Title"/> 的档位倍率。
    /// ★ 共享的 HwTextStyle 档位最大只有 38，教室后排不够看；这里写具体值才能突破上限，
    /// 而且不动那张被众多公示共用的持久化档位表。
    /// </summary>
    public double? TitleSize { get; set; }

    /// <summary>同上，正文直接字号。</summary>
    public double? ContentSize { get; set; }

    /// <summary>
    /// 锁定这张便签：锁定后不能在板上拖动 / 缩放 / 双击编辑（防课堂上误碰）。
    /// <para>与整板 <see cref="DoodleBoardConfig.Locked"/> 独立：这里只锁单张卡片。</para>
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// ★ 这张便签自己的风格（<c>HomeworkThemes</c> 的 key，如 classic / blue / green）。
    /// 空 = 跟随黑板主题。编辑窗里选「主题风格」改的是这里，<b>不是整块黑板的主题</b>，
    /// 所以同一块板上每张便签可以各有底色，改一张不影响其它卡。
    /// </summary>
    public string NoteTheme { get; set; } = "";

    public DoodleAutoStyle Clone() => new()
    {
        Title = Title?.Clone(),
        Content = Content?.Clone(),
        Subject = Subject?.Clone(),
        TitleSize = TitleSize,
        ContentSize = ContentSize,
        SubjectSize = SubjectSize,
        Locked = Locked,
        NoteTheme = NoteTheme
    };
}

/// <summary>板头（板名 / 班级 / 时间）的独立文字格式。</summary>
public class DoodleHeaderStyle
{
    public double TitleSize { get; set; } = 18;
    public double InfoSize { get; set; } = 14;

    /// <summary>空 = 跟随主题墨色；填了就用指定色。</summary>
    public string Color { get; set; } = "";
    public bool Bold { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public double SafeTitleSize => Math.Clamp(double.IsNaN(TitleSize) ? 18 : TitleSize, 14, DoodleStyle.MaxHeaderFontSize);

    [System.Text.Json.Serialization.JsonIgnore]
    public double SafeInfoSize => Math.Clamp(double.IsNaN(InfoSize) ? 14 : InfoSize, 12, DoodleStyle.MaxHeaderFontSize);

    public DoodleHeaderStyle Clone() => new()
    {
        TitleSize = TitleSize,
        InfoSize = InfoSize,
        Color = Color ?? "",
        Bold = Bold
    };
}

/// <summary>
/// 一块涂鸦板的配置（存在 <see cref="AppSettings.DoodleBoards"/>）。
/// <para>★ 位置 / 尺寸用可空 double：null = 第一次创建，交给服务挑个不重叠的位置。
/// 写 null 而不是 0，是因为 0 是合法坐标，两者混用会让"自动摆放"永远不生效。</para>
/// </summary>
/// <summary>一块板里的一个"板面/页"，各自有自己的便签、模块与摆放。</summary>
public class DoodleBoardPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "板面 1";

    /// <summary>这一页展示哪些模块。null = 老配置回落到默认三类；空 = 这一页故意不放任何便签。</summary>
    public List<string> Modules { get; set; } = new();

    /// <summary>这一页的自定义文字模块。</summary>
    public List<DoodleCustomModule> CustomModules { get; set; } = new();

    /// <summary>这一页的手动便签。</summary>
    public List<DoodleNote> Notes { get; set; } = new();

    /// <summary>单张自动便签的格式覆盖。</summary>
    public Dictionary<string, DoodleAutoStyle> AutoStyles { get; set; } = new();

    /// <summary>每张便签的位置与大小（键 = AutoNote.Key / 手动便签 Id）。</summary>
    public Dictionary<string, DoodleNotePlacement> Placements { get; set; } = new();

    public DoodleBoardPage Clone() => new()
    {
        Id = Id,
        Name = Name,
        Modules = (Modules ?? new List<string>()).ToList(),
        CustomModules = (CustomModules ?? new List<DoodleCustomModule>())
            .Select(m => new DoodleCustomModule { Id = m.Id, Title = m.Title, Body = m.Body }).ToList(),
        Notes = (Notes ?? new List<DoodleNote>())
            .Select(n => new DoodleNote { Id = n.Id, Title = n.Title, Body = n.Body }).ToList(),
        AutoStyles = (AutoStyles ?? new Dictionary<string, DoodleAutoStyle>()).ToDictionary(
            kv => kv.Key, kv => kv.Value?.Clone() ?? new DoodleAutoStyle()),
        Placements = (Placements ?? new Dictionary<string, DoodleNotePlacement>()).ToDictionary(
            kv => kv.Key, kv => kv.Value?.Clone() ?? new DoodleNotePlacement())
    };
}

public class DoodleBoardConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>板名（显示在板头，用户可改）。</summary>
    public string Name { get; set; } = "涂鸦板";

    /// <summary>主题键，见 <see cref="DoodleBoardThemes"/>。</summary>
    public string ThemeKey { get; set; } = DoodleBoardThemes.DefaultKey;

    /// <summary>用途，见 <see cref="DoodleModes"/>。</summary>
    public string Mode { get; set; } = DoodleModes.Notes;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }

    /// <summary>
    /// 是否置顶。★ 涂鸦板现在是「桌面底层板」：永远压在所有窗口之下，不抢前台。
    /// 这个字段仅为读老配置保留，界面不再提供开关，构造窗口时一律按 false 处理。
    /// </summary>
    public bool TopMost { get; set; }

    /// <summary>锁定后不能拖动、不能缩放、不能编辑（防课堂上误碰）。</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 是否最小化（收成一条板名条）。
    /// <para>★ 只影响窗口高度：内容、页、笔迹全都留着，展开即恢复。
    /// <b>高度必须仍按展开态存</b>（见 DoodleBoardWindow.PersistGeometry），
    /// 否则下次启动会拿收起后的 44 去 Clamp，变成一条永远打不开的窄缝。</para>
    /// </summary>
    public bool Collapsed { get; set; }

    /// <summary>细笔 / 粗笔（手写用）。★ 已被 <see cref="PenWidth"/> 取代，仅为读老配置保留。</summary>
    public bool ThickPen { get; set; }

    // ---- 可调样式（null = 用默认值，方便老 json 平滑升级） ----

    /// <summary>便签字号基准（标题），正文自动小 1.5。</summary>
    public double? FontSize { get; set; }

    /// <summary>便签文字色；null = 跟随主题。</summary>
    public string? TextColor { get; set; }

    /// <summary>笔宽（DIP）。</summary>
    public double? PenWidth { get; set; }

    /// <summary>笔色；null = 跟随主题默认墨色。</summary>
    public string? PenColor { get; set; }

    /// <summary>橡皮直径（DIP）。</summary>
    public double? EraserSize { get; set; }

    /// <summary>手动便签列表。</summary>
    public List<DoodleNote> Notes { get; set; } = new();

    /// <summary>
    /// 自动便签的文字格式覆盖，键 =「来源:条目 Id」（见 <see cref="AutoNote.Key"/>）。
    /// 空字典 = 全部走板默认格式。
    /// </summary>
    public Dictionary<string, DoodleAutoStyle> AutoStyles { get; set; } = new();

    // ---- 板面 / 模块（多页面与自选内容）----

    /// <summary>这块板的所有板面。空 = 老配置，界面会把便签 / 格式 / 摆放迁到第一页。</summary>
    public List<DoodleBoardPage> Pages { get; set; } = new();

    /// <summary>当前显示的页索引。</summary>
    public int ActivePage { get; set; }

    /// <summary>板头文字（板名 / 班级 / 时间）的独立格式；教室后排要看得清。</summary>
    public DoodleHeaderStyle HeaderStyle { get; set; } = new();

    /// <summary>给老配置补一个默认页，把原来板级的便签 / 格式迁进去（幂等）。</summary>
    public DoodleBoardPage EnsurePage()
    {
        Pages ??= new List<DoodleBoardPage>();
        if (Pages.Count == 0)
        {
            Pages.Add(new DoodleBoardPage
            {
                Name = "板面 1",
                // 老配置原来固定显示通知/作业/缺交三类，迁移后显式写上，界面回显才对得上
                Modules = new List<string>(DoodleModules.Defaults),
                Notes = (Notes ?? new List<DoodleNote>())
                    .Select(n => new DoodleNote { Id = n.Id, Title = n.Title, Body = n.Body }).ToList(),
                AutoStyles = (AutoStyles ?? new Dictionary<string, DoodleAutoStyle>()).ToDictionary(
                    kv => kv.Key, kv => kv.Value?.Clone() ?? new DoodleAutoStyle())
            });
        }
        ActivePage = Math.Clamp(ActivePage, 0, Pages.Count - 1);
        return Pages[ActivePage];
    }

    /// <summary>照抄一份（服务改配置时不污染界面上正在用的对象）。</summary>
    public DoodleBoardConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        ThemeKey = ThemeKey,
        Mode = Mode,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        TopMost = TopMost,
        Locked = Locked,
        Collapsed = Collapsed,
        ThickPen = ThickPen,
        FontSize = FontSize,
        TextColor = TextColor,
        PenWidth = PenWidth,
        PenColor = PenColor,
        EraserSize = EraserSize,
        Notes = Notes.Select(n => new DoodleNote { Id = n.Id, Title = n.Title, Body = n.Body }).ToList(),
        // ★ 字典必须深拷贝：直接复用引用的话，界面里改了格式会绕过服务层悄悄改掉磁盘上的对象，
        // 「取消 / 关闭不保存」就形同虚设。
        AutoStyles = (AutoStyles ?? new Dictionary<string, DoodleAutoStyle>()).ToDictionary(
            kv => kv.Key,
            kv => kv.Value?.Clone() ?? new DoodleAutoStyle()),
        Pages = (Pages ?? new List<DoodleBoardPage>()).Select(p => p.Clone()).ToList(),
        ActivePage = ActivePage,
        HeaderStyle = (HeaderStyle ?? new DoodleHeaderStyle()).Clone()
    };
}

/// <summary>涂鸦板的尺寸与几何常量（界面与服务共用同一套，避免各处硬编不同数字）。</summary>
public static class DoodleBoardMetrics
{
    public const double MinWidth = 300;
    public const double MinHeight = 220;

    /// <summary>
    /// 尺寸上限对用户不再设限：板子能一直拖到当前屏幕工作区那么大，由使用者决定。
    /// <para>★ 这里只留一个「settings.json 被手改坏」的绝对兜底 —— 没有它，一条
    /// <c>"Width": 90000</c> 就能让板子宽到屏幕外、再也拖不回来。</para>
    /// </summary>
    public const double HardMaxWidth = 4000;
    public const double HardMaxHeight = 3000;

    public const double DefaultWidth = 560;

    /// <summary>
    /// 便签板默认高度。底部工具带占 54、板头 42，三行便签连右下角缩放手柄
    /// 需要约 370 的内容高度 —— 460 会把手柄压在工具带边界上，所以给到 500。
    /// </summary>
    public const double DefaultHeight = 500;

    /// <summary>手写板的默认尺寸：底部有"工具 + 颜色"两条带，机身要更高才写得开。</summary>
    public const double InkDefaultWidth = 680;
    public const double InkDefaultHeight = 520;

    /// <summary>板头高度（板名 / 模式切换 / 设置 / 关闭都在这一行）。</summary>
    public const double HeadHeight = 42;

    /// <summary>
    /// 边缘缩放命中区（DIP）。比桌面常规的 8 大：教室一体机是手指操作，
    /// 8px 的边框基本点不中，会变成"想调大小结果在拖窗口"。
    /// </summary>
    public const double ResizeMargin = 14;

    /// <summary>底部工具带的按钮边长。★ 触屏 44 是下限，小于这个数手指点不准。</summary>
    public const double ToolButtonSize = 44;

    /// <summary>三档预设 + 一档「铺满」，宽度 → 高度按 1.4 比例算。</summary>
    public static readonly (string Name, double W, double H)[] Presets =
    {
        ("小", 380, 300),
        ("中", 560, 400),
        ("大", 800, 560),
        ("特大", 1100, 760)
    };

    public static double ClampWidth(double w) =>
        Math.Clamp(double.IsNaN(w) ? DefaultWidth : w, MinWidth, HardMaxWidth);

    public static double ClampHeight(double h) =>
        Math.Clamp(double.IsNaN(h) ? DefaultHeight : h, MinHeight, HardMaxHeight);

    /// <summary>按给定上限（通常是屏幕工作区）夹取，用于"打开时别超出屏幕"。</summary>
    public static double ClampWidthTo(double w, double limit) =>
        Math.Clamp(double.IsNaN(w) ? DefaultWidth : w, MinWidth, Math.Max(MinWidth, Math.Min(limit, HardMaxWidth)));

    public static double ClampHeightTo(double h, double limit) =>
        Math.Clamp(double.IsNaN(h) ? DefaultHeight : h, MinHeight, Math.Max(MinHeight, Math.Min(limit, HardMaxHeight)));
}

/// <summary>
/// 板上可调的外观参数（字体大小与颜色、笔、橡皮）。
/// <para>★ 全部集中在这里定义上下限与步长：工具条按钮、诊断断言、配置夹取
/// 必须走同一套常量，否则「界面上调到 30、存回去变 24」这类错会反复出现。</para>
/// </summary>
public static class DoodleStyle
{
    public const double MinFontSize = 11;
    /// <summary>教室大屏后排要能看清，上限从 30 放到 88（约 A4 纸标题那么大）。</summary>
    public const double MaxFontSize = 88;
    public const double FontStep = 1;
    public const double DefaultFontSize = 13;

    /// <summary>大屏档位。与工具带 / 编辑窗共用同一张表，避免各处硬编不同数字。</summary>
    public static readonly double[] BigFontSizes = { 13, 18, 24, 32, 42, 56, 72, 88 };
    public static readonly string[] BigLevelNames = { "常规", "较大", "大", "特大", "超大", "巨大", "极大", "屏幕" };

    public static double BigSizeOf(int level) =>
        BigFontSizes[Math.Clamp(level, 0, BigFontSizes.Length - 1)];

    public static int BigIndexOf(double size)
    {
        for (int i = 0; i < BigFontSizes.Length; i++)
            if (size <= BigFontSizes[i] + 0.01) return i;
        return BigFontSizes.Length - 1;
    }

    /// <summary>板头字号档位上限：比便签再大一档，教室后排才看得清班级和时间。</summary>
    public const double MaxHeaderFontSize = 96;

    /// <summary>正文比标题小多少。太小会看不出层级，太大又显得碎。</summary>
    public const double BodyShrink = 1.5;

    public const double MinPenWidth = 2;
    public const double MaxPenWidth = 24;
    public const double PenStep = 2;
    public const double DefaultPenWidth = 4;

    public const double MinEraser = 8;
    public const double MaxEraser = 64;
    public const double EraserStep = 8;
    public const double DefaultEraser = 16;

    /// <summary>便签文字可选色（深色系，压在浅色便签上都够对比度）。</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> NoteColors = new List<(string, string)>
    {
        ("墨黑", "#25303D"),
        ("教育蓝", "#1E4FA3"),
        ("警示红", "#C0392B"),
        ("墨绿", "#1F7A4D"),
        ("棕", "#8A5A00"),
        ("灰", "#6B7280")
    };

    /// <summary>笔色可选色（前几个是粉笔色，最后一个深墨给白板用）。</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> PenColors = new List<(string, string)>
    {
        ("粉笔白", "#E9F1EA"),
        ("淡黄", "#F2D8A8"),
        ("粉红", "#F09595"),
        ("浅绿", "#9BD9A8"),
        ("浅蓝", "#9FC4F0"),
        ("深墨", "#25303D")
    };

    public static double ClampFont(double v) =>
        Math.Clamp(double.IsNaN(v) ? DefaultFontSize : v, MinFontSize, MaxFontSize);

    public static double ClampPen(double v) =>
        Math.Clamp(double.IsNaN(v) ? DefaultPenWidth : v, MinPenWidth, MaxPenWidth);

    public static double ClampEraser(double v) =>
        Math.Clamp(double.IsNaN(v) ? DefaultEraser : v, MinEraser, MaxEraser);

    /// <summary>字号 → 便签正文行高。1.55 是中文正文的舒适倍率，低于 1.4 会挤。</summary>
    public static double LineHeightFor(double fontSize) => Math.Round(fontSize * 1.55, 1);
}
