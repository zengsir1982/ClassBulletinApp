using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 桌面背面服务：把「当天课表」「作业公示」「缺交作业名单」三张便签合成一张壁纸应用到桌面背景，
/// 并在课表 / 作业 / 名单内容变化、跨天时自动重绘（仅在实际已应用时才自动刷新，避免误改壁纸）。
/// </summary>
public static class BackdropService
{
    public static string OutputPath => Path.Combine(Store.AppDir, "desktop_backdrop.png");

    private static DispatcherTimer? _timer;
    private static string _lastSignature = "";
    private static bool _busy;
    private static bool _started;

    /// <summary>当前设置是否要求显示任一内容（课表 / 作业 / 缺交名单）。</summary>
    public static bool IsContentEnabled
    {
        get
        {
            var b = Store.LoadSettings().Backdrop ?? new BackdropSettings();
            return b.ShowSchedule || b.ShowHomework || b.ShowMissing;
        }
    }

    /// <summary>当前桌面壁纸是否就是本程序生成的桌面背面图。</summary>
    public static bool IsApplied()
    {
        try
        {
            return string.Equals(WallpaperHelper.GetCurrentWallpaper(), OutputPath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Start()
    {
        if (_started) return;
        _started = true;
        AppEvents.ScheduleChanged += () => RefreshIfNeeded();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => RefreshIfNeeded();
        _timer.Start();
    }

    /// <summary>课表变化 / 定时轮询时，若已应用到桌面则按内容签名判断是否需要重绘。</summary>
    public static void RefreshIfNeeded()
    {
        try
        {
            if (!IsContentEnabled || !IsApplied()) return;
            string sig = ComputeSignature();
            if (sig == _lastSignature) return;
            Apply(silent: true);
        }
        catch { }
    }

    // ---------- 应用 / 清除 ----------
    /// <summary>按当前设置渲染并应用为桌面壁纸。</summary>
    public static bool Apply(bool silent, out string message)
    {
        message = "";
        if (_busy) return false;
        _busy = true;
        try
        {
            var s = Store.LoadSettings();
            var b = s.Backdrop ??= new BackdropSettings();

            // 作业位置统一由 Backdrop 保存，同时写回 homework.json 的 Layout，保证旧入口一致
            var board = Store.LoadHomework();
            if (b.HomeworkLayout == null && board != null)
                b.HomeworkLayout = board.Layout;   // 兼容旧数据：继承作业文件里的位置
            else if (b.HomeworkLayout != null && board != null)
                board.Layout = b.HomeworkLayout;

            if (!b.ShowSchedule && !b.ShowHomework && !b.ShowMissing)
            {
                Clear();
                message = "已关闭全部内容，桌面壁纸已恢复。";
                return true;
            }

            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null)
            {
                message = "无法获取主屏幕分辨率，应用桌面背景失败。";
                return false;
            }

            TodayResult? today = null;
            string title = "", dateText = "", weekText = "";
            if (b.ShowSchedule)
            {
                var built = BuildToday(s);
                if (built.HasValue)
                {
                    today = built.Value.Res;
                    title = built.Value.Title;
                    dateText = built.Value.DateText;
                    weekText = built.Value.WeekText;
                }
            }

            string? origin = string.IsNullOrWhiteSpace(s.SavedWallpaper) ? null : s.SavedWallpaper;

            var missing = b.ShowMissing ? Store.LoadMissingBoard() : null;

            Directory.CreateDirectory(Store.AppDir);
            BackdropRenderer.RenderToPng(today, title, dateText, weekText,
                b.ShowHomework ? board : null, missing, b,
                screen.Bounds.Width, screen.Bounds.Height, origin, OutputPath);

            // 首次应用前记录系统原壁纸，便于「清除」恢复
            var cur = WallpaperHelper.GetCurrentWallpaper();
            if (!string.Equals(cur, OutputPath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(cur) && !string.Equals(cur, s.SavedWallpaper, StringComparison.OrdinalIgnoreCase))
            {
                s.SavedWallpaper = cur;
            }
            Store.SaveSettings(s);
            WallpaperHelper.SetWallpaper(OutputPath);
            _lastSignature = ComputeSignature();
            message = "已应用到桌面背景（" + string.Join(" + ",
                          new[]
                          {
                              b.ShowSchedule ? "当天课表" : null,
                              b.ShowHomework ? "作业公示" : null,
                              b.ShowMissing ? "缺交名单" : null
                          }.Where(x => x != null)) + "）。";
            return true;
        }
        catch (Exception ex)
        {
            message = "应用桌面背景失败：" + ex.Message;
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    public static bool Apply(bool silent) => Apply(silent, out _);

    /// <summary>恢复系统原壁纸。</summary>
    public static void Clear()
    {
        try
        {
            var s = Store.LoadSettings();
            WallpaperHelper.SetWallpaper(string.IsNullOrEmpty(s.SavedWallpaper) ? "" : s.SavedWallpaper);
            _lastSignature = "";
        }
        catch { }
    }

    /// <summary>悬浮课表底部 / 托盘菜单：一键开关「当天课表显示在桌面背景」。</summary>
    public static bool ToggleSchedule(out string message)
    {
        var s = Store.LoadSettings();
        s.Backdrop ??= new BackdropSettings();
        bool turnOn = !s.Backdrop.ShowSchedule;
        s.Backdrop.ShowSchedule = turnOn;
        // 只切换「当天课表」这一项：作业公示是否一起显示由「显示设置」里的桌面背景区块独立控制
        Store.SaveSettings(s);

        bool ok = Apply(silent: false, out message);
        if (!ok && turnOn)
        {
            // 渲染失败时回滚开关，避免界面显示"已开启"但实际没有内容
            s.Backdrop.ShowSchedule = !turnOn;
            Store.SaveSettings(s);
        }
        return ok;
    }

    /// <summary>
    /// 悬浮课表底部 / 托盘菜单：一键开关「作业公示显示在桌面背景」。
    /// 翻转 Backdrop.ShowHomework 并按当前设置重绘（含缺交名单）。
    /// </summary>
    public static bool ToggleHomework(out string message)
    {
        var s = Store.LoadSettings();
        s.Backdrop ??= new BackdropSettings();
        bool turnOn = !s.Backdrop.ShowHomework;
        s.Backdrop.ShowHomework = turnOn;
        // 只切换「作业公示」这一项：当天课表 / 缺交名单是否一起显示由各自开关独立控制
        Store.SaveSettings(s);

        bool ok = Apply(silent: false, out message);
        if (!ok && turnOn)
        {
            s.Backdrop.ShowHomework = !turnOn;
            Store.SaveSettings(s);
        }
        return ok;
    }

    /// <summary>
    /// 缺交名单便签底部 / 托盘菜单：一键开关「缺交作业名单显示在桌面背景」。
    /// 翻转 Backdrop.ShowMissing 并按当前设置重绘。
    /// </summary>
    public static bool ToggleMissing(out string message)
    {
        var s = Store.LoadSettings();
        s.Backdrop ??= new BackdropSettings();
        bool turnOn = !s.Backdrop.ShowMissing;
        s.Backdrop.ShowMissing = turnOn;
        Store.SaveSettings(s);

        bool ok = Apply(silent: false, out message);
        if (!ok && turnOn)
        {
            s.Backdrop.ShowMissing = !turnOn;
            Store.SaveSettings(s);
        }
        return ok;
    }

    /// <summary>
    /// 预览 / 诊断：按当前设置渲染一张桌面背景图到指定路径（不修改系统壁纸）。
    /// 用于「应用前先看效果」，也便于自动化测试。
    /// </summary>
    public static bool RenderPreview(string outPath, int width, int height, out string message)
    {
        message = "";
        try
        {
            var s = Store.LoadSettings();
            var b = s.Backdrop ?? new BackdropSettings();
            var board = Store.LoadHomework();

            TodayResult? today = null;
            string title = "", dateText = "", weekText = "";
            if (b.ShowSchedule)
            {
                var built = BuildToday(s);
                if (built.HasValue)
                {
                    today = built.Value.Res;
                    title = built.Value.Title;
                    dateText = built.Value.DateText;
                    weekText = built.Value.WeekText;
                }
            }

            BackdropRenderer.RenderToPng(today, title, dateText, weekText,
                b.ShowHomework ? board : null,
                b.ShowMissing ? Store.LoadMissingBoard() : null,
                b, width, height,
                string.IsNullOrWhiteSpace(s.SavedWallpaper) ? null : s.SavedWallpaper, outPath);
            message = "已生成桌面背景预览图：" + outPath;
            return true;
        }
        catch (Exception ex)
        {
            message = "生成预览图失败：" + ex.Message;
            return false;
        }
    }

    // ---------- 内容构建 ----------
    internal static (TodayResult Res, string Title, string DateText, string WeekText)? BuildToday(AppSettings s)
    {
        bool teacher = string.Equals(s.ScheduleKind, "Teacher", StringComparison.OrdinalIgnoreCase);
        var sf = teacher ? Store.LoadTeacherSchedule() : Store.LoadSchedule();
        string name = teacher ? s.SelectedTeacher : s.SelectedClass;
        if (sf == null || sf.Classes.Count == 0 || string.IsNullOrEmpty(name)) return null;

        var date = BoardBuilder.ResolveDisplayDate(s);
        List<LessonOverride>? overrides = null;
        if (teacher)
            overrides = TeacherOverrideApplier.BuildTeacherOverrides(sf, name, Store.LoadOverrides());

        var res = BoardBuilder.Build(name, s.SemesterStartMonday, sf, date, overrides);
        string title = string.IsNullOrWhiteSpace(res.ClassName) ? name : res.ClassName;
        if (teacher && !string.IsNullOrWhiteSpace(title)) title = title + " 老师";
        return (res, title, res.DateText, res.WeekLine);
    }

    /// <summary>内容签名：用于判断是否真的需要重绘（日期 / 显示对象 / 课程内容任一变化）。</summary>
    private static string ComputeSignature()
    {
        try
        {
            var s = Store.LoadSettings();
            var b = s.Backdrop ?? new BackdropSettings();
            var sb = new StringBuilder();
            sb.Append(s.ScheduleKind).Append('|').Append(s.SelectedClass).Append('|')
              .Append(s.SelectedTeacher).Append('|').Append(BoardBuilder.ResolveDisplayDate(s).ToString("yyyyMMdd"))
              .Append('|').Append(b.ShowSchedule).Append('|').Append(b.ShowHomework).Append('|').Append(b.ShowMissing);

            if (b.ShowSchedule)
            {
                var built = BuildToday(s);
                if (built.HasValue)
                {
                    foreach (var r in built.Value.Res.Rows)
                    {
                        sb.Append('#').Append(r.PeriodText).Append(r.TimeRange);
                        foreach (var l in r.Lessons)
                            sb.Append(l.Subject).Append(',').Append(l.Teacher).Append(',').Append(l.OverrideBadgeText);
                    }
                    var slay = b.ScheduleLayout;
                    sb.Append("|L").Append(slay?.Anchor).Append(slay?.OffsetX)
                      .Append(slay?.OffsetY).Append(slay?.WidthRatio)
                      .Append(slay?.UseFixedSize).Append(slay?.FixedWidth).Append(slay?.FixedHeight)
                      .Append("|ST").Append(b.ScheduleTheme);
                }
            }

            if (b.ShowHomework)
            {
                var board = Store.LoadHomework();
                if (board != null)
                {
                    sb.Append('|').Append(board.Title).Append(board.UpdatedAt.Ticks);
                    // 文本样式（加粗 / 颜色 / 字号档位）也纳入签名，改格式能触发重绘
                    foreach (var it in board.Items)
                        sb.Append('#').Append(it.Subject).Append(',').Append(it.Content)
                          .Append('~').Append(it.Bold).Append(it.Color).Append(it.SizeLevel);
                }
                var hwLay = b.HomeworkLayout ?? board?.Layout;
                sb.Append("|H").Append(hwLay?.Anchor).Append(hwLay?.OffsetX)
                  .Append(hwLay?.OffsetY).Append(hwLay?.WidthRatio)
                  .Append(hwLay?.UseFixedSize).Append(hwLay?.FixedWidth).Append(hwLay?.FixedHeight)
                  .Append("|T").Append(board?.Theme);
            }

            if (b.ShowMissing)
            {
                var mb = Store.LoadMissingBoard();
                sb.Append('|').Append(mb.Title).Append(mb.UpdatedAt.Ticks).Append("|MT").Append(mb.Theme);
                foreach (var m in mb.Items)
                    sb.Append('@').Append(m.Subject).Append(',').Append(m.Students)
                      .Append('~').Append(m.Bold).Append(m.Color).Append(m.SizeLevel);

                var mlay = b.MissingLayout;
                sb.Append("|M").Append(mlay?.Anchor).Append(mlay?.OffsetX)
                  .Append(mlay?.OffsetY).Append(mlay?.WidthRatio)
                  .Append(mlay?.UseFixedSize).Append(mlay?.FixedWidth).Append(mlay?.FixedHeight);
            }

            return sb.ToString();
        }
        catch { return Guid.NewGuid().ToString(); }
    }
}
