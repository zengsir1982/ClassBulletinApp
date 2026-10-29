using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScheduleCore.Models;

namespace ScheduleCore.Services;

public static class Store
{
    public static string AppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassBulletinApp");

    static string SchedulePath => Path.Combine(AppDir, "schedule.json");

    /// <summary>
    /// 持久化总开关：游客模式下为 false，所有 Save* 直接跳过（改动只留内存、不落盘），
    /// 退出后自动丢弃；登录（admin）通过 LoginWindow 置为 true 后正常保存。
    /// </summary>
    public static bool EnablePersistence { get; set; }

    static string TeacherSchedulePath => Path.Combine(AppDir, "teacher_schedule.json");
    static string NotificationsPath => Path.Combine(AppDir, "notifications.json");
    static string SettingsPath => Path.Combine(AppDir, "settings.json");
    static string OverridesPath => Path.Combine(AppDir, "overrides.json");
    static string HomeworkPath => Path.Combine(AppDir, "homework.json");
    static string MissingPath => Path.Combine(AppDir, "missing.json");

    static JsonSerializerOptions Opt => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static void Ensure() => Directory.CreateDirectory(AppDir);

    /// <summary>
    /// 通用 JSON 读取：文件不存在或内容为空（0 字节 / 仅空白）时返回 null，
    /// 统一防止「文件存在但被清空/截断」导致 Deserialize 抛 JsonException 而让程序启动崩溃。
    /// </summary>
    static T? ReadJsonOrNull<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonSerializer.Deserialize<T>(text, Opt);
    }

    public static void SaveSchedule(ScheduleFile sf)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(SchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadSchedule()
        => ReadJsonOrNull<ScheduleFile>(SchedulePath);

    public static void SaveTeacherSchedule(ScheduleFile sf)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(TeacherSchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadTeacherSchedule()
        => ReadJsonOrNull<ScheduleFile>(TeacherSchedulePath);

    public static void SaveHomework(HomeworkBoard board)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(HomeworkPath, JsonSerializer.Serialize(board, Opt));
    }

    public static HomeworkBoard? LoadHomework()
        => ReadJsonOrNull<HomeworkBoard>(HomeworkPath);

    public static void SaveMissingBoard(MissingBoard board)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(MissingPath, JsonSerializer.Serialize(board, Opt));
    }

    /// <summary>
    /// 读取「缺交作业名单」独立便签数据。首次调用（还没有 missing.json）时，
    /// 自动把旧版 homework.json 里的 Missing 列表迁移过来，迁移后清空旧字段避免两处重复。
    /// </summary>
    public static MissingBoard LoadMissingBoard()
    {
        if (File.Exists(MissingPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<MissingBoard>(File.ReadAllText(MissingPath), Opt);
                if (loaded != null) return loaded;
            }
            catch { }
            return new MissingBoard();
        }

        var board = new MissingBoard();
        try
        {
            var hw = LoadHomework();
            if (hw?.Missing is { Count: > 0 })
            {
                board.Items = hw.Missing;
                board.UpdatedAt = hw.UpdatedAt;
                hw.Missing = new();
                File.WriteAllText(HomeworkPath, JsonSerializer.Serialize(hw, Opt));
            }
        }
        catch { }
        SaveMissingBoard(board);
        return board;
    }

    public static void SaveNotifications(List<Notification> list)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(NotificationsPath, JsonSerializer.Serialize(list, Opt));
    }

    public static List<Notification> LoadNotifications()
        => ReadJsonOrNull<List<Notification>>(NotificationsPath) ?? new();

    public static void SaveSettings(AppSettings s)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Opt));
    }

    public static AppSettings LoadSettings()
        => ReadJsonOrNull<AppSettings>(SettingsPath) ?? new();

    public static void SaveOverrides(List<LessonOverride> list)
    {
        if (!EnablePersistence) return;
        Ensure();
        File.WriteAllText(OverridesPath, JsonSerializer.Serialize(list, Opt));
    }

    public static List<LessonOverride> LoadOverrides()
        => ReadJsonOrNull<List<LessonOverride>>(OverridesPath) ?? new();

    /// <summary>
    /// 每周首次启动（本周周一尚未清空过）时，清除全部调代课（temp + manual 一并清空，因为「调代课」整体按教学周管理），
    /// 并把本周周一日期写入设置标记，避免一周内重复清空。仅在已登录（EnablePersistence=true）时由 App 调用。
    /// </summary>
    public static void TryWeeklyOverrideReset()
    {
        var today = DateTime.Today;
        int diff = (7 + ((int)today.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
        var monday = today.AddDays(-diff);
        string key = monday.ToString("yyyy-MM-dd");

        var s = LoadSettings();
        if (s.LastClearedMonday == key) return;   // 本周已清空过

        SaveOverrides(new List<LessonOverride>());  // 清除全部调代课
        s.LastClearedMonday = key;
        SaveSettings(s);
    }
}
