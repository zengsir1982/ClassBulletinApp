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

// ===== 单班导入校验（可选：第二个参数传 "single"） =====
if (args.Length > 1 && args[1] == "single")
{
    Console.WriteLine("\n=== 单班导入校验 ===");
    var singlePath = @"F:\study\班级公布栏\八年级X班-班级课程表.xlsx";
    var ssf = ScheduleImporter.ImportSingleClass(singlePath);
    Console.WriteLine($"班级数: {ssf.Classes.Count}");
    foreach (var c in ssf.Classes)
    {
        Console.WriteLine($"=== {c.ClassName} ===");
        for (int d = 0; d < 5; d++)
        {
            if (c.Data.TryGetValue(d, out var day))
            {
                string[] names = { "周一", "周二", "周三", "周四", "周五" };
                Console.WriteLine($" {names[d]}:");
                foreach (var p in day.Keys.OrderBy(k => k))
                    foreach (var l in day[p])
                        Console.WriteLine($"   第{p}节 {l.TimeRange} {l.Subject} / {l.Teacher} {l.Location}");
            }
        }
    }
    Console.WriteLine("SINGLE DONE");
}

// ===== 教师个人课表导入校验（第三个参数传 "teacher"） =====
if (args.Length > 1 && args[1] == "teacher")
{
    Console.WriteLine("\n=== 教师课表导入校验 ===");
    var teacherPath = @"F:/study/班级公布栏/某某老师-教师课程表.xlsx";
    var ts = ScheduleImporter.ImportTeacherSchedule(teacherPath);
    Console.WriteLine($"教师数: {ts.Classes.Count}");
    foreach (var c in ts.Classes)
    {
        Console.WriteLine($"=== 教师: {c.ClassName} ===");
        for (int d = 0; d < 5; d++)
        {
            if (c.Data.TryGetValue(d, out var day))
            {
                string[] names = { "周一", "周二", "周三", "周四", "周五" };
                Console.WriteLine($" {names[d]}:");
                foreach (var p in day.Keys.OrderBy(k => k))
                    foreach (var l in day[p])
                        // 教师课表：Teacher 字段实为所教班级
                        Console.WriteLine($"   第{p}节 {l.TimeRange} {l.Subject} / 班级={l.Teacher} {l.Location}");
            }
        }
    }
    Console.WriteLine("TEACHER DONE");
}

// ===== 教师提取校验（第二个参数传 "extract"） =====
if (args.Length > 1 && args[1] == "extract")
{
    Console.WriteLine("\n=== 从全校课表反向整理教师个人课表 ===");
    var teachers = TeacherExtractor.Build(sf);
    Console.WriteLine($"提取到的教师人数: {teachers.Classes.Count}");
    var sampleT = teachers.Classes.FirstOrDefault(c => c.ClassName.Contains("曾"))
                  ?? teachers.Classes.FirstOrDefault();
    if (sampleT != null)
    {
        Console.WriteLine($"=== 样例教师: {sampleT.ClassName} ===");
        string[] names = { "周一", "周二", "周三", "周四", "周五" };
        for (int d = 0; d < 5; d++)
        {
            if (!sampleT.Data.TryGetValue(d, out var day)) continue;
            Console.WriteLine($" {names[d]}:");
            foreach (var p in day.Keys.OrderBy(k => k))
                foreach (var l in day[p])
                    Console.WriteLine($"   第{p}节 {l.TimeRange} {l.Subject} / 班级={l.Teacher} {l.Location}");
        }
    }
    Console.WriteLine($"全校教师课时总数: {teachers.Classes.Sum(c => c.Data.Values.Sum(d => d.Values.Sum(v => v.Count)))}");
    Console.WriteLine("EXTRACT DONE");

    // 带第三个参数 "save" 时才落盘：用于在没有重新导入全校课表的情况下，
    // 从已有的全校班课表反向重建教师个人课表（App 导入时也会自动做这一步）。
    if (args.Contains("save"))
    {
        Store.SaveTeacherSchedule(teachers);
        Console.WriteLine($"已写入教师课表：教师 {teachers.Classes.Count} 人 -> " + Store.AppDir);
    }
}
