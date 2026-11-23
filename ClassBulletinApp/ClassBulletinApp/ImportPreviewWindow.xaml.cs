using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClosedXML.Excel;
using ScheduleCore.Models;
using ScheduleCore.Services;
using static ScheduleCore.Services.ScheduleTemplateProbe;

namespace ClassBulletinApp;

/// <summary>
/// 智能导入的预览/校正窗口。打开时传入文件路径，内部做探测并允许微调参数，
/// 点「确认导入」后 DialogResult=true，调用方从 <see cref="Result"/> 取解析好的课表。
/// </summary>
public partial class ImportPreviewWindow : Window
{
    readonly string _path;
    ProbeResult _probe = new();
    TemplateHints _hints = new();
    ScheduleGeneralImporter.GeneralResult? _result;
    int _lastRow, _lastCol;

    public ScheduleFile? Result => _result?.Schedule;
    public List<ScheduleGeneralImporter.SwapMark> Marks => _result?.Marks ?? new();

    public ImportPreviewWindow(string path)
    {
        InitializeComponent();
        _path = path;
        TxtFile.Text = path;
        Owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        try
        {
            _probe = ScheduleTemplateProbe.Probe(path);
            if (!_probe.Ok || _probe.Candidates.Count == 0)
            {
                TxtTip.Text = "未能自动识别课表布局：" + _probe.Message + "。请在中间手动指定参数后点「按参数重新解析」。";
                _hints = new TemplateHints();
            }
            else
            {
                var best = _probe.Best;
                foreach (var c in _probe.Candidates) CbSheet.Items.Add(c.SheetName);
                CbSheet.SelectedIndex = 0;
                _hints = best;
            }
            LoadRawPreview();
        }
        catch (Exception ex)
        {
            TxtTip.Text = "读取文件失败：" + ex.Message;
            _hints = new TemplateHints();
        }
        LoadHintsToUi();
        Reparse();

        // 原始表格补上行号：填「表头行/节次行」时能直接数到第几行
        GridRaw.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    void LoadHintsToUi()
    {
        if (CbSheet.Items.Count > 0 && _hints.SheetIndex >= 1 && _hints.SheetIndex <= CbSheet.Items.Count)
            CbSheet.SelectedIndex = _hints.SheetIndex - 1;
        CbOrientation.SelectedIndex = _hints.DayOrientation == DayOrientation.Horizontal ? 0 : 1;
        CbMainDim.SelectedIndex = _hints.MainDimension == MainDimension.ClassesInRows ? 0
                                : _hints.MainDimension == MainDimension.ClassesInColumns ? 1 : 2;
        CbPeriodStyle.SelectedIndex = _hints.PeriodStyle == PeriodStyle.LabelWithNumber ? 0
                                    : _hints.PeriodStyle == PeriodStyle.MorningNumber ? 1 : 2;
        CbCellLayout.SelectedIndex = _hints.CellLayout == CellLayout.NewLine ? 0
                                   : _hints.CellLayout == CellLayout.Space ? 1
                                   : _hints.CellLayout == CellLayout.Parenthesis ? 2 : 3;
        TxtCellSep.Text = _hints.CustomSeparator ?? "";
        TxtCellSep.IsEnabled = CbCellLayout.SelectedIndex == 3;
        ChkTeacher.IsChecked = _hints.IsTeacherSchedule;
        TxtDayHeader.Text = _hints.DayHeaderIndex > 0 ? _hints.DayHeaderIndex.ToString() : "";
        TxtPeriodLabel.Text = _hints.PeriodLabelIndex > 0 ? _hints.PeriodLabelIndex.ToString() : "";
        TxtSingleName.Text = _hints.SingleName ?? "";

        int conf = _hints.Confidence;
        if (conf >= 80) { TxtConfidence.Text = $"识别置信度 {conf}% · 高"; TxtConfidence.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 122, 70)); }
        else if (conf >= 50) { TxtConfidence.Text = $"识别置信度 {conf}% · 中，建议核对参数"; TxtConfidence.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 120, 20)); }
        else { TxtConfidence.Text = $"识别置信度 {conf}% · 低，请务必核对参数"; TxtConfidence.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(192, 57, 43)); }

        if (_probe.Ok && _hints.Notes.Count > 0)
            TxtTip.Text = string.Join("；", _hints.Notes) + "。确认无误后点右下「确认导入」。";
    }

    void CollectUiToHints()
    {
        _hints.SheetIndex = Math.Max(1, CbSheet.SelectedIndex + 1);
        if (CbSheet.SelectedIndex >= 0 && CbSheet.SelectedIndex < CbSheet.Items.Count)
            _hints.SheetName = CbSheet.Items[CbSheet.SelectedIndex].ToString() ?? "";
        _hints.DayOrientation = CbOrientation.SelectedIndex == 1 ? DayOrientation.Vertical : DayOrientation.Horizontal;
        _hints.MainDimension = CbMainDim.SelectedIndex == 1 ? MainDimension.ClassesInColumns
                             : CbMainDim.SelectedIndex == 2 ? MainDimension.Single : MainDimension.ClassesInRows;
        _hints.PeriodStyle = CbPeriodStyle.SelectedIndex == 1 ? PeriodStyle.MorningNumber
                           : CbPeriodStyle.SelectedIndex == 2 ? PeriodStyle.PlainNumber : PeriodStyle.LabelWithNumber;
        _hints.CellLayout = CbCellLayout.SelectedIndex == 1 ? CellLayout.Space
                          : CbCellLayout.SelectedIndex == 2 ? CellLayout.Parenthesis
                          : CbCellLayout.SelectedIndex == 3 ? CellLayout.Custom
                          : CellLayout.NewLine;
        _hints.CustomSeparator = TxtCellSep.Text ?? "";
        _hints.IsTeacherSchedule = ChkTeacher.IsChecked == true;
        _hints.DayHeaderIndex = ParseInt(TxtDayHeader.Text);
        _hints.PeriodLabelIndex = ParseInt(TxtPeriodLabel.Text);
        _hints.SingleName = TxtSingleName.Text?.Trim() ?? "";
    }

    static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;

    /// <summary>1 → A、26 → Z、27 → AA（只用来给原始表格标列名）。</summary>
    static string ColumnLetter(int c)
    {
        var s = "";
        while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
        return s;
    }

    void Reparse()
    {
        CollectUiToHints();
        StatClasses.Text = "0";
        StatLessons.Text = "0";
        StatUnparsed.Text = "0";
        SamplePanel.Children.Clear();
        _result = null;

        // 二一标准布局：直接转交专用解析器，拿到完整课时（含固定时段）
        if (_hints.UseBuiltInEryiParser)
        {
            try
            {
                var sf = ScheduleImporter.Import(_path);
                int lessons = 0;
                foreach (var c in sf.Classes)
                    foreach (var d in c.Data.Values)
                        foreach (var p in d.Values)
                            lessons += p.Count;
                StatClasses.Text = sf.Classes.Count.ToString();
                StatLessons.Text = lessons.ToString();
                StatUnparsed.Text = "0";
                _result = new ScheduleGeneralImporter.GeneralResult { Schedule = sf };
                ShowSamples(sf);
                TxtStatus.Text = $"已用二一专用解析器解析 {sf.Classes.Count} 个班、{lessons} 课时。";
                return;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "专用解析器失败，已回退通用解析：" + ex.Message;
                var copy = CloneHints(_hints);
                copy.UseBuiltInEryiParser = false;
                _hints = copy;
            }
        }

        try
        {
            _result = ScheduleGeneralImporter.Parse(_path, _hints);
            int lessons = 0;
            foreach (var c in _result.Schedule.Classes)
                foreach (var d in c.Data.Values)
                    foreach (var p in d.Values)
                        lessons += p.Count;
            StatClasses.Text = _result.Schedule.Classes.Count.ToString();
            StatLessons.Text = lessons.ToString();
            StatUnparsed.Text = _result.UnparsedCells.ToString();
            UnparsedBox.Background = _result.UnparsedCells > 0
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(252, 237, 237))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 243, 251));
            ShowSamples(_result.Schedule);
            var notes = new[]
            {
                lessons == 0 ? "解析出 0 课时，请核对参数" : "",
                _result.UnparsedCells > 0 ? $"有 {_result.UnparsedCells} 个格未识别" : ""
            }.Where(x => x.Length > 0).ToArray();
            TxtStatus.Text = notes.Length == 0
                ? $"解析完成：{_result.Schedule.Classes.Count} 个班、{lessons} 课时。"
                : string.Join("；", notes);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "解析失败：" + ex.Message;
        }
    }

    static TemplateHints CloneHints(TemplateHints h) => new TemplateHints
    {
        SheetIndex = h.SheetIndex, SheetName = h.SheetName, DayOrientation = h.DayOrientation,
        MainDimension = h.MainDimension, DayHeaderIndex = h.DayHeaderIndex,
        PeriodLabelIndex = h.PeriodLabelIndex, DataStartIndex = h.DataStartIndex,
        NameIndex = h.NameIndex, PeriodStyle = h.PeriodStyle, CellLayout = h.CellLayout,
        CustomSeparator = h.CustomSeparator,
        PeriodOrientation = h.PeriodOrientation, CellSpan = h.CellSpan,
        SingleName = h.SingleName, IsTeacherSchedule = h.IsTeacherSchedule,
        UseBuiltInEryiParser = h.UseBuiltInEryiParser, Confidence = h.Confidence,
        Notes = new List<string>(h.Notes)
    };

    void ShowSamples(ScheduleFile sf)
    {
        int shown = 0;
        foreach (var c in sf.Classes)
        {
            if (shown >= 3) break;
            var head = new TextBlock
            {
                Text = c.ClassName,
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 79, 163)),
                Margin = new Thickness(0, shown == 0 ? 0 : 10, 0, 4)
            };
            SamplePanel.Children.Add(head);
            int line = 0;
            foreach (var dk in c.Data.Keys.OrderBy(x => x))
            {
                foreach (var pk in c.Data[dk].Keys.OrderBy(x => x))
                {
                    // 同一节课可能拆出单双周两门课，全部列出，让用户看清单双周识别结果
                    var desc = string.Join("  ＋  ", c.Data[dk][pk].Select(l =>
                        l.Subject +
                        (string.IsNullOrEmpty(l.Teacher) ? "" : $"·{l.Teacher}") +
                        (l.Week == 1 ? "（单）" : l.Week == 2 ? "（双）" : "")));
                    var tb = new TextBlock
                    {
                        Text = $"周{dk + 1} 第{pk}节  {desc}",
                        FontSize = 12, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81)),
                        Margin = new Thickness(8, 1, 0, 1), TextWrapping = TextWrapping.Wrap
                    };
                    SamplePanel.Children.Add(tb);
                    if (++line >= 6) break;
                    if (line >= 6) break;
                }
                if (line >= 6) break;
            }
            shown++;
        }
    }

    void LoadRawPreview()
    {
        try
        {
            using var wb = new XLWorkbook(_path);
            int sheetIdx = _probe.Ok && _probe.Candidates.Count > 0 ? _probe.Best.SheetIndex : 1;
            var ws = sheetIdx >= 1 && sheetIdx <= wb.Worksheets.Count ? wb.Worksheet(sheetIdx) : wb.Worksheet(1);
            _lastRow = Math.Min(ws.LastCellUsed()?.Address.RowNumber ?? 1, 14);
            _lastCol = Math.Min(ws.LastCellUsed()?.Address.ColumnNumber ?? 1, 20);

            int count = 0;
            var dt = new DataTable();
            // 列名直接用 Excel 的列字母（A/B/C…）：DataGrid 自动生成的表头取的就是列名，
            // 这样下面填「节次列/行」「表头行/列」时能直接对上号。
            for (int c = 1; c <= _lastCol; c++) dt.Columns.Add(ColumnLetter(c), typeof(string));
            for (int r = 1; r <= _lastRow; r++)
            {
                var row = dt.NewRow();
                for (int c = 1; c <= _lastCol; c++)
                {
                    var v = (ws.Cell(r, c).GetString() ?? "").Replace("\r", "").Replace("\n", " ");
                    if (v.Length > 14) v = v.Substring(0, 14) + "…";
                    row[c - 1] = v;
                }
                dt.Rows.Add(row);
                count++;
            }
            GridRaw.ItemsSource = dt.DefaultView;
        }
        catch (Exception ex)
        {
            TxtTip.Text = "原始表格预览失败：" + ex.Message;
        }
    }

    void NumberOnly(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "[0-9]");
    }

    /// <summary>r143：只有选了「自定义符号」才让分隔符输入框可用；切过去空着就先填个最常用的。</summary>
    void CbCellLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtCellSep == null) return;                  // XAML 解析期就会触发一次，控件还没建好
        bool custom = CbCellLayout.SelectedIndex == 3;
        TxtCellSep.IsEnabled = custom;
        if (custom && string.IsNullOrWhiteSpace(TxtCellSep.Text)) TxtCellSep.Text = "｜";
    }

    /// <summary>诊断（--shot / --ui-check）用：切到「自定义符号」档位并按新参数重新解析，便于截实景。</summary>
    internal void DiagSelectCustomSeparator()
    {
        CbCellLayout.SelectedIndex = 3;
        if (string.IsNullOrWhiteSpace(TxtCellSep.Text)) TxtCellSep.Text = "｜";
        Reparse();
    }

    void BtnReparse_Click(object sender, RoutedEventArgs e)
    {
        // 用户手动改过参数后，自动退出"二一专用"分支（除非他没动过任何关键参数）
        Reparse();
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || _result.Schedule.Classes.Count == 0 || AllEmpty(_result.Schedule))
        {
            MessageBox.Show("当前参数没有解析出任何课程。请先在中间调整参数并点「按参数重新解析」，确认右侧有课时后再导入。",
                "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_result.UnparsedCells > 0)
        {
            var answer = MessageBox.Show($"有 {_result.UnparsedCells} 个单元格未能识别，导入后这些格子会是空的。仍要继续吗？",
                "存在未识别单元格", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
        Close();
    }

    static bool AllEmpty(ScheduleFile sf)
    {
        foreach (var c in sf.Classes)
            foreach (var d in c.Data.Values)
                foreach (var p in d.Values)
                    if (p.Count > 0) return false;
        return true;
    }
}
