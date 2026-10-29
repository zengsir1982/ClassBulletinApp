using System.Windows;
using System.Windows.Controls;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 找回 / 重置密码窗口：先输入用户名，再凭「用户中心」设置的电子邮箱、手机号码或密保问题答案
/// 验证身份；验证通过后，找回模式直接显示当前密码，重置模式可设置新密码。
/// </summary>
public partial class RetrievePasswordWindow : Window
{
    /// <summary>身份已验证通过（用于重置模式进入新密码输入）。</summary>
    private bool _verified;

    public RetrievePasswordWindow()
    {
        InitializeComponent();
        Mode_Changed(null, null);
    }

    private void Mode_Changed(object? sender, RoutedEventArgs? e)
    {
        // 加载期 RadioButton 的 IsChecked 会触发本事件，而此时较靠后的控件尚未实例化，
        // 故先做空值保护；初始的折叠状态已由 XAML 默认值提供。
        if (TxtReveal == null || PnlReset == null || BtnConfirmReset == null || BtnVerify == null || TxtMsg == null)
            return;
        // 切换模式时收起结果区，避免串味
        TxtReveal.Visibility = Visibility.Collapsed;
        PnlReset.Visibility = Visibility.Collapsed;
        BtnConfirmReset.Visibility = Visibility.Collapsed;
        BtnVerify.Visibility = Visibility.Visible;
        _verified = false;
        ShowMsg("", false);
    }

    private void TxtUser_TextChanged(object sender, TextChangedEventArgs e)
    {
        var s = Store.LoadSettings();
        string u = (TxtUser.Text ?? "").Trim();
        TxtSecQuestion.Text = (u == s.Username && !string.IsNullOrEmpty(s.SecurityQuestion))
            ? s.SecurityQuestion
            : "（请输入正确的用户名后显示）";
    }

    private void BtnVerify_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        string user = (TxtUser.Text ?? "").Trim();
        if (user != s.Username)
        {
            ShowMsg("用户名不正确。", false);
            return;
        }

        bool emailOk = !string.IsNullOrEmpty(s.Email) && (TxtEmail.Text ?? "").Trim() == s.Email;
        bool phoneOk = !string.IsNullOrEmpty(s.Phone) && (TxtPhone.Text ?? "").Trim() == s.Phone;
        bool secOk = !string.IsNullOrEmpty(s.SecurityQuestion) && !string.IsNullOrEmpty(s.SecurityAnswer)
                     && (TxtAnswer.Text ?? "").Trim() == s.SecurityAnswer;

        bool anySet = !string.IsNullOrEmpty(s.Email) || !string.IsNullOrEmpty(s.Phone) || !string.IsNullOrEmpty(s.SecurityQuestion);
        if (!anySet)
        {
            ShowMsg("该账号未设置任何找回方式（邮箱 / 手机 / 密保）。请先以管理员身份登录，在「用户中心」设置后再使用本功能。", false);
            return;
        }
        if (!(emailOk || phoneOk || secOk))
        {
            ShowMsg("验证失败：电子邮箱 / 手机号码 / 密保答案与您设置的不符。", false);
            return;
        }

        _verified = true;
        if (RbRetrieve.IsChecked == true)
        {
            TxtReveal.Text = "验证通过。您的密码是：" + s.Password;
            TxtReveal.Visibility = Visibility.Visible;
            ShowMsg("请妥善保管您的密码。", true);
            BtnVerify.Visibility = Visibility.Collapsed;
        }
        else
        {
            PnlReset.Visibility = Visibility.Visible;
            BtnConfirmReset.Visibility = Visibility.Visible;
            BtnVerify.Visibility = Visibility.Collapsed;
            ShowMsg("验证通过，请设置新密码。", true);
        }
    }

    private void BtnConfirmReset_Click(object sender, RoutedEventArgs e)
    {
        if (!_verified) return;
        string p1 = TxtNewPwd.Password ?? "";
        string p2 = TxtNewPwd2.Password ?? "";
        if (string.IsNullOrEmpty(p1))
        {
            ShowMsg("新密码不能为空。", false);
            return;
        }
        if (p1 != p2)
        {
            ShowMsg("两次输入的新密码不一致。", false);
            return;
        }

        try
        {
            var s = Store.LoadSettings();
            s.Password = p1;
            // 登录界面阶段 EnablePersistence 仍为 false（游客开关尚未置位），临时开启以落盘新密码
            bool prev = Store.EnablePersistence;
            Store.EnablePersistence = true;
            try { Store.SaveSettings(s); }
            finally { Store.EnablePersistence = prev; }
            ShowMsg("密码已重置，请用新密码登录。", true);
            PnlReset.IsEnabled = false;
            BtnConfirmReset.IsEnabled = false;
        }
        catch (System.Exception ex)
        {
            ShowMsg("重置失败：" + ex.Message, false);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void ShowMsg(string text, bool ok)
    {
        if (string.IsNullOrEmpty(text)) { TxtMsg.Visibility = Visibility.Collapsed; return; }
        TxtMsg.Text = text;
        TxtMsg.Foreground = ok
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        TxtMsg.Visibility = Visibility.Visible;
    }
}
