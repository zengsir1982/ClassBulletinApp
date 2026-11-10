using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

// 项目同时引用 System.Windows.Forms（托盘），Color / Brushes 在 System.Drawing 里也有同名类型，
// 必须显式别名，否则 CS0104 二义。
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace ClassBulletinApp;

/// <summary>
/// 周课程表配色（r79 重做）。
///
/// <para><b>为什么要有这一层。</b>改造前，一个格子里同时压着四种饱和色：学科墨黑、教师名深蓝
/// <c>#1E4FA3</c>、地点绿 <c>#2E8B57</c>，再加一枚饱和底白字的徽标；徽标本身还有紫 /
/// 靛 / 青绿 / 蓝 / 橙五种。7 列 × 8 节铺满一屏之后，观感就是「花花绿绿」。</para>
///
/// <para><b>收敛口径。</b>所有颜色只允许在「同一亮度带」里变化：
/// 底色固定 L≈95.5%、描边 L≈90.5%、字色 L≈31%，饱和度也锁在同一档，
/// 只有<b>色相</b>随学科变。这样即便整屏二十种学科，明暗节奏仍然一致，看上去是一套色板，
/// 而不是一堆互不相干的颜色。饱和度刻意压到 40% 上下 —— 教室投影 / 一体机低亮度屏上，
/// 高饱和的浅底会发灰发脏。</para>
///
/// <para><b>语义色只留三种。</b>调课 = 琥珀、代课 = 蓝、已调空 = 中性灰。
/// 来源（手动 / 临时课表导入 / 数据包同步）不再用颜色区分，改由悬停提示说明，
/// 避免同一个格子上出现第二种色系。</para>
/// </summary>
internal static class WeekPalette
{
    // 学科 → 色相（0..360）。只挑「相邻学科色相差 ≥15°」的取值，保证扫视时不糊成一片。
    static readonly Dictionary<string, double> HueOfSubject = new(StringComparer.Ordinal)
    {
        ["语文"] = 8,
        ["数学"] = 214,
        ["英语"] = 268,
        ["物理"] = 190,
        ["化学"] = 150,
        ["生物"] = 88,
        ["道德与法治"] = 32,
        ["政治"] = 32,
        ["道法"] = 32,
        ["思想品德"] = 32,
        ["思政"] = 32,
        ["历史"] = 348,
        ["地理"] = 168,
        ["体育"] = 105,
        ["体育与健康"] = 105,
        ["音乐"] = 300,
        ["美术"] = 330,
        ["信息科技"] = 205,
        ["信息技术"] = 205,
        ["电脑"] = 205,
        ["科学"] = 175,
        ["综合与实践"] = 42,
        ["综合实践"] = 42,
        ["劳动"] = 55,
        ["校本课程"] = 195,
        ["地方课程"] = 60,
        ["班会"] = 250,
        ["心理"] = 285,
        ["写字"] = 20,
        ["阅读"] = 230,
        ["自习"] = 220,
        ["早读"] = 15,
        ["午休"] = 210,
    };

    /// <summary>按长度倒序的前缀匹配表：让「体育与健康(男)」这类带后缀的写法也能命中。</summary>
    static readonly (string Key, double Hue)[] Prefixes = HueOfSubject
        .OrderByDescending(kv => kv.Key.Length)
        .Select(kv => (kv.Key, kv.Value))
        .ToArray();

    /// <summary>没登记过的学科按名字取一个稳定色相（不随进程变化，不能用 string.GetHashCode）。</summary>
    static readonly double[] FallbackHues =
        { 8, 32, 55, 88, 105, 150, 168, 190, 205, 214, 230, 250, 268, 285, 300, 330 };

    public sealed class Tint
    {
        public SolidColorBrush Bg = WpfBrushes.Transparent;      // 卡片底（极淡）
        public SolidColorBrush Border = WpfBrushes.Transparent;  // 卡片描边
        public SolidColorBrush Subject = WpfBrushes.Black;       // 学科名
        public SolidColorBrush Teacher = WpfBrushes.Gray;        // 任课教师 / 所教班级
        public SolidColorBrush Minor = WpfBrushes.Gray;          // 地点等次要信息
    }

    public sealed class Badge
    {
        public Badge(SolidColorBrush bg, SolidColorBrush fg) { Bg = bg; Fg = fg; }
        public SolidColorBrush Bg { get; }
        public SolidColorBrush Fg { get; }
    }

    static readonly Dictionary<string, Tint> Cache = new(StringComparer.Ordinal);

    /// <summary>取某个学科的整套颜色（带缓存 —— 每 30 秒重建一次，不缓存会持续产生垃圾）。</summary>
    public static Tint For(string? subject)
    {
        string key = (subject ?? "").Trim();
        if (key.Length == 0) key = "　";      // 空格子也走缓存，避免每次新建透明刷

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;

            double h = HueOf(key);
            var tint = key == "　"
                ? new Tint
                {
                    Bg = WpfBrushes.Transparent,
                    Border = WpfBrushes.Transparent,
                    Subject = Brush(Hsl(h, 0.00, 0.45)),
                    Teacher = Brush(Hsl(h, 0.00, 0.55)),
                    Minor = Brush(Hsl(h, 0.00, 0.62)),
                }
                : new Tint
                {
                    Bg = Brush(Hsl(h, 0.42, 0.955)),
                    Border = Brush(Hsl(h, 0.34, 0.905)),
                    Subject = Brush(Hsl(h, 0.45, 0.310)),
                    Teacher = Brush(Hsl(h, 0.30, 0.460)),
                    Minor = Brush(Hsl(h, 0.24, 0.540)),
                };

            Cache[key] = tint;
            return tint;
        }
    }

    /// <summary>调代课徽标：只按类型着色（调=琥珀 / 代=蓝 / 已调空=中性灰），不再按来源换色系。</summary>
    public static Badge BadgeOf(string? type, bool isEmpty)
    {
        if (isEmpty) return new Badge(Brush(Hsl(215, 0.14, 0.940)), Brush(Hsl(215, 0.13, 0.420)));

        bool substitute = string.Equals((type ?? "").Trim(), "代", StringComparison.Ordinal);
        return substitute
            ? new Badge(Brush(Hsl(214, 0.62, 0.930)), Brush(Hsl(214, 0.62, 0.330)))
            : new Badge(Brush(Hsl(32, 0.72, 0.920)), Brush(Hsl(32, 0.74, 0.310)));
    }

    /// <summary>学科 → 色相。先查表，再按前缀匹配，最后用与进程无关的稳定散列。</summary>
    public static double HueOf(string? subject)
    {
        string s = (subject ?? "").Trim();
        if (s.Length == 0) return 214;
        if (HueOfSubject.TryGetValue(s, out double exact)) return exact;

        foreach (var (key, hue) in Prefixes)
            if (s.StartsWith(key, StringComparison.Ordinal)) return hue;

        // ★ 不能用 string.GetHashCode()：.NET Core 起它按进程随机化，
        //   同一个「编程」今天深蓝明天可能变粉，重建一次就跳色。
        int acc = 0;
        foreach (char c in s) acc = (acc * 31 + c) & 0x7FFFFFFF;
        return FallbackHues[acc % FallbackHues.Length];
    }

    /// <summary>HSL → RGB（WPF 没有 HSL 构造器）。h: 0..360，s/l: 0..1。</summary>
    static WpfColor Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));

        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        double m = l - c / 2;
        return WpfColor.FromRgb(
            (byte)Math.Round(Math.Clamp(r + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b + m, 0, 1) * 255));
    }

    static SolidColorBrush Brush(WpfColor c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();          // 冻结后跨线程复用零成本，也不会有 Dispatcher 归属问题
        return b;
    }
}
