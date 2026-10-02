using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class UpdateHttpRegression
{
    internal static async Task RunIsolatedAsync(Action<bool, string> check, bool publicManifestOnly = false)
    {
        var previousConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
        var previousData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var isolated = Path.Combine(temporaryRoot, "scfa_update_http_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolated);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", isolated);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(isolated, "data"));
            var config = new ConfigService();
            var tasks = new TaskService();
            var log = new LogService();
            if (publicManifestOnly)
            {
                config.Current.UpdateChannel = "developer";
                config.Current.UpdateManifestUrl = "https://github.com/wager-code/FACN/releases/download/4.0.0-dev62/update-manifest.json";
                using var auth = new AuthApiClient("https://github.com", "");
                var result = await new UpdateService(config, auth, tasks, log).CheckAsync();
                check(result.MetadataComplete && result.LatestVersion == "4.0.0-dev62" &&
                    result.Sha256 == "273359cbba98dc31c88a31515cb534388a10a70067faf047eaaaa1134733da25" && result.Size == 164535350,
                    "实际GitHub HTTPS跳转清单由生产更新服务读取并核对已发行附件身份");
            }
            else await RunAsync(config, tasks, log, check);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousData);
            if (Path.GetFullPath(isolated).StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(isolated, recursive: true);
        }
    }

    internal static async Task RunAsync(ConfigService config, TaskService tasks, LogService log, Action<bool, string> check)
    {
        var originalUrl = config.Current.UpdateManifestUrl;
        var originalChannel = config.Current.UpdateChannel;
        config.Current.UpdateChannel = "developer";
        config.Current.UpdateManifestUrl = "https://updates.example.test/manifest.json";
        using var auth = new AuthApiClient("https://updates.example.test", "");
        try
        {
            foreach (var status in new[] { 301, 302, 303, 307, 308 })
            {
                using var handler = new FixtureHandler((request, _) => Task.FromResult(
                    request.RequestUri!.AbsolutePath == "/manifest.json"
                        ? Redirect(status, "https://assets.example.test/release.json") : Manifest()));
                var updater = new UpdateService(config, auth, tasks, log,
                    _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62");
                await CheckSuccessAsync(async () => (await updater.CheckAsync()).Available && handler.Requests.Count == 2,
                    check, $"更新清单允许受控HTTPS {status}跳转到公开附件");
            }
            using (var handler = new FixtureHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/manifest.json" ? Redirect(302, "nested/final.json") : Manifest())))
            {
                var updater = Create(config, auth, tasks, log, handler);
                await CheckSuccessAsync(async () => (await updater.CheckAsync()).Available &&
                    handler.Requests[1] == "https://updates.example.test/nested/final.json", check,
                    "HTTPS相对跳转按当前地址解析");
            }
            foreach (var (location, name) in new[]
            {
                ("http://assets.example.test/manifest.json", "更新跳转拒绝HTTPS降级"),
                ("https://user:secret@assets.example.test/manifest.json", "更新跳转拒绝URL用户信息"),
                ("file:///D:/manifest.json", "更新跳转拒绝非网络协议")
            })
            {
                using var handler = new FixtureHandler((_, _) => Task.FromResult(Redirect(302, location)));
                await CheckFailureAsync<InvalidDataException>(() => Create(config, auth, tasks, log, handler).CheckAsync(), check, name);
                check(handler.Requests.Count == 1, name + "且不请求跳转目标");
            }
            using (var handler = new FixtureHandler((_, _) => Task.FromResult(Redirect(302, "/manifest.json"))))
            {
                await CheckFailureAsync<InvalidDataException>(() => Create(config, auth, tasks, log, handler).CheckAsync(), check,
                    "更新跳转循环达到五次上限后失败");
                check(handler.Requests.Count <= 6, "跳转循环不会无限请求");
            }
            using (var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found))))
                await CheckFailureAsync<InvalidDataException>(() => Create(config, auth, tasks, log, handler).CheckAsync(), check,
                    "更新跳转缺少Location明确拒绝");
            using var pinnedAuth = new AuthApiClient("https://updates.example.test", new string('a', 64));
            using (var handler = new FixtureHandler((_, _) => Task.FromResult(Redirect(302, "https://assets.example.test/release.json"))))
            {
                await CheckFailureAsync<InvalidDataException>(() => Create(config, pinnedAuth, tasks, log, handler).CheckAsync(), check,
                    "证书固定账号地址不能跳转到另一主机绕过固定证书");
                check(handler.Requests.Count == 1, "被拒绝的固定证书跨主机跳转不发送目标请求");
            }
            using (var handler = new FixtureHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/manifest.json" ? Redirect(307, "/same-host.json") : Manifest())))
                await CheckSuccessAsync(async () => (await Create(config, pinnedAuth, tasks, log, handler).CheckAsync()).Available,
                    check, "证书固定地址仍允许同主机HTTPS跳转");
            var payload = new byte[2048]; payload[0] = (byte)'M'; payload[1] = (byte)'Z';
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var redirectContent = new TrackedContent([]);
            var finalContent = new TrackedContent(payload);
            using (var handler = new FixtureHandler((request, _) => Task.FromResult(
                request.RequestUri!.Host == "updates.example.test"
                    ? Redirect(302, "https://assets.example.test/client.exe", redirectContent)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = finalContent })))
            {
                var info = new AppUpdateInfo { Available = true, MetadataComplete = true,
                    LatestVersion = "4.0.0-http-redirect", DownloadUrl = "https://updates.example.test/client.exe",
                    Size = payload.Length, Sha256 = hash };
                await CheckSuccessAsync(async () => await ContentHash.FileSha256Async(
                    await Create(config, auth, tasks, log, handler).DownloadAsync(info)) == hash,
                    check, "更新包跟随HTTPS跳转后仍校验大小SHA与PE");
                check(redirectContent.Disposed && finalContent.Disposed, "跳转和最终响应正文都及时释放");
            }
            await CheckDeadlinesAsync(config, auth, tasks, log, check);
        }
        finally
        {
            config.Current.UpdateManifestUrl = originalUrl;
            config.Current.UpdateChannel = originalChannel;
        }
    }

    private static async Task CheckDeadlinesAsync(ConfigService config, AuthApiClient auth,
        TaskService tasks, LogService log, Action<bool, string> check)
    {
        using (var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var handler = new FixtureHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Manifest();
        }))
        {
            var updater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62", TimeSpan.FromMilliseconds(150));
            await CheckFailureAsync<TimeoutException>(() => updater.CheckAsync(fallback.Token), check,
                "清单总期限覆盖响应头等待且报告超时");
        }
        var stalledManifest = new HoldingStream([]);
        using (var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var handler = new FixtureHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "updates.example.test" ? Redirect(302, "https://assets.example.test/slow.json") :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalledManifest) })))
        {
            var updater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62", TimeSpan.FromMilliseconds(150));
            await CheckFailureAsync<TimeoutException>(() => updater.CheckAsync(fallback.Token), check,
                "跳转后的无ContentLength正文仍受清单总期限限制");
            check(stalledManifest.Disposed && handler.Requests.Count == 2, "清单正文超时释放跳转后的响应流");
        }
        using (var caller = new CancellationTokenSource())
        using (var handler = new FixtureHandler(async (_, ct) =>
        {
            caller.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Manifest();
        }))
        {
            var updater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62", TimeSpan.FromSeconds(2));
            await CheckFailureAsync<OperationCanceledException>(() => updater.CheckAsync(caller.Token), check,
                "调用方取消仍为取消而非网络超时");
        }
        using (var handler = new FixtureHandler((_, _) =>
        {
            var response = Manifest();
            response.Content.Headers.ContentLength = 1024 * 1024 + 1;
            return Task.FromResult(response);
        }))
            await CheckFailureAsync<InvalidDataException>(() => Create(config, auth, tasks, log, handler).CheckAsync(), check,
                "清单声明大于1MiB时拒绝读取");
        using (var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new byte[1024 * 1024 + 1]))
        })))
            await CheckFailureAsync<InvalidDataException>(() => Create(config, auth, tasks, log, handler).CheckAsync(), check,
                "无ContentLength清单仍检查实际1MiB正文上限");
        var payload = new byte[1024]; payload[0] = (byte)'M'; payload[1] = (byte)'Z';
        var timeoutStream = new HoldingStream(payload);
        var timeoutInfo = new AppUpdateInfo { Available = true, MetadataComplete = true,
            LatestVersion = "4.0.0-http-timeout", DownloadUrl = "https://updates.example.test/client.exe",
            Size = 2048, Sha256 = new string('a', 64) };
        using (var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(timeoutStream) })))
        {
            var updater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62", TimeSpan.FromMilliseconds(150));
            await CheckFailureAsync<TimeoutException>(() => updater.DownloadAsync(timeoutInfo, fallback.Token), check,
                "更新包正文停滞超过总期限报告失败");
            check(!File.Exists(Path.Combine(updater.StagingRoot, timeoutInfo.LatestVersion, "SCFA内容中心.exe")) &&
                tasks.Tasks.First().Status == "失败" && timeoutStream.Disposed,
                "更新包超时删除不完整暂存文件并释放正文");
        }
        var canceledStream = new HoldingStream(payload);
        var cancelInfo = new AppUpdateInfo { Available = true, MetadataComplete = true,
            LatestVersion = "4.0.0-http-canceled", DownloadUrl = timeoutInfo.DownloadUrl, Size = 2048, Sha256 = timeoutInfo.Sha256 };
        using (var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(canceledStream) })))
        {
            var updater = new UpdateService(config, auth, tasks, log,
                _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62", TimeSpan.FromSeconds(5));
            var download = updater.DownloadAsync(cancelInfo);
            await canceledStream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            tasks.Tasks.First().CancelCommand.Execute(null);
            await CheckFailureAsync<OperationCanceledException>(() => download, check,
                "任务取消在正在读取正文时及时保留取消语义");
            check(!File.Exists(Path.Combine(updater.StagingRoot, cancelInfo.LatestVersion, "SCFA内容中心.exe")) &&
                tasks.Tasks.First().Status == "已取消" && canceledStream.Disposed,
                "任务取消清理未完成更新包并释放正文");
        }
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class HoldingStream(byte[] prefix) : Stream
    {
        private bool _sent;
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent && prefix.Length > 0) { prefix.CopyTo(buffer); _sent = true; return prefix.Length; }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static UpdateService Create(ConfigService config, AuthApiClient auth, TaskService tasks, LogService log, FixtureHandler handler)
        => new(config, auth, tasks, log, _ => new HttpClient(handler, disposeHandler: false), "4.0.0-dev62");
    private static HttpResponseMessage Redirect(int status, string location, HttpContent? content = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content ?? new ByteArrayContent([]) };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static HttpResponseMessage Manifest() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new AppUpdateManifest
        {
            Version = "4.0.0-dev64", Channel = "developer", Url = "https://assets.example.test/client.exe",
            Size = 2048, Sha256 = new string('a', 64)
        }), Encoding.UTF8, "application/json")
    };
    private static async Task CheckSuccessAsync(Func<Task<bool>> action, Action<bool, string> check, string name)
    {
        try { check(await action(), name); }
        catch (Exception ex) { Console.Error.WriteLine(name + ": " + ex.GetType().Name); check(false, name); }
    }
    private static async Task CheckFailureAsync<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    {
        try { await action(); check(false, name); }
        catch (Exception ex) { check(ex is T, name); }
    }
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return action(request, ct);
        }
    }
    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        internal bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
