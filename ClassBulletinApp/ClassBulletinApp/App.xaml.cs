using System;
using System.IO;
using System.Runtime.InteropServices;
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
                    "handwriting" => new HandwritingPopup(),
                    "handball" => new HandwritingBallWindow(),
                    "schedulewin" => new ScheduleWindow(),
                    "dragarmed" => new ScheduleWindow(),
                    "weekdrag" => new MainWindow { InitialTabIndex = 0 },
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
            try { mw = new MainWindow(); mw.Show(); }
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
            // 悬浮课表：手势与拖动逻辑都在这里，XAML 里的 Stylus 附加属性（关系统长按 / Flicks）
            // 也只有构造一次才能确认解析没问题。
            Check("ScheduleWindow", () => new ScheduleWindow());
            Check("LoginWindow", () => new LoginWindow());
            Check("RetrievePasswordWindow", () => new RetrievePasswordWindow());
            Check("LayoutWindow", () => new LayoutWindow { InitialTabIndex = 2 });
            Check("HomeworkWindow", () => new HomeworkWindow());
            Check("MissingWindow", () => new MissingWindow());
            Check("MissingEditWindow", () => new MissingEditWindow(Store.LoadMissingBoard()));
            Check("HomeworkEditWindow", () => new HomeworkEditWindow(Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard()));
            Check("HandwritingPopup", () => new HandwritingPopup());
            Check("HandwritingBallWindow", () => new HandwritingBallWindow());
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
