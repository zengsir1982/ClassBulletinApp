using System;
using System.Windows;
using Microsoft.Win32;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = Store.LoadSettings();
        ChkFullscreen.IsChecked = s.LaunchFullscreen;
        ChkLocked.IsChecked = s.LaunchLocked;
        TxtVersion.Text = "班级公布栏 " + AppInfo.VersionText;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        s.LaunchFullscreen = ChkFullscreen.IsChecked == true;
        s.LaunchLocked = ChkLocked.IsChecked == true;
        Store.SaveSettings(s);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>把本程序加入/移出当前用户开机自启（Run 注册表）。</summary>
    public static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("ClassBulletinApp", exe);
            }
            else
            {
                key.DeleteValue("ClassBulletinApp", false);
            }
        }
        catch { /* 无权限时静默忽略 */ }
    }
}
