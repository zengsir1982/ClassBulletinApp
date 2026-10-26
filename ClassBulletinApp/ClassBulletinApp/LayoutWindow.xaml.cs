using System;
using System.Windows;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class LayoutWindow : Window
{
    private readonly AppSettings _s;

    public LayoutWindow()
    {
        InitializeComponent();
        _s = Store.LoadSettings();
        LoadValues();
        WireLabels();
    }

    private void LoadValues()
    {
        var sl = _s.ScheduleLayout;
        CbSchAnchor.SelectedIndex = sl.Anchor switch
        {
            "Left" => 0, "Right" => 1, "Top" => 2, "Bottom" => 3, _ => 4
        };
        SlSchWidth.Value = sl.Width;
        SlSchHeight.Value = sl.Height;
        SlSchOpacity.Value = sl.Opacity;
        SlSchOverlay.Value = sl.OverlayOpacity;
        ChkSchTop.IsChecked = sl.TopMost;
        ChkSchClick.IsChecked = sl.ClickThrough;
        ChkSchHide.IsChecked = sl.AutoHide;
        ChkSchClassHide.IsChecked = sl.AutoHideDuringClass;

        var nl = _s.NotificationLayout;
        CbNotAnchor.SelectedIndex = nl.Anchor == "Bottom" ? 1 : 0;
        SlNotHeight.Value = nl.Height;
        SlNotFont.Value = nl.FontSize;
        SlNotSpeed.Value = nl.MarqueeSpeed;
        SlNotOpacity.Value = nl.Opacity;
        SlNotOverlay.Value = nl.OverlayOpacity;
        ChkNotTop.IsChecked = nl.TopMost;
        ChkNotClick.IsChecked = nl.ClickThrough;
        ChkNotHide.IsChecked = nl.AutoHide;
        ChkNotClassHide.IsChecked = nl.AutoHideDuringClass;
    }

    private void WireLabels()
    {
        SlSchWidth.ValueChanged += (_, _) => TxtSchWidth.Text = $"{SlSchWidth.Value:0} 像素";
        SlSchHeight.ValueChanged += (_, _) => TxtSchHeight.Text = $"{SlSchHeight.Value:0} 像素";
        SlSchOpacity.ValueChanged += (_, _) => TxtSchOpacity.Text = $"{SlSchOpacity.Value:0%}";
        SlSchOverlay.ValueChanged += (_, _) => TxtSchOverlay.Text = $"{SlSchOverlay.Value:0%}";
        SlNotHeight.ValueChanged += (_, _) => TxtNotHeight.Text = $"{SlNotHeight.Value:0} 像素";
        SlNotFont.ValueChanged += (_, _) => TxtNotFont.Text = $"{SlNotFont.Value:0} 号";
        SlNotSpeed.ValueChanged += (_, _) => TxtNotSpeed.Text = $"{SlNotSpeed.Value:0} 像素/秒";
        SlNotOpacity.ValueChanged += (_, _) => TxtNotOpacity.Text = $"{SlNotOpacity.Value:0%}";
        SlNotOverlay.ValueChanged += (_, _) => TxtNotOverlay.Text = $"{SlNotOverlay.Value:0%}";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var sl = _s.ScheduleLayout;
        sl.Anchor = CbSchAnchor.SelectedIndex switch { 0 => "Left", 1 => "Right", 2 => "Top", 3 => "Bottom", _ => "Center" };
        sl.Width = SlSchWidth.Value;
        sl.Height = SlSchHeight.Value;
        sl.Opacity = SlSchOpacity.Value;
        sl.OverlayOpacity = SlSchOverlay.Value;
        sl.TopMost = ChkSchTop.IsChecked == true;
        sl.ClickThrough = ChkSchClick.IsChecked == true;
        // 鼠标穿透与靠边隐藏互斥：穿透开启时强制关闭两类靠边隐藏
        sl.AutoHide = ChkSchHide.IsChecked == true && !sl.ClickThrough;
        sl.AutoHideDuringClass = ChkSchClassHide.IsChecked == true && !sl.ClickThrough;

        var nl = _s.NotificationLayout;
        nl.Anchor = CbNotAnchor.SelectedIndex == 1 ? "Bottom" : "Top";
        nl.Height = SlNotHeight.Value;
        nl.FontSize = SlNotFont.Value;
        nl.MarqueeSpeed = SlNotSpeed.Value;
        nl.Opacity = SlNotOpacity.Value;
        nl.OverlayOpacity = SlNotOverlay.Value;
        nl.TopMost = ChkNotTop.IsChecked == true;
        nl.ClickThrough = ChkNotClick.IsChecked == true;
        nl.AutoHide = ChkNotHide.IsChecked == true && !nl.ClickThrough;
        nl.AutoHideDuringClass = ChkNotClassHide.IsChecked == true && !nl.ClickThrough;

        Store.SaveSettings(_s);
        AppEvents.RaiseLayoutChanged();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
