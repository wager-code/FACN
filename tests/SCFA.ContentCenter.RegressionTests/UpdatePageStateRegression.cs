using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;
using SCFA.ContentCenter.ViewModels;

internal static class UpdatePageStateRegression
{
    internal static async Task RunAsync(ConfigService config, TaskService tasks, LogService log, Action<bool, string> check)
    {
        var originalChannel = config.Current.UpdateChannel;
        var originalUrl = config.Current.UpdateManifestUrl;
        var originalAuto = config.Current.AutoCheckUpdates;
        try
        {
            config.Current.UpdateChannel = "developer";
            config.Current.UpdateManifestUrl = "https://updates.example.test/page-manifest.json";
            config.Current.AutoCheckUpdates = false;
            using var auth = new AuthApiClient("https://updates.example.test", "");
            using var handler = new PageUpdateHandler { HoldDownload = true };
            var updater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62");
            var page = new UpdatesPageViewModel(config, updater, log);
            await InvokeAsync(page, "CheckAsync");
            var download = InvokeAsync(page, "DownloadAsync");
            await handler.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                check(!page.CheckCommand.CanExecute(null) && !page.DownloadCommand.CanExecute(null) &&
                      !page.ApplyCommand.CanExecute(null), "客户端更新下载期间检查、下载与安装共享互斥状态");
                page.SelectedChannel = "beta";
                check(page.SelectedChannel == "developer", "下载期间禁止切换通道以防元数据与安装包错配");
                // Invoke the actual method to cover automatic/direct entry points as well as command gating.
                handler.Version = "4.0.0-dev64";
                var requests = handler.ManifestRequests;
                await InvokeAsync(page, "CheckAsync");
                check(handler.ManifestRequests == requests, "下载期间直接进入检查也不会发出并行清单请求");
            }
            finally { handler.DownloadRelease.TrySetResult(); await download; }
            check(page.Info?.LatestVersion == "4.0.0-dev63" && page.StagedPath.Contains("4.0.0-dev63") &&
                  page.ApplyCommand.CanExecute(null), "下载完成时版本信息与暂存包保持同一次检查的对应关系");

            handler.Version = "4.0.0-dev63";
            var staged = page.StagedPath;
            await InvokeAsync(page, "CheckAsync");
            check(page.StagedPath == staged && File.Exists(staged) && page.ApplyCommand.CanExecute(null),
                "重复检查同一版本和包元数据保留已验证安装状态");

            handler.PackageUrl = "https://updates.example.test/replaced-client.exe";
            await InvokeAsync(page, "CheckAsync");
            check(page.StagedPath.Length == 0 && !page.ApplyCommand.CanExecute(null),
                "同版本发布URL改变后不复用旧安装就绪状态");
            handler.PackageUrl = "https://updates.example.test/page-client.exe";
            await InvokeAsync(page, "CheckAsync");
            await InvokeAsync(page, "DownloadAsync");
            handler.DeclaredSize = 2049;
            await InvokeAsync(page, "CheckAsync");
            check(page.StagedPath.Length == 0 && !page.ApplyCommand.CanExecute(null),
                "同版本清单尺寸改变后阻止使用旧暂存包安装");
            handler.DeclaredSize = null;
            await InvokeAsync(page, "CheckAsync");
            await InvokeAsync(page, "DownloadAsync");
            handler.HashOverride = new string('1', 64);
            await InvokeAsync(page, "CheckAsync");
            check(page.StagedPath.Length == 0 && !page.ApplyCommand.CanExecute(null),
                "同版本清单哈希改变后阻止使用旧暂存包安装");
            handler.HashOverride = null;
            await InvokeAsync(page, "CheckAsync");
            await InvokeAsync(page, "DownloadAsync");
            page.SelectedChannel = "beta";
            check(page.Info is null && page.StagedPath.Length == 0 &&
                  !page.DownloadCommand.CanExecute(null) && !page.ApplyCommand.CanExecute(null) && File.Exists(staged),
                "切换通道立即清除旧版本页面状态但保留磁盘下载缓存");
            page.SelectedChannel = "developer";
            await InvokeAsync(page, "CheckAsync");
            await InvokeAsync(page, "DownloadAsync");
            handler.FailManifest = true;
            await InvokeAsync(page, "CheckAsync");
            check(page.Info is null && page.StagedPath.Length == 0 && !page.ApplyCommand.CanExecute(null) &&
                  page.StageStateLabel == "尚未暂存安装包" && page.CheckCommand.CanExecute(null),
                "检查失败清除过期安装状态并允许重试");
            handler.FailManifest = false;
            await InvokeAsync(page, "CheckAsync");
            await InvokeAsync(page, "DownloadAsync");
            handler.Version = "4.0.0-dev64";
            await InvokeAsync(page, "CheckAsync");
            check(page.Info?.LatestVersion == handler.Version && page.StagedPath.Length == 0 &&
                  !page.ApplyCommand.CanExecute(null), "发现不同版本清单时不沿用旧版本安装状态");
            handler.Version = "4.0.0-dev61";
            await InvokeAsync(page, "CheckAsync");
            check(page.UpdateStateLabel == "当前客户端高于更新通道版本", "更新页准确显示本地高于通道版本而非已是最新");
            handler.Version = "";
            await InvokeAsync(page, "CheckAsync");
            check(page.UpdateStateLabel.Contains("尚未发布客户端版本"), "未发布版本的通道不误报客户端已是最新");

            using var autoHandler = new PageUpdateHandler { HoldManifest = true };
            var autoUpdater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(autoHandler, disposeHandler: false), "4.0.0-dev62");
            config.Current.AutoCheckUpdates = true;
            var autoPage = new UpdatesPageViewModel(config, autoUpdater, log);
            await autoHandler.ManifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task? duplicateCheck = null;
            try
            {
                check(!autoPage.CheckCommand.CanExecute(null), "页面自动检查也阻止用户发起第二次检查");
                duplicateCheck = InvokeAsync(autoPage, "CheckAsync");
            }
            finally { autoHandler.ManifestRelease.TrySetResult(); }
            if (duplicateCheck is not null) await duplicateCheck;
            check(autoHandler.ManifestRequests == 1, "自动检查与直接调用共用互斥入口");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (autoPage.Info is null && DateTime.UtcNow < deadline) await Task.Delay(10);
            check(autoPage.Info is not null && autoPage.CheckCommand.CanExecute(null), "自动检查结束恢复可操作状态");
        }
        finally
        {
            config.Current.UpdateChannel = originalChannel;
            config.Current.UpdateManifestUrl = originalUrl;
            config.Current.AutoCheckUpdates = originalAuto;
            await config.SaveAsync();
        }
    }

    internal static void RunUiSmoke(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previousConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
            var previousData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
            var root = Path.Combine(Path.GetTempPath(), "scfa_update_page_ui_" + Guid.NewGuid().ToString("N"));
            SCFA.ContentCenter.App? app = null;
            Window? host = null;
            try
            {
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", root);
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(root, "data"));
                app = new SCFA.ContentCenter.App();
                app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var config = new ConfigService();
                config.Current.AutoCheckUpdates = false;
                config.Current.UpdateChannel = "developer";
                config.Current.UpdateManifestUrl = "https://updates.example.test/page-manifest.json";
                using var handler = new PageUpdateHandler { HoldDownload = true };
                using var auth = new AuthApiClient("https://updates.example.test", "");
                var service = new UpdateService(config, auth, new TaskService(), new LogService(),
                    _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62");
                var page = new UpdatesPageViewModel(config, service, new LogService());
                var view = new SCFA.ContentCenter.Views.UpdatesView { DataContext = page };
                host = new Window { Content = view, Width = 1040, Height = 760, Opacity = 0, ShowInTaskbar = false };
                host.Show();
                host.UpdateLayout();
                var combo = Descendants(view).OfType<ComboBox>().Single();
                var buttons = Descendants(view).OfType<Button>().ToArray();
                var apply = buttons.Single(b => ReferenceEquals(b.Command, page.ApplyCommand));
                var downloadButton = buttons.Single(b => ReferenceEquals(b.Command, page.DownloadCommand));
                Pump(InvokeAsync(page, "CheckAsync"));
                var download = InvokeAsync(page, "DownloadAsync");
                Pump(handler.DownloadStarted.Task);
                Pump(Task.CompletedTask);
                try
                {
                    check(!combo.IsEnabled && buttons.Where(b => ReferenceEquals(b.Command, page.CheckCommand) ||
                        ReferenceEquals(b.Command, page.DownloadCommand) || ReferenceEquals(b.Command, page.ApplyCommand)).All(b => !b.IsEnabled),
                        "真实WPF更新页下载时禁用通道及检查、下载、安装按钮");
                }
                finally { handler.DownloadRelease.TrySetResult(); Pump(download); }
                check(combo.IsEnabled && apply.IsEnabled && File.Exists(page.StagedPath),
                    "真实WPF更新页下载完成恢复通道与安装按钮");
                combo.SelectedItem = "beta";
                Pump(Task.CompletedTask);
                check(page.SelectedChannel == "beta" && page.Info is null && !apply.IsEnabled && !downloadButton.IsEnabled,
                    "真实WPF通道选择绑定清除旧状态并禁用旧包安装");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                host?.Close();
                app?.Shutdown();
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", previousConfig);
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousData);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception ex) { failure ??= ex; }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) { Console.Error.WriteLine(failure); check(false, "真实WPF更新页状态与绑定回归"); }
    }

    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Update page UI fixture timed out");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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

    private static Task InvokeAsync(UpdatesPageViewModel page, string name) =>
        (Task)typeof(UpdatesPageViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

    private sealed class PageUpdateHandler : HttpMessageHandler
    {
        private readonly byte[] _payload = CreatePayload();
        internal string Version = "4.0.0-dev63";
        internal string PackageUrl = "https://updates.example.test/page-client.exe";
        internal string? HashOverride;
        internal long? DeclaredSize;
        internal bool HoldDownload, HoldManifest, FailManifest;
        internal int ManifestRequests;
        internal TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DownloadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ManifestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ManifestRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ManifestRequests);
                ManifestStarted.TrySetResult();
                if (HoldManifest) await ManifestRelease.Task.WaitAsync(ct);
                if (FailManifest) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var manifest = new AppUpdateManifest
                {
                    Version = Version, Channel = "developer", Url = PackageUrl,
                    Size = DeclaredSize ?? _payload.Length,
                    Sha256 = HashOverride ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_payload)).ToLowerInvariant()
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json") };
            }
            DownloadStarted.TrySetResult();
            if (HoldDownload) await DownloadRelease.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payload) };
        }
        private static byte[] CreatePayload()
        {
            var payload = new byte[2048];
            payload[0] = (byte)'M'; payload[1] = (byte)'Z';
            for (var i = 2; i < payload.Length; i++) payload[i] = (byte)(i % 239);
            return payload;
        }
    }
}