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

    public static void SaveSchedule(ScheduleFile sf)
    {
        Ensure();
        File.WriteAllText(SchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadSchedule()
    {
        if (!File.Exists(SchedulePath)) return null;
        return JsonSerializer.Deserialize<ScheduleFile>(File.ReadAllText(SchedulePath), Opt);
    }

    public static void SaveTeacherSchedule(ScheduleFile sf)
    {
        Ensure();
        File.WriteAllText(TeacherSchedulePath, JsonSerializer.Serialize(sf, Opt));
    }

    public static ScheduleFile? LoadTeacherSchedule()
    {
        if (!File.Exists(TeacherSchedulePath)) return null;
        return JsonSerializer.Deserialize<ScheduleFile>(File.ReadAllText(TeacherSchedulePath), Opt);
    }

    public static void SaveHomework(HomeworkBoard board)
    {
        Ensure();
        File.WriteAllText(HomeworkPath, JsonSerializer.Serialize(board, Opt));
    }

    public static HomeworkBoard? LoadHomework()
    {
        if (!File.Exists(HomeworkPath)) return null;
        return JsonSerializer.Deserialize<HomeworkBoard>(File.ReadAllText(HomeworkPath), Opt);
    }

    public static void SaveMissingBoard(MissingBoard board)
    {
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
        Ensure();
        File.WriteAllText(NotificationsPath, JsonSerializer.Serialize(list, Opt));
    }

    public static List<Notification> LoadNotifications()
    {
        if (!File.Exists(NotificationsPath)) return new();
        return JsonSerializer.Deserialize<List<Notification>>(File.ReadAllText(NotificationsPath), Opt) ?? new();
    }

    public static void SaveSettings(AppSettings s)
    {
        Ensure();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Opt));
    }

    public static AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Opt) ?? new();
    }

    public static void SaveOverrides(List<LessonOverride> list)
    {
        Ensure();
        File.WriteAllText(OverridesPath, JsonSerializer.Serialize(list, Opt));
    }

    public static List<LessonOverride> LoadOverrides()
    {
        if (!File.Exists(OverridesPath)) return new();
        return JsonSerializer.Deserialize<List<LessonOverride>>(File.ReadAllText(OverridesPath), Opt) ?? new();
    }
}
