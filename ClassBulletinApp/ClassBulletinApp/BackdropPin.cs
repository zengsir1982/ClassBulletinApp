using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 「设为背景」一键操作（各悬浮窗共用）：
/// 把悬浮窗<b>当前</b>的位置与大小，直接锁定为该便签在桌面背景上的排版（固定尺寸），
/// 同时打开它在桌面背景的显示并立即重绘壁纸。
/// <para>· 悬浮窗自身的隐藏 / 自动隐藏由调用方在成功后处理（本类不碰窗口）。</para>
/// <para>· 桌面背景只按主屏分辨率渲染，悬浮窗不在主屏时直接拒绝，避免画到屏幕外。</para>
/// <para>· 与「位置」里的排版窗口共用同一套换算公式（Anchor=TopLeft + 偏移 + 固定宽高），
///   所以这里锁进去的矩形，和排版窗口里看到的就是同一个。</para>
/// </summary>
public static class BackdropPin
{
    /// <summary>要锁定的便签种类。自定义便签有多张，需额外传便签 Id。</summary>
    public enum Kind { Schedule, Homework, Missing, Countdown, Duty, CustomNote }

    private const double MINW = 200;   // 与 BackdropEditorWindow 的最小尺寸保持一致
    private const double MINH = 160;

    /// <summary>与 BackdropRenderer.Place 的 60*scale 基准边距对应（1080p 基准）。</summary>
    private const double BASE_MARGIN = 60;

    /// <summary>
    /// 执行「设为背景」：读窗口矩形 → 写入排版 → 打开显示 → 重绘壁纸。
    /// </summary>
    public static bool Pin(Kind kind, Window win, out string message)
        => Pin(kind, null, win, out message);

    /// <summary>
    /// 执行「设为背景」：读窗口矩形 → 写入排版 → 打开显示 → 重绘壁纸。
    /// </summary>
    /// <param name="kind">哪张便签。</param>
    /// <param name="noteId">自定义便签的 Id（<see cref="Kind.CustomNote"/> 时必填）。</param>
    /// <param name="win">对应的悬浮窗（用于取当前矩形与 DPI）。</param>
    /// <param name="message">成功 / 失败的人话说明。</param>
    /// <returns>壁纸是否已成功应用（false 时调用方不要隐藏悬浮窗）。</returns>
    public static bool Pin(Kind kind, string? noteId, Window win, out string message)
    {
        message = "";

        if (kind == Kind.CustomNote && string.IsNullOrEmpty(noteId))
        {
            message = "找不到这张自定义便签的标识，已取消。";
            return false;
        }

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
        {
            message = "无法获取主屏幕分辨率，已取消。";
            return false;
        }

        double sw = screen.Bounds.Width;
        double sh = screen.Bounds.Height;

        // WPF 逻辑像素(DIP) → 物理像素：桌面背景按主屏物理分辨率渲染
        double k = 1.0;
        try
        {
            var d = VisualTreeHelper.GetDpi(win);
            if (d.DpiScaleX > 0) k = d.DpiScaleX;
        }
        catch { }

        var s = Store.LoadSettings();

        if (!TryRect(s, kind, noteId, win, out double dix, out double diy, out double diw, out double dih))
        {
            message = "读不到这个悬浮窗的位置与大小：请先把它摆好（或拖动一下）再点「设为背景」。";
            return false;
        }

        double px = dix * k, py = diy * k, pw = diw * k, ph = dih * k;

        // 桌面背景只渲染主屏：中心点必须落在主屏内
        double cx = px + pw / 2, cy = py + ph / 2;
        if (cx < 0 || cy < 0 || cx > sw || cy > sh)
        {
            message = "这个悬浮窗不在主屏范围内（桌面背景只渲染主屏），已取消。请先把它挪到主屏再试。";
            return false;
        }

        // 夹进主屏：上界留 40px（与 BackdropRenderer.ResolveCardWidth / Place 的 width-40 一致，
        // 否则渲染时会被再夹一次，画出来比锁定的尺寸小一圈）
        double maxW = Math.Max(MINW, sw - 40);
        double maxH = Math.Max(MINH, sh - 40);
        pw = Math.Clamp(pw, MINW, maxW);
        ph = Math.Clamp(ph, MINH, maxH);
        px = Math.Clamp(px, 0, Math.Max(0, sw - pw));
        py = Math.Clamp(py, 0, Math.Max(0, sh - ph));

        double scale = sh / 1080.0;
        var b = s.Backdrop ??= new BackdropSettings();
        var lay = LayoutOf(b, kind, noteId);

        lay.Anchor = "TopLeft";
        lay.OffsetX = px / scale - BASE_MARGIN;
        lay.OffsetY = py / scale - BASE_MARGIN;
        lay.WidthRatio = pw / sw;
        lay.UseFixedSize = true;      // 锁定尺寸：高度不随内容多少漂移
        lay.FixedWidth = pw;
        lay.FixedHeight = ph;

        SetVisible(b, kind, noteId, true);
        // 记下「这个窗口等会儿要能被放回来」：取消背景时按这份清单恢复悬浮窗，
        // 只有点了「设为背景」的窗口才在清单里，用户原本关掉的便签不会被强行弹出。
        b.AddPinnedRestore(TokenOf(kind, noteId));
        Store.SaveSettings(s);

        // 作业位置同步回 homework.json 的 Layout，保证旧入口 / 旧数据一致
        if (kind == Kind.Homework && b.HomeworkLayout is { } hwLay)
        {
            var board = Store.LoadHomework();
            if (board != null)
            {
                board.Layout = hwLay;
                Store.SaveHomework(board);
            }
        }

        bool ok = BackdropService.Apply(silent: false, out string applyMsg);
        if (!ok)
        {
            // 渲染失败：回滚「显示在桌面背景」，避免界面显示开着、壁纸上却没有
            SetVisible(b, kind, noteId, false);
            Store.SaveSettings(s);
            message = string.IsNullOrWhiteSpace(applyMsg) ? "应用桌面背景失败。" : applyMsg;
            return false;
        }

        AppEvents.RaiseBackdropChanged();

        // 上了壁纸就不要再浮一个手写球：壁纸的意义是"一眼看全"，球浮在上面挡住内容。
        // 集中在这里隐藏，省得每个「设为背景」的入口各写一遍、漏一个就漏一个。
        App.HandwritingBall?.ApplyVisibility(false);

        message = "已按当前位置与大小设为桌面背景（" + Math.Round(pw) + " × " + Math.Round(ph) + " 像素）。";
        return true;
    }

    /// <summary>把一个窗口种类翻译成「待恢复清单」里的标识。</summary>
    private static string TokenOf(Kind kind, string? noteId) => kind switch
    {
        Kind.Schedule => "Schedule",
        Kind.Homework => "Homework",
        Kind.Missing => "Missing",
        Kind.Countdown => "Countdown",
        Kind.Duty => "Duty",
        _ => "note:" + (noteId ?? "")
    };

    /// <summary>
    /// 取消桌面背景：撤掉壁纸内容、恢复系统原壁纸，并把当初「设为背景」时隐藏掉的悬浮窗放回来。
    /// <para>这是「设为背景」的反向操作 —— 只撤壁纸不放回窗口，用户会觉得便签"点一下就不见了"。</para>
    /// </summary>
    public static bool Unpin(out string message)
    {
        message = "";
        try
        {
            var s = Store.LoadSettings();
            var b = s.Backdrop ??= new BackdropSettings();

            var tokens = RestoreTargets(b);
            bool anyContent = b.ShowSchedule || b.ShowHomework || b.ShowMissing
                              || b.ShowCountdown || b.ShowDuty || b.ShowAnyCustomNote();
            bool applied = BackdropService.IsApplied();

            if (!anyContent && !applied && tokens.Count == 0)
            {
                message = "当前没有设置桌面背景。";
                return false;
            }

            b.ShowSchedule = false;
            b.ShowHomework = false;
            b.ShowMissing = false;
            b.ShowCountdown = false;
            b.ShowDuty = false;
            if (b.CustomNotes != null)
                foreach (var entry in b.CustomNotes.Values)
                    if (entry != null) entry.Visible = false;
            b.PinnedRestores = new List<string>();
            Store.SaveSettings(s);

            BackdropService.Clear();
            AppEvents.RaiseBackdropChanged();
            if (tokens.Count > 0) AppEvents.RaiseRestoreFloating(tokens.ToArray());

            // 球按设置恢复：只在「设为背景」时临时藏起来，不该顺手把用户的开关改掉
            App.HandwritingBall?.ApplyVisibility(Store.LoadSettings().ShowHandwritingBall);

            message = applied ? "已取消桌面背景，系统原壁纸已恢复" : "已取消桌面背景设置";
            message += tokens.Count > 0 ? "；课表 / 便签已恢复为悬浮窗显示。" : "。";
            AppEvents.RaiseBackdropOffDone(true, message);
            return true;
        }
        catch (Exception ex)
        {
            message = "取消桌面背景失败：" + ex.Message;
            AppEvents.RaiseBackdropOffDone(false, message);
            return false;
        }
    }

    /// <summary>
    /// 取消背景时要放回来的窗口清单：
    /// 优先用「设为背景」时逐个记下的清单；旧数据没记过，就按壁纸上当前画着的内容反推。
    /// </summary>
    private static List<string> RestoreTargets(BackdropSettings b)
    {
        var res = new List<string>();

        void Add(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            if (!res.Contains(t, StringComparer.OrdinalIgnoreCase)) res.Add(t);
        }

        foreach (var t in b.PinnedRestores ?? new List<string>()) Add(t);
        if (res.Count > 0) return res;

        if (b.ShowSchedule) Add("Schedule");
        if (b.ShowHomework) Add("Homework");
        if (b.ShowMissing) Add("Missing");
        if (b.ShowCountdown) Add("Countdown");
        if (b.ShowDuty) Add("Duty");
        if (b.CustomNotes != null)
            foreach (var kv in b.CustomNotes)
                if (kv.Value?.Visible == true) Add("note:" + kv.Key);

        return res;
    }

    /// <summary>取该便签对应的排版对象；为 null 时按默认值补一个并挂回设置。</summary>
    private static HomeworkWallpaperLayout LayoutOf(BackdropSettings b, Kind kind, string? noteId) => kind switch
    {
        Kind.Schedule => b.ScheduleLayout ??= new HomeworkWallpaperLayout { Anchor = "MiddleLeft", WidthRatio = 0.38 },
        Kind.Homework => b.HomeworkLayout ??= new HomeworkWallpaperLayout { Anchor = "MiddleRight", WidthRatio = 0.4 },
        Kind.Countdown => b.CountdownLayout ??= new HomeworkWallpaperLayout { Anchor = "TopRight", UseFixedSize = true, FixedWidth = 360, FixedHeight = 240 },
        Kind.Duty => b.DutyLayout ??= new HomeworkWallpaperLayout { Anchor = "BottomLeft", UseFixedSize = true, FixedWidth = 400, FixedHeight = 340 },
        Kind.CustomNote => b.EntryOf(noteId!).Layout,
        _ => b.MissingLayout ??= new HomeworkWallpaperLayout { Anchor = "BottomRight", UseFixedSize = true, FixedWidth = 420, FixedHeight = 360 }
    };

    /// <summary>打开 / 关闭该便签在桌面背景上的显示开关。</summary>
    private static void SetVisible(BackdropSettings b, Kind kind, string? noteId, bool on)
    {
        switch (kind)
        {
            case Kind.Schedule: b.ShowSchedule = on; break;
            case Kind.Homework: b.ShowHomework = on; break;
            case Kind.Countdown: b.ShowCountdown = on; break;
            case Kind.Duty: b.ShowDuty = on; break;
            case Kind.CustomNote: b.EntryOf(noteId!).Visible = on; break;
            default: b.ShowMissing = on; break;
        }
    }

    /// <summary>
    /// 取窗口矩形（DIP）。优先用窗口的实时坐标；窗口正处在自动隐藏的滑出状态、
    /// 或从未摆放过时，退回存档坐标，避免把「只露出一条边」的矩形锁成桌面背景。
    /// </summary>
    private static bool TryRect(AppSettings s, Kind kind, string? noteId, Window win,
        out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;

        bool liveOk = !double.IsNaN(win.Left) && !double.IsNaN(win.Top)
                      && win.ActualWidth > 0 && win.ActualHeight > 0
                      && FloatingHelper.IsMostlyOnScreen(win, 0.5);

        if (liveOk)
        {
            left = win.Left; top = win.Top;
            width = win.ActualWidth; height = win.ActualHeight;
            return true;
        }

        if (kind == Kind.CustomNote)
        {
            var note = s.CustomNotes?.FirstOrDefault(n => n.Id == noteId);
            return TrySaved(note?.Left, note?.Top, note?.Width, note?.Height,
                            out left, out top, out width, out height);
        }

        return kind switch
        {
            Kind.Schedule => TrySaved(s.ScheduleLayout?.Left, s.ScheduleLayout?.Top,
                                      s.ScheduleLayout?.Width, s.ScheduleLayout?.Height,
                                      out left, out top, out width, out height),
            Kind.Homework => TrySaved(s.HwWinLeft, s.HwWinTop, s.HwWinWidth, s.HwWinHeight,
                                      out left, out top, out width, out height),
            Kind.Countdown => TrySaved(s.PlacementOf("countdown").Left, s.PlacementOf("countdown").Top,
                                      s.PlacementOf("countdown").Width, s.PlacementOf("countdown").Height,
                                      out left, out top, out width, out height),
            Kind.Duty => TrySaved(s.PlacementOf("duty").Left, s.PlacementOf("duty").Top,
                                      s.PlacementOf("duty").Width, s.PlacementOf("duty").Height,
                                      out left, out top, out width, out height),
            _ => TrySaved(s.MsWinLeft, s.MsWinTop, s.MsWinWidth, s.MsWinHeight,
                                      out left, out top, out width, out height)
        };
    }

    private static bool TrySaved(double? l, double? t, double? w, double? h,
        out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;
        if (l is not double dl || t is not double dt) return false;
        if (w is not double dw || h is not double dh || dw <= 0 || dh <= 0) return false;
        left = dl; top = dt; width = dw; height = dh;
        return true;
    }
}
