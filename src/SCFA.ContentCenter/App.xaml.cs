using System.Windows;
using SCFA.ContentCenter.Services;
using SCFA.ContentCenter.Views;

namespace SCFA.ContentCenter;

public partial class App : Application
{
    public static ServiceRegistry Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mainWindowSmoke = e.Args.Contains("--main-window-construction-smoke", StringComparer.OrdinalIgnoreCase);
        if (e.Args.FirstOrDefault() == "--apply-update")
        {
            try { await UpdateApplier.ApplyAsync(e.Args); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "SCFA 内容中心更新失败", MessageBoxButton.OK, MessageBoxImage.Error); }
            Shutdown();
            return;
        }
        if (e.Args.FirstOrDefault() == "--cleanup-update")
        {
            try { await UpdateApplier.CleanupAsync(e.Args); }
            catch (Exception ex) { LogService.Bootstrap("清理更新临时文件失败", ex); }
        }
        LogService.Bootstrap("应用启动，开始初始化服务");
        DispatcherUnhandledException += (_, args) =>
        {
            try { Services?.Log.Error("Unhandled UI exception", args.Exception); } catch { }
            MessageBox.Show(args.Exception.Message, "SCFA 内容中心发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            Services = await ServiceRegistry.CreateAsync();
            if (mainWindowSmoke)
            {
                var smokeWindow = new MainWindow { Opacity = 0, ShowInTaskbar = false };
                Current.MainWindow = smokeWindow;
                smokeWindow.Show();
                smokeWindow.UpdateLayout();
                smokeWindow.PrepareForWindowHandoff();
                smokeWindow.Close();
                Shutdown(0);
                return;
            }
            LogService.Bootstrap("服务初始化完成，准备创建登录窗口");
            OpenLoginWindow();
            LogService.Bootstrap($"登录窗口创建完成；窗口数={Current.Windows.Count}，可见={Current.MainWindow?.IsVisible}");
        }
        catch (Exception ex)
        {
            LogService.Bootstrap("应用启动失败", ex);
            if (mainWindowSmoke) { Shutdown(-1); return; }
            MessageBox.Show(ex.Message, "SCFA 内容中心启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    public static void OpenMainWindow()
    {
        var old = Current.MainWindow;
        var main = new MainWindow();
        Current.MainWindow = main;
        main.Show();
        if (old is LoginWindow login) login.PrepareForWindowHandoff();
        else if (old is MainWindow oldMain) oldMain.PrepareForWindowHandoff();
        old?.Close();
    }

    public static void OpenLoginWindow()
    {
        var old = Current.MainWindow;
        var login = new LoginWindow();
        Current.MainWindow = login;
        login.Show();
        if (old is LoginWindow oldLogin) oldLogin.PrepareForWindowHandoff();
        else if (old is MainWindow main) main.PrepareForWindowHandoff();
        if (!ReferenceEquals(old, login)) old?.Close();
    }
}
