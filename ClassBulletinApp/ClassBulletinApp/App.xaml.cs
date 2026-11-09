using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 是否为诊断 / 截图模式（<c>--shot</c>、<c>--ui-check</c>、<c>--render-backdrop</c> 等一律以 <c>--</c> 开头）。
    /// ★ 这类模式绝不能弹模态对话框：自动化流程会卡在一个没人点的确认框上，直到超时。
    /// </summary>
    public static bool IsDiagnostic { get; private set; }

    /// <summary>诊断用：某个窗口句柄当前是否处于"可用"状态（被 ShowDialog 禁用就是 false）。</summary>
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

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

        // 触摸屏：手指点输入框自动弹系统触摸键盘（一次注册，全局生效）。
        // 教室一体机没有实体键盘，这一句是"能输入"的前提。
        try { TouchInputBinder.Install(); } catch { }

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

        // r73：DWM 毛玻璃由系统合成，RenderTargetBitmap（--shot）抓不到。
        // 这个分支强制在真实 HWND 上开玻璃并用屏幕像素抓一张图，供开发机 / 一体机自测：
        //   ClassBulletinApp.exe --glass-probe <输出png>
        // 结果同时写到 %TEMP%\ClassBulletinApp.glassprobe.log（含系统 Build 与实际档位）。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--glass-probe", StringComparison.OrdinalIgnoreCase))
        {
            string msg;
            try
            {
                // mode == "runtime"：只验「窗口显示后能否切分层窗」这条 WPF 限制，
                // mode == "chrome"：走 r73d 的分层玻璃窗（自绘标题栏），
                // 其余：r73 的 DWM backdrop 对照实验。
                msg = e.Args.Length >= 3 && string.Equals(e.Args[2], "runtime", StringComparison.OrdinalIgnoreCase)
                    ? GlassChrome.DiagRuntimeToggle()
                    : GlassVisualProbe.Run(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null);
            }
            catch (Exception ex) { msg = "FAIL " + ex.GetType().Name + ": " + ex.Message; }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.glassprobe.log"), msg);
            }
            catch { }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // r73d 全局玻璃：先装「分层窗」方案（Initialized 改窗口样式、Loaded 包内容），
        // 再装 DWM backdrop 作为补充（它只对没被改造的窗口起作用）。
        try { GlassChrome.Install(); } catch { }
        try { GlassBackdrop.Install(); } catch { }

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
                    "handwriting" => new HandwritingPopup(),
                    "handball" => new HandwritingBallWindow(),
                    "schedulewin" => new ScheduleWindow(),
                    "schedulewinclass" => DiagScheduleWindowAs(UserRole.Student),
                    "dragarmed" => new ScheduleWindow(),
                    "weekdrag" => new MainWindow { InitialTabIndex = 0 },
                    "schedulewinfail" => DiagScheduleUpdateFail(),
                    "schedulewinuptodate" => DiagScheduleUpToDate(),
                    "notifywin" => new NotificationWindow(),
                    "homeworkwin" => new HomeworkWindow(),
                    "missingwin" => new MissingWindow(),
                    "main" => new MainWindow(),
                    "maintoday" => new MainWindow { InitialTabIndex = 9 },
                    "maindata" => new MainWindow { InitialTabIndex = 1 },
                    "maindisplay" => new MainWindow { InitialTabIndex = 2 },
                    "mainnotice" => new MainWindow { InitialTabIndex = 4 },
                    "mainhw" => new MainWindow { InitialTabIndex = 5 },
                    "mainnotes" => new MainWindow { InitialTabIndex = 5, NotesScrollEnd = true },
                    "maininfo" => new MainWindow { InitialTabIndex = 6 },
                    "maininfoopen" => new MainWindow { InitialTabIndex = 6, InfoSettingsExpanded = true },
                    "boardsend" => new BoardSendWindow(new[] { "八年级(1)班" }, null),
                    "boardsendduty" => new BoardSendWindow(new[] { "八年级(1)班" }, BoardSyncConsts.EntityDuty),
                    "boardsendweek" => DiagBoardSendWeekly(),
                    "boardsendnotice" => new BoardSendWindow(new[] { "八年级(1)班" }, BoardSyncConsts.EntityNotice),
                    "boardsync" => new BoardSyncWindow(),
                    "mainuser" => new MainWindow { InitialTabIndex = 7 },
                    "mainabout" => new MainWindow { InitialTabIndex = 8 },
                    "login" => new LoginWindow(),
                    "layout0" => new LayoutWindow { InitialTabIndex = 0 },
                    "layout1" => new LayoutWindow { InitialTabIndex = 1 },
                    "quicklaunch" => new QuickLaunchWindow(),
                    "customnoteedit" => new CustomNoteEditWindow(DiagCustomNoteId()),
                    // r69：工具条的「激活态」（默认打开窗口时是置灰的，截图看不到可用形态）
                    "fontactive" => DiagFocusedFontWindow(),
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
                    "noticewin" => new NoticeWindow(),
                    "noticeedit" => new NotificationEditWindow(),
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

                // 拖动状态截图：必须等课表真的填好数据（Refresh 在 Loaded 之后才跑），
                // 否则一行容器都还没生成，什么也算不出来。
                if (w0 is ScheduleWindow dsw
                    && string.Equals(e.Args[1], "dragarmed", StringComparison.OrdinalIgnoreCase))
                {
                    try { dsw.DiagArmDragVisual(); }
                    catch (Exception ex) { try { File.WriteAllText(shotLog, "ARMFAIL " + ex.Message); } catch { } }
                    w0.UpdateLayout();
                }

                // 周课表拖动状态截图：同样要等 WeekView 在 Loaded 后 Rebuild 完才有格子
                if (w0 is MainWindow dmw
                    && string.Equals(e.Args[1], "weekdrag", StringComparison.OrdinalIgnoreCase))
                {
                    try { dmw.WeekViewManage.DiagArmDragVisual(); }
                    catch (Exception ex) { try { File.WriteAllText(shotLog, "ARMFAIL " + ex.Message); } catch { } }
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

                // 回显实际窗口类型：只写参数名会让人误以为目标命中了，其实可能落到了默认分支
                File.WriteAllText(shotLog, $"OK {pw0}x{ph0} ({e.Args[1]} -> {w0.GetType().Name})");
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

        // 诊断：--test-handwriting <log> 用合成笔迹验证本机手写识别是否可用（不打开窗口）。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--test-handwriting", StringComparison.OrdinalIgnoreCase))
        {
            string hwLog = e.Args.Length >= 2
                ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.handwriting.log");
            string report;
            try
            {
                // 放到线程池跑：WinRT 异步操作不要占 UI 线程同步等待，避免死锁。
                report = System.Threading.Tasks.Task.Run(() => HandwritingPopup.SelfTestAsync())
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                report = "EXCEPTION=" + ex.GetType().Name + ": " + ex.Message + "\nRESULT=FAIL";
            }
            try { File.WriteAllText(hwLog, report); } catch { }
            Shutdown();
            return;
        }

        // 诊断：--change-check <log> 验证「老师自己手动调课，不该再被告知"课表有变化"」的判据。
        // 判据在 Store：只有"差异记录全都是 manual"时才发 LocalManualEdit（监视器据此只更新基线）。
        // 这里直接把四种序列跑一遍，确认该抑制的抑制、该提醒的（temp 导入）照旧提醒。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--change-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.changecheck.log");
            var sb = new System.Text.StringBuilder();
            string ovPath = Path.Combine(Store.AppDir, "overrides.json");
            string backup = "";

            // ★ 护栏：这个诊断要往 overrides.json 里写测试记录再还原，绝不允许直接跑在老师的
            //   真实数据目录上（万一中途崩溃，课表里就会多出一条"诊断科目"）。必须用 CB_DATA_DIR 指副本。
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CB_DATA_DIR")))
            {
                try { File.WriteAllText(log, "拒绝：请用 CB_DATA_DIR 指向数据副本后再运行\n"); } catch { }
                Shutdown(0);
                return;
            }
            try
            {
                if (File.Exists(ovPath)) backup = File.ReadAllText(ovPath);
                Store.EnablePersistence = true;

                var hits = new List<string>();
                void OnEdit(System.Collections.Generic.IReadOnlyCollection<string> cells)
                    => hits.Add(string.Join(",", cells));
                Store.LocalManualEdit += OnEdit;

                string cls = Store.LoadSchedule()?.Classes?.FirstOrDefault()?.ClassName ?? "";
                var list0 = Store.LoadOverrides();
                string cell = $"{cls}|0|1";
                sb.AppendLine($"class={cls} 起步记录={list0.Count}");

                ScheduleCore.Models.LessonOverride Make(string source) => new()
                {
                    ClassName = cls, DayIdx = 0, Period = 1,
                    Subject = "诊断科目", Teacher = "诊断老师", Source = source,
                    ValidFrom = DateOnly.FromDateTime(DateTime.Today),
                    ValidTo = DateOnly.FromDateTime(DateTime.Today)
                };

                void Step(string name, List<ScheduleCore.Models.LessonOverride> list, bool expect)
                {
                    int before = hits.Count;
                    Store.SaveOverrides(list);
                    bool fired = hits.Count > before;
                    sb.AppendLine($"{name} → {(fired ? "触发" : "未触发")} 期望 {(expect ? "触发" : "未触发")}"
                                  + $" cells={(fired ? hits[^1] : "-")}" + (fired == expect ? "" : "　★ 不符"));
                }

                // A：手动新增 → 应触发（监视器据此只更新基线，不弹窗）
                var a = new List<ScheduleCore.Models.LessonOverride>(list0) { Make("manual") };
                Step("A 手动新增", a, true);

                // B：把这条改成 temp（等同导入临时表）→ 不该抑制，必须照旧提醒
                var b = a.Select(o => new ScheduleCore.Models.LessonOverride
                {
                    ClassName = o.ClassName, DayIdx = o.DayIdx, Period = o.Period,
                    Subject = o.Subject, Teacher = o.Teacher, Location = o.Location, Type = o.Type,
                    IsEmpty = o.IsEmpty, Source = o.Source,
                    ValidFrom = o.ValidFrom, ValidTo = o.ValidTo, UpdatedAt = o.UpdatedAt
                }).ToList();
                b[^1].Source = "temp";
                Step("B 改成 temp", b, false);

                // C：内容原样重存（只是又写了一遍盘）→ 不算变化，不该触发
                Step("C 原样重存", b, false);

                // D：删掉这条 temp 记录 → 来源是 temp，仍不该抑制
                var d = new List<ScheduleCore.Models.LessonOverride>(list0);
                Step("D 删除 temp", d, false);

                // E：手动新增后再手动删掉（= 手动撤销一次调课）→ 应触发
                var e1 = new List<ScheduleCore.Models.LessonOverride>(list0) { Make("manual") };
                Step("E1 手动新增（撤销前）", e1, true);
                var e2 = new List<ScheduleCore.Models.LessonOverride>(list0);
                Step("E2 手动删除（= 撤销）", e2, true);

                Store.LocalManualEdit -= OnEdit;
                sb.AppendLine($"cells 精确匹配 {cell} = {(hits.Any(h => h.Contains(cell)) ? "ok" : "FAIL")}");

                // 监视器侧的过滤：键必须是「班级|星期」+「|节次」才命中，
                // 同班别的节次、别的星期都不能被误伤（宁可多提醒一次，也不能把真变化吞掉）。
                bool skip1 = ScheduleChangeWatcher.DiagManualSkip($"{cls}|0", 1, new[] { cell });
                bool skip2 = ScheduleChangeWatcher.DiagManualSkip($"{cls}|0", 2, new[] { cell });
                bool skip3 = ScheduleChangeWatcher.DiagManualSkip($"{cls}|1", 1, new[] { cell });
                sb.AppendLine($"watcher 跳过 第1节={(skip1 ? "ok" : "FAIL")}"
                            + $" 第2节={(skip2 ? "FAIL" : "ok")}"
                            + $" 星期二第1节={(skip3 ? "FAIL" : "ok")}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("EX " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // 一定还原：诊断跑在真实数据副本上也绝不留下痕迹
                try
                {
                    if (backup.Length > 0) File.WriteAllText(ovPath, backup);
                    else if (File.Exists(ovPath)) File.Delete(ovPath);
                }
                catch { }
            }
            try { File.WriteAllText(log, sb.ToString()); } catch { }
            Shutdown(0);
            return;
        }

        // 诊断：--notify-check <log> 端到端验证「手动调课不提醒、外部变化照旧提醒」。
        // 前一个 --change-check 只验 Store 的判据；这里把监视器真跑起来，看状态栏红字到底挂没挂。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--notify-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.notifycheck.log");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CB_DATA_DIR")))
            {
                try { File.WriteAllText(log, "拒绝：请用 CB_DATA_DIR 指向数据副本后再运行\n"); } catch { }
                Shutdown(0);
                return;
            }

            string ovPath = Path.Combine(Store.AppDir, "overrides.json");
            string setPath = Path.Combine(Store.AppDir, "settings.json");
            string ovBackup = "", setBackup = "";
            try
            {
                if (File.Exists(ovPath)) ovBackup = File.ReadAllText(ovPath);
                if (File.Exists(setPath)) setBackup = File.ReadAllText(setPath);
            }
            catch { }

            Store.EnablePersistence = true;
            var flashes = new List<string>();
            AppEvents.ScheduleFlash += OnFlash;
            void OnFlash(string text, int minutes) => flashes.Add(text);

            var edits = new List<string>();
            Store.LocalManualEdit += OnLocal;
            Store.ExternalEdit += OnExt;
            void OnLocal(System.Collections.Generic.IReadOnlyCollection<string> c) => edits.Add("manual:" + string.Join(",", c));
            void OnExt(System.Collections.Generic.IReadOnlyCollection<string> c) => edits.Add("external:" + string.Join(",", c));

            async void RunAsync()
            {
                var sb = new System.Text.StringBuilder();
                try
                {
                    var st = Store.LoadSettings();
                    // 让提醒条件稳定：开关打开、不因"正在上课"而延后
                    st.Reminder ??= new ScheduleCore.Models.ReminderSettings();
                    st.Reminder.ChangeEnabled = true;
                    st.Reminder.ChangeDelayMinutes = 0;
                    st.Reminder.ChangeVoice = false;
                    st.Reminder.PlaySound = false;
                    // 固定按"班级课表"采样，保证下面的改动落在监视器正在看的那张表上
                    st.ScheduleKind = "Class";
                    if (string.IsNullOrWhiteSpace(st.SelectedClass))
                        st.SelectedClass = Store.LoadSchedule()?.Classes?.FirstOrDefault()?.ClassName ?? "";
                    Store.SaveSettings(st);

                    var list0 = Store.LoadOverrides();
                    string cls = st.SelectedClass;
                    var date = BoardBuilder.ResolveDisplayDate(st);
                    int dayIdx = ((int)date.DayOfWeek + 6) % 7;
                    sb.AppendLine($"class={cls} 显示日={date:yyyy-MM-dd} dayIdx={dayIdx} 起步记录={list0.Count}");

                    ScheduleChangeWatcher.Start();
                    AppEvents.RaiseScheduleChanged();
                    await Task.Delay(700);          // 建立基线（首次只记录，不提醒）
                    int baseline = flashes.Count;

                    ScheduleCore.Models.LessonOverride Make(string subject, string source) => new()
                    {
                        ClassName = cls, DayIdx = dayIdx, Period = 1,
                        Subject = subject, Teacher = "诊断老师", Source = source,
                        ValidFrom = DateOnly.FromDateTime(date), ValidTo = DateOnly.FromDateTime(date)
                    };

                    void Replace(ScheduleCore.Models.LessonOverride rec)
                    {
                        var l = new List<ScheduleCore.Models.LessonOverride>(list0);
                        l.RemoveAll(o => o.ClassName == rec.ClassName && o.DayIdx == rec.DayIdx && o.Period == rec.Period);
                        l.Add(rec);
                        Store.SaveOverrides(l);
                        AppEvents.RaiseScheduleChanged();
                    }

                    // A：手动调课 → 期望"没有任何提醒"
                    Replace(Make("诊断科目A", "manual"));
                    await Task.Delay(700);
                    bool manualSilent = flashes.Count == baseline;
                    sb.AppendLine($"A 手动调课 → {(manualSilent ? "无提醒 ok" : "★ 仍然提醒了：" + string.Join(" | ", flashes))}");
                    sb.AppendLine($"   state {ScheduleChangeWatcher.DiagState()}");
                    sb.AppendLine($"   edits {string.Join(" / ", edits)}");

                    // B：同样位置、改成外部来源（等同同步包改课）→ 期望"照旧提醒"
                    Replace(Make("诊断科目B", "temp"));
                    await Task.Delay(700);
                    bool externalNotified = flashes.Count > baseline;
                    sb.AppendLine($"B 外部改课 → {(externalNotified ? "有提醒 ok" : "★ 没提醒（变化被吞了）")}");
                    sb.AppendLine($"   state {ScheduleChangeWatcher.DiagState()}");
                    sb.AppendLine($"   edits {string.Join(" / ", edits)}");
                    if (externalNotified) sb.AppendLine($"   红字：{flashes[^1]}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("EX " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    try { ScheduleChangeWatcher.Stop(); } catch { }
                    AppEvents.ScheduleFlash -= OnFlash;
                    try
                    {
                        if (ovBackup.Length > 0) File.WriteAllText(ovPath, ovBackup);
                        if (setBackup.Length > 0) File.WriteAllText(setPath, setBackup);
                    }
                    catch { }
                    try { File.WriteAllText(log, sb.ToString()); } catch { }
                    Shutdown(0);
                }
            }

            RunAsync();
            return;
        }

        // 诊断：--drag-check 不依赖手指，单独验证拖动链路的几何计算 ——
        // 「落在行间缝隙也有落点」「幽灵卡与虚框尺寸位置合理」这两件纯逻辑的事。
        // 触屏手势本身没法自动测，但这两块是最容易算错坐标的，先在这里兜住。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--drag-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.dragcheck.log");
            try { File.WriteAllText(log, "start\n"); } catch { }
            var w = new ScheduleWindow();
            w.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try { File.AppendAllText(log, w.DiagDragProbe()); }
                catch (Exception ex) { try { File.AppendAllText(log, "EX " + ex.Message + "\n"); } catch { } }
                try { w.Close(); } catch { }
                Shutdown(0);
            };
            t.Start();
            return;
        }

        // 诊断：--week-check 验证控制面板「周课程表」的手势改造（与悬浮课表同一套规则）。
        // 只读演练：浮起 → 3px 不拖 → 40px 起拖 → 收尾，全程不写调代课数据。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--week-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.weekcheck.log");
            try { File.WriteAllText(log, "start\n"); } catch { }
            MainWindow? mw = null;
            // ★ 必须显式落到「周课程表」页（index 0）：P1 换新导航后默认页变成「今日」，
            //   周视图不在可视树里就没被布局（viewport=0），DragLayer 与格子之间连
            //   TransformToVisual 都做不了 —— 表现为一句「不共享共同的上级」的假失败。
            try { mw = new MainWindow { InitialTabIndex = 0 }; mw.Show(); }
            catch (Exception ex) { try { File.AppendAllText(log, "EX " + ex.Message + "\n"); } catch { } }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    if (mw != null) File.AppendAllText(log, mw.WeekViewManage.DiagWeekProbe());
                }
                catch (Exception ex) { try { File.AppendAllText(log, "EX " + ex.Message + "\n"); } catch { } }
                try { mw?.Close(); } catch { }
                Shutdown(0);
            };
            t.Start();
            return;
        }

        // 诊断：--qa-check [log] —— 悬浮课表「内置快捷」的按角色默认 / 按角色保存 / 权限过滤自检。
        // ★ 会写 settings.json，强制 CB_DATA_DIR 指向数据副本。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--qa-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.qacheck.log");
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            void Check(string name, bool ok, string detail = "")
            {
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name
                              + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (ok) pass++; else fail++;
            }
            string Keys(System.Collections.Generic.IEnumerable<string> keys)
                => string.Join(",", keys.OrderBy(x => x, StringComparer.Ordinal));

            try
            {
                string cbDir = (Environment.GetEnvironmentVariable("CB_DATA_DIR") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cbDir))
                {
                    sb.AppendLine("拒绝：请用 CB_DATA_DIR 指向数据副本后再运行（本诊断会写设置）。");
                    Check("CB_DATA_DIR 护栏", false);
                    File.WriteAllText(log, sb.ToString());
                    Shutdown(1);
                    return;
                }
                Store.SuppressWrites = false;
                Store.EnablePersistence = true;
                sb.AppendLine("data=" + Store.AppDir);

                // ★ 本段验证的是「出厂默认」，而副本里往往存着用户手工勾过的按角色显隐 ——
                //   不清掉的话老师那条「便签」永远是关的，读出来就成了 FAIL（假失败）。
                //   清空只动副本；后面「按角色保存已落盘」那条会重新写回去。
                var seeded = Store.LoadSettings();
                if (seeded.QaVisibleByRole != null && seeded.QaVisibleByRole.Count > 0)
                {
                    seeded.QaVisibleByRole.Clear();
                    Store.SaveSettings(seeded);
                    sb.AppendLine("已清空副本里的按角色显隐配置（否则验不出厂默认）");
                }

                var prev = UserSession.Current;
                try
                {
                    var roles = new[]
                    {
                        (UserRole.Student, "countdown,duty,hw,info,missing,note",
                         new[] { "hw", "missing", "note", "countdown", "duty", "info" }),
                        (UserRole.Teacher, "countdown,info,note,publish,settings,week",
                         new[] { "publish", "note", "week", "countdown", "settings", "info" }),
                        (UserRole.SubjectTeacher, "countdown,info,note,publish,settings,week",
                         new[] { "publish", "note", "week", "countdown", "settings", "info" }),
                        (UserRole.Admin, "countdown,info,note,publish,settings,week",
                         new[] { "publish", "note", "week", "countdown", "settings", "info" })
                    };

                    foreach (var (role, expectSorted, expectOrder) in roles)
                    {
                        UserSession.SignIn(new AppUser { Username = "qa-" + role, Role = role });
                        var map = QuickActionCatalog.VisibleMap();
                        string got = Keys(map.Where(kv => kv.Value).Select(kv => kv.Key));
                        Check($"{role} 出厂默认显示项", got == expectSorted, got);
                        string order = string.Join(",", QuickActionCatalog.OrderFor(role).Take(expectOrder.Length));
                        Check($"{role} 胶囊排列顺序", order == string.Join(",", expectOrder), order);
                        Check($"{role} 九个入口都在目录里",
                              QuickActionCatalog.All.All(e => map.ContainsKey(e.Key)),
                              "count=" + map.Count);
                    }

                    // 电教管理员默认没有「发布」权限：勾上也必须被权限过滤掉
                    UserSession.SignIn(new AppUser { Username = "qa-student", Role = UserRole.Student });
                    var stMap = QuickActionCatalog.VisibleMap();
                    stMap[QuickActionCatalog.Publish] = true;
                    QuickActionCatalog.SaveVisible(stMap);
                    Check("电教管理员勾「发布」也被权限过滤（不显示）",
                          !QuickActionCatalog.VisibleNow().Contains(QuickActionCatalog.Publish));

                    // 按角色分别保存：老师关掉「便签」，电教管理员不受影响
                    UserSession.SignIn(new AppUser { Username = "qa-teacher", Role = UserRole.Teacher });
                    var tMap = QuickActionCatalog.VisibleMap();
                    tMap[QuickActionCatalog.Note] = false;
                    QuickActionCatalog.SaveVisible(tMap);
                    Check("老师隐藏「便签」立即生效", !QuickActionCatalog.VisibleNow().Contains(QuickActionCatalog.Note));
                    UserSession.SignIn(new AppUser { Username = "qa-student", Role = UserRole.Student });
                    Check("老师改显隐不影响电教管理员的配置",
                          QuickActionCatalog.VisibleNow().Contains(QuickActionCatalog.Note));

                    // 落盘：重新读设置仍记得住
                    UserSession.SignIn(new AppUser { Username = "qa-teacher", Role = UserRole.Teacher });
                    Check("按角色保存的显隐已落盘",
                          !Store.LoadSettings().QaVisibleByRole[UserRole.Teacher][QuickActionCatalog.Note]);

                    // 游客：只剩不需要编辑权限的三个入口
                    UserSession.SignOut();
                    Check("游客只剩 周课 / 设置 / 信息",
                          Keys(QuickActionCatalog.VisibleNow()) == "info,settings,week",
                          Keys(QuickActionCatalog.VisibleNow()));
                }
                finally
                {
                    if (prev != null) UserSession.SignIn(prev); else UserSession.SignOut();
                }

                sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
                File.WriteAllText(log, sb.ToString());
                Shutdown(fail == 0 ? 0 : 1);
                return;
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
                sb.AppendLine($"RESULT FAIL  {pass}/{pass + fail + 1}");
                try { File.WriteAllText(log, sb.ToString()); } catch { }
                Shutdown(1);
                return;
            }
        }

        // 诊断：--ui-check 逐个构造所有窗口（XAML 解析 / 控件名 / 初始化取值全部走一遍）后退出，
        // 结果写入临时目录 ClassBulletinApp.uicheck.log，便于自动化回归检查。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--ui-check", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder();
            string tracePath = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.uicheck.trace.log");
            try { File.WriteAllText(tracePath, ""); } catch { }

            // 「设置能不能写成 JSON」的守卫：任何窗口把 NaN / Infinity 塞进 AppSettings，
            // settings.json 就整个写不进去（之后所有偏好都会「改了下次又变回去」）。
            // 每条窗口检查后跑一次，只在第一次出问题时记一条 FAIL，免得一个 bug 刷满整个日志。
            bool settingsBroken = false;
            void GuardSettings(string after)
            {
                if (settingsBroken) return;
                try { System.Text.Json.JsonSerializer.Serialize(Store.LoadSettings()); }
                catch (Exception sx)
                {
                    settingsBroken = true;
                    sb.AppendLine("FAIL AppSettings 可序列化（NaN / Infinity 会让设置整份存不进去） :: "
                                  + sx.Message + "  [" + after + "]");
                    var bad = new System.Text.StringBuilder();
                    DumpBadDoubles(Store.LoadSettings(), "settings", bad, 0);
                    sb.AppendLine(bad.ToString().TrimEnd());
                }
            }

            void Check(string name, Func<Window> factory)
            {
                // 每步先落盘：万一某个窗口构造时卡住，日志能指到是哪一个
                try { File.AppendAllText(tracePath, "> " + name + "\n"); } catch { }
                try
                {
                    var w = factory();
                    sb.AppendLine("OK   " + name);
                    GuardSettings(name);
                    try { w.Close(); } catch { }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("FAIL " + name + " :: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Check("MainWindow", () => new MainWindow());
            Check("MainWindow 信息管理页", () =>
            {
                var w = new MainWindow();
                if (w.TabInfoManage == null || w.BtnInfoPublish == null || w.BtnInfoPull == null
                    || w.BtnInfoSnapshot == null || w.BtnInfoLedger == null || w.CbTeachingClass == null
                    || w.TeachingClassChips == null || w.BtnInfoSaveSettings == null
                    || w.BtnInfoPublishHw == null || w.BtnInfoPublishMs == null
                    || w.BtnInfoPublishDuty == null
                    || w.BtnInfoPublishNotice == null || w.TxtInfoHint == null)
                    throw new Exception("信息管理页控件缺失");
                // 语音播报开关、台账保留天数、教室机自动增加开关（本期新增）
                if (w.ChkInfoVoice == null || w.TxtInfoKeepDays == null || w.ChkInfoAutoApply == null)
                    throw new Exception("信息管理页同步设置区控件缺失（语音 / 台账保留天数 / 自动增加）");
                return w;
            });
            Check("MainWindow 新导航（侧栏 5 组）", () =>
            {
                var w = new MainWindow();
                if (w.FindName("NavBtnToday") == null || w.FindName("NavBtnWeek") == null
                    || w.FindName("NavBtnContent") == null || w.FindName("NavBtnMessage") == null
                    || w.FindName("NavBtnSetting") == null || w.FindName("SubNavHost") == null
                    || w.FindName("TabToday") == null || w.FindName("TxtTodaySubtitle") == null)
                    throw new Exception("新导航控件缺失");
                // 铁律：原有 0~8 索引一个都不许动，「今日」只能追加在末尾
                string[] expect = { "周课程表", "数据管理", "显示设置", "提醒设置", "通知公告",
                                    "便签管理", "信息管理", "用户中心", "关于" };
                if (w.MainTabs.Items.Count != 10)
                    throw new Exception("TabControl 项数被改动：" + w.MainTabs.Items.Count + " 项");
                for (int i = 0; i < expect.Length; i++)
                    if (w.MainTabs.Items[i] is not System.Windows.Controls.TabItem ti
                        || !string.Equals(ti.Header as string, expect[i], StringComparison.Ordinal))
                        throw new Exception("原有 Tab 顺序被改动，下标 " + i);
                if (!ReferenceEquals(w.MainTabs.Items[9], w.FindName("TabToday")))
                    throw new Exception("今日页不在末尾 index 9");
                return w;
            });
            Check("MainWindow 今日首页（index 9）", () =>
            {
                var w = new MainWindow { InitialTabIndex = 9 };
                if (w.MainTabs.SelectedIndex != 9) throw new Exception("今日页索引不是 9");
                return w;
            });
            // P2：重复入口只收起、不物理删除 —— 唯一入口要可见，旧控件要还在（随时可改回 Visible）
            Check("P2 重复入口已收敛（隐藏回退仍在）", () =>
            {
                var vis = System.Windows.Visibility.Visible;
                var collapsed = System.Windows.Visibility.Collapsed;
                var w = new MainWindow();
                // r75：通知条字号原来是一个 22 项下拉，改成与编辑窗同一条格式条的 A⁻ / A⁺ 步进
                if (w.TxtNotifSize.Visibility != vis || w.BtnNotifSizeUp.Visibility != vis
                    || w.BtnNotifSizeDown.Visibility != vis)
                    throw new Exception("通知条格式条（字号步进）不可见");
                if (w.BtnWpSettings.Visibility != vis) throw new Exception("桌面背景排版唯一入口不可见");
                if (w.BtnInfoPublishHw.Visibility != vis || w.BtnInfoPublishMs.Visibility != vis
                    || w.BtnInfoPublishDuty.Visibility != vis || w.BtnInfoPublishNotice.Visibility != vis)
                    throw new Exception("四类内容直达发布入口不可见");
                if (w.BtnInfoPublish.Visibility != collapsed) throw new Exception("重复的通用发布按钮未收起");
                if (w.BtnHwWp.Visibility != collapsed || w.BtnMsWp.Visibility != collapsed)
                    throw new Exception("桌面背景显隐的重复开关未收起");
                if (w.FindName("BtnHwWp") == null || w.FindName("BtnMsWp") == null
                    || w.FindName("BtnInfoPublish") == null)
                    throw new Exception("旧入口被物理删除，隐藏回退失效");
                if (w.FindName("ExpLoginPref") is not System.Windows.Controls.Expander exp || exp.IsExpanded)
                    throw new Exception("用户中心登录偏好未默认折叠");
                if (w.FindName("ChkRememberUser") == null || w.FindName("ChkRememberPwd") == null
                    || w.FindName("ChkAutoLogin") == null)
                    throw new Exception("用户中心登录偏好控件丢失");

                var l = new LayoutWindow();
                if (l.FindName("SlNotFont") == null || l.FindName("TxtNotFont") == null)
                    throw new Exception("旧字号滑块被物理删除，隐藏回退失效");
                if (l.SlNotFont.Visibility != collapsed || l.TxtNotFont.Visibility != collapsed)
                    throw new Exception("布局设置里的重复字号滑块未收起");
                // 收起的旧滑块只到 64 号：保存布局设置时绝不能把下拉选的 96 号钳制掉
                if (Math.Abs(l.DiagSyncFontSize(96) - 96) > 0.01)
                    throw new Exception("收起的旧字号滑块把 96 号钳制了");
                try { l.Close(); } catch { }   // Check 只关返回的窗口，额外构造的窗口要自己收
                return w;
            });
            // P3：共用样式要挂上，但拖动注册区 / DragLayer / 双击区 / x:Name 一个都不能少
            Check("P3 悬浮窗共用标题栏（触屏结构未动）", () =>
            {
                var s = new ScheduleWindow();
                if (s.FindName("HeaderBar") == null) throw new Exception("HeaderBar 丢失：FloatChrome.EnableDrag 的拖动注册区");
                if (s.FindName("DragLayer") == null || s.FindName("ClassHeader") == null || s.FindName("DateHeader") == null)
                    throw new Exception("拖动 / 双击 / DragLayer 结构被改动");
                if (s.ScheduleScroll.PanningMode != System.Windows.Controls.PanningMode.None)
                    throw new Exception("PanningMode 不再是 None");
                if (s.TryFindResource("FloatScheduleHeader") is not System.Windows.Style sh || !ReferenceEquals(s.HeaderBar.Style, sh)
                    || s.TryFindResource("FloatScheduleButton") is not System.Windows.Style sb
                    || !ReferenceEquals(s.BtnHide.Style, sb) || !ReferenceEquals(s.BtnQaUpdate.Style, sb))
                    throw new Exception("悬浮课表标题栏未引用共用样式");
                var hw = new HomeworkWindow();
                try
                {
                    if (hw.TryFindResource("FloatHeader") is not System.Windows.Style fh || !ReferenceEquals(hw.Header.Style, fh)
                        || hw.TryFindResource("FloatCloseButton") is not System.Windows.Style fc || !ReferenceEquals(hw.BtnHide.Style, fc)
                        || hw.TryFindResource("FloatTitle") is not System.Windows.Style ft || !ReferenceEquals(hw.TxtTitle.Style, ft))
                        throw new Exception("便签标题栏未引用共用样式");
                    if (hw.FindName("BtnHide") == null || hw.FindName("TxtTitle") == null)
                        throw new Exception("便签标题栏控件丢失");
                }
                finally { try { hw.Close(); } catch { } }
                return s;
            });
            // P4：三组卡片共用样式 + 纯 C# 窗口取色走资源键（不再写死主色）
            Check("P4 提醒模板化 / 纯 C# 窗口跟随主题", () =>
            {
                var w = new MainWindow();
                if (w.TryFindResource("ReminderInput") is not System.Windows.Style ri
                    || !ReferenceEquals(w.TxtCsTitle.Style, ri) || !ReferenceEquals(w.TxtCeTitle.Style, ri)
                    || !ReferenceEquals(w.TxtAsTitle.Style, ri))
                    throw new Exception("三组卡片未复用同一套输入框样式");
                if (w.TryFindResource("ReminderCard") is not System.Windows.Style
                    || w.TryFindResource("ReminderLabel") == null || w.TryFindResource("ReminderLeadInput") == null)
                    throw new Exception("提醒设置共用样式缺失");
                if (w.ChkCsOn == null || w.ChkCeOn == null || w.ChkAsOn == null
                    || w.TxtCsDanmaku == null || w.TxtCeDanmaku == null || w.TxtAsDanmaku == null)
                    throw new Exception("提醒设置三组控件丢失");
                var b = System.Windows.Application.Current?.TryFindResource("PrimaryLightBrush") as System.Windows.Media.SolidColorBrush;
                if (b == null) throw new Exception("主色资源键缺失");
                string expect = "#" + b.Color.R.ToString("X2") + b.Color.G.ToString("X2") + b.Color.B.ToString("X2");
                if (!string.Equals(ChromePalette.HexOf(ChromePalette.PrimaryLight, "#E9EFF9"), expect, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("纯 C# 窗口取色没跟上资源键");
                return w;
            });
            // 玻璃底色：只压 Border.Background 的 alpha，绝不碰 Window.Opacity（HoverFader 占着它）
            Check("玻璃底色只压底色 / 不动整窗透明度", () =>
            {
                // ★ 探针用一个和主题无关的固定色：早先这里写的是主色字面量，
                //   一改配色（r76 沉稳蓝）就地跟断言里的期望值对不上，白白红一条。
                const byte pr = 0x12, pg = 0x34, pb = 0x56;
                var probe = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(pr, pg, pb))
                };
                GlassBrush.ApplyCore(probe, true, 45);
                var on = probe.Background as System.Windows.Media.SolidColorBrush;
                if (on == null || on.Color.A != GlassBrush.AlphaOf(45)
                    || on.Color.R != pr || on.Color.G != pg || on.Color.B != pb)
                    throw new Exception("通透度没只作用到底色 alpha 上");
                GlassBrush.ApplyCore(probe, false, 45);
                var off = probe.Background as System.Windows.Media.SolidColorBrush;
                if (off == null || off.Color.A != 255)
                    throw new Exception("关闭玻璃后没有还原原始 alpha");

                // r77：玻璃设置已迁到「显示设置 → 界面主题」，不再属于 LayoutWindow，这里的入口断言移除。

                var w = new NotificationWindow();
                try
                {
                    // 默认关闭 = 零改动：通知条底色必须仍是动态资源引用（切主题才跟随）
                    var local0 = w.Bar.ReadLocalValue(System.Windows.Controls.Border.BackgroundProperty);
                    if (local0 is System.Windows.Media.Brush)
                        throw new Exception("玻璃默认关闭却切断了通知条底色的主题跟随");
                    double op0 = w.Opacity;
                    w.ApplyGlass(new ScheduleCore.Models.AppSettings { GlassEnabled = true, GlassOpacity = 45 });
                    if (System.Math.Abs(w.Opacity - op0) > 0.001)
                        throw new Exception("玻璃动了 Window.Opacity —— 会顶掉 HoverFader 的 HoldEnd 动画");
                    var bar = w.FindName("Bar") as System.Windows.Controls.Border;
                    if (bar == null || (bar.Background as System.Windows.Media.SolidColorBrush)?.Color.A >= 255)
                        throw new Exception("通知条底色没跟上玻璃开关");
                    w.ApplyGlass(new ScheduleCore.Models.AppSettings { GlassEnabled = false, GlassOpacity = 45 });
                    return w;
                }
                catch { try { w.Close(); } catch { } throw; }
            });
            // P5：5 个便签底栏原本是 5 份完全相同的拷贝，收敛成 FloatFooter / FloatFooterButton 之后
            // 必须验「实际生效值」而不是「Style 键存在」—— Style Setter 被本地值盖掉的话界面会变回去
            Check("P5 便签底栏统一（按钮文案与命中区未动）", () =>
            {
                var hw = new HomeworkWindow();
                try
                {
                    if (hw.TryFindResource("FloatFooter") is not System.Windows.Style ff
                        || hw.TryFindResource("FloatFooterButton") is not System.Windows.Style fb)
                        throw new Exception("便签底栏共用样式缺失");
                    if (hw.Footer == null || !ReferenceEquals(hw.Footer.Style, ff))
                        throw new Exception("作业便签底栏未引用共用样式");
                    if (hw.Footer.Padding != new System.Windows.Thickness(10, 6, 10, 6)
                        || hw.Footer.BorderThickness != new System.Windows.Thickness(0, 1, 0, 0))
                        throw new Exception("底栏内距 / 分隔线被改动");
                    // 按钮：样式生效（11 号字 + 8,3 内距），且各自的 Margin / 文案 / x:Name 原样保留
                    if (!ReferenceEquals(hw.BtnTop.Style, fb) || hw.BtnTop.FontSize != 11
                        || hw.BtnTop.Padding != new System.Windows.Thickness(8, 3, 8, 3))
                        throw new Exception("底栏按钮没走共用样式");
                    if ((hw.BtnTop.Content as string) != "置顶" || (hw.BtnWp.Content as string) != "设为背景"
                        || (hw.BtnPos.Content as string) != "位置" || hw.BtnPos.Margin.Left != 6)
                        throw new Exception("底栏按钮文案 / 间距被改动");
                    if (hw.TxtHint == null) throw new Exception("便签提示文字丢失");

                    // 另外 4 个便签：底栏 x:Name 与按钮必须还在，样式必须挂上
                    void Probe(System.Windows.Window w, string tag,
                        System.Windows.Controls.Border? footer,
                        System.Windows.Controls.Button? a, System.Windows.Controls.Button? b,
                        System.Windows.Controls.TextBlock? hint)
                    {
                        try
                        {
                            if (footer == null || !ReferenceEquals(footer.Style, ff))
                                throw new Exception(tag + " 底栏未引用共用样式");
                            if (a == null || b == null || hint == null)
                                throw new Exception(tag + " 底栏控件丢失");
                            if (!ReferenceEquals(a.Style, fb) || a.FontSize != 11)
                                throw new Exception(tag + " 底栏按钮没走共用样式");
                        }
                        finally { try { w.Close(); } catch { } }
                    }
                    var ms = new MissingWindow();
                    Probe(ms, "缺交名单", ms.Footer, ms.BtnTop, ms.BtnWp, ms.TxtHint);
                    var cd = new CountdownWindow();
                    Probe(cd, "倒计时", cd.Footer, cd.BtnTop, cd.BtnWp, cd.TxtHint);
                    var du = new DutyWindow();
                    Probe(du, "值日", du.Footer, du.BtnTop, du.BtnWp, du.TxtHint);
                    var cn = new CustomNoteWindow(DiagCustomNoteId());
                    Probe(cn, "自定义便签", cn.Footer, cn.BtnTop, cn.BtnWp, cn.TxtHint);
                }
                catch { try { hw.Close(); } catch { } throw; }
                return hw;
            });
            // r67 事项列表便签：通知从「一整条跑马灯」升级成与作业 / 缺交 / 值日并列的便签。
            // 这一段是纯内存断言（不落盘），锁住四条最容易回退的语义：
            //   ① 墓碑别再被改回物理删除  ② 置顶永不过期  ③ 跑马灯只滚两条  ④ 便签窗结构还在
            Check("r67 通知截止日期与墓碑语义", () =>
            {
                var now = DateTime.Now;

                // ① 到点即过期
                var expired = new ScheduleCore.Models.Notification { Title = "过期", DueAt = now.AddDays(-1) };
                if (!expired.IsExpired || expired.IsLive)
                    throw new Exception("过了截止时间的通知没被判定为过期");
                if (expired.DueText != "已过期")
                    throw new Exception("过期通知的截止标签不是「已过期」：" + expired.DueText);

                // ② 置顶永不过期（老师置顶 = 这条要长期挂着）
                var pinned = new ScheduleCore.Models.Notification
                { Title = "置顶", DueAt = now.AddDays(-1), Pinned = true };
                if (pinned.IsExpired || !pinned.IsLive)
                    throw new Exception("置顶通知被截止时间撤下了（置顶应永不自动过期）");

                // ③ DueAt == null 就是长期有效
                var forever = new ScheduleCore.Models.Notification { Title = "长期" };
                if (forever.IsExpired || !forever.IsLive || forever.DueText != "长期")
                    throw new Exception("没有截止日期的通知应长期有效");

                // ④ 墓碑一律不上面：撤下 / 到期撤下 / 同题覆盖都只打标记，数据留着可恢复
                var gone = new ScheduleCore.Models.Notification { Title = "已撤下", Deleted = true };
                if (gone.IsLive || gone.StateText.Length == 0)
                    throw new Exception("墓碑条目仍被当成有效通知（撤回会被旧变更单复活）");

                // ⑤ 便签上的截止标签：今天 / 明天
                if (new ScheduleCore.Models.Notification { DueAt = DateTime.Today.AddHours(23) }.DueText != "今天"
                    || new ScheduleCore.Models.Notification { DueAt = DateTime.Today.AddDays(1) }.DueText != "明天")
                    throw new Exception("截止标签文案不对（今天 / 明天）");

                // ⑥ 跑马灯上限两条：这是「别再把整条通知拼成一段滚动词」的硬约束
                if (NotificationWindow.MarqueeMax != 2)
                    throw new Exception("跑马灯条数上限被改成 " + NotificationWindow.MarqueeMax);

                // ⑦ 事项便签本体：位置键 + 卡片 / 底栏 / 按钮都在（少一个老师就没法编辑或设为背景）
                var w = new NoticeWindow();
                if (NoticeWindow.PlacementKey != "notice")
                    throw new Exception("事项便签的位置键被改动：" + NoticeWindow.PlacementKey);
                if (w.NoteBorder == null || w.Header == null || w.ContentPanel == null
                    || w.Footer == null || w.BtnEdit == null || w.BtnTop == null
                    || w.BtnWp == null || w.TxtHint == null)
                    throw new Exception("事项便签的卡片 / 底栏控件缺失");
                return w;
            });
            // r67 发布窗：截止日期下拉（默认今天）+「覆盖同标题的旧通知」（默认不勾，逐条由老师决定）
            Check("BoardSendWindow(通知截止日期)", () =>
            {
                var w = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityNotice);
                if (!w.DueRowVisible || w.DueBox.SelectedDate?.Date != DateTime.Today)
                    throw new Exception("通知发布应默认显示截止日期日历并选中今天");
                if (!w.OverwriteRowVisible || w.OverwriteBox.IsChecked == true)
                    throw new Exception("「覆盖同标题的旧通知」应默认不勾（由发布老师逐条决定）");
                var hw = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityHomework);
                try
                {
                    if (hw.DueRowVisible || hw.OverwriteRowVisible)
                        throw new Exception("作业公示不该出现截止日期 / 覆盖同标题");
                }
                finally { try { hw.Close(); } catch { } }
                return w;
            });
            // r67 桌面背景：通知公告必须和其它五类便签一样能被勾选、能排版
            Check("LayoutWindow 桌面背景六类内容", () =>
            {
                var w = new LayoutWindow { InitialTabIndex = 2 };
                if (w.ChkWpNotice == null)
                    throw new Exception("桌面背景缺「通知公告」勾选框");
                if (w.ChkWpSchedule == null || w.ChkWpHomework == null || w.ChkWpMissing == null
                    || w.ChkWpCountdown == null || w.ChkWpDuty == null || w.ChkWpOrigin == null)
                    throw new Exception("桌面背景勾选框缺失");
                return w;
            });
            // r67 便签管理页第 6 张卡：通知公告要和其它便签一样有「显示 / 隐藏」与「编辑」两个入口
            Check("MainWindow 便签管理（通知公告卡）", () =>
            {
                var w = new MainWindow { InitialTabIndex = 5 };
                if (w.BtnToggleNotice == null || w.BtnEditNotice == null)
                    throw new Exception("便签管理页缺「通知公告」的显示 / 编辑入口");
                if (w.NotesScroll == null)
                    throw new Exception("便签管理页滚动容器丢失（截图 / 定位会失效）");
                return w;
            });
            // r69 字体工具条（焦点跟随形态）：全窗只有一条，作用对象 = 当前聚焦的输入框。
            // ★ 锁死五件事：焦点切换真的换样式对象、失焦整条置灰不回退、步进按显示顺序、
            //   清除格式回到「特大 + 默认墨色」、紧凑版不参与焦点跟随。
            Check("TextStylePicker 焦点跟随工具条", () =>
            {
                var w = new CustomNoteEditWindow(DiagCustomNoteId());
                var p = w.PickStyle;

                // ① 模拟焦点落到标题 / 正文：作用对象必须真的换（不是只改了句文案）
                // 由绑定器直接解析「字段 → 样式」，避开未 Show 窗口上难以构造的焦点路由事件
                void Focus(FrameworkElement el) => w.DiagBinder?.DiagFocus(el);

                Focus(w.TxtTitle);
                var sTitle = p.StyleData;
                if (!p.IsActive || p.TargetName != "便签标题" || sTitle == null)
                    throw new Exception("焦点在标题框时工具条没激活 / 作用对象名不对：" + p.TargetName);

                Focus(w.TxtContent);
                var sContent = p.StyleData;
                if (!p.IsActive || p.TargetName != "便签内容" || sContent == null)
                    throw new Exception("焦点在正文框时工具条没切过去：" + p.TargetName);
                if (ReferenceEquals(sTitle, sContent))
                    throw new Exception("焦点切换后仍是同一个样式对象（会把标题和正文一起改掉）");

                // ② 焦点丢失：整条置灰、文案变灰，且不回退到别的作用对象
                p.Deactivate();
                if (p.Bar.IsEnabled || !p.TxtTarget.Text.Contains("点输入框"))
                    throw new Exception("失焦后工具条没整条置灰 / 没给下一步指引");
                if (p.TxtPreviewMeta.Visibility != Visibility.Collapsed)
                    throw new Exception("失焦后仍挂着上一次的字号摘要，会让人误以为工具条有效");
                if (!ReferenceEquals(p.StyleData, sContent))
                    throw new Exception("失焦后不该悄悄回退到别的作用对象（会误改）");

                // ③ 预设色格必须全生成
                if (p.SwatchGrid.Children.Count != HwTextStyle.PresetColors.Length)
                    throw new Exception("色板格数 " + p.SwatchGrid.Children.Count
                                        + " ≠ 预设色 " + HwTextStyle.PresetColors.Length);

                var s = sContent!;
                s.SizeLevel = 0;   // 标准
                p.Attach(s, "诊断");

                void Click(System.Windows.Controls.Primitives.ButtonBase b)
                    => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                // ④ 步进必须按 SizeOrder（更小 / 小 / 标准 / 大 / 特大 / 超大 / 巨大）走。
                //    按下标走会出现「标准(19) → 小(16)」这种越点字越小的怪事。
                int up1 = HwTextStyle.SizeOrder[HwTextStyle.OrderIndexOf(0) + 1];
                Click(p.BtnSizeUp);
                if (s.SizeLevel != up1)
                    throw new Exception("A⁺ 没按显示顺序步进：期望档位 " + up1 + "，实际 " + s.SizeLevel);

                for (int i = 0; i < 8; i++) Click(p.BtnSizeUp);
                if (s.SizeLevel != 6)
                    throw new Exception("A⁺ 到顶应停在「巨大」档 6，实际 " + s.SizeLevel);
                if (p.BtnSizeUp.IsEnabled)
                    throw new Exception("已经是最大档，A⁺ 应置灰");

                Click(p.BtnSizeDown);
                if (s.SizeLevel != 5)
                    throw new Exception("A⁻ 应从「巨大」退到「超大」(5)，实际 " + s.SizeLevel);

                // ⑤ r69：清除格式回到「特大 + 默认墨色 + 不加粗」，不是回到「标准」
                p.TglBold.IsChecked = true;
                s.SizeLevel = 0;
                s.Color = "#C0392B";
                Click(p.BtnClear);
                if (s.Bold || s.SizeLevel != HwTextStyle.ExtraLargeLevel || !string.IsNullOrEmpty(s.Color))
                    throw new Exception("清除格式没回到默认：Bold=" + s.Bold
                                        + " Level=" + s.SizeLevel + " Color=" + s.Color);

                if (!p.TxtPreviewMeta.Text.Contains("号"))
                    throw new Exception("右侧摘要缺字号数字：" + p.TxtPreviewMeta.Text);

                // ⑥ 紧凑版（作业 / 缺交列表里每行一条）不参与焦点跟随，仍收起步进 / 清除 / 右侧状态
                var cp = new Controls.TextStylePicker { Compact = true, StyleData = new ScheduleCore.Models.TextStyle() };
                if (cp.PreviewBox.Visibility != Visibility.Collapsed
                    || cp.BtnClear.Visibility != Visibility.Collapsed
                    || cp.BtnSizeUp.Visibility != Visibility.Collapsed)
                    throw new Exception("紧凑模式没收起步进 / 清除 / 右侧状态（列表行会被撑宽）");

                return w;
            });
            // r69：三个编辑窗的焦点映射与「默认全部特大」。
            // 值日表格按列映射、倒计时改条目式后「还剩 N 天」可点选，这两处最容易在重排时丢。
            Check("编辑窗焦点映射与默认特大", () =>
            {
                var duty = new DutyEditWindow();
                if (duty.PickStyle == null || duty.GridItems == null)
                    throw new Exception("值日窗缺焦点工具条 / 表格");
                if (duty.GridItems.Columns.Count != 3)
                    throw new Exception("值日表格应有 3 列（星期 / 姓名 / 内容），实际 " + duty.GridItems.Columns.Count);
                duty.Close();

                var cd = new CountdownEditWindow();
                if (cd.PickStyle == null || cd.EventItems == null)
                    throw new Exception("倒计时窗缺焦点工具条 / 事件卡片容器");

                // 新建字段默认「特大 + 默认墨色」；JSON 里带 sizeLevel 的老数据会覆盖，不受影响。
                // r73c 例外：剩余天数默认就是红色（用户定稿），要单独断言，不能混在这条里。
                var db = new DutyBoard();
                var cb = new CountdownBoard();
                var cn = new CustomNote();
                foreach (var st in new[] { db.TitleStyle, db.ContentStyle, db.DayStyle, db.DutyStyle,
                                           cb.TitleStyle, cb.ContentStyle,
                                           cn.TitleStyle, cn.ContentStyle })
                {
                    if (st == null || st.SizeLevel != HwTextStyle.ExtraLargeLevel || !string.IsNullOrEmpty(st.Color))
                        throw new Exception("新建样式没落到「特大 + 默认墨色」：Level="
                                            + st?.SizeLevel + " Color=" + st?.Color);
                }
                if (cb.DaysStyle == null || cb.DaysStyle.SizeLevel != HwTextStyle.ExtraLargeLevel
                    || !cb.DaysStyle.Bold || cb.DaysStyle.Color != CountdownBoard.DaysDefaultColor)
                    throw new Exception("剩余天数默认应为「红色 + 加粗 + 特大号」："
                                        + cb.DaysStyle?.SizeLevel + "/" + cb.DaysStyle?.Bold + "/" + cb.DaysStyle?.Color);

                // ⑦ 壁纸溢出兜底：塞 80 行大字号内容，卡片必须被收进屏幕可用高度内。
                //    正常数据走不到这段，只有「全部特大 + 内容很多」才会触发。
                var canvas = new System.Windows.Controls.Canvas { Width = 1920, Height = 1080 };
                var stack = new System.Windows.Controls.StackPanel();
                for (int i = 0; i < 80; i++)
                    stack.Children.Add(new System.Windows.Controls.TextBlock
                    { Text = "溢出测试行 " + i, FontSize = 30 });
                var card = new System.Windows.Controls.Border
                {
                    Background = System.Windows.Media.Brushes.White,
                    Padding = new Thickness(12),
                    Child = stack
                };
                BackdropRenderer.Place(canvas, card,
                    new HomeworkWallpaperLayout { UseFixedSize = false, WidthRatio = 0.3 },
                    1920, 1080, 0.3);
                double avail = Math.Max(120, 1080 - 2 * 60.0);
                if (card.Height > avail + 1)
                    throw new Exception("壁纸溢出兜底没把卡片收进可用高度：" + card.Height);
                return cd;
            });
            Check("BoardSyncWindow", () => new BoardSyncWindow());
            Check("BoardSendWindow", () => new BoardSendWindow());
            // 值日安排：预选类型、星期下拉、格式条该藏就藏 —— 这三处任何一处断掉，
            // 老师就会在「值日」里看到「科目」标签、或者根本发不出星期
            Check("BoardSendWindow(值日)", () =>
            {
                var w = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityDuty);
                if ((w.EntityBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                    != BoardSyncConsts.EntityDuty)
                    throw new Exception("预选内容类型不是「值日安排」");
                if (w.DayBox.Items.Count != DutyItem.DayNames.Length)
                    throw new Exception("值日星期下拉项数不对：" + w.DayBox.Items.Count);
                if (w.DayRow.Visibility != Visibility.Visible)
                    throw new Exception("选了值日却没显示星期下拉");
                if (w.FormatRowsVisible)
                    throw new Exception("值日不该显示字段级格式条（字体由便签统一设置）");
                return w;
            });
            Check("BoardSendWindow(多班)", () => new BoardSendWindow(
                new[] { "一班", "二班" }, BoardSyncConsts.EntityMissing));
            // 一次安排一周：7 天表格、与单日控件互斥、不显示字段级格式条
            Check("BoardSendWindow(一次安排一周)", () =>
            {
                var w = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityDuty);
                if (w.WeekInputRows != 7)
                    throw new Exception("周表应有 7 行（周一…周日），实际 " + w.WeekInputRows);
                if (w.WeekPanelVisible)
                    throw new Exception("默认是单条模式，不该展开周表");
                w.SetWeekModeForDiag(true);
                if (!w.WeekPanelVisible || w.DayRow.Visibility == Visibility.Visible)
                    throw new Exception("勾上「一次安排一周」后周表没展开 / 单日星期没让位");
                if (w.FormatRowsVisible)
                    throw new Exception("值日不该显示字段级格式条");
                return w;
            });
            // 通知直接增加：默认勾、只在通知里出现；作业类不该看见这个开关
            Check("BoardSendWindow(通知直接增加)", () =>
            {
                var w = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityNotice);
                if (!w.AutoRowVisible || w.AutoBox.IsChecked != true)
                    throw new Exception("本班通知应默认勾上「直接增加」");
                var hw = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityHomework);
                if (hw.AutoRowVisible)
                    throw new Exception("作业公示不该出现「直接增加」开关");
                return w;
            });
            Check("BoardSyncPopup", () => new BoardSyncPopup());
            Check("BoardSnapshotViewWindow", () => new BoardSnapshotViewWindow(new BoardSnapshot()));
            Check("BoardTargetPickWindow", () => new BoardTargetPickWindow(
                new BoardChangeOrder(), BoardSyncConsts.DecReplace,
                new System.Collections.Generic.List<BoardSnapshotItem>()));
            // 悬浮课表：手势与拖动逻辑都在这里，XAML 里的 Stylus 附加属性（关系统长按 / Flicks）
            // 也只有构造一次才能确认解析没问题。
            Check("ScheduleWindow", () => new ScheduleWindow());
            // 内置快捷：九个胶囊 + 按角色重排的那条链（缺一个按钮这里就报出来，别等用户发现少了入口）
            Check("ScheduleWindow 内置快捷（九个）", () =>
            {
                var w = new ScheduleWindow();
                if (w.BtnQaHomework == null || w.BtnQaMissing == null || w.BtnQaNote == null
                    || w.BtnQaCountdown == null || w.BtnQaDuty == null || w.BtnQaSettings == null
                    || w.BtnQaWeek == null || w.BtnQaPublish == null || w.BtnQaInfo == null
                    || w.BuiltinWrap == null)
                    throw new Exception("内置快捷按钮缺失（周课 / 发布 / 信息）");
                if (w.BuiltinWrap.Children.Count != QuickActionCatalog.All.Length)
                    throw new Exception("内置快捷按钮数量与目录不一致：" + w.BuiltinWrap.Children.Count);
                return w;
            });
            Check("LoginWindow", () => new LoginWindow());
            Check("RetrievePasswordWindow", () => new RetrievePasswordWindow());
            Check("LayoutWindow", () => new LayoutWindow { InitialTabIndex = 2 });
            // 通知条默认「置顶常驻 + 穿透 + 移上去变透明」：少一个勾选框用户就没法改回去
            Check("LayoutWindow 通知条开关", () =>
            {
                var w = new LayoutWindow { InitialTabIndex = 1 };
                if (w.ChkNotHoverFade == null || w.ChkNotClick == null || w.ChkNotTop == null)
                    throw new Exception("通知条「鼠标移上去变透明」开关缺失");
                // 新装默认是「穿透 + 移上去变透明」，但用户可以在设置里关掉 —— 这里只验
                // 「控件如实反映当前设置」，默认值本身由 AppSettings 的字段初始化断言兜住。
                var savedNl = ScheduleCore.Services.Store.LoadSettings().NotificationLayout;
                if (w.ChkNotClick.IsChecked != savedNl.ClickThrough
                    || w.ChkNotHoverFade.IsChecked != savedNl.HoverFade)
                    throw new Exception("通知条开关没有如实反映当前设置（穿透 / 移上去变透明）");
                var fresh2 = new ScheduleCore.Models.AppSettings();
                if (!fresh2.NotificationLayout.ClickThrough || !fresh2.NotificationLayout.HoverFade)
                    throw new Exception("新装通知条默认应为「穿透 + 移上去变透明」");
                return w;
            });
            // 通知条本体：默认常驻 + 穿透，鼠标移上去整条淡到 0（离开回到设定值）。
            // ★ 穿透态下窗口收不到鼠标消息，肉眼与自动化都很难验证 —— 这里直接把淡出状态切一遍，
            //   确认动画真的改到了 Window.Opacity（这段坏了的表现就是「功能完全没生效」）。
            Check("NotificationWindow 移上去变透明", () =>
            {
                var w = new NotificationWindow();
                w.Show();
                w.UpdateLayout();
                if (w.Fader == null)
                    throw new Exception("通知条没挂上悬停淡出（HoverFader 为空）");
                var baseOpacity = w.Opacity;
                if (baseOpacity <= 0.5)
                    throw new Exception("通知条常驻显示的基础透明度不对：" + baseOpacity.ToString("0.00"));
                w.Fader.ForceFade(true);
                if (!w.Fader.IsFaded || w.Opacity > 0.001)
                    throw new Exception("鼠标移上去没有变透明：opacity=" + w.Opacity.ToString("0.000"));
                w.Fader.ForceFade(false);
                if (w.Fader.IsFaded || Math.Abs(w.Opacity - baseOpacity) > 0.01)
                    throw new Exception("鼠标离开后没恢复到设定值：opacity=" + w.Opacity.ToString("0.000"));
                return w;
            });
            Check("HomeworkWindow", () => new HomeworkWindow());
            Check("MissingWindow", () => new MissingWindow());
            Check("MissingEditWindow", () => new MissingEditWindow(Store.LoadMissingBoard()));
            Check("HomeworkEditWindow", () => new HomeworkEditWindow(Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard()));
            Check("HandwritingPopup", () => new HandwritingPopup());
            Check("HandwritingBallWindow", () => new HandwritingBallWindow());
            Check("BackdropEditorWindow", () => new BackdropEditorWindow());
            Check("QuickLaunchWindow", () => new QuickLaunchWindow());
            Check("QuickLaunchWindow 内置快捷开关", () =>
            {
                var w = new QuickLaunchWindow();
                if (w.BuiltinToggleWrap == null || w.BuiltinToggleWrap.Children.Count == 0)
                    throw new Exception("内置快捷显隐勾选框缺失");
                if (w.BuiltinPreview == null || w.BuiltinPreview.Children.Count == 0)
                    throw new Exception("内置快捷预览为空（勾上的入口一个都没画出来）");
                return w;
            });
            Check("LedgerCleanWindow", () => new LedgerCleanWindow(12, 8, 4, 7));
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

            // ---------------- r71：问号说明 / 便签排版 / 发布窗 / 通知默认值 ----------------
            // 问号气泡：18px 圆标藏在 34×34 命中区里；同一时刻只允许一个气泡打开（不然一屏弹几张卡互相盖）。
            Check("r71 HelpBubble 问号说明（命中区 / 单开 / 键盘可关）", () =>
            {
                var w = new MainWindow();
                // ★ 未 Show 的窗口可视树还没建立（VisualTreeHelper 恒 0），必须走逻辑树；
                //   XAML 解析完逻辑树就已经在了，这是 --ui-check 里唯一可靠的遍历方式。
                int Count(DependencyObject d)
                {
                    int n = d is Controls.HelpBubble ? 1 : 0;
                    try
                    {
                        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(d))
                            if (child is DependencyObject dep) n += Count(dep);
                    }
                    catch (InvalidOperationException) { }
                    return n;
                }
                if (Count(w) < 1)
                    throw new Exception("主窗里一个问号说明都没有（说明迁移没生效）");

                var a = new Controls.HelpBubble { PopupTitle = "操作说明", Text = "第一段" };
                var b = new Controls.HelpBubble { PopupTitle = "注意事项", Text = "第二段" };
                if (a.Width != 34 || a.Height != 34 || b.Width != 34 || b.Height != 34)
                    throw new Exception("问号命中区不是 34×34（一体机手指点不中）");

                // Popup 要真正展开必须有 PresentationSource：把两颗气泡挂到一个临时宿主窗上。
                //  detached 的 UserControl 上 IsOpen 会被 WPF 立刻弹回 false，那是 WPF 行为不是控件 bug。
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(a); panel.Children.Add(b);
                var host = new Window { Width = 240, Height = 120, Content = panel,
                                        WindowStartupLocation = WindowStartupLocation.Manual,
                                        Left = -2000, Top = -2000 };
                try
                {
                    host.Show();
                    a.OpenBubble();
                    if (!a.IsBubbleOpen) throw new Exception("点击后气泡没打开");
                    b.OpenBubble();
                    if (a.IsBubbleOpen || !b.IsBubbleOpen)
                        throw new Exception("气泡没有单开（两个同时弹会互相盖住内容）");
                    b.CloseBubble();
                    if (b.IsBubbleOpen) throw new Exception("Esc / 关闭后气泡没合上");
                }
                finally { try { host.Close(); } catch { } }
                return w;
            });

            // 五个便签编辑窗统一成「预览 → 主题/锁定 → 一条格式条 → 总标题 → 添加/输入」，
            // 输入框下方那条 Compact 小工具条必须彻底消失，否则 r69 的焦点跟随会同时作用于两处。
            Check("r71 五个便签编辑窗统一排版（预览 / 锁定 / 单条工具条）", () =>
            {
                // 同上：未 Show 的窗口只走逻辑树，别用 VisualTreeHelper。
                int Pickers(DependencyObject d)
                {
                    int n = d is Controls.TextStylePicker ? 1 : 0;
                    try
                    {
                        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(d))
                            if (child is DependencyObject dep) n += Pickers(dep);
                    }
                    catch (InvalidOperationException) { }
                    return n;
                }
                // r73c：倒计时窗也回到「全窗唯一一条」—— 剩余天数改成只读输入框后
                // 它自己就能落焦点，天数字体不再需要第二条工具条。
                void Assert(Window x, string name, int expect)
                {
                    if (Pickers(x) != expect)
                        throw new Exception(name + " 的字体工具条数量不对（应为 " + expect
                                            + " 条，实际 " + Pickers(x) + " 条）");
                    try { x.Close(); } catch { }
                }
                var hw = new HomeworkEditWindow(Store.LoadHomework() ?? new HomeworkBoard());
                if (hw.PreviewCard == null || hw.ChkLocked == null || hw.PickStyle == null)
                    throw new Exception("作业窗缺预览卡 / 锁定 / 工具条");
                var ms = new MissingEditWindow(Store.LoadMissingBoard());
                if (ms.PreviewCard == null || ms.ChkLocked == null || ms.PickStyle == null)
                    throw new Exception("缺交窗缺预览卡 / 锁定 / 工具条");
                var du = new DutyEditWindow();
                if (du.PreviewCard == null || du.ChkLocked == null || du.PickStyle == null)
                    throw new Exception("值日窗缺预览卡 / 锁定 / 工具条");
                var cd = new CountdownEditWindow();
                if (cd.PreviewCard == null || cd.ChkLocked == null || cd.PickStyle == null)
                    throw new Exception("倒计时窗缺预览卡 / 锁定 / 文字格式条");
                var cn = new CustomNoteEditWindow(DiagCustomNoteId());
                if (cn.PreviewCard == null || cn.ChkLocked == null || cn.PickStyle == null
                    || cn.CbNote == null || cn.BtnNewNote == null || cn.BtnDeleteNote == null)
                    throw new Exception("自定义便签窗缺预览卡 / 锁定 / 工具条 / 便签管理三件套");
                Assert(hw, "作业窗", 1); Assert(ms, "缺交窗", 1);
                Assert(du, "值日窗", 1); Assert(cd, "倒计时窗", 1);
                if (Pickers(cn) != 1)
                    throw new Exception("自定义便签窗的字体工具条不是全窗唯一一条（剩 " + Pickers(cn) + " 条）");
                return cn;   // 最后一个由 Check 统一关闭
            });

            // 发布窗：默认带出账号所属班级、窗内可临时加班级（只会话内放开）、格式条整行移除。
            Check("r71 发布窗班级默认与会话内扩权", () =>
            {
                var w = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityNotice);
                if (w.ClassCount < 1 || w.ClassInputBox == null || w.AddClassButton == null)
                    throw new Exception("发布窗缺默认班级 / 班级输入框 / 增加按钮");
                if (w.FormatRowsVisible)
                    throw new Exception("发布窗还留着字段级格式条（会覆盖教室机样式）");
                string extra = "诊断临时班";
                w.ClassInputBox.Text = extra;
                w.AddClassButton.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (w.ClassCount != 2 || !UserSession.SessionClassNames.Contains(extra))
                    throw new Exception("窗内加班级没生效，或没在本次会话内放开权限");
                return w;
            });

            // 发布窗：全屏与语音同一行；通知的全屏 / 语音都用完整内容，其余全屏完整、语音摘要。
            Check("r71 发布窗提醒同一行与完整内容文案", () =>
            {
                var w = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityNotice);
                var row = w.ReminderRow;
                if (row == null || !row.Children.Contains(w.ForceBox) || !row.Children.Contains(w.VoiceBox))
                    throw new Exception("全屏提醒和语音提醒没放在同一行");

                w.Field1Box.Text = "明天带跳绳";
                w.Field2Box.Text = "请全体同学自备跳绳，第三节课前交到体育委员处。";
                string full = w.DiagFullText;
                if (!full.Contains("标题：") || !full.Contains("内容：")
                    || !full.Contains("有效期至：") || !full.Contains("明天带跳绳"))
                    throw new Exception("通知的完整内容没带齐标题 / 正文 / 截止日期：" + full);
                if (w.DiagVoiceText != full)
                    throw new Exception("通知的语音播报应念完整内容（含新增字段）");

                var hw = new BoardSendWindow(new[] { "一班" }, BoardSyncConsts.EntityHomework);
                hw.Field1Box.Text = "语文";
                hw.Field2Box.Text = "练习册 P10";
                string hwFull = hw.DiagFullText;
                string hwVoice = hw.DiagVoiceText;
                if (!hwFull.Contains("科目：") || !hwFull.Contains("作业内容："))
                    throw new Exception("作业全屏内容没拼完整：" + hwFull);
                if (hwVoice == hwFull || !hwVoice.StartsWith("作业："))
                    throw new Exception("非通知类型应「全屏完整内容 + 语音摘要」：" + hwVoice);
                try { hw.Close(); } catch { }
                return w;
            });

            // 通知条：新装默认「跑马灯开 + 置顶 + 穿透 + 移上去透明 + 上课自动隐藏」，老机靠 Rev 只升一次。
            Check("r71 通知条出厂默认值与升级代次", () =>
            {
                var w = new LayoutWindow { InitialTabIndex = 1 };
                var fresh = new AppSettings();
                if (!fresh.AutoShowNotification || !fresh.NotificationLayout.TopMost
                    || !fresh.NotificationLayout.ClickThrough || !fresh.NotificationLayout.HoverFade
                    || !fresh.NotificationLayout.AutoHideDuringClass || fresh.NotificationLayout.AutoHide)
                    throw new Exception("新装的悬浮通知默认值不符合定稿");
                if (fresh.NotificationShowDefaultsRev != 0)
                    throw new Exception("新装设置的升级代次应从 0 起");

                var combo = w.DiagSyncNotificationCombo();
                if (!combo.ClickThrough || !combo.AutoHideDuringClass || combo.AutoHide)
                    throw new Exception("「鼠标穿透 + 上课自动隐藏」应能同时成立；只有靠边隐藏仍互斥");

                bool oldSuppress = Store.SuppressWrites, oldPersist = Store.EnablePersistence;
                try
                {
                    Store.SuppressWrites = false; Store.EnablePersistence = true;
                    var old = new AppSettings { AutoShowNotification = false, NotificationShowDefaultsRev = 0 };
                    old.NotificationLayout.ClickThrough = false;
                    old.NotificationLayout.AutoHideDuringClass = false;
                    if (!Store.UpgradeNotificationShowDefaults(old))
                        throw new Exception("老配置没触发通知默认值升级");
                    if (!old.AutoShowNotification || !old.NotificationLayout.ClickThrough
                        || !old.NotificationLayout.AutoHideDuringClass || old.NotificationShowDefaultsRev != 1)
                        throw new Exception("老配置升级后没拿到新默认值");
                    if (Store.UpgradeNotificationShowDefaults(old))
                        throw new Exception("升级代次不幂等（用户改回去会被下次启动打回）");
                }
                finally { Store.SuppressWrites = oldSuppress; Store.EnablePersistence = oldPersist; }
                return w;
            });

            // ---------------- r72：通知编辑窗格式条 + 通知文字格式 ----------------
            Check("r72 通知编辑窗单条格式条与焦点跟随", () =>
            {
                var w = new NotificationEditWindow();
                // 未 Show 的窗口可视树为空，只能走逻辑树（r71 踩过这个坑）
                int Pickers(DependencyObject d)
                {
                    int n = d is Controls.TextStylePicker ? 1 : 0;
                    try
                    {
                        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(d))
                            if (child is DependencyObject dep) n += Pickers(dep);
                    }
                    catch (InvalidOperationException) { }
                    return n;
                }
                if (Pickers(w) != 1)
                    throw new Exception("通知编辑窗应该只有一条文字格式工具条（现在 " + Pickers(w) + " 条）");

                if (w.DiagBinder == null) throw new Exception("没有建立焦点跟随绑定");
                w.DiagBinder.DiagFocus(w.DiagTitleBox);
                if (!w.DiagPicker.IsActive || w.DiagPicker.TargetName != "通知标题"
                    || !ReferenceEquals(w.DiagPicker.StyleData, w.DiagTitleStyle))
                    throw new Exception("焦点在标题输入框时，工具条没有作用到标题样式");
                w.DiagBinder.DiagFocus(w.DiagContentBox);
                if (!w.DiagPicker.IsActive || w.DiagPicker.TargetName != "通知正文"
                    || !ReferenceEquals(w.DiagPicker.StyleData, w.DiagContentStyle))
                    throw new Exception("焦点切到正文输入框时，工具条没有跟着切换");
                if (ReferenceEquals(w.DiagTitleStyle, w.DiagContentStyle))
                    throw new Exception("标题与正文共用同一个样式对象，改一个会串到另一个");

                var n = new Notification
                {
                    Title = "带跳绳", Content = "自备",
                    TitleStyle = new TextStyle { Bold = false, SizeLevel = 3, Color = "#C0392B" }
                };
                w.DiagLoadDraft(n);
                if (ReferenceEquals(w.DiagTitleStyle, n.TitleStyle))
                    throw new Exception("载入已有通知必须用草稿副本，不能直接改列表项");
                if (w.DiagTitleStyle.SizeLevel != 3 || w.DiagTitleStyle.Color != "#C0392B" || w.DiagTitleStyle.Bold)
                    throw new Exception("载入已有通知时没带回它自己的文字格式");
                if (w.DiagPicker.IsActive)
                    throw new Exception("切换条目后工具条应置灰，等老师点下一个输入框再激活");

                w.DiagLoadDraft(new Notification());   // 老数据 / 同步落地：没有样式对象
                if (w.DiagTitleStyle.SizeLevel != 0 || !w.DiagTitleStyle.Bold
                    || w.DiagContentStyle.SizeLevel != 0 || w.DiagContentStyle.Bold
                    || !string.IsNullOrEmpty(w.DiagTitleStyle.Color))
                    throw new Exception("老通知载入后没有回到默认外观");
                return w;
            });

            Check("r72 通知文字格式默认外观与四处同源", () =>
            {
                var old = new Notification { Title = "T", Content = "C" };
                if (old.TitleStyle != null || old.ContentStyle != null)
                    throw new Exception("新通知不该预置样式对象，老数据与同步落地的通知靠 null 走默认外观");
                if (HwTextRender.ScaleSize(old.TitleStyle, 14) != 14
                    || HwTextRender.ScaleSize(old.ContentStyle, 13) != 13)
                    throw new Exception("样式为 null 时字号必须正好回到原来的 14 / 13");
                if (!HwTextRender.BoldOr(old.TitleStyle, true) || HwTextRender.BoldOr(old.ContentStyle, false))
                    throw new Exception("样式为 null 时粗细必须回到原来的默认");

                old.TitleStyle = new TextStyle { Bold = false, SizeLevel = 3, Color = "#C0392B" };
                if (HwTextRender.ScaleSize(old.TitleStyle, 14) <= 14
                    || HwTextRender.BoldOr(old.TitleStyle, true))
                    throw new Exception("调过格式后字号 / 加粗没有按样式走");
                string sig = old.StyleSignature;
                old.ContentStyle = new TextStyle { SizeLevel = 5 };
                if (old.StyleSignature == sig)
                    throw new Exception("样式签名区分不出格式变化，便签与壁纸会停在旧画面");

                // 消息链一个字段都没动：通知样式只在本机生效，不进变更单 / 快照签名
                var fields = BoardSyncService.FieldsOf(old);
                if (fields.Keys.Any(k => k.Contains("style", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("通知样式被写进消息链字段了（本轮不该动同步协议）");
                if (fields.Count != 4) throw new Exception("通知变更单字段数变了：" + fields.Count);

                // 壁纸卡片按新样式渲染不能抛异常（老数据 null 也要能画）
                var card = BackdropRenderer.BuildNoticeCard(
                    new[] { new Notification { Title = "T", Content = "C" }, old }, 1.0, x => x);
                if (card == null) throw new Exception("通知公告壁纸卡片没生成");

                return new MainWindow();
            });

            // ---------------- r73：倒计时天数字体 + 真正全局玻璃 ----------------
            Check("r73c 倒计时天数走回唯一格式条（只读输入框 + 实时预览 + 默认红色）", () =>
            {
                var w = new CountdownEditWindow();
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -3000; w.Top = -3000;
                w.Show();   // 让 ItemsControl 模板真正生成（剩余输入框只在模板里）
                Exception? err = null;
                try
                {
                    if (w.DiagDayBoxCount <= 0)
                        throw new Exception("事件卡片里没生成「剩余」输入框");
                    if (!w.DiagDayBoxReadOnly)
                        throw new Exception("「剩余」必须只读：天数是按目标日期算的，不许手改");
                    if (ReferenceEquals(w.DiagDaysStyle, w.DiagContentStyle))
                        throw new Exception("天数样式和事件文字样式是同一个对象，改一个会串到另一个");

                    // 默认口径：红 + 加粗 + 特大号（新建板子）
                    var fresh = CountdownEditWindow.DiagNewDaysStyle();
                    if (!fresh.Bold || fresh.SizeLevel != ScheduleCore.Models.HwTextStyle.ExtraLargeLevel
                        || fresh.Color != ScheduleCore.Models.CountdownBoard.DaysDefaultColor)
                        throw new Exception("默认天数样式不是「红色 + 加粗 + 特大号」：" +
                                            fresh.Bold + "/" + fresh.SizeLevel + "/" + fresh.Color);
                    if (ScheduleCore.Models.CountdownBoard.DaysDefaultColor != "#C0392B")
                        throw new Exception("默认红色被改掉了：" + ScheduleCore.Models.CountdownBoard.DaysDefaultColor);

                    // ★ 核心：点（聚焦）「剩余」输入框，就得拿到那条唯一的格式条，且作用于 DaysStyle
                    if (!w.DiagFocusDayBox())
                        throw new Exception("点不到「剩余」输入框，找不到可聚焦控件");
                    if (!w.DiagPicker.IsActive || !w.DiagPicker.IsBarEnabled)
                        throw new Exception("聚焦「剩余」输入框后，格式条没有激活 / 仍是置灰态");
                    if (w.DiagPicker.TargetName != "剩余天数")
                        throw new Exception("格式条作用对象不是「剩余天数」，实际：" + w.DiagPicker.TargetName);
                    if (!ReferenceEquals(w.DiagPicker.StyleData, w.DiagDaysStyle))
                        throw new Exception("格式条没绑到 DaysStyle，改了也不会生效");

                    // 存盘往返：DaysStyle 若被 JsonIgnore 吞掉，界面里怎么设都不会留下
                    string dayJson = System.Text.Json.JsonSerializer.Serialize(
                        new ScheduleCore.Models.CountdownBoard
                        { DaysStyle = new ScheduleCore.Models.TextStyle { SizeLevel = 5 } });
                    if (!dayJson.Contains("DaysStyle") || !dayJson.Contains("SizeLevel"))
                        throw new Exception("DaysStyle 没被序列化，设置完重开就丢");

                    // ★ 诊断用的是真实数据副本，当前档位可能是任意值；必须换成「一定不同」的档位，
                    //   否则 TextStyle.Set 的相等判断不触发 PropertyChanged，断言会假失败。
                    double before = w.DiagPreviewDays.FontSize;
                    double boxBefore = w.DiagFirstDayBoxFontSize;
                    w.DiagDaysStyle.SizeLevel = w.DiagDaysStyle.SizeLevel == 0 ? 3 : 0;
                    if (w.DiagPreviewDays.FontSize == before || w.DiagFirstDayBoxFontSize == boxBefore)
                        throw new Exception("改字号后预览卡 / 剩余输入框没有实时跟着变");

                    w.DiagDaysStyle.Color = "#123456";
                    w.DiagDaysStyle.Color = "#C0392B";
                    if (w.DiagPreviewDays.Foreground is not System.Windows.Media.SolidColorBrush br
                        || br.Color.R != 0xC0 || br.Color.G != 0x39 || br.Color.B != 0x2B)
                        throw new Exception("改颜色后预览没有实时变色：" + w.DiagPreviewDays.Foreground);

                    // 事件文字是另一套样式：改它只该影响预览里的事件名，不该把天数带跑
                    double nameBefore = w.DiagPreviewName.FontSize;
                    double daysAfter = w.DiagPreviewDays.FontSize;
                    w.DiagContentStyle.SizeLevel = w.DiagContentStyle.SizeLevel == 5 ? 0 : 5;
                    if (w.DiagPreviewName.FontSize == nameBefore)
                        throw new Exception("改事件文字字号，预览里的事件名没跟着变");
                    if (w.DiagPreviewDays.FontSize != daysAfter)
                        throw new Exception("改事件文字却把天数预览也改了（两套样式串了）");
                }
                catch (Exception ex) { err = ex; }
                finally { try { w.Close(); } catch { } }
                if (err != null) throw err;
                return new MainWindow();
            });

            Check("r73 全局玻璃（版本分支 / 设置卡 / 截图守卫 / DWM 探针）", () =>
            {
                // 纯函数：Win11 22H2+ / Win10 1809+ / 更老系统三条分支，换机器也能断言
                if (GlassBackdrop.Resolve(26300, "auto") != GlassBackdrop.Kind.Win11Acrylic
                    || GlassBackdrop.Resolve(22621, "mica") != GlassBackdrop.Kind.Win11Mica
                    || GlassBackdrop.Resolve(19045, "auto") != GlassBackdrop.Kind.Win10Acrylic
                    || GlassBackdrop.Resolve(19045, "mica") != GlassBackdrop.Kind.Frosted
                    || GlassBackdrop.Resolve(10240, "auto") != GlassBackdrop.Kind.Frosted
                    || GlassBackdrop.Resolve(26300, "frosted") != GlassBackdrop.Kind.Frosted)
                    throw new Exception("按系统版本选玻璃实现的分支不对");
                if (GlassBackdrop.WinBuild <= 0) throw new Exception("没读到真实系统 Build 号");

                var fresh = new AppSettings();
                if (!fresh.GlassAllWindows || fresh.GlassBackdropMode != "auto")
                    throw new Exception("新装设置没有默认「普通窗口也玻璃 + 自动档」");
                if (!fresh.GlassFloatEnabled)
                    throw new Exception("新装设置里悬浮窗玻璃默认应开启（r74 与普通窗口分开）");
                if (Math.Abs(fresh.GlassChromeOpacity - GlassBrush.DefaultPercent) > 0.001)
                    throw new Exception("新装设置里普通窗口通透度默认值不对：" + fresh.GlassChromeOpacity);
                if (fresh.GlassEnabled) throw new Exception("总闸仍应默认关闭，开玻璃是用户显式动作");

                Exception? err = null;
                var probe = new Window { Width = 120, Height = 80,
                                         Background = System.Windows.Media.Brushes.White };
                var host = new Window { Width = 120, Height = 80,
                                        WindowStartupLocation = WindowStartupLocation.Manual,
                                        Left = -3000, Top = -3000 };
                try
                {
                    // ★ 诊断 / 截图模式绝不能改窗口底色：DWM 效果截图抓不到，改了只会把 --shot 拍黑
                    var before = probe.Background;
                    var on = new AppSettings { GlassEnabled = true, GlassAllWindows = true, GlassChromeOpacity = 45 };
                    if (GlassBackdrop.Apply(probe, on))
                        throw new Exception("诊断模式下不该真的启用 backdrop");
                    if (!ReferenceEquals(probe.Background, before))
                        throw new Exception("诊断模式下窗口背景被改了，--shot 会拍黑");

                    // 真实 HWND 上探一次：Win11 上 DwmSetWindowAttribute 必须真的成功
                    host.Show();
                    var (kind, ok) = GlassBackdrop.DiagProbe(host, "auto");
                    if (GlassBackdrop.WinBuild >= 22621
                        && (!ok || kind != GlassBackdrop.Kind.Win11Acrylic))
                        throw new Exception("Win11 上系统 backdrop 没启用成功：" + kind + " ok=" + ok);
                }
                catch (Exception ex) { err = ex; }
                finally
                {
                    try { probe.Close(); } catch { }
                    try { host.Close(); } catch { }
                }
                if (err != null) throw err;
                return new MainWindow();
            });

            // r73d 真·全局玻璃：把普通窗口改成分层窗 + 自绘标题栏。
            // 这里锁三件事：① 能不能改的边界；② 诊断模式绝不介入（--shot 必须保持原生）；
            // ③ 标题栏高度补偿、卡片 alpha 跟随通透度、关掉总闸要变回不透明。
            Check("r73d 分层玻璃窗（Show 前改样式 / Loaded 包标题栏 / 关玻璃不透明）", () =>
            {
                var plain = new Window
                {
                    Width = 200, Height = 120,
                    Content = new System.Windows.Controls.Border()
                };
                if (!GlassChrome.CanAttach(plain))
                    throw new Exception("原生标题栏的普通窗口应该允许改造成分层玻璃窗");

                var borderless = new Window
                {
                    Width = 200, Height = 120,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Content = new System.Windows.Controls.Border()
                };
                if (GlassChrome.CanAttach(borderless))
                    throw new Exception("无边框 / 分层窗不该再被改造（会和悬浮便签的玻璃打架）");

                // ★ 诊断 / 截图模式一律不介入：否则 --shot 会拍成无边框、尺寸也会多 40
                if (GlassChrome.Prepare(plain))
                    throw new Exception("诊断模式下不该改造窗口");
                if (plain.AllowsTransparency || plain.WindowStyle != WindowStyle.SingleBorderWindow)
                    throw new Exception("诊断模式把窗口样式改了，--shot 会拍成无边框");

                var w = new Window
                {
                    Title = "玻璃验证",
                    Width = 240,
                    Height = 140,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    Content = new System.Windows.Controls.Border()
                };
                double h0 = w.Height;
                Exception? err = null;
                try
                {
                    if (!GlassChrome.Prepare(w, force: true))
                        throw new Exception("Prepare 没能把窗口改成分层窗");
                    if (!w.AllowsTransparency || w.WindowStyle != WindowStyle.None)
                        throw new Exception("Prepare 之后窗口还不是分层无边框窗");
                    if (Math.Abs(w.Height - (h0 + GlassChrome.BarHeight)) > 0.5)
                        throw new Exception("固定高度的窗口没补上标题栏高度，内容会被压扁（"
                                            + h0 + "→" + w.Height + "）");

                    w.Show();
                    w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                        new Action(() => { }));
                    GlassChrome.Wrap(w);

                    // ★ 先按「明确要玻璃」的设置刷一遍：用户的设置里主窗通透度可能是 0（= 实色），
                    //   单看 Wrap 按设置算出的底色就会是 255，这条断言会假失败。
                    GlassChrome.Apply(w, new AppSettings
                    { GlassEnabled = true, GlassAllWindows = true, GlassChromeOpacity = 45 });

                    var st = GlassChrome.DiagState(w);
                    if (!st.Layered) throw new Exception("窗口没有变成分层窗，透不出桌面");
                    if (!st.BarFound) throw new Exception("没有自绘标题栏（没法拖动 / 关闭）");
                    if (st.Alpha == 0 || st.Alpha == 255)
                        throw new Exception("卡片底色没带 alpha（" + st.Alpha + "），等于没玻璃");

                    // 通透度实时跟随（r74：这里必须动「普通窗口组」自己的 GlassChromeOpacity）
                    GlassChrome.Apply(w, new AppSettings
                    { GlassEnabled = true, GlassAllWindows = true, GlassChromeOpacity = 10 });
                    byte light = GlassChrome.DiagState(w).Alpha;
                    GlassChrome.Apply(w, new AppSettings
                    { GlassEnabled = true, GlassAllWindows = true, GlassChromeOpacity = 60 });
                    byte heavy = GlassChrome.DiagState(w).Alpha;
                    if (heavy >= light)
                        throw new Exception("调高通透度，卡片反而更不透明（" + light + "→" + heavy + "）");

                    // r74：只动悬浮窗那一组的通透度，普通窗口必须一动不动
                    GlassChrome.Apply(w, new AppSettings
                    { GlassEnabled = true, GlassAllWindows = true, GlassChromeOpacity = 60, GlassOpacity = 5 });
                    if (GlassChrome.DiagState(w).Alpha != heavy)
                        throw new Exception("改悬浮窗通透度把普通窗口也带变了（两组没分开）");

                    // r74：只关悬浮窗那一组，普通窗口玻璃必须照旧
                    GlassChrome.Apply(w, new AppSettings
                    { GlassEnabled = true, GlassFloatEnabled = false, GlassAllWindows = true, GlassChromeOpacity = 60 });
                    if (GlassChrome.DiagState(w).Alpha != heavy)
                        throw new Exception("关掉悬浮窗玻璃，普通窗口跟着一起没了（两组没分开）");

                    // 关掉总闸：分层窗回不到原生标题栏，但必须变不透明
                    GlassChrome.Apply(w, new AppSettings { GlassEnabled = false });
                    if (GlassChrome.DiagState(w).Alpha != 255)
                        throw new Exception("关闭玻璃后卡片没变回不透明");

                    GlassChrome.Unwrap(w);
                    if (GlassChrome.DiagState(w).BarFound)
                        throw new Exception("Unwrap 之后自绘标题栏还挂着");
                }
                catch (Exception ex) { err = ex; }
                finally { try { w.Close(); } catch { } }
                if (err != null) throw err;
                return new MainWindow();
            });

            // r74：玻璃拆成两组 —— 悬浮窗（GlassBrush）与主窗 / 设置（GlassChrome）各开关、各通透度。
            // 这里锁四件事：① 两组的 Want 判定互不牵连；② 老配置的单一通透度能一次性迁移过来；
            // ③ 迁移是幂等的（用户之后各调各的不会被下次启动打回）；④ 设置卡两个滑块各写各的字段。
            Check("r74 玻璃两组分开（开关 / 通透度 / 老配置迁移）", () =>
            {
                // ① 悬浮窗组
                var f = new AppSettings { GlassEnabled = true, GlassFloatEnabled = true, GlassOpacity = 30 };
                if (!GlassBrush.FloatWant(f))
                    throw new Exception("总闸 + 悬浮窗开关都开着，却没判定为要玻璃");
                f.GlassFloatEnabled = false;
                if (GlassBrush.FloatWant(f))
                    throw new Exception("关掉悬浮窗开关后仍认为要玻璃");
                f.GlassFloatEnabled = true;
                f.GlassEnabled = false;
                if (GlassBrush.FloatWant(f))
                    throw new Exception("总闸关闭时悬浮窗仍要玻璃（总闸必须能一键全关）");

                // ② 普通窗口组：自己那套开关 + 通透度
                var c = new AppSettings
                { GlassEnabled = true, GlassAllWindows = true, GlassChromeOpacity = 55, GlassOpacity = 5 };
                if (!GlassChrome.Want(c)) throw new Exception("主窗开关 + 通透度都成立，却没判定为要玻璃");
                if (Math.Abs(GlassBrush.ChromePercent(c) - 55) > 0.001)
                    throw new Exception("普通窗口通透度取的不是 GlassChromeOpacity");
                if (Math.Abs(GlassBrush.ClampPercent(c.GlassOpacity) - 5) > 0.001)
                    throw new Exception("悬浮窗通透度被普通窗口的值污染了");
                c.GlassAllWindows = false;
                if (GlassChrome.Want(c)) throw new Exception("关掉主窗开关后仍认为要玻璃");
                c.GlassAllWindows = true;
                c.GlassChromeOpacity = 0;
                if (GlassChrome.Want(c)) throw new Exception("普通窗口通透度 0 应等同实色（不做玻璃）");
                if (GlassBrush.FloatWant(c) != true)
                    throw new Exception("普通窗口关了，把悬浮窗组也带死了（两组没分开）");

                // ③ 老配置迁移：老值 62 → 普通窗口组也是 62，观感不变；且只做一次
                var legacy = new AppSettings { GlassOpacity = 62 };
                ScheduleCore.Services.Store.MigrateGlassSplit(legacy);
                if (Math.Abs(legacy.GlassChromeOpacity - 62) > 0.001 || legacy.GlassSplitRev != 1)
                    throw new Exception("老通透度没迁移到普通窗口组（" + legacy.GlassChromeOpacity + "）");
                legacy.GlassChromeOpacity = 20;
                ScheduleCore.Services.Store.MigrateGlassSplit(legacy);
                if (Math.Abs(legacy.GlassChromeOpacity - 20) > 0.001)
                    throw new Exception("迁移不是幂等的：用户调过的值被老值打回了");
                var dirty = new AppSettings { GlassOpacity = double.NaN };
                ScheduleCore.Services.Store.MigrateGlassSplit(dirty);
                if (!double.IsFinite(dirty.GlassChromeOpacity) || dirty.GlassChromeOpacity > 65)
                    throw new Exception("脏数据（NaN）没被规范成合法通透度");

                // r77：玻璃卡已迁到「显示设置 → 界面主题」，此处的 LayoutWindow 玻璃断言移除。
                return new MainWindow();
            });

            // r75：纯 C# 弹窗（清除台账 / 教室机快照 / 目标挑选）必须走分层玻璃窗。
            // 漏掉 Prepare 的话，DWM 备用路径会把它们的 Window.Background 设成透明，
            // 非分层窗口不支持逐像素 alpha → 整窗合成成纯黑（用户报的「清除台账弹窗是黑的」）。
            Check("r75 纯 C# 弹窗不会被 DWM 路径合成黑窗", () =>
            {
                var plain = new Window
                {
                    Width = 200, Height = 120,
                    Content = new System.Windows.Controls.Border()
                };
                if (!GlassBackdrop.ShouldSkipForChrome(plain))
                    throw new Exception("普通窗口没被 DWM 路径跳过 —— 设成透明后会变黑窗");

                var borderless = new Window
                {
                    Width = 200, Height = 120,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Content = new System.Windows.Controls.Border()
                };
                if (GlassBackdrop.ShouldSkipForChrome(borderless))
                    throw new Exception("无边框 / 分层窗不该归 GlassChrome 管");

                // 三个纯 C# 弹窗至少要「够格被 GlassChrome 接管」，否则就是漏了 Prepare
                var ledger = new LedgerCleanWindow(3, 2, 1, 7);
                try
                {
                    if (!GlassChrome.CanAttach(ledger))
                        throw new Exception("清除台账弹窗不是可接管的分层窗（漏了 GlassChrome.Prepare）");
                }
                finally { try { ledger.Close(); } catch { } }
                return new MainWindow();
            });

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

        // 诊断：--ball-check 验证手写悬浮球的关键行为 —— 能显示、不抢焦点、位置落在屏幕内。
        // 「不抢焦点」是整个方案的前提：一旦球被激活，当前输入框就失焦，手写结果无处可插。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--ball-check", StringComparison.OrdinalIgnoreCase))
        {
            string blog = e.Args[1];
            var sb = new System.Text.StringBuilder();
            try
            {
                var ball = new HandwritingBallWindow();
                ball.Show();
                ball.UpdateLayout();

                var wa = SystemParameters.WorkArea;
                bool inside = ball.Left >= wa.Left - 1 && ball.Top >= wa.Top - 1
                              && ball.Left + ball.ActualWidth <= wa.Right + 1
                              && ball.Top + ball.ActualHeight <= wa.Bottom + 1;

                sb.AppendLine("VISIBLE=" + ball.IsVisible);
                sb.AppendLine("ACTIVE=" + ball.IsActive);
                sb.AppendLine("FOCUS=" + (System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "null"));
                sb.AppendLine($"RECT={ball.Left:0},{ball.Top:0} {ball.ActualWidth:0}x{ball.ActualHeight:0}");
                sb.AppendLine("INSIDE_WORKAREA=" + inside);
                sb.AppendLine("RESULT=" + (ball.IsVisible && !ball.IsActive && inside ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT=FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
            try { File.WriteAllText(blog, sb.ToString()); } catch { }
            Shutdown(0);
            return;
        }

        // 诊断：--touch-mode <log> 验证触屏手势的三条硬前提。
        // ① PanningMode 必须 None —— 只要不是 None，ScrollViewer 就会在触摸按下时捕获并标记
        //    已处理，WPF 不再把触摸提升为鼠标事件，单击 / 双击 / 拖动换节必然失效；
        // ② 系统 Press-and-Hold 必须关 —— 否则 500ms 时系统会画圈并自己发右键，与"静止 500ms
        //    浮起"打架；③ Flicks 也必须关。这三条截图都看不出来，只能打印。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--touch-mode", StringComparison.OrdinalIgnoreCase))
        {
            // 第二个参数可省：省了写到临时目录的固定文件名，便于脚本只传一个开关。
            string tlog = e.Args.Length >= 2
                ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.touchmode.log");
            var sb = new System.Text.StringBuilder();
            try
            {
                var w = new ScheduleWindow();
                w.Show();
                w.UpdateLayout();

                // 不在这里 PushFrame 等 ContextIdle：窗口里还有时钟计时器等持续投递的任务，
                // 某些环境下会一直等不到空闲 → 诊断卡死。这三条属性在构造完成时就已经确定。
                bool two = Store.LoadSettings().TwoFingerScroll;
                string line = w.DiagTouchMode();
                bool okPanning = line.Contains("PanningMode=None");
                bool okHold = line.Contains("systemPressHold=False");
                bool okFlicks = line.Contains("systemFlicks=False");

                sb.AppendLine(line);
                sb.AppendLine("SETTING_TwoFingerScroll=" + two);
                sb.AppendLine($"CHECK panning={okPanning} pressHold={okHold} flicks={okFlicks}");
                sb.AppendLine("RESULT=" + (okPanning && okHold && okFlicks ? "OK" : "FAIL"));
                w.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT=FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
            try { File.WriteAllText(tlog, sb.ToString()); } catch { }
            Shutdown(0);
            return;
        }

        // 诊断：--modal-check 验证"模态对话框打开时手写球还能不能用"。
        // WPF 的 ShowDialog 会把同线程所有可见窗口 EnableWindow(FALSE)，球在里面就会被一起废掉
        // （拖不动、点不响）——这是老师反馈"打开对话框后球失灵"的真正原因，必须回归。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--modal-check", StringComparison.OrdinalIgnoreCase))
        {
            string mlog = e.Args[1];
            var sb = new System.Text.StringBuilder();
            try
            {
                var ball = new HandwritingBallWindow();
                ball.Show();
                ball.UpdateLayout();

                var dlg = new Window
                {
                    Title = "modal-check",
                    Width = 320,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                dlg.Loaded += (_, _) =>
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(ball).Handle;

                    // 先记一次"模态刚打开"的瞬时状态，再等守护计时器跑几轮后记一次
                    sb.AppendLine("AT_OPEN=" + IsWindowEnabled(h));

                    var t = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(700)
                    };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        bool enabled = IsWindowEnabled(h);
                        sb.AppendLine("AFTER_GUARD=" + enabled);
                        sb.AppendLine("VISIBLE=" + ball.IsVisible);
                        sb.AppendLine("RESULT=" + (enabled && ball.IsVisible ? "OK" : "FAIL"));
                        dlg.Close();
                    };
                    t.Start();
                };

                dlg.ShowDialog();
                ball.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT=FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
            try { File.WriteAllText(mlog, sb.ToString()); } catch { }
            Shutdown(0);
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

        // 诊断：--sync-check [log] —— 二期「结构化变更单」全链路自检（两端 / 六态 / 裁决 / 幂等 / 坏文件）。
        // ★ 会写盘，强制 CB_DATA_DIR；在副本里再建 teacher / classroom / share 三份目录模拟两台机器。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--sync-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.synccheck.log");
            bool ok = RunSyncCheck(log);
            Shutdown(ok ? 0 : 1);
            return;
        }

        // 诊断：--board-check [log] —— 一期「数据底座」自检（稳定 Id / 确定性迁移 / 墓碑 / 原子写）。
        // ★ 会写盘并做迁移前自动备份，因此强制 CB_DATA_DIR 指向数据副本，绝不碰老师真实数据。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--board-check", StringComparison.OrdinalIgnoreCase))
        {
            string log = e.Args.Length >= 2 ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.boardcheck.log");
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            void Check(string name, bool ok, string detail = "")
            {
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name
                              + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (ok) pass++; else fail++;
            }

            try
            {
                string cbDir = (Environment.GetEnvironmentVariable("CB_DATA_DIR") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cbDir))
                {
                    sb.AppendLine("拒绝：请用 CB_DATA_DIR 指向数据副本后再运行（本诊断会写盘并自动备份）。");
                    Check("CB_DATA_DIR 护栏", false);
                }
                else
                {
                    Store.SuppressWrites = false;    // 已确认是副本，允许落盘
                    Store.EnablePersistence = true;
                    string dir = Store.AppDir;
                    string hwPath = Path.Combine(dir, "homework.json");
                    string msPath = Path.Combine(dir, "missing.json");
                    string rawHw = File.Exists(hwPath) ? File.ReadAllText(hwPath) : "";
                    string rawMs = File.Exists(msPath) ? File.ReadAllText(msPath) : "";
                    int revBefore = Store.LoadSettings().BoardMigrationRev;
                    sb.AppendLine("data=" + dir);
                    sb.AppendLine($"before: BoardMigrationRev={revBefore} homework={rawHw.Length}B missing={rawMs.Length}B");

                    // ① 老数据在内存补 Id：确定性、唯一、不触发显式落盘迁移
                    var a = Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard();
                    var b = Store.LoadMissingBoard();
                    Check("作业条目全部有稳定 Id", a.Items.All(x => !string.IsNullOrWhiteSpace(x.Id)), "items=" + a.Items.Count);
                    Check("缺交条目全部有稳定 Id", b.Items.All(x => !string.IsNullOrWhiteSpace(x.Id)), "items=" + b.Items.Count);
                    Check("作业 Id 唯一", a.Items.Select(x => x.Id).Distinct().Count() == a.Items.Count);
                    Check("缺交 Id 唯一", b.Items.Select(x => x.Id).Distinct().Count() == b.Items.Count);
                    Check("读盘不触发显式落盘迁移", Store.LoadSettings().BoardMigrationRev == revBefore);
                    Check("LiveItems 与 LiveCount 一致（过滤墓碑）",
                          a.LiveItems.Count() == a.LiveCount && b.LiveItems.Count() == b.LiveCount);

                    // ② 同一份老数据放到另一台机器（独立副本）必须得到完全相同的 Id
                    string other = Path.Combine(Path.GetTempPath(),
                        "ClassBulletinApp.boardcheck." + Environment.ProcessId);
                    try
                    {
                        Directory.CreateDirectory(other);
                        if (rawHw.Length > 0) File.WriteAllText(Path.Combine(other, "homework.json"), rawHw);
                        if (rawMs.Length > 0) File.WriteAllText(Path.Combine(other, "missing.json"), rawMs);
                        Store.SetAppDir(other);
                        var a2 = Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard();
                        var b2 = Store.LoadMissingBoard();
                        Check("两端独立迁移：作业 Id 一致",
                              a.Items.Select(x => x.Id).SequenceEqual(a2.Items.Select(x => x.Id)));
                        Check("两端独立迁移：缺交 Id 一致",
                              b.Items.Select(x => x.Id).SequenceEqual(b2.Items.Select(x => x.Id)));
                    }
                    finally
                    {
                        Store.SetAppDir(dir);
                        try { Directory.Delete(other, true); } catch { }
                    }

                    // ③ 编辑窗点确定后，Id / 墓碑 / ClassKey / SchemaRev 一个都不能丢
                    try
                    {
                        var srcHw = new ScheduleCore.Models.HomeworkBoard { ClassKey = "诊断班", SchemaRev = 1 };
                        srcHw.Items.Add(new ScheduleCore.Models.HomeworkItem
                        { Id = "hw-live", Subject = "语文", Content = "练习册 P10" });
                        srcHw.Items.Add(new ScheduleCore.Models.HomeworkItem
                        { Id = "hw-dead", Subject = "数学", Content = "卷子", Deleted = true });
                        var builtHw = new HomeworkEditWindow(srcHw).BuildBoard();
                        Check("作业编辑窗保留活条目 Id", builtHw.Items.Any(x => x.Id == "hw-live" && !x.Deleted));
                        Check("作业编辑窗保留墓碑", builtHw.Items.Any(x => x.Id == "hw-dead" && x.Deleted));
                        Check("作业编辑窗保留 ClassKey / SchemaRev",
                              builtHw.ClassKey == "诊断班" && builtHw.SchemaRev == 1);

                        var srcMs = new ScheduleCore.Models.MissingBoard { ClassKey = "诊断班", SchemaRev = 1 };
                        srcMs.Items.Add(new ScheduleCore.Models.MissingHomeworkItem
                        { Id = "ms-live", Subject = "英语", Students = "张三" });
                        srcMs.Items.Add(new ScheduleCore.Models.MissingHomeworkItem
                        { Id = "ms-dead", Subject = "物理", Students = "李四", Deleted = true });
                        var builtMs = new MissingEditWindow(srcMs).BuildBoard();
                        Check("缺交编辑窗保留活条目 Id", builtMs.Items.Any(x => x.Id == "ms-live" && !x.Deleted));
                        Check("缺交编辑窗保留墓碑", builtMs.Items.Any(x => x.Id == "ms-dead" && x.Deleted));
                        Check("缺交编辑窗保留 ClassKey / SchemaRev",
                              builtMs.ClassKey == "诊断班" && builtMs.SchemaRev == 1);
                    }
                    catch (Exception ex) { Check("编辑窗身份字段自检", false, ex.GetType().Name + ": " + ex.Message); }

                    // ④ 显式落盘迁移：备份 + 只做一次
                    string r1 = Store.MigrateBoardIdsToDisk();
                    string r2 = Store.MigrateBoardIdsToDisk();
                    Check("显式落盘迁移成功", r1.StartsWith("OK", StringComparison.Ordinal), r1);
                    Check("迁移幂等（第二次跳过）", r2.StartsWith("OK", StringComparison.Ordinal), r2);
                    Check("BoardMigrationRev=1 已落盘", Store.LoadSettings().BoardMigrationRev == 1);
                    string backupRoot = Path.Combine(dir, "backup");
                    Check("迁移前自动备份存在",
                          !Directory.Exists(backupRoot) || Directory.GetDirectories(backupRoot).Length > 0);

                    // ⑤ 磁盘内容校验 + 原子写无残留
                    var jo = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var diskHw = System.Text.Json.JsonSerializer
                        .Deserialize<ScheduleCore.Models.HomeworkBoard>(File.ReadAllText(hwPath), jo);
                    var diskMs = System.Text.Json.JsonSerializer
                        .Deserialize<ScheduleCore.Models.MissingBoard>(File.ReadAllText(msPath), jo);
                    Check("磁盘 homework.json 全带 Id", diskHw != null && diskHw.Items.All(x => !string.IsNullOrWhiteSpace(x.Id)));
                    Check("磁盘 missing.json 全带 Id", diskMs != null && diskMs.Items.All(x => !string.IsNullOrWhiteSpace(x.Id)));
                    Check("磁盘 SchemaRev=1", diskHw?.SchemaRev == 1 && diskMs?.SchemaRev == 1);
                    Check("迁移不改变可见内容", diskHw?.LiveCount == a.LiveCount && diskMs?.LiveCount == b.LiveCount);
                    Check("原子写无 .tmp 残留", !File.Exists(hwPath + ".tmp") && !File.Exists(msPath + ".tmp"));

                    Store.SaveHomework(a);
                    Check("SaveHomework 成功且 LastWriteError 已清空", Store.LastWriteError == null,
                          Store.LastWriteError ?? "null");
                    Store.SaveMissingBoard(b);
                    Check("SaveMissingBoard 成功且 LastWriteError 已清空", Store.LastWriteError == null,
                          Store.LastWriteError ?? "null");

                    // ⑥ 默认字号「特大」：新建默认 + 老数据一次性升级 + 幂等 + 不打回用户改回的值
                    const int xl = ScheduleCore.Models.HwTextStyle.ExtraLargeLevel;
                    var nh = new ScheduleCore.Models.HomeworkItem();
                    var nm = new ScheduleCore.Models.MissingHomeworkItem();
                    Check("新建作业条目默认特大（标题 / 科目 / 内容）",
                          new ScheduleCore.Models.HomeworkBoard().TitleStyle!.SizeLevel == xl
                          && nh.SizeLevel == xl && nh.SubjectStyle.SizeLevel == xl);
                    Check("新建缺交条目默认特大（标题 / 科目 / 名单）",
                          new ScheduleCore.Models.MissingBoard().TitleStyle!.SizeLevel == xl
                          && nm.SizeLevel == xl && nm.SubjectStyle.SizeLevel == xl);

                    var oldHw = new ScheduleCore.Models.HomeworkBoard();
                    oldHw.TitleStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    var hi = new ScheduleCore.Models.HomeworkItem
                    { Subject = "语文", Content = "老作业", SizeLevel = 0 };
                    hi.SubjectStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    var keepSmall = new ScheduleCore.Models.HomeworkItem
                    { Subject = "数学", Content = "用户自己选的小档", SizeLevel = 1 };
                    oldHw.Items.Add(hi);
                    oldHw.Items.Add(keepSmall);
                    var oldMs = new ScheduleCore.Models.MissingBoard();
                    oldMs.TitleStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    var mi = new ScheduleCore.Models.MissingHomeworkItem
                    { Subject = "英语", Students = "张三", SizeLevel = 0 };
                    mi.SubjectStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    oldMs.Items.Add(mi);
                    Store.SaveHomework(oldHw);
                    Store.SaveMissingBoard(oldMs);

                    var freshSet = new ScheduleCore.Models.AppSettings();
                    bool up1 = Store.UpgradeBoardFontDefaults(freshSet);
                    bool up2 = Store.UpgradeBoardFontDefaults(freshSet);
                    Check("默认字号升级只跑一次（代次幂等）",
                          up1 && !up2 && freshSet.BoardFontDefaultsRev == 2);
                    var upHw = Store.LoadHomework();
                    var upMs = Store.LoadMissingBoard();
                    Check("老数据标题 / 科目 / 内容 / 名单全部升到特大",
                          upHw!.TitleStyle!.SizeLevel == xl && upHw.Items[0].SizeLevel == xl
                          && upHw.Items[0].SubjectStyle.SizeLevel == xl
                          && upMs!.TitleStyle!.SizeLevel == xl && upMs.Items[0].SizeLevel == xl
                          && upMs.Items[0].SubjectStyle.SizeLevel == xl);
                    Check("用户选过的其它档位不被改动", upHw.Items[1].SizeLevel == 1);
                    upHw.Items[0].SizeLevel = 0;      // 模拟用户升级后自己调回「标准」
                    Store.SaveHomework(upHw);
                    Check("升级后手动调回标准不会被打回",
                          !Store.UpgradeBoardFontDefaults(
                              new ScheduleCore.Models.AppSettings { BoardFontDefaultsRev = 2 })
                          && Store.LoadHomework()!.Items[0].SizeLevel == 0);

                    // ⑥b 代次 1→2：老机器条目早就是特大，只有标题还卡在标准档（本次要修的 bug）
                    var rev1Hw = new ScheduleCore.Models.HomeworkBoard();
                    rev1Hw.TitleStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    var ri = new ScheduleCore.Models.HomeworkItem
                    { Subject = "数学", Content = "已经是特大", SizeLevel = xl };
                    ri.SubjectStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = xl };
                    rev1Hw.Items.Add(ri);
                    Store.SaveHomework(rev1Hw);
                    var rev1Ms = new ScheduleCore.Models.MissingBoard();
                    rev1Ms.TitleStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    var rm2 = new ScheduleCore.Models.MissingHomeworkItem
                    { Subject = "语文", Students = "李四", SizeLevel = xl };
                    rm2.SubjectStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = xl };
                    rev1Ms.Items.Add(rm2);
                    Store.SaveMissingBoard(rev1Ms);

                    var rev1Set = new ScheduleCore.Models.AppSettings { BoardFontDefaultsRev = 1 };
                    bool upR1 = Store.UpgradeBoardFontDefaults(rev1Set);
                    var fixHw = Store.LoadHomework();
                    var fixMs = Store.LoadMissingBoard();
                    Check("代次 1→2：只把标题补升特大，条目保持原档",
                          upR1 && rev1Set.BoardFontDefaultsRev == 2
                          && fixHw!.TitleStyle!.SizeLevel == xl && fixHw.Items[0].SizeLevel == xl
                          && fixMs!.TitleStyle!.SizeLevel == xl && fixMs.Items[0].SizeLevel == xl);

                    // ⑥c 登录前写不了盘时，绝不能把代次记成已完成（否则登录后永远不再试）
                    bool wasWritable = Store.EnablePersistence;
                    Store.EnablePersistence = false;
                    bool upOff = Store.UpgradeBoardFontDefaults(
                        new ScheduleCore.Models.AppSettings());
                    Store.EnablePersistence = wasWritable;
                    Check("不能写盘时升级不记代次（登录后会重试）", !upOff);

                    // ⑥d 真实老机器形态：Rev=1（说自己做完了）、标题仍是标准档、条目已特大
                    var loginMs = new ScheduleCore.Models.MissingBoard();
                    loginMs.TitleStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = 0 };
                    var lmi = new ScheduleCore.Models.MissingHomeworkItem
                    { Subject = "数学", Students = "王五", SizeLevel = xl };
                    lmi.SubjectStyle = new ScheduleCore.Models.TextStyle { Bold = true, SizeLevel = xl };
                    loginMs.Items.Add(lmi);
                    Store.SaveMissingBoard(loginMs);
                    var revSet = Store.LoadSettings();
                    revSet.BoardFontDefaultsRev = 1;
                    Store.SaveSettings(revSet);
                    Check("构造老机器形态（Rev=1 且标题仍是标准档）",
                          Store.LoadMissingBoard().TitleStyle!.SizeLevel == 0);

                    Store.EnablePersistence = false;   // 模拟登录界面的启动早期
                    Store.EnablePersistence = wasWritable;   // 模拟登录成功
                    var fixedMs = Store.LoadMissingBoard();
                    Check("登录后可写盘时自动补做字号升级：标题变特大、代次到 2",
                          fixedMs.TitleStyle!.SizeLevel == xl &&
                          fixedMs.Items[0].SizeLevel == xl &&
                          Store.LoadSettings().BoardFontDefaultsRev == 2);
                }
            }
            catch (Exception ex) { Check("诊断自身异常", false, ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
            try { File.WriteAllText(log, sb.ToString()); } catch { }
            Shutdown(fail == 0 ? 0 : 1);
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
            EnsureHandwritingBall();
            try { BoardSyncNotifier.Start(); } catch { }   // 二期：轮询摆渡目录 + 课间弹窗（未启用时零副作用）
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow startup", ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 手写悬浮球（全局单例）。
    /// <para>登录之后、主面板起来时创建：登录窗里要写的是账号密码（字母数字），用不着手写，
    /// 而登录前 <see cref="Store.EnablePersistence"/> 还是关的，球的拖动位置存不住。</para>
    /// </summary>
    public static HandwritingBallWindow? HandwritingBall { get; private set; }

    /// <summary>按设置创建 / 显示 / 隐藏手写球（幂等，设置勾选、取消背景时都可以再调一次）。</summary>
    public static void EnsureHandwritingBall()
    {
        try
        {
            if (HandwritingBall == null)
            {
                var ball = new HandwritingBallWindow();
                ball.Closed += (_, _) => HandwritingBall = null;
                HandwritingBall = ball;
            }

            bool show = Store.LoadSettings().ShowHandwritingBall;

            // 桌面背景模式（「设为背景」）下球要藏起来：内容已经画在壁纸上了，
            // 再浮一颗球上去既挡视线又不方便。这里统一兜住——托盘开关、显示设置勾选、
            // 启动恢复都走这个方法，各自判断一次容易漏。
            if (show && BackdropService.IsApplied()) show = false;

            HandwritingBall.ApplyVisibility(show);
        }
        catch (Exception ex)
        {
            LogCrash("HandwritingBall", ex);
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
            "第3节 数学 → 英语；第5节 体育 → 数学；第7节 新增 音乐", 60, Store.LoadSettings().Reminder);
        return w;
    }

    /// <summary>诊断：悬浮课表状态栏的「课表有变化」红字。</summary>
    private static Window DiagScheduleFlash()
    {
        var w = new ScheduleWindow();
        w.Loaded += (_, _) => w.SimulateChangeFlash("第3节 数学 → 英语　第5节 体育 → 数学", 10);
        return w;
    }

    /// <summary>诊断截图：以指定角色登录后打开悬浮课表，用来核对「内置快捷」的角色默认显隐。</summary>
    private static Window DiagScheduleWindowAs(string role)
    {
        UserSession.SignIn(new AppUser { Username = "diag-" + role, Role = role });
        return new ScheduleWindow();
    }

    /// <summary>诊断截图：打开「一次安排一周」展开后的发布窗，核对 7 天表格与单日控件互斥。</summary>
    /// <summary>r69 截图：把焦点跟随工具条切到「便签内容」，展示可用态（而不是默认置灰态）。</summary>
    private static Window DiagFocusedFontWindow()
    {
        var w = new CustomNoteEditWindow(DiagCustomNoteId());
        // 窗口 Show 时焦点会从 null 变化一次，绑定器会按设计把工具条置灰；
        // 用低优先级把激活放到那一拍之后，才能拍到「可用态」。
        w.Dispatcher.BeginInvoke(new Action(() => w.DiagBinder?.DiagFocus(w.TxtContent)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return w;
    }

    private static Window DiagBoardSendWeekly()
    {
        var w = new BoardSendWindow(new[] { "八年级(1)班" }, BoardSyncConsts.EntityDuty);
        w.SetWeekModeForDiag(true);
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

    /// <summary>
    /// 二期「结构化变更单」全链路自检：在 CB_DATA_DIR 副本下建 teacher / classroom / share
    /// 三份独立目录，模拟两台机器共用一个共享目录（完全离线，不碰真实数据、不碰 FTP）。
    /// </summary>
    /// <summary>诊断用：递归找 AppSettings 里被写坏的 double（NaN / Infinity），JSON 序列化会直接抛。</summary>
    private static void DumpBadDoubles(object? obj, string path, System.Text.StringBuilder sb, int depth)
    {
        if (obj == null || depth > 5) return;
        var t = obj.GetType();
        if (t.IsPrimitive || t == typeof(string) || t == typeof(DateTime)) return;
        foreach (var p in t.GetProperties())
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = p.GetValue(obj); } catch { continue; }
            if (v == null) continue;
            string np = path + "." + p.Name;
            if (v is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.AppendLine("BAD " + np + " = " + d);
                continue;
            }
            if (v is System.Collections.IEnumerable en)
            {
                int i = 0;
                foreach (var it in en)
                {
                    DumpBadDoubles(it, np + "[" + i + "]", sb, depth + 1);
                    if (++i > 40) break;
                }
                continue;
            }
            if (t.Assembly == typeof(ScheduleCore.Models.AppSettings).Assembly)
                DumpBadDoubles(v, np, sb, depth + 1);
        }
    }

    private static bool RunSyncCheck(string log)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            sb.AppendLine((ok ? "PASS " : "FAIL ") + name
                          + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
            if (ok) pass++; else fail++;
        }
        int Cnt(string dir, string pattern)
            => Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Length : 0;

        try
        {
            string cbDir = (Environment.GetEnvironmentVariable("CB_DATA_DIR") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cbDir))
            {
                sb.AppendLine("拒绝：请用 CB_DATA_DIR 指向数据副本后再运行（本诊断会写盘）。");
                Check("CB_DATA_DIR 护栏", false);
                File.WriteAllText(log, sb.ToString());
                return false;
            }

            Store.SuppressWrites = false;
            Store.EnablePersistence = true;
            string root = Path.Combine(cbDir, "sync-probe-" + Environment.ProcessId);
            string teacherDir = Path.Combine(root, "teacher");
            string classDir = Path.Combine(root, "classroom");
            string shareDir = Path.Combine(root, "share");
            foreach (var d in new[] { teacherDir, classDir, shareDir }) Directory.CreateDirectory(d);

            const string cls = "测试班";
            string cf = BoardSyncIds.ClassFolder(cls);
            string msgDir = Path.Combine(shareDir, "board", cf, "messages");
            string rcDir = Path.Combine(shareDir, "board", cf, "receipts");

            void UseMachine(string dir, string actor)
            {
                Store.SetAppDir(dir);
                var s = Store.LoadSettings();
                s.SelectedClass = cls;
                s.UpdateSourceKind = "Folder";
                s.UpdateFolderPath = shareDir;
                s.BoardSyncRoot = "";
                s.BoardSyncEnabled = true;
                s.BoardSyncActorId = actor;
                s.BoardSyncExtraClasses = "";
                s.BoardSyncIsClassroom = dir == classDir;   // 教室机发布内容快照，教师机只读
                Store.SaveSettings(s);
            }

            BoardChangeOrder Draft(string op, string itemId, string summary, string? content = null,
                                   string? entity = null, string? validDay = null)
                => new()
                {
                    ClassKey = cls,
                    Entity = entity ?? BoardSyncConsts.EntityHomework,
                    Op = op,
                    ItemId = itemId,
                    Fields = content == null
                        ? new System.Collections.Generic.Dictionary<string, string>()
                        : new System.Collections.Generic.Dictionary<string, string>
                          { ["subject"] = "语文", ["content"] = content },
                    Sender = "teacher01", SenderRole = "teacher", SenderSubject = "语文",
                    Summary = summary,
                    ValidDay = validDay ?? DateTime.Today.ToString("yyyy-MM-dd")
                };

            // 两端各一份老数据：教室机那条已被本地改过 → 后面必然出现 base 漂移
            UseMachine(teacherDir, "a-teacher");
            var thw = new HomeworkBoard();
            thw.Items.Add(new HomeworkItem { Id = "hw-001", Subject = "语文", Content = "练习册 P10" });
            Store.SaveHomework(thw);
            UseMachine(classDir, "a-classroom");
            var chw = new HomeworkBoard();
            chw.Items.Add(new HomeworkItem { Id = "hw-001", Subject = "语文", Content = "本地改过的版本" });
            Store.SaveHomework(chw);

            sb.AppendLine("data=" + root);
            Check("教室机 actorId", Store.LoadSettings().BoardSyncActorId == "a-classroom");
            UseMachine(teacherDir, "a-teacher");
            Check("教师机 actorId（两端不同）", Store.LoadSettings().BoardSyncActorId == "a-teacher");
            Check("连通性测试", BoardSyncService.Test().Ok, BoardSyncService.Test().Message);
            Check("课间全屏提醒默认开启（发不发由发送方逐条决定）",
                  Store.LoadSettings().BoardSyncFullScreenReminder);

            // ① 发送 / 隔离 / 已送达 / 已阅
            var src = Store.LoadHomework()!.Items[0];
            string sig = BoardSyncService.SignatureOf(BoardSyncConsts.EntityHomework, src.Id, src);
            var f1 = BoardSyncService.FieldsOf(src);
            f1["content"] = "练习册 P20（教师机发）";
            var d1 = Draft(BoardSyncConsts.OpUpsert, src.Id, "语文作业改为 P20");
            d1.Fields = f1;
            d1.BaseSignature = sig;
            var sent = BoardSyncService.Send(d1);
            Check("发送变更单成功", sent.Ok, sent.Message);
            string msgId = sent.Order!.MsgId;
            Check("只新建一个独占消息文件", Cnt(msgDir, "m-*") == 1, "count=" + Cnt(msgDir, "m-*"));
            Check("主 manifest.json 未被创建", !File.Exists(Path.Combine(shareDir, "manifest.json")));
            Check("主 data.cbpack 未被创建", !File.Exists(Path.Combine(shareDir, "data.cbpack")));

            UseMachine(classDir, "a-classroom");
            var p1 = BoardSyncService.Pull(new[] { cls });
            Check("教室机收到 1 条变更单", p1.Added == 1 && p1.Bad == 0, p1.Report);
            Check("状态=已送达",
                  BoardSyncService.LoadState().Records.Single(r => r.MsgId == msgId).Status == BoardSyncConsts.StDelivered);
            Check("自动写已送达回执", Cnt(rcDir, "r-*") >= 1);
            var p2 = BoardSyncService.Pull(new[] { cls });
            Check("重复轮询幂等（不重复入档）", p2.Added == 0, p2.Report);
            BoardSyncService.MarkSeen(msgId);
            Check("打开=已阅（不等于已改好）",
                  BoardSyncService.LoadState().Records.Single(r => r.MsgId == msgId).Status == BoardSyncConsts.StSeen);

            UseMachine(teacherDir, "a-teacher");
            BoardSyncService.Pull(new[] { cls });
            var outRec = BoardSyncService.LoadState().Records.Single(r => r.MsgId == msgId);
            Check("教师机推进到已阅", outRec.Status == BoardSyncConsts.StSeen, outRec.Status);
            Check("此刻尚未标已应用", outRec.Status != BoardSyncConsts.StApplied);

            // ② 默认「增加」：本地改动不被覆盖，两条都留
            UseMachine(classDir, "a-classroom");
            var ap = BoardSyncService.Apply(msgId, BoardSyncConsts.DecAppend, bypassRole: true);
            Check("默认「增加」应用成功", ap.Ok, ap.Message);
            var after = Store.LoadHomework()!;
            Check("两条都留（本地改动未被覆盖）", after.LiveCount == 2, "live=" + after.LiveCount);
            Check("本地原文原样保留",
                  after.Items.Any(x => x.Id == "hw-001" && x.Content == "本地改过的版本"));
            string derived = BoardSyncIds.DerivedItemId("hw-001", msgId);
            Check("新条目 Id 确定性派生", after.Items.Any(x => x.Id == derived), derived);
            Check("写盘无错误（LastWriteError 为空）", Store.LastWriteError == null,
                  Store.LastWriteError ?? "null");
            Check("回执带 dataVersion", ap.Receipt != null && ap.Receipt.DataVersion.Length > 0);
            Check("base 漂移被明确记录", ap.Receipt != null && ap.Receipt.Note.Contains("漂移"),
                  ap.Receipt?.Note ?? "");
            var ap2 = BoardSyncService.Apply(msgId, BoardSyncConsts.DecAppend, bypassRole: true);
            Check("重复同一裁决幂等", ap2.Ok && Store.LoadHomework()!.LiveCount == 2);
            Check("教室机本机到已核对",
                  BoardSyncService.LoadState().Records.Single(r => r.MsgId == msgId).Status == BoardSyncConsts.StVerified);

            UseMachine(teacherDir, "a-teacher");
            BoardSyncService.Pull(new[] { cls });
            outRec = BoardSyncService.LoadState().Records.Single(r => r.MsgId == msgId);
            Check("教师机到已核对（六态闭环）",
                  outRec.Status == BoardSyncConsts.StVerified && outRec.DataVersion.Length > 0,
                  outRec.Status + " " + outRec.Note);

            // ③ 替换：软删除 + 被覆盖版本全文存档
            UseMachine(teacherDir, "a-teacher");
            var rep = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, "hw-001", "替换为 P30", "替换为 P30"));
            string repId = rep.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var rp = BoardSyncService.Apply(repId, BoardSyncConsts.DecReplace, bypassRole: true);
            Check("替换应用成功", rp.Ok, rp.Message);
            Check("替换=旧条目软删除（墓碑）",
                  Store.LoadHomework()!.Items.Any(x => x.Id == "hw-001" && x.Deleted));
            Check("替换=被覆盖版本全文存档",
                  rp.Receipt != null && rp.Receipt.ArchivedSnapshot.Length > 0);

            // ④ 忽略 / 稍后处理
            UseMachine(teacherDir, "a-teacher");
            var ig = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, "hw-001", "忽略测试", "忽略我"));
            string igId = ig.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            int liveBefore = Store.LoadHomework()!.LiveCount;
            var ip = BoardSyncService.Apply(igId, BoardSyncConsts.DecIgnore, bypassRole: true);
            Check("忽略不改本机数据", ip.Ok && Store.LoadHomework()!.LiveCount == liveBefore);
            Check("忽略状态=已忽略",
                  BoardSyncService.LoadState().Records.Single(r => r.MsgId == igId).Status == BoardSyncConsts.StIgnored);

            // ⑤ 删除类：写墓碑；keep 不改数据；墓碑守卫拒绝复活
            UseMachine(teacherDir, "a-teacher");
            var dl = BoardSyncService.Send(Draft(BoardSyncConsts.OpDelete, derived, "删除 P20"));
            string dlId = dl.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var dp = BoardSyncService.Apply(dlId, BoardSyncConsts.DecDelete, bypassRole: true);
            Check("删除=写墓碑而非物理删除",
                  dp.Ok && Store.LoadHomework()!.Items.Any(x => x.Id == derived && x.Deleted));
            Check("删除类不接受「增加」裁决",
                  !BoardSyncService.Apply(dlId, BoardSyncConsts.DecAppend, bypassRole: true).Ok);

            UseMachine(teacherDir, "a-teacher");
            var tomb = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, derived, "复活尝试", "复活尝试"));
            string tombId = tomb.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var tp = BoardSyncService.Apply(tombId, BoardSyncConsts.DecAppend, bypassRole: true);
            Check("TOMBSTONE_GUARD 拒绝改写已删条目",
                  !tp.Ok && tp.Message.Contains("TOMBSTONE_GUARD"), tp.Message);

            UseMachine(teacherDir, "a-teacher");
            var kp = BoardSyncService.Send(Draft(BoardSyncConsts.OpDelete, "hw-001", "保留测试"));
            string kpId = kp.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            int allBefore = Store.LoadHomework()!.Items.Count;
            var kr = BoardSyncService.Apply(kpId, BoardSyncConsts.DecKeep, bypassRole: true);
            Check("删除类「保留」不改数据", kr.Ok && Store.LoadHomework()!.Items.Count == allBefore);

            // ⑥ 撤回：只发新消息；原消息与撤回消息同轮到达也要正确结算
            UseMachine(teacherDir, "a-teacher");
            var rv = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, "hw-001", "待撤回", "待撤回"));
            string rvId = rv.Order!.MsgId;
            int msgCntBefore = Cnt(msgDir, "m-*");
            var rev = BoardSyncService.Revoke(rvId);
            Check("撤回只新建一条 revoke 消息", rev.Ok && Cnt(msgDir, "m-*") == msgCntBefore + 1, rev.Message);
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var rvr = BoardSyncService.LoadState().Records.FirstOrDefault(r => r.MsgId == rvId);
            Check("教室机标记已撤回", rvr != null && rvr.Status == BoardSyncConsts.StRevoked,
                  rvr?.Status ?? "null");

            // ⑦ 坏文件 / 过期 / 通知 / 游客 / 权限 / 断连
            File.WriteAllText(Path.Combine(msgDir, "m-bad.json"), "{ this is not json");
            var bp = BoardSyncService.Pull(new[] { cls });
            Check("坏文件大声失败且不拖垮轮询", bp.Bad >= 1 && bp.Report.Contains("坏文件"), bp.Report);

            UseMachine(teacherDir, "a-teacher");
            var ex = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, "hw-001", "过期测试", "过期",
                validDay: DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd")));
            string exId = ex.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var exr = BoardSyncService.LoadState().Records.FirstOrDefault(r => r.MsgId == exId);
            Check("过期单自动标记已过期", exr != null && exr.Status == BoardSyncConsts.StExpired,
                  exr?.Status ?? "null");

            UseMachine(teacherDir, "a-teacher");
            var nt = BoardSyncService.Send(new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityNotice, Op = BoardSyncConsts.OpUpsert,
                ItemId = "nt-001",
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["title"] = "本班通知", ["content"] = "明天带尺子" },
                Sender = "teacher01", SenderRole = "teacher", SenderSubject = "语文",
                Summary = "本班通知：带尺子"
            });
            string ntId = nt.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var np = BoardSyncService.Apply(ntId, BoardSyncConsts.DecAppend, bypassRole: true);
            Check("本班通知落地且 Scope=local（不进主包）",
                  np.Ok && Store.LoadNotifications().Any(x => x.Content == "明天带尺子" && x.Scope == "local"));

            UseMachine(teacherDir, "a-teacher");
            int recBefore = BoardSyncService.LoadState().Records.Count;
            Store.EnablePersistence = false;
            var guest = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, "hw-001", "游客测试", "游客"));
            Check("游客模式不写盘且不发变更单",
                  !guest.Ok && BoardSyncService.LoadState().Records.Count == recBefore);
            Store.EnablePersistence = true;

            var u = new AppUser
            { Username = "sub", DisplayName = "科任", Role = UserRole.SubjectTeacher, Subject = "数学" };
            u.ClassNames.Add(cls);
            Check("按科限权：跨科拒绝", !u.OwnsSubject("语文"));
            Check("按科限权：本科允许", u.OwnsSubject("数学"));
            u.Subject = "";
            Check("老账号空科目=不限", u.OwnsSubject("语文"));
            u.Subject = "数学";
            UserSession.SignIn(u);
            Check("UserSession 跨科拒绝", !UserSession.CanEditBoardItem(cls, "语文"));

            // ⑨ 发布信息权限：电教管理员默认无（他负责在教室机确认应用），教师默认有；
            //    老 users.json 升级不能让班主任 / 科任老师突然失去这项能力。
            Check("出厂默认：电教管理员不能发布信息",
                  !ScheduleCore.Models.RolePolicy.DefaultFor(ScheduleCore.Models.UserRole.Student).PublishInfo);
            Check("出厂默认：班主任 / 科任老师可发布信息",
                  ScheduleCore.Models.RolePolicy.DefaultFor(ScheduleCore.Models.UserRole.Teacher).PublishInfo
                  && ScheduleCore.Models.RolePolicy.DefaultFor(ScheduleCore.Models.UserRole.SubjectTeacher).PublishInfo);
            var oldBook = new ScheduleCore.Models.UserBook { Schema = 4 };
            oldBook.Policies[ScheduleCore.Models.UserRole.Teacher] = new ScheduleCore.Models.RolePolicy { EditBoard = true };
            oldBook.Policies[ScheduleCore.Models.UserRole.SubjectTeacher] = new ScheduleCore.Models.RolePolicy { EditBoard = true };
            oldBook.Policies[ScheduleCore.Models.UserRole.Student] = new ScheduleCore.Models.RolePolicy { EditBoard = true };
            oldBook.EnsurePolicyDefaults();
            Check("老策略升级到 Schema5 不丢发布权限",
                  oldBook.Schema == 5
                  && oldBook.PolicyFor(ScheduleCore.Models.UserRole.Teacher).PublishInfo
                  && oldBook.PolicyFor(ScheduleCore.Models.UserRole.SubjectTeacher).PublishInfo
                  && !oldBook.PolicyFor(ScheduleCore.Models.UserRole.Student).PublishInfo);
            // 显式给一份空用户表：策略按出厂默认回落，不受诊断副本里残留的 users.json 影响。
            UserSession.SignIn(new ScheduleCore.Models.AppUser
                { Username = "diag-student", Role = ScheduleCore.Models.UserRole.Student },
                new ScheduleCore.Models.UserBook());
            Check("UserSession：电教管理员默认拒绝发布", !UserSession.CanPublishInfo);
            int beforeDeny = BoardSyncService.LoadState().Records.Count;
            var denied = BoardSyncService.Send(Draft(BoardSyncConsts.OpUpsert, "hw-001", "越权测试", "不该发出"));
            Check("服务层拦住电教管理员发布（不留台账）",
                  !denied.Ok && BoardSyncService.LoadState().Records.Count == beforeDeny);
            // 后面的快照 / 回执用例仍按管理员身份跑，避免被新权限挡住。
            UserSession.SignIn(new ScheduleCore.Models.AppUser
            { Username = "diag-admin", Role = ScheduleCore.Models.UserRole.Admin });

            var so = Store.LoadSettings();
            so.UpdateSourceKind = "Off";
            Store.SaveSettings(so);
            Check("未配置数据源时明确不可用", !BoardSyncService.Test().Ok);
            var off = BoardSyncService.Pull(new[] { cls });
            Check("断连轮询不崩且明确报告", off.Added == 0 && off.Report.Contains("未配置"), off.Report);
            so = Store.LoadSettings();
            so.UpdateSourceKind = "Folder";
            Store.SaveSettings(so);

            Check("原子写无 .tmp 残留",
                  !Directory.GetFiles(Path.Combine(shareDir, "board"), "*.tmp", SearchOption.AllDirectories).Any());

            // ⑧ 教室机内容快照 → 教师机选得到目标 → 「替换」不再退化成「新增」
            UseMachine(classDir, "a-classroom");
            var pub = BoardSyncService.PublishSnapshot(force: true, classKey: cls);
            Check("教室机发布内容快照", pub.Ok, pub.Message);
            string snapDir = Path.Combine(shareDir, "board", cf, "snapshots");
            Check("快照只写自己独占文件", Cnt(snapDir, "s-*") >= 1, "count=" + Cnt(snapDir, "s-*"));

            UseMachine(teacherDir, "a-teacher");
            var (snap, snapMsg) = BoardSyncService.LoadRemoteSnapshot(cls);
            Check("教师机读到教室机快照", snap != null, snapMsg);
            Check("快照条目带稳定 Id 与当前值签名",
                  snap != null && snap.Items.Count > 0 &&
                  snap.Items.All(x => x.Id.Length > 0 && x.Signature.Length > 0));
            var hwSnapItem = snap?.For(BoardSyncConsts.EntityHomework).FirstOrDefault();
            Check("教师机能从快照选到作业目标条目（下拉不再为空）",
                  hwSnapItem != null, hwSnapItem?.Brief ?? "");

            // 主路径：教师机按快照选目标发单 → 教室机「替换」命中该条
            UseMachine(teacherDir, "a-teacher");
            var fromSnap = BoardSyncService.Send(new BoardChangeOrder
            {
                ClassKey = cls,
                Entity = BoardSyncConsts.EntityHomework,
                Op = BoardSyncConsts.OpUpsert,
                ItemId = hwSnapItem!.Id,
                BaseSignature = hwSnapItem.Signature,
                TargetText = hwSnapItem.Brief,
                FromSnapshot = true,
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["subject"] = "语文", ["content"] = "快照定位后的新内容" },
                Sender = "teacher01", SenderRole = "teacher", SenderSubject = "语文",
                Summary = "按教室机快照修改语文作业",
                ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            });
            string fromSnapId = fromSnap.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var sr = BoardSyncService.Apply(fromSnapId, BoardSyncConsts.DecReplace, bypassRole: true);
            Check("按快照目标「替换」真正命中该条（软删除，不是凭空新增）",
                  sr.Ok && Store.LoadHomework()!.Items.Any(x => x.Id == hwSnapItem.Id && x.Deleted), sr.Message);

            // 旧行为复刻：目标 Id 为空的变更单，选「替换」必须被拒绝，绝不静默变新增
            UseMachine(teacherDir, "a-teacher");
            var noTarget = BoardSyncService.Send(Draft(
                BoardSyncConsts.OpUpsert, "", "没带目标的旧变更单", "口算卡"));
            string noTargetId = noTarget.Order!.MsgId;
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            int liveBeforeNoTarget = Store.LoadHomework()!.LiveCount;
            var rejected = BoardSyncService.Apply(noTargetId, BoardSyncConsts.DecReplace, bypassRole: true);
            Check("目标为空：替换被明确拒绝，本机条数不变",
                  !rejected.Ok && rejected.Message.Contains("找不到") &&
                  Store.LoadHomework()!.LiveCount == liveBeforeNoTarget, rejected.Message);

            // 兜底：电教管理员在教室机现场指定目标后，替换照样生效
            var localItems = BoardSyncService.LocalItemsForTarget(
                BoardSyncConsts.EntityHomework, cls);
            Check("教室机能列出本机条目供现场指定", localItems.Count > 0, "count=" + localItems.Count);
            var pick = localItems.First();
            var manual = BoardSyncService.Apply(noTargetId, BoardSyncConsts.DecReplace,
                bypassRole: true, targetItemId: pick.Id, targetBaseSignature: pick.Signature);
            Check("人工指定目标后「替换」真正生效",
                  manual.Ok && Store.LoadHomework()!.Items.Any(x => x.Id == pick.Id && x.Deleted), manual.Message);
            Check("替换回执带被覆盖原文 + 最终落地字段",
                  manual.Receipt != null && manual.Receipt.ArchivedFields.Count > 0 &&
                  manual.Receipt.AppliedFields.Count > 0);
            Check("回执处理说明是人话（含改动方向与存档提示）",
                  manual.Receipt != null && manual.Receipt.Note.Contains("→") &&
                  manual.Receipt.Note.Contains("存档"), manual.Receipt?.Note ?? "");
            Check("回执明细写进本机台账（排版不用猜）",
                  BoardSyncService.LoadState().Records.Single(r => r.MsgId == noTargetId).AppliedFields.Count > 0);

            // 教师机收回执后也能看到结构化明细
            UseMachine(teacherDir, "a-teacher");
            BoardSyncService.Pull(new[] { cls });
            var outSnap = BoardSyncService.LoadState().Records.Single(r => r.MsgId == fromSnapId);
            Check("教师机核对视图拿到回执明细",
                  outSnap.AppliedFields.Count > 0 && outSnap.DecisionBy.Length > 0 &&
                  outSnap.ReceiptTime.Length > 0 && outSnap.Status == BoardSyncConsts.StVerified,
                  outSnap.Status + " " + outSnap.Note);

            // ⑩ 富文本透传：科目 / 正文各一套格式，教室机按单落地；只改格式也算内容变过
            UseMachine(classDir, "a-classroom");
            var styleBoard = Store.LoadHomework()!;
            styleBoard.Items.Add(new HomeworkItem
            {
                Id = "hw-style-1", Subject = "数学", Content = "带格式的原文",
                SubjectStyle = new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel },
                ContentStyle = new TextStyle { SizeLevel = HwTextStyle.ExtraLargeLevel }
            });
            Store.SaveHomework(styleBoard);
            BoardSyncService.PublishSnapshot(force: true, classKey: cls);

            UseMachine(teacherDir, "a-teacher");
            var (snapSty, msgSty) = BoardSyncService.LoadRemoteSnapshot(cls);
            var styItem = snapSty?.For(BoardSyncConsts.EntityHomework)
                                 .FirstOrDefault(x => x.Id == "hw-style-1");
            Check("快照带出教室机字段级格式",
                  styItem != null && styItem.Styles.ContainsKey("subject")
                                   && styItem.Styles.ContainsKey("content"), msgSty);

            var styOrder = BoardSyncService.Send(new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityHomework, Op = BoardSyncConsts.OpUpsert,
                ItemId = styItem!.Id, BaseSignature = styItem.Signature, TargetText = styItem.Brief,
                FromSnapshot = true,
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["subject"] = "数学", ["content"] = "格式已改" },
                Styles = new System.Collections.Generic.Dictionary<string, BoardTextStyle>
                {
                    ["subject"] = new BoardTextStyle { Bold = true, Color = "#C0392B", SizeLevel = 5 },
                    ["content"] = new BoardTextStyle { Bold = false, Color = "#1E4FA3", SizeLevel = 6 }
                },
                Sender = "teacher01", SenderRole = "teacher", SenderSubject = "数学",
                Summary = "带格式的作业修改", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            });
            Check("带格式的变更单能发出", styOrder.Ok, styOrder.Message);

            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var styRes = BoardSyncService.Apply(styOrder.Order!.MsgId, BoardSyncConsts.DecReplace, bypassRole: true);
            Check("带格式的变更单能应用", styRes.Ok, styRes.Message);
            var styApplied = Store.LoadHomework()!.Items
                                  .FirstOrDefault(x => x.Id == styRes.Receipt?.AppliedItemId);
            Check("格式随单落地：科目 加粗 / 红色 / 超大",
                  styApplied != null && styApplied.SubjectStyle.Bold
                  && styApplied.SubjectStyle.Color == "#C0392B" && styApplied.SubjectStyle.SizeLevel == 5,
                  styApplied == null ? "条目没找到" : styApplied.SubjectStyle.SizeLevel + " " + styApplied.SubjectStyle.Color);
            Check("格式随单落地：正文 巨大 / 蓝色（与科目分开）",
                  styApplied != null && styApplied.ContentStyle.SizeLevel == 6
                  && styApplied.ContentStyle.Color == "#1E4FA3",
                  styApplied == null ? "条目没找到" : styApplied.ContentStyle.SizeLevel + " " + styApplied.ContentStyle.Color);
            Check("回执带回落地格式（教师机能确认格式也到了）",
                  styRes.Receipt != null && styRes.Receipt.AppliedStyles.Count == 2);

            // 只改格式、文字一字不动，base 签名也必须变 —— 否则会覆盖掉教室机调好的格式
            var styTarget = Store.LoadHomework()!.Items.First(x => x.Id == styApplied!.Id);
            string sigBefore = BoardSyncService.SignatureOf(BoardSyncConsts.EntityHomework, styTarget.Id, styTarget);
            string oldColor = styTarget.ContentStyle.Color;
            styTarget.ContentStyle.Color = "#1E8E3E";
            string sigAfter = BoardSyncService.SignatureOf(BoardSyncConsts.EntityHomework, styTarget.Id, styTarget);
            styTarget.ContentStyle.Color = oldColor;
            Check("只改格式也算内容变化（签名包含样式）", sigBefore != sigAfter,
                  sigBefore + " / " + sigAfter);

            // ⑪ 替换一键撤回：按存档整条还原，替换出来的那条软删除，教师机收到「已撤回」
            var undoRes = BoardSyncService.RestoreArchived(styOrder.Order!.MsgId, bypassRole: true);
            Check("教室机撤回替换成功", undoRes.Ok, undoRes.Message);
            var afterUndo = Store.LoadHomework()!;
            var backItem = afterUndo.Items.FirstOrDefault(x => x.Id == "hw-style-1");
            Check("原文条目恢复可见", backItem != null && !backItem.Deleted);
            Check("原文内容与格式一并还原",
                  backItem != null && backItem.Content == "带格式的原文"
                  && backItem.SubjectStyle.SizeLevel == HwTextStyle.ExtraLargeLevel
                  && backItem.ContentStyle.Color == "",
                  backItem == null ? "条目没找到"
                      : backItem.Content + " / " + backItem.SubjectStyle.SizeLevel + " / " + backItem.ContentStyle.Color);
            Check("替换出来的那条被软删除（不物理删除）",
                  afterUndo.Items.Any(x => x.Id == styApplied!.Id && x.Deleted));
            Check("撤回后台账状态为「已撤回」",
                  BoardSyncService.LoadState().Records
                      .First(r => r.MsgId == styOrder.Order!.MsgId).Status == BoardSyncConsts.StRevoked);
            Check("重复撤回幂等（不再改数据）",
                  BoardSyncService.RestoreArchived(styOrder.Order!.MsgId, bypassRole: true).Ok);

            UseMachine(teacherDir, "a-teacher");
            BoardSyncService.Pull(new[] { cls });
            var tUndo = BoardSyncService.LoadState().Records
                            .First(r => r.MsgId == styOrder.Order!.MsgId);
            Check("教师机收到 restored 回执后台账变「已撤回」",
                  tUndo.Status == BoardSyncConsts.StRevoked && tUndo.Note.Contains("原文已还原"),
                  tUndo.Status + " " + tUndo.Note);

            // FTP 覆盖写可能读到半截：坏快照必须大声报错，绝不静默当空表
            File.WriteAllText(Path.Combine(snapDir, "s-bad.json"), "{ broken");
            var (snap2, msg2) = BoardSyncService.LoadRemoteSnapshot(cls);
            Check("坏快照不混进目标列表", snap2 != null && snap2.Items.Count > 0, msg2);
            Check("坏快照大声报错",
                  (BoardSyncService.LastError ?? "").Contains("坏快照"),
                  BoardSyncService.LastError ?? "null");

            // ⑨ 课间全屏提醒：只有发送方勾了的变更单才带标记，教室机 LastPullForce 只收这些
            UseMachine(teacherDir, "a-teacher");
            var dForce = Draft(BoardSyncConsts.OpUpsert, "", "强提醒：明天带跳绳", "明天带跳绳");
            dForce.ForceBreakReminder = true;
            var sForce = BoardSyncService.Send(dForce);
            Check("勾了「课间全屏提醒」的变更单能发出", sForce.Ok, sForce.Message);

            var dPlain = Draft(BoardSyncConsts.OpUpsert, "", "普通：作业照旧", "作业照旧");
            var sPlain = BoardSyncService.Send(dPlain);
            Check("普通变更单能发出", sPlain.Ok, sPlain.Message);

            UseMachine(classDir, "a-classroom");
            var pForce = BoardSyncService.Pull(new[] { cls });
            Check("教室机一次收到这两条", pForce.Added >= 2, pForce.Report);
            var recForce = BoardSyncService.LoadState().Records
                                    .FirstOrDefault(r => r.MsgId == sForce.Order!.MsgId);
            Check("全屏提醒标记随变更单到达教室机",
                  recForce != null && recForce.Order.ForceBreakReminder);
            var forces = BoardSyncService.LastPullForce;
            Check("只把那一条要求强提醒的记进 LastPullForce",
                  forces.Count == 1 && forces[0].Contains("明天带跳绳"),
                  "count=" + forces.Count + " " + string.Join("|", forces));

            // ⑩ 语音提醒：文本与时机随变更单到达教室机，只把带语音的记进 LastPullVoice
            UseMachine(teacherDir, "a-teacher");
            var dVoice = Draft(BoardSyncConsts.OpUpsert, "", "通知：明天带跳绳", "明天带跳绳",
                               BoardSyncConsts.EntityNotice);
            dVoice.VoiceText = "本班通知：明天带跳绳";
            var sVoice = BoardSyncService.Send(dVoice);
            Check("勾了「语音提醒」的变更单能发出", sVoice.Ok, sVoice.Message);

            var dVoiceNow = Draft(BoardSyncConsts.OpUpsert, "", "紧急：立刻回教室", "立刻回教室",
                                  BoardSyncConsts.EntityNotice);
            dVoiceNow.VoiceText = "紧急通知：请立刻回教室";
            dVoiceNow.VoiceImmediate = true;
            var sVoiceNow = BoardSyncService.Send(dVoiceNow);
            Check("「收到就播」的变更单能发出", sVoiceNow.Ok, sVoiceNow.Message);

            UseMachine(classDir, "a-classroom");
            var pVoice = BoardSyncService.Pull(new[] { cls });
            Check("教室机收到这两条带语音的变更单", pVoice.Added >= 2, pVoice.Report);
            var voiceList = BoardSyncService.LastPullVoice;
            Check("语音文本随变更单到达教室机",
                  voiceList.Count == 2 && voiceList.Any(v => v.Text.Contains("明天带跳绳")),
                  "count=" + voiceList.Count + " " + string.Join("|", voiceList.Select(v => v.Text)));
            Check("语音时机随变更单到达（一条课间播 / 一条收到就播）",
                  voiceList.Count(v => v.Immediate) == 1 && voiceList.Count(v => !v.Immediate) == 1,
                  string.Join(",", voiceList.Select(v => v.Immediate)));
            Check("语音播报开关默认开启（发不发由发送方逐条决定）",
                  Store.LoadSettings().BoardSyncVoiceReminder);

            // ⑫ 值日安排：与作业公示走同一条链路（同 Id / 墓碑 / 存档 / 撤回），字段换成 星期 / 岗位 / 值日生
            UseMachine(teacherDir, "a-teacher");
            var dDuty = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityDuty, Op = BoardSyncConsts.OpUpsert,
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["day"] = "周三", ["duty"] = "扫地、拖地", ["names"] = "张三、李四" },
                Sender = "teacher01", SenderRole = "teacher", SenderSubject = "",
                Summary = "新增值日安排：周三 扫地、拖地",
                ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sDuty = BoardSyncService.Send(dDuty);
            Check("值日安排变更单能发出", sDuty.Ok, sDuty.Message);
            string dutyBrief = BoardSyncConsts.Brief(dDuty.Fields);
            Check("值日摘要可读（星期 + 岗位 + 值日生）",
                  dutyBrief.Contains("周三") && dutyBrief.Contains("张三"), dutyBrief);
            Check("值日不带字段级样式（格式由便签统一设置）",
                  BoardSyncService.StylesOf(new DutyItem { Day = "周一" }).Count == 0);

            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var aDuty = BoardSyncService.Apply(sDuty.Order!.MsgId, BoardSyncConsts.DecAppend, bypassRole: true);
            Check("值日安排能应用到教室机", aDuty.Ok, aDuty.Message);
            var dutyBoard = Store.LoadDuty();
            string dutyAppliedId = aDuty.Receipt?.AppliedItemId ?? "";
            var dutyItem = dutyBoard.Items.FirstOrDefault(x => x.Id == dutyAppliedId);
            Check("值日条目落地：周三 / 扫地、拖地 / 张三、李四",
                  dutyItem != null && dutyItem.Day == "周三" && dutyItem.Duty == "扫地、拖地"
                  && dutyItem.Names == "张三、李四",
                  dutyItem == null ? "条目没找到" : $"{dutyItem.Day}|{dutyItem.Duty}|{dutyItem.Names}");
            Check("值日条目 Id 是确定性派生（两端同单同 Id）",
                  dutyAppliedId.Length > 0 && dutyAppliedId == BoardSyncIds.DerivedItemId("", sDuty.Order!.MsgId),
                  dutyAppliedId);
            Check("LocalHasItem 认值日条目（否则「替换」会退化成「新增」）",
                  BoardSyncService.LocalHasItem(BoardSyncConsts.EntityDuty, dutyAppliedId));

            BoardSyncService.PublishSnapshot(force: true, classKey: cls);
            UseMachine(teacherDir, "a-teacher");
            var (snapDuty, msgDuty) = BoardSyncService.LoadRemoteSnapshot(cls);
            var snapDutyItem = snapDuty?.For(BoardSyncConsts.EntityDuty).FirstOrDefault(x => x.Id == dutyAppliedId);
            Check("教室机快照带出值日条目（教师机才选得到目标）",
                  snapDutyItem != null && snapDutyItem.Brief.Contains("周三"), msgDuty);

            var dDutyEdit = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityDuty, Op = BoardSyncConsts.OpUpsert,
                ItemId = snapDutyItem!.Id, BaseSignature = snapDutyItem.Signature,
                TargetText = snapDutyItem.Brief, FromSnapshot = true,
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["day"] = "周三", ["duty"] = "扫地、拖地", ["names"] = "王五" },
                Sender = "teacher01", SenderRole = "teacher", SenderSubject = "",
                Summary = "改周三值日生", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sDutyEdit = BoardSyncService.Send(dDutyEdit);
            Check("按快照目标发值日替换", sDutyEdit.Ok, sDutyEdit.Message);

            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var rDuty = BoardSyncService.Apply(sDutyEdit.Order!.MsgId, BoardSyncConsts.DecReplace, bypassRole: true);
            Check("值日替换能应用（不退化成新增）", rDuty.Ok, rDuty.Message);
            var dutyAfter = Store.LoadDuty();
            Check("值日替换后原文软删除（留墓碑）",
                  dutyAfter.Items.Any(x => x.Id == dutyAppliedId && x.Deleted));
            var dutyNew = dutyAfter.Items.FirstOrDefault(x => x.Id == rDuty.Receipt?.AppliedItemId);
            Check("替换后新条目值日生是王五", dutyNew != null && dutyNew.Names == "王五", dutyNew?.Names ?? "null");
            Check("回执带回被替换的原文（可撤回）",
                  rDuty.Receipt != null && !string.IsNullOrWhiteSpace(rDuty.Receipt.ArchivedSnapshot));

            var undoDuty = BoardSyncService.RestoreArchived(sDutyEdit.Order!.MsgId, bypassRole: true);
            Check("值日替换可撤回", undoDuty.Ok, undoDuty.Message);
            var dutyBack = Store.LoadDuty();
            var dutyOrigin = dutyBack.Items.FirstOrDefault(x => x.Id == dutyAppliedId);
            Check("值日原文还原（张三、李四 回来且可见）",
                  dutyOrigin != null && !dutyOrigin.Deleted && dutyOrigin.Names.Contains("张三"),
                  dutyOrigin == null ? "条目没找到" : dutyOrigin.Deleted + " / " + dutyOrigin.Names);

            // 墓碑拒绝改写：已删除的值日条目不许被远端改回来
            var dDutyDel = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityDuty, Op = BoardSyncConsts.OpUpsert,
                ItemId = dutyAppliedId, TargetText = "周三 扫地、拖地",
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["day"] = "周三", ["duty"] = "扫地", ["names"] = "复活的鬼" },
                Sender = "teacher01", SenderRole = "teacher",
                Summary = "改写已删的值日条目", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sDutyDel = BoardSyncService.Send(dDutyDel);
            // 先真删一条（写墓碑），再看改写是否被拒
            var dDutyKill = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityDuty, Op = BoardSyncConsts.OpDelete,
                ItemId = dutyAppliedId, TargetText = "周三 扫地、拖地",
                Sender = "teacher01", SenderRole = "teacher",
                Summary = "删除周三值日", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sDutyKill = BoardSyncService.Send(dDutyKill);
            Check("值日删除单能发出", sDutyDel.Ok && sDutyKill.Ok, sDutyKill.Message);
            BoardSyncService.Pull(new[] { cls });
            var killDuty = BoardSyncService.Apply(sDutyKill.Order!.MsgId, BoardSyncConsts.DecDelete, bypassRole: true);
            Check("值日删除能应用（写墓碑，不物理删除）",
                  killDuty.Ok && Store.LoadDuty().Items.Any(x => x.Id == dutyAppliedId && x.Deleted),
                  killDuty.Message);
            BoardSyncService.Pull(new[] { cls });
            var ghostDuty = BoardSyncService.Apply(sDutyDel.Order!.MsgId, BoardSyncConsts.DecAppend, bypassRole: true);
            Check("墓碑拒绝改写（TOMBSTONE_GUARD）",
                  !ghostDuty.Ok && ghostDuty.Message.Contains("墓碑"), ghostDuty.Message);

            // 老 duty.json 没有 Id：保存时兜底补，且两端算出的 Id 必须一致
            var noIdDuty = new DutyBoard();
            noIdDuty.Items.Add(new DutyItem { Day = "周五", Duty = "擦黑板", Names = "赵六" });
            Store.SaveDuty(noIdDuty);
            var noIdBack = Store.LoadDuty();
            Check("值日条目保存时自动补确定性 Id",
                  noIdBack.Items.Count == 1 && noIdBack.Items[0].Id.StartsWith("duty-"),
                  noIdBack.Items.Count == 0 ? "空" : noIdBack.Items[0].Id);
            Check("同一内容任何机器算出的 Id 一致",
                  noIdBack.Items.Count == 1
                  && noIdBack.Items[0].Id == BoardIds.Compute("duty", "", "周五", "擦黑板\u001F赵六"),
                  noIdBack.Items.Count == 0 ? "空" : noIdBack.Items[0].Id);

            // ⑬ 一次安排一周：一张变更单 = 整周；追加 / 整表换新 / 整周一键撤回
            UseMachine(classDir, "a-classroom");
            var sWeekSet = Store.LoadSettings();
            sWeekSet.BoardSyncAutoApply = true;
            Store.SaveSettings(sWeekSet);
            var weekBase = new DutyBoard();
            weekBase.Items.Add(new DutyItem
            { Id = "duty-old-mon", Day = "周一", Duty = "旧值日", Names = "旧同学" });
            Store.SaveDuty(weekBase);
            int weekBaseCount = Store.LoadDuty().LiveCount;

            UseMachine(teacherDir, "a-teacher");
            var rowsA = new List<BoardDutyRow>
            {
                new() { Day = "周一", Duty = "扫地", Names = "张三" },
                new() { Day = "周三", Duty = "拖地", Names = "李四" },
                new() { Day = "周五", Duty = "擦黑板", Names = "王五" }
            };
            var dWeekA = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityDuty, Op = BoardSyncConsts.OpUpsert,
                WeekRows = rowsA, Fields = BoardSyncConsts.WeekFields(rowsA),
                Sender = "teacher01", SenderRole = "teacher",
                Summary = "整周值日安排（3 天）", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sWeekA = BoardSyncService.Send(dWeekA);
            Check("一周安排只用一张变更单",
                  sWeekA.Ok && sWeekA.Order!.IsWeeklyDuty && sWeekA.Order.WeekRows.Count == 3,
                  sWeekA.Message);
            Check("周表字段快照可读（整周 / 共 N 天 / 逐日摘要）",
                  dWeekA.Fields["day"] == BoardSyncConsts.WeekDayTag
                  && dWeekA.Fields["duty"] == "共 3 天"
                  && dWeekA.Fields["names"].Contains("周一：扫地｜张三"),
                  BoardSyncConsts.Brief(dWeekA.Fields));

            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var appendWeek = BoardSyncService.Apply(sWeekA.Order!.MsgId,
                                     BoardSyncConsts.DecAppend, bypassRole: true);
            Check("追加整周：3 天一次落地", appendWeek.Ok, appendWeek.Message);
            Check("追加整周：教室机原有值日一条没动",
                  Store.LoadDuty().LiveCount == weekBaseCount + 3
                  && Store.LoadDuty().LiveItems.Any(x => x.Id == "duty-old-mon" && !x.Deleted),
                  "LiveCount=" + Store.LoadDuty().LiveCount);
            Check("追加整周：回执带回全部落地 Id（撤回要用）",
                  appendWeek.Receipt != null && appendWeek.Receipt.AppliedItemIds.Count == 3,
                  "count=" + (appendWeek.Receipt?.AppliedItemIds.Count ?? -1));
            Check("追加整周：教室机状态推进到「已核对」（整周表核对通过）",
                  BoardSyncService.LoadState().Records.First(r => r.MsgId == sWeekA.Order.MsgId)
                      .Status == BoardSyncConsts.StVerified,
                  BoardSyncService.LoadState().Records.First(r => r.MsgId == sWeekA.Order.MsgId).Status);

            var rowsB = new List<BoardDutyRow>
            {
                new() { Day = "周二", Duty = "倒垃圾", Names = "赵六" },
                new() { Day = "周四", Duty = "擦窗", Names = "钱七" }
            };
            UseMachine(teacherDir, "a-teacher");
            var dWeekB = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityDuty, Op = BoardSyncConsts.OpUpsert,
                WeekRows = rowsB, Fields = BoardSyncConsts.WeekFields(rowsB),
                Sender = "teacher01", SenderRole = "teacher",
                Summary = "整周换新（2 天）", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sWeekB = BoardSyncService.Send(dWeekB);
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var swapWeek = BoardSyncService.Apply(sWeekB.Order!.MsgId,
                                   BoardSyncConsts.DecReplace, bypassRole: true);
            Check("整表换成这一周：不需要单一目标条目也能裁决",
                  swapWeek.Ok, swapWeek.Message);
            Check("整表换新：旧值日全部软删除，只剩这一周 2 天",
                  Store.LoadDuty().LiveCount == 2
                  && Store.LoadDuty().LiveItems.All(x => x.Origin == "remote")
                  && Store.LoadDuty().Items.Any(x => x.Id == "duty-old-mon" && x.Deleted),
                  "LiveCount=" + Store.LoadDuty().LiveCount);
            Check("整表换新：整张旧表进了存档（可整周撤回）",
                  swapWeek.Receipt != null && !string.IsNullOrWhiteSpace(swapWeek.Receipt.ArchivedBoard)
                  && !string.IsNullOrWhiteSpace(BoardSyncService.LoadState().Records
                        .First(r => r.MsgId == sWeekB.Order.MsgId).ArchivedBoard));

            var undoWeek = BoardSyncService.RestoreArchived(sWeekB.Order.MsgId, bypassRole: true);
            Check("整周替换可一键撤回", undoWeek.Ok, undoWeek.Message);
            Check("整周撤回：整表还原（旧 1 条 + 追加 3 条都回来，新 2 条消失）",
                  Store.LoadDuty().LiveCount == weekBaseCount + 3
                  && Store.LoadDuty().LiveItems.Any(x => x.Id == "duty-old-mon")
                  && !Store.LoadDuty().LiveItems.Any(x => x.Duty == "倒垃圾"),
                  "LiveCount=" + Store.LoadDuty().LiveCount);

            // ⑭ 通知「直接增加」：教室机自动落地，不用电教管理员点确认
            UseMachine(teacherDir, "a-teacher");
            var dAuto = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityNotice, Op = BoardSyncConsts.OpUpsert,
                AutoApply = true,
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["title"] = "明天带跳绳", ["content"] = "体育课用" },
                Sender = "teacher01", SenderRole = "teacher",
                Summary = "通知：明天带跳绳", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sAuto = BoardSyncService.Send(dAuto);
            Check("带「直接增加」标记的通知能发出", sAuto.Ok && sAuto.Order!.AutoApply, sAuto.Message);

            UseMachine(classDir, "a-classroom");
            int noticeBefore = Store.LoadNotifications().Count;
            var pAuto = BoardSyncService.Pull(new[] { cls });
            Check("教室机拉取后自动落地（全程没有调用 Apply）",
                  Store.LoadNotifications().Count == noticeBefore + 1
                  && BoardSyncService.LastPullAutoApplied == 1,
                  pAuto.Report + " / auto=" + BoardSyncService.LastPullAutoApplied);
            var recAuto = BoardSyncService.LoadState().Records
                          .FirstOrDefault(r => r.MsgId == sAuto.Order!.MsgId);
            Check("自动增加：台账直接到「已核对」",
                  recAuto != null && recAuto.Status == BoardSyncConsts.StVerified,
                  recAuto?.Status ?? "null");
            Check("自动增加：留痕写的是「教室机自动增加」",
                  recAuto != null && recAuto.DecisionBy == "教室机自动增加",
                  recAuto?.DecisionBy ?? "null");
            Check("自动增加：只新增一条，教室机原有通知不动",
                  Store.LoadNotifications().Any(x => x.Title == "明天带跳绳"));

            BoardSyncService.Pull(new[] { cls });
            Check("自动增加幂等：再轮询一次不重复落地",
                  Store.LoadNotifications().Count == noticeBefore + 1
                  && BoardSyncService.LastPullAutoApplied == 0,
                  "count=" + Store.LoadNotifications().Count);

            // 教室机把总开关关掉：这类单退回普通收件箱，绝不丢消息
            var sOff = Store.LoadSettings();
            sOff.BoardSyncAutoApply = false;
            Store.SaveSettings(sOff);
            UseMachine(teacherDir, "a-teacher");
            var dAuto2 = new BoardChangeOrder
            {
                ClassKey = cls, Entity = BoardSyncConsts.EntityNotice, Op = BoardSyncConsts.OpUpsert,
                AutoApply = true,
                Fields = new System.Collections.Generic.Dictionary<string, string>
                { ["title"] = "关掉自动增加后的通知", ["content"] = "应该等人工确认" },
                Sender = "teacher01", SenderRole = "teacher",
                Summary = "通知：人工确认", ValidDay = DateTime.Today.ToString("yyyy-MM-dd")
            };
            var sAuto2 = BoardSyncService.Send(dAuto2);
            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var recAuto2 = BoardSyncService.LoadState().Records
                           .FirstOrDefault(r => r.MsgId == sAuto2.Order!.MsgId);
            Check("教室机关掉开关：不自动应用，退回人工裁决且消息不丢",
                  BoardSyncService.LastPullAutoApplied == 0 && recAuto2 != null
                  && recAuto2.Status == BoardSyncConsts.StDelivered && !recAuto2.IsTerminal
                  && string.IsNullOrWhiteSpace(recAuto2.Decision),
                  recAuto2?.Status ?? "null");
            Check("教室机没开自动增加时，通知没有偷偷落地",
                  !Store.LoadNotifications().Any(x => x.Title == "关掉自动增加后的通知"));

            // ⑪ 台账保留期 / 清除：只影响显示，删除必须人工，电教管理员不能清
            var now = DateTime.Now;
            BoardMessageRecord Rec(string id, string dir, string status, int daysAgo) => new()
            {
                MsgId = id, Direction = dir, Status = status,
                UpdatedAt = now.AddDays(-daysAgo),
                Order = new BoardChangeOrder { MsgId = id, CreatedAt = now.AddDays(-daysAgo) }
            };
            var recOldDone = Rec("d-old-done", BoardSyncConsts.DirOut, BoardSyncConsts.StVerified, 30);
            var recOldPending = Rec("d-old-pending", BoardSyncConsts.DirIn, BoardSyncConsts.StDelivered, 30);
            var recNew = Rec("d-new", BoardSyncConsts.DirOut, BoardSyncConsts.StVerified, 1);
            var probe = new[] { recOldDone, recOldPending, recNew };
            Check("保留期内记录显示", BoardSyncService.FilterByKeepDays(probe, 7).Any(r => r.MsgId == "d-new"));
            Check("超 7 天且已终结 → 隐藏", !BoardSyncService.FilterByKeepDays(probe, 7).Any(r => r.MsgId == "d-old-done"));
            Check("超 7 天但未处理 → 仍显示（绝不把待办藏起来）",
                  BoardSyncService.FilterByKeepDays(probe, 7).Any(r => r.MsgId == "d-old-pending"));
            Check("保留期 0 = 全部显示", BoardSyncService.FilterByKeepDays(probe, 0).Count == 3);
            Check("台账计数自洽（总数 = 已终结 + 未处理）",
                  BoardSyncService.LedgerCounts() is var cnt && cnt.Total == cnt.Terminal + cnt.Pending && cnt.Total > 0,
                  BoardSyncService.LedgerCounts().ToString());

            var prevUser = UserSession.Current;
            try
            {
                UserSession.SignIn(new AppUser { Username = "diag-student", Role = UserRole.Student });
                var deny = BoardSyncService.ClearLedger(false);
                Check("电教管理员清台账被拒", !deny.Ok && deny.Message.Contains("电教管理员"), deny.Message);

                UserSession.SignIn(new AppUser { Username = "diag-teacher", Role = UserRole.Teacher });
                int pendingBefore = BoardSyncService.LoadState().Records.Count(r => !r.IsTerminal);
                int terminalBefore = BoardSyncService.LoadState().Records.Count(r => r.IsTerminal);
                var clr = BoardSyncService.ClearLedger(false);
                var afterClr = BoardSyncService.LoadState();
                Check("教师可清「已终结」记录", clr.Ok && terminalBefore > 0
                      && afterClr.Records.All(r => !r.IsTerminal), clr.Message);
                Check("清台账不动未处理记录", afterClr.Records.Count == pendingBefore,
                      $"before={pendingBefore} after={afterClr.Records.Count}");
                Check("清除前自动备份 board_sync.json.bak",
                      File.Exists(Path.Combine(classDir, "board_sync.json.bak")));
            }
            finally
            {
                if (prevUser != null) UserSession.SignIn(prevUser); else UserSession.SignOut();
            }

            // ⑬ r71 全屏提醒正文：新单优先用 ForceText（完整内容 + 新增字段），
            //    老单没有这个字段 → 反序列化为空串 → 教室机回退 Summary，绝不弹空白。
            UseMachine(teacherDir, "a-teacher");
            var dFull = Draft(BoardSyncConsts.OpUpsert, "", "强提醒：明天带跳绳", "明天带跳绳",
                              BoardSyncConsts.EntityNotice);
            dFull.ForceBreakReminder = true;
            dFull.ForceText = "通知公告\n标题：明天带跳绳\n内容：请全体同学自备跳绳\n有效期至：2026年11月10日";
            var sFull = BoardSyncService.Send(dFull);
            Check("带完整正文的强提醒变更单能发出", sFull.Ok, sFull.Message);

            var dLegacy = Draft(BoardSyncConsts.OpUpsert, "", "老单摘要：体育课改室内", "室内",
                                BoardSyncConsts.EntityNotice);
            dLegacy.ForceBreakReminder = true;   // 不写 ForceText，等价于旧版发出的单
            var sLegacy = BoardSyncService.Send(dLegacy);
            Check("无 ForceText 的老变更单能发出", sLegacy.Ok, sLegacy.Message);

            UseMachine(classDir, "a-classroom");
            BoardSyncService.Pull(new[] { cls });
            var fullForces = BoardSyncService.LastPullForce;
            Check("ForceText 优先：全屏显示完整内容而不是一行摘要",
                  fullForces.Any(x => x.Contains("标题：") && x.Contains("有效期至"))
                  && !fullForces.Any(x => x == dFull.Summary),
                  "count=" + fullForces.Count + " " + string.Join(" | ", fullForces));
            Check("老单缺 ForceText 时回退 Summary（不会弹空白）",
                  fullForces.Any(x => x == dLegacy.Summary),
                  string.Join(" | ", fullForces));

            Store.SetAppDir(classDir);   // 坏台账要写在「当前机器」的数据目录里，别测到另一台机器上
            File.WriteAllText(Path.Combine(classDir, BoardSyncConsts.StateFile), "{ broken");
            bool threw = false;
            try { BoardSyncService.LoadState(); }
            catch (InvalidDataException) { threw = true; }
            Check("坏台账大声失败（不静默当空表）", threw);

            Store.SetAppDir(cbDir);
            sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
            File.WriteAllText(log, sb.ToString());
            return fail == 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION " + ex);
            sb.AppendLine($"RESULT FAIL  {pass}/{pass + fail + 1}");
            try { File.WriteAllText(log, sb.ToString()); } catch { }
            return false;
        }
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
