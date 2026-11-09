using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ScheduleCore.Models;

/// <summary>
/// 值日生名单便签：按星期列出每天的值日安排，默认高亮「今天」。
/// 风格与作业/缺交便签一致（主题 + 标题样式 + 内容样式），可悬浮、可设为桌面背景。
/// </summary>
public class DutyBoard
{
    /// <summary>所属班级键（按班分片同步用）。老数据为空，与作业 / 缺交的行为完全一致。</summary>
    public string ClassKey { get; set; } = "";

    /// <summary>存储格式代次：0 = 无 Id 的老格式；1 = 已带稳定 Id / 墓碑。</summary>
    public int SchemaRev { get; set; }

    /// <summary>可见条目（排除软删除墓碑）。渲染 / 统计一律用这个，别直接遍历 Items。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<DutyItem> LiveItems => (Items ?? new List<DutyItem>()).Where(x => !x.Deleted);

    /// <summary>可见条目数。<b>必须 JsonIgnore</b>：计算值写进 JSON 会变成「看起来能改、其实读盘又算回去」的假字段。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int LiveCount => (Items ?? new List<DutyItem>()).Count(x => !x.Deleted);

    public string Title { get; set; } = "值日生名单";

    /// <summary>标题样式：字号 / 颜色 / 加粗；空 = 主题标题色 + 加粗 + 标准档。</summary>
    // r69：新建字段默认「特大 + 默认墨色」（与作业公示一致）。JSON 里带 sizeLevel 的老数据
    // 反序列化时会覆盖这些默认值，所以老设置一条不动。
    public TextStyle TitleStyle { get; set; } = new() { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>内容样式（值日生姓名）。</summary>
    public TextStyle ContentStyle { get; set; } = new() { SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>星期列样式（「周一」等）；基准 12 号按档位等比缩放，加粗 + 主题弱色（今天那一行仍自动改用强调色）。</summary>
    public TextStyle DayStyle { get; set; } = new() { SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>值日内容样式（「扫地、拖地」等第二行小字）；基准 11 号按档位等比缩放 + 主题弱色。</summary>
    public TextStyle DutyStyle { get; set; } = new() { SizeLevel = HwTextStyle.ExtraLargeLevel };

    /// <summary>值日安排（按星期顺序显示）。</summary>
    public List<DutyItem> Items { get; set; } = new();

    /// <summary>便签主题风格标识（见 HomeworkThemes.Presets）。</summary>
    public string Theme { get; set; } = "green";

    /// <summary>是否高亮今天是星期几那一条（一眼看到今天谁值日）。</summary>
    public bool HighlightToday { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 按「周一…周日」排序，「每天」排在最前。
    /// <para>★ 只列可见条目：软删除的墓碑留在 Items 里只为让远端的旧指令找得到它，
    /// 绝不能再出现在便签 / 桌面背景 / 内容快照里。</para>
    /// </summary>
    public List<DutyItem> SortedItems()
        => LiveItems
            .OrderBy(x => x.DayOrder())
            .ThenBy(x => x.Duty, StringComparer.CurrentCulture)
            .ToList();
}

/// <summary>
/// 某一天的值日安排。
/// <para>★ 继承 <see cref="BoardItemBase"/>：值日安排现在也能以「结构化变更单」发布
/// （与作业公示同一套逻辑），远端指令必须能指到唯一一条，否则只能整表覆盖、
/// 教室机本地改的东西会被抹掉。几个身份字段对老 JSON 全是可缺省的，老数据行为不变。</para>
/// </summary>
public class DutyItem : BoardItemBase, INotifyPropertyChanged
{
    /// <summary>星期名称固定顺序；「每天」用于每天都一样的固定岗位。</summary>
    public static readonly string[] DayNames = { "每天", "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private string _day = "周一";
    private string _duty = "";
    private string _names = "";

    /// <summary>星期（每天 / 周一 … 周日）。</summary>
    public string Day { get => _day; set => Set(ref _day, value); }

    /// <summary>值日内容（如「扫地、拖地」，可留空只写人名）。</summary>
    public string Duty { get => _duty; set => Set(ref _duty, value); }

    /// <summary>值日生姓名（每行一个或用逗号 / 顿号分隔）。</summary>
    public string Names { get => _names; set => Set(ref _names, value); }

    /// <summary>排序权重：「每天」= 0，周一 = 1 … 周日 = 7；未知值排最后。</summary>
    public int DayOrder()
    {
        int i = Array.IndexOf(DayNames, (Day ?? "").Trim());
        return i < 0 ? 99 : i;
    }

    /// <summary>显示用星期名（未知值原样返回，空值按周一）。</summary>
    public string DayText() => string.IsNullOrWhiteSpace(Day) ? "周一" : Day.Trim();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
