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
            Check("LayoutWindow", () => new LayoutWindow { InitialTabIndex = 2 });
            Check("HomeworkWindow", () => new HomeworkWindow());
            Check("MissingWindow", () => new MissingWindow());
            Check("MissingEditWindow", () => new MissingEditWindow(Store.LoadMissingBoard()));
            Check("HomeworkEditWindow", () => new HomeworkEditWindow(Store.LoadHomework() ?? new ScheduleCore.Models.HomeworkBoard()));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "ClassBulletinApp.uicheck.log"), sb.ToString());
            }
            catch { }
            Shutdown(0);
            return;
        }

        // 应用用户选择的主题配色（标题栏 / 按钮 / 强调色）
        try { ThemeManager.Apply(Store.LoadSettings().Theme); }
        catch { }

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
