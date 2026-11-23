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
                    "schedulehand" => new ScheduleWindow(),
                    "schedulehandwide" => new ScheduleWindow(),
                    "schedulehanddark" => new ScheduleWindow(),
                    "schedulehandpages" => new ScheduleWindow(),
                    "weekdrag" => new MainWindow { InitialTabIndex = 0 },
                    "schedulewinfail" => DiagScheduleUpdateFail(),
                    "schedulewinuptodate" => DiagScheduleUpToDate(),
                    "notifywin" => new NotificationWindow(),
                    // r131：通知条徽章两态实景（换课红 / 倒计时琥珀），用注入数据不依赖真实底表
                    "notifybadgechange" => DiagNotifyBadge(true),
                    "notifybadgecountdown" => DiagNotifyBadge(false),
                    // r133：校历 / 调休窗口（列表里带一条补课 + 一条放假，便于看排版）
                    "schoolcalendar" => new SchoolCalendarWindow(DateTime.Today),
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
                    // r125：手动录入（课程设置自动调出 + 方案下拉 + 拖动提示）与单格编辑器
                    "manual" => DiagManualWindow(),
                    // r140：保存到该班撞上同名导入课表时的三选一（原 MessageBox 是/否/取消）
                    "conflict" => new SaveClassConflictDialog("八年级(22)班", "八年级(22)班（手录）"),
                    "celledit" => DiagCellEditorWindow(),
                    // r142：作息 / 科目库的编辑弹窗（原「课表基础数据维护」页签已删除，编辑改在小窗里）
                    "perioddialog" => DiagPeriodDialog(),
                    "subjectdialog" => DiagSubjectDialog(),
                    // r130：下载模板的选择窗（4 类模板 + 单双周说明）
                    "tplpick" => new ImportTemplatePickWindow(),
                    // r143：智能导入预览窗（自定义分隔符档位的实景）
                    "importpreview" => DiagImportPreview(),
                    "dragcard" => DiagManualWindow(),
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
                    // 涂鸦板：便签容器（黑板绿 / 亚麻米）与手写黑板
                    "doodle" => DiagDoodleBoard(DoodleBoardThemes.DefaultKey, DoodleModes.Notes, false),
                    "doodlenotes" => DiagDoodleBoard("linen", DoodleModes.Notes, false),
                    "doodleink" => DiagDoodleBoard("chalk", DoodleModes.Ink, true),
                    "doodlewhite" => DiagDoodleBoard("white", DoodleModes.Ink, false),
                    // 大板 + 大字号：验证板子拖到 1200 宽时便签自动变 4 列、字号放大后不溢出
                    "doodlebig" => DiagDoodleBoard("linen", DoodleModes.Notes, false, 1200, 820, 20),
                    // 最大字号：验证 30 号字下便签不溢出、标题正文层级还在
                    "doodlehuge" => DiagDoodleBoard("chalk", DoodleModes.Notes, false, 760, 560, 30),
                    // 自动便签的格式编辑窗（复用悬浮便签的编辑窗，只改格式）
                    "autostyle" => DiagAutoStyleWindow(),
                    // 板上一张自动便签被套用了格式（红字 + 更大 + 不加粗）
                    "doodleformat" => DiagDoodleFormatted(),
                    // r95：黑板管理窗（打开 / 收起 / 改名 / 删除）
                    "doodlemanage" => DiagDoodleManager(),
                    // r96：大字号实景 / 大屏编辑窗 / 板头格式窗
                    "doodlebigfont" => DiagDoodleBigFont(),
                    "doodleedit" => DiagDoodleBigScreenEdit(),
                    // r99：作业公示多科目编辑窗（每条 = 科目 + 内容两个输入框）
                    "doodlehwedit" => DiagDoodleHomeworkEdit(),
                    // r101：值日便签整份编辑窗（星期 / 姓名 / 值日内容 三段）
                    "doodleduty" => DiagDoodleDutyEdit(),
                    // r100：课表便签一节课一行
                    "doodleschedule" => DiagDoodleSchedule(),
                    // r104：课表便签编辑窗 —— 内容只读，但可单独调「第N节」格式
                    "doodlescheduleedit" => new DoodleBigScreenEditWindow(new DoodleBigEditTarget
                    {
                        Module = DoodleModules.Schedule,
                        Title = "今日课程",
                        Body = "第1节 语文\n第2节 数学\n第3节 英语\n第4节 物理\n第5节 化学",
                        BodyEditable = false,
                        BodyHint = "课程来自整周课表，请到主程序「课程表」修改；这里只调格式与风格。",
                        Style = new DoodleAutoStyle { TitleSize = 40, ContentSize = 30, SubjectSize = 36 }
                    }),
                    "doodleheader" => DiagDoodleHeaderStyle(),
                    "doodlemin" => DiagDoodleBoardCollapsed(),
                    // r144：通知公告便签的配色与字号已并进编辑窗（原「noticestyle」独立窗删除）
                    "noticeedit" => new NotificationEditWindow(),
                    // r144：代次维护窗（本机高水位 + 发布计数器）
                    "revreset" => new RevResetWindow(),
                    // r146：发布端更新回执面板（造四台机的假状态，核对汇总与四态排版）
                    "receipts" => DiagReceiptShot(),
                    // r144：悬浮课表双击弹出的换班窗（核对班级按 1,2,…,10 的自然序）
                    "scheduletarget" => new ScheduleTargetWindow(),
                    "dutyedit" => new DutyEditWindow(),
                    "backupwin" => new BackupWindow(),
                    "updatesettings" => new UpdateSettingsWindow(),
                    "settingspush" => new SettingsPushWindow(),
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

                // 板书模式截图：窗口先加宽 420，再展开手写区并灌两笔，否则拍到的是一片空白纸
                // r145：另外两个名字专门拍新东西 —— schedulehanddark=墨绿黑板 + 白笔，
                //       schedulehandpages=三页板书（停在第 2 页，能看到页码条）
                if (w0 is ScheduleWindow hsw
                    && (string.Equals(e.Args[1], "schedulehand", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Args[1], "schedulehandwide", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Args[1], "schedulehanddark", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Args[1], "schedulehandpages", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        // ★ 先换底色再灌样例笔画：笔迹带着画它那一刻的颜色，顺序反了就是"深底黑笔"看不见
                        if (string.Equals(e.Args[1], "schedulehanddark", StringComparison.OrdinalIgnoreCase))
                            hsw.DiagSetHandTheme("green");

                        hsw.DiagOpenHandForShot();
                        // 宽板书：拖窗口左沿把手写区拉到 620 —— 用来对比「课表列宽一点没变」
                        if (string.Equals(e.Args[1], "schedulehandwide", StringComparison.OrdinalIgnoreCase))
                            hsw.DiagLeftResize(hsw.DiagCourseWidth + 620);

                        if (string.Equals(e.Args[1], "schedulehandpages", StringComparison.OrdinalIgnoreCase))
                        {
                            hsw.DiagAddHandPage();      // 第 2 页：空白
                            hsw.DiagAddHandPage();      // 第 3 页
                            hsw.DiagDrawHandSample();
                            hsw.DiagGotoHandPage(2);    // 停在第 2 页 → 页码显示 2/3
                            hsw.DiagDrawHandSample();
                        }
                    }
                    catch (Exception ex) { try { File.WriteAllText(shotLog, "HANDFAIL " + ex.Message); } catch { } }
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

                // r127：拖动浮起卡片实景（真实拖动过程中截不到稳定画面，这里把卡片停在网格中部）
                if (w0 is ManualScheduleWindow gmw
                    && string.Equals(e.Args[1], "dragcard", StringComparison.OrdinalIgnoreCase))
                {
                    try { gmw.DiagShowDragCard(); }
                    catch (Exception ex) { try { File.WriteAllText(shotLog, "CARDFAIL " + ex.Message); } catch { } }
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
                if (w0 is DoodleBoardWindow ddb)
                    File.AppendAllText(shotLog, "\n" + ddb.DiagLayoutInfo());
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
                    // r116：板书 / 通知 / 黑板三个显示类入口对所有角色默认显示（含游客），排在各自默认末尾
                    var roles = new[]
                    {
                        (UserRole.Student, "board,countdown,duty,hand,hw,info,missing,note,notice",
                         new[] { "hw", "missing", "note", "countdown", "duty", "info", "hand", "notice", "board" }),
                        (UserRole.Teacher, "board,countdown,hand,info,note,notice,publish,settings,week",
                         new[] { "publish", "note", "week", "countdown", "settings", "info", "hand", "notice", "board" }),
                        (UserRole.SubjectTeacher, "board,countdown,hand,info,note,notice,publish,settings,week",
                         new[] { "publish", "note", "week", "countdown", "settings", "info", "hand", "notice", "board" }),
                        (UserRole.Admin, "board,countdown,hand,info,note,notice,publish,settings,week",
                         new[] { "publish", "note", "week", "countdown", "settings", "info", "hand", "notice", "board" })
                    };

                    foreach (var (role, expectSorted, expectOrder) in roles)
                    {
                        UserSession.SignIn(new AppUser { Username = "qa-" + role, Role = role });
                        var map = QuickActionCatalog.VisibleMap();
                        string got = Keys(map.Where(kv => kv.Value).Select(kv => kv.Key));
                        Check($"{role} 出厂默认显示项", got == expectSorted, got);
                        string order = string.Join(",", QuickActionCatalog.OrderFor(role).Take(expectOrder.Length));
                        Check($"{role} 胶囊排列顺序", order == string.Join(",", expectOrder), order);
                        Check($"{role} 全部入口都在目录里",
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

                    // 游客：不用编辑权限的入口全在 —— 原来的 周课 / 设置 / 信息 + 板书 / 通知 / 黑板
                    UserSession.SignOut();
                    Check("游客显示 周课 / 设置 / 信息 / 板书 / 通知 / 黑板",
                          Keys(QuickActionCatalog.VisibleNow()) == "board,hand,info,notice,settings,week",
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

        // 诊断：--manual-drag-check 用真实系统鼠标在「手动录入」网格里拖格子，
        // 验「拖动 = 移动 / 交换」，且不会被单击填充顶掉（r125 只测了纯逻辑，真机拖不动才暴露）。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--manual-drag-check", StringComparison.OrdinalIgnoreCase))
        {
            string dlog = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.manualdrag.log");
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            void Flush() { try { File.WriteAllText(dlog, sb.ToString()); } catch { } }
            void Check(string name, bool ok, string detail = "")
            {
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (ok) pass++; else fail++;
                Flush();
            }
            sb.AppendLine("START " + DateTime.Now.ToString("HH:mm:ss"));
            Flush();
            try
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CB_DATA_DIR")))
                {
                    sb.AppendLine("拒绝：请用 CB_DATA_DIR 指向数据副本后再运行（本诊断会模拟真实鼠标）。");
                    sb.AppendLine("RESULT FAIL  0/1");
                    try { File.WriteAllText(dlog, sb.ToString()); } catch { }
                    Shutdown(1);
                    return;
                }
                Store.SuppressWrites = true;   // 连副本都不写

                var t = ScheduleTemplates.CreateWithBuiltIns();
                var sf = new ScheduleFile();
                var cls = new ClassSchedule { ClassName = "拖动测试班" };
                cls.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Period = 1, Subject = "语文", Teacher = "张老师", Week = 0 } },
                    [2] = new List<Lesson> { new() { Period = 2, Subject = "英语", Teacher = "李老师", Week = 0 } }
                };
                sf.Classes.Add(cls);
                // 课程设置里放一门「数学」并让列表默认选中它：一旦被单击填充，格子里就会变成数学
                t.UpsertSetup(new ClassSetup
                {
                    ClassName = "拖动测试班", PeriodTemplateName = "初中 7 节", SavedAt = DateTime.Now,
                    Pairs = new List<SubjectPair> { new() { Subject = "数学", Teacher = "陈老师", WeeklyPeriods = 5 } }
                });

                var w = new ManualScheduleWindow(sf, t)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 80,
                    Top = 60,
                    Topmost = true   // 探针要保证真鼠标点到的是这个窗口，不能被别的窗口盖住
                };
                w.Show();
                w.Activate();
                PumpMs(900);
                w.UpdateLayout();

                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var cells = (Dictionary<(int day, int period), Button>)typeof(ManualScheduleWindow)
                    .GetField("_cellButtons", bf)!.GetValue(w)!;
                var data = (Dictionary<int, Dictionary<int, List<Lesson>>>)typeof(ManualScheduleWindow)
                    .GetField("_data", bf)!.GetValue(w)!;
                var lb = (System.Windows.Controls.ListBox)typeof(ManualScheduleWindow).GetField("_lbPairs", bf)!.GetValue(w)!;
                var hint = (System.Windows.Controls.TextBlock)typeof(ManualScheduleWindow)
                    .GetField("_txtHint", bf)!.GetValue(w)!;

                Check("窗口显示、格子已生成、左侧已选中一门课",
                      w.IsVisible && cells.Count >= 20 && lb.SelectedItem is SubjectPair,
                      $"cells={cells.Count} 选中={(lb.SelectedItem as SubjectPair)?.Subject}");

                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                var content = (FrameworkElement)w.Content;

                // ★ 屏幕坐标必须从「客户区原点（物理像素）」+「格子相对窗口的 DIP 偏移 × 缩放」算，
                // 不能直接拿 PointToScreen：125% 缩放下单位口径不一致，第一版探针差 20% 点到别处。
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);   // 非前台时第一次点击会被"激活窗口"吃掉
                PumpMs(300);
                var clientOrigin = new DPOINT { X = 0, Y = 0 };
                ClientToScreen(hwnd, ref clientOrigin);

                (int x, int y) Center((int day, int period) key)
                {
                    var b = cells[key];
                    var off = b.TransformToAncestor(w).Transform(
                        new System.Windows.Point(b.ActualWidth / 2, b.ActualHeight / 2));
                    return ((int)Math.Round(clientOrigin.X + off.X * dpi.DpiScaleX),
                            (int)Math.Round(clientOrigin.Y + off.Y * dpi.DpiScaleY));
                }
                string CellText(int day, int period)
                {
                    var b = cells[(day, period)];
                    return (b.Content as string ?? "").Replace("\n", "/");
                }

                // 输入探针：确认系统的按下/移动/抬起到底有没有进到窗口、进到格子按钮，
                // 以及鼠标捕获在拖动过程中落到谁身上（捕获被别人抢走 = 后续 move/up 全丢）。
                int nDown = 0, nMove = 0, nUp = 0, nBtnMove = 0;
                var capLog = new List<string>();
                w.PreviewMouseDown += (_, _) => nDown++;
                w.PreviewMouseMove += (_, _) =>
                {
                    nMove++;
                    if (capLog.Count < 8)
                    {
                        var cap = System.Windows.Input.Mouse.Captured;
                        capLog.Add(cap == null ? "null"
                            : cap is Button ? "Button" : cap.GetType().Name);
                    }
                };
                w.PreviewMouseLeftButtonUp += (_, _) => nUp++;
                cells[(0, 1)].MouseMove += (_, _) => nBtnMove++;

                sb.AppendLine($"dpi={dpi.DpiScaleX:0.##} client={clientOrigin.X},{clientOrigin.Y} 源格={CellText(0, 1)} 目标格={CellText(0, 3)}");
                Flush();

                // ① 拖到空格 = 移动
                var from = Center((0, 1));
                var to = Center((0, 3));
                SetCursorPos(from.x, from.y);
                PumpMs(120);
                sb.AppendLine("源格坐标 " + from.x + "," + from.y + " 光标处窗口="
                              + DescribeHwnd(WindowFromPoint(new DPOINT { X = from.x, Y = from.y })));
                Flush();
                RealDrag(from.x, from.y, to.x - from.x, to.y - from.y, 12);
                PumpMs(900);
                sb.AppendLine($"输入计数 down={nDown} move={nMove} up={nUp} 源格MouseMove={nBtnMove}");
                sb.AppendLine("捕获序列 " + string.Join(",", capLog));
                Flush();

                bool srcEmpty = !data.TryGetValue(0, out var d0) || !d0.ContainsKey(1);
                bool moved = data.TryGetValue(0, out var d1) && d1.TryGetValue(3, out var l3)
                             && l3.Count == 1 && l3[0].Subject == "语文";
                Check("拖到空格 = 移动（原格清空、目标格保留原课程）", srcEmpty && moved,
                      $"原格={(srcEmpty ? "空" : CellText(0, 1))} 目标格={CellText(0, 3)}");
                Check("拖动没有被单击填充顶掉（目标不是课程设置里选中的「数学」）",
                      CellText(0, 3) != "数学", "目标格=" + CellText(0, 3));

                // ② 拖到有课的格子 = 两格交换（拖动后网格重建过，格子按钮要重新取）
                var from2 = Center((0, 3));
                var to2 = Center((0, 2));
                RealDrag(from2.x, from2.y, to2.x - from2.x, to2.y - from2.y, 12);
                PumpMs(900);

                Check("拖到有课格 = 两格交换",
                      CellText(0, 3).StartsWith("英语") && CellText(0, 2).StartsWith("语文"),
                      $"第3节={CellText(0, 3)} 第2节={CellText(0, 2)}");
                Check("提示语说明了交换", hint.Text.Contains("交换"), hint.Text);

                // r127：拖动时的浮起卡片不能留在覆盖层里（源格也不能一直发灰）
                var dragLayer = (System.Windows.Controls.Canvas)typeof(ManualScheduleWindow)
                    .GetField("_dragLayer", bf)!.GetValue(w)!;
                Check("拖动结束后浮起卡片已收回、源格恢复不透明",
                      dragLayer.Children.Count == 0 && cells[(0, 3)].Opacity > 0.99,
                      $"覆盖层残留 {dragLayer.Children.Count} 个，源格不透明度={cells[(0, 3)].Opacity:0.##}");

                // ③ 已有课的格子单击 = 不填充（r126 规则：有课的只负责被拖走，改内容靠双击）
                var p3 = Center((0, 2));
                RealDrag(p3.x, p3.y, 0, 0, 1);
                PumpMs(700);
                Check("已有课的格子单击不填充（还是语文，没被换成选中的数学）",
                      CellText(0, 2).StartsWith("语文"), "第2节=" + CellText(0, 2));

                // ④ 空格单击 = 填充选中的课程（220ms 延时后落子）
                var p4 = Center((0, 5));
                RealDrag(p4.x, p4.y, 0, 0, 1);
                PumpMs(700);
                Check("空格单击会填充选中的课程", CellText(0, 5).StartsWith("数学"), "第5节=" + CellText(0, 5));

                w.Close();
                sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
                Flush();
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
                sb.AppendLine($"RESULT FAIL  {pass}/{pass + fail + 1}");
                Flush();
                Shutdown(1);
                return;
            }
        }

        // 诊断：--manual-data-check 用真实底表逐班核对「手动录入」网格内容是否等于该班底表。
        // 用户反馈：自动更新后再进手动录入，在班级列表选班，显示的课表与更新后的课表不一致。
        // 这条链路只有「选班 → LoadClassIntoGrid → _data」几步，用真实数据跑一遍就能定位：
        // 是取值时机（IsEditable ComboBox 的 Text 滞后）还是数据源本身不同。
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--manual-data-check", StringComparison.OrdinalIgnoreCase))
        {
            string mlog = Path.Combine(Path.GetTempPath(), "ClassBulletinApp.manualdata.log");
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            void Flush() { try { File.WriteAllText(mlog, sb.ToString()); } catch { } }
            void Check(string name, bool ok, string detail = "")
            {
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (ok) pass++; else fail++;
                Flush();
            }
            sb.AppendLine("START " + DateTime.Now.ToString("HH:mm:ss"));
            Flush();
            try
            {
                Store.SuppressWrites = true;
                var sf = Store.LoadSchedule();
                if (sf == null || sf.Classes.Count == 0)
                {
                    // 本机底表被清过（如刚点过「一键清除全部导入数据」）时不算失败：
                    // 这条探针的意义是拿真实数据核对，没有数据就跳过，免得以后误判成回归。
                    sb.AppendLine("SKIP 本机 schedule.json 底表是空的，没有班级可核对（先导入课表再跑这条）。");
                    sb.AppendLine("RESULT SKIP  0/0");
                    Flush();
                    Shutdown(0);
                    return;
                }
                var tmpl = Store.LoadScheduleTemplates();
                sb.AppendLine($"底表 {sf.Classes.Count} 个班，ImportedAt={sf.ImportedAt:yyyy-MM-dd HH:mm}，文件={Store.ScheduleStamp:HH:mm:ss}");
                Flush();

                var w = new ManualScheduleWindow(sf, tmpl);
                w.Show();
                PumpMs(800);
                w.UpdateLayout();

                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var cbClass = (System.Windows.Controls.ComboBox)typeof(ManualScheduleWindow).GetField("_cbClass", bf)!.GetValue(w)!;
                var hint = (System.Windows.Controls.TextBlock)typeof(ManualScheduleWindow).GetField("_txtHint", bf)!.GetValue(w)!;

                string Sig(Dictionary<int, Dictionary<int, List<Lesson>>> data)
                {
                    var list = new List<string>();
                    foreach (var d in data)
                        foreach (var p in d.Value)
                            list.Add(d.Key + ":" + p.Key + "=" + string.Join("/", p.Value.Select(l => l.Subject)));
                    list.Sort(StringComparer.Ordinal);
                    return string.Join(";", list);
                }

                // 用前 5 个有课的班模拟「用户在班级列表里逐个选班」
                var probe = sf.Classes.Where(c => c.Data.Count > 0).Take(5).ToList();
                Check("班级下拉项数 ≥ 底表班数", cbClass.Items.Count >= sf.Classes.Count,
                      $"下拉 {cbClass.Items.Count} 项 / 底表 {sf.Classes.Count} 班");

                for (int i = 0; i < probe.Count; i++)
                {
                    var want = probe[i];
                    int idx = cbClass.Items.IndexOf(want.ClassName);
                    cbClass.SelectedIndex = idx;
                    PumpMs(200);
                    var data = (Dictionary<int, Dictionary<int, List<Lesson>>>)typeof(ManualScheduleWindow)
                        .GetField("_data", bf)!.GetValue(w)!;
                    string got = Sig(data), exp = Sig(want.Data);
                    bool ok = got == exp;
                    string detail = "";
                    if (!ok)
                    {
                        // 顺带指出「网格里现在到底是谁的课表」，方便一眼定位错位是哪种
                        var landed = sf.Classes.FirstOrDefault(c => Sig(c.Data) == got)?.ClassName ?? "（没有任何班匹配）";
                        detail = $"下拉选中={cbClass.SelectedItem} 实际显示={landed} 提示={hint.Text}";
                    }
                    Check("选「" + want.ClassName + "」后网格 = 该班底表", ok, detail);

                    // 底表对了，还要说清"大屏上另有调代课"——否则仍会被当成不一致
                    int ov = Store.LoadOverrides().Count(o => !o.Suspended
                             && string.Equals(o.ClassName, want.ClassName, StringComparison.Ordinal));
                    if (ov > 0)
                        Check("「" + want.ClassName + "」提示里点明了 " + ov + " 条调代课不在此展开",
                              hint.Text.Contains("条临时调课"), hint.Text);
                }

                // 再验一次「同一班重选」（点同一项）与「新建班级」不会把网格弄脏
                if (probe.Count > 0)
                {
                    cbClass.SelectedIndex = cbClass.Items.IndexOf(probe[0].ClassName);
                    PumpMs(150);
                    cbClass.SelectedIndex = 0;
                    PumpMs(150);
                    var data0 = (Dictionary<int, Dictionary<int, List<Lesson>>>)typeof(ManualScheduleWindow)
                        .GetField("_data", bf)!.GetValue(w)!;
                    Check("反复切班后仍与当前选中班一致",
                          Sig(data0) == Sig(sf.Classes[0].Data),
                          "选中=" + cbClass.SelectedItem);
                }

                w.Close();
                sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
                Flush();
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
                sb.AppendLine($"RESULT FAIL  {pass}/{pass + fail + 1}");
                Flush();
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

            void Check(string name, Func<Window?> factory)
            {
                // 每步先落盘：万一某个窗口构造时卡住，日志能指到是哪一个
                try { File.AppendAllText(tracePath, "> " + name + "\n"); } catch { }
                try
                {
                    var w = factory();
                    sb.AppendLine("OK   " + name);
                    GuardSettings(name);
                    try { w?.Close(); } catch { }
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
                // r79：用户要求「信息页把发布信息按钮放在立即检查那一行的前边」，所以通用发布按钮
                // 重新可见，并且必须是工具栏 WrapPanel 的第一项、紧靠立即检查之前。
                if (w.BtnInfoPublish.Visibility != vis) throw new Exception("通用发布按钮未恢复可见");
                var infoBar = System.Windows.LogicalTreeHelper.GetParent(w.BtnInfoPublish) as System.Windows.Controls.WrapPanel;
                if (infoBar == null || !ReferenceEquals(infoBar.Children[0], w.BtnInfoPublish)
                    || !infoBar.Children.Contains(w.BtnInfoPull))
                    throw new Exception("发布信息按钮没有排在立即检查之前");
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
            // r131 通知条徽章三态：有换课=红、无换课+开关开+有倒计时=琥珀、否则隐藏。
            // ★ 全程不碰跑马灯，不占那两条名额 —— 用 Diag 注入数据，纯内存，不写盘。
            Check("r131 通知条徽章三态与开关", () =>
            {
                var w = new NotificationWindow();
                try
                {
                    var badge = (System.Windows.Controls.Border)typeof(NotificationWindow)
                        .GetField("Badge", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .GetValue(w)!;
                    var label = (System.Windows.Controls.TextBlock)w.FindName("BadgeLabel")!;
                    var value = (System.Windows.Controls.TextBlock)w.FindName("BadgeValue")!;
                    string cls = Store.LoadSettings().SelectedClass ?? "七年级(9)班";
                    var today = DateTime.Today;
                    // r133：徽章的「今天」走校历。诊断注入的数据必须用同一口径，
                    // 否则本机设了调休（周六补周三）时注入到周六列、徽章却去周三列找，会假失败。
                    int todayIdx = SchoolCalendar.Resolve(Store.LoadSchedule(), today,
                        Store.LoadSettings().SemesterStartMonday).DayIdx;

                    // ① 当天有换课 → 红徽章 + 节数，倒计时被压住
                    w.DiagCountdown = new CountdownBoard
                    {
                        Items = { new CountdownItem { Name = "期末考试", TargetDate = today.AddDays(30).ToString("yyyy-MM-dd") } }
                    };
                    w.DiagShowCountdownBadge = true;
                    w.DiagTodayOverrides = new List<LessonOverride>
                    {
                        new LessonOverride { ClassName = cls, DayIdx = todayIdx,
                            Period = 3, Subject = "语文", Teacher = "张老师", Type = "代", Source = "manual" },
                        new LessonOverride { ClassName = cls, DayIdx = todayIdx,
                            Period = 6, Subject = "自习", Type = "调", Source = "temp" }
                    };
                    w.RefreshBadge();
                    if (badge.Visibility != System.Windows.Visibility.Visible)
                        throw new Exception("有换课时徽章没显示");
                    if (label.Text != "今日换课" || value.Text != "2")
                        throw new Exception("换课徽章文案不对：" + label.Text + "/" + value.Text);

                    // ② 无换课 + 开关开 + 未过期倒计时 → 琥珀徽章，取最近一条
                    w.DiagTodayOverrides = new List<LessonOverride>();
                    w.RefreshBadge();
                    if (label.Text != "期末考试" || !value.Text.Contains("30"))
                        throw new Exception("倒计时徽章没取最近一条：" + label.Text + "/" + value.Text);

                    // ③ 开关关 → 徽章隐藏（换课仍优先，这里无换课所以隐藏）
                    w.DiagShowCountdownBadge = false;
                    w.RefreshBadge();
                    if (badge.Visibility != System.Windows.Visibility.Collapsed)
                        throw new Exception("关了开关倒计时徽章还在");

                    // ④ 开关关但有换课 → 红徽章仍显示（换课不受开关影响）
                    w.DiagTodayOverrides = new List<LessonOverride>
                    {
                        new LessonOverride { ClassName = cls, DayIdx = todayIdx,
                            Period = 1, Subject = "数学", Source = "manual" }
                    };
                    w.RefreshBadge();
                    if (badge.Visibility != System.Windows.Visibility.Visible || label.Text != "今日换课")
                        throw new Exception("关了开关后换课徽章被误伤");

                    // ④b r132 ★ 只数「今天」这一列，不数整周
                    //   场景：一条周一起、周五止的调课占用周一~周五；今天只有 1 处换课、其它星期 2 处。
                    //   旧写法 ForDate 不看星期，会把这 3 处全算进"今天"。
                    int otherIdx = (todayIdx + 2) % 7;
                    var from = DateOnly.FromDateTime(today.AddDays(-3));
                    var to = DateOnly.FromDateTime(today.AddDays(3));
                    w.DiagShowCountdownBadge = false;
                    w.DiagTodayOverrides = new List<LessonOverride>
                    {
                        new LessonOverride { ClassName = cls, DayIdx = todayIdx, Period = 2,
                            Subject = "语文", Source = "manual", ValidFrom = from, ValidTo = to },
                        new LessonOverride { ClassName = cls, DayIdx = todayIdx, Period = 2,
                            Subject = "语文", Source = "temp", ValidFrom = from, ValidTo = to },   // 同格重复来源 → 仍算 1
                        new LessonOverride { ClassName = cls, DayIdx = otherIdx, Period = 2,
                            Subject = "数学", Source = "manual", ValidFrom = from, ValidTo = to },
                        new LessonOverride { ClassName = cls, DayIdx = otherIdx, Period = 3,
                            Subject = "英语", Source = "manual", ValidFrom = from, ValidTo = to }
                    };
                    w.RefreshBadge();
                    if (value.Text != "1")
                        throw new Exception("换课计数没有只数今天（本周其它星期 / 同格重复来源被算进去了）：" + value.Text);

                    // ⑤ 全空 → 隐藏
                    w.DiagTodayOverrides = new List<LessonOverride>();
                    w.DiagCountdown = new CountdownBoard();
                    w.DiagShowCountdownBadge = true;
                    w.RefreshBadge();
                    if (badge.Visibility != System.Windows.Visibility.Collapsed)
                        throw new Exception("没换课没倒计时时徽章没隐藏");

                    // ⑥ 过期倒计时不算（DaysLeft<0 过滤），"就是今天"高亮
                    var cd = new CountdownBoard { Items =
                    {
                        new CountdownItem { Name = "过期事件", TargetDate = today.AddDays(-3).ToString("yyyy-MM-dd") },
                        new CountdownItem { Name = "今天考试", TargetDate = today.ToString("yyyy-MM-dd") }
                    }};
                    w.DiagCountdown = cd;
                    w.RefreshBadge();
                    if (label.Text != "今天考试" || value.Text != "就是今天")
                        throw new Exception("过期倒计时没被过滤 / 今天没高亮：" + label.Text + "/" + value.Text);

                    // ⑦ 长名字截断
                    if (NotificationWindow.ShortName("七年级期中综合素质测评") != "七年级期中综…")
                        throw new Exception("徽章长名截断不对：" + NotificationWindow.ShortName("七年级期中综合素质测评"));

                    // ⑧ 跑马灯名额仍为 2，没被徽章挤掉；且 r132 位置已对调（按钮最左、徽章最右）
                    if (NotificationWindow.MarqueeMax != 2)
                        throw new Exception("徽章改动把跑马灯名额改了：" + NotificationWindow.MarqueeMax);
                    var btnHide = (System.Windows.Controls.Button)w.FindName("BtnHide")!;
                    if (System.Windows.Controls.Grid.GetColumn(btnHide) != 0
                        || System.Windows.Controls.Grid.GetColumn(badge) != 2)
                        throw new Exception("徽章与自动隐藏按钮的位置没对调（应为 按钮=左、徽章=右）");

                    w.DiagTodayOverrides = null; w.DiagCountdown = null; w.DiagShowCountdownBadge = null;
                    return w;
                }
                catch { w.DiagTodayOverrides = null; w.DiagCountdown = null; w.DiagShowCountdownBadge = null; try { w.Close(); } catch { } throw; }
            });

            // r133 校历 / 调休：日期 ≠ 上哪天的课（周六补周三的课、某天放假）。
            // 断言全部跑在内存里（自造 ScheduleFile），不碰真实课表；最后返回校历窗口顺手验一次能构造。
            Check("r133 校历调休换算与显示", () =>
            {
                string sem = new DateTime(2026, 9, 1).ToString("yyyy-MM-dd");
                var wed = new DateTime(2026, 11, 18);
                var sat = new DateTime(2026, 11, 21);
                var sun = new DateTime(2026, 11, 22);
                var mon = new DateTime(2026, 11, 23);
                string D(DateTime d) => d.ToString("yyyy-MM-dd");

                var rules = new List<SchoolCalendarRule>
                {
                    new() { Date = D(sat), Kind = CalendarKind.Makeup, AsDate = D(wed), Note = "上周三停课" },
                    new() { Date = D(sun), Kind = CalendarKind.Off, Note = "校运会" },
                    new() { Date = D(mon), Kind = CalendarKind.Off, Note = "校运会" },
                    new() { Date = "2026-11-25", Kind = CalendarKind.Makeup, AsDate = "" }   // 残缺规则
                };

                // ① 周六按周三上课：星期、单双周、调课口径全部指向 11-18
                var plan = SchoolCalendar.Resolve(rules, sat, sem);
                if (!plan.IsMakeup || plan.DayIdx != 2) throw new Exception("周六没有按周三上课：DayIdx=" + plan.DayIdx);
                if (plan.Parity != WeekHelper.GetParity(wed, sem)) throw new Exception("补课的单双周没有按 11-18 算");
                if (plan.TagText != "按第12周周三上课") throw new Exception("大屏标签文案不对：" + plan.TagText);
                if (plan.ShortTag != "按12周周三" || plan.SourceWeek != 12)
                    throw new Exception("调休标签没带上周三所在周次：" + plan.ShortTag + "/周=" + plan.SourceWeek);
                if (plan.OverrideDate != wed.Date) throw new Exception("调课没有按被映射的那天取");

                // ② 放假
                var offPlan = SchoolCalendar.Resolve(rules, mon, sem);
                if (!offPlan.IsOff || offPlan.TagText != "今日放假") throw new Exception("放假日没认出来：" + offPlan.TagText);

                // ③ 没有规则的日子一律照日历星期走
                var noRule = SchoolCalendar.Resolve(new List<SchoolCalendarRule>(), sun, sem);
                if (noRule.IsMakeup || noRule.IsOff || noRule.DayIdx != 6)
                    throw new Exception("没有规则的日子被改写了：DayIdx=" + noRule.DayIdx);

                // ④ 残缺规则（补课但没填按哪天）绝不能把当天课表弄没
                var badPlan = SchoolCalendar.Resolve(rules, new DateTime(2026, 11, 25), sem);
                if (badPlan.IsMakeup || badPlan.DayIdx != 2) throw new Exception("残缺规则让当天变成了非正常日");

                // ⑤ 取消「沿用调课」
                var noInherit = SchoolCalendar.Resolve(new List<SchoolCalendarRule>
                {
                    new() { Date = D(sat), AsDate = D(wed), InheritOverrides = false }
                }, sat, sem);
                if (noInherit.UsesOverrides) throw new Exception("取消沿用调课后仍然会套调课");

                // ⑥ 批量粘贴解析
                var pasted = SchoolCalendar.ParseLine("2026-11-21 补课 2026-11-18 上周三停课", 2026);
                if (pasted == null || pasted.AsDateValue != wed.Date || !pasted.Note.Contains("停课"))
                    throw new Exception("批量粘贴解析不对：" + (pasted?.DisplayText ?? "null"));
                var pastedOff = SchoolCalendar.ParseLine("2026-11-23 放假 校运会", 2026);
                if (pastedOff == null || !pastedOff.IsOff) throw new Exception("批量粘贴的放假日解析不对");
                if (SchoolCalendar.ParseLine("随便写点什么", 2026) != null) throw new Exception("乱写的行居然解析成功了");

                // ⑦ 大屏：周六那一屏排的是周三的课（周三=语文，周六自带的体育不该出现）
                var sf = new ScheduleFile { Calendar = rules };
                var cls = new ClassSchedule { ClassName = "测试班" };
                cls.Data[2] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Subject = "语文" } },
                    [3] = new List<Lesson> { new() { Subject = "数学" } }
                };
                cls.Data[5] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Subject = "体育" } }
                };
                sf.Classes.Add(cls);

                var board = BoardBuilder.Build("测试班", sem, sf, sat, new List<LessonOverride>());
                if (!board.IsMakeup || board.DayTag != "按第12周周三上课" || board.IsWeekend)
                    throw new Exception("大屏没有标出调休补课：" + board.DayTag + "/IsWeekend=" + board.IsWeekend);
                if (board.WeekLine != "按第12周周三上课")
                    throw new Exception("调休日大屏还在显示今天的周次星期：" + board.WeekLine);
                var subj = board.Rows.Where(r => r.Period > 0).SelectMany(r => r.Lessons)
                                     .Select(l => l.Subject ?? "").ToList();
                if (!subj.Contains("语文") || subj.Contains("体育"))
                    throw new Exception("周六没有按周三的课表排：" + string.Join(",", subj));

                // ⑧ 沿用调课：周三第 1 节的代课要跟着搬到周六
                var ovs = new List<LessonOverride>
                {
                    new() { ClassName = "测试班", DayIdx = 2, Period = 1, Subject = "美术",
                            Teacher = "王老师", Type = "代", Source = "manual" }
                };
                var board2 = BoardBuilder.Build("测试班", sem, sf, sat, ovs);
                string? first = board2.Rows.FirstOrDefault(r => r.Period == 1)?.Lessons.FirstOrDefault()?.Subject;
                if (first != "美术") throw new Exception("补课日没有沿用那天的调课：" + first);

                var strictSf = new ScheduleFile
                {
                    Calendar = new List<SchoolCalendarRule>
                    {
                        new() { Date = D(sat), AsDate = D(wed), InheritOverrides = false }
                    }
                };
                strictSf.Classes.Add(cls);
                var board3 = BoardBuilder.Build("测试班", sem, strictSf, sat, ovs);
                string? first3 = board3.Rows.FirstOrDefault(r => r.Period == 1)?.Lessons.FirstOrDefault()?.Subject;
                if (first3 != "语文") throw new Exception("取消沿用调课后还是套了调课：" + first3);

                // ⑨ 放假日：不排课，只给一句「今日放假」
                var offBoard = BoardBuilder.Build("测试班", sem, sf, mon, new List<LessonOverride>());
                if (!offBoard.IsOff) throw new Exception("放假日没标出来");
                if (!offBoard.Rows.SelectMany(r => r.Lessons).Any(l => (l.Subject ?? "").Contains("放假")))
                    throw new Exception("放假日没有「今日放假」提示");

                // ⑩ 周视图：周六列标「按12周周三」、周日照常放假；两列的格子要各存各的（按列号取，不能按 DayIdx）
                var week = WeekBuilder.BuildWeek("测试班", sem, sf, new List<LessonOverride>(), new DateTime(2026, 11, 16));
                if (!week.DayHeaders[5].Contains("按12周周三"))
                    throw new Exception("周视图周六列没标周次：" + week.DayHeaders[5]);
                if (!week.DayHeaders[6].Contains("放假"))
                    throw new Exception("周视图没标放假：" + week.DayHeaders[6]);
                var wedCell = week.Cells.FirstOrDefault(c => c.Col == 2 && c.Period == 1);
                var satCell = week.Cells.FirstOrDefault(c => c.Col == 5 && c.Period == 1);
                if (wedCell == null || satCell == null || satCell.Subject != "语文" || satCell.DayIdx != 2)
                    throw new Exception("周视图周六列没有按周三的课表：" + satCell?.Subject);
                if (!satCell.IsMakeupColumn) throw new Exception("周视图周六列没标成补课列（会被误当成周三那列编辑）");
                if (week.Cells.FirstOrDefault(c => c.Col == 6 && c.Period == 1)?.HasLesson == true)
                    throw new Exception("放假日那列还在排课");

                return new SchoolCalendarWindow();
            });

            // r133 入口：数据管理页有「校历 / 调休」按钮，日期切换窗有直达入口
            Check("r133 校历入口与权限", () =>
            {
                var w = new MainWindow();
                if (w.BtnSchoolCalendar == null) throw new Exception("数据管理页缺「校历 / 调休」按钮");
                var ds = new DateSwitchWindow(DateTime.Today);
                var btn = ds.FindName("BtnCalendar") as System.Windows.Controls.Button;
                if (btn == null) throw new Exception("日期切换窗缺「校历 / 调休」直达入口");
                ds.Close();
                return w;
            });

            // r135 课表作息：一份 Bell 同时服务个人用 / 单班用 / 学校统一用。
            // 断言全在内存里造 ScheduleFile，不碰真实课表；最后返回作息窗口供框架收尾。
            Check("r135 课表作息逐节解析与优先级", () =>
            {
                string sem = new DateTime(2026, 9, 1).ToString("yyyy-MM-dd");
                var mon = new DateTime(2026, 11, 23);
                var sf = new ScheduleFile();
                var cls = new ClassSchedule { ClassName = "七年级1班" };
                sf.Classes.Add(cls);
                var l1 = new Lesson { Period = 1, Subject = "语文", TimeRange = "08:20-09:05" };
                var l5 = new Lesson { Period = 5, Subject = "体育", TimeRange = "14:00-14:45" };

                // ① 没设 Bell → 旧路径（导入时的逐课副本）
                if (BellResolver.ResolveTimeRange(sf, cls, l1) != "08:20-09:05")
                    throw new Exception("Bell 为空时没有走旧的 Lesson.TimeRange");

                var bell = new BellSchedule
                {
                    Name = "诊断全校作息", UpdatedAt = DateTime.Now, UpdatedFrom = "diag",
                    Periods = new List<PeriodTime> { new() { Index = 1, TimeRange = "08:00-08:45" } },
                    FixedSlots = new List<FixedSlot> { new() { Name = "全校大课间", AfterPeriod = 1, TimeRange = "08:45-09:00" } }
                };
                sf.Bell = bell;

                // ② 有 Bell 且这一节有值 → Bell 优先
                if (BellResolver.ResolveTimeRange(sf, cls, l1) != "08:00-08:45")
                    throw new Exception("Bell 没有覆盖第 1 节的时间");

                // ③ Bell 没有第 5 节 → 逐节回退，不是整体切换
                if (BellResolver.ResolveTimeRange(sf, cls, l5) != "14:00-14:45")
                    throw new Exception("Bell 缺第 5 节时没有逐节回退");

                // ④ HH:MM 是占位符，不能当成有效时间
                bell.Periods.Add(new PeriodTime { Index = 2, TimeRange = "HH:MM-HH:MM" });
                if (BellResolver.ResolveTimeRange(sf, cls, new Lesson { Period = 2, TimeRange = "09:10-09:55" }) != "09:10-09:55")
                    throw new Exception("HH:MM 占位符被当成有效时间");

                // ⑤ 手动班保留 r129 语义，不受 Bell 影响
                cls.Origin = "Manual";
                if (BellResolver.ResolveTimeRange(sf, cls, l1) != "08:20-09:05")
                    throw new Exception("手动班被 Bell 覆盖了");
                cls.Origin = "";

                // ⑥ 公共时间：显式设置的 Bell 优先于二一 FixedByGrade
                sf.FixedByGrade["七年级"] = new List<FixedSlot>
                {
                    new() { Name = "二一大课间", AfterPeriod = 1, TimeRange = "09:50-10:10" }
                };
                var fx = BoardBuilder.ResolveFixedSlots(sf, "七年级1班");
                if (fx == null || fx.Count == 0 || fx[0].Name != "全校大课间")
                    throw new Exception("Bell 的公共时间没有优先于二一 FixedByGrade");

                // ⑦ Bell 为空 → 二一兜底
                sf.Bell = null;
                fx = BoardBuilder.ResolveFixedSlots(sf, "七年级1班");
                if (fx == null || fx.Count == 0 || fx[0].Name != "二一大课间")
                    throw new Exception("Bell 为空时没有回退到二一 FixedByGrade");

                // ⑧ 两者都空 → null，绝不再冒出内置初中的大课间（r135 修的幽灵数据）
                sf.FixedByGrade.Clear();
                if (BoardBuilder.ResolveFixedSlots(sf, "七年级1班") != null)
                    throw new Exception("没有作息时仍冒出了本机内置模板的大课间");

                // ⑨ 大屏：逐节读 Bell，缺的那节回退
                sf.Bell = bell;
                cls.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { l1 }, [5] = new List<Lesson> { l5 }
                };
                var board = BoardBuilder.Build("七年级1班", sem, sf, mon, new List<LessonOverride>());
                if (board.Rows.FirstOrDefault(r => r.Period == 1)?.TimeRange != "08:00-08:45")
                    throw new Exception("大屏第 1 节时间不是 Bell 的值");
                if (board.Rows.FirstOrDefault(r => r.Period == 5)?.TimeRange != "14:00-14:45")
                    throw new Exception("大屏第 5 节没有逐节回退");

                // ⑩ 周视图与悬浮窗同一套解析（不能各读各的字段）
                var week = WeekBuilder.BuildWeek("七年级1班", sem, sf, new List<LessonOverride>(), mon);
                var cell1 = week.Cells.FirstOrDefault(c => c.Col == 0 && c.Period == 1);
                if (cell1?.TimeRange != "08:00-08:45") throw new Exception("周视图时间没有走 Bell");

                // ⑪ 导入表没时间但 Bell 有 → 上课自动隐藏不再永久失效
                //    （周末用一条校历规则把今天映射成周一，保证任何日子都能断）
                var now = DateTime.Now;
                var sf2 = new ScheduleFile
                {
                    Calendar = new List<SchoolCalendarRule>
                    {
                        new() { Date = now.ToString("yyyy-MM-dd"), Kind = CalendarKind.Makeup,
                                AsDate = new DateTime(2026, 11, 16).ToString("yyyy-MM-dd") }
                    },
                    Bell = new BellSchedule
                    {
                        Periods = new List<PeriodTime> { new() { Index = 1, TimeRange = "00:00-23:59" } }
                    }
                };
                var cls2 = new ClassSchedule { ClassName = "七年级2班" };
                cls2.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Period = 1, TimeRange = "" } }
                };
                sf2.Classes.Add(cls2);
                if (!WeekHelper.IsClassTimeNow(sf2, "七年级2班", sem))
                    throw new Exception("导入表无时间但 Bell 有时，上课自动隐藏仍失效");

                return null;   // r142：作息编辑已并入手动录入窗口的弹窗，这里不再返回窗口
            });

            // r142：作息弹窗「保存并设为全校作息」要能把节次 + 公共时间整份写进课表
            Check("r142 作息弹窗写入全校作息", () =>
            {
                UserSession.SignIn(new AppUser { Username = "diag-bell", Role = UserRole.Admin });
                var p = new PeriodTemplate
                {
                    Name = "诊断作息",
                    Periods = new List<PeriodTime> { new() { Index = 1, TimeRange = "08:00-08:45" } },
                    FixedSlots = new List<FixedSlot> { new() { Name = "大课间", AfterPeriod = 1, TimeRange = "08:45-09:00" } }
                };
                var dlg = new PeriodTemplateDialog(p, canManageData: true);
                if (!dlg.DiagCanApplyToSchool)
                    throw new Exception("管理员打开作息弹窗时「保存并设为全校作息」应当可用");
                dlg.DiagCommitToSchool();
                if (!dlg.AppliedToSchool) throw new Exception("点了「保存并设为全校作息」却没标记已应用");

                var bell = Store.LoadSchedule()?.Bell;
                if (bell == null || bell.Name != "诊断作息" || bell.Periods.Count != 1 || bell.FixedSlots.Count != 1)
                    throw new Exception("设为全校作息后 Bell 没有完整写入课表");
                if (bell.Periods[0].TimeRange != "08:00-08:45" || bell.FixedSlots[0].Name != "大课间")
                    throw new Exception("Bell 内容与弹窗里的作息不一致");

                // 没有权限的人：按钮必须禁用，别等到点了才弹"无权"
                var dlg2 = new PeriodTemplateDialog(new PeriodTemplate { Name = "只读" }, canManageData: false);
                if (dlg2.DiagCanApplyToSchool)
                    throw new Exception("没有数据管理权限时「保存并设为全校作息」应当禁用");
                return null;
            });

            // r136 课表基础数据维护：提取 / 三态合并 / 冲突 / 包合并 / 学期名（纯内存，不落盘）
            Check("r136 基础数据提取与三态合并", () =>
            {
                var sf = new ScheduleFile { SourceFile = "2026学年第一学期 全校总课表.xlsx" };
                var c1 = new ClassSchedule { ClassName = "七年级(1)班" };
                c1.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Period = 1, Subject = "语文", Teacher = "张老师", TimeRange = "08:00-08:45" } },
                    [2] = new List<Lesson> { new() { Period = 2, Subject = "数学", Teacher = "李老师", TimeRange = "08:55-09:40" } }
                };
                var c2 = new ClassSchedule { ClassName = "七年级(2)班" };
                c2.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Period = 1, Subject = "语文", Teacher = "张老师", TimeRange = "08:00-08:45" } }
                };
                sf.Classes.Add(c1); sf.Classes.Add(c2);

                // ① 班级 / 科目 / 教师 从课表提取并计数
                var r = BaseDataExtractor.Extract(sf);
                if (r.Classes.Count != 2) throw new Exception("班级提取数不对：" + r.Classes.Count);
                if (r.Subjects.Count != 2) throw new Exception("科目去重不对：" + r.Subjects.Count);
                if (r.Teachers.Count != 2) throw new Exception("教师提取不对：" + r.Teachers.Count);
                var zhang = r.Teachers.FirstOrDefault(x => x.Name == "张老师");
                if (zhang == null || zhang.Count != 2) throw new Exception("张老师出现次数应为 2");

                // ② 学期名建议值从来源文件名猜出
                if (r.SuggestedTermName == null || !r.SuggestedTermName.Contains("学年"))
                    throw new Exception("学期名建议值没从标题猜出：" + r.SuggestedTermName);

                // ③ 节次时间提取（同节同值不冲突）
                if (r.BellPeriods.Count != 2 || r.Conflicts.Count != 0)
                    throw new Exception("节次时间提取/冲突判定不对");
                if (r.BellPeriods.First(x => x.Index == 1).TimeRange != "08:00-08:45")
                    throw new Exception("第1节时间提取错");

                // ④ 占位符 HH:MM 不当作时间：单独构造一份只有占位符的课表
                var sfPh = new ScheduleFile();
                var cph = new ClassSchedule { ClassName = "占位班" };
                cph.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [3] = new List<Lesson> { new() { Period = 3, Subject = "语文", TimeRange = "HH:MM-HH:MM" } }
                };
                sfPh.Classes.Add(cph);
                var r2 = BaseDataExtractor.Extract(sfPh);
                if (r2.BellPeriods.Count != 0)
                    throw new Exception("占位符 HH:MM 被当成真实节次时间");

                // ⑤ 同一节两种时间 → 记冲突，不自动取值
                c2.Data[0][1][0].TimeRange = "08:10-08:55";
                var r3 = BaseDataExtractor.Extract(sf);
                var conflict = r3.Conflicts.FirstOrDefault(x => x.Key == "第1节");
                if (conflict == null || conflict.Candidates.Count != 2)
                    throw new Exception("同节两种时间没记成冲突");
                if (r3.BellPeriods.Any(x => x.Index == 1))
                    throw new Exception("有冲突还自动定了第1节时间");
                // 未裁决前 Apply 必须拒绝
                bool rejected = false;
                try { BaseDataExtractor.Apply(sf, r3); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("冲突未裁决却允许 Apply");
                conflict.Chosen = conflict.Candidates[0];
                BaseDataExtractor.Apply(sf, r3);
                if (sf.Bell == null || sf.Bell.Periods.Count == 0)
                    throw new Exception("裁决后 Apply 没写入 Bell");
                c2.Data[0][1][0].TimeRange = "08:00-08:45";

                // ⑤b 「从课表节次自动填入」不能因为同节多值就整表空：按出现次数取多数值
                var sfVote = new ScheduleFile();
                for (int i = 0; i < 3; i++)
                {
                    var vc = new ClassSchedule { ClassName = "投票班" + i };
                    vc.Data[0] = new Dictionary<int, List<Lesson>>
                    {
                        [1] = new List<Lesson> { new() { Period = 1, TimeRange = i == 2 ? "08:10-08:55" : "08:00-08:45" } }
                    };
                    sfVote.Classes.Add(vc);
                }
                var suggested = BaseDataExtractor.SuggestBellPeriods(sfVote, out int contested);
                if (suggested.Count != 1 || suggested[0].TimeRange != "08:00-08:45" || contested != 1)
                    throw new Exception("自动填入没有按多数班级取节次时间：" + string.Join(",", suggested.Select(x => x.TimeRange)) + "/分歧=" + contested);
                if (BaseDataExtractor.SuggestBellPeriods(sfPh, out _).Count != 0)
                    throw new Exception("占位符时间被自动填入了");

                // ⑥ 三态：手动项保留、排除项（墓碑）不复活、改名旧名不复活
                var sf2 = new ScheduleFile();
                var c = new ClassSchedule { ClassName = "九年级(1)班" };
                c.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Period = 1, Subject = "英语", Teacher = "王老师" } }
                };
                sf2.Classes.Add(c);
                sf2.ClassEntries.Add(new BaseDataEntry { Name = "我手动加的班", Origin = BaseDataOrigin.Manual });
                sf2.ClassEntries.Add(new BaseDataEntry { Name = "已排除的班", Origin = BaseDataOrigin.Excluded, Tombstoned = true });
                sf2.ClassEntries.Add(new BaseDataEntry { Name = "旧班名", Origin = BaseDataOrigin.Excluded, Tombstoned = true, RenamedTo = "九年级(1)班" });
                var r4 = BaseDataExtractor.Extract(sf2);
                if (!r4.Classes.Any(x => x.Name == "我手动加的班" && x.Origin == BaseDataOrigin.Manual))
                    throw new Exception("手动项没保留");
                if (!r4.Classes.Any(x => x.Name == "已排除的班" && x.Tombstoned))
                    throw new Exception("排除项墓碑丢了");
                if (r4.Classes.Count(x => x.Name == "九年级(1)班") > 1)
                    throw new Exception("改名旧名被自动项复活，导致重复");

                // ⑦ 包 Apply 的 MergeBaseDataFrom：手动 / 排除项并回，同名自动项不覆盖手动
                var pkg = new ScheduleFile();
                pkg.ClassEntries.Add(new BaseDataEntry { Name = "七年级(1)班", Origin = BaseDataOrigin.Auto });
                int kept = pkg.MergeBaseDataFrom(sf2);
                if (kept <= 0) throw new Exception("包合并没有并回任何手动项");
                if (!pkg.ClassEntries.Any(x => x.Name == "我手动加的班" && x.Origin == BaseDataOrigin.Manual))
                    throw new Exception("包 Apply 把手动清单项冲掉了");

                return null;   // r142：基础数据维护窗口已删除，这批断言只测纯逻辑
            });

            // r142：基础数据维护窗口删除后 —— 开学日必须回到标题栏且真的能改，旧入口不许残留
            Check("r142 开学日回到标题栏、旧入口清除", () =>
            {
                UserSession.SignIn(new AppUser { Username = "diag-admin2", Role = UserRole.Admin });
                var mw = new MainWindow();

                var dp = mw.FindName("DpSemester") as System.Windows.Controls.DatePicker;
                if (dp == null) throw new Exception("顶栏缺开学日期 DatePicker");
                if (!dp.IsEnabled) throw new Exception("有数据管理权限时开学日期必须可编辑（原来被 XAML 写死禁用了）");
                if (mw.FindName("LnkEditSemester") != null)
                    throw new Exception("开学日期已能直接改，顶栏那个「修改」文字链应已删除");
                if (mw.FindName("BtnBaseData") != null)
                    throw new Exception("「课表基础数据维护」按钮应已删除");

                // 不只是"看起来能改"：改一下，确认真写进了设置
                string before = Store.LoadSettings().SemesterStartMonday;
                dp.SelectedDate = new DateTime(2026, 9, 1);
                if (Store.LoadSettings().SemesterStartMonday != "2026-09-01")
                    throw new Exception("改开学日期没落盘：" + Store.LoadSettings().SemesterStartMonday);

                // 还原，别把诊断日期留在设置里
                dp.SelectedDate = DateTime.TryParse(before, out var back) ? back : (DateTime?)null;
                mw.Close();

                // 没有数据管理权限的角色：禁用而不是隐藏（位置固定，用户不会以为功能没了）
                // ★ 断言"跟权限联动"而不是"某个角色必定禁用"：本机 users.json 可以给任意角色授权，
                //   写死角色默认值会在别人改过策略的机器上误报。
                UserSession.SignIn(new AppUser { Username = "diag-subject", Role = UserRole.SubjectTeacher });
                var mw2 = new MainWindow();
                // 显式刷一遍权限：诊断窗口不 Show，构造期的自动登录 / 游客分支不一定走全
                typeof(MainWindow).GetMethod("ApplyPermissions",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(mw2, null);
                var dp2 = mw2.FindName("DpSemester") as System.Windows.Controls.DatePicker;
                if (dp2 == null) throw new Exception("顶栏缺开学日期 DatePicker");
                if (dp2.IsEnabled != UserSession.CanManageData)
                    throw new Exception("换角色后开学日期可用性没跟着权限变（权限=" + UserSession.CanManageData
                                        + "，控件=" + dp2.IsEnabled + "）");
                mw2.Close();
                return null;
            });

            // r144：智能导入此前没有任何权限门 —— 旁边的「下载模板」锁着，主按钮却敞着。
            // 语义与「二一导入 · 总课表」「一键清除全部导入数据」一致：整班覆盖底表 = ManageData。
            Check("r144 智能导入并入「总课表 / 清除课表」权限", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Button Btn(MainWindow w, string name) =>
                    (Button)typeof(MainWindow).GetField(name, bf)!.GetValue(w)!;

                // ① 管理员：主按钮与「下载模板」都可用，且两者状态必须一致（同一权限组）
                UserSession.SignIn(new AppUser { Username = "diag-admin4", Role = UserRole.Admin });
                var mw = new MainWindow();
                typeof(MainWindow).GetMethod("ApplyPermissions", bf)!.Invoke(mw, null);
                var smart = Btn(mw, "BtnImportSmart");
                var dl = Btn(mw, "BtnDownloadTemplates");
                if (!smart.IsEnabled) throw new Exception("管理员都点不了智能导入");
                if (smart.IsEnabled != dl.IsEnabled)
                    throw new Exception("智能导入与「下载模板」没跟同一权限走（一个开一个关）");
                mw.Close();

                // ② 无数据管理权限：造一份"所有能力全关"的用户表。
                //    ★ 不能拿某个角色的出厂默认当断言依据 —— 本机 users.json 可以给任意角色授权。
                var book = new UserBook { Schema = 5 };
                book.Policies[UserRole.Teacher] = new RolePolicy();      // 9 项全关
                book.Users.Add(new AppUser { Username = "diag-nodata", Role = UserRole.Teacher });

                var mw2 = new MainWindow();
                UserSession.SignIn(book.Users[0], book);                 // 构造完再登录，免得被构造期流程覆盖
                typeof(MainWindow).GetMethod("ApplyPermissions", bf)!.Invoke(mw2, null);

                if (UserSession.CanManageData)
                    throw new Exception("诊断前提不成立：这份全关策略居然还有数据管理权");

                var smart2 = Btn(mw2, "BtnImportSmart");
                var dl2 = Btn(mw2, "BtnDownloadTemplates");
                if (smart2.IsEnabled)
                    throw new Exception("没有「总课表 / 清除课表」权限时，智能导入按钮仍然可点");
                if (dl2.IsEnabled)
                    throw new Exception("没有数据管理权限时「下载模板」仍然可点");
                if (string.IsNullOrWhiteSpace(smart2.ToolTip as string))
                    throw new Exception("被禁用的智能导入没有告诉用户为什么不能点");
                mw2.Close();

                // 收尾：把会话还给管理员，别把"全关"状态漏给后面的诊断
                UserSession.SignIn(new AppUser { Username = "diag", Role = UserRole.Admin });
                return null;
            });

            // r146：回执面板含机器名 / 部署信息，跟「下发设置」同为发布级查看权限。
            Check("r146 更新回执入口并入发布级权限", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Button Btn(MainWindow w, string name) =>
                    (Button)typeof(MainWindow).GetField(name, bf)!.GetValue(w)!;

                UserSession.SignIn(new AppUser { Username = "diag-admin6", Role = UserRole.Admin });
                var mw = new MainWindow();
                typeof(MainWindow).GetMethod("ApplyPermissions", bf)!.Invoke(mw, null);
                var b1 = Btn(mw, "BtnUpdateReceipts");
                var b2 = Btn(mw, "BtnPushSettings");
                if (!b1.IsEnabled || b1.IsEnabled != b2.IsEnabled)
                    throw new Exception("回执入口没有跟「下发设置」走同一权限");
                mw.Close();

                var book = new UserBook { Schema = 5 };
                book.Policies[UserRole.Teacher] = new RolePolicy();      // 全关
                book.Users.Add(new AppUser { Username = "diag-nopub", Role = UserRole.Teacher });
                var mw2 = new MainWindow();
                UserSession.SignIn(book.Users[0], book);
                typeof(MainWindow).GetMethod("ApplyPermissions", bf)!.Invoke(mw2, null);
                var denied = Btn(mw2, "BtnUpdateReceipts");
                if (denied.IsEnabled) throw new Exception("无发布权限时回执入口仍可点");
                if (string.IsNullOrWhiteSpace(denied.ToolTip as string))
                    throw new Exception("被禁用的回执入口没有说明原因");
                mw2.Close();

                UserSession.SignIn(new AppUser { Username = "diag", Role = UserRole.Admin });
                return null;
            });

            // r148：今日页四张卡，只汇总状态；卡片里的按钮回到原页面处理。
            Check("r148 今日页四卡片控件齐全且能刷新", () =>
            {
                var w = new MainWindow { InitialTabIndex = 9 };
                string[] names =
                {
                    "PillTodayTodo", "TxtTodayTodoPill", "TxtTodayTodoValue", "TxtTodayTodoDetail",
                    "PillTodayLesson", "TxtTodayLessonPill", "TxtTodayLessonValue", "TxtTodayLessonDetail",
                    "PillTodayUpdate", "TxtTodayUpdatePill", "TxtTodayUpdateValue", "TxtTodayUpdateDetail",
                    "PillTodayStatus", "TxtTodayStatusPill", "TxtTodayStatusValue", "TxtTodayStatusDetail",
                    "BtnTodayCheckUpdate", "BtnTodayReceipts", "BtnTodayPublish", "BtnTodayReminder"
                };
                foreach (string n in names)
                    if (w.FindName(n) == null) throw new Exception("今日页控件缺失：" + n);

                typeof(MainWindow).GetMethod("RefreshTodayPage",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(w, null);
                var todo = (System.Windows.Controls.TextBlock)w.FindName("TxtTodayTodoValue")!;
                var lesson = (System.Windows.Controls.TextBlock)w.FindName("TxtTodayLessonValue")!;
                var update = (System.Windows.Controls.TextBlock)w.FindName("TxtTodayUpdateValue")!;
                var status = (System.Windows.Controls.TextBlock)w.FindName("TxtTodayStatusValue")!;
                if (string.IsNullOrWhiteSpace(todo.Text) || string.IsNullOrWhiteSpace(lesson.Text)
                    || string.IsNullOrWhiteSpace(update.Text) || string.IsNullOrWhiteSpace(status.Text))
                    throw new Exception("刷新后四卡仍有空白主文案");
                w.Close();
                return null;
            });

            // r148：「立即检查更新」并入 ManageUpdate；今日页两颗按钮也按权限显隐。
            Check("r148 检查更新并入 ManageUpdate 权限门", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Button Btn(MainWindow w, string name) => (Button)typeof(MainWindow).GetField(name, bf)!.GetValue(w)!;

                UserSession.SignIn(new AppUser { Username = "diag-admin8", Role = UserRole.Admin });
                var admin = new MainWindow();
                typeof(MainWindow).GetMethod("ApplyPermissions", bf)!.Invoke(admin, null);
                if (!Btn(admin, "BtnUpdateCheck").IsEnabled) throw new Exception("管理员点不了立即检查更新");
                if (((Button)admin.FindName("BtnTodayCheckUpdate")!).Visibility != Visibility.Visible)
                    throw new Exception("管理员看不到今日页的检查更新按钮");
                if (((Button)admin.FindName("BtnTodayPublish")!).Visibility != Visibility.Visible)
                    throw new Exception("管理员看不到待办卡里的「发布内容」按钮");
                if (((Button)admin.FindName("BtnTodayReminder")!).Visibility != Visibility.Visible)
                    throw new Exception("今日页缺少「提醒设置」入口");
                admin.Close();

                var book = new UserBook { Schema = 5 };
                book.Policies[UserRole.Teacher] = new RolePolicy();      // 9 项全关
                book.Users.Add(new AppUser { Username = "diag-noupdate", Role = UserRole.Teacher });
                var deny = new MainWindow();
                UserSession.SignIn(book.Users[0], book);
                typeof(MainWindow).GetMethod("ApplyPermissions", bf)!.Invoke(deny, null);
                var b = Btn(deny, "BtnUpdateCheck");
                if (b.IsEnabled) throw new Exception("无 ManageUpdate 时立即检查更新仍可点");
                if (string.IsNullOrWhiteSpace(b.ToolTip as string))
                    throw new Exception("被禁用的检查更新没有说明原因");
                if (((Button)deny.FindName("BtnTodayCheckUpdate")!).Visibility != Visibility.Collapsed
                    || ((Button)deny.FindName("BtnTodayReceipts")!).Visibility != Visibility.Collapsed
                    || ((Button)deny.FindName("BtnTodayPublish")!).Visibility != Visibility.Collapsed)
                    throw new Exception("无权限时今日页按钮没有收起");

                // 第二道闸：绕过按钮直接调方法也要被挡住，且给出同一句拒绝说明
                typeof(MainWindow).GetMethod("ManualCheckUpdateAsync", bf)!.Invoke(deny, null);
                var status = (System.Windows.Controls.TextBlock)typeof(MainWindow).GetField("TxtStatus", bf)!.GetValue(deny)!;
                if (!string.Equals(status.Text, UserSession.DenyManageUpdate, StringComparison.Ordinal))
                    throw new Exception("直接触发检查更新没有走权限第二道闸：" + status.Text);
                deny.Close();

                UserSession.SignIn(new AppUser { Username = "diag", Role = UserRole.Admin });
                return null;
            });

            // r148：下发面板的危险项预览与统一确认文案（不落盘，纯读）。
            Check("r148 下发危险项预览与统一确认", () =>
            {
                var w = new SettingsPushWindow();
                var prev = w.FindName("TxtPushPreview") as System.Windows.Controls.TextBlock;
                if (prev == null) throw new Exception("下发面板缺少变更预览");
                typeof(SettingsPushWindow).GetMethod("UpdatePushPreview",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(w, null);
                if (!prev.Text.StartsWith("本次下发：", StringComparison.Ordinal)
                    || !prev.Text.Contains("相对本机：", StringComparison.Ordinal))
                    throw new Exception("变更预览内容不完整：" + prev.Text);

                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var ps = new PackageSettings { SourceGroupIncluded = true, CalendarIncluded = true };
                var reset = new SettingsPushService.RevResetRequest { Applied = true };
                var warns = (List<string>)typeof(SettingsPushWindow).GetMethod("CollectWarnings", bf)!
                    .Invoke(w, new object[] { ps, reset })!;
                // 只勾了来源 + 重置，两条必须都在；强制重推单独再验一次
                if (warns.Count != 2 || !warns.Any(x => x.Contains("更新来源")) || !warns.Any(x => x.Contains("代次")))
                    throw new Exception("危险项登记不全：" + string.Join(" | ", warns));
                ((System.Windows.Controls.CheckBox)w.FindName("ChkForce")!).IsChecked = true;
                var warns2 = (List<string>)typeof(SettingsPushWindow).GetMethod("CollectWarnings", bf)!
                    .Invoke(w, new object[] { ps, reset })!;
                if (warns2.Count != 3 || !warns2.Any(x => x.Contains("强制重新下发")))
                    throw new Exception("强制重推没有纳入统一确认：" + string.Join(" | ", warns2));
                w.Close();
                return null;
            });

            // r144：通知公告的「风格…」与「编辑…」里的主题/字号重复，删前者、字号并进编辑窗。
            Check("r144 便签管理去掉重复的风格入口", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var mw = new MainWindow { InitialTabIndex = 1 };
                if (mw.FindName("BtnStyleNotice") != null)
                    throw new Exception("「便签管理」里那个「风格…」按钮还在（与编辑窗里的风格重复）");
                if (mw.FindName("BtnEditNotice") is not Button edit || !edit.IsEnabled)
                    throw new Exception("通知公告的「编辑…」入口不见了或不可用");
                if (mw.FindName("BtnToggleNotice") == null)
                    throw new Exception("通知公告的显隐按钮不见了");
                mw.Close();
                return null;
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
            // 内置快捷：十二个胶囊 + 按角色重排的那条链（缺一个按钮这里就报出来，别等用户发现少了入口）
            Check("ScheduleWindow 内置快捷（十二个）", () =>
            {
                var w = new ScheduleWindow();
                if (w.BtnQaHomework == null || w.BtnQaMissing == null || w.BtnQaNote == null
                    || w.BtnQaCountdown == null || w.BtnQaDuty == null || w.BtnQaSettings == null
                    || w.BtnQaWeek == null || w.BtnQaPublish == null || w.BtnQaInfo == null
                    || w.BtnQaHand == null || w.BtnQaNotice == null || w.BtnQaBoard == null
                    || w.BuiltinWrap == null)
                    throw new Exception("内置快捷按钮缺失（板书 / 通知 / 黑板 等）");
                if (w.BuiltinWrap.Children.Count != QuickActionCatalog.All.Length)
                    throw new Exception("内置快捷按钮数量与目录不一致：" + w.BuiltinWrap.Children.Count);
                return w;
            });
            // r116：三个开关型胶囊 —— 板书按钮与标题栏共用一条切换链，状态回执事件接线正确
            Check("快捷 板书 / 通知 / 黑板 状态高亮接线", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);
                if (!w.DiagHandModeActive) throw new Exception("快捷板书没能把板书模式切起来");
                w.DiagSetHandMode(false);
                AppEvents.RaiseQuickToolsStateChanged(true, true);
                AppEvents.RaiseQuickToolsStateQuery();   // 无人订阅也不该抛
                return w;
            });
            // r118：智能导入 —— 现场造一份"传统单班"模板（星期纵列 + 节次横排 + 括号双行格），
            // 验证探测能认出布局、通用解析能拼回课时。不依赖任何外部样本文件，诊断自包含。
            Check("r118 智能导入：模板探测 + 通用解析", () =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), "cb_smart_import_" + Guid.NewGuid().ToString("N") + ".xlsx");
                try
                {
                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        var ws = wb.AddWorksheet("课程表");
                        ws.Cell(1, 1).Value = "课　　程　　表";
                        ws.Cell(3, 14).Value = "七(1)班";
                        ws.Cell(4, 1).Value = "时间　星期";
                        ws.Cell(5, 3).Value = "第1节"; ws.Cell(5, 5).Value = "第2节"; ws.Cell(5, 7).Value = "第3节";
                        ws.Cell(6, 3).Value = "8:10～8:50"; ws.Cell(6, 5).Value = "9:20～10:00"; ws.Cell(6, 7).Value = "10:10～10:50";
                        // 星期纵列：A7/A9/A11 各一个（节次横排在 R5），模拟真实老式单班表
                        ws.Cell(7, 1).Value = "星期一";
                        // 单双周格：上格"语文(王老师)"+下格"地理(孙老师)"都是完整科目+教师 → 应拆成 W1/W2
                        ws.Cell(7, 3).Value = "语文(王老师)"; ws.Cell(8, 3).Value = "地理(孙老师)";
                        // 全周格：上格只有科目、下格只有教师 → 应保持 W0 一门
                        ws.Cell(7, 5).Value = "数学"; ws.Cell(8, 5).Value = "李老师";
                        ws.Cell(7, 7).Value = "英语(张老师)"; ws.Cell(8, 7).Value = "张老师";
                        ws.Cell(9, 1).Value = "星期二";
                        ws.Cell(9, 3).Value = "历史(赵老师)"; ws.Cell(10, 3).Value = "赵老师";
                        ws.Cell(11, 1).Value = "星期三";
                        ws.Cell(11, 5).Value = "地理(孙老师)"; ws.Cell(12, 5).Value = "孙老师";
                        wb.SaveAs(tmp);
                    }
                    var probe = ScheduleCore.Services.ScheduleTemplateProbe.Probe(tmp);
                    if (!probe.Ok || probe.Candidates.Count == 0) throw new Exception("探测器没有返回候选布局");
                    var hints = probe.Best;
                    if (hints.PeriodOrientation != ScheduleTemplateProbe.PeriodOrientation.Horizontal)
                        throw new Exception("节次横排没认出来，实际=" + hints.PeriodOrientation);
                    var parsed = ScheduleCore.Services.ScheduleGeneralImporter.Parse(tmp, hints);
                    int lessons = 0, odd = 0, even = 0, full = 0;
                    foreach (var c in parsed.Schedule.Classes)
                        foreach (var d in c.Data.Values) foreach (var p in d.Values)
                            foreach (var ls in p)
                            {
                                lessons++;
                                if (ls.Week == 1) odd++; else if (ls.Week == 2) even++; else full++;
                            }
                    if (lessons < 2) throw new Exception("解析出的课时太少：" + lessons);
                    // r119：单双周双格必须拆成 W1/W2 两门不同科目；全周格仍是 W0 一门
                    if (odd < 1 || even < 1) throw new Exception($"单双周未拆出：单周={odd} 双周={even}");
                    if (full < 1) throw new Exception("全周课丢失");
                    // 单周那门应叫"语文"，双周那门应叫"地理"（上=单、下=双）
                    bool okSplit = false;
                    foreach (var c in parsed.Schedule.Classes)
                        foreach (var d in c.Data.Values) foreach (var p in d.Values)
                            if (p.Any(x => x.Week == 1 && x.Subject == "语文") && p.Any(x => x.Week == 2 && x.Subject == "地理"))
                                okSplit = true;
                    if (!okSplit) throw new Exception("单双周科目/顺序不对：期望上格语文=单周、下格地理=双周");
                    return new LoginWindow(); // Check 需要一个 Window，借一个无害的当壳
                }
                finally { try { File.Delete(tmp); } catch { } }
            });
            // r120：二一专用导入四合一 —— 按文件名智能预选类型（不弹窗，纯静态判定）
            Check("r120 二一导入类型预选", () =>
            {
                EryiImportKind Guess(string n)
                {
                    var arr = new[] { @"C:\x\" + n };
                    return EryiImportKindWindow.Recommend(arr, out _);
                }
                if (Guess("七年级全校总课程表.xlsx") != EryiImportKind.Base) throw new Exception("总课表识别错");
                if (Guess("2026-11-20临时总课表.xlsx") != EryiImportKind.Temp) throw new Exception("临时课表识别错");
                if (Guess("八年级(22)班-班级课程表.xlsx") != EryiImportKind.Single) throw new Exception("单班识别错");
                if (Guess("曾胜康-教师课程表.xlsx") != EryiImportKind.Teacher) throw new Exception("教师表识别错");
                return new LoginWindow();
            });
            // r121：手动录入 + 时间表/科目设置 —— 内置模板、周次三值、整班替换/追加、两窗口可构造。
            // 全程用副本数据目录、不写真实数据（SuppressWrites 由 --ui-check 统一置位）。
            Check("r121 手动录入与时间表设置", () =>
            {
                var t = ScheduleCore.Models.ScheduleTemplates.CreateWithBuiltIns();
                if (t.PeriodTemplates.Count < 3) throw new Exception("内置节次模板少于 3 套");
                if (t.DefaultPeriodTemplate == null || t.DefaultPeriodTemplate.Periods.Count == 0)
                    throw new Exception("默认节次模板无节次");
                if (t.SubjectLibraries.Count == 0 || t.DefaultSubjectLibrary == null
                    || t.DefaultSubjectLibrary.Items.Count == 0) throw new Exception("科目库预置为空");
                if (t.DefaultSubjectLibrary.Items.Any(x => !string.IsNullOrWhiteSpace(x.Teacher)))
                    throw new Exception("预置科目不应自带教师");
                // Week 语义：0 全周 / 1 单周 / 2 双周（Lesson 注释约定）
                var l = new ScheduleCore.Models.Lesson { Period = 1, Subject = "数学", Week = 2 };
                if (l.Week != 2) throw new Exception("Lesson.Week 双周值不对");

                // 整班替换/追加：模拟 SaveToClass 的合并语义，验证其他班不动、新班追加
                var sf = new ScheduleFile();
                sf.Classes.Add(new ClassSchedule { ClassName = "一班" });
                sf.Classes.Add(new ClassSchedule { ClassName = "二班" });
                var edited = new ClassSchedule { ClassName = "二班", Data = new() };
                int idx = sf.Classes.FindIndex(c => c.ClassName == edited.ClassName);
                if (idx >= 0) sf.Classes[idx] = edited; else sf.Classes.Add(edited);
                if (sf.Classes.Count != 2 || sf.Classes[0].ClassName != "一班" || sf.Classes[1] != edited)
                    throw new Exception("替换已有班级时其他班被改动");
                var edited2 = new ClassSchedule { ClassName = "三班" };
                idx = sf.Classes.FindIndex(c => c.ClassName == edited2.ClassName);
                if (idx >= 0) sf.Classes[idx] = edited2; else sf.Classes.Add(edited2);
                if (sf.Classes.Count != 3 || !sf.Classes.Any(c => c.ClassName == "三班"))
                    throw new Exception("新班级未追加");

                // 录入窗口能构造（不 Show，不弹窗）
                var manual = new ManualScheduleWindow(sf, t);
                manual.Close();
                return new LoginWindow();
            });
            // r122 / r142：录入窗口根 Grid 三行各就各位 + 新顶栏（6 列、下拉垂直居中）
            // 原「模板名称输入光标不跳」已随基础数据维护窗口删除：那段测的是旧窗口的列表重建，
            // 现在作息编辑只有单选一份、弹窗内不重建列表，问题是结构性消失而非被绕过。
            Check("r122 录入窗口布局与顶栏对齐", () =>
            {
                var sf2 = new ScheduleFile();
                sf2.Classes.Add(new ClassSchedule { ClassName = "一班" });
                var manual2 = new ManualScheduleWindow(sf2, ScheduleTemplates.CreateWithBuiltIns());
                var root = (System.Windows.Controls.Grid)manual2.Content;
                // r142：只剩三行 —— 0 顶栏 / 1 主体 / 2 底栏（原来那两行长说明收进了底栏状态条）
                if (root.RowDefinitions.Count != 3)
                    throw new Exception("录入窗口根 Grid 行数不对：" + root.RowDefinitions.Count);
                int[] wantRows = { 0, 1, 2 };
                for (int i = 0; i < wantRows.Length; i++)
                    if (System.Windows.Controls.Grid.GetRow(root.Children[i]) != wantRows[i])
                        throw new Exception("录入窗口第 " + i + " 个区块不在第 " + wantRows[i] + " 行（底部栏与顶栏可能重叠）");
                var toolbar = root.Children[0] as System.Windows.Controls.Grid
                    ?? throw new Exception("录入顶栏不是 Grid（WrapPanel 会错位）");
                // 学期名 / 班级标签 / 班级下拉 / 弹性 / 清空网格 / 删除该班
                if (toolbar.ColumnDefinitions.Count != 6)
                    throw new Exception("录入顶栏列数不是 6：" + toolbar.ColumnDefinitions.Count);
                foreach (var cb in toolbar.Children.OfType<System.Windows.Controls.ComboBox>())
                    if (cb.VerticalAlignment != VerticalAlignment.Center)
                        throw new Exception("录入顶栏下拉框未垂直居中");
                manual2.Close();
                return new LoginWindow();
            });
            // r123：科目优先批量录入 —— 单/双门转换、现有课表提取合并、自动铺分散、窗口可构造
            Check("r123 科目配对批量录入", () =>
            {
                var single = new SubjectPair { Subject = "数学", Teacher = "张老师", Week = 0, WeeklyPeriods = 5 };
                var one = single.ToLessons(3);
                if (one.Count != 1 || one[0].Period != 3 || one[0].Week != 0 || one[0].Subject != "数学")
                    throw new Exception("单门配对转 Lesson 错");
                var dual = new SubjectPair { TwoLessons = true, Subject = "生物", Teacher = "樊老师", Subject2 = "地理", Teacher2 = "梁老师" };
                var two = dual.ToLessons(2);
                if (two.Count != 2 || two[0].Week != 1 || two[1].Week != 2 || two[0].Subject != "生物" || two[1].Subject != "地理")
                    throw new Exception("双门配对应转 W1/W2 两门");

                var data = new Dictionary<int, Dictionary<int, List<Lesson>>>();
                void Put(int day, List<Lesson> lessons)
                {
                    if (!data.TryGetValue(day, out var dm)) data[day] = dm = new Dictionary<int, List<Lesson>>();
                    dm[1] = lessons;
                }
                Put(0, new List<Lesson> { new Lesson { Period = 1, Subject = "生物", Teacher = "樊老师", Week = 1 }, new Lesson { Period = 1, Subject = "地理", Teacher = "梁老师", Week = 2 } });
                Put(1, new List<Lesson> { new Lesson { Period = 1, Subject = "生物", Teacher = "樊老师", Week = 1 }, new Lesson { Period = 1, Subject = "地理", Teacher = "梁老师", Week = 2 } });
                Put(2, new List<Lesson> { new Lesson { Period = 1, Subject = "数学", Teacher = "张老师", Week = 0 } });
                var extracted = ManualScheduleWindow.ExtractPairs(data);
                if (extracted.Count != 2) throw new Exception("提取去重错：期望 2 条，实际 " + extracted.Count);
                var gotDual = extracted.FirstOrDefault(p => p.TwoLessons);
                if (gotDual == null || gotDual.Subject != "生物" || gotDual.Subject2 != "地理")
                    throw new Exception("同格 W1+W2 没合并成双门配对");

                var empties = new List<(int day, int period)>();
                for (int p = 1; p <= 3; p++) for (int d = 0; d < 5; d++) empties.Add((d, p));
                var chosen = ManualScheduleWindow.PickSpread(empties, 5);
                if (chosen.Count != 5 || chosen.Select(x => x.day).Distinct().Count() != 5)
                    throw new Exception("自动铺没有按天分散（5 节应铺到 5 天）");

                var sf3 = new ScheduleFile();
                sf3.Classes.Add(new ClassSchedule { ClassName = "一班" });
                var t3 = ScheduleTemplates.CreateWithBuiltIns();
                var w3 = new ManualScheduleWindow(sf3, t3);
                var bf3 = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var lb = typeof(ManualScheduleWindow).GetField("_lbPairs", bf3)!.GetValue(w3);
                if (lb == null) throw new Exception("录入窗口缺少任课清单列表");
                w3.Close();
                var pe = new PairEditorWindow(null, t3.DefaultSubjectLibrary);
                pe.Close();
                return new LoginWindow();
            });
            // r124：公共时间（模板/大屏）+ 任课表导入 + 4 种导入模板可被探测器识别
            Check("r124 公共时间与导入模板", () =>
            {
                var t = ScheduleTemplates.CreateWithBuiltIns();
                var junior = t.PeriodTemplates.First(p => p.Name.Contains("初中"));
                if (!junior.FixedSlots.Any(f => f.Name == "大课间")) throw new Exception("内置模板缺大课间");
                if (junior.FixedSlots.Any(f => f.AfterPeriod < 0 || f.AfterPeriod > junior.Periods.Count))
                    throw new Exception("公共时间 AfterPeriod 越界");

                // 旧版 schedule_templates.json 没有 FixedSlots 字段：内置模板读取时应补回预置
                var loadedBuiltIn = Store.LoadScheduleTemplates().PeriodTemplates.FirstOrDefault(p => p.IsBuiltIn);
                if (loadedBuiltIn != null && loadedBuiltIn.FixedSlots.Count == 0)
                    throw new Exception("内置模板「" + loadedBuiltIn.Name + "」的公共时间没补上");

                // 公共时间插入顺序：第1节 → 大课间 → 第2节，且固定行 Period=0
                var body = new List<PeriodRow>
                {
                    new PeriodRow { Period = 1, PeriodText = "第1节" },
                    new PeriodRow { Period = 2, PeriodText = "第2节" }
                };
                var slots = new List<FixedSlot> { new() { Name = "大课间", AfterPeriod = 1, TimeRange = "09:50-10:10" } };
                var withFixed = BoardBuilder.InsertFixedSlots(body, slots, 0);
                if (withFixed.Count != 3) throw new Exception("公共时间插入后行数不对：" + withFixed.Count);
                if (!withFixed[1].IsFixed || withFixed[1].PeriodText != "大课间" || withFixed[1].Period != 0)
                    throw new Exception("大课间没插在第1节之后（或 Period 不是 0）");

                // 任课表模板 → 导入往返
                var subjectTmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb_subject_" + Guid.NewGuid().ToString("N") + ".xlsx");
                try
                {
                    SubjectTableImporter.WriteTemplate(subjectTmp);
                    var imported = SubjectTableImporter.Import(subjectTmp);
                    if (imported.Count < 3) throw new Exception("任课表导入条数不对：" + imported.Count);
                    var yw = imported.FirstOrDefault(p => p.Subject == "语文");
                    if (yw == null || yw.Teacher != "张老师" || yw.WeeklyPeriods != 5)
                        throw new Exception("任课表字段解析错");
                }
                finally { try { System.IO.File.Delete(subjectTmp); } catch { } }

                // 4 种导入模板都必须能被智能探测器识别
                var kinds = new[]
                {
                    ImportTemplateWriter.TemplateKind.Base,
                    ImportTemplateWriter.TemplateKind.TempBase,
                    ImportTemplateWriter.TemplateKind.SingleClass,
                    ImportTemplateWriter.TemplateKind.Teacher
                };
                foreach (var kind in kinds)
                {
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb_tpl_" + Guid.NewGuid().ToString("N") + ".xlsx");
                    try
                    {
                        ImportTemplateWriter.Write(tmp, kind);
                        var pr = ScheduleTemplateProbe.Probe(tmp);
                        if (!pr.Ok || pr.Best.Confidence < 40)
                            throw new Exception(kind + " 模板未被识别（置信度 " + (pr.Ok ? pr.Best.Confidence : 0) + "）");
                        if (kind == ImportTemplateWriter.TemplateKind.Teacher && !pr.Best.IsTeacherSchedule)
                            throw new Exception("教师模板未被识别为教师课表");
                        if (kind == ImportTemplateWriter.TemplateKind.SingleClass
                            && pr.Best.MainDimension != ScheduleTemplateProbe.MainDimension.Single)
                            throw new Exception("单班模板未被识别为单班");
                    }
                    finally { try { System.IO.File.Delete(tmp); } catch { } }
                }

                var pe2 = new PairEditorWindow(null, t.DefaultSubjectLibrary);
                pe2.Close();
                var pick = new ImportTemplatePickWindow();
                pick.Close();
                return new LoginWindow();
            });
            // r125：格子编辑器按课程设置预填 + 单双周两门 / 班级课程设置自动保存 / 拖动移动交换 / 方案下拉
            Check("r125 录入窗口四项优化", () =>
            {
                var lib = ScheduleTemplates.CreateWithBuiltIns().DefaultSubjectLibrary;
                var pairs = new List<SubjectPair>
                {
                    new() { Subject = "语文", Teacher = "张老师", Location = "1班教室", Week = 0, WeeklyPeriods = 5 },
                    new() { TwoLessons = true, Subject = "生物", Teacher = "樊老师", Location = "生物室",
                            Subject2 = "地理", Teacher2 = "梁老师", Location2 = "地理室", WeeklyPeriods = 2 }
                };

                // ① 按科目找配对：命中 Subject2 也算
                if (SubjectPair.FindBySubject(pairs, " 语文 ")?.Teacher != "张老师")
                    throw new Exception("按科目找配对失败（应忽略首尾空格）");
                if (SubjectPair.FindBySubject(pairs, "地理")?.Subject2 != "地理")
                    throw new Exception("双门配对的第 2 门没被命中");

                // ② 打开编辑器选科目 → 自动预填教师/地点/周次（且可改）
                var ce = new CellEditorWindow(0, 2, new List<Lesson>(), lib, pairs);
                var bf2 = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                T Get<T>(string name) => (T)typeof(CellEditorWindow).GetField(name, bf2)!.GetValue(ce)!;
                var cbSubject = Get<System.Windows.Controls.ComboBox>("_cbSubject");
                var tbTeacher = Get<System.Windows.Controls.TextBox>("_txtTeacher");
                var tbLocation = Get<System.Windows.Controls.TextBox>("_txtLocation");
                var cbWeek = Get<System.Windows.Controls.ComboBox>("_cbWeek");

                cbSubject.SelectedItem = "语文";
                if (tbTeacher.Text != "张老师" || tbLocation.Text != "1班教室")
                    throw new Exception("选科目后没有预填教师/地点：" + tbTeacher.Text + "/" + tbLocation.Text);
                if (cbWeek.SelectedIndex != 0) throw new Exception("全周课程的周次没预填成全周");
                tbTeacher.Text = "李老师";   // 预填后可改
                if (tbTeacher.Text != "李老师") throw new Exception("预填后教师改不动");
                tbTeacher.Text = "张老师";

                // ③ 选「单双周（两门）」→ 第 2 门展开；提交产出 W1+W2 两门
                var sp2 = Get<System.Windows.Controls.StackPanel>("_secondPanel");
                if (sp2.Visibility != Visibility.Collapsed) throw new Exception("没选两门时第 2 门不该显示");
                cbWeek.SelectedIndex = 3;
                if (sp2.Visibility != Visibility.Visible) throw new Exception("选了单双周两门，第 2 门没展开");
                var cbSubject2 = Get<System.Windows.Controls.ComboBox>("_cbSubject2");
                var tbTeacher2 = Get<System.Windows.Controls.TextBox>("_txtTeacher2");
                cbSubject2.Text = "美术";
                tbTeacher2.Text = "王老师";
                var built = ce.BuildLessons();   // 走同一套校验/拼装逻辑（不真的弹对话框）
                if (built.Count != 2)
                    throw new Exception("两门只产出了 " + built.Count + " 条 Lesson");
                if (built[0].Week != 1 || built[1].Week != 2)
                    throw new Exception("两门的单/双周标记不对");
                if (built[1].Subject != "美术") throw new Exception("第 2 门科目丢了");
                ce.Close();

                // ④ 打开本来就是两门的格子 → 自动进入两门模式并回填
                var ce2 = new CellEditorWindow(1, 3, new List<Lesson>
                {
                    new() { Period = 3, Subject = "生物", Teacher = "樊老师", Week = 1 },
                    new() { Period = 3, Subject = "地理", Teacher = "梁老师", Week = 2 }
                }, lib, pairs);
                var cbWeek2 = (System.Windows.Controls.ComboBox)typeof(CellEditorWindow).GetField("_cbWeek", bf2)!.GetValue(ce2)!;
                var sp22 = (System.Windows.Controls.StackPanel)typeof(CellEditorWindow).GetField("_secondPanel", bf2)!.GetValue(ce2)!;
                if (cbWeek2.SelectedIndex != 3 || sp22.Visibility != Visibility.Visible)
                    throw new Exception("打开单双周两门的格子没有自动进入两门模式");
                ce2.Close();

                // ⑤ 课程设置按班级自动保存：写入 → 读回 → 换班不串
                var t5 = ScheduleTemplates.CreateWithBuiltIns();
                t5.UpsertSetup(new ClassSetup { ClassName = "一班", PeriodTemplateName = "初中 7 节", SavedAt = DateTime.Now, Pairs = pairs.Select(x => x.Clone()).ToList() });
                t5.UpsertSetup(new ClassSetup { ClassName = "二班", SavedAt = DateTime.Now, Pairs = new List<SubjectPair> { new() { Subject = "数学", Teacher = "陈老师" } } });
                if (t5.ClassSetups.Count != 2) throw new Exception("班级设置应为 2 份，实际 " + t5.ClassSetups.Count);
                var back = System.Text.Json.JsonSerializer.Deserialize<ScheduleTemplates>(
                    System.Text.Json.JsonSerializer.Serialize(t5))!;
                var one = back.FindSetup(" 一班 ");
                if (one == null || one.Pairs.Count != 2) throw new Exception("课程设置没有随 schedule_templates.json 往返");
                if (!one.Pairs.Any(x => x.TwoLessons && x.Subject2 == "地理")) throw new Exception("双门配对往返后丢了第 2 门");
                if (back.FindSetup("三班") != null) throw new Exception("不存在的班不该找到设置");

                // ⑥ 拖动：空格=移动，有课=交换，原格不能残留
                var data = new Dictionary<int, Dictionary<int, List<Lesson>>>
                {
                    [0] = new() { [1] = new List<Lesson> { new() { Period = 1, Subject = "语文", Week = 0 } } },
                    [1] = new() { [2] = new List<Lesson> { new() { Period = 2, Subject = "数学", Week = 0 } } }
                };
                if (ManualScheduleWindow.MoveOrSwap(data, (0, 1), (0, 2))) throw new Exception("拖到空格不该算交换");
                if (data[0].ContainsKey(1) || data[0][2][0].Subject != "语文") throw new Exception("拖到空格没把课移过去");
                if (!ManualScheduleWindow.MoveOrSwap(data, (0, 2), (1, 2))) throw new Exception("拖到有课的格子应算交换");
                if (data[0][2][0].Subject != "数学" || data[1][2][0].Subject != "语文")
                    throw new Exception("交换后两格内容不对");
                if (ManualScheduleWindow.MoveOrSwap(data, (4, 7), (0, 1))) throw new Exception("空格不该能拖出东西");

                // ⑦ 窗口：方案下拉在、顶栏 6 列（r142 瘦身后）
                var sf5 = new ScheduleFile();
                sf5.Classes.Add(new ClassSchedule { ClassName = "一班" });
                var w5 = new ManualScheduleWindow(sf5, ScheduleTemplates.CreateWithBuiltIns());
                var cbPlan = (System.Windows.Controls.ComboBox)typeof(ManualScheduleWindow).GetField("_cbPlan", bf2)!.GetValue(w5)!;
                if (cbPlan.Items.Count < 1 || cbPlan.Items[0] as string != "（不使用方案）")
                    throw new Exception("课表方案下拉没初始化");
                var w5toolbar = ((System.Windows.Controls.Grid)w5.Content).Children[0] as System.Windows.Controls.Grid;
                if (w5toolbar == null || w5toolbar.ColumnDefinitions.Count != 6)
                    throw new Exception("录入顶栏列数应为 6（学期名 + 班级 + 弹性 + 清空网格 + 删除该班）");
                // r137：课程设置「清空」按钮；下载模板为文字链 + 紧跟问号
                if (w5.DiagClearPairsButton == null || (string)w5.DiagClearPairsButton.Content != "清空")
                    throw new Exception("任课安排表缺「清空」按钮");
                if (w5.DiagDownloadTemplateLink == null
                    || (string)w5.DiagDownloadTemplateLink.Content != "下载模板"
                    || w5.DiagDownloadTemplateLink.Style == null)
                    throw new Exception("下载任课安排模板没改成文字链");
                if (w5.DiagDownloadTemplateHelp == null
                    || !w5.DiagDownloadTemplateHelp.PopupTitle.Contains("下载模板"))
                    throw new Exception("下载模板文字链后面缺问号说明");
                w5.Close();

                // ⑧ 只在课程设置里出现过的班（还没保存到班）也要能选到，否则下次调不回来
                var t6 = ScheduleTemplates.CreateWithBuiltIns();
                t6.UpsertSetup(new ClassSetup { ClassName = "三班", SavedAt = DateTime.Now });
                var sf6 = new ScheduleFile();
                sf6.Classes.Add(new ClassSchedule { ClassName = "一班" });
                var w6 = new ManualScheduleWindow(sf6, t6);
                var cbClass6 = (System.Windows.Controls.ComboBox)typeof(ManualScheduleWindow)
                    .GetField("_cbClass", bf2)!.GetValue(w6)!;
                if (!cbClass6.Items.OfType<string>().Contains("三班"))
                    throw new Exception("只在课程设置里的班没出现在班级下拉");
                w6.Close();
                return new LoginWindow();
            });
            // r139 / r142：模板三件套的落点 —— 作息与科目库在左栏设置区（点图标弹窗编辑），
            //               方案在手录窗口「课表方案」行；旧「时间表与科目设置」窗口与基础数据窗都已删除。
            Check("r142 手录窗口设置区与新落点", () =>
            {
                var sf7 = new ScheduleFile { TermName = "2026学年第一学期" };
                sf7.Classes.Add(new ClassSchedule { ClassName = "一班" });
                var w7 = new ManualScheduleWindow(sf7, ScheduleTemplates.CreateWithBuiltIns());

                var buttons = new List<string>();
                void Walk(System.Windows.DependencyObject node)
                {
                    foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node))
                    {
                        if (child is Button b && b.Content is string s) buttons.Add(s);
                        if (child is System.Windows.DependencyObject d) Walk(d);
                    }
                }
                Walk(w7);

                // ① 旧入口必须彻底消失（连旧名一起），否则用户还会去找那个窗口
                if (buttons.Any(t => t.Contains("时间表与科目设置"))) throw new Exception("还留着「时间表与科目设置」入口");
                if (buttons.Any(t => t.Contains("节次模板"))) throw new Exception("旧标签「节次模板」还在界面上");
                if (buttons.Any(t => t.Contains("删除方案"))) throw new Exception("「删除方案」应已改成图标按钮，不该再有文字按钮");

                // ② 四个导入入口仍在左栏，且按 r139 的新名称
                foreach (var want in new[] { "从科目库导入", "从现有课表提取", "导入任课安排", "下载模板" })
                    if (!buttons.Any(t => t.Contains(want))) throw new Exception("左栏缺少入口：" + want + "（现有：" + string.Join(" / ", buttons) + "）");

                // ③ 增删改仍留在列表下方
                foreach (var want in new[] { "新增", "编辑", "删除", "清空" })
                    if (!buttons.Any(t => t == want)) throw new Exception("任课安排表缺少按钮：" + want);

                var bf7 = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                object F(string name) => typeof(ManualScheduleWindow).GetField(name, bf7)!.GetValue(w7)!;

                // ④ 学期 / 课表版本名称回到最左上角，且回填的就是课表里的名称
                var tbTerm = (System.Windows.Controls.TextBox)F("_tbTermName");
                if (tbTerm.Text != "2026学年第一学期")
                    throw new Exception("左上角学期名没有回填 TermName：" + tbTerm.Text);

                // ⑤ 三个设置行都在左栏；作息 / 科目库 / 方案各一个图标动作按钮
                if (F("_cbPeriod") is not System.Windows.Controls.ComboBox
                    || F("_cbSubjectLib") is not System.Windows.Controls.ComboBox
                    || F("_cbPlan") is not System.Windows.Controls.ComboBox)
                    throw new Exception("设置区缺少作息 / 科目库 / 课表方案下拉");
                foreach (var f in new[] { "_btnEditPeriod", "_btnEditSubjectLib", "_btnDeletePlan" })
                {
                    var b = (Button)F(f);
                    if (b.Content is not System.Windows.Shapes.Path)
                        throw new Exception(f + " 不是图标按钮（应为 Path，不是文字）");
                }
                var delPlan = (Button)F("_btnDeletePlan");
                if (delPlan.IsEnabled) throw new Exception("没选方案时「删除方案」不该可用");

                // ⑥ 设置区必须排在任课安排表上方（左栏两行：0 设置 / 1 任课安排）
                var root7 = (System.Windows.Controls.Grid)w7.Content;
                var work7 = root7.Children.OfType<System.Windows.Controls.Grid>()
                    .FirstOrDefault(g => System.Windows.Controls.Grid.GetRow(g) == 1)
                    ?? throw new Exception("找不到主体区");
                var leftCol = work7.Children.OfType<System.Windows.Controls.Grid>().FirstOrDefault();
                if (leftCol == null || leftCol.RowDefinitions.Count != 2)
                    throw new Exception("左栏不是「设置区 + 任课安排表」两行");
                if (System.Windows.Controls.Grid.GetRow(leftCol.Children[0]) != 0
                    || System.Windows.Controls.Grid.GetRow(leftCol.Children[1]) != 1)
                    throw new Exception("设置区没有排在任课安排表上方");

                w7.Close();
                return new LoginWindow();
            });
            // r128：切班后网格必须等于「该班底表」
            //  ★ 真 bug：IsEditable 的 ComboBox 在 SelectionChanged 回调里 Text 还是上一个选中项，
            //    CurrentClassName 读 Text 就会一直显示上一个班 —— 用户看到的是"进去的课表和更新的课表不一致"。
            Check("r128 切班后网格等于该班底表", () =>
            {
                var sf8 = new ScheduleFile();
                for (int i = 1; i <= 3; i++)
                {
                    var cc = new ClassSchedule { ClassName = "切换测试班" + i };
                    cc.Data[0] = new Dictionary<int, List<Lesson>>
                    {
                        [1] = new List<Lesson> { new() { Period = 1, Subject = "科目" + i, Teacher = "老师" + i, Week = 0 } }
                    };
                    sf8.Classes.Add(cc);
                }
                var w8 = new ManualScheduleWindow(sf8, ScheduleTemplates.CreateWithBuiltIns());
                w8.Show();
                PumpMs(400);
                var bf8 = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var cb8 = (System.Windows.Controls.ComboBox)typeof(ManualScheduleWindow).GetField("_cbClass", bf8)!.GetValue(w8)!;
                for (int i = 2; i >= 1; i--)   // 倒着选，专门制造"Text 滞后 = 上一个班"
                {
                    cb8.SelectedIndex = i;     // 索引 i 对应 切换测试班(i+1)
                    PumpMs(120);
                    var data = (Dictionary<int, Dictionary<int, List<Lesson>>>)typeof(ManualScheduleWindow)
                        .GetField("_data", bf8)!.GetValue(w8)!;
                    string got = data.TryGetValue(0, out var dm8) && dm8.TryGetValue(1, out var lm8) && lm8.Count > 0
                        ? lm8[0].Subject : "";
                    string want = "科目" + (i + 1);
                    if (got != want) throw new Exception($"选第 {i} 项应显示 {want}，实际 {got}（下拉选中={cb8.SelectedItem}）");
                }
                // 手输新班名仍然可用（IsEditable 的原本用途：直接敲一个新班名保存）
                cb8.Text = "手输新班";
                var curName = (string)typeof(ManualScheduleWindow).GetProperty("CurrentClassName", bf8)!.GetValue(w8)!;
                if (curName != "手输新班") throw new Exception("手输班名失效，取到的是：" + curName);

                // 用户取消切班时，下拉要退回网格里正在显示的那个班（别让选中项与内容错开）
                cb8.SelectedIndex = 0;
                PumpMs(100);
                typeof(ManualScheduleWindow).GetMethod("RevertClassSelection", bf8)!.Invoke(w8, null);
                if (!Equals(cb8.SelectedItem, "切换测试班1"))
                    throw new Exception("回滚后下拉选中=" + cb8.SelectedItem + "（应为 切换测试班1）");
                w8.Close();
                return new LoginWindow();
            });
            // r127 / r142：作息文本往返 + 行编辑（现在在作息编辑弹窗上测）+ 拖动浮卡
            Check("r127 作息时间表与拖动浮卡", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

                // ① 往返：内置模板 → 多行文本 → 再解析，节次数与公共时间位置都要原样保住
                var srcT = ScheduleTemplates.CreateWithBuiltIns().PeriodTemplates.First(p => p.IsBuiltIn);
                if (srcT.FixedSlots.Count == 0) throw new Exception("内置模板没有公共时间，无法验证往返");
                var sheet = ScheduleSheetText.FormatScheduleSheet(srcT);
                if (!sheet.Contains('|')) throw new Exception("作息时间表里没有公共时间行：" + sheet.Replace("\n", " / "));
                var backT = new PeriodTemplate();
                ScheduleSheetText.ParseScheduleSheet(sheet, backT);
                if (backT.Periods.Count != srcT.Periods.Count)
                    throw new Exception("往返后节次数变了：" + backT.Periods.Count + " != " + srcT.Periods.Count);
                string wantSlots = string.Join(",", srcT.FixedSlots.Select(f => f.Name + "@" + f.AfterPeriod));
                string gotSlots = string.Join(",", backT.FixedSlots.Select(f => f.Name + "@" + f.AfterPeriod));
                if (wantSlots != gotSlots) throw new Exception("往返后公共时间位置变了：" + gotSlots + " 应为 " + wantSlots);

                // ② 行编辑：行首插入 = 在该行前，行尾插入 = 在该行后；节次自动重排、占位符自动选中
                //    r142：改在作息编辑弹窗上测 —— 弹窗编辑的是「当前这一份」，确定时才回写，所以这里只验文本操作。
                var tpl2 = new PeriodTemplate { Name = "行编辑测试" };
                var dlg2 = new PeriodTemplateDialog(tpl2, canManageData: true);
                var tDlg = typeof(PeriodTemplateDialog);
                var sheetBox = (System.Windows.Controls.TextBox)tDlg.GetField("_tbSheet", bf)!.GetValue(dlg2)!;
                var insLine = tDlg.GetMethod("InsertLine", bf)!;
                var delLine = tDlg.GetMethod("DeleteLine", bf)!;

                int LineStart(string text, int idx)
                {
                    int pos = 0;
                    for (int i = 0; i < idx; i++) { int nlPos = text.IndexOf('\n', pos); if (nlPos < 0) break; pos = nlPos + 1; }
                    return pos;
                }

                sheetBox.Text = "第1节 08:00-08:40\n第2节 08:50-09:30";
                sheetBox.CaretIndex = LineStart(sheetBox.Text, 1);   // 第 2 行行首
                insLine.Invoke(dlg2, new object[] { true });
                if (!sheetBox.Text.Split('\n')[1].StartsWith("第2节"))
                    throw new Exception("行首点添加没插在该行前面：" + sheetBox.Text.Replace("\n", " / "));
                if (!sheetBox.SelectedText.Contains("HH:MM"))
                    throw new Exception("插入后没选中时间占位符，用户还得先删掉再敲");
                if (sheetBox.Text.Split('\n').Length != 3)
                    throw new Exception("添加节次后行数不对：" + sheetBox.Text.Split('\n').Length);

                sheetBox.CaretIndex = sheetBox.Text.Length;          // 最后一行行尾
                insLine.Invoke(dlg2, new object[] { false });
                var lines2 = sheetBox.Text.Split('\n').ToList();
                if (!lines2.Last().Contains('|')) throw new Exception("添加公共时间没插在末尾：" + sheetBox.Text.Replace("\n", " / "));
                if (!lines2.Last().Contains("大课间")) throw new Exception("公共时间默认名不是大课间：" + lines2.Last());

                int fsLine = lines2.FindIndex(l => l.Contains('|'));
                sheetBox.CaretIndex = LineStart(sheetBox.Text, fsLine);
                delLine.Invoke(dlg2, new object[] { false });
                if (sheetBox.Text.Contains('|')) throw new Exception("删除公共时间后还留着公共时间行");
                if (sheetBox.Text.Split('\n').Count(l => l.Trim().Length > 0) != 3)
                    throw new Exception("删公共时间把节次也删了：" + sheetBox.Text.Replace("\n", " / "));

                sheetBox.Text = "第1节 08:00-08:40";
                sheetBox.CaretIndex = 0;
                delLine.Invoke(dlg2, new object[] { true });
                if (!sheetBox.Text.Contains("第1节")) throw new Exception("最后一个节次被删掉了（网格会变成空的）");

                // 删对行类型：光标在节次行点「删除公共时间」要拦住，不能把课节删掉
                sheetBox.Text = "第1节 08:00-08:40\n第2节 08:50-09:30";
                sheetBox.CaretIndex = 0;
                delLine.Invoke(dlg2, new object[] { false });
                if (sheetBox.Text.Split('\n').Length != 2) throw new Exception("在节次行点「删除公共时间」却删了行");
                dlg2.Close();

                // ④ 拖动浮卡：能生成、跟着坐标走、收起后不留残留、源格恢复不透明
                var sf9 = new ScheduleFile();
                var c9 = new ClassSchedule { ClassName = "浮卡测试班" };
                c9.Data[0] = new Dictionary<int, List<Lesson>>
                {
                    [1] = new List<Lesson> { new() { Period = 1, Subject = "语文", Teacher = "张老师", Week = 0 } }
                };
                sf9.Classes.Add(c9);
                var w9 = new ManualScheduleWindow(sf9, ScheduleTemplates.CreateWithBuiltIns());
                var wm = typeof(ManualScheduleWindow);
                var layer = (System.Windows.Controls.Canvas)wm.GetField("_dragLayer", bf)!.GetValue(w9)!;
                if (layer.IsHitTestVisible) throw new Exception("浮卡覆盖层会挡住鼠标命中");
                if (System.Windows.Controls.Grid.GetRowSpan(layer) != 3) throw new Exception("浮卡覆盖层没铺满窗口");
                var showGhost = wm.GetMethod("ShowDragGhost", bf)!;
                var moveGhost = wm.GetMethod("MoveDragGhost", bf)!;
                var hideGhost = wm.GetMethod("HideDragGhost", bf)!;
                var fakeBtn = new Button { Content = "语文\n张老师" };
                showGhost.Invoke(w9, new object[] { fakeBtn });
                if (layer.Children.Count != 1) throw new Exception("拖动浮卡没生成");
                var ghost = (System.Windows.Controls.Border)layer.Children[0];
                if (ghost.Width < 80 || ghost.Height < 36) throw new Exception($"浮卡尺寸不对：{ghost.Width}x{ghost.Height}");
                if (fakeBtn.Opacity > 0.5) throw new Exception("源格没变虚影，看不出卡片被拎起来");
                moveGhost.Invoke(w9, new object[] { new System.Windows.Point(200, 150) });
                if (Math.Abs(System.Windows.Controls.Canvas.GetLeft(ghost) - (200 - ghost.Width / 2 + 8)) > 0.5)
                    throw new Exception("浮卡没跟着光标坐标走");
                hideGhost.Invoke(w9, Array.Empty<object>());
                if (layer.Children.Count != 0) throw new Exception("浮卡没清掉");
                if (fakeBtn.Opacity < 0.99) throw new Exception("源格透明度没恢复（会一直发灰）");
                w9.Close();
                return new LoginWindow();
            });
            // r129：手动录入的课表与「电子表格 / 自动更新」的课表彻底分开
            //  ① 同名班靠「（手动录入）」尾巴区分；② 导入/自动更新落盘时保留手动班；
            //  ③ 手动班的公共时间用自己那份，不受年级作息 / 全局默认模板影响；④ 节次时间写进课表；
            //  ⑤ 删除班级只对手动班开放；⑥ 模板窗口有滚动条、删除按钮移到左栏、设为默认与四个按钮同行。
            Check("r129 手动录入与导入课表互相独立", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

                // ① 班名尾巴（重复调用不重复加）；旧数据没有 Origin 但带尾巴也算手动
                if (ClassSchedule.WithManualSuffix("七年级(1)班") != "七年级(1)班（手录）")
                    throw new Exception("班名没加尾巴：" + ClassSchedule.WithManualSuffix("七年级(1)班"));
                if (ClassSchedule.WithManualSuffix("七年级(1)班（手录）") != "七年级(1)班（手录）")
                    throw new Exception("已有尾巴又被加了一次");
                // r140：尾巴缩短成「（手录）」，但旧尾巴「（手动录入）」必须继续认得、且不再叠一个新尾巴
                if (ClassSchedule.WithManualSuffix("七年级(1)班（手动录入）") != "七年级(1)班（手动录入）")
                    throw new Exception("旧尾巴没被认出，又叠了一个新尾巴：" + ClassSchedule.WithManualSuffix("七年级(1)班（手动录入）"));
                if (!new ClassSchedule { ClassName = "七年级(1)班（手录）" }.IsManual)
                    throw new Exception("带「（手录）」尾巴的班没被认成手动班");
                if (!new ClassSchedule { ClassName = "七年级(1)班（手动录入）" }.IsManual)
                    throw new Exception("带「（手动录入）」尾巴的旧数据没被认成手动班");
                if (new ClassSchedule { ClassName = "七年级(1)班", Origin = "" }.IsManual)
                    throw new Exception("导入的班被误判为手动班");

                // ② 导入 / 自动更新只换导入的那一套，手动班原样保留
                var prev = new ScheduleFile();
                var impOld = new ClassSchedule { ClassName = "七年级(1)班" };
                impOld.Data[0] = new Dictionary<int, List<Lesson>> { [1] = new List<Lesson> { new() { Period = 1, Subject = "语文" } } };
                var man = new ClassSchedule { ClassName = "七年级(1)班（手动录入）", Origin = "Manual" };
                man.Data[0] = new Dictionary<int, List<Lesson>> { [1] = new List<Lesson> { new() { Period = 1, Subject = "数学" } } };
                prev.Classes.Add(impOld);
                prev.Classes.Add(man);

                var incoming = new ScheduleFile();
                var impNew = new ClassSchedule { ClassName = "七年级(1)班" };
                impNew.Data[0] = new Dictionary<int, List<Lesson>> { [1] = new List<Lesson> { new() { Period = 1, Subject = "英语" } } };
                incoming.Classes.Add(impNew);

                int kept = ClassBulletinApp.MainWindow.MergeManualClasses(incoming, prev);
                if (kept != 1) throw new Exception("手动班没被保留（kept=" + kept + "）");
                if (incoming.Classes.Count != 2) throw new Exception("合并后班级数不对：" + incoming.Classes.Count);
                var gotImp = incoming.Classes.First(c => c.ClassName == "七年级(1)班");
                if (gotImp.Data[0][1][0].Subject != "英语")
                    throw new Exception("导入的班被手动班覆盖了：" + gotImp.Data[0][1][0].Subject);
                var gotMan = incoming.Classes.FirstOrDefault(c => c.ClassName == "七年级(1)班（手动录入）");
                if (gotMan == null) throw new Exception("同名的手动班在导入后丢了");
                if (gotMan.Data[0][1][0].Subject != "数学") throw new Exception("手动班的课被改动了");
                if (ClassBulletinApp.MainWindow.MergeManualClasses(incoming, prev) != 0 || incoming.Classes.Count != 2)
                    throw new Exception("重复合并会长出第二份手动班");

                // 数据包 / 二一缓存走「准入判断」：本机没这份班的课程设置 → 不保留（教室机跟着发布端走）
                var incB = new ScheduleFile();
                incB.Classes.Add(new ClassSchedule { ClassName = "七年级(1)班" });
                if (incB.MergeManualFrom(prev, c => false) != 0 || incB.Classes.Count != 1)
                    throw new Exception("准入判断拒绝了却还是把手动班并了回来");
                var incC = new ScheduleFile();
                incC.Classes.Add(new ClassSchedule { ClassName = "七年级(1)班" });
                if (incC.MergeManualFrom(prev, c => true) != 1)
                    throw new Exception("本机手排的班（准入为真）没有保留");

                // ③ 手动班的公共时间用自己那份；导入班仍按年级作息
                var sfx = new ScheduleFile();
                sfx.FixedByGrade["七年级"] = new List<FixedSlot> { new() { Name = "年级大课间", AfterPeriod = 1 } };
                var mcl = new ClassSchedule
                {
                    ClassName = "七年级(9)班（手动录入）", Origin = "Manual", PeriodTemplateName = "不存在的作息",
                    FixedSlots = new List<FixedSlot> { new() { Name = "手动眼保健操", AfterPeriod = 2 } }
                };
                sfx.Classes.Add(mcl);
                sfx.Classes.Add(new ClassSchedule { ClassName = "七年级(9)班" });
                var gotFx = BoardBuilder.ResolveFixedSlots(sfx, "七年级(9)班（手动录入）");
                if (gotFx == null || gotFx.Count != 1 || gotFx[0].Name != "手动眼保健操")
                    throw new Exception("手动班的公共时间没用自己的：" + string.Join(",", (gotFx ?? new List<FixedSlot>()).Select(f => f.Name)));
                var gotFx2 = BoardBuilder.ResolveFixedSlots(sfx, "七年级(9)班");
                if (gotFx2 == null || gotFx2[0].Name != "年级大课间")
                    throw new Exception("导入班反而没走年级作息：" + (gotFx2 == null ? "(null)" : gotFx2[0].Name));

                // ④ 保存时把作息时间表里的节次时间写进课表（大屏才显示 08:00-08:45）
                var tpl8 = ScheduleTemplates.CreateWithBuiltIns().PeriodTemplates.First(p => p.Name.Contains("高中"));
                var data8 = new Dictionary<int, Dictionary<int, List<Lesson>>>
                {
                    [0] = new Dictionary<int, List<Lesson>> { [1] = new List<Lesson> { new() { Period = 1, Subject = "语文" } } }
                };
                ManualScheduleWindow.StampPeriodTimes(data8, tpl8);
                if (data8[0][1][0].TimeRange != "08:00-08:45")
                    throw new Exception("节次时间没写进课表：" + data8[0][1][0].TimeRange);
                // 占位符（HH:MM-HH:MM）没填时不要写成时间
                var tplPh = new PeriodTemplate { Name = "占位", Periods = new List<PeriodTime> { new() { Index = 1, TimeRange = "HH:MM-HH:MM" } } };
                var dataPh = new Dictionary<int, Dictionary<int, List<Lesson>>>
                {
                    [0] = new Dictionary<int, List<Lesson>> { [1] = new List<Lesson> { new() { Period = 1, Subject = "语文" } } }
                };
                ManualScheduleWindow.StampPeriodTimes(dataPh, tplPh);
                if (dataPh[0][1][0].TimeRange.Length != 0)
                    throw new Exception("占位符被当成时间写进去了：" + dataPh[0][1][0].TimeRange);

                // ⑤ 删除按钮：两种班都能删（导入班要提示"下次更新会回来"，手动班是真删）
                var w10 = new ManualScheduleWindow(sfx, ScheduleTemplates.CreateWithBuiltIns());
                w10.Show();
                PumpMs(300);
                var cb10 = (System.Windows.Controls.ComboBox)typeof(ManualScheduleWindow).GetField("_cbClass", bf)!.GetValue(w10)!;
                var del10 = (Button)typeof(ManualScheduleWindow).GetField("_btnDeleteClass", bf)!.GetValue(w10)!;
                if (cb10.Items.IndexOf("七年级(9)班（手动录入）") < 0) throw new Exception("手动班没出现在班级下拉里");
                cb10.SelectedItem = "七年级(9)班";
                PumpMs(120);
                if (!del10.IsEnabled) throw new Exception("导入的班也应能删（提示下次更新会回来即可）");
                if (!((del10.ToolTip as string) ?? "").Contains("重新导入"))
                    throw new Exception("导入班的删除提示没说清「下次更新会回来」：" + del10.ToolTip);
                cb10.SelectedItem = "七年级(9)班（手动录入）";
                PumpMs(120);
                if (!del10.IsEnabled) throw new Exception("手动录入的班不能删除");
                if (!((del10.ToolTip as string) ?? "").Contains("手动录入"))
                    throw new Exception("手动班的删除提示不对：" + del10.ToolTip);

                // 重名保存规则：导入班同名 → 自动加「（手录）」尾巴；选「否」就不保存
                string n1 = w10.ResolveSaveName("七年级(9)班", _ => MessageBoxResult.Yes);
                if (n1 != "七年级(9)班（手录）") throw new Exception("同名导入班保存时应改成带尾巴的名字，实际：" + n1);
                string n2 = w10.ResolveSaveName("七年级(9)班", _ => MessageBoxResult.No);
                if (n2 != "") throw new Exception("选了「回去改名」却仍然保存：" + n2);
                string n3 = w10.ResolveSaveName("七年级(9)班（手动录入）", _ => MessageBoxResult.Cancel);
                if (n3 != "七年级(9)班（手动录入）") throw new Exception("自己这份手动课表不该被改名：" + n3);
                string n4 = w10.ResolveSaveName("全新班", _ => MessageBoxResult.Cancel);
                if (n4 != "全新班") throw new Exception("新班级不该弹重名确认：" + n4);

                // r140：重名弹窗不再用「是 / 否 / 取消」——按钮直接写结果，且默认（直接关窗）不能误存
                var cd14 = new SaveClassConflictDialog("七年级(9)班", "七年级(9)班（手录）");
                var cdBtn = new List<string>();
                void WalkCd(System.Windows.DependencyObject node)
                {
                    foreach (var ch in System.Windows.LogicalTreeHelper.GetChildren(node))
                    {
                        if (ch is Button cb14 && cb14.Content is string s14) cdBtn.Add(s14);
                        if (ch is System.Windows.DependencyObject d14) WalkCd(d14);
                    }
                }
                WalkCd(cd14);
                foreach (var want in new[] { "分开存（推荐）", "改个班名", "取消" })
                    if (!cdBtn.Contains(want))
                        throw new Exception("重名弹窗缺按钮：" + want + "（现有：" + string.Join(" / ", cdBtn) + "）");
                if (cdBtn.Contains("是") || cdBtn.Contains("否"))
                    throw new Exception("重名弹窗还在用是 / 否：" + string.Join(" / ", cdBtn));
                if (cd14.Result != SaveClassConflictDialog.Choice.Cancel)
                    throw new Exception("重名弹窗默认结果不是「取消」——关掉窗口会误存");
                cd14.Close();

                // 删除：只回传班名，由 MainWindow 落盘
                w10.DeleteClass(confirm: false);
                if (w10.DeleteClassName != "七年级(9)班（手动录入）")
                    throw new Exception("删除班级没回传正确的班名：" + (w10.DeleteClassName ?? "(null)"));

                // ⑥ 作息设置现在在手录窗口左栏（r142）：下拉默认选中「默认作息」，右边是图标编辑入口
                var w11 = new ManualScheduleWindow(new ScheduleFile(), ScheduleTemplates.CreateWithBuiltIns());
                var tW11 = typeof(ManualScheduleWindow);
                var bf11 = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var cbPer = (System.Windows.Controls.ComboBox)tW11.GetField("_cbPeriod", bf11)!.GetValue(w11)!;
                if (cbPer.SelectedItem is not PeriodTemplate sel || !sel.IsDefault)
                    throw new Exception("作息下拉没默认选中标记为默认的那份：" + cbPer.SelectedItem);
                var btnEditPer = (Button)tW11.GetField("_btnEditPeriod", bf11)!.GetValue(w11)!;
                if (!btnEditPer.IsEnabled) throw new Exception("作息编辑入口不该禁用");
                if (btnEditPer.Content is not System.Windows.Shapes.Path)
                    throw new Exception("作息编辑入口应是图标按钮（不是文字按钮）");
                w11.Close();
                return new LoginWindow();
            });
            // r130：①「下载模板」从独立按钮改成「智能导入课表」后面的文字链 + 问号说明；
            //  ② 4 种模板都要能表达单双周（一格两门：语文-单|数学-双），并真的能导进来。
            Check("r130 下载模板入口与单双周模板", () =>
            {
                var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

                // ① 入口位置：同一个横向 StackPanel 里，紧跟智能导入按钮，后面跟一颗问号
                var mw = new MainWindow { InitialTabIndex = 1 };
                var smart = (Button)typeof(MainWindow).GetField("BtnImportSmart", bf)!.GetValue(mw)!;
                var dl = (Button)typeof(MainWindow).GetField("BtnDownloadTemplates", bf)!.GetValue(mw)!;
                if ((dl.Content as string) != "下载模板")
                    throw new Exception("下载入口文案还是：" + dl.Content);
                if (System.Windows.LogicalTreeHelper.GetParent(dl) is not System.Windows.Controls.StackPanel row
                    || !ReferenceEquals(System.Windows.LogicalTreeHelper.GetParent(smart), row))
                    throw new Exception("「下载模板」没有和「智能导入课表」排在一起（又变回独立按钮了）");
                if (row.Children.IndexOf(dl) != row.Children.IndexOf(smart) + 1)
                    throw new Exception("「下载模板」不在「智能导入课表」右边第一位");
                var asks = row.Children.OfType<ClassBulletinApp.Controls.HelpBubble>().ToList();
                if (asks.Count == 0) throw new Exception("「下载模板」后面没有问号说明");
                if (asks[0].Text.Length < 40 || !asks[0].Text.Contains("模板"))
                    throw new Exception("问号里没讲清下载模板的用处");
                mw.Close();

                // ② 模板里要有单双周示例，并且能被智能探测器 + 通用解析器读成 Week=1 / Week=2 两门
                var kinds = new[]
                {
                    ImportTemplateWriter.TemplateKind.Base,
                    ImportTemplateWriter.TemplateKind.TempBase,
                    ImportTemplateWriter.TemplateKind.SingleClass,
                    ImportTemplateWriter.TemplateKind.Teacher
                };
                foreach (var kind in kinds)
                {
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "cb_tpl130_" + Guid.NewGuid().ToString("N") + ".xlsx");
                    try
                    {
                        ImportTemplateWriter.Write(tmp, kind);
                        var pr = ScheduleTemplateProbe.Probe(tmp);
                        if (!pr.Ok) throw new Exception(kind + " 模板没被识别");
                        var g = ScheduleGeneralImporter.Parse(tmp, pr.Best);
                        if (g.UnparsedCells > 0)
                            throw new Exception(kind + " 模板有 " + g.UnparsedCells + " 个格子没解析出来");
                        var lessons = g.Schedule.Classes.SelectMany(c => c.Data.Values)
                            .SelectMany(d => d.Values).SelectMany(x => x).ToList();
                        int w1 = lessons.Count(l => l.Week == 1);
                        int w2 = lessons.Count(l => l.Week == 2);
                        if (w1 != 1 || w2 != 1)
                            throw new Exception(kind + " 模板的单双周示例没解析成两条（单周 " + w1 + " / 双周 " + w2 + "）");
                        var one = lessons.First(l => l.Week == 1);
                        var two = lessons.First(l => l.Week == 2);
                        if (one.Subject.Length == 0 || two.Subject.Length == 0)
                            throw new Exception(kind + " 单双周课程没取到科目");
                        if (one.Subject.EndsWith("单") || two.Subject.EndsWith("双"))
                            throw new Exception(kind + " 单双周标记没从科目名里清掉：" + one.Subject + " / " + two.Subject);
                        if (kind != ImportTemplateWriter.TemplateKind.Teacher
                            && (one.Teacher.Length == 0 || two.Teacher.Length == 0))
                            throw new Exception(kind + " 单双周两门的教师没被 | 分开：" + one.Teacher + " / " + two.Teacher);
                    }
                    finally { try { System.IO.File.Delete(tmp); } catch { } }
                }

                // ③ 说明文案要写清单双周写法（否则用户还是不知道一格两门怎么填）
                if (!ImportTemplateWriter.WeekHint.Contains("|") || !ImportTemplateWriter.WeekHint.Contains("单"))
                    throw new Exception("模板说明里没有单双周写法");

                // ④ 用户照抄时容易写成「语文（单）」「数学（双周）」——标记不能留在科目名里
                var tmp2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "cb_tpl130w_" + Guid.NewGuid().ToString("N") + ".xlsx");
                try
                {
                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        var ws = wb.Worksheets.Add("班级课表");
                        ws.Cell(1, 1).Value = "七年级(1)班-班级课程表";
                        ws.Cell(2, 1).Value = "节次";
                        string[] days = { "星期一", "星期二", "星期三", "星期四", "星期五" };
                        for (int d = 0; d < days.Length; d++) ws.Cell(2, d + 2).Value = days[d];
                        for (int p = 1; p <= 7; p++) ws.Cell(p + 2, 1).Value = "第" + p + "节";
                        ws.Cell(3, 2).Value = "语文（单）|数学（双周）\n张老师|李老师";
                        wb.SaveAs(tmp2);
                    }
                    var pr2 = ScheduleTemplateProbe.Probe(tmp2);
                    if (!pr2.Ok) throw new Exception("括号写法测试文件没被识别");
                    var g2 = ScheduleGeneralImporter.Parse(tmp2, pr2.Best);
                    var ls2 = g2.Schedule.Classes.SelectMany(c => c.Data.Values)
                        .SelectMany(d => d.Values).SelectMany(x => x).ToList();
                    var a1 = ls2.FirstOrDefault(l => l.Week == 1);
                    var a2 = ls2.FirstOrDefault(l => l.Week == 2);
                    if (a1 == null || a2 == null) throw new Exception("「语文（单）/数学（双周）」没被识别成单双周");
                    if (a1.Subject != "语文" || a2.Subject != "数学")
                        throw new Exception("括号里的单双周标记没从科目名清掉：" + a1.Subject + " / " + a2.Subject);
                }
                finally { try { System.IO.File.Delete(tmp2); } catch { } }

                return new LoginWindow();
            });
            Check("LoginWindow", () => new LoginWindow());
            Check("RetrievePasswordWindow", () => new RetrievePasswordWindow());
            // r105：板书模式 —— 悬浮课表左边那块手写区能开、能写、能收，笔迹保留 7 天
            Check("悬浮课表 板书模式", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);
                if (!w.DiagHandModeActive) throw new Exception("切到板书模式后手写区没有展开");
                if (w.DiagHandWidth < 280) throw new Exception("手写区宽度过窄：" + w.DiagHandWidth.ToString("0"));
                if (w.DiagHandKeepDays != 7) throw new Exception("笔迹保留天数应为 7，实际 " + w.DiagHandKeepDays);
                if (w.DiagDrawHandSample() < 2) throw new Exception("手写区写不进笔迹");
                w.DiagSetHandMode(false);
                if (w.DiagHandModeActive) throw new Exception("收起后手写区还在");
                return w;
            });
            // r107：拖两区中间那条缝 = 改【课表】宽度（手写区宽度一丝不变、窗口右边界钉死）
            Check("悬浮课表 板书拖缝只改课表", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);
                double hand0 = w.DiagHandWidth;
                double course0 = w.DiagCourseWidth;
                double right0 = (double.IsNaN(w.Left) ? 0 : w.Left) + course0 + hand0;

                w.DiagDragCourseTo(course0 - 160);            // 往窄里拖
                double hand1 = w.DiagHandWidth;
                double course1 = w.DiagCourseWidth;
                double right1 = (double.IsNaN(w.Left) ? 0 : w.Left) + w.Width;

                if (Math.Abs(hand1 - hand0) > 0.5)
                    throw new Exception("拖缝把手写区宽度改了（应保持 " + hand0.ToString("0")
                                        + "，实际 " + hand1.ToString("0") + "）");
                if (course1 > course0 - 100)
                    throw new Exception("课表宽度没有跟着变窄：" + course1.ToString("0"));
                if (Math.Abs(right1 - right0) > 1)
                    throw new Exception("窗口右边界没有钉住：" + right0.ToString("0")
                                        + " → " + right1.ToString("0"));
                return w;
            });
            // r107：拖窗口左沿 = 改【手写区】宽度（课表列宽一丝不变）—— 旧实现正好反了
            Check("悬浮课表 板书拖左沿只动手写区", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);
                double hand0 = w.DiagHandWidth;
                double course0 = w.DiagCourseWidth;

                w.DiagLeftResize(hand0 + course0 + 180);      // 窗口宽 +180 = 左沿往左拖
                double hand1 = w.DiagHandWidth;
                double course1 = w.DiagCourseWidth;

                if (Math.Abs(course1 - course0) > 0.5)
                    throw new Exception("拖左沿把课表列宽改了（应保持 " + course0.ToString("0")
                                        + "，实际 " + course1.ToString("0") + "）");
                if (hand1 < hand0 + 120)
                    throw new Exception("拖左沿没有把手写区拉宽：" + hand0.ToString("0")
                                        + " → " + hand1.ToString("0"));
                return w;
            });
            // r107：板书工具条要在教室大屏上用（按钮 ≥44 高、四个按钮都带图标 + 文字）
            Check("悬浮课表 板书工具条放大带图标", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);
                if (w.DiagHandToolSize < 44)
                    throw new Exception("工具条按钮太小：" + w.DiagHandToolSize.ToString("0"));
                if (w.DiagHandToolIconCount != 4)
                    throw new Exception("工具条图标不全：" + w.DiagHandToolIconCount + "/4");
                return w;
            });
            // r145：板书底色可换（7 档）+ 笔色跟着底色走（深底自动换白笔，否则写了看不见）
            Check("r145 板书底色可换且笔色跟着变", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);

                if (w.DiagHandLookCount < 6)
                    throw new Exception("底色档位太少：" + w.DiagHandLookCount + " 档");
                if (new ScheduleCore.Models.AppSettings().ScheduleHandTheme != "paper")
                    throw new Exception("出厂底色应为备忘纸");
                w.DiagSetHandTheme("paper");     // 从已知档位起跑，别受上次诊断残留影响
                if (!w.DiagHandHasRuledLines)
                    throw new Exception("备忘纸档应有横线");

                // 深色底：底色变了、横线还在、笔自动换成浅色
                w.DiagSetHandTheme("green");
                if (w.DiagHandThemeKey != "green") throw new Exception("换底色没生效");
                if (!w.DiagHandCanvasBg.EndsWith("2E4A3C", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("书写区底色没换成墨绿：" + w.DiagHandCanvasBg);
                if (!w.DiagHandHasRuledLines) throw new Exception("墨绿黑板档把横线弄丢了");
                if (w.DiagHandPenColor == "#1F2A37")
                    throw new Exception("深色底上还留着黑笔 —— 写了看不见");

                // 纯白档：横线要真的消失（不是画成白色）
                w.DiagSetHandTheme("white");
                if (w.DiagHandHasRuledLines) throw new Exception("纯白档应该没有横线");
                if (!w.DiagHandCanvasBg.EndsWith("FFFFFF", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("纯白档底色不对：" + w.DiagHandCanvasBg);
                if (w.DiagHandPenColor != "#1F2A37")
                    throw new Exception("换回浅色底后笔色没回到深色：" + w.DiagHandPenColor);

                // 落盘：重启还得是这个底色
                if (Store.LoadSettings().ScheduleHandTheme != "white")
                    throw new Exception("底色没写进设置：" + Store.LoadSettings().ScheduleHandTheme);

                w.DiagSetHandTheme("paper");     // 还原，别把诊断用的底色留给真机
                return w;
            });
            // r145：板书写满能加页 —— 同一天多页互不干扰，删页要重编号，至少留 1 页
            Check("r145 板书分页加删切换", () =>
            {
                var w = new ScheduleWindow();
                w.DiagSetHandMode(true);
                if (w.DiagHandPageCount != 1) throw new Exception("一开始应只有 1 页");
                if (w.DiagHandPage != 1) throw new Exception("页码起点不是 1");

                // 第 1 页写两笔
                int s1 = w.DiagDrawHandSample();
                w.DiagSaveHandInkNow();
                if (s1 < 2) throw new Exception("第 1 页写不进笔迹");

                // 加页：页码跟着走、画布清空、两页都有文件
                if (w.DiagAddHandPage() != 2) throw new Exception("加页后总页数不是 2");
                if (w.DiagHandPage != 2) throw new Exception("加页后没切到新页");
                if (w.DiagHandStrokeCount != 0) throw new Exception("新页不应带着上一页的笔迹");
                if (!w.DiagHandPageFileExists(1) || !w.DiagHandPageFileExists(2))
                    throw new Exception("页文件没落盘：1=" + w.DiagHandPageFileExists(1)
                                        + " 2=" + w.DiagHandPageFileExists(2));

                // 第 2 页写两笔，再翻回第 1 页看原来的笔迹
                w.DiagDrawHandSample();
                w.DiagSaveHandInkNow();
                w.DiagGotoHandPage(1);
                if (w.DiagHandPage != 1) throw new Exception("翻页没切到第 1 页");
                if (w.DiagHandStrokeCount < 2) throw new Exception("第 1 页的笔迹丢了：" + w.DiagHandStrokeCount);
                w.DiagGotoHandPage(2);
                if (w.DiagHandStrokeCount < 2) throw new Exception("第 2 页的笔迹丢了：" + w.DiagHandStrokeCount);

                // 删当前页（第 2 页）：剩 1 页、文件归零、页码落回 1
                if (w.DiagDeleteHandPage() != 1) throw new Exception("删页后总页数不是 1");
                if (w.DiagHandPage != 1) throw new Exception("删页后页码没落回 1");
                if (w.DiagHandPageFileExists(2)) throw new Exception("删掉的页文件还在");
                if (!w.DiagHandPageFileExists(1)) throw new Exception("第 1 页被误删了");

                // 只剩 1 页时删不动
                w.DiagDeleteHandPage();
                if (w.DiagHandPageCount != 1) throw new Exception("只剩 1 页时不该能删");
                var del = w.FindName("BtnHandPageDel") as Button;
                if (del != null && del.IsEnabled) throw new Exception("只有 1 页时「删页」按钮应禁用");
                return w;
            });
            // r106：进板书模式 = 常驻 + 不置顶（自动隐藏时窗口会缩到屏幕边上，板书根本没法写）
            Check("悬浮课表 板书模式常驻不置顶", () =>
            {
                var w = new ScheduleWindow { Topmost = true };
                w.DiagToggleHand();
                if (!w.DiagHandModeActive) throw new Exception("板书模式没打开");
                if (!w.DiagResidentNoTop) throw new Exception("板书模式没有切成常驻 + 不置顶");
                w.DiagToggleHand();
                if (w.DiagHandModeActive) throw new Exception("再点一次没有收起手写区");
                return w;
            });
            // r106：显示设置页的黑板开关与两个启动选项
            Check("显示设置 黑板开关与启动选项", () =>
            {
                var fresh = new ScheduleCore.Models.AppSettings();
                if (!fresh.DoodleRestoreOnStart || fresh.DoodleStartCollapsed)
                    throw new Exception("黑板启动选项默认值不对（应为 自动打开 / 不先收起）");

                var w = new MainWindow { InitialTabIndex = 2 };
                if (w.BtnToggleBoard == null || w.BtnBoardManager == null
                    || w.ChkBoardRestore == null || w.ChkBoardStartMin == null)
                    throw new Exception("显示设置页缺少黑板开关 / 黑板管理 / 启动选项");

                // 未 Show 时 Loaded 不触发，手动回显一次（与真机走同一个方法）
                w.DiagSyncBoardStartOptions();
                var st = ScheduleCore.Services.Store.LoadSettings();
                if (w.ChkBoardRestore.IsChecked != st.DoodleRestoreOnStart)
                    throw new Exception("「启动时自动打开黑板」没有反映当前设置");
                if (w.ChkBoardStartMin.IsChecked != st.DoodleStartCollapsed)
                    throw new Exception("「启动时先收成板名条」没有反映当前设置");
                if (w.ChkBoardStartMin.IsEnabled != st.DoodleRestoreOnStart)
                    throw new Exception("「先收成板名条」应与「自动打开」联动");
                return w;
            });
            Check("LayoutWindow", () => new LayoutWindow { InitialTabIndex = 2 });            // 通知条默认「置顶常驻 + 穿透 + 移上去变透明」：少一个勾选框用户就没法改回去
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
                //  ★ 另外 Show() 只把 HWND 建出来，宿主窗的 Loaded / Popup 的窗口创建都排在 Dispatcher 队列里，
                //    不推空队列就断言 IsOpen 会读到一个「刚设又被布局回调弹回」的中间态（Release 下必现的假失败）。
                static void Pump()
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }

                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(a); panel.Children.Add(b);
                var host = new Window { Width = 240, Height = 120, Content = panel,
                                        WindowStartupLocation = WindowStartupLocation.Manual,
                                        Left = -2000, Top = -2000 };
                try
                {
                    host.Show();
                    Pump();
                    a.OpenBubble();
                    Pump();
                    if (!a.IsBubbleOpen) throw new Exception("点击后气泡没打开");
                    b.OpenBubble();
                    Pump();
                    if (a.IsBubbleOpen || !b.IsBubbleOpen)
                        throw new Exception("气泡没有单开（两个同时弹会互相盖住内容）");
                    b.CloseBubble();
                    Pump();
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

            // ---------------- r79d：通知编辑窗「文字格式」行内的主题风格下拉 ----------------
            // r144：字号也并进这一行（原来在「便签管理 → 风格…」独立窗），本段一并验。
            Check("r79d 通知编辑窗文字格式行内主题下拉", () =>
            {
                var before = Store.LoadSettings();
                string oldTheme = before.NoticeNoteTheme;
                double oldScale = before.NoticeNoteScale;

                var w = new NotificationEditWindow();
                try
                {
                    if (w.DiagThemeItemCount != ScheduleCore.Models.HomeworkThemes.Presets.Count)
                        throw new Exception("主题风格下拉项数不对（现在 " + w.DiagThemeItemCount + " 个，应该是 "
                                            + ScheduleCore.Models.HomeworkThemes.Presets.Count + " 个）");
                    if (w.DiagRowCount != 4)
                        throw new Exception("为便签外观多起了一行（Grid 行数 " + w.DiagRowCount + "，应该是 4）");
                    if (!w.DiagThemeInFormatBar)
                        throw new Exception("主题风格下拉没有和文字格式工具条放在同一行");

                    // 初始选中态必须跟 settings.json 一致，不能自己编一个默认值
                    string want = string.IsNullOrWhiteSpace(oldTheme) ? "blue" : oldTheme.Trim();
                    if (!string.Equals(w.DiagNoteTheme, want, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("打开窗口时没有带回当前配色（期望 " + want + "，实际 " + w.DiagNoteTheme + "）");

                    // 走真实的 ComboBox 选中事件：换主题要真的写进 settings.json
                    string other = ScheduleCore.Models.HomeworkThemes.Presets
                        .First(t => !string.Equals(t.Key, w.DiagNoteTheme, StringComparison.OrdinalIgnoreCase)).Key;
                    w.DiagPickTheme(other);
                    if (!string.Equals(Store.LoadSettings().NoticeNoteTheme, other, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("切换主题风格下拉后没有写进 settings.json");

                    // r144：只改主题不许顺手把字号倍率改掉（反过来同理，见下）
                    if (Math.Abs(Store.LoadSettings().NoticeNoteScale - oldScale) > 0.0001)
                        throw new Exception("改主题时把便签字号倍率也改了");

                    // 字号下拉：三档（小 / 标准 / 大），选中要落盘，且不能把主题冲掉
                    var (cnt, sel) = w.DiagScaleState;
                    if (cnt != 3) throw new Exception("便签字号下拉应有 3 档，实际 " + cnt);
                    double big = w.DiagPickScale(2);                 // 2 = 大
                    if (Math.Abs(big - 1.15) > 0.0001) throw new Exception("选「大」没换算成 1.15，得到 " + big);
                    if (Math.Abs(Store.LoadSettings().NoticeNoteScale - 1.15) > 0.0001)
                        throw new Exception("改便签字号后没有写进 settings.json");
                    if (!string.Equals(Store.LoadSettings().NoticeNoteTheme, other, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("改字号时把刚选的主题配色冲掉了");
                }
                finally
                {
                    // 还原成进来时的样子，别影响后面那些跟便签风格有关的断言
                    try
                    {
                        var s = Store.LoadSettings();
                        s.NoticeNoteTheme = oldTheme;
                        s.NoticeNoteScale = oldScale;
                        if (Store.EnablePersistence) Store.SaveSettings(s);
                    }
                    catch { }
                }
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

            // r112：随包通道只带观感类 + 内容指纹驱动代次 + 独立下发通道（PushId）
            Check("设置通道：随包只带观感类 / 内容不变不升代次 / 独立下发按 PushId / 提醒只走独立下发", () =>
            {
                string d = Path.Combine(Path.GetTempPath(), "cbuicheck-settings-" + Guid.NewGuid().ToString("N")[..8]);
                System.IO.Directory.CreateDirectory(d);
                string storeBackup = "";
                try
                {
                    var sch = new ClassSchedule { ClassName = "诊断班" };
                    var sf = new ScheduleFile();
                    sf.Classes.Add(sch);

                    var st = new AppSettings
                    {
                        AutoShowSchedule = true, AutoShowHomework = false,
                        AutoUpdateEnabled = true, UpdateSourceKind = "Folder",
                        UpdateFolderPath = @"\\server\课表", BoardSyncEnabled = true,
                        GiteeToken = "SECRET_TOKEN_X", AutoLoginPassword = "SECRET_PWD",
                        BoardSyncActorId = "DEVICE-ID-X",
                        SettingsPublishRev = 4
                    };
                    st.Reminder.Enabled = true;

                    string pkg = Path.Combine(d, "s.cbpack");
                    var ex = SchedulePackage.Export(pkg, sf, new List<LessonOverride>(),
                        new List<Notification>(), st, "diag", includeSettings: true);
                    if (!ex.Ok) throw new Exception("带设置导出失败：" + ex.Error);
                    if (st.SettingsPublishRev != 5) throw new Exception("发布端代次未自增到 5，实际 " + st.SettingsPublishRev);

                    var ins = SchedulePackage.Inspect(pkg);
                    if (!ins.Ok || ins.Meta == null || !ins.Meta.SettingsIncluded || ins.Meta.Settings == null)
                        throw new Exception("包未正确携带设置");
                    if (ins.Meta.SettingsRev != 5) throw new Exception("包内设置代次不是 5");

                    // ★ r112：随包通道**不得**携带更新来源组（否则每次课表发布都会重置教室机数据源）
                    if (ins.Meta.Settings.SourceGroupIncluded)
                        throw new Exception("随包通道不该包含更新来源组");
                    if (!string.IsNullOrEmpty(ins.Meta.Settings.UpdateFolderPath)
                        || ins.Meta.Settings.AutoUpdateEnabled
                        || !string.IsNullOrEmpty(ins.Meta.Settings.BoardSyncRoot))
                        throw new Exception("随包通道漏出了数据源 / 同步根目录字段");

                    // 观感类必须正常带上
                    if (!ins.Meta.Settings.AutoShowSchedule || ins.Meta.Settings.AutoShowHomework)
                        throw new Exception("随包通道丢了观感设置（显示开关）");
                    if (!ins.Meta.Settings.BoardSyncEnabled)
                        throw new Exception("随包通道丢了同步开关");

                    // ★ r113：提醒退出随包通道 —— 包内白名单与老兼容字段都不得携带提醒
                    if (ins.Meta.Settings.Reminder != null)
                        throw new Exception("随包通道仍带着提醒（提醒只能走独立下发）");
                    if (ins.Meta.Reminder != null)
                        throw new Exception("新包仍写入了老兼容字段 meta.Reminder（提醒不该随包）");

                    // 排除本机私有：把整个 meta 序列化后不得出现密码 / 设备 ID / 数据源路径串
                    string metaJson = System.Text.Json.JsonSerializer.Serialize(ins.Meta);
                    if (metaJson.Contains("SECRET_TOKEN_X") || metaJson.Contains("SECRET_PWD")
                        || metaJson.Contains("DEVICE-ID-X") || metaJson.Contains(@"\\server\课表"))
                        throw new Exception("设置白名单混进了密码 / 设备 ID / 数据源路径");
                    // 包内**原始** meta.json 也不能出现提醒字段（Export 用 WhenWritingNull，能直接测出来）
                    string metaRaw;
                    using (var zs = System.IO.Compression.ZipFile.OpenRead(pkg))
                    using (var sr = new System.IO.StreamReader(zs.GetEntry("meta.json")!.Open()))
                        metaRaw = sr.ReadToEnd();
                    if (metaRaw.Contains("\"Reminder\""))
                        throw new Exception("包内 meta.json 仍出现 Reminder 字段（提醒应完全退出随包通道）");

                    // ★ 内容没变 → 再导出不升代次
                    string pkg2 = Path.Combine(d, "s2.cbpack");
                    var ex2 = SchedulePackage.Export(pkg2, sf, new List<LessonOverride>(),
                        new List<Notification>(), st, "diag", includeSettings: true);
                    if (!ex2.Ok) throw new Exception("二次导出失败");
                    if (st.SettingsPublishRev != 5)
                        throw new Exception($"内容未变却升了代次（{st.SettingsPublishRev}），教室机会被反复重刷设置");

                    // ★ 内容变了 → 升一代
                    st.ShowPanelOnStart = !st.ShowPanelOnStart;
                    string pkg3 = Path.Combine(d, "s3.cbpack");
                    var ex3 = SchedulePackage.Export(pkg3, sf, new List<LessonOverride>(),
                        new List<Notification>(), st, "diag", includeSettings: true);
                    if (!ex3.Ok) throw new Exception("三次导出失败");
                    if (st.SettingsPublishRev != 6)
                        throw new Exception($"内容变了却没升代次（{st.SettingsPublishRev}）");

                    // 老包（不携带设置）→ SettingsIncluded=false，行为不变
                    string pkgOld = Path.Combine(d, "old.cbpack");
                    var stOld = new AppSettings();
                    var exOld = SchedulePackage.Export(pkgOld, sf, new List<LessonOverride>(),
                        new List<Notification>(), stOld, "diag", includeSettings: false);
                    if (!exOld.Ok) throw new Exception("老包导出失败");
                    var insOld = SchedulePackage.Inspect(pkgOld);
                    if (!insOld.Ok || insOld.Meta == null || insOld.Meta.SettingsIncluded)
                        throw new Exception("老包不该标记携带设置");

                    // 三分支决策（纯函数，不碰磁盘）
                    bool b1 = SchedulePackage.ShouldApplySettings(0, 5, true, out string r1);
                    bool b2 = SchedulePackage.ShouldApplySettings(3, 5, true, out string r2);
                    bool b3 = SchedulePackage.ShouldApplySettings(5, 5, true, out string r3);
                    bool b4 = SchedulePackage.ShouldApplySettings(9, 5, true, out string r4);
                    bool bManual = SchedulePackage.ShouldApplySettings(9, 5, false, out string rM);
                    if (!b1 || r1 != "首次全量应用初始设置") throw new Exception("首次分支错：" + r1);
                    if (!b2 || r2 != "代次提升") throw new Exception("代次提升分支错：" + r2);
                    if (b3 || b4 || r3 != "代次未提升跳过") throw new Exception("代次未提升分支错：" + r3);
                    if (!bManual || rM != "手动导入覆盖") throw new Exception("手动导入分支错：" + rM);

                    // ── 独立下发通道 ────────────────────────────────────────────
                    string feedDir = Path.Combine(d, "feed");
                    System.IO.Directory.CreateDirectory(feedDir);
                    var baseMan = new PackageManifest
                    {
                        Schema = SchedulePackage.SchemaVersion,
                        Kind = "data",
                        DataSha256 = "sha-original",
                        Files = new List<PackageFile>()
                    };
                    File.WriteAllText(Path.Combine(feedDir, "manifest.json"),
                        SchedulePackage.SerializeManifest(baseMan));
                    File.WriteAllText(Path.Combine(feedDir, "data.cbpack"), "FAKE-PACKAGE-BODY");

                    var feed = new FolderFeedSource(feedDir);
                    var psPush = SchedulePackage.BuildPackageSettings(st, includeSourceGroup: true);
                    var push1 = SettingsPushService.Push(feed, psPush);
                    if (!push1.Ok) throw new Exception("下发设置失败：" + push1.Message);
                    if (push1.Skipped) throw new Exception("首次下发不该被跳过");
                    if (string.IsNullOrEmpty(push1.PushId)) throw new Exception("下发没有生成 PushId");

                    // ★ 只上传 manifest：data.cbpack 必须原封不动
                    if (File.ReadAllText(Path.Combine(feedDir, "data.cbpack")) != "FAKE-PACKAGE-BODY")
                        throw new Exception("下发设置改动了 data.cbpack（应只传 manifest）");

                    var manRemote = SchedulePackage.ParseManifest(
                        File.ReadAllText(Path.Combine(feedDir, "manifest.json")));
                    if (manRemote == null || !manRemote.SettingsIncluded || manRemote.Settings == null)
                        throw new Exception("数据源 manifest 没有写入设置");
                    if (manRemote.DataSha256 != "sha-original")
                        throw new Exception("下发设置改动了整包摘要（会让教室机重下课表）");
                    if (manRemote.Settings.SourceGroupIncluded != true
                        || manRemote.Settings.UpdateFolderPath != @"\\server\课表")
                        throw new Exception("独立通道没带上更新来源组");

                    // ★ r113：独立通道必须仍带提醒（随包通道退出后，它是提醒唯一的落地通道）
                    if (manRemote.Settings.Reminder == null)
                        throw new Exception("独立通道丢了提醒设置");

                    // ★ r113：只改提醒时，回执必须逐项列出（原来漏比提醒 → 永远"内容与本机一致"）
                    var stCmp = Store.LoadSettings();
                    var psSame = SchedulePackage.BuildPackageSettings(stCmp, includeSourceGroup: false);
                    if (SettingsPushService.DescribeChanges(stCmp, psSame).Count != 0)
                        throw new Exception("同内容却报出了变更项");

                    var psRem = SchedulePackage.BuildPackageSettings(stCmp, includeSourceGroup: false);
                    psRem.Reminder = System.Text.Json.JsonSerializer.Deserialize<ReminderSettings>(
                        System.Text.Json.JsonSerializer.Serialize(stCmp.Reminder))!;
                    psRem.Reminder.Enabled = !stCmp.Reminder.Enabled;
                    var remChanges = SettingsPushService.DescribeChanges(stCmp, psRem);
                    if (!remChanges.Any(c => c.Contains("提醒")))
                        throw new Exception("只改提醒时回执没列出提醒项（会被误判成下发失败）");

                    // 内容相同 → 不重复推送
                    var push2 = SettingsPushService.Push(feed, psPush);
                    if (!push2.Ok || !push2.Skipped)
                        throw new Exception("内容相同却重复推送了");

                    // 教室机按 PushId 应用：第一次应用、同一 PushId 第二次跳过
                    storeBackup = System.Text.Json.JsonSerializer.Serialize(Store.LoadSettings());
                    var ap1 = SettingsPushService.ApplyFromManifest(manRemote);
                    if (!ap1.Applied) throw new Exception("PushId 变化未触发应用：" + ap1.Message);
                    var ap2 = SettingsPushService.ApplyFromManifest(manRemote);
                    if (ap2.Applied) throw new Exception("同一 PushId 被重复应用");

                    // 应用后本机应真的拿到数据源路径（独立通道允许写这几项）
                    if (Store.LoadSettings().UpdateFolderPath != @"\\server\课表")
                        throw new Exception("独立通道没有应用更新来源组");

                    // ★ 代次未提升时必须整体跳过设置，连提醒也不能被这次课表更新改掉
                    //   （老包兼容分支原本无条件写 meta.Reminder，会绕过代次判断）
                    var stGuard = Store.LoadSettings();
                    stGuard.AppliedSettingsRev = 999;
                    stGuard.Reminder.Enabled = false;
                    Store.SaveSettings(stGuard);

                    var insSkip = SchedulePackage.Inspect(pkg3);   // 包代次 6 < 本机 999
                    var apSkip = SchedulePackage.Apply(insSkip, settingsByRev: true);
                    if (!apSkip.Ok) throw new Exception("跳过分支 Apply 失败：" + apSkip.Error);
                    if (apSkip.SettingsApplied) throw new Exception("代次更高时不该应用设置");
                    if (Store.LoadSettings().Reminder.Enabled)
                        throw new Exception("代次跳过时提醒仍被改写（老兼容分支绕过了代次判定）");

                    // ★ r113：代次只升不降 + 提醒不随包
                    //   强制应用（手动导入路径 settingsByRev=false）也不能把本机高水位拉低，
                    //   否则下一个大代次包又能覆盖一遍设置（现场 37 与 242 之间锯齿）。
                    var stHigh = Store.LoadSettings();
                    stHigh.AppliedSettingsRev = 999;
                    stHigh.Reminder.Enabled = false;
                    Store.SaveSettings(stHigh);

                    var apHigh = SchedulePackage.Apply(SchedulePackage.Inspect(pkg3), settingsByRev: false);
                    if (!apHigh.Ok) throw new Exception("高水位 Apply 失败：" + apHigh.Error);
                    if (!apHigh.SettingsApplied) throw new Exception("手动导入路径没应用设置");
                    if (Store.LoadSettings().AppliedSettingsRev != 999)
                        throw new Exception($"代次被小包拉低了（Math.Max 没生效）：{Store.LoadSettings().AppliedSettingsRev}");
                    if (Store.LoadSettings().Reminder.Enabled)
                        throw new Exception("应用数据包把提醒改掉了（提醒应从随包通道退出）");

                    // ★ r143 教学日历（校历 + 开学日）：走同一份 manifest，但校历本体写在
                    //   schedule.json 里 —— 所以代次高水位必须是独立的一套，否则刚下发的调休
                    //   会被随后一版课表包按"整份替换"冲回旧值。
                    string schedBackup = System.Text.Json.JsonSerializer.Serialize(Store.LoadSchedule());
                    try
                    {
                        var stCal = Store.LoadSettings();
                        stCal.AppliedCalendarRev = 0;
                        stCal.CalendarPublishRev = 0;
                        stCal.CalendarPublishFingerprint = "";
                        stCal.SemesterStartMonday = "2026-09-01";
                        Store.SaveSettings(stCal);

                        var sfPub = Store.LoadSchedule() ?? throw new Exception("本机没有课表，诊断前提不成立");
                        sfPub.Calendar = new List<SchoolCalendarRule>
                        {
                            new() { Date = "2026-11-21", Kind = CalendarKind.Makeup, AsDate = "2026-11-18" },
                            new() { Date = "2026-11-20", Kind = CalendarKind.Off }
                        };
                        Store.SaveSchedule(sfPub);

                        // ① 发布端组包：日历组要带全（开学日 + 2 条规则 + 代次）
                        var psCal = SchedulePackage.BuildPackageSettings(Store.LoadSettings(), includeSourceGroup: false);
                        SchedulePackage.IncludeCalendarGroup(psCal, Store.LoadSettings(), Store.LoadSchedule());
                        if (!psCal.CalendarIncluded || psCal.Calendar.Count != 2 || psCal.CalendarRev < 1)
                            throw new Exception($"教学日历组没装进下发内容（Included={psCal.CalendarIncluded} "
                                              + $"Count={psCal.Calendar.Count} Rev={psCal.CalendarRev}）");

                        var pushCal = SettingsPushService.Push(feed, psCal);
                        if (!pushCal.Ok) throw new Exception("下发教学日历失败：" + pushCal.Message);

                        var manCal = SchedulePackage.ParseManifest(
                            File.ReadAllText(Path.Combine(feedDir, "manifest.json")));
                        if (manCal?.Settings == null || !manCal.Settings.CalendarIncluded
                            || manCal.Settings.Calendar.Count != 2)
                            throw new Exception("manifest 里没带上教学日历");

                        // ② 教室机：校历清空、代次归零，应用后应当整组拿到
                        var sfRoom = Store.LoadSchedule()!;
                        sfRoom.Calendar = new List<SchoolCalendarRule>();
                        Store.SaveSchedule(sfRoom);
                        var stRoom = Store.LoadSettings();
                        stRoom.AppliedCalendarRev = 0;
                        stRoom.LastSettingsPushId = "";
                        Store.SaveSettings(stRoom);

                        var apCal = SettingsPushService.ApplyFromManifest(manCal);
                        if (!apCal.Applied || !apCal.CalendarTouched || apCal.CalendarCount != 2)
                            throw new Exception($"教学日历没落地：Applied={apCal.Applied} "
                                              + $"Touched={apCal.CalendarTouched} Count={apCal.CalendarCount}");
                        if (Store.LoadSchedule()!.Calendar.Count != 2)
                            throw new Exception("schedule.json 里的校历条数不对");
                        if (Store.LoadSettings().AppliedCalendarRev != manCal.Settings.CalendarRev)
                            throw new Exception("教学日历高水位没抬高");
                        if (Store.LoadSettings().SemesterStartMonday != "2026-09-01")
                            throw new Exception("开学日没有跟着教学日历一起下发");

                        // ③ 代次守卫（谁新谁生效）：本机代次更高时，旧代次的校历不能盖回来
                        var stHi = Store.LoadSettings();
                        stHi.AppliedCalendarRev = 999;
                        stHi.LastSettingsPushId = "";
                        Store.SaveSettings(stHi);
                        var sfKeep = Store.LoadSchedule()!;
                        sfKeep.Calendar = new List<SchoolCalendarRule>
                            { new() { Date = "2026-12-01", Kind = CalendarKind.Off } };
                        Store.SaveSchedule(sfKeep);

                        var apOld = SettingsPushService.ApplyFromManifest(manCal);
                        if (!apOld.Ok) throw new Exception("旧代次下发整体失败了：" + apOld.Message);
                        if (apOld.CalendarTouched) throw new Exception("代次更低却覆盖了本机校历");
                        var sfKept = Store.LoadSchedule()!;
                        if (sfKept.Calendar.Count != 1 || sfKept.Calendar[0].Date != "2026-12-01")
                            throw new Exception("本机校历被旧代次冲掉了");

                        // ④ 整包侧同样的守卫：包里的校历代次不如本机时，整份替换要放过 Calendar
                        var apPkgCal = SchedulePackage.Apply(SchedulePackage.Inspect(pkg3), settingsByRev: true);
                        if (!apPkgCal.Ok) throw new Exception("整包 Apply 失败：" + apPkgCal.Error);
                        var sfPkgCal = Store.LoadSchedule()!;
                        if (sfPkgCal.Calendar.Count != 1 || sfPkgCal.Calendar[0].Date != "2026-12-01")
                            throw new Exception("整包把本机更新的校历冲掉了（代次守卫没生效）");

                        // ⑤ r144 代次重置：这是"教室机代次虚高 → 管理员发多少遍都没反应"的唯一出路。
                        //   先把两个高水位都顶到 999（现场最常见的卡死形态）。
                        var stJammed = Store.LoadSettings();
                        stJammed.AppliedSettingsRev = 999;
                        stJammed.AppliedCalendarRev = 999;
                        stJammed.LastSettingsPushId = "";
                        Store.SaveSettings(stJammed);

                        var psJ = SchedulePackage.BuildPackageSettings(Store.LoadSettings(), includeSourceGroup: false);
                        SchedulePackage.IncludeCalendarGroup(psJ, Store.LoadSettings(), Store.LoadSchedule());

                        // ① 普通下发：校历进不来（这正是用户看到的现象），且不该偷偷执行重置
                        var pushJ = SettingsPushService.Push(feed, psJ, force: true);
                        if (!pushJ.Ok) throw new Exception("代次虚高时下发直接失败了：" + pushJ.Message);
                        var manJ = SchedulePackage.ParseManifest(
                            File.ReadAllText(Path.Combine(feedDir, "manifest.json")));
                        if (manJ == null) throw new Exception("读不到 manifest");
                        if (!string.IsNullOrEmpty(manJ.RevResetId))
                            throw new Exception("没要求重置，manifest 里却带了重置指令");
                        var apJ = SettingsPushService.ApplyFromManifest(manJ);
                        if (apJ.CalendarTouched)
                            throw new Exception("代次虚高时校历竟然也能落进去（守卫没生效）");
                        if (apJ.ResetDone) throw new Exception("普通下发不该执行重置");

                        // ② 带重置指令再推：教室机先清零，再把这一版内容真的落下去
                        var resetReq = new SettingsPushService.RevResetRequest { Applied = true };
                        var pushR = SettingsPushService.Push(feed, psJ, reset: resetReq);
                        if (!pushR.Ok || pushR.Skipped) throw new Exception("带重置的下发失败：" + pushR.Message);
                        var manR = SchedulePackage.ParseManifest(
                            File.ReadAllText(Path.Combine(feedDir, "manifest.json")));
                        if (manR == null || string.IsNullOrEmpty(manR.RevResetId) || !manR.RevResetApplied)
                            throw new Exception("manifest 没带上重置指令");

                        var apR = SettingsPushService.ApplyFromManifest(manR);
                        if (!apR.ResetDone) throw new Exception("教室机没有执行重置指令");
                        if (!apR.CalendarTouched)
                            throw new Exception("重置之后校历仍然进不来（这个功能的唯一目的没达成）");
                        var stAfterReset = Store.LoadSettings();
                        if (stAfterReset.AppliedCalendarRev != manR.Settings.CalendarRev)
                            throw new Exception($"重置后高水位不对（{stAfterReset.AppliedCalendarRev} vs "
                                              + $"{manR.Settings.CalendarRev}）");
                        if (stAfterReset.AppliedSettingsRev != manR.SettingsRev)
                            throw new Exception("重置后设置代次没有按本次下发重新记录");
                        if (stAfterReset.LastRevResetId != manR.RevResetId)
                            throw new Exception("重置令牌没有落盘（进程重启后会再执行一遍）");

                        // ③ 同一道指令只能执行一次：否则每次轮询都把代次打回 0，
                        //    教室机本地调整的设置会被下发值无休止地覆盖。
                        stAfterReset.AppliedCalendarRev = 42;
                        Store.SaveSettings(stAfterReset);
                        var apAgain = SettingsPushService.ApplyFromManifest(manR);
                        if (apAgain.ResetDone) throw new Exception("同一道重置指令被重复执行了");
                        if (Store.LoadSettings().AppliedCalendarRev != 42)
                            throw new Exception("重复执行把代次又清零了");

                        // ④ 本机窗体只清勾选的项，别顺手把发布端计数器也清了
                        var stLocal = Store.LoadSettings();
                        stLocal.AppliedCalendarRev = 77;
                        stLocal.SettingsPublishRev = 9;
                        stLocal.PackageRev = 11;
                        Store.SaveSettings(stLocal);
                        var done = RevResetService.Reset(new[] { RevResetService.RevKind.AppliedCalendar });
                        if (done.Count != 1) throw new Exception("重置返回的变更条数不对：" + done.Count);
                        var stL2 = Store.LoadSettings();
                        if (stL2.AppliedCalendarRev != 0)
                            throw new Exception("勾了「已应用教学日历代次」却没清零");
                        if (stL2.SettingsPublishRev != 9 || stL2.PackageRev != 11)
                            throw new Exception("重置顺手清掉了没勾的项");

                        // ⑤ 窗口结构：教室机 4 项 + 发布端 3 项，「只选教室机侧」必须整组选中
                        var rw = new RevResetWindow();
                        try
                        {
                            if (rw.DiagRowCount != 7 || rw.DiagClassroomRows != 4 || rw.DiagPublisherRows != 3)
                                throw new Exception($"代次维护窗分组不对（总 {rw.DiagRowCount} / 教室机 "
                                                  + $"{rw.DiagClassroomRows} / 发布端 {rw.DiagPublisherRows}）");
                            rw.DiagSelectOnly(true);
                            if (rw.DiagChecked.Count != 4)
                                throw new Exception("「只选教室机侧」没把 4 项都选上");
                            rw.DiagSelectOnly(false);
                            if (rw.DiagChecked.Count != 3)
                                throw new Exception("「只选发布端」没把 3 项都选上");
                        }
                        finally { rw.Close(); }
                    }
                    finally
                    {
                        try
                        {
                            var restoreSf = System.Text.Json.JsonSerializer.Deserialize<ScheduleFile>(schedBackup);
                            if (restoreSf != null) Store.SaveSchedule(restoreSf);
                        }
                        catch { }
                    }

                    return new LoginWindow();
                }
                finally
                {
                    // 诊断改过的设置全部还原，避免影响后续断言
                    if (storeBackup.Length > 0)
                    {
                        try
                        {
                            var restore = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(storeBackup);
                            if (restore != null) Store.SaveSettings(restore);
                        }
                        catch { }
                    }
                    try { System.IO.Directory.Delete(d, true); } catch { }
                }
            });

            // ★ r114：下发只写磁盘，界面控件不会自己变 —— 锁住「设置 → 提醒面板」这条重载路径。
            //   现场反馈：教室机下发后必须退出软件重登才看到提醒变化；而「启动显示面板」那项在独立
            //   设置窗口里，每次打开都读盘，看着"立刻生效"，于是被误判成"提醒下发失败"。
            Check("提醒面板：设置变了 ReloadReminderUi 能跟着刷新", () =>
            {
                string backup = System.Text.Json.JsonSerializer.Serialize(Store.LoadSettings());
                try
                {
                    var s = Store.LoadSettings();
                    s.Reminder ??= new ReminderSettings();
                    bool target = !s.Reminder.Enabled;
                    s.Reminder.Enabled = target;
                    Store.SaveSettings(s);

                    var w = new MainWindow();

                    // 程序里只有 Window_Loaded 会调 ReloadReminderUi；诊断不 Show 窗口，
                    // 这里反射直接调一次，验证重载本身能把控件刷成磁盘值。
                    var mi = w.GetType().GetMethod("ReloadReminderUi",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (mi == null)
                        throw new Exception("找不到 ReloadReminderUi（改名/删除？下发后的界面刷新会静默失效）");
                    mi.Invoke(w, null);

                    if (w.ChkRemindEnabled == null)
                        throw new Exception("提醒总开关控件缺失");
                    if (w.ChkRemindEnabled.IsChecked != target)
                        throw new Exception($"重载后提醒总开关没跟随设置（期望 {target}，实际 {w.ChkRemindEnabled.IsChecked}）");

                    return new LoginWindow();
                }
                finally
                {
                    try
                    {
                        var r = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(backup);
                        if (r != null) Store.SaveSettings(r);
                    }
                    catch { }
                }
            });

            // ★ r115：B3 的两块地基 —— ①设置文件戳会随写盘变化（切页判新）；
            //   ②变更分组能分清「提醒」与「同步」，下发后只重载真的变了的那一页。
            Check("设置刷新地基：文件戳随写盘变化 / 变更分组分得清提醒与同步", () =>
            {
                string backup = System.Text.Json.JsonSerializer.Serialize(Store.LoadSettings());
                try
                {
                    // 本段验证的就是"写盘 → 戳变化"，必须真的落盘（诊断默认可能处于不写盘状态）
                    bool oldPersist = Store.EnablePersistence;
                    Store.EnablePersistence = true;
                    try
                    {
                        var stamp0 = Store.SettingsStamp;
                        var stamp1 = stamp0;
                        // ★ 文件系统的写入时间戳有粒度（连续两次写可能落在同一刻度），
                        //   单次写盘后 stamp 可能恰好没变 —— 这是诊断的时序问题，不是产品 bug。
                        //   最多重试 5 次、每次等 30ms 越过一个刻度，并且每次写不同的内容，
                        //   保证在任何时间粒度下都能观察到戳变化。
                        for (int attempt = 0; attempt < 5 && stamp1 == stamp0; attempt++)
                        {
                            if (attempt > 0) System.Threading.Thread.Sleep(30);
                            var sProbe = Store.LoadSettings();
                            sProbe.LastUpdateCheckInfo = "stamp-probe-" + Guid.NewGuid().ToString("N")[..8];
                            Store.SaveSettings(sProbe);
                            stamp1 = Store.SettingsStamp;
                        }
                        if (stamp1 == stamp0)
                            throw new Exception($"改写 settings.json 后文件戳没变"
                                + $"（{stamp0:O} / {stamp1:O}，dir={Store.AppDir}，persist={Store.EnablePersistence}）");
                    }
                    finally { Store.EnablePersistence = oldPersist; }

                    var st = Store.LoadSettings();

                    // 情况一：内容完全一致 → 两组都不该报变更
                    var ps0 = SchedulePackage.BuildPackageSettings(st, includeSourceGroup: false);
                    var g0 = SettingsPushService.TouchedGroups(st, ps0);
                    if (g0.Reminder || g0.Sync)
                        throw new Exception("内容没变却报出了变更组（会导致无谓重载、冲掉正在输入的内容）");

                    // 情况二：只改提醒 → 只有提醒组
                    //   ★ 必须先克隆：BuildPackageSettings 里的 Reminder 与 st.Reminder 是同一个引用，
                    //     直接改会把"本机值"也改掉，比对结果永远相同。
                    var ps1 = SchedulePackage.BuildPackageSettings(st, includeSourceGroup: false);
                    ps1.Reminder = System.Text.Json.JsonSerializer.Deserialize<ReminderSettings>(
                        System.Text.Json.JsonSerializer.Serialize(st.Reminder))!;
                    ps1.Reminder.Enabled = !st.Reminder.Enabled;
                    var g1 = SettingsPushService.TouchedGroups(st, ps1);
                    if (!g1.Reminder) throw new Exception("只改提醒时没识别出提醒组");
                    if (g1.Sync) throw new Exception("只改提醒却被判成同步组变了");

                    // 情况三：只改同步开关 → 只有同步组
                    var ps2 = SchedulePackage.BuildPackageSettings(st, includeSourceGroup: false);
                    ps2.BoardSyncEnabled = !st.BoardSyncEnabled;
                    var g2 = SettingsPushService.TouchedGroups(st, ps2);
                    if (!g2.Sync) throw new Exception("只改同步开关时没识别出同步组");
                    if (g2.Reminder) throw new Exception("只改同步却被判成提醒组变了");

                    // 提醒页必须有 x:Name，切页刷新才挂得上
                    var w = new MainWindow();
                    if (w.TabReminder == null)
                        throw new Exception("提醒页缺少 TabReminder（切页自动重载会失效）");

                    // 端到端：设置文件被外部改过之后，切回「信息管理」页应当自动重载，不必重启软件
                    bool targetSync = !Store.LoadSettings().BoardSyncVoiceReminder;
                    var sSync = Store.LoadSettings();
                    sSync.BoardSyncVoiceReminder = targetSync;
                    Store.SaveSettings(sSync);

                    w.MainTabs.SelectedItem = w.TabReminder;      // 先离开信息页
                    w.MainTabs.SelectedItem = w.TabInfoManage;    // 再回来 → 文件戳变了，应当重载
                    if (w.ChkInfoVoice == null)
                        throw new Exception("信息管理页语音开关控件缺失");
                    if (w.ChkInfoVoice.IsChecked != targetSync)
                        throw new Exception($"切页没有重载信息管理页（期望 {targetSync}，实际 {w.ChkInfoVoice.IsChecked}）");

                    return new LoginWindow();
                }
                finally
                {
                    try
                    {
                        var r = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(backup);
                        if (r != null) Store.SaveSettings(r);
                    }
                    catch { }
                }
            });

            // ---------------- r143：悬浮窗标签去重 / 导入自定义分隔符 ----------------
            // 用户反馈：调休那天标题栏同时写着「按第12周周五上课」和绿胶囊「按12周周五」，同一句话说两遍。
            // 现在规则是「有校历标签时只显示一处，且用胶囊说完整句 + 加粗放大」。
            Check("r143 悬浮课表只显示一处「按第几周周几」", () =>
            {
                var sf = new ScheduleFile();
                sf.Classes.Add(new ClassSchedule { ClassName = "七（1）班" });
                sf.Calendar.Add(new SchoolCalendarRule
                {
                    Date = "2026-11-21", Kind = CalendarKind.Makeup, AsDate = "2026-11-18"   // 周六补周三
                });
                sf.Calendar.Add(new SchoolCalendarRule
                {
                    Date = "2026-11-20", Kind = CalendarKind.Off
                });

                var w = new ScheduleWindow();

                // ① 补课日：周次行让位，只留胶囊，胶囊是完整文案、字号 ≥13 且加粗
                var makeup = BoardBuilder.Build("七（1）班", "2026-09-01", sf, new DateTime(2026, 11, 21));
                if (string.IsNullOrEmpty(makeup.DayTag))
                    throw new Exception("补课日没算出校历标签（诊断前提不成立）");
                var m = w.DiagDayTagState(makeup);
                if (m.weekShown)
                    throw new Exception("补课日周次行还显示着，会和右侧胶囊重复（用户要「只显示一个」）");
                if (m.tag != makeup.DayTag)
                    throw new Exception($"胶囊应显示完整「{makeup.DayTag}」，实际「{m.tag}」");
                if (m.size < 13 || !m.bold)
                    throw new Exception($"胶囊不够醒目：字号 {m.size}、加粗 {m.bold}");

                // ② 放假日：周次与「今日放假」是两条信息，不算重复 → 周次行保留
                var off = BoardBuilder.Build("七（1）班", "2026-09-01", sf, new DateTime(2026, 11, 20));
                var o = w.DiagDayTagState(off);
                if (!o.weekShown) throw new Exception("放假日把周次行也隐藏了（那是两条不同信息）");
                if (o.tag != "今日放假") throw new Exception("放假日胶囊文案应为「今日放假」，实际「" + o.tag + "」");

                // ③ 普通日：没有校历标签，胶囊必须收起、周次行照常
                var normal = BoardBuilder.Build("七（1）班", "2026-09-01", sf, new DateTime(2026, 11, 19));
                var n = w.DiagDayTagState(normal);
                if (!n.weekShown) throw new Exception("普通日周次行被误隐藏");
                if (n.tag != "") throw new Exception("普通日不该出现校历胶囊：" + n.tag);
                return w;
            });

            // 智能导入原来只有 换行 / 空格 / 括号 三档，遇到「语文｜罗叶全」「数学·许同琛」这类
            // 自造分隔符的课表就拆不开。r143 增加「自定义符号」档位 + 探测器预填候选符号。
            Check("r143 智能导入支持自定义分隔符", () =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), "cb_r143_sep_check.xlsx");
                try
                {
                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        var ws = wb.AddWorksheet("课表");
                        ws.Cell(1, 1).Value = "七年级总课表";
                        ws.Cell(2, 1).Value = "节次";          // 节次标签列 = 1
                        ws.Cell(2, 3).Value = "星期一";        // 表头行 = 2；星期从第 3 列起（第 2 列留给班名）
                        ws.Cell(2, 4).Value = "星期二";
                        ws.Cell(3, 1).Value = "1";             // 第 1 节
                        ws.Cell(4, 1).Value = "2";             // 第 2 节
                        ws.Cell(3, 2).Value = "七（1）班";     // 名称列 = 2
                        ws.Cell(4, 2).Value = "七（1）班";
                        ws.Cell(3, 3).Value = "语文｜罗叶全";        // 周一第 1 节
                        ws.Cell(3, 4).Value = "数学｜许同琛";        // 周二第 1 节
                        ws.Cell(4, 3).Value = "体育｜吴文来｜操场";  // 周一第 2 节（带地点）
                        wb.SaveAs(tmp);
                    }

                    var hints = new ScheduleCore.Services.ScheduleTemplateProbe.TemplateHints
                    {
                        SheetIndex = 1,
                        DayOrientation = ScheduleCore.Services.ScheduleTemplateProbe.DayOrientation.Horizontal,
                        MainDimension = ScheduleCore.Services.ScheduleTemplateProbe.MainDimension.ClassesInRows,
                        DayHeaderIndex = 2,
                        PeriodLabelIndex = 1,
                        NameIndex = 2,
                        PeriodStyle = ScheduleCore.Services.ScheduleTemplateProbe.PeriodStyle.PlainNumber,
                        CellLayout = ScheduleCore.Services.ScheduleTemplateProbe.CellLayout.Custom,
                        CustomSeparator = "｜"
                    };
                    var res = ScheduleGeneralImporter.Parse(tmp, hints);
                    var cs = res.Schedule.Classes.FirstOrDefault();
                    if (cs == null) throw new Exception("自定义分隔符解析没有产出任何班级");
                    if (cs.ClassName != "七（1）班") throw new Exception("班级名取错：" + cs.ClassName);
                    if (!cs.Data.TryGetValue(0, out var d0) || !d0.TryGetValue(1, out var p1))
                        throw new Exception("周一第 1 节没解析出来");
                    if (p1.Count != 1 || p1[0].Subject != "语文" || p1[0].Teacher != "罗叶全")
                        throw new Exception($"「语文｜罗叶全」拆错了：{p1.Count} 节 / {p1[0].Subject} / {p1[0].Teacher}");
                    if (!cs.Data.TryGetValue(1, out var d1) || !d1.TryGetValue(1, out var t1)
                        || t1[0].Subject != "数学" || t1[0].Teacher != "许同琛")
                        throw new Exception("「数学｜许同琛」拆错了");
                    var p2 = cs.Data[0][2][0];
                    if (p2.Subject != "体育" || p2.Teacher != "吴文来" || p2.Location != "操场")
                        throw new Exception($"三段式「体育｜吴文来｜操场」拆错了：{p2.Subject}/{p2.Teacher}/{p2.Location}");

                    // 探测器要能自己看出「｜」是分隔符并预填，用户只需把档位切过去
                    var probe = ScheduleTemplateProbe.Probe(tmp);
                    if (!probe.Ok || probe.Candidates.Count == 0)
                        throw new Exception("探测器没认出这个文件");
                    if (probe.Best.CustomSeparator != "｜")
                        throw new Exception("探测器没预填自定义分隔符，实际「" + probe.Best.CustomSeparator + "」");
                }
                finally { try { File.Delete(tmp); } catch { } }
                return new System.Windows.Window();
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
        // 诊断：--doodle-dragtest 用真实系统鼠标拖右下角 / 拖板头，验缩放与移动真的生效
        // （r81 的教训：只验纯逻辑会得到"诊断全绿但真机拖不动"，所以这里必须发真鼠标消息）
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--doodle-dragtest", StringComparison.OrdinalIgnoreCase))
        {
            string dlog = e.Args.Length >= 2 ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.doodledrag.log");
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            int CheckCount = 0;
            // ★ 每步即时落盘：这条诊断会真的摆窗口、发鼠标消息，一旦某步卡死，
            // 只在末尾写 RESULT 会让人读到上一轮的旧日志、误判成"运行正常"。
            void Flush() { try { File.WriteAllText(dlog, sb.ToString()); } catch { } }
            void Check(string name, bool ok, string detail = "")
            {
                CheckCount++;
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name
                              + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (ok) pass++; else fail++;
                Flush();
            }

            sb.AppendLine("START " + DateTime.Now.ToString("HH:mm:ss"));
            Flush();

            try
            {
                string cbDir = (Environment.GetEnvironmentVariable("CB_DATA_DIR") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cbDir))
                {
                    sb.AppendLine("拒绝：请用 CB_DATA_DIR 指向数据副本后再运行（本诊断会模拟鼠标并可能落盘）。");
                    sb.AppendLine("RESULT FAIL  0/1");
                    try { File.WriteAllText(dlog, sb.ToString()); } catch { }
                    Shutdown(1);
                    return;
                }

                Store.SuppressWrites = true;   // 绝不动真实设置，连副本都不写

                var cfg = new DoodleBoardConfig
                {
                    Name = "拖动测试",
                    ThemeKey = DoodleBoardThemes.DefaultKey,
                    Mode = DoodleModes.Notes,
                    // ★ 不能太窄：否则第一张便签的手柄正好落在窗口右侧 14 DIP 缩放命中区里，
                    // 系统会把鼠标当"拖窗口"，看起来卡片变宽其实是整块板变宽，永远验不到便签缩放。
                    Width = 620,
                    Height = 420,
                    Left = 180,
                    Top = 150
                };

                var w = new DoodleBoardWindow(cfg)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    DiagAutoOverride = new System.Collections.Generic.List<AutoNote>
                    {
                        new()
                        {
                            Source = "通知", Module = DoodleModules.Notice, Id = "grip-1",
                            Title = "缩放测试", Body = "双击能改内容"
                        }
                    }
                };
                w.DiagRebuildNotes();
                w.Left = 180;
                w.Top = 150;
                w.Show();
                PumpMs(900);
                w.UpdateLayout();

                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
                double w0 = w.Width, h0 = w.Height;

                Check("板已显示且尺寸正确", w.IsVisible && w0 > 400 && h0 > 300,
                      $"{(int)w0}x{(int)h0} dpi={sx:0.##}");

                int px = (int)Math.Round((w.Left + w0 - 8) * sx);
                int py = (int)Math.Round((w.Top + h0 - 8) * sy);

                // ★ 板是「桌面底层」，跑诊断的机器上很容易被前台程序（浏览器等）压住，
                // 那时点击根本落不到板上 —— 那是"被遮挡当然点不到"，不是拖动链路的错。
                // 所以拖动这一段先把板临时置顶，只验"拖得动"；压底另用遮挡探针单独验。
                w.Topmost = true;
                w.Activate();
                PumpMs(400);
                IntPtr atCorner = WindowFromPoint(new DPOINT { X = px, Y = py });
                Check("拖动起点位于板自身（临时置顶后无遮挡）",
                      atCorner == w.DiagHandle,
                      $"under={DescribeHwnd(atCorner)} board={DescribeHwnd(w.DiagHandle)}");

                RealDrag(px, py, (int)Math.Round(520 * sx), (int)Math.Round(320 * sy));
                PumpMs(700);

                double w1 = w.Width, h1 = w.Height;

                // 程序刚 Show 完那几百毫秒里，系统偶尔不接这次按下（真机人手不会有这种竞争）。
                // 探针重试一次，避免把时序抖动记成"拖不动"。
                if (w1 - w0 < 200 || h1 - h0 < 120)
                {
                    PumpMs(500);
                    RealDrag(px, py, (int)Math.Round(520 * sx), (int)Math.Round(320 * sy));
                    PumpMs(700);
                    w1 = w.Width; h1 = w.Height;
                }
                // 拖到多少受系统 modal resize loop 丢消息影响，只要确实明显变大就算过
                Check("真实鼠标拖右下角：宽度变大", w1 - w0 > 200, $"{(int)w0} → {(int)w1}");
                Check("真实鼠标拖右下角：高度变大", h1 - h0 > 120, $"{(int)h0} → {(int)h1}");

                // ---- r94：真实系统鼠标拖便签右下角 ----
                // ★ 放在窗口缩放之后：小板上第一张卡的手柄贴着窗口右边，会被系统Resize吃掉；
                // 板放大后手柄才远离窗口边缘，探针验的才是"便签缩放"而不是"窗口缩放"。
                var gripPt = w.DiagFirstNoteGripCenter;
                var noteSize0 = w.DiagFirstNoteSize;
                Check("便签缩放手柄可见且命中区 ≥ 44",
                      gripPt.X > 0 && w.DiagFirstNoteGripHit >= 43.5,
                      $"hit={w.DiagFirstNoteGripHit:0.#} at=({gripPt.X:0},{gripPt.Y:0})");
                Check("便签有稳定拖动键用于记忆摆放", !string.IsNullOrWhiteSpace(w.DiagFirstNoteKey),
                      w.DiagFirstNoteKey);

                RealDrag((int)Math.Round(gripPt.X), (int)Math.Round(gripPt.Y),
                         (int)Math.Round(86 * sx), (int)Math.Round(54 * sy), 8);
                PumpMs(600);
                if (w.DiagFirstNoteSize.Width - noteSize0.Width < 50)
                {
                    PumpMs(400);
                    gripPt = w.DiagFirstNoteGripCenter;
                    RealDrag((int)Math.Round(gripPt.X), (int)Math.Round(gripPt.Y),
                             (int)Math.Round(86 * sx), (int)Math.Round(54 * sy), 8);
                    PumpMs(600);
                }
                var noteSize1 = w.DiagFirstNoteSize;
                Check("真实鼠标拖便签角：卡片变大",
                      noteSize1.Width - noteSize0.Width > 50 && noteSize1.Height - noteSize0.Height > 30,
                      $"{(int)noteSize0.Width}x{(int)noteSize0.Height} → {(int)noteSize1.Width}x{(int)noteSize1.Height}");
                var remembered = w.DiagFirstPlacement;
                Check("缩放结果写回当前板面的摆放记忆",
                      remembered?.Width is double rw && Math.Abs(rw - noteSize1.Width) < 2,
                      remembered?.Width is double rw2
                          ? $"{rw2:0}x{remembered.Height:0}" : "null");

                Check("便签模式工具按钮命中区 ≥ 44", w.DiagMinToolHit >= 43.5,
                      w.DiagMinToolHit.ToString("0.#"));
                w.SetMode(DoodleModes.Ink);
                PumpMs(400);
                Check("手写模式工具按钮命中区 ≥ 44", w.DiagMinToolHit >= 43.5,
                      w.DiagMinToolHit.ToString("0.#"));
                Check("手写模式工具带含笔/橡皮/清空图标", w.DiagToolIconCount >= 3,
                      w.DiagToolIconCount.ToString());
                w.SetMode(DoodleModes.Notes);
                PumpMs(300);

                double l0 = w.Left, t0 = w.Top;
                int hx = (int)Math.Round((w.Left + 70) * sx);
                int hy = (int)Math.Round((w.Top + 16) * sy);
                RealDrag(hx, hy, (int)Math.Round(-70 * sx), (int)Math.Round(-52 * sy), 6);
                PumpMs(600);

                if (Math.Abs(w.Left - l0) < 30 && Math.Abs(w.Top - t0) < 20)
                {
                    PumpMs(400);
                    RealDrag(hx, hy, (int)Math.Round(-70 * sx), (int)Math.Round(-52 * sy), 6);
                    PumpMs(600);
                }
                Check("真实鼠标拖板头：窗口移动",
                      Math.Abs(w.Left - l0) > 30 || Math.Abs(w.Top - t0) > 20,
                      $"({(int)l0},{(int)t0}) → ({(int)w.Left},{(int)w.Top})");

                Check("板头按钮区域仍可点击（关闭未被 HTCAPTION 吃掉）",
                      w.DiagCloseUsable);

                // ★ 尺寸上限单独验：直接设一个大尺寸，证明不再被 900×620 卡住。
                // 目标尺寸按工作区算 —— 窗口本来就不能大过工作区，写死 1400×900 在
                // 1080p + 125% 缩放的机器上一定会被系统夹到 816 高，那属于环境限制不是代码问题。
                var wa = SystemParameters.WorkArea;
                double tw = Math.Min(1200, wa.Width - 60);
                double th = Math.Min(720, wa.Height - 60);
                w.ApplyPreset(tw, th);
                PumpMs(400);
                Check("尺寸能设到 900×620 以上（不再有硬上限）",
                      Math.Abs(w.Width - tw) < 2 && Math.Abs(w.Height - th) < 2,
                      $"目标 {(int)tw}x{(int)th} 实得 {(int)w.Width}x{(int)w.Height}");

                // ---- 桌面底层：不能遮挡其他窗口 ----
                w.Topmost = false;          // 撤销"为了拖动而临时置顶"
                PumpMs(300);
                Check("板不在最前（WPF 与 WS_EX_TOPMOST 均为否）", w.DiagIsBottomMost);

                // 只有"改样式"是查不出遮挡的：必须真摆一个窗口压在板上，再用 WindowFromPoint
                // 问系统"这个点上最上层是谁"。r81 的教训就是光看代码全绿、真机上盖不住/点不动。
                var probe = new Window
                {
                    Title = "遮挡探针",
                    Width = 320,
                    Height = 220,
                    Left = w.Left + 80,
                    Top = w.Top + 80,
                    WindowStyle = WindowStyle.None,
                    Background = System.Windows.Media.Brushes.White,
                    ShowInTaskbar = false,
                    Topmost = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                probe.Show();
                probe.Activate();
                PumpMs(600);

                IntPtr probeHwnd = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
                IntPtr boardHwnd = w.DiagHandle;

                var pp = probe.PointToScreen(new System.Windows.Point(probe.Width / 2, probe.Height / 2));
                IntPtr under = WindowFromPoint(new DPOINT { X = (int)Math.Round(pp.X), Y = (int)Math.Round(pp.Y) });
                Check("普通窗口能压在板上面（板不遮挡其他窗口）",
                      under == probeHwnd,
                      $"under={DescribeHwnd(under)} probe={DescribeHwnd(probeHwnd)}");

                try { probe.Close(); } catch { }

                // ---- r96：真实鼠标在便签上方写字。只查 IsHitTestVisible 会是"诊断全绿、
                // 真机写不出字"，所以这里必须发真鼠标消息并数笔迹条数 ----
                w.Topmost = true;   // 临时提到最前，确保光标落点在板上而不是别的窗口
                PumpMs(300);
                w.DiagSetInkActive(true);
                PumpMs(250);
                int strokes0 = w.DiagInkStrokeCount;
                var sp = w.PointToScreen(new System.Windows.Point(w.Width * 0.22, 175));
                RealDrag((int)Math.Round(sp.X), (int)Math.Round(sp.Y),
                         (int)Math.Round(110 * sx), (int)Math.Round(34 * sy), 8);
                PumpMs(500);
                Check("真实鼠标在便签上方写出笔迹（手写层收到输入）",
                      w.DiagInkStrokeCount > strokes0,
                      $"{strokes0} → {w.DiagInkStrokeCount}");
                w.DiagSetInkActive(false);
                Check("取消手写工具后回到便签层",
                      w.DiagNotesHitTest && !w.DiagInkHitTest && !w.DiagInkActive);

                w.Close();
            }
            catch (Exception ex) { Check("诊断自身异常", false, ex.GetType().Name + ": " + ex.Message); }


            sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
            try { File.WriteAllText(dlog, sb.ToString()); } catch { }
            Shutdown(fail == 0 ? 0 : 1);
            return;
        }

        // 诊断：--doodle-check 涂鸦板的配置持久化 / 主题回落 / 尺寸夹取 / 笔迹 ISF 往返
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--doodle-check", StringComparison.OrdinalIgnoreCase))
        {
            string dlog = e.Args.Length >= 2 ? e.Args[1]
                : Path.Combine(Path.GetTempPath(), "ClassBulletinApp.doodlecheck.log");
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
                    sb.AppendLine("拒绝：请用 CB_DATA_DIR 指向数据副本后再运行（本诊断会写盘）。");
                    sb.AppendLine("RESULT FAIL  0/1");
                    try { File.WriteAllText(dlog, sb.ToString()); } catch { }
                    Shutdown(1);
                    return;
                }

                // 起始状态清空，避免上一轮残留影响计数
                var s0 = Store.LoadSettings();
                s0.DoodleBoards ??= new System.Collections.Generic.List<DoodleBoardConfig>();
                s0.DoodleBoards.Clear();
                Store.SaveSettings(s0);

                Check("初始状态 0 块板", (Store.LoadSettings().DoodleBoards?.Count ?? -1) == 0);

                var cfg = new DoodleBoardConfig
                {
                    Name = "测试板",
                    ThemeKey = "chalk",
                    Mode = DoodleModes.Notes,
                    Left = 120,
                    Top = 80,
                    Width = 560,
                    Height = 400
                };
                cfg.EnsurePage();   // 笔迹现在按「板 + 页」存，诊断也要拿到稳定的页 Id
                DoodleBoardService.SaveConfig(cfg);
                var s1 = Store.LoadSettings();
                Check("新建后设置里出现 1 块板", s1.DoodleBoards.Count == 1);
                Check("位置尺寸原样存回",
                      s1.DoodleBoards[0].Left == 120 && s1.DoodleBoards[0].Top == 80 &&
                      s1.DoodleBoards[0].Width == 560 && s1.DoodleBoards[0].Height == 400);

                cfg.ThemeKey = "linen";
                cfg.Mode = DoodleModes.Ink;
                DoodleBoardService.SaveConfig(cfg);
                var s2 = Store.LoadSettings();
                Check("换主题与用途能存进设置",
                      s2.DoodleBoards[0].ThemeKey == "linen" && s2.DoodleBoards[0].Mode == DoodleModes.Ink);
                Check("重复保存不会长出第二块板", s2.DoodleBoards.Count == 1);

                cfg.Notes.Add(new DoodleNote { Title = "收作业本", Body = "课前交" });
                DoodleBoardService.SaveConfig(cfg);
                var s3 = Store.LoadSettings();
                Check("手动便签能存回", s3.DoodleBoards[0].Notes.Count == 1 &&
                                        s3.DoodleBoards[0].Notes[0].Title == "收作业本");

                cfg.Notes.Clear();
                DoodleBoardService.SaveConfig(cfg);
                Check("删掉便签后设置里也没了",
                      (Store.LoadSettings().DoodleBoards[0].Notes?.Count ?? -1) == 0);

                // 可调样式：字号 / 字色 / 笔宽 / 笔色 / 橡皮都必须原样存回，
                // 否则用户调完一重启就复位，等于没做
                cfg.FontSize = 21;
                cfg.TextColor = "#C0392B";
                cfg.PenWidth = 14;
                cfg.PenColor = "#F09595";
                cfg.EraserSize = 40;
                DoodleBoardService.SaveConfig(cfg);
                var sty = Store.LoadSettings().DoodleBoards[0];
                Check("字号/字色/笔宽/笔色/橡皮能存回",
                      sty.FontSize == 21 && sty.TextColor == "#C0392B" && sty.PenWidth == 14 &&
                      sty.PenColor == "#F09595" && sty.EraserSize == 40);
                Check("样式参数走同一套夹取",
                      Math.Abs(DoodleStyle.ClampFont(99) - DoodleStyle.MaxFontSize) < 0.01 &&
                      Math.Abs(DoodleStyle.ClampFont(1) - DoodleStyle.MinFontSize) < 0.01 &&
                      Math.Abs(DoodleStyle.ClampPen(0) - DoodleStyle.MinPenWidth) < 0.01 &&
                      Math.Abs(DoodleStyle.ClampEraser(999) - DoodleStyle.MaxEraser) < 0.01);

                Check("未知主题键回落到黑板绿", DoodleBoardThemes.Get("不存在的主题").Key == "chalk");
                Check("空主题键回落到黑板绿", DoodleBoardThemes.Get(null).Key == "chalk");
                Check("尺寸下限夹到 300", Math.Abs(DoodleBoardMetrics.ClampWidth(10) - 300) < 0.01);
                // ★ 上限不再是 900×620：用户想拖多大就多大，只剩"手改 json 到离谱值"的绝对兜底
                Check("宽度不再被 900 卡住", Math.Abs(DoodleBoardMetrics.ClampWidth(1500) - 1500) < 0.01);
                Check("高度不再被 620 卡住", Math.Abs(DoodleBoardMetrics.ClampHeight(1500) - 1500) < 0.01);
                Check("离谱宽度回落到绝对兜底",
                      Math.Abs(DoodleBoardMetrics.ClampWidth(90000) - DoodleBoardMetrics.HardMaxWidth) < 0.01);
                Check("离谱高度回落到绝对兜底",
                      Math.Abs(DoodleBoardMetrics.ClampHeight(90000) - DoodleBoardMetrics.HardMaxHeight) < 0.01);
                Check("按工作区夹取（打开时别超出屏幕）",
                      Math.Abs(DoodleBoardMetrics.ClampWidthTo(2000, 1366) - 1366) < 0.01 &&
                      Math.Abs(DoodleBoardMetrics.ClampHeightTo(2000, 728) - 728) < 0.01);
                Check("高度下限夹到 220", Math.Abs(DoodleBoardMetrics.ClampHeight(5) - 220) < 0.01);

                var src = new DoodleBoardConfig { Name = "A" };
                src.Notes.Add(new DoodleNote { Title = "原标题" });
                var copy = src.Clone();
                copy.Name = "B";
                copy.Notes[0].Title = "改过";
                Check("Clone 深拷贝便签，不污染原对象",
                      src.Name == "A" && src.Notes[0].Title == "原标题");

                // 样式字段漏在 Clone 外面 = 界面上改了、存回去还是旧值，且极难发现
                var src2 = new DoodleBoardConfig
                {
                    FontSize = 22, TextColor = "#1F7A4D", PenWidth = 18,
                    PenColor = "#9FC4F0", EraserSize = 48
                };
                var copy2 = src2.Clone();
                Check("Clone 带上字号/字色/笔宽/笔色/橡皮",
                      copy2.FontSize == 22 && copy2.TextColor == "#1F7A4D" && copy2.PenWidth == 18 &&
                      copy2.PenColor == "#9FC4F0" && copy2.EraserSize == 48);

                // ---- 自动便签的字体格式（双击套用「悬浮便签」编辑窗）----
                // ★ r94 起键用模块键而不是中文来源：不同模块可能出现同名条目，且双击要靠它分派编辑窗
                Check("自动便签格式键 = 模块:条目Id",
                      new AutoNote { Source = "通知", Module = DoodleModules.Notice, Id = "abc123",
                                     Title = "明日大扫除" }.Key == "notice:abc123");
                Check("没有 Id 时退回模块:标题",
                      new AutoNote { Source = "作业", Module = DoodleModules.Homework,
                                     Title = "数学" }.Key == "homework:数学");

                cfg.EnsurePage();
                cfg.Pages[0].AutoStyles["notice:abc123"] = new DoodleAutoStyle
                {
                    Title = new TextStyle { Bold = false, Color = "#1E4FA3", SizeLevel = 5 },
                    Content = new TextStyle { Bold = true, Color = "#C0392B", SizeLevel = 1 }
                };
                DoodleBoardService.SaveConfig(cfg);

                var savedAuto = Store.LoadSettings().DoodleBoards[0];
                savedAuto.EnsurePage();
                DoodleAutoStyle? st1 = null;
                savedAuto.Pages[0].AutoStyles?.TryGetValue("notice:abc123", out st1);
                Check("单张自动便签的格式能存回当前板面",
                      st1?.Title?.Bold == false && st1?.Title?.Color == "#1E4FA3" &&
                      st1?.Title?.SizeLevel == 5 &&
                      st1?.Content?.Bold == true && st1?.Content?.Color == "#C0392B" &&
                      st1?.Content?.SizeLevel == 1);

                // 深拷贝：改副本不能污染原对象，否则「取消」形同虚设
                var copyAuto = savedAuto.Clone();
                copyAuto.Pages[0].AutoStyles["notice:abc123"].Title!.Bold = true;
                bool srcBoldAfter = savedAuto.Pages[0].AutoStyles["notice:abc123"].Title?.Bold ?? true;
                Check("AutoStyles 深拷贝（改副本不污染原对象）", srcBoldAfter == false);

                // ---- r94：多板面 / 自选模块 / 自定义模块 / 摆放记忆 ----
                var paged = new DoodleBoardConfig { Name = "多页测试" };
                var p1 = paged.EnsurePage();
                p1.Name = "上午";
                p1.Modules = new List<string> { DoodleModules.Notice, DoodleModules.Countdown };
                p1.Placements["notice:n1"] = new DoodleNotePlacement
                {
                    X = 24, Y = 36, Width = 320, Height = 180
                };
                var p2 = new DoodleBoardPage { Name = "下午" };
                p2.Modules = new List<string> { DoodleModules.Duty, DoodleModules.Custom };
                p2.CustomModules.Add(new DoodleCustomModule
                {
                    Id = "custom-1", Title = "班级公约", Body = "按时到校"
                });
                paged.Pages.Add(p2);
                paged.ActivePage = 1;
                DoodleBoardService.SaveConfig(paged);

                var savedPaged = Store.LoadSettings().DoodleBoards.First(x => x.Name == "多页测试");
                Check("多板面与当前页索引能存回",
                      savedPaged.Pages.Count == 2 && savedPaged.ActivePage == 1 &&
                      savedPaged.Pages[0].Name == "上午" && savedPaged.Pages[1].Name == "下午");
                Check("每页独立记住模块与自定义内容",
                      savedPaged.Pages[0].Modules.Contains(DoodleModules.Countdown) &&
                      savedPaged.Pages[1].Modules.Contains(DoodleModules.Duty) &&
                      savedPaged.Pages[1].CustomModules.Count == 1 &&
                      savedPaged.Pages[1].CustomModules[0].Id == "custom-1");
                var savedPlace = savedPaged.Pages[0].Placements["notice:n1"];
                Check("每张便签的位置尺寸按板面记住",
                      savedPlace.X == 24 && savedPlace.Y == 36 &&
                      savedPlace.Width == 320 && savedPlace.Height == 180);
                var pagedCopy = savedPaged.Clone();
                pagedCopy.Pages[0].Placements["notice:n1"].Width = 999;
                Check("Clone 深拷贝板面摆放（改副本不污染原对象）",
                      savedPaged.Pages[0].Placements["notice:n1"].Width == 320);

                var customNotes = DoodleAutoNotes.Load(
                    new List<string> { DoodleModules.Custom },
                    new List<DoodleCustomModule>
                    {
                        new() { Id = "c-9", Title = "今日提醒", Body = "带跳绳" }
                    });
                Check("自定义文字模块能折成自动便签",
                      customNotes.Count == 1 && customNotes[0].Module == DoodleModules.Custom &&
                      customNotes[0].Key == "custom:c-9" && customNotes[0].Body == "带跳绳");

                // 档位 → 倍率：基准档必须是 1.0，否则一打开编辑窗便签就跳号
                Check("字号倍率基准档（特大）= 1.0",
                      Math.Abs(DoodleBoardWindow.DiagScaleForLevel(HwTextStyle.ExtraLargeLevel) - 1.0) < 0.01);
                Check("更大档位倍率 > 1（能放大）",
                      DoodleBoardWindow.DiagScaleForLevel(6) > 1.01,
                      DoodleBoardWindow.DiagScaleForLevel(6).ToString("0.##"));
                Check("更小档位倍率 < 1（能缩小）",
                      DoodleBoardWindow.DiagScaleForLevel(4) < 0.99,
                      DoodleBoardWindow.DiagScaleForLevel(4).ToString("0.##"));

                // 渲染级：格式必须真的画到卡片上，而不只是存进 json
                {
                    var rw = new DoodleBoardWindow(new DoodleBoardConfig
                    {
                        Name = "格式测试",
                        ThemeKey = "chalk",
                        Mode = DoodleModes.Notes,
                        Width = 560,
                        Height = 460
                    });
                    rw.DiagAutoOverride = new System.Collections.Generic.List<AutoNote>
                    {
                        new() { Source = "通知", Id = "abc123", Title = "明日大扫除", Body = "上午 8:00" }
                    };
                    rw.DiagRebuildNotes();
                    double baseSize = rw.DiagAutoTitleFontSize;
                    Check("默认格式：标题加粗、基准字号",
                          rw.DiagAutoTitleBold && baseSize > 10, baseSize.ToString("0.#"));

                    rw.DiagApplyAutoStyle("notice:abc123", bold: false, color: "#1E4FA3", sizeLevel: 6);
                    Check("单张格式真的画到卡片上（不加粗 + 放大 + 变蓝）",
                          !rw.DiagAutoTitleBold &&
                          rw.DiagAutoTitleFontSize > baseSize + 0.5 &&
                          string.Equals(rw.DiagAutoTitleColor, "#FF1E4FA3", StringComparison.OrdinalIgnoreCase),
                          $"{rw.DiagAutoTitleFontSize:0.#} {rw.DiagAutoTitleColor}");

                    Check("格式覆盖只记了被改的那一张", rw.DiagAutoStyledCount == 1,
                          rw.DiagAutoStyledCount.ToString());

                    // 共用悬浮便签的编辑窗，但绝不能顺手在桌面多造一张自定义便签
                    int notesBefore = Store.LoadSettings().CustomNotes?.Count ?? 0;
                    var dlg = new CustomNoteEditWindow(new CustomNote
                    {
                        Id = "doodle:diag",
                        Title = "明日大扫除",
                        Content = "上午 8:00 各班组织",
                        TitleStyle = new TextStyle { Bold = false, Color = "#C0392B", SizeLevel = 5 },
                        ContentStyle = new TextStyle { Bold = true, Color = "#1F7A4D", SizeLevel = 1 }
                    }, formatOnly: true, boardTitleFont: 13, boardBodyFont: 11.5);
                    dlg.DiagCommitAsOk();

                    Check("格式模式下能带回两份格式",
                          dlg.ResultTitleStyle?.Bold == false && dlg.ResultTitleStyle?.Color == "#C0392B" &&
                          dlg.ResultTitleStyle?.SizeLevel == 5 &&
                          dlg.ResultContentStyle?.Bold == true && dlg.ResultContentStyle?.Color == "#1F7A4D");
                    Check("格式模式不会往自定义便签列表里塞东西",
                          (Store.LoadSettings().CustomNotes?.Count ?? 0) == notesBefore,
                          $"{notesBefore} → {Store.LoadSettings().CustomNotes?.Count ?? 0}");
                    dlg.Close();

                    rw.Close();

                    // 窗口关掉后把这块临时板从设置里摘掉，别影响后面「清空后 0 块板」
                    var cleanup = Store.LoadSettings();
                    cleanup.DoodleBoards?.RemoveAll(b => b.Name == "格式测试");
                    Store.SaveSettings(cleanup);
                }

                // ---- r94：真实窗口的板头 / 菜单 / 多板面 / 拖角手柄 / 双击分派 ----
                {
                    var uiCfg = new DoodleBoardConfig
                    {
                        Name = "r94诊断板",
                        ThemeKey = "chalk",
                        Mode = DoodleModes.Notes,
                        Width = 620,
                        Height = 500
                    };
                    uiCfg.Notes.Add(new DoodleNote { Title = "拖动测试", Body = "双击改内容" });
                    uiCfg.EnsurePage();
                    uiCfg.Pages.Add(new DoodleBoardPage
                    {
                        Name = "板面 2",
                        Modules = new List<string> { DoodleModules.Duty }
                    });

                    var ui = new DoodleBoardWindow(uiCfg);
                    string head = ui.DiagHeaderInfo;
                    Check("板头同时有板名、日期时间、页面码",
                          head.StartsWith("r94诊断板|") && head.Split('|').Length == 4 &&
                          head.Split('|')[2].Length >= 11 && head.EndsWith("页面 1/2"), head);

                    var menu = ui.DiagTopMenuHeaders;
                    Check("菜单含便签管理/黑板风格/新建板（页面已迁到板头）",
                          menu.Contains("便签管理") && menu.Contains("黑板风格") &&
                          menu.Contains("＋ 新建一块板") && !menu.Contains("板面"),
                          string.Join("/", menu));

                    var headBtns = ui.DiagHeadTextButtons;
                    Check("板头直达：便签管理 / 页面下拉",
                          headBtns.Contains("便签管理") && headBtns.Any(b => b.StartsWith("页面")),
                          string.Join("/", headBtns));

                    var noteMenu = ui.DiagNoteManageHeaders;
                    Check("便签管理含六类内容模块",
                          noteMenu.Contains("通知") && noteMenu.Contains("作业") &&
                          noteMenu.Contains("缺交") && noteMenu.Contains("课表") &&
                          noteMenu.Contains("倒计时") && noteMenu.Contains("值日"),
                          string.Join("/", noteMenu));
                    Check("便签管理含自定义/导入/清空/手动便签",
                          noteMenu.Any(h => h.Contains("新增手动便签")) &&
                          noteMenu.Any(h => h.Contains("自定义文字模块")) &&
                          noteMenu.Any(h => h.Contains("导入模块设置")) &&
                          noteMenu.Any(h => h.Contains("本页不放任何便签")),
                          string.Join("/", noteMenu));

                    Check("板头时间带年份",
                          System.Text.RegularExpressions.Regex.IsMatch(head.Split('|')[2], @"^\d{4}-\d{2}-\d{2}"),
                          head.Split('|')[2]);

                    // r98：便签正文 / 标题自动换行，去掉两行截断
                    Check("便签正文自动换行且无两行截断", ui.DiagAutoBodyWraps);
                    Check("便签标题也自动换行", ui.DiagAutoTitleWraps);

                    ui.DiagSetInkTool(eraser: false);
                    Check("手写笔模式只显示笔粗滑块 + 颜色胶囊",
                          ui.DiagHasPenSlider && !ui.DiagHasEraserSlider && ui.DiagHasColorCapsule);
                    ui.DiagSetInkTool(eraser: true);
                    Check("橡皮模式只显示橡皮范围滑块 + 颜色胶囊",
                          !ui.DiagHasPenSlider && ui.DiagHasEraserSlider && ui.DiagHasColorCapsule);
                    ui.DiagSetInkActive(false);
                    Check("便签层字色也收成颜色胶囊", ui.DiagHasColorCapsule);

                    // r108：锁定按钮挪进工具带（切换按钮左边）—— 锁住也一眼看得见，不会再卡死
                    ui.DiagSetLocked(true);
                    Check("工具带锁定按钮状态同步", ui.DiagLocked && ui.DiagLockText == "已锁定", ui.DiagLockText);
                    ui.DiagSetLocked(false);
                    Check("解锁后按钮恢复", !ui.DiagLocked && ui.DiagLockText == "锁定", ui.DiagLockText);
                    Check("锁定按钮在「手写/便签」切换按钮左边", ui.DiagLockBeforeModeButton);
                    Check("板名钉在工具带最右边（板的最右下角）",
                          ui.DiagTitleInToolBar && ui.DiagCornerTitle == "r94诊断板", ui.DiagCornerTitle);
                    // r99：切换按钮改回文字，避免与画笔图标并排成「两支笔」
                    Check("手写/便签切换按钮在工具带最前面且是文字", ui.DiagModeSwitchIsText);
                    Check("板头班级/时间贴着上沿（不再空一行）", ui.DiagHeadTopGap <= 12,
                          ui.DiagHeadTopGap.ToString("0"));
                    // r103：十套板面统一木色包边
                    Check("所有板面都有木色包边", ui.DiagWoodFrameIsWood,
                          ui.DiagWoodFrameWidth.ToString("0"));
                    // r103：卡片左上角不再有浅色模块名（标题即小标题）
                    Check("便签不再重复显示模块名", ui.DiagNoteHasNoSourceLabel);
                    // r104：行首科目单独一套字体格式（作业 / 缺交的科目名、课表的第N节）
                    Check("科目与内容字号默认就分开、科目加粗",
                          ui.DiagFirstNoteSubjectFontSize > ui.DiagFirstNoteBodyFontSize &&
                          ui.DiagFirstNoteSubjectBold,
                          $"{ui.DiagFirstNoteSubjectFontSize:0.#} / {ui.DiagFirstNoteBodyFontSize:0.#}");
                    ui.DiagSetSubjectStyle(30, "#C0392B", true);
                    Check("改科目格式后卡片上的科目真的跟着变",
                          Math.Abs(ui.DiagFirstNoteSubjectFontSize - 30) < 0.6 &&
                          ui.DiagFirstNoteSubjectColor.Contains("C0392B", StringComparison.OrdinalIgnoreCase),
                          $"{ui.DiagFirstNoteSubjectFontSize:0.#} {ui.DiagFirstNoteSubjectColor}");
                    // r102：板头并成一行 —— 班级/时间在左、菜单按钮在右，两者垂直居中
                    Check("板头只有一行（班级时间与菜单栏同一行）", ui.DiagHeadSingleRow);
                    Check("板头班级时间与菜单栏纵向对齐", ui.DiagHeadAlignDelta <= 6,
                          ui.DiagHeadAlignDelta.ToString("0.0"));

                    // r105：最小化 = 收成一条板名条（不能用 WindowState.Minimized，板没有任务栏按钮）
                    Check("板头有最小化按钮", ui.DiagHasMinimizeButton);
                    Check("收起高度正好一条板名条", ui.DiagCollapseIsCompact,
                          ui.DiagCollapsedHeight.ToString("0"));
                    ui.DiagCollapse(true);
                    Check("收起后板面整块隐藏、仍处于收起态", ui.DiagCollapsed && ui.DiagBodyHidden);
                    Check("收起后窗口真的变矮（只剩板名条）",
                          Math.Abs(ui.Height - ui.DiagCollapsedHeight) < 6, ui.Height.ToString("0"));
                    double expandedBefore = ui.DiagCollapsedHeight;
                    ui.DiagCollapse(false);
                    Check("再展开后板面回来、收起态清除", !ui.DiagCollapsed && !ui.DiagBodyHidden);
                    Check("展开后窗口高度回到展开态（不是板名条的高度）", ui.Height > expandedBefore + 100,
                          ui.Height.ToString("0"));
                    // r107：收起后点「展开」必须真的展开 —— r105 的按钮点击写死 SetCollapsed(true)
                    ui.DiagCollapse(true);
                    Check("收起后点「展开」按钮真的能展开", ui.DiagClickMinButtonAndReturns());

                    // r99：便签风格只作用于这一张卡，不改整块黑板材质
                    string boardThemeBefore = ui.DiagBoardThemeKey;
                    ui.DiagSetFirstAutoNoteTheme("blue");
                    Check("便签各自风格真的画到卡片底色（清新蓝）",
                          ui.DiagFirstNoteBackground.IndexOf("E3F2FD", StringComparison.OrdinalIgnoreCase) >= 0,
                          ui.DiagFirstNoteBackground);
                    Check("便签风格不改黑板材质（ThemeKey 不变）",
                          ui.DiagBoardThemeKey == boardThemeBefore, ui.DiagBoardThemeKey);

                    Check("每张便签都有右下角缩放手柄", ui.DiagGripCount >= 1,
                          ui.DiagGripCount.ToString());
                    Check("第一张便签有稳定拖动键", !string.IsNullOrWhiteSpace(ui.DiagFirstNoteKey),
                          ui.DiagFirstNoteKey);

                    ui.DiagToggleModule(DoodleModules.Schedule, on: true);
                    Check("菜单勾选后当前页真的加入展示模块",
                          ui.DiagModules.Contains(DoodleModules.Schedule));
                    ui.DiagToggleModule(DoodleModules.Schedule, on: false);
                    Check("取消勾选后模块从当前页移除",
                          !ui.DiagModules.Contains(DoodleModules.Schedule));

                    ui.DiagSwitchPage(1);
                    Check("切换页面后页码与模块一起换",
                          ui.DiagActivePage == 1 && ui.DiagPageLabel == "页面 2/2" &&
                          ui.DiagModules.Contains(DoodleModules.Duty), ui.DiagPageLabel);

                    // r97：清空曾经"点了没反应"，根因是空模块表被当成默认三类又填回来
                    ui.DiagClearPage();
                    Check("清空这一页后真的零模块零便签",
                          ui.DiagModules.Count == 0 && ui.DiagRenderedNoteCount == 0,
                          $"模块 {ui.DiagModules.Count} / 卡片 {ui.DiagRenderedNoteCount}");
                    ui.DiagAddPage();
                    Check("新建板面默认空白（便签只在第一板面）",
                          ui.DiagActivePage == 2 && ui.DiagModules.Count == 0 &&
                          ui.DiagRenderedNoteCount == 0,
                          $"第 {ui.DiagActivePage + 1} 页 / 模块 {ui.DiagModules.Count}");

                    Check("大屏窗的写回目标能定位到各类正式公示",
                          ui.DiagEditorFor(DoodleModules.Notice) == "NoticeEditor" &&
                          ui.DiagEditorFor(DoodleModules.Homework) == "HomeworkEditor" &&
                          ui.DiagEditorFor(DoodleModules.Missing) == "MissingEditor" &&
                          ui.DiagEditorFor(DoodleModules.Countdown) == "CountdownEditor" &&
                          ui.DiagEditorFor(DoodleModules.Duty) == "DutyEditor" &&
                          ui.DiagEditorFor(DoodleModules.Custom) == "CustomModuleEditor");
                    Check("课表没有单条可改内容，如实提示",
                          ui.DiagEditorFor(DoodleModules.Schedule) == "ScheduleHint");

                    ui.Close();
                    var cleanup2 = Store.LoadSettings();
                    cleanup2.DoodleBoards?.RemoveAll(b => b.Name == "r94诊断板");
                    Store.SaveSettings(cleanup2);
                }

                // 笔迹 ISF 往返
                string inkPath = DoodleBoardService.InkPath(cfg.Id, cfg.Pages[0].Id);
                Check("笔迹路径在 doodle 目录下且是 .isf",
                      inkPath.Replace('\\', '/').Contains("/doodle/") && inkPath.EndsWith(".isf"));

                var inkDir = Path.GetDirectoryName(inkPath);
                if (!string.IsNullOrEmpty(inkDir)) Directory.CreateDirectory(inkDir);

                var strokes = new System.Windows.Ink.StrokeCollection();
                var pts = new System.Windows.Input.StylusPointCollection(new[]
                {
                    new System.Windows.Input.StylusPoint(10, 10),
                    new System.Windows.Input.StylusPoint(40, 26),
                    new System.Windows.Input.StylusPoint(80, 18)
                });
                strokes.Add(new System.Windows.Ink.Stroke(pts));

                using (var fs = File.Create(inkPath)) strokes.Save(fs);
                Check("笔迹文件已落盘", File.Exists(inkPath) && new FileInfo(inkPath).Length > 0);

                int backCount = -1;
                using (var fs = File.OpenRead(inkPath))
                    backCount = new System.Windows.Ink.StrokeCollection(fs).Count;
                Check("笔迹 ISF 往返条数一致", backCount == 1, "读回 " + backCount);

                // ---- r95：收起 ≠ 删除；已收起的板必须能从管理入口找回来 ----
                var liveCfg = Store.LoadSettings().DoodleBoards.FirstOrDefault(b => b.Id == cfg.Id);
                Check("诊断板仍在设置里", liveCfg != null);
                if (liveCfg != null)
                {
                    var win = DoodleBoardService.Show(liveCfg);
                    Check("打开后登记为已显示", ReferenceEquals(DoodleBoardService.FindOpen(cfg.Id), win));

                    DoodleBoardService.CloseBoard(win, removeConfig: false);
                    Check("收起后配置仍在", Store.LoadSettings().DoodleBoards.Any(b => b.Id == cfg.Id));
                    Check("收起后笔迹仍在", File.Exists(inkPath));
                    Check("收起后不再登记为已显示", DoodleBoardService.FindOpen(cfg.Id) == null);

                    var again = DoodleBoardService.OpenOrActivate(cfg.Id);
                    Check("管理入口能重新打开收起的板",
                          again != null && ReferenceEquals(DoodleBoardService.FindOpen(cfg.Id), again));
                    var again2 = DoodleBoardService.OpenOrActivate(cfg.Id);
                    Check("重复打开不会长出第二块板",
                          ReferenceEquals(again, again2) &&
                          Store.LoadSettings().DoodleBoards.Count(b => b.Id == cfg.Id) == 1);

                    Check("改名成功", DoodleBoardService.RenameConfig(cfg.Id, "改名测试板"));
                    Check("改名写回设置",
                          Store.LoadSettings().DoodleBoards.First(b => b.Id == cfg.Id).Name == "改名测试板");
                    Check("改名同步到已打开窗口的板头", again!.Title == "改名测试板");

                    // 截图入口会往这两个静态字段塞假列表；这里必须读真实设置
                    DoodleBoardManagerWindow.DiagConfigsOverride = null;
                    DoodleBoardManagerWindow.DiagOpenIds = null;
                    var mgr = new DoodleBoardManagerWindow();
                    mgr.DiagRefresh();
                    Check("管理窗列出全部板",
                          mgr.DiagRowCount == Store.LoadSettings().DoodleBoards.Count,
                          mgr.DiagRowCount + " 行");
                    Check("管理窗汇总显示正在显示的板",
                          mgr.DiagSummary.Contains("正在显示"), mgr.DiagSummary);
                    mgr.Close();

                    // ★ 删除顺序：必须关完窗口（Closing 会写回配置）再删，否则板永远删不掉
                    DoodleBoardService.DeleteById(cfg.Id);
                    Check("删除后设置里没有这块板",
                          !Store.LoadSettings().DoodleBoards.Any(b => b.Id == cfg.Id));
                    Check("删除后笔迹一起清掉", !File.Exists(inkPath));
                    Check("删除后不再是已显示", DoodleBoardService.FindOpen(cfg.Id) == null);
                }

                // ---- r96：大字号 / 同页双层 / 按页笔迹 / 板头样式 / 更多主题 ----
                Check("黑板主题扩到 10 套以上",
                      DoodleBoardThemes.All.Count >= 10, DoodleBoardThemes.All.Count + " 套");

                // r102：每套板面都要「能写字 + 贴上去看得出卡片」，不能只有黑板绿能用
                {
                    static double Lum(string hex)
                    {
                        try
                        {
                            var c = (System.Windows.Media.Color)System.Windows.Media
                                .ColorConverter.ConvertFromString(hex);
                            static double Ch(double v)
                            {
                                v /= 255.0;
                                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                            }
                            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
                        }
                        catch { return 0.5; }
                    }
                    static double Ratio(string a, string b)
                    {
                        double la = Lum(a), lb = Lum(b);
                        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
                    }

                    bool inkOk = true, noteOk = true;
                    int chalkCount = 0;
                    string worst = "";
                    foreach (var t in DoodleBoardThemes.All)
                    {
                        double r = Ratio(t.Ink, t.Background);
                        if (r < 4.5) { inkOk = false; worst = t.Name + " " + r.ToString("0.0"); }
                        // 便签底色与板面至少差一个明度带（白板靠边框，阈值放到 0.05）
                        if (Math.Abs(Lum(t.NoteBackground) - Lum(t.Background)) < 0.05)
                        { noteOk = false; worst = t.Name + " 便签太接近板面"; }
                        if (t.Chalk) chalkCount++;
                    }
                    Check("每套板面墨色对比 ≥ 4.5:1（黑板上写字看得清）", inkOk, worst);
                    Check("每套板面的便签都与板面明显区分（贴上去有卡片感）", noteOk, worst);
                    Check("深色板都标记了粉笔模式（手写默认浅色笔）", chalkCount >= 4,
                          chalkCount + " 套");
                    Check("黑板绿仍是默认主题", DoodleBoardThemes.DefaultKey == "chalk",
                          DoodleBoardThemes.DefaultKey);
                }
                Check("大屏字号上限到 88",
                      Math.Abs(DoodleStyle.BigSizeOf(DoodleStyle.BigFontSizes.Length - 1) - 88) < 0.01);
                Check("字号夹取上限同步到 88",
                      Math.Abs(DoodleStyle.ClampFont(200) - 88) < 0.01);

                {
                    var big = new DoodleBigScreenEditWindow(new DoodleBigEditTarget
                    {
                        Title = "后排也看得清",
                        Body = "这是一段教室大屏正文",
                        Style = new DoodleAutoStyle { TitleSize = 88 }
                    });
                    Check("大屏编辑窗默认读到超大字号", big.Draft.TitleSize >= 87.99,
                          big.Draft.TitleSize.ToString("0"));
                    Check("大屏编辑窗正文字号随标题放大",
                          big.Draft.ContentSize > 40, big.Draft.ContentSize.ToString("0"));
                    big.Close();
                }

                // ---- r99：焦点格式条 / 便签风格 / 双输入框 / 强制换行 / 橡皮图标 ----
                string cutKept = DoodleAutoNotes.Cut("第一行\n第二行\n第三行", 60);
                Check("自动便签正文保留回车强制换行（Cut 不再压平）",
                      cutKept == "第一行\n第二行\n第三行", cutKept.Replace("\n", "\\n"));
                Check("Cut 仍按长度截断且不丢换行",
                      DoodleAutoNotes.Cut("甲\n乙", 3).Contains("\n"));

                {
                    var ed = new DoodleBigScreenEditWindow(new DoodleBigEditTarget
                    {
                        Title = "明日大扫除",
                        Body = "甲行\r\n乙行",
                        Style = new DoodleAutoStyle { TitleSize = 56 }
                    });
                    Check("编辑窗文字格式条启用大屏档位", ed.DiagPickerIsBigScreen);
                    Check("编辑窗文字格式条按焦点切换作用对象", ed.DiagPickerFollowsFocus);
                    Check("编辑窗主题风格来自悬浮便签主题表",
                          ed.DiagNoteThemeCount == HomeworkThemes.Presets.Count,
                          ed.DiagNoteThemeCount.ToString());
                    Check("编辑窗风格不再改整块黑板", ed.DiagThemeDoesNotTouchBoard);
                    ed.DiagSetSingleBody("第一段\n第二段");
                    ed.DiagCommit();
                    Check("单文本编辑窗保存回车强制换行",
                          ed.Draft.Body == "第一段\n第二段", ed.Draft.Body.Replace("\n", "\\n"));
                    Check("单文本编辑窗主题写入便签而非黑板",
                          ed.Draft.SaveNoteTheme && !ed.Draft.SaveTheme);
                    ed.Close();
                }

                {
                    // ★ 整份模式：条目来自真实公示数据（每条一个科目），不按正文换行拆
                    var hw = new DoodleBigScreenEditWindow(new DoodleBigEditTarget
                    {
                        Title = "各科作业公示",
                        Module = DoodleModules.Homework,
                        MultiEntry = true,
                        WholeBoard = true,
                        BoardTitle = "各科作业公示",
                        Entries = new List<DoodleBigEditEntry>
                        {
                            new() { Id = "hwA", Title = "数学", Body = "课本 P32 第 1-8 题" },
                            new() { Id = "hwB", Title = "语文", Body = "背诵课文\n默写古诗" }
                        }
                    });
                    Check("作业公示走「整份各科记录」模式", hw.DiagIsWholeBoard);
                    Check("作业公示用「添加科目」而不是增加条目",
                          hw.DiagAddButtonText.Contains("添加科目"), hw.DiagAddButtonText);
                    Check("每条记录 = 科目 + 内容两个输入框",
                          hw.DiagEntryFieldCount >= 5, hw.DiagEntryFieldCount.ToString());
                    Check("整份模式一条记录对应一条数据（不按正文换行拆）",
                          hw.DiagEntryCount == 2, hw.DiagEntryCount.ToString());
                    Check("整份模式带回公示条目的稳定 Id",
                          hw.DiagEntryId(0) == "hwA" && hw.DiagEntryId(1) == "hwB",
                          hw.DiagEntryId(0) + "/" + hw.DiagEntryId(1));
                    hw.DiagSetSubject(0, "数学", "课本 P32 第 1-8 题");
                    hw.DiagCommit();
                    Check("整份保存交出结构化条目（不是「科目：内容」拼接文本）",
                          hw.Draft.Entries.Count == 2 &&
                          hw.Draft.Entries[0].Id == "hwA" &&
                          hw.Draft.Entries[0].Title == "数学" &&
                          !hw.Draft.Entries[0].Body.Contains("语文"),
                          hw.Draft.Entries.Count.ToString());
                    Check("内容里的回车只影响排版、保留在字段内",
                          hw.Draft.Entries[1].Body.Contains("\n"));
                    hw.Close();
                }

                {
                    // ★ 整份落回：改一条、加一条、删一条 → 数据逐条对应，删除只打墓碑
                    var hb = new HomeworkBoard
                    {
                        Title = "各科作业公示",
                        Items = new List<HomeworkItem>
                        {
                            new() { Id = "d1", Subject = "数学", Content = "课本 P32" },
                            new() { Id = "d2", Subject = "语文", Content = "背诵课文" }
                        }
                    };
                    DoodleBoardWindow.ApplyHomeworkEntries(hb, new List<DoodleBigEditEntry>
                    {
                        new() { Id = "d1", Title = "数学", Body = "课本 P32 第 1-8 题" },
                        new() { Id = "", Title = "英语", Body = "单词表" }
                    });
                    Check("整份落回：改的改、加的新加、删的打墓碑",
                          hb.LiveCount == 2 &&
                          hb.Items.Any(x => !x.Deleted && x.Subject == "数学" && x.Content == "课本 P32 第 1-8 题") &&
                          hb.Items.Any(x => !x.Deleted && x.Subject == "英语") &&
                          hb.Items.Any(x => x.Id == "d2" && x.Deleted),
                          $"live={hb.LiveCount} total={hb.Items.Count}");
                    Check("删除只打墓碑、不物理移除（防对端旧变更单复活）",
                          hb.Items.Any(x => x.Id == "d2"));
                }

                {
                    var mb = new MissingBoard
                    {
                        Title = "缺交作业名单",
                        Items = new List<MissingHomeworkItem>
                        {
                            new() { Id = "m1", Subject = "数学", Students = "张三" }
                        }
                    };
                    DoodleBoardWindow.ApplyMissingEntries(mb, new List<DoodleBigEditEntry>
                    {
                        new() { Id = "m1", Title = "数学", Body = "张三、李四" },
                        new() { Id = "", Title = "英语", Body = "王五" }
                    });
                    Check("缺交名单也是一条记录一条数据",
                          mb.LiveCount == 2 &&
                          mb.Items.Any(x => !x.Deleted && x.Students == "张三、李四") &&
                          mb.Items.Any(x => !x.Deleted && x.Subject == "英语"),
                          $"live={mb.LiveCount}");
                }

                {
                    // ★ 值日：整份值日表（星期 / 姓名 / 值日内容），不再当成单条「今日值日」
                    var db = new DutyBoard
                    {
                        Title = "值日生名单",
                        Items = new List<DutyItem>
                        {
                            new() { Id = "dy1", Day = "周一", Names = "张三", Duty = "扫地" },
                            new() { Id = "dy2", Day = "周二", Names = "李四", Duty = "拖地" }
                        }
                    };
                    DoodleBoardWindow.ApplyDutyEntries(db, new List<DoodleBigEditEntry>
                    {
                        new() { Id = "dy1", Title = "周一", Body = "张三、王五", Extra = "扫地" },
                        new() { Id = "", Title = "周三", Body = "赵六", Extra = "擦窗" }
                    });
                    Check("值日表逐条落回：改的改、加的新加、删的打墓碑",
                          db.LiveCount == 2 &&
                          db.Items.Any(x => !x.Deleted && x.Day == "周一" &&
                                            x.Names == "张三、王五" && x.Duty == "扫地") &&
                          db.Items.Any(x => !x.Deleted && x.Day == "周三") &&
                          db.Items.Any(x => x.Id == "dy2" && x.Deleted),
                          $"live={db.LiveCount} total={db.Items.Count}");
                    Check("值日保存不再往岗位名 / 姓名里塞「今日值日」",
                          db.Items.All(x => x.Duty != "今日值日" && x.Names != "今日值日"));
                }

                {
                    // ★ 值日编辑窗：整份模式 + 三个字段（星期 / 姓名 / 值日内容）
                    var dw = new DoodleBigScreenEditWindow(new DoodleBigEditTarget
                    {
                        Module = DoodleModules.Duty,
                        MultiEntry = true,
                        WholeBoard = true,
                        BoardTitle = "值日生名单",
                        Entries = new List<DoodleBigEditEntry>
                        {
                            new() { Id = "dw1", Title = "周一", Body = "张三", Extra = "扫地" },
                            new() { Id = "dw2", Title = "周二", Body = "李四", Extra = "拖地" }
                        }
                    });
                    Check("值日编辑窗走「整份值日表」而不是单条",
                          dw.DiagIsWholeBoard && dw.DiagIsDuty);
                    Check("值日每条带稳定 Id 和「值日内容」第三字段",
                          dw.DiagEntryId(0) == "dw1" && dw.DiagEntryExtra(0) == "扫地",
                          dw.DiagEntryId(0) + "/" + dw.DiagEntryExtra(0));
                    dw.DiagSetDuty(0, "周一", "张三、王五", "扫地");
                    dw.DiagCommit();
                    Check("值日保存带的是整份标题（不是「今日值日」）且字段没串",
                          dw.Draft.Title == "值日生名单" &&
                          dw.Draft.Entries[0].Body == "张三、王五" &&
                          dw.Draft.Entries[0].Extra == "扫地",
                          dw.Draft.Title + "/" + dw.Draft.Entries[0].Extra);
                    dw.Close();
                }

                {
                    // ★ 勾「自定义」但一条都没建过 → 自动补一张空白卡，否则板上什么都不出
                    var pg = new DoodleBoardPage();
                    bool added = DoodleBoardWindow.EnsureCustomModule(pg);
                    var cn = DoodleAutoNotes.Load(new[] { DoodleModules.Custom }, pg.CustomModules);
                    Check("勾选「自定义」自动补一张空白卡，板上真的出现卡片",
                          added && pg.CustomModules.Count == 1 && cn.Count == 1,
                          $"added={added} modules={pg.CustomModules.Count} notes={cn.Count}");
                    Check("自动补的自定义卡有标题、可双击编辑",
                          cn.Count == 1 && cn[0].Title.Length > 0 && cn[0].Id.Length > 0,
                          cn.Count == 1 ? cn[0].Title : "");
                }

                {
                    // ★ 全部科目合成一张卡（与悬浮便签同形态）：正文一科一行。
                    //   走纯函数断言，不依赖写盘（诊断是游客模式，写盘不生效）。
                    var mn = DoodleAutoNotes.FromMissing(new MissingBoard
                    {
                        Title = "缺交作业名单",
                        Items = new List<MissingHomeworkItem>
                        {
                            new() { Id = "diagm1", Subject = "数学", Students = "甲" },
                            new() { Id = "diagm2", Subject = "语文", Students = "乙" },
                            new() { Id = "diagm3", Subject = "英语", Students = "丙" }
                        }
                    });
                    Check("缺交多科目合成一张卡（不再一科一张）", mn.Count == 1, "cards=" + mn.Count);
                    Check("缺交正文一科一行、科目与内容都在",
                          mn.Count == 1 && mn[0].Body.Split('\n').Length == 3 &&
                          mn[0].Body.Contains("数学：甲") && mn[0].Body.Contains("英语：丙"),
                          mn.Count == 1 ? mn[0].Body.Replace("\n", "\\n") : "");
                    Check("缺交合并卡是宽卡、标题用整份公示标题",
                          mn.Count == 1 && mn[0].Wide && mn[0].Title == "缺交作业名单",
                          mn.Count == 1 ? mn[0].Title : "");

                    var hn = DoodleAutoNotes.FromHomework(new HomeworkBoard
                    {
                        Items = new List<HomeworkItem>
                        {
                            new() { Id = "diagh1", Subject = "数学", Content = "P32" },
                            new() { Id = "diagh2", Subject = "语文", Content = "背诵" },
                            new() { Id = "diagh3", Subject = "英语", Content = "单词" }
                        }
                    });
                    Check("作业多科目合成一张卡", hn.Count == 1, "cards=" + hn.Count);
                    Check("作业正文一科一行且是宽卡",
                          hn.Count == 1 && hn[0].Wide &&
                          hn[0].Body.Split('\n').Length == 3 && hn[0].Body.Contains("数学：P32"),
                          hn.Count == 1 ? hn[0].Body.Replace("\n", "\\n") : "");
                }

                {
                    string sch = DoodleAutoNotes.BuildScheduleBody(
                        new[] { "第1节 语文", "第2节 数学", "第3节 英语" });
                    Check("今日课程一节课一行（不再「 · 」挤成一行）",
                          sch.Split('\n').Length == 3 && !sch.Contains("·"),
                          sch.Replace("\n", "\\n"));
                }

                {
                    // r104：课表卡拖高（诊断里固定 320）后，各行按卡片高度平均分布
                    var scw = DiagDoodleSchedule() as DoodleBoardWindow;
                    Check("课表卡拖高后各行平均分布（Star 行）", scw?.DiagScheduleRowsAreStar == true);
                }

                Check("橡皮图标换成参考图粗线稿",
                      DoodleIcons.Eraser(System.Windows.Media.Brushes.Black).StrokeThickness >= 2.2);

                {
                    var rcfg = new DoodleBoardConfig { Name = "r96诊断板" };
                    var rw = new DoodleBoardWindow(rcfg);
                    rw.DiagAutoOverride = new System.Collections.Generic.List<AutoNote>
                    {
                        new() { Source = "通知", Module = DoodleModules.Notice,
                                Id = "abc123", Title = "明日大扫除", Body = "上午 8:00" }
                    };
                    rw.DiagRebuildNotes();
                    double before = rw.DiagFirstNoteTitleFontSize;
                    rw.DiagApplyBigFont(88);
                    Check("大字号真的画到卡片上",
                          rw.DiagFirstNoteTitleFontSize > before + 20,
                          $"{before:0} → {rw.DiagFirstNoteTitleFontSize:0}");

                    Check("初始在便签层：便签可点、笔迹穿透",
                          rw.DiagNotesHitTest && !rw.DiagInkHitTest);
                    rw.DiagSetInkActive(true);
                    Check("选手写工具切到笔迹层：能写、便签只读",
                          rw.DiagInkActive && rw.DiagInkHitTest && !rw.DiagNotesHitTest);
                    rw.DiagSetInkActive(false);
                    Check("取消工具回到便签层", !rw.DiagInkActive && rw.DiagNotesHitTest);

                    rw.DiagApplyHeaderStyle(new DoodleHeaderStyle
                    {
                        TitleSize = 64, InfoSize = 42, Bold = true
                    });
                    Check("板头字号真的套到文字上",
                          Math.Abs(rw.DiagHeaderTitleFontSize - 64) < 0.01,
                          rw.DiagHeaderTitleFontSize.ToString("0"));
                    Check("板头样式深拷贝",
                          rcfg.Clone().HeaderStyle?.TitleSize == 64);

                    string ink1 = DoodleBoardService.InkPath(rcfg.Id, "p1");
                    string ink2 = DoodleBoardService.InkPath(rcfg.Id, "p2");
                    Check("不同页的笔迹文件互相独立", ink1 != ink2);
                    rw.Close();
                }

                // 兜底清空，诊断退出时副本回到 0 块（用户真实设置从不参与诊断）
                var s4 = Store.LoadSettings();
                s4.DoodleBoards?.Clear();
                Store.SaveSettings(s4);
                Check("兜底清空后 0 块板", (Store.LoadSettings().DoodleBoards?.Count ?? -1) == 0);
            }
            catch (Exception ex) { Check("诊断自身异常", false, ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine($"RESULT {(fail == 0 ? "PASS" : "FAIL")}  {pass}/{pass + fail}");
            try { File.WriteAllText(dlog, sb.ToString()); } catch { }
            Shutdown(fail == 0 ? 0 : 1);
            return;
        }

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

                    // ⑥e r111：通知 / 值日便签默认特大加粗；已有内容沿用原格式
                    var freshNotice = new ScheduleCore.Models.Notification();
                    BoardSyncService.EnsureNoticeBigStyle(freshNotice, null);
                    Check("新添加通知默认特大加粗",
                          freshNotice.TitleStyle?.Bold == true && freshNotice.TitleStyle.SizeLevel == xl
                          && freshNotice.ContentStyle?.Bold == true && freshNotice.ContentStyle.SizeLevel == xl,
                          $"T={freshNotice.TitleStyle?.Bold}/{freshNotice.TitleStyle?.SizeLevel} "
                          + $"C={freshNotice.ContentStyle?.Bold}/{freshNotice.ContentStyle?.SizeLevel}");

                    var oldNotice = new ScheduleCore.Models.Notification
                    {
                        TitleStyle = new ScheduleCore.Models.TextStyle { Bold = false, SizeLevel = 0 },
                        ContentStyle = new ScheduleCore.Models.TextStyle { Bold = false, SizeLevel = 1 }
                    };
                    var coveredNotice = new ScheduleCore.Models.Notification();
                    BoardSyncService.EnsureNoticeBigStyle(coveredNotice, oldNotice);
                    Check("覆盖已有通知沿用原字体格式",
                          coveredNotice.TitleStyle?.Bold == false && coveredNotice.TitleStyle.SizeLevel == 0
                          && coveredNotice.ContentStyle?.Bold == false && coveredNotice.ContentStyle.SizeLevel == 1);

                    var freshDuty = new ScheduleCore.Models.DutyBoard();
                    BoardSyncService.EnsureDutyBigStyle(freshDuty, hadContent: false);
                    Check("空值日板新添加默认特大加粗",
                          freshDuty.TitleStyle.Bold && freshDuty.TitleStyle.SizeLevel == xl
                          && freshDuty.ContentStyle.SizeLevel == xl
                          && freshDuty.DayStyle.SizeLevel == xl && freshDuty.DutyStyle.SizeLevel == xl);

                    var oldDuty = new ScheduleCore.Models.DutyBoard
                    {
                        TitleStyle = new ScheduleCore.Models.TextStyle { Bold = false, SizeLevel = 0 },
                        ContentStyle = new ScheduleCore.Models.TextStyle { SizeLevel = 1 }
                    };
                    BoardSyncService.EnsureDutyBigStyle(oldDuty, hadContent: true);
                    Check("已有内容的值日板保留原字体格式",
                          !oldDuty.TitleStyle.Bold && oldDuty.TitleStyle.SizeLevel == 0
                          && oldDuty.ContentStyle.SizeLevel == 1);
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
            var s0 = Store.LoadSettings();

            // r79：登录后默认只显示悬浮窗（悬浮课表 + 上次开着的各种便签），控制面板留到需要时再开。
            // 兜底：如果一个悬浮窗都不会自动出现，仍然把控制面板打开 ——
            // 否则用户双击图标后看到的是「什么也没发生」，以为程序坏了。
            bool anyFloat = s0.AutoShowSchedule || s0.AutoShowNotification || s0.AutoShowHomework
                            || s0.AutoShowMissing || s0.AutoShowNotice
                            || s0.CustomNotes?.Any(c => c.Visible) == true
                            || s0.PlacementOf(CountdownWindow.PlacementKey).Visible
                            || s0.PlacementOf(DutyWindow.PlacementKey).Visible;
            bool hidePanel = !s0.ShowPanelOnStart && anyFloat;

            var win = new MainWindow();
            MainWindow = win;

            if (hidePanel)
            {
                // 先透明再 Show：首帧不会闪出一整块控制面板。
                // ★ 用 ContentRendered 而不是 Loaded：升级说明窗挂在 Loaded 上，
                //   必须等窗口真正显示出来、它弹完再收，否则会把「知道了」一起藏掉。
                win.Opacity = 0;
                win.ShowInTaskbar = false;
            }

            win.Show();

            if (hidePanel)
            {
                win.ContentRendered += (_, _) =>
                {
                    try
                    {
                        win.Hide();
                        win.Opacity = 1;
                        win.ShowInTaskbar = true;
                    }
                    catch (Exception ex) { LogCrash("HidePanelOnStart", ex); }
                };
            }

            EnsureHandwritingBall();
            try { BoardSyncNotifier.Start(); } catch { }   // 二期：轮询摆渡目录 + 课间弹窗（未启用时零副作用）
        }
        catch (Exception ex)
        {
            LogCrash("MainWindow startup", ex);
            Shutdown(1);
        }

        // 诊断：--windows-check <输出 txt> —— 走完真实启动流程（登录 + 按设置开窗）后，
        // 把每个窗口的「是否可见」写进文件再退出。用于验证「登录后只显示悬浮窗、不打开控制面板」，
        // 这东西没法靠 --shot 验（截图是单独构造窗口，不经过启动逻辑）。
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--windows-check", StringComparison.OrdinalIgnoreCase))
        {
            string outFile = e.Args[1];
            var probe = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            probe.Tick += (_, _) =>
            {
                probe.Stop();
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (Window w in Windows)
                    {
                        string title = "";
                        try { title = w.Title ?? ""; } catch { }
                        sb.AppendLine($"{(w.IsVisible ? "VISIBLE" : "HIDDEN ")}\t{title}\t{w.GetType().Name}" +
                                      $"\tvis={w.Visibility} state={w.WindowState} loaded={w.IsLoaded} op={w.Opacity:0.##} at={w.Left:0},{w.Top:0}");
                    }
                    File.WriteAllText(outFile, sb.ToString());
                }
                catch (Exception ex) { LogCrash("windows-check", ex); }
                Shutdown(0);
            };
            probe.Start();
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
    // ===== 真实输入探针用（--doodle-dragtest）=====
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref DPOINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(DPOINT pt);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder buf, int max);

    /// <summary>诊断：把句柄描述成「类名/标题」，方便判断遮挡者到底是谁。</summary>
    private static string DescribeHwnd(IntPtr h)
    {
        if (h == IntPtr.Zero) return "0";
        var cls = new System.Text.StringBuilder(256);
        var txt = new System.Text.StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        GetWindowText(h, txt, txt.Capacity);
        return $"{cls}|{txt}";
    }

    /// <summary>Win32 POINT（物理像素）。★ 不要用 System.Windows.Point 顶替：那个是 double 且是 DIP。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DPOINT { public int X; public int Y; }
    private const uint MouseDownFlag = 0x0002;
    private const uint MouseUpFlag = 0x0004;

    /// <summary>跑消息泵指定毫秒。诊断里要等窗口布局 / 事件回调，不能 Thread.Sleep。</summary>
    private static void PumpMs(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 真实鼠标拖拽：从 <paramref name="fromPx"/> 按下，分几步移到 <paramref name="deltaPx"/>。
    /// <para>★ 必须在后台线程发消息：返回 HTCAPTION / HTBOTTOMRIGHT 后系统会进 modal resize/move loop，
    /// 主线程的 Dispatcher 被占住，同步发消息会自己把自己锁死。</para>
    /// </summary>
    private static void RealDrag(int fromPx, int fromPy, int deltaDx, int deltaDy, int steps = 10)
    {
        var worker = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(260);
            SetCursorPos(fromPx, fromPy);
            System.Threading.Thread.Sleep(90);
            mouse_event(MouseDownFlag, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(90);

            for (int i = 1; i <= steps; i++)
            {
                SetCursorPos(fromPx + deltaDx * i / steps, fromPy + deltaDy * i / steps);
                System.Threading.Thread.Sleep(55);
            }

            mouse_event(MouseUpFlag, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(160);
        })
        { IsBackground = true };

        worker.Start();

        // ★ 不能 Join：主线程被阻塞时，系统根本没机会处理"按下"这条消息，
        // 10 条移动消息会全挤在队列里被合并/丢弃，实测只生效 20% 左右（拖 520 只涨 119）。
        // 改成边发边泵消息，这样按下→拖动→松手是逐帧真实发生的，和老师用手拖一样。
        int waited = 0;
        while (worker.IsAlive && waited < 6000)
        {
            PumpMs(40);
            waited += 40;
        }
    }


    /// <para>自动便签走 <see cref="DoodleBoardWindow.DiagAutoOverride"/> 注入，
    /// 因为诊断副本里本来就没有今天的通知作业，直接读只会拍到空态、看不出卡片长什么样。</para>
    /// </summary>
    /// <summary>诊断：便签板上有一张自动便签套用了自定义格式（验证格式真的落到卡片上）。</summary>
    /// <summary>--shot receipts：造四台机的假回执，核对发布端面板的汇总卡与四态排版。</summary>
    private static Window DiagReceiptShot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cbreceiptshot-" + Guid.NewGuid().ToString("N")[..6]);
        var src = new FolderFeedSource(dir);
        const string sha = "A1B2C3D4E5F6A7B8-SHOT";
        UpdateReceiptService.Write(src, new UpdateReceipt
        {
            ActorId = "a-shot01", DeviceName = "一楼一教室", TargetSha = sha,
            State = "applied", PackageDone = true, ClassCount = 18, OverrideAdded = 6,
            NoticeAdded = 2, UserCount = 9, AppliedSettingsRev = 7, AppliedCalendarRev = 3,
            AppVersion = AppInfo.VersionText
        });
        UpdateReceiptService.Write(src, new UpdateReceipt
        {
            ActorId = "a-shot02", DeviceName = "二楼三教室", TargetSha = "OLD-PACKAGE-SHA-SHOT",
            State = "checked", ClassCount = 18, AppVersion = AppInfo.VersionText
        });
        UpdateReceiptService.Write(src, new UpdateReceipt
        {
            ActorId = "a-shot03", DeviceName = "三楼二教室", TargetSha = sha,
            State = "failed", ReasonCode = "DOWNLOAD_FAIL",
            ReasonText = "下载失败：数据包校验不通过", ClassCount = 18, AppVersion = AppInfo.VersionText
        });
        UpdateReceiptService.Write(src, new UpdateReceipt
        {
            ActorId = "a-shot04", DeviceName = "四楼一教室", TargetSha = sha,
            State = "applied", ClassCount = 18, AppVersion = AppInfo.VersionText
        }, createdAt: DateTime.Now.AddHours(-4));
        return new UpdateReceiptWindow(src, sha, 30);
    }

    private static Window DiagDoodleFormatted()
    {
        var cfg = new DoodleBoardConfig
        {
            Name = "班级公告",
            ThemeKey = "chalk",
            Mode = DoodleModes.Notes,
            Width = 560,
            Height = 460
        };
        cfg.Notes.Add(new DoodleNote { Title = "收作业本", Body = "下午第一节课前" });
        cfg.EnsurePage();
        cfg.Pages[0].AutoStyles["notice:abc"] = new DoodleAutoStyle
        {
            Title = new TextStyle { Bold = false, Color = "#C0392B", SizeLevel = 5 },
            Content = new TextStyle { Bold = true, Color = "#1E4FA3", SizeLevel = 5 }
        };

        var w = new DoodleBoardWindow(cfg)
        {
            DiagAutoOverride = new System.Collections.Generic.List<AutoNote>
            {
                new() { Source = "通知", Module = DoodleModules.Notice, Id = "abc",
                        Title = "明日大扫除", Body = "上午 8:00 各班组织" },
                new() { Source = "作业", Module = DoodleModules.Homework, Id = "all", Wide = true,
                        Title = "各科作业公示",
                        Body = "数学：练习册 P32–P35\n语文：背诵课文第二段\n英语：单词表 Unit 3\n物理：实验报告" },
                new() { Source = "缺交", Module = DoodleModules.Missing, Id = "all", Wide = true,
                        Title = "缺交作业名单",
                        Body = "数学：王芳、李明\n英语：陈昊" }
            }
        };
        // 构造时已按真实数据画过一遍，注入假数据后必须重画一次，否则截到的是副本里的真通知
        w.DiagRebuildNotes();
        return w;
    }

    /// <summary>诊断：黑板管理窗；注入三块假板，一次看到「正在显示 / 已收起」两种状态。</summary>
    private static Window DiagDoodleManager()
    {
        var a = new DoodleBoardConfig
        {
            Id = "diag-mgr-a",
            Name = "高三(2)班 · 今日公示",
            ThemeKey = "chalk",
            Mode = DoodleModes.Notes,
            Width = 900,
            Height = 520
        };
        a.Pages.Add(new DoodleBoardPage
        {
            Name = "上午",
            Notes = new System.Collections.Generic.List<DoodleNote>
            {
                new() { Title = "收作业本", Body = "第一节课前" },
                new() { Title = "大扫除", Body = "下午第三节课" }
            }
        });

        var b = new DoodleBoardConfig
        {
            Id = "diag-mgr-b",
            Name = "数学作业手写板",
            ThemeKey = "brand",
            Mode = DoodleModes.Ink,
            Width = 760,
            Height = 560
        };

        var c = new DoodleBoardConfig
        {
            Id = "diag-mgr-c",
            Name = "值日安排",
            ThemeKey = "linen",
            Mode = DoodleModes.Notes,
            Width = 640,
            Height = 480
        };
        c.Pages.Add(new DoodleBoardPage
        {
            Name = "值日",
            Notes = new System.Collections.Generic.List<DoodleNote>
            {
                new() { Title = "值日生", Body = "李明、王芳" }
            }
        });

        DoodleBoardManagerWindow.DiagConfigsOverride =
            new System.Collections.Generic.List<DoodleBoardConfig> { a, b, c };
        DoodleBoardManagerWindow.DiagOpenIds =
            new System.Collections.Generic.HashSet<string> { a.Id, b.Id };
        return new DoodleBoardManagerWindow();
    }

    /// <summary>诊断：大字号实景（高对比主题 + 56 号标题 + 放大板头）。</summary>
    private static Window DiagDoodleBigFont()
    {
        var cfg = new DoodleBoardConfig
        {
            Name = "教室大屏公示",
            ThemeKey = "contrast",
            Mode = DoodleModes.Notes,
            Width = 1200,
            Height = 820
        };
        cfg.HeaderStyle = new DoodleHeaderStyle { TitleSize = 32, InfoSize = 22, Bold = true };
        cfg.EnsurePage();
        cfg.Pages[0].Modules = new List<string> { DoodleModules.Notice, DoodleModules.Homework, DoodleModules.Missing };
        cfg.Pages[0].AutoStyles["notice:abc"] = new DoodleAutoStyle
        {
            TitleSize = 56,
            ContentSize = 36,
            Title = new TextStyle { Color = "#FFE081" }
        };
        return new DoodleBoardWindow(cfg)
        {
            DiagAutoOverride = new System.Collections.Generic.List<AutoNote>
            {
                new() { Source = "通知", Module = DoodleModules.Notice, Id = "abc",
                        Title = "明日大扫除", Body = "上午 8:00 各班组织，请提前五分钟到场" },
                new() { Source = "作业", Module = DoodleModules.Homework, Id = "all", Wide = true,
                        Title = "各科作业公示",
                        Body = "数学：练习册 P32–P35\n语文：背诵课文第二段\n英语：单词表 Unit 3\n物理：实验报告" },
                new() { Source = "缺交", Module = DoodleModules.Missing, Id = "all", Wide = true,
                        Title = "缺交作业名单",
                        Body = "数学：王芳、李明\n英语：陈昊" }
            }
        };
    }

    /// <summary>诊断：独立的大屏编辑窗（字号 / 颜色 / 主题一个窗口里改）。</summary>
    private static Window DiagDoodleBigScreenEdit() =>
        new DoodleBigScreenEditWindow(new DoodleBigEditTarget
        {
            Title = "明日大扫除",
            Body = "上午 8:00 各班在操场集合，请班主任提前五分钟到场。",
            Style = new DoodleAutoStyle { TitleSize = 56, ContentSize = 40 }
        });

    /// <summary>r100 诊断：课表便签 —— 验证一节课一行，不再把所有课用「 · 」挤成一行。</summary>
    private static Window DiagDoodleSchedule()
    {
        var cfg = new DoodleBoardConfig
        {
            Name = "今日课程",
            ThemeKey = DoodleBoardThemes.DefaultKey,
            Mode = DoodleModes.Notes,
            Width = DoodleBoardMetrics.DefaultWidth,
            Height = DoodleBoardMetrics.DefaultHeight
        };
        cfg.EnsurePage();
        cfg.Pages[0].Modules = new System.Collections.Generic.List<string> { DoodleModules.Schedule };

        // 优先用真实课表折出来的便签；诊断副本里没课表时退回 8 节样例，保证截图有意义
        var auto = DoodleAutoNotes.Load(new[] { DoodleModules.Schedule });
        if (auto.Count == 0)
        {
            var periods = new[]
            {
                ("第1节", "语文"), ("第2节", "数学"), ("第3节", "英语"), ("第4节", "物理"),
                ("第5节", "化学"), ("第6节", "体育"), ("第7节", "历史"), ("第8节", "自习")
            };
            auto.Add(new AutoNote
            {
                Source = "今日", Module = DoodleModules.Schedule, Id = "今日", Title = "今日课程",
                Body = DoodleAutoNotes.BuildScheduleBody(
                    periods.Select(p => p.Item1 + " " + p.Item2)),
                Lines = periods.Select(p => new AutoLine
                {
                    Subject = p.Item1, Text = p.Item2, Separator = " "
                }).ToList()
            });
        }

        // r104：把课表卡放到 320 高 —— 验证「拖高后各行按卡片高度平均分布」
        foreach (var n in auto)
        {
            cfg.Pages[0].Placements[n.Key] = new DoodleNotePlacement
            {
                X = 12, Y = 12,
                Width = DoodleBoardMetrics.DefaultWidth - 40, Height = 320
            };
        }

        var w = new DoodleBoardWindow(cfg) { DiagAutoOverride = auto };
        w.DiagRebuildNotes();
        return w;
    }

    /// <summary>r99 诊断：作业公示编辑窗，验「+ 添加科目」与每条两个输入框的版式。</summary>
    private static Window DiagDoodleHomeworkEdit() =>
        new DoodleBigScreenEditWindow(new DoodleBigEditTarget
        {
            Title = "各科作业公示",
            Module = DoodleModules.Homework,
            MultiEntry = true,
            WholeBoard = true,
            BoardTitle = "各科作业公示",
            Entries = new List<DoodleBigEditEntry>
            {
                new() { Id = "hw1", Title = "数学", Body = "课本 P32 第 1-8 题" },
                new() { Id = "hw2", Title = "语文", Body = "背诵课文第二段\n默写古诗两首" },
                new() { Id = "hw3", Title = "英语", Body = "抄写单词表 Unit 3" }
            },
            Style = new DoodleAutoStyle { TitleSize = 42, ContentSize = 32 }
        });

    /// <summary>r101 诊断：值日整份编辑窗，验「星期 / 值日生姓名 / 值日内容」三段与整周记录。</summary>
    private static Window DiagDoodleDutyEdit() =>
        new DoodleBigScreenEditWindow(new DoodleBigEditTarget
        {
            Module = DoodleModules.Duty,
            MultiEntry = true,
            WholeBoard = true,
            BoardTitle = "值日生名单",
            Entries = new List<DoodleBigEditEntry>
            {
                new() { Id = "dy1", Title = "周一", Body = "小天，小地，小朱", Extra = "扫地" },
                new() { Id = "dy2", Title = "周二", Body = "小东，小王，小明", Extra = "拖地" },
                new() { Id = "dy3", Title = "周三", Body = "小小，大大，小张", Extra = "擦窗" },
                new() { Id = "dy4", Title = "周四", Body = "小一，小二，小古", Extra = "倒垃圾" },
                new() { Id = "dy5", Title = "周五", Body = "一一，三三，四四", Extra = "扫地" }
            },
            Style = new DoodleAutoStyle { TitleSize = 42, ContentSize = 32 }
        });

    /// <summary>诊断：板头文字格式窗。</summary>
    private static Window DiagDoodleHeaderStyle() =>
        new DoodleHeaderStyleWindow(new DoodleHeaderStyle
        {
            TitleSize = 50, InfoSize = 32, Bold = true
        });

    /// <summary>诊断：自动便签的「只改格式」编辑窗（来自涂鸦板双击）。</summary>
    private static Window DiagAutoStyleWindow()
    {
        var draft = new CustomNote
        {
            Id = "doodle:diag:通知:abc123",
            Title = "明日大扫除",
            Content = "上午 8:00 各班组织，班主任到场",
            TitleStyle = new TextStyle { Bold = true, SizeLevel = HwTextStyle.ExtraLargeLevel },
            ContentStyle = new TextStyle { Bold = false, Color = "#1E4FA3", SizeLevel = 5 }
        };
        return new CustomNoteEditWindow(draft, formatOnly: true,
            boardTitleFont: 13, boardBodyFont: 11.5);
    }

    private static Window DiagDoodleBoard(string theme, string mode, bool seedInk,
        double? width = null, double? height = null, double? fontSize = null)
    {
        var cfg = new DoodleBoardConfig
        {
            Name = "班级公告",
            ThemeKey = theme,
            Mode = mode,
            FontSize = fontSize,
            // 手写模式底部有"工具 + 颜色"两条带，按手写默认尺寸截才能看出真实手感
            Width = width ?? (mode == DoodleModes.Ink ? DoodleBoardMetrics.InkDefaultWidth : DoodleBoardMetrics.DefaultWidth),
            Height = height ?? (mode == DoodleModes.Ink ? DoodleBoardMetrics.InkDefaultHeight : DoodleBoardMetrics.DefaultHeight)
        };
        cfg.Notes.Add(new DoodleNote { Title = "收作业本", Body = "下午第一节课前" });
        cfg.Notes.Add(new DoodleNote { Title = "家长会 周四", Body = "19:00 三楼会议室" });

        var w = new DoodleBoardWindow(cfg)
        {
            DiagAutoOverride = new System.Collections.Generic.List<AutoNote>
            {
                new() { Source = "通知", Module = DoodleModules.Notice, Id = "n1",
                        Title = "明日大扫除", Body = "上午 8:00 各班组织" },
                // r102：作业 / 缺交全部科目合成一张卡（一科一行），与悬浮便签同形态
                // r104：样例也带结构化 Lines —— 否则截图里科目那一段还是和内容同一种字，看不出格式
                new() { Source = "作业", Module = DoodleModules.Homework, Id = "all", Wide = true,
                        Title = "各科作业公示",
                        Body = "数学：练习册 P32–P35\n语文：背诵课文第二段\n英语：单词表 Unit 3",
                        Lines = new List<AutoLine>
                        {
                            new() { Subject = "数学", Text = "练习册 P32–P35" },
                            new() { Subject = "语文", Text = "背诵课文第二段" },
                            new() { Subject = "英语", Text = "单词表 Unit 3" }
                        } },
                new() { Source = "缺交", Module = DoodleModules.Missing, Id = "all", Wide = true,
                        Title = "缺交作业名单",
                        Body = "数学：王芳、李明\n语文：张强",
                        Lines = new List<AutoLine>
                        {
                            new() { Subject = "数学", Text = "王芳、李明" },
                            new() { Subject = "语文", Text = "张强" }
                        } }
            }
        };
        w.DiagRebuildNotes();
        if (seedInk) w.DiagSeedInk();
        return w;
    }

    /// <summary>收起态截图：只看那一条板名条长什么样。</summary>
    private static Window DiagDoodleBoardCollapsed()
    {
        var w = (DoodleBoardWindow)DiagDoodleBoard(DoodleBoardThemes.DefaultKey, DoodleModes.Notes, false);
        w.DiagCollapse(true);
        return w;
    }

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

    /// <summary>r131 诊断截图：通知条徽章两态。change=true 注入当天换课，false 注入最近倒计时。</summary>
    private static Window DiagNotifyBadge(bool change)
    {
        var w = new NotificationWindow();
        var today = DateTime.Today;
        string cls = Store.LoadSettings().SelectedClass ?? "七年级(9)班";
        w.Loaded += (_, _) =>
        {
            w.DiagShowCountdownBadge = true;
            if (change)
            {
                int day = ((int)today.DayOfWeek + 6) % 7;
                w.DiagTodayOverrides = new List<LessonOverride>
                {
                    new LessonOverride { ClassName = cls, DayIdx = day, Period = 3, Subject = "语文", Teacher = "张老师", Type = "代", Source = "manual" },
                    new LessonOverride { ClassName = cls, DayIdx = day, Period = 6, Subject = "自习", Type = "调", Source = "temp" },
                    new LessonOverride { ClassName = cls, DayIdx = day, Period = 7, Subject = "体育", Teacher = "李老师", Type = "调", Source = "manual" }
                };
            }
            else
            {
                w.DiagTodayOverrides = new List<LessonOverride>();
                w.DiagCountdown = new CountdownBoard { Items =
                {
                    new CountdownItem { Name = "期末考试", TargetDate = today.AddDays(30).ToString("yyyy-MM-dd") }
                }};
            }
            w.RefreshBadge();
        };
        return w;
    }

    /// <summary>r143 诊断截图：智能导入预览窗，造一份用「｜」分隔科目与教师的小表并切到自定义档位。</summary>
    private static Window DiagImportPreview()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "cb_diag_import.xlsx");
        try
        {
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.AddWorksheet("七年级总课表");
                ws.Cell(1, 1).Value = "七年级总课表";
                ws.Cell(2, 1).Value = "节次";
                ws.Cell(2, 3).Value = "星期一"; ws.Cell(2, 4).Value = "星期二"; ws.Cell(2, 5).Value = "星期三";
                for (int i = 0; i < 4; i++)
                {
                    ws.Cell(3 + i, 1).Value = (i + 1).ToString();
                    ws.Cell(3 + i, 2).Value = "七（1）班";
                    ws.Cell(3 + i, 3).Value = "语文｜罗叶全";
                    ws.Cell(3 + i, 4).Value = "数学｜许同琛";
                    ws.Cell(3 + i, 5).Value = "英语｜李惠燕";
                }
                wb.SaveAs(tmp);
            }
        }
        catch { }
        var w = new ImportPreviewWindow(tmp);
        w.DiagSelectCustomSeparator();
        return w;
    }

    /// <summary>r142 诊断截图：作息编辑弹窗（带一份填好的作息，便于核对排版与两个保存按钮）。</summary>
    private static Window DiagPeriodDialog()
    {
        UserSession.SignIn(new AppUser { Username = "diag-admin", Role = UserRole.Admin });
        var p = ScheduleTemplates.CreateWithBuiltIns().PeriodTemplates
            .FirstOrDefault(x => x.Name.Contains("初中")) ?? new PeriodTemplate { Name = "全日制" };
        var tpl = new PeriodTemplate { Name = string.IsNullOrWhiteSpace(p.Name) ? "全日制" : p.Name, IsDefault = true, IsBuiltIn = p.IsBuiltIn };
        ScheduleSheetText.ParseScheduleSheet(ScheduleSheetText.FormatScheduleSheet(p), tpl);
        return new PeriodTemplateDialog(tpl, canManageData: true);
    }

    /// <summary>r142 诊断截图：常用科目库编辑弹窗。</summary>
    private static Window DiagSubjectDialog()
    {
        var lib = ScheduleTemplates.CreateWithBuiltIns().SubjectLibraries.FirstOrDefault()
                  ?? new SubjectLibrary { Name = "本校常用" };
        return new SubjectLibraryDialog(lib);
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

    /// <summary>诊断截图：手动录入窗口（带一个班的课 + 已保存的课程设置 + 一个课表方案）。</summary>
    private static Window DiagManualWindow()
    {
        var t = ScheduleTemplates.CreateWithBuiltIns();
        var sf = new ScheduleFile();
        var cls = new ClassSchedule { ClassName = "七年级(1)班" };
        string[,] demo = { { "语文", "数学", "英语", "物理", "历史" }, { "数学", "英语", "语文", "化学", "体育" }, { "英语", "语文", "数学", "生物", "音乐" } };
        for (int p = 1; p <= 3; p++)
        {
            for (int d = 0; d < 5; d++)
            {
                if (!cls.Data.TryGetValue(d, out var dm)) cls.Data[d] = dm = new Dictionary<int, List<Lesson>>();
                dm[p] = new List<Lesson> { new() { Period = p, Subject = demo[p - 1, d], Teacher = "任课老师", Week = 0 } };
            }
        }
        sf.Classes.Add(cls);

        var pairs = new List<SubjectPair>
        {
            new() { Subject = "语文", Teacher = "张老师", Location = "1 班教室", WeeklyPeriods = 5 },
            new() { Subject = "数学", Teacher = "陈老师", Location = "1 班教室", WeeklyPeriods = 5 },
            new() { TwoLessons = true, Subject = "生物", Teacher = "樊老师", Location = "生物室",
                    Subject2 = "地理", Teacher2 = "梁老师", Location2 = "地理室", WeeklyPeriods = 2 },
            new() { Subject = "体育", Teacher = "王老师", Location = "操场", WeeklyPeriods = 3 }
        };
        t.UpsertSetup(new ClassSetup
        {
            ClassName = "七年级(1)班",
            PeriodTemplateName = "初中 7 节",
            SubjectLibraryName = "本校常用",
            SavedAt = DateTime.Now,
            Pairs = pairs
        });
        t.Plans.Add(new SavedSchedulePlan { Name = "七年级标准课表", SavedAt = DateTime.Now, PeriodTemplateName = "初中 7 节", Data = cls.Data });
        return new ManualScheduleWindow(sf, t);
    }

    /// <summary>诊断截图：单格编辑器（一格两门形态，用来核对第 2 门展开后的排版）。</summary>
    private static Window DiagCellEditorWindow()
    {
        var lib = ScheduleTemplates.CreateWithBuiltIns().DefaultSubjectLibrary;
        var pairs = new List<SubjectPair>
        {
            new() { Subject = "语文", Teacher = "张老师", Location = "1 班教室", Week = 0 },
            new() { TwoLessons = true, Subject = "生物", Teacher = "樊老师", Location = "生物室",
                    Subject2 = "地理", Teacher2 = "梁老师", Location2 = "地理室" }
        };
        return new CellEditorWindow(1, 3, new List<Lesson>
        {
            new() { Period = 3, Subject = "生物", Teacher = "樊老师", Location = "生物室", Week = 1 },
            new() { Period = 3, Subject = "地理", Teacher = "梁老师", Location = "地理室", Week = 2 }
        }, lib, pairs);
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

                // ★「全部清空」路径：上面只清了已终结，这里剩的全是未处理记录 ——
                //   必须被全部清掉（原 bug：all=true 只算数不删，提示已清 N 条但列表纹丝不动）。
                int leftBeforeAll = BoardSyncService.LoadState().Records.Count;
                var clrAll = BoardSyncService.ClearLedger(true);
                var afterAll = BoardSyncService.LoadState();
                Check("教师「全部清空」台账：未处理记录也一并清掉",
                      clrAll.Ok && leftBeforeAll > 0 && afterAll.Records.Count == 0,
                      clrAll.Message + " / 清前=" + leftBeforeAll + " 清后=" + afterAll.Records.Count);
                Check("全部清空提示的条数与清前总数一致",
                      clrAll.Message.Contains("已清除 " + leftBeforeAll + " 条"),
                      clrAll.Message);
                // 恢复台账，别让本段清空影响 ⑬ 段的 Send / Pull 断言（它们依赖这里的记录）
                BoardSyncService.SaveState(afterClr);
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
            // ---- r146：课表更新回执（文件夹通道 / 序号 / 四态判定 / 脱敏 / 发布端面板） ----
            string rdir = Path.Combine(Path.GetTempPath(), "cbreceiptdiag-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var rsrc = new FolderFeedSource(rdir);
                const string curSha = "SHA-CURRENT-DIAG";

                var r0 = new UpdateReceipt
                {
                    ActorId = "a-diag01", DeviceName = "CLASS-ROOM-01",
                    TargetSha = curSha, State = "applied", PackageDone = true,
                    ClassCount = 12, OverrideAdded = 3, AppVersion = "diag"
                };
                long s1 = UpdateReceiptService.Write(rsrc, r0);
                long s2 = UpdateReceiptService.Write(rsrc, r0);
                int fileCount = rsrc.ListStatus().Count;
                Check("r146 回执：同机序号递增且只留一份文件",
                      s1 == 1 && s2 == 2 && fileCount == 1,
                      $"Seq {s1}→{s2}，文件 {fileCount} 份");

                UpdateReceiptService.Write(rsrc, new UpdateReceipt
                {
                    ActorId = "a-diag02", DeviceName = "CLASS-ROOM-02",
                    TargetSha = "SHA-OLD-DIAG", State = "applied", AppVersion = "diag"
                });
                string dirty = "下载失败：C:\\feed\\data.cbpack ftp://u:p@host/x 密码 pwd=123456";
                string clean = UpdateReceiptService.SafeReason(dirty);
                UpdateReceiptService.Write(rsrc, new UpdateReceipt
                {
                    ActorId = "a-diag03", DeviceName = "CLASS-ROOM-03",
                    TargetSha = curSha, State = "failed",
                    ReasonCode = "DOWNLOAD_FAIL", ReasonText = clean, AppVersion = "diag"
                });
                UpdateReceiptService.Write(rsrc, new UpdateReceipt
                {
                    ActorId = "a-diag04", DeviceName = "CLASS-ROOM-04",
                    TargetSha = curSha, State = "applied", AppVersion = "diag"
                }, createdAt: DateTime.Now.AddHours(-5));

                Check("r146 回执：错误文本去掉路径 / 地址 / 密码",
                      clean.Contains("下载失败") && !clean.Contains("C:\\") &&
                      !clean.Contains("ftp://") && !clean.Contains("123456"), clean);

                var (rsum, rdevs) = UpdateReceiptService.Collect(rsrc, curSha, 30, persistSeen: false);
                Check("r146 回执：四态判定 最新 / 落后 / 失败 / 失联",
                      rdevs.Count == 4 && rsum.Total == 4 &&
                      rdevs.Any(d => d.ActorId == "a-diag01" && d.Code == "latest") &&
                      rdevs.Any(d => d.ActorId == "a-diag02" && d.Code == "behind") &&
                      rdevs.Any(d => d.ActorId == "a-diag03" && d.Code == "failed") &&
                      rdevs.Any(d => d.ActorId == "a-diag04" && d.Code == "stale"),
                      string.Join(",", rdevs.Select(d => d.ActorId + ":" + d.Code)));

                // 序号倒退：先让服务记住高水位，再手工写一份更小的序号
                UpdateReceiptService.Collect(rsrc, curSha, 30, persistSeen: true);
                var rolled = new UpdateReceipt
                {
                    ActorId = "a-diag02", DeviceName = "CLASS-ROOM-02",
                    TargetSha = curSha, State = "applied", Seq = 0, AppVersion = "diag"
                };
                rsrc.WriteStatusOwn(UpdateReceiptService.FileName("a-diag02"),
                    System.Text.Json.JsonSerializer.Serialize(rolled));
                var (_, rdevs2) = UpdateReceiptService.Collect(rsrc, curSha, 30, persistSeen: false);
                Check("r146 回执：序号倒退标异常而不是静默相信",
                      rdevs2.Any(d => d.ActorId == "a-diag02" && d.Code == "invalid"),
                      string.Join(",", rdevs2.Select(d => d.ActorId + ":" + d.Code)));

                var rwin = new UpdateReceiptWindow(rsrc, curSha, 30);
                Check("r146 回执：发布端面板列全设备并给出详情",
                      rwin.DiagRowCount == 4 && rwin.DiagStates.Contains("latest") &&
                      rwin.DiagStates.Contains("stale"), rwin.DiagStates);
                rwin.DiagSelect(0);
                Check("r146 回执：详情含设备 Id 与读写数",
                      rwin.DiagDetailText.Contains("设备 Id") && rwin.DiagDetailText.Contains("读写数"),
                      rwin.DiagDetailText[..Math.Min(60, rwin.DiagDetailText.Length)]);
                rwin.DiagSetAlias("a-diag01", "一教室");
                Check("r146 回执：别名只存在发布机本地",
                      rwin.DiagNames.Contains("一教室"), rwin.DiagNames);
                rwin.DiagSetAlias("a-diag01", "");
                rwin.Close();
            }
            catch (Exception rex) { Check("回执诊断自身异常", false, rex.GetType().Name + ": " + rex.Message); }
            finally { try { if (Directory.Exists(rdir)) Directory.Delete(rdir, true); } catch { } }

            // ---- r147：待办角标（口径 / 冲突捞回 / 撤回排除 / 游客隐藏 / 角标换算） ----
            try
            {
                var tst = new BoardSyncState { ActorId = "diag" };
                tst.Records.Add(new BoardMessageRecord
                {
                    MsgId = "m1", Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StDelivered, Seen = false, Order = new BoardChangeOrder()
                });
                tst.Records.Add(new BoardMessageRecord
                {
                    MsgId = "m2", Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StSeen, Seen = true, Order = new BoardChangeOrder()
                });
                var tc = PendingTodoService.Compute(tst, classroomSide: true);
                Check("r147 待办：未裁决收件算待办、已阅未办仍算",
                      tc.Todo == 2 && tc.NeedDecision == 2 && tc.SeenButPending == 1,
                      $"待办 {tc.Todo} / 待裁决 {tc.NeedDecision} / 已阅未办 {tc.SeenButPending}");

                var tst2 = new BoardSyncState { ActorId = "diag" };
                tst2.Records.Add(new BoardMessageRecord
                {
                    MsgId = "c1", Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StConflict, Order = new BoardChangeOrder()
                });
                tst2.Records.Add(new BoardMessageRecord
                {
                    MsgId = "v1", Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StVerified, Order = new BoardChangeOrder()
                });
                var revokeOrder = new BoardChangeOrder();
                revokeOrder.Op = BoardSyncConsts.OpRevoke;
                tst2.Records.Add(new BoardMessageRecord
                {
                    MsgId = "r1", Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StDelivered, Order = revokeOrder
                });
                var tc2 = PendingTodoService.Compute(tst2, classroomSide: true);
                Check("r147 待办：冲突从终结态捞回、撤回单排除",
                      tc2.Todo == 1 && tc2.Conflict == 1, $"待办 {tc2.Todo} / 冲突 {tc2.Conflict}");

                var tst3 = new BoardSyncState { ActorId = "diag" };
                tst3.Records.Add(new BoardMessageRecord
                {
                    MsgId = "out1", Direction = BoardSyncConsts.DirOut,
                    Status = BoardSyncConsts.StDelivered, Order = new BoardChangeOrder()
                });
                tst3.Records.Add(new BoardMessageRecord
                {
                    MsgId = "in1", Direction = BoardSyncConsts.DirIn,
                    Status = BoardSyncConsts.StDelivered, Order = new BoardChangeOrder()
                });
                Check("r147 待办：教师机数发件、教室机数收件（方向分流）",
                      PendingTodoService.Compute(tst3, classroomSide: true).Todo == 1 &&
                      PendingTodoService.Compute(tst3, classroomSide: false).Todo == 1);

                Check("r147 角标文字：0 空 / 99+ 封顶",
                      PendingTodoService.BadgeText(0) == "" &&
                      PendingTodoService.BadgeText(7) == "7" &&
                      PendingTodoService.BadgeText(150) == "99+");

                using (var icon = TrayBadgeService.Badged(3))
                    Check("r147 角标：绘制图标成功且原始图标是系统共享",
                          icon.Width >= 16 && icon.Height >= 16 &&
                          TrayBadgeService.IsSystemIcon(TrayBadgeService.Plain));
            }
            catch (Exception tex) { Check("待办角标诊断自身异常", false, tex.GetType().Name + ": " + tex.Message); }

            // ---- r148：今日页读回执只汇总、不消费「已见」状态 ----
            try
            {
                string ddir = Path.Combine(Path.GetTempPath(), "cbtodayreceipt-" + Guid.NewGuid().ToString("N")[..6]);
                var dsrc = new FolderFeedSource(ddir);
                const string todaySha = "SHA-TODAY-DIAG";
                UpdateReceiptService.Write(dsrc, new UpdateReceipt
                {
                    ActorId = "a-diag-today", DeviceName = "CLASS-TODAY",
                    TargetSha = todaySha, State = "applied", AppVersion = "diag"
                });
                string seenPath = Path.Combine(Store.AppDir, "receipt-seen.json");
                string before = File.Exists(seenPath) ? File.ReadAllText(seenPath) : "<none>";
                var (dsum, _) = UpdateReceiptService.Collect(dsrc, todaySha, 30, persistSeen: false);
                string seenAfter = File.Exists(seenPath) ? File.ReadAllText(seenPath) : "<none>";
                Check("r148 今日页回执只读：汇总有效且不写已见标记",
                      dsum.Total == 1 && string.Equals(before, seenAfter, StringComparison.Ordinal),
                      $"设备 {dsum.Total}，已见标记 {(before == seenAfter ? "未变" : "被改写")}");
                try { if (Directory.Exists(ddir)) Directory.Delete(ddir, true); } catch { }
            }
            catch (Exception dex) { Check("今日页回执诊断自身异常", false, dex.GetType().Name + ": " + dex.Message); }

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
