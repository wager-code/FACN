using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;
using SCFA.ContentCenter.Views;

internal static class LoginAccountRegression
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var oldConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
        var oldData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", root);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(root, "data"));
            var config = new ConfigService();
            var session = new UserSessionService();
            var history = new LoginAccountHistoryService(config, session);
            config.Current.RememberLoginAccount = false; // Old config must not opt out of the new default.
            await history.SaveAsync(" Alpha ", "fixture-secret", false, true);
            check(config.Current.RememberLoginAccount && config.Current.LastLoginAccount == "Alpha" &&
                  config.Current.LoginAccounts.Count == 1, "成功登录始终记住账号并兼容旧配置");
            check(!config.Current.AutoLogin && !config.Current.RememberLoginPassword &&
                  config.Current.LoginAccounts[0].PasswordEncrypted == "" &&
                  !File.ReadAllText(config.ConfigPath).Contains("fixture-secret"), "不记住密码时不保存密码或启用自动登录");
            for (var i = 0; i < 10; i++) await history.SaveAsync("other-" + i, "", false, false);
            await config.LoadAsync();
            check(config.Current.LoginAccounts.Count == 11, "超过八个账号的记录保持保存且重新加载不丢失");
            await history.SaveAsync("ALPHA", "fixture-secret", true, true);
            check(config.Current.LoginAccounts.Count == 11 &&
                  LoginCredentialProtector.TryUnprotect(config.Current.LoginAccounts[0].PasswordEncrypted, out var password) &&
                  password == "fixture-secret" && !File.ReadAllText(config.ConfigPath).Contains("fixture-secret"),
                  "重复账号忽略大小写并用Windows用户加密保存密码");
            await session.SaveAsync(new UserInfo { Username = "Alpha" }, "fixture-token",
                DateTimeOffset.UtcNow.AddHours(1).ToString("O"), "https://example.test", "");
            var sessionPath = Path.Combine(root, "data", "session.dat");
            var savedBytes = File.ReadAllBytes(sessionPath);
            await history.DeleteAsync("other-0");
            check(config.Current.AutoLogin && config.Current.LastLoginAccount == "ALPHA" &&
                  config.Current.LoginAccounts.Count == 10 && File.ReadAllBytes(sessionPath).SequenceEqual(savedBytes),
                  "删除其他账号保留最后登录账号的密码和会话");
            await history.DeleteAsync(" alpha ");
            check(!config.Current.AutoLogin && !config.Current.RememberLoginPassword &&
                  config.Current.LastLoginAccount == "" && config.Current.LoginPasswordEncrypted == "" &&
                  !File.Exists(sessionPath), "删除最后登录账号同时清除旧密码镜像和自动登录会话");
            foreach (var account in config.Current.LoginAccounts.Select(x => x.Account).ToArray())
                await history.DeleteAsync(account);
            await config.LoadAsync();
            check(config.Current.LoginAccounts.Count == 0 && config.Current.LastLoginAccount == "",
                "全部删除后重新加载不会通过旧字段恢复已删除账号");
            await history.SaveAsync("retain", "fixture-secret", true, true);
            await session.SaveAsync(new UserInfo { Username = "retain" }, "fixture-token");
            Directory.CreateDirectory(config.ConfigPath + ".tmp");
            try
            {
                var failed = false;
                try { await history.DeleteAsync("retain"); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                check(failed && config.Current.LoginAccounts.Single().Account == "retain" &&
                      config.Current.AutoLogin && File.Exists(sessionPath),
                      "账号删除保存失败时保留原记录和会话供重试");
            }
            finally { Directory.Delete(config.ConfigPath + ".tmp"); }
            await history.SaveAsync("retain", "", false, false);
            check(config.Current.LoginAccounts.Single().PasswordEncrypted == "" &&
                  config.Current.LoginPasswordEncrypted == "" && !config.Current.AutoLogin,
                  "再次登录取消记住密码清除该账号保存的密码");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", oldConfig);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", oldData);
        }
    }

    internal static void RunUiSmoke(Action<bool, string> check)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var oldConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
            var oldData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
            var isolated = Path.Combine(Path.GetTempPath(), "scfa_login_ui_" + Guid.NewGuid().ToString("N"));
            SCFA.ContentCenter.App? app = null;
            LoginWindow? window = null;
            try
            {
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", isolated);
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(isolated, "data"));
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = new SCFA.ContentCenter.App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var services = ServiceRegistry.CreateAsync().GetAwaiter().GetResult();
                // No await captures the UI dispatcher during fixture setup.
                var next = ConfigService.Clone(services.Config.Current);
                next.LoginAccounts =
                [
                    new() { Account = "测试账号一", PasswordEncrypted = LoginCredentialProtector.Protect("fixture-secret"), LastUsedAt = DateTimeOffset.UtcNow },
                    new() { Account = "测试账号二", LastUsedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
                ];
                next.LastLoginAccount = "测试账号一";
                next.RememberLoginPassword = true;
                next.AutoLogin = false;
                var save = services.Config.SaveAsync(next);
                PumpUntil(() => save.IsCompleted);
                save.GetAwaiter().GetResult();
                typeof(SCFA.ContentCenter.App).GetProperty("Services")!.SetValue(null, services);
                window = new LoginWindow { Opacity = 0, ShowInTaskbar = false };
                window.Show();
                var accounts = (ComboBox)window.FindName("AccountBox");
                var password = (PasswordBox)window.FindName("PasswordBox");
                var remember = (CheckBox)window.FindName("RememberPasswordBox");
                var auto = (CheckBox)window.FindName("AutoLoginBox");
                check(window.FindName("RememberAccountBox") is null && accounts.Text == "测试账号一" &&
                      password.Password == "fixture-secret", "真实登录页无需账号勾选即可恢复账号和加密密码");
                window.UpdateLayout();
                check(remember.HorizontalAlignment == HorizontalAlignment.Left &&
                      auto.HorizontalAlignment == HorizontalAlignment.Right &&
                      remember.TranslatePoint(new Point(), window).X < auto.TranslatePoint(new Point(), window).X,
                      "真实登录页记住密码在左侧、自动登录在右侧");
                auto.IsChecked = true;
                remember.IsChecked = false;
                check(auto.IsChecked == false, "取消记住密码同时取消自动登录");
                auto.IsChecked = true;
                check(remember.IsChecked == true, "勾选自动登录同时启用记住密码");
                accounts.Text = "临时账号";
                PumpUntil(() => password.Password.Length == 0);
                check(!remember.IsChecked.GetValueOrDefault() && !auto.IsChecked.GetValueOrDefault(),
                      "手动输入不同账号时清除上一账号的密码和自动登录选项");
                accounts.SelectedItem = "测试账号一";
                check(password.Password == "fixture-secret" && remember.IsChecked == true,
                      "重新选择保存账号恢复该账号的加密密码");
                auto.IsChecked = true;
                window.UpdateLayout();
                SaveImage((FrameworkElement)window.Content, "login-account-layout.png");
                accounts.ApplyTemplate();
                var popup = (Popup)accounts.Template.FindName("PART_Popup", accounts);
                popup.Child.Opacity = 0; // Native popup remains invisible during automated verification.
                accounts.IsDropDownOpen = true;
                PumpUntil(() => accounts.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem);
                ((FrameworkElement)popup.Child).UpdateLayout();
                var rows = Enumerable.Range(0, accounts.Items.Count)
                    .Select(i => (ComboBoxItem)accounts.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
                var buttons = rows.SelectMany(Descendants).OfType<Button>().ToArray();
                check(buttons.Length == 2 && buttons.All(x => (string)x.Content == "×" &&
                      x.ActualWidth >= 28), "真实下拉列表每个账号右侧显示可操作的删除叉号");
                var popupContent = Descendants(popup.Child).OfType<ScrollViewer>().First();
                SaveImage(popupContent, "login-account-dropdown.png");
                buttons.Single(x => (string)x.Tag == "测试账号二").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => accounts.IsEnabled);
                check(accounts.Items.Count == 1 && accounts.Text == "测试账号一" &&
                      password.Password == "fixture-secret" && remember.IsChecked == true && auto.IsChecked == true,
                      "点击其他账号叉号保留当前账号、密码及未提交的选项");
                accounts.IsDropDownOpen = true;
                PumpUntil(() => accounts.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem);
                var remaining = Descendants((ComboBoxItem)accounts.ItemContainerGenerator.ContainerFromIndex(0)).OfType<Button>().Single();
                remaining.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => accounts.IsEnabled);
                check(accounts.Items.Count == 0 && accounts.Text == "" && password.Password == "" &&
                      !remember.IsChecked.GetValueOrDefault() && !auto.IsChecked.GetValueOrDefault() &&
                      services.Config.Current.LoginAccounts.Count == 0,
                      "点击当前账号叉号清空输入和保存记录并保持窗口可用");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                window?.PrepareForWindowHandoff();
                window?.Close();
                app?.Shutdown();
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", oldConfig);
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", oldData);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) throw new TimeoutException("登录页WPF检查超时");
        check(error is null, "登录页WPF检查完成" + (error is null ? "" : ": " + error));
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("登录页异步操作超时");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveImage(FrameworkElement root, string name)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "login-account-qa");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
            (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        png.Save(stream);
    }
}
