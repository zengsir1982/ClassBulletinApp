using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ScheduleCore.Models;
using ScheduleCore.Services;
using Win = System.Windows;
using Media = System.Windows.Media;

namespace ClassBulletinApp;

/// <summary>
/// r73「真正全局玻璃」：给<b>普通窗口</b>（主窗、设置、编辑、向导等）启用 Windows 系统级毛玻璃。
///
/// <para>★ 为什么必须另起一套、不能复用 <see cref="GlassBrush"/>：r65 的玻璃只改悬浮窗根
/// Border 底色的 alpha，能透出桌面是因为那 10 个窗口全是 <c>AllowsTransparency=True</c> 的分层窗口。
/// 普通窗口是不透明 HWND，把内部 Border 调半透明只会露出窗口自己的白底，视觉上等于没变化。
/// 这里改的是 <b>DWM 窗口级 backdrop</b>，尝试让系统合成器在窗口透明像素处绘制背景，
/// 不改标题栏、不改布局、不新增全屏元素，因此不碰 <c>WM_NCHITTEST</c> 白名单与触屏铁律。</para>
///
/// <para><b>★ 实测限制（Win11 26H2，--glass-probe 彩色底图对照）：</b>三种系统档位
/// （Acrylic / AcrylicBlurBehind / Mica）都返回成功，但抓图哈希完全一致，窗口只得到一层
/// 桌面淡色底，<b>不会模糊透出后方窗口</b> —— Win11 22H2 之后系统对普通顶层窗口就是这么处理的。
/// Win10 上未公开 API 仍可能给出真亚克力，故保留这条路径与自测工具。
/// 要做到悬浮窗那种「透出桌面」的玻璃，唯一办法是把普通窗口改成
/// <c>AllowsTransparency=True</c> 的分层窗并自绘标题栏（工程量大，会动触屏拖动与最大化）。</para>
///
/// <para>★ 三条硬约束：
/// <list type="number">
/// <item>已实现 <see cref="IGlassSurface"/> 的悬浮窗一律跳过，继续走 r65，避免两套玻璃叠加；</item>
/// <item><c>AllowsTransparency=True</c> 的分层窗口不能再用 DWM backdrop，跳过；</item>
/// <item>诊断 / 截图模式（<see cref="App.IsDiagnostic"/>）一律不动窗口背景 —— DWM 合成结果
/// <c>RenderTargetBitmap</c> 本来就抓不到，改了反而会把 --shot 拍成黑底。</item>
/// </list></para>
/// </summary>
internal static class GlassBackdrop
{
    /// <summary>系统实际采用的玻璃实现。</summary>
    internal enum Kind { Off, Win11Acrylic, Win11Mica, Win10Acrylic, Frosted }

    // 用户在设置里能选的档位（持久化字符串，别改字面量）
    internal const string ModeAuto = "auto";
    internal const string ModeAcrylic = "acrylic";
    internal const string ModeMica = "mica";
    internal const string ModeFrosted = "frosted";
    /// <summary>诊断 / 高级用：强制走未公开 API 的 AcrylicBlurBehind（在 Win11 上可能仍能真模糊）。不进设置下拉。</summary>
    internal const string ModeAcrylicLegacy = "acrylic-legacy";

    // ---- Win32 常量 ----
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    const int DWMSBT_NONE = 1;
    const int DWMSBT_MAINWINDOW = 2;       // Mica
    const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_DISABLED = 0;
    const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WcaData data);

    [DllImport("ntdll.dll")]
    static extern int RtlGetVersion(ref OsVersionInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    struct WcaData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OsVersionInfoEx
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    // ---- 原值记录（附加属性，保证反复 Apply 幂等、关闭时能精确还原）----
    static readonly Win.DependencyProperty OrigBgProperty = Win.DependencyProperty.RegisterAttached(
        "GlassBackdropOrigBg", typeof(Media.Brush), typeof(GlassBackdrop), new Win.PropertyMetadata(null));
    static readonly Win.DependencyProperty OrigCompBgProperty = Win.DependencyProperty.RegisterAttached(
        "GlassBackdropOrigCompBg", typeof(Media.Color), typeof(GlassBackdrop),
        new Win.PropertyMetadata(Media.Colors.White));
    static readonly Win.DependencyProperty OrigAlphaProperty = Win.DependencyProperty.RegisterAttached(
        "GlassBackdropOrigAlpha", typeof(byte), typeof(GlassBackdrop), new Win.PropertyMetadata((byte)255));

    static bool _installed;

    /// <summary>真实系统 Build 号（RtlGetVersion 不受 app.manifest 兼容性声明影响）。</summary>
    internal static int WinBuild { get; } = ReadBuild();

    static int ReadBuild()
    {
        try
        {
            var info = new OsVersionInfoEx { dwOSVersionInfoSize = Marshal.SizeOf<OsVersionInfoEx>() };
            if (RtlGetVersion(ref info) == 0 && info.dwBuildNumber > 0) return info.dwBuildNumber;
        }
        catch { }
        return Environment.OSVersion.Version.Build;
    }

    /// <summary>
    /// 纯函数：按系统版本 + 用户档位决定实际用哪种实现。抽出来是为了 --ui-check 能在
    /// 任意机器上断言 Win11 / Win10 / 老系统三条分支，不必依赖当前开发机的版本。
    /// </summary>
    internal static Kind Resolve(int build, string? mode)
    {
        string m = string.IsNullOrWhiteSpace(mode) ? ModeAuto : mode!.Trim().ToLowerInvariant();
        if (m == ModeFrosted) return Kind.Frosted;
        if (m == ModeAcrylicLegacy) return Kind.Win10Acrylic;
        if (m == ModeMica)
            return build >= 22621 ? Kind.Win11Mica : Kind.Frosted;   // Win10 没有 Mica，回退磨砂
        // auto / acrylic：Win11 22H2+ 走官方 backdrop，Win10 1809+ 走未公开 API 的 Acrylic，
        // 更老的系统只能磨砂面板。
        if (build >= 22621) return Kind.Win11Acrylic;
        if (build >= 17763) return Kind.Win10Acrylic;
        return Kind.Frosted;
    }

    /// <summary>在 App.OnStartup 里注册一次：此后任何新开的普通窗口 Loaded 时自动套玻璃。</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        Win.EventManager.RegisterClassHandler(typeof(Win.Window), Win.FrameworkElement.LoadedEvent,
            new Win.RoutedEventHandler((s, _) => { if (s is Win.Window w) Apply(w); }), true);
    }

    /// <summary>设置变更 / 实时预览时刷新所有已打开的普通窗口。</summary>
    internal static void RefreshAll(AppSettings? settings = null)
    {
        var app = Win.Application.Current;
        if (app == null) return;
        foreach (Win.Window w in app.Windows) Apply(w, settings);
    }

    /// <summary>
    /// DWM 路径要不要跳过这个窗口（= 交给 <see cref="GlassChrome"/> 或干脆不碰）。
    /// <para>★ 安全网（r74）：本来该由 GlassChrome 接管、但暂时没挂上的窗口
    /// （典型场景：开窗时玻璃是关的，之后在设置里打开 → 这个窗口已经无法再改成分层窗），
    /// 绝不能走 DWM 路径 —— <c>MakeTransparent</c> 会把 Window.Background 设成透明，
    /// 而非分层窗口不支持逐像素 alpha，整窗会被合成为<b>纯黑</b>
    /// （用户报的「清除台账弹窗界面是黑的」就是这么来的）。</para>
    /// </summary>
    internal static bool ShouldSkipForChrome(Win.Window w)
        => w != null && (GlassChrome.IsPrepared(w) || GlassChrome.CanAttach(w));

    /// <summary>单个窗口应用（或撤掉）玻璃。返回是否真的启用了系统 backdrop。</summary>
    internal static bool Apply(Win.Window w, AppSettings? settings = null, bool force = false)
    {
        if (w == null) return false;
        // 悬浮窗 / 分层窗口由 GlassBrush 负责，两套叠加会互相盖掉
        if (w is IGlassSurface || w.AllowsTransparency) return false;
        // 已被 r73d 的分层玻璃接管（Prepare 阶段就设了 AllowsTransparency），这里一律不碰
        if (ShouldSkipForChrome(w)) return false;

        var s = settings ?? Store.LoadSettings();
        // r74：普通窗口组有自己的开关与通透度，跟悬浮窗彻底分开
        bool want = s?.GlassEnabled == true && s?.GlassAllWindows == true
                    && GlassBrush.ChromePercent(s) > 0;

        // ★ 诊断 / 截图模式绝不动窗口背景：DWM 效果截图抓不到，改了只会把 --shot 拍黑
        if (App.IsDiagnostic && !force) { Restore(w); return false; }
        if (!force && !want) { Restore(w); return false; }
        if (!w.IsLoaded) return false;

        IntPtr hwnd = new Win.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var kind = Resolve(WinBuild, s?.GlassBackdropMode);
        bool ok = kind switch
        {
            Kind.Win11Acrylic => SetWin11(hwnd, DWMSBT_TRANSIENTWINDOW),
            Kind.Win11Mica => SetWin11(hwnd, DWMSBT_MAINWINDOW),
            Kind.Win10Acrylic => SetWin10Acrylic(hwnd),
            Kind.Frosted => DisableBackdrop(hwnd),
            _ => false
        };

        if (ok && kind != Kind.Frosted)
        {
            double percent = GlassBrush.ChromePercent(s);
            MakeTransparent(w, percent);
            // ★ 实测（--glass-probe 彩色底图对照）：WPF 非分层窗口不支持逐像素 alpha，
            //   内容层半透明画刷会被预乘成灰实色，反而把窗口压暗。DWM 成功时一律不叠
            //   ApplyFrosted；磨砂面板只在「系统 backdrop 不可用」的回退分支里用。
            //   Win11 26H2 上 DWM 只给桌面淡色底、不模糊后方窗口；Win10 上若 Acrylic
            //   真生效，透明窗口底就是它显示的前提，这条路径仍保留。
            ClearFrosted(w);
            return true;
        }

        // 系统不支持 / 调用失败 / 用户选磨砂：还原窗口底色，只做内容层的磨砂面板
        RestoreWindowColors(w);
        if (want) ApplyFrosted(w, GlassBrush.ChromePercent(s));
        else ClearFrosted(w);
        return false;
    }

    /// <summary>彻底撤掉玻璃并还原所有被改过的属性。</summary>
    internal static void Restore(Win.Window w)
    {
        if (w == null) return;
        bool touched = w.ReadLocalValue(OrigCompBgProperty) != Win.DependencyProperty.UnsetValue
                    || w.ReadLocalValue(OrigBgProperty) != Win.DependencyProperty.UnsetValue;
        ClearFrosted(w);
        // 从未改过（尤其诊断里未 Show 的窗口）：绝不碰 Win32 ——
        // WindowInteropHelper.Handle 会 EnsureHandle 强行造 HWND，污染诊断环境。
        if (!touched) return;
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = new Win.Interop.WindowInteropHelper(w).Handle; } catch { }
        if (hwnd != IntPtr.Zero) DisableBackdrop(hwnd);
        RestoreWindowColors(w);
    }

    // ---------- 系统级 backdrop ----------

    static bool SetWin11(IntPtr hwnd, int backdropType)
    {
        try
        {
            int dark = 0;   // 应用是浅色主题，别让 Mica 变成深色
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int v = backdropType;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    static bool SetWin10Acrylic(IntPtr hwnd)
    {
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                // ABGR：浅蓝白 #F2F5FA + 60% 不透明，颜色和主窗底色一致
                GradientColor = unchecked((int)0x99FAF5F2u),
                AnimationId = 0
            };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, p, false);
                var data = new WcaData { Attribute = WCA_ACCENT_POLICY, Data = p, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch { return false; }
    }

    static bool DisableBackdrop(IntPtr hwnd)
    {
        try
        {
            int v = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
            var policy = new AccentPolicy { AccentState = ACCENT_DISABLED };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, p, false);
                var data = new WcaData { Attribute = WCA_ACCENT_POLICY, Data = p, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
            return true;
        }
        catch { return false; }
    }

    // ---------- 让窗口内容露出系统 backdrop ----------

    /// <summary>
    /// 两个关键动作：① HwndTarget 的清屏色改透明（WPF 窗口默认的白底其实来自这里）；
    /// ② 显式写了实色 Window.Background 的窗口，把它调成半透明，否则整片实色会把玻璃盖住。
    /// 动态资源绑定的 Background 一律不碰（改成本地值会切断主题跟随，r65 已踩过这个坑）。
    /// </summary>
    static void MakeTransparent(Win.Window w, double percent)
    {
        IntPtr hwnd = new Win.Interop.WindowInteropHelper(w).Handle;
        var source = Win.Interop.HwndSource.FromHwnd(hwnd);
        var target = source?.CompositionTarget;
        if (target == null) return;

        if (w.ReadLocalValue(OrigCompBgProperty) == Win.DependencyProperty.UnsetValue)
            w.SetValue(OrigCompBgProperty, target.BackgroundColor);
        target.BackgroundColor = Media.Colors.Transparent;

        var local = w.ReadLocalValue(Win.Controls.Control.BackgroundProperty);
        if (local is Media.SolidColorBrush sb)
        {
            if (w.ReadLocalValue(OrigBgProperty) == Win.DependencyProperty.UnsetValue)
                w.SetValue(OrigBgProperty, sb);
            // ★ 必须是「完全透明」而不是半透明：非分层 WPF 窗口不支持逐像素 alpha，
            //   半透明白会被合成成一片浅灰实色，DWM 就找不到可绘制 backdrop 的透明像素。
            w.Background = Media.Brushes.Transparent;
        }
        // 其它情况（默认透明 / DynamicResource / 图片刷）保持原样，只靠清屏色透出 backdrop
    }

    static void RestoreWindowColors(Win.Window w)
    {
        try
        {
            IntPtr hwnd = new Win.Interop.WindowInteropHelper(w).Handle;
            var target = Win.Interop.HwndSource.FromHwnd(hwnd)?.CompositionTarget;
            if (target != null && w.ReadLocalValue(OrigCompBgProperty) != Win.DependencyProperty.UnsetValue)
            {
                target.BackgroundColor = (Media.Color)w.GetValue(OrigCompBgProperty);
                w.ClearValue(OrigCompBgProperty);
            }
            if (w.ReadLocalValue(OrigBgProperty) != Win.DependencyProperty.UnsetValue)
            {
                w.Background = w.GetValue(OrigBgProperty) as Media.Brush;
                w.ClearValue(OrigBgProperty);
            }
        }
        catch { /* 窗口正在关闭，HWND 已失效，忽略 */ }
    }

    // ---------- 磨砂面板回退（不改窗口结构、不动 HwndTarget） ----------

    /// <summary>
    /// 内容层磨砂：把根 Content 往下两层的「浅色 + 本地实色」Border / Panel 背景按通透度调低 alpha。
    /// 动态资源绑定、渐变、图片刷、深色 / 主色块一律跳过 —— 前者会切断主题跟随，后者会让文字失去反差。
    /// 收集与还原共用同一份清单，保证开了能关回去。
    /// </summary>
    static void ApplyFrosted(Win.Window w, double percent)
    {
        byte a = GlassBrush.AlphaOf(percent);
        foreach (var d in CollectTargets(w))
        {
            var prop = BackgroundPropertyOf(d);
            if (prop == null || d is not Win.FrameworkElement fe) continue;
            var local = fe.ReadLocalValue(prop);
            if (local is not Media.SolidColorBrush sb) continue;
            var c = sb.Color;
            if (c.A < 200 || (c.R + c.G + c.B) / 3 < 200) continue;   // 只动不透明的浅色底

            if (fe.ReadLocalValue(OrigAlphaProperty) == Win.DependencyProperty.UnsetValue)
                fe.SetValue(OrigAlphaProperty, c.A);
            byte origA = (byte)fe.GetValue(OrigAlphaProperty);
            fe.SetValue(prop, Frozen(Media.Color.FromArgb((byte)Math.Min(origA, a), c.R, c.G, c.B)));
        }
    }

    static void ClearFrosted(Win.Window w)
    {
        foreach (var d in CollectTargets(w)) RestoreOne(d);
    }

    static Win.DependencyProperty? BackgroundPropertyOf(Win.DependencyObject d)
        => d is Win.Controls.Border ? Win.Controls.Border.BackgroundProperty
         : d is Win.Controls.Panel ? Win.Controls.Panel.BackgroundProperty
         : null;

    static List<Win.DependencyObject> CollectTargets(Win.Window w)
    {
        var list = new List<Win.DependencyObject>();
        var root = w.Content as Win.DependencyObject;
        if (root == null) return list;
        list.Add(root);
        var queue = new Queue<(Win.DependencyObject Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && list.Count < 80)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= 2) continue;
            try
            {
                foreach (object child in Win.LogicalTreeHelper.GetChildren(node))
                    if (child is Win.DependencyObject d)
                    {
                        list.Add(d);
                        queue.Enqueue((d, depth + 1));
                    }
            }
            catch (InvalidOperationException) { }
        }
        return list;
    }

    static void RestoreOne(Win.DependencyObject d)
    {
        if (d is not Win.FrameworkElement fe) return;
        if (fe.ReadLocalValue(OrigAlphaProperty) == Win.DependencyProperty.UnsetValue) return;
        byte a = (byte)fe.GetValue(OrigAlphaProperty);
        var prop = BackgroundPropertyOf(d);
        if (prop == null) return;
        if (fe.ReadLocalValue(prop) is Media.SolidColorBrush sb)
        {
            var c = sb.Color;
            if (c.A != a) fe.SetValue(prop, Frozen(Media.Color.FromArgb(a, c.R, c.G, c.B)));
        }
        fe.ClearValue(OrigAlphaProperty);
    }

    static Media.SolidColorBrush Frozen(Media.Color c)
    {
        var b = new Media.SolidColorBrush(c);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    // ---------- 诊断 ----------

    /// <summary>诊断用：强制在真实 HWND 上调一次系统 backdrop，返回 (实际档位, 是否调用成功)。</summary>
    internal static (Kind Kind, bool Ok) DiagProbe(Win.Window w, string? mode)
    {
        var kind = Resolve(WinBuild, mode);
        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = new Win.Interop.WindowInteropHelper(w).Handle; } catch { }
        if (hwnd == IntPtr.Zero) return (kind, false);
        bool ok = kind switch
        {
            Kind.Win11Acrylic => SetWin11(hwnd, DWMSBT_TRANSIENTWINDOW),
            Kind.Win11Mica => SetWin11(hwnd, DWMSBT_MAINWINDOW),
            Kind.Win10Acrylic => SetWin10Acrylic(hwnd),
            _ => false
        };
        return (kind, ok);
    }
}
