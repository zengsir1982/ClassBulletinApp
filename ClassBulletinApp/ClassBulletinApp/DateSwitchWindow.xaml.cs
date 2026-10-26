using System;
using System.Windows;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class DateSwitchWindow : Window
{
    public DateSwitchWindow(DateTime current)
    {
        InitializeComponent();
        Dp.SelectedDate = current.Date;
    }

    private void Prev_Click(object sender, RoutedEventArgs e)
        => Dp.SelectedDate = (Dp.SelectedDate ?? DateTime.Today).AddDays(-1);

    private void Next_Click(object sender, RoutedEventArgs e)
        => Dp.SelectedDate = (Dp.SelectedDate ?? DateTime.Today).AddDays(1);

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        s.DisplayDateMode = "Today";
        s.FixedDisplayDate = "";
        Store.SaveSettings(s);
        AppEvents.RaiseScheduleChanged();
        DialogResult = true;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Dp.SelectedDate == null) { Close(); return; }
        var s = Store.LoadSettings();
        s.DisplayDateMode = "Fixed";
        s.FixedDisplayDate = Dp.SelectedDate.Value.ToString("yyyy-MM-dd");
        Store.SaveSettings(s);
        AppEvents.RaiseScheduleChanged();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
