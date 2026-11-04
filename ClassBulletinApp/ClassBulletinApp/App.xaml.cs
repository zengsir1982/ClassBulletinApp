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

        // 诊断 / 测试：CB_DATA_DIR 可把数据目录指到别处。
        // 典型用法：复制一份真实配置到临时目录再跑截图 / 渲染，既能看到真实数据，又不会改动用户的 settings.json。
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("CB_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(dataDir)) Store.SetAppDir(dataDir!.Trim().Trim('"'));
        }
        catch { }

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

            // 环境变量 CB_DIAG_ROLE=student|teacher|subject|admin 可换预览身份：
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
            // 班主任 / 科任老师身份要真的能改到某个班，否则截图里连本班都是灰的，看不出区别
            if (diagRole == ScheduleCore.Models.UserRole.Teacher
                || diagRole == ScheduleCore.Models.UserRole.SubjectTeacher)
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

        // 诊断：--dir-check <路径> 把"路径到底能不能读"的每一步都打出来。
        // 起因：Directory.Exists 对 UNC 共享会吞异常返回 false，导致"目录明明能进、程序却说不可达"，
        // 光看结果分不清是路径错、权限错还是网络不通，这里把 Exists / 列目录 / 异常类型分三行报。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--dir-check", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder();
            var p2 = e.Args[1];
            sb.AppendLine("path=" + p2 + "  len=" + p2.Length);
            sb.AppendLine("dotnet_dir_exists=" + Directory.Exists(p2));
            try
            {
                var ents = Directory.GetFileSystemEntries(p2);
                sb.AppendLine("getentries=ok count=" + ents.Length);
            }
            catch (Exception ex)
            {
                sb.AppendLine("getentries=FAIL " + ex.GetType().Name + " hresult=" + ex.HResult
                    + " msg=" + ex.Message);
            }
            try
            {
                var di = new DirectoryInfo(p2);
                sb.AppendLine("directoryinfo_exists=" + di.Exists);
            }
            catch (Exception ex) { sb.AppendLine("directoryinfo=FAIL " + ex.Message); }

            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.dircheck.log"), sb.ToString()); }
            catch { }
            Shutdown(0);
            return;
        }

        // 诊断：--cred-check 验证「凭据管理器读写」这条通路是否正常（写入 / 读回 / 删除 往返）。
        // ★ 一律用假目标名和假密码，绝不碰真实共享凭据：这个命令是给现场排查用的，
        //   不能因为它把用户已经存好的账号密码覆盖掉。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--cred-check", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder();
            const string target = "CB_DIAG_SELFCHECK";
            const string user = "diaguser";
            const string pwd = "diag-p@ss-123";
            try
            {
                SmbCredential.Write(target, user, pwd);
                var back = SmbCredential.ReadUser(target);
                SmbCredential.Delete(target);
                var after = SmbCredential.ReadUser(target);

                sb.AppendLine($"write=ok  read={(back ?? "null")}  read_matches={string.Equals(back, user, StringComparison.Ordinal)}");
                sb.AppendLine($"delete=ok  after_delete={(after ?? "null")}");

                // 可选第二个参数：顺手探一次共享路径，验证"权限不足"和"路径不存在"能不能分开报
                if (e.Args.Length >= 2)
                {
                    // 先实测"断开旧连接"：换凭据后必须断开，否则测出来的是旧账号的结果。
                    // 这一步也会让 P/Invoke 签名在真机上跑一遍（签名错会直接抛异常，比上线才发现好）。
                    bool disconnected = SmbCredential.DisconnectHost(e.Args[1]);
                    sb.AppendLine($"disconnect({e.Args[1]}) = {disconnected}");

                    var (ok, msg, cnt, canWrite) = SmbCredential.Probe(e.Args[1]);
                    sb.AppendLine($"probe({e.Args[1]}) ok={ok} xlsx={cnt} writable={canWrite}");
                    sb.AppendLine("  " + msg.Replace("\n", "\n  "));

                    // 可选第三个参数 map：真建一个盘符再删掉，验证映射功能（会临时出现在"此电脑"里）
                    if (e.Args.Length >= 3 && string.Equals(e.Args[2], "map", StringComparison.OrdinalIgnoreCase))
                    {
                        var (mok, mmsg, drive) = SmbCredential.MapDrive(e.Args[1]);
                        sb.AppendLine($"map={mok} drive={drive} msg={mmsg}");
                        if (mok)
                        {
                            sb.AppendLine($"  drive_readable={Directory.Exists(drive + "\\")}");
                            sb.AppendLine($"  unmap={SmbCredential.UnmapDrive(drive)}");
                            sb.AppendLine($"  after_unmap={Directory.Exists(drive + "\\")}");
                        }
                    }
                }

                sb.AppendLine(string.Equals(back, user, StringComparison.Ordinal) && after == null ? "RESULT=PASS" : "RESULT=FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"RESULT=FAIL {ex.GetType().Name}: {ex.Message}");
            }
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.credcheck.log"), sb.ToString()); }
            catch { }
            Shutdown(0);
            return;
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
                // 诊断模式若指向数据副本（CB_DATA_DIR 非空），允许写盘，便于验收
                // "迁移 / 设置改动确实落盘"。未设 CB_DATA_DIR 时保持不写盘，绝不污染真实设置。
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CB_DATA_DIR")))
                    Store.EnablePersistence = true;

                Window w0 = e.Args[1].ToLowerInvariant() switch
                {
                    "editor" => new BackdropEditorWindow(),
                    "homeworkedit" => new HomeworkEditWindow(Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard()),
                    "missingedit" => new MissingEditWindow(Store.LoadMissingBoard()),
                    "schedulewin" => new ScheduleWindow(),
                    "schedulewinfail" => DiagScheduleUpdateFail(),
                    "schedulewinuptodate" => DiagScheduleUpToDate(),
                    "notifywin" => new NotificationWindow(),
                    "homeworkwin" => new HomeworkWindow(),
                    "missingwin" => new MissingWindow(),
                    "main" => new MainWindow(),
                    "maindata" => new MainWindow { InitialTabIndex = 1 },
                    "maindisplay" => new MainWindow { InitialTabIndex = 2 },
                    "mainnotice" => new MainWindow { InitialTabIndex = 4 },
                    "mainhw" => new MainWindow { InitialTabIndex = 5 },
                    "mainuser" => new MainWindow { InitialTabIndex = 6 },
                    "mainabout" => new MainWindow { InitialTabIndex = 7 },
                    "login" => new LoginWindow(),
                    "layout0" => new LayoutWindow { InitialTabIndex = 0 },
                    "layout1" => new LayoutWindow { InitialTabIndex = 1 },
                    "quicklaunch" => new QuickLaunchWindow(),
                    "customnoteedit" => new CustomNoteEditWindow(DiagCustomNoteId()),
                    "customnote" => new CustomNoteWindow(DiagCustomNoteId()),
                    "countdownwin" => new CountdownWindow(),
                    "fullscreenremind" => DiagFullScreenReminder(),
                    "mainremind" => new MainWindow { InitialTabIndex = 3 },
                    "mainremindend" => new MainWindow { InitialTabIndex = 3, ReminderScrollEnd = true },
                    "schedulewinflash" => DiagScheduleFlash(),
                    "remindpopup" => DiagReminderPopup(),
                    "remindlayout" => DiagReminderLayout(),
                    "remindlayoutsave" => DiagReminderLayoutSave(),
                    "countdownedit" => new CountdownEditWindow(),
                    "dutywin" => new DutyWindow(),
                    "dutyedit" => new DutyEditWindow(),
                    "backupwin" => new BackupWindow(),
                    "updatesettings" => new UpdateSettingsWindow(),
                    "sharewizard" => new ShareAccessWizardWindow(),
                    "sharewizard1" => new ShareAccessWizardWindow { InitialStep = 1 },
                    "sharewizard2" => new ShareAccessWizardWindow { InitialStep = 2 },
                    "clearoverrides" => new ClearOverridesWindow(),
                    "suspended" => new SuspendedOverridesWindow(),
                    "usermanager" => new UserManagerWindow(),
                    "useredit" => new UserEditWindow(null, new ScheduleCore.Models.UserBook(), new[] { "八(22)班", "八(23)班" }),
                    "rolepolicy" => new RolePolicyWindow(Store.LoadUsers()),
                    "traymenu" => new TrayMenuWindow(),
                    "about" => new AboutWindow(),
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

                // 可选等待：CB_SHOT_WAIT_MS=8000 让窗口先跑一会儿再截图。
                // 有些状态是异步才出现的（比如启动后探测共享目录、网络超时后的红字提示），
                // 不等就截只能拍到初始画面，会误判成"功能没生效"。
                // 用 DispatcherFrame 而不是 Thread.Sleep：消息泵要继续跑，async 回调才有机会执行。
                if (int.TryParse(Environment.GetEnvironmentVariable("CB_SHOT_WAIT_MS"), out int shotWait)
                    && shotWait > 0)
                {
                    var waitFrame = new DispatcherFrame();
                    var waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(shotWait) };
                    waitTimer.Tick += (_, _) => { waitTimer.Stop(); waitFrame.Continue = false; };
                    waitTimer.Start();
                    Dispatcher.PushFrame(waitFrame);
                    w0.UpdateLayout();
                }

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

        // 诊断：--test-backdrop-off <日志路径> 验证「取消背景 → 恢复悬浮窗」整条链路后退出。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--test-backdrop-off", StringComparison.OrdinalIgnoreCase))
        {
            RunBackdropOffDiag(e.Args[1]);
            return;
        }

        // 诊断：--check-update 联网检查 Gitee 上的最新发布版本（不发请求给数据源），
        // 结果写到临时目录 ClassBulletinApp.updcheck.log 后退出。用于验证「关于 → 检查更新」的取值。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--check-update", StringComparison.OrdinalIgnoreCase))
        {
            RunUpdateCheckDiag();
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
            Check("CountdownWindow", () => new CountdownWindow());
            Check("FullScreenReminderWindow", () => new FullScreenReminderWindow());
            Check("ReminderPopup", () => new ReminderPopup());
            Check("ReminderPopup(编辑)", () => new ReminderPopup(interactive: true));
            Check("CountdownEditWindow", () => new CountdownEditWindow());
            Check("DutyWindow", () => new DutyWindow());
            Check("DutyEditWindow", () => new DutyEditWindow());
            Check("BackupWindow", () => new BackupWindow());
            Check("UpdateSettingsWindow", () => new UpdateSettingsWindow());
            Check("ShareAccessWizardWindow", () => new ShareAccessWizardWindow());
            Check("ClearOverridesWindow", () => new ClearOverridesWindow());
            Check("SuspendedOverridesWindow", () => new SuspendedOverridesWindow());
            Check("UserManagerWindow", () => new UserManagerWindow());
            Check("UserEditWindow", () => new UserEditWindow(null,
                new ScheduleCore.Models.UserBook(), new[] { "八(22)班" }));
            Check("RolePolicyWindow", () => new RolePolicyWindow(Store.LoadUsers()));
            Check("TrayMenuWindow", () => new TrayMenuWindow());
            Check("AboutWindow", () => new AboutWindow());
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

    /// <summary>
    /// 诊断：悬浮课表的「更新课表」按钮切到失败态后截图，用来核对红字「更新失败」的样式
    /// （正常情况要等真的更新失败才能看到，截图流程里没法触发）。
    /// </summary>
    /// <summary>诊断：提醒小窗（按当前存档的位置与大小显示，便于核对缩放效果）。</summary>
    private static Window DiagReminderPopup()
    {
        var w = new ReminderPopup();
        w.Loaded += (_, _) => w.ShowReminder("课表有变化",
            "第3节 数学 → 英语；第5节 体育 → 数学", 600, Store.LoadSettings().Reminder);
        return w;
    }

    /// <summary>诊断：提醒小窗的「调整位置与大小」模式（带提示条与保存按钮）。</summary>
    private static Window DiagReminderLayout()
    {
        var w = new ReminderPopup(interactive: true);
        w.Loaded += (_, _) => w.BeginLayoutEdit(
            Store.LoadSettings().Reminder ?? new ScheduleCore.Models.ReminderSettings(), _ => { });
        return w;
    }

    /// <summary>
    /// 诊断：走一遍「调整小窗位置与大小 → 保存」的完整链路（模拟拖到 180%、移到 240,200），
    /// 用于确认值真的落到 settings.json。
    /// </summary>
    private static Window DiagReminderLayoutSave()
    {
        Store.EnablePersistence = true;   // --shot 默认关写盘，这里要真的落盘才能验收
        var host = new ReminderPopup(interactive: true);
        var r = Store.LoadSettings().Reminder ?? new ScheduleCore.Models.ReminderSettings();
        host.Loaded += (_, _) =>
        {
            host.BeginLayoutEdit(r, saved =>
            {
                var s2 = Store.LoadSettings();
                s2.Reminder ??= new ScheduleCore.Models.ReminderSettings();
                s2.Reminder.PopupScale = saved.PopupScale;
                s2.Reminder.PopupLeft = saved.PopupLeft;
                s2.Reminder.PopupTop = saved.PopupTop;
                Store.SaveSettings(s2);
            });

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    host.DiagApplyScale(1.8);
                    host.Left = 240;
                    host.Top = 200;
                    host.UpdateLayout();     // 让缩放后的 ActualWidth 先生效，模拟用户拖完再点保存
                    host.DiagCommit();
                }
                catch { }
            };
            timer.Start();
        };
        host.Show();

        // 陪跑窗口：截图它即可，真正被核对的是 settings.json
        return new Window
        {
            Width = 360,
            Height = 120,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "save layout diag",
                Margin = new Thickness(16),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>诊断：全屏变化提示（带示例内容，便于核对排版）。</summary>
    private static Window DiagFullScreenReminder()
    {
        var w = new FullScreenReminderWindow();
        w.Loaded += (_, _) => w.ShowReminder("课表已更新",
            "第3节 数学 → 英语；第5节 体育 → 数学；第7节 新增 音乐", 60);
        return w;
    }

    /// <summary>诊断：悬浮课表状态栏的「课表有变化」红字。</summary>
    private static Window DiagScheduleFlash()
    {
        var w = new ScheduleWindow();
        w.Loaded += (_, _) => w.SimulateChangeFlash("第3节 数学 → 英语　第5节 体育 → 数学", 10);
        return w;
    }

    private static Window DiagScheduleUpdateFail()
    {
        var w = new ScheduleWindow();
        w.Loaded += (_, _) => w.SimulateQaFailure("模拟：数据源不可用，本次更新失败（诊断截图用）。");
        return w;
    }

    /// <summary>诊断截图：模拟"已是最新版本"，检查状态栏的红色提示与 10 秒自动消失。</summary>
    private static Window DiagScheduleUpToDate()
    {
        var w = new ScheduleWindow();
        w.Loaded += (_, _) => w.SimulateUpToDate();
        return w;
    }

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

    /// <summary>
    /// 诊断：验证「取消背景 → 恢复悬浮窗」。造一份"已经设为背景"的状态，跑一遍
    /// <see cref="BackdropPin.Unpin"/>，把取消后的开关 / 窗口可见性写进日志。
    /// </summary>
    private void RunBackdropOffDiag(string logPath)
    {
        var sb = new System.Text.StringBuilder();
        void Log(string line)
        {
            sb.AppendLine(line);
            try { File.WriteAllText(logPath, sb.ToString()); } catch { }
        }

        void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        MainWindow? w = null;
        try
        {
            // ★ 诊断模式默认不写盘（EnablePersistence=false），这个测试恰恰要验证"写盘后的状态"，
            //   所以临时打开 —— 但只允许在 CB_DATA_DIR 指向的数据副本上跑，绝不碰真实设置。
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CB_DATA_DIR")))
            {
                Log("请在 CB_DATA_DIR 指向数据副本的情况下运行本测试（避免改到真实设置）。");
                Shutdown(0);
                return;
            }
            Store.EnablePersistence = true;

            // ① 造状态：课表 + 作业画在壁纸上，并记下"当初藏了哪些窗口"
            var s = Store.LoadSettings();
            s.Backdrop ??= new ScheduleCore.Models.BackdropSettings();
            s.Backdrop.ShowSchedule = true;
            s.Backdrop.ShowHomework = true;
            s.Backdrop.ShowMissing = false;
            string noteId = s.CustomNotes.FirstOrDefault()?.Id ?? "";
            if (noteId.Length > 0) s.Backdrop.EntryOf(noteId).Visible = true;
            s.Backdrop.PinnedRestores = new List<string> { "Schedule", "Homework" };
            if (noteId.Length > 0) s.Backdrop.PinnedRestores.Add("note:" + noteId);

            // 启动时自动显示的窗口一律关掉，避免干扰"取消背景放回来的窗口"判断
            s.AutoShowSchedule = false;
            s.AutoShowNotification = false;
            s.AutoShowHomework = false;
            s.AutoShowMissing = false;
            Store.SaveSettings(s);
            Log($"① 准备：ShowSchedule={s.Backdrop.ShowSchedule} ShowHomework={s.Backdrop.ShowHomework} note={noteId}");

            w = new MainWindow { ShowInTaskbar = false };
            w.Show();
            Pump(800);

            bool ok = BackdropPin.Unpin(out string msg);
            Log($"② Unpin={ok}　{msg}");
            Pump(800);

            var s2 = Store.LoadSettings();
            Log($"③ 取消后开关：ShowSchedule={s2.Backdrop?.ShowSchedule} ShowHomework={s2.Backdrop?.ShowHomework} " +
                $"便签Visible={(noteId.Length > 0 ? s2.Backdrop?.EntryOf(noteId).Visible.ToString() : "-")} " +
                $"PinnedRestores={s2.Backdrop?.PinnedRestores?.Count ?? 0} AutoHide={s2.ScheduleLayout?.AutoHide}");

            foreach (Window win in System.Windows.Application.Current.Windows)
            {
                if (ReferenceEquals(win, w)) continue;
                Log($"④ 窗口 {win.GetType().Name} IsVisible={win.IsVisible}");
            }
        }
        catch (Exception ex)
        {
            Log("FAIL " + ex);
        }
        finally
        {
            try { w?.Close(); } catch { }
            Shutdown(0);
        }
    }

    /// <summary>
    /// 诊断：<c>--check-update</c> 联网检查 Gitee 上的最新发布版本，结果写入
    /// <c>%TEMP%\ClassBulletinApp.updcheck.log</c>。只读，不改任何设置、不动数据源。
    /// </summary>
    private async void RunUpdateCheckDiag()
    {
        string log = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.updcheck.log");
        try
        {
            var (ok, msg, info) = await UpdateChecker.CheckAsync();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Ok=" + ok);
            sb.AppendLine("Message=" + msg);
            if (info != null)
            {
                sb.AppendLine("Tag=" + info.Tag);
                sb.AppendLine("Name=" + info.Name);
                sb.AppendLine("IsNewer=" + info.IsNewer);
                sb.AppendLine("PageUrl=" + info.PageUrl);
                sb.AppendLine("AssetUrl=" + info.AssetUrl);
                sb.AppendLine("AssetName=" + info.AssetName);
                sb.AppendLine("BodyLen=" + (info.Body?.Length ?? 0));
            }
            File.WriteAllText(log, sb.ToString());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(log, "FAIL " + ex); } catch { }
        }
        Shutdown(0);
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
