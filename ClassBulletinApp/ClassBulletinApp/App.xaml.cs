using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 是否为诊断 / 截图模式（<c>--shot</c>、<c>--ui-check</c>、<c>--render-backdrop</c> 等一律以 <c>--</c> 开头）。
    /// ★ 这类模式绝不能弹模态对话框：自动化流程会卡在一个没人点的确认框上，直到超时。
    /// </summary>
    public static bool IsDiagnostic { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        IsDiagnostic = e.Args.Length >= 1
                       && e.Args[0].StartsWith("--", StringComparison.Ordinal);

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

        // 诊断 / 预览模式给一个临时管理员身份：界面上的按钮是否可用取决于角色，
        // 不给身份的话截图里全是灰按钮，看不出真实效果。诊断模式下 EnablePersistence=false，不写盘。
        if (e.Args.Length >= 1 && e.Args[0].StartsWith("--", StringComparison.Ordinal))
        {
            // 诊断模式不写任何文件：否则跑一次截图就把用户表迁移做了、下次启动身份都变了
            Store.SuppressWrites = true;

            // 环境变量 CB_DIAG_ROLE=student|teacher|admin 可换预览身份：
            // 受限身份的界面（灰按钮 / tooltip）必须能截出来目视确认，
            // 否则"管理员截图里按钮全是亮的"，权限是不是真生效了看不出来。
            string diagRole = (Environment.GetEnvironmentVariable("CB_DIAG_ROLE") ?? "").Trim().ToLowerInvariant();
            if (!ScheduleCore.Models.UserRole.IsValid(diagRole))
                diagRole = ScheduleCore.Models.UserRole.Admin;

            var diagUser = new ScheduleCore.Models.AppUser
            {
                Username = "diag",
                DisplayName = "诊断预览",
                Role = diagRole
            };
            // 班主任身份要真的能改到某个班，否则截图里连本班都是灰的，看不出区别
            if (diagRole == ScheduleCore.Models.UserRole.Teacher)
            {
                try
                {
                    var sc = Store.LoadSchedule();
                    if (sc != null && sc.Classes.Count > 0)
                        diagUser.ClassNames.Add(sc.Classes[0].ClassName);
                }
                catch { }
            }
            UserSession.SignIn(diagUser);
        }

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
                    "maindata" => new MainWindow { InitialTabIndex = 1 },
                    "maindisplay" => new MainWindow { InitialTabIndex = 2 },
                    "mainnotice" => new MainWindow { InitialTabIndex = 4 },
                    "mainhw" => new MainWindow { InitialTabIndex = 5 },
                    "mainuser" => new MainWindow { InitialTabIndex = 6 },
                    "login" => new LoginWindow(),
                    "layout0" => new LayoutWindow { InitialTabIndex = 0 },
                    "layout1" => new LayoutWindow { InitialTabIndex = 1 },
                    "quicklaunch" => new QuickLaunchWindow(),
                    "customnoteedit" => new CustomNoteEditWindow(DiagCustomNoteId()),
                    "customnote" => new CustomNoteWindow(DiagCustomNoteId()),
                    "backupwin" => new BackupWindow(),
                    "updatesettings" => new UpdateSettingsWindow(),
                    "clearoverrides" => new ClearOverridesWindow(),
                    "suspended" => new SuspendedOverridesWindow(),
                    "usermanager" => new UserManagerWindow(),
                    "useredit" => new UserEditWindow(null, new ScheduleCore.Models.UserBook(), new[] { "八(22)班", "八(23)班" }),
                    "rolepolicy" => new RolePolicyWindow(Store.LoadUsers()),
                    "upgradenotice" => new UpgradeNoticeWindow(
                        "1.0.4.0",
                        UpgradeNoticeWindow.BuildItems(new AppSettingsSnapshot(0, "2026-10-31 23:40"), "1.0.0.0")),
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
                w0.UpdateLayout();   // 再量一次：Show 后首帧拿到的 ActualWidth/Height 可能还是期望值，会把图裁小

                var root0 = (FrameworkElement)w0.Content;
                // 截图区域要算上根元素的 Margin（UpdateSettingsWindow 根是 Margin=18 的 DockPanel，
                // 只按 ActualWidth/Height 开画布会把右、下各切掉 18px，看起来像按钮被裁一半）。
                int pw0 = Math.Max(1, (int)Math.Ceiling(root0.RenderSize.Width + root0.Margin.Left + root0.Margin.Right));
                int ph0 = Math.Max(1, (int)Math.Ceiling(root0.RenderSize.Height + root0.Margin.Top + root0.Margin.Bottom));
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
            string tracePath = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.uicheck.trace.log");
            try { File.WriteAllText(tracePath, ""); } catch { }

            void Check(string name, Func<Window> factory)
            {
                // 每步先落盘：万一某个窗口构造时卡住，日志能指到是哪一个
                try { File.AppendAllText(tracePath, "> " + name + "\n"); } catch { }
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
            Check("BackupWindow", () => new BackupWindow());
            Check("UpdateSettingsWindow", () => new UpdateSettingsWindow());
            Check("ClearOverridesWindow", () => new ClearOverridesWindow());
            Check("SuspendedOverridesWindow", () => new SuspendedOverridesWindow());
            Check("UserManagerWindow", () => new UserManagerWindow());
            Check("UserEditWindow", () => new UserEditWindow(null,
                new ScheduleCore.Models.UserBook(), new[] { "八(22)班" }));
            Check("RolePolicyWindow", () => new RolePolicyWindow(Store.LoadUsers()));
            Check("UpgradeNoticeWindow", () => new UpgradeNoticeWindow(
                "1.0.4.0",
                UpgradeNoticeWindow.BuildItems(new AppSettingsSnapshot(0, "2026-10-31 23:40"), "1.0.0.0")));
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

        // 诊断：--login-state 只判断"这次启动会不会自动登录"，写一行日志后退出（不开任何窗口）
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--login-state", StringComparison.OrdinalIgnoreCase))
        {
            string log = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.login.log");
            try
            {
                var st = Store.LoadSettings();
                var bk = Store.LoadUsers();
                var au = ShouldAutoLogin(st, bk);
                File.WriteAllText(log,
                    $"AutoLogin={st.AutoLogin} AutoLoginUser={st.AutoLoginUser ?? ""} " +
                    $"HasPassword={!string.IsNullOrEmpty(st.AutoLoginPassword)} " +
                    $"Role={(au == null ? "-" : au.Role)} " +
                    $"→ 本次自动登录={(au != null ? "True" : "False")}");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(log, "FAIL " + ex.Message); } catch { }
            }
            Shutdown(0);
            return;
        }

        // 应用用户选择的主题配色（标题栏 / 按钮 / 强调色）
        try { ThemeManager.Apply(Store.LoadSettings().Theme); }
        catch { }

        // 登录：优先用「自动登录账号」（只能是班主任 / 电教管理员）直接进入；
        // 否则弹登录窗。未登录（关闭登录窗）直接退出；游客模式不持久化任何数据。
        try
        {
            var st0 = Store.LoadSettings();
            var book = Store.LoadUsers();
            var autoUser = ShouldAutoLogin(st0, book);

            if (autoUser != null)
            {
                // 直接以保存的受限账号进入（管理员永不参与自动登录，见 ShouldAutoLogin）
                UserSession.SignIn(autoUser);
                Store.EnablePersistence = true;
            }
            else
            {
                var login = new LoginWindow();
                if (login.ShowDialog() != true) { Shutdown(0); return; }
                Store.EnablePersistence = !login.IsGuest;
                if (login.User != null) UserSession.SignIn(login.User);
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

    /// <summary>
    /// 是否直接以已保存的账号自动登录（跳过登录窗）。返回 null = 不自动登录。
    /// <para>必须同时满足：开了自动登录、自动登录账号存在且启用、
    /// <b>不是管理员</b>、保存的密码校验通过。</para>
    /// <para>★ 「不是管理员」是硬规则：教室机开机自动进入，如果自动登录的是管理员，
    /// 谁坐到电脑前谁就有管理员权限，整套角色就白做了。</para>
    /// </summary>
    private static ScheduleCore.Models.AppUser? ShouldAutoLogin(
        ScheduleCore.Models.AppSettings s, ScheduleCore.Models.UserBook book)
    {
        if (s == null || !s.AutoLogin) return null;
        if (string.IsNullOrWhiteSpace(s.AutoLoginUser)) return null;

        var u = book.Find(s.AutoLoginUser);
        if (u == null || !u.Enabled) return null;
        if (u.Role == ScheduleCore.Models.UserRole.Admin) return null;

        return ScheduleCore.Services.PasswordHasher.Verify(s.AutoLoginPassword, u.PasswordHash, u.Salt)
            ? u : null;
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
