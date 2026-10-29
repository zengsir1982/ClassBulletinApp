using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 启动登录窗：管理员（默认 admin / 000）登录后正常使用并保存数据；
/// 「游客体验」进入游客模式，不持久化任何改动，退出后自动丢弃（等于清空）。
/// 关闭本窗（未登录）则整个程序退出。
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>true = 游客模式（不持久化）；false = 已登录管理员。</summary>
    public bool IsGuest { get; private set; } = true;

    public LoginWindow()
    {
        InitializeComponent();
        TxtUser.Text = "";
        TxtPwd.Password = "";
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        string user = (TxtUser.Text ?? "").Trim();
        string pwd = TxtPwd.Password ?? "";
        if (user == s.Username && pwd == s.Password)
        {
            IsGuest = false;
            DialogResult = true;
        }
        else
        {
            TxtError.Text = "用户名或密码不正确。";
            TxtError.Visibility = Visibility.Visible;
        }
    }

    private void BtnGuest_Click(object sender, RoutedEventArgs e)
    {
        IsGuest = true;
        DialogResult = true;
    }

    private void TxtPwd_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnLogin_Click(sender, e);
    }

    private void Txt_TextChanged(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
    }
}
