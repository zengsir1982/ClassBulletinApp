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
    
        GlassChrome.Prepare(this);}

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

        // 密保 / 邮箱 / 手机目前挂在"管理员"这一个账号上（存在 settings 里）。
        // 其他角色（班主任 / 电教管理员）由管理员在用户管理里重置密码，不走这里。
        bool isAdminAccount = false;
        try
        {
            var book = Store.LoadUsers();
            var au = book.Find(u);
            isAdminAccount = au != null && au.Role == UserRole.Admin;
        }
        catch { }

        TxtSecQuestion.Text = (isAdminAccount && !string.IsNullOrEmpty(s.SecurityQuestion))
            ? s.SecurityQuestion
            : "（请输入正确的用户名后显示）";
    }

    private void BtnVerify_Click(object sender, RoutedEventArgs e)
    {
        var s = Store.LoadSettings();
        string user = (TxtUser.Text ?? "").Trim();

        UserBook book;
        try { book = Store.LoadUsers(); }
        catch (System.Exception ex) { ShowMsg("读取用户表失败：" + ex.Message, false); return; }

        var target = book.Find(user);
        if (target == null)
        {
            ShowMsg("用户名不正确。", false);
            return;
        }
        if (target.Role != UserRole.Admin)
        {
            ShowMsg("只有管理员账号可以在这里自助找回密码。其他账号（班主任 / 电教管理员）请让管理员在「用户管理」里重置。", false);
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

        // ★ 密码现在只存哈希，**无法还原成明文**，所以"找回"只能是"重置"。
        //   与其显示一串假东西，不如直接引导到重置。
        if (RbRetrieve.IsChecked == true)
        {
            TxtReveal.Text = "验证通过。为安全起见，密码不以明文保存、无法查看；请在「重置密码」里设置一个新密码。";
            TxtReveal.Visibility = Visibility.Visible;
            ShowMsg("请切换到「重置密码」。", true);
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
            string user = (TxtUser.Text ?? "").Trim();
            var book = Store.LoadUsers();
            var target = book.Find(user);
            if (target == null || target.Role != UserRole.Admin)
            {
                ShowMsg("该账号不存在或不是管理员，无法在这里重置。", false);
                return;
            }

            // 哈希 + 新盐；明文只留一份在 settings.Password（兼容旧逻辑，且登录已不再用它比对）
            PasswordHasher.SetPassword(target, p1);

            var s = Store.LoadSettings();
            s.Password = p1;
            s.LastUsername = target.Username;

            // 登录界面阶段 EnablePersistence 仍为 false（游客开关尚未置位），临时开启以落盘新密码
            bool prev = Store.EnablePersistence;
            Store.EnablePersistence = true;
            try
            {
                Store.SaveUsers(book);
                Store.SaveSettings(s);
            }
            finally { Store.EnablePersistence = prev; }

            // 自动登录若正用这个账号，密码也得跟着更新，否则下次开机自动登录会失败
            if (string.Equals(s.AutoLoginUser, target.Username, StringComparison.OrdinalIgnoreCase))
            {
                s.AutoLoginPassword = p1;
                bool prev2 = Store.EnablePersistence;
                Store.EnablePersistence = true;
                try { Store.SaveSettings(s); } finally { Store.EnablePersistence = prev2; }
            }

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
