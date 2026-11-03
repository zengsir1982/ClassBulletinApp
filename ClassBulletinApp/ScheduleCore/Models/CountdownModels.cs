using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ScheduleCore.Models;

/// <summary>
/// 倒计时便签：一张卡里可以放多条事件（如「期末考试 30 天」「运动会 12 天」），
/// 按目标日期从近到远排列，可悬浮在桌面、也可设为桌面背景。
/// </summary>
public class CountdownBoard
{
    /// <summary>卡片总标题。</summary>
    public string Title { get; set; } = "倒计时";

    /// <summary>标题样式：字号 / 颜色 / 加粗；空 = 主题标题色 + 加粗 + 标准档。</summary>
    public TextStyle TitleStyle { get; set; } = new() { Bold = true };

    /// <summary>条目样式（所有条目共用一套，省得设置项越排越长）。</summary>
    public TextStyle ContentStyle { get; set; } = new();

    /// <summary>
    /// 右侧天数样式（「还有 30 天」里的数字与「天」字）。
    /// <para>默认口径：字号 = 条目字号 + 4、加粗、主题强调色 —— 与加这个设置项之前完全一致。</para>
    /// </summary>
    public TextStyle DaysStyle { get; set; } = new();

    /// <summary>倒计时事件（按目标日期升序显示）。</summary>
    public List<CountdownItem> Items { get; set; } = new();

    /// <summary>便签主题风格标识（见 HomeworkThemes.Presets）。</summary>
    public string Theme { get; set; } = "classic";

    /// <summary>目标日期就是今天时，是否把该条高亮成「就是今天」。</summary>
    public bool HighlightToday { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>按目标日期升序、日期无法解析的排最后。</summary>
    public List<CountdownItem> SortedItems()
        => (Items ?? new List<CountdownItem>())
            .OrderBy(x => x.Target() ?? DateTime.MaxValue)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
}

/// <summary>单条倒计时事件。</summary>
public class CountdownItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _targetDate = "";
    private string _note = "";

    /// <summary>事件名称（如「期末考试」）。</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>目标日期（yyyy-MM-dd 字符串，存字符串避免时区与格式差异）。</summary>
    public string TargetDate
    {
        get => _targetDate;
        set
        {
            if (_targetDate == value) return;
            _targetDate = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetDate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetDateValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaysTip)));
        }
    }

    /// <summary>
    /// 目标日期的日期类型（供编辑界面的日期选择器双向绑定）。
    /// <para>★ 只给界面用：<c>[JsonIgnore]</c> 保证存档格式与 <see cref="TargetDate"/> 完全一致，
    /// 不会因为界面上多一个属性而改变 json 结构。</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? TargetDateValue
    {
        get => Target();
        set => TargetDate = value?.ToString("yyyy-MM-dd") ?? "";
    }

    /// <summary>剩余天数的可读文案（编辑列表里直接展示，省得用户自己数）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DaysTip => DaysText();

    /// <summary>备注（可空，如「语文 / 数学 / 英语」）。</summary>
    public string Note { get => _note; set => Set(ref _note, value); }

    /// <summary>解析目标日期；为空或格式不对返回 null。</summary>
    public DateTime? Target()
        => DateTime.TryParse(TargetDate, out var d) ? d.Date : null;

    /// <summary>距今天的天数：正数=还有几天，0=就是今天，负数=已过去几天。</summary>
    public int DaysLeft()
    {
        var t = Target();
        return t == null ? 0 : (t.Value.Date - DateTime.Today).Days;
    }

    /// <summary>显示用文案：还有 30 天 / 就是今天 / 已过去 3 天；日期无效时提示补填。</summary>
    public string DaysText()
    {
        if (Target() == null) return "未设日期";
        int d = DaysLeft();
        return d > 0 ? $"还有 {d} 天" : d == 0 ? "就是今天" : $"已过去 {-d} 天";
    }

    /// <summary>只需显示数字时的文本（配合「天」字用）。</summary>
    public string DaysNumber()
    {
        if (Target() == null) return "—";
        int d = DaysLeft();
        return d >= 0 ? d.ToString() : (-d).ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
