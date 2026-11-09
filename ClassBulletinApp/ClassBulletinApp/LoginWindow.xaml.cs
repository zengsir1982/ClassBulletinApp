using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleCore.Models;
using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 启动登录窗：查本机用户表（users.json）验证身份。
///
/// <para>角色：管理员可做一切；班主任只能改本班课表；电教管理员（学生）只能控制本机显示。
/// 「游客体验」= 只能看和控制显示，不持久化任何改动。</para>
///
/// <para>★ 自动登录只对<b>非管理员</b>生效：教室机开机自动进入受限身份，
/// 否则"谁坐到电脑前谁就是管理员"，权限形同虚设。</para>
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>登录成功的账号；<c>null</c> = 游客模式（不持久化）。</summary>
    public AppUser? User { get; private set; }

    /// <summary>true = 游客模式（不持久化）；false = 已登录。</summary>
    public bool IsGuest => User == null;

    /// <summary>本次登录成功所用的用户名（供 App 记住，下次预填）。</summary>
    public string EnteredUser { get; private set; } = "";

    /// <summary>登录窗上「开机自动登录」的勾选状态。</summary>
    public bool AutoLoginWanted { get; private set; } = true;

    /// <summary>构造期间为 true：给勾选框赋初值时不要触发联动/写盘。</summary>
    private bool _loading = true;

    public LoginWindow()
    {
        InitializeComponent();

        var s = Store.LoadSettings();

        // 预填用户名：勾了「记住账号」用上次登录名；没勾但记住了密码，也要知道是哪个账号
        string user = "";
        if (s.RememberUsername && !string.IsNullOrWhiteSpace(s.LastUsername))
            user = s.LastUsername;
        else if (!string.IsNullOrWhiteSpace(s.AutoLoginUser))
            user = s.AutoLoginUser;

        TxtUser.Text = user ?? "";

        // 预填密码：只在勾了「记住密码」且是记住的那个账号时（管理员密码从不写进 AutoLoginPassword）
        if (s.RememberPassword && !string.IsNullOrEmpty(user)
            && string.Equals(user, s.AutoLoginUser, StringComparison.OrdinalIgnoreCase))
            TxtPwd.Password = s.AutoLoginPassword ?? "";

        ChkRememberUser.IsChecked = s.RememberUsername;
        ChkRememberPwd.IsChecked = s.RememberPassword;
        ChkAutoLogin.IsChecked = s.AutoLogin;

        _loading = false;
        UpdateSubtitle();
    
        GlassChrome.Prepare(this);}

    /// <summary>三个勾选联动：自动登录必须有密码；取消记住密码则自动登录一并取消。</summary>
    private void LoginPref_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (ChkAutoLogin.IsChecked == true && ChkRememberPwd.IsChecked != true)
            ChkRememberPwd.IsChecked = true;

        if (ChkRememberPwd.IsChecked != true && ChkAutoLogin.IsChecked == true)
            ChkAutoLogin.IsChecked = false;

        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        if (ChkAutoLogin.IsChecked == true)
            TxtSubtitle.Text = "已开启开机自动登录，日常开机无需再输入账号密码；也可以改用游客身份体验（不保存任何数据）";
        else if (ChkRememberUser.IsChecked == true || ChkRememberPwd.IsChecked == true)
            TxtSubtitle.Text = "已为你记住部分登录信息；也可以改用游客身份体验（不保存任何数据）";
        else
            TxtSubtitle.Text = "未记住账号密码，每次启动都需要重新登录；也可以改用游客身份体验（不保存任何数据）";
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        string user = (TxtUser.Text ?? "").Trim();
        string pwd = TxtPwd.Password ?? "";

        if (string.IsNullOrEmpty(user))
        {
            ShowError("请输入用户名。");
            return;
        }

        UserBook book;
        try { book = Store.LoadUsers(); }
        catch (Exception ex) { ShowError("读取用户表失败：" + ex.Message); return; }

        var u = book.Find(user);
        if (u == null || !u.Enabled || !PasswordHasher.Verify(pwd, u.PasswordHash, u.Salt))
        {
            ShowError(u != null && !u.Enabled
                ? "该账号已被停用，请联系管理员。"
                : "用户名或密码不正确。");
            return;
        }

        User = u;
        EnteredUser = u.Username;
        AutoLoginWanted = ChkAutoLogin.IsChecked == true;

        bool rememberUser = ChkRememberUser.IsChecked == true;
        bool rememberPwd = ChkRememberPwd.IsChecked == true;

        // ---- 自动登录：只允许受限角色 ----
        var s = Store.LoadSettings();
        bool isAdmin = u.Role == UserRole.Admin;

        if (isAdmin && AutoLoginWanted)
        {
            // 管理员：不写自动登录凭据。用户若勾了，明确告知为什么忽略。
            MessageBox.Show(
                "管理员账号不参与「开机自动登录」。\n\n"
                + "这是为了防止教室机开机就变成管理员（谁坐到电脑前谁有权限）。\n"
                + "本机仍会自动以"
                + (string.IsNullOrWhiteSpace(s.AutoLoginUser) ? "受限账号" : $"「{s.AutoLoginUser}」")
                + "进入，要改数据时再从这里切到管理员即可。",
                "关于自动登录", MessageBoxButton.OK, MessageBoxImage.Information);
            ChkAutoLogin.IsChecked = false;
            AutoLoginWanted = false;
        }

        s.RememberUsername = rememberUser;
        s.RememberPassword = rememberPwd;
        // 自动登录必须有密码，且管理员永不参与
        s.AutoLogin = AutoLoginWanted && rememberPwd && !isAdmin;

        if (rememberPwd)
        {
            s.AutoLoginUser = u.Username;
            s.AutoLoginPassword = pwd;
        }
        else
        {
            s.AutoLoginUser = "";
            s.AutoLoginPassword = "";
        }

        s.LastUsername = rememberUser ? u.Username : "";
        Store.SaveSettings(s);

        DialogResult = true;
    }

    void ShowError(string msg)
    {
        TxtError.Text = msg;
        TxtError.Visibility = Visibility.Visible;
    }

    private void BtnGuest_Click(object sender, RoutedEventArgs e)
    {
        User = null;
        DialogResult = true;
    }

    private void BtnForgot_Click(object sender, RoutedEventArgs e)
    {
        try { new RetrievePasswordWindow { Owner = this }.ShowDialog(); }
        catch { }
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
