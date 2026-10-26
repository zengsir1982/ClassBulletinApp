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

    // ---------- 数据清除（导入前 / 手动清空用） ----------
    public static bool ExistsSchedule() => File.Exists(SchedulePath);
    public static bool ExistsTeacherSchedule() => File.Exists(TeacherSchedulePath);
    public static bool ExistsOverrides() => File.Exists(OverridesPath);
    public static bool ExistsNotifications() => File.Exists(NotificationsPath);

    public static void DeleteSchedule() { try { File.Delete(SchedulePath); } catch { } }
    public static void DeleteTeacherSchedule() { try { File.Delete(TeacherSchedulePath); } catch { } }
    public static void DeleteOverrides() { try { File.Delete(OverridesPath); } catch { } }
    public static void DeleteNotifications() { try { File.Delete(NotificationsPath); } catch { } }

    /// <summary>清空全部课表类数据：班级课表、教师个人课表、调课代课记录、通知公告。</summary>
    public static void ClearAllData()
    {
        DeleteSchedule();
        DeleteTeacherSchedule();
        DeleteOverrides();
        DeleteNotifications();
    }
}
