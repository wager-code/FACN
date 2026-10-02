using System.Windows;
using System.Windows.Threading;
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
        var updateStartup = e.Args.FirstOrDefault() == "--cleanup-update";
        var updateStartupSmoke = updateStartup && e.Args.Contains("--update-startup-smoke", StringComparer.Ordinal);
        var startupArgs = updateStartupSmoke ? e.Args.Where(x => x != "--update-startup-smoke").ToArray() : e.Args;
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
            OpenLoginWindow(updateStartupSmoke);
            LogService.Bootstrap($"登录窗口创建完成；窗口数={Current.Windows.Count}，可见={Current.MainWindow?.IsVisible}");
            if (updateStartup)
            {
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (Current.MainWindow is not { IsVisible: true })
                            throw new InvalidOperationException("新客户端登录窗口尚未就绪");
                        Current.MainWindow.UpdateLayout();
                    }, DispatcherPriority.ContextIdle);
                    await UpdateApplier.CleanupAsync(startupArgs);
                    if (updateStartupSmoke) Shutdown(0);
                }
                catch (Exception ex)
                {
                    LogService.Bootstrap("确认更新启动或清理临时文件失败", ex);
                    if (startupArgs.Length == 6 || updateStartupSmoke) Shutdown(-1);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Bootstrap("应用启动失败", ex);
            if (mainWindowSmoke || updateStartup) { Shutdown(-1); return; }
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

    public static void OpenLoginWindow() => OpenLoginWindow(false);

    private static void OpenLoginWindow(bool hidden)
    {
        var old = Current.MainWindow;
        var login = new LoginWindow();
        if (hidden) { login.Opacity = 0; login.ShowInTaskbar = false; }
        Current.MainWindow = login;
        login.Show();
        if (old is LoginWindow oldLogin) oldLogin.PrepareForWindowHandoff();
        else if (old is MainWindow main) main.PrepareForWindowHandoff();
        if (!ReferenceEquals(old, login)) old?.Close();
    }
}
