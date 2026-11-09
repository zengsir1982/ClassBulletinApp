using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ScheduleCore.Models;

/// <summary>
/// 作业 / 缺交条目的「同步身份」字段。
/// <para>★ 为「办公室机 ⇄ 教室机」的结构化变更单与双向同步预留：远端指令必须能指到唯一条目，
/// 否则只能整表覆盖，教室机本地改动会被抹掉。所有字段对老数据都是可缺省的 ——
/// 老 JSON 里没有这些键时反序列化得到默认值，渲染行为完全不变。</para>
/// <para>★ 老程序读新 JSON 会忽略未知字段，一旦保存就会把 Id / 墓碑抹掉，
/// 因此启用同步的两端必须同时升级（见发布说明）。</para>
/// </summary>
public abstract class BoardItemBase
{
    /// <summary>稳定 Id（确定性派生，两端各自迁移无 Id 老数据也会得到同一个值）。空 = 尚未迁移。</summary>
    public string Id { get; set; } = "";

    /// <summary>软删除墓碑。物理删除会让对端一条晚到的旧字段 op 把条目「复活」。</summary>
    public bool Deleted { get; set; }

    /// <summary>来源：local = 本机编辑；teacher = 教师机变更单；用于信息管理器显示来源标签。</summary>
    public string Origin { get; set; } = "local";

    /// <summary>派生关系：「增加（两条都留）」产生的新条目指回原条目 Id 与变更单。</summary>
    public string Parent { get; set; } = "";

    /// <summary>字段级版本（字段名 → 变更单 Id / HLC），墓碑必须保留全部字段版本。</summary>
    public Dictionary<string, string> FieldHlc { get; set; } = new();
}

/// <summary>
/// 确定性 Id 与老数据迁移。算法与阶段 0 验证程序 probe.py 的 stable_id 一致：
/// <c>prefix + sha256(classKey|归一科目|内容)[:16]</c>。
/// <para>为什么不用 Guid：两端各自给同一份老数据生成随机 Id 会得到两个不同 Id，
/// 合并后变成重复行。确定性 Id 让两端独立迁移也能对齐。</para>
/// </summary>
public static class BoardIds
{
    public static string NormalizeSubject(string? s)
        => string.Concat((s ?? "").Replace('\u3000', ' ').Where(c => !char.IsWhiteSpace(c)));

    public static string Compute(string prefix, string classKey, string? subject, string? content)
    {
        string raw = $"{classKey ?? ""}|{NormalizeSubject(subject)}|{(content ?? "").Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return prefix + "-" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    /// <summary>
    /// 给一批条目补齐 Id（幂等：已有 Id 一律不动）。完全相同内容的条目按出现次序追加 #2/#3，
    /// 保证 Id 唯一且两端算出的结果一致（两端老数据顺序相同）。返回是否发生了改动。
    /// </summary>
    public static bool Ensure<T>(IEnumerable<T> items, string prefix, string classKey,
                                 Func<T, string?> subject, Func<T, string?> content,
                                 Action<T, string> setId) where T : BoardItemBase
    {
        bool changed = false;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in items)
            if (!string.IsNullOrEmpty(it.Id)) used.Add(it.Id);

        foreach (var it in items)
        {
            if (!string.IsNullOrEmpty(it.Id)) continue;
            string baseId = Compute(prefix, classKey, subject(it), content(it));
            string id = baseId;
            int n = 2;
            while (!used.Add(id)) id = baseId + "#" + n++;
            setId(it, id);
            changed = true;
        }
        return changed;
    }
}

/// <summary>
/// 单科作业条目。
/// <para>实现 <see cref="INotifyPropertyChanged"/>：编辑窗口里改格式（加粗 / 颜色 / 字号）后，
/// 行内输入框的即时预览与桌面背景能立刻跟着变，不必关掉重开。</para>
/// </summary>
public class HomeworkItem : BoardItemBase, INotifyPropertyChanged
{
    private string _subject = "";
    private string _content = "";

    public HomeworkItem()
    {
        // 默认「特大」：标题 / 科目 / 内容都按这一档渲染，教室后排也看得清。
        // 只设默认值，不挡用户改：JSON 里有 sizeLevel 时反序列化会覆盖它，
        // 老数据里仍是标准档的由 Store.UpgradeBoardFontDefaults 一次性升级。
        ContentStyle.SizeLevel = HwTextStyle.ExtraLargeLevel;
        SubjectStyle.SizeLevel = HwTextStyle.ExtraLargeLevel;

        // 正文样式对象内部变化时，把属性名原样转发出去 ——
        // 编辑窗里绑定 {Binding Bold} / {Binding Color} 的输入框才能即时预览。
        ContentStyle.PropertyChanged += (_, e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
        SubjectStyle.PropertyChanged += (_, e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Subject" + e.PropertyName));
    }

    public string Subject { get => _subject; set => Set(ref _subject, value); }
    public string Content { get => _content; set => Set(ref _content, value); }

    /// <summary>
    /// 作业内容的样式（加粗 / 颜色 / 字号档位）。
    /// <para>★ 不参与 JSON 序列化：外面的 <see cref="Bold"/> / <see cref="Color"/> / <see cref="SizeLevel"/>
    /// 是它的代理属性，序列化后仍是原来的平铺三个字段，老数据包与老版本双向兼容。</para>
    /// </summary>
    [JsonIgnore]
    public TextStyle ContentStyle { get; set; } = new();

    /// <summary>作业内容是否加粗。</summary>
    public bool Bold { get => ContentStyle.Bold; set => ContentStyle.Bold = value; }

    /// <summary>作业内容颜色（#RRGGBB；空表示默认墨色，实际取主题正文色）。</summary>
    public string Color { get => ContentStyle.Color; set => ContentStyle.Color = value ?? ""; }

    /// <summary>作业内容字号档位（见 <see cref="HwTextStyle.LevelNames"/>）。</summary>
    public int SizeLevel { get => ContentStyle.SizeLevel; set => ContentStyle.SizeLevel = value; }

    /// <summary>
    /// 科目名（「【语文】」这几个字）的独立样式：字号 / 颜色 / 加粗。
    /// 为空（老数据）时按「加粗 + 主题强调色 + 标准档」渲染，与加这个功能之前完全一致。
    /// </summary>
    public TextStyle SubjectStyle { get; set; } = new() { Bold = true };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 作业 / 缺交 / 自定义便签共用的文本样式档位。
/// <para>★ 字号档位是持久化值（SizeLevel 存的就是数组下标），只能在末尾追加，不能重排或删除，
/// 否则老数据（users.json / homework.json 里的 sizeLevel）会变成别的字号。</para>
/// </summary>
public static class HwTextStyle
{
    /// <summary>
    /// 字号档位对应的基准字号（按档位索引）。前四个是历史档位，顺序不可动；
    /// 4 起的「更小 / 超大 / 巨大」是后加的，只追加不动前面。
    /// </summary>
    public static readonly double[] Sizes = { 19, 16, 23, 27, 14, 32, 38 };

    /// <summary>取指定档位的字号（越界自动收敛到合法范围）。</summary>
    public static double SizeOf(int level) => Sizes[Math.Clamp(level, 0, Sizes.Length - 1)];

    /// <summary>档位名称（下标即档位）。</summary>
    public static readonly string[] LevelNames = { "标准", "小", "大", "特大", "更小", "超大", "巨大" };

    /// <summary>
    /// 「特大」档下标。作业公示 / 缺交名单的标题、科目、内容 / 名单默认都用这一档，
    /// 保证教室后排也看得清。★ 只改默认取值，绝不重排 <see cref="Sizes"/> / <see cref="LevelNames"/>
    /// （它们是持久化下标，重排会让老数据变成别的字号）。
    /// </summary>
    public const int ExtraLargeLevel = 3;

    /// <summary>下拉框的显示顺序：按字号从小到大排列的档位下标（持久化仍用档位本身）。</summary>
    public static readonly int[] SizeOrder = { 4, 1, 0, 2, 3, 5, 6 };

    /// <summary>档位下标 → 在 <see cref="SizeOrder"/> 里的位置；找不到返回 0。</summary>
    public static int OrderIndexOf(int level)
    {
        int i = Array.IndexOf(SizeOrder, Math.Clamp(level, 0, Sizes.Length - 1));
        return i < 0 ? 0 : i;
    }

    /// <summary>预设颜色（值 / 显示名），悬浮窗下拉与桌面背景渲染共用。前六项为历史值，不要改。</summary>
    public static readonly (string Value, string Name)[] PresetColors =
    {
        ("", "默认墨色"),
        ("#C0392B", "红色"),
        ("#2D6CDF", "蓝色"),
        ("#1E8E3E", "绿色"),
        ("#E07B00", "橙色"),
        ("#7B3FBF", "紫色"),
        ("#0F766E", "青色"),
        ("#1E40AF", "深蓝"),
        ("#C2185B", "玫红"),
        ("#795548", "棕色"),
        ("#4B5563", "灰色"),
        ("#000000", "黑色")
    };

    /// <summary>颜色值 → 显示名；不在预设里时返回 hex 本身（自定义颜色）。</summary>
    public static string ColorName(string? hex)
    {
        var v = (hex ?? "").Trim();
        foreach (var c in PresetColors)
            if (string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase)) return c.Name;
        return string.IsNullOrEmpty(v) ? PresetColors[0].Name : v.ToUpperInvariant();
    }
}

/// <summary>
/// 作业公示的便签主题风格：决定便签底色、正文色、标题 / 科目强调色、边框与顶部条配色。
/// 悬浮便签与桌面背景卡片共用同一套主题，保证两处观感一致。
/// </summary>
public class HomeworkTheme
{
    /// <summary>主题标识（持久化用）。</summary>
    public string Key { get; set; } = "";

    /// <summary>主题显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>便签底色。</summary>
    public string Background { get; set; } = "#FFF9C4";

    /// <summary>正文文字色。</summary>
    public string Foreground { get; set; } = "#4A3F1A";

    /// <summary>科目 / 强调色。</summary>
    public string Accent { get; set; } = "#A17400";

    /// <summary>总标题色。</summary>
    public string TitleColor { get; set; } = "#8D6E00";

    /// <summary>卡片 / 便签边框色。</summary>
    public string BorderColor { get; set; } = "#F0DFA0";

    /// <summary>顶部条背景色。</summary>
    public string HeaderBackground { get; set; } = "#FFE082";

    /// <summary>顶部条文字色。</summary>
    public string HeaderForeground { get; set; } = "#5D4037";

    /// <summary>缺交名单强调色。</summary>
    public string MissingColor { get; set; } = "#C0392B";

    /// <summary>次要文字（更新时间等）颜色。</summary>
    public string MutedColor { get; set; } = "#A39464";
}

/// <summary>作业公示预设主题（便签风格）。</summary>
public static class HomeworkThemes
{
    public static readonly IReadOnlyList<HomeworkTheme> Presets = new List<HomeworkTheme>
    {
        new() { Key = "classic", Name = "经典便签黄",
                Background = "#FFF9C4", Foreground = "#4A3F1A", Accent = "#A17400", TitleColor = "#8D6E00",
                BorderColor = "#F0DFA0", HeaderBackground = "#FFE082", HeaderForeground = "#5D4037",
                MissingColor = "#C0392B", MutedColor = "#A39464" },

        new() { Key = "blue", Name = "清新蓝",
                Background = "#E3F2FD", Foreground = "#1A3A5C", Accent = "#1565C0", TitleColor = "#0D47A1",
                BorderColor = "#BBDEFB", HeaderBackground = "#BBDEFB", HeaderForeground = "#0D47A1",
                MissingColor = "#C0392B", MutedColor = "#7A9AB5" },

        new() { Key = "pink", Name = "少女粉",
                Background = "#FCE4EC", Foreground = "#5C2A3A", Accent = "#C2185B", TitleColor = "#AD1457",
                BorderColor = "#F8BBD0", HeaderBackground = "#F8BBD0", HeaderForeground = "#AD1457",
                MissingColor = "#C62828", MutedColor = "#B08A96" },

        new() { Key = "green", Name = "护眼绿",
                Background = "#E8F5E9", Foreground = "#1B3A20", Accent = "#2E7D32", TitleColor = "#1B5E20",
                BorderColor = "#C8E6C9", HeaderBackground = "#C8E6C9", HeaderForeground = "#1B5E20",
                MissingColor = "#B71C1C", MutedColor = "#7A9A7E" },

        new() { Key = "kraft", Name = "牛皮纸",
                Background = "#EFE0C8", Foreground = "#4A3728", Accent = "#8D6E63", TitleColor = "#5D4037",
                BorderColor = "#D7C4A3", HeaderBackground = "#D7C4A3", HeaderForeground = "#4A3728",
                MissingColor = "#A02B2B", MutedColor = "#A08D76" },

        new() { Key = "dark", Name = "暗夜黑板",
                Background = "#2B3038", Foreground = "#E6EAF2", Accent = "#7FB3FF", TitleColor = "#FFFFFF",
                BorderColor = "#454C57", HeaderBackground = "#363C46", HeaderForeground = "#FFFFFF",
                MissingColor = "#FF8A80", MutedColor = "#8A93A1" },

        new() { Key = "plain", Name = "素雅白",
                Background = "#FFFFFF", Foreground = "#333B47", Accent = "#2D6CDF", TitleColor = "#1F2A37",
                BorderColor = "#D8DEE9", HeaderBackground = "#EAF1FD", HeaderForeground = "#1F2A37",
                MissingColor = "#C0392B", MutedColor = "#B0B6BF" }
    };

    /// <summary>按标识取主题；未知或为空时回退到「经典便签黄」。</summary>
    public static HomeworkTheme Find(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var hit = Presets.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return Presets[0];
    }

    /// <summary>取主题在预设列表中的下标（用于下拉选中）。</summary>
    public static int IndexOf(string? key)
    {
        for (int i = 0; i < Presets.Count; i++)
            if (string.Equals(Presets[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}

/// <summary>
/// 作业公示图在桌面壁纸上的摆放方式：
/// Anchor 为九宫格锚点，OffsetX / OffsetY 为相对锚点的像素偏移（右、下为正），
/// WidthRatio 为卡片宽度占屏幕宽度的比例；开启 UseFixedSize 后改用固定像素尺寸，
/// 卡片位置不再随作业条数多少而漂移，定位更稳定。
/// </summary>
public class HomeworkWallpaperLayout
{
    /// <summary>
    /// 九宫格锚点：TopLeft / TopCenter / TopRight / MiddleLeft / MiddleCenter /
    /// MiddleRight / BottomLeft / BottomCenter / BottomRight（兼容旧值 Right = MiddleRight）。
    /// </summary>
    public string Anchor { get; set; } = "MiddleRight";

    /// <summary>水平偏移（像素，向右为正）。</summary>
    public double OffsetX { get; set; }

    /// <summary>垂直偏移（像素，向下为正）。</summary>
    public double OffsetY { get; set; }

    /// <summary>卡片宽度占屏幕宽度的比例 0.25~0.9。</summary>
    public double WidthRatio { get; set; } = 0.4;

    /// <summary>
    /// 是否使用固定像素大小的作业公示板。开启后宽度 / 高度固定，
    /// 卡片位置不会随作业条数多少而漂移，定位更稳定可靠。
    /// </summary>
    public bool UseFixedSize { get; set; }

    /// <summary>固定宽度（像素，0 表示仍按屏幕比例）。</summary>
    public double FixedWidth { get; set; } = 420;

    /// <summary>固定高度（像素，0 表示高度随内容自适应）。</summary>
    public double FixedHeight { get; set; } = 560;

    /// <summary>
    /// 卡片宽度是否跟着内容走（<see cref="FixedWidth"/> 只当上限），高度也随内容。
    /// 倒计时卡默认 false：宽度由用户在排版编辑器里拖动决定（取消自适应收窄）。
    /// <para>null = 用各卡片的默认值（倒计时默认自适应：名字和天数紧挨着排，卡片收窄才像一块整体）；
    /// 用户在排版设置里亲手拖过大小后会写成 false，尊重他定的尺寸。</para>
    /// </summary>
    public bool? FitContent { get; set; }
}

/// <summary>单条缺交作业记录：可指定科目（留空表示通用），以及缺交学生姓名（换行 / 逗号分隔）。</summary>
public class MissingHomeworkItem : BoardItemBase, INotifyPropertyChanged
{
    private string _subject = "";
    private string _students = "";

    public MissingHomeworkItem()
    {
        // 默认「特大」：科目标题 / 名单都按这一档渲染（与作业公示一致）。
        ContentStyle.SizeLevel = HwTextStyle.ExtraLargeLevel;
        SubjectStyle.SizeLevel = HwTextStyle.ExtraLargeLevel;

        ContentStyle.PropertyChanged += (_, e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
        SubjectStyle.PropertyChanged += (_, e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Subject" + e.PropertyName));
    }

    /// <summary>科目（留空表示通用 / 不区分科目）。</summary>
    public string Subject { get => _subject; set => Set(ref _subject, value); }

    /// <summary>缺交学生姓名：每行一个或用逗号 / 顿号分隔均可。</summary>
    public string Students { get => _students; set => Set(ref _students, value); }

    /// <summary>名单样式（加粗 / 颜色 / 字号）；不参与序列化，外面三个属性是它的代理。</summary>
    [JsonIgnore]
    public TextStyle ContentStyle { get; set; } = new();

    /// <summary>名单是否加粗。</summary>
    public bool Bold { get => ContentStyle.Bold; set => ContentStyle.Bold = value; }

    /// <summary>名单颜色（#RRGGBB；空表示默认墨色）。</summary>
    public string Color { get => ContentStyle.Color; set => ContentStyle.Color = value ?? ""; }

    /// <summary>名单字号档位（见 <see cref="HwTextStyle.LevelNames"/>）。</summary>
    public int SizeLevel { get => ContentStyle.SizeLevel; set => ContentStyle.SizeLevel = value; }

    /// <summary>科目名（「【语文】」）的独立样式：字号 / 颜色 / 加粗；空 = 加粗 + 主题强调色 + 标准档。</summary>
    public TextStyle SubjectStyle { get; set; } = new() { Bold = true };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>作业公示栏内容：标题 + 各科目作业 + 主题风格 + 桌面背景摆放位置。</summary>
public class HomeworkBoard
{
    /// <summary>所属班级键（按班分片同步用）。老数据为空，迁移时按当前显示班级补。</summary>
    public string ClassKey { get; set; } = "";

    /// <summary>存储格式代次：0 = 无 Id 的老格式；1 = 已带稳定 Id / 墓碑。</summary>
    public int SchemaRev { get; set; }

    /// <summary>可见条目（排除软删除墓碑）。渲染 / 统计一律用这个，别直接遍历 Items。</summary>
    [JsonIgnore]
    public IEnumerable<HomeworkItem> LiveItems => (Items ?? new()).Where(x => !x.Deleted);

    /// <summary>可见条目数。<b>必须 JsonIgnore</b>：它是计算值，写进 JSON 会变成
    /// 「看起来能改、其实下次读盘又算回去」的假字段。</summary>
    [JsonIgnore]
    public int LiveCount => (Items ?? new()).Count(x => !x.Deleted);

    public string Title { get; set; } = "各科作业公示";

    /// <summary>总标题（顶部条那行字）的样式：字号 / 颜色 / 加粗；空 = 主题标题色 + 加粗 + 特大档。</summary>
    public TextStyle TitleStyle { get; set; } = new() { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };

    public List<HomeworkItem> Items { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 旧版缺交作业名单（已独立为 MissingBoard，单独悬浮便签 + 单独桌面便签）。
    /// 保留字段仅为兼容老数据：首次读取 missing.json 时会自动迁移过去，此后不再参与渲染。
    /// </summary>
    public List<MissingHomeworkItem> Missing { get; set; } = new();

    /// <summary>便签主题风格标识（见 HomeworkThemes.Presets），为空时为经典便签黄。</summary>
    public string Theme { get; set; } = "classic";

    /// <summary>作为桌面壁纸时的显示位置与大小。</summary>
    public HomeworkWallpaperLayout Layout { get; set; } = new();
}

/// <summary>
/// 缺交作业名单（独立便签）：拥有独立的主题风格、独立的悬浮便签与独立的桌面背景便签，
/// 不再挤在作业公示卡片里，避免作业条数变化时把它顶出可视区域。
/// </summary>
public class MissingBoard
{
    /// <summary>所属班级键（按班分片同步用）。老数据为空，迁移时按当前显示班级补。</summary>
    public string ClassKey { get; set; } = "";

    /// <summary>存储格式代次：0 = 无 Id 的老格式；1 = 已带稳定 Id / 墓碑。</summary>
    public int SchemaRev { get; set; }

    /// <summary>可见条目（排除软删除墓碑）。渲染 / 统计一律用这个，别直接遍历 Items。</summary>
    [JsonIgnore]
    public IEnumerable<MissingHomeworkItem> LiveItems => (Items ?? new()).Where(x => !x.Deleted);

    /// <summary>可见条目数。<b>必须 JsonIgnore</b>：它是计算值，写进 JSON 会变成
    /// 「看起来能改、其实下次读盘又算回去」的假字段。</summary>
    [JsonIgnore]
    public int LiveCount => (Items ?? new()).Count(x => !x.Deleted);

    public string Title { get; set; } = "缺交作业名单";

    /// <summary>总标题的样式：字号 / 颜色 / 加粗；空 = 主题标题色 + 加粗 + 特大档。</summary>
    public TextStyle TitleStyle { get; set; } = new() { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>各科目缺交记录（科目可留空表示通用）。</summary>
    public List<MissingHomeworkItem> Items { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>便签主题风格标识（见 HomeworkThemes.Presets）。</summary>
    public string Theme { get; set; } = "pink";
}

/// <summary>
/// 自定义便签：用户自建的自由内容便签（如教师寄语、温馨提示等），可同时存在多张。
/// 内容与作业 / 缺交便签无关，只有标题 + 自由文本 + 主题风格 + 锁定；
/// 悬浮窗的摆放、置顶、锁定、是否显示都随便签一起持久化，便于独立管理。
/// </summary>
public class CustomNote
{
    /// <summary>便签唯一标识（用于定位悬浮窗 / 同步显示开关）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>便签标题（显示在顶部条）。</summary>
    public string Title { get; set; } = "自定义便签";

    /// <summary>标题的样式：字号 / 颜色 / 加粗；空 = 主题标题色 + 加粗 + 默认墨色。</summary>
    // r69：默认「特大」；老数据里的 sizeLevel 会覆盖这个默认值。
    public TextStyle TitleStyle { get; set; } = new() { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>便签正文（支持多行）。</summary>
    public string Content { get; set; } = "";

    /// <summary>正文样式（加粗 / 颜色 / 字号）；不参与序列化，下面三个属性是它的代理，JSON 格式保持不变。</summary>
    [JsonIgnore]
    // r69：新建便签正文默认「特大」；老数据里的 sizeLevel 会覆盖。
    public TextStyle ContentStyle { get; set; } = new() { SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>正文是否加粗。</summary>
    public bool Bold { get => ContentStyle.Bold; set => ContentStyle.Bold = value; }

    /// <summary>正文颜色（#RRGGBB；空表示随主题正文色）。</summary>
    public string Color { get => ContentStyle.Color; set => ContentStyle.Color = value ?? ""; }

    /// <summary>正文字号档位：0=标准 1=小 2=大 3=特大（与作业公示共用 HwTextStyle）。</summary>
    public int SizeLevel { get => ContentStyle.SizeLevel; set => ContentStyle.SizeLevel = value; }

    /// <summary>便签主题风格标识（见 HomeworkThemes.Presets）。</summary>
    public string Theme { get; set; } = "plain";

    /// <summary>是否锁定（锁定后不能移动 / 缩放 / 编辑，防误操作）。</summary>
    public bool Locked { get; set; }

    /// <summary>最近更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>悬浮窗左上角 X（逻辑像素，null = 首次打开自动居中）。</summary>
    public double? Left { get; set; }

    /// <summary>悬浮窗左上角 Y（逻辑像素）。</summary>
    public double? Top { get; set; }

    /// <summary>悬浮窗宽度（逻辑像素，0 = 默认宽度）。</summary>
    public double? Width { get; set; }

    /// <summary>悬浮窗高度（逻辑像素，0 = 默认高度）。</summary>
    public double? Height { get; set; }

    /// <summary>是否置顶显示（默认置于普通层级，仅在点击「置顶」后才置顶）。</summary>
    public bool TopMost { get; set; }

    /// <summary>是否在屏幕上显示（控制面板 / 托盘可切换）。</summary>
    public bool Visible { get; set; } = true;
}
