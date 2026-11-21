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
    
        GlassChrome.Prepare(this);}

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

    /// <summary>
    /// r133：调休上课的正规做法在校历里，这里只是直达入口（带上面选的日期）。
    /// 关掉校历后如果「今天」已经被设成补课日 / 放假日，就顺手把显示日期切回今天 ——
    /// 用户刚设完调休，最想看的是"今天的样子"，而不是继续停在某个固定日期上。
    /// </summary>
    private void Calendar_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new SchoolCalendarWindow(Dp.SelectedDate ?? DateTime.Today) { Owner = this };
        wnd.ShowDialog();

        var s = Store.LoadSettings();
        if (string.Equals(s.DisplayDateMode, "Fixed", StringComparison.OrdinalIgnoreCase))
        {
            var plan = SchoolCalendar.Resolve(Store.LoadSchedule(), DateTime.Today, s.SemesterStartMonday);
            if (plan.IsMakeup || plan.IsOff)
            {
                s.DisplayDateMode = "Today";
                s.FixedDisplayDate = "";
                Store.SaveSettings(s);
                AppEvents.RaiseScheduleChanged();
            }
        }
        DialogResult = true;
        Close();
    }
}
