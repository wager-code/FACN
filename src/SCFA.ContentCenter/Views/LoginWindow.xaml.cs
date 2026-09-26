using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.Views;

public partial class LoginWindow : Window
{
    private bool _handoffClose;
    private bool _registerMode;
    private bool _restoreAttempted;
    private bool _loadingAccounts;
    private bool _updatingOptions;
    private bool _syncingPasswordVisibility;
    private string _lastLoadedAccount = "";
    public LoginWindow()
    {
        InitializeComponent();
        LoadRememberedCredentials();
        OfflineButton.Visibility = App.Services.Config.Current.OfflineAllowed ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(App.Services.Config.LoadWarning)) SetStatus(App.Services.Config.LoadWarning);
        Loaded += LoginWindow_Loaded;
    }

    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_restoreAttempted) return;
        _restoreAttempted = true;
        if (!App.Services.Config.Current.AutoLogin)
        {
            FocusPreferredInput();
            return;
        }
        var saved = await App.Services.Session.LoadAsync();
        if (saved is null) { FocusPreferredInput(); return; }
        if (!saved.MatchesAuthority(App.Services.Auth.BaseUrl, App.Services.Auth.PinnedCertSha256))
        {
            App.Services.Session.Clear();
            SetStatus("账号服务器配置已变化，请重新登录。");
            FocusPreferredInput();
            return;
        }
        if (saved.IsExpired)
        {
            App.Services.Session.Clear();
            SetStatus("登录会话已过期，请重新登录。");
            FocusPreferredInput();
            return;
        }

        SetLoginEnabled(false);
        SetStatus("正在恢复登录会话…");
        try
        {
            App.Services.ReconfigureAuth();
            App.Services.Auth.SetToken(saved.Token);
            var result = await App.Services.Auth.MeAsync();
            App.Services.CurrentUser = result.User;
            App.Services.OfflineMode = false;
            await App.Services.Session.SaveAsync(
                result.User,
                saved.Token,
                saved.ExpiresAt?.ToString("O"),
                App.Services.Auth.BaseUrl,
                App.Services.Auth.PinnedCertSha256);
            App.OpenMainWindow();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            App.Services.Auth.SetToken("");
            App.Services.Session.Clear();
            SetStatus("登录会话已失效，请重新登录。");
        }
        catch (Exception ex)
        {
            App.Services.Auth.SetToken("");
            SetStatus("自动登录验证失败，可手动登录或离线进入。");
            App.Services.Log.Error("自动登录验证失败", ex);
        }
        finally
        {
            if (IsVisible) { SetLoginEnabled(true); FocusPreferredInput(); }
        }
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var account = AccountBox.Text.Trim(); var password = PasswordBox.Password;
        if (account.Length == 0 || password.Length == 0) { SetStatus("请输入账号和密码。"); return; }
        SubmitButton.IsEnabled = false; SetStatus(_registerMode ? "正在注册…" : "正在连接账号服务器…");
        try
        {
            App.Services.ReconfigureAuth();
            if (_registerMode)
            {
                await App.Services.Auth.RegisterAsync(account, EmailBox.Text.Trim(), password);
                _registerMode = false; ApplyMode(); SetStatus("注册成功，请使用新账号登录。"); PasswordBox.Clear(); return;
            }
            var result = await App.Services.Auth.LoginAsync(account, password);
            var verified = await App.Services.Auth.MeAsync();
            App.Services.CurrentUser = verified.User; App.Services.OfflineMode = false;
            await SaveRememberedCredentialsAsync(account, password);
            if (App.Services.Config.Current.AutoLogin)
            {
                await App.Services.Session.SaveAsync(
                    verified.User,
                    result.Token,
                    result.ExpiresAt,
                    App.Services.Auth.BaseUrl,
                    App.Services.Auth.PinnedCertSha256);
            }
            else App.Services.Session.Clear();
            App.OpenMainWindow();
        }
        catch (Exception ex) { App.Services.Auth.SetToken(""); SetStatus(ex.Message); App.Services.Log.Error("登录失败", ex); }
        finally { SubmitButton.IsEnabled = true; }
    }

    private void SwitchButton_Click(object sender, RoutedEventArgs e) { _registerMode = !_registerMode; ApplyMode(); SetStatus(""); }
    private void ApplyMode()
    {
        ModeTitle.Text = _registerMode ? "注册 SCFA 账号" : "登录 SCFA 内容中心";
        ModeSubtitle.Text = _registerMode ? "创建账号后即可使用在线内容服务" : "继续管理地图与模组";
        SubmitButton.Content = _registerMode ? "注册" : "登录";
        AccountPrompt.Text = _registerMode ? "已经有账号？" : "还没有账号？";
        SwitchButton.Content = _registerMode ? "返回登录" : "立即注册";
        EmailPanel.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        RememberPanel.Visibility = RememberHint.Visibility = _registerMode ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.BeginInvoke(FocusPreferredInput, DispatcherPriority.Input);
    }
    private void SetLoginEnabled(bool enabled)
    {
        AccountBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        VisiblePasswordBox.IsEnabled = enabled;
        PasswordVisibilityButton.IsEnabled = enabled;
        EmailBox.IsEnabled = enabled;
        RememberAccountBox.IsEnabled = enabled;
        RememberPasswordBox.IsEnabled = enabled;
        AutoLoginBox.IsEnabled = enabled;
        SubmitButton.IsEnabled = enabled;
        SwitchButton.IsEnabled = enabled;
        OfflineButton.IsEnabled = enabled;
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    public void PrepareForWindowHandoff() => _handoffClose = true;
    private void SetStatus(string? value)
    {
        StatusText.Text = value ?? "";
        StatusPanel.Visibility = string.IsNullOrWhiteSpace(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadRememberedCredentials()
    {
        var config = App.Services.Config.Current;
        var accounts = config.LoginAccounts ??= [];
        _loadingAccounts = true;
        AccountBox.ItemsSource = accounts.OrderByDescending(x => x.LastUsedAt).Select(x => x.Account).ToList();
        var preferred = config.RememberLoginAccount ? config.LastLoginAccount : "";
        if (config.RememberLoginAccount && string.IsNullOrWhiteSpace(preferred)) preferred = accounts.OrderByDescending(x => x.LastUsedAt).Select(x => x.Account).FirstOrDefault() ?? "";
        AccountBox.Text = preferred;
        _loadingAccounts = false;
        RememberAccountBox.IsChecked = config.RememberLoginAccount;
        LoadPasswordForAccount(preferred, config.RememberLoginPassword, config.AutoLogin);
    }

    private async Task SaveRememberedCredentialsAsync(string account, string password)
    {
        var config = App.Services.Config.Current;
        var rememberAccount = RememberAccountBox.IsChecked == true;
        var rememberPassword = rememberAccount && RememberPasswordBox.IsChecked == true;
        var autoLogin = rememberPassword && AutoLoginBox.IsChecked == true;
        var records = config.LoginAccounts ??= [];
        var existing = records.FirstOrDefault(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
        if (rememberAccount)
        {
            existing ??= new Models.LoginAccountRecord { Account = account };
            existing.Account = account;
            existing.PasswordEncrypted = rememberPassword ? LoginCredentialProtector.Protect(password) : "";
            existing.LastUsedAt = DateTimeOffset.UtcNow;
            records.RemoveAll(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
            records.Insert(0, existing);
            if (records.Count > 8) records.RemoveRange(8, records.Count - 8);
        }
        else records.RemoveAll(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
        config.RememberLoginAccount = rememberAccount;
        config.RememberLoginPassword = rememberPassword;
        config.AutoLogin = autoLogin;
        config.LastLoginAccount = rememberAccount ? account : "";
        config.LoginPasswordEncrypted = rememberPassword ? existing?.PasswordEncrypted ?? "" : "";
        await App.Services.Config.SaveAsync();
        _lastLoadedAccount = account;
    }

    private void RememberOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingOptions) return;
        _updatingOptions = true;
        if (ReferenceEquals(sender, RememberPasswordBox) && RememberPasswordBox.IsChecked == true && RememberAccountBox.IsChecked != true)
            RememberAccountBox.IsChecked = true;
        else if (ReferenceEquals(sender, RememberAccountBox) && RememberAccountBox.IsChecked == false && RememberPasswordBox.IsChecked == true)
            RememberPasswordBox.IsChecked = false;
        if (ReferenceEquals(sender, AutoLoginBox) && AutoLoginBox.IsChecked == true)
        {
            RememberAccountBox.IsChecked = true;
            RememberPasswordBox.IsChecked = true;
        }
        if (ReferenceEquals(sender, RememberPasswordBox) && RememberPasswordBox.IsChecked != true)
            AutoLoginBox.IsChecked = false;
        if (ReferenceEquals(sender, RememberAccountBox) && RememberAccountBox.IsChecked != true)
            AutoLoginBox.IsChecked = false;
        _updatingOptions = false;
    }

    private void AccountBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingAccounts || AccountBox.SelectedItem is not string account) return;
        _loadingAccounts = true;
        AccountBox.Text = account;
        _loadingAccounts = false;
        LoadPasswordForAccount(account, true, App.Services.Config.Current.AutoLogin);
    }

    private void AccountBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loadingAccounts) return;
        var account = AccountBox.Text.Trim();
        if (string.Equals(account, _lastLoadedAccount, StringComparison.OrdinalIgnoreCase)) return;
        _updatingOptions = true;
        PasswordBox.Clear();
        RememberPasswordBox.IsChecked = false;
        AutoLoginBox.IsChecked = false;
        _updatingOptions = false;
    }

    private void LoadPasswordForAccount(string account, bool rememberPassword, bool autoLogin)
    {
        _updatingOptions = true;
        PasswordBox.Clear();
        RememberPasswordBox.IsChecked = false;
        AutoLoginBox.IsChecked = false;
        var record = App.Services.Config.Current.LoginAccounts.FirstOrDefault(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
        if (rememberPassword && record is not null && LoginCredentialProtector.TryUnprotect(record.PasswordEncrypted, out var password))
        {
            PasswordBox.Password = password;
            RememberPasswordBox.IsChecked = true;
            AutoLoginBox.IsChecked = autoLogin && string.Equals(App.Services.Config.Current.LastLoginAccount, account, StringComparison.OrdinalIgnoreCase);
        }
        else if (rememberPassword && record is not null && !string.IsNullOrWhiteSpace(record.PasswordEncrypted))
        {
            record.PasswordEncrypted = "";
            App.Services.Config.Current.RememberLoginPassword = false;
            App.Services.Config.Current.LoginPasswordEncrypted = "";
            App.Services.Config.Current.AutoLogin = false;
            SetStatus("已保存的密码无法在当前 Windows 账户中解密，请重新输入密码。");
            _ = App.Services.Config.SaveAsync();
        }
        _lastLoadedAccount = account;
        _updatingOptions = false;
    }

    private void AccountBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (_registerMode) EmailBox.Focus();
        else FocusPasswordInput();
        e.Handled = true;
    }

    private void EmailBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        FocusPasswordInput();
        e.Handled = true;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPasswordVisibility) return;
        _syncingPasswordVisibility = true;
        VisiblePasswordBox.Text = PasswordBox.Password;
        _syncingPasswordVisibility = false;
    }

    private void VisiblePasswordBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPasswordVisibility) return;
        _syncingPasswordVisibility = true;
        PasswordBox.Password = VisiblePasswordBox.Text;
        _syncingPasswordVisibility = false;
    }

    private void PasswordVisibilityButton_Changed(object sender, RoutedEventArgs e)
    {
        var showPassword = PasswordVisibilityButton.IsChecked == true;
        _syncingPasswordVisibility = true;
        if (showPassword)
        {
            VisiblePasswordBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            VisiblePasswordBox.Visibility = Visibility.Visible;
            VisiblePasswordBox.CaretIndex = VisiblePasswordBox.Text.Length;
            VisiblePasswordBox.Focus();
        }
        else
        {
            PasswordBox.Password = VisiblePasswordBox.Text;
            VisiblePasswordBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
        _syncingPasswordVisibility = false;
    }

    private void FocusPasswordInput()
    {
        if (PasswordVisibilityButton.IsChecked == true) VisiblePasswordBox.Focus();
        else PasswordBox.Focus();
    }

    private void FocusPreferredInput()
    {
        if (!IsVisible) return;
        if (string.IsNullOrWhiteSpace(AccountBox.Text)) AccountBox.Focus();
        else if (_registerMode && string.IsNullOrWhiteSpace(EmailBox.Text)) EmailBox.Focus();
        else if (PasswordBox.Password.Length == 0) FocusPasswordInput();
        else SubmitButton.Focus();
    }
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_handoffClose) Application.Current.Shutdown();
    }

    private void OfflineButton_Click(object sender, RoutedEventArgs e)
    {
        App.Services.OfflineMode = true;
        App.Services.CurrentUser = new Models.UserInfo { Username = "local", DisplayName = "本地用户", RoleLabel = "离线" };
        App.OpenMainWindow();
    }
}
