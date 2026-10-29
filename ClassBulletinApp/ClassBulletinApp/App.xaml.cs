using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 由托盘“退出”显式关闭，关闭所有窗口不再自动退出（便于隐藏到托盘常驻）。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局异常捕获：避免“打开即闪退”且无任何提示。
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // 诊断 / 预览：--render-backdrop <输出png> [宽] [高]
        // 按当前配置生成一张桌面背面图后退出，不修改系统壁纸（供预览与自动化测试使用）。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--render-backdrop", StringComparison.OrdinalIgnoreCase))
        {
            int w = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var ww) ? ww : 1920;
            int h = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var hh) ? hh : 1080;
            bool ok = BackdropService.RenderPreview(e.Args[1], w, h, out string msg);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.render.log"),
                    (ok ? "OK " : "FAIL ") + msg);
            }
            catch { }
            Shutdown(ok ? 0 : 1);
            return;
        }

        base.OnStartup(e);

        // 诊断：--pos-check 实测作业 / 缺交便签窗口的落点与可见性，结果写入 ClassBulletinApp.poscheck.log
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--pos-check", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder();
            void Probe(string name, Func<Window> factory)
            {
                try
                {
                    var w = factory();
                    w.Show();
                    w.UpdateLayout();
                    var frame = new DispatcherFrame();
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);

                    var area = FloatingHelper.WorkAreaDip(w);
                    // 全部按逻辑像素（DIP）比较：WinForms 的 WorkingArea 是物理像素，125% 缩放下直接比会误判
                    bool onScreen = w.Left >= area.Left - 2 && w.Top >= area.Top - 2 &&
                        w.Left + w.Width <= area.Right + 2 && w.Top + w.Height <= area.Bottom + 2;
                    sb.AppendLine($"{name}: Visible={w.IsVisible} Left={w.Left:0} Top={w.Top:0} " +
                        $"W={w.Width:0} H={w.Height:0} Actual={w.ActualWidth:0}x{w.ActualHeight:0} " +
                        $"OnScreen={onScreen} Area(DIP)={area.Left:0},{area.Top:0} {area.Width:0}x{area.Height:0}");
                    try { w.Close(); } catch { }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Probe("HomeworkWindow", () => new HomeworkWindow());
            Probe("MissingWindow", () => new MissingWindow());
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.poscheck.log"), sb.ToString()); }
            catch { }
            Shutdown(0);
            return;
        }

        // 诊断：--shot <layout|editor|homeworkedit|missingedit> <输出png> 打开指定窗口并截图后退出
        if (e.Args.Length >= 3 && string.Equals(e.Args[0], "--shot", StringComparison.OrdinalIgnoreCase))
        {
            string shotLog = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.editorshot.log");
            try
            {
                Window w0 = e.Args[1].ToLowerInvariant() switch
                {
                    "editor" => new BackdropEditorWindow(),
                    "homeworkedit" => new HomeworkEditWindow(Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard()),
                    "missingedit" => new MissingEditWindow(Store.LoadMissingBoard()),
                    "schedulewin" => new ScheduleWindow(),
                    "notifywin" => new NotificationWindow(),
                    "homeworkwin" => new HomeworkWindow(),
                    "missingwin" => new MissingWindow(),
                    "main" => new MainWindow(),
                    "mainnotice" => new MainWindow { InitialTabIndex = 4 },
                    "mainhw" => new MainWindow { InitialTabIndex = 5 },
                    "layout0" => new LayoutWindow { InitialTabIndex = 0 },
                    "layout1" => new LayoutWindow { InitialTabIndex = 1 },
                    "quicklaunch" => new QuickLaunchWindow(),
                    "customnoteedit" => new CustomNoteEditWindow(DiagCustomNoteId()),
                    "customnote" => new CustomNoteWindow(DiagCustomNoteId()),
                    _ => new LayoutWindow { InitialTabIndex = 2 }
                };
                w0.WindowStartupLocation = WindowStartupLocation.Manual;
                w0.Left = 40;
                w0.Top = 40;
                w0.Show();
                w0.UpdateLayout();

                var frame0 = new DispatcherFrame();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame0.Continue = false));
                Dispatcher.PushFrame(frame0);

                var root0 = (FrameworkElement)w0.Content;
                int pw0 = Math.Max(1, (int)Math.Ceiling(root0.ActualWidth));
                int ph0 = Math.Max(1, (int)Math.Ceiling(root0.ActualHeight));
                var rtb0 = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    pw0, ph0, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb0.Render(root0);

                var enc0 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc0.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb0));
                using (var fs0 = File.Create(e.Args[2])) enc0.Save(fs0);

                File.WriteAllText(shotLog, $"OK {pw0}x{ph0} ({e.Args[1]})");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(shotLog, "FAIL " + ex); } catch { }
            }
            CleanupDiagTempNote();
            Shutdown(0);
            return;
        }

        // 诊断：--editor-shot <输出png> 打开「所见即所得排版」窗口并截图后退出（供自动化核对渲染效果）
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--editor-shot", StringComparison.OrdinalIgnoreCase))
        {
            string shotLog = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.editorshot.log");
            try
            {
                var w = new BackdropEditorWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 40,
                    Top = 40
                };
                w.Show();
                w.UpdateLayout();

                var frame = new DispatcherFrame();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                var root = (FrameworkElement)w.Content;
                int pw = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
                int ph = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(root);

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Create(e.Args[1])) enc.Save(fs);

                File.WriteAllText(shotLog, $"OK {pw}x{ph}");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(shotLog, "FAIL " + ex); } catch { }
            }
            Shutdown(0);
            return;
        }

        // 诊断：--ui-check 逐个构造所有窗口（XAML 解析 / 控件名 / 初始化取值全部走一遍）后退出，
        // 结果写入临时目录 ClassBulletinApp.uicheck.log，便于自动化回归检查。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--ui-check", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder();
            void Check(string name, Func<Window> factory)
            {
                try
                {
                    var w = factory();
                    sb.AppendLine("OK   " + name);
                    try { w.Close(); } catch { }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("FAIL " + name + " :: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Check("MainWindow", () => new MainWindow());
            Check("LoginWindow", () => new LoginWindow());
            Check("RetrievePasswordWindow", () => new RetrievePasswordWindow());
            Check("LayoutWindow", () => new LayoutWindow { InitialTabIndex = 2 });
            Check("HomeworkWindow", () => new HomeworkWindow());
            Check("MissingWindow", () => new MissingWindow());
            Check("MissingEditWindow", () => new MissingEditWindow(Store.LoadMissingBoard()));
            Check("HomeworkEditWindow", () => new HomeworkEditWindow(Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard()));
            Check("BackdropEditorWindow", () => new BackdropEditorWindow());
            Check("QuickLaunchWindow", () => new QuickLaunchWindow());
            Check("CustomNoteEditWindow", () => new CustomNoteEditWindow(DiagCustomNoteId()));
            Check("CustomNoteWindow", () => new CustomNoteWindow(DiagCustomNoteId()));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.uicheck.log"), sb.ToString());
            }
            catch { }
            CleanupDiagTempNote();
            Shutdown(0);
            return;
        }

        // 诊断：--editor-open 直接打开「所见即所得排版」窗口（便于自动化交互测试 / 单独调试排版）
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--editor-open", StringComparison.OrdinalIgnoreCase))
        {
            var win0 = new BackdropEditorWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 60
            };
            MainWindow = win0;
            win0.Show();
            return;
        }

        // 应用用户选择的主题配色（标题栏 / 按钮 / 强调色）
        try { ThemeManager.Apply(Store.LoadSettings().Theme); }
        catch { }

        // 登录：未登录（关闭登录窗）直接退出；游客模式不持久化任何数据
        try
        {
            var login = new LoginWindow();
            if (login.ShowDialog() != true) { Shutdown(0); return; }
            Store.EnablePersistence = !login.IsGuest;
            if (!login.IsGuest)
            {
                Store.TryWeeklyOverrideReset();   // 每周首次启动清空全部调代课
                var s = Store.LoadSettings();     // F9：记住上次登录的用户名，下次预填
                s.LastUsername = login.EnteredUser;
                Store.SaveSettings(s);
            }
        }
        catch (Exception ex)
        {
            LogCrash("Login", ex);
            try { System.Windows.Forms.MessageBox.Show("登录失败：" + ex.Message, "班级公布栏", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); } catch { }
            Shutdown(1);
            return;
        }

        // 启动主控制面板（原来的 StartupUri 改为手动创建，便于支持上面的诊断模式）
        try
        {
            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow startup", ex);
            Shutdown(1);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception);
        try
        {
            System.Windows.Forms.MessageBox.Show(
                "程序运行过程中遇到错误：\n" + e.Exception.Message +
                "\n\n完整堆栈已记录到 ClassBulletinApp.crash.log（位于系统临时目录）。",
                "班级公布栏 - 运行错误", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch { }
        e.Handled = true; // 保持程序继续运行，避免闪退
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception("未知错误");
        LogCrash("UnhandledException (IsTerminating=" + e.IsTerminating + ")", ex);
        try
        {
            System.Windows.Forms.MessageBox.Show(
                "程序遇到严重错误：\n" + ex.Message +
                "\n\n完整堆栈已记录到 ClassBulletinApp.crash.log（位于系统临时目录）。",
                "班级公布栏 - 严重错误", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch { }
    }

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.crash.log");
            TrimIfTooLarge(path);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}\n{ex}\n\n");
        }
        catch { }
    }

    /// <summary>
    /// 诊断用：返回一个可用于构造 CustomNoteWindow 的便签 Id。
    /// 若当前没有自定义便签，则临时写入一张并在 CleanupDiagTempNote 中移除，避免污染用户数据。
    /// </summary>
    private static string? _diagTempNoteId;

    private static string DiagCustomNoteId()
    {
        var s = Store.LoadSettings();
        if (s.CustomNotes.Count > 0) return s.CustomNotes[0].Id;
        var note = new ScheduleCore.Models.CustomNote
        {
            Title = "界面检查临时便签",
            Content = "（自动生成，仅用于界面检查）",
            Theme = "plain"
        };
        s.CustomNotes.Add(note);
        Store.SaveSettings(s);
        _diagTempNoteId = note.Id;
        return note.Id;
    }

    private static void CleanupDiagTempNote()
    {
        if (_diagTempNoteId == null) return;
        try
        {
            var s = Store.LoadSettings();
            s.CustomNotes.RemoveAll(x => x.Id == _diagTempNoteId);
            Store.SaveSettings(s);
        }
        catch { }
        finally { _diagTempNoteId = null; }
    }

    /// <summary>日志超过 1MB 时只保留末尾 256KB，避免反复写日志把临时目录撑大。</summary>
    private static void TrimIfTooLarge(string path)
    {
        const long maxBytes = 1024 * 1024;
        const long keepBytes = 256 * 1024;
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length <= maxBytes) return;

        var tail = new char[keepBytes];
        using (var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            sr.BaseStream.Seek(-keepBytes, SeekOrigin.End);
            sr.Read(tail, 0, tail.Length);
        }
        File.WriteAllText(path, new string(tail));
    }
}
