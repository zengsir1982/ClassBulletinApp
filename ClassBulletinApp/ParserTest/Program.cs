using System;
using System.Linq;
using ScheduleCore.Services;

var path = args.Length > 0 ? args[0] :
    @"F:\study\班级公布栏\全校班级临时总课表.xlsx";

Console.WriteLine("导入文件: " + path);
var sf = ScheduleImporter.Import(path);
Console.WriteLine($"班级数: {sf.Classes.Count}");
Console.WriteLine($"导入时间: {sf.ImportedAt:yyyy-MM-dd HH:mm}");

foreach (var c in sf.Classes.Take(3))
{
    Console.WriteLine($"\n=== {c.ClassName} 星期一 ===");
    if (c.Data.TryGetValue(0, out var day))
        foreach (var p in day.Keys.OrderBy(k => k))
            foreach (var l in day[p])
                Console.WriteLine($"  第{p}节 {l.TimeRange} {l.Subject} / {l.Teacher} {(l.Location)} {(l.Week == 1 ? "单周" : l.Week == 2 ? "双周" : "")}");
}

Console.WriteLine("\n=== 导入临时课表（调/代 标记）===");
var (tsf, overrides) = ScheduleImporter.ImportTempSchedule(path);
Console.WriteLine($"基础课表班级数: {tsf.Classes.Count}");
Console.WriteLine($"调/代覆盖条数: {overrides.Count}");
int tiao = overrides.Count(o => o.Type == "调");
int dai = overrides.Count(o => o.Type == "代");
Console.WriteLine($"  其中 调课={tiao}  代课={dai}");
Console.WriteLine("  样例（含地点/组合标记）:");
foreach (var o in overrides.Where(o => !string.IsNullOrEmpty(o.Location) || o.Subject.Contains("体育")).Take(6))
    Console.WriteLine($"    {o.ClassName} 周{o.DayIdx + 1} 第{o.Period}节 [{o.Type}] {o.Subject} / {o.Teacher} {o.Location}");

// 验证基础课表已剥离 (调)/(代) 标记
var sample = tsf.Classes.First(c => c.ClassName == "八年级(14)班");
if (sample.Data.TryGetValue(0, out var d0))
    foreach (var p in d0.Keys.OrderBy(k => k))
        foreach (var l in d0[p])
            if (l.Subject.Contains("("))
                Console.WriteLine($"  [警告] 仍有括号残留: {l.Subject}");
Console.WriteLine("  标记剥离检查: 若上方无 [警告] 则科目名已干净。");

Console.WriteLine("\nDONE");
