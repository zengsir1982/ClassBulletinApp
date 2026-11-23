using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

/// <summary>
/// 作息时间表的纯文本 ⇄ <see cref="PeriodTemplate"/> 互转。
/// <para>一行一条：「第1节 08:10-08:55」是节次行；「大课间 |09:50-10:10|」是公共时间行
/// （不占节次序号，出现顺序就是它在课表里的位置）。</para>
/// <para>r139：从 ScheduleTemplateManagerWindow 抽出来，作为文本 ⇄ 模板的唯一实现。
/// r142 起两个编辑入口都收在手动录入课表窗口（作息 / 科目库点行尾图标弹窗编辑、方案在下拉里），
/// 旧的模板管理窗与课表基础数据维护窗都已删除 —— 格式仍只此一份，避免两处编辑器写出不一样的作息。</para>
/// </summary>
public static class ScheduleSheetText
{
    /// <summary>时间占位符：插入新行后选中它，用户直接打字就能覆盖（不用先删掉再敲）。</summary>
    public const string TimePh = "HH:MM-HH:MM";

    /// <summary>模板 → 多行作息时间表：节次行 + 紧跟它的公共时间行。</summary>
    public static string FormatScheduleSheet(PeriodTemplate p)
    {
        var lines = new List<string>();
        var slots = p.FixedSlots.ToList();
        var used = new HashSet<FixedSlot>();

        void EmitAfter(int after)
        {
            foreach (var f in slots)
            {
                if (used.Contains(f) || f.AfterPeriod != after) continue;
                lines.Add(f.Name + " |" + (string.IsNullOrWhiteSpace(f.TimeRange) ? TimePh : f.TimeRange) + "|");
                used.Add(f);
            }
        }

        EmitAfter(0);
        foreach (var pt in p.Periods.OrderBy(x => x.Index))
        {
            lines.Add("第" + pt.Index + "节 " + (string.IsNullOrWhiteSpace(pt.TimeRange) ? TimePh : pt.TimeRange));
            EmitAfter(pt.Index);
        }
        // 兜底：AfterPeriod 指向不存在的节次（外部数据），放到最后，别让这条公共时间凭空消失
        foreach (var f in slots)
            if (!used.Contains(f))
                lines.Add(f.Name + " |" + (string.IsNullOrWhiteSpace(f.TimeRange) ? TimePh : f.TimeRange) + "|");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 多行作息时间表 → 模板。含「|」的行是公共时间（AfterPeriod = 它前面最近的节次号，没有则为 0），
    /// 其余行是节次。顺序即位置，所以用户不需要再写「第几节后」。
    /// </summary>
    public static void ParseScheduleSheet(string? text, PeriodTemplate p)
    {
        var periods = new List<PeriodTime>();
        var slots = new List<FixedSlot>();
        int lastIndex = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Contains('|'))
            {
                var name = line.Split('|')[0].Trim();
                if (name.Length == 0) continue;
                slots.Add(new FixedSlot { Name = name, TimeRange = ExtractTimeRange(line), AfterPeriod = lastIndex });
            }
            else
            {
                var pm = Regex.Match(line, @"第\s*(\d+)\s*节");
                int idx = pm.Success ? int.Parse(pm.Groups[1].Value) : periods.Count + 1;
                periods.Add(new PeriodTime { Index = idx, TimeRange = ExtractTimeRange(line) });
                lastIndex = idx;
            }
        }
        p.Periods = periods;
        p.FixedSlots = slots;
    }

    /// <summary>从一行里抠出 HH:MM-HH:MM（容忍 ~ 与「至」，去掉中间空格）。</summary>
    public static string ExtractTimeRange(string line)
    {
        var m = Regex.Match(line, @"(\d{1,2}:\d{2}\s*[-~至]\s*\d{1,2}:\d{2})");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", "") : "";
    }

    public static List<string> SplitSheetLines(string? text)
        => string.IsNullOrEmpty(text) ? new List<string>() : text.Split('\n').ToList();

    /// <summary>节次行按出现顺序重排 第1节…第N节（公共时间行原位置不动）。</summary>
    public static void RenumberPeriodLines(List<string> lines)
    {
        int n = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.Contains('|')) continue;
            n++;
            var m = Regex.Match(line, @"^第\s*\d+\s*节\s*");
            string rest = m.Success ? line.Substring(m.Length) : line;
            lines[i] = "第" + n + "节 " + rest;
        }
    }
}
