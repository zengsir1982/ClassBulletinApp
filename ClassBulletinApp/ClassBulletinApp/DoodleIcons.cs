using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
// 项目同时引用了 WPF 与 System.Drawing（同名类型极多），别名消歧义，避免 CS0104
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Pen = System.Windows.Media.Pen;

namespace ClassBulletinApp;

/// <summary>
/// 涂鸦板工具条的线稿图标。
/// <para>★ 一律用 <see cref="Path"/> 画（24×24 逻辑坐标 + Uniform 缩放），不用图片也不用字体图标：
/// 图片随主题换色要重新出图，字体图标在教室一体机上可能缺字形显示成方框，
/// 而 Path 只改 Stroke 就能跟主题走，任意尺寸都不糊。</para>
/// </summary>
internal static class DoodleIcons
{
    // 24×24 网格上画的线稿，统一 1.8 描边 + 圆头圆角，远看比细线清楚
    private const string PenData =
        "M 4.2 19.8 L 5.9 14.9 L 16.4 4.4 L 19.6 7.6 L 9.1 18.1 Z " +
        "M 5.9 14.9 L 9.1 18.1 " +
        "M 14.6 6.2 L 17.8 9.4";

    // r99：按老师给的参考图重画 —— 一块斜 45° 的圆角橡皮，
    // ① 外轮廓（顶部尖角 / 右侧 / 底部擦面 / 左下圆角）；② 一条斜切线切出左下纸套；
    // ③ 右下一条水平擦痕；④ 左下两粒橡皮屑。整体比原来的平行四边形更像真实橡皮。
    private const string EraserData =
        "M 11.6 2.2 L 20.8 11.4 Q 22.0 12.6 20.8 13.8 L 13.8 20.8 " +
        "Q 12.6 22.0 11.4 20.8 L 3.2 12.6 Q 2.0 11.4 3.2 10.2 L 10.2 3.2 " +
        "Q 11.4 2.0 11.6 2.2 Z " +
        "M 5.8 9.2 L 12.4 15.8 " +
        "M 14.6 20.8 H 22.6 " +
        "M 1.8 20.0 L 3.8 20.0 " +
        "M 3.0 22.6 L 4.4 21.2";

    private const string TrashData =
        "M 4.5 7.2 H 19.5 " +
        "M 9.4 7.2 V 4.4 H 14.6 V 7.2 " +
        "M 6.4 7.2 L 7.5 19.8 H 16.5 L 17.6 7.2 " +
        "M 10.2 11.2 V 16.2 " +
        "M 13.8 11.2 V 16.2";

    // 三条带旋钮的横线 —— 比齿轮更像"可调的参数"，也不会在 22px 下糊成一团
    private const string SlidersData =
        "M 3.8 7.0 H 20.2 " +
        "M 9.0 4.2 V 9.8 " +
        "M 3.8 12.0 H 20.2 " +
        "M 15.4 9.2 V 14.8 " +
        "M 3.8 17.0 H 20.2 " +
        "M 7.2 14.2 V 19.8";

    // 三点竖排：22px 下唯一不会糊的"更多/设置"语义。滑块图标在这个尺寸下
    // 三条横线会和穿过它们的短竖线挤成"井"字，远看像乱码。
    private const string MoreData =
        "M 12 5.0 m -2.2 0 a 2.2 2.2 0 1 0 4.4 0 a 2.2 2.2 0 1 0 -4.4 0 " +
        "M 12 12.0 m -2.2 0 a 2.2 2.2 0 1 0 4.4 0 a 2.2 2.2 0 1 0 -4.4 0 " +
        "M 12 19.0 m -2.2 0 a 2.2 2.2 0 1 0 4.4 0 a 2.2 2.2 0 1 0 -4.4 0";

    private const string CloseData = "M 6.4 6.4 L 17.6 17.6 M 17.6 6.4 L 6.4 17.6";

    private const string MinusData = "M 5.4 12 H 18.6";

    private const string PlusData = "M 12 5.4 V 18.6 M 5.4 12 H 18.6";

    // 鼠标箭头：回到「选择 / 便签」层，笔画收起。轮廓比实心在深色主题下更清楚
    private const string SelectData =
        "M 5.8 3.4 L 5.8 18.6 L 10.0 14.7 L 13.0 20.6 L 16.0 19.2 L 13.1 13.4 L 19.0 13.2 Z";

    // 板面翻页：粗一点的箭头，教室后排也能看出方向
    private const string PrevData = "M 15.0 4.6 L 7.4 12.0 L 15.0 19.4";
    private const string NextData = "M 9.0 4.6 L 16.6 12.0 L 9.0 19.4";

    // 撤销（r107）：左指向箭头 + 一条回绕的尾巴，比「转一圈的圆箭头」在 24px 下更清楚
    private const string UndoData =
        "M 8.8 4.8 L 3.8 9.8 L 8.8 14.8 " +
        "M 3.8 9.8 H 13.0 " +
        "C 17.4 9.8 20.0 12.6 20.0 16.0 C 20.0 19.4 17.2 20.6 14.2 20.6";

    // 选择 / 上一页 / 下一页 / 笔 / 橡皮 / 撤销 / 清空 / 设置（调节）/ 关闭 / 减 / 加
    public static Path Select(Brush stroke) => Make(SelectData, stroke);
    public static Path Prev(Brush stroke) => Make(PrevData, stroke);
    public static Path Next(Brush stroke) => Make(NextData, stroke);
    public static Path Pen(Brush stroke) => Make(PenData, stroke);
    public static Path Undo(Brush stroke) => Make(UndoData, stroke);

    /// <summary>橡皮单独加粗一点：参考图是粗线稿，2.0 描边在 22px 下显得太细。</summary>
    public static Path Eraser(Brush stroke)
    {
        var p = Make(EraserData, stroke, 24);
        p.StrokeThickness = 2.2;
        return p;
    }
    public static Path Trash(Brush stroke) => Make(TrashData, stroke);
    public static Path Sliders(Brush stroke) => Make(SlidersData, stroke);

    /// <summary>三点（设置 / 更多）。实心圆，不是线稿，所以走 Fill。</summary>
    public static Path More(Brush brush) => MakeFilled(MoreData, brush);
    public static Path Close(Brush stroke) => Make(CloseData, stroke);
    public static Path Minus(Brush stroke) => Make(MinusData, stroke);
    public static Path Plus(Brush stroke) => Make(PlusData, stroke);

    /// <summary>
    /// 造一个线稿图标。几何本身不随颜色变，但 Brush 不是 Freezable 的共享常量，
    /// 所以每个图标各自持有一份 —— 换主题时要逐个改 Stroke。
    /// </summary>
    private static Path Make(string data, Brush stroke, double size = 22)
    {
        var geo = Geometry.Parse(data);
        geo.Freeze();

        var p = new Path
        {
            Data = geo,
            Stroke = stroke,
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            SnapsToDevicePixels = false
        };
        return p;
    }

    private static Path MakeFilled(string data, Brush brush, double size = 22)
    {
        var geo = Geometry.Parse(data);
        geo.Freeze();

        return new Path
        {
            Data = geo,
            Fill = brush,
            Stroke = null,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

    /// <summary>
    /// 色点：一个实心圆 + 可选的外环（选中态）。
    /// <para>★ 选中不能只靠"圆变大"，一体机远看几个像素的差别看不出来，必须给一圈描边。</para>
    /// </summary>
    public static FrameworkElement Swatch(string hex, bool selected, Brush ringBrush, double dot = 20,
        Brush? outline = null)
    {
        var host = new System.Windows.Controls.Grid
        {
            Width = 30,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        if (selected)
        {
            host.Children.Add(new Ellipse
            {
                Width = 28,
                Height = 28,
                Stroke = ringBrush,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            });
        }

        host.Children.Add(new Ellipse
        {
            Width = dot,
            Height = dot,
            Fill = Solid(hex),
            // ★ 深色板上的深墨点会整块融进底色：必须靠一圈"与板面对比"的描边托出轮廓，
            // 描边色由调用方按板面明暗给（深板给浅描边、浅板给深描边）。
            Stroke = outline ?? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            StrokeThickness = 1
        });

        return host;
    }

    public static SolidColorBrush Solid(string hex)
    {
        var b = new SolidColorBrush(Parse(hex));
        b.Freeze();
        return b;
    }

    /// <summary>把 "#RRGGBB" 安全地变成颜色；写错了回落到深墨，绝不抛。</summary>
    public static Color Parse(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { /* 手改坏的色值不该让整块板打不开 */ }
        return Color.FromRgb(0x25, 0x30, 0x3D);
    }

    /// <summary>图标的描边笔（备用：需要给 Path 统一换 Pen 而非 Stroke 时用）。</summary>
    public static Pen StrokePen(Brush b) => new(b, 1.8)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };
}
