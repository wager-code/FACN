using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class PackagedUpdateRegression
{
    internal static async Task RunWorkerAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Packaged worker arguments invalid");
        await UpdateApplier.ApplyAsync(["--apply-update", args[2], int.MaxValue.ToString(), args[3]], args[1], start =>
        {
            start.ArgumentList.Add("--update-startup-smoke");
            start.CreateNoWindow = true;
            var child = Process.Start(start) ?? throw new IOException("Unable to launch packaged client");
            File.WriteAllText(args[2] + ".fixture-child", child.Id + "|" + child.StartTime.ToUniversalTime().Ticks);
            return child;
        });
    }

    internal static async Task RunAsync(string package, string baseline, Action<bool, string> check)
    {
        package = Path.GetFullPath(package);
        baseline = Path.GetFullPath(baseline);
        var version = ReadVersion(package);
        var oldVersion = ReadVersion(baseline);
        if (!File.Exists(package) || !File.Exists(baseline))
            throw new FileNotFoundException("Published package or baseline missing");
        var root = Path.Combine(Path.GetTempPath(), "scfa_packaged_update_" + Guid.NewGuid().ToString("N"));
        var previousConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
        var previousData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        var previousExtract = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", root);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", data);
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(root, "bundle-extract"));
            var config = new ConfigService();
            config.Current.ApiBaseUrl = "https://updates.example.test";
            config.Current.ApiDirectUrl = "";
            config.Current.ApiDirectCertSha256 = "";
            config.Current.PublicationApiBaseUrl = config.Current.ApiBaseUrl;
            config.Current.AutoLogin = false;
            config.Current.AutoCheckUpdates = false;
            config.Current.UpdateChannel = "developer";
            config.Current.UpdateManifestUrl = "https://updates.example.test/manifest.json";
            await config.SaveAsync();
            var expected = await ContentHash.FileSha256Async(package);
            var original = await ContentHash.FileSha256Async(baseline);
            var manifest = new AppUpdateManifest
            {
                Version = version, Channel = "developer", Url = "https://updates.example.test/client.exe",
                Size = new FileInfo(package).Length, Sha256 = expected,
                Notes = "Isolated published-package regression"
            };
            using var handler = new PublishedPackageHandler(manifest, package);
            using var auth = new AuthApiClient(config.Current.ApiBaseUrl, "");
            var updater = new UpdateService(config, auth, new TaskService(), new LogService(),
                _ => new HttpClient(handler, disposeHandler: false), oldVersion);
            var info = await updater.CheckAsync();
            check(info.Available && info.MetadataComplete && info.CurrentVersion == oldVersion &&
                info.LatestVersion == version, "真实发布包元数据可从旧版本识别为可安装更新");
            var staged = await updater.DownloadAsync(info);
            var second = await updater.DownloadAsync(await updater.CheckAsync());
            check(staged == second && handler.PackageRequests == 1 &&
                new FileInfo(staged).Length == manifest.Size && await ContentHash.FileSha256Async(staged) == expected,
                "真实发布包重复检查和下载复用完整缓存且不重复传输");

            var target = Path.Combine(root, "installed", "SCFA内容中心.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(baseline, target);
            var successful = await RunApplyAsync(staged, target, expected);
            check(successful == 0 && await ContentHash.FileSha256Async(target) == expected &&
                await WaitForCleanupAsync(staged, target),
                "真实单文件包通过独立更新进程替换旧客户端，初始化确认后清理");

            // The failure occurs in the actual WPF child's service initialization.
            staged = await updater.DownloadAsync(info);
            File.Copy(baseline, target, true);
            config.Current.ApiBaseUrl = "invalid-endpoint";
            await config.SaveAsync();
            var failed = await RunApplyAsync(staged, target, expected);
            check(failed != 0 && File.Exists(staged) &&
                await ContentHash.FileSha256Async(target) == original &&
                !Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.failed-update_*").Any(),
                "真实单文件包启动失败由独立更新进程恢复旧便携包完整哈希");

            config.Current.ApiBaseUrl = "https://updates.example.test";
            await config.SaveAsync();
            var retried = await updater.DownloadAsync(info);
            var retryExit = await RunApplyAsync(retried, target, expected);
            check(handler.PackageRequests == 2 && retryExit == 0 &&
                await ContentHash.FileSha256Async(target) == expected && await WaitForCleanupAsync(retried, target),
                "真实包更新失败后复用暂存包重试，成功后清理且原发行包不变");
            check(await ContentHash.FileSha256Async(baseline) == original &&
                await ContentHash.FileSha256Async(package) == expected,
                "隔离更新与回滚不改动原始旧版和候选发行包");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", previousExtract);
            try { Directory.Delete(root, true); }
            catch (IOException ex) { Console.Error.WriteLine("Packaged fixture cleanup: " + ex.Message); }
        }
    }

    private static string ReadVersion(string path) =>
        (FileVersionInfo.GetVersionInfo(path).ProductVersion ?? throw new InvalidDataException("Missing package version")).Split('+')[0];

    private static async Task<int> RunApplyAsync(string staged, string target, string hash)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SCFA.ContentCenter.RegressionTests.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--packaged-update-worker", staged, target, hash })
            start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new IOException("Unable to launch update worker");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            await worker.WaitForExitAsync(timeout.Token);
            await WaitForOwnedChildAsync(target);
            return worker.ExitCode;
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
        }
    }

    private static async Task WaitForOwnedChildAsync(string target)
    {
        var record = target + ".fixture-child";
        if (!File.Exists(record)) return;
        var identity = File.ReadAllText(record).Split('|');
        Process child;
        try { child = Process.GetProcessById(int.Parse(identity[0])); }
        catch (ArgumentException) { return; }
        using (child)
        {
            // Do not wait on or stop an unrelated process after PID reuse.
            if (child.StartTime.ToUniversalTime().Ticks != long.Parse(identity[1])) return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await child.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
    }

    private static async Task<bool> WaitForCleanupAsync(string staged, string target)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(35))
        {
            if (!File.Exists(staged) && !Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.pre-update_*").Any())
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    private sealed class PublishedPackageHandler(AppUpdateManifest manifest, string package) : HttpMessageHandler
    {
        internal int PackageRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal))
                content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json");
            else
            {
                Interlocked.Increment(ref PackageRequests);
                content = new StreamContent(File.OpenRead(package));
                content.Headers.ContentLength = new FileInfo(package).Length;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
