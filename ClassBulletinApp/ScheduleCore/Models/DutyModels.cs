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
    public string Title { get; set; } = "值日生名单";

    /// <summary>标题样式：字号 / 颜色 / 加粗；空 = 主题标题色 + 加粗 + 标准档。</summary>
    public TextStyle TitleStyle { get; set; } = new() { Bold = true };

    /// <summary>内容样式（值日生姓名）。</summary>
    public TextStyle ContentStyle { get; set; } = new();

    /// <summary>星期列样式（「周一」等）；默认 12 号加粗 + 主题弱色（今天那一行仍自动改用强调色）。</summary>
    public TextStyle DayStyle { get; set; } = new();

    /// <summary>值日内容样式（「扫地、拖地」等第二行小字）；默认 11 号 + 主题弱色。</summary>
    public TextStyle DutyStyle { get; set; } = new();

    /// <summary>值日安排（按星期顺序显示）。</summary>
    public List<DutyItem> Items { get; set; } = new();

    /// <summary>便签主题风格标识（见 HomeworkThemes.Presets）。</summary>
    public string Theme { get; set; } = "green";

    /// <summary>是否高亮今天是星期几那一条（一眼看到今天谁值日）。</summary>
    public bool HighlightToday { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>按「周一…周日」排序，「每天」排在最前。</summary>
    public List<DutyItem> SortedItems()
        => (Items ?? new List<DutyItem>())
            .OrderBy(x => x.DayOrder())
            .ThenBy(x => x.Duty, StringComparer.CurrentCulture)
            .ToList();
}

/// <summary>某一天的值日安排。</summary>
public class DutyItem : INotifyPropertyChanged
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
