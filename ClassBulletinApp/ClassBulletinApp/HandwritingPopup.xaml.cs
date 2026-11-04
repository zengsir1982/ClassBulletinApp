using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.Input.Inking;

// WinForms 与 WPF 同名类型（项目同时引用两者），显式指定 WPF 版本。
using WpfStylusPoint = System.Windows.Input.StylusPoint;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ClassBulletinApp;

/// <summary>
/// 手写输入面板：白板上写字 → 自动识别 → 点候选字插入。
/// <para><b>为什么自己做而不用系统键盘的手写：</b>TabTip 的手写布局要用户自己去点笔形图标切换，
/// 各版本位置不一样；而且姓名里的生僻字在整句识别里准确率低，这里可以只写一个字、只认一个字。</para>
/// <para><b>识别引擎：</b>走 WinRT <see cref="InkRecognizerContainer"/>，用系统已安装的手写识别器（中文/英文），
/// <b>完全离线</b>。.NET 8 的 WPF 已经移除了 <c>InkAnalyzer</c>，所以识别不能靠 WPF 自身。</para>
/// <para><b>没有识别器怎么办：</b>只降级成"能写不能转"，面板顶部给提示，绝不弹崩溃。</para>
/// <para><b>连续写字：</b>点候选字是"插入这个字并清空白板"，<b>不关闭面板</b>——写名字通常要两三个字，
/// 每写一个就要重开一次面板的话没人愿意用。收工再点「完成」。</para>
/// <para><b>删除：</b>改名字时前面往往压着旧字，触屏上点到字缝里按退格很别扭，
/// 所以面板自带「⌫ 删一字」（按住连删）和「清空输入框」（点两次确认），直接作用在那个目标框上。</para>
/// </summary>
public partial class HandwritingPopup : Window
{
    private readonly DispatcherTimer _debounce;
    private readonly List<string> _candidates = new();
    private string _best = "";
    private bool _analyzing;
    private InkRecognizerContainer? _container;

    /// <summary>识别出文字后怎么处理。为 <c>null</c> 表示"没有可插入的输入框"，只进结果框供复制。</summary>
    private readonly Action<string>? _commit;

    /// <summary>删掉目标框光标前一个字。为 <c>null</c> 表示没有目标框。</summary>
    private readonly Action? _deleteBack;

    /// <summary>清空目标框。为 <c>null</c> 表示没有目标框。</summary>
    private readonly Action? _clearTarget;

    /// <summary>长按「删一字」时的连删定时器。</summary>
    private DispatcherTimer? _delRepeat;

    /// <summary>「清空输入框」的确认状态与自动撤销定时器（防误触）。</summary>
    private DateTime _clearArmed = DateTime.MinValue;
    private DispatcherTimer? _clearReset;

    /// <summary>是否以模态方式打开（决定关闭时能不能设 <c>DialogResult</c>）。</summary>
    public bool Modal { get; set; }

    /// <summary>累计写入/待复制的文字。</summary>
    public string ResultText => TxtResult?.Text ?? "";

    public HandwritingPopup(Action<string>? commit = null, Action? deleteBack = null, Action? clearTarget = null)
    {
        InitializeComponent();

        _commit = commit;
        _deleteBack = deleteBack;
        _clearTarget = clearTarget;

        // 红外触摸一体机没有压感，笔画粗细只能固定成档位；FitToCurve 让折线看起来像笔迹。
        Pad.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black,
            Width = 4,
            Height = 4,
            FitToCurve = true,
            IgnorePressure = true,
            StylusTip = StylusTip.Ellipse
        };

        // 停笔 450ms 再识别：边写边认会闪烁，也拖慢手感。
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RecognizeAsync(); };
        Pad.Strokes.StrokesChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };

        ApplyWidth(2);   // 默认「中」
        PrepareRecognizer();
        WireDeleteButtons();

        if (_commit == null)
            TxtStatus.Text = "先写一个字，识别后点候选字会进入下面的「结果」框，可以复制后粘贴到别处。" +
                             "（如果先点一下要改的输入框再开手写，就能直接插入、删改）";
    }

    // ---------- 删除按钮 ----------

    /// <summary>
    /// 「删一字」用按下/抬起而不是 Click：按住不放要能连续删。
    /// 触摸在 WPF 里会被提升成鼠标事件，所以只挂鼠标事件即可，两边都挂会"按一下删两个字"。
    /// </summary>
    private void WireDeleteButtons()
    {
        if (_deleteBack == null)
        {
            BtnDelText.IsEnabled = false;
            BtnClearText.IsEnabled = false;
            BtnDelText.ToolTip = "没有可以编辑的输入框：先点一下要改的那个输入框，再打开手写。";
            BtnClearText.ToolTip = BtnDelText.ToolTip;
            return;
        }

        BtnDelText.PreviewMouseLeftButtonDown += (_, e) =>
        {
            DeleteOnce();
            StartDeleteRepeat();
            BtnDelText.CaptureMouse();
            e.Handled = true;
        };

        BtnDelText.PreviewMouseLeftButtonUp += (_, e) =>
        {
            StopDeleteRepeat();
            if (BtnDelText.IsMouseCaptured) BtnDelText.ReleaseMouseCapture();
            e.Handled = true;
        };

        // 手指划出按钮范围也要停，否则会一直删下去
        BtnDelText.LostMouseCapture += (_, _) => StopDeleteRepeat();
    }

    private void DeleteOnce()
    {
        if (_deleteBack == null) return;

        try
        {
            _deleteBack();
            TxtStatus.Text = "已删掉一个字，继续按可以接着删。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "删除失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 按住 350ms 后才进入连删（间隔 90ms）：短按只删一个，长按才连删，
    /// 避免"想删一个字却删了一串"。不用两个定时器接力，单个定时器改间隔最不容易出错。
    /// </summary>
    private void StartDeleteRepeat()
    {
        StopDeleteRepeat();

        _delRepeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _delRepeat.Tick += (_, _) =>
        {
            DeleteOnce();

            if (_delRepeat != null && _delRepeat.Interval.TotalMilliseconds > 90)
                _delRepeat.Interval = TimeSpan.FromMilliseconds(90);
        };
        _delRepeat.Start();
    }

    private void StopDeleteRepeat()
    {
        _delRepeat?.Stop();
        _delRepeat = null;
    }

    private void BtnClearText_Click(object sender, RoutedEventArgs e)
    {
        if (_clearTarget == null)
        {
            TxtStatus.Text = "现在没有可以清空的输入框。";
            return;
        }

        if ((DateTime.UtcNow - _clearArmed).TotalSeconds > 3.0)
        {
            ArmClear();
            return;
        }

        DisarmClear();

        try
        {
            _clearTarget();
            TxtStatus.Text = "已清空输入框，可以开始写了。";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "清空失败：" + ex.Message;
        }
    }

    /// <summary>第一次点：变成红色「再点一次确认」，3 秒内不点就自动变回原样。</summary>
    private void ArmClear()
    {
        _clearArmed = DateTime.UtcNow;

        BtnClearText.Content = "再点一次确认";
        BtnClearText.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC));
        BtnClearText.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        BtnClearText.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

        TxtStatus.Text = "再点一次就会清空输入框里的全部文字。";

        _clearReset?.Stop();
        _clearReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _clearReset.Tick += (_, _) => DisarmClear();
        _clearReset.Start();
    }

    private void DisarmClear()
    {
        _clearArmed = DateTime.MinValue;
        _clearReset?.Stop();
        _clearReset = null;

        BtnClearText.Content = "清空输入框";
        BtnClearText.Background = Brushes.White;
        BtnClearText.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
        BtnClearText.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37));
    }

    // ---------- 定位 ----------

    /// <summary>
    /// 把面板放到"不遮住目标输入框"的位置：优先紧贴目标上方，其次下方，
    /// 都放不下就整体挪到屏幕的另一半，并把水平位置对齐到目标框。
    /// </summary>
    /// <param name="targetDip">目标输入框在屏幕上的矩形（DIP）。为 null 表示居中。</param>
    public void PlaceSmart(Rect? targetDip)
    {
        var wa = SystemParameters.WorkArea;
        double w = Width;
        double h = Height;
        double left, top;

        if (targetDip is Rect t)
        {
            double above = t.Top - h - 12;
            double below = t.Bottom + 12;

            if (above >= wa.Top + 8) top = above;
            else if (below + h <= wa.Bottom - 8) top = below;
            else top = t.Top < wa.Top + wa.Height / 2
                    ? wa.Bottom - h - 16          // 目标在上半屏 → 面板靠底
                    : wa.Top + 16;                // 目标在下半屏 → 面板靠顶

            left = t.Left + t.Width / 2 - w / 2;
        }
        else
        {
            left = wa.Left + (wa.Width - w) / 2;
            top = wa.Top + (wa.Height - h) / 2;
        }

        if (left < wa.Left + 8) left = wa.Left + 8;
        if (left + w > wa.Right - 8) left = wa.Right - 8 - w;
        if (top < wa.Top + 8) top = wa.Top + 8;
        if (top + h > wa.Bottom - 8) top = wa.Bottom - 8 - h;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    // ---------- 识别器准备 ----------

    /// <summary>挑一个中文识别器（姓名场景）；没有中文就用第一个可用的。</summary>
    private void PrepareRecognizer()
    {
        try
        {
            _container = new InkRecognizerContainer();
            IReadOnlyList<InkRecognizer> all = _container.GetRecognizers();

            if (all.Count == 0)
            {
                _container = null;
                TxtStatus.Text = "本机未安装手写识别组件，只能手写、不能转成文字。";
                return;
            }

            InkRecognizer? zh = all.FirstOrDefault(r =>
                    r.Name.Contains("中文") || r.Name.Contains("Chinese", StringComparison.OrdinalIgnoreCase))
                ?? all[0];

            _container.SetDefaultRecognizer(zh);
        }
        catch (Exception)
        {
            _container = null;
            TxtStatus.Text = "本机手写识别组件不可用，只能手写、不能转成文字。";
        }
    }

    // ---------- 识别 ----------

    private async System.Threading.Tasks.Task RecognizeAsync()
    {
        if (_analyzing) return;

        if (Pad.Strokes.Count == 0)
        {
            _best = "";
            _candidates.Clear();
            RenderCandidates();
            TxtStatus.Text = "在上面写一个字或一个词…";
            return;
        }

        if (_container == null)
        {
            TxtStatus.Text = "本机未安装手写识别组件，只能手写、不能转成文字。";
            return;
        }

        _analyzing = true;
        try
        {
            var inkStrokes = new InkStrokeContainer();
            var builder = new InkStrokeBuilder();
            int added = 0;

            foreach (Stroke s in Pad.Strokes)
            {
                var points = new List<Windows.Foundation.Point>();
                foreach (WpfStylusPoint p in s.StylusPoints)
                    points.Add(new Windows.Foundation.Point(p.X, p.Y));

                if (points.Count < 2) continue;
                inkStrokes.AddStroke(builder.CreateStroke(points));
                added++;
            }

            if (added == 0) return;

            var results = await _container.RecognizeAsync(inkStrokes, InkRecognitionTarget.All);

            var list = new List<string>();
            foreach (var r in results)
            {
                foreach (var c in r.GetTextCandidates())
                {
                    string text = (c ?? "").Trim();
                    if (text.Length > 0 && !list.Contains(text)) list.Add(text);
                    if (list.Count >= 10) break;
                }
                if (list.Count >= 10) break;
            }

            _best = list.Count > 0 ? list[0] : "";
            _candidates.Clear();
            _candidates.AddRange(list);
            RenderCandidates();

            TxtStatus.Text = list.Count > 0
                ? "点下面的字插入（认错了可以清空重写，或点别的候选）"
                : "没认出来，写大一点、笔画分开一点再试";
        }
        catch (Exception)
        {
            _best = "";
            _candidates.Clear();
            RenderCandidates();
            TxtStatus.Text = "识别失败：本机手写识别组件可能未安装，请改用系统触摸键盘输入。";
        }
        finally
        {
            _analyzing = false;
        }
    }

    private void RenderCandidates()
    {
        CandidatePanel.Children.Clear();

        foreach (var text in _candidates)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 26,
                MinWidth = 64,
                MinHeight = 54,
                Padding = new Thickness(16, 4, 16, 4),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x37)),
            };
            btn.Click += (_, _) => Accept(text);
            CandidatePanel.Children.Add(btn);
        }
    }

    // ---------- 写入 ----------

    /// <summary>点候选字：插入到目标输入框（或只进结果框），然后清空白板，<b>面板不关</b>。</summary>
    private void Accept(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // 面板里点过「清空输入框」后要复位，否则下次改别的框会少一步确认
        if (_clearArmed != DateTime.MinValue) DisarmClear();

        if (_commit != null)
        {
            try
            {
                _commit(text);
                AppendResult(text);
                TxtStatus.Text = $"已插入「{text}」，可以接着写下一个字。";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "插入失败：" + ex.Message + "（已放进结果框，可以复制）";
                AppendResult(text);
            }
        }
        else
        {
            AppendResult(text);
            TxtStatus.Text = $"已加入结果「{text}」，可点「复制」再粘贴到别处。";
        }

        ClearPad();
    }

    private void AppendResult(string text)
    {
        string cur = TxtResult.Text ?? "";
        if (cur.Length > 300) return;   // 防止一直写把结果框撑爆
        TxtResult.Text = cur + text;
        TxtResult.ScrollToEnd();
    }

    private void ClearPad()
    {
        _debounce.Stop();
        Pad.Strokes.Clear();     // 会触发 StrokesChanged 把定时器又开起来，所以下面再停一次
        _debounce.Stop();
        _best = "";
        _candidates.Clear();
        RenderCandidates();
    }

    // ---------- 按钮 ----------

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (Pad.Strokes.Count == 0) return;
        Pad.Strokes.RemoveAt(Pad.Strokes.Count - 1);
        _ = RecognizeAsync();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        ClearPad();
        TxtStatus.Text = "已清空白板，重新写一个。";
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        string text = TxtResult.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            TxtStatus.Text = "还没有内容可复制，先写一个字。";
            return;
        }

        // 剪贴板偶尔被别的程序占着，重试几次比直接报错友好
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                TxtStatus.Text = "已复制到剪贴板，可以粘贴到任何程序里。";
                return;
            }
            catch { System.Threading.Thread.Sleep(60); }
        }
        TxtStatus.Text = "复制失败：剪贴板被其他程序占用，请稍后再试。";
    }

    /// <summary>完成：白板上还有没插入的笔迹就先插入，再收起面板。</summary>
    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        if (Pad.Strokes.Count > 0 && !string.IsNullOrWhiteSpace(_best))
            Accept(_best);

        ClosePanel();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => ClosePanel();

    private void ClosePanel()
    {
        StopDeleteRepeat();
        _clearReset?.Stop();

        try
        {
            if (Modal) DialogResult = true;
            else Close();
        }
        catch
        {
            Close();
        }
    }

    private void BtnThin_Click(object sender, RoutedEventArgs e) => ApplyWidth(1);
    private void BtnMid_Click(object sender, RoutedEventArgs e) => ApplyWidth(2);
    private void BtnThick_Click(object sender, RoutedEventArgs e) => ApplyWidth(3);

    /// <summary>1=细 2=中 3=粗；同时高亮当前档位，避免"点了没反应"。</summary>
    private void ApplyWidth(int level)
    {
        double w = level switch { 1 => 2.0, 2 => 4.0, _ => 7.0 };
        var a = Pad.DefaultDrawingAttributes.Clone();
        a.Width = w;
        a.Height = w;
        Pad.DefaultDrawingAttributes = a;

        Highlight(BtnThin, level == 1);
        Highlight(BtnMid, level == 2);
        Highlight(BtnThick, level == 3);
    }

    private static void Highlight(Button btn, bool on)
    {
        if (btn == null) return;
        btn.Background = on
            ? new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFD))
            : Brushes.White;
        btn.BorderBrush = on
            ? new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF))
            : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
    }

    /// <summary>
    /// 诊断用：不打开窗口，用一组合成笔迹（一个「十」）跑完整识别流程，
    /// 验证"本机有识别器、管线能通、能出候选"。输出写日志，供无人值守回归。
    /// 识别结果本身不保证正确（合成笔画只为走通流程），关键是 <c>RESULT=OK</c>。
    /// </summary>
    internal static async System.Threading.Tasks.Task<string> SelfTestAsync()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var container = new InkRecognizerContainer();
            IReadOnlyList<InkRecognizer> all = container.GetRecognizers();
            sb.AppendLine("recognizers=" + all.Count);
            foreach (var r in all) sb.AppendLine("  - " + r.Name);

            if (all.Count == 0)
            {
                sb.AppendLine("RESULT=NO_RECOGNIZER");
                return sb.ToString();
            }

            InkRecognizer pick = all.FirstOrDefault(r =>
                    r.Name.Contains("中文") || r.Name.Contains("Chinese", StringComparison.OrdinalIgnoreCase))
                ?? all[0];
            container.SetDefaultRecognizer(pick);
            sb.AppendLine("picked=" + pick.Name);

            var pad = new InkStrokeContainer();
            var builder = new InkStrokeBuilder();

            // 合成「十」：一横 + 一竖
            pad.AddStroke(builder.CreateStroke(new List<Windows.Foundation.Point>
            {
                new(40, 70), new(80, 68), new(120, 70), new(160, 72)
            }));
            pad.AddStroke(builder.CreateStroke(new List<Windows.Foundation.Point>
            {
                new(100, 30), new(100, 60), new(100, 95), new(100, 120)
            }));

            var results = await container.RecognizeAsync(pad, InkRecognitionTarget.All);

            var list = new List<string>();
            foreach (var r in results)
            {
                foreach (var c in r.GetTextCandidates())
                {
                    string t = (c ?? "").Trim();
                    if (t.Length > 0 && !list.Contains(t)) list.Add(t);
                    if (list.Count >= 10) break;
                }
                if (list.Count >= 10) break;
            }

            sb.AppendLine("candidates=" + string.Join("|", list));
            sb.AppendLine(list.Count > 0 ? "RESULT=OK" : "RESULT=EMPTY");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION=" + ex.GetType().Name + ": " + ex.Message);
            sb.AppendLine("RESULT=FAIL");
        }
        return sb.ToString();
    }
}
